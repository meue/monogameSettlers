using Homestead.Simulation.Buildings;
using Homestead.Simulation.Magic;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Ai;

/// <summary>
/// The AI's soldiers outside buildings. It knows where every enemy soldier is (for now). Defence comes
/// first: enemies on the own land are fought at once, and enough soldiers stay back against enemies
/// close to it. Idle soldiers strike enemy groups (soldiers, also geologists and scouts revealed to it) near the land when they clearly outnumber them
/// (guerilla) – also those held back against them, as long as enough stay against the others – and spare ones go on campaigns against enemy military buildings (see AiPlayer.Campaign). Strikes
/// and campaigns are re-checked every think (follow, reinforce, retreat); afterwards the soldiers go
/// home. The rest are placed by the character's traits (decision point "guarding"). Soldiers walking on a
/// plain move order of the AI (to a post, home) count as idle: their order is weighed again every think.
/// Retreating ones (forced move order) are free for defence, strikes and campaigns too, but guarding leaves
/// them walking on until they are there.
/// </summary>
public sealed partial class AiPlayer
{
    private long _nextMilitary;
    private readonly List<Strike> _strikes = new();

    /// <summary>After a guerilla strike is called off, its enemies are left alone this long (no strike, retreat, strike again …).</summary>
    private const long CalledOffTicks = 30 * World.TicksPerSecond;

    /// <summary>Enemy soldiers a strike against was called off, with the tick guerilla may go for them again.</summary>
    private readonly Dictionary<Settler, long> _leftAlone = new();

    /// <summary>A running guerilla attack: who strikes, whom, and where the enemies were last.</summary>
    internal sealed class Strike(List<Settler> soldiers, HashSet<Settler> targets, GridPoint center, bool guerilla)
    {
        /// <summary>Outside the own land by choice (not defence): given up under tower fire.</summary>
        public bool Guerilla { get; } = guerilla;

        public List<Settler> Soldiers { get; } = soldiers;
        public HashSet<Settler> Targets { get; set; } = targets;
        public GridPoint Center { get; set; } = center;

        /// <summary>Wounded strikers withdrawn from the fight for now (see <see cref="TendWounded"/>).</summary>
        public HashSet<Settler> Resting { get; } = new();

        /// <summary>The strikers in the fight: all but the withdrawn.</summary>
        public IEnumerable<Settler> Fighting => Soldiers.Where(s => !Resting.Contains(s));

        /// <summary>The trait leading it, or null for the built-in all-in.</summary>
        public StrikeLeader? Leader { get; set; }
    }

    /// <summary>Enemy soldiers, geologists and revealed scouts outside buildings, grouped (seen at the last military think).</summary>
    public IReadOnlyList<EnemyGroup> EnemyGroups { get; private set; } = [];

    /// <summary>
    /// Enemy soldiers (and geologists and scouts, which do not fight) close together; <see cref="Strength"/> as they would
    /// fight at <see cref="Center"/>.
    /// </summary>
    public sealed record EnemyGroup(GridPoint Center, IReadOnlyList<Settler> Soldiers, double Strength)
    {
        /// <summary>Whether there are soldiers among them (not just geologists or scouts).</summary>
        public bool Armed => Soldiers.Any(s => s.Profession == Profession.Soldier);
    }

