using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Military;

/// <summary>
/// Soldiers and territory: barracks turn carriers into soldiers (one weapon each), military buildings
/// call idle soldiers of their player within <see cref="MilitaryConfig.GarrisonCallRadius"/> into their soldier places, and an occupied military building
/// claims the land around it.
/// </summary>
/// <remarks>
/// Garrison size: the player sets how many soldiers of each type a building wants (<see cref="ChangeWanted"/>);
/// a new building wants one swordsman. It never wants none and never more than its places of that kind
/// (infantry places for swordsmen and pikemen, bowman places for bowmen). Soldiers no longer wanted turn
/// back or come out, but never the last one inside.
/// </remarks>
/// <remarks>
/// Territory: a building claims every point of its circle (<see cref="MilitaryConfig.Territory"/>) that
/// belongs to nobody or to a player whose own buildings do not cover it; other players' buildings on the
/// land taken go down. A torn-down building's land stays with its player, unprotected. A captured
/// building's land goes to another building of the old owner covering it, else to another player's
/// covering building, then the captor claims its circle. Land of a loaded map keeps the owners stored in the map.
/// Watchdog: a soldier or trainee that makes no progress towards the door for <see cref="StallTicks"/>
/// is released and the place called again.
/// </remarks>
public sealed partial class Military
{
    public const int StallTicks = 400;
    private const int DispatchInterval = 10;

    private readonly World _world;
    private readonly MilitaryConfig _config;
    private readonly Dictionary<int, Garrison> _garrisons = new();
    private readonly Dictionary<int, Barrack> _barracks = new();

    internal Military(World world, MilitaryConfig config)
    {
        _world = world;
        _config = config;
    }

    public MilitaryConfig Config => _config;

    public int SoldiersTrained { get; private set; }

    /// <summary>Soldiers in the building's places (null for a free place); empty for other buildings.</summary>
    public IReadOnlyList<Settler?> Occupants(Building building) =>
        _garrisons.TryGetValue(building.Id, out var g) ? g.Places : [];

    /// <summary>What is left of the military building's door (<see cref="MilitaryConfig.DoorHealth"/> when whole; 0 broken).</summary>
    public float DoorHealth(Building building) => _garrisons.TryGetValue(building.Id, out var g) ? g.DoorHealth : _config.DoorHealth;

    /// <summary>Where the guard of the building's broken door stands, in front of the door.</summary>
    public GridPoint FightPoint(Building building) => GarrisonOf(building) is { } g ? FightPointOf(g) : building.Door;

    /// <summary>The fields next to <see cref="FightPoint"/> where melee attackers fight the door and its guard.</summary>
    public IReadOnlyList<GridPoint> DoorSpots(Building building) => GarrisonOf(building) is { } g ? DoorSpotsOf(g) : [];

    /// <summary>The soldier standing in the building's broken door, if any.</summary>
    public Settler? DoorGuard(Building building) => _garrisons.TryGetValue(building.Id, out var g) ? g.Guard : null;

    public bool IsOccupied(Building building) => _garrisons.TryGetValue(building.Id, out var g) && g.Places.Any(p => p is not null);

    /// <summary>Whether a finished military building stands empty and no soldier is on the way to it.</summary>
    public bool AwaitsSoldiers(Building building) =>
        _garrisons.TryGetValue(building.Id, out var g) && g.Places.All(p => p is null) && g.Coming.Count == 0;

    /// <summary>How many soldiers of <paramref name="type"/> the military building wants (0 for other buildings).</summary>
    public int Wanted(Building building, SoldierType type) => GarrisonOf(building)?.Wanted[(int)type] ?? 0;

    /// <summary>
    /// Whether the wanted number of <paramref name="type"/> can change by <paramref name="delta"/>: never
    /// empty, never more than the places (see <see cref="ChangeWanted"/>).
    /// </summary>
    public bool CanChangeWanted(Building building, SoldierType type, int delta) => Changed(building, type, delta) is not null;

