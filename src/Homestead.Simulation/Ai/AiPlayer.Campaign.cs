using Homestead.Simulation.Buildings;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Ai;

/// <summary>
/// Campaigns against enemy land: the AI picks an occupied enemy military building near its own land,
/// weakly defended and reachable without running into enemy groups, gathers enough spare soldiers
/// at its own military building nearest to it, and attacks once they are together (see <c>attack</c>
/// in ai.json). A trait of the character may lead the campaign instead (<see cref="AiTrait.LeadCampaign"/>).
/// </summary>
public sealed partial class AiPlayer
{
    private Campaign? _campaign;

    internal sealed class Campaign(Building target, List<Settler> soldiers, GridPoint rally, long since)
    {
        public Building Target { get; } = target;
        public List<Settler> Soldiers { get; } = soldiers;

        /// <summary>Door of the own military building nearest the target, where the soldiers gather.</summary>
        public GridPoint Rally { get; } = rally;
        public long Since { get; } = since;
        public bool Attacking { get; set; }

        /// <summary>The trait leading it, or null for the built-in gather-and-attack.</summary>
        public CampaignLeader? Leader { get; set; }

        /// <summary>Wounded soldiers withdrawn from the fight for now (see <see cref="TendWounded"/>).</summary>
        public HashSet<Settler> Resting { get; } = new();
    }

    /// <summary>While gathering, a campaign is given up only below this share of the abort ratio (it started with just enough).</summary>
    private const double GatherSlack = 0.85;

    /// <summary>A target given up or retreated from is left alone this long (otherwise the next think gathers against it again).</summary>
    private const int GiveUpTicks = 60 * World.TicksPerSecond;

    /// <summary>Per target building id: no campaign against it before this tick.</summary>
    private readonly Dictionary<int, long> _givenUp = new();

    /// <param name="only">A challenge (<see cref="Challenge"/>) or superiority (<see cref="CheckSuperiority"/>): only against this
    /// player, whatever the armies, the distance, the character's timing and its ratio say (one to one is enough).</param>
    /// <param name="ratio">With <paramref name="only"/>: the force against the defence instead of one to one.</param>
    /// <param name="offensive">An offensive going on (<see cref="ContinueOffensive"/>): any enemy near its focus or the allied
    /// front, like a challenge otherwise.</param>
    private void StartCampaign(List<Building> military, List<Settler> spare, int? only = null, double? ratio = null, Offensive? offensive = null)
    {
        var rules = _config.Attack;
        bool forced = only is not null || offensive is not null;
        // Bowmen do not attack buildings: they stay home for defence and guerilla.
        var fighters = spare.Where(s => s.SoldierType != SoldierType.Bowman).ToList();
        if (fighters.Count < (forced ? 1 : rules.MinSoldiers))
            return;
        // Decision point "timing": a trait may hold campaigns back.
        if (!forced && Character.Traits.Any(t => !t.AllowCampaign(this)))
            return;
        if (ChooseCampaign(military, fighters, only, ratio, offensive) is not { } chosen)
            return;
        spare.RemoveAll(chosen.Force.Contains);
        fighters.RemoveAll(chosen.Force.Contains);
        // A share of the other spare soldiers comes along (all of them: full strength); against a challenger at least half.
        double commit = forced ? Math.Max(rules.Commit, ChallengeCommit) : rules.Commit;
        var along = fighters.OrderBy(s => PathFinder.Distance(s.Position, chosen.Rally.Door))
            .Take((int)Math.Ceiling(fighters.Count * commit)).ToList();
        chosen.Force.AddRange(along);
        spare.RemoveAll(along.Contains);
        var campaign = new Campaign(chosen.Target, chosen.Force, chosen.Rally.Door, _world.Tick);
        _campaign = campaign;
        foreach (var trait in Character.Traits)
            if ((campaign.Leader = trait.LeadCampaign(this, campaign, spare)) is not null)
                return;
        _world.Military.Move(chosen.Force, chosen.Rally.Door);
        Note($"gathers {chosen.Force.Count} soldiers at {chosen.Rally.Position.X},{chosen.Rally.Position.Y} " +
             $"against the {chosen.Target.Definition.Name} of player {chosen.Target.Player + 1} at {chosen.Target.Position.X},{chosen.Target.Position.Y}");
    }

