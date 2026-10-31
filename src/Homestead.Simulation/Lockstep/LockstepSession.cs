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
/// the next one begins and applied <paramref name="DelayTurns"/> turns later – the time it has to reach everybody.
/// </summary>
public sealed record LockstepOptions(int TurnTicks = 2, int DelayTurns = 2);

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

    public LockstepSession(World world, int localPlayer, CommandCodec codec, ILockstepLink link, LockstepOptions? options = null)
    {
        Options = options ?? new LockstepOptions();
        if (Options.TurnTicks < 1 || Options.DelayTurns < 0)
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
    /// One tick, if its turn's commands are known: at a turn's beginning this computer's input for turn + delay goes out,
    /// then the turn's bundle goes into <see cref="World.Commands"/> and the world steps. False: still waiting for the bundle.
    /// </summary>
    public bool TryStep()
    {
        foreach (string message in _link.Receive())
        {
            var bundle = _codec.DecodeBundle(message);
            _bundles[bundle.Turn] = bundle.Commands;
        }
        if (_world.Tick % Options.TurnTicks == 0)
        {
            long turn = Turn;
            if (_sentThrough < turn + Options.DelayTurns)
            {
                _sentThrough = turn + Options.DelayTurns;
                _link.Send(_codec.Encode(new TurnInput(LocalPlayer, _sentThrough, _outgoing.ToList())));
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
}
