using Homestead.Simulation.Buildings;
using Homestead.Simulation.Magic;
using Homestead.Simulation.Map;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Tests;

/// <summary>Teams (<see cref="World.Teams"/>): allies do not fight, bewitch, take land from or outlast each other.</summary>
public class TeamTests
{
    private static readonly BuildingCatalog Romans = BuildingCatalog.Load(Civilization.Roman);

    private static World Teams(int size, params int[] teams)
    {
        var world = new World(new GameMap(size), TestConfig.NoTools) { PlayerCount = teams.Length };
        world.Teams.AddRange(teams);
        return world;
    }

    [Fact]
    public void Only_players_on_the_same_team_are_allies()
    {
        var world = Teams(20, 1, 1, 0, 0, 2);
        Assert.True(world.AreAllies(0, 1));
        Assert.True(world.AreAllies(2, 2));
        Assert.False(world.AreAllies(2, 3)); // no team: against everybody
        Assert.False(world.AreAllies(0, 4));
        Assert.False(world.AreAllies(0, 7)); // beyond the list: no team
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void Soldiers_side_by_side_fight_only_enemies(int otherTeam, bool fight)
    {
        var world = Teams(60, 1, otherTeam);
        var own = world.SpawnSoldier(new GridPoint(30, 30), SoldierType.Swordsman)!;
        var other = world.SpawnSoldier(new GridPoint(32, 30), SoldierType.Swordsman, player: 1)!;

        Run(world, 10 * World.TicksPerSecond);

        Assert.Equal(fight, own.Health < world.MaxHealth(0, SoldierType.Swordsman, 1) || own.IsDead || other.IsDead
                            || other.Health < world.MaxHealth(1, SoldierType.Swordsman, 1));
    }

    [Fact]
    public void Nobody_attacks_an_allys_tower()
    {
        var world = Teams(80, 1, 1);
        var tower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(40, 40), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman, player: 1);
        Run(world, 20);
        var soldier = world.SpawnSoldier(new GridPoint(40, 55), SoldierType.Swordsman)!;

        Assert.Equal(0, world.Military.Attack([soldier], tower));
        Assert.Equal(0, world.Military.Attack(0, tower));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void Idle_soldiers_go_for_towers_in_sight_only_of_enemies(int otherTeam, bool attack)
    {
        var world = Teams(80, 1, otherTeam);
        var tower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(40, 40), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman, player: 1);
        Run(world, 20);
        var soldier = world.SpawnSoldier(new GridPoint(tower.Door.X + 2, tower.Door.Y + 4), SoldierType.Swordsman, level: 3)!;

        Run(world, 3 * World.TicksPerSecond);

        Assert.Equal(attack, soldier.HasTask);
    }

    [Fact]
    public void Spells_against_enemies_spare_allies()
    {
        var world = Teams(60, 1, 1, 2);
        var ally = world.SpawnSoldier(new GridPoint(30, 30), SoldierType.Swordsman, player: 1)!;
        var enemy = world.SpawnSoldier(new GridPoint(32, 30), SoldierType.Swordsman, player: 2)!;
        world.Peoples = [Civilization.Amazon, Civilization.Roman, Civilization.Roman];
        var freeze = world.Magic.SpellsOf(0).Single(s => s.Kind == SpellKind.Freeze);

        Assert.False(world.Magic.Affects(freeze, 0, ally));
        Assert.True(world.Magic.Affects(freeze, 0, enemy));
    }

    [Fact]
    public void Pioneers_and_towers_take_no_land_of_an_ally()
    {
        var world = Teams(80, 1, 1);
        var map = world.Map!;
        for (int y = 0; y < 80; y++)
            for (int x = 40; x < 80; x++)
                map.SetOwner(new GridPoint(x, y), 1);

        Assert.False(world.Pioneering.IsClaimable(new GridPoint(45, 40), 0));
        world.PlaceBuilding(Romans["tower"], 0, new GridPoint(36, 40), force: true);
        world.SpawnSoldier(new GridPoint(36, 46), SoldierType.Swordsman);
        Run(world, 30 * World.TicksPerSecond);

        Assert.Equal(1, map.OwnerAt(new GridPoint(42, 40)));
        Assert.Equal(0, map.OwnerAt(new GridPoint(34, 40)));
    }

    [Fact]
    public void A_free_game_is_won_once_the_enemies_are_defeated_while_allies_stand()
    {
        var world = Teams(120, 1, 1, 2);
        world.Goals = new ConquestGoals();
        world.PlaceBuilding(Romans["tower"], 0, new GridPoint(20, 20), force: true);
        world.PlaceBuilding(Romans["tower"], 1, new GridPoint(60, 60), force: true);
        var enemy = world.PlaceBuilding(Romans["tower"], 2, new GridPoint(100, 100), force: true)!;
        Run(world, 2 * World.TicksPerSecond);
        Assert.Equal(MissionOutcome.Open, world.Goals.OutcomeOf(0));

        world.RemoveBuilding(enemy);
        Run(world, 2 * World.TicksPerSecond);

        Assert.Equal(MissionOutcome.Won, world.Goals.OutcomeOf(0));
    }

    [Fact]
    public void Allies_share_their_sight()
    {
        var world = Teams(80, 1, 1, 0);
        world.Spawn(new GridPoint(60, 20), player: 1);
        world.Spawn(new GridPoint(20, 60), player: 2);
        var vision = new Vision(80, player: 0);

        vision.Update(world);

        Assert.True(vision.IsVisible(new GridPoint(60, 20)));
        Assert.False(vision.IsVisible(new GridPoint(20, 60)), "an enemy gives no sight");
        Assert.True(world.Military.Sees(0, new GridPoint(60, 20)));
    }

    [Fact]
    public void A_scout_revealed_by_an_ally_is_revealed_to_the_whole_team()
    {
        var world = Teams(80, 1, 1, 2);
        var scout = world.Spawn(new GridPoint(40, 40), Profession.Scout, player: 2)!;
        world.SpawnSoldier(new GridPoint(45, 40), SoldierType.Swordsman, player: 1);

        Run(world, 5);

        Assert.True(scout.IsRevealedTo(1));
        Assert.True(scout.IsRevealedTo(0));
    }

    private static void Run(World world, int ticks)
    {
        for (int t = 0; t < ticks; t++)
            world.Step();
    }
}
