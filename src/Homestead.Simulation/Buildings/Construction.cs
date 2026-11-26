using Homestead.Simulation.Economy;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Buildings;

/// <summary>
/// Sends diggers and builders to construction sites and runs their work.
/// </summary>
/// <remarks>
/// Diggers move a point of the site (footprint, or the slope around it, see <see cref="World.PlanLevelling"/>)
/// by up to <see cref="ProductionConfig.LevellingHeightPerDig"/> towards its target height per work cycle,
/// never making the slope to a neighbor steeper than <see cref="ProductionConfig.LevellingMaxSlope"/>
/// (on a hillside they start at the top), up to <see cref="MaxDiggersPerSite"/> per site.
/// When all points are level, the site becomes solid and builders take the builder spots of the
/// building definition. Each build cycle uses one unit of delivered material; without material a
/// builder is released (idle, free for another site; it steps away from the site), and a site offers builder spots only while it
/// has more material than the builders on their way will use. Workers whose site disappears are simply released, those of a
/// stopped site (<see cref="Building.IsPaused"/>) after their work cycle.
/// If sites need more diggers or builders than there are, carriers are recruited, each fetching its
/// tool first (shovel/hammer, see <see cref="ProductionConfig.Tools"/>). Without tool rules for these
/// trades (e.g. <see cref="ProductionConfig.None"/>) nobody is recruited and only existing ones work.
/// </remarks>
public sealed class Construction
{
    public const int DigTicks = 24;
    public const int BuildTicks = 30;
    public const int MaxDiggersPerSite = 3;
    public const int DispatchInterval = 5;
    private const string DiggerTrade = "DIGGER";
    private const string BuilderTrade = "BRICKLAYER";

    private readonly World _world;
    private readonly Dictionary<int, Assignment> _assignments = new();
    private readonly List<Recruit> _recruits = new();
    private readonly Dictionary<int, int> _missingDiggers = new();
    private readonly Dictionary<int, int> _missingBuilders = new();

    /// <summary>Levelled points of buildings that came down, growing back over (see <see cref="Regrow"/>), by index.</summary>
    private readonly List<int> _bare = new();

    /// <summary>Whether <see cref="_bare"/> holds the bare levelled ground; a game saved before it existed looks once.</summary>
    private bool _bareSurveyed;

    internal Construction(World world)
    {
        _world = world;
        _bareSurveyed = true; // levelled ground of the original map stays
    }

    public int Assigned => _assignments.Count;

    /// <summary>Diggers/builders the sites still lack and cannot recruit because the tool is missing.</summary>
    internal int WaitingFor(Material tool, int player) =>
        (_world.Recruitment.ToolFor(DiggerTrade) == tool ? _missingDiggers.GetValueOrDefault(player) : 0)
        + (_world.Recruitment.ToolFor(BuilderTrade) == tool ? _missingBuilders.GetValueOrDefault(player) : 0);

    internal void Tick()
    {
        foreach (var assignment in _assignments.Values.ToList())
            Advance(assignment);
        if (_world.Tick % DispatchInterval == 0)
        {
            Dispatch();
            RecruitWorkers();
        }
        if (_world.Tick % World.TicksPerSecond == 0)
            Regrow();
    }

    /// <summary>The building came down: its levelled ground grows back over (<see cref="Regrow"/>).</summary>
    internal void Fell(Building building)
    {
        var map = _world.Map!;
        foreach (var p in building.BlockedPoints)
            if (map.Contains(p) && map.TerrainAt(p) == Map.TerrainType.Flattened && !_bare.Contains(map.Index(p)))
                _bare.Add(map.Index(p));
    }

    /// <summary>
    /// Once a second: every bare levelled point at the edge (next to ground that is not levelled) goes back to what it was
    /// with a chance of 1 in <see cref="ProductionConfig.RegrowSeconds"/>, so a fallen building's ground grows over
    /// from the outside in. Points built on again leave the list.
    /// </summary>
    private void Regrow()
    {
        if (_world.Map is not { } map)
            return;
        if (!_bareSurveyed)
        {
            _bareSurveyed = true;
            for (int i = 0; i < map.PointCount; i++)
                if (map.TerrainAt(i) == Map.TerrainType.Flattened && _world.BuildingAt(map.Point(i)) is null)
                    _bare.Add(i);
        }
        int seconds = _world.Production.Config.RegrowSeconds is > 0 and var s ? s : ProductionConfig.DefaultRegrowSeconds;
        ulong second = (ulong)(_world.Tick / World.TicksPerSecond);
        for (int i = _bare.Count - 1; i >= 0; i--)
        {
            var p = map.Point(_bare[i]);
            if (map.TerrainAt(p) != Map.TerrainType.Flattened || _world.BuildingAt(p) is not null)
            {
                _bare.RemoveAt(i);
                continue;
            }
            if (!AtEdge(map, p) || Mix((ulong)_bare[i], second, (ulong)_world.Seed) % (ulong)seconds != 0)
                continue;
            map.Unlevel(p);
            _bare.RemoveAt(i);
        }
    }

