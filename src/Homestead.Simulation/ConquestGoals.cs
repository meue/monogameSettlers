namespace Homestead.Simulation;

/// <summary>
/// Victory and defeat of a free game, checked once a second: a player who has lost every finished military building
/// (tower, big tower, castle – lost, destroyed or torn down) once having had one is defeated for good and loses; every
/// enemy of a player (player not on its team) defeated wins for it. Decided once per player, then it stays; the game goes on,
/// and the other players' defeats are still noted.
/// </summary>
public sealed class ConquestGoals : IGameGoals
{
    private readonly Dictionary<int, MissionOutcome> _outcomes = new();

    private readonly Dictionary<int, long> _decidedAt = new();

    /// <summary>The players who have had a military building (only they can lose them all).</summary>
    private readonly HashSet<int> _hadGuard = new();

    private readonly HashSet<int> _defeated = new();

    public MissionOutcome OutcomeOf(int player) => _outcomes.GetValueOrDefault(player);

    public long DecidedAt(int player) => _decidedAt.GetValueOrDefault(player, -1);

    public bool IsDefeated(int player) => _defeated.Contains(player);

    public void Tick(World world)
    {
        if (world.Tick % World.TicksPerSecond != 0)
            return;
        var guarded = world.Buildings
            .Where(b => b.Exists && b.State == Buildings.BuildingState.Finished && b.Definition.Occupiers.Count > 0)
            .Select(b => b.Player)
            .ToHashSet();
        _hadGuard.UnionWith(guarded);
        foreach (int player in _hadGuard)
            if (!guarded.Contains(player))
                _defeated.Add(player);
        // Every player of the game, also those of a world set up without a count (tests).
        int players = Math.Max(Math.Max(world.PlayerCount, 1), _hadGuard.Count == 0 ? 0 : _hadGuard.Max() + 1);
        for (int player = 0; player < players; player++)
        {
            int self = player;
            if (_defeated.Contains(self))
                Decide(self, MissionOutcome.Lost, world.Tick);
            else if (_hadGuard.Any(p => !world.AreAllies(p, self)) && _hadGuard.Where(p => !world.AreAllies(p, self)).All(_defeated.Contains))
                Decide(self, MissionOutcome.Won, world.Tick);
        }
    }

    public void Decide(int player, MissionOutcome outcome, long tick)
    {
        if (OutcomeOf(player) != MissionOutcome.Open || outcome == MissionOutcome.Open)
            return;
        _outcomes[player] = outcome;
        _decidedAt[player] = tick;
    }
}
