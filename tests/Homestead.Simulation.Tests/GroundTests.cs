using Homestead.Assets;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Map;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Tests;

/// <summary>Walking speed by ground, trails trodden by carriers, farmland under grain.</summary>
public class GroundTests
{
    private static readonly BuildingCatalog Romans = BuildingCatalog.Load(Civilization.Roman);

    private static readonly WalkingRules Rules = new(TrailSpeed: 1.2, MountainSpeed: 0.8, TrailSteps: 3, TrailFadeTicks: 300 * World.TicksPerSecond);

    [Theory]
    [InlineData(GroundCover.Trail, TerrainType.Grass, 67)] // 10 steps of 8 / 1.2 ticks
    [InlineData(GroundCover.None, TerrainType.Mountain, 100)] // 10 steps of 8 / 0.8 ticks
    [InlineData(GroundCover.None, TerrainType.Grass, 80)]
    public void Settlers_walk_faster_on_trails_and_slower_on_mountains(GroundCover cover, TerrainType terrain, int expected)
    {
        var map = new GameMap(30);
        for (int i = 0; i < map.PointCount; i++)
        {
            map.SetTerrain(map.Point(i), terrain);
            map.SetCover(map.Point(i), cover);
        }
        var world = new World(map, ProductionConfig.None with { Walking = Rules });
        var settler = world.Spawn(new GridPoint(5, 5))!;
        Assert.True(world.WalkTo(settler, new GridPoint(15, 5)));

        int ticks = 0;
        while (settler.Destination is not null && ticks < 1000)
        {
            world.Step();
            ticks++;
        }

        Assert.Equal(new GridPoint(15, 5), settler.Position);
        Assert.InRange(ticks, expected - 1, expected + 1);
    }

    [Fact]
    public void Carriers_at_work_tread_trails_on_grass_which_fade_when_unused()
    {
        var map = new GameMap(60);
        var world = new World(map, ProductionConfig.None with { Walking = Rules });
        world.PlaceBuilding(Romans["sawmill"], 0, new GridPoint(30, 30));
        world.AddLooseGoods(new GridPoint(12, 14), Material.Trunk, 6);
        world.Spawn(new GridPoint(20, 40), Profession.Carrier);

        for (int t = 0; t < 3000; t++)
            world.Step();

        Assert.True(world.Trails.Count > 5, $"{world.Trails.Count} trail points");
        for (int t = 0; t < 310 * World.TicksPerSecond; t++)
            world.Step();
        Assert.Equal(0, world.Trails.Count);
        Assert.DoesNotContain(Enumerable.Range(0, map.PointCount), i => map.CoverAt(map.Point(i)) == GroundCover.Trail);
    }

    [Fact]
    public void No_trails_off_grass()
    {
        var map = new GameMap(60);
        for (int i = 0; i < map.PointCount; i++)
            map.SetTerrain(map.Point(i), TerrainType.Desert);
        var world = new World(map, ProductionConfig.None with { Walking = Rules });
        world.PlaceBuilding(Romans["sawmill"], 0, new GridPoint(30, 30), force: true);
        world.AddLooseGoods(new GridPoint(12, 14), Material.Trunk, 6);
        world.Spawn(new GridPoint(20, 40), Profession.Carrier);

        for (int t = 0; t < 3000; t++)
            world.Step();

        Assert.True(world.Logistics.Delivered > 0);
        Assert.Equal(0, world.Trails.Count);
    }

    [Fact]
    public void Sowing_grain_turns_the_grass_around_into_farmland_too()
    {
        var map = new GameMap(70);
        var world = new World(map, TestConfig.NoTools);
        world.PlaceBuilding(Romans["farm"], 0, new GridPoint(30, 20));
        world.Spawn(new GridPoint(45, 40), Profession.Carrier);

        for (int t = 0; t < 20_000 && world.Production.FieldsSown < 1; t++)
            world.Step();

        var field = Enumerable.Range(0, map.PointCount).Select(map.Point).Single(p => map.ObjectAt(p) == MapObjectKind.Field);
        var farmland = Enumerable.Range(0, map.PointCount).Select(map.Point).Where(p => map.CoverAt(p) == GroundCover.Farmland).ToList();
        Assert.Contains(field, farmland);
        Assert.True(farmland.Count > 1, "the grass next to the field becomes farmland too");
        Assert.All(farmland, p => Assert.InRange(PathFinder.Distance(field, p), 0, 1));
    }

