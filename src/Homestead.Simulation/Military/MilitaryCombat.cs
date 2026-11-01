using Homestead.Simulation.Buildings;
using Homestead.Simulation.Magic;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Military;

/// <summary>
/// Combat: attacks on enemy military buildings, fights between soldiers, defenders, bowmen on towers
/// and the capture of buildings.
/// </summary>
/// <remarks>
/// A fighter looks for the nearest enemy soldier outside the buildings within sight (also while on a
/// move or attack order; it stops as soon as the enemy is within reach). Melee soldiers walk up to it
/// and strike from the neighboring field, bowmen shoot from up to their range but not at enemies nearer than
/// their minimum range (<see cref="SoldierStats.MinRange"/>): they step back first; one hit every attack
/// interval. A shot hurts at once, as in the original (nobody shoots at an enemy that is as good as
/// dead); its arrow only shows on screen, flying <see cref="ArrowTicks"/>. Near the enemy the way is planned locally around everyone standing (see <see cref="Approach"/>). Idle soldiers fight enemies
/// that come within sight.
/// </remarks>
/// <remarks>
/// Enemy military buildings are taken the same way on their own: an idle soldier or one on a (not forced)
/// move order attacks the nearest defended enemy military building whose door is within sight
/// (<see cref="NearestEnemyBuilding"/>) unless an enemy soldier is nearer: whichever is nearer comes first
/// (<see cref="PreferNearer"/>). A moving one carries on afterwards if it did not move in. Bowmen never
/// attack buildings, neither on their own nor by order: they could hurt neither the door nor its guard.
/// </remarks>
/// <remarks>
/// Attacked buildings: the soldiers stay inside (bowmen keep shooting from the top). Without an enemy an
/// attacker walks to its target's door: melee soldiers (pikemen too, as if their range were 1) break it
/// from the few fields next to it (<see cref="MilitaryConfig.DoorFighters"/>; the others queue behind;
/// <see cref="MilitaryConfig.DoorHealth"/>). In the broken door stands one infantry soldier at a time
/// (<see cref="DefendDoors"/>): it cannot walk, strikes the attackers on those fields and can be fought only
/// by them. When it falls the next one steps in, the bowmen last: they shoot from the door at enemies
/// within range (minimum range too), but are fought the same way. The attacker that killed the
/// last defender walks in and takes the building: it, its soldier places and its land change sides,
/// and buildings standing on land their player lost are destroyed. With no enemy near for
/// <see cref="MilitaryConfig.DoorCalmTicks"/> the guard goes back inside and the door repairs itself evenly,
/// from broken to whole in <see cref="MilitaryConfig.DoorRegenTicks"/>; already its first moment of repair
/// takes another hit to break it.
/// </remarks>
public sealed partial class Military
{
    private const int EngageInterval = 10;
    private const int ReplanTicks = 20;

    /// <summary>Door guards (by soldier id) and the building they stand in.</summary>
    private readonly Dictionary<int, Garrison> _guards = new();

    private readonly Dictionary<int, Fighter> _fighters = new();

    /// <summary>
    /// Soldier id → the tick of its next strike or shot, on the ground or from a tower. Kept by the soldier, not its order:
    /// a new order mid-fight (a move, then fighting again) must not let it strike again at once.
    /// </summary>
    private readonly Dictionary<int, long> _nextShot = new();
    private readonly Dictionary<int, (long Tick, float Strength)> _strengths = new();
    private const int UnreachableTicks = 200;
    private const int RetargetTicks = 10;

    public int Kills { get; private set; }

    private readonly List<Arrow> _arrows = new();

    /// <summary>Arrows in flight.</summary>
    public IReadOnlyList<Arrow> Arrows => _arrows;

    /// <summary>Flight time of an arrow over a distance in fields.</summary>
    public static int ArrowTicks(int distance) => 2 + distance / 2;

    public int Captures { get; private set; }

    /// <summary>
    /// Sends the player's soldiers against an enemy military building (none of an ally): idle soldiers and the soldiers
    /// of own military buildings within <see cref="MilitaryConfig.AttackRange"/> (one stays in each), no bowmen.
    /// Returns how many go.
    /// </summary>
    public int Attack(int player, Building target)
    {
        if (!target.Exists || _world.AreAllies(target.Player, player) || !_garrisons.ContainsKey(target.Id))
            return 0;
        int range = _config.AttackRange;
        var attackers = _world.Settlers
            .Where(s => s.Profession == Profession.Soldier && s.SoldierType != SoldierType.Bowman && s.Player == player
                        && !s.HasTask && !s.IsInside && PathFinder.Distance(s.Position, target.Door) <= range)
            .ToList();
        foreach (var g in _garrisons.Values.Where(g => g.Building.Player == player && g.Building.Exists
                                                       && PathFinder.Distance(g.Building.Door, target.Door) <= range))
        {
            for (int i = 0; i < g.Places.Length; i++)
            {
                if (g.Places[i] is not { } soldier || soldier.SoldierType == SoldierType.Bowman || g.Places.Count(p => p is not null) <= 1)
                    continue;
                g.Places[i] = null;
                LeaveBuilding(soldier);
                attackers.Add(soldier);
            }
        }
        foreach (var soldier in attackers)
        {
            soldier.HasTask = true;
            _fighters[soldier.Id] = new Fighter(soldier) { Target = target };
        }
        return attackers.Count;
    }

    /// <summary>
    /// Orders soldiers (of one player, outside buildings) to a point; they spread over free fields around
    /// it. Enemies in sight on the way are fought first, then they carry on.
    /// </summary>
    /// <param name="forced">Forced order (escape, retreat): walk there without fighting anybody on the way.</param>
    /// <param name="hold">Hold the spot once there („Position halten“, see <see cref="Hold"/>).</param>
    public void Move(IEnumerable<Settler> soldiers, GridPoint destination, bool forced = false, bool hold = false)
    {
        var list = Commandable(soldiers);
        // Unreachable (sea, another island): the reachable point nearest to it.
        if (list.Count > 0 && _world.CommandTarget(list[0], destination) is { } reachable)
            destination = reachable;
        var spots = FreeSpotsAround(destination, list);
        for (int i = 0; i < list.Count; i++)
        {
            var soldier = list[i];
            var spot = spots.Count > 0 ? spots[i % spots.Count] : destination;
            _fighters[soldier.Id] = new Fighter(soldier) { MoveTo = _world.CommandTarget(soldier, spot) ?? spot, Forced = forced, Hold = hold };
            soldier.HasTask = true;
        }
    }

    /// <summary>Waypoints a soldier keeps after its current order (Shift+right click); a further one replaces the last.</summary>
    public const int MaxWaypoints = 3;

    /// <summary>
    /// A waypoint (Shift+right click): soldiers with an order carry it out, then walk on to this point; up to
    /// <see cref="MaxWaypoints"/> wait in turn, a further one replaces the last. Soldiers without an order (or holding,
    /// patrolling) walk there at once, like <see cref="Move"/>.
    /// </summary>
    public void AddWaypoint(IEnumerable<Settler> soldiers, GridPoint destination, bool forced = false)
    {
        var list = soldiers.Where(s => s.Profession == Profession.Soldier && !s.IsDead && !s.IsInside && !_guards.ContainsKey(s.Id)).Distinct().ToList();
        if (list.Count > 0 && _world.CommandTarget(list[0], destination) is { } reachable)
            destination = reachable;
        var spots = FreeSpotsAround(destination, list);
        var walking = new List<Settler>();
        for (int i = 0; i < list.Count; i++)
        {
            var soldier = list[i];
            var spot = spots.Count > 0 ? spots[i % spots.Count] : destination;
            if (_fighters.TryGetValue(soldier.Id, out var f) && f is { Hold: false, PatrolBack: null })
            {
                if (f.Waypoints.Count == MaxWaypoints)
                    f.Waypoints.RemoveAt(f.Waypoints.Count - 1);
                f.Waypoints.Add(new Waypoint(_world.CommandTarget(soldier, spot) ?? spot, forced));
            }
            else
                walking.Add(soldier);
        }
        if (walking.Count > 0)
            Move(walking, destination, forced);
    }

