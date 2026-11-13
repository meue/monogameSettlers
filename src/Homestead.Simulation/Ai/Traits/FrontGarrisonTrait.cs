using System.Text.Json;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Ai.Traits;

/// <summary>
/// Garrison by front (<c>frontGarrison</c> in docs/KI-TACTICS.md): military buildings within
/// <c>frontDistance</c> of an enemy military building (sites included) are fully staffed, those further
/// back get only <c>backline</c> soldiers. A building stays front until no enemy stands within
/// <c>frontDistance</c> + <see cref="Hysteresis"/> anymore, so the garrison does not flicker at the
/// threshold. The soldiers that come out are free for strikes and campaigns.
/// </summary>
internal sealed class FrontGarrisonTrait(JsonElement settings) : AiTrait
{
    public const string TraitId = "frontGarrison";

    private readonly int _frontDistance = (int)Setting(settings, "frontDistance", 45);
    private readonly int _backline = (int)Setting(settings, "backline", 1);
    private const int Hysteresis = 10;

    /// <summary>Own buildings classified front at the last military think, by id (saved with the game).</summary>
    private readonly HashSet<int> _front = new();

    public override string Id => TraitId;

    internal override int? GarrisonLimit(AiPlayer ai, Building building)
    {
        bool EnemyWithin(int distance) => ai.World.Buildings.Any(b => b.Exists && ai.IsEnemy(b.Player)
                                                                      && b.Definition.Occupiers.Count > 0
                                                                      && PathFinder.Distance(b.Position, building.Position) <= distance);
        bool front = _front.Contains(building.Id) ? EnemyWithin(_frontDistance + Hysteresis) : EnemyWithin(_frontDistance);
        if (front)
            _front.Add(building.Id);
        else
            _front.Remove(building.Id);
        return front ? null : _backline;
    }
}