    [Fact]
    public void Sown_grain_leaves_farmland_until_the_farm_is_torn_down()
    {
        var map = new GameMap(70);
        var world = new World(map, TestConfig.NoTools.WithCropGrowTicks("grain", 700));
        var farm = world.PlaceBuilding(Romans["farm"], 0, new GridPoint(30, 20))!;
        world.PlaceBuilding(Romans["stock"], 0, new GridPoint(55, 25));
        for (int i = 0; i < 3; i++)
            world.Spawn(new GridPoint(45 + i, 40), Profession.Carrier);

        for (int t = 0; t < 20_000 && world.Production.CropHarvested < 2; t++)
            world.Step();

        var farmland = Enumerable.Range(0, map.PointCount).Select(map.Point).Where(p => map.CoverAt(p) == GroundCover.Farmland).ToList();
        Assert.True(world.Production.CropHarvested >= 2);
        // Every field lies on farmland, which stays after the harvest and is walkable.
        var fields = Enumerable.Range(0, map.PointCount).Select(map.Point).Where(p => map.ObjectAt(p) == MapObjectKind.Field).ToList();
        Assert.All(fields, p => Assert.Equal(GroundCover.Farmland, map.CoverAt(p)));
        Assert.True(farmland.Count >= Math.Max(1, fields.Count), $"{farmland.Count} farmland, {fields.Count} fields");
        Assert.All(farmland, p => Assert.True(map.IsWalkable(p)));
        var center = farm.Position + farm.Definition.WorkCenter!.Value;
        Assert.All(farmland, p => Assert.True(PathFinder.InCircle(center, p, farm.Definition.WorkRadius!.Value)));

        world.RemoveBuilding(farm);
        Assert.DoesNotContain(Enumerable.Range(0, map.PointCount), i => map.CoverAt(map.Point(i)) == GroundCover.Farmland);
    }

    [Fact]
    public void The_path_search_takes_a_trail_when_it_is_quicker()
    {
        var map = new GameMap(40);
        var world = new World(map, ProductionConfig.None with { Walking = Rules });
        // A trail one row below the straight way, 20 fields long: 2 steps more, but quicker.
        for (int x = 5; x <= 26; x++)
            map.SetCover(new GridPoint(x, 11), GroundCover.Trail);
        var path = world.PathFinder!.FindPath(new GridPoint(5, 10), new GridPoint(25, 10))!;

        var p = new GridPoint(5, 10);
        int onTrail = 0;
        foreach (var step in path)
        {
            p = p.Neighbor(step);
            if (map.CoverAt(p) == GroundCover.Trail)
                onTrail++;
        }
        Assert.True(onTrail >= 15, $"{onTrail} of {path.Count} steps on the trail");
    }

    [Fact]
    public void No_trails_next_to_rivers_and_farms_sow_over_trails()
    {
        var map = new GameMap(60);
        for (int y = 0; y < 60; y++)
            map.SetTerrain(new GridPoint(20, y), TerrainType.River);
        var world = new World(map, ProductionConfig.None with { Walking = Rules });
        world.PlaceBuilding(Romans["sawmill"], 0, new GridPoint(35, 30));
        world.AddLooseGoods(new GridPoint(10, 30), Material.Trunk, 6);
        world.Spawn(new GridPoint(12, 40), Profession.Carrier);

        for (int t = 0; t < 4000; t++)
            world.Step();

        Assert.True(world.Trails.Count > 5);
        for (int y = 0; y < 60; y++)
            foreach (int x in new[] { 19, 20, 21 })
                Assert.NotEqual(GroundCover.Trail, map.CoverAt(new GridPoint(x, y)));
    }

