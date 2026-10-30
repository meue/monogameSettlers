using Homestead.Assets;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Map;
using Homestead.Simulation.Military;

namespace Homestead.Simulation.Tests;

public class CommandTests
{
    private static readonly BuildingCatalog Romans = BuildingCatalog.Load(Civilization.Roman);

    [Fact]
    public void Commands_take_effect_on_the_next_step_in_the_order_issued_with_their_results()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools);
        var soldier = world.SpawnSoldier(new GridPoint(10, 30), SoldierType.Swordsman)!;
        var move = world.Commands.Issue(new MoveUnits(0, [soldier.Id], new GridPoint(30, 30), Forced: false));
        var hold = world.Commands.Issue(new HoldPosition(0, [soldier.Id]));
        Assert.True(move.Sequence < hold.Sequence);
        Assert.Null(world.Military.MoveTarget(soldier));

        world.Step();
        Assert.Empty(world.Commands.Pending);
        Assert.Equal([move, hold], world.Commands.Applied.Select(a => a.Command));
        Assert.Equal((1, false), world.Commands.Applied[0].Result);
        // Applied in order: the hold came last.
        Assert.True(world.Military.Holds(soldier));

        world.Step();
        Assert.Empty(world.Commands.Applied);
    }

    [Fact]
    public void A_command_acts_only_on_what_belongs_to_its_player()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools);
        var enemy = world.SpawnSoldier(new GridPoint(10, 30), SoldierType.Swordsman, player: 1)!;
        var tower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(40, 40))!;
        world.Commands.Issue(new MoveUnits(0, [enemy.Id], new GridPoint(30, 30), Forced: false));
        world.Commands.Issue(new Demolish(0, tower.Id));
        world.Step();

        Assert.Equal((0, false), world.Commands.Applied[0].Result);
        Assert.Null(world.Military.MoveTarget(enemy));
        Assert.Equal(false, world.Commands.Applied[1].Result);
        Assert.True(tower.Exists);
    }

    [Fact]
    public void Commands_waiting_are_kept_in_a_saved_game()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools);
        var soldier = world.SpawnSoldier(new GridPoint(10, 30), SoldierType.Swordsman)!;
        world.Commands.Issue(new PatrolUnits(0, [soldier.Id], new GridPoint(30, 30)));

        var stream = new MemoryStream();
        Persistence.Snapshot.Write(stream, world);
        stream.Position = 0;
        var loaded = Persistence.Snapshot.Read<World>(stream);
        loaded.Step();
        Assert.Equal(new GridPoint(10, 30), loaded.Military.PatrolBack(loaded.Settlers.Single(s => s.Id == soldier.Id)));
    }
}
