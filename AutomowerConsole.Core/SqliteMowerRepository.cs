using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AutomowerConsole.Core;

// SQLite-backed IMowerRepository - one .db file per mower (Storage.
// GetMowerDbPath), per the 2026-07-30 storage-migration plan. RawEvents is
// the permanent, unprocessed source of truth (every REST poll response or
// WebSocket event, exactly as received); Observations is a *derived*,
// sparse, query-friendly table built from it - only the columns a given raw
// record actually carries get populated, everything else stays NULL. This
// is what avoids JsonlMowerRepository's alternative (every written line
// repeating every field, since JSONL has no concept of a partially-filled
// row) without needing any debounce/throttling logic: a position-only
// WebSocket event is just a small, genuinely non-duplicative insert.
//
// A connection is opened fresh per method call rather than held for the
// repository's lifetime - the idiomatic Dapper/SQLite pattern (a local file
// connection is cheap to open), and simpler than making IMowerRepository
// IDisposable, which today's many short-lived `repositoryFactory.
// ForMower(...)` call sites (CoverageService, ScheduleService, the web
// pages) don't expect to have to manage.
public class SqliteMowerRepository(string mowerName) : IMowerRepository
{
    private readonly string _dbPath = Storage.GetMowerDbPath(mowerName);

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS RawEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Timestamp TEXT NOT NULL,
            Source TEXT NOT NULL,
            RawJson TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_rawevents_timestamp ON RawEvents(Timestamp);

        CREATE TABLE IF NOT EXISTS Observations (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RawEventId INTEGER NOT NULL REFERENCES RawEvents(Id),
            Timestamp TEXT NOT NULL,
            Source TEXT NOT NULL,
            Activity TEXT NULL,
            WorkAreaId INTEGER NULL,
            BatteryPercent INTEGER NULL,
            Latitude REAL NULL,
            Longitude REAL NULL,
            PlannerNextStartTimestamp INTEGER NULL
        );
        CREATE INDEX IF NOT EXISTS idx_observations_timestamp ON Observations(Timestamp);

