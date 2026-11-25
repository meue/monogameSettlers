using Homestead.Simulation.Buildings;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Ai;

/// <summary>
/// An offensive (the AI chat: an ally asked to attack a common enemy): it starts like a challenge against that player, but does
/// not stop at one building. Whenever a campaign is over (target taken, destroyed or taken by an ally) the next one starts at
/// once against an occupied military building of any enemy within <c>offensive.range</c> of the last target – or within
/// <c>offensive.frontRange</c> of allied soldiers out on enemy land, which are preferred (marching on together). No time limit:
/// it ends when no target is left within reach or the soldiers at hand are too weak for any, on a retreat or a campaign
/// given up, and with an assault. Saved with the game (lazily: games saved before have none).
/// </summary>
public sealed partial class AiPlayer
{
    internal sealed class Offensive(int player)
    {
        /// <summary>The player the ally asked to attack (the first target's).</summary>
        public int Player { get; } = player;

        /// <summary>Where the last target stood, null until the first campaign has started.</summary>
        public GridPoint? Focus { get; set; }

        /// <summary>Campaigns started so far.</summary>
        public int Campaigns { get; set; }
    }

    private Offensive? _offensive;

    /// <summary>The player its offensive was started against, or null without one.</summary>
    public int? OffensiveAgainst => _offensive?.Player;

    /// <summary>Starts an offensive against <paramref name="player"/>: a challenge at the next think, then target after target.</summary>
    public bool StartOffensive(int player)
    {
        if (_offensive is not null || !Challenge(player))
            return false;
        _offensive = new Offensive(player);
        Note($"starts an offensive against player {player + 1}");
        return true;
    }

    /// <summary>Right after a challenge was taken up (or not): the first target is the focus, without one the offensive is over.</summary>
    private void OffensiveTakenUp()
    {
        if (_offensive is not { Focus: null } o)
            return;
        if (_campaign is null)
        {
            EndOffensive("no target within reach");
            return;
        }
        o.Focus = _campaign.Target.Position;
        o.Campaigns++;
    }

    /// <summary>No campaign under way: the next target near the last one or the allies' front, or the end.</summary>
    private void ContinueOffensive(List<Building> military, List<Settler> spare)
    {
        var o = _offensive!;
        StartCampaign(military, spare, offensive: o);
        if (_campaign is null)
        {
            EndOffensive("no target left within reach");
            return;
        }
        o.Focus = _campaign.Target.Position;
        o.Campaigns++;
    }

    private void EndOffensive(string why)
    {
        if (_offensive is not { } o)
            return;
        Note($"the offensive started against player {o.Player + 1} is over after {o.Campaigns} campaigns: {why}");
        _offensive = null;
    }

    /// <summary>
    /// Positions of allied soldiers (not the own) outside on land of one of its enemies, thinned out to one per 8×8 fields:
    /// where the allies fight, the offensive goes too.
    /// </summary>
    private List<GridPoint> AlliedFront()
    {
        var map = _world.Map!;
        var seen = new HashSet<(int, int)>();
        var front = new List<GridPoint>();
        foreach (var s in _world.Settlers)
        {
            if (s.Profession != Profession.Soldier || s.IsDead || s.IsInside || s.Player == Player || IsEnemy(s.Player))
                continue;
            if (map.OwnerAt(s.Position) is not { } owner || !IsEnemy(owner) || !seen.Add((s.Position.X / 8, s.Position.Y / 8)))
                continue;
            front.Add(s.Position);
        }
        return front;
    }

    /// <summary>
    /// How far an offensive's possible target is (for the score), null when out of reach: from the last target within
    /// <c>offensive.range</c>, from the allied front within <c>offensive.frontRange</c> – the front counts as half the way.
    /// </summary>
    private double? OffensiveDistance(Offensive o, List<GridPoint> front, GridPoint target)
    {
        var rules = _config.Offensive ?? AiConfig.OffensiveRules.Default;
        double? best = null;
        if (o.Focus is { } focus && PathFinder.Distance(focus, target) is var d && d <= rules.Range)
            best = d;
        foreach (var point in front)
            if (PathFinder.Distance(point, target) is var f && f <= rules.FrontRange && (best is null || f / 2.0 < best))
                best = f / 2.0;
        return best;
    }
}
