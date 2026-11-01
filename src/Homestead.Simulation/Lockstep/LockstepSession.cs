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
public sealed record LockstepOptions(int TurnTicks = 2, int DelayTurns = 2, int CheckTurns = 10);

/// <summary>A player whose world differed from this computer's, and in which parts (<see cref="WorldChecksums.Differences"/>).</summary>
public sealed record DesyncedPlayer(int Player, IReadOnlyList<string> Parts);

/// <summary>
/// The worlds went apart: when <paramref name="Turn"/> began (tick <paramref name="Tick"/>), these players' checksums differed
/// from this computer's. With three or more players, the one differing from everybody else is likely the odd one – this one,
/// if every other player is listed.
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
    private readonly Dictionary<long, IReadOnlyList<GameCommand>> _bundles = new();
    private long _sequence;
    /// <summary>The last turn this computer has sent its input for.</summary>
    private long _sentThrough;
    /// <summary>This computer's checksums sent, by turn, until the others' come back to compare.</summary>
    private readonly Dictionary<long, WorldChecksums> _ownChecks = new();

    public LockstepSession(World world, int localPlayer, CommandCodec codec, ILockstepLink link, LockstepOptions? options = null)
    {
        Options = options ?? new LockstepOptions();
        if (Options.TurnTicks < 1 || Options.DelayTurns < 0 || Options.CheckTurns < 1)
            throw new ArgumentException("a turn takes a tick at least, the delay is never negative", nameof(options));
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
        {
            var bundle = _codec.DecodeBundle(message);
            _bundles[bundle.Turn] = bundle.Commands;
            _newest = Math.Max(_newest, bundle.Newest);
            Compare(bundle.Checks ?? []);
        }
        if (Desync is not null)
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
                _link.Send(_codec.Encode(new TurnInput(LocalPlayer, _sentThrough, _outgoing.ToList(), check)));
                _outgoing.Clear();
            }
            if (turn >= FirstTurn)
            {
                if (!_bundles.Remove(turn, out var commands))
                {
                    Waiting = true;
                    return false;
                }
                _world.Commands.Enqueue(commands);
            }
        }
        Waiting = false;
        _world.Step();
        return true;
    }

    /// <summary>The other players' checksums against this computer's of the same turn; the first difference is the <see cref="Desync"/>.</summary>
    private void Compare(IReadOnlyList<PlayerCheck> checks)
    {
        foreach (var turn in checks.Select(c => c.Check.Turn).Distinct())
        {
            if (!_ownChecks.Remove(turn, out var own))
                continue;
            ChecksCompared++;
            var differing = checks
                .Where(c => c.Check.Turn == turn && c.Player != LocalPlayer)
                .Select(c => new DesyncedPlayer(c.Player, own.Differences(c.Check.Checksums)))
                .Where(d => d.Parts.Count > 0)
                .ToArray();
            if (differing.Length > 0)
                Desync ??= new DesyncReport(turn, own.Tick, differing);
        }
    }
}