    /// <summary>
    /// Changes the wanted number of <paramref name="type"/> (a player's order). With all infantry places
    /// wanted, one more swordsman replaces a pikeman and the other way round. Returns false if not allowed.
    /// </summary>
    public bool ChangeWanted(Building building, SoldierType type, int delta)
    {
        if (Changed(building, type, delta) is not { } wanted)
            return false;
        wanted.CopyTo(GarrisonOf(building)!.Wanted, 0);
        return true;
    }

    private int[]? Changed(Building building, SoldierType type, int delta)
    {
        if (GarrisonOf(building) is not { } g)
            return null;
        var wanted = (int[])g.Wanted.Clone();
        wanted[(int)type] += delta;
        var occupiers = building.Definition.Occupiers;
        int infantryPlaces = occupiers.Count(o => !o.ForBowman);
        if (type != SoldierType.Bowman && delta > 0)
        {
            // Full: the other infantry type makes room.
            int other = (int)(type == SoldierType.Swordsman ? SoldierType.Pikeman : SoldierType.Swordsman);
            int over = wanted[(int)SoldierType.Swordsman] + wanted[(int)SoldierType.Pikeman] - infantryPlaces;
            if (over > 0)
                wanted[other] -= Math.Min(over, wanted[other]);
        }
        if (wanted[(int)type] < 0 || wanted.Sum() < 1)
            return null;
        int infantry = wanted[(int)SoldierType.Swordsman] + wanted[(int)SoldierType.Pikeman];
        return infantry <= infantryPlaces && wanted[(int)SoldierType.Bowman] <= occupiers.Count(o => o.ForBowman) ? wanted : null;
    }

    /// <summary>The garrison of a finished military building, created on first use.</summary>
    private Garrison? GarrisonOf(Building building)
    {
        if (_garrisons.TryGetValue(building.Id, out var g))
            return g;
        if (!building.Exists || building.State != BuildingState.Finished || building.Definition.Occupiers.Count == 0)
            return null;
        return _garrisons[building.Id] = new Garrison(building) { DoorHealth = _config.DoorHealth };
    }

    /// <summary>A short description for the player (null for non-military buildings).</summary>
    public string? Status(Building building)
    {
        if (_barracks.TryGetValue(building.Id, out var barrack))
            return barrack.Recruits.Count == 0 ? "barracks: waiting for weapons and carriers"
                : "a recruit is on the way";
        if (!_garrisons.TryGetValue(building.Id, out var g))
            return null;
        int inside = g.Places.Count(p => p is not null);
        return $"{inside}/{g.Places.Length} soldiers" + (g.Coming.Count > 0 ? $", {g.Coming.Count} on the way" : "")
               + (inside == 0 ? " (claims no land yet)" : "")
               + (g.DoorHealth < _config.DoorHealth ? $", door {(int)Math.Ceiling(100 * g.DoorHealth / _config.DoorHealth)}%" : "");
    }

    internal void Tick()
    {
        if (_world.Tick % DispatchInterval == 0)
        {
            Discover();
            foreach (var barrack in _barracks.Values.ToList())
                CallTrainees(barrack);
            // One soldier per building and round, emptiest first: every building gets its first
            // soldier (and claims its land) before any building fills up.
            foreach (var garrison in _garrisons.Values.ToList())
                ReleaseUnwanted(garrison);
            // The soldiers outside, by player (in settler order), looked through for every place to fill.
            var outside = _world.Settlers.Where(s => s.Profession == Profession.Soldier && !s.IsInside)
                .GroupBy(s => s.Player).ToDictionary(group => group.Key, group => group.ToList());
            foreach (var garrison in _garrisons.Values.OrderBy(g => g.Places.Count(p => p is not null) + g.Coming.Count).ToList())
                CallSoldier(garrison, outside.GetValueOrDefault(garrison.Building.Player) ?? []);
        }
        foreach (var barrack in _barracks.Values.ToList())
            AdvanceBarrack(barrack);
        foreach (var garrison in _garrisons.Values.ToList())
            AdvanceGarrison(garrison);
        RevealScouts();
        TickCombat();
    }

    private void Discover()
    {
        foreach (var building in _world.Buildings)
        {
            if (!building.Exists || building.State != BuildingState.Finished)
                continue;
            if (building.Definition.Occupiers.Count > 0)
                GarrisonOf(building);
            else if (building.Definition.Name == "barrack" && !_barracks.ContainsKey(building.Id))
                _barracks[building.Id] = new Barrack(building);
        }
    }

