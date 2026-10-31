using System.Text.Json.Nodes;

namespace Homestead.Simulation.Lockstep;

/// <summary>
/// The messages between a computer and the server (<c>Homestead.Server</c>), each a JSON object with its kind in <c>type</c>.
/// To the server: <see cref="Hello"/> (name, <see cref="GameFingerprint"/>) first, then <see cref="List"/>, <see cref="Create"/>
/// (a game name and a <see cref="GameSetup"/>: its human players are the seats), <see cref="Join"/> (a game's id),
/// <see cref="Leave"/>, <see cref="Start"/> (the host, once every seat is taken) and in the game <see cref="Input"/> (a
/// <see cref="TurnInput"/>). From the server: <see cref="Welcome"/> or <see cref="Refused"/> (reasons), <see cref="Games"/>,
/// <see cref="Lobby"/> (the game's seats, the own one), <see cref="Started"/> (the setup with the game's fingerprint, the own
/// player, the <see cref="LockstepOptions"/>), <see cref="Bundle"/> (a <see cref="TurnBundle"/>), <see cref="Left"/> (a player
/// gone), <see cref="Closed"/> (the host left the lobby) and <see cref="Error"/>.
/// </summary>
public static class ServerMessages
{
    public const string Hello = "hello", List = "list", Create = "create", Join = "join", Leave = "leave", Start = "start", Input = "input";

    public const string Welcome = "welcome", Refused = "refused", Games = "games", Lobby = "lobby", Started = "started", Bundle = "bundle",
        Left = "left", Closed = "closed", Error = "error";

    /// <summary>The largest message either side takes (a setup with its fingerprint is a few kilobytes); a larger one ends the connection.</summary>
    public const int MaxLength = 256 * 1024;

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
}