    [Fact]
    public void Farmland_replaces_a_trail_and_takes_no_new_one()
    {
        var map = new GameMap(70);
        var world = new World(map, TestConfig.NoTools.WithCropGrowTicks("grain", 700));
        var farm = world.PlaceBuilding(Romans["farm"], 0, new GridPoint(30, 20))!;
        var center = farm.Position + farm.Definition.WorkCenter!.Value;
        foreach (var p in PathFinder.Circle(center, farm.Definition.WorkRadius!.Value))
            map.SetCover(p, GroundCover.Trail);
        world.PlaceBuilding(Romans["stock"], 0, new GridPoint(55, 25));
        for (int i = 0; i < 3; i++)
            world.Spawn(new GridPoint(45 + i, 40), Profession.Carrier);

        for (int t = 0; t < 20_000 && world.Production.FieldsSown < 3; t++)
            world.Step();

        var fields = Enumerable.Range(0, map.PointCount).Select(map.Point).Where(p => map.ObjectAt(p) == MapObjectKind.Field).ToList();
        Assert.True(fields.Count >= 3);
        Assert.All(fields, p => Assert.Equal(GroundCover.Farmland, map.CoverAt(p)));
    }

    [Fact]
    public void Farmland_keeps_a_point_away_from_rivers_and_cuts_trails_around_it()
    {
        var map = new GameMap(70);
        var world = new World(map, TestConfig.NoTools.WithCropGrowTicks("grain", 700));
        var farm = world.PlaceBuilding(Romans["farm"], 0, new GridPoint(30, 20))!;
        var center = farm.Position + farm.Definition.WorkCenter!.Value;
        int radius = farm.Definition.WorkRadius!.Value;
        // A river through the fields, trails all around.
        for (int y = 0; y < 70; y++)
            map.SetTerrain(new GridPoint(center.X, y), TerrainType.River);
        foreach (var p in PathFinder.Circle(center, radius + 2))
            if (map.TerrainAt(p) == TerrainType.Grass)
                map.SetCover(p, GroundCover.Trail);
        world.PlaceBuilding(Romans["stock"], 0, new GridPoint(55, 25));
        for (int i = 0; i < 3; i++)
            world.Spawn(new GridPoint(45 + i, 40), Profession.Carrier);

        for (int t = 0; t < 20_000 && world.Production.FieldsSown < 6; t++)
            world.Step();

        var farmland = Enumerable.Range(0, map.PointCount).Select(map.Point).Where(p => map.CoverAt(p) == GroundCover.Farmland).ToList();
        Assert.True(farmland.Count >= 6, $"{farmland.Count} farmland");
        foreach (var p in farmland)
            for (int d = 0; d < DirectionExtensions.Count; d++)
            {
                var q = p.Neighbor((Direction)d);
                Assert.NotEqual(TerrainType.River, map.TerrainAt(q));
                Assert.NotEqual(GroundCover.Trail, map.CoverAt(q));
            }
    }

    [Fact]
    public void A_fallen_buildings_levelled_ground_grows_back_over_from_the_edge()
    {
        var map = new GameMap(60);
        var world = new World(map, TestConfig.NoTools);
        var house = world.PlaceConstructionSite(Romans["medium_livinghouse"], 0, new GridPoint(30, 30))!;
        var ground = house.BlockedPoints.ToList();
        Assert.All(ground, p => Assert.Equal(TerrainType.Flattened, map.TerrainAt(p)));
        var keep = world.PlaceConstructionSite(Romans["sawmill"], 0, new GridPoint(45, 30))!; // stays levelled

        world.RemoveBuilding(house);
        Run(world, 3 * World.TicksPerSecond);
        Assert.Contains(ground, p => map.TerrainAt(p) == TerrainType.Flattened); // not all at once
        // The edge first: a point grows over only next to ground that is not levelled any more.
        var seen = ground.ToHashSet();
        for (int t = 0; t < 30 * 60 * World.TicksPerSecond && ground.Any(p => map.TerrainAt(p) == TerrainType.Flattened); t++)
        {
            world.Step();
            foreach (var p in seen.Where(p => map.TerrainAt(p) == TerrainType.Grass).ToList())
            {
                seen.Remove(p);
                Assert.Contains(Enumerable.Range(0, DirectionExtensions.Count).Select(d => p.Neighbor((Direction)d)),
                    q => map.TerrainAt(q) != TerrainType.Flattened);
            }
        }
        Assert.All(ground, p => Assert.Equal(TerrainType.Grass, map.TerrainAt(p)));
        Assert.All(keep.BlockedPoints, p => Assert.Equal(TerrainType.Flattened, map.TerrainAt(p)));
    }