    /// <summary>The target building, the own military building to gather at and the soldiers needed, or null without a target.</summary>
    private (Building Target, Building Rally, List<Settler> Force)? ChooseCampaign(List<Building> military, List<Settler> fighters, int? only,
        double? onlyRatio = null, Offensive? offensive = null)
    {
        var rules = _config.Attack;
        bool forced = only is not null || offensive is not null;
        double ratio = offensive is not null ? (_config.Offensive ?? AiConfig.OffensiveRules.Default).Ratio
            : only is null ? rules.Ratio : onlyRatio ?? Math.Min(rules.Ratio, 1.0);
        int minSoldiers = forced ? 1 : rules.MinSoldiers;
        var front = offensive is null ? [] : AlliedFront();
        var pathFinder = _world.PathFinder!;
        // Team against team; the own army fights on foreign land, the enemy's at home.
        var armies = Armies();
        double ownArmy = TeamArmy(armies, Player);
        (Building Target, Building Rally, List<Settler> Force, double Score)? best = null;
        foreach (var (id, until) in _givenUp.ToList())
            if (_world.Tick >= until)
                _givenUp.Remove(id);
        foreach (var target in _world.Buildings.Where(b => b.Exists && IsEnemy(b.Player) && IsMilitary(b)
                                                           && b.State == BuildingState.Finished && _world.Military.IsOccupied(b)
                                                           && !_givenUp.ContainsKey(b.Id) && (only is null ? !InTruce(b.Player) : b.Player == only)))
        {
            // An offensive goes on near its last target or the allies' front only.
            double? onward = offensive is null ? null : OffensiveDistance(offensive, front, target.Position);
            if (offensive is not null && onward is null)
                continue;
            // Clearly stronger than that enemy as a whole.
            if (!forced && ownArmy < TeamArmy(armies, target.Player) * rules.ArmyRatio)
                continue;
            // An outpost far away could not be held. Measured from the own land: the own military buildings stand
            // some 30 fields behind its edge, as the enemy's do behind theirs.
            if (!forced && rules.MaxDistance > 0 && !OwnLandWithin(target.Position, rules.MaxDistance))
                continue;
            var rally = military.MinBy(b => PathFinder.Distance(b.Position, target.Position))!;
            // A challenge or offensive fights its way through whatever stands on the way.
            if (!pathFinder.AreConnected(rally.Door, target.Door) || !forced && !WayIsClear(rally.Door, target))
                continue;
            double defense = Defense(target);
            var force = new List<Settler>();
            // Allies at the target take part of it on (an ally's campaign under way is joined).
            double strength = AlliedStrength(target.Position, rules.DefenseRadius);
            foreach (var soldier in fighters.OrderBy(s => PathFinder.Distance(s.Position, rally.Door)))
            {
                if (strength >= defense * ratio && force.Count >= minSoldiers)
                    break;
                force.Add(soldier);
                strength += Strength(soldier, target.Position);
            }
            if (strength < defense * ratio || force.Count < minSoldiers)
                continue;
            // Weakly defended and close to the own land first.
            double score = (defense + 1) * (1 + (onward ?? PathFinder.Distance(rally.Position, target.Position)) / 30.0);
            if (best is null || score < best.Value.Score)
                best = (target, rally, force, score);
        }
        return best is { } b ? (b.Target, b.Rally, b.Force) : null;
    }

    /// <summary>Every think: wait for the gathering, attack, retreat when too weak, go home when it is over.</summary>
    private void FollowCampaign()
    {
        if (_campaign is not { } c)
            return;
        var rules = _config.Attack;
        c.Soldiers.RemoveAll(s => s.IsDead || s.IsInside || s.Player != Player); // also won over by a spell
        c.Resting.IntersectWith(c.Soldiers);
        var target = c.Target;
        // Taken by an ally (or the own side) counts as won: otherwise it would besiege a friend's building for good.
        if (!target.Exists || !IsEnemy(target.Player) || c.Soldiers.Count == 0)
        {
            Note(target.Player == Player ? $"took the {target.Definition.Name} at {target.Position.X},{target.Position.Y}"
                : target.Exists && !IsEnemy(target.Player) ? $"player {target.Player + 1} took the {target.Definition.Name} at {target.Position.X},{target.Position.Y}"
                : c.Soldiers.Count == 0 ? $"lost the campaign against {target.Position.X},{target.Position.Y}"
                : $"the {target.Definition.Name} at {target.Position.X},{target.Position.Y} is gone");
            if (c.Soldiers.Count > 0)
                _world.Military.Move(c.Soldiers, HomeDoor());
            _campaign = null;
            return;
        }
        if (c.Attacking)
            TendWounded(c.Soldiers, c.Resting, target.Door, Defense(target), AlliedStrength(target.Position, rules.DefenseRadius),
                back => _world.Military.Attack(back, target));
        if (c.Leader is { } leader)
        {
            if (!leader.Follow(this, c))
                _campaign = null;
            return;
        }

        double strength = CampaignStrength(c), defense = Defense(target);
        if (!c.Attacking)
        {
            // Weighed again every think, also on the way to the gathering point.
            bool gathered = c.Soldiers.All(s => PathFinder.Distance(s.Position, c.Rally) <= _config.Military.GuardDistance);
            // A little below the start's ratio: a defender stepping into range must not call it off at once.
            if (strength < defense * rules.AbortRatio * GatherSlack && !TurnWithSpell(target.Position, strength, defense * rules.AbortRatio))
            {
                Note($"gives up the campaign against {target.Position.X},{target.Position.Y}: the defence got stronger");
                EndOffensive("gave up a campaign");
                _world.Military.Move(c.Soldiers, HomeDoor());
                _givenUp[target.Id] = _world.Tick + GiveUpTicks;
                _campaign = null;
                return;
            }
            if (!gathered && _world.Tick - c.Since < rules.GatherTicks)
                return;
            _world.Military.Attack(c.Soldiers, target);
            c.Attacking = true;
            Note($"attacks the {target.Definition.Name} at {target.Position.X},{target.Position.Y} with {c.Soldiers.Count} soldiers");
            return;
        }
        if (strength < defense * _config.Military.RetreatRatio && !TurnWithSpell(target.Door, strength, defense * _config.Military.RetreatRatio))
        {
            Note($"retreats from {target.Position.X},{target.Position.Y}");
            EndOffensive("retreated");
            _world.Military.Move(c.Soldiers, RetreatDoor(CenterOf(c.Soldiers), target.Door), forced: true);
            _givenUp[target.Id] = _world.Tick + GiveUpTicks;
            _campaign = null;
        }
    }

