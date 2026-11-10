using System.Text;
using Homestead.Simulation.Ai;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Commands;

namespace Homestead.Simulation.Lockstep;

/// <summary>A computer's connection to the server: sends its turn inputs, receives the turn bundles (JSON, see <see cref="CommandCodec"/>).</summary>
public interface ILockstepLink
{
    void Send(string message);

    /// <summary>The messages come in since the last call, in the order they came.</summary>
    IReadOnlyList<string> Receive();
}

/// <summary>
/// How a lockstep game runs: a turn is <paramref name="TurnTicks"/> ticks; what a player orders during a turn is sent when
/// the next one begins and applied <paramref name="DelayTurns"/> turns later – the time it has to reach everybody. Every
/// <paramref name="CheckTurns"/> turns (default: a second) the input carries the world's checksums, to notice a desync.
/// </summary>
public sealed record LockstepOptions(int TurnTicks = 2, int DelayTurns = 2, int CheckTurns = 10)
{
    /// <summary>Within what a game can run with (they may come from any server).</summary>
    public bool IsValid => TurnTicks is >= 1 and <= 20 && DelayTurns is >= 0 and <= 50 && CheckTurns is >= 1 and <= 1000;
}

/// <summary>A player whose world differed from this computer's, and in which parts (<see cref="WorldChecksums.Differences"/>).</summary>
public sealed record DesyncedPlayer(int Player, IReadOnlyList<string> Parts);

/// <summary>
/// The worlds went apart: when <paramref name="Turn"/> began (tick <paramref name="Tick"/>), these players' checksums differed
/// from this computer's, and no majority stood with this computer – with one, the odd ones are dropped by the server and the game
/// goes on (a client lying about its checksums must not end a game).
/// </summary>
public sealed record DesyncReport(long Turn, long Tick, IReadOnlyList<DesyncedPlayer> Players);

/// <summary>
/// One computer's side of a lockstep game: every computer simulates the whole world; only the human players' commands go
/// over the network. Commands issued here (<see cref="Issue"/>) are not applied here at once but collected, sent as this
/// player's input for turn T + delay when turn T begins (an empty one too), and applied only as part of the server's
/// <see cref="TurnBundle"/> for that turn – the same commands, decoded from the same JSON, in the same order, at the same tick
/// (the turn's first) on every computer, the own ones included. A turn whose bundle is not there yet waits
/// (<see cref="TryStep"/> returns false); turns before the first input could arrive have none.
/// </summary>
public sealed class LockstepSession
{
    private readonly World _world;
    private readonly CommandCodec _codec;
    private readonly ILockstepLink _link;
    private readonly List<GameCommand> _outgoing = new();
    private readonly Dictionary<long, TurnBundle> _bundles = new();
    private readonly List<int> _takenOver = new();
    private long _sequence;
    /// <summary>The last turn this computer has sent its input for.</summary>
    private long _sentThrough;
    /// <summary>This computer's checksums sent, by turn, until the others' come back to compare.</summary>
    private readonly Dictionary<long, WorldChecksums> _ownChecks = new();

    public LockstepSession(World world, int localPlayer, CommandCodec codec, ILockstepLink link, LockstepOptions? options = null)
    {
        Options = options ?? new LockstepOptions();
        if (!Options.IsValid)
            throw new ArgumentException("a turn takes a tick at least, the delay is never negative, nothing is out of all proportion", nameof(options));
        if (world.Tick % Options.TurnTicks != 0)
            throw new ArgumentException("the game starts at a turn's beginning", nameof(world));
        _world = world;
        LocalPlayer = localPlayer;
        _codec = codec;
        _link = link;
        FirstTurn = world.Tick / Options.TurnTicks + Options.DelayTurns;
        _sentThrough = FirstTurn - 1;
    }

    public LockstepOptions Options { get; }

    public int LocalPlayer { get; }

    /// <summary>The first turn with inputs (the server's first bundle); every computer of the game starts at the same tick.</summary>
    public long FirstTurn { get; }

    /// <summary>The turn the next tick belongs to.</summary>
    public long Turn => _world.Tick / Options.TurnTicks;

    /// <summary>The last <see cref="TryStep"/> stood still: the turn's bundle has not come yet.</summary>
    public bool Waiting { get; private set; }

    /// <summary>
    /// How many turns the fastest computer is ahead of this one (from the newest input it sent, <see cref="TurnBundle.Newest"/>):
    /// above one, this computer should step faster to catch up – the others wait for it otherwise.
    /// </summary>
    public long Behind => Math.Max(0, _newest - Options.DelayTurns - Turn);

    /// <summary>The newest turn any player has sent an input for, as the server last told.</summary>
    private long _newest;

    /// <summary>The worlds went apart: the game stops for good (<see cref="TryStep"/> returns false).</summary>
    public DesyncReport? Desync { get; private set; }

    /// <summary>What the server sent makes no sense: the game stops for good (<see cref="TryStep"/> returns false), the reason for the console.</summary>
    public string? Failure { get; private set; }

    /// <summary>How many turns' checksums were compared with the other players' (and agreed, as long as there is no <see cref="Desync"/>).</summary>
    public int ChecksCompared { get; private set; }

    /// <summary>Queues an order of the local player for the next input sent; returns it with its number (to know its result again).</summary>
    public GameCommand Issue(GameCommand command)
    {
        if (command.Player != LocalPlayer)
            throw new ArgumentException($"player {LocalPlayer + 1} cannot order for player {command.Player + 1}", nameof(command));
        command.Sequence = ++_sequence;
        _outgoing.Add(command);
        return command;
    }