    [Fact]
    public void Levelled_ground_goes_back_to_what_it_was()
    {
        var map = new GameMap(20);
        map.SetTerrain(new GridPoint(5, 5), TerrainType.DesertBorderOuter);
        map.SetTerrain(new GridPoint(5, 5), TerrainType.Flattened);
        map.SetTerrain(new GridPoint(6, 5), TerrainType.Flattened);
        Assert.Equal(TerrainType.DesertBorderOuter, map.Unlevelled(new GridPoint(5, 5)));

        map.Unlevel(new GridPoint(5, 5));
        map.Unlevel(new GridPoint(6, 5));

        Assert.Equal(TerrainType.DesertBorderOuter, map.TerrainAt(new GridPoint(5, 5)));
        Assert.Equal(TerrainType.Grass, map.TerrainAt(new GridPoint(6, 5)));
    }

    [Fact]
    public void Farms_sow_on_a_fallen_buildings_ground_and_keep_grass_between_farmland_and_levelled_ground()
    {
        var map = new GameMap(70);
        var world = new World(map, TestConfig.NoTools.WithCropGrowTicks("grain", 700) with { RegrowSeconds = 1_000_000 });
        var farm = world.PlaceBuilding(Romans["farm"], 0, new GridPoint(30, 20))!;
        var center = farm.Position + farm.Definition.WorkCenter!.Value;
        var house = PathFinder.Circle(center, 4).Select(p => world.PlaceConstructionSite(Romans["medium_livinghouse"], 0, p))
            .First(site => site is not null)!;
        var ruin = house.BlockedPoints.ToHashSet();
        world.RemoveBuilding(house);
        world.PlaceBuilding(Romans["stock"], 0, new GridPoint(55, 25));
        for (int i = 0; i < 3; i++)
            world.Spawn(new GridPoint(45 + i, 40), Profession.Carrier);

        for (int t = 0; t < 40_000 && !ruin.Any(p => map.ObjectAt(p) == MapObjectKind.Field); t++)
            world.Step();

        Assert.Contains(ruin, p => map.ObjectAt(p) == MapObjectKind.Field);
        var farmland = Enumerable.Range(0, map.PointCount).Select(map.Point).Where(p => map.CoverAt(p) == GroundCover.Farmland).ToList();
        Assert.Contains(farmland, ruin.Contains);
        foreach (var p in farmland)
            for (int d = 0; d < DirectionExtensions.Count; d++)
                Assert.NotEqual(TerrainType.Flattened, map.TerrainAt(p.Neighbor((Direction)d)));
    }

    private static void Run(World world, int ticks)
    {
        for (int t = 0; t < ticks; t++)
            world.Step();
    }

    [Theory]
    [InlineData(TerrainType.DesertBorderOuter)]
    [InlineData(TerrainType.MudBorderOuter)]
    [InlineData(TerrainType.Sand)]
    public void Trails_keep_a_point_away_from_other_ground_and_farmland(TerrainType ground)
    {
        var map = new GameMap(30);
        map.SetTerrain(new GridPoint(10, 10), ground);
        map.SetCover(new GridPoint(20, 10), GroundCover.Farmland);

        Assert.False(map.CoverFits(new GridPoint(11, 10), GroundCover.Trail));
        Assert.False(map.CoverFits(new GridPoint(11, 10), GroundCover.Farmland));
        Assert.True(map.CoverFits(new GridPoint(12, 10), GroundCover.Trail));
        Assert.False(map.CoverFits(new GridPoint(21, 10), GroundCover.Trail));
        Assert.True(map.CoverFits(new GridPoint(21, 10), GroundCover.Farmland));
    }
}
