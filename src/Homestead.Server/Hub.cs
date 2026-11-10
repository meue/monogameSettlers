using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Homestead.Simulation;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Lockstep;

namespace Homestead.Server;

/// <summary>A game on the server: in the lobby until the host starts it, then its turns go through <see cref="Relay"/>.</summary>
internal sealed class Game(string id, string name, Connection host, GameSetup setup, int[] seats)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public Connection Host { get; } = host;

    /// <summary>The setup with the host's fingerprint: every computer joining must have the same program and data.</summary>
    public GameSetup Setup { get; set; } = setup;

    /// <summary>The human players: one computer each.</summary>
    public int[] Seats { get; } = seats;

    public Dictionary<int, Connection> Taken { get; } = new();
    public TurnRelay? Relay { get; set; }
    public bool Started => Relay is not null;

    /// <summary>How many of the relay's desyncs are in the log already.</summary>
    public int DesyncsLogged { get; set; }

    /// <summary>When the last turn was handed out (<see cref="Environment.TickCount64"/>).</summary>
    public long LastTurn { get; set; }
}

/// <summary>
/// The server's state and rules (<see cref="ServerMessages"/>): computers say hello (another network protocol is refused), list,
/// open and join games – joining needs the host's program and game data (<see cref="GameFingerprint"/>), the reasons go back
/// otherwise – and the host starts a game once every seat is taken: each computer gets the setup, its player and the
/// <see cref="LockstepOptions"/>, then the turns go through a <see cref="TurnRelay"/>. Leaving the lobby frees the seat (the
/// host leaving closes the game); leaving a running game is told to the others, who go on without the player (a computer player takes
/// over on every computer, see <see cref="TurnRelay.Remove"/>). Everything happens under one lock; sending is queued per connection.
/// Nothing a computer sends is trusted (docs/MULTIPLAYER.md): whatever it says is checked here or by the relay, whoever breaks a
/// rule no honest client breaks is dropped, and <see cref="ServerLimits"/> bounds what anybody can hold on to.
/// </summary>
public sealed class Hub(LockstepOptions options, ServerLimits limits, ILogger<Hub> log)
{
    /// <summary>What a lobby request costs of the message budget, a turn's input costing 1: they do more work and bring larger answers.</summary>
    private const double LobbyCost = 5;

    /// <summary>Games in one list, refusal reasons in one message: to stay well below <see cref="ServerMessages.MaxLength"/>.</summary>
    private const int MaxListed = 200, MaxReasons = 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, Game> _games = new();
    private readonly HashSet<Connection> _connections = new();
    private readonly Dictionary<string, int> _addresses = new();
    private int _admitted;
    private int _nextId;

    /// <summary>Games and connections, for the status line.</summary>
    public string Status
    {
        get
        {
            lock (_gate)
                return $"{_games.Count} games ({_games.Values.Count(g => g.Started)} running), {_connections.Count} connected";
        }
    }

    /// <summary>
    /// Makes room for a connection from the address before its WebSocket is accepted; false: too many. Every true needs its
    /// <see cref="Release"/>.
    /// </summary>
    public bool Admit(string address)
    {
        lock (_gate)
        {
            int fromAddress = _addresses.GetValueOrDefault(address);
            if (_admitted >= limits.MaxConnections || fromAddress >= limits.MaxConnectionsPerAddress)
                return false;
            _admitted++;
            _addresses[address] = fromAddress + 1;
            return true;
        }
    }

    public void Release(string address)
    {
        lock (_gate)
        {
            _admitted--;
            if (_addresses[address] > 1)
                _addresses[address]--;
            else
                _addresses.Remove(address);
        }
    }

