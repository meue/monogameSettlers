using System.Text;
using System.Text.Json.Nodes;

namespace Homestead.Simulation.Lockstep;

/// <summary>
/// What the server does in a lockstep game: collects every human player's <see cref="TurnInput"/> per turn and, once all
/// of a turn are in, hands out its <see cref="TurnBundle"/> – turns in order, players in order, each player's commands as
/// sent. It reads the commands only as JSON (no game logic; a server in another language does the same): commands that
/// claim another player than their sender or are of no known type are dropped, inputs that break the rules are refused.
/// Turns before <paramref name="firstTurn"/> have no inputs (see <see cref="LockstepSession"/>).
/// The inputs' <see cref="StateCheck"/>s go along in the bundle (every computer compares them with its own); the relay compares
/// them too and notes the turns whose worlds differ (<see cref="DesyncedTurns"/>) and, where a majority agrees, the players
/// outside it (<see cref="TakeOutOfLine"/>).
/// Against flooding: nothing an honest client does is refused, so whoever is (<see cref="Refused"/>, <see cref="Dropped"/>) can be
/// dropped by the server. Limits: turns at most <paramref name="maxAhead"/> beyond the next one handed out, at most
/// <paramref name="maxCommands"/> commands and <see cref="MaxInputBytes"/> per input (so a bundle of all players stays below
/// <see cref="ServerMessages.MaxLength"/>, which the clients enforce), at most <see cref="MaxPendingBytes"/> held per player.
/// A player gone is taken out (<see cref="Remove"/>): the others go on without waiting for it.
/// </summary>
public sealed class TurnRelay(IEnumerable<int> players, long firstTurn, int maxAhead = 100, int maxCommands = TurnRelay.MaxCommands,
    long? delayTurns = null)
{
    /// <summary>Most commands in an input (<see cref="LockstepSession"/> keeps the rest for the next one).</summary>
    public const int MaxCommands = 50;

    /// <summary>Most bytes of commands in an input.</summary>
    public const int MaxInputBytes = 28 * 1024;

    /// <summary>Most bytes of a player's inputs waiting for the others.</summary>
    public const int MaxPendingBytes = 512 * 1024;

    /// <summary>The fields of a check's <see cref="WorldChecksums"/> on the wire.</summary>
    private static readonly int ChecksumFields = typeof(WorldChecksums).GetProperties().Length;

    private readonly List<int> _players = players.Distinct().Order().ToList();

    /// <summary>A game starts at tick 0, so the first turn with inputs is the delay.</summary>
    private readonly long _delay = delayTurns ?? firstTurn;

    /// <summary>Per turn not yet handed out: each player's commands (JSON array) and check received so far.</summary>
    private readonly SortedDictionary<long, SortedDictionary<int, Input>> _inputs = new();

    private readonly Dictionary<int, long> _pending = new();

    private readonly List<long> _desynced = new();

    private readonly List<int> _outOfLine = new();

    /// <summary>Players taken out whose leaving is not in a bundle yet.</summary>
    private readonly List<int> _left = new();

    /// <summary>The furthest turn any player has sent an input for (handed on in every bundle).</summary>
    private long _newest;

    private sealed record Input(JsonArray Commands, JsonObject? Check, int Bytes);

    /// <summary>The next turn to hand out.</summary>
    public long NextTurn { get; private set; } = firstTurn;

    /// <summary>Commands dropped because they claimed another player than their sender or are no commands.</summary>
    public int Dropped { get; private set; }

    /// <summary>Inputs refused: unreadable, for someone else, a turn gone or sent already, beyond a limit, with a broken check.</summary>
    public int Refused { get; private set; }

    /// <summary>The turns whose checks differed between the players.</summary>
    public IReadOnlyList<long> DesyncedTurns => _desynced;

    /// <summary>The players the next turn is waiting for.</summary>
    public IEnumerable<int> Missing =>
        _players.Where(p => !_inputs.TryGetValue(NextTurn, out var complete) || !complete.ContainsKey(p));

    /// <summary>The players still in the game.</summary>
    public int PlayerCount => _players.Count;

    /// <summary>
    /// The players whose checks differed from a majority of three or more since the last call: their world is not the others' (or
    /// they lie about it). Without a majority nobody can tell, the computers stop by themselves.
    /// </summary>
    public IReadOnlyList<int> TakeOutOfLine()
    {
        var players = _outOfLine.Distinct().ToArray();
        _outOfLine.Clear();
        return players;
    }

    /// <summary>
    /// A player's input (JSON of a <see cref="TurnInput"/>, from <paramref name="sender"/>'s connection); returns the bundles
    /// (JSON) it completes, in order.
    /// </summary>
    public IReadOnlyList<string> Receive(int sender, string input)
    {
        if (!_players.Contains(sender))
            return [];
        try
        {
            var node = JsonNode.Parse(input)!.AsObject();
            long turn = node["turn"]!.GetValue<long>();
            if (node["player"]?.GetValue<int>() == sender && turn >= NextTurn && turn <= NextTurn + maxAhead
                && (!_inputs.TryGetValue(turn, out var byPlayer) || !byPlayer.ContainsKey(sender))
                && (node["commands"] as JsonArray)?.Count <= maxCommands && Accept(sender, turn, node))
                return Complete();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException
            or ArgumentException)
        {
            // Not an input at all: nothing to hand on.
        }
        Refused++;
        return [];
    }

    /// <summary>Takes the input in unless it is too large or its check broken.</summary>
    private bool Accept(int sender, long turn, JsonObject node)
    {
        var commands = new JsonArray();
        foreach (var command in node["commands"]!.AsArray())
            if (IsCommandOf(sender, command))
                commands.Add(command!.DeepClone());
            else
                Dropped++;
        var check = node["check"] as JsonObject;
        if (node["check"] is not null && (check is null || !IsCheck(check, turn)))
            return false;
        // As it will go out: the bundle holds these bytes.
        int bytes = Encoding.UTF8.GetByteCount(commands.ToJsonString());
        long pending = _pending.GetValueOrDefault(sender) + bytes;
        if (bytes > MaxInputBytes || pending > MaxPendingBytes)
            return false;
        if (!_inputs.TryGetValue(turn, out var byPlayer))
            _inputs[turn] = byPlayer = new SortedDictionary<int, Input>();
        byPlayer[sender] = new Input(commands, check, bytes);
        _pending[sender] = pending;
        _newest = Math.Max(_newest, turn);
        return true;
    }

    private static readonly HashSet<string> CommandNames = CommandCodec.CommandTypes.Select(t => t.Name).ToHashSet();

    private static bool IsCommandOf(int sender, JsonNode? command) =>
        command is JsonObject fields
        && fields["player"] is JsonValue player && player.TryGetValue(out int claimed) && claimed == sender
        && fields["$type"] is JsonValue type && type.TryGetValue(out string? name) && CommandNames.Contains(name);

    /// <summary>A <see cref="StateCheck"/> of the turn the input was sent in.</summary>
    private bool IsCheck(JsonObject check, long turn) =>
        check["turn"] is JsonValue value && value.TryGetValue(out long checkedTurn) && checkedTurn == turn - _delay
        && check["checksums"] is JsonObject sums && sums.Count == ChecksumFields
        && sums.All(part => part.Value is JsonValue number && (number.TryGetValue(out ulong _) || number.TryGetValue(out long _)));

    /// <summary>
    /// Takes a player out, what it had sent included (it never arrived at the others): the game goes on with the rest. The next
    /// bundle names the player in <c>left</c>, and with that turn a computer player takes over on every computer
    /// (<see cref="LockstepSession"/>). Returns the bundles this completes. Every computer learns of it through the bundles alone,
    /// so all of them still apply the same commands at the same ticks.
    /// </summary>
    public IReadOnlyList<string> Remove(int player)
    {
        if (!_players.Remove(player))
            return [];
        foreach (var byPlayer in _inputs.Values)
            byPlayer.Remove(player);
        _pending.Remove(player);
        _left.Add(player);
        return Complete();
    }

    /// <summary>The bundles of the turns every player has sent.</summary>
    private List<string> Complete()
    {
        var bundles = new List<string>();
        while (_players.Count > 0 && _inputs.TryGetValue(NextTurn, out var complete) && complete.Count == _players.Count)
        {
            var all = new JsonArray();
            var checks = new JsonArray();
            foreach (var (player, input) in complete)
            {
                foreach (var command in input.Commands)
                    all.Add(command!.DeepClone());
                if (input.Check is { } check)
                    checks.Add(new JsonObject { ["player"] = player, ["check"] = check.DeepClone() });
                _pending[player] -= input.Bytes;
            }
            CompareChecks(complete);
            var bundle = new JsonObject { ["turn"] = NextTurn, ["commands"] = all, ["newest"] = _newest };
            if (checks.Count > 0)
                bundle["checks"] = checks;
            if (_left.Count > 0)
            {
                // The turn a computer player takes over: the same on every computer.
                bundle["left"] = new JsonArray(_left.Select(p => (JsonNode)p).ToArray());
                _left.Clear();
            }
            bundles.Add(bundle.ToJsonString());
            _inputs.Remove(NextTurn);
            NextTurn++;
        }
        return bundles;
    }

    private void CompareChecks(SortedDictionary<int, Input> inputs)
    {
        var groups = inputs.Where(i => i.Value.Check is not null)
            .GroupBy(i => i.Value.Check!["checksums"]!.ToJsonString())
            .OrderByDescending(g => g.Count())
            .ToArray();
        if (groups.Length < 2)
            return;
        _desynced.Add(NextTurn - _delay);
        if (groups[0].Count() * 2 > groups.Sum(g => g.Count()))
            _outOfLine.AddRange(groups.Skip(1).SelectMany(g => g.Select(i => i.Key)));
    }
}
