using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Map;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Ai;

/// <summary>
/// A computer player. It uses the same orders as a human player (construction sites; the rest is
/// done by the settlers) and knows the whole map.
/// </summary>
/// <remarks>
/// Every <see cref="AiConfig.ThinkTicks"/> it does two things:
/// <list type="bullet">
/// <item>Economy: starts the first building of its wishes it has not got yet and can afford: a living
/// house when carriers run short, the toolsmith when a worker waits for a tool, then the build order
/// of <c>data/ai.json</c>. Each building goes next to what it works on (trees, stones, ore, water),
/// otherwise into the middle of its production chain, otherwise near the home tower.</item>
/// <item>Expansion: when the own land lacks quarry stone, ore, trees, water or room, a tower goes to the
/// own-land spot nearest the closest unclaimed place with it, so a game cannot stall on a start area
/// without mountains or stones.</item>
/// </list>
/// </remarks>
public sealed partial class AiPlayer
{
    private const int SurveyTicks = 200;
    private const int SearchRadius = 30;
    private const int TowerSearchRadius = 60;
    private const int MaxLog = 40;

    /// <summary>How long a building may stand without anything left to work on.</summary>
    private const int UselessTicks = 3 * 60 * World.TicksPerSecond;

    /// <summary>Offsets sorted by distance, for searching spots nearest a point.</summary>
    private static readonly GridPoint[] Spiral = PathFinder.Circle(new GridPoint(0, 0), TowerSearchRadius)
        .OrderBy(o => PathFinder.Distance(new GridPoint(0, 0), o)).ToArray();

    private static readonly string[] Basics = ["lumberjack", "sawmill", "stonecutter"];

    private readonly World _world;
    private readonly BuildingCatalog _catalog;
    private readonly AiConfig _config;
    private readonly MapSurvey _survey;
    private readonly List<string> _log = new();
    private long _nextSurvey;
    private long _nextForesterCheck;

    /// <summary>The best chance of the spots the last mine placement looked at (for the note when none was good enough).</summary>
    [NonSerialized] private double _bestMineChance;
    private bool _lacksRoom;

    /// <summary>Resource-bound buildings that found no spot near their resource (the expansion goes for more of it).</summary>
    private readonly HashSet<string> _noSpot = new();

    /// <summary>
    /// When the buildings in <see cref="_noSpot"/> are looked for again, <see cref="NoSpotRetryTicks"/> after: the search goes over
    /// hundreds of fields and nearly always comes out the same (16 players on a big map: fishers without a free bank took a quarter
    /// of the simulation). Not in the saved config: a field added there is 0 in older saves.
    /// </summary>
    private readonly Dictionary<string, long> _noSpotRetry = new();
    private const int NoSpotRetryTicks = 15 * World.TicksPerSecond;
    private readonly Dictionary<int, long> _uselessSince = new();
    private readonly HashSet<string> _reported = new();
    private Building? _home;

    /// <param name="character">Name of the character (ai.json <c>characters</c>, or traits joined by '+'); null: drawn by the people's weights with <paramref name="random"/>.</param>
    public AiPlayer(World world, BuildingCatalog catalog, int player, AiConfig? config = null, string? character = null, Random? random = null)
    {
        _world = world;
        _catalog = catalog;
        _config = config ?? AiConfig.Load(civilization: catalog.Civilization, character: character);
        Player = player;
        _survey = new MapSurvey(world, player);
        Character = character is null ? AiCharacter.Draw(_config, random ?? new Random(player)) : AiCharacter.Create(_config, character);
        // The character's own values (e.g. a lower attack ratio) on top of the people's.
        if (config is null)
            _config = AiConfig.Load(civilization: catalog.Civilization, character: Character.Id);
        // Decision point "build order": each trait may rearrange it, in the character's order.
        foreach (var trait in Character.Traits)
            _config = _config with { BuildOrder = trait.ArrangeBuildOrder(_config.BuildOrder) };
        if (Character.Traits.Count > 0)
            Note($"plays as {Character.Id} ({string.Join(", ", Character.Traits.Select(t => t.Id))})");
    }

    public int Player { get; }

    /// <summary>What it plays like: the traits it uses on top of the built-in behavior.</summary>
    public AiCharacter Character { get; }

    internal World World => _world;

    /// <summary>Whether the player is an enemy: another player not on the own team (allies are left alone).</summary>
    internal bool IsEnemy(int player) => !_world.AreAllies(Player, player);

    /// <summary>The home military building (the first one), where soldiers gather without a task.</summary>
    internal Building? Home => _home;

    internal AiConfig Config => _config;

    /// <summary>The build order it plays by: the people's, with the character's moves and traits applied.</summary>
    public IReadOnlyList<(string Building, int Count)> BuildOrder => _config.BuildOrder;

    /// <summary>The latest decisions, oldest first (for watching and debugging).</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>Every decision as it is noted (time prefixed), for watching a game live.</summary>
    public event Action<AiPlayer, string>? Noted;

    /// <summary>The trait a noted decision came from (trait notes start with the trait's id), or null.</summary>
    public AiTrait? TraitOfNote(string line) =>
        Character.Traits.FirstOrDefault(t => line[(line.IndexOf(' ') + 1)..].StartsWith(t.Id, StringComparison.Ordinal));

    /// <summary>Points of land the player owned at the last survey.</summary>
    public int Land => _survey.Own.Own;

    /// <summary>Free building land on the own land at the last survey.</summary>
    public int FreeLand => _survey.Own.Buildable;

    /// <summary>Water (sea, lakes, rivers) on the own land at the last survey.</summary>
    public int WaterLand => _survey.Own.Water;