    private void Command(List<Building> mine)
    {
        var config = _config.Military;
        EnemyGroups = FindEnemyGroups(config.GroupRadius);
        var military = mine.Where(b => IsMilitary(b) && b.State == BuildingState.Finished).ToList();
        if (military.Count == 0)
            return;
        var idle = IdleSoldiers();
        InspectScouts(military, idle);
        Staff(military, idle);
        var map = _world.Map!;
        bool onOwnLand(EnemyGroup g) => map.OwnerAt(g.Center) == Player;
        bool near(EnemyGroup g, int range) => military.Any(b => PathFinder.Distance(b.Position, g.Center) <= range);
        bool untargeted(EnemyGroup g) => !_strikes.Any(k => g.Soldiers.Any(k.Targets.Contains));

        // Enemies on the own land break a truce with their player.
        foreach (var group in EnemyGroups.Where(g => g.Armed && onOwnLand(g)))
            foreach (int player in group.Soldiers.Select(s => s.Player).Distinct().Where(InTruce).ToList())
                EndTruce(player, "its soldiers stand on the own land");
        // Defence first: enemies on the own land are fought with any idle soldier.
        foreach (var group in EnemyGroups.Where(g => onOwnLand(g) && untargeted(g)).OrderByDescending(g => g.Strength))
            TryStrike(group, idle, "defends against", guerilla: false);

        bool threatening(EnemyGroup g) => !onOwnLand(g) && near(g, config.ThreatRange) && untargeted(g);

        // Guerilla outside the own land, the weakest worthwhile group first. It comes before holding
        // soldiers back: the defenders may strike the enemies they stand against themselves; only
        // those needed against the other groups close to the land stay out of it. An attack on the land
        // (enemies under way or fighting) is met anywhere; enemies just standing around not deep in
        // their own land (military.hinterlandDepth).
        foreach (var (enemy, until) in _leftAlone.ToList())
            if (_world.Tick >= until || enemy.IsDead)
                _leftAlone.Remove(enemy);
        foreach (var group in EnemyGroups.Where(g => !onOwnLand(g) && near(g, config.StrikeRange) && untargeted(g)
                                                     && !g.Soldiers.Any(_leftAlone.ContainsKey) && !InTruce(g)).OrderBy(g => g.Strength))
        {
            bool standing = IsStanding(group);
            if (standing && InHinterland(group))
                continue;
            var pool = idle.Except(Reserve(idle, EnemyGroups.Where(g => g != group && threatening(g)))).ToList();
            if (!TryStrike(group, pool, "strikes", guerilla: true, standing ? "standing around" : "attacking the land"))
                continue;
            idle.RemoveAll(_strikes[^1].Soldiers.Contains);
            break;
        }

        // Helping an ally: the helpers are not among the idle any more.
        FollowHelp();

        // Against the enemies still close to the land enough soldiers stay back; only the rest is spare.
        var guards = Reserve(idle, EnemyGroups.Where(threatening));
        var spare = idle.Except(guards).ToList();

        // A campaign against enemy land, never while enemy soldiers stand on the own land; a challenge first (also then).
        if (_campaign is null && ChallengedBy is { } challenger)
        {
            StartCampaign(military, spare, only: challenger);
            Note(_campaign is null ? $"cannot take up the challenge of player {challenger + 1}: no target within reach"
                : $"takes up the challenge of player {challenger + 1}");
            ChallengedBy = null;
            OffensiveTakenUp();
        }
        else if (_campaign is null && _offensive is not null)
            ContinueOffensive(military, spare);
        if (_campaign is null && !EnemyGroups.Any(g => g.Armed && onOwnLand(g)))
            CheckSuperiority(military, spare);
        if (_campaign is null && !EnemyGroups.Any(g => g.Armed && onOwnLand(g)))
            StartCampaign(military, spare);

        // Decision point "guarding": the first trait that takes it places the idle soldiers; without one they stay where they are.
        // Retreating soldiers walk on: only a new action stops their retreat.
        var rest = guards.Concat(spare).Where(s => _world.Military.ForcedMoveTarget(s) is null).ToList();
        // The wounded among them go to a hospital first.
        var patients = SendToHospital(rest);
        rest.RemoveAll(patients.Contains);
        foreach (var trait in Character.Traits)
            if (trait.Guard(this, military, rest))
                break;

        // Decision point "errands": every trait may send settlers off on its own.
        foreach (var trait in Character.Traits)
            trait.RunErrands(this, military);
    }

