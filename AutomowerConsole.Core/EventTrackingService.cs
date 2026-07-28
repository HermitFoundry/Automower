using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AutomowerConsole.Core;

// Experimental: logs whatever the Husqvarna WebSocket event-push API
// actually sends for one mower, over however long this runs - a genuine
// "let's see what we get" data-gathering exercise (see SKILL.md's
// "WebSocket / event-push API" section for the confirmed-usable research
// this is built from), not a replacement for 'track' yet. Deliberately
// simple/thin compared to aioautomower's own reconnect handling
// (session.py) - good enough for an unattended day-long experiment, not
// tuned as a production client.
public class EventTrackingService
{
    private const string WebSocketUrl = "wss://ws.openapi.husqvarna.dev/v1";

    // Proactive reconnect cadence - matches aioautomower's own observed
    // behavior (session.py reconnects every ~7195s regardless of errors),
    // which strongly suggested a server-side connection lifetime rather
    // than an arbitrary client choice. Also refreshes the access token
    // along the way (see AutomowerConnect.GetFreshAccessTokenAsync).
    private static readonly TimeSpan ProactiveReconnectAfter = TimeSpan.FromHours(2);

    public async Task RunAsync(string mowerId, string mowerName, CancellationToken cancellationToken)
    {
        var connect = AutomowerConnect.Instance;
        Storage.EnsureDataDir();
        var logPath = Storage.GetEventLogPath(mowerName);

        Console.WriteLine($"Event-tracking {mowerName} via WebSocket. Logging every event for this mower to {logPath}. Press Ctrl+C to stop.");
        Console.WriteLine($"Reconnects automatically on any disconnect, and proactively every {ProactiveReconnectAfter.TotalHours:0}h.");

        var totalRecordCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                totalRecordCount += await ListenOnceAsync(connect, mowerId, mowerName, logPath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] WebSocket error: {ex.Message} - reconnecting in 5s");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Console.WriteLine($"Stopped. {totalRecordCount} event(s) logged in total. Log file: {logPath}");
    }

    // Runs one WebSocket connection until it closes, errors, or the
    // proactive reconnect timer fires - returns the count of events logged
    // during just this connection. A real Ctrl+C (cancellationToken itself)
    // propagates out as OperationCanceledException for RunAsync's loop to
    // stop on; the internal proactive-reconnect timer is swallowed here and
    // just ends the connection cleanly so the caller loops around again.
    private static async Task<int> ListenOnceAsync(AutomowerConnect connect, string mowerId, string mowerName, string logPath, CancellationToken cancellationToken)
    {
        var token = await connect.GetFreshAccessTokenAsync();

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionCts.CancelAfter(ProactiveReconnectAfter);

        var recordCount = 0;
        try
        {
            await ws.ConnectAsync(new Uri(WebSocketUrl), cancellationToken);
            Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] connected");

            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                var text = await ReceiveFullMessageAsync(ws, buffer, connectionCts.Token);
                if (text is null)
                {
                    Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] server closed the connection");
                    break;
                }

                // Empty messages are a liveness signal, not real data - see
                // aioautomower's session.py, which treats one the same way
                // ("not real ping/pong, but a way to check if the websocket
                // is still alive").
                if (text.Length == 0)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                // Two shapes: the initial {"ready": ..., "connectionId": ...}
                // handshake (no "id" field at all), and per-mower event
                // deltas {"id": "...", "type": "...", "attributes": {...}}.
                // Logging the handshake too - it's part of "what do we
                // actually get from this API", the whole point of this
                // command - but only events for the one mower being
                // watched, not the whole account's firehose.
                var isReadyMessage = root.TryGetProperty("ready", out _);
                var matchesMower = root.TryGetProperty("id", out var idEl) && idEl.GetString() == mowerId;
                if (!isReadyMessage && !matchesMower)
                {
                    continue;
                }

                var record = new
                {
                    timestamp = DateTimeOffset.Now,
                    mowerId,
                    mowerName,
                    message = root,
                };
                await File.AppendAllTextAsync(logPath, JsonSerializer.Serialize(record) + Environment.NewLine, cancellationToken);
                recordCount++;

                var summary = isReadyMessage ? "ready" : root.GetProperty("type").GetString();
                Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] logged ({summary}) - {recordCount} event(s) this connection");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the proactive-reconnect timer fired, not a real Ctrl+C.
            Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] proactive reconnect ({ProactiveReconnectAfter.TotalHours:0}h elapsed)");
        }
        finally
        {
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnecting", closeCts.Token);
                }
                catch
                {
                    // Best-effort - a close that doesn't complete cleanly
                    // shouldn't block reconnecting.
                }
            }
        }

        return recordCount;
    }

    // A single WebSocket "message" can arrive as several frames
    // (EndOfMessage false until the last one) - collects them all before
    // handing back one complete text payload. Null return means the
    // connection was closed by the server.
    private static async Task<string?> ReceiveFullMessageAsync(ClientWebSocket ws, byte[] buffer, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
