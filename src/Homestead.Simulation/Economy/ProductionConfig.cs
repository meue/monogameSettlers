using System.Text.Json;
using Homestead.Simulation.Map;

namespace Homestead.Simulation.Economy;

public enum GatherAction
{
    CutTree,
    CutStone,
    PlantTree,
    Fish,
    DrawWater,

    /// <summary>Harvest a ripe field of the gatherer's crop (grain, vines, rice, beehives), otherwise sow a new one.</summary>
    Farm,
}

/// <summary>
/// Something a farm-like gatherer sows and harvests: object codes <see cref="First"/> (just sown) up to
/// <see cref="Ripe"/>, growing in <see cref="GrowTicks"/> on the given <see cref="Ground"/>. A crop that
/// <see cref="Regrows"/> starts over after the harvest (vines, beehives) instead of leaving free ground.
/// With <see cref="SlopeBonus"/> it grows faster on slopes: up to (1 + bonus) times as fast where the
/// height to a neighbor differs by <see cref="SlopeHeight"/> or more. With <see cref="TreeBonus"/> it grows faster near trees:
/// up to (1 + bonus) times as fast with <see cref="IdealTrees"/> adult trees within <see cref="TreeRadius"/> (beehives); its
/// gatherer also puts new fields where the most trees are.
/// </summary>
/// <param name="Farmland">Sowing turns the ground into farmland, which stays until the farm is gone.</param>
/// <param name="FarmlandRadius">Sowing also turns the grass this many steps around into farmland (where it fits).</param>
/// <param name="MaxFields">Fields one building keeps at most (beehives); 0: no limit.</param>
/// <param name="FieldSpacing">A new field at least this many steps from every other of the crop (beehives); 0: fields may touch.</param>
public sealed record CropRecipe(byte First, byte Ripe, int GrowTicks, IReadOnlySet<TerrainType> Ground, bool Regrows = false,
    double SlopeBonus = 0, int SlopeHeight = 4, bool Farmland = false, int FarmlandRadius = 0, int MaxFields = 0,
    double TreeBonus = 0, int TreeRadius = 0, int IdealTrees = 10, int FieldSpacing = 0)
{
    public bool Contains(byte code) => code >= First && code <= Ripe;

    public static readonly CropRecipe Grain = new(MapObjectCodes.FirstGrain, MapObjectCodes.RipeGrain, 200 * World.TicksPerSecond,
        new HashSet<TerrainType> { TerrainType.Grass });
}

public sealed record WorkshopRecipe(IReadOnlyList<Material> Inputs, IReadOnlyList<Material> Outputs, int Ticks);

/// <param name="PickupTicks">Pause between finishing the work and picking up the result (the woodcutter after felling).</param>
/// <param name="Crop">Farm: what is sown and harvested (see <see cref="ProductionConfig.Crops"/>).</param>
/// <param name="SowTicks">Farm: how long sowing takes (beehives: one round of setting one up); 0: <paramref name="WorkTicks"/>.</param>
public sealed record GathererRecipe(GatherAction Action, Material? Output, int WorkTicks, int RestTicks, int PickupTicks = 0,
    string Crop = "grain", int SowTicks = 0);

/// <summary>
/// A mine: takes ore from the ground within <see cref="Radius"/> of the building. A unit of food (any
/// of the building's input piles, they differ per civilization) lasts <see cref="PreferredRuns"/>
/// trips into the mine if it is the <see cref="PreferredFood"/>, otherwise <see cref="OtherRuns"/>.
/// </summary>
public sealed record MineRecipe(ResourceType Resource, Material Output, int Ticks, int Radius,
    Material? PreferredFood = null, int PreferredRuns = 1, int OtherRuns = 1)
{
    public int RunsFor(Material food) => food == PreferredFood ? PreferredRuns : OtherRuns;
}

/// <summary>
/// A smith: like a workshop, but it chooses one of several <see cref="Products"/> per cycle (see
/// <see cref="Production"/>: products ordered by the player first, then tools that are missing, otherwise
/// by the player's shares while a product has fewer than <see cref="KeepInStock"/> units around – 0: no limit).
/// </summary>
/// <param name="Shares">Default share per product (0–1, Waren › Herstellung); null: 1 for every product.</param>
public sealed record SmithRecipe(IReadOnlyList<Material> Inputs, IReadOnlyList<Material> Products, int Ticks, int KeepInStock,
    IReadOnlyDictionary<Material, double>? Shares = null)
{
    public double ShareOf(Material product) => Shares is null ? 1 : Shares.GetValueOrDefault(product);
}