    /// <summary>
    /// One tick, if its turn's commands are known: at a turn's beginning this computer's input for turn + delay goes out
    /// (now and then with the world's checksums), then the turn's bundle goes into <see cref="World.Commands"/> and the world
    /// steps. False: still waiting for the bundle, or the worlds went apart (<see cref="Desync"/>).
    /// </summary>
    public bool TryStep()
    {
        foreach (string message in _link.Receive())
            TakeBundle(message);
        if (Desync is not null || Failure is not null)
        {
            Waiting = false;
            return false;
        }
        if (_world.Tick % Options.TurnTicks == 0)
        {
            long turn = Turn;
            if (_sentThrough < turn + Options.DelayTurns)
            {
                _sentThrough = turn + Options.DelayTurns;
                StateCheck? check = null;
                if (turn % Options.CheckTurns == 0)
                {
                    var checksums = WorldChecksum.Parts(_world);
                    _ownChecks[turn] = checksums;
                    check = new StateCheck(turn, checksums);
                }
                _link.Send(_codec.Encode(new TurnInput(LocalPlayer, _sentThrough, TakeOutgoing(), check)));
            }
            if (turn >= FirstTurn)
            {
                if (!_bundles.Remove(turn, out var bundle))
                {
                    Waiting = true;
                    return false;
                }
                foreach (int player in bundle.Left ?? [])
                    TakeOver(player);
                _world.Commands.Enqueue(bundle.Commands);
            }
        }
        Waiting = false;
        _world.Step();
        return true;
    }

    /// <summary>
    /// What fits into an input: what the server takes of one (<see cref="TurnRelay"/>), the rest waits for the next. A single command
    /// too large for any input (only a selection beyond all sense can be) cannot go at all.
    /// </summary>
    private List<GameCommand> TakeOutgoing()
    {
        var taken = new List<GameCommand>();
        int bytes = 2, used = 0; // the array's brackets
        for (; used < _outgoing.Count && taken.Count < TurnRelay.MaxCommands; used++)
        {
            int size = Encoding.UTF8.GetByteCount(_codec.Encode(_outgoing[used])) + 1;
            if (size > TurnRelay.MaxInputBytes)
                continue;
            if (bytes + size > TurnRelay.MaxInputBytes)
                break;
            bytes += size;
            taken.Add(_outgoing[used]);
        }
        _outgoing.RemoveRange(0, used);
        return taken;
    }

    /// <summary>A bundle from the server. One not to be read or for a turn out of all reach can only be a broken or hostile server.</summary>
    private void TakeBundle(string message)
    {
        if (Failure is not null)
            return;
        TurnBundle bundle;
        try
        {
            bundle = _codec.DecodeBundle(message);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            Failure = "unreadable turn from the server";
            return;
        }
        if (bundle.Turn > Turn + MaxTurnsAhead || _bundles.Count >= MaxTurnsAhead)
        {
            Failure = "turns out of reach from the server";
            return;
        }
        if (bundle.Turn >= Turn)
            _bundles[bundle.Turn] = bundle;
        _newest = Math.Max(_newest, bundle.Newest);
        Compare(bundle.Checks ?? []);
    }

    /// <summary>
    /// The players a computer player took over since the last call (they left the game), for the messages. The same players at the
    /// same tick on every computer: the server names them in a bundle.
    /// </summary>
    public IReadOnlyList<int> TakeTakenOver()
    {
        var players = _takenOver.ToArray();
        _takenOver.Clear();
        return players;
    }

    /// <summary>
    /// A computer player plays on for a human who left, from the turn's first tick – its character drawn from the world's seed, so
    /// the same on every computer. Not for a player who is gone from the game anyway (defeated, or not on the map).
    /// </summary>
    private void TakeOver(int player)
    {
        if (player >= _world.Peoples.Count || _world.AiPlayers.Any(a => a.Player == player) || _world.Goals?.IsDefeated(player) == true)
            return;
        var catalog = BuildingCatalog.Load(_world.PeopleOf(player));
        _world.AiPlayers.Add(new AiPlayer(_world, catalog, player, random: new Random(unchecked(_world.Seed * 31 + 1009 * (player + 1)))));
        _takenOver.Add(player);
    }

    /// <summary>Turns ahead of this computer the server can have handed out (a computer lagging far behind the others stays below it too: they wait).</summary>
    private const int MaxTurnsAhead = 10_000;

    /// <summary>
    /// The other players' checksums against this computer's of the same turn: the first difference this computer has no majority
    /// for is the <see cref="Desync"/>. In a majority of three or more it goes on: the server drops the odd ones.
    /// </summary>
    private void Compare(IReadOnlyList<PlayerCheck> checks)
    {
        foreach (var turn in checks.Select(c => c.Check.Turn).Distinct())
        {
            if (!_ownChecks.Remove(turn, out var own))
                continue;
            ChecksCompared++;
            var others = checks.Where(c => c.Check.Turn == turn && c.Player != LocalPlayer).DistinctBy(c => c.Player).ToArray();
            var differing = others
                .Select(c => new DesyncedPlayer(c.Player, own.Differences(c.Check.Checksums)))
                .Where(d => d.Parts.Count > 0)
                .ToArray();
            bool majority = (others.Length + 1 - differing.Length) * 2 > others.Length + 1;
            if (differing.Length > 0 && !majority)
                Desync ??= new DesyncReport(turn, own.Tick, differing);
        }
    }
}