    internal void Tick()
    {
        if ((_world.Tick + Player * 7) % _config.ThinkTicks != 0)
            return;
        if (_world.Tick >= _nextSurvey)
        {
            _survey.Update();
            _nextSurvey = _world.Tick + SurveyTicks;
        }
        var mine = _world.Buildings.Where(b => b.Exists && b.Player == Player).ToList();
        // Home: the military building most of the own buildings can be reached from. A map may wall in a
        // door (Z10: two Egyptian castles side by side), and nothing would be reachable from there.
        if (_home is not { Exists: true } || _home.Player != Player)
            _home = mine.Where(IsMilitary).OrderBy(b => b.State == BuildingState.Finished ? 0 : 1)
                        .ThenByDescending(b => mine.Count(o => _world.Reachable(Player, b.Door, o.Door))).FirstOrDefault()
                    ?? mine.FirstOrDefault();
        if (_home is null)
            return;
        TearDownUseless(mine);
        TearDownSpareHouse(mine);
        FreeSmithForToolsmith(mine);
        mine.RemoveAll(b => !b.Exists);
        PauseForesters(mine);
        if (AssaultOn is null)
        {
            FollowStrikes(mine);
            FollowCampaign();
        }
        LeadPriests();
        UpgradeSoldiers();
        ArmForLateGame();
        if (_world.Tick >= _nextMilitary)
        {
            if (AssaultOn is null)
                Command(mine);
            else
                FollowAssault(mine);
            _nextMilitary = _world.Tick + _config.Military.ThinkTicks;
        }
        SampleGrowth(mine);
        Expand(mine);
        BuildEconomy(mine);
    }

    // ---- economy ----

    private void BuildEconomy(List<Building> mine)
    {
        if (mine.Count(b => b.State != BuildingState.Finished && !IsMilitary(b)) >= _config.MaxSites)
            return;
        int planks = Free(Material.Plank, mine), stones = Free(Material.Stone, mine);
        // Everything but the basic producers leaves enough for a new stonecutter: a player out of
        // stones with no stonecutter it can afford would be stuck for good.
        var cutter = _catalog.Find("stonecutter");
        int keepPlanks = cutter is null ? 0 : Cost(cutter, Material.Plank), keepStones = cutter is null ? 0 : Cost(cutter, Material.Stone);
        foreach (var (name, count) in Wishes(mine))
        {
            if (mine.Count(b => b.Definition.Name == name) >= count || _catalog.Find(name) is not { } definition)
                continue;
            if (LacksOre(definition, mine))
                continue;
            // Another one would only wait as well (farms while no scythe can be made).
            if (WaitsForWorker(name, mine))
                continue;
            // A forester only where the wood runs short: in a full forest it would stop at once.
            if (name == "forester" && ForesterSpot(mine) is null)
                continue;
            if (!SmithLeftFor(definition, mine))
                continue;
            if (_noSpot.Contains(name) && _world.Tick < _noSpotRetry.GetValueOrDefault(name))
                continue;
            // The toolsmith only comes when a tool is missing, maybe the saw: then no planks come until it works.
            bool basic = Basics.Contains(name) || name == Toolsmith;
            if (!Enough(planks, Cost(definition, Material.Plank) + (basic ? 0 : keepPlanks))
                || !Enough(stones, Cost(definition, Material.Stone) + (basic ? 0 : keepStones)))
            {
                // Wait for the material rather than skip ahead, unless nothing would ever bring it.
                bool noSupply = Basics.Contains(name) && !mine.Any(b => b.Definition.Name == name);
                if (!noSupply)
                    return;
            }
            if (AnchorFor(definition, mine) is not { } anchor)
            {
                NoteOnce(name, $"cannot build {name}: nothing to work on in own land");
                continue; // the expansion goes for it
            }
            if ((Place(definition, anchor) ?? PlaceFurther(definition, mine) ?? PlaceWhereRoomy(definition) ?? PlaceForester(definition, mine)) is { } site)
            {
                _reported.Remove(name);
                _noSpot.Remove(name);
                Note($"builds {name} at {site.Position.X},{site.Position.Y}");
                return;
            }
            NoteOnce(name, $"cannot build {name}: no spot near {anchor.X},{anchor.Y}"
                           + (_world.Production.Config.Mines.ContainsKey(name) ? $" (best chance {_bestMineChance:P0})" : ""));
            if (!IsResourceBound(name))
                _lacksRoom = true;
            else
            {
                _noSpot.Add(name);
                _noSpotRetry[name] = _world.Tick + NoSpotRetryTicks;
            }
        }
    }

    /// <summary>Whether a finished building of this kind still waits for its worker (tool missing, no carrier).</summary>
    private bool WaitsForWorker(string name, List<Building> mine) =>
        mine.Any(b => b.Definition.Name == name && b.State == BuildingState.Finished && _world.StatusOf(b).StartsWith("no worker", StringComparison.Ordinal));

    /// <summary>
    /// A living house comes first when carriers run short: fewer free ones than freeCarriers, no more
    /// carriers in all than carriersPerProductionBuilding per production building (sites included),
    /// or the carrier reserve (nearly) stops recruiting. Waiting until no carrier may convert any more
    /// is too late: the plank and stone makers may then lack workers for good, and the house its material.
    /// Residents still to come count as carriers.
    /// </summary>
    private bool HouseWanted(List<Building> mine)
    {
        if (mine.Any(b => IsLivingHouse(b.Definition.Name) && b.State != BuildingState.Finished))
            return false;
        int coming = mine.Sum(b => _world.Housing.ResidentsLeft(b) ?? 0);
        int free = _world.Settlers.Count(s => s.Player == Player && s.IsAvailableCarrier) + coming;
        int carriers = _world.Settlers.Count(s => s.Player == Player && s.IsCarrier) + coming;
        int producers = mine.Count(b => b.Definition.Worker is not null);
        return free < _config.FreeCarriers
               || carriers <= producers * _config.CarriersPerProductionBuilding
               || _world.Recruitment.Convertible(Player) < _config.HouseWhenConvertible;
    }

    /// <summary>
    /// Tears down a living house no one needs: one that has released all its residents, while without
    /// it at least spareRoom places would still be free and no house is wanted. Its material comes back
    /// in part. The biggest such house first.
    /// </summary>
    /// <summary>
    /// Foresters stop planting while their work area has <see cref="AiConfig.ForesterMaxTrees"/> trees or more
    /// (saplings count: they grow up), and start again below.
    /// </summary>
    private void PauseForesters(List<Building> mine)
    {
        if (_world.Tick < _nextForesterCheck)
            return;
        _nextForesterCheck = _world.Tick + _config.ForesterCheckTicks;
        var map = _world.Map!;
        foreach (var forester in mine)
        {
            if (forester.Definition.Name != "forester" || !_world.Production.CanPause(forester))
                continue;
            int trees = PathFinder.Circle(forester.WorkCenter, forester.Definition.WorkRadius ?? Production.DefaultRadius)
                .Count(p => map.Contains(p) && map.ObjectAt(p) == MapObjectKind.Tree);
            bool pause = trees >= _config.ForesterMaxTrees;
            if (pause != forester.IsPaused)
            {
                _world.SetPaused(forester, pause);
                Note($"forester at {forester.Position.X},{forester.Position.Y}: {trees} trees, {(pause ? "stopped" : "restarted")}");
            }
        }
    }

