namespace AutomowerConsole.Core;

// Experimental: logs whatever the Husqvarna WebSocket event-push API
// actually sends for one mower, over however long this runs - a genuine
// "let's see what we get" data-gathering exercise (see SKILL.md's
// "WebSocket / event-push API" section for the confirmed-usable research
// this is built from), not a replacement for 'track' yet. Connection
// mechanics (connect/reconnect/2h-proactive-reconnect/message-framing) live
// in the shared MowerEventStream, used by this and the hybrid tracker
// alike; this class only owns turning each message into a RecordAsync call
// plus its own console diagnostics.
public class EventTrackingService(IMowerRepositoryFactory repositoryFactory)
{
    private readonly MowerEventStream _stream = new();

    public async Task RunAsync(string mowerId, string mowerName, CancellationToken cancellationToken)
    {
        var repository = repositoryFactory.ForMower(mowerName);

        Console.WriteLine($"Event-tracking {mowerName} via WebSocket. Logging every event for this mower to {Storage.GetEventLogPath(mowerName)}. Press Ctrl+C to stop.");
        Console.WriteLine($"Reconnects automatically on any disconnect, and proactively every {MowerEventStream.ProactiveReconnectAfter.TotalHours:0}h.");

        var totalRecordCount = 0;

        // MowerEventStream.RunAsync already swallows cancellation internally
        // (returns normally once the caller's token fires, same as this
        // method's own reconnect loop did before that logic moved there) -
        // nothing to catch here, just fall through to the summary below.
        await _stream.RunAsync(
            mowerId,
            async (isReady, type, rawJson, countThisConnection) =>
            {
                var source = isReady ? "event:ready" : $"event:{type}";
                await repository.RecordAsync(DateTimeOffset.Now, source, rawJson, cancellationToken);
                totalRecordCount++;
                var summary = isReady ? "ready" : type;
                Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] logged ({summary}) - {countThisConnection} event(s) this connection");
            },
            message => Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}"),
            cancellationToken);

        Console.WriteLine($"Stopped. {totalRecordCount} event(s) logged in total. Log file: {Storage.GetEventLogPath(mowerName)}");
    }
}