    /// <summary>
    /// The AI wants its military buildings full: bowmen on the bowman places; the infantry there stays,
    /// free infantry places go to idle swordsmen, then idle pikemen, the rest waits for swordsmen.
    /// Decision point "garrison": a trait may limit a building to fewer soldiers (infantry first, at
    /// least one); the others come out.
    /// </summary>
    private void Staff(List<Building> military, List<Settler> idle)
    {
        var m = _world.Military;
        int freeSwordsmen = idle.Count(s => s.SoldierType == SoldierType.Swordsman);
        int freePikemen = idle.Count(s => s.SoldierType == SoldierType.Pikeman);
        foreach (var building in military)
        {
            int places = building.Definition.Occupiers.Count;
            int limit = AssaultOn is not null ? 1 : Math.Max(1, Character.Traits.Select(t => t.GarrisonLimit(this, building)).Min() ?? places);
            if (_releasing.Contains(building))
                limit = Math.Max(1, Math.Min(limit, m.Occupants(building).Count(o => o is not null) - 1)); // one out to look at a suspect
            int infantryPlaces = building.Definition.Occupiers.Count(o => !o.ForBowman);
            int infantry = Math.Min(infantryPlaces, limit);
            int swordsmen = m.Assigned(building, SoldierType.Swordsman), pikemen = m.Assigned(building, SoldierType.Pikeman);
            // Over the limit: pikemen leave before swordsmen.
            int over = Math.Max(0, swordsmen + pikemen - infantry);
            int pikemenLeaving = Math.Min(over, pikemen);
            pikemen -= pikemenLeaving;
            swordsmen -= over - pikemenLeaving;
            int room = Math.Max(0, infantry - swordsmen - pikemen);
            int moreSwordsmen = Math.Min(room, freeSwordsmen), morePikemen = Math.Min(room - moreSwordsmen, freePikemen);
            freeSwordsmen -= moreSwordsmen;
            freePikemen -= morePikemen;
            var target = new Dictionary<SoldierType, int>
            {
                [SoldierType.Swordsman] = swordsmen + room - morePikemen,
                [SoldierType.Pikeman] = pikemen + morePikemen,
                [SoldierType.Bowman] = Math.Min(places - infantryPlaces, limit - infantry),
            };
            // Fewer first (frees places), then more, then fewer again (a building is never left wanting none).
            for (int pass = 0; pass < 3; pass++)
                foreach (var (type, count) in target)
                {
                    int sign = pass == 1 ? 1 : -1;
                    while (sign * (count - m.Wanted(building, type)) > 0 && m.ChangeWanted(building, type, sign))
                    {
                    }
                }
        }
    }

    /// <summary>
    /// Enemies not attacking the land: none of them fights, and those walking stay on their own player's land
    /// (a recruit on his way to the gathering point, soldiers moving to a post).
    /// </summary>
    private bool IsStanding(EnemyGroup group)
    {
        var map = _world.Map!;
        return group.Soldiers.All(s => s.Activity is not (Activity.Fighting or Activity.Shooting)
                                       && (s.Destination is not { } to || map.OwnerAt(to) == s.Player));
    }

    /// <summary>
    /// Whether the group stands deeper than <see cref="AiConfig.MilitaryRules.HinterlandDepth"/> in the land of
    /// the player it stands on (other than this one): no point of other land within that distance.
    /// </summary>
    private bool InHinterland(EnemyGroup group)
    {
        int depth = _config.Military.HinterlandDepth;
        var map = _world.Map!;
        if (depth <= 0 || map.OwnerAt(group.Center) is not { } owner || owner == Player)
            return false;
        foreach (var p in PathFinder.Circle(group.Center, depth))
            if (map.Contains(p) && map.OwnerAt(p) != owner)
                return false;
        return true;
    }

