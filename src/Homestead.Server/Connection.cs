using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Homestead.Simulation.Lockstep;

namespace Homestead.Server;

/// <summary>
/// One computer's WebSocket: messages are read one after the other (<see cref="ReadAsync"/>), sent from a queue by a task of
/// their own (one send at a time, as WebSockets need). What the hub knows of it – name, fingerprint, game and seat – is
/// changed only under the hub's lock. Both directions are limited (<see cref="ServerLimits"/>): whoever sends too much or does
/// not read what it gets is dropped, and neither can hold memory or a task of the server.
/// </summary>
internal sealed class Connection(int id, WebSocket socket, ServerLimits limits)
{
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _drop = new();
    private readonly TokenBucket _messages = new(limits.MessagesPerSecond, limits.MessageBurst);
    private readonly TokenBucket _bytes = new(limits.BytesPerSecond, limits.ByteBurst);
    private long _queuedBytes;

    public int Id { get; } = id;

    /// <summary>Set by the handshake (<see cref="ServerMessages.Hello"/>); null before.</summary>
    public string? Name { get; set; }

    public GameFingerprint? Fingerprint { get; set; }

    public Game? Game { get; set; }

    /// <summary>The player this computer plays in <see cref="Game"/>.</summary>
    public int Seat { get; set; }

    /// <summary>When it connected and when its last message came (<see cref="Environment.TickCount64"/>).</summary>
    public long Connected { get; } = Environment.TickCount64;

    public long LastHeard { get; set; } = Environment.TickCount64;

    /// <summary>On the way out: what it still sends does not count any more.</summary>
    public bool IsDropped { get; private set; }

    /// <summary>Takes a message's cost from its budget; false: over the limit.</summary>
    public bool Charge(double cost) => _messages.Take(cost);

    public void Send(string message)
    {
        if (Interlocked.Add(ref _queuedBytes, message.Length) > limits.MaxQueuedBytes)
            Abort();
        else
            _outgoing.Writer.TryWrite(message);
    }

    /// <summary>Ends the connection once what is queued is sent (a refused handshake, a flood).</summary>
    public void Drop()
    {
        IsDropped = true;
        _outgoing.Writer.TryComplete();
    }

    /// <summary>
    /// Ends the connection now: it does not take what is sent to it. Called under the hub's lock, possibly while a game's players
    /// are walked through: the cancellation must not run the reader's continuation on this thread.
    /// </summary>
    private void Abort()
    {
        Drop();
        _ = _drop.CancelAsync();
    }

    /// <summary>The messages as they come; null for one that is not a JSON object. Ends with the connection, a too long message or a flood.</summary>
    public async IAsyncEnumerable<JsonObject?> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _drop.Token);
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, linked.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException)
            {
                yield break;
            }
            if (result.MessageType == WebSocketMessageType.Close)
                yield break;
            // Per frame, not per message: a message in a thousand empty pieces costs as much.
            if (!Charge(1) || !_bytes.Take(result.Count))
            {
                await CloseAsync(WebSocketCloseStatus.PolicyViolation, "too much");
                yield break;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > ServerMessages.MaxLength)
            {
                await CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too long");
                yield break;
            }
            if (!result.EndOfMessage)
                continue;
            JsonObject? parsed;
            try
            {
                parsed = JsonNode.Parse(message.ToArray()) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                parsed = null;
            }
            message.SetLength(0);
            yield return parsed;
        }
    }

    /// <summary>
    /// Sends what is queued until the connection is dropped or gone, then closes it – and lets <see cref="ReadAsync"/> wait up
    /// to two seconds for the other side's close: a socket let go before that may be reset, and a reset can throw away the last
    /// messages (a refusal) before the other side has read them. A send stuck longer than the limit (the other side reads
    /// nothing) gives up, or the connection could never end.
    /// </summary>
    public async Task WriteAsync(CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _drop.Token);
        try
        {
            await foreach (string message in _outgoing.Reader.ReadAllAsync(linked.Token))
            {
                Interlocked.Add(ref _queuedBytes, -message.Length);
                using var send = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                send.CancelAfter(TimeSpan.FromSeconds(limits.SendTimeoutSeconds));
                await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, send.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException)
        {
            // Gone.
        }
        await CloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        if (!_drop.IsCancellationRequested)
            _drop.CancelAfter(TimeSpan.FromSeconds(2));
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(status, reason, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception e) when (e is WebSocketException or TimeoutException or ObjectDisposedException or IOException)
        {
            // Gone already.
        }
    }
}