    private void TearDownSpareHouse(List<Building> mine)
    {
        if (_world.Housing.Room(Player) is not { } room || HouseWanted(mine))
            return;
        int spare = room - _world.Housing.Needing(Player);
        var house = mine.Where(b => b.State == BuildingState.Finished && _world.Housing.ResidentsLeft(b) == 0
                                    && spare - _world.Housing.RoomOf(b) >= _config.SpareRoom)
            .MaxBy(b => _world.Housing.RoomOf(b));
        if (house is null)
            return;
        TearDown(house, $"{spare} places spare");
    }

    /// <summary>Tears a building down – but never the own last finished garrison: without one the game
    /// is lost (<see cref="ConquestGoals"/>). The player is asked before (client), the AI never risks it.</summary>
    private void TearDown(Building building, string why)
    {
        if (building.Definition.Occupiers.Count > 0 && !_world.Buildings.Any(b => b != building && b.Exists
                && b.Player == Player && b.State == BuildingState.Finished && b.Definition.Occupiers.Count > 0))
        {
            Note($"keeps {building.Definition.Name} at {building.Position.X},{building.Position.Y}: the last garrison");
            return;
        }
        Note($"tears down {building.Definition.Name} at {building.Position.X},{building.Position.Y}: {why}");
        _world.RemoveBuilding(building);
    }

    private bool IsLivingHouse(string name) => name == _config.House || _config.HouseSizes.Any(h => h.Building == name);

    /// <summary>
    /// The living house to build: the biggest one the wood and stone production can afford (enough
    /// finished sawmills and stonecutters, per people in ai.json), otherwise the small one.
    /// </summary>
    private BuildingDefinition? HouseToBuild(List<Building> mine)
    {
        int sawmills = mine.Count(b => b.Definition.Name == "sawmill" && b.State == BuildingState.Finished);
        int stonecutters = mine.Count(b => b.Definition.Name == "stonecutter" && b.State == BuildingState.Finished);
        foreach (var size in _config.HouseSizes)
            if (sawmills >= size.Sawmills && stonecutters >= size.Stonecutters && _catalog.Find(size.Building) is { } house)
                return house;
        return _catalog.Find(_config.House);
    }

    private IEnumerable<(string Name, int Count)> Wishes(List<Building> mine)
    {
        if (HouseWanted(mine) && HouseToBuild(mine) is { } house)
            yield return (house.Name, mine.Count(b => b.Definition.Name == house.Name) + 1);
        if ((_hospitalAnchor = HospitalSpot(mine)) is not null)
            yield return (HospitalName, mine.Count(b => b.Definition.Name == HospitalName) + 1);
        // A storehouse only once the player makes or owns gold or gems (taken over with land too), goods worth storing
        // (tools, weapons, gold, gems) pile up, and the storehouses there are have no room left for them.
        var storage = mine.Where(b => b.State == BuildingState.Finished).SelectMany(b => b.Piles).Where(p => p.Role == PileRole.Storage).ToList();
        bool stockComing = mine.Any(b => b.Definition.Name == "stock" && b.State != BuildingState.Finished);
        int waiting = _world.Piles.Where(p => p.Role is PileRole.Loose or PileRole.Offer && p.Material is { } m && _world.Logistics.IsStored(Player, m)
                                              && _world.BelongsTo(p, Player)).Sum(p => p.Count);
        bool treasure = _world.InStock(Player, Material.Gold) + _world.InStock(Player, Material.Gems) > 0
                        || mine.Any(b => b.State == BuildingState.Finished && b.Definition.Name is "goldmelt" or "gemsmine");
        if (treasure && !stockComing && waiting >= _config.StockWhenWaiting && storage.Count(p => p.Material is null) < 2)
            yield return ("stock", mine.Count(b => b.Definition.Name == "stock") + 1);
        // A stonecutter or woodcutter with nothing left in range: a new one where there is some.
        foreach (var (producer, idle) in new[] { ("stonecutter", "no stone in range"), ("lumberjack", "no tree in range") })
        {
            var all = mine.Where(b => b.Definition.Name == producer).ToList();
            if (all.Count > 0 && all.All(b => b.State == BuildingState.Finished && _world.StatusOf(b) == idle))
                yield return (producer, all.Count + 1);
        }
        if (mine.Any(b => b.State == BuildingState.Finished && _world.StatusOf(b).Contains("tool missing")))
            yield return (Toolsmith, 1);
        // Mines without food: more food, one building at a time.
        bool hungry = mine.Any(b => b.State == BuildingState.Finished && _world.StatusOf(b) == "waiting for food");
        if (hungry && !mine.Any(b => b.Definition.Name is "fisher" or "farm" or "mill" or "baker"
                                     && (b.State != BuildingState.Finished || WaitsForWorker(b.Definition.Name, mine))))
        {
            int fishers = mine.Count(b => b.Definition.Name == "fisher"), farms = mine.Count(b => b.Definition.Name == "farm");
            // Bread: two farms per mill and baker.
            int bakeries = (farms + 1) / 2;
            yield return ("mill", bakeries);
            yield return ("baker", bakeries);
            if (fishers <= farms)
                yield return ("fisher", fishers + 1);
            yield return ("farm", farms + 1);
        }
        foreach (var wish in _config.BuildOrder)
            yield return wish;
        foreach (var wish in _growth)
            yield return wish;
    }

    /// <summary>
    /// A building that processes a mine's output (iron melt, gold melt) while no own building (sites
    /// included) yields it and none of it lies on the own land (start goods): it would only stand idle.
    /// </summary>
    private bool LacksOre(BuildingDefinition definition, List<Building> mine)
    {
        var mines = _world.Production.Config.Mines;
        var mined = mines.Values.Select(m => m.Output).ToHashSet();
        return Inputs(definition).Any(m => mined.Contains(m) && !mine.Any(b => Outputs(b.Definition).Contains(m)
                                                                             || mines.TryGetValue(b.Definition.Name, out var r) && r.Output == m)
                                           && !_world.Piles.Any(p => p.Material == m && p.Count > 0 && p.Role != PileRole.Request && _world.BelongsTo(p, Player)));
    }

