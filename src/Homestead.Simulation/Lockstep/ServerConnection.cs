using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Homestead.Simulation.Buildings;

namespace Homestead.Simulation.Lockstep;

/// <summary>The server turned the computer away: another protocol, program or data (<see cref="Reasons"/>), or a full game.</summary>
public sealed class ServerRefusedException(IReadOnlyList<string> reasons) : Exception("refused by the server: " + string.Join("; ", reasons))
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>
/// A computer's connection to the server (WebSocket, <see cref="ServerMessages"/>): connected with <see cref="ConnectAsync"/>
/// (the handshake checks protocol and program), then lobby requests (<see cref="ListGames"/>, <see cref="Create"/>,
/// <see cref="Join"/>, <see cref="Leave"/>, <see cref="Start"/>) whose answers come in through <see cref="Receive"/> – polled
/// by the game loop, like the turns: once started, <see cref="Link"/> carries the <see cref="LockstepSession"/>'s inputs and
/// bundles. Messages are read on a background task; nothing here touches the world.
/// </summary>
public sealed class ServerConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    /// <summary>Messages come in and not yet taken (all but bundles); the reading task adds, the game loop takes – under its lock.</summary>
    private readonly List<JsonObject> _messages = new();
    private readonly ConcurrentQueue<string> _bundles = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reading, _writing;

    private ServerConnection(ClientWebSocket socket)
    {
        _socket = socket;
        _reading = Task.Run(ReadAsync);
        _writing = Task.Run(WriteAsync);
        Link = new BundleLink(this);
    }

    /// <summary>The turns' way to the server, for <see cref="LockstepSession"/>.</summary>
    public ILockstepLink Link { get; }

    /// <summary>Why the connection ended (null while open).</summary>
    public string? ClosedBecause { get; private set; }

    public bool IsOpen => ClosedBecause is null;

    /// <summary>Connects to the server (e.g. <c>ws://host:8080/ws</c>) and says hello; the server checks the protocol.</summary>
    /// <exception cref="ServerRefusedException">The server does not take this computer.</exception>
    public static async Task<ServerConnection> ConnectAsync(Uri server, string name, GameFingerprint fingerprint, CancellationToken cancel = default)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(server, cancel);
        var connection = new ServerConnection(socket);
        try
        {
            connection.Send(ServerMessages.Hello, new JsonObject { ["name"] = name, ["fingerprint"] = JsonNode.Parse(fingerprint.ToJson()) });
            var answer = await connection.WaitForAsync([ServerMessages.Welcome, ServerMessages.Refused], TimeSpan.FromSeconds(10), cancel);
            if (ServerMessages.TypeOf(answer) == ServerMessages.Refused)
                throw new ServerRefusedException(Reasons(answer));
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>The reasons of a <see cref="ServerMessages.Refused"/> message.</summary>
    public static IReadOnlyList<string> Reasons(JsonObject refused) =>
        (refused["reasons"] as JsonArray ?? []).Take(MaxReasons).Select(r => r is JsonValue value && value.TryGetValue(out string? text) ? PlayerText.Clean(text, 200) : "")
        .ToArray();

    private const int MaxReasons = 20;

    /// <summary>Messages kept for the game loop: an honest server never gets near (the game loop takes them every frame), so more end the connection.</summary>
    private const int MaxQueued = 100_000;

    public void ListGames() => Send(ServerMessages.List);

    /// <summary>Opens a game: its human players are the seats, this computer takes the first.</summary>
    public void Create(string name, GameSetup setup) =>
        Send(ServerMessages.Create, new JsonObject { ["name"] = name, ["setup"] = JsonNode.Parse(setup.ToJson()) });

    public void Join(string game) => Send(ServerMessages.Join, new JsonObject { ["game"] = game });

    public void Leave() => Send(ServerMessages.Leave);

    /// <summary>The own people and color (palette index) in the lobby; null: drawn when the game starts.</summary>
    public void Choose(Civilization? people, int? color) =>
        Send(ServerMessages.Choose, new JsonObject { ["people"] = people?.ToString(), ["color"] = color });

    /// <summary>The host starts the game once every seat is taken.</summary>
    public void Start() => Send(ServerMessages.Start);

    /// <summary>The messages come in since the last call (all but the turns' bundles), in order.</summary>
    public IReadOnlyList<JsonObject> Receive()
    {
        lock (_messages)
        {
            var messages = _messages.ToList();
            _messages.Clear();
            return messages;
        }
    }

    /// <summary>Waits for the next message of one of the kinds (others stay for <see cref="Receive"/>), for tools and tests.</summary>
    /// <exception cref="TimeoutException">None came in time, or the connection ended.</exception>
    public async Task<JsonObject> WaitForAsync(IReadOnlyCollection<string> types, TimeSpan timeout, CancellationToken cancel = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_messages)
            {
                int index = _messages.FindIndex(m => types.Contains(ServerMessages.TypeOf(m)));
                if (index >= 0)
                {
                    var message = _messages[index];
                    _messages.RemoveAt(index);
                    return message;
                }
            }
            if (DateTime.UtcNow >= until || !IsOpen)
                throw new TimeoutException($"no {string.Join("/", types)} from the server{(IsOpen ? "" : $" ({ClosedBecause})")}");
            await Task.Delay(5, cancel);
        }
    }

    private void Send(string type, JsonObject? fields = null) => _outgoing.Writer.TryWrite(ServerMessages.Of(type, fields));

    private async Task WriteAsync()
    {
        try
        {
            await foreach (string message in _outgoing.Reader.ReadAllAsync(_stop.Token))
                await _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, _stop.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            ClosedBecause ??= e.Message;
        }
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Answered, so the server can let go of the connection cleanly.
                    if (_socket.State == WebSocketState.CloseReceived)
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    ClosedBecause ??= "closed by the server";
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > ServerMessages.MaxLength)
                {
                    ClosedBecause ??= "message too long";
                    return;
                }
                if (!result.EndOfMessage)
                    continue;
                if (JsonNode.Parse(message.ToArray()) is JsonObject received)
                {
                    if (ServerMessages.TypeOf(received) == ServerMessages.Bundle && received["bundle"] is JsonObject bundle)
                        _bundles.Enqueue(bundle.ToJsonString());
                    else
                        lock (_messages)
                            _messages.Add(received);
                    if (_bundles.Count + _messages.Count > MaxQueued)
                    {
                        ClosedBecause ??= "too many messages";
                        return;
                    }
                }
                message.SetLength(0);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Whatever a server sends: this task ending quietly would look like a server keeping silent.
            ClosedBecause ??= e.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception e) when (e is WebSocketException or TimeoutException or ObjectDisposedException)
        {
            // Gone already.
        }
        _stop.Cancel();
        await Task.WhenAll(_reading, _writing).WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
        ClosedBecause ??= "closed";
        _socket.Dispose();
    }

    private sealed class BundleLink(ServerConnection connection) : ILockstepLink
    {
        public void Send(string message) =>
            connection._outgoing.Writer.TryWrite($"{{\"type\":\"{ServerMessages.Input}\",\"input\":{message}}}");

        public IReadOnlyList<string> Receive()
        {
            var bundles = new List<string>();
            while (connection._bundles.TryDequeue(out string? bundle))
                bundles.Add(bundle);
            return bundles;
        }
    }
}