/// <summary>Production balancing, loaded from <c>data/production.json</c>. Keyed by building name.</summary>
public sealed record ProductionConfig
{
    public required IReadOnlyDictionary<string, WorkshopRecipe> Workshops { get; init; }
    public required IReadOnlyDictionary<string, GathererRecipe> Gatherers { get; init; }
    public IReadOnlyDictionary<string, MineRecipe> Mines { get; init; } = new Dictionary<string, MineRecipe>();
    public IReadOnlyDictionary<string, SmithRecipe> Smiths { get; init; } = new Dictionary<string, SmithRecipe>();

    /// <summary>
    /// The tool a carrier must pick up to become this trade (keys: worker names of the building data,
    /// plus DIGGER and BRICKLAYER). Trades not listed need none.
    /// </summary>
    public IReadOnlyDictionary<string, Material> Tools { get; init; } = new Dictionary<string, Material>();
    public required int TreeGrowTicks { get; init; }

    /// <summary>A felled tree's stump stays this long, then the field is free (for the forester) again.</summary>
    public int StumpTicks { get; init; } = 60 * World.TicksPerSecond;
    public required int StonesPerCut { get; init; }

    /// <summary>Crops of the farm-like gatherers by name (grain, wine, rice, honey).</summary>
    public IReadOnlyDictionary<string, CropRecipe> Crops { get; init; } = new Dictionary<string, CropRecipe> { ["grain"] = CropRecipe.Grain };

    /// <summary>The crop a field code belongs to, if any.</summary>
    public CropRecipe? CropOf(byte code) => Crops.Values.FirstOrDefault(c => c.Contains(code));

    /// <summary>A copy with another grow time for one crop (tests).</summary>
    public ProductionConfig WithCropGrowTicks(string crop, int ticks) =>
        this with { Crops = Crops.ToDictionary(c => c.Key, c => c.Key == crop ? c.Value with { GrowTicks = ticks } : c.Value) };

    /// <summary>Ore units a mine gets per amount step of a field (original maps: 0-15 per field).</summary>
    public int OrePerAmount { get; init; } = 1;

    /// <summary>
    /// Amount at which a field counts as full for a mine's chance (<see cref="Production.MineChance"/>):
    /// ore left around the mine against this amount on every field there. 0: every run hits while ore is left.
    /// </summary>
    public int FullOreAmount { get; init; }

    /// <summary>Fish a fisher gets per amount step of a water field.</summary>
    public int FishPerAmount { get; init; } = 1;

    /// <summary>Every this many ticks a depleted field regains one amount step (up to its start value); 0 = never.</summary>
    public int RegenerateTicks { get; init; }

    /// <summary>
    /// Percent of the units taken that use up the ground: quarry stone, everything in mountains (ores,
    /// coal, gems, sulfur) and fish. 100 as in the original, 0 = never runs out.
    /// </summary>
    public Depletion Depletion { get; init; } = new(100, 100, 100);

    /// <summary>Share of its construction material a building leaves when it comes down (sites: of what was built in).</summary>
    public double DemolitionPayback { get; init; } = 0.5;

    /// <summary>
    /// Mean seconds until a point of a fallen building's levelled ground at the edge grows back over, see
    /// <see cref="Buildings.Construction"/> (0 from a game saved before there was this value: <see cref="DefaultRegrowSeconds"/>).
    /// </summary>
    public int RegrowSeconds { get; init; } = DefaultRegrowSeconds;

    public const int DefaultRegrowSeconds = 20;

    /// <summary>How geologists search (see <see cref="Geology"/>).</summary>
    public GeologistRules Geologists { get; init; } = new(10, 3 * World.TicksPerSecond, 2 * World.TicksPerSecond,
        4 * 60 * World.TicksPerSecond, 9 * 60 * World.TicksPerSecond);

    /// <summary>Donkey farms, markets and donkeys (see <see cref="Trade"/>); null in games saved before there were any: <see cref="TradeRules.Default"/>.</summary>
    public TradeRules? Trade { get; init; }

