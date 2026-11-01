using Homestead.Simulation.Ai;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Map;

namespace Homestead.Simulation.Tests;

public class ConquestGoalsTests
{
    private static readonly BuildingCatalog Romans = BuildingCatalog.Load(Civilization.Roman);

    private static void Seconds(World world, int seconds)
    {
        for (int t = 0; t < seconds * World.TicksPerSecond; t++)
            world.Step();
    }

    [Fact]
    public void A_free_game_is_won_once_every_other_player_lost_all_military_buildings()
    {
        var world = new World(new GameMap(120), TestConfig.NoTools) { Goals = new ConquestGoals() };
        world.PlaceBuilding(Romans["tower"], 0, new GridPoint(20, 20), force: true);
        var first = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(80, 80), force: true)!;
        var second = world.PlaceBuilding(Romans["tower"], 2, new GridPoint(90, 30), force: true)!;
        world.PlaceBuilding(Romans["sawmill"], 2, new GridPoint(100, 40), force: true);
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Open, world.Goals.OutcomeOf(0));

        world.RemoveBuilding(first);
        Seconds(world, 2);
        Assert.True(world.Goals.IsDefeated(1));
        Assert.Equal(MissionOutcome.Open, world.Goals.OutcomeOf(0));

        // Other buildings do not keep a player in the game.
        world.RemoveBuilding(second);
        Seconds(world, 2);
        Assert.True(world.Goals.IsDefeated(2));
        Assert.Equal(MissionOutcome.Won, world.Goals.OutcomeOf(0));
    }

    [Fact]
    public void A_free_game_is_lost_with_the_last_own_military_building()
    {
        var world = new World(new GameMap(120), TestConfig.NoTools) { Goals = new ConquestGoals() };
        var own = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(20, 20), force: true)!;
        world.PlaceBuilding(Romans["sawmill"], 0, new GridPoint(30, 30), force: true);
        world.PlaceBuilding(Romans["tower"], 1, new GridPoint(80, 80), force: true);
        Seconds(world, 2);
        world.RemoveBuilding(own);
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Lost, world.Goals.OutcomeOf(0));
        Assert.False(world.Goals.IsDefeated(1));
    }

    [Fact]
    public void Every_player_has_its_own_outcome()
    {
        var world = new World(new GameMap(120), TestConfig.NoTools) { Goals = new ConquestGoals() };
        world.Teams.AddRange([0, 1, 1]);
        world.PlaceBuilding(Romans["tower"], 0, new GridPoint(20, 20), force: true);
        var second = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(80, 80), force: true)!;
        var third = world.PlaceBuilding(Romans["tower"], 2, new GridPoint(90, 30), force: true)!;
        Seconds(world, 2);
        world.RemoveBuilding(second);
        Seconds(world, 2);
        // Player 2 is out, its ally player 3 still in: nobody has won yet.
        Assert.Equal(MissionOutcome.Lost, world.Goals.OutcomeOf(1));
        Assert.Equal(MissionOutcome.Open, world.Goals.OutcomeOf(0));
        Assert.Equal(MissionOutcome.Open, world.Goals.OutcomeOf(2));
        Assert.Equal(-1, world.Goals.DecidedAt(0));

        world.RemoveBuilding(third);
        Seconds(world, 2);
        Assert.Equal(MissionOutcome.Won, world.Goals.OutcomeOf(0));
        Assert.Equal(MissionOutcome.Lost, world.Goals.OutcomeOf(2));
        Assert.InRange(world.Goals.DecidedAt(2), world.Goals.DecidedAt(1) + 1, world.Tick);

        // The cheat decides only for the one who uses it; decided stays decided.
        world.Goals.Decide(1, MissionOutcome.Won, world.Tick);
        Assert.Equal(MissionOutcome.Lost, world.Goals.OutcomeOf(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_defeated_computer_player_stops_thinking(bool defeated)
    {
        var map = new GameMap(120);
        for (int y = 60; y < 100; y++)
        for (int x = 60; x < 100; x++)
            map.SetOwner(new GridPoint(x, y), 1);
        var world = new World(map, TestConfig.NoTools) { Goals = new ConquestGoals() };
        world.PlaceBuilding(Romans["tower"], 0, new GridPoint(20, 20), force: true);
        var tower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(80, 80), force: true)!;
        Seconds(world, 2);
        if (defeated)
        {
            world.RemoveBuilding(tower);
            Seconds(world, 2);
        }
        Assert.Equal(defeated, world.Goals.IsDefeated(1));
        var ai = new AiPlayer(world, Romans, 1);
        world.AiPlayers.Add(ai);
        Seconds(world, 30);
        // The survey of the own land is the first thing a thinking computer player does.
        Assert.Equal(!defeated, ai.FreeLand > 0);
    }
}
