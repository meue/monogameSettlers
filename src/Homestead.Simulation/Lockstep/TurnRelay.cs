using System.Text.Json.Nodes;

namespace Homestead.Simulation.Lockstep;

/// <summary>
/// What the server does in a lockstep game: collects every human player's <see cref="TurnInput"/> per turn and, once all
/// of a turn are in, hands out its <see cref="TurnBundle"/> – turns in order, players in order, each player's commands as
/// sent. It reads the commands only as JSON (no game logic; a server in another language does the same): commands that
/// claim another player than their sender are dropped, a second input of a player for a turn and inputs for turns handed
/// out already are ignored. Turns before <paramref name="firstTurn"/> have no inputs (see <see cref="LockstepSession"/>).
/// </summary>
public sealed class TurnRelay(IEnumerable<int> players, long firstTurn)
{
    private readonly int[] _players = players.Distinct().Order().ToArray();

    /// <summary>Per turn not yet handed out: each player's commands (JSON array) received so far.</summary>
    private readonly SortedDictionary<long, SortedDictionary<int, JsonArray>> _inputs = new();

    /// <summary>The next turn to hand out.</summary>
    public long NextTurn { get; private set; } = firstTurn;

    /// <summary>Commands dropped because they claimed another player than their sender.</summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// A player's input (JSON of a <see cref="TurnInput"/>, from <paramref name="sender"/>'s connection); returns the bundles
    /// (JSON) it completes, in order.
    /// </summary>
    public IReadOnlyList<string> Receive(int sender, string input)
    {
        JsonObject node;
        long turn;
        try
        {
            node = JsonNode.Parse(input)!.AsObject();
            turn = node["turn"]!.GetValue<long>();
            if (node["player"]?.GetValue<int>() != sender)
                return [];
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException)
        {
            // Not an input at all: nothing to hand on.
            return [];
        }
        if (!_players.Contains(sender) || turn < NextTurn)
            return [];
        if (!_inputs.TryGetValue(turn, out var byPlayer))
            _inputs[turn] = byPlayer = new SortedDictionary<int, JsonArray>();
        if (byPlayer.ContainsKey(sender))
            return [];
        var commands = new JsonArray();
        foreach (var command in node["commands"] as JsonArray ?? [])
            if (command is JsonObject && command["player"] is JsonValue player && player.TryGetValue(out int claimed) && claimed == sender)
                commands.Add(command.DeepClone());
            else
                Dropped++;
        byPlayer[sender] = commands;

        var bundles = new List<string>();
        while (_inputs.TryGetValue(NextTurn, out var complete) && complete.Count == _players.Length)
        {
            var all = new JsonArray();
            foreach (var (_, own) in complete)
                foreach (var command in own)
                    all.Add(command!.DeepClone());
            bundles.Add(new JsonObject { ["turn"] = NextTurn, ["commands"] = all }.ToJsonString());
            _inputs.Remove(NextTurn);
            NextTurn++;
        }
        return bundles;
    }
}