    // ---- barracks ----

    /// <summary>
    /// As many carriers as there are weapons to take up (manual recruitment: of the types planned) walk to the
    /// barracks at once, as far as the carrier reserve allows; each becomes a soldier the moment it gets there.
    /// </summary>
    private void CallTrainees(Barrack b)
    {
        if (!b.Building.Exists)
            return;
        int player = b.Building.Player;
        // Recruits on their way are carriers yet: they count against the reserve, at every barracks of the player.
        int walking = _barracks.Values.Where(o => o.Building.Player == player).Sum(o => o.Recruits.Count);
        int wanted = Math.Min(Recruitable(b.Building) - b.Recruits.Count, _world.Recruitment.Convertible(player) - walking);
        if (wanted <= 0)
            return;
        var door = b.Building.Door;
        var trainees = _world.Settlers
            .Where(s => s.IsAvailableCarrier && s.Player == player && _world.Reachable(s, door))
            .OrderBy(s => PathFinder.Distance(s.Position, door)).Take(wanted).ToList();
        foreach (var trainee in trainees)
        {
            var recruit = new Recruit(trainee, new WalkWatch(_world.Tick));
            b.Recruits.Add(recruit);
            trainee.HasTask = true;
            if (!_world.WalkTo(trainee, door))
                Release(b, recruit);
        }
    }

    /// <summary>Soldiers the barracks may train from the weapons it has now (manual recruitment: no more of a type than planned).</summary>
    private int Recruitable(Building barrack) =>
        RecruitablePiles(barrack).GroupBy(p => _config.Weapons[p.Material!.Value])
            .Sum(g => IsManualRecruitment(barrack.Player) ? Math.Min(g.Sum(p => p.Count), Planned(barrack.Player, g.Key)) : g.Sum(p => p.Count));

    private bool HasWeapon(Building barrack) => RecruitablePiles(barrack).Any();

    /// <summary>The barracks' weapon piles it may train from now: any (automatic) or of the types planned (manual).</summary>
    private IEnumerable<Pile> RecruitablePiles(Building barrack) =>
        barrack.Piles.Where(p => p.Role == PileRole.Request && p.Count > 0 && p.Material is { } m && _config.Weapons.TryGetValue(m, out var type)
                                 && (!IsManualRecruitment(barrack.Player) || Planned(barrack.Player, type) > 0));

    // ---- recruitment: automatic (every weapon becomes a soldier) or manual (only as many of a type as planned) ----

    private readonly HashSet<int> _manualRecruitment = new();
    private readonly Dictionary<(int Player, SoldierType Type), int> _planned = new();

    /// <summary>Whether the player's barracks only train the soldiers planned (<see cref="Planned"/>).</summary>
    public bool IsManualRecruitment(int player) => _manualRecruitment.Contains(player);

    public void SetManualRecruitment(int player, bool manual)
    {
        if (manual)
            _manualRecruitment.Add(player);
        else
            _manualRecruitment.Remove(player);
    }

    /// <summary>Soldiers of the type the player wants trained as soon as possible (manual recruitment).</summary>
    public int Planned(int player, SoldierType type) => _planned.GetValueOrDefault((player, type));

    /// <summary>Plans more (or, negative, fewer) soldiers of the type; never below 0.</summary>
    public void ChangePlanned(int player, SoldierType type, int delta) =>
        _planned[(player, type)] = Math.Max(0, Planned(player, type) + delta);

    /// <summary>
    /// The player's weapons of the type for new soldiers: in its piles (storehouses, barracks, workshops) and
    /// loose on its land (without territory rules: anywhere, carriers fetch them from everywhere then).
    /// </summary>
    public int WeaponsInStock(int player, SoldierType type)
    {
        var weapon = _config.WeaponOf(type);
        var map = _world.Map!;
        return _world.Piles.Where(p => p.Material == weapon && p.Count > 0
                                       && (p.Owner is { } owner ? owner.Player == player : !_world.TerritoryRules || map.OwnerAt(p.Position) == player))
            .Sum(p => p.Count);
    }

