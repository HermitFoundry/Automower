using System.Text.Json;

namespace AutomowerConsole.Core;

// Event-driven mower tracking, per the 2026-07-30 hybrid design: WebSocket
// events (via the shared MowerEventStream) drive the fast-changing fields
// (activity/work area/battery/position) with near-instant precision instead
// of TrackingService.RunAsync's poll-interval latency; a much-reduced-
// frequency REST poll (Config.RestRefreshIntervalSeconds) runs underneath
// purely to keep statistics (for daily-statistics/seasons) and the cached
// schedule fresh, since events never carry either. Unlike RunAsync's fixed-
// shape "every write is a full snapshot" design, every WebSocket event here
// is just a cheap, genuinely sparse RecordAsync call - no debounce or
// merge-cache logic needed at all, since SqliteMowerRepository's RawEvents/
// Observations schema was specifically designed around this (see
// docs/database-schema.md). Runs two loops concurrently: the WebSocket
// event stream and the REST-refresh timer, until cancellationToken fires.
public class HybridTrackingService(ScheduleService schedule, IMowerRepositoryFactory repositoryFactory)
{
    private readonly MowerEventStream _stream = new();

    public async Task RunAsync(string mowerId, string mowerName, Config config, CancellationToken cancellationToken)
    {
        var connect = AutomowerConnect.Instance;
        var repository = repositoryFactory.ForMower(mowerName);
        await connect.AuthenticateAsync();

        Console.WriteLine($"Hybrid-tracking {mowerName}: WebSocket events drive live status, REST refresh every " +
                           $"{config.RestRefreshIntervalSeconds}s keeps statistics/schedule current. Press Ctrl+C to stop.");

        var eventCount = 0;

        var restRefreshTask = RunRestRefreshLoopAsync(connect, mowerId, mowerName, repository, config, cancellationToken);
        var eventTask = _stream.RunAsync(
            mowerId,
            async (isReady, type, rawJson, countThisConnection) =>
            {
                var source = isReady ? "event:ready" : $"event:{type}";
                await repository.RecordAsync(DateTimeOffset.Now, source, rawJson, cancellationToken);
                eventCount++;

                // Only mower-event-v2 (activity/work-area transitions) gets
                // its own console line - battery/position tick far more
                // often than a human watching a tmux session would want
                // printed (matches 'track' only printing once per poll, not
                // once per underlying HTTP response field).
                if (type == "mower-event-v2")
                {
                    Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {DescribeMowerEvent(rawJson)} " +
                                       $"({eventCount} event(s) total this connection)");
                }
            },
            message => Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}"),
            cancellationToken);

        await Task.WhenAll(restRefreshTask, eventTask);

        Console.WriteLine($"Stopped. {eventCount} WebSocket event(s) recorded this session.");
    }

    private static string DescribeMowerEvent(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("mower", out var mower))
        {
            return "mower-event-v2 (no mower attributes)";
        }

        var parts = new List<string>();
        if (mower.TryGetProperty("activity", out var activity)) parts.Add($"activity={activity.GetString()}");
        if (mower.TryGetProperty("workAreaId", out var workAreaId)) parts.Add($"workAreaId={workAreaId.GetInt64()}");
        if (mower.TryGetProperty("state", out var state)) parts.Add($"state={state.GetString()}");
        return parts.Count > 0 ? string.Join(", ", parts) : "mower-event-v2 (no recognized fields)";
    }

    // Same shape as TrackingService.RunAsync's own poll loop (try/catch per
    // iteration, Task.Delay at the end also try/catch) - deliberately
    // consistent style, even though this one runs far less often.
    private async Task RunRestRefreshLoopAsync(
        AutomowerConnect connect, string mowerId, string mowerName, IMowerRepository repository, Config config, CancellationToken cancellationToken)
    {
        // Same startup catch-up RunAsync itself uses - covers gaps from
        // restarts or extended offline stretches (e.g. winter storage),
        // which just show up as a gap in the daily-statistics table, no
        // special "season" handling needed (see TrackingService.
        // GroupIntoSeasons' zero-baseline exception for the related case).
        var (lastKnownDate, lastKnownStatistics, lastStoredStatisticsDate) =
            await TrackingService.BackfillDailyStatisticsAsync(repository, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var timestamp = DateTimeOffset.Now;
            try
            {
                var raw = await connect.GetMowerRawAsync(mowerId);
                using var doc = JsonDocument.Parse(raw);
                var attributes = doc.RootElement.GetProperty("data").GetProperty("attributes");

                await repository.RecordAsync(timestamp, "rest", raw, cancellationToken);

                var tasks = attributes.TryGetProperty("calendar", out var calendarElement)
                    ? calendarElement.Deserialize<CalendarInfo>()?.Tasks ?? []
                    : [];
                schedule.SaveScheduleForMower(mowerName, tasks);

                var newStatistics = attributes.TryGetProperty("statistics", out var statsEl) ? statsEl.Deserialize<StatisticsInfo>() : null;
                (lastKnownDate, lastKnownStatistics, lastStoredStatisticsDate) = await TrackingService.CheckDayRolloverAsync(
                    repository, lastKnownDate, lastKnownStatistics, lastStoredStatisticsDate, timestamp, newStatistics, cancellationToken);

                Console.WriteLine($"[{timestamp:yyyy-MM-dd HH:mm:ss}] REST refresh - statistics/schedule updated, next in {config.RestRefreshIntervalSeconds}s");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{timestamp:yyyy-MM-dd HH:mm:ss}] REST refresh failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(config.RestRefreshIntervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
