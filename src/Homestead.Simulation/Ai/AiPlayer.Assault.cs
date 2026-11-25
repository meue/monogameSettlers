using Homestead.Simulation.Buildings;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Ai;

/// <summary>
/// An assault (cheat <c>/attackme</c>): the AI throws everything it has at one player and never falls back. Its
/// military buildings keep one soldier each, all others – new recruits too – go against that player's military
/// building nearest the army (bowmen walk along to its door); no strikes, campaigns, withdrawing or hospital
/// any more. Ends when that player has no building left.
/// </summary>
public sealed partial class AiPlayer
{
    /// <summary>The player it assaults, or null.</summary>
    public int? AssaultOn { get; private set; }

    /// <summary>Starts the assault on <paramref name="player"/> at the next think (no ally of it).</summary>
    public void Assault(int player)
    {
        if (!IsEnemy(player))
            return;
        EndTruce(player, "assaulted");
        AssaultOn = player;
        _strikes.Clear();
        _campaign = null;
        _offensive = null;
        _nextMilitary = _world.Tick;
        Note($"assaults player {player + 1} with everything it has");
    }

    private List<Settler> SoldiersOutside() => _world.Settlers
        .Where(s => s.Player == Player && s.Profession == Profession.Soldier && !s.IsInside && !s.IsDead)
        .ToList();

    /// <summary>Every military think while assaulting: the garrisons down to one, everyone else against the player.</summary>
    private void FollowAssault(List<Building> mine)
    {
        int enemy = AssaultOn!.Value;
        var theirs = _world.Buildings.Where(b => b.Exists && b.Player == enemy).ToList();
        if (theirs.Count == 0)
        {
            Note($"the assault on player {enemy + 1} is over: nothing left");
            AssaultOn = null;
            return;
        }
        var military = mine.Where(b => IsMilitary(b) && b.State == BuildingState.Finished).ToList();
        var soldiers = SoldiersOutside();
        Staff(military, soldiers.Where(s => !s.HasTask).ToList());
        var m = _world.Military;
        // Soldiers already fighting or on their way to one of the player's buildings go on; the others take the order.
        var free = soldiers.Where(s => !s.HasTask || m.MoveTarget(s) is not null || m.ForcedMoveTarget(s) is not null
                                       || m.AttackTarget(s) is { } t && (!t.Exists || t.Player != enemy)).ToList();
        if (free.Count == 0)
            return;
        var center = CenterOf(soldiers);
        var target = theirs.Where(b => IsMilitary(b) && b.State == BuildingState.Finished)
                         .MinBy(b => PathFinder.Distance(b.Position, center))
                     ?? theirs.MinBy(b => PathFinder.Distance(b.Position, center))!;
        int attacking = IsMilitary(target) ? m.Attack(free, target) : 0;
        // Bowmen (and everyone, when no military building is left) walk to its door and fight what they meet.
        var walking = free.Where(s => m.AttackTarget(s) != target && PathFinder.Distance(s.Position, target.Door) > 3).ToList();
        if (walking.Count > 0)
            m.Move(walking, target.Door);
        if (attacking > 0)
            Note($"assault: {attacking} soldiers against the {target.Definition.Name} at {target.Position.X},{target.Position.Y}");
    }
}