    private void AdvanceBarrack(Barrack b)
    {
        if (!b.Building.Exists)
        {
            foreach (var recruit in b.Recruits.ToList())
                Release(b, recruit);
            _barracks.Remove(b.Building.Id);
            return;
        }
        foreach (var recruit in b.Recruits.ToList())
            AdvanceRecruit(b, recruit);
    }

    private void AdvanceRecruit(Barrack b, Recruit recruit)
    {
        var trainee = recruit.Settler;
        // At the door or next to it: several recruits arrive at once, only one fits on the door.
        var door = b.Building.Door;
        bool there = trainee.State != SettlerState.Stepping && PathFinder.Distance(trainee.Position, door) <= 1;
        if (!there && !Arrived(trainee, door, recruit.Walk, out bool stalled))
        {
            if (stalled)
                Release(b, recruit);
            return;
        }
        trainee.Path.Clear();
        trainee.Destination = null;
        // The weapon with the most units in stock (manual recruitment: of a type planned).
        var pile = RecruitablePiles(b.Building).MaxBy(p => p.Count);
        if (pile is null)
        {
            Release(b, recruit);
            return;
        }
        var type = _config.Weapons[pile.Material!.Value];
        pile.Count--;
        pile.ResetMaterialIfEmpty();
        if (IsManualRecruitment(b.Building.Player))
            ChangePlanned(b.Building.Player, type, -1);
        // A soldier the moment the carrier gets to the door, without going in.
        Train(b, recruit, type);
    }

    private void Train(Barrack b, Recruit recruit, SoldierType type)
    {
        var trainee = recruit.Settler;
        trainee.IsWorking = false;
        trainee.Activity = Activity.None;
        trainee.Profession = Profession.Soldier;
        trainee.SoldierType = type;
        // The level the player's upgrades (small temple) give newly trained soldiers of the type.
        trainee.Level = _world.Magic.SoldierLevel(b.Building.Player, type);
        trainee.Health = _world.MaxHealth(trainee.Player, type, trainee.Level);
        trainee.HasTask = false;
        SoldiersTrained++;
        b.Recruits.Remove(recruit);
        // The barracks' work area serves as the rally point of its new soldiers.
        Move([trainee], b.Building.WorkCenter);
    }

    private void Release(Barrack b, Recruit recruit)
    {
        var trainee = recruit.Settler;
        if (trainee.IsInside)
            _world.LeaveBuilding(trainee);
        trainee.IsWorking = false;
        trainee.Activity = Activity.None;
        trainee.HasTask = false;
        b.Recruits.Remove(recruit);
    }

    // ---- military buildings ----

    private void CallSoldier(Garrison g, List<Settler> outside)
    {
        if (!g.Building.Exists)
            return;
        var door = g.Building.Door;
        var pathFinder = _world.PathFinder!;
        int reach = _config.GarrisonCallReach;
        for (int i = 0; i < g.Places.Length; i++)
        {
            // A place is free, or its soldier is no longer wanted but must stay until relieved (the last one inside).
            if (g.Places[i] is { SoldierType: { } inside } && Assigned(g, inside) <= g.Wanted[(int)inside]
                || g.Coming.ContainsKey(i) || g.Defending.ContainsKey(i))
                continue;
            var place = g.Building.Definition.Occupiers[i];
            var missing = Enum.GetValues<SoldierType>().Where(t => place.Accepts(t) && g.Wanted[(int)t] > Assigned(g, t)).ToList();
            if (missing.Count == 0)
                continue;
            var soldier = outside
                .Where(s => s.Profession == Profession.Soldier && !s.HasTask && !s.IsInside && s.Player == g.Building.Player
                            && s.SoldierType is { } type && missing.Contains(type) && PathFinder.Distance(s.Position, door) <= reach
                            && pathFinder.AreConnected(s.Position, door))
                .MinBy(s => PathFinder.Distance(s.Position, door));
            if (soldier is null)
                continue;
            soldier.HasTask = true;
            g.Coming[i] = (soldier, new WalkWatch(_world.Tick));
            if (!_world.WalkTo(soldier, door))
                Release(g, i);
            return;
        }
    }