    /// <summary>
    /// Patrol (Alt+right click): the soldiers walk to the point and back to where they stand, to and fro until another
    /// order. Enemies on the way are fought as on a move order, buildings taken up on their own too; then they walk on.
    /// </summary>
    public void Patrol(IEnumerable<Settler> soldiers, GridPoint destination)
    {
        var list = Commandable(soldiers);
        if (list.Count > 0 && _world.CommandTarget(list[0], destination) is { } reachable)
            destination = reachable;
        var spots = FreeSpotsAround(destination, list);
        for (int i = 0; i < list.Count; i++)
        {
            var soldier = list[i];
            var spot = spots.Count > 0 ? spots[i % spots.Count] : destination;
            _fighters[soldier.Id] = new Fighter(soldier) { MoveTo = _world.CommandTarget(soldier, spot) ?? spot, PatrolBack = soldier.StepTarget };
            soldier.HasTask = true;
        }
    }

    /// <summary>The waypoints the soldier walks to after its current order, in turn.</summary>
    public IReadOnlyList<GridPoint> Waypoints(Settler soldier) =>
        _fighters.TryGetValue(soldier.Id, out var f) ? f.Waypoints.Select(w => w.Spot).ToList() : [];

    /// <summary>The other end of the soldier's patrol (the one it is not walking to); null if it does not patrol.</summary>
    public GridPoint? PatrolBack(Settler soldier) => _fighters.TryGetValue(soldier.Id, out var f) ? f.PatrolBack : null;

    /// <summary>
    /// Where the soldier walks on a plain (not forced) move order while it is neither fighting nor
    /// attacking a building on the way; null otherwise. Such a soldier may take a new order any time.
    /// </summary>
    public GridPoint? MoveTarget(Settler soldier) =>
        _fighters.TryGetValue(soldier.Id, out var f) && f is { MoveTo: { } to, Forced: false, Hold: false, Target: null, Enemy: null } ? to : null;

    /// <summary>The enemy building the soldier is ordered (or set itself) to take; null otherwise.</summary>
    public Building? AttackTarget(Settler soldier) => _fighters.TryGetValue(soldier.Id, out var f) ? f.Target : null;

    /// <summary>Where the soldier walks on a forced move order (escape, retreat); null otherwise.</summary>
    public GridPoint? ForcedMoveTarget(Settler soldier) =>
        _fighters.TryGetValue(soldier.Id, out var f) && f is { MoveTo: { } to, Forced: true, Hold: false, Target: null, Enemy: null } ? to : null;

    /// <summary>
    /// „Position halten“: the soldiers stay where they stand until they get another order. They fight only enemies
    /// within their reach (melee: next to them; bowmen: within range, not nearer than the minimum) and chase nobody;
    /// they are not idle, so no building calls them in. Returns how many hold.
    /// </summary>
    public int Hold(IEnumerable<Settler> soldiers)
    {
        var list = Commandable(soldiers);
        foreach (var soldier in list)
        {
            // Mid-step: the field it is stepping onto.
            _fighters[soldier.Id] = new Fighter(soldier) { MoveTo = soldier.StepTarget, Hold = true, Holding = true };
            soldier.HasTask = true;
            if (soldier.Path.Count > 0)
                _world.WalkTo(soldier, soldier.StepTarget);
        }
        return list.Count;
    }

    /// <summary>The spot the soldier is ordered to hold (on its way there or holding it); null otherwise.</summary>
    public GridPoint? HoldTarget(Settler soldier) =>
        _fighters.TryGetValue(soldier.Id, out var f) && f is { MoveTo: { } to, Hold: true } ? to : null;

    /// <summary>Whether the soldier has reached the spot it is ordered to hold (and holds it).</summary>
    public bool Holds(Settler soldier) => _fighters.TryGetValue(soldier.Id, out var f) && f is { Hold: true, Holding: true };

    /// <summary>
    /// Orders the given soldiers to attack an enemy military building (enemies on the way first); bowmen
    /// among them take no order.
    /// </summary>
    public int Attack(IEnumerable<Settler> soldiers, Building target)
    {
        if (!target.Exists || !_garrisons.ContainsKey(target.Id))
            return 0;
        var list = Commandable(soldiers.Where(s => s.SoldierType != SoldierType.Bowman)).Where(s => !_world.AreAllies(s.Player, target.Player)).ToList();
        foreach (var soldier in list)
        {
            _fighters[soldier.Id] = new Fighter(soldier) { Target = target };
            soldier.HasTask = true;
        }
        return list.Count;
    }

    /// <summary>Soldiers outside that can take an order; ones walking into a building drop that.</summary>
    private List<Settler> Commandable(IEnumerable<Settler> soldiers)
    {
        // A door guard holds its post.
        var list = soldiers.Where(s => s.Profession == Profession.Soldier && !s.IsDead && !s.IsInside && !_guards.ContainsKey(s.Id))
            .Distinct().ToList();
        foreach (var g in _garrisons.Values)
        {
            foreach (var (place, coming) in g.Coming.ToList())
                if (list.Contains(coming.Soldier))
                    g.Coming.Remove(place);
            foreach (var (place, defender) in g.Defending.ToList())
                if (list.Contains(defender))
                    g.Defending.Remove(place);
        }
        foreach (var soldier in list)
            SetActivity(soldier, Activity.None);
        return list;
    }

    /// <summary>
    /// Walkable points nearest to <paramref name="center"/>, one per soldier; fields somebody else stands on or another
    /// soldier is sent to only when there are too few others (two sent to one field end up waiting for each other).
    /// </summary>
    private List<GridPoint> FreeSpotsAround(GridPoint center, List<Settler> soldiers)
    {
        var map = _world.Map!;
        var movers = soldiers.Select(s => s.Id).ToHashSet();
        var sent = _fighters.Values.Where(f => f.MoveTo is not null && !movers.Contains(f.Soldier.Id)).Select(f => f.MoveTo!.Value).ToHashSet();
        var spots = new List<GridPoint>();
        var taken = new List<GridPoint>();
        for (int r = 0; r <= 12 && spots.Count < soldiers.Count; r++)
            for (int dy = -r; dy <= r && spots.Count < soldiers.Count; dy++)
            for (int dx = -r; dx <= r && spots.Count < soldiers.Count; dx++)
            {
                var p = new GridPoint(center.X + dx, center.Y + dy);
                if (PathFinder.Distance(center, p) == r && map.IsWalkable(p))
                    (_world.OccupantAt(p) is { } other && !movers.Contains(other.Id) || sent.Contains(p) ? taken : spots).Add(p);
            }
        spots.AddRange(taken.Take(soldiers.Count - spots.Count));
        return spots;
    }

    private void TickCombat()
    {
        // Who can fight at all, once per tick: looking through every settler for each bowman and
        // fighter is what made long games slow.
        _combatants.Clear();
        _queueSpotsTaken.Clear();
        foreach (var settler in _world.Settlers)
            if ((settler.Profession is Profession.Soldier or Profession.Priest or Profession.Geologist or Profession.Scout or Profession.Pioneer
                     or Profession.WarMachine
                 || IsLoadedDonkey(settler)) && !settler.IsInside && !settler.IsDead)
                _combatants.Add(settler);
        SortIntoCells();
        if (_world.Tick % EngageInterval == 0)
            EngageIdleSoldiers();
        DefendDoors();
        _arrows.RemoveAll(a => a.Tick + a.FlightTicks <= _world.Tick);
        ShootFromBuildings();
        foreach (var fighter in _fighters.Values.ToList())
            Advance(fighter);
    }