    /// <summary>
    /// Whether the player has every build order entry before the first one for <paramref name="building"/>;
    /// entries with nothing to work on in the own land do not count.
    /// </summary>
    private bool ReachedBefore(string building, List<Building> mine)
    {
        foreach (var (name, count) in _config.BuildOrder)
        {
            if (name == building)
                return true;
            if (mine.Count(b => b.Definition.Name == name) < count && _catalog.Find(name) is { } definition
                && AnchorFor(definition, mine) is not null)
                return false;
        }
        return false;
    }

    /// <summary>Planks or stones the player has beyond what its construction sites still need.</summary>
    private int Free(Material material, List<Building> mine)
    {
        int stock = _world.Piles.Where(p => p.Material == material && p.Role != PileRole.Request && _world.BelongsTo(p, Player))
            .Sum(p => p.Available);
        int demand = mine.Where(b => b.State != BuildingState.Finished).SelectMany(b => b.Piles)
            .Where(p => p.Role == PileRole.Request && p.Material == material).Sum(p => Math.Max(0, p.Space));
        return stock - demand;
    }

    private int Cost(BuildingDefinition definition, Material material) =>
        definition.ConstructionPiles.Where(p => p.Material == material).Sum(p => _world.ConstructionCost(Player, p));

    /// <summary>
    /// Whether <paramref name="free"/> units cover <paramref name="needed"/> (cost plus what is kept back),
    /// asking for at most one full pile: a producer keeps no more than that and then waits (only tools,
    /// weapons and gold are stored), so with a single sawmill an Asian farm (9 planks) would never be
    /// started. The rest comes while the site is levelled.
    /// </summary>
    private static bool Enough(int free, int needed) => free >= Math.Min(needed, Materials.MaxPileSize);

    /// <summary>
    /// Where a building should go: next to what it works on, otherwise near home. Null if that is
    /// missing on own land.
    /// </summary>
    private GridPoint? AnchorFor(BuildingDefinition definition, List<Building> mine)
    {
        if (ResourceCells(definition.Name, mine) is { } cells)
            return cells.Select(c => (GridPoint?)c).FirstOrDefault();
        return definition.Name switch
        {
            HospitalName => _hospitalAnchor ?? _home!.Position,
            "forester" => ForesterSpot(mine),
            _ => ChainCenter(definition, mine) ?? _home!.Position,
        };
    }

    /// <summary>
    /// Where the building's production chain is: the middle of the own buildings that supply what it
    /// needs or need what it makes (iron mine and coal mine for the iron melt, mill and waterworks for
    /// the baker). Keeps transport ways short and spreads the economy over the land. A further one of
    /// a kind goes to the part of the chain the existing ones do not serve yet (a second sawmill to the
    /// woodcutters at the new wood). Null if there are none yet.
    /// </summary>
    private GridPoint? ChainCenter(BuildingDefinition definition, List<Building> mine)
    {
        var inputs = Inputs(definition);
        var outputs = Outputs(definition);
        var related = mine.Where(b => !IsMilitary(b) && !b.Piles.Any(p => p.Role == PileRole.Storage) && b.Definition.Name != definition.Name
                                      && (Outputs(b.Definition).Overlaps(inputs) || Inputs(b.Definition).Overlaps(outputs)))
            .ToList();
        var same = mine.Where(b => b.Definition.Name == definition.Name).Select(b => b.Position).ToList();
        var unserved = related.Where(b => same.All(s => PathFinder.Distance(s, b.Position) > ServedDistance)).ToList();
        if (same.Count > 0 && unserved.Count > 0)
            related = unserved;
        if (related.Count == 0)
            return null;
        return new GridPoint((int)Math.Round(related.Average(b => b.Position.X)), (int)Math.Round(related.Average(b => b.Position.Y)));
    }

    /// <summary>
    /// Where a forester is wanted: at the woodcutter that runs short of trees (<see cref="NeedyLumberjacks"/>), or at home
    /// while there is no woodcutter yet (no trees on the own land: the foresters of the build order bring the first wood).
    /// With woodcutters one forester at a time: until the last one works, its trees do not count yet.
    /// </summary>
    private GridPoint? ForesterSpot(List<Building> mine)
    {
        bool woodcutters = mine.Any(b => b.Definition.Name == "lumberjack");
        if (woodcutters && mine.Any(b => b.Definition.Name == "forester"
                                         && (b.State != BuildingState.Finished || _world.StatusOf(b).StartsWith("no worker", StringComparison.Ordinal))))
            return null;
        if (NeedyLumberjacks(mine).FirstOrDefault() is { } lumberjack)
            return lumberjack.Position;
        return woodcutters ? null : _home!.Position;
    }

    /// <summary>No room for a forester at the neediest woodcutter: at the next needy ones.</summary>
    private Building? PlaceForester(BuildingDefinition definition, List<Building> mine) =>
        definition.Name == "forester"
            ? NeedyLumberjacks(mine).Skip(1).Take(3).Select(l => Place(definition, l.Position)).FirstOrDefault(site => site is not null)
            : null;

    /// <summary>
    /// The finished woodcutters with fewer trees (saplings too) in their work area than
    /// <see cref="AiConfig.ForesterBuildBelowTrees"/> and no forester (sites too) plants around it (its work area takes in the
    /// woodcutter's middle), the fewest first.
    /// </summary>
    private IEnumerable<Building> NeedyLumberjacks(List<Building> mine)
    {
        var map = _world.Map!;
        return mine.Where(l => l.Definition.Name == "lumberjack" && l.State == BuildingState.Finished
                               && !mine.Any(f => f.Definition.Name == "forester"
                                                 && PathFinder.InCircle(f.WorkCenter, l.WorkCenter, f.Definition.WorkRadius ?? Production.DefaultRadius)))
            .Select(l => (Lumberjack: l, Trees: PathFinder.Circle(l.WorkCenter, l.Definition.WorkRadius ?? Production.DefaultRadius)
                .Count(p => map.Contains(p) && map.ObjectAt(p) == MapObjectKind.Tree)))
            .Where(l => l.Trees < _config.ForesterBuildBelowTrees)
            .OrderBy(l => l.Trees)
            .Select(l => l.Lumberjack);
    }