    /// <summary>
    /// Puts the soldier straight into a free place of the finished military building (map loading); false if
    /// no place takes it. The building then wants the soldiers it holds. Claims no land: a loaded map keeps its owners.
    /// </summary>
    internal bool Station(Building building, Settler soldier)
    {
        if (GarrisonOf(building) is not { } g || soldier.SoldierType is not { } type)
            return false;
        int place = Enumerable.Range(0, g.Places.Length)
            .FirstOrDefault(i => g.Places[i] is null && building.Definition.Occupiers[i].Accepts(type), -1);
        if (place < 0)
            return false;
        if (g.Places.All(p => p is null))
            Array.Clear(g.Wanted);
        g.Wanted[(int)type]++;
        g.Places[place] = soldier;
        soldier.HasTask = true;
        _world.EnterBuildingAt(soldier, building.Door);
        return true;
    }

    /// <summary>Soldiers of a type inside the building, on the way in or out defending it.</summary>
    public int Assigned(Building building, SoldierType type) => GarrisonOf(building) is { } g ? Assigned(g, type) : 0;

    private static int Assigned(Garrison g, SoldierType type) =>
        g.Places.Count(s => s?.SoldierType == type) + g.Coming.Values.Count(c => c.Soldier.SoldierType == type)
        + g.Defending.Values.Count(s => s.SoldierType == type);

    /// <summary>
    /// More soldiers of a type than wanted: those on the way turn back first, then soldiers inside come
    /// out – never the last one, so the building is not left empty until the others have arrived.
    /// </summary>
    private void ReleaseUnwanted(Garrison g)
    {
        if (!g.Building.Exists)
            return;
        foreach (var type in Enum.GetValues<SoldierType>())
        {
            int excess = g.Places.Count(s => s?.SoldierType == type) + g.Coming.Values.Count(c => c.Soldier.SoldierType == type)
                         - g.Wanted[(int)type];
            foreach (var (place, (soldier, _)) in g.Coming.Where(c => c.Value.Soldier.SoldierType == type).ToList())
            {
                if (excess <= 0)
                    break;
                Release(g, place);
                if (!soldier.IsInside && soldier.Destination is not null)
                    _world.WalkTo(soldier, soldier.StepTarget);
                excess--;
            }
            for (int i = 0; i < g.Places.Length && excess > 0; i++)
            {
                if (g.Places[i] is not { } soldier || soldier.SoldierType != type || g.Places.Count(p => p is not null) <= 1)
                    continue;
                g.Places[i] = null;
                LeaveBuilding(soldier);
                soldier.HasTask = false;
                excess--;
            }
        }
    }

    private void AdvanceGarrison(Garrison g)
    {
        if (!g.Building.Exists)
        {
            Abandon(g);
            return;
        }
        foreach (var (place, (soldier, watch)) in g.Coming.ToList())
        {
            if (soldier.IsDead)
            {
                g.Coming.Remove(place);
                continue;
            }
            if (!Arrived(soldier, g.Building.Door, watch, out bool stalled))
            {
                if (stalled)
                    Release(g, place);
                continue;
            }
            g.Coming.Remove(place);
            // Relieved: the soldier no longer wanted comes out as the new one goes in.
            if (g.Places[place] is { } relieved)
            {
                _world.EnterBuilding(soldier);
                g.Places[place] = soldier;
                LeaveBuilding(relieved);
                relieved.HasTask = false;
                continue;
            }
            _world.EnterBuilding(soldier);
            bool first = !g.Places.Any(p => p is not null);
            g.Places[place] = soldier;
            if (first)
                Claim(g.Building);
        }
    }

    private void Release(Garrison g, int place)
    {
        if (g.Coming.Remove(place, out var coming))
            coming.Soldier.HasTask = false;
    }

