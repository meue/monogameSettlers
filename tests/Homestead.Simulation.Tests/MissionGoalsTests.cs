using Homestead.Simulation.Buildings;
using Homestead.Simulation.Map;

namespace Homestead.Simulation.Tests;

public class MissionGoalsTests
{
    private static readonly BuildingCatalog Romans = BuildingCatalog.Load(Civilization.Roman);

    private static void Seconds(World world, int seconds)
    {
        for (int t = 0; t < seconds * World.TicksPerSecond; t++)
            world.Step();
    }

    [Fact]
    public void A_mission_is_won_once_no_other_player_has_a_building()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools) { Goals = new MissionGoals() };
        world.PlaceBuilding(Romans["sawmill"], 0, new GridPoint(20, 20), force: true);
        var first = world.PlaceBuilding(Romans["sawmill"], 1, new GridPoint(50, 50), force: true)!;
        var second = world.PlaceBuilding(Romans["lumberjack"], 2, new GridPoint(60, 30), force: true)!;
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Open, world.Goals.OutcomeOf(0));

        world.RemoveBuilding(first);
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Open, world.Goals.OutcomeOf(0));

        world.RemoveBuilding(second);
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Won, world.Goals.OutcomeOf(0));
        Assert.InRange(world.Goals.DecidedAt(0), 1, world.Tick);

        // Decided once: losing everything afterwards changes nothing.
        foreach (var building in world.Buildings.ToList())
            world.RemoveBuilding(building);
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Won, world.Goals.OutcomeOf(0));
    }

    [Fact]
    public void A_mission_is_won_with_an_allied_building_standing_once_the_enemies_are_gone()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools) { Goals = new MissionGoals(), PlayerCount = 3 };
        world.Teams.AddRange([1, 1, 2]);
        world.PlaceBuilding(Romans["sawmill"], 0, new GridPoint(20, 20), force: true);
        world.PlaceBuilding(Romans["sawmill"], 1, new GridPoint(50, 50), force: true);
        var enemy = world.PlaceBuilding(Romans["lumberjack"], 2, new GridPoint(60, 30), force: true)!;
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Open, world.Goals.OutcomeOf(0)); // an enemy building still blocks

        world.RemoveBuilding(enemy);
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Won, world.Goals.OutcomeOf(0)); // an allied one does not
    }

    [Fact]
    public void A_mission_is_lost_without_an_own_building()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools) { Goals = new MissionGoals() };
        var own = world.PlaceBuilding(Romans["sawmill"], 0, new GridPoint(20, 20), force: true)!;
        world.PlaceBuilding(Romans["sawmill"], 1, new GridPoint(50, 50), force: true);
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Open, world.Goals.OutcomeOf(0));

        world.RemoveBuilding(own);
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Lost, world.Goals.OutcomeOf(0));
    }

    [Fact]
    public void Free_games_have_no_goals()
    {
        var world = new World(new GameMap(40), TestConfig.NoTools);
        Seconds(world, 2);
        Assert.Null(world.Goals);
    }

    [Fact]
    public void A_mission_starting_without_buildings_is_lost_only_once_the_settlement_falls()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools) { Goals = new MissionGoals() };
        Assert.NotNull(world.Spawn(new GridPoint(20, 20), Profession.Carrier, 0));
        world.PlaceBuilding(Romans["sawmill"], 1, new GridPoint(50, 50), force: true);
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Open, world.Goals.OutcomeOf(0));

        var own = world.PlaceBuilding(Romans["sawmill"], 0, new GridPoint(25, 30), force: true)!;
        Seconds(world, 2);
        world.RemoveBuilding(own);
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Lost, world.Goals.OutcomeOf(0));
    }
}
