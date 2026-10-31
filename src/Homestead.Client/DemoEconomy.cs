using Homestead.Simulation;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Map;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Client;

/// <summary>
/// Development helper: sets up a small economy (storehouse, a few workshops, goods and tools lying
/// around, carriers) to watch the logistics at work. Carriers become specialists by picking up the
/// tools. Not the game's start equipment.
/// </summary>
public static class DemoEconomy
{
    private static readonly string[] BuildingNames = ["stock", "sawmill", "lumberjack", "stonecutter", "forester", "farm", "mill", "baker", "toolsmith"];

    /// <summary>Tools for the demo's workers plus a few diggers and builders.</summary>
    private static readonly (Material Tool, int Count)[] Tools =
    [
        (Material.Axe, 1), (Material.Pick, 4), (Material.Saw, 1), (Material.Scythe, 1), (Material.FishingRod, 1),
        (Material.Hammer, 4), (Material.Blade, 3), (Material.Iron, 4), (Material.Coal, 4),
    ];
    private static readonly Material[] Goods = [Material.Trunk, Material.Crop, Material.Flour, Material.Water, Material.Stone, Material.Plank,
        Material.Bread, Material.Fish];

    /// <summary>Buildings that only make sense next to water or on top of their resource.</summary>
    private static readonly (string Name, ResourceType? Resource)[] PlacedAtResources =
    [
        ("waterworks", null), ("fisher", ResourceType.Fish), ("coalmine", ResourceType.Coal),
        ("ironmine", ResourceType.IronOre), ("goldmine", ResourceType.GoldOre),
    ];

    public static string Create(World world, BuildingCatalog catalog, GridPoint near, Random random, int carriers = 24,
        int diggers = 0, int builders = 0, int player = 0)
    {
        int placed = 0;
        foreach (string name in BuildingNames)
            if (PlaceNear(world, catalog[name], near, random, player: player) is not null)
                placed++;
        foreach (var (name, resource) in PlacedAtResources)
            if (NearestResource(world.Map!, near, resource) is { } spot && PlaceNear(world, catalog[name], spot, random, 2, player) is not null)
                placed++;

        int piles = 0;
        for (int attempt = 0; attempt < 200 && piles < 10; attempt++)
        {
            var at = new GridPoint(near.X + random.Next(-25, 26), near.Y + random.Next(-25, 26));
            if (world.Map!.Contains(at) && world.AddLooseGoods(at, Goods[random.Next(Goods.Length)], random.Next(3, 9)) is not null)
                piles++;
        }

        foreach (var (tool, count) in Tools)
            for (int i = 0; i < 100; i++)
                if (world.AddLooseGoods(new GridPoint(near.X + random.Next(-8, 9), near.Y + random.Next(-8, 9)), tool, count) is not null)
                    break;

        // Construction material, so placed sites can be built.
        foreach (var material in new[] { Material.Plank, Material.Stone })
            for (int i = 0, added = 0; i < 100 && added < 4; i++)
                if (world.AddLooseGoods(new GridPoint(near.X + random.Next(-12, 13), near.Y + random.Next(-12, 13)), material, 8) is not null)
                    added++;

        int spawned = 0;
        foreach (var (profession, count) in new[] { (Profession.Carrier, carriers), (Profession.Digger, diggers), (Profession.Builder, builders) })
            for (int i = 0; i < count; i++)
                if (world.Spawn(new GridPoint(near.X + random.Next(-6, 7), near.Y + random.Next(-6, 7)), profession, player) is not null)
                    spawned++;

        world.Housing.SettleStartPopulation(); // the demo's settlers have room
        return $"demo economy: {placed} buildings, {piles} piles, {spawned} workers";
    }

    /// <summary>Nearest point (within 60 steps) with the resource, or nearest water if <paramref name="resource"/> is null.</summary>
    private static GridPoint? NearestResource(GameMap map, GridPoint near, ResourceType? resource)
    {
        for (int r = 1; r <= 60; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var p = new GridPoint(near.X + dx, near.Y + dy);
            if (!map.Contains(p) || PathFinder.Distance(near, p) != r)
                continue;
            bool fits = resource is { } type ? map.ResourceAmount(p, type) > 0 : map.TerrainAt(p) == TerrainType.Water;
            if (fits)
                return p;
        }
        return null;
    }

    /// <summary>Tries positions in growing rings around <paramref name="near"/> until the building fits.</summary>
    public static Building? PlaceNear(World world, BuildingDefinition definition, GridPoint near, Random random, int firstRing = 6, int player = 0)
    {
        for (int r = firstRing; r < 40; r += 2)
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var at = new GridPoint(near.X + random.Next(-r, r + 1), near.Y + random.Next(-r, r + 1));
            if (PathFinder.Distance(at, near) < r - 2)
                continue;
            // Keep the protected ring free as well, so piles and doors stay reachable.
            if (definition.Protected.Any(o => !world.Map!.IsWalkable(at + o)))
                continue;
            if (world.PlaceBuilding(definition, player, at) is { } building)
                return building;
        }
        return null;
    }
}