    /// <summary>A building of the chain this close to one of the kind counts as served by it.</summary>
    private const int ServedDistance = 12;

    private static HashSet<Material> Inputs(BuildingDefinition definition) =>
        definition.RequestPiles.Where(p => p.Material is not null).Select(p => p.Material!.Value).ToHashSet();

    private HashSet<Material> Outputs(BuildingDefinition definition)
    {
        var outputs = definition.OfferPiles.Where(p => p.Material is not null).Select(p => p.Material!.Value).ToHashSet();
        if (_world.Production.Config.Smiths.TryGetValue(definition.Name, out var smith))
            outputs.UnionWith(smith.Products);
        return outputs;
    }

    /// <summary>Buildings that go where their resource is, not where their chain is.</summary>
    private bool IsResourceBound(string name) =>
        _world.Production.Config.Mines.ContainsKey(name) || name is "lumberjack" or "stonecutter" or "fisher" or "waterworks" or "forester" or "rice_farm" or "beekeeping";

    /// <summary>
    /// For buildings that work on a resource (trees, stones, ore, fish, river, swamp): the own cells with
    /// it, best first. Null for other buildings. Beekeepers want trees too, away from the woodcutters (felled
    /// trees leave the hives without bees' pasture).
    /// </summary>
    private IEnumerable<GridPoint>? ResourceCells(string name, List<Building> mine)
    {
        if (_world.Production.Config.Mines.TryGetValue(name, out var mineRecipe))
            return OwnCells(c => c.Ore[(int)mineRecipe.Resource], mine, name, spacing: 6);
        return name switch
        {
            "lumberjack" => OwnCells(c => c.Trees, mine, name, spacing: 10),
            "beekeeping" => OwnCells(c => c.Trees, mine, name, spacing: 16, crowdedBy: "lumberjack"),
            "stonecutter" => OwnCells(c => c.StoneUnits, mine, name, spacing: 10),
            "fisher" => OwnCells(c => c.Fish, mine, name, spacing: 10),
            "waterworks" => OwnCells(c => c.River, mine, name, spacing: 6),
            "rice_farm" => OwnCells(c => c.Swamp, mine, name, spacing: 12),
            _ => null,
        };
    }

