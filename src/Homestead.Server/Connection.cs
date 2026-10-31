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
/// changed only under the hub's lock.
/// </summary>
internal sealed class Connection(int id, WebSocket socket)
{
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _drop = new();

    public int Id { get; } = id;

    /// <summary>Set by the handshake (<see cref="ServerMessages.Hello"/>); null before.</summary>
    public string? Name { get; set; }

    public GameFingerprint? Fingerprint { get; set; }

    public Game? Game { get; set; }

    /// <summary>The player this computer plays in <see cref="Game"/>.</summary>
    public int Seat { get; set; }

    public void Send(string message) => _outgoing.Writer.TryWrite(message);

    /// <summary>Ends the connection once what is queued is sent (a refused handshake, a flood).</summary>
    public void Drop()
    {
        _outgoing.Writer.TryComplete();
    }

    /// <summary>The messages as they come; null for one that is not a JSON object. Ends with the connection or a too long message.</summary>
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
            catch (Exception e) when (e is OperationCanceledException or WebSocketException)
            {
                yield break;
            }
            if (result.MessageType == WebSocketMessageType.Close)
                yield break;
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
    /// messages (a refusal) before the other side has read them.
    /// </summary>
    public async Task WriteAsync(CancellationToken cancel)
    {
        try
        {
            await foreach (string message in _outgoing.Reader.ReadAllAsync(cancel))
                await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancel);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            // Gone.
        }
        await CloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        _drop.CancelAfter(TimeSpan.FromSeconds(2));
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(status, reason, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception e) when (e is WebSocketException or TimeoutException or ObjectDisposedException)
        {
            // Gone already.
        }
    }
}
