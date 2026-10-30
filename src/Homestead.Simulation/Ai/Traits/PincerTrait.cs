using System.Text.Json;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Ai.Traits;

/// <summary>
/// Pincer and feint (<c>pincer</c> in docs/KI-TACTICS.md). A campaign with at least <c>minSoldiers</c>
/// is split: a small feint (<c>feintShare</c>) gathers at the own building nearest the target as usual,
/// the main force walks round in an arc to a staging point at the target's side (<c>flankAngle</c>
/// degrees off the feint's direction, <c>flankDistance</c> fields away, at least out of reach of the
/// target's bowmen). Once both are in place the feint attacks and draws the defenders out;
/// <c>strikeSeconds</c> later the main force strikes from the side. Of the two sides the one with fewer
/// enemies around staging point and arc is taken, a coin decides between equal ones. The way round takes
/// time, so <c>extraShare</c> more of the spare soldiers come along, and the attack is only given up when
/// the soldiers have become weaker than the defence (<c>military.retreatRatio</c>).
/// </summary>
internal sealed class PincerTrait(JsonElement settings) : AiTrait
{
    public const string TraitId = "pincer";

    private readonly int _minSoldiers = (int)Setting(settings, "minSoldiers", 4);
    private readonly double _feintShare = Setting(settings, "feintShare", 0.35);
    private readonly double _flankAngle = Setting(settings, "flankAngle", 110) * Math.PI / 180;
    private readonly int _flankDistance = (int)Setting(settings, "flankDistance", 14);
    private readonly int _strikeTicks = (int)(Setting(settings, "strikeSeconds", 8) * World.TicksPerSecond);
    private readonly double _extraShare = Setting(settings, "extraShare", 0.5);
    private Random? _random;

    public override string Id => TraitId;

    internal override CampaignLeader? LeadCampaign(AiPlayer ai, AiPlayer.Campaign campaign, List<Settler> spare)
    {
        if (campaign.Soldiers.Count < _minSoldiers)
            return null;
        var world = ai.World;
        _random ??= new Random(4242 + world.Seed);
        int bowRange = world.Military.Config.Soldiers[SoldierType.Bowman].Range;
        double distance = Math.Max(_flankDistance, bowRange + 3);
        (GridPoint Staging, GridPoint Waypoint, double Danger)? best = null;
        foreach (int side in _random.Next(2) == 0 ? new[] { 1, -1 } : new[] { -1, 1 })
            if (Flank(ai, campaign, side, distance) is { } flank && (best is null || flank.Danger < best.Value.Danger))
                best = flank;
        if (best is not { } chosen)
            return null;

        var extra = spare.OrderBy(s => PathFinder.Distance(s.Position, campaign.Rally))
            .Take((int)Math.Ceiling(campaign.Soldiers.Count * _extraShare)).ToList();
        campaign.Soldiers.AddRange(extra);
        spare.RemoveAll(extra.Contains);
        int feintCount = Math.Max(2, (int)Math.Round(campaign.Soldiers.Count * _feintShare));
        var feint = campaign.Soldiers.OrderBy(s => PathFinder.Distance(s.Position, campaign.Rally)).Take(feintCount).ToList();
        var main = campaign.Soldiers.Except(feint).ToList();
        world.Military.Move(feint, campaign.Rally);
        world.Military.Move(main, chosen.Waypoint);
        var target = campaign.Target;
        ai.Note($"pincer against the {target.Definition.Name} of player {target.Player + 1} at {target.Position.X},{target.Position.Y}: " +
                $"{feint.Count} feint from {campaign.Rally.X},{campaign.Rally.Y}, {main.Count} go round via {chosen.Waypoint.X},{chosen.Waypoint.Y} " +
                $"to {chosen.Staging.X},{chosen.Staging.Y}");
        return new Leader(this, feint, main, chosen.Staging, chosen.Waypoint, world.Tick);
    }

    /// <summary>Staging point and waypoint of the arc on one side (+1/-1), with the enemy strength around them.</summary>
    private (GridPoint Staging, GridPoint Waypoint, double Danger)? Flank(AiPlayer ai, AiPlayer.Campaign campaign, int side, double distance)
    {
        var (tx, ty) = Plane(campaign.Target.Position);
        var (rx, ry) = Plane(campaign.Rally);
        double dx = rx - tx, dy = ry - ty, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
            return null;
        dx /= length;
        dy /= length;
        GridPoint? At(double angle, double radius)
        {
            double cos = DeterministicMath.Cos(angle), sin = DeterministicMath.Sin(angle);
            return NearestOpen(ai, Grid(tx + (dx * cos - dy * sin) * radius, ty + (dx * sin + dy * cos) * radius), campaign.Rally);
        }
        if (At(side * _flankAngle, distance) is not { } staging || At(side * _flankAngle / 2, Math.Max((length + distance) / 2, distance + 4)) is not { } waypoint)
            return null;
        int clearance = ai.Config.Attack.PathClearance;
        double danger = ai.EnemyGroups.Where(g => PathFinder.Distance(g.Center, staging) <= clearance || PathFinder.Distance(g.Center, waypoint) <= clearance)
            .Sum(g => g.Strength);
        return (staging, waypoint, danger);
    }