    /// <summary>A donkey carrying goods: enemies strike it once to make it drop them (it cannot die).</summary>
    private static bool IsLoadedDonkey(Settler settler) => settler is { Profession: Profession.Donkey, Carrying: not null };

    /// <summary>Soldiers, priests, geologists, scouts, war machines and loaded donkeys outside the buildings at the start of this tick.</summary>
    private readonly List<Settler> _combatants = new();

    /// <summary>
    /// Enemy soldiers, priests, geologists, war machines and revealed scouts outside the buildings: who can be fought. Not door
    /// guards: only the attackers at the door fight them (see <see cref="Advance"/>). Arrows do not hurt war machines:
    /// none for <paramref name="bowman"/>.
    /// </summary>
    private IEnumerable<Settler> EnemiesOf(int player, bool bowman = false) =>
        _combatants.Where(s => IsEnemy(s, player, bowman));

    private bool IsEnemy(Settler s, int player, bool bowman) =>
        !_world.AreAllies(s.Player, player) && !s.IsInside && !s.IsDead && !_guards.ContainsKey(s.Id) && s.IsRevealedTo(player)
        && !(bowman && s.Profession == Profession.WarMachine);

    /// <summary>Fields per side of a cell of <see cref="_cells"/>.</summary>
    private const int CellSize = 8;

    /// <summary>
    /// The combatants by player and where they stood at the tick's start: indices into <see cref="_combatants"/> per cell of
    /// <see cref="CellSize"/> fields (all cells of player 0, then of player 1, …). With hundreds of soldiers on a big map, each one
    /// going through all of them every tick took most of the combat's time; by player, as an army's own crowd around a
    /// soldier was most of what it still looked through.
    /// </summary>
    [NonSerialized] private List<int>[]? _cells;
    [NonSerialized] private int _cellsPerRow;
    [NonSerialized] private int _cellPlayers;
    [NonSerialized] private List<int>? _usedCells;
    [NonSerialized] private List<int>? _nearBuffer;

    private void SortIntoCells()
    {
        int size = _world.Map!.Size;
        _cellsPerRow = (size + CellSize - 1) / CellSize;
        int players = Math.Max(_world.PlayerCount, _combatants.Count == 0 ? 1 : _combatants.Max(s => s.Player) + 1);
        if (_cells is null || _cells.Length != players * _cellsPerRow * _cellsPerRow)
        {
            _cells = new List<int>[players * _cellsPerRow * _cellsPerRow];
            _usedCells = new List<int>();
        }
        _cellPlayers = players;
        foreach (int cell in _usedCells!)
            _cells[cell].Clear();
        _usedCells.Clear();
        for (int i = 0; i < _combatants.Count; i++)
        {
            int cell = _combatants[i].Player * _cellsPerRow * _cellsPerRow + CellOf(_combatants[i].Position);
            var list = _cells[cell] ??= new List<int>();
            if (list.Count == 0)
                _usedCells.Add(cell);
            list.Add(i);
        }
    }

    private int CellOf(GridPoint p) =>
        Math.Clamp(p.Y / CellSize, 0, _cellsPerRow - 1) * _cellsPerRow + Math.Clamp(p.X / CellSize, 0, _cellsPerRow - 1);

    /// <summary>
    /// <see cref="EnemiesOf"/> within <paramref name="range"/> of the point (by <see cref="DistanceTo"/>), in the same order. Looks
    /// only at the cells around it; a field more for the wide ones and one for a step taken since the tick's start.
    /// </summary>
    private List<Settler> EnemiesNear(GridPoint from, int range, int player, bool bowman = false)
    {
        var near = _nearBuffer ??= new List<int>();
        near.Clear();
        // Hex distance d keeps both coordinates within d.
        int reach = range + 2;
        int x0 = Math.Max(0, (from.X - reach) / CellSize), x1 = Math.Min(_cellsPerRow - 1, Math.Max(0, from.X + reach) / CellSize);
        int y0 = Math.Max(0, (from.Y - reach) / CellSize), y1 = Math.Min(_cellsPerRow - 1, Math.Max(0, from.Y + reach) / CellSize);
        for (int p = 0; p < _cellPlayers; p++)
        {
            if (_world.AreAllies(p, player))
                continue;
            int first = p * _cellsPerRow * _cellsPerRow;
            for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
                if (_cells![first + cy * _cellsPerRow + cx] is { Count: > 0 } list)
                    near.AddRange(list);
        }
        near.Sort();
        var enemies = new List<Settler>();
        foreach (int i in near)
        {
            var s = _combatants[i];
            if (IsEnemy(s, player, bowman) && DistanceTo(from, s) <= range)
                enemies.Add(s);
        }
        return enemies;
    }

    private static bool IsBowman(Settler soldier) => soldier.SoldierType == SoldierType.Bowman;

    /// <summary>Steps from the point to the enemy's nearest field: beside a war machine's ring is within a sword's reach.</summary>
    private static int DistanceTo(GridPoint from, Settler enemy) =>
        Math.Max(0, PathFinder.Distance(from, enemy.Position) - (enemy.IsWide ? 1 : 0));

    private Settler? NearestEnemy(GridPoint from, int player, int range, int minRange = 0, bool bowman = false) =>
        EnemiesNear(from, range, player, bowman).Where(e => DistanceTo(from, e) >= minRange)
            .MinBy(e => DistanceTo(from, e));

    /// <summary>Ticks to the next strike; hasted by a spell, faster.</summary>
    private int HitInterval(Settler soldier, SoldierStats stats) =>
        soldier.CharmOf(SpellKind.Haste, _world.Tick) is { } haste ? Math.Max(1, (int)Math.Round(stats.AttackTicks / haste.Speed)) : stats.AttackTicks;

    /// <summary>
    /// The damage of the soldier's next strike: by type and level, its combat strength where it stands, and the spells on it –
    /// Haste, Strengthen away from its own land, Fear on the land of the player who cast it.
    /// </summary>
    public float DamageOf(Settler soldier)
    {
        double damage = _config.Damage(soldier.SoldierType!.Value, soldier.Level) * CombatStrengthOf(soldier);
        long tick = _world.Tick;
        var owner = _world.Map?.OwnerAt(soldier.Position);
        if (soldier.CharmOf(SpellKind.Haste, tick) is { } haste)
            damage *= haste.Factor;
        if (soldier.CharmOf(SpellKind.Strengthen, tick) is { } strength && owner != soldier.Player)
            damage *= strength.Factor;
        if (soldier.CharmOf(SpellKind.Fear, tick) is { } fear && owner == fear.Caster)
            damage *= fear.Factor;
        return (float)damage;
    }

    /// <summary>What the enemy takes of the damage: its armor, a shield spell, cursed arrows against bowmen.</summary>
    public float Taken(Settler enemy, Settler attacker, float damage)
    {
        long tick = _world.Tick;
        if (attacker.SoldierType == SoldierType.Bowman && enemy.CharmOf(SpellKind.CursedArrows, tick) is { } arrows)
            damage *= (float)arrows.Factor;
        if (enemy.CharmOf(SpellKind.Shield, tick) is { } shield)
            damage *= (float)shield.Factor;
        return enemy.SoldierType is { } type ? _config.Soldiers[type].Taken(damage) : damage;
    }

    /// <summary>Soldiers standing around fight enemies that come within sight, then take enemy military buildings in sight.</summary>
    private void EngageIdleSoldiers()
    {
        foreach (var soldier in _world.Settlers.Where(s => s.Profession == Profession.Soldier && !s.HasTask && !s.IsInside).ToList())
        {
            var enemy = NearestEnemy(soldier.Position, soldier.Player, _config.Sight(soldier.SoldierType!.Value), bowman: IsBowman(soldier));
            var building = NearestEnemyBuilding(soldier);
            if (building is not null && (enemy is null
                    || PathFinder.Distance(soldier.Position, building.Door) < DistanceTo(soldier.Position, enemy)))
                _fighters[soldier.Id] = new Fighter(soldier) { Target = building, AutoTarget = true };
            else if (enemy is not null)
                _fighters[soldier.Id] = new Fighter(soldier);
            else
                continue;
            soldier.HasTask = true;
        }
    }