    /// <summary>Shipyards and docks (see <see cref="Shipping"/>); null in games saved before there were any: <see cref="ShippingRules.Default"/>.</summary>
    public ShippingRules? Shipping { get; init; }

    /// <summary>Walking speed by ground and how trails form (see <see cref="Trails"/>); <see cref="WalkingRules.Plain"/>: same speed everywhere, no trails.</summary>
    public WalkingRules Walking { get; init; } = WalkingRules.Plain;

    /// <summary>Units of food one food pile of a mine holds.</summary>
    public int MineFoodPileCapacity { get; init; } = Materials.MaxPileSize;

    /// <summary>Trips into a new mine that cost no food (once per building: a rebuilt mine gets them again).</summary>
    public int MineFreeRuns { get; init; }

    /// <summary>Units one storehouse pile holds (a storehouse has six piles).</summary>
    public int StoragePileCapacity { get; init; } = Materials.MaxPileSize;

    /// <summary>Share of a player's carriers and specialists that always stays carriers (0 = none).</summary>
    public double KeptCarriers { get; init; }

    /// <summary>Most diggers a player has, as a share of its carriers and specialists (1 = no limit; at least one is always allowed).</summary>
    public double MaxDiggers { get; init; } = 1;

    /// <summary>Most builders a player has, as a share like <see cref="MaxDiggers"/>.</summary>
    public double MaxBuilders { get; init; } = 1;

    /// <summary>Fields kept free between a new building and the surroundings of the others.</summary>
    public int BuildingMargin { get; init; }

    /// <summary>Levelling: the greatest height difference between neighbors it leaves (or does not make worse).</summary>
    public int LevellingMaxSlope { get; init; } = 8;

    /// <summary>Levelling: height a digger moves a point by per work cycle.</summary>
    public int LevellingHeightPerDig { get; init; } = 2;

    /// <summary>Levelling: the most height a site may need moved in all; more and it cannot be built.</summary>
    public int LevellingMaxWork { get; init; } = 300;

    /// <summary>
    /// Materials carriers bring to storehouses (null = all). Everything else stays at its producer's
    /// output pile until somebody needs it.
    /// </summary>
    public IReadOnlySet<Material>? StoredMaterials { get; init; }

    public bool IsStored(Material material) => StoredMaterials?.Contains(material) != false;

    /// <summary>Default order in which carriers take goods (Waren › Warentransport); goods not listed come last.</summary>
    public IReadOnlyList<Material> TransportOrder { get; init; } = [];

    /// <summary>
    /// Default distribution of a material among the targets that use it (shares 0–1 by building name,
    /// <see cref="Logistics.Construction"/> for construction sites; like the original's food for mines).
    /// A material listed here gives unlisted targets 0; a material not listed gives every target 1.
    /// The player changes them in Waren › Verteilung (<see cref="Logistics.ShareOf"/>).
    /// </summary>
    public IReadOnlyDictionary<Material, IReadOnlyDictionary<string, double>> Distribution { get; init; } =
        new Dictionary<Material, IReadOnlyDictionary<string, double>>();

    /// <summary>Carriers a living house releases once it is finished (by building name).</summary>
    public IReadOnlyDictionary<string, int> Residents { get; init; } = new Dictionary<string, int>();

    /// <summary>Time between two residents leaving the same house.</summary>
    public int ResidentIntervalTicks { get; init; } = World.TicksPerSecond;

    /// <summary>No production at all (worlds without workers, logistics tests).</summary>
    public static ProductionConfig None { get; } = new()
    {
        Workshops = new Dictionary<string, WorkshopRecipe>(),
        Gatherers = new Dictionary<string, GathererRecipe>(),
        TreeGrowTicks = 1,
        StonesPerCut = 1,
    };