    /// <summary>
    /// The building is gone (torn down): its soldiers come out. Its land stays with the player, but
    /// unprotected: the next enemy building claiming it takes it (see <see cref="Claim"/>).
    /// </summary>
    private void Abandon(Garrison g)
    {
        foreach (var place in g.Coming.Keys.ToList())
            Release(g, place);
        if (g.Guard is { } guard)
        {
            _guards.Remove(guard.Id);
            SetActivity(guard, Activity.None);
            guard.HasTask = false;
            g.Guard = null;
        }
        g.Defending.Clear();
        for (int i = 0; i < g.Places.Length; i++)
        {
            if (g.Places[i] is not { } soldier)
                continue;
            LeaveBuilding(soldier);
            soldier.HasTask = false;
            g.Places[i] = null;
        }
        _garrisons.Remove(g.Building.Id);
    }

    // ---- territory ----

    private double TerritoryRadius(Building building) => _config.Territory.GetValueOrDefault(building.Definition.Name);

    private static bool InCircle(GridPoint center, GridPoint p, double radius)
    {
        int dx = p.X - center.X, dy = p.Y - center.Y;
        return dx * dx + dy * dy - dx * dy <= radius * radius;
    }

    private IEnumerable<GridPoint> TerritoryOf(Building building)
    {
        double radius = TerritoryRadius(building);
        var map = _world.Map!;
        return PathFinder.Circle(building.Position, (int)Math.Ceiling(radius))
            .Where(p => map.Contains(p) && InCircle(building.Position, p, radius));
    }

    /// <summary>
    /// Whether a military building of the player (other than <paramref name="except"/>) covers the point: an occupied one
    /// its circle, a finished one always its own ground – military buildings go down only when their player tears them
    /// down or a war machine destroys them, never with land taken around them (even while empty, e.g. the last soldier
    /// standing guard outside a broken door).
    /// </summary>
    private bool Covers(int player, GridPoint p, Building? except = null) => _garrisons.Values.Any(g =>
        g.Building != except && g.Building.Exists && g.Building.Player == player
        && (g.Places.Any(s => s is not null) && InCircle(g.Building.Position, p, TerritoryRadius(g.Building)) || StandsOn(g.Building, p)));

    private static bool StandsOn(Building building, GridPoint p) =>
        p == building.Door || building.Definition.Blocked.Contains(new GridPoint(p.X - building.Position.X, p.Y - building.Position.Y));

    /// <summary>
    /// The building takes the land in its circle that is free or not protected by its owner's occupied
    /// buildings (never an ally's), and always the ground it stands on (a taken tower would otherwise stand on enemy land and
    /// go down with the next change of land); other players' buildings on the land taken go down.
    /// </summary>
    private void Claim(Building building)
    {
        var map = _world.Map!;
        var taken = new HashSet<GridPoint>();
        var ground = building.BlockedPoints.Append(building.Door).Where(map.Contains).ToHashSet();
        foreach (var p in TerritoryOf(building).Union(ground))
            if (map.OwnerAt(p) is not { } owner || !_world.AreAllies(owner, building.Player) && (ground.Contains(p) || !Covers(owner, p)))
            {
                if (map.OwnerAt(p) is not null)
                    taken.Add(p);
                map.SetOwner(p, building.Player);
            }
        if (taken.Count == 0)
            return;
        foreach (var other in _world.Buildings.Where(b => b.Exists && b.Player != building.Player && taken.Contains(b.Position)).ToList())
            _world.RemoveBuilding(other);
    }

    /// <summary>Whether the point is land an occupied military building of its owner covers (pioneers cannot take it).</summary>
    internal bool IsProtected(GridPoint p) => _world.Map!.OwnerAt(p) is { } owner && Covers(owner, p);

    /// <summary>
    /// A pioneer takes the point for the player (free or unprotected land of a non-ally, see <see cref="IsProtected"/>);
    /// another player's building standing there – or reaching onto it, unless its own ground is protected –
    /// goes down with its ground, as when a military building claims it.
    /// </summary>
    internal void TakeLand(GridPoint p, int player)
    {
        var map = _world.Map!;
        if (map.OwnerAt(p) is { } owner && _world.AreAllies(owner, player))
            return;
        bool foreign = map.OwnerAt(p) is not null;
        map.SetOwner(p, player);
        if (!foreign)
            return;
        if (_world.BuildingAt(p) is { } reaching && reaching.Player != player && reaching.Position != p && !IsProtected(reaching.Position))
            TakeLand(reaching.Position, player);
        foreach (var other in _world.Buildings.Where(b => b.Exists && b.Player != player && b.Position == p).ToList())
            _world.RemoveBuilding(other);
    }