    /// <summary>Buildings a soldier could not reach on its own: (soldier id, building id) → until when it does not try again.</summary>
    private readonly Dictionary<(int Soldier, int Building), long> _unreachableBuildings = new();

    /// <summary>
    /// The nearest defended enemy military building whose door is within the soldier's sight and that it
    /// did not just fail to reach; none for bowmen.
    /// </summary>
    private Building? NearestEnemyBuilding(Settler soldier)
    {
        if (soldier.SoldierType == SoldierType.Bowman)
            return null;
        int sight = _config.Sight(soldier.SoldierType!.Value);
        Building? nearest = null;
        int best = int.MaxValue;
        foreach (var g in _garrisons.Values)
        {
            var building = g.Building;
            if (_world.AreAllies(building.Player, soldier.Player) || !building.Exists || g.IsEmpty)
                continue;
            int distance = PathFinder.Distance(soldier.Position, building.Door);
            if (distance > sight || distance >= best)
                continue;
            if (_unreachableBuildings.TryGetValue((soldier.Id, building.Id), out long until))
            {
                if (until > _world.Tick)
                    continue;
                _unreachableBuildings.Remove((soldier.Id, building.Id));
            }
            nearest = building;
            best = distance;
        }
        return nearest;
    }

    /// <summary>
    /// Enemy soldiers and enemy buildings count alike for a soldier free to choose (no attack order, not
    /// forced): it goes for whichever is nearer, an enemy within reach always first.
    /// </summary>
    private void PreferNearer(Fighter f, SoldierStats stats)
    {
        var soldier = f.Soldier;
        int enemyDistance = f.Enemy is { } enemy ? DistanceTo(soldier.Position, enemy) : int.MaxValue;
        if (stats.InReach(enemyDistance))
            return;
        if (f.Target is null && _world.Tick % EngageInterval == 0 && NearestEnemyBuilding(soldier) is { } building)
        {
            f.Target = building;
            f.AutoTarget = true;
            f.NextReplan = -10 * ReplanTicks; // head for its door right away
        }
        if (f.Target is { } target && f.Enemy is not null && PathFinder.Distance(soldier.Position, target.Door) < enemyDistance)
        {
            f.Enemy = null;
            if (f.Chasing)
                f.NextReplan = -10 * ReplanTicks; // back to the door right away
        }
    }

    /// <summary>
    /// An attack the soldier took up on its own is over: on a move order it carries on, otherwise it is done.
    /// </summary>
    /// <param name="unreachable">The building could not be reached: leave it for a while.</param>
    private void EndAttack(Fighter f, bool unreachable = false)
    {
        if (f.AutoTarget && unreachable && f.Target is { } target)
            _unreachableBuildings[(f.Soldier.Id, target.Id)] = _world.Tick + UnreachableTicks;
        if (f.AutoTarget && f.MoveTo is not null)
        {
            f.Target = null;
            f.AutoTarget = false;
            SetActivity(f.Soldier, Activity.None);
            f.NextReplan = -10 * ReplanTicks; // walk on right away
            return;
        }
        Dismiss(f);
    }

    /// <summary>
    /// Broken doors: one defender at a time stands in front of it, infantry first, then the bowmen. Once it
    /// is calm the guard goes in and the door repairs itself.
    /// </summary>
    private void DefendDoors()
    {
        foreach (var g in _garrisons.Values)
        {
            if (!g.Building.Exists)
                continue;
            if (g.DoorHealth < _config.DoorHealth || g.Guard is not null)
            {
                if (EnemyNearDoor(g))
                    g.LastThreat = _world.Tick;
                else if (_world.Tick - g.LastThreat >= _config.DoorCalmTicks)
                    g.DoorHealth = Math.Min(_config.DoorHealth, g.DoorHealth + _config.DoorHealth / _config.DoorRegenTicks);
            }
            if (g.DoorBroken && g.Guard is null)
            {
                // Infantry first, then the bowmen come down.
                int place = Array.FindIndex(g.Places, p => p is { SoldierType: not SoldierType.Bowman });
                if (place < 0)
                    place = Array.FindIndex(g.Places, p => p is not null);
                if (place >= 0)
                    PostGuard(g, place);
            }
            if (g.Guard is { } guard)
                GuardDoor(g, guard);
        }
    }

    /// <summary>Whether an enemy stands within <see cref="MilitaryConfig.DoorCalmRadius"/> of the door fight.</summary>
    private bool EnemyNearDoor(Garrison g)
    {
        var center = FightPointOf(g);
        int radius = _config.DoorCalmRadius;
        return EnemiesNear(center, radius, g.Building.Player).Count > 0;
    }

    /// <summary>The soldier of the place comes out of the broken door to stand in front of it.</summary>
    private void PostGuard(Garrison g, int place)
    {
        var guard = g.Places[place]!;
        g.Places[place] = null;
        g.Defending[place] = guard;
        g.Guard = guard;
        _guards[guard.Id] = g;
        LeaveBuilding(guard);
        guard.HasTask = true;
        guard.Facing = Direction.SouthEast;
    }

    /// <summary>The door is whole again and its guard back at it: it goes into its place.</summary>
    private void ReturnGuard(Garrison g, Settler guard)
    {
        int place = g.Defending.First(d => d.Value == guard).Key;
        g.Defending.Remove(place);
        _guards.Remove(guard.Id);
        g.Guard = null;
        SetActivity(guard, Activity.None);
        _world.EnterBuilding(guard);
        g.Places[place] = guard;
    }

    /// <summary>
    /// The door guard steps out to the <see cref="Garrison.FightPoint"/> in front of the broken door and
    /// stays there: infantry strikes an enemy on the fighting fields next to it, a bowman shoots at the
    /// nearest enemy within range. Once the door is whole again it goes back in.
    /// </summary>
    private void GuardDoor(Garrison g, Settler guard)
    {
        if (guard.FrozenUntil > _world.Tick)
        {
            SetActivity(guard, Activity.None);
            return;
        }
        var post = g.DoorBroken ? FightPointOf(g) : g.Building.Door;
        if (guard.Position != post || guard.State == SettlerState.Stepping || guard.Destination is not null)
        {
            SetActivity(guard, Activity.None);
            if (guard.Destination != post && guard.State != SettlerState.Stepping)
                _world.WalkTo(guard, post);
            return;
        }
        if (!g.DoorBroken)
        {
            ReturnGuard(g, guard);
            return;
        }
        var stats = _config.Soldiers[guard.SoldierType!.Value];
        bool bowman = guard.SoldierType == SoldierType.Bowman;
        var spots = DoorSpotsOf(g);
        var enemy = bowman
            ? NearestEnemy(guard.Position, guard.Player, stats.Range, stats.MinRange, IsBowman(guard))
            : EnemiesOf(guard.Player, IsBowman(guard)).Where(e => spots.Contains(e.Position)).MinBy(e => e.Health);
        if (enemy is null)
        {
            SetActivity(guard, Activity.None);
            return;
        }
        guard.Facing = FacingTowards(guard.Position, enemy.Position);
        SetActivity(guard, bowman ? Activity.Shooting : Activity.Fighting);
        if (_world.Tick < g.GuardNextHit)
            return;
        int interval = HitInterval(guard, stats);
        Hit(guard, enemy);
        g.GuardNextHit = _world.Tick + interval;
        guard.ShotTicks = interval;
    }

