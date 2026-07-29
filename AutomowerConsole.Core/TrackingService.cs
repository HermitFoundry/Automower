using System.Text;
using System.Text.Json;

namespace AutomowerConsole.Core;

// Reads and writes the per-mower track-<mower>.jsonl log: the live polling
// loop ('track') and the historical session summary ('sessions'). Both
// revolve around the same artifact, hence one class. Prints live progress
// during RunAsync (same accepted-Console-output pattern as
// MowerService.ResolveMowerAsync - it's feedback intrinsic to the operation,
// not a separate presentation layer) but SummarizeSessions returns data;
// callers (CLI or web) do their own presentation on top.
public class TrackingService(ScheduleService schedule, IMowerRepositoryFactory repositoryFactory)
{
    // Activity values that mean "sitting at the charging station", as opposed
    // to actually out in the garden. Used to group charger-adjacent sessions
    // (SummarizeSessions/BackfillChargingEndBattery) and to split charger
    // time into Charging vs. Parked (AggregateActivity/SplitChargerSessions).
    public static bool IsAtCharger(string activity) => activity is "CHARGING" or "PARKED_IN_CS";

    public async Task RunAsync(string mowerId, string mowerName, Config config, int activeIntervalSeconds, CancellationToken cancellationToken)
    {
        var connect = AutomowerConnect.Instance;
        var repository = repositoryFactory.ForMower(mowerName);
        var logPath = Storage.GetTrackLogPath(mowerName);
        await connect.AuthenticateAsync();

        Console.WriteLine($"Tracking {mowerName}. Logging to {logPath}. Press Ctrl+C to stop.");
        Console.WriteLine($"  Active/scheduled: every {activeIntervalSeconds}s   Idle (daytime): every {config.IdleIntervalSeconds}s   " +
                           $"Night ({config.NightStartHour:00}:00-{config.NightEndHour:00}:00): every {config.NightIntervalSeconds}s");
        Console.WriteLine("The mower's schedule is refreshed from its cached copy each poll (no extra API cost) - run 'schedule' to force an update.");

        // Seed with whatever's cached so the very first wait (before any poll)
        // already has something to work with; refreshed for real after each poll.
        var tasks = schedule.GetCachedTasks(mowerName);

        // Daily lifetime-statistics snapshots (see MowerRepository.cs's
        // DailyStatisticsSnapshot) - backfill any fully-completed day missing
        // from the daily log first (covers gaps from restarts or extended
        // offline stretches, e.g. winter storage - that just shows up as a
        // gap in the daily table, no special "season" handling needed), then
        // track state in memory below so a same-run day rollover gets
        // recorded the moment it's first seen, without re-scanning the whole
        // track log on every poll.
        var (lastKnownDate, lastKnownStatistics, lastStoredStatisticsDate) =
            await BackfillDailyStatisticsAsync(repository, cancellationToken);

        var recordCount = 0;
        long totalBytes = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var timestamp = DateTimeOffset.Now;
            var nextIntervalSeconds = config.IdleIntervalSeconds;

            try
            {
                var raw = await connect.GetMowerRawAsync(mowerId);

                using var doc = JsonDocument.Parse(raw);
                var attributes = doc.RootElement.GetProperty("data").GetProperty("attributes");
                var activity = attributes.GetProperty("mower").GetProperty("activity").GetString() ?? "";

                // The mower payload already carries the schedule - update the
                // cache from it for free instead of a separate daily fetch.
                tasks = attributes.TryGetProperty("calendar", out var calendarElement)
                    ? calendarElement.Deserialize<CalendarInfo>()?.Tasks ?? []
                    : [];
                schedule.SaveScheduleForMower(mowerName, tasks);

                (nextIntervalSeconds, var reason) = schedule.DetermineTrackingInterval(tasks, activity, activeIntervalSeconds, timestamp, config);

                // Every poll gets logged now, including repeat polls while
                // still sitting at the charger - previously those were
                // suppressed (only the arrival poll and the poll where
                // battery first hit 100% were kept), which could leave a
                // charging session with a multi-hour gap and no record of
                // when it actually finished, if the mower's own activity
                // label happened to flip from CHARGING to PARKED_IN_CS on
                // the same poll that crossed 100% - the 100% poll then
                // starts the *next* session instead of ending this one,
                // leaving BatteryEnd stuck at whatever it was on arrival
                // (confirmed against a real gap: 2026-07-27, AM308V, a
                // Charging session logged only 26%->26% with a 1h43m gap
                // before the next, unrelated Parked poll at 100%).
                // SummarizeSessions' BackfillChargingEndBattery covers the
                // history already collected under the old suppressed
                // scheme; logging every poll going forward makes new gaps
                // like that one far less likely in the first place (the
                // largest possible miss is one poll interval, not up to
                // ~30 minutes at night).
                var byteCount = Encoding.UTF8.GetByteCount(raw);
                await repository.AppendPollAsync(mowerId, raw, timestamp, cancellationToken);

                // Rolled over to a new calendar day since the last poll we
                // saw - record yesterday's end-of-day snapshot now, using the
                // last statistics actually observed for it (not this poll's,
                // which already belongs to today).
                var today = DateOnly.FromDateTime(timestamp.Date);
                if (lastKnownDate is { } previousDate && previousDate < today && lastKnownStatistics is not null &&
                    (lastStoredStatisticsDate is null || lastStoredStatisticsDate < previousDate))
                {
                    await repository.AppendDailyStatisticsAsync(new DailyStatisticsSnapshot(previousDate, lastKnownStatistics), cancellationToken);
                    lastStoredStatisticsDate = previousDate;
                }
                lastKnownDate = today;
                if (attributes.TryGetProperty("statistics", out var statsEl))
                {
                    lastKnownStatistics = statsEl.Deserialize<StatisticsInfo>();
                }

                recordCount++;
                totalBytes += byteCount;
                Console.WriteLine($"[{timestamp:yyyy-MM-dd HH:mm:ss}] logged (activity: {activity}, {byteCount} bytes, next check in {nextIntervalSeconds}s - {reason}) - " +
                                   $"{recordCount} records, {totalBytes / 1024.0:F1} KB this session");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{timestamp:yyyy-MM-dd HH:mm:ss}] poll failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(nextIntervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Console.WriteLine($"Stopped. {recordCount} records logged, {totalBytes / 1024.0:F1} KB this session. Log file: {logPath}");
    }

    // Appends a daily statistics snapshot for every fully-completed calendar
    // day present in the track log but missing from the daily-statistics
    // log - covers gaps from restarts or extended offline stretches (e.g.
    // winter storage), which just show up as a gap in the daily table, no
    // special "season" handling needed. Also returns the in-memory state
    // RunAsync's live loop needs going forward: the most recent poll's
    // date/statistics seen so far (null for a brand new mower with no track
    // log yet), and the latest date already stored (so the live loop never
    // double-records a day this backfill already caught).
    private static async Task<(DateOnly? LastKnownDate, StatisticsInfo? LastKnownStatistics, DateOnly? LastStoredDate)> BackfillDailyStatisticsAsync(
        IMowerRepository repository, CancellationToken cancellationToken)
    {
        var history = repository.GetHistory();
        var stored = repository.GetDailyStatisticsHistory();
        var lastStoredDate = stored.Count > 0 ? stored.Max(s => s.Date) : (DateOnly?)null;

        var today = DateOnly.FromDateTime(DateTimeOffset.Now.Date);
        var missingDays = history.Polls
            .Where(p => p.Statistics is not null)
            .GroupBy(p => DateOnly.FromDateTime(p.Timestamp.Date))
            .Where(g => g.Key < today && (lastStoredDate is null || g.Key > lastStoredDate))
            .OrderBy(g => g.Key);

        foreach (var day in missingDays)
        {
            var lastPollOfDay = day.OrderBy(p => p.Timestamp).Last();
            await repository.AppendDailyStatisticsAsync(new DailyStatisticsSnapshot(day.Key, lastPollOfDay.Statistics!), cancellationToken);
            lastStoredDate = day.Key;
        }

        var lastPoll = history.Polls.Count > 0 ? history.Polls.MaxBy(p => p.Timestamp) : null;
        var lastKnownDate = lastPoll is not null ? DateOnly.FromDateTime(lastPoll.Timestamp.Date) : (DateOnly?)null;
        return (lastKnownDate, lastPoll?.Statistics, lastStoredDate);
    }

    // Reads a mower's track-<mower>.jsonl and summarizes it into sessions: runs
    // of consecutive polls sharing the same (mower.activity, mower.workAreaId) -
    // split on either changing, since activity can stay MOWING across a switch
    // from one work area straight into the next. A session's end is the
    // timestamp of the *next* differing poll (not its own last poll), since
    // that's the earliest point we can confirm the state changed - this matters
    // most for charger stays, where 'track' only logs the arrival poll and the
    // poll where battery reaches 100% (see RunAsync), skipping everything in
    // between - a session can be just those two log lines (or one, if it
    // never reaches 100% before leaving), with its real end only knowable
    // from what comes after it. The 100%-arrival second point is what gives
    // BatteryEnd a real charged-to value instead of just repeating
    // BatteryStart. Returned newest first.
    public List<TrackSession> SummarizeSessions(string mowerName, bool includeCalendarInfo)
    {
        var history = repositoryFactory.ForMower(mowerName).GetHistory();
        if (history.Polls.Count == 0)
        {
            Console.WriteLine($"No track log found for {mowerName} at {Storage.GetTrackLogPath(mowerName)}.");
            return [];
        }

        var workAreaNames = history.WorkAreaNames;
        var latestCalendarTasks = history.LatestCalendarTasks;
        var points = history.Polls
            .Select(p => (Time: p.Timestamp, p.Activity, Battery: p.BatteryPercent, p.WorkAreaId, PlannerNextStart: p.PlannerNextStartTimestamp))
            .ToList();
        points.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Grouping has to scan oldest-to-newest (each session's end depends on
        // the *next* point chronologically); reversed to newest-first at the end.
        var sessions = new List<TrackSession>();
        var i = 0;
        while (i < points.Count)
        {
            var activity = points[i].Activity;
            var workAreaId = points[i].WorkAreaId;
            var start = points[i].Time;
            var batteryStart = points[i].Battery;

            var j = i;
            while (j + 1 < points.Count && points[j + 1].Activity == activity && points[j + 1].WorkAreaId == workAreaId)
            {
                j++;
            }
            var batteryEnd = points[j].Battery;
            var end = j + 1 < points.Count ? points[j + 1].Time : (DateTimeOffset?)null;

            DateTimeOffset? nextCalendarStart = null;
            DateTimeOffset? nextPlannedStart = null;
            DateTimeOffset? chargeCompleteAt = null;
            if (IsAtCharger(activity))
            {
                for (var k = i; k <= j; k++)
                {
                    if (points[k].Battery >= 100)
                    {
                        chargeCompleteAt = points[k].Time;
                        break;
                    }
                }

                if (includeCalendarInfo)
                {
                    nextCalendarStart = schedule.NextCalendarStart(latestCalendarTasks, start);
                    var plannerNextStart = points[i].PlannerNextStart;
                    nextPlannedStart = plannerNextStart > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(plannerNextStart).ToLocalTime()
                        : null;
                }
            }

            sessions.Add(new TrackSession(
                start, end, activity, batteryStart, batteryEnd,
                workAreaNames.TryGetValue(workAreaId, out var waName) ? waName : null,
                nextCalendarStart, nextPlannedStart, chargeCompleteAt));

            i = j + 1;
        }

        sessions = BackfillChargingEndBattery(sessions);
        sessions.Reverse();

        if (history.SkippedLines > 0)
        {
            Console.WriteLine($"  ({history.SkippedLines} malformed line(s) skipped)");
        }

        return sessions;
    }

    // Pure: corrects a Charging/Parked session's own BatteryEnd when it's
    // stuck below what the *next* contiguous at-charger session started at.
    // Happens when the mower's own activity label flips (e.g. CHARGING ->
    // PARKED_IN_CS) on the very same poll that crosses 100% - that poll
    // starts a *new* session instead of ending this one, so this session's
    // last known point can be well below 100% even though it's known (from
    // what immediately follows, with no gap) to have actually finished
    // charging. Confirmed against a real case: 2026-07-27, AM308V, a
    // Charging session logged only 26%->26% with a 1h43m gap before the
    // next, unrelated Parked poll at 100% (see RunAsync, which now logs
    // every poll instead of suppressing repeats at the charger, making new
    // gaps like that one far less likely - this backfill mainly matters for
    // history collected before that change, though the label-flip-on-the-
    // same-poll case can still happen even with dense polling).
    //
    // Only applies across a genuine contiguous boundary (this session's End
    // equals the next session's Start exactly - no gap) between two
    // at-charger sessions, and only when the next one's BatteryStart is
    // actually higher - so it never invents a value, only recovers one
    // that's already known from what comes immediately after. Expects
    // chronological (oldest-first) input, same as the caller's own
    // ordering before it reverses to newest-first.
    public static List<TrackSession> BackfillChargingEndBattery(List<TrackSession> chronological)
    {
        var result = new List<TrackSession>(chronological);
        for (var idx = 0; idx < result.Count - 1; idx++)
        {
            var current = result[idx];
            var next = result[idx + 1];
            if (IsAtCharger(current.Activity) && IsAtCharger(next.Activity) &&
                current.End == next.Start && next.BatteryStart > current.BatteryEnd)
            {
                result[idx] = current with { BatteryEnd = next.BatteryStart };
            }
        }
        return result;
    }

    // Pure: expands a charger session that has a ChargeCompleteAt into two -
    // "actually charging" (Start -> ChargeCompleteAt) and "parked, already
    // full" (ChargeCompleteAt -> End) - instead of leaving it as one row
    // whose raw Activity/battery range makes it look like nothing happened
    // for however long the whole stay took. Same split AggregateDailyActivity
    // already applies to the day *totals*; this applies it to the session
    // *list* itself, for callers that display individual sessions rather
    // than day rollups (e.g. AutomowerWeb's session tables) - deliberately
    // not folded into SummarizeSessions itself, so the CLI's 'sessions'
    // command (single line per charger stay, by design - see SKILL.md) is
    // unaffected unless a caller explicitly asks for this.
    //
    // Reassigns Activity on both halves ("CHARGING"/"PARKED_IN_CS") rather
    // than keeping whatever the original raw activity happened to be -
    // that's the whole point of ChargeCompleteAt: a more reliable signal
    // for "was it actually charging" than the API's own per-poll activity
    // label, which is already documented elsewhere as unreliable for this.
    // NextCalendarStart/NextPlannedStart (only meaningful as of the stay's
    // arrival) stay on the Charging half and are cleared on the Parked
    // half, so a caller rendering both doesn't show the same calendar
    // prediction twice. Sessions with no ChargeCompleteAt, or where it
    // equals Start/effective-end (arrived already full, or a same-instant
    // edge case), pass through unchanged - nothing to split. Preserves
    // input order (newest-first, if that's what was passed in): the Parked
    // half - the more recent of the two - takes the original session's
    // position, the Charging half follows immediately after it.
    //
    // A still-ongoing session (End is null) uses 'now' only to decide
    // *whether* to split - same as AggregateDailyActivity treating an
    // ongoing charger stay's duration as running up to 'now' for the day
    // totals. The Parked half's own End stays null (still ongoing), not set
    // to 'now' - a fixed timestamp would go stale the instant this method
    // returns, misrepresenting a still-growing stay as one that stopped
    // growing at whatever moment this happened to run.
    public static List<TrackSession> SplitChargerSessions(IEnumerable<TrackSession> sessions)
    {
        var result = new List<TrackSession>();
        foreach (var s in sessions)
        {
            var effectiveEnd = s.End ?? DateTimeOffset.Now;
            if (IsAtCharger(s.Activity) &&
                s.ChargeCompleteAt is { } completeAt && completeAt > s.Start && completeAt < effectiveEnd)
            {
                result.Add(s with { Start = completeAt, Activity = "PARKED_IN_CS", BatteryStart = 100, NextCalendarStart = null, NextPlannedStart = null });
                result.Add(s with { End = completeAt, Activity = "CHARGING", BatteryEnd = 100 });
            }
            else
            {
                result.Add(s);
            }
        }
        return result;
    }

    // Rolls SummarizeSessions' output up by calendar day. Thin wrapper around
    // the pure AggregateDailyActivity below - kept separate so the
    // aggregation itself is unit-testable without needing a real track log
    // file (see AutomowerConsole.Tests/TrackingServiceTests.cs, built from a
    // real 3-day session history).
    public List<DailyActivity> SummarizeDailyActivity(string mowerName)
        => AggregateDailyActivity(SummarizeSessions(mowerName, includeCalendarInfo: false));

    // Same idea as SummarizeDailyActivity, one calendar month per row instead
    // of one day - the long-range view once even the daily rollup gets too
    // long to be useful (see AutomowerWeb's MowerDetails page: session
    // history is windowed to the last 7 days, daily to the last month,
    // monthly is the only one of the three left unwindowed).
    public List<MonthlyActivity> SummarizeMonthlyActivity(string mowerName)
        => AggregateMonthlyActivity(SummarizeSessions(mowerName, includeCalendarInfo: false));

    // Pure: total mowing time per work area per day (summed if the same area
    // was mowed more than once that day), plus a charger-time split per day -
    // CHARGING and PARKED_IN_CS are still combined into one "time spent at
    // the charger" total (the activity label itself is an unreliable signal
    // for whether real charging is happening - not attempted here), but that
    // total is now divided into Charging (session start -> the point battery
    // first hit 100%, per TrackSession.ChargeCompleteAt) and Parked (that
    // point -> session end, i.e. sitting there already charged, not actively
    // charging anymore). A session with no ChargeCompleteAt (never reached
    // 100% before it ended, or ended still ongoing, or predates the field
    // existing) counts entirely as Charging, none as Parked - "still
    // charging" as far as this data can tell. Sessions that don't fit either
    // bucket (Going home, Leaving, Stopped, ...) are not represented - only
    // Mowing and Charging/Parked were asked for. A session (and its
    // Charging/Parked split) is attributed entirely to its *start* day, same
    // simplification 'sessions' itself makes for its single date column - an
    // overnight charge isn't split across the two days it actually spans.
    // Returned newest day first, matching 'sessions'.
    public static List<DailyActivity> AggregateDailyActivity(IEnumerable<TrackSession> sessions) =>
        AccumulateByBucket(sessions, t => DateOnly.FromDateTime(t.Date))
            .Select(b => new DailyActivity(b.Bucket, b.Acc.Mowing, b.Acc.Charging, b.Acc.Parked))
            .ToList();

    // Same aggregation as AggregateDailyActivity, bucketed by calendar month
    // (the 1st of each month standing in for "this month") instead of by
    // day - see AccumulateByBucket, which both share.
    public static List<MonthlyActivity> AggregateMonthlyActivity(IEnumerable<TrackSession> sessions) =>
        AccumulateByBucket(sessions, t => new DateOnly(t.Year, t.Month, 1))
            .Select(b => new MonthlyActivity(b.Bucket, b.Acc.Mowing, b.Acc.Charging, b.Acc.Parked))
            .ToList();

    // Shared by AggregateDailyActivity/AggregateMonthlyActivity - identical
    // charging/parked-split and per-work-area mowing accumulation, differing
    // only in how a session's Start maps to a bucket (a day vs. a month).
    // Returned newest bucket first, matching 'sessions'.
    private static List<(DateOnly Bucket, DailyAccumulator Acc)> AccumulateByBucket(
        IEnumerable<TrackSession> sessions, Func<DateTimeOffset, DateOnly> bucketOf)
    {
        var byBucket = new SortedDictionary<DateOnly, DailyAccumulator>();

        foreach (var s in sessions.OrderBy(s => s.Start))
        {
            var bucket = bucketOf(s.Start);
            var end = s.End ?? DateTimeOffset.Now;

            if (!byBucket.TryGetValue(bucket, out var acc))
            {
                acc = new DailyAccumulator();
                byBucket[bucket] = acc;
            }

            if (IsAtCharger(s.Activity))
            {
                if (s.ChargeCompleteAt is { } completeAt)
                {
                    acc.Charging += completeAt - s.Start;
                    acc.Parked += end - completeAt;
                }
                else
                {
                    acc.Charging += end - s.Start;
                }
            }
            else if (s.Activity == "MOWING")
            {
                acc.AddMowing(s.WorkAreaName, end - s.Start);
            }
        }

        return byBucket
            .Where(kv => kv.Value.Mowing.Count > 0 || kv.Value.Charging > TimeSpan.Zero || kv.Value.Parked > TimeSpan.Zero)
            .Select(kv => (kv.Key, kv.Value))
            .Reverse()
            .ToList();
    }

    private class DailyAccumulator
    {
        public List<WorkAreaTime> Mowing { get; } = [];
        public TimeSpan Charging;
        public TimeSpan Parked;

        public void AddMowing(string? workAreaName, TimeSpan duration)
        {
            var index = Mowing.FindIndex(m => m.WorkAreaName == workAreaName);
            if (index >= 0)
            {
                Mowing[index] = Mowing[index] with { Duration = Mowing[index].Duration + duration };
            }
            else
            {
                Mowing.Add(new WorkAreaTime(workAreaName, duration));
            }
        }
    }
}

public record TrackSession(
    DateTimeOffset Start,
    DateTimeOffset? End,
    string Activity,
    int BatteryStart,
    int BatteryEnd,
    string? WorkAreaName,
    DateTimeOffset? NextCalendarStart,
    DateTimeOffset? NextPlannedStart,
    // First point within this charger session where battery reached 100%
    // (see RunAsync's second per-stay log point) - null means either not a
    // charger session, or one that never reached 100% before it ended
    // ("still charging" the whole time, incl. currently-ongoing ones).
    // Forward-looking only: track logs written before this field existed
    // only ever have the one arrival point, so historical sessions always
    // report null here, not a real "arrived already full" or "never
    // finished" fact.
    DateTimeOffset? ChargeCompleteAt = null);

public record DailyActivity(DateOnly Date, List<WorkAreaTime> Mowing, TimeSpan Charging, TimeSpan Parked);

// Month is always the 1st of that month (e.g. 2026-07-01 for July 2026) -
// same shape as DailyActivity, just bucketed coarser; a caller displaying it
// formats Month as "yyyy-MM" instead of "yyyy-MM-dd".
public record MonthlyActivity(DateOnly Month, List<WorkAreaTime> Mowing, TimeSpan Charging, TimeSpan Parked);

public record WorkAreaTime(string? WorkAreaName, TimeSpan Duration);
