using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;

namespace Homestead.Simulation.Commands;

/// <summary>
/// A player's order to the simulation: the only way a client changes the world. Commands are plain data (players,
/// ids of settlers, buildings and ships, points, enums, numbers; a building type as its definition, known by people
/// and name), so a lockstep game can send them to every client, where they are applied on the same tick in the same
/// order (<see cref="CommandQueue"/>). A command acts only on what belongs to its <see cref="Player"/>.
/// </summary>
public abstract record GameCommand(int Player)
{
    /// <summary>Where the command stands among all commands issued (set when issued; applied in this order).</summary>
    public long Sequence { get; internal set; }

    /// <summary>Applies the command; the result (counts, outcomes) goes back to the client that issued it.</summary>
    internal abstract object? Apply(World world);

    /// <summary>The player's own settlers (alive) among the ids.</summary>
    protected List<Settler> Own(World world, IReadOnlyCollection<int> ids)
    {
        var wanted = ids.ToHashSet();
        return world.Settlers.Where(s => wanted.Contains(s.Id) && s.Player == Player && !s.IsDead).ToList();
    }

    /// <summary>The player's own building with the id, if it still stands.</summary>
    protected Building? OwnBuilding(World world, int id) =>
        world.Buildings.FirstOrDefault(b => b.Id == id && b.Exists && b.Player == Player);

    /// <summary>Any building with the id, if it still stands (an attack's target).</summary>
    protected static Building? AnyBuilding(World world, int id) => world.Buildings.FirstOrDefault(b => b.Id == id && b.Exists);

    /// <summary>The player's own ships among the ids.</summary>
    protected List<Ship> OwnShips(World world, IReadOnlyCollection<int> ids)
    {
        var wanted = ids.ToHashSet();
        return world.Shipping.Ships.Where(s => wanted.Contains(s.Id) && s.Player == Player).ToList();
    }
}

/// <summary>
/// Commands waiting for the next tick. <see cref="World.Step"/> applies them first, in the order they were issued;
/// their results stay readable until the following step. In a lockstep game the network layer feeds this queue with
/// the commands of every player for the tick.
/// </summary>
public sealed class CommandQueue
{
    private readonly List<GameCommand> _pending = new();
    private long _issued;

    [NonSerialized]
    private List<(GameCommand Command, object? Result)>? _applied;

    /// <summary>Commands not yet applied.</summary>
    public IReadOnlyList<GameCommand> Pending => _pending;

    /// <summary>The commands the last step applied, with their results.</summary>
    public IReadOnlyList<(GameCommand Command, object? Result)> Applied => _applied ??= new();

    /// <summary>Queues a command for the next tick and returns it (with its sequence number).</summary>
    public GameCommand Issue(GameCommand command)
    {
        command.Sequence = ++_issued;
        _pending.Add(command);
        return command;
    }

    /// <summary>
    /// A paused game alone (no lockstep): the orders take effect at once, as the next step would apply them. A network
    /// game pauses for everybody and applies commands only in <see cref="World.Step"/>.
    /// </summary>
    public void ApplyWhilePaused(World world) => Apply(world);

    internal void Apply(World world)
    {
        var applied = _applied ??= new();
        applied.Clear();
        if (_pending.Count == 0)
            return;
        var due = _pending.ToList();
        _pending.Clear();
        foreach (var command in due)
            applied.Add((command, command.Apply(world)));
    }
}
