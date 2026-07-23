using System.Text;
using System.Text.Json;
using System.Globalization;

namespace AutomowerConsole;

// Reads and writes the per-mower track-<mower>.jsonl log: the live polling
// loop ('track') and the historical session summary ('sessions'). Both
// revolve around the same artifact, hence one class. Prints live progress
// during RunAsync (same accepted-Console-output pattern as
// MowerService.ResolveMowerAsync - it's feedback intrinsic to the operation,
// not a separate presentation layer) but SummarizeSessions returns data;
// Program.cs still does that command's line formatting/printing.
internal class TrackingService(ScheduleService schedule)
{
    // Activity values that mean "sitting at the charging station", as opposed to
    // actually out in the garden. Used to suppress repeat polls while parked.
    public static bool IsAtCharger(string activity) => activity is "CHARGING" or "PARKED_IN_CS";

    public async Task RunAsync(string mowerId, string mowerName, Config config, int activeIntervalSeconds, CancellationToken cancellationToken)
    {
        var connect = AutomowerConnect.Instance;
        Storage.EnsureDataDir();
        var logPath = Storage.GetTrackLogPath(mowerName);
        await connect.AuthenticateAsync();

        Console.WriteLine($"Tracking {mowerName}. Logging to {logPath}. Press Ctrl+C to stop.");
        Console.WriteLine($"  Active/scheduled: every {activeIntervalSeconds}s   Idle (daytime): every {config.IdleIntervalSeconds}s   " +
                           $"Night ({config.NightStartHour:00}:00-{config.NightEndHour:00}:00): every {config.NightIntervalSeconds}s");
        Console.WriteLine("While parked at the charging station and no schedule/mowing is active, only the first poll after arrival is logged.");
        Console.WriteLine("The mower's schedule is refreshed from schedule.json's cache each poll (no extra API cost) - run 'schedule' to force an update.");

        // Seed with whatever's cached so the very first wait (before any poll)
        // already has something to work with; refreshed for real after each poll.
        var tasks = schedule.GetCachedTasks(mowerId);

        string? lastActivity = null;
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
                schedule.SaveScheduleForMower(mowerId, mowerName, tasks);

                var atCharger = IsAtCharger(activity);
                var wasAtCharger = lastActivity is not null && IsAtCharger(lastActivity);
                (nextIntervalSeconds, var reason) = schedule.DetermineTrackingInterval(tasks, activity, activeIntervalSeconds, timestamp, config);

                if (!(atCharger && wasAtCharger))
                {
                    var byteCount = Encoding.UTF8.GetByteCount(raw);
                    var record = new
                    {
                        timestamp,
                        mowerId,
                        mowerName,
                        bytes = byteCount,
                        response = doc.RootElement,
                    };
                    await File.AppendAllTextAsync(logPath, JsonSerializer.Serialize(record) + Environment.NewLine, cancellationToken);

                    recordCount++;
                    totalBytes += byteCount;
                    Console.WriteLine($"[{timestamp:yyyy-MM-dd HH:mm:ss}] logged (activity: {activity}, {byteCount} bytes, next check in {nextIntervalSeconds}s - {reason}) - " +
                                       $"{recordCount} records, {totalBytes / 1024.0:F1} KB this session");
                }
                else
                {
                    Console.WriteLine($"[{timestamp:yyyy-MM-dd HH:mm:ss}] skipped (still at charger, next check in {nextIntervalSeconds}s - {reason})");
                }

                lastActivity = activity;
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

    // Reads a mower's track-<mower>.jsonl and summarizes it into sessions: runs
    // of consecutive polls sharing the same (mower.activity, mower.workAreaId) -
    // split on either changing, since activity can stay MOWING across a switch
    // from one work area straight into the next. A session's end is the
    // timestamp of the *next* differing poll (not its own last poll), since
    // that's the earliest point we can confirm the state changed - this matters
    // most for charger stays, where 'track' only logs one poll on arrival and
    // then skips repeats, so a session can be a single log line whose real end
    // is only knowable from what comes after it. Returned newest first.
    public List<TrackSession> SummarizeSessions(string mowerName, bool includeCalendarInfo)
    {
        var logPath = Storage.GetTrackLogPath(mowerName);
        if (!File.Exists(logPath))
        {
            Console.WriteLine($"No track log found for {mowerName} at {logPath}.");
            return [];
        }

        var points = new List<(DateTimeOffset Time, string Activity, int Battery, long WorkAreaId, long PlannerNextStart)>();
        var workAreaNames = new Dictionary<long, string>();
        var latestCalendarTasks = Array.Empty<CalendarTask>();
        var skipped = 0;
        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var timestamp = root.GetProperty("timestamp").GetDateTimeOffset();
                var attributes = root.GetProperty("response").GetProperty("data").GetProperty("attributes");
                var mowerObj = attributes.GetProperty("mower");
                var activity = mowerObj.GetProperty("activity").GetString() ?? "UNKNOWN";
                var workAreaId = mowerObj.TryGetProperty("workAreaId", out var waIdEl) ? waIdEl.GetInt64() : 0L;
                var battery = attributes.GetProperty("battery").GetProperty("batteryPercent").GetInt32();
                var plannerNextStart = attributes.TryGetProperty("planner", out var plannerEl) &&
                    plannerEl.TryGetProperty("nextStartTimestamp", out var nextStartEl)
                    ? nextStartEl.GetInt64()
                    : 0L;
                points.Add((timestamp, activity, battery, workAreaId, plannerNextStart));

                if (attributes.TryGetProperty("workAreas", out var workAreasEl) && workAreasEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var wa in workAreasEl.EnumerateArray())
                    {
                        if (wa.TryGetProperty("workAreaId", out var idEl) &&
                            wa.TryGetProperty("name", out var nameEl) &&
                            !string.IsNullOrWhiteSpace(nameEl.GetString()))
                        {
                            workAreaNames[idEl.GetInt64()] = nameEl.GetString()!.Trim();
                        }
                    }
                }

                if (attributes.TryGetProperty("calendar", out var calendarEl) &&
                    calendarEl.Deserialize<CalendarInfo>() is { Tasks.Length: > 0 } calendarInfo)
                {
                    latestCalendarTasks = calendarInfo.Tasks;
                }
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        if (points.Count == 0)
        {
            Console.WriteLine($"Track log for {mowerName} has no readable records.");
            return [];
        }

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
            if (includeCalendarInfo && IsAtCharger(activity))
            {
                nextCalendarStart = schedule.NextCalendarStart(latestCalendarTasks, start);
                var plannerNextStart = points[i].PlannerNextStart;
                nextPlannedStart = plannerNextStart > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(plannerNextStart).ToLocalTime()
                    : null;
            }

            sessions.Add(new TrackSession(
                start, end, activity, batteryStart, batteryEnd,
                workAreaNames.TryGetValue(workAreaId, out var waName) ? waName : null,
                nextCalendarStart, nextPlannedStart));

            i = j + 1;
        }

