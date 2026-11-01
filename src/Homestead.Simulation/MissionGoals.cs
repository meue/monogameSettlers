namespace Homestead.Simulation;

public enum MissionOutcome { Open, Won, Lost }

/// <summary>
/// Victory and defeat of a campaign mission for the own player (player 0), checked once a second: lost without a
/// building of its own (finished or under construction) once it had one, or without settlers too (some missions start
/// with settlers only, who found the settlement first); won once no other player has one (destroyed or captured).
/// Decided once, then it stays; the game goes on.
/// </summary>
public sealed class MissionGoals : IGameGoals
{
    /// <summary>The player the mission is played as (the human of the campaign maps).</summary>
    public const int Player = 0;

    private MissionOutcome Outcome { get; set; }

    private long Decided { get; set; }

    public MissionOutcome OutcomeOf(int player) => player == Player ? Outcome : MissionOutcome.Open;

    public long DecidedAt(int player) => player == Player && Outcome != MissionOutcome.Open ? Decided : -1;

    /// <summary>The own player has had a building (so losing the last one loses the mission).</summary>
    private bool _hadBuilding;

    public void Tick(World world)
    {
        if (Outcome != MissionOutcome.Open || world.Tick % World.TicksPerSecond != 0)
            return;
        bool own = false, others = false;
        foreach (var building in world.Buildings)
            if (!building.Exists)
                continue;
            else if (building.Player == Player)
                own = true;
            else
                others = true;
        _hadBuilding |= own;
        if (!own && (_hadBuilding || !world.Settlers.Any(s => s.Player == Player)))
            Decide(Player, MissionOutcome.Lost, world.Tick);
        else if (!others)
            Decide(Player, MissionOutcome.Won, world.Tick);
    }

    /// <summary>Computer players never give up in missions.</summary>
    public bool IsDefeated(int player) => false;

    public void Decide(int player, MissionOutcome outcome, long tick)
    {
        if (player != Player || Outcome != MissionOutcome.Open || outcome == MissionOutcome.Open)
            return;
        Outcome = outcome;
        Decided = tick;
    }
}