    /// <summary>Bowmen in military buildings shoot at enemies within their tower range (<see cref="MilitaryConfig.TowerRange"/>) of the building.</summary>
    private void ShootFromBuildings()
    {
        foreach (var g in _garrisons.Values)
        {
            if (!g.Building.Exists)
                continue;
            // All bowmen of a building shoot from the same spot: one search for the target will do,
            // another only once it has died.
            Settler? enemy = null;
            bool searched = false;
            foreach (var bowman in g.Places)
            {
                if (bowman is not { SoldierType: SoldierType.Bowman } || _nextShot.GetValueOrDefault(bowman.Id) > _world.Tick
                    || bowman.FrozenUntil > _world.Tick) // frozen on top of the building
                    continue;
                var stats = _config.Soldiers[SoldierType.Bowman];
                if (!searched || enemy is { IsDead: true })
                {
                    enemy = NearestEnemy(g.Building.Position, bowman.Player, _config.TowerRange, bowman: true);
                    searched = true;
                }
                if (enemy is null)
                    break;
                bowman.Facing = FacingTowards(g.Building.Position, enemy.Position);
                SetActivity(bowman, Activity.Shooting);
                Hit(bowman, enemy, g.Building);
                bowman.ShotTicks = stats.AttackTicks;
                _nextShot[bowman.Id] = _world.Tick + stats.AttackTicks;
            }
            // Nobody left to shoot at: the bowmen lower their bows once the last shot is through.
            foreach (var bowman in g.Places)
                if (bowman is { Activity: Activity.Shooting } && _nextShot.GetValueOrDefault(bowman.Id) <= _world.Tick)
                    SetActivity(bowman, Activity.None);
        }
    }

    private void Advance(Fighter f)
    {
        var soldier = f.Soldier;
        if (soldier.IsDead)
        {
            _fighters.Remove(soldier.Id);
            return;
        }
        if (_fighters.GetValueOrDefault(soldier.Id) != f)
            return; // done earlier this tick (took a building with its strike)
        var stats = _config.Soldiers[soldier.SoldierType!.Value];
        // Frozen by a spell: stands still, strikes nobody.
        if (soldier.FrozenUntil > _world.Tick)
        {
            SetActivity(soldier, Activity.None);
            return;
        }

        if (f.Holding)
            HoldingEnemy(f, stats);
        else if (f.Forced)
            f.Enemy = null;
        else
        {
            ChooseEnemy(f, stats);
            if (f.Target is null || f.AutoTarget)
                PreferNearer(f, stats);
        }
        f.Chasing = f.Enemy is not null;

        if (f.Enemy is { } enemy)
        {
            int distance = DistanceTo(soldier.Position, enemy);
            if (stats.InReach(distance))
            {
                // Stop after the current step. Walkers are mid-step whenever this runs (movement comes
                // first in a tick), so waiting for a standstill would let them walk on.
                if (soldier.Path.Count > 0)
                    _world.WalkTo(soldier, soldier.StepTarget);
                if (soldier.State == SettlerState.Stepping)
                    return;
                soldier.Facing = FacingTowards(soldier.Position, enemy.Position);
                SetActivity(soldier, soldier.SoldierType == SoldierType.Bowman ? Activity.Shooting : Activity.Fighting);
                if (_world.Tick >= _nextShot.GetValueOrDefault(soldier.Id))
                {
                    int interval = HitInterval(soldier, stats);
                    Hit(soldier, enemy);
                    _nextShot[soldier.Id] = _world.Tick + interval;
                    soldier.ShotTicks = interval;
                }
                return;
            }
            SetActivity(soldier, Activity.None);
            if (soldier.Destination is null || _world.Tick >= f.NextReplan)
            {
                if (!Approach(f, enemy, stats))
                {
                    // No way there (walled in, other side of the water): leave it for a while.
                    f.Unreachable[enemy.Id] = _world.Tick + UnreachableTicks;
                    f.Enemy = null;
                }
                f.NextReplan = _world.Tick + ReplanTicks;
            }
            return;
        }

        // An attacker at a door keeps its activity while it fights there (resetting it every tick would
        // restart the animation); every other way through sets it to none.
        if (f.Target is null)
            SetActivity(soldier, Activity.None);
        if (f.Target is { } target)
        {
            if (!target.Exists || _world.AreAllies(target.Player, soldier.Player))
            {
                EndAttack(f);
                return;
            }
            bool standing = soldier.State != SettlerState.Stepping;
            if (!_garrisons.TryGetValue(target.Id, out var g))
            {
                EndAttack(f);
                return;
            }
            var door = target.Door;
            if (g.IsEmpty)
            {
                SetActivity(soldier, Activity.None);
                // Nobody left inside (and nobody struck a last defender down): the first at the door walks in.
                if (soldier.Position == door && standing)
                {
                    Capture(target, soldier);
                    return;
                }
            }
            else
            {
                // Defended: melee soldiers break the door from the fighting fields in front of it, then fight
                // its guard from there; the others queue behind. Nobody else stands in the door or on the
                // guard's field.
                var spots = DoorSpotsOf(g);
                var fightPoint = FightPointOf(g);
                // A walk-through (watchdog) can leave two on one field: only its owner fights from there.
                if (standing && soldier.Destination is null && spots.Contains(soldier.Position)
                    && _world.OccupantAt(soldier.Position) == soldier)
                {
                    // The next defender is coming, or the guard is not yet at its post.
                    if (g.DoorBroken && (g.Guard is not { } posted || posted.Position != fightPoint || posted.State == SettlerState.Stepping))
                    {
                        SetActivity(soldier, Activity.None);
                        return;
                    }
                    soldier.Facing = FacingTowards(soldier.Position, fightPoint);
                    SetActivity(soldier, Activity.Fighting);
                    if (_world.Tick >= _nextShot.GetValueOrDefault(soldier.Id))
                    {
                        int interval = HitInterval(soldier, stats);
                        // A guard still on its way back in after the repair began: the door is in the way again.
                        if (g.DoorBroken && g.Guard is { } guard)
                            Hit(soldier, guard);
                        else
                        {
                            _world.Cue(new SoundCue(SoundCueKind.Strike, soldier.Position, soldier.Player, soldier.SoldierType, soldier.People ?? _world.PeopleOf(soldier.Player)));
                            g.DoorHealth = Math.Max(0, g.DoorHealth - DamageOf(soldier));
                            g.LastThreat = _world.Tick;
                            _world.Events.Raise("buildingAttacked", g.Building.Player, g.Building.Position, building: g.Building);
                        }
                        _nextShot[soldier.Id] = _world.Tick + interval;
                        soldier.ShotTicks = interval;
                    }
                    return;
                }
                SetActivity(soldier, Activity.None);
                // Heading for a fighting field somebody else took meanwhile: wait elsewhere.
                bool taken = soldier.Destination is { } heading && spots.Contains(heading)
                             && _world.OccupantAt(heading) is { } holder && holder != soldier;
                if (soldier.Destination is null || taken || _world.Tick >= f.NextReplan + 10 * ReplanTicks)
                {
                    var free = spots.Where(p => _world.OccupantAt(p) is null || _world.OccupantAt(p) == soldier)
                        .Select(p => (GridPoint?)p).MinBy(p => PathFinder.Distance(soldier.Position, p!.Value));
                    var wait = free ?? QueueSpot(soldier, fightPoint, 2, spots.Append(door).ToArray());
                    if (!_world.WalkTo(soldier, wait) && !_world.WalkTo(soldier, door))
                        EndAttack(f, unreachable: true);
                    f.NextReplan = _world.Tick;
                }
                return;
            }
            if (soldier.Destination is null || _world.Tick >= f.NextReplan + 10 * ReplanTicks)
            {
                if (!_world.WalkTo(soldier, door))
                    EndAttack(f, unreachable: true);
                f.NextReplan = _world.Tick;
            }
            return;
        }
        if (f.MoveTo is { } spot)
        {
            if (soldier.Position == spot || soldier.Destination is null && PathFinder.Distance(soldier.Position, spot) <= 1 && f.NextReplan > 0)
            {
                if (f.Hold)
                {
                    // There: from now on it fights only what comes within its reach.
                    f.Holding = true;
                    f.Forced = false;
                    return;
                }
                if (f.PatrolBack is { } back)
                {
                    // Patrolling: back to the other end.
                    (f.MoveTo, f.PatrolBack) = (back, spot);
                    f.NextReplan = 0;
                    return;
                }
                Dismiss(f);
                return;
            }
            if (soldier.Destination is null || _world.Tick >= f.NextReplan + 10 * ReplanTicks)
            {
                if (!_world.WalkTo(soldier, spot))
                    Dismiss(f);
                f.NextReplan = _world.Tick;
            }
            return;
        }
        Dismiss(f);
    }

