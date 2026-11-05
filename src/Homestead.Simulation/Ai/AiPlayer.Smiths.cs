using Homestead.Simulation.Buildings;

namespace Homestead.Simulation.Ai;

/// <summary>
/// The toolsmith is not in the build order: it comes when a worker waits for a tool (<see cref="Wishes"/>). For that a
/// smith must be left: without a toolsmith, another weaponsmith is only built while a smith (or a hammer to make one)
/// remains besides it. Should the toolsmith still find none (hammers stolen, used up by builders), a weaponsmith is torn
/// down: its smith keeps trade and hammer and takes the toolsmith, which then makes hammers for more.
/// </summary>
public sealed partial class AiPlayer
{
    private const string Toolsmith = "toolsmith";
    private const string SmithTrade = "SMITH";

    /// <summary>
    /// Smiths to be had beyond the smith buildings still without one: jobless smiths, hammers lying on own land, carriers
    /// on their way to a hammer for a smith building; less those buildings (sites included) without a smith yet.
    /// </summary>
    private int SpareSmiths(List<Building> mine)
    {
        var hammer = _world.Recruitment.ToolFor(SmithTrade);
        int jobless = _world.Settlers.Count(s => s.Player == Player && !s.IsDead && s.Trade == SmithTrade && s.Workplace is null);
        int hammers = hammer is { } tool ? _world.Piles.Where(p => p.Material == tool && _world.BelongsTo(p, Player)).Sum(p => p.Available) : 0;
        int fetching = _world.Recruitment.Active.Count(r => r.Settler.Player == Player && r.Trade == SmithTrade);
        int waiting = mine.Count(b => b.Definition.Worker == SmithTrade && (b.State != BuildingState.Finished || _world.Production.WorkerOf(b) is null));
        return jobless + hammers + fetching - waiting;
    }

    /// <summary>Whether a smith building other than the toolsmith may come: one smith stays for the toolsmith while there is none.</summary>
    private bool SmithLeftFor(BuildingDefinition definition, List<Building> mine) =>
        definition.Worker != SmithTrade || definition.Name == Toolsmith
        || SpareSmiths(mine) >= (mine.Any(b => b.Definition.Name == Toolsmith) ? 1 : 2);

    /// <summary>A toolsmith no smith can come to: a weaponsmith comes down, its smith goes over (unstaffed ones first, they would take him).</summary>
    private void FreeSmithForToolsmith(List<Building> mine)
    {
        var toolsmith = mine.FirstOrDefault(b => b.Definition.Name == Toolsmith && b.Exists);
        if (toolsmith is null || _world.Production.WorkerOf(toolsmith) is not null || SpareSmiths(mine) >= 0)
            return;
        var smithy = mine.Where(b => b.Exists && b.Definition.Worker == SmithTrade && b != toolsmith)
            .OrderBy(b => _world.Production.WorkerOf(b) is null ? 0 : 1).FirstOrDefault();
        if (smithy is null)
            return;
        TearDown(smithy, "its smith for the toolsmith");
    }
}