    public static ProductionConfig Load(string? dataDirectory = null)
    {
        string path = Path.Combine(dataDirectory ?? Buildings.BuildingCatalog.DefaultDataDirectory, "production.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        static int Ticks(JsonElement e, string name) => (int)Math.Round(e.GetProperty(name).GetDouble() * World.TicksPerSecond);
        static Material Material(JsonElement e) => Materials.Parse(e.GetString()!)!.Value;
        static IReadOnlyList<Material> MaterialList(JsonElement e) => e.EnumerateArray().Select(Material).ToArray();

        var workshops = root.GetProperty("workshops").EnumerateObject().ToDictionary(
            w => w.Name,
            w => new WorkshopRecipe(MaterialList(w.Value.GetProperty("inputs")), MaterialList(w.Value.GetProperty("outputs")),
                Ticks(w.Value, "seconds")));
        var gatherers = root.GetProperty("gatherers").EnumerateObject().ToDictionary(
            g => g.Name,
            g => new GathererRecipe(
                Enum.Parse<GatherAction>(g.Value.GetProperty("action").GetString()!),
                g.Value.TryGetProperty("output", out var output) ? Materials.Parse(output.GetString()!) : null,
                Ticks(g.Value, "workSeconds"), Ticks(g.Value, "restSeconds"),
                g.Value.TryGetProperty("pickupSeconds", out _) ? Ticks(g.Value, "pickupSeconds") : 0,
                g.Value.TryGetProperty("crop", out var crop) ? crop.GetString()! : "grain",
                g.Value.TryGetProperty("sowSeconds", out _) ? Ticks(g.Value, "sowSeconds") : 0));
        var codes = new Dictionary<string, (byte First, byte Ripe)>
        {
            ["grain"] = (MapObjectCodes.FirstGrain, MapObjectCodes.RipeGrain),
            ["wine"] = (MapObjectCodes.FirstWine, MapObjectCodes.RipeWine),
            ["rice"] = (MapObjectCodes.FirstRice, MapObjectCodes.RipeRice),
            ["honey"] = (MapObjectCodes.FirstHive, MapObjectCodes.FullHive),
        };
        var crops = root.GetProperty("crops").EnumerateObject().Where(c => !c.Name.StartsWith('_')).ToDictionary(
            c => c.Name,
            c => new CropRecipe(codes[c.Name].First, codes[c.Name].Ripe, Ticks(c.Value, "growSeconds"),
                c.Value.GetProperty("ground").EnumerateArray().Select(t => Enum.Parse<TerrainType>(t.GetString()!)).ToHashSet(),
                c.Value.TryGetProperty("regrows", out var regrows) && regrows.GetBoolean(),
                c.Value.TryGetProperty("slopeBonus", out var slope) ? slope.GetDouble() : 0,
                c.Value.TryGetProperty("slopeHeight", out var slopeHeight) ? slopeHeight.GetInt32() : 4,
                c.Value.TryGetProperty("farmland", out var farmland) && farmland.GetBoolean(),
                c.Value.TryGetProperty("farmlandRadius", out var farmlandRadius) ? farmlandRadius.GetInt32() : 0,
                c.Value.TryGetProperty("maxFields", out var maxFields) ? maxFields.GetInt32() : 0,
                c.Value.TryGetProperty("treeBonus", out var treeBonus) ? treeBonus.GetDouble() : 0,
                c.Value.TryGetProperty("treeRadius", out var treeRadius) ? treeRadius.GetInt32() : 0,
                c.Value.TryGetProperty("idealTrees", out var idealTrees) ? idealTrees.GetInt32() : 10,
                c.Value.TryGetProperty("fieldSpacing", out var fieldSpacing) ? fieldSpacing.GetInt32() : 0));
        var mineFood = root.GetProperty("mineFood");
        var mines = root.GetProperty("mines").EnumerateObject().Where(m => !m.Name.StartsWith('_')).ToDictionary(
            m => m.Name,
            m => new MineRecipe(
                Enum.Parse<ResourceType>(m.Value.GetProperty("resource").GetString()!),
                Material(m.Value.GetProperty("output")),
                Ticks(m.Value, "seconds"),
                m.Value.GetProperty("radius").GetInt32(),
                m.Value.TryGetProperty("prefersFood", out var preferred) ? Material(preferred) : null,
                mineFood.GetProperty("preferredRuns").GetInt32(),
                mineFood.GetProperty("otherRuns").GetInt32()));
        var smiths = root.GetProperty("smiths").EnumerateObject().Where(m => !m.Name.StartsWith('_')).ToDictionary(
            m => m.Name,
            m => new SmithRecipe(MaterialList(m.Value.GetProperty("inputs")), MaterialList(m.Value.GetProperty("products")),
                Ticks(m.Value, "seconds"), m.Value.GetProperty("keepInStock").GetInt32(),
                m.Value.TryGetProperty("shares", out var shares)
                    ? shares.EnumerateObject().ToDictionary(s => Materials.Parse(s.Name)!.Value, s => s.Value.GetDouble())
                    : null));
        var tools = root.GetProperty("tools").EnumerateObject()
            .Where(t => !t.Name.StartsWith('_'))
            .ToDictionary(t => t.Name, t => Material(t.Value));
        var resources = root.GetProperty("resources");
        var housing = root.GetProperty("housing");

        return new ProductionConfig
        {
            Workshops = workshops,
            Gatherers = gatherers,
            Mines = mines,
            Distribution = root.GetProperty("distribution").EnumerateObject().Where(d => !d.Name.StartsWith('_')).ToDictionary(
                d => Materials.Parse(d.Name)!.Value,
                d => (IReadOnlyDictionary<string, double>)d.Value.EnumerateObject().Where(w => !w.Name.StartsWith('_')).ToDictionary(w => w.Name, w => w.Value.GetDouble())),
            Smiths = smiths,
            Tools = tools,
            StoragePileCapacity = root.GetProperty("storage").GetProperty("pileCapacity").GetInt32(),
            MineFreeRuns = mineFood.TryGetProperty("freeRuns", out var freeRuns) ? freeRuns.GetInt32() : 0,
            MineFoodPileCapacity = mineFood.TryGetProperty("pileCapacity", out var foodPile) ? foodPile.GetInt32() : Materials.MaxPileSize,
            KeptCarriers = root.GetProperty("recruitment").GetProperty("keptCarriers").GetDouble(),
            MaxDiggers = root.GetProperty("recruitment").TryGetProperty("maxDiggers", out var maxDiggers) ? maxDiggers.GetDouble() : 1,
            MaxBuilders = root.GetProperty("recruitment").TryGetProperty("maxBuilders", out var maxBuilders) ? maxBuilders.GetDouble() : 1,
            BuildingMargin = root.GetProperty("placement").GetProperty("margin").GetInt32(),
            LevellingMaxSlope = root.GetProperty("levelling").GetProperty("maxSlope").GetInt32(),
            LevellingHeightPerDig = root.GetProperty("levelling").GetProperty("heightPerDig").GetInt32(),
            LevellingMaxWork = root.GetProperty("levelling").GetProperty("maxWork").GetInt32(),
            StoredMaterials = root.GetProperty("storage").GetProperty("materials").EnumerateArray()
                .Select(m => Materials.Parse(m.GetString()!)!.Value).ToHashSet(),
            TransportOrder = root.GetProperty("storage").TryGetProperty("transportOrder", out var order)
                ? order.EnumerateArray().Select(m => Materials.Parse(m.GetString()!)!.Value).ToList()
                : [],
            Residents = housing.GetProperty("residents").EnumerateObject().ToDictionary(r => r.Name, r => r.Value.GetInt32()),
            ResidentIntervalTicks = Ticks(housing, "intervalSeconds"),
            TreeGrowTicks = Ticks(root.GetProperty("trees"), "growSeconds"),
            StumpTicks = root.GetProperty("trees").TryGetProperty("stumpSeconds", out _) ? Ticks(root.GetProperty("trees"), "stumpSeconds") : 60 * World.TicksPerSecond,
            Crops = crops,
            StonesPerCut = root.GetProperty("stones").GetProperty("stonesPerCut").GetInt32(),
            OrePerAmount = resources.GetProperty("orePerAmount").GetInt32(),
            FullOreAmount = resources.TryGetProperty("fullAmount", out var fullAmount) ? fullAmount.GetInt32() : 0,
            FishPerAmount = resources.GetProperty("fishPerAmount").GetInt32(),
            RegenerateTicks = Ticks(resources, "regenerateSeconds"),
            Geologists = root.TryGetProperty("geologists", out var geologists)
                ? new GeologistRules(geologists.GetProperty("searchRadius").GetInt32(), Ticks(geologists, "surveySeconds"),
                    Ticks(geologists, "signSeconds"), (int)(geologists.GetProperty("signMinutes")[0].GetDouble() * 60 * World.TicksPerSecond),
                    (int)(geologists.GetProperty("signMinutes")[1].GetDouble() * 60 * World.TicksPerSecond))
                : new GeologistRules(10, 3 * World.TicksPerSecond, 2 * World.TicksPerSecond, 4 * 60 * World.TicksPerSecond, 9 * 60 * World.TicksPerSecond),
            Walking = root.TryGetProperty("walking", out var walking)
                ? new WalkingRules(walking.GetProperty("trailSpeed").GetDouble(), walking.GetProperty("mountainSpeed").GetDouble(),
                    walking.GetProperty("trailSteps").GetInt32(), Ticks(walking, "trailFadeSeconds"))
                : WalkingRules.Plain,
            Trade = root.TryGetProperty("trade", out var trade)
                ? new TradeRules(trade.GetProperty("donkeyFarm").GetString()!, MaterialList(trade.GetProperty("farmInputs")), Ticks(trade, "farmSeconds"),
                    trade.GetProperty("keepIdle").GetInt32(), trade.GetProperty("market").GetString()!, trade.GetProperty("load").GetInt32(),
                    Ticks(trade, "loadSeconds"), trade.GetProperty("sight").GetInt32())
                {
                    Harbor = trade.TryGetProperty("harbor", out var harbor) ? harbor.GetString()! : "harbor",
                }
                : TradeRules.Default,
            Shipping = root.TryGetProperty("shipping", out var shipping)
                ? new ShippingRules(shipping.GetProperty("dockyard").GetString()!, shipping.GetProperty("dockLength").GetInt32(),
                    shipping.GetProperty("dockDistance").GetInt32())
                {
                    Costs = shipping.GetProperty("ships").EnumerateObject().ToDictionary(
                        k => Enum.Parse<ShipKind>(k.Name, ignoreCase: true),
                        k => (IReadOnlyDictionary<Material, int>)k.Value.GetProperty("cost").EnumerateObject()
                            .ToDictionary(c => Materials.Parse(c.Name)!.Value, c => c.Value.GetInt32())),
                    BuildTicks = Ticks(shipping, "buildSeconds"),
                    ShipStepTicks = Ticks(shipping, "stepSeconds"),
                    ShipSight = shipping.GetProperty("sight").GetInt32(),
                    Passengers = shipping.GetProperty("ships").EnumerateObject().ToDictionary(
                        k => Enum.Parse<ShipKind>(k.Name, ignoreCase: true),
                        k => k.Value.TryGetProperty("passengers", out var passengers) ? passengers.GetInt32() : 0),
                    LandingDistance = shipping.GetProperty("landingDistance").GetInt32(),
                    CargoPiles = shipping.GetProperty("cargoPiles").GetInt32(),
                    CargoPileSize = shipping.GetProperty("pileSize").GetInt32(),
                    CargoTicks = Ticks(shipping, "loadSeconds"),
                    LoadWaitTicks = Ticks(shipping, "loadWaitSeconds"),
                }
                : ShippingRules.Default,
            DemolitionPayback = root.TryGetProperty("demolition", out var demolition) ? demolition.GetProperty("payback").GetDouble() : 0.5,
            RegrowSeconds = root.TryGetProperty("demolition", out var regrow) && regrow.TryGetProperty("regrowSeconds", out var regrowSeconds)
                ? regrowSeconds.GetInt32() : DefaultRegrowSeconds,
            Depletion = root.TryGetProperty("depletion", out var depletion)
                ? new Depletion(depletion.GetProperty("stone").GetInt32(), depletion.GetProperty("mountain").GetInt32(), depletion.GetProperty("fish").GetInt32())
                : new Depletion(100, 100, 100),
        };
    }
}

/// <summary>Percent of the units taken that use up the ground, per kind of resource (see <see cref="ProductionConfig.Depletion"/>).</summary>
public sealed record Depletion(int Stone, int Mountain, int Fish);

/// <summary>
/// Geologists search fields within <paramref name="SearchRadius"/> of the point they were sent to, each
/// for <paramref name="SurveyTicks"/> plus <paramref name="SignTicks"/>; a sign stays between
/// <paramref name="SignMinTicks"/> and <paramref name="SignMaxTicks"/>.
/// </summary>
public sealed record GeologistRules(int SearchRadius, int SurveyTicks, int SignTicks, int SignMinTicks, int SignMaxTicks);

/// <summary>
/// Land trade: the <paramref name="DonkeyFarm"/> makes a donkey of its <paramref name="FarmInputs"/> every
/// <paramref name="FarmTicks"/> while the player has fewer than <paramref name="KeepIdle"/> donkeys without a job;
/// a <paramref name="Market"/>'s donkey carries up to <paramref name="Load"/> units, each taking <paramref name="LoadTicks"/>
/// to load or unload. A donkey sees <paramref name="Sight"/> fields (fog of war; little, so it is no scout).
/// </summary>
public sealed record TradeRules(string DonkeyFarm, IReadOnlyList<Material> FarmInputs, int FarmTicks, int KeepIdle, string Market, int Load,
    int LoadTicks, int Sight)
{
    public static TradeRules Default { get; } = new("donkey_farm", [Material.Crop, Material.Water], 20 * World.TicksPerSecond, 4, "market_place", 2,
        World.TicksPerSecond / 2, 3);

    /// <summary>The building trading over the sea with trade ships (<see cref="Shipping"/>).</summary>
    public string Harbor { get; init; } = "harbor";
}

/// <summary>
/// Shipping: a <paramref name="Dockyard"/>'s dock is a pier <paramref name="DockLength"/> fields long from a shore field
/// at most <paramref name="DockDistance"/> from the shipyard.
/// </summary>
public sealed record ShippingRules(string Dockyard, int DockLength, int DockDistance)
{
    public static ShippingRules Default { get; } = new("dockyard", 4, 20);