    /// <summary>
    /// The field in front of the door where its guard stands (see <see cref="OutwardNeighbors"/>; the door
    /// itself if there is none), clear of the building so it can be seen.
    /// </summary>
    private GridPoint FightPointOf(Garrison g)
    {
        if (g.FightPoint is { } known)
            return known;
        var door = g.Building.Door;
        if (SetDoorPoints(g) is { } set)
            return (g.FightPoint = door + set.Guard).Value;
        return (g.FightPoint = OutwardNeighbors(g.Building, door).DefaultIfEmpty(door).First()).Value;
    }

    /// <summary>
    /// The building's hand-set door fight fields (<see cref="MilitaryConfig.DoorPoints"/>), if any and all walkable;
    /// otherwise a warning and the automatic ones.
    /// </summary>
    private DoorPoints? SetDoorPoints(Garrison g)
    {
        var definition = g.Building.Definition;
        if (!_config.DoorPoints.TryGetValue((definition.Civilization, definition.Name), out var set))
            return null;
        var map = _world.Map!;
        var door = g.Building.Door;
        if (set.Fighters.Prepend(set.Guard).All(o => map.Contains(door + o) && map.IsWalkable(door + o)))
            return set;
        if (_warnedDoorPoints.Add((definition.Civilization, definition.Name)))
            Console.Error.WriteLine($"military.json door.points: a field of {definition.Civilization} {definition.Name} at {g.Building.Position} is not walkable (wrong offset or something in the way); using the automatic ones.");
        return null;
    }

    private readonly HashSet<(Civilization, string)> _warnedDoorPoints = new();

    /// <summary>
    /// The fields next to the <see cref="FightPointOf">fight point</see> where melee attackers fight: its walkable
    /// neighbors in front (not the door), as many as <see cref="MilitaryConfig.DoorFighters"/> allows.
    /// </summary>
    private GridPoint[] DoorSpotsOf(Garrison g)
    {
        if (g.DoorSpots is { } known)
            return known;
        var building = g.Building;
        int count = _config.DoorFighters.GetValueOrDefault(building.Definition.Name, 2);
        if (SetDoorPoints(g) is { } set)
            return g.DoorSpots = set.Fighters.Select(o => building.Door + o).Take(count).ToArray();
        return g.DoorSpots = OutwardNeighbors(building, FightPointOf(g)).Where(p => p != building.Door).Take(count).ToArray();
    }

    /// <summary>
    /// The walkable neighbors of the point, those in front of the building first (lower on screen, so
    /// drawn over it), then farthest from it.
    /// </summary>
    private IEnumerable<GridPoint> OutwardNeighbors(Building building, GridPoint point)
    {
        var map = _world.Map!;
        return Enumerable.Range(0, DirectionExtensions.Count)
            .Select(d => point.Neighbor((Direction)d))
            .Where(p => map.Contains(p) && map.IsWalkable(p))
            .OrderByDescending(p => p.Y)
            .ThenByDescending(p => PathFinder.Distance(building.Position, p));
    }