    /// <summary>The walkable point nearest <paramref name="p"/> (within a few fields) that can be reached from <paramref name="from"/>.</summary>
    private static GridPoint? NearestOpen(AiPlayer ai, GridPoint p, GridPoint from)
    {
        var map = ai.World.Map!;
        var pathFinder = ai.World.PathFinder!;
        for (int r = 0; r <= 4; r++)
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                var q = new GridPoint(p.X + dx, p.Y + dy);
                if (PathFinder.Distance(p, q) == r && map.Contains(q) && map.IsWalkable(q) && pathFinder.AreConnected(from, q))
                    return q;
            }
        return null;
    }

    // The hex grid in the plane: (1,0), (0,1) and (1,1) are the neighbors at distance 1.
    private static (double X, double Y) Plane(GridPoint p) => (p.X - p.Y / 2.0, p.Y * Math.Sqrt(3) / 2);

    private static GridPoint Grid(double x, double y)
    {
        double gy = y / (Math.Sqrt(3) / 2);
        return new GridPoint((int)Math.Round(x + gy / 2), (int)Math.Round(gy));
    }

    /// <summary>Round the arc, wait for both groups, feint, then strike from the side; retreat when too weak.</summary>
    private sealed class Leader(PincerTrait trait, List<Settler> feint, List<Settler> main, GridPoint staging, GridPoint waypoint, long since)
        : CampaignLeader
    {
        private Phase _phase;
        private long _feintAt;

        public override bool Follow(AiPlayer ai, AiPlayer.Campaign campaign)
        {
            feint.RemoveAll(s => !campaign.Soldiers.Contains(s));
            main.RemoveAll(s => !campaign.Soldiers.Contains(s));
            var world = ai.World;
            var target = campaign.Target;
            int near = ai.Config.Military.GuardDistance;
            long gather = ai.Config.Attack.GatherTicks;
            bool At(List<Settler> group, GridPoint p) => group.All(s => PathFinder.Distance(s.Position, p) <= near);
            double Strength() => ai.CampaignStrength(campaign);
            string where = $"{target.Position.X},{target.Position.Y}";

            switch (_phase)
            {
                case Phase.Round:
                    if (At(main, waypoint) || world.Tick - since >= gather)
                    {
                        world.Military.Move(main, staging);
                        _phase = Phase.Stage;
                    }
                    return true;

                case Phase.Stage:
                    if (!(At(feint, campaign.Rally) && At(main, staging)) && world.Tick - since < 2 * gather)
                        return true;
                    if (Strength() < ai.Defense(target) * ai.Config.Military.RetreatRatio)
                    {
                        ai.Note($"pincer: gives up against {where}: the defence got stronger");
                        world.Military.Move(campaign.Soldiers, ai.HomeDoor());
                        return false;
                    }
                    campaign.Attacking = true;
                    if (feint.Count == 0 || main.Count == 0)
                    {
                        world.Military.Attack(campaign.Soldiers, target);
                        _phase = Phase.Strike;
                        ai.Note($"pincer: attacks {where} with {campaign.Soldiers.Count} soldiers at once");
                        return true;
                    }
                    world.Military.Attack(feint, target);
                    _feintAt = world.Tick;
                    _phase = Phase.Feint;
                    ai.Note($"pincer: {feint.Count} feint against {where}");
                    return true;

                case Phase.Feint:
                    if (world.Tick - _feintAt < trait._strikeTicks && feint.Count > 0 && main.Count > 0)
                        return true;
                    _phase = Phase.Strike;
                    if (main.Count == 0)
                    {
                        ai.Note($"pincer: the main force against {where} is lost, the feint fights on");
                        return true;
                    }
                    world.Military.Attack(main, target);
                    ai.Note($"pincer: {main.Count} strike {where} from the side");
                    return true;

                default:
                    if (Strength() < ai.Defense(target) * ai.Config.Military.RetreatRatio)
                    {
                        ai.Note($"pincer: retreats from {where}");
                        world.Military.Move(campaign.Soldiers, ai.RetreatDoor(AiPlayer.CenterOf(campaign.Soldiers), target.Door), forced: true);
                        return false;
                    }
                    return true;
            }
        }

        private enum Phase
        {
            Round,
            Stage,
            Feint,
            Strike,
        }
    }
}