    public async Task RunAsync(WebSocket socket, CancellationToken cancel)
    {
        Connection connection;
        lock (_gate)
            _connections.Add(connection = new Connection(++_nextId, socket, limits));
        var writing = connection.WriteAsync(cancel);
        try
        {
            await foreach (var message in connection.ReadAsync(cancel))
                lock (_gate)
                {
                    if (connection.IsDropped)
                        break;
                    connection.LastHeard = Environment.TickCount64;
                    try
                    {
                        Handle(connection, message);
                    }
                    catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or System.Text.Json.JsonException)
                    {
                        // A field of the wrong kind (a number for a name, …): the message is answered, the connection stays.
                        log.LogDebug("{Id}: unreadable message: {Kind}", connection.Id, e.GetType().Name);
                        Error(connection, "unreadable message");
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        // A bug, or something nobody thought of: this connection ends, the others must not notice.
                        log.LogError(e, "{Id}: message failed", connection.Id);
                        Kick(connection, "server error");
                    }
                }
        }
        finally
        {
            lock (_gate)
            {
                LeaveGame(connection);
                _connections.Remove(connection);
            }
            connection.Drop();
            await writing;
        }
    }

    /// <summary>
    /// Once a second: drops connections that never said hello or fell silent outside a game, players a running game has been
    /// waiting for while another has sent its turn, and closes running games nobody sends to any more.
    /// </summary>
    public void Sweep()
    {
        long now = Environment.TickCount64;
        bool Over(long since, double seconds) => now - since > seconds * 1000;
        lock (_gate)
        {
            foreach (var connection in _connections.ToArray())
                if (connection.Fingerprint is null && Over(connection.Connected, limits.HandshakeSeconds))
                    Kick(connection, "no hello");
                else if (connection.Game is not { Started: true } && Over(connection.LastHeard, limits.IdleSeconds))
                    Kick(connection, "idle too long");
            foreach (var game in _games.Values.Where(g => g.Started).ToArray())
            {
                int[] missing = game.Relay!.Missing.ToArray();
                if (Over(game.LastTurn, limits.AbandonedSeconds))
                {
                    log.LogInformation("game {Game} abandoned", game.Id);
                    foreach (var member in game.Taken.Values.ToArray())
                        Kick(member, "game abandoned");
                    _games.Remove(game.Id);
                }
                else if (Over(game.LastTurn, limits.StallSeconds) && missing.Length < game.Relay.PlayerCount)
                    foreach (int player in missing)
                    {
                        log.LogInformation("game {Game}: player {Player} stalled it", game.Id, player);
                        if (game.Taken.TryGetValue(player, out var member))
                            Kick(member, "not responding");
                        else
                            Hand(game, game.Relay.Remove(player));
                    }
            }
        }
    }

    /// <summary>Tells the computer why, takes it out of its game and drops it. Under the lock.</summary>
    private void Kick(Connection connection, string reason)
    {
        if (connection.IsDropped)
            return;
        log.LogInformation("{Id} dropped: {Reason}", connection.Id, reason);
        Refuse(connection, reason);
        LeaveGame(connection);
        connection.Drop();
    }

    private void Handle(Connection connection, JsonObject? message)
    {
        string? type = message is null ? null : ServerMessages.TypeOf(message);
        if (message is null || type is null)
        {
            Error(connection, "not a message");
            return;
        }
        if (type != ServerMessages.Input && !connection.Charge(LobbyCost))
        {
            Kick(connection, "too many requests");
            return;
        }
        if (connection.Fingerprint is null && type != ServerMessages.Hello)
        {
            Kick(connection, "say hello first");
            return;
        }
        switch (type)
        {
            case ServerMessages.Hello:
                Hello(connection, message);
                break;
            case ServerMessages.List:
                connection.Send(ServerMessages.Of(ServerMessages.Games, new JsonObject { ["games"] = GameList() }));
                break;
            case ServerMessages.Create:
                Create(connection, message);
                break;
            case ServerMessages.Join:
                Join(connection, message["game"]?.GetValue<string>());
                break;
            case ServerMessages.Leave:
                LeaveGame(connection);
                break;
            case ServerMessages.Start:
                Start(connection);
                break;
            case ServerMessages.Choose:
                Choose(connection, message);
                break;
            case ServerMessages.Input:
                Input(connection, message["input"] as JsonObject);
                break;
            default:
                Error(connection, "unknown message");
                break;
        }
    }

    private void Hello(Connection connection, JsonObject message)
    {
        if (connection.Fingerprint is not null)
        {
            Error(connection, "hello already said");
            return;
        }
        GameFingerprint? fingerprint;
        try
        {
            fingerprint = message["fingerprint"] is JsonObject node ? GameFingerprint.FromJson(node.ToJsonString()) : null;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
        {
            fingerprint = null;
        }
        if (fingerprint is null || fingerprint.Protocol != GameFingerprint.CurrentProtocol)
        {
            Kick(connection, fingerprint is null
                ? "no fingerprint"
                : $"network protocol {fingerprint.Protocol} ≠ {GameFingerprint.CurrentProtocol} (server {GameFingerprint.ProgramVersion})");
            return;
        }
        if (fingerprint.Problems() is [var problem, ..])
        {
            Kick(connection, problem);
            return;
        }
        string name = PlayerText.Clean(message["name"]?.GetValue<string>(), ServerMessages.MaxNameLength);
        connection.Name = name.Length == 0 ? $"Player {connection.Id}" : name;
        connection.Fingerprint = fingerprint;
        log.LogInformation("{Name} ({Id}) connected, {Version}", connection.Name, connection.Id, fingerprint.Version);
        connection.Send(ServerMessages.Of(ServerMessages.Welcome, new JsonObject
        {
            ["protocol"] = GameFingerprint.CurrentProtocol, ["server"] = GameFingerprint.ProgramVersion,
        }));
    }

    private JsonArray GameList() => new(_games.Values.Where(g => !g.Started).OrderBy(g => g.Name, StringComparer.Ordinal).Take(MaxListed).Select(g => (JsonNode)new JsonObject
    {
        ["id"] = g.Id, ["name"] = g.Name, ["host"] = g.Host.Name, ["seats"] = g.Seats.Length, ["taken"] = g.Taken.Count,
        ["data"] = g.Setup.Fingerprint!.Data,
    }).ToArray());

    private void Create(Connection connection, JsonObject message)
    {
        GameSetup setup;
        try
        {
            // The host's fingerprint goes in below, whatever the setup says.
            setup = GameSetup.FromJson(message["setup"]!.ToJsonString()) with { Fingerprint = null };
            // The names, too: the server puts in who took each seat when the game starts.
            setup = setup with { Players = setup.Players?.Select(p => p is null ? null! : p with { Name = null }).ToArray()! };
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NullReferenceException or NotSupportedException)
        {
            Error(connection, "no game setup");
            return;
        }
        if (setup.Problems() is [var problem, ..])
        {
            Error(connection, problem);
            return;
        }
        int[] seats = setup.Players.Select((p, player) => (p, player)).Where(s => s.p.Human).Select(s => s.player).ToArray();
        if (seats.Length == 0)
        {
            Error(connection, "a game needs a human player");
            return;
        }
        LeaveGame(connection);
        if (_games.Count >= limits.MaxGames)
        {
            Error(connection, "the server is full");
            return;
        }
        string id;
        do
            id = Convert.ToHexStringLower(BitConverter.GetBytes(Random.Shared.Next()))[..6];
        while (_games.ContainsKey(id));
        string name = PlayerText.Clean(message["name"]?.GetValue<string>(), ServerMessages.MaxGameNameLength);
        var game = new Game(id, name.Length == 0 ? PlayerText.Clean($"{connection.Name}'s game", ServerMessages.MaxGameNameLength) : name, connection,
            setup with { Fingerprint = connection.Fingerprint }, seats);
        _games[id] = game;
        Seat(connection, game, seats[0]);
        log.LogInformation("{Name} opened game {Game} for {Seats} humans", connection.Name, id, seats.Length);
    }

    private void Join(Connection connection, string? id)
    {
        if (id is null || !_games.TryGetValue(id, out var game) || game.Started)
        {
            Refuse(connection, "no such game open");
            return;
        }
        if (connection.Game == game)
            return;
        var differences = connection.Fingerprint!.Differences(game.Setup.Fingerprint!);
        if (differences.Count > 0)
        {
            Refuse(connection, differences.Take(MaxReasons).ToArray());
            return;
        }
        if (game.Seats.Where(s => !game.Taken.ContainsKey(s)).Cast<int?>().FirstOrDefault() is not { } seat)
        {
            Refuse(connection, "the game is full");
            return;
        }
        LeaveGame(connection);
        Seat(connection, game, seat);
    }

    private void Seat(Connection connection, Game game, int seat)
    {
        game.Taken[seat] = connection;
        connection.Game = game;
        connection.Seat = seat;
        SendLobby(game);
    }

    private void SendLobby(Game game)
    {
        foreach (var member in game.Taken.Values)
            member.Send(ServerMessages.Of(ServerMessages.Lobby, new JsonObject
            {
                ["game"] = game.Id, ["name"] = game.Name, ["host"] = game.Host.Seat, ["you"] = member.Seat,
                ["seats"] = new JsonArray(game.Seats.Select(s => (JsonNode)new JsonObject
                {
                    ["player"] = s, ["name"] = SeatName(game, s),
                    ["people"] = game.Setup.Players[s].People?.ToString(), ["color"] = game.Setup.Players[s].Color,
                }).ToArray()),
                // The computer players, so the humans see whom they play with or against.
                ["computers"] = new JsonArray(game.Setup.Players.Select((p, player) => (p, player)).Where(c => !c.p.Human).Select(c => (JsonNode)new JsonObject
                {
                    ["player"] = c.player, ["people"] = c.p.People?.ToString(), ["color"] = c.p.Color, ["character"] = c.p.Character,
                }).ToArray()),
                ["colors"] = new JsonArray(game.Setup.Players.Where(p => p.Color is not null).Select(p => (JsonNode)p.Color!.Value).ToArray()),
            }));
    }

    /// <summary>
    /// The name of whoever took the seat, unique within the game: a name taken on an earlier seat gets a number (<c>Anna (2)</c>),
    /// or two players of the same name would be told apart by nothing in the lobby or the chat. Null for a free seat.
    /// </summary>
    private static string? SeatName(Game game, int seat)
    {
        if (!game.Taken.TryGetValue(seat, out var connection) || connection.Name is not { } name)
            return null;
        int same = game.Seats.TakeWhile(s => s != seat)
            .Count(s => game.Taken.TryGetValue(s, out var other) && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase));
        return same == 0 ? name : $"{name} ({same + 1})";
    }

    /// <summary>A player's own people and color in the lobby (null: drawn at the start); a color someone has already is refused.</summary>
    private void Choose(Connection connection, JsonObject message)
    {
        if (connection.Game is not { Started: false } game)
        {
            Error(connection, "not in a lobby");
            return;
        }
        Civilization? people = message["people"] is JsonValue value && value.TryGetValue(out string? name)
                               && Enum.TryParse<Civilization>(name, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
        int? color = message["color"] is JsonValue number && number.TryGetValue(out int index) ? index : null;
        if (color is < 0 or > 63)
        {
            Error(connection, "no such color");
            return;
        }
        if (color is { } wanted && game.Setup.Players.Where((p, player) => player != connection.Seat && p.Color == wanted).Any())
        {
            Error(connection, "the color is taken");
            SendLobby(game);
            return;
        }
        SetChoice(game, connection.Seat, people, color);
        SendLobby(game);
    }

    private static void SetChoice(Game game, int seat, Civilization? people, int? color) =>
        game.Setup = game.Setup with
        {
            Players = game.Setup.Players.Select((p, player) => player == seat ? p with { People = people, Color = color } : p).ToArray(),
        };

    private void Start(Connection connection)
    {
        if (connection.Game is not { Started: false } game || game.Host != connection)
        {
            Error(connection, "only the host starts its game");
            return;
        }
        if (game.Taken.Count < game.Seats.Length)
        {
            Error(connection, "not every seat is taken");
            return;
        }
        game.Setup = game.Setup with
        {
            Players = game.Setup.Players.Select((p, player) => p.Human ? p with { Name = SeatName(game, player) } : p).ToArray(),
        };
        game.Relay = new TurnRelay(game.Seats, firstTurn: options.DelayTurns);
        game.LastTurn = Environment.TickCount64;
        var setup = JsonNode.Parse(game.Setup.ToJson());
        foreach (var member in game.Taken.Values)
            member.Send(ServerMessages.Of(ServerMessages.Started, new JsonObject
            {
                ["game"] = game.Id, ["setup"] = setup?.DeepClone(), ["player"] = member.Seat, ["options"] = ServerMessages.Options(options),
            }));
        log.LogInformation("game {Game} started: {Players}", game.Id, string.Join(", ", game.Taken.Values.Select(c => c.Name)));
    }

    private void Input(Connection connection, JsonObject? input)
    {
        if (connection.Game is not { Relay: { } relay } game || input is null)
        {
            Error(connection, "no game running");
            return;
        }
        int broken = relay.Refused + relay.Dropped;
        string text;
        try
        {
            text = input.ToJsonString();
        }
        catch (InvalidOperationException)
        {
            // Text that cannot be written again (a lone surrogate).
            Kick(connection, "input against the rules");
            return;
        }
        Hand(game, relay.Receive(connection.Seat, text));
        for (; game.DesyncsLogged < relay.DesyncedTurns.Count; game.DesyncsLogged++)
            log.LogWarning("game {Game}: desync at turn {Turn}", game.Id, relay.DesyncedTurns[game.DesyncsLogged]);
        // A majority agrees on the world: the others are out, lying or not.
        foreach (int player in relay.TakeOutOfLine())
            if (game.Taken.TryGetValue(player, out var member))
                Kick(member, "out of sync");
        if (relay.Refused + relay.Dropped > broken)
            Kick(connection, "input against the rules");
    }

    /// <summary>The bundles to everyone in the game.</summary>
    private static void Hand(Game game, IReadOnlyList<string> bundles)
    {
        foreach (string bundle in bundles)
        {
            game.LastTurn = Environment.TickCount64;
            foreach (var member in game.Taken.Values)
                member.Send($"{{\"type\":\"{ServerMessages.Bundle}\",\"bundle\":{bundle}}}");
        }
    }

    private void LeaveGame(Connection connection)
    {
        if (connection.Game is not { } game)
            return;
        connection.Game = null;
        game.Taken.Remove(connection.Seat);
        if (!game.Started && game.Host == connection)
        {
            _games.Remove(game.Id);
            foreach (var member in game.Taken.Values)
            {
                member.Game = null;
                member.Send(ServerMessages.Of(ServerMessages.Closed, new JsonObject { ["game"] = game.Id }));
            }
            log.LogInformation("game {Game} closed by its host", game.Id);
        }
        else if (!game.Started)
        {
            // The seat is free again, with free choice for whoever takes it.
            SetChoice(game, connection.Seat, null, null);
            SendLobby(game);
        }
        else
        {
            foreach (var member in game.Taken.Values)
                member.Send(ServerMessages.Of(ServerMessages.Left, new JsonObject { ["game"] = game.Id, ["player"] = connection.Seat }));
            // Nobody waits for the player: what the turns still needed from it is off the list.
            Hand(game, game.Relay!.Remove(connection.Seat));
            if (game.Taken.Count == 0)
                _games.Remove(game.Id);
            log.LogInformation("{Name} left running game {Game}", connection.Name, game.Id);
        }
    }

    private static void Refuse(Connection connection, params string[] reasons) =>
        connection.Send(ServerMessages.Of(ServerMessages.Refused, new JsonObject
        {
            ["reasons"] = new JsonArray(reasons.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
        }));

    private static void Error(Connection connection, string text) =>
        connection.Send(ServerMessages.Of(ServerMessages.Error, new JsonObject { ["message"] = text }));
}