    /// <summary>
    /// Where an attacker waits its turn at a defended door: the free walkable field nearest to it, at
    /// least <paramref name="ring"/> fields from the fight point and not on the <paramref name="kept"/> fields
    /// (fighting fields, door); the fight point if there is none.
    /// </summary>
    private GridPoint QueueSpot(Settler soldier, GridPoint door, int ring, GridPoint[] kept)
    {
        var map = _world.Map!;
        for (int r = ring; r <= ring + 3; r++)
        {
            GridPoint? best = null;
            int bestDistance = int.MaxValue;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                var p = new GridPoint(door.X + dx, door.Y + dy);
                if (PathFinder.Distance(door, p) != r || kept.Contains(p) || !map.Contains(p) || !map.IsWalkable(p)
                    || _world.OccupantAt(p) is { } other && other != soldier || _queueSpotsTaken.Contains(p))
                    continue;
                int distance = PathFinder.Distance(soldier.Position, p);
                if (distance < bestDistance)
                {
                    best = p;
                    bestDistance = distance;
                }
            }
            if (best is { } spot)
            {
                _queueSpotsTaken.Add(spot);
                return spot;
            }
        }
        // No room left near the door (a whole army at it): it waits where it stands, or on the nearest free field –
        // not on the guard's field, where all the rest would pile up and nobody could fight.
        var here = soldier.StepTarget;
        if (here != door && !kept.Contains(here) && _world.OccupantAt(here) == soldier)
            return here;
        for (int r = 1; r <= 12; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var p = new GridPoint(here.X + dx, here.Y + dy);
            if (PathFinder.Distance(here, p) == r && p != door && !kept.Contains(p) && map.IsWalkable(p) && _world.OccupantAt(p) is null
                && _queueSpotsTaken.Add(p))
                return p;
        }
        return here;
    }

    /// <summary>
    /// Queue spots handed out this tick: fields are only taken once a soldier steps on, so attackers looking in the same tick
    /// would all pick the same one, walk there in a line and end up sharing it (through the watchdog), all moving as one.
    /// </summary>
    private readonly HashSet<GridPoint> _queueSpotsTaken = new();

    /// <summary>A soldier holding its position fights only an enemy within its reach: the one it fights while it stays so, otherwise the nearest.</summary>
    private void HoldingEnemy(Fighter f, SoldierStats stats)
    {
        var soldier = f.Soldier;
        if (f.Enemy is { } current && !current.IsDead && !current.IsInside && !_world.AreAllies(current.Player, soldier.Player)
            && stats.InReach(DistanceTo(soldier.Position, current)))
            return;
        f.Enemy = null;
        if (_world.Tick >= f.NextRetarget)
        {
            f.Enemy = NearestEnemy(soldier.Position, soldier.Player, stats.Range, stats.MinRange, IsBowman(soldier));
            f.NextRetarget = _world.Tick + (f.Enemy is null ? RetargetTicks / 2 : 0);
        }
    }

    /// <summary>
    /// Picks the enemy to fight. An enemy already within reach (the neighboring field for melee) is
    /// attacked at once; every <see cref="RetargetTicks"/> a clearly nearer enemy replaces the current
    /// one, so a soldier does not wait behind someone who stepped into its way. A bowman takes one within
    /// reach before a nearer one that is too close.
    /// </summary>
    private void ChooseEnemy(Fighter f, SoldierStats stats)
    {
        var soldier = f.Soldier;
        // Gone, inside, or won over by a spell: look for another one.
        if (f.Enemy is { } current && (current.IsDead || current.IsInside || _world.AreAllies(current.Player, soldier.Player)
                                       || current.Profession == Profession.Donkey && !IsLoadedDonkey(current) || DistanceTo(soldier.Position, current) > _config.Sight(soldier.SoldierType!.Value) + 4))
            f.Enemy = null;

        int currentDistance = f.Enemy is { } e ? DistanceTo(soldier.Position, e) : int.MaxValue;
        if (stats.InReach(currentDistance))
            return;
        bool recheck = f.Enemy is null || _world.Tick >= f.NextRetarget;
        var candidates = EnemiesNear(soldier.Position, recheck ? _config.Sight(soldier.SoldierType!.Value) : stats.Range, soldier.Player, IsBowman(soldier))
            .Select(x => (Enemy: x, Distance: DistanceTo(soldier.Position, x)))
            .Where(x => !(f.Unreachable.TryGetValue(x.Enemy.Id, out long until) && until > _world.Tick))
            .ToList();
        if (candidates.Count == 0)
            return;
        var inReach = candidates.Where(x => stats.InReach(x.Distance)).ToList();
        var nearest = (inReach.Count > 0 ? inReach : candidates).MinBy(x => x.Distance);
        if (stats.InReach(nearest.Distance) || nearest.Distance < currentDistance - 1)
        {
            f.Enemy = nearest.Enemy;
            f.NextReplan = 0; // walk towards the new one right away
        }
        if (recheck)
            f.NextRetarget = _world.Tick + RetargetTicks;
    }

    /// <summary>
    /// Plans the way to a field within reach of the enemy (for a bowman too close: back off). Near the enemy the search stays inside
    /// <see cref="MilitaryConfig.CombatPathRadius"/> and treats everyone standing (fighting, waiting, idle; own
    /// soldiers too) as an obstacle, so a soldier walks around a busy friend instead of queueing
    /// behind it. When the crowd blocks every way, it goes to the reachable field closest to reach of any
    /// enemy (and takes that enemy). False if the enemy cannot be reached at all.
    /// </summary>
    private bool Approach(Fighter f, Settler enemy, SoldierStats stats)
    {
        var soldier = f.Soldier;
        var start = soldier.StepTarget;
        if (DistanceTo(start, enemy) > _config.CombatPathRadius)
            return _world.WalkTo(soldier, enemy.Position); // still far: the plain way, the crowd is handled on arrival

        var map = _world.Map!;
        var enemies = EnemiesNear(start, _config.CombatPathRadius + stats.Range, soldier.Player, IsBowman(soldier));
        // The combatants are those of the tick's start: a defender sent out since is not among them yet.
        if (!enemies.Contains(enemy))
            enemies.Add(enemy);
        int Reach(GridPoint p) => enemies.Min(e => stats.OffReach(DistanceTo(p, e)));

        var cameFrom = new Dictionary<GridPoint, (GridPoint From, Direction Step)> { [start] = (start, default) };
        var queue = new Queue<GridPoint>();
        queue.Enqueue(start);
        GridPoint? goal = null;
        var closest = start;
        int closestReach = Reach(start);
        while (queue.Count > 0 && goal is null)
        {
            var current = queue.Dequeue();
            for (int d = 0; d < DirectionExtensions.Count; d++)
            {
                var next = current.Neighbor((Direction)d);
                if (cameFrom.ContainsKey(next) || !PathFinder.InCircle(start, next, _config.CombatPathRadius)
                    || !map.Contains(next) || !map.IsWalkable(next) || _world.OccupantAt(next) is { State: not SettlerState.Stepping } other && other != soldier)
                    continue;
                cameFrom[next] = (current, (Direction)d);
                if (stats.InReach(DistanceTo(next, enemy)))
                {
                    goal = next;
                    break;
                }
                // Breadth first: the first field at a distance is also the nearest to reach.
                int reach = Reach(next);
                if (reach < closestReach)
                {
                    closest = next;
                    closestReach = reach;
                }
                queue.Enqueue(next);
            }
        }

        if (goal is null)
        {
            if (closest == start)
            {
                // Hemmed in for now: wait here, the crowd moves. Give up only if there is no way at all.
                _world.WalkTo(soldier, start);
                return _world.PathFinder!.AreConnected(start, enemy.Position);
            }
            goal = closest;
            f.Enemy = enemies.MinBy(e => stats.OffReach(DistanceTo(closest, e)));
        }

        var steps = new List<Direction>();
        for (var p = goal.Value; p != start; p = cameFrom[p].From)
            steps.Add(cameFrom[p].Step);
        steps.Reverse();
        _world.Follow(soldier, steps, goal.Value);
        return true;
    }

    /// <summary>
    /// Combat strength of the player away from own land (neutral or enemy land): a start by the number of players
    /// (<see cref="CombatStrengthConfig.StartPercent"/>),
    /// raised by the goods (gold; Egyptians: gems count double) lying on the player's own land, each
    /// further percent dearer (<see cref="CombatStrengthConfig.Raise"/>), without limit (2 = twice the
    /// damage away from home, which pushes a rich player to attack), plus the flat
    /// <see cref="Hardcore.CombatStrengthBonus"/> of hardcore players. Recomputed at most once per tick.
    /// </summary>
    public float CombatStrength(int player)
    {
        if (_strengths.TryGetValue(player, out var cached) && cached.Tick == _world.Tick)
            return cached.Strength;
        var goods = _config.CombatStrength.GoodsOf(_world.PeopleOf(player));
        var map = _world.Map!;
        float units = _world.Piles
            .Where(p => p.Count > 0 && p.Material is { } m && goods.ContainsKey(m) && map.OwnerAt(p.Position) == player)
            .Sum(p => p.Count * goods[p.Material!.Value]);
        float strength = _config.CombatStrength.Raise(_config.CombatStrength.Start(_world.PlayerCount), units);
        strength += HardcoreBonus(player);
        _strengths[player] = (_world.Tick, strength);
        return strength;
    }

    /// <summary>The player's combat strength at the point: always 1 on own land (plus the hardcore bonus).</summary>
    public float CombatStrengthAt(int player, GridPoint at) =>
        _world.Map?.OwnerAt(at) == player ? 1 + HardcoreBonus(player) : CombatStrength(player);

    private float HardcoreBonus(int player) => _world.IsHardcore(player) ? Hardcore.CombatStrengthBonus : 0;

    /// <summary>The soldier's combat strength where it stands.</summary>
    public float CombatStrengthOf(Settler soldier) => CombatStrengthAt(soldier.Player, soldier.Position);

    /// <summary>A strike or shot hurts at once; a bowman's arrow (from <paramref name="tower"/>, if he is in one) is only for the eye.</summary>
    private void Hit(Settler attacker, Settler enemy, Building? tower = null)
    {
        if (attacker.SoldierType == SoldierType.Bowman)
        {
            var from = tower?.Position ?? attacker.Position;
            attacker.LastShotTick = _world.Tick;
            _arrows.Add(new Arrow(attacker, tower, enemy, from, enemy.Position, _world.Tick,
                ArrowTicks(PathFinder.Distance(from, enemy.Position))));
        }
        _world.Cue(new SoundCue(SoundCueKind.Strike, tower?.Position ?? attacker.Position, attacker.Player, attacker.SoldierType,
            attacker.People ?? _world.PeopleOf(attacker.Player)));
        if (enemy.Profession == Profession.Donkey)
        {
            // One strike and it drops its load and turns home; it takes no harm.
            _world.Trade.Struck(enemy);
            _world.Events.Raise("donkeyRobbed", enemy.Player, enemy.Position, settler: enemy);
            return;
        }
        enemy.Health -= Taken(enemy, attacker, DamageOf(attacker));
        var home = DefendedBy(enemy);
        if (home is not null)
        {
            home.LastThreat = _world.Tick;
            _world.Events.Raise("buildingAttacked", enemy.Player, home.Building.Position, building: home.Building);
        }
        else
            _world.Events.Raise("attacked", enemy.Player, enemy.Position, settler: enemy);
        if (enemy.Health <= 0)
        {
            Kill(enemy, attacker);
            if (home is { IsEmpty: true } && home.Building.Exists && !attacker.IsInside && !attacker.IsDead)
                Capture(home.Building, attacker); // the one striking the last defender down is inside at once
        }
    }

    /// <summary>The garrison whose door the soldier guards, if any.</summary>
    private Garrison? DefendedBy(Settler soldier) => _guards.GetValueOrDefault(soldier.Id);

    /// <summary>
    /// Whether the soldier belongs to a military building: in its garrison, guarding or defending its door, or on the way in
    /// (spells moving or changing units leave those alone).
    /// </summary>
    internal bool IsTowerBound(Settler soldier) =>
        soldier.Profession == Profession.Soldier && (IsGarrisoned(soldier) || _guards.ContainsKey(soldier.Id)
            || _garrisons.Values.Any(g => g.Defending.ContainsValue(soldier) || g.Coming.Values.Any(c => c.Soldier == soldier)));

    /// <summary>Whether the soldier is in the garrison of a military building (inside, or on top of it).</summary>
    internal bool IsGarrisoned(Settler soldier) => soldier.IsInside && _garrisons.Values.Any(g => g.Places.Contains(soldier));

    /// <summary>The soldier lays down its weapon and becomes a unit of another profession (Egyptian spell: bows to pioneers).</summary>
    internal void Disarm(Settler soldier, Profession profession)
    {
        Release(soldier);
        soldier.Profession = profession;
        soldier.SoldierType = null;
        soldier.Level = 1;
        soldier.Charms = null;
        soldier.Health = Economy.Geology.HealthOf(_config, profession);
    }

    private void Kill(Settler soldier, Settler attacker)
    {
        _world.History.Killed(soldier, attacker);
        Release(soldier);
        if (soldier.Profession == Profession.WarMachine)
        {
            // A machine breaks apart: dust, no ghost.
            _world.WarMachines.Forget(soldier);
            _world.AddDustCloud(soldier.Position);
            _world.Cue(new SoundCue(SoundCueKind.BuildingFalls, soldier.Position, soldier.Player));
        }
        else
        {
            _world.AddGhost(soldier);
            _world.Cue(new SoundCue(SoundCueKind.Death, soldier.Position, soldier.Player, People: soldier.People ?? _world.PeopleOf(soldier.Player)));
        }
        _world.Remove(soldier);
        Kills++;
    }

    /// <summary>
    /// The soldier drops every order and place it was on its way to (it dies, changes sides or is put
    /// elsewhere by a spell); it stands idle afterwards.
    /// </summary>
    internal void Release(Settler soldier)
    {
        _fighters.Remove(soldier.Id);
        _nextShot.Remove(soldier.Id);
        if (_guards.Remove(soldier.Id, out var guarded))
            guarded.Guard = null;
        foreach (var g in _garrisons.Values)
        {
            foreach (var (place, defender) in g.Defending.ToList())
                if (defender == soldier)
                    g.Defending.Remove(place);
            foreach (var (place, coming) in g.Coming.ToList())
                if (coming.Soldier == soldier)
                    g.Coming.Remove(place);
        }
        SetActivity(soldier, Activity.None);
        soldier.HasTask = false;
        if (!soldier.IsDead && !soldier.IsInside && soldier.Destination is not null)
            _world.WalkTo(soldier, soldier.StepTarget);
    }

    /// <summary>The attacker takes the building: it changes sides with its land.</summary>
    private void Capture(Building building, Settler attacker)
    {
        var g = _garrisons[building.Id];
        foreach (var place in g.Coming.Keys.ToList())
            Release(g, place);
        Unclaim(building);
        int loser = building.Player;
        building.Player = attacker.Player;
        _world.Events.Raise("buildingLost", loser, building.Position, building: building);
        _world.Events.Raise("buildingCaptured", attacker.Player, building.Position, building: building);
        g.DoorHealth = _config.DoorHealth; // the new owner starts behind a whole door
        _fighters.Remove(attacker.Id);
        SetActivity(attacker, Activity.None);
        int free = Enumerable.Range(0, g.Places.Length)
            .Where(i => attacker.SoldierType is { } type && building.Definition.Occupiers[i].Accepts(type))
            .DefaultIfEmpty(0).First();
        _world.EnterBuildingAt(attacker, building.Door);
        g.Places[free] = attacker;
        // The new owner starts with the attacker alone.
        Garrison.Initial(building, attacker.SoldierType).CopyTo(g.Wanted, 0);
        Claim(building);
        Captures++;
        _world.History.Captured(attacker.Player, loser);

        // Buildings on land their player no longer owns go down.
        var map = _world.Map!;
        foreach (var other in _world.Buildings.Where(b => b.Exists && b != building).ToList())
            if (map.OwnerAt(other.Position) is { } owner && owner != other.Player)
                _world.RemoveBuilding(other);
    }

    /// <summary>The order is done: on to the next waypoint, if any; otherwise the soldier stands idle.</summary>
    private void Dismiss(Fighter f)
    {
        if (f.Waypoints.Count > 0)
        {
            var next = f.Waypoints[0];
            var walking = new Fighter(f.Soldier) { MoveTo = next.Spot, Forced = next.Forced };
            walking.Waypoints.AddRange(f.Waypoints.Skip(1));
            _fighters[f.Soldier.Id] = walking;
            SetActivity(f.Soldier, Activity.None);
            return;
        }
        _fighters.Remove(f.Soldier.Id);
        SetActivity(f.Soldier, Activity.None);
        f.Soldier.HasTask = false;
    }

    /// <summary>A soldier steps out of its building; a bowman stops shooting from the top.</summary>
    private void LeaveBuilding(Settler soldier)
    {
        _world.LeaveBuilding(soldier);
        SetActivity(soldier, Activity.None);
    }

    private static void SetActivity(Settler soldier, Activity activity)
    {
        if (soldier.Activity == activity)
            return;
        soldier.Activity = activity;
        soldier.ActivityTicks = 0;
        soldier.IsWorking = activity != Activity.None;
    }

    /// <summary>The direction of the neighbor nearest to <paramref name="to"/>: where one at <paramref name="from"/> looks to face it.</summary>
    public static Direction FacingTowards(GridPoint from, GridPoint to)
    {
        var best = Direction.SouthWest;
        int bestDistance = int.MaxValue;
        for (int d = 0; d < DirectionExtensions.Count; d++)
        {
            int distance = PathFinder.Distance(from.Neighbor((Direction)d), to);
            if (distance < bestDistance)
            {
                best = (Direction)d;
                bestDistance = distance;
            }
        }
        return best;
    }

    private sealed class Fighter(Settler soldier)
    {
        public Settler Soldier { get; } = soldier;

        /// <summary>Attack order: the enemy building to take.</summary>
        public Building? Target { get; set; }

        public Settler? Enemy { get; set; }
        public Dictionary<int, long> Unreachable { get; } = new();
        public long NextReplan { get; set; }
        public long NextRetarget { get; set; }

        /// <summary>Move order: where to go when no enemy is around.</summary>
        public GridPoint? MoveTo { get; set; }

        /// <summary>Forced move order: no fighting on the way.</summary>
        public bool Forced { get; set; }

        /// <summary>Hold the <see cref="MoveTo"/> spot once there instead of being done.</summary>
        public bool Hold { get; init; }

        /// <summary>It has reached the spot it holds.</summary>
        public bool Holding { get; set; }

        /// <summary>The <see cref="Target"/> was taken up on its own (seen, not ordered).</summary>
        public bool AutoTarget { get; set; }

        /// <summary>It went for an enemy soldier last tick.</summary>
        public bool Chasing { get; set; }

        /// <summary>Where to walk after this order, in turn (Shift+right click).</summary>
        public List<Waypoint> Waypoints { get; } = new();

        /// <summary>Patrol: the other end, walked to once <see cref="MoveTo"/> is reached (and so on).</summary>
        public GridPoint? PatrolBack { get; set; }
    }

    /// <summary>A point to walk to after the current order, forced (no fighting on the way) or not.</summary>
    private readonly record struct Waypoint(GridPoint Spot, bool Forced);
}

/// <summary>
/// An arrow in flight (for display; its damage was done when it was shot) from a bowman (on <paramref name="Tower"/>, if he shoots from one) at
/// <paramref name="Target"/>, launched at <paramref name="Tick"/> from <paramref name="From"/> towards
/// where the target stood then (<paramref name="To"/>).
/// </summary>
public sealed record Arrow(Settler Shooter, Building? Tower, Settler Target, GridPoint From, GridPoint To, long Tick,
    int FlightTicks);

