using Homestead.Assets;
using Homestead.Assets.Maps;
using Homestead.Simulation.Ai;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Magic;
using Homestead.Simulation.Map;
using Homestead.Simulation.Pathfinding;
using Homestead.Simulation.Persistence;
using Xunit.Abstractions;

namespace Homestead.Simulation.Tests;

public class AiTests(ITestOutputHelper output)
{
    private static readonly BuildingCatalog Romans = BuildingCatalog.Load(Civilization.Roman);

    [Fact]
    public void The_ai_expands_towards_quarry_stone_its_start_land_lacks()
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!; // like the start equipment
        for (int i = 0; i < 10; i++)
            world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        for (int i = 0; i < 20; i++)
            world.Spawn(new GridPoint(45, 88), Profession.Carrier);
        // Without tools sites do not recruit: diggers and builders come along.
        for (int i = 0; i < 4; i++)
        {
            world.Spawn(new GridPoint(45, 88), Profession.Digger);
            world.Spawn(new GridPoint(45, 88), Profession.Builder);
        }
        for (int i = 0; i < 4; i++)
        {
            world.AddLooseGoods(new GridPoint(48 + i * 2, 74), Material.Plank, 8);
            world.AddLooseGoods(new GridPoint(48 + i * 2, 70), Material.Stone, 8);
        }
        // Quarry stones far outside the start land.
        var stones = new[] { new GridPoint(112, 80), new GridPoint(116, 82), new GridPoint(114, 86) };
        foreach (var stone in stones)
            map.SetObjectCode(stone, MapObjectCodes.FullStone);
        var ai = new AiPlayer(world, Romans, 0);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 15 * 60 * World.TicksPerSecond && !stones.Any(s => map.OwnerAt(s) == 0); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        foreach (var b in world.Buildings.Where(b => b.Exists))
            output.WriteLine($"{b.Definition.Name} at {b.Position.X},{b.Position.Y}: {world.StatusOf(b)}");
        output.WriteLine($"tick {world.Tick}, soldiers idle {world.Settlers.Count(s => s.Profession == Profession.Soldier && !s.HasTask && !s.IsInside)}");
        Assert.Contains(ai.Log, l => l.Contains("expands towards stone"));
        Assert.Contains(stones, s => map.OwnerAt(s) == 0);
    }

    [Fact]
    public void The_ai_moves_the_work_area_of_an_idle_stonecutter_to_stone_nearby_instead_of_tearing_it_down()
    {
        var map = new GameMap(120);
        var world = new World(map, TestConfig.NoTools);
        var stonecutter = world.PlaceBuilding(Romans["stonecutter"], 0, new GridPoint(40, 60))!;
        world.PlaceBuilding(Romans["stock"], 0, new GridPoint(30, 40));
        for (int i = 0; i < 4; i++)
            world.Spawn(new GridPoint(36, 66), Profession.Carrier);
        // Out of the work area's reach, but within reach of a moved work area.
        var rock = new GridPoint(stonecutter.WorkCenter.X + 30, stonecutter.WorkCenter.Y);
        map.SetObjectCode(rock, MapObjectCodes.FullStone);
        var ai = new AiPlayer(world, Romans, 0);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 6 * 60 * World.TicksPerSecond && world.Production.StonesCut == 0; t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Contains(ai.Log, l => l.Contains("moves the work area of stonecutter"));
        Assert.DoesNotContain(ai.Log, l => l.Contains("tears down stonecutter"));
        Assert.True(stonecutter.Exists);
        Assert.True(world.Production.StonesCut > 0);
    }

    [Fact]
    public void The_ai_stops_a_forester_with_enough_trees_and_restarts_it_once_they_are_gone()
    {
        var map = new GameMap(80);
        var world = new World(map, TestConfig.NoTools);
        var forester = world.PlaceBuilding(Romans["forester"], 0, new GridPoint(40, 40))!;
        var trees = Enumerable.Range(0, 10).Select(i => new GridPoint(34 + 2 * i, 50)).ToArray();
        foreach (var tree in trees)
            map.SetObject(tree, MapObjectKind.Tree);
        var ai = new AiPlayer(world, Romans, 0);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 15 * World.TicksPerSecond && !forester.IsPaused; t++)
            world.Step();
        Assert.True(forester.IsPaused, "ten trees in the work area");

        map.SetObjectCode(trees[0], MapObjectCodes.None);
        for (int t = 0; t < 15 * World.TicksPerSecond && forester.IsPaused; t++)
            world.Step();
        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.False(forester.IsPaused, "nine trees left");
    }

    [Fact]
    public void A_woodcutter_in_a_full_forest_gets_no_forester()
    {
        var (world, _) = WoodAi(Romans, world =>
        {
            Grove(world.Map!, 50, 60, 70, 92);
            Assert.NotNull(world.PlaceBuilding(Romans["lumberjack"], 0, new GridPoint(58, 100)));
        }, ("lumberjack", 1), ("forester", 1));

        Think(world, 30);

        Assert.DoesNotContain(world.Buildings, b => b.Definition.Name == "forester");
    }

    [Fact]
    public void A_woodcutter_running_short_of_trees_gets_one_forester_next_to_it()
    {
        Building lumberjack = null!;
        var (world, ai) = WoodAi(Romans, world =>
        {
            Grove(world.Map!, 56, 70, 60, 74); // a few trees only
            lumberjack = world.PlaceBuilding(Romans["lumberjack"], 0, new GridPoint(52, 80))!;
            Assert.NotNull(lumberjack);
        }, ("lumberjack", 1), ("forester", 2));

        // The build order would allow two; the trees of the first take a while to count.
        Think(world, 90);

        foreach (string line in ai.Log)
            output.WriteLine(line);
        var forester = Assert.Single(world.Buildings, b => b.Definition.Name == "forester");
        Assert.True(PathFinder.Distance(forester.Position, lumberjack.Position) < 15, $"forester at {forester.Position}");
    }

    [Fact]
    public void No_forester_where_it_would_not_plant_for_the_woodcutter_short_of_trees()
    {
        var (world, ai) = WoodAi(Romans, world =>
        {
            // At the far edge of the land: no room for a forester near it, plenty further back.
            Assert.NotNull(world.PlaceBuilding(Romans["lumberjack"], 0, new GridPoint(40 + LandRadius(world) + 20, 80), force: true));
        }, ("lumberjack", 1), ("forester", 2));

        Think(world, 60);

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.DoesNotContain(world.Buildings, b => b.Definition.Name == "forester");
    }

    private static int LandRadius(World world) =>
        Enumerable.Range(0, 40).Last(r => world.Map!.OwnerAt(new GridPoint(40 + r, 80)) == 0);

    [Fact]
    public void A_storehouse_only_once_there_is_gold_even_with_weapons_piling_up()
    {
        var (world, ai) = WoodAi(Romans, world =>
        {
            Assert.NotNull(world.PlaceBuilding(Romans["weaponsmith"], 0, new GridPoint(50, 76)));
            world.AddLooseGoods(new GridPoint(44, 94), Material.Sword, 8);
            world.AddLooseGoods(new GridPoint(46, 94), Material.Sword, 8);
        });

        Think(world, 30);
        Assert.DoesNotContain(world.Buildings, b => b.Definition.Name == "stock");

        // Gold taken over with the land counts as well as gold made.
        world.AddLooseGoods(new GridPoint(48, 94), Material.Gold, 2);
        Think(world, 30);

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Contains(world.Buildings, b => b.Definition.Name == "stock");
    }

    /// <summary>Only smiths and sawyers need their tools (builders and diggers come without).</summary>
    private static readonly ProductionConfig SmithTools = TestConfig.NoTools with
    {
        Tools = new Dictionary<string, Material> { ["SMITH"] = Material.Hammer, ["SAWMILLER"] = Material.Saw },
    };

    [Fact]
    public void A_further_weaponsmith_leaves_a_smith_for_the_toolsmith()
    {
        var (world, ai) = WoodAi(Romans, SmithTools, world =>
        {
            Assert.NotNull(world.PlaceBuilding(Romans["weaponsmith"], 0, new GridPoint(50, 76)));
            world.AddLooseGoods(new GridPoint(44, 94), Material.Hammer, 2);
            // Smiths wait for a coal mine without coal lying around (the weaponsmith takes in 8).
            world.AddLooseGoods(new GridPoint(48, 94), Material.Coal, 8);
            world.AddLooseGoods(new GridPoint(50, 94), Material.Coal, 8);
        }, ("weaponsmith", 2));

        Think(world, 30);
        Assert.Single(world.Buildings, b => b.Definition.Name == "weaponsmith");

        // A hammer more: one smith for the new weaponsmith, one stays.
        world.AddLooseGoods(new GridPoint(46, 94), Material.Hammer, 1);
        Think(world, 30);

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Equal(2, world.Buildings.Count(b => b.Definition.Name == "weaponsmith"));
    }

    [Fact]
    public void A_missing_tool_brings_the_toolsmith_and_a_weaponsmith_gives_up_its_smith_for_it()
    {
        // The only hammer went to the weaponsmith, the saw is gone: the sawmill stands idle.
        var (world, ai) = WoodAi(Romans, SmithTools, world =>
        {
            var weaponsmith = world.PlaceBuilding(Romans["weaponsmith"], 0, new GridPoint(50, 76))!;
            world.AddLooseGoods(new GridPoint(44, 94), Material.Hammer, 1);
            for (int t = 0; t < 60 * World.TicksPerSecond && world.Production.WorkerOf(weaponsmith) is null; t++)
                world.Step();
            Assert.NotNull(world.PlaceBuilding(Romans["sawmill"], 0, new GridPoint(30, 70)));
            world.AddLooseGoods(new GridPoint(48, 94), Material.Coal, 8);
            world.AddLooseGoods(new GridPoint(50, 94), Material.Coal, 8);
            for (int i = 0; i < 2; i++)
                world.Spawn(new GridPoint(45, 90), Profession.Builder); // none come from carriers without a builder's tool
        });
        var weaponsmith = world.Buildings.Single(b => b.Definition.Name == "weaponsmith");
        var smith = world.Production.WorkerOf(weaponsmith);
        Assert.NotNull(smith);

        Building? toolsmith = null;
        for (int t = 0; t < 300 * World.TicksPerSecond && (toolsmith is null || world.Production.WorkerOf(toolsmith) is null); t++)
        {
            world.Step();
            toolsmith ??= world.Buildings.FirstOrDefault(b => b.Definition.Name == "toolsmith" && b.Exists);
        }

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.NotNull(toolsmith);
        Assert.False(weaponsmith.Exists);
        Assert.Same(smith, world.Production.WorkerOf(toolsmith));
    }

    [Fact]
    public void The_beekeeping_goes_to_the_trees_away_from_the_woodcutters()
    {
        var amazons = BuildingCatalog.Load(Civilization.Amazon);
        var (world, ai) = WoodAi(amazons, world =>
        {
            Grove(world.Map!, 20, 78, 32, 90); // west, the woodcutter's
            Grove(world.Map!, 52, 78, 64, 90); // east, nobody's (a little farther from home)
            Assert.NotNull(world.PlaceBuilding(amazons["lumberjack"], 0, new GridPoint(26, 96)));
        }, ("beekeeping", 1));

        Think(world, 10);

        foreach (string line in ai.Log)
            output.WriteLine(line);
        var beekeeping = Assert.Single(world.Buildings, b => b.Definition.Name == "beekeeping");
        Assert.True(beekeeping.Position.X > 45, $"beekeeping at {beekeeping.Position}");
    }

    /// <summary>
    /// An AI with only this build order, its land around a tower at 40,80 (staffed already, x 10–70, y 50–110), with planks
    /// and stones and carriers; <paramref name="setUp"/> runs on the land before the AI first looks at it.
    /// </summary>
    private static (World World, AiPlayer Ai) WoodAi(BuildingCatalog catalog, Action<World> setUp, params (string, int)[] buildOrder) =>
        WoodAi(catalog, TestConfig.NoTools, setUp, buildOrder);

    private static (World World, AiPlayer Ai) WoodAi(BuildingCatalog catalog, ProductionConfig config, Action<World> setUp, params (string, int)[] buildOrder)
    {
        var world = new World(new GameMap(160), config) { TerritoryRules = true };
        var tower = world.PlaceBuilding(catalog["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        for (int t = 0; t < 30 * World.TicksPerSecond && world.Map!.OwnerAt(tower.Position) != 0; t++)
            world.Step();
        for (int i = 0; i < 10; i++)
            world.Spawn(new GridPoint(45, 88), Profession.Carrier);
        for (int i = 0; i < 4; i++)
        {
            world.AddLooseGoods(new GridPoint(36 + i * 2, 96), Material.Plank, 8);
            world.AddLooseGoods(new GridPoint(36 + i * 2, 99), Material.Stone, 8);
        }
        setUp(world);
        var ai = new AiPlayer(world, catalog, 0, AiConfig.Load(civilization: catalog.Civilization) with { BuildOrder = buildOrder });
        world.AiPlayers.Add(ai);
        return (world, ai);
    }

    /// <summary>A tree on every other field of the rectangle.</summary>
    private static void Grove(GameMap map, int left, int top, int right, int bottom)
    {
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
            if ((x + y) % 2 == 0)
                map.SetObjectCode(new GridPoint(x, y), MapObjectCodes.FirstTree);
    }

    private static void Think(World world, int seconds)
    {
        for (int t = 0; t < seconds * World.TicksPerSecond; t++)
            world.Step();
    }

    [Fact]
    public void Defenders_strike_the_enemies_they_stand_against()
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman); // the garrison
        world.Step();
        // Three idle soldiers against two enemies close to the land: all three would be held back
        // against them, yet together they are strong enough to strike.
        for (int i = 0; i < 3; i++)
            world.SpawnSoldier(new GridPoint(44 + i, 84), SoldierType.Swordsman);
        for (int i = 0; i < 2; i++)
            world.SpawnSoldier(new GridPoint(40 + i, 125), SoldierType.Swordsman, player: 1);
        Assert.NotEqual(0, map.OwnerAt(new GridPoint(40, 125)));
        var ai = new AiPlayer(world, Romans, 0);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 30 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("strikes")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Contains(ai.Log, l => l.Contains("strikes 2 enemies"));
    }

    [Theory]
    [InlineData("standard", false)]
    [InlineData("standard@military.reinforceRadius=0", true)]
    public void Scattered_enemies_nearby_count_as_the_strike_targets_defence(string character, bool strikes)
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman); // the garrison
        world.Step();
        for (int i = 0; i < 3; i++)
            world.SpawnSoldier(new GridPoint(44 + i, 84), SoldierType.Swordsman);
        // One enemy alone, two more 20 fields behind him (out of military.threatRange, nobody is held back against
        // them): not one group, not on the way, but they would come to help.
        world.SpawnSoldier(new GridPoint(40, 135), SoldierType.Swordsman, player: 1);
        for (int i = 0; i < 2; i++)
            world.SpawnSoldier(new GridPoint(40 + i, 155), SoldierType.Swordsman, player: 1);
        var ai = new AiPlayer(world, Romans, 0, character: character);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 10 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("strikes")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Equal(strikes, ai.Log.Any(l => l.Contains("strikes 1 enemies")));
    }

    [Theory]
    [InlineData("standard", false)]
    [InlineData("standard@military.towerFireRatio=0", true)]
    public void Guerilla_leaves_a_lone_soldier_next_to_a_full_castle_alone(string character, bool strikes)
    {
        var map = new GameMap(200);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 100), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman); // the garrison
        var castle = world.PlaceBuilding(Romans["castle"], 1, new GridPoint(130, 100), force: true)!;
        int bowmen = castle.Definition.Occupiers.Count(o => o.ForBowman);
        world.SpawnSoldier(castle.Door, SoldierType.Swordsman, player: 1);
        world.Military.ChangeWanted(castle, SoldierType.Bowman, bowmen);
        for (int i = 0; i < bowmen; i++)
            world.SpawnSoldier(castle.Door, SoldierType.Bowman, player: 1);
        for (int t = 0; t < 600; t++)
            world.Step();
        Assert.Equal(bowmen, world.Military.Assigned(castle, SoldierType.Bowman));
        for (int i = 0; i < 10; i++)
            world.SpawnSoldier(new GridPoint(44 + i, 106), SoldierType.Swordsman);
        world.SpawnSoldier(castle.Door + new GridPoint(-6, 4), SoldierType.Swordsman, player: 1); // the lone one
        var ai = new AiPlayer(world, Romans, 0, character: character);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 10 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("strikes")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Equal(strikes, ai.Log.Any(l => l.Contains("strikes 1 enemies")));
    }

    [Theory]
    [InlineData("standard", false)]
    [InlineData("standard@military.towerFireRatio=0", true)]
    public void Guerilla_weighs_the_tower_fire_on_the_way_of_most_strikers_not_just_the_nearest(string character, bool strikes)
    {
        var map = new GameMap(200);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(100, 40), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman); // the garrison
        var castle = world.PlaceBuilding(Romans["castle"], 1, new GridPoint(100, 90), force: true)!;
        int bowmen = castle.Definition.Occupiers.Count(o => o.ForBowman);
        world.SpawnSoldier(castle.Door, SoldierType.Swordsman, player: 1);
        world.Military.ChangeWanted(castle, SoldierType.Bowman, bowmen);
        for (int i = 0; i < bowmen; i++)
            world.SpawnSoldier(castle.Door, SoldierType.Bowman, player: 1);
        for (int t = 0; t < 600; t++)
            world.Step();
        // A lone enemy beyond the castle, out of its bowmen's reach; the strikers come from the north, past
        // the castle – only one of them waits south of the enemy.
        var lone = new GridPoint(100, 125);
        Assert.True(Pathfinding.PathFinder.Distance(lone, castle.Position) > world.Military.Config.Soldiers[SoldierType.Bowman].Range);
        world.SpawnSoldier(lone, SoldierType.Swordsman, player: 1);
        world.SpawnSoldier(new GridPoint(100, 145), SoldierType.Swordsman); // out of sight of the lone one
        for (int i = 0; i < 12; i++)
            world.SpawnSoldier(new GridPoint(94 + i, 48), SoldierType.Swordsman);
        var ai = new AiPlayer(world, Romans, 0, character: character);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 10 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("strikes")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Equal(strikes, ai.Log.Any(l => l.Contains("strikes 1 enemies")));
    }

    [Fact]
    public void A_recruit_walking_deep_in_its_own_land_is_no_attack_on_the_land()
    {
        var map = new GameMap(200);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(100, 40), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman); // the garrison
        world.Step();
        for (int y = 90; y < 200; y++)
        for (int x = 0; x < 200; x++)
            map.SetOwner(new GridPoint(x, y), 1);
        for (int i = 0; i < 6; i++)
            world.SpawnSoldier(new GridPoint(97 + i, 48), SoldierType.Swordsman);
        // 20 fields deep in its land, on his way to a point there (like a recruit to the gathering point).
        var recruit = world.SpawnSoldier(new GridPoint(100, 110), SoldierType.Swordsman, player: 1)!;
        world.Military.Move([recruit], new GridPoint(130, 112));
        var ai = new AiPlayer(world, Romans, 0, character: "standard@military.hinterlandDepth=8");
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 5 * World.TicksPerSecond; t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.DoesNotContain(ai.Log, l => l.Contains("strikes"));
    }

    [Fact]
    public void The_ai_needs_more_soldiers_against_enemies_on_their_own_land()
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman); // the garrison
        world.Step();
        // The enemies stand on their own land: full strength there, the AI's soldiers only half.
        foreach (var p in Pathfinding.PathFinder.Circle(new GridPoint(40, 125), 12))
            map.SetOwner(p, 1);
        for (int i = 0; i < 6; i++)
            world.SpawnSoldier(new GridPoint(42 + i, 84), SoldierType.Swordsman);
        for (int i = 0; i < 2; i++)
            world.SpawnSoldier(new GridPoint(40 + i, 125), SoldierType.Swordsman, player: 1);
        var ai = new AiPlayer(world, Romans, 0);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 30 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("strikes")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        // 2 enemies × 1.5 (strikeRatio) at full strength take 6 soldiers at 50 %, not 3.
        Assert.Contains(ai.Log, l => l.Contains("strikes 2 enemies") && l.Contains("with 6 soldiers"));
    }

    [Fact]
    public void A_general_feints_and_strikes_the_enemy_tower_from_the_side()
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var own = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        var enemy = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(100, 80), force: true)!;
        world.SpawnSoldier(own.Door, SoldierType.Swordsman);
        world.SpawnSoldier(enemy.Door, SoldierType.Swordsman, player: 1);
        world.Step();
        for (int i = 0; i < 8; i++)
            world.SpawnSoldier(new GridPoint(44 + i, 88), SoldierType.Swordsman);
        var ai = new AiPlayer(world, Romans, 0, character: "general");
        world.AiPlayers.Add(ai);
        Assert.Equal("general", ai.Character.Id);

        bool sideways = false;
        for (int t = 0; t < 5 * 60 * World.TicksPerSecond && enemy.Player != 0; t++)
        {
            world.Step();
            // When the feint attacks, the main force waits off to the side, not behind the feint.
            if (!sideways && ai.Log.Any(l => l.Contains("feint against")))
                sideways = world.Settlers.Count(s => s.Player == 0 && !s.IsInside
                                                     && Pathfinding.PathFinder.Distance(s.Position, own.Door) > 30
                                                     && Pathfinding.PathFinder.Distance(s.Position, enemy.Position) < 25) >= 2;
        }

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Contains(ai.Log, l => l.Contains("pincer against the tower"));
        Assert.Contains(ai.Log, l => l.Contains("feint against"));
        Assert.Contains(ai.Log, l => l.Contains("from the side"));
        Assert.True(sideways, "the main force stands at the side when the feint attacks");
        Assert.Equal(0, enemy.Player);
    }

    [Theory]
    [InlineData("standard", false)]
    [InlineData("general", true)]
    public void Idle_soldiers_gather_at_home_only_with_guarding(string character, bool gathers)
    {
        var world = new World(new GameMap(160), TestConfig.NoTools) { TerritoryRules = true };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman); // the garrison
        world.Step();
        var soldier = world.SpawnSoldier(new GridPoint(70, 80), SoldierType.Swordsman)!;
        var start = soldier.Position;
        world.AiPlayers.Add(new AiPlayer(world, Romans, 0, character: character));

        for (int t = 0; t < 30 * World.TicksPerSecond; t++)
            world.Step();

        if (gathers)
            Assert.True(Pathfinding.PathFinder.Distance(soldier.Position, tower.Door) <= 10, $"at {soldier.Position}");
        else
            Assert.True(Pathfinding.PathFinder.Distance(soldier.Position, start) <= 3, $"at {soldier.Position}");
    }

    [Fact]
    public void Unknown_characters_and_traits_are_refused()
    {
        var world = new World(new GameMap(40), TestConfig.NoTools);
        Assert.Throws<ArgumentException>(() => new AiPlayer(world, Romans, 0, character: "nobody"));
        Assert.Equal("legion", new AiPlayer(world, Romans, 0).Character.Id); // the people's character
        Assert.Throws<InvalidDataException>(() => AiTrait.Create("nothing", default));
    }

    [Fact]
    public void Characters_can_be_made_up_from_traits_and_values()
    {
        var plain = AiConfig.Load(civilization: Civilization.Roman);
        var variant = AiConfig.Load(civilization: Civilization.Roman,
            character: "general@attack.ratio=1.7@buildOrderMoves.barrack=-3@pincer.feintShare=0.5");
        Assert.Equal(1.7, variant.Attack.Ratio);
        Assert.Equal(1.7, variant.Attack.ArmyRatio); // defaults to the ratio
        Assert.Equal(1.7, variant.Attack.AbortRatio);
        Assert.Equal(40, variant.Military.StrikeRange); // the general's own value stays
        int Barrack(AiConfig c) => c.BuildOrder.ToList().FindIndex(e => e.Building == "barrack");
        Assert.Equal(Barrack(plain) - 3, Barrack(variant));
        Assert.Equal(plain.BuildOrder.Count, variant.BuildOrder.Count);
        var traits = variant.Characters["general@attack.ratio=1.7@buildOrderMoves.barrack=-3@pincer.feintShare=0.5"];
        Assert.Equal(0.5, traits.Single(t => t.Trait == "pincer").Settings.GetProperty("feintShare").GetDouble());

        var world = new World(new GameMap(40), TestConfig.NoTools);
        Assert.Equal(["waves", "frontGarrison"], new AiPlayer(world, Romans, 0, character: "waves+frontGarrison").Character.Traits.Select(t => t.Id));
        Assert.Throws<ArgumentException>(() => new AiPlayer(world, Romans, 0, character: "waves+nothing"));
        Assert.Equal(["waves", "pincer", "formation", "everyOtherTower", "guardByThreat"], new AiPlayer(world, Romans, 0, character: "general+waves").Character.Traits.Select(t => t.Id));
    }

    [Fact]
    public void With_front_garrison_only_buildings_near_the_enemy_are_fully_staffed()
    {
        var world = new World(new GameMap(160), TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var back = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(30, 80), force: true)!;
        var front = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(90, 80), force: true)!;
        world.PlaceBuilding(Romans["tower"], 1, new GridPoint(130, 80), force: true);
        world.SpawnSoldier(back.Door, SoldierType.Swordsman);
        world.SpawnSoldier(front.Door, SoldierType.Swordsman);
        world.Step();
        world.AiPlayers.Add(new AiPlayer(world, Romans, 0, character: "frontGarrison"));

        for (int t = 0; t < 10 * World.TicksPerSecond; t++)
            world.Step();

        int Wanted(Building b) => Enum.GetValues<SoldierType>().Sum(type => world.Military.Wanted(b, type));
        Assert.Equal(1, Wanted(back));
        Assert.Equal(Romans["tower"].Occupiers.Count, Wanted(front));
    }

    [Fact]
    public void Front_garrison_stays_front_up_to_ten_fields_beyond_the_last_enemy()
    {
        var world = new World(new GameMap(160), TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var own = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(20, 80), force: true)!;
        var near = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(64, 80), force: true)!;
        var far = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(70, 80), force: true)!;
        world.SpawnSoldier(own.Door, SoldierType.Swordsman);
        world.Step();
        world.AiPlayers.Add(new AiPlayer(world, Romans, 0, character: "frontGarrison"));

        for (int t = 0; t < 10 * World.TicksPerSecond; t++)
            world.Step();

        int Wanted(Building b) => Enum.GetValues<SoldierType>().Sum(type => world.Military.Wanted(b, type));
        Assert.Equal(Romans["tower"].Occupiers.Count, Wanted(own)); // 44 fields to the enemy: front

        world.RemoveBuilding(near); // the nearest enemy stands 50 fields off: front up to 45 + 10
        for (int t = 0; t < 10 * World.TicksPerSecond; t++)
            world.Step();
        Assert.Equal(Romans["tower"].Occupiers.Count, Wanted(own));

        world.RemoveBuilding(far); // no enemy left: one transition to the backline
        for (int t = 0; t < 10 * World.TicksPerSecond; t++)
            world.Step();
        Assert.Equal(1, Wanted(own));

        for (int t = 0; t < 15 * World.TicksPerSecond; t++) // and no flicker afterwards
        {
            world.Step();
            Assert.Equal(1, Wanted(own));
        }
    }

    [Fact]
    public void Front_garrison_classification_survives_save_load()
    {
        var world = new World(new GameMap(160), TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var own = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(20, 80), force: true)!;
        var near = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(64, 80), force: true)!;
        world.PlaceBuilding(Romans["tower"], 1, new GridPoint(70, 80), force: true);
        world.SpawnSoldier(own.Door, SoldierType.Swordsman);
        world.Step();
        world.AiPlayers.Add(new AiPlayer(world, Romans, 0, character: "frontGarrison"));

        for (int t = 0; t < 10 * World.TicksPerSecond; t++)
            world.Step();
        world.RemoveBuilding(near); // the enemy left stands 50 fields off: front only via the saved classification
        for (int t = 0; t < 10 * World.TicksPerSecond; t++)
            world.Step();

        var stream = new MemoryStream();
        Snapshot.Write(stream, world);
        stream.Position = 0;
        var loaded = Snapshot.Read<World>(stream);
        for (int t = 0; t < 3 * World.TicksPerSecond; t++)
            loaded.Step();

        var tower = loaded.Buildings.Single(b => b.Player == 0 && b.Position == own.Position);
        int Wanted(Building b) => Enum.GetValues<SoldierType>().Sum(type => loaded.Military.Wanted(b, type));
        Assert.Equal(Romans["tower"].Occupiers.Count, Wanted(tower));
    }

    [Theory]
    [InlineData("standard", false)]
    [InlineData("standard@attack.commit=1", true)]
    public void A_committed_campaign_takes_all_spare_soldiers(string character, bool all)
    {
        var world = new World(new GameMap(160), TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var own = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        var enemy = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(100, 80), force: true)!;
        world.SpawnSoldier(own.Door, SoldierType.Swordsman);
        world.SpawnSoldier(enemy.Door, SoldierType.Swordsman, player: 1);
        world.Step();
        for (int i = 0; i < 10; i++)
            world.SpawnSoldier(new GridPoint(44 + i, 88), SoldierType.Swordsman);
        var ai = new AiPlayer(world, Romans, 0, character: character);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 20 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("gathers")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Equal(all, ai.Log.Any(l => l.Contains("gathers 10 soldiers")));
        Assert.Contains(ai.Log, l => l.Contains("gathers"));
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(25, false)]
    public void The_campaign_distance_limit_counts_from_the_own_land(int maxDistance, bool campaign)
    {
        // Towers at a common border stand 60 fields apart, the enemy's 30 from the own land.
        var world = new World(new GameMap(160), TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var own = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        var enemy = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(100, 80), force: true)!;
        world.SpawnSoldier(own.Door, SoldierType.Swordsman);
        world.SpawnSoldier(enemy.Door, SoldierType.Swordsman, player: 1);
        world.Step();
        for (int i = 0; i < 10; i++)
            world.SpawnSoldier(new GridPoint(44 + i, 88), SoldierType.Swordsman);
        var ai = new AiPlayer(world, Romans, 0, character: $"standard@attack.maxDistance={maxDistance}");
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 20 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("gathers")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Equal(campaign, ai.Log.Any(l => l.Contains("gathers")));
    }

    [Fact]
    public void An_early_army_moves_iron_melt_weaponsmith_and_barrack_behind_wood_and_stone()
    {
        var world = new World(new GameMap(40), TestConfig.NoTools);
        foreach (var people in Enum.GetValues<Civilization>())
        {
            var order = new AiPlayer(world, BuildingCatalog.Load(people), 0, character: "earlyArmy").BuildOrder;
            var plain = AiConfig.Load(civilization: people).BuildOrder;
            Assert.Equal(plain.Take(4), order.Take(4));
            Assert.Equal([("ironmelt", 1), ("weaponsmith", 1), ("barrack", 1)], order.Skip(4).Take(3));
            Assert.Equal(plain.Count, order.Count);
        }
    }

    [Fact]
    public void Idle_soldiers_gather_at_the_forward_base_and_fall_back_from_a_stronger_enemy()
    {
        var world = new World(new GameMap(200), TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var home = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(30, 100), force: true)!;
        var front = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(90, 100), force: true)!;
        world.PlaceBuilding(Romans["tower"], 1, new GridPoint(170, 100), force: true);
        world.SpawnSoldier(home.Door, SoldierType.Swordsman);
        world.SpawnSoldier(front.Door, SoldierType.Swordsman);
        world.Step();
        var soldiers = Enumerable.Range(0, 3).Select(i => world.SpawnSoldier(new GridPoint(34 + i, 108), SoldierType.Swordsman)!).ToList();
        var ai = new AiPlayer(world, Romans, 0, character: "forwardBase");
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 60 * World.TicksPerSecond; t++)
            world.Step();
        Assert.All(soldiers, s => Assert.True(Pathfinding.PathFinder.Distance(s.Position, front.Door) <= 10, $"at {s.Position}"));

        // Many enemies come close to the base (outside the own land): the soldiers fall back home.
        for (int i = 0; i < 12; i++)
            world.SpawnSoldier(new GridPoint(125 + i % 4, 96 + i / 4), SoldierType.Swordsman, player: 1);
        for (int t = 0; t < 60 * World.TicksPerSecond; t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Contains(ai.Log, l => l.Contains("forwardBase: falls back"));
        Assert.All(soldiers.Where(s => !s.IsDead), s => Assert.True(Pathfinding.PathFinder.Distance(s.Position, home.Door) <= 10, $"at {s.Position}"));
    }

    [Fact]
    public void The_ai_upgrades_swordsmen_bowmen_pikemen_to_level_2_and_then_to_level_3()
    {
        var map = new GameMap(100);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true };
        world.PlaceBuilding(Romans["tower"], 0, new GridPoint(50, 50), force: true);
        world.PlaceBuilding(Romans["temple"], 0, new GridPoint(40, 50), force: true);
        world.Magic.AddMana(0, 1000);
        var ai = new AiPlayer(world, Romans, 0);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 10 * World.TicksPerSecond && world.Magic.UpgradeThreshold(0) is not null; t++)
            world.Step();

        var upgrades = ai.Log.Where(l => l.Contains("upgrades")).ToList();
        foreach (string line in upgrades)
            output.WriteLine(line);
        Assert.Equal(["Swordsman to level 2", "Bowman to level 2", "Pikeman to level 2", "Swordsman to level 3", "Bowman to level 3", "Pikeman to level 3"],
            upgrades.Select(l => l[(l.IndexOf("upgrades ") + 9)..l.IndexOf(" (")]));
    }

    [Fact]
    public void Ai_players_build_up_an_economy_on_a_multiplayer_map()
    {
        GameInstallation installation;
        try { installation = GameInstallation.Locate(); }
        catch (Exception) { output.WriteLine("No installation found, skipped."); return; }

        var original = installation.MapFiles.Select(OriginalMap.Open).First(m => !m.IsSinglePlayer && m.Buildings.Count == 0);
        var (world, report) = OriginalScenario.Load(original);
        for (int player = 0; player < report.Catalogs.Count; player++)
            world.AiPlayers.Add(new AiPlayer(world, report.Catalogs[player], player));

        for (int t = 0; t < 20 * 60 * World.TicksPerSecond; t++)
            world.Step();

        foreach (var ai in world.AiPlayers)
        {
            var own = world.Buildings.Where(b => b.Exists && b.Player == ai.Player && b.State == BuildingState.Finished).ToList();
            output.WriteLine($"player {ai.Player + 1}: {own.Count} buildings, land {ai.Land}: " +
                             string.Join(", ", own.GroupBy(b => b.Definition.Name).Select(g => $"{g.Key} {g.Count()}")));
            Assert.True(own.Count >= 15, $"player {ai.Player + 1} has {own.Count} buildings");
            foreach (string name in new[] { "lumberjack", "sawmill", "stonecutter", "small_livinghouse" })
                Assert.Contains(own, b => b.Definition.Name == name);
            Assert.True(own.Count(b => b.Definition.Name == "tower") >= 2, $"player {ai.Player + 1} expanded");
            // Nothing stuck: every construction site of the start phase got finished.
            Assert.True(world.Buildings.Count(b => b.Exists && b.Player == ai.Player && b.State != BuildingState.Finished) <= 4);
        }
    }

    [Fact]
    public void Retreating_soldiers_take_up_a_strike_on_the_way()
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman); // the garrison
        world.Step();
        var retreating = Enumerable.Range(0, 3).Select(i => world.SpawnSoldier(new GridPoint(44 + i, 84), SoldierType.Swordsman)!).ToList();
        world.Military.Move(retreating, new GridPoint(40, 20), forced: true);
        for (int i = 0; i < 2; i++)
            world.SpawnSoldier(new GridPoint(40 + i, 125), SoldierType.Swordsman, player: 1);
        var ai = new AiPlayer(world, Romans, 0);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 3 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("strikes")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Contains(ai.Log, l => l.Contains("strikes 2 enemies"));
    }

    [Fact]
    public void Retreating_soldiers_walk_on_without_a_new_action()
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 30), force: true);
        world.Step();
        var retreating = Enumerable.Range(0, 3).Select(i => world.SpawnSoldier(new GridPoint(44 + i, 84), SoldierType.Swordsman)!).ToList();
        var to = new GridPoint(70, 80);
        world.Military.Move(retreating, to, forced: true);
        var ai = new AiPlayer(world, Romans, 0);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 3 * World.TicksPerSecond; t++)
            world.Step();

        // Nobody to fight: guarding does not send them elsewhere, they still retreat where they were sent.
        Assert.All(retreating, s => Assert.True(world.Military.ForcedMoveTarget(s) is { } target && PathFinder.Distance(target, to) <= 3));
    }

    [Fact]
    public void Soldiers_walking_to_a_post_take_up_a_strike_on_the_way()
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman); // the garrison
        world.Step();
        var walking = Enumerable.Range(0, 3).Select(i => world.SpawnSoldier(new GridPoint(44 + i, 84), SoldierType.Swordsman)!).ToList();
        world.Military.Move(walking, new GridPoint(40, 20));
        for (int i = 0; i < 2; i++)
            world.SpawnSoldier(new GridPoint(40 + i, 125), SoldierType.Swordsman, player: 1);
        var ai = new AiPlayer(world, Romans, 0);
        world.AiPlayers.Add(ai);

        // Long before they would arrive up there.
        for (int t = 0; t < 3 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("strikes")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Contains(ai.Log, l => l.Contains("strikes 2 enemies"));
    }

    [Fact]
    public void Enemy_geologists_are_struck_and_killed()
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        world.Step();
        world.SpawnSoldier(new GridPoint(44, 84), SoldierType.Swordsman);
        world.SpawnSoldier(new GridPoint(45, 84), SoldierType.Swordsman);
        var carrier = world.Spawn(new GridPoint(40, 122), Profession.Carrier, player: 1)!;
        world.Geology.Convert([carrier]);
        Assert.Equal(Profession.Geologist, carrier.Profession);
        var ai = new AiPlayer(world, Romans, 0);
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 60 * World.TicksPerSecond && !carrier.IsDead; t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Contains(ai.Log, l => l.Contains("strikes 1 enemies") && l.Contains("with 1 soldiers"));
        Assert.True(carrier.IsDead);
    }

    [Theory]
    [InlineData(20, false)] // deep in their land: left alone
    [InlineData(4, true)] // at the edge of it
    public void Enemies_standing_deep_in_their_land_are_left_alone_with_a_hinterland_depth(int landRadius, bool struck)
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 60), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        world.Step();
        foreach (var p in PathFinder.Circle(new GridPoint(40, 120), landRadius))
            map.SetOwner(p, 1);
        for (int i = 0; i < 8; i++)
            world.SpawnSoldier(new GridPoint(42 + i, 66), SoldierType.Swordsman);
        world.SpawnSoldier(new GridPoint(40, 120), SoldierType.Swordsman, player: 1);
        var ai = new AiPlayer(world, Romans, 0, character: "standard@military.hinterlandDepth=8");
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 10 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("strikes")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Equal(struck, ai.Log.Any(l => l.Contains("strikes 1 enemies") && l.Contains("standing around")));
    }

    [Fact]
    public void Every_other_tower_gets_the_idle_soldiers()
    {
        var world = new World(new GameMap(200), TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var towers = new[] { 30, 50, 70, 90 }.Select(x => world.PlaceBuilding(Romans["tower"], 0, new GridPoint(x, 80), force: true)!).ToList();
        world.PlaceBuilding(Romans["tower"], 1, new GridPoint(185, 80), force: true);
        foreach (var tower in towers)
            world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        world.Step();
        for (int i = 0; i < 8; i++)
            world.SpawnSoldier(new GridPoint(30 + i, 90), SoldierType.Swordsman);
        var ai = new AiPlayer(world, Romans, 0, character: "frontGarrison+everyOtherTower");
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 40 * World.TicksPerSecond; t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        int Near(Building b) => world.Settlers.Count(s => s.Player == 0 && !s.IsInside && PathFinder.Distance(s.Position, b.Door) <= 6);
        // Front first: the tower nearest the enemy and the one after its neighbor.
        Assert.Equal([0, 4, 0, 4], towers.Select(Near));
    }

    [Fact]
    public void Front_towers_get_the_idle_soldiers()
    {
        var world = new World(new GameMap(200), TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var back = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(30, 80), force: true)!;
        var front = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(70, 80), force: true)!;
        var side = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(110, 30), force: true)!;
        world.PlaceBuilding(Romans["tower"], 1, new GridPoint(185, 80), force: true);
        foreach (var tower in new[] { back, front, side })
            world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        world.Step();
        for (int i = 0; i < 8; i++)
            world.SpawnSoldier(new GridPoint(30 + i, 90), SoldierType.Swordsman);
        var ai = new AiPlayer(world, Romans, 0, character: "frontGarrison+frontTowers");
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 90 * World.TicksPerSecond; t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        int Near(Building b) => world.Settlers.Count(s => s.Player == 0 && !s.IsInside && PathFinder.Distance(s.Position, b.Door) <= 6);
        Assert.Equal(0, Near(back));
        Assert.Equal(4, Near(front));
        Assert.Equal(4, Near(side));
    }

    [Fact]
    public void Rotating_towers_change_every_minute()
    {
        var world = new World(new GameMap(200), TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var towers = Enumerable.Range(0, 10).Select(i => world.PlaceBuilding(Romans["tower"], 0, new GridPoint(20 + i % 5 * 30, 50 + i / 5 * 60), force: true)!).ToList();
        foreach (var tower in towers)
            world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        world.Step();
        for (int i = 0; i < 6; i++)
            world.SpawnSoldier(new GridPoint(30 + i, 90), SoldierType.Swordsman);
        var ai = new AiPlayer(world, Romans, 0, character: "frontGarrison+rotatingTowers");
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 130 * World.TicksPerSecond; t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        var rounds = ai.Log.Where(l => l.Contains("rotatingTowers: guards")).Select(l => l[(l.IndexOf("guards", StringComparison.Ordinal) + 7)..]).ToList();
        Assert.True(rounds.Count >= 3, string.Join(" | ", rounds));
        Assert.All(rounds, r => Assert.Equal(3, r.Split(' ').Length)); // 30 % of 10
        Assert.True(rounds.Distinct().Count() > 1);
    }

    /// <summary>Player 0 owns the land left of x = 80 with a staffed tower, player 1 the rest; carriers to become scouts.</summary>
    private static (World World, AiPlayer Ai) ThiefSetup(string character = "thief")
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { PlayerCount = 2 };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(60, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        world.Step();
        for (int y = 0; y < 160; y++)
        for (int x = 0; x < 160; x++)
            map.SetOwner(new GridPoint(x, y), x < 80 ? 0 : 1);
        for (int i = 0; i < 3; i++)
            world.Spawn(new GridPoint(55 + i, 90), Profession.Carrier);
        var ai = new AiPlayer(world, Romans, 0, character: character);
        world.AiPlayers.Add(ai);
        return (world, ai);
    }

    [Fact]
    public void A_thief_sends_scouts_for_gold_at_the_edge_of_enemy_land_before_coal_and_brings_it_home()
    {
        var (world, ai) = ThiefSetup();
        world.AddLooseGoods(new GridPoint(84, 76), Material.Coal, 4);
        world.AddLooseGoods(new GridPoint(85, 86), Material.Gold, 1);
        world.AddLooseGoods(new GridPoint(120, 80), Material.Gold, 4); // deep in the enemy land

        for (int t = 0; t < 120 * World.TicksPerSecond && !world.Piles.Any(p => p.Material == Material.Gold && world.Map!.OwnerAt(p.Position) == 0); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Equal(2, world.Settlers.Count(s => s.Profession == Profession.Scout));
        Assert.Contains(ai.Log, l => l.Contains("thief: sends a scout for Gold at 85,86"));
        Assert.Contains(world.Piles, p => p.Material == Material.Gold && world.Map!.OwnerAt(p.Position) == 0);
        Assert.Equal(4, world.Piles.Single(p => p.Position == new GridPoint(120, 80)).Count);
    }

    [Fact]
    public void A_thief_leaves_goods_alone_while_enemy_soldiers_are_near()
    {
        var (world, ai) = ThiefSetup();
        world.AddLooseGoods(new GridPoint(85, 80), Material.Gold, 4);
        world.SpawnSoldier(new GridPoint(90, 82), SoldierType.Swordsman, level: 3, player: 1);

        for (int t = 0; t < 20 * World.TicksPerSecond; t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.DoesNotContain(ai.Log, l => l.Contains("thief: sends"));
        Assert.Equal(4, world.Piles.Single(p => p.Material == Material.Gold).Count);
    }

    [Fact]
    public void A_thief_steals_tools_from_a_storehouse_right_at_the_border()
    {
        var amazons = BuildingCatalog.Load(Civilization.Amazon);
        var map = new GameMap(160);
        var world = new World(map, ProductionConfig.Load()) { PlayerCount = 2, TerritoryRules = true };
        // The own military building far away: the distance counts from the own land.
        var tower = world.PlaceBuilding(amazons["tower"], 1, new GridPoint(150, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman, player: 1);
        world.Step();
        for (int y = 0; y < 160; y++)
        for (int x = 0; x < 160; x++)
            map.SetOwner(new GridPoint(x, y), x < 80 ? 0 : 1);
        var stock = world.PlaceBuilding(Romans["stock"], 0, new GridPoint(74, 80), force: true)!;
        world.AddLooseGoods(new GridPoint(60, 70), Material.Axe, 8);
        for (int i = 0; i < 4; i++)
            world.Spawn(new GridPoint(60 + i, 85), Profession.Carrier);
        for (int i = 0; i < 4; i++)
            world.Spawn(new GridPoint(105 + i, 90), Profession.Carrier, player: 1);
        for (int t = 0; t < 3000 && stock.Piles.Sum(p => p.Material == Material.Axe ? p.Count : 0) < 8; t++)
            world.Step();
        var ai = new AiPlayer(world, amazons, 1, character: "thief");
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 60 * World.TicksPerSecond && !world.Piles.Any(p => p.Material == Material.Axe && map.OwnerAt(p.Position) == 1); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Contains(world.Piles, p => p.Material == Material.Axe && map.OwnerAt(p.Position) == 1);
    }

    /// <summary>Player 0 (Romans: Convert costs 60) owns the land left of x = 80 with a staffed tower; a priest near the border.</summary>
    private static (World World, AiPlayer Ai, Settler Priest) SummonerSetup(int mana)
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { PlayerCount = 2 };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        world.Step();
        for (int y = 0; y < 160; y++)
        for (int x = 0; x < 160; x++)
            map.SetOwner(new GridPoint(x, y), x < 80 ? 0 : 1);
        var priest = world.Magic.SpawnPriest(new GridPoint(74, 80), 0)!;
        world.Magic.AddMana(0, mana);
        var ai = new AiPlayer(world, Romans, 0, character: "summoner");
        world.AiPlayers.Add(ai);
        return (world, ai, priest);
    }

    [Fact]
    public void A_summoner_spends_spare_mana_on_goods_away_from_the_border_near_its_priest()
    {
        var (world, ai, priest) = SummonerSetup(130); // 2 × 60 kept for converting, 10 for the summoning
        int piles = world.Piles.Count();

        for (int t = 0; t < 30 * World.TicksPerSecond && world.Piles.Count() == piles; t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        var summoned = world.Piles.Skip(piles).ToList();
        Assert.NotEmpty(summoned);
        Assert.Equal(120, world.Magic.Mana(0));
        Assert.All(summoned, p => Assert.True(p.Position.X <= 80 - 10, $"{p.Position} too near the border"));
        Assert.All(summoned, p => Assert.True(PathFinder.Distance(p.Position, new GridPoint(74, 80)) <= 12 + 5 + 1, $"{p.Position} too far from the priest"));
    }

    [Fact]
    public void A_summoner_keeps_the_mana_for_its_own_spell()
    {
        var (world, ai, _) = SummonerSetup(125); // 125 − 10 < 2 × 60

        for (int t = 0; t < 30 * World.TicksPerSecond; t++)
            world.Step();

        Assert.DoesNotContain(ai.Log, l => l.Contains("summoner:"));
        Assert.Equal(125, world.Magic.Mana(0));
    }

    /// <summary>An AI whose build order is done (only what the test places), measuring growth every 20 seconds.</summary>
    private static AiPlayer GrowingAi(World world, params (string, int)[] buildOrder) =>
        new(world, Romans, 0, AiConfig.Load(civilization: Civilization.Roman) with
        {
            BuildOrder = buildOrder,
            Growth = new AiConfig.GrowthRules(20 * World.TicksPerSecond, 0.25, 4, 0.7, 8),
        });

    [Fact]
    public void A_busy_weaponsmith_gets_another_one()
    {
        var map = new GameMap(120);
        var world = new World(map, TestConfig.NoTools);
        world.PlaceBuilding(Romans["weaponsmith"], 0, new GridPoint(50, 60));
        world.PlaceBuilding(Romans["stock"], 0, new GridPoint(30, 40));
        for (int i = 0; i < 20; i++)
            world.Spawn(new GridPoint(44, 66), Profession.Carrier);
        for (int i = 0; i < 4; i++)
        {
            world.AddLooseGoods(new GridPoint(56 + 2 * i, 70), Material.Iron, 8);
            world.AddLooseGoods(new GridPoint(56 + 2 * i, 74), Material.Coal, 8);
        }
        var ai = GrowingAi(world, ("weaponsmith", 1));
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 2 * 60 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("grows:")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Contains(ai.Log, l => l.Contains("grows: wants weaponsmith 2: weaponsmiths busy"));
    }

    [Fact]
    public void Iron_short_while_the_melt_waits_for_ore_brings_a_mine_not_a_melt()
    {
        var map = new GameMap(120);
        var world = new World(map, TestConfig.NoTools);
        world.PlaceBuilding(Romans["weaponsmith"], 0, new GridPoint(50, 60));
        world.PlaceBuilding(Romans["ironmelt"], 0, new GridPoint(70, 60));
        world.PlaceBuilding(Romans["stock"], 0, new GridPoint(30, 40));
        for (int i = 0; i < 12; i++)
            world.Spawn(new GridPoint(44, 66), Profession.Carrier);
        for (int i = 0; i < 4; i++)
            world.AddLooseGoods(new GridPoint(56 + 2 * i, 74), Material.Coal, 8);
        var ai = GrowingAi(world, ("weaponsmith", 1), ("ironmelt", 1), ("ironmine", 0));
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 90 * World.TicksPerSecond; t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Contains(ai.Log, l => l.Contains("grows: wants ironmine 1: IronOre short"));
        Assert.DoesNotContain(ai.Log, l => l.Contains("grows: wants ironmelt"));
        Assert.DoesNotContain(ai.Log, l => l.Contains("grows: wants weaponsmith"));
    }

    private static readonly BuildingCatalog Amazons = BuildingCatalog.Load(Civilization.Amazon);

    /// <summary>Player 0 (Amazons: Freeze) owns the land left of x = 80 with a staffed tower.</summary>
    private static World FreezeSetup()
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { PlayerCount = 2, Peoples = [Civilization.Amazon, Civilization.Roman] };
        var tower = world.PlaceBuilding(Amazons["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        world.Step();
        for (int y = 0; y < 160; y++)
        for (int x = 0; x < 160; x++)
            map.SetOwner(new GridPoint(x, y), x < 80 ? 0 : 1);
        return world;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frozen_enemies_count_nothing_in_the_balance(bool frozen)
    {
        var world = FreezeSetup();
        for (int i = 0; i < 3; i++)
            world.SpawnSoldier(new GridPoint(60 + i, 84), SoldierType.Swordsman);
        var enemies = Enumerable.Range(0, 4).Select(i => world.SpawnSoldier(new GridPoint(96 + i, 84), SoldierType.Swordsman, player: 1)).ToList();
        if (frozen)
        {
            var priest = world.Magic.SpawnPriest(new GridPoint(86, 84), 0)!;
            world.Magic.AddMana(0, 30);
            Assert.Equal(CastResult.Started, world.Magic.Cast([priest], new GridPoint(97, 84)));
            for (int t = 0; t < 5 * World.TicksPerSecond && enemies.Any(e => e.FrozenUntil <= world.Tick); t++)
                world.Step();
            Assert.All(enemies, e => Assert.True(e.FrozenUntil > world.Tick));
        }
        var ai = new AiPlayer(world, Amazons, 0, character: "standard");
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 3 * World.TicksPerSecond && !ai.Log.Any(l => l.Contains("strikes")); t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Equal(frozen, ai.Log.Any(l => l.Contains("strikes 4 enemies")));
    }

    [Fact]
    public void Priests_freeze_two_fights_at_once()
    {
        var world = FreezeSetup();
        // Two fights 30 fields apart, each three against three.
        foreach (int y in new[] { 60, 100 })
            for (int i = 0; i < 3; i++)
            {
                world.SpawnSoldier(new GridPoint(70 + i, y), SoldierType.Swordsman);
                world.SpawnSoldier(new GridPoint(70 + i, y + 2), SoldierType.Swordsman, player: 1);
            }
        world.Magic.SpawnPriest(new GridPoint(60, 60), 0);
        world.Magic.SpawnPriest(new GridPoint(60, 100), 0);
        var freeze = world.Magic.SpellOf(0)!;
        world.Magic.AddMana(0, world.Magic.CostOf(0, freeze) + world.Magic.CostOf(0, freeze, ahead: 1)); // the second one dearer
        var ai = new AiPlayer(world, Amazons, 0, character: "standard");
        world.AiPlayers.Add(ai);

        for (int t = 0; t < 10 * World.TicksPerSecond && ai.Log.Count(l => l.Contains("casts Freeze")) < 2; t++)
            world.Step();

        foreach (string line in ai.Log)
            output.WriteLine(line);
        var targets = ai.Log.Where(l => l.Contains("casts Freeze")).Select(l => int.Parse(l.Split(',')[^1].Split(' ')[0])).ToList();
        Assert.Equal(2, targets.Count);
        Assert.True(Math.Abs(targets[0] - targets[1]) >= 20, string.Join(", ", targets));
    }
}
