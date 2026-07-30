using System.Text;
using System.Text.Json;

namespace AutomowerConsole.Core;

// One poll's worth of the fields TrackingService.SummarizeSessions and
// CoverageService.GetCoverageByWorkArea each already pulled out of the same
// raw JSON independently - now parsed once by IMowerRepository.GetHistory()
// and shared. Latitude/Longitude come from positions[0] only (the newest
// breadcrumb) - see JsonlMowerRepository.GetHistory() for why the rest of
// that poll's up-to-50-entry array is deliberately not surfaced here.
//
// WorkAreaIdObservedAt is the timestamp of whichever observation last
// actually set WorkAreaId - equal to Timestamp itself for a REST-sourced
// poll (a REST response is always one atomic snapshot, so WorkAreaId and
// Latitude/Longitude are always in sync by construction), but can trail
// Timestamp for a hybrid-tracked, WebSocket-event-sourced poll: a
// position-event-v2 row's own WorkAreaId is necessarily carried forward
// from whatever the last mower-event-v2 said, since no single event
// carries both a position and a work area together. CoverageService uses
// this to avoid pairing a fresh GPS fix with a stale, carried-forward
// WorkAreaId - see its own comment for why that pairing used to be safe to
// assume (under REST-only polling) and no longer is.
public record PollRecord(
    DateTimeOffset Timestamp,
    string Activity,
    int BatteryPercent,
    long WorkAreaId,
    long PlannerNextStartTimestamp,
    double? Latitude,
    double? Longitude,
    StatisticsInfo? Statistics,
    DateTimeOffset WorkAreaIdObservedAt);

// One calendar day's end-of-day snapshot of the mower's lifetime usage
// counters (StatisticsInfo - cumulative since setup, not a daily delta) -
// see TrackingService.RunAsync for how/when this gets written. Diffing two
// of these (e.g. season start vs. season end) gives "how much this season";
// keeping one per day instead of just a running total also gives within-
// season trends for free.
public record DailyStatisticsSnapshot(DateOnly Date, StatisticsInfo Statistics);

// Single-pass result of scanning a mower's whole poll history. WorkAreaNames
// and LatestCalendarTasks are rolling snapshots accumulated across the same
// scan that produces Polls - kept alongside it rather than as separate
// methods so a caller needing more than one of the three doesn't cost more
// than one file read.
public record MowerHistory(
    IReadOnlyList<PollRecord> Polls,
    IReadOnlyDictionary<long, string> WorkAreaNames,
    CalendarTask[] LatestCalendarTasks,
    int SkippedLines);

// Parses a raw REST mower response's {"data":{"attributes":{...}}} shape -
// shared between JsonlMowerRepository.GetHistory() (unwraps its own JSONL
// line envelope first) and SqliteMowerRepository (a "rest"-sourced
// RawEvents.RawJson row already *is* this shape directly) - factored out so
// the parsing details, including the hard-won positions[0]-only reasoning,
// exist in exactly one place rather than two copies drifting apart.
internal static class RestSnapshotParser
{
    public static (string Activity, long WorkAreaId, int BatteryPercent, long PlannerNextStartTimestamp, double? Latitude, double? Longitude, StatisticsInfo? Statistics)
        ParsePollFields(JsonElement attributes)
    {
        var mowerObj = attributes.GetProperty("mower");
        var activity = mowerObj.GetProperty("activity").GetString() ?? "UNKNOWN";
        var workAreaId = mowerObj.TryGetProperty("workAreaId", out var waIdEl) ? waIdEl.GetInt64() : 0L;
        var battery = attributes.GetProperty("battery").GetProperty("batteryPercent").GetInt32();
        var plannerNextStart = attributes.TryGetProperty("planner", out var plannerEl) &&
            plannerEl.TryGetProperty("nextStartTimestamp", out var nextStartEl)
            ? nextStartEl.GetInt64()
            : 0L;

        // Only the newest breadcrumb (positions[0]) - not the rest of that
        // poll's up-to-50-entry array. Investigated 2026-07-29 after real
        // dots turned up clearly outside their own work area on 3 different
        // mowers: a poll's full breadcrumb array can still hold the tail
        // end of the *previous* stay (e.g. several minutes parked near a
        // charger in a different work area) for many polls after the mower
        // has already moved on - confirmed by 3 real episodes where the
        // outlier count decayed by exactly 2 per poll (46->44->42->...),
        // the signature of a sliding 50-point buffer aging out stale
        // history. positions[0] is always exactly synchronized with that
        // same poll's own workAreaId, so it's the only entry in the array
        // actually guaranteed correct - confirmed empirically: doing this
        // dropped the outlier count to zero on real data.
        double? latitude = null, longitude = null;
        if (attributes.TryGetProperty("positions", out var positionsEl) &&
            positionsEl.ValueKind == JsonValueKind.Array && positionsEl.GetArrayLength() > 0)
        {
            var newest = positionsEl[0];
            if (newest.TryGetProperty("latitude", out var latEl) && newest.TryGetProperty("longitude", out var lonEl))
            {
                latitude = latEl.GetDouble();
                longitude = lonEl.GetDouble();
            }
        }

        var statistics = attributes.TryGetProperty("statistics", out var statsEl)
            ? statsEl.Deserialize<StatisticsInfo>()
            : null;

        return (activity, workAreaId, battery, plannerNextStart, latitude, longitude, statistics);
    }

