using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AutomowerConsole.Core;

// Called for every message received for the watched mower, including the
// initial {"ready": ..., "connectionId": ...} handshake (isReady true, type
// null). countThisConnection resets to 1 on every (re)connect, not across
// the whole RunAsync lifetime - a caller wanting a running total across
// reconnects keeps its own counter, same as EventTrackingService already did
// before this was factored out.
public delegate Task MowerEventHandler(bool isReady, string? type, string rawJson, int countThisConnection);

// Optional connection-lifecycle diagnostics (connected / server closed /
// proactive reconnect / error-then-retry) - a plain string, not a
// structured type, since callers only ever want to log it, each in their
// own established console style (EventTrackingService's timestamped lines
// vs. whatever the hybrid tracker wants) rather than a shared format
// imposed here.
public delegate void MowerEventDiagnostic(string message);

// Shared low-level WebSocket connect/reconnect/receive-loop mechanics for
// Husqvarna's event-push API - factored out of EventTrackingService
// (2026-07-30) so it and the hybrid event-consuming tracker share one
// reconnect implementation instead of two. See SKILL.md's "WebSocket /
// event-push API" section for the confirmed behavior this is built from -
// official 2h hard connection limit, the mower's own 10-min-idle throttle,
// no real server ping/pong (an empty message is the liveness signal).
public class MowerEventStream
{
    private const string WebSocketUrl = "wss://ws.openapi.husqvarna.dev/v1";

    // Proactive reconnect cadence - matches aioautomower's own observed
    // behavior (session.py reconnects every ~7195s regardless of errors),
    // which strongly suggested a server-side connection lifetime rather
    // than an arbitrary client choice, later confirmed officially (2h hard
    // limit). Also refreshes the access token along the way (see
    // AutomowerConnect.GetFreshAccessTokenAsync).
    public static readonly TimeSpan ProactiveReconnectAfter = TimeSpan.FromHours(2);

    // Runs until cancellationToken fires, reconnecting on any error (5s
    // delay) and proactively every ProactiveReconnectAfter. onDiagnostic is
    // best-effort connection-lifecycle logging, entirely optional.
    public async Task RunAsync(
        string mowerId, MowerEventHandler onMessage, MowerEventDiagnostic? onDiagnostic, CancellationToken cancellationToken)
    {
        var connect = AutomowerConnect.Instance;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ListenOnceAsync(connect, mowerId, onMessage, onDiagnostic, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                onDiagnostic?.Invoke($"WebSocket error: {ex.Message} - reconnecting in 5s");
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
    }

    // Runs one WebSocket connection until it closes, errors, or the
    // proactive reconnect timer fires. A real Ctrl+C (cancellationToken
    // itself) propagates out as OperationCanceledException for RunAsync's
    // loop to stop on; the internal proactive-reconnect timer is swallowed
    // here and just ends the connection cleanly so the caller loops around
    // again.
    private static async Task ListenOnceAsync(
        AutomowerConnect connect, string mowerId, MowerEventHandler onMessage, MowerEventDiagnostic? onDiagnostic, CancellationToken cancellationToken)
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
            onDiagnostic?.Invoke("connected");

            var buffer = new byte[16 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                var text = await ReceiveFullMessageAsync(ws, buffer, connectionCts.Token);
                if (text is null)
                {
                    onDiagnostic?.Invoke("server closed the connection");
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
                // Only events for the one mower being watched are surfaced -
                // not the whole account's firehose.
                var isReadyMessage = root.TryGetProperty("ready", out _);
                var matchesMower = root.TryGetProperty("id", out var idEl) && idEl.GetString() == mowerId;
                if (!isReadyMessage && !matchesMower)
                {
                    continue;
                }

                recordCount++;
                var type = isReadyMessage ? null : root.GetProperty("type").GetString();
                await onMessage(isReadyMessage, type, text, recordCount);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the proactive-reconnect timer fired, not a real Ctrl+C.
            onDiagnostic?.Invoke($"proactive reconnect ({ProactiveReconnectAfter.TotalHours:0}h elapsed)");
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
