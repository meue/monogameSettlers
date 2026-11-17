using System.Text.Json.Nodes;

namespace Homestead.Simulation.Lockstep;

/// <summary>A game the server started, for this computer: the setup, the own player, how its turns run.</summary>
public sealed record StartedGame(GameSetup Setup, int Player, LockstepOptions Options);

/// <summary>
/// The messages between a computer and the server (<c>Homestead.Server</c>), each a JSON object with its kind in <c>type</c>.
/// To the server: <see cref="Hello"/> (name, <see cref="GameFingerprint"/>) first, then <see cref="List"/>, <see cref="Create"/>
/// (a game name and a <see cref="GameSetup"/>: its human players are the seats), <see cref="Join"/> (a game's id),
/// <see cref="Leave"/>, <see cref="Choose"/> (the own people and color in the lobby, null: free), <see cref="Start"/> (the host, once every seat is taken) and in the game <see cref="Input"/> (a
/// <see cref="TurnInput"/>). From the server: <see cref="Welcome"/> or <see cref="Refused"/> (reasons), <see cref="Games"/>,
/// <see cref="Lobby"/> (the game's seats with name, people and color, the colors taken, the own seat), <see cref="Started"/> (the setup with the game's fingerprint, the own
/// player, the <see cref="LockstepOptions"/>), <see cref="Bundle"/> (a <see cref="TurnBundle"/>), <see cref="Left"/> (a player
/// gone), <see cref="Closed"/> (the host left the lobby) and <see cref="Error"/>.
/// </summary>
public static class ServerMessages
{
    public const string Hello = "hello", List = "list", Create = "create", Join = "join", Leave = "leave", Choose = "choose", Start = "start",
        Input = "input";

    public const string Welcome = "welcome", Refused = "refused", Games = "games", Lobby = "lobby", Started = "started", Bundle = "bundle",
        Left = "left", Closed = "closed", Error = "error";

    /// <summary>The largest message either side takes (a setup with its fingerprint is a few kilobytes); a larger one ends the connection.</summary>
    public const int MaxLength = 256 * 1024;

    /// <summary>Longest player and game name.</summary>
    public const int MaxNameLength = 32, MaxGameNameLength = 40;

    /// <summary>A message of the kind with these fields.</summary>
    public static string Of(string type, JsonObject? fields = null)
    {
        var message = new JsonObject { ["type"] = type };
        foreach (var (name, value) in fields ?? [])
            message[name] = value?.DeepClone();
        return message.ToJsonString();
    }

    /// <summary>The message's kind, null if it is none.</summary>
    public static string? TypeOf(JsonObject message) => message["type"] is JsonValue type && type.TryGetValue(out string? text) ? text : null;

    public static JsonObject Options(LockstepOptions options) => new()
    {
        ["turnTicks"] = options.TurnTicks, ["delayTurns"] = options.DelayTurns, ["checkTurns"] = options.CheckTurns,
    };

    public static LockstepOptions Options(JsonObject? node) => node is null
        ? new LockstepOptions()
        : new LockstepOptions(node["turnTicks"]!.GetValue<int>(), node["delayTurns"]!.GetValue<int>(), node["checkTurns"]!.GetValue<int>());

    /// <summary>
    /// A <see cref="Started"/> message, checked as far as it goes without the map: the server is somebody's too, so what it says must
    /// not be able to break this computer. The setup must carry a fingerprint (only then does <see cref="GameSetup.Start"/> compare it).
    /// </summary>
    /// <exception cref="InvalidDataException">Not a game this computer can play.</exception>
    public static StartedGame ParseStarted(JsonObject message, string? dataDirectory = null)
    {
        GameSetup setup;
        int player;
        LockstepOptions options;
        try
        {
            setup = GameSetup.FromJson(message["setup"]!.ToJsonString());
            player = message["player"]!.GetValue<int>();
            options = Options(message["options"] as JsonObject);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NullReferenceException or InvalidOperationException or FormatException
            or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("unreadable game start", e);
        }
        var problems = setup.Problems(dataDirectory).ToList();
        if (setup.Fingerprint is null)
            problems.Add("no fingerprint");
        if (!options.IsValid)
            problems.Add("bad turn options");
        if (problems.Count == 0 && (player < 0 || player >= setup.Players.Count || !setup.Players[player].Human))
            problems.Add("not one of the human players");
        return problems.Count == 0 ? new StartedGame(setup, player, options) : throw new InvalidDataException(string.Join("; ", problems));
    }
}