    public static void AccumulateWorkAreaNames(JsonElement attributes, Dictionary<long, string> workAreaNames)
    {
        if (!attributes.TryGetProperty("workAreas", out var workAreasEl) || workAreasEl.ValueKind != JsonValueKind.Array)
        {
            return;
        }

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

    public static CalendarTask[]? TryGetNonEmptyCalendarTasks(JsonElement attributes)
        => attributes.TryGetProperty("calendar", out var calendarEl) &&
           calendarEl.Deserialize<CalendarInfo>() is { Tasks.Length: > 0 } calendarInfo
            ? calendarInfo.Tasks
            : null;
}

// Storage seam for one mower's data - poll history, WebSocket event log, and
// cached schedule. Shaped by what TrackingService/CoverageService/
// ScheduleService actually need (per the 2026-07-29 SQLite-migration
// discussion), not by today's JSONL file mechanics, so a future SQLite-backed
// implementation can drop in behind it with no call-site changes.
// JsonlMowerRepository below is that first, behavior-preserving
// implementation.
public interface IMowerRepository
{
    // Records one raw payload - a REST poll response (source "rest") or a
    // WebSocket event (source "event:<type>", e.g. "event:battery-event-v2",
    // or "event:ready" for the handshake) - unconditionally and
    // unprocessed, then derives whatever PollRecord-relevant fields that
    // payload's shape carries. See the 2026-07-30 SQLite-migration plan:
    // this is the one seam change from Phase 1's AppendPollAsync/
    // AppendEventAsync - both folded into a single write path since a REST
    // poll and a WebSocket event are now just two shapes of the same thing
    // ("we learned something, here's the raw proof"), sharing one caller
    // contract instead of two nearly-identical ones.
    Task RecordAsync(DateTimeOffset timestamp, string source, string rawJson, CancellationToken cancellationToken = default);

    // Re-derives whatever PollRecord-relevant data is stored from the raw
    // payloads RecordAsync already persisted, using the current extraction
    // logic - the recovery path when that logic turns out to have had a
    // bug, or a newly-added field needs backfilling into history that
    // predates the code change, without ever needing to touch/re-collect
    // the raw data itself. A no-op for a backend (like JsonlMowerRepository)
    // that already re-derives everything fresh on every GetHistory() call.
    Task RebuildObservationsAsync(CancellationToken cancellationToken = default);

    MowerHistory GetHistory();

    CalendarTask[] GetSchedule();
    void SaveSchedule(CalendarTask[] tasks);

    // Append-only, one row per calendar day, forever - "180 records for a
    // season" (see the 2026-07-29 discussion) is trivial at this scale, so
    // no pruning/compaction is attempted. Never call this twice for the same
    // Date; TrackingService.RunAsync guards against that itself rather than
    // pushing an upsert-vs-append decision down into the storage seam.
    Task AppendDailyStatisticsAsync(DailyStatisticsSnapshot snapshot, CancellationToken cancellationToken = default);
    IReadOnlyList<DailyStatisticsSnapshot> GetDailyStatisticsHistory();
}

public interface IMowerRepositoryFactory
{
    IMowerRepository ForMower(string mowerName);
}

public class JsonlMowerRepositoryFactory : IMowerRepositoryFactory
{
    public IMowerRepository ForMower(string mowerName) => new JsonlMowerRepository(mowerName);
}

// Behaves identically to the raw File.* calls it replaces - same
// track-<mower>.jsonl/events-<mower>.jsonl paths and line format, so
// existing history keeps working unchanged. Schedule caching moves from one
// shared schedule.json (all mowers in one dict, rewritten wholesale on every
// single poll from any of the independent 'track' daemons - a real write
// race between them) to one file per mower, seeded once from the old shared
// file if present so no cached schedule is lost in the switch.
public class JsonlMowerRepository(string mowerName) : IMowerRepository
{
    private readonly string _logPath = Storage.GetTrackLogPath(mowerName);
    private readonly string _eventLogPath = Storage.GetEventLogPath(mowerName);
    private readonly string _schedulePath = Storage.GetScheduleLogPath(mowerName);
    private readonly string _statisticsLogPath = Storage.GetStatisticsLogPath(mowerName);