    /// <summary>
    /// Sends the nearest soldiers of <paramref name="pool"/> against the group if they get strikeRatio
    /// times the strength they will meet: the group plus enemy groups waiting on the way there. Guerilla
    /// leaves enemies alone that stand under too much tower fire (<see cref="UnderTowerFire"/>).
    /// </summary>
    private bool TryStrike(EnemyGroup group, List<Settler> pool, string verb, bool guerilla, string? remark = null)
    {
        var config = _config.Military;
        var candidates = pool.Where(s => PathFinder.Distance(s.Position, group.Center) <= config.StrikeRange * 2)
            .OrderBy(s => PathFinder.Distance(s.Position, group.Center)).ToList();
        if (candidates.Count == 0)
            return false;
        // What they meet depends on where they come from: first from the nearest, then from the middle of
        // those chosen (most of them may walk past enemy towers the nearest one does not), until it holds.
        var from = candidates[0].Position;
        var strikers = new List<Settler>();
        for (int round = 0; round < 3; round++)
        {
            if (guerilla && UnderTowerFire(group, from))
                return false;
            double opposition = Opposition(group, from);
            strikers.Clear();
            // Allies fighting there already: joining them is worth more.
            double strength = AlliedStrength(group.Center, StrikeSupportRadius);
            foreach (var soldier in candidates)
            {
                // Geologists and scouts alone do not fight back: one soldier will do.
                if (strength >= opposition * config.StrikeRatio && strikers.Count > 0)
                    break;
                strikers.Add(soldier);
                strength += Strength(soldier, group.Center);
            }
            if (strength < opposition * config.StrikeRatio)
                return false;
            var middle = CenterOf(strikers);
            if (PathFinder.Distance(middle, from) <= 3)
                break;
            from = middle;
            if (round == 2 && (guerilla && UnderTowerFire(group, from) || strength < Opposition(group, from) * config.StrikeRatio))
                return false;
        }
        var strike = new Strike(strikers, group.Soldiers.ToHashSet(), group.Center, guerilla);
        _strikes.Add(strike);
        Note($"{verb} {group.Soldiers.Count} enemies at {group.Center.X},{group.Center.Y} with {strikers.Count} soldiers{(remark is null ? "" : $" ({remark})")}");
        // Decision point "leading fights": the first trait that takes the strike places the strikers; otherwise they all go in.
        foreach (var trait in Character.Traits)
            if ((strike.Leader = trait.LeadStrike(this, strike, group)) is not null)
                break;
        if (strike.Leader is null)
            _world.Military.Move(strikers, group.Center);
        pool.RemoveAll(strikers.Contains);
        return true;
    }

    /// <summary>The idle soldiers that stay back against <paramref name="threats"/>: strikeRatio times their strength, nearest first.</summary>
    private List<Settler> Reserve(List<Settler> idle, IEnumerable<EnemyGroup> threats)
    {
        var groups = threats.ToList();
        // They would fight on the own land: there the guards have their full strength, the enemies their reduced one.
        double danger = groups.SelectMany(g => g.Soldiers).Sum(s => Power(s) * _world.Military.CombatStrength(s.Player)) * _config.Military.StrikeRatio;
        var guards = new List<Settler>();
        // Allies standing against them hold part of it.
        double kept = groups.Sum(g => AlliedStrength(g.Center, _config.Military.ThreatRange));
        foreach (var soldier in idle.OrderBy(s => groups.Select(g => PathFinder.Distance(s.Position, g.Center)).DefaultIfEmpty(0).Min()))
        {
            if (kept >= danger)
                break;
            guards.Add(soldier);
            kept += Power(soldier);
        }
        return guards;
    }

    /// <summary>Whether enemy soldiers stand within <c>military.threatRange</c> of an own military building.</summary>
    internal bool Threatened(List<Building> military) =>
        EnemyGroups.Any(g => g.Armed && military.Any(b => PathFinder.Distance(b.Position, g.Center) <= _config.Military.ThreatRange));

    [NonSerialized] private List<Building>? _enemyMilitary;
    [NonSerialized] private long _enemyMilitaryTick = -1;