        sessions.Reverse();

        if (skipped > 0)
        {
            Console.WriteLine($"  ({skipped} malformed line(s) skipped)");
        }

        return sessions;
    }

    // Rolls SummarizeSessions' output up by calendar day. Thin wrapper around
    // the pure AggregateDailyActivity below - kept separate so the
    // aggregation itself is unit-testable without needing a real track log
    // file (see AutomowerConsole.Tests/TrackingServiceTests.cs, built from a
    // real 3-day session history).
    public List<DailyActivity> SummarizeDailyActivity(string mowerName)
        => AggregateDailyActivity(SummarizeSessions(mowerName, includeCalendarInfo: false));

    // Pure: total mowing time per work area per day (summed if the same area
    // was mowed more than once that day), plus one combined charging total
    // per day (CHARGING and PARKED_IN_CS together - "time spent at the
    // charger", not the finer distinction between actively charging and
    // sitting there already full; a real mower's own activity label for
    // this is inconsistent - sometimes it's plainly "Charging", sometimes a
    // "Parked" session with flat reported battery turns out to have charged
    // anyway, only visible via a much-higher battery% on the *next* session -
    // not attempted here, kept as the simple combined sum by design).
    // Sessions that don't fit either bucket (Going home, Leaving, Stopped,
    // ...) are not represented - only Mowing and Charging were asked for. A
    // session is attributed entirely to its *start* day, same simplification
    // 'sessions' itself makes for its single date column - an overnight
    // charge isn't split across the two days it actually spans. Returned
    // newest day first, matching 'sessions'.
    public static List<DailyActivity> AggregateDailyActivity(IEnumerable<TrackSession> sessions)
    {
        var byDay = new SortedDictionary<DateOnly, DailyAccumulator>();

        foreach (var s in sessions.OrderBy(s => s.Start))
        {
            var day = DateOnly.FromDateTime(s.Start.Date);
            var duration = (s.End ?? DateTimeOffset.Now) - s.Start;

            if (!byDay.TryGetValue(day, out var acc))
            {
                acc = new DailyAccumulator();
                byDay[day] = acc;
            }

            if (IsAtCharger(s.Activity))
            {
                acc.Charging += duration;
            }
            else if (s.Activity == "MOWING")
            {
                acc.AddMowing(s.WorkAreaName, duration);
            }
        }

        var result = byDay
            .Where(kv => kv.Value.Mowing.Count > 0 || kv.Value.Charging > TimeSpan.Zero)
            .Select(kv => new DailyActivity(kv.Key, kv.Value.Mowing, kv.Value.Charging))
            .ToList();
        result.Reverse();
        return result;
    }

    private class DailyAccumulator
    {
        public List<WorkAreaTime> Mowing { get; } = [];
        public TimeSpan Charging;

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

internal record TrackSession(
    DateTimeOffset Start,
    DateTimeOffset? End,
    string Activity,
    int BatteryStart,
    int BatteryEnd,
    string? WorkAreaName,
    DateTimeOffset? NextCalendarStart,
    DateTimeOffset? NextPlannedStart);

internal record DailyActivity(DateOnly Date, List<WorkAreaTime> Mowing, TimeSpan Charging);

internal record WorkAreaTime(string? WorkAreaName, TimeSpan Duration);