    // "rest" writes a track-<mower>.jsonl line (as AppendPollAsync did
    // before this method existed); anything else (an "event:<type>"/
    // "event:ready" source) writes an events-<mower>.jsonl line instead
    // (as AppendEventAsync did) - same two files, same line formats,
    // unchanged, just reached through one method instead of two now that
    // SqliteMowerRepository's RawEvents table unifies them for real.
    // mowerId is no longer a parameter (RecordAsync is shaped by what the
    // SQLite backend's RawEvents table needs, which has no use for it - the
    // mower's identity is already implicit in which per-mower .db file this
    // is) - JSONL lines simply stop carrying that field going forward;
    // nothing ever read it back out of them.
    public async Task RecordAsync(DateTimeOffset timestamp, string source, string rawJson, CancellationToken cancellationToken = default)
    {
        Storage.EnsureDataDir();
        using var doc = JsonDocument.Parse(rawJson);
        if (source == "rest")
        {
            var record = new
            {
                timestamp,
                mowerName,
                bytes = Encoding.UTF8.GetByteCount(rawJson),
                response = doc.RootElement,
            };
            await File.AppendAllTextAsync(_logPath, JsonSerializer.Serialize(record) + Environment.NewLine, cancellationToken);
        }
        else
        {
            var record = new
            {
                timestamp,
                mowerName,
                message = doc.RootElement,
            };
            await File.AppendAllTextAsync(_eventLogPath, JsonSerializer.Serialize(record) + Environment.NewLine, cancellationToken);
        }
    }

    // GetHistory() below always re-derives everything fresh from
    // track-<mower>.jsonl on every call - there's no separate materialized
    // "Observations" table to go stale here, so nothing to rebuild.
    public Task RebuildObservationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    // Reads and groups track-<mower>.jsonl in one pass - same fields,
    // same grouping-by-(activity, workAreaId) logic previously duplicated
    // between TrackingService and CoverageService now live in one place.
    public MowerHistory GetHistory()
    {
        if (!File.Exists(_logPath))
        {
            return new MowerHistory([], new Dictionary<long, string>(), [], 0);
        }

        var polls = new List<PollRecord>();
        var workAreaNames = new Dictionary<long, string>();
        var latestCalendarTasks = Array.Empty<CalendarTask>();
        var skipped = 0;

        foreach (var line in File.ReadLines(_logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var timestamp = root.GetProperty("timestamp").GetDateTimeOffset();
                var attributes = root.GetProperty("response").GetProperty("data").GetProperty("attributes");

                var (activity, workAreaId, battery, plannerNextStart, latitude, longitude, statistics) =
                    RestSnapshotParser.ParsePollFields(attributes);
                polls.Add(new PollRecord(timestamp, activity, battery, workAreaId, plannerNextStart, latitude, longitude, statistics, timestamp));

                RestSnapshotParser.AccumulateWorkAreaNames(attributes, workAreaNames);
                if (RestSnapshotParser.TryGetNonEmptyCalendarTasks(attributes) is { } calendarTasks)
                {
                    latestCalendarTasks = calendarTasks;
                }
            }
            catch (Exception)
            {
                // Same tolerance as before - skip a malformed line rather than fail the whole read.
                skipped++;
            }
        }

        return new MowerHistory(polls, workAreaNames, latestCalendarTasks, skipped);
    }

    public CalendarTask[] GetSchedule()
    {
        if (!File.Exists(_schedulePath))
        {
            return MigrateFromSharedScheduleFile();
        }
        var entry = JsonSerializer.Deserialize<MowerSchedule>(File.ReadAllText(_schedulePath));
        return entry?.Tasks ?? [];
    }

    public void SaveSchedule(CalendarTask[] tasks)
    {
        Storage.EnsureDataDir();
        var entry = new MowerSchedule(mowerName, DateTimeOffset.Now, tasks);
        File.WriteAllText(_schedulePath, JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
    }

    // One-time seed from the old shared schedule.json (keyed by mower id,
    // one dict for all mowers) so moving to per-mower files doesn't blank
    // out an already-cached schedule. Harmless to leave in place indefinitely
    // - once the per-mower file exists, this path is never hit again for
    // that mower.
    private CalendarTask[] MigrateFromSharedScheduleFile()
    {
        var legacy = Storage.LoadSchedules();
        var match = legacy.Values.FirstOrDefault(s => s.MowerName == mowerName);
        if (match is null)
        {
            return [];
        }
        SaveSchedule(match.Tasks);
        return match.Tasks;
    }

    public async Task AppendDailyStatisticsAsync(DailyStatisticsSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Storage.EnsureDataDir();
        await File.AppendAllTextAsync(_statisticsLogPath, JsonSerializer.Serialize(snapshot) + Environment.NewLine, cancellationToken);
    }

    public IReadOnlyList<DailyStatisticsSnapshot> GetDailyStatisticsHistory()
    {
        if (!File.Exists(_statisticsLogPath))
        {
            return [];
        }

        var result = new List<DailyStatisticsSnapshot>();
        foreach (var line in File.ReadLines(_statisticsLogPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<DailyStatisticsSnapshot>(line) is { } snapshot)
                {
                    result.Add(snapshot);
                }
            }
            catch (Exception)
            {
                // Same tolerance as GetHistory() - skip a malformed line rather than fail the whole read.
            }
        }
        return result;
    }
}