    /// <summary><see cref="EnemyMilitary"/> once per tick: tower fire is weighed for many groups and ways.</summary>
    private List<Building> EnemyMilitaryNow()
    {
        if (_enemyMilitary is null || _enemyMilitaryTick != _world.Tick)
            (_enemyMilitary, _enemyMilitaryTick) = (EnemyMilitary(), _world.Tick);
        return _enemyMilitary;
    }

    /// <summary>Occupied military buildings of other players (where the enemy is).</summary>
    internal List<Building> EnemyMilitary() =>
        _world.Buildings.Where(b => b.Exists && IsEnemy(b.Player) && IsMilitary(b) && b.State == BuildingState.Finished).ToList();

    /// <summary>
    /// Shares the idle soldiers out evenly among the posts (the first ones get one more where it does not
    /// come out even). Who is already at a post or on the way to it (within <c>military.guardDistance</c>)
    /// stays there while it has room; the others go to the nearest post with room left.
    /// </summary>
    internal void Distribute(IReadOnlyList<Building> posts, List<Settler> idle)
    {
        if (posts.Count == 0)
            return;
        int guardDistance = _config.Military.GuardDistance;
        var room = posts.Select((_, i) => idle.Count / posts.Count + (i < idle.Count % posts.Count ? 1 : 0)).ToArray();
        var left = new List<Settler>();
        foreach (var soldier in idle)
        {
            int at = Enumerable.Range(0, posts.Count).Where(i => room[i] > 0)
                .FirstOrDefault(i => PathFinder.Distance(Heading(soldier), posts[i].Door) <= guardDistance, -1);
            if (at >= 0)
                room[at]--;
            else
                left.Add(soldier);
        }
        var sent = new Dictionary<int, List<Settler>>();
        foreach (var soldier in left)
        {
            int to = Enumerable.Range(0, posts.Count).Where(i => room[i] > 0).MinBy(i => PathFinder.Distance(soldier.Position, posts[i].Door));
            room[to]--;
            (sent.TryGetValue(to, out var list) ? list : sent[to] = new List<Settler>()).Add(soldier);
        }
        foreach (var (i, soldiers) in sent)
            _world.Military.Move(soldiers, posts[i].Door);
        idle.Clear();
    }

    /// <summary>Where soldiers go when a strike or campaign is over.</summary>
    internal GridPoint HomeDoor() => _home is { Exists: true } h && h.Player == Player ? h.Door
        : _world.Buildings.Where(b => b.Exists && b.Player == Player && IsMilitary(b)).Select(b => b.Door).FirstOrDefault();

    /// <summary>
    /// The group's strength plus that of other enemy groups near the way from <paramref name="from"/> to it,
    /// of the other enemy soldiers within <c>military.reinforceRadius</c> of it (they come to help) and of the
    /// bowmen in enemy military buildings within reach of the way or the group (they shoot from safety: a
    /// single soldier next to a full castle is not alone).
    /// </summary>
    private double Opposition(EnemyGroup group, GridPoint from)
    {
        int clearance = _config.Attack.PathClearance;
        var way = Way(from, group.Center);
        var members = group.Soldiers.ToHashSet();
        bool other(EnemyGroup g) => g != group && !g.Soldiers.Any(members.Contains);
        var onTheWay = EnemyGroups.Where(g => other(g) && way.Any(p => PathFinder.Distance(g.Center, p) <= clearance)).ToList();
        int reinforce = _config.Military.ReinforceRadius;
        double helpers = EnemyGroups.Except(onTheWay).Where(other)
            .SelectMany(g => g.Soldiers)
            .Where(s => s.Profession == Profession.Soldier && PathFinder.Distance(s.Position, group.Center) <= reinforce)
            .Sum(s => Strength(s, group.Center));
        return group.Strength + onTheWay.Sum(g => g.Strength) + helpers + TowerFire(way);
    }

    private void LeaveAlone(EnemyGroup group)
    {
        foreach (var enemy in group.Soldiers)
            _leftAlone[enemy] = _world.Tick + CalledOffTicks;
    }