    private void Unclaim(Building building)
    {
        var map = _world.Map!;
        foreach (var p in TerritoryOf(building))
        {
            if (map.OwnerAt(p) != building.Player || Covers(building.Player, p, building))
                continue;
            var other = _garrisons.Values.FirstOrDefault(g => g.Building != building && g.Building.Exists && g.Places.Any(s => s is not null)
                                                              && InCircle(g.Building.Position, p, TerritoryRadius(g.Building)));
            map.SetOwner(p, other?.Building.Player);
        }
    }

    // ---- walking ----

    /// <summary>True once the settler stands at the door; <paramref name="stalled"/> is the watchdog.</summary>
    private bool Arrived(Settler settler, GridPoint door, WalkWatch watch, out bool stalled)
    {
        stalled = false;
        if (settler.State != SettlerState.Stepping && settler.Destination is null)
        {
            if (settler.Position == door)
                return true;
            if (!_world.WalkTo(settler, door))
            {
                stalled = true;
                return false;
            }
        }
        int remaining = settler.RemainingPath.Count();
        if (remaining < watch.BestRemaining)
        {
            watch.BestRemaining = remaining;
            watch.LastProgress = _world.Tick;
        }
        stalled = _world.Tick - watch.LastProgress > StallTicks;
        return false;
    }

    private sealed class WalkWatch(long now)
    {
        public int BestRemaining { get; set; } = int.MaxValue;
        public long LastProgress { get; set; } = now;
    }

    private sealed class Garrison(Building building)
    {
        public Building Building { get; } = building;
        public Settler?[] Places { get; } = new Settler?[building.Definition.Occupiers.Count];

        /// <summary>Soldiers wanted per <see cref="SoldierType"/>: at first one swordsman (a bowman where only bowmen fit).</summary>
        public int[] Wanted { get; } = Initial(building);

        public static int[] Initial(Building building, SoldierType? type = null)
        {
            var wanted = new int[Enum.GetValues<SoldierType>().Length];
            wanted[(int)(type ?? (building.Definition.Occupiers.Any(o => !o.ForBowman) ? SoldierType.Swordsman : SoldierType.Bowman))] = 1;
            return wanted;
        }
        public Dictionary<int, (Settler Soldier, WalkWatch Watch)> Coming { get; } = new();

        /// <summary>Soldiers out of their place to fight attackers (by place): the door guard and bowmen come down.</summary>
        public Dictionary<int, Settler> Defending { get; } = new();

        /// <summary>What is left of the door; at 0 it is broken and the infantry defends it one by one.</summary>
        public float DoorHealth { get; set; }

        public bool DoorBroken => DoorHealth <= 0;

        /// <summary>The last tick an enemy was near the door fight or hit the door or its guard (see <see cref="MilitaryConfig.DoorCalmTicks"/>).</summary>
        public long LastThreat { get; set; }

        /// <summary>The infantry soldier standing in the broken door (also in <see cref="Defending"/>).</summary>
        public Settler? Guard { get; set; }

        public long GuardNextHit { get; set; }

        /// <summary>Where the guard of a broken door stands: the field in front of the door; found on first use.</summary>
        public GridPoint? FightPoint { get; set; }

        /// <summary>The fields next to <see cref="FightPoint"/> where melee attackers fight (see <see cref="MilitaryConfig.DoorFighters"/>).</summary>
        public GridPoint[]? DoorSpots { get; set; }

        public bool IsEmpty => Places.All(p => p is null) && Defending.Count == 0;
    }

    private sealed class Barrack(Building building)
    {
        public Building Building { get; } = building;

        /// <summary>The carriers on their way to the door.</summary>
        public List<Recruit> Recruits { get; } = new();
    }

    private sealed class Recruit(Settler settler, WalkWatch walk)
    {
        public Settler Settler { get; } = settler;
        public WalkWatch Walk { get; } = walk;
    }
}