    /// <summary>
    /// The target's garrison (weighted by <c>attack.garrisonWeight</c>: a castle's is worth more) plus
    /// enemy soldiers outside near it, as they fight at the target, plus the bowmen of other enemy military
    /// buildings that reach it.
    /// </summary>
    internal double Defense(Building target) =>
        TowerFire([target.Position], except: target) +
        _world.Military.Occupants(target).Where(s => s is not null).Sum(s => Strength(s!, target.Position))
        * (_config.Attack.GarrisonWeight?.GetValueOrDefault(target.Definition.Name, 1.0) ?? 1.0)
        + _world.Settlers.Where(s => s.Profession == Profession.Soldier && IsEnemy(s.Player) && !s.IsInside && !s.IsDead
                                     && PathFinder.Distance(s.Position, target.Position) <= _config.Attack.DefenseRadius)
            .Sum(s => Strength(s, target.Position));

    /// <summary>Whether the player's land comes within <paramref name="range"/> steps of <paramref name="at"/>.</summary>
    private bool OwnLandWithin(GridPoint at, int range)
    {
        var map = _world.Map!;
        var counts = OwnLandByRow();
        int size = map.Size;
        // Row by row: the own points in the row's stretch of the hexagon, from running counts.
        for (int dy = -range; dy <= range; dy++)
        {
            int y = at.Y + dy;
            if (y < 0 || y >= size)
                continue;
            int from = Math.Max(0, at.X + Math.Max(-range, dy - range)), to = Math.Min(size - 1, at.X + Math.Min(range, dy + range));
            if (from <= to && counts[y * (size + 1) + to + 1] > counts[y * (size + 1) + from])
                return true;
        }
        return false;
    }

    /// <summary>Own points per row up to each column (row y, column x: index y * (size + 1) + x), redone when land changes hands.</summary>
    [NonSerialized] private int[]? _ownByRow;
    [NonSerialized] private int _ownByRowVersion = -1;

    private int[] OwnLandByRow()
    {
        var map = _world.Map!;
        if (_ownByRow is not null && _ownByRowVersion == map.OwnerVersion)
            return _ownByRow;
        int size = map.Size;
        var counts = _ownByRow ??= new int[size * (size + 1)];
        byte self = (byte)Player;
        for (int y = 0, i = 0; y < size; y++)
        {
            int row = y * (size + 1), count = 0;
            counts[row] = 0;
            for (int x = 0; x < size; x++, i++)
            {
                if (map.OwnerAt(i) == self)
                    count++;
                counts[row + x + 1] = count;
            }
        }
        _ownByRowVersion = map.OwnerVersion;
        return counts;
    }

    /// <summary>All soldiers of each player, in buildings and outside, without the combat strength; by player.</summary>
    private Dictionary<int, double> Armies()
    {
        var armies = new Dictionary<int, double>();
        foreach (var s in _world.Settlers)
            if (s.Profession == Profession.Soldier && !s.IsDead)
                armies[s.Player] = armies.GetValueOrDefault(s.Player) + Power(s);
        return armies;
    }

    /// <summary>No enemy group near the way (groups at the target count as its defence).</summary>
    internal bool WayIsClear(GridPoint from, Building target)
    {
        var rules = _config.Attack;
        int steps = Math.Max(1, PathFinder.Distance(from, target.Position) / 3);
        for (int i = 0; i <= steps; i++)
        {
            var p = new GridPoint(from.X + (target.Position.X - from.X) * i / steps, from.Y + (target.Position.Y - from.Y) * i / steps);
            if (EnemyGroups.Any(g => g.Armed && PathFinder.Distance(g.Center, target.Position) > rules.DefenseRadius
                                     && PathFinder.Distance(g.Center, p) <= rules.PathClearance))
                return false;
        }
        return true;
    }
}