    /// <summary>Points every 3 fields on the straight line from <paramref name="from"/> to <paramref name="to"/>, both ends included.</summary>
    private static List<GridPoint> Way(GridPoint from, GridPoint to)
    {
        int steps = Math.Max(1, PathFinder.Distance(from, to) / 3);
        return Enumerable.Range(0, steps + 1).Select(i => new GridPoint(from.X + (to.X - from.X) * i / steps,
            from.Y + (to.Y - from.Y) * i / steps)).ToList();
    }

    /// <summary>
    /// Whether the bowmen of enemy military buildings reaching the way from <paramref name="from"/> or the group
    /// are more than <c>military.towerFireRatio</c> times the group's strength: they cannot be struck back, a
    /// strike there costs more than it wins (a lone soldier next to a full castle).
    /// </summary>
    private bool UnderTowerFire(EnemyGroup group, GridPoint from)
    {
        double ratio = _config.Military.TowerFireRatio;
        return ratio > 0 && TowerFire(Way(from, group.Center)) > group.Strength * ratio;
    }

    /// <summary>
    /// Strength of the bowmen in enemy military buildings that reach one of <paramref name="points"/>
    /// (apart from <paramref name="except"/>), as they fight from their own land.
    /// </summary>
    internal double TowerFire(IReadOnlyCollection<GridPoint> points, Building? except = null)
    {
        int range = _world.Military.Config.TowerRange;
        return EnemyMilitaryNow().Where(b => b != except && points.Any(p => PathFinder.Distance(b.Position, p) <= range))
            .Sum(b => _world.Military.Occupants(b).Where(s => s is { SoldierType: SoldierType.Bowman }).Sum(s => Strength(s!, b.Position)));
    }

    /// <summary>
    /// Where soldiers retreat to from <paramref name="from"/>: the nearest own military building that is not
    /// nearer to the enemies at <paramref name="enemy"/> than they are (not through them), otherwise home.
    /// </summary>
    internal GridPoint RetreatDoor(GridPoint from, GridPoint enemy)
    {
        int danger = PathFinder.Distance(from, enemy);
        var pathFinder = _world.PathFinder!;
        return _world.Buildings.Where(b => b.Exists && b.Player == Player && IsMilitary(b) && b.State == BuildingState.Finished
                                           && PathFinder.Distance(b.Door, enemy) >= danger && pathFinder.AreConnected(from, b.Door))
            .MinBy(b => PathFinder.Distance(b.Door, from))?.Door ?? HomeDoor();
    }

    /// <summary>Middle of the soldiers' positions.</summary>
    internal static GridPoint CenterOf(IReadOnlyCollection<Settler> soldiers) =>
        new((int)soldiers.Average(s => s.Position.X), (int)soldiers.Average(s => s.Position.Y));

    /// <summary>
    /// Own soldiers free for orders: outside, idle or walking on a move order (to a post, home, also retreating
    /// after a strike or campaign), not part of a running strike or campaign.
    /// </summary>
    private List<Settler> IdleSoldiers() => _world.Settlers
        .Where(s => s.Player == Player && s.Profession == Profession.Soldier && !s.IsInside && !s.IsDead
                    && (!s.HasTask || _world.Military.MoveTarget(s) is not null || _world.Military.ForcedMoveTarget(s) is not null)
                    && !_strikes.Any(k => k.Soldiers.Contains(s)) && _campaign?.Soldiers.Contains(s) != true
                    && _help?.Soldiers.Contains(s) != true)
        .ToList();

    /// <summary>Where an idle soldier will stand: where it walks to on a move order, otherwise where it is.</summary>
    internal GridPoint Heading(Settler soldier) => _world.Military.MoveTarget(soldier) ?? soldier.Position;

