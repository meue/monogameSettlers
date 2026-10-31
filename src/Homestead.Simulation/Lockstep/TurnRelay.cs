using System.Text.Json.Nodes;

namespace Homestead.Simulation.Lockstep;

/// <summary>
/// What the server does in a lockstep game: collects every human player's <see cref="TurnInput"/> per turn and, once all
/// of a turn are in, hands out its <see cref="TurnBundle"/> – turns in order, players in order, each player's commands as
/// sent. It reads the commands only as JSON (no game logic; a server in another language does the same): commands that
/// claim another player than their sender are dropped, a second input of a player for a turn and inputs for turns handed
/// out already are ignored. Turns before <paramref name="firstTurn"/> have no inputs (see <see cref="LockstepSession"/>).
/// The inputs' <see cref="StateCheck"/>s go along in the bundle (every computer compares them with its own); the relay compares
/// them too and notes the turns whose worlds differ (<see cref="DesyncedTurns"/>, for the server's log).
/// Against flooding: inputs for turns more than <paramref name="maxAhead"/> beyond the next one handed out, or with more than
/// <paramref name="maxCommands"/> commands, are refused (<see cref="Refused"/>; the server may drop such a connection).
/// </summary>
public sealed class TurnRelay(IEnumerable<int> players, long firstTurn, int maxAhead = 100, int maxCommands = 200)
{
    private readonly int[] _players = players.Distinct().Order().ToArray();

    /// <summary>Per turn not yet handed out: each player's commands (JSON array) and check received so far.</summary>
    private readonly SortedDictionary<long, SortedDictionary<int, (JsonArray Commands, JsonObject? Check)>> _inputs = new();

    private readonly List<long> _desynced = new();

    /// <summary>The next turn to hand out.</summary>
    public long NextTurn { get; private set; } = firstTurn;

    /// <summary>Commands dropped because they claimed another player than their sender.</summary>
    public int Dropped { get; private set; }

    /// <summary>Inputs refused for going too far ahead or carrying too many commands.</summary>
    public int Refused { get; private set; }

    /// <summary>The turns whose checks differed between the players.</summary>
    public IReadOnlyList<long> DesyncedTurns => _desynced;

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
        if (turn > NextTurn + maxAhead || (node["commands"] as JsonArray)?.Count > maxCommands)
        {
            Refused++;
            return [];
        }
        if (!_inputs.TryGetValue(turn, out var byPlayer))
            _inputs[turn] = byPlayer = new SortedDictionary<int, (JsonArray, JsonObject?)>();
        if (byPlayer.ContainsKey(sender))
            return [];
        var commands = new JsonArray();
        foreach (var command in node["commands"] as JsonArray ?? [])
            if (command is JsonObject && command["player"] is JsonValue player && player.TryGetValue(out int claimed) && claimed == sender)
                commands.Add(command.DeepClone());
            else
                Dropped++;
        byPlayer[sender] = (commands, node["check"] as JsonObject);

        var bundles = new List<string>();
        while (_inputs.TryGetValue(NextTurn, out var complete) && complete.Count == _players.Length)
        {
            var all = new JsonArray();
            var checks = new JsonArray();
            foreach (var (player, (own, check)) in complete)
            {
                foreach (var command in own)
                    all.Add(command!.DeepClone());
                if (check is not null)
                    checks.Add(new JsonObject { ["player"] = player, ["check"] = check.DeepClone() });
            }
            foreach (var sameTurn in complete.Values.Select(i => i.Check).OfType<JsonObject>().GroupBy(c => c["turn"]?.ToJsonString()))
                if (sameTurn.Select(c => c["checksums"]?.ToJsonString()).Distinct().Count() > 1 && long.TryParse(sameTurn.Key, out long checkedTurn))
                    _desynced.Add(checkedTurn);
            var bundle = new JsonObject { ["turn"] = NextTurn, ["commands"] = all };
            if (checks.Count > 0)
                bundle["checks"] = checks;
            bundles.Add(bundle.ToJsonString());
            _inputs.Remove(NextTurn);
            NextTurn++;
        }
        return bundles;
    }
}