    private static bool AtEdge(Map.GameMap map, GridPoint p)
    {
        for (int d = 0; d < DirectionExtensions.Count; d++)
            if (p.Neighbor((Direction)d) is var q && map.Contains(q) && map.TerrainAt(q) != Map.TerrainType.Flattened)
                return true;
        return false;
    }

    /// <summary>A deterministic hash (SplitMix64 finaliser) standing in for a dice roll per point and second.</summary>
    private static ulong Mix(ulong a, ulong b, ulong c)
    {
        ulong z = a * 0x9E3779B97F4A7C15UL ^ b * 0xC2B2AE3D27D4EB4FUL ^ c * 0x165667B19E3779F9UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Recruits diggers and builders while the sites have more free spots than there are workers.</summary>
    private void RecruitWorkers()
    {
        _recruits.RemoveAll(r => r.State != RecruitState.FetchingTool);
        _missingDiggers.Clear();
        _missingBuilders.Clear();
        // Workers per site, counted once: recruiting assigns no one.
        var assigned = new Dictionary<Building, int>();
        foreach (var assignment in _assignments.Values)
            assigned[assignment.Site] = assigned.GetValueOrDefault(assignment.Site) + 1;
        foreach (var sites in _world.Buildings.Where(b => b.Exists && b.State != BuildingState.Finished && !b.IsPaused).GroupBy(b => b.Player))
        {
            int player = sites.Key;
            _missingDiggers[player] = Recruit(Profession.Digger, DiggerTrade, player, sites.ToList(), site => site.State == BuildingState.Flattening
                ? Math.Min(MaxDiggersPerSite, site.Unlevelled.Count) - assigned.GetValueOrDefault(site)
                : 0);
            _missingBuilders[player] = Recruit(Profession.Builder, BuilderTrade, player, sites.ToList(), site => site.State == BuildingState.Constructing
                ? site.Definition.BuilderSpots.Count - assigned.GetValueOrDefault(site)
                : 0);
        }
    }

    /// <summary>Starts recruitments for the shortfall; returns how many could not be started.</summary>
    private int Recruit(Profession profession, string trade, int player, List<Building> sites, Func<Building, int> freeSpots)
    {
        if (_world.Recruitment.ToolFor(trade) is null)
            return 0;
        int wanted = sites.Sum(s => Math.Max(0, freeSpots(s)));
        int idle = 0, workers = 0;
        foreach (var s in _world.Settlers)
            if (s.Profession == profession && s.Player == player)
            {
                workers++;
                if (!_assignments.ContainsKey(s.Id))
                    idle++;
            }
        int coming = _recruits.Count(r => r.Profession == profession && r.Settler.Player == player);
        int missing = wanted - idle - coming;
        // Never more diggers/builders than the player's share allows (Siedler › Berufe).
        int existing = workers + coming;
        missing = Math.Min(missing, _world.Recruitment.MaxOf(player, profession == Profession.Digger ? Share.Diggers : Share.Builders) - existing);
        // Diggers and builders leave the carrier reserve's last conversions to the workers of the
        // buildings under way: otherwise they take them all and the finished buildings stay empty.
        // One of each is always allowed, else those buildings would never be built.
        int headroom = _world.Recruitment.Convertible(player) - _world.Production.AwaitingWorkers(player);
        if (headroom <= 0 && workers == 0)
            headroom = 1;
        while (missing > 0 && headroom-- > 0)
        {
            var site = sites.First(s => freeSpots(s) > 0);
            if (_world.Recruitment.Start(profession, trade, site.Door, site.Player) is not { } recruit)
                break;
            if (recruit.State == RecruitState.FetchingTool)
                _recruits.Add(recruit);
            missing--;
        }
        return missing;
    }

    private void Dispatch()
    {
        var pathFinder = _world.PathFinder!;
        var sites = _world.Buildings.Where(b => b.Exists && b.State != BuildingState.Finished && !b.IsPaused).ToList();
        if (sites.Count == 0)
            return;

        foreach (var worker in _world.Settlers)
        {
            if (worker.Profession is not (Profession.Digger or Profession.Builder)
                || _assignments.ContainsKey(worker.Id) || !worker.IsStrolling && (worker.Destination is not null || worker.State == SettlerState.Stepping))
                continue;

            Assignment? best = null;
            int bestDistance = int.MaxValue;
            foreach (var site in sites)
            {
                if (site.Player != worker.Player)
                    continue;
                var spot = worker.Profession == Profession.Digger ? FreeDigSpot(site, worker.Position, null) : FreeBuilderSpot(site);
                if (spot is not { } s)
                    continue;
                int distance = PathFinder.Distance(worker.Position, s.Spot);
                if (distance < bestDistance && _world.Reachable(worker, s.Spot))
                {
                    best = new Assignment(worker, site, s.Spot, s.Facing);
                    bestDistance = distance;
                }
            }
            if (best is null)
                continue;

            _assignments[worker.Id] = best;
            worker.HasTask = true;
            if (!_world.WalkTo(worker, best.Spot))
                Release(best);
        }
    }

    private void Advance(Assignment a)
    {
        var worker = a.Worker;
        var site = a.Site;
        bool digging = worker.Profession == Profession.Digger;
        if (!site.Exists || site.State != (digging ? BuildingState.Flattening : BuildingState.Constructing))
        {
            Release(a);
            return;
        }
        if (_world.IsStranded(worker))
        {
            Release(a);
            if (worker.Destination is not null)
                _world.WalkTo(worker, worker.StepTarget);
            return;
        }

        if (!a.Working)
        {
            // Stopped (the site's panel): those not at work yet are free at once.
            if (site.IsPaused)
            {
                Release(a);
                return;
            }
            if (worker.Destination is not null || worker.State == SettlerState.Stepping)
                return;
            if (worker.Position != a.Spot)
            {
                if (!_world.WalkTo(worker, a.Spot))
                    Release(a);
                return;
            }
            a.Working = true;
            worker.IsWorking = true;
            worker.Facing = a.Facing;
            a.TicksLeft = digging ? DigTicks : 0;
            if (digging)
                SetActivity(worker, Activity.Digging);
        }

        if (digging)
            AdvanceDigging(a);
        else
            AdvanceBuilding(a);
    }

    private void AdvanceDigging(Assignment a)
    {
        if (--a.TicksLeft > 0)
            return;

        var site = a.Site;
        var map = _world.Map!;
        if (site.Unlevelled.TryGetValue(a.Spot, out int target))
        {
            // A neighbor changed meanwhile: no step here while others dig on; alone, the step anyway.
            int step = DigStep(site, a.Spot,
                force: !_assignments.Values.Any(o => o.Site == site && o.Worker != a.Worker && o.Worker.Profession == Profession.Digger));
            map.SetHeight(a.Spot, map.HeightAt(a.Spot) + step);
            if (map.HeightAt(a.Spot) == target)
                _world.Levelled(site, a.Spot);
            else if (!site.IsPaused && DigStep(site, a.Spot) != 0)
            {
                // More to do here, and the slopes allow it: another cycle on the same point.
                a.TicksLeft = DigTicks;
                return;
            }
        }

        if (!site.IsPaused && FreeDigSpot(site, a.Spot, a.Worker) is { } next)
        {
            // Carry on with the nearest remaining point of the same site.
            a.Spot = next.Spot;
            a.Working = false;
            a.Worker.IsWorking = false;
            SetActivity(a.Worker, Activity.None);
            _world.WalkTo(a.Worker, a.Spot);
            return;
        }

        Release(a);
        bool othersDigging = _assignments.Values.Any(o => o.Site == site && o.Worker.Profession == Profession.Digger);
        if (site.Unlevelled.Count == 0 && !othersDigging)
            _world.BeginConstruction(site);
    }

    private void AdvanceBuilding(Assignment a)
    {
        var site = a.Site;
        if (a.TicksLeft == 0)
        {
            // Start a build cycle if material is there; otherwise the builder is free for other sites
            // and comes back (or another one does) when material has been delivered.
            if (site.IsPaused || !_world.ConsumeConstructionMaterial(site))
            {
                Release(a);
                StepAway(a.Worker, site);
                return;
            }
            a.TicksLeft = BuildTicks;
            SetActivity(a.Worker, Activity.Building);
        }

        if (--a.TicksLeft > 0)
            return;

        site.BuildStepsDone++;
        if (site.BuildStepsDone >= site.BuildStepsTotal)
        {
            _world.FinishConstruction(site);
            foreach (var other in _assignments.Values.Where(o => o.Site == site).ToList())
                Release(other);
        }
    }

    /// <summary>
    /// Nearest footprint point still to level that no other digger works on, or null if there is none
    /// or the site already has enough diggers (not counting <paramref name="self"/>).
    /// </summary>
    private (GridPoint Spot, Direction Facing)? FreeDigSpot(Building site, GridPoint from, Settler? self)
    {
        if (site.State != BuildingState.Flattening)
            return null;
        var others = _assignments.Values.Where(o => o.Site == site && o.Worker != self).ToList();
        if (others.Count >= MaxDiggersPerSite)
            return null;
        var taken = others.Select(o => o.Spot).ToHashSet();
        var free = site.Unlevelled.Keys.Where(p => !taken.Contains(p)).ToList();
        // Only where the slopes allow a step; should nothing allow one with nobody else at work (ground that
        // was steeper than allowed), any point, so the site never gets stuck.
        var workable = free.Where(p => DigStep(site, p) != 0).ToList();
        if (workable.Count == 0 && others.Count == 0)
            workable = free;
        if (workable.Count == 0)
            return null;
        return (workable.MinBy(p => PathFinder.Distance(from, p)), Direction.SouthWest);
    }

    /// <summary>
    /// The height change one work cycle makes at a point of the site: towards its target, at most
    /// <see cref="ProductionConfig.LevellingHeightPerDig"/>, and only as far as no neighbor ends up more than
    /// <see cref="ProductionConfig.LevellingMaxSlope"/> apart (or further apart than before). 0 if no step is
    /// allowed; with <paramref name="force"/> the full step regardless.
    /// </summary>
    private int DigStep(Building site, GridPoint p, bool force = false)
    {
        var map = _world.Map!;
        var config = _world.Production.Config;
        if (!site.Unlevelled.TryGetValue(p, out int target))
            return 0;
        int height = map.HeightAt(p);
        int direction = Math.Sign(target - height);
        int most = Math.Min(Math.Max(1, config.LevellingHeightPerDig), Math.Abs(target - height));
        if (force)
        {
            // Still the largest allowed step, if there is one.
            int allowed = DigStep(site, p);
            return allowed != 0 ? allowed : direction * most;
        }
        for (int step = most; step >= 1; step--)
        {
            int to = height + direction * step;
            bool fits = true;
            for (int d = 0; d < DirectionExtensions.Count && fits; d++)
            {
                var n = p.Neighbor((Direction)d);
                if (!map.Contains(n))
                    continue;
                int neighbor = map.HeightAt(n);
                fits = Math.Abs(to - neighbor) <= Math.Max(config.LevellingMaxSlope, Math.Abs(height - neighbor));
            }
            if (fits)
                return direction * step;
        }
        return 0;
    }

    /// <summary>
    /// A builder spot of the site no one works on, or null if there is none or the site has no material
    /// for one more builder: each builder there not in a build cycle yet will use one unit.
    /// </summary>
    private (GridPoint Spot, Direction Facing)? FreeBuilderSpot(Building site)
    {
        if (site.State != BuildingState.Constructing)
            return null;
        var builders = _assignments.Values.Where(o => o.Site == site).ToList();
        int material = site.Piles.Where(p => p.Role == PileRole.Request).Sum(p => p.Count);
        if (material <= builders.Count(o => o.TicksLeft == 0))
            return null;
        var map = _world.Map!;
        var taken = builders.Select(o => o.Spot).ToHashSet();
        foreach (var spot in site.Definition.BuilderSpots)
        {
            var p = site.Position + spot.Offset;
            if (!taken.Contains(p) && map.IsWalkable(p))
                return (p, spot.Facing);
        }
        return null;
    }

    /// <summary>
    /// One step from the builder spot to a free neighbor farthest from the site (free ground preferred),
    /// so the idle builder no longer stands in the site's way. Strolling: free for any site meanwhile.
    /// </summary>
    private void StepAway(Settler worker, Building site)
    {
        var map = _world.Map!;
        GridPoint? best = null;
        (bool FreeGround, int Distance) bestScore = (false, -1);
        for (int d = 0; d < DirectionExtensions.Count; d++)
        {
            var p = worker.Position.Neighbor((Direction)d);
            if (!map.IsWalkable(p) || _world.IsOccupied(p) || !_world.Reachable(worker, p))
                continue;
            var score = (_world.IsFreeGround(p), PathFinder.Distance(p, site.Position));
            if (best is null || score.CompareTo(bestScore) > 0)
            {
                best = p;
                bestScore = score;
            }
        }
        if (best is { } target && _world.WalkTo(worker, target))
            worker.IsStrolling = true;
    }

    private void Release(Assignment a)
    {
        _assignments.Remove(a.Worker.Id);
        a.Worker.HasTask = false;
        a.Worker.IsWorking = false;
        SetActivity(a.Worker, Activity.None);
    }

    private static void SetActivity(Settler settler, Activity activity)
    {
        if (settler.Activity == activity)
            return;
        settler.Activity = activity;
        settler.ActivityTicks = 0;
    }

    private sealed class Assignment(Settler worker, Building site, GridPoint spot, Direction facing)
    {
        public Settler Worker { get; } = worker;
        public Building Site { get; } = site;
        public GridPoint Spot { get; set; } = spot;
        public Direction Facing { get; } = facing;
        public bool Working { get; set; }
        public int TicksLeft { get; set; }
    }
}