    /// <summary>
    /// Checks the running strikes every think: the enemy group is found again (it may have moved or
    /// got reinforcements), the strikers follow it, idle soldiers nearby join when the odds got worse,
    /// and when they are still too weak the strike is called off and the strikers retreat to the nearest
    /// own military building away from the enemies.
    /// </summary>
    private void FollowStrikes(List<Building> mine)
    {
        if (_strikes.Count == 0)
            return;
        var config = _config.Military;
        var groups = FindEnemyGroups(config.GroupRadius);
        EnemyGroups = groups;
        foreach (var strike in _strikes.ToList())
        {
            strike.Soldiers.RemoveAll(s => s.IsDead || s.IsInside || s.Player != Player); // also won over by a spell
            strike.Resting.IntersectWith(strike.Soldiers);
            // The targets may have split up (one runs after a striker): all parts stay targets, the nearest is gone for.
            var tracked = groups.Where(g => g.Soldiers.Any(strike.Targets.Contains)).ToList();
            var group = strike.Soldiers.Count == 0 ? null
                : tracked.MinBy(g => PathFinder.Distance(g.Center, CenterOf(strike.Soldiers)))
                  ?? groups.Where(g => PathFinder.Distance(g.Center, strike.Center) <= config.GroupRadius * 2)
                      .MinBy(g => PathFinder.Distance(g.Center, strike.Center));
            if (group is null)
            {
                // Won, or nobody left: the survivors go home.
                if (strike.Soldiers.Count > 0)
                    _world.Military.Move(strike.Soldiers, HomeDoor());
                _strikes.Remove(strike);
                continue;
            }
            strike.Targets = tracked.Append(group).SelectMany(g => g.Soldiers).ToHashSet();

            double allied = AlliedStrength(group.Center, StrikeSupportRadius);
            double strength = allied + strike.Soldiers.Sum(s => Strength(s, group.Center));
            // What they will meet: the group and whoever waits on the way to it.
            var from = CenterOf(strike.Soldiers);
            // Lured under the towers: nothing to win there.
            if (strike.Guerilla && UnderTowerFire(group, from))
            {
                _world.Military.Move(strike.Soldiers, RetreatDoor(from, group.Center), forced: true);
                Note($"calls off the strike at {group.Center.X},{group.Center.Y}: the enemies stand under tower fire");
                LeaveAlone(group);
                _strikes.Remove(strike);
                continue;
            }
            double opposition = Opposition(group, from);
            if (strength < opposition * config.RetreatRatio)
            {
                // Reinforcements first; if there are not enough, retreat.
                var helpers = IdleSoldiers().Where(s => PathFinder.Distance(s.Position, group.Center) <= config.StrikeRange * 2)
                    .OrderBy(s => PathFinder.Distance(s.Position, group.Center)).ToList();
                var joining = new List<Settler>();
                foreach (var helper in helpers)
                {
                    if (strength >= opposition * config.StrikeRatio)
                        break;
                    joining.Add(helper);
                    strength += Strength(helper, group.Center);
                }
                if (strength >= opposition * config.RetreatRatio)
                {
                    _world.Military.Move(joining, group.Center);
                    strike.Soldiers.AddRange(joining);
                    Note($"sends {joining.Count} more against {group.Soldiers.Count} enemies at {group.Center.X},{group.Center.Y}");
                }
                else if (!TurnWithSpell(group.Center, strength, opposition * config.RetreatRatio))
                {
                    _world.Military.Move(strike.Soldiers, RetreatDoor(from, group.Center), forced: true);
                    Note($"calls off the strike at {group.Center.X},{group.Center.Y}: {group.Soldiers.Count} enemies are too strong");
                    if (strike.Guerilla)
                        LeaveAlone(group);
                    _strikes.Remove(strike);
                    continue;
                }
            }

            var center = group.Center;
            TendWounded(strike.Soldiers, strike.Resting, center, opposition, allied, back => _world.Military.Move(back, center));

            // The enemies moved: the strikers that are not fighting follow them (unless a trait leads them; not those
            // it placed on a spot to hold).
            bool led = strike.Leader?.Follow(this, strike, group) == true;
            if (!led && PathFinder.Distance(group.Center, strike.Center) > 3)
            {
                var following = strike.Fighting.Where(s => s.Activity is not (Activity.Fighting or Activity.Shooting)
                                                           && _world.Military.HoldTarget(s) is null).ToList();
                if (following.Count > 0)
                    _world.Military.Move(following, group.Center);
            }
            strike.Center = group.Center;
        }
    }

