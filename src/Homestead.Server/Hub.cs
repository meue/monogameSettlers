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
}

/// <summary>
/// The server's state and rules (<see cref="ServerMessages"/>): computers say hello (another network protocol is refused), list,
/// open and join games – joining needs the host's program and game data (<see cref="GameFingerprint"/>), the reasons go back
/// otherwise – and the host starts a game once every seat is taken: each computer gets the setup, its player and the
/// <see cref="LockstepOptions"/>, then the turns go through a <see cref="TurnRelay"/>. Leaving the lobby frees the seat (the
/// host leaving closes the game); leaving a running game is told to the others (who then wait: taking over by a computer
/// player is still to come). Everything happens under one lock; sending is queued per connection.
/// </summary>
public sealed class Hub(LockstepOptions options, ILogger<Hub> log)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Game> _games = new();
    private readonly HashSet<Connection> _connections = new();
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

    public async Task RunAsync(WebSocket socket, CancellationToken cancel)
    {
        Connection connection;
        lock (_gate)
            _connections.Add(connection = new Connection(++_nextId, socket));
        var writing = connection.WriteAsync(cancel);
        try
        {
            await foreach (var message in connection.ReadAsync(cancel))
                lock (_gate)
                {
                    try
                    {
                        Handle(connection, message);
                    }
                    catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or System.Text.Json.JsonException)
                    {
                        // A field of the wrong kind (a number for a name, …): the message is answered, the connection stays.
                        log.LogWarning("{Id}: unreadable {Message}: {Error}", connection.Id, message?.ToJsonString(), e.Message);
                        Error(connection, "unreadable message");
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

    private void Handle(Connection connection, JsonObject? message)
    {
        string? type = message is null ? null : ServerMessages.TypeOf(message);
        if (message is null || type is null)
        {
            Error(connection, "not a message");
            return;
        }
        if (connection.Fingerprint is null && type != ServerMessages.Hello)
        {
            Refuse(connection, "say hello first");
            connection.Drop();
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
                Error(connection, $"unknown message '{type}'");
                break;
        }
    }

    private void Hello(Connection connection, JsonObject message)
    {
        GameFingerprint? fingerprint;
        try
        {
            fingerprint = message["fingerprint"] is JsonObject node ? GameFingerprint.FromJson(node.ToJsonString()) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            fingerprint = null;
        }
        if (fingerprint is null || fingerprint.Protocol != GameFingerprint.CurrentProtocol)
        {
            Refuse(connection, fingerprint is null
                ? "no fingerprint"
                : $"network protocol {fingerprint.Protocol} ≠ {GameFingerprint.CurrentProtocol} (server {GameFingerprint.ProgramVersion})");
            connection.Drop();
            return;
        }
        string name = (message["name"]?.GetValue<string>() ?? "").Trim();
        connection.Name = name.Length == 0 ? $"Player {connection.Id}" : name[..Math.Min(name.Length, 32)];
        connection.Fingerprint = fingerprint;
        log.LogInformation("{Name} ({Id}) connected, {Version}", connection.Name, connection.Id, fingerprint.Version);
        connection.Send(ServerMessages.Of(ServerMessages.Welcome, new JsonObject
        {
            ["protocol"] = GameFingerprint.CurrentProtocol, ["server"] = GameFingerprint.ProgramVersion,
        }));
    }

    private JsonArray GameList() => new(_games.Values.Where(g => !g.Started).OrderBy(g => g.Name, StringComparer.Ordinal).Select(g => (JsonNode)new JsonObject
    {
        ["id"] = g.Id, ["name"] = g.Name, ["host"] = g.Host.Name, ["seats"] = g.Seats.Length, ["taken"] = g.Taken.Count,
        ["data"] = g.Setup.Fingerprint!.Data,
    }).ToArray());

    private void Create(Connection connection, JsonObject message)
    {
        LeaveGame(connection);
        GameSetup setup;
        try
        {
            setup = GameSetup.FromJson(message["setup"]!.ToJsonString());
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NullReferenceException or NotSupportedException)
        {
            Error(connection, "no game setup");
            return;
        }
        int[] seats = setup.Players.Select((p, player) => (p, player)).Where(s => s.p.Human).Select(s => s.player).ToArray();
        if (seats.Length == 0)
        {
            Error(connection, "a game needs a human player");
            return;
        }
        string id;
        do
            id = Convert.ToHexStringLower(BitConverter.GetBytes(Random.Shared.Next()))[..6];
        while (_games.ContainsKey(id));
        string name = (message["name"]?.GetValue<string>() ?? "").Trim();
        var game = new Game(id, name.Length == 0 ? $"{connection.Name}'s game" : name[..Math.Min(name.Length, 40)], connection,
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
            Refuse(connection, differences.ToArray());
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
                    ["player"] = s, ["name"] = game.Taken.TryGetValue(s, out var taken) ? taken.Name : null,
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
        game.Relay = new TurnRelay(game.Seats, firstTurn: options.DelayTurns);
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
        int refused = relay.Refused;
        foreach (string bundle in relay.Receive(connection.Seat, input.ToJsonString()))
            foreach (var member in game.Taken.Values)
                member.Send($"{{\"type\":\"{ServerMessages.Bundle}\",\"bundle\":{bundle}}}");
        if (relay.Refused > refused)
            log.LogWarning("{Name} in game {Game}: input refused (too far ahead or too many commands)", connection.Name, game.Id);
        for (; game.DesyncsLogged < relay.DesyncedTurns.Count; game.DesyncsLogged++)
            log.LogWarning("game {Game}: desync at turn {Turn}", game.Id, relay.DesyncedTurns[game.DesyncsLogged]);
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