    /// <summary>
    /// Own cells with some of something, the most first (closer to home counts more); cells next to a
    /// building of the same kind (or of <paramref name="crowdedBy"/>) count less, so a second woodcutter goes to another wood.
    /// </summary>
    private IEnumerable<GridPoint> OwnCells(Func<MapSurvey.Cell, int> amount, List<Building> mine, string name, int spacing, string? crowdedBy = null)
    {
        var home = _home!.Position;
        return _survey.Cells
            .Where(c => c.Own > 0 && amount(c.OwnPart!) > 0)
            .Select(c => (Cell: c, Score: amount(c.OwnPart!) / (1.0 + PathFinder.Distance(home, c.Center) / 20.0)
                                          / (1 + mine.Count(b => (b.Definition.Name == name || b.Definition.Name == crowdedBy)
                                                                 && PathFinder.Distance(b.Position, c.Center) < spacing))))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Cell.Center);
    }

    private readonly Dictionary<string, long> _nextRoomySearch = new();

    /// <summary>Further cells tried for a resource-bound building when the best one has no spot.</summary>
    private const int FurtherCells = 3;

    /// <summary>
    /// No spot at the best cell of its resource (a crowded river bank): the next cells with some of it are
    /// tried, at most once a minute per building.
    /// </summary>
    private Building? PlaceFurther(BuildingDefinition definition, List<Building> mine)
    {
        if (ResourceCells(definition.Name, mine) is not { } cells || _world.Tick < _nextRoomySearch.GetValueOrDefault(definition.Name))
            return null;
        _nextRoomySearch[definition.Name] = _world.Tick + 60 * World.TicksPerSecond;
        foreach (var cell in cells.Skip(1).Take(FurtherCells))
            if (Place(definition, cell) is { } site)
                return site;
        return null;
    }

    /// <summary>
    /// No spot near the chain (big buildings in a crowded town): the own cells with the most free land
    /// are tried, at most once a minute per building, as it takes a while.
    /// </summary>
    private Building? PlaceWhereRoomy(BuildingDefinition definition)
    {
        if (IsResourceBound(definition.Name) || _world.Tick < _nextRoomySearch.GetValueOrDefault(definition.Name))
            return null;
        _nextRoomySearch[definition.Name] = _world.Tick + 60 * World.TicksPerSecond;
        foreach (var cell in _survey.Cells.Where(c => c.Own > 0).OrderByDescending(c => c.OwnPart!.Buildable).Take(5))
            if (Place(definition, cell.Center) is { } site)
                return site;
        return null;
    }

    /// <summary>A construction site on the free own-land spot nearest the anchor that suits the building.</summary>
    private Building? Place(BuildingDefinition definition, GridPoint anchor)
    {
        var map = _world.Map!;
        var pathFinder = _world.PathFinder!;
        var homeDoor = _home!.Door;
        bool isMine = _world.Production.Config.Mines.ContainsKey(definition.Name);
        GridPoint? bestMine = null;
        double bestChance = 0;
        int checkedMines = 0;
        _bestMineChance = 0;
        foreach (var offset in Spiral)
        {
            // A forester further off would not plant for the woodcutter it is meant for (see NeedyLumberjacks).
            if (PathFinder.Distance(default, offset) > (definition.Name == "forester" ? (definition.WorkRadius ?? Production.DefaultRadius) - 2 : SearchRadius))
                break;
            var p = anchor + offset;
            // Reachability first: a lookup, while placing checks a lot (levelling among them).
            if (!map.Contains(p) || map.OwnerAt(p) != Player || !_world.Reachable(Player, homeDoor, p + definition.Door)
                || !_world.CanPlace(definition, p, out _, Player) || OnFields(definition, p))
                continue;
            if (isMine)
            {
                // The best chance of the nearest fitting spots, if it is good enough.
                double chance = _world.Production.MineChanceAt(definition.Name, p) ?? 0;
                _bestMineChance = Math.Max(_bestMineChance, chance);
                if (chance >= _config.MineMinChance && chance > bestChance)
                    (bestMine, bestChance) = (p, chance);
                // Spots without the ore do not count, or a vein just off the anchor is never found.
                if (chance > 0 && ++checkedMines >= 25)
                    break;
                continue;
            }
            if (Suits(definition, p))
                return _world.PlaceConstructionSite(definition, Player, p);
        }
        return bestMine is { } spot ? _world.PlaceConstructionSite(definition, Player, spot) : null;
    }

    private bool Suits(BuildingDefinition definition, GridPoint p)
    {
        var map = _world.Map!;
        return definition.Name switch
        {
            "lumberjack" => Around(p, 10, q => MapObjectCodes.IsAdultTree(map.ObjectCodeAt(q)) ? 1 : 0) >= 3,
            "stonecutter" => Around(p, 10, q => MapObjectCodes.StoneLeft(map.ObjectCodeAt(q))) > 0,
            "fisher" => Around(p, 8, q => IsWater(q) ? map.ResourceAmount(q, ResourceType.Fish) : 0) > 0,
            // Big waterworks (Egyptian) cannot stand close to the bank; the worker walks there.
            "waterworks" => Around(p + (definition.WorkCenter ?? default), (definition.WorkRadius ?? Production.DefaultRadius) - 2,
                q => map.TerrainAt(q) == TerrainType.River ? 1 : 0) > 0,
            // Room for the fields below the farm: free grass not sown or kept for the fields of another farm.
            "farm" => Around(p + (definition.WorkCenter ?? default), definition.WorkRadius ?? 6,
                q => map.CoverFits(q, GroundCover.Farmland) && map.ObjectCodeAt(q) == MapObjectCodes.None && map.CoverAt(q) != GroundCover.Farmland
                     && _world.IsFreeGround(q) && !InFields(q) ? 1 : 0) >= FarmFieldPoints,
            // Vines, rice and beehives: enough free ground of their kind around.
            _ when _world.Production.Config.Gatherers.TryGetValue(definition.Name, out var gatherer) && gatherer.Action == GatherAction.Farm
                   && _world.Production.Config.Crops.TryGetValue(gatherer.Crop, out var crop) =>
                Around(p + (definition.WorkCenter ?? default), definition.WorkRadius ?? 6,
                    q => crop.Ground.Contains(map.TerrainAt(q)) && map.ObjectCodeAt(q) == MapObjectCodes.None && _world.IsFreeGround(q) ? 1 : 0) >= 10,
            _ => true,
        };
    }

    private bool IsWater(GridPoint p) => _world.Map!.TerrainAt(p) is TerrainType.Water or TerrainType.River;

    /// <summary>Free grass a new farm wants around its work center (below it) for its fields.</summary>
    private const int FarmFieldPoints = 35;

    /// <summary>Points this close to the work center of an own farm are kept for its fields.</summary>
    private const int FieldRadius = 4;

    /// <summary>
    /// Whether the building would stand on farmland or on the fields kept for an own farm (around its work
    /// center, sown or not yet): the AI builds elsewhere.
    /// </summary>
    private bool OnFields(BuildingDefinition definition, GridPoint p)
    {
        var map = _world.Map!;
        var fields = FieldCenters();
        foreach (var offset in definition.Blocked)
        {
            var q = p + offset;
            if (map.Contains(q) && map.CoverAt(q) == GroundCover.Farmland || InFields(q, fields))
                return true;
        }
        return false;
    }

    private bool InFields(GridPoint q) => InFields(q, FieldCenters());

    private static bool InFields(GridPoint q, List<GridPoint> fields) => fields.Any(c => PathFinder.Distance(c, q) <= FieldRadius);

    private long _fieldCentersTick = -1;
    private List<GridPoint> _fieldCenters = [];

    /// <summary>Work centers of the own farms whose crop turns into farmland (sites too), once per tick.</summary>
    private List<GridPoint> FieldCenters()
    {
        if (_fieldCentersTick == _world.Tick)
            return _fieldCenters;
        var config = _world.Production.Config;
        _fieldCenters = _world.Buildings
            .Where(b => b.Exists && b.Player == Player && config.Gatherers.TryGetValue(b.Definition.Name, out var g)
                        && g.Action == GatherAction.Farm && config.Crops.TryGetValue(g.Crop, out var crop) && crop.Farmland)
            .Select(b => b.Position + (b.Definition.WorkCenter ?? default))
            .ToList();
        _fieldCentersTick = _world.Tick;
        return _fieldCenters;
    }

    private int Around(GridPoint center, int radius, Func<GridPoint, int> value)
    {
        var map = _world.Map!;
        int sum = 0;
        foreach (var q in PathFinder.Circle(center, radius))
            if (map.Contains(q))
                sum += value(q);
        return sum;
    }

    /// <summary>
    /// Buildings that have run out of work for good (quarry stones cut, fish caught, mine exhausted) go
    /// after a while: the worker keeps its trade and takes the job at the new building that goes where
    /// there is some left. A stonecutter or fisher first gets its work area moved (at most
    /// <see cref="World.MaxWorkCenterDistance"/> fields) to where more is in range than now, if there is such a place.
    /// </summary>
    private void TearDownUseless(List<Building> mine)
    {
        foreach (var building in mine)
        {
            if (building.State != BuildingState.Finished
                || (_world.StatusOf(building) is not ("no stone in range" or "no fish in range" or "mine is exhausted")
                    && !(_world.Production.MineChance(building) < _config.MineAbandonChance)))
            {
                _uselessSince.Remove(building.Id);
                continue;
            }
            long since = _uselessSince.TryGetValue(building.Id, out long t) ? t : _uselessSince[building.Id] = _world.Tick;
            if (_world.Tick - since < UselessTicks)
                continue;
            _uselessSince.Remove(building.Id);
            if (BetterWorkCenter(building) is { } better && _world.SetWorkCenter(building, better) is { } moved)
            {
                Note($"moves the work area of {building.Definition.Name} at {building.Position.X},{building.Position.Y} to {moved.X},{moved.Y}");
                continue;
            }
            string why = _world.Production.MineChance(building) is { } chance ? $"chance {chance:P0}" : _world.StatusOf(building);
            TearDown(building, why);
        }
    }

    /// <summary>
    /// For a stonecutter or fisher: the work center within <see cref="World.MaxWorkCenterDistance"/> of the
    /// building that has the most stone (fish) on own land in range – nearest the building among equals –
    /// if that is more than the current one has; null otherwise.
    /// </summary>
    private GridPoint? BetterWorkCenter(Building building)
    {
        var map = _world.Map!;
        Func<GridPoint, int>? amount = building.Definition.Name switch
        {
            "stonecutter" => q => MapObjectCodes.StoneLeft(map.ObjectCodeAt(q)),
            "fisher" => q => map.TerrainAt(q) is TerrainType.Water or TerrainType.River ? map.ResourceAmount(q, ResourceType.Fish) : 0,
            _ => null,
        };
        if (amount is null || !building.HasWorkArea)
            return null;
        int radius = building.Definition.WorkRadius ?? Production.DefaultRadius;
        // The amounts around read once: summed up for every center anew they took a third of a second (16 players).
        // A circle reaches up to twice its radius along a coordinate (see PathFinder.Circle).
        var area = PathFinder.Circle(default, radius).ToArray();
        int reach = 2 * (World.MaxWorkCenterDistance + radius), side = 2 * reach + 1;
        int left = building.Position.X - reach, top = building.Position.Y - reach;
        var amounts = new int[side * side];
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            var q = new GridPoint(left + x, top + y);
            if (map.Contains(q) && (!_world.TerritoryRules || map.OwnerAt(q) == Player))
                amounts[y * side + x] = amount(q);
        }
        int InRange(GridPoint center)
        {
            int sum = 0;
            foreach (var offset in area)
                sum += amounts[(center.Y + offset.Y - top) * side + center.X + offset.X - left];
            return sum;
        }
        // The current center may lie anywhere (set by the player): counted on its own.
        int now = Around(building.WorkCenter, radius, q => !_world.TerritoryRules || map.OwnerAt(q) == Player ? amount(q) : 0);
        var best = PathFinder.Circle(building.Position, World.MaxWorkCenterDistance).Where(map.Contains)
            .Select(c => (Center: c, Amount: InRange(c)))
            .OrderByDescending(c => c.Amount).ThenBy(c => PathFinder.Distance(c.Center, building.Position))
            .FirstOrDefault();
        return best.Amount > now ? best.Center : null;
    }

    // ---- expansion ----

    /// <summary>
    /// Builds a tower towards what the land lacks, if its material is on hand. Nothing is kept back
    /// for a tower that cannot be afforded yet: planks and stones are made again.
    /// </summary>
    private void Expand(List<Building> mine)
    {
        if (_catalog.Find(_config.ExpansionBuilding) is not { } tower)
            return;
        int towerSites = mine.Count(b => IsMilitary(b) && b.State != BuildingState.Finished);
        if (towerSites >= _config.MaxTowerSites)
            return;
        // Somebody has to occupy it.
        int spareSoldiers = _world.Settlers.Count(s => s.Player == Player && s.Profession == Profession.Soldier && !s.HasTask && !s.IsInside);
        int emptyTowers = mine.Count(b => IsMilitary(b) && b.State == BuildingState.Finished && !_world.Military.IsOccupied(b));
        spareSoldiers -= emptyTowers + towerSites;
        if (spareSoldiers <= 0)
            return;
        (int Planks, int Stones) cost = (Cost(tower, Material.Plank), Cost(tower, Material.Stone));
        int planks = Free(Material.Plank, mine), stones = Free(Material.Stone, mine);
        // A wanted living house comes before towers: they leave its material.
        (int Planks, int Stones) keep = HouseWanted(mine) && HouseToBuild(mine) is { } house
            ? (Cost(house, Material.Plank), Cost(house, Material.Stone))
            : default;
        if (!Enough(planks, cost.Planks + keep.Planks) || !Enough(stones, cost.Stones + keep.Stones))
            return;

        // Places an own tower (or tower site) will cover anyway do not need another.
        var military = mine.Where(IsMilitary).Select(b => b.Position).ToList();
        int covered = (int)(_world.Military.Config.Territory.GetValueOrDefault(tower.Name, 20) * 0.7);
        bool Uncovered(MapSurvey.Cell c) => military.All(m => PathFinder.Distance(m, c.Center) > covered);

        foreach (var (need, wanted) in Needs(spareSoldiers, mine))
        {
            // Plain land only from a full store: what the economy needs comes first.
            if (need == "land" && (planks - keep.Planks < cost.Planks + _config.LandStock || stones - keep.Stones < cost.Stones + _config.LandStock))
                continue;
            if (NearestUnclaimed(c => wanted(c) && Uncovered(c)) is not { } target)
                continue;
            if (PlaceTower(tower, target, mine, resource: need is not ("land" or "room")) is not { } site)
            {
                NoteOnce($"expand:{need}", $"cannot expand towards {need} at {target.X},{target.Y}: no tower spot");
                continue;
            }
            _reported.Remove($"expand:{need}");
            Note($"expands towards {need} at {target.X},{target.Y}: tower at {site.Position.X},{site.Position.Y}");
            if (need == "room")
                _lacksRoom = false;
            if (need == "water")
                _noSpot.Remove("waterworks");
            return;
        }
    }

    /// <summary>What the own land lacks, most urgent first, with how to recognize a cell that has it.</summary>
    private IEnumerable<(string Need, Func<MapSurvey.Cell, bool> Has)> Needs(int spareSoldiers, List<Building> mine)
    {
        var own = _survey.Own;
        // Stones almost gone first, then the ores, then more stones for later.
        if (own.StoneUnits < _config.MinStoneUnits)
            yield return ("stone", c => c.StoneUnits >= 10);
        // An ore counts as lacking too when its mine finds no spot good enough (a few fields at the border).
        foreach (var ore in _config.Ores)
            if (own.Ore[(int)ore] == 0 || MineOf(ore) is { } oreMine && _noSpot.Contains(oreMine))
                yield return (ore.ToString(), c => c.Ore[(int)ore] > 0);
        // Free building land is a resource too: towards the cells with the most of it.
        bool roomy(MapSurvey.Cell c) => c.Buildable >= MapSurvey.CellSize * MapSurvey.CellSize * 2 / 3;
        if (own.Buildable < _config.MinFreeLand)
            yield return ("room", roomy);
        if (own.StoneUnits >= _config.MinStoneUnits && own.StoneUnits < _config.WantedStoneUnits)
            yield return ("stone", c => c.StoneUnits >= 10);
        if (own.Trees < _config.MinTrees)
            yield return ("trees", c => c.Trees >= 10);
        if (own.River == 0 || _noSpot.Contains("waterworks"))
            yield return ("water", c => c.River > 0);
        // Rice wants swampy ground (Asians' mana).
        if (own.Swamp < 20 && _config.BuildOrder.Any(b => b.Building == "rice_farm"))
            yield return ("swamp", c => c.Swamp >= 20);
        if (_lacksRoom)
            yield return ("room", roomy);
        // Gold (gems) for the combat strength: late, once the build order up to its mine is done.
        foreach (var ore in _config.LateOres)
            if (MineOf(ore) is { } oreMine && (own.Ore[(int)ore] == 0 || _noSpot.Contains(oreMine)) && ReachedBefore(oreMine, mine))
                yield return (ore.ToString(), c => c.Ore[(int)ore] > 0);
        // Nothing lacking: soldiers to spare still take land, as a player would.
        if (spareSoldiers >= _config.SpareSoldiersForLand)
            yield return ("land", roomy);
    }

    /// <summary>The mine that digs the ore, if any.</summary>
    private string? MineOf(ResourceType ore) => _world.Production.Config.Mines.FirstOrDefault(m => m.Value.Resource == ore).Key;

    /// <summary>Center of the nearest cell nobody owns that has what is wanted and can be walked to.</summary>
    private GridPoint? NearestUnclaimed(Func<MapSurvey.Cell, bool> wanted)
    {
        var home = _home!;
        var pathFinder = _world.PathFinder!;
        return _survey.Cells
            .Where(c => c.Own == 0 && c.Foreign == 0 && c.Anchor is not null && wanted(c))
            .OrderBy(c => PathFinder.Distance(home.Position, c.Center))
            .Where(c => pathFinder.AreConnected(home.Door, c.Anchor!.Value))
            .Select(c => (GridPoint?)c.Center)
            .FirstOrDefault();
    }

    /// <summary>
    /// A tower on the own-land spot nearest the target, if that gets the land closer to it. Spots whose new
    /// land would be mostly water are passed over (<see cref="AiConfig.MaxWaterShare"/>); towards a
    /// <paramref name="resource"/> the spot with the least water is taken if every spot has too much.
    /// </summary>
    private Building? PlaceTower(BuildingDefinition tower, GridPoint target, List<Building> mine, bool resource)
    {
        var map = _world.Map!;
        int closest = mine.Where(IsMilitary).Select(b => PathFinder.Distance(b.Position, target)).DefaultIfEmpty(int.MaxValue).Min();
        int radius = (int)_world.Military.Config.Territory.GetValueOrDefault(tower.Name, 20);
        GridPoint? leastWater = null;
        double leastShare = double.MaxValue;
        foreach (var p in TowerCandidates(target))
        {
            if (PathFinder.Distance(p, target) + 6 > closest)
                break;
            if (!map.Contains(p) || map.OwnerAt(p) != Player || !_world.CanPlace(tower, p, out _, Player)
                || !_world.Reachable(Player, _home!.Door, p + tower.Door) || OnFields(tower, p))
                continue;
            double share = WaterShare(p, radius);
            if (share <= _config.MaxWaterShare)
                return _world.PlaceConstructionSite(tower, Player, p);
            if (share < leastShare)
                (leastWater, leastShare) = (p, share);
        }
        if (resource && leastWater is { } spot)
        {
            Note($"tower at {spot.X},{spot.Y} takes {leastShare:P0} water");
            return _world.PlaceConstructionSite(tower, Player, spot);
        }
        return null;
    }

    /// <summary>
    /// Points nearest the target first: the spiral around it, then, for targets farther than the spiral
    /// reaches, the own land beyond it, so the towers walk towards a far target step by step.
    /// </summary>
    private IEnumerable<GridPoint> TowerCandidates(GridPoint target)
    {
        // The circle holds every point up to this grid distance (and some beyond): sorted by distance throughout.
        var origin = new GridPoint(0, 0);
        foreach (var offset in Spiral.TakeWhile(o => PathFinder.Distance(origin, o) <= TowerSearchRadius))
            yield return target + offset;
        var map = _world.Map!;
        var beyond = new List<GridPoint>();
        for (int y = 0; y < map.Size; y++)
        for (int x = 0; x < map.Size; x++)
        {
            var p = new GridPoint(x, y);
            if (map.OwnerAt(p) == Player && PathFinder.Distance(p, target) > TowerSearchRadius)
                beyond.Add(p);
        }
        foreach (var p in beyond.OrderBy(p => PathFinder.Distance(p, target)))
            yield return p;
    }

    /// <summary>Share of water among the unclaimed points within <paramref name="radius"/> (every other point sampled).</summary>
    private double WaterShare(GridPoint center, int radius)
    {
        var map = _world.Map!;
        int unclaimed = 0, water = 0;
        foreach (var offset in TerritorySample(radius))
        {
            var p = center + offset;
            if (!map.Contains(p) || map.OwnerAt(p) is not null)
                continue;
            unclaimed++;
            if (IsWater(p))
                water++;
        }
        return unclaimed == 0 ? 0 : (double)water / unclaimed;
    }

    private static readonly Dictionary<int, GridPoint[]> TerritorySamples = new();

    private static GridPoint[] TerritorySample(int radius)
    {
        lock (TerritorySamples)
        {
            if (!TerritorySamples.TryGetValue(radius, out var sample))
                TerritorySamples[radius] = sample = PathFinder.Circle(new GridPoint(0, 0), radius)
                    .Where(o => (o.X & 1) == 0 && (o.Y & 1) == 0).ToArray();
            return sample;
        }
    }

    private static bool IsMilitary(Building b) => b.Definition.Occupiers.Count > 0;

    /// <summary>Notes a problem once, until the building can be built again.</summary>
    private void NoteOnce(string key, string text)
    {
        if (_reported.Add(key))
            Note(text);
    }

    internal void Note(string text)
    {
        long seconds = _world.Tick / World.TicksPerSecond;
        string line = $"{seconds / 60}:{seconds % 60:00} {text}";
        _log.Add(line);
        if (_log.Count > MaxLog)
            _log.RemoveAt(0);
        Noted?.Invoke(this, line);
    }
}