    /// <summary>Enemy soldiers outside buildings, joined into groups of neighbours within <paramref name="radius"/>.</summary>
    private List<EnemyGroup> FindEnemyGroups(int radius)
    {
        var enemies = _world.Settlers.Where(s => s.Profession is Profession.Soldier or Profession.Geologist or Profession.Scout or Profession.Pioneer && IsEnemy(s.Player)
                                                 && !s.IsInside && !s.IsDead && s.IsRevealedTo(Player)).ToList();
        var groups = new List<EnemyGroup>();
        // Neighbours are looked for in squares of the radius (the 3 × 3 around): with a big army a search
        // through all enemies for every member took long. Groups and their order as found in list order.
        int side = Math.Max(1, radius);
        var squares = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < enemies.Count; i++)
        {
            var key = (enemies[i].Position.X / side, enemies[i].Position.Y / side);
            (squares.TryGetValue(key, out var list) ? list : squares[key] = new List<int>()).Add(i);
        }
        var grouped = new bool[enemies.Count];
        var found = new List<int>();
        for (int first = 0; first < enemies.Count; first++)
        {
            if (grouped[first])
                continue;
            grouped[first] = true;
            var members = new List<Settler> { enemies[first] };
            for (int m = 0; m < members.Count; m++)
            {
                var at = members[m].Position;
                found.Clear();
                for (int sy = at.Y / side - 1; sy <= at.Y / side + 1; sy++)
                for (int sx = at.X / side - 1; sx <= at.X / side + 1; sx++)
                    if (squares.TryGetValue((sx, sy), out var square))
                        foreach (int j in square)
                            if (!grouped[j] && PathFinder.Distance(enemies[j].Position, at) <= radius)
                                found.Add(j);
                found.Sort();
                foreach (int j in found)
                {
                    grouped[j] = true;
                    members.Add(enemies[j]);
                }
            }
            var center = new GridPoint((int)members.Average(m => m.Position.X), (int)members.Average(m => m.Position.Y));
            groups.Add(new EnemyGroup(center, members, members.Sum(m => Strength(m, center))));
        }
        return groups;
    }

    /// <summary>
    /// Fighting value of a soldier (own or enemy) fighting at <paramref name="at"/>: health times damage
    /// per second times its player's combat strength there – full on its own land, reduced on neutral
    /// and foreign land. The AI knows every player's combat strength. A frozen soldier neither strikes nor
    /// gets away while it is struck: freshly frozen it counts nothing, then more as it thaws.
    /// </summary>
    internal double Strength(Settler soldier, GridPoint at) =>
        Power(soldier) * _world.Military.CombatStrengthAt(soldier.Player, at) * Thawed(soldier);

    /// <summary>0 for a soldier frozen just now, rising to 1 as the freeze wears off (1 if not frozen).</summary>
    private double Thawed(Settler soldier)
    {
        long left = soldier.FrozenUntil - _world.Tick;
        if (left <= 0)
            return 1;
        int lasts = _world.Magic.Config.Find(SpellKind.Freeze)?.Ticks ?? 0;
        return lasts <= 0 ? 0 : Math.Clamp(1 - (double)left / lasts, 0, 1);
    }

    /// <summary>
    /// Health (as much more as its armour makes it last) times damage per second, without the combat strength
    /// (0 for geologists and scouts: they do not fight).
    /// </summary>
    private double Power(Settler soldier)
    {
        var military = _world.Military.Config;
        if (soldier.SoldierType is not { } type)
            return 0;
        var stats = military.Soldiers[type];
        double perSecond = military.Damage(type, soldier.Level) * World.TicksPerSecond / stats.AttackTicks;
        return soldier.Health * stats.Toughness * perSecond;
    }
}