        CREATE TABLE IF NOT EXISTS DailyStatistics (
            Date TEXT PRIMARY KEY,
            CuttingBladeUsageTime INTEGER NOT NULL,
            DownTime INTEGER NOT NULL,
            NumberOfChargingCycles INTEGER NOT NULL,
            NumberOfCollisions INTEGER NOT NULL,
            TotalChargingTime INTEGER NOT NULL,
            TotalCuttingTime INTEGER NOT NULL,
            TotalDriveDistance INTEGER NOT NULL,
            TotalRunningTime INTEGER NOT NULL,
            TotalSearchingTime INTEGER NOT NULL,
            UpTime INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Schedule (
            Id INTEGER PRIMARY KEY CHECK (Id = 1),
            FetchedAt TEXT NOT NULL,
            TasksJson TEXT NOT NULL
        );
        """;

    private SqliteConnection OpenConnection()
    {
        Storage.EnsureDataDir();
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        // WAL mode - one writer (this process) and many readers (the web
        // app's own short-lived connections) can proceed concurrently
        // without blocking each other, unlike SQLite's default rollback
        // journal mode.
        connection.Execute("PRAGMA journal_mode=WAL;");
        connection.Execute(SchemaSql);
        return connection;
    }

    public async Task RecordAsync(DateTimeOffset timestamp, string source, string rawJson, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var rawEventId = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO RawEvents (Timestamp, Source, RawJson) VALUES (@Timestamp, @Source, @RawJson); SELECT last_insert_rowid();",
            new { Timestamp = timestamp.ToString("o"), Source = source, RawJson = rawJson },
            transaction);

        var observation = ObservationExtractor.Extract(source, rawJson);
        await connection.ExecuteAsync(
            """
            INSERT INTO Observations (RawEventId, Timestamp, Source, Activity, WorkAreaId, BatteryPercent, Latitude, Longitude, PlannerNextStartTimestamp)
            VALUES (@RawEventId, @Timestamp, @Source, @Activity, @WorkAreaId, @BatteryPercent, @Latitude, @Longitude, @PlannerNextStartTimestamp)
            """,
            new
            {
                RawEventId = rawEventId,
                Timestamp = timestamp.ToString("o"),
                Source = source,
                observation.Activity,
                observation.WorkAreaId,
                observation.BatteryPercent,
                observation.Latitude,
                observation.Longitude,
                observation.PlannerNextStartTimestamp,
            },
            transaction);

        transaction.Commit();
    }

    // Wipes and re-derives every Observations row from RawEvents using the
    // current ObservationExtractor logic - the recovery path if that logic
    // ever turns out to have had a bug, without needing to re-collect or
    // touch the raw data at all.
    public async Task RebuildObservationsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync("DELETE FROM Observations;", transaction: transaction);

        var rawRows = await connection.QueryAsync<(long Id, string Timestamp, string Source, string RawJson)>(
            "SELECT Id, Timestamp, Source, RawJson FROM RawEvents ORDER BY Timestamp;",
            transaction: transaction);

        foreach (var row in rawRows)
        {
            var observation = ObservationExtractor.Extract(row.Source, row.RawJson);
            await connection.ExecuteAsync(
                """
                INSERT INTO Observations (RawEventId, Timestamp, Source, Activity, WorkAreaId, BatteryPercent, Latitude, Longitude, PlannerNextStartTimestamp)
                VALUES (@RawEventId, @Timestamp, @Source, @Activity, @WorkAreaId, @BatteryPercent, @Latitude, @Longitude, @PlannerNextStartTimestamp)
                """,
                new
                {
                    RawEventId = row.Id,
                    row.Timestamp,
                    row.Source,
                    observation.Activity,
                    observation.WorkAreaId,
                    observation.BatteryPercent,
                    observation.Latitude,
                    observation.Longitude,
                    observation.PlannerNextStartTimestamp,
                },
                transaction);
        }

        transaction.Commit();
    }

    // PollRecord is a fully-populated-per-poll shape (matches JsonlMower
    // Repository's contract exactly, so SummarizeSessions/CoverageService
    // need no changes) - reconstructed here with a straightforward
    // carry-forward scan over Observations' sparse rows: each column keeps
    // its last known non-null value as the scan proceeds chronologically.
    // WorkAreaNames/LatestCalendarTasks aren't in Observations at all
    // (nothing queries them that granularly) - re-derived by parsing every
    // "rest"-sourced RawEvents row's full payload, the only source that
    // ever carries workAreas[]/calendar.
    public MowerHistory GetHistory()
    {
        using var connection = OpenConnection();

        // Queried as dynamic rows and mapped by hand, not Query<ObservationRow>
        // directly - a private record nested inside this class confused
        // Dapper's constructor-matching (confirmed: "no parameterless
        // constructor or one matching signature" at runtime even though
        // ObservationRow's own constructor matches the query's columns
        // exactly) - sidesteps relying on Dapper's reflection-based
        // materialization for this shape at all, same reasoning as
        // GetDailyStatisticsHistory's manual StatisticsInfo mapping below.
        var rows = connection.Query("SELECT * FROM Observations ORDER BY Timestamp;")
            .Select(r => (IDictionary<string, object?>)r)
            .Select(r => new ObservationRow(
                (long)r["Id"]!,
                (long)r["RawEventId"]!,
                (string)r["Timestamp"]!,
                (string)r["Source"]!,
                (string?)r["Activity"],
                (long?)r["WorkAreaId"],
                (int?)(long?)r["BatteryPercent"],
                (double?)r["Latitude"],
                (double?)r["Longitude"],
                (long?)r["PlannerNextStartTimestamp"]))
            .ToList();

        var polls = new List<PollRecord>(rows.Count);
        string? activity = null;
        long workAreaId = 0;
        int battery = 0;
        long plannerNextStart = 0;
        double? latitude = null;
        double? longitude = null;
        StatisticsInfo? statistics = null;

        foreach (var row in rows)
        {
            activity ??= "UNKNOWN";
            if (row.Activity is not null) activity = row.Activity;
            if (row.WorkAreaId is not null) workAreaId = row.WorkAreaId.Value;
            if (row.BatteryPercent is not null) battery = row.BatteryPercent.Value;
            if (row.PlannerNextStartTimestamp is not null) plannerNextStart = row.PlannerNextStartTimestamp.Value;
            if (row.Latitude is not null) latitude = row.Latitude;
            if (row.Longitude is not null) longitude = row.Longitude;
            if (row.Source == "rest")
            {
                // A REST row is always a full snapshot - its own statistics
                // (or lack of any) always wins over whatever carried
                // forward, rather than carrying an older REST poll's value
                // past a newer one that genuinely had none.
                statistics = TryParseStatistics(row.Source, GetRawJsonForObservation(connection, row.RawEventId));
            }

            polls.Add(new PollRecord(
                DateTimeOffset.Parse(row.Timestamp), activity, battery, workAreaId, plannerNextStart, latitude, longitude, statistics));
        }

        var workAreaNames = new Dictionary<long, string>();
        var latestCalendarTasks = Array.Empty<CalendarTask>();
        foreach (var rawJson in connection.Query<string>(
            "SELECT RawJson FROM RawEvents WHERE Source = 'rest' ORDER BY Timestamp;"))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var attributes = doc.RootElement.GetProperty("data").GetProperty("attributes");
                RestSnapshotParser.AccumulateWorkAreaNames(attributes, workAreaNames);
                if (RestSnapshotParser.TryGetNonEmptyCalendarTasks(attributes) is { } calendarTasks)
                {
                    latestCalendarTasks = calendarTasks;
                }
            }
            catch (Exception)
            {
                // Same tolerance as JsonlMowerRepository - skip a malformed
                // row rather than fail the whole read.
            }
        }

        return new MowerHistory(polls, workAreaNames, latestCalendarTasks, 0);
    }

    private static string GetRawJsonForObservation(SqliteConnection connection, long rawEventId)
        => connection.QuerySingle<string>("SELECT RawJson FROM RawEvents WHERE Id = @Id;", new { Id = rawEventId });

    private static StatisticsInfo? TryParseStatistics(string source, string rawJson)
    {
        if (source != "rest") return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var attributes = doc.RootElement.GetProperty("data").GetProperty("attributes");
            return attributes.TryGetProperty("statistics", out var statsEl) ? statsEl.Deserialize<StatisticsInfo>() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public CalendarTask[] GetSchedule()
    {
        using var connection = OpenConnection();
        var tasksJson = connection.QuerySingleOrDefault<string>("SELECT TasksJson FROM Schedule WHERE Id = 1;");
        return tasksJson is null ? [] : JsonSerializer.Deserialize<CalendarTask[]>(tasksJson) ?? [];
    }

    public void SaveSchedule(CalendarTask[] tasks)
    {
        using var connection = OpenConnection();
        connection.Execute(
            """
            INSERT INTO Schedule (Id, FetchedAt, TasksJson) VALUES (1, @FetchedAt, @TasksJson)
            ON CONFLICT(Id) DO UPDATE SET FetchedAt = @FetchedAt, TasksJson = @TasksJson;
            """,
            new { FetchedAt = DateTimeOffset.Now.ToString("o"), TasksJson = JsonSerializer.Serialize(tasks) });
    }

    public async Task AppendDailyStatisticsAsync(DailyStatisticsSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        var s = snapshot.Statistics;
        await connection.ExecuteAsync(
            """
            INSERT INTO DailyStatistics
                (Date, CuttingBladeUsageTime, DownTime, NumberOfChargingCycles, NumberOfCollisions,
                 TotalChargingTime, TotalCuttingTime, TotalDriveDistance, TotalRunningTime, TotalSearchingTime, UpTime)
            VALUES
                (@Date, @CuttingBladeUsageTime, @DownTime, @NumberOfChargingCycles, @NumberOfCollisions,
                 @TotalChargingTime, @TotalCuttingTime, @TotalDriveDistance, @TotalRunningTime, @TotalSearchingTime, @UpTime)
            """,
            new
            {
                Date = snapshot.Date.ToString("O"),
                s.CuttingBladeUsageTime,
                s.DownTime,
                s.NumberOfChargingCycles,
                s.NumberOfCollisions,
                s.TotalChargingTime,
                s.TotalCuttingTime,
                s.TotalDriveDistance,
                s.TotalRunningTime,
                s.TotalSearchingTime,
                s.UpTime,
            });
    }

    public IReadOnlyList<DailyStatisticsSnapshot> GetDailyStatisticsHistory()
    {
        using var connection = OpenConnection();
        // Queried as dynamic rows and mapped by hand, not Query<StatisticsInfo>
        // directly - StatisticsInfo's properties are { get; init; }, not a
        // positional record, and this sidesteps any doubt about Dapper's
        // reflection-based materialization of init-only properties rather
        // than relying on it.
        var rows = connection.Query("SELECT * FROM DailyStatistics ORDER BY Date;");
        var result = new List<DailyStatisticsSnapshot>();
        foreach (var row in rows)
        {
            IDictionary<string, object?> r = row;
            var statistics = new StatisticsInfo
            {
                CuttingBladeUsageTime = (long)r[nameof(StatisticsInfo.CuttingBladeUsageTime)]!,
                DownTime = (long)r[nameof(StatisticsInfo.DownTime)]!,
                NumberOfChargingCycles = (long)r[nameof(StatisticsInfo.NumberOfChargingCycles)]!,
                NumberOfCollisions = (long)r[nameof(StatisticsInfo.NumberOfCollisions)]!,
                TotalChargingTime = (long)r[nameof(StatisticsInfo.TotalChargingTime)]!,
                TotalCuttingTime = (long)r[nameof(StatisticsInfo.TotalCuttingTime)]!,
                TotalDriveDistance = (long)r[nameof(StatisticsInfo.TotalDriveDistance)]!,
                TotalRunningTime = (long)r[nameof(StatisticsInfo.TotalRunningTime)]!,
                TotalSearchingTime = (long)r[nameof(StatisticsInfo.TotalSearchingTime)]!,
                UpTime = (long)r[nameof(StatisticsInfo.UpTime)]!,
            };
            result.Add(new DailyStatisticsSnapshot(DateOnly.Parse((string)r["Date"]!), statistics));
        }
        return result;
    }

    private record ObservationRow(
        long Id, long RawEventId, string Timestamp, string Source,
        string? Activity, long? WorkAreaId, int? BatteryPercent, double? Latitude, double? Longitude, long? PlannerNextStartTimestamp);
}

public class SqliteMowerRepositoryFactory : IMowerRepositoryFactory
{
    public IMowerRepository ForMower(string mowerName) => new SqliteMowerRepository(mowerName);
}

// Maps one raw payload (a REST poll response, or a WebSocket event's own
// {"id":...,"type":...,"attributes":{...partial...}} shape) to whichever
// Observations columns it actually carries. A REST response always
// populates every column (RestSnapshotParser.ParsePollFields - same
// parsing JsonlMowerRepository uses); a WebSocket event only populates
// whatever its own event type carries - field shapes confirmed against
// real captured events 2026-07-30 (see events-AM308V-Nede.jsonl), nested
// the same way REST nests them, just partial:
//   event:mower-event-v2   -> attributes.mower.{activity,workAreaId}
//   event:battery-event-v2 -> attributes.battery.batteryPercent
//   event:position-event-v2 -> attributes.position.{latitude,longitude} (a
//     single point, not an array like REST's positions[] - confirmed, and
//     actually better for coverage purposes: no stale-buffer risk at all,
//     since there's no buffer to begin with)
//   event:planner-event-v2 -> attributes.planner.nextStartTimestamp
// Every other event type (calendar/cuttingHeight/headlights/message) and
// the "ready" handshake carry nothing Observations tracks.
internal static class ObservationExtractor
{
    public static (string? Activity, long? WorkAreaId, int? BatteryPercent, double? Latitude, double? Longitude, long? PlannerNextStartTimestamp)
        Extract(string source, string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        if (source == "rest")
        {
            var attributes = root.GetProperty("data").GetProperty("attributes");
            var (activity, workAreaId, battery, plannerNextStart, latitude, longitude, _) = RestSnapshotParser.ParsePollFields(attributes);
            return (activity, workAreaId, battery, latitude, longitude, plannerNextStart);
        }

        if (!root.TryGetProperty("attributes", out var eventAttributes))
        {
            // The "ready" handshake (no "attributes" at all) - nothing to extract.
            return (null, null, null, null, null, null);
        }

        return source switch
        {
            "event:mower-event-v2" => ExtractMowerEvent(eventAttributes),
            "event:battery-event-v2" => (null, null, GetInt(eventAttributes, "battery", "batteryPercent"), null, null, null),
            "event:position-event-v2" => ExtractPositionEvent(eventAttributes),
            "event:planner-event-v2" => (null, null, null, null, null, GetLong(eventAttributes, "planner", "nextStartTimestamp")),
            _ => (null, null, null, null, null, null),
        };
    }

    private static (string?, long?, int?, double?, double?, long?) ExtractMowerEvent(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("mower", out var mower))
        {
            return (null, null, null, null, null, null);
        }
        var activity = mower.TryGetProperty("activity", out var a) ? a.GetString() : null;
        var workAreaId = mower.TryGetProperty("workAreaId", out var w) ? w.GetInt64() : (long?)null;
        return (activity, workAreaId, null, null, null, null);
    }

    private static (string?, long?, int?, double?, double?, long?) ExtractPositionEvent(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("position", out var position))
        {
            return (null, null, null, null, null, null);
        }
        var lat = position.TryGetProperty("latitude", out var latEl) ? latEl.GetDouble() : (double?)null;
        var lon = position.TryGetProperty("longitude", out var lonEl) ? lonEl.GetDouble() : (double?)null;
        return (null, null, null, lat, lon, null);
    }

    private static int? GetInt(JsonElement attributes, string obj, string field)
        => attributes.TryGetProperty(obj, out var o) && o.TryGetProperty(field, out var v) ? v.GetInt32() : null;

    private static long? GetLong(JsonElement attributes, string obj, string field)
        => attributes.TryGetProperty(obj, out var o) && o.TryGetProperty(field, out var v) ? v.GetInt64() : null;
}