    /// <summary>The material of each kind of ship.</summary>
    public IReadOnlyDictionary<ShipKind, IReadOnlyDictionary<Material, int>> Costs { get; init; } =
        new Dictionary<ShipKind, IReadOnlyDictionary<Material, int>>
        {
            [ShipKind.Ferry] = new Dictionary<Material, int> { [Material.Plank] = 6, [Material.Iron] = 2 },
            [ShipKind.TradeShip] = new Dictionary<Material, int> { [Material.Plank] = 8, [Material.Iron] = 3 },
        };

    /// <summary>How long the worker hammers one unit into the hull.</summary>
    public int BuildTicks { get; init; } = 4 * World.TicksPerSecond;

    /// <summary>Ticks a ship takes from one field to the next.</summary>
    public int ShipStepTicks { get; init; } = 10;

    /// <summary>How far a ship sees (fog of war).</summary>
    public int ShipSight { get; init; } = 16;

    /// <summary>How many units each kind of ship takes aboard.</summary>
    public IReadOnlyDictionary<ShipKind, int> Passengers { get; init; } = new Dictionary<ShipKind, int> { [ShipKind.Ferry] = 7 };

    /// <summary>Land at most this far from a ship is near enough to board it and to unload it.</summary>
    public int LandingDistance { get; init; } = 6;

    /// <summary>Piles of goods a trade ship carries (each of one good).</summary>
    public int CargoPiles { get; init; } = 3;

    /// <summary>Units in a pile a trade ship carries.</summary>
    public int CargoPileSize { get; init; } = 8;

    /// <summary>Ticks to load a unit onto a trade ship (unloading puts a whole pile down in that time).</summary>
    public int CargoTicks { get; init; } = World.TicksPerSecond / 2;

    /// <summary>How long a trade ship not yet full waits at the harbor for more goods the harbor still wants.</summary>
    public int LoadWaitTicks { get; init; } = 15 * World.TicksPerSecond;
}

/// <summary>
/// Walking speed by ground: <paramref name="TrailSpeed"/> times as fast on a trail, <paramref name="MountainSpeed"/>
/// times on mountains. A trail forms on grass where carriers at work stepped <paramref name="TrailSteps"/> times
/// (0: never), each step less than <paramref name="TrailFadeTicks"/> after the one before; unused that long, it fades.
/// </summary>
public sealed record WalkingRules(double TrailSpeed, double MountainSpeed, int TrailSteps, int TrailFadeTicks)
{
    public static WalkingRules Plain { get; } = new(1, 1, 0, 0);
}
