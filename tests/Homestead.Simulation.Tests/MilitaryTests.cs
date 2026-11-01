using Homestead.Assets;
using Homestead.Assets.Maps;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Map;
using Homestead.Simulation.Military;
using Homestead.Simulation.Pathfinding;
using Xunit.Abstractions;

namespace Homestead.Simulation.Tests;

public class MilitaryTests(ITestOutputHelper output)
{
    private static readonly BuildingCatalog Romans = BuildingCatalog.Load(Civilization.Roman);

    [Fact]
    public void Weak_hits_still_hurt_armoured_soldiers()
    {
        // With little combat strength (many players) a hit does less than 2: halved and rounded down it was 0, and pikemen never fell.
        var pikeman = MilitaryConfig.Load().Soldiers[SoldierType.Pikeman];
        Assert.Equal(1, pikeman.Taken(1.3f));
        Assert.Equal(4, pikeman.Taken(9));
        Assert.Equal(0, pikeman.Taken(0)); // cursed arrows still do nothing
    }

    [Fact]
    public void Barracks_turn_carriers_and_weapons_into_soldiers()
    {
        var world = new World(new GameMap(70), TestConfig.NoTools);
        var barrack = world.PlaceBuilding(Romans["barrack"], 0, new GridPoint(30, 30))!;
        world.AddLooseGoods(new GridPoint(50, 50), Material.Sword, 2);
        world.AddLooseGoods(new GridPoint(52, 50), Material.Bow, 1);
        Spawn(world, 5, new GridPoint(40, 45));

        // Trained at the door, the moment the carrier gets there: nobody goes in.
        bool atDoor = true;
        int trained = 0;
        Run(world, 10_000, stopWhen: () =>
        {
            Assert.DoesNotContain(world.Settlers, s => s.IsInside);
            if (world.Military.SoldiersTrained > trained)
            {
                trained = world.Military.SoldiersTrained;
                atDoor &= world.Settlers.Any(s => s.Profession == Profession.Soldier && PathFinder.Distance(s.Position, barrack.Door) <= 1);
            }
            return world.Military.SoldiersTrained == 3;
        });
        Assert.True(atDoor, "the recruit becomes a soldier at the door");

        var soldiers = world.Settlers.Where(s => s.Profession == Profession.Soldier).ToList();
        Assert.Equal(3, soldiers.Count);
        Assert.Equal(2, soldiers.Count(s => s.SoldierType == SoldierType.Swordsman));
        Assert.Equal(1, soldiers.Count(s => s.SoldierType == SoldierType.Bowman));
        Assert.DoesNotContain(world.Piles, p => p.Material is Material.Sword or Material.Bow && p.Count > 0);
    }

    [Fact]
    public void Barracks_call_a_carrier_for_every_weapon_at_once()
    {
        var world = new World(new GameMap(70), TestConfig.NoTools);
        var barrack = world.PlaceBuilding(Romans["barrack"], 0, new GridPoint(30, 30))!;
        world.AddLooseGoods(new GridPoint(36, 36), Material.Sword, 8);
        Spawn(world, 20, new GridPoint(40, 45));

        int walking = 0;
        Run(world, 10_000, stopWhen: () =>
        {
            walking = Math.Max(walking, world.Settlers.Count(s => s.IsCarrier && s.Destination == barrack.Door));
            return world.Military.SoldiersTrained == 8;
        });

        output.WriteLine($"at most {walking} recruits on the way at the same time");
        Assert.Equal(8, world.Military.SoldiersTrained);
        Assert.True(walking >= 4, "one per weapon, not one after the other");
        Assert.Equal(12, world.Settlers.Count(s => s.IsCarrier));
    }

    [Fact]
    public void With_manual_recruitment_barracks_train_only_the_soldiers_planned()
    {
        var world = new World(new GameMap(70), TestConfig.NoTools);
        world.PlaceBuilding(Romans["barrack"], 0, new GridPoint(30, 30));
        world.AddLooseGoods(new GridPoint(50, 50), Material.Sword, 3);
        world.AddLooseGoods(new GridPoint(52, 50), Material.Bow, 2);
        Spawn(world, 6, new GridPoint(40, 45));
        world.Military.SetManualRecruitment(0, true);
        Assert.Equal(3, world.Military.WeaponsInStock(0, SoldierType.Swordsman));

        Run(world, 3_000);
        Assert.Equal(0, world.Military.SoldiersTrained); // nothing planned

        world.Military.ChangePlanned(0, SoldierType.Bowman, 1);
        world.Military.ChangePlanned(0, SoldierType.Swordsman, 5);
        world.Military.ChangePlanned(0, SoldierType.Swordsman, -4);
        Run(world, 10_000, stopWhen: () => world.Military.SoldiersTrained == 2);
        Run(world, 2_000);

        var soldiers = world.Settlers.Where(s => s.Profession == Profession.Soldier).ToList();
        Assert.Equal(1, soldiers.Count(s => s.SoldierType == SoldierType.Swordsman));
        Assert.Equal(1, soldiers.Count(s => s.SoldierType == SoldierType.Bowman));
        Assert.Equal(0, world.Military.Planned(0, SoldierType.Swordsman));
        Assert.Equal(0, world.Military.Planned(0, SoldierType.Bowman));

        // Back to automatic: the rest of the weapons are taken.
        world.Military.SetManualRecruitment(0, false);
        Run(world, 10_000, stopWhen: () => world.Military.SoldiersTrained == 5);
        Assert.Equal(5, world.Military.SoldiersTrained);
    }

    [Fact]
    public void New_soldiers_gather_in_the_work_area_of_their_barracks()
    {
        var world = new World(new GameMap(70), TestConfig.NoTools);
        var barrack = world.PlaceBuilding(Romans["barrack"], 0, new GridPoint(30, 30))!;
        var rally = world.SetWorkCenter(barrack, new GridPoint(40, 22))!.Value;
        world.AddLooseGoods(new GridPoint(50, 50), Material.Sword, 2);
        Spawn(world, 4, new GridPoint(40, 45));

        Run(world, 10_000, stopWhen: () => world.Military.SoldiersTrained == 2
                                         && world.Settlers.Where(s => s.Profession == Profession.Soldier).All(s => !s.HasTask));

        var soldiers = world.Settlers.Where(s => s.Profession == Profession.Soldier).ToList();
        Assert.Equal(2, soldiers.Count);
        Assert.All(soldiers, s => Assert.InRange(PathFinder.Distance(s.Position, rally), 0, 2));
    }

    [Fact]
    public void Soldiers_occupy_a_tower_and_it_claims_the_land_around_it()
    {
        var map = new GameMap(100);
        var world = new World(map, TestConfig.NoTools);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(50, 50))!;
        SpawnSoldiers(world, SoldierType.Swordsman, 1, new GridPoint(70, 70));
        SpawnSoldiers(world, SoldierType.Bowman, 3, new GridPoint(70, 75));
        Assert.False(world.Military.IsOccupied(tower));
        Assert.Null(map.OwnerAt(tower.Position));
        Assert.True(world.Military.ChangeWanted(tower, SoldierType.Bowman, 2));
        Assert.False(world.Military.ChangeWanted(tower, SoldierType.Bowman, 1)); // two bowman places only

        Run(world, 5_000, stopWhen: () => world.Military.IsOccupied(tower) && world.Military.Occupants(tower).All(s => s is not null));

        var occupants = world.Military.Occupants(tower);
        Assert.Equal(3, occupants.Count);
        Assert.All(occupants, s => Assert.True(s!.IsInside));
        Assert.Equal(2, occupants.Count(s => s!.SoldierType == SoldierType.Bowman)); // two bowman places only
        Assert.Contains(world.Settlers, s => s.SoldierType == SoldierType.Bowman && !s.IsInside);
        Assert.Equal(0, map.OwnerAt(new GridPoint(50 + 30, 50)));
        Assert.Null(map.OwnerAt(new GridPoint(50 + 31, 50)));
        Assert.Equal("3/3 soldiers", world.StatusOf(tower));
    }

    [Fact]
    public void A_new_tower_calls_one_swordsman_only()
    {
        var world = new World(new GameMap(100), TestConfig.NoTools);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(50, 50))!;
        SpawnSoldiers(world, SoldierType.Pikeman, 2, new GridPoint(60, 60));
        Run(world, 1_000);
        Assert.False(world.Military.IsOccupied(tower)); // no pikeman instead

        SpawnSoldiers(world, SoldierType.Swordsman, 3, new GridPoint(60, 65));
        Run(world, 3_000);

        Assert.Equal([SoldierType.Swordsman], world.Military.Occupants(tower).Where(s => s is not null).Select(s => s!.SoldierType!.Value));
    }

    [Fact]
    public void A_tower_calls_only_soldiers_within_reach()
    {
        var world = new World(new GameMap(250), TestConfig.NoTools);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(30, 30))!;
        int reach = world.Military.Config.GarrisonCallRadius;
        SpawnSoldiers(world, SoldierType.Swordsman, 1, new GridPoint(30 + reach + 10, 30));
        Run(world, 200);
        Assert.DoesNotContain(world.Military.Occupants(tower), s => s is not null);
        Assert.Contains("0/", world.StatusOf(tower));
        Assert.DoesNotContain("on the way", world.StatusOf(tower)); // the far soldier is not called

        SpawnSoldiers(world, SoldierType.Swordsman, 1, new GridPoint(30 + reach - 10, 30));
        Run(world, 5_000, stopWhen: () => world.Military.IsOccupied(tower));
        Assert.True(world.Military.IsOccupied(tower));
        Assert.Contains(world.Settlers, s => s.SoldierType == SoldierType.Swordsman && !s.IsInside
                                             && PathFinder.Distance(s.Position, tower.Door) > reach);
    }

    [Fact]
    public void Wanted_soldiers_never_leave_the_building_empty_or_overfull()
    {
        var world = new World(new GameMap(100), TestConfig.NoTools);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(50, 50))!;
        var military = world.Military;
        Assert.False(military.ChangeWanted(tower, SoldierType.Swordsman, -1)); // never empty
        Assert.True(military.ChangeWanted(tower, SoldierType.Bowman, 2));
        Assert.False(military.ChangeWanted(tower, SoldierType.Bowman, 1)); // two bowman places only
        Assert.True(military.ChangeWanted(tower, SoldierType.Bowman, -2));

        // The swordsman comes out only once the pikeman (replacing it on the only infantry place) is inside.
        SpawnSoldiers(world, SoldierType.Swordsman, 1, new GridPoint(52, 56));
        Run(world, 2_000, stopWhen: () => military.IsOccupied(tower));
        Assert.True(military.ChangeWanted(tower, SoldierType.Pikeman, 1));
        Assert.Equal(0, military.Wanted(tower, SoldierType.Swordsman));
        Assert.Equal(1, military.Wanted(tower, SoldierType.Pikeman));
        Run(world, 500);
        Assert.Equal(SoldierType.Swordsman, military.Occupants(tower).Single(s => s is not null)!.SoldierType);

        SpawnSoldiers(world, SoldierType.Pikeman, 1, new GridPoint(60, 60));
        Run(world, 3_000, stopWhen: () => military.Occupants(tower).All(s => s?.SoldierType != SoldierType.Swordsman)
                                          && military.IsOccupied(tower));
        Assert.Equal(SoldierType.Pikeman, military.Occupants(tower).Single(s => s is not null)!.SoldierType);
        Assert.Contains(world.Settlers, s => s.SoldierType == SoldierType.Swordsman && !s.IsInside && !s.HasTask);
    }

    [Fact]
    public void A_torn_down_tower_keeps_its_land_until_an_enemy_claims_it()
    {
        var map = new GameMap(120);
        var world = new World(map, TestConfig.NoTools);
        var west = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(30, 60))!;
        var east = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(60, 60))!;
        SpawnSoldiers(world, SoldierType.Swordsman, 2, new GridPoint(45, 80));
        Run(world, 5_000, stopWhen: () => world.Military.IsOccupied(west) && world.Military.IsOccupied(east));
        var mill = world.PlaceBuilding(Romans["mill"], 0, new GridPoint(80, 60))!;     // only the eastern tower covers it
        var sawmill = world.PlaceBuilding(Romans["sawmill"], 0, new GridPoint(50, 50))!; // the western one too

        world.RemoveBuilding(east);
        Run(world, 20);

        Assert.Equal(0, map.OwnerAt(new GridPoint(85, 60)));
        Assert.True(mill.Exists);
        Assert.Equal(1, world.Settlers.Count(s => s.Profession == Profession.Soldier && !s.IsInside && !s.HasTask)); // came out

        // An enemy tower takes the unprotected land, but not what the western tower covers.
        var enemy = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(100, 60), force: true)!;
        world.SpawnSoldier(enemy.Door, SoldierType.Swordsman, player: 1);
        Run(world, 2_000, stopWhen: () => world.Military.IsOccupied(enemy));

        Assert.Equal(1, map.OwnerAt(new GridPoint(85, 60)));
        Assert.False(mill.Exists);
        Assert.True(sawmill.Exists);
        Assert.Equal(0, map.OwnerAt(sawmill.Position));
    }

    [Fact]
    public void Buildings_need_own_land_when_territory_rules_apply()
    {
        var map = new GameMap(70);
        foreach (var p in PathFinder.Circle(new GridPoint(20, 20), 15).Where(map.Contains))
            map.SetOwner(p, 0);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true };

        Assert.True(world.CanPlace(Romans["sawmill"], new GridPoint(20, 20), out _, player: 0));
        Assert.False(world.CanPlace(Romans["sawmill"], new GridPoint(20, 20), out string reason, player: 1));
        Assert.Equal("not on own land", reason);
        Assert.False(world.CanPlace(Romans["sawmill"], new GridPoint(50, 50), out _, player: 0));
    }

    [Fact]
    public void Carriers_only_serve_their_own_player()
    {
        var world = new World(new GameMap(80), ProductionConfig.None);
        var site = world.PlaceConstructionSite(Romans["sawmill"], 1, new GridPoint(30, 30))!;
        world.AddLooseGoods(new GridPoint(40, 40), Material.Plank, 8);
        Spawn(world, 3, new GridPoint(45, 45)); // player 0

        Run(world, 1_000);
        Assert.Empty(world.Logistics.Jobs);

        world.Spawn(new GridPoint(50, 50), Profession.Carrier, player: 1);
        Run(world, 100);
        Assert.Single(world.Logistics.Jobs);
        Assert.Equal(1, world.Logistics.Jobs.First().Carrier.Player);
        Assert.NotNull(site);
    }

    [Fact]
    public void Territory_radii_reproduce_the_land_of_the_original_maps()
    {
        GameInstallation installation;
        try { installation = GameInstallation.Locate(); }
        catch (Exception) { output.WriteLine("No installation found, skipped."); return; }

        // Recompute the land from the military buildings alone (one soldier each) and compare with the owner bytes.
        long agree = 0, total = 0, missing = 0, extra = 0;
        int occupied = 0, military = 0;
        var maps = installation.MapFiles.Select(OriginalMap.Open)
            .Where(m => m.Buildings.Any(b => b.Type is 14 or 15 or 16)).Take(40).ToList();
        Assert.NotEmpty(maps);
        foreach (var original in maps)
        {
            var area = original.Area;
            var map = new GameMap(area.Size);
            var world = new World(map, ProductionConfig.None);
            foreach (var b in original.Buildings.Where(b => b.Type is 14 or 15 or 16))
            {
                string name = b.Type switch { 14 => "tower", 15 => "big_tower", _ => "castle" };
                var building = world.PlaceBuilding(Romans[name], b.Player, new GridPoint(b.X, b.Y), force: true);
                if (building is null)
                    continue;
                world.SpawnSoldier(building.Door, SoldierType.Swordsman, player: b.Player);
            }
            Run(world, 3_000, stopWhen: () => world.Buildings.All(world.Military.IsOccupied));
            military += world.Buildings.Count;
            occupied += world.Buildings.Count(world.Military.IsOccupied);
            for (int i = 0; i < map.PointCount; i++)
            {
                int stored = area.Owner[i];
                if (stored == GameMap.NoOwner && map.OwnerAt(map.Point(i)) is null)
                    continue;
                total++;
                var computed = map.OwnerAt(map.Point(i));
                if (computed == (stored == GameMap.NoOwner ? null : stored))
                    agree++;
                else if (computed is null)
                    missing++;
                else if (stored == GameMap.NoOwner)
                    extra++;
            }
        }
        output.WriteLine($"owned points agreeing: {agree}/{total} = {100.0 * agree / total:F1}%; not claimed {missing}, " +
                         $"claimed but not in the map {extra}, other owner {total - agree - missing - extra}; occupied {occupied}/{military}");
        // Land we claim must be the map's land (precision). Some maps also give players land that no
        // military building covers (set in the editor), so not every owned point is claimed.
        long claimed = total - missing;
        output.WriteLine($"precision {100.0 * agree / claimed:F1}%");
        Assert.Equal(military, occupied);
        Assert.True(agree >= claimed * 0.98);
    }

    [Fact]
    public void Two_soldiers_of_different_players_fight_until_one_is_dead()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools);
        var red = world.SpawnSoldier(new GridPoint(20, 30), SoldierType.Swordsman, level: 3)!;
        var blue = world.SpawnSoldier(new GridPoint(26, 30), SoldierType.Swordsman, level: 1, player: 1)!;

        Run(world, 3_000, stopWhen: () => red.IsDead || blue.IsDead);

        Assert.True(blue.IsDead, "the level 3 swordsman wins");
        Assert.False(red.IsDead);
        Assert.True(red.Health < world.Military.Config.MaxHealth(SoldierType.Swordsman, 3));
        Assert.Equal(1, world.Military.Kills);
        Assert.DoesNotContain(blue, world.Settlers);
        Run(world, 50);
        Assert.False(red.HasTask, "back to idle after the fight");
    }

    [Fact]
    public void The_history_samples_every_30_seconds_and_counts_kills_and_losses()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools) { PlayerCount = 2 };
        var red = world.SpawnSoldier(new GridPoint(20, 30), SoldierType.Swordsman, level: 3)!;
        var blue = world.SpawnSoldier(new GridPoint(26, 30), SoldierType.Swordsman, level: 1, player: 1)!;

        Run(world, GameHistory.SampleTicks * 2 + 1);

        Assert.True(blue.IsDead);
        Assert.Equal(3, world.History.Samples(0).Count); // at 0, 30 and 60 seconds
        var last0 = world.History.Samples(0)[^1];
        var last1 = world.History.Samples(1)[^1];
        Assert.Equal((1, 0, 1), (last0.Kills, last0.Losses, last0.Soldiers));
        Assert.Equal((0, 1, 0), (last1.Kills, last1.Losses, last1.Soldiers));
        Assert.Equal(1, world.History.Samples(1)[0].Soldiers);
    }

    [Fact]
    public void A_soldier_attacks_an_enemy_that_steps_next_to_it_instead_of_its_far_target()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools);
        var a = world.SpawnSoldier(new GridPoint(20, 30), SoldierType.Swordsman, level: 3)!;
        var far = world.SpawnSoldier(new GridPoint(30, 30), SoldierType.Bowman, player: 1)!;
        Run(world, 12); // a has picked the bowman and walks towards it
        Assert.True(a.HasTask);
        var blocker = world.SpawnSoldier(a.StepTarget.Neighbor(Direction.East), SoldierType.Pikeman, player: 1)!;
        float bowmanHealth = far.Health;

        Run(world, 1_000, stopWhen: () => blocker.IsDead);

        Assert.True(blocker.IsDead, "the soldier fought the enemy next to it");
        Assert.Equal(bowmanHealth, far.Health);
    }

    [Fact]
    public void Soldiers_sent_through_an_enemy_group_fight_it_and_then_reach_their_destination()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools);
        var own = Enumerable.Range(0, 4).Select(i => world.SpawnSoldier(new GridPoint(10 + i, 40), SoldierType.Swordsman, level: 3)!).ToList();
        var enemies = Enumerable.Range(0, 3).Select(i => world.SpawnSoldier(new GridPoint(38 + i, 40), SoldierType.Swordsman, player: 1)!).ToList();
        var destination = new GridPoint(65, 40);

        world.Military.Move(own, destination);
        Run(world, 6_000, stopWhen: () => enemies.All(e => e.IsDead) && own.Where(s => !s.IsDead).All(s => !s.HasTask));

        Assert.All(enemies, e => Assert.True(e.IsDead));
        var survivors = own.Where(s => !s.IsDead).ToList();
        Assert.NotEmpty(survivors);
        Assert.All(survivors, s => Assert.InRange(PathFinder.Distance(s.Position, destination), 0, 3));
    }

    [Fact]
    public void A_hospital_calls_wounded_soldiers_one_by_one_and_heals_them()
    {
        var world = new World(new GameMap(100), TestConfig.NoTools);
        var hospital = world.PlaceBuilding(Romans["hospital"], 0, new GridPoint(40, 40))!;
        Spawn(world, 1, new GridPoint(30, 55)); // becomes the healer
        var door = hospital.Door;
        // Two own soldiers win a fight each and are left wounded, within call range of the door.
        var wounded = new List<Settler>();
        foreach (int dy in new[] { -6, 6 })
        {
            wounded.Add(world.SpawnSoldier(new GridPoint(door.X + 12, door.Y + dy), SoldierType.Swordsman, level: 3)!);
            world.SpawnSoldier(new GridPoint(door.X + 15, door.Y + dy), SoldierType.Swordsman, level: 1, player: 1);
        }
        float max = world.Military.Config.MaxHealth(SoldierType.Swordsman, 3);
        Run(world, 3_000, stopWhen: () => !world.Settlers.Any(s => s.Player == 1));
        Assert.All(wounded, s => Assert.True(s.Health < max));

        var healedAt = new Dictionary<Settler, long>();
        Run(world, 5_000, stopWhen: () =>
        {
            foreach (var s in wounded.Where(s => s.Health >= max && !healedAt.ContainsKey(s)))
                healedAt[s] = world.Tick;
            return healedAt.Count == 2;
        });

        Assert.Equal(2, healedAt.Count);
        Assert.Equal(2, world.Production.HealsDone);
        Assert.True(Math.Abs(healedAt[wounded[0]] - healedAt[wounded[1]]) >= world.Military.Config.HospitalCallPauseTicks,
            "the next soldier is called only after the pause");
        Run(world, 20);
        // Healed at the door (the first one may have stepped aside for the second).
        Assert.All(wounded, s => Assert.InRange(PathFinder.Distance(s.Position, door), 0, 4));
    }

    [Fact]
    public void The_hospital_heals_the_weakest_first_and_leaves_soldiers_holding_their_position()
    {
        var world = new World(new GameMap(100), TestConfig.NoTools);
        var hospital = world.PlaceBuilding(Romans["hospital"], 0, new GridPoint(40, 40))!;
        Spawn(world, 1, new GridPoint(30, 55)); // becomes the healer
        var door = hospital.Door;
        var near = world.SpawnSoldier(new GridPoint(door.X + 5, door.Y), SoldierType.Swordsman)!;
        var far = world.SpawnSoldier(new GridPoint(door.X + 15, door.Y + 6), SoldierType.Swordsman)!;
        var holding = world.SpawnSoldier(new GridPoint(door.X + 3, door.Y + 4), SoldierType.Swordsman)!;
        SetHealth(near, 60);
        SetHealth(far, 20);
        SetHealth(holding, 10);
        world.Military.Hold([holding]);

        var healed = new List<Settler>();
        Run(world, 5_000, stopWhen: () =>
        {
            healed.AddRange(new[] { near, far }.Where(s => s.Health >= 100 && !healed.Contains(s)));
            return healed.Count == 2;
        });

        Assert.Equal([far, near], healed); // the weakest first, although further away
        Assert.Equal(10, holding.Health);
        Assert.True(world.Military.Holds(holding));
    }

    [Fact]
    public void Once_the_healer_is_out_the_soldier_is_healed_wherever_it_runs()
    {
        var world = new World(new GameMap(100), TestConfig.NoTools);
        var hospital = world.PlaceBuilding(Romans["hospital"], 0, new GridPoint(40, 40))!;
        Spawn(world, 1, new GridPoint(30, 55));
        var door = hospital.Door;
        var soldier = world.SpawnSoldier(new GridPoint(door.X + 12, door.Y), SoldierType.Swordsman, level: 3)!;
        world.SpawnSoldier(new GridPoint(door.X + 15, door.Y), SoldierType.Swordsman, level: 1, player: 1);
        float max = world.Military.Config.MaxHealth(SoldierType.Swordsman, 3);
        Run(world, 3_000, stopWhen: () => !world.Settlers.Any(s => s.Player == 1));
        Assert.True(soldier.Health < max);
        var healer = world.Settlers.Single(s => s.Trade == "HEALER");

        Run(world, 3_000, stopWhen: () => healer.Activity == Activity.Healing);
        Assert.Equal(Activity.Healing, healer.Activity);
        Assert.Equal(door.Neighbor(Direction.SouthWest), healer.Position); // a step down-left of the door
        Assert.Equal(Direction.SouthEast, healer.Facing); // turned to the soldier down-right of him
        Assert.True(soldier.Health < max);
        world.Military.Move([soldier], new GridPoint(door.X + 20, door.Y + 10)); // runs off at once

        Run(world, world.Military.Config.HealTicks + 5);

        Assert.Equal(max, soldier.Health);
        Assert.True(PathFinder.Distance(soldier.Position, door) > 1, "healed although it ran off");
        Run(world, 200, stopWhen: () => healer.IsInside);
        Assert.True(healer.IsInside, "the healer goes back in");
    }

    [Fact]
    public void Bowmen_in_a_tower_shoot_at_enemies_in_range()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 40))!;
        SpawnSoldiers(world, SoldierType.Bowman, 2, new GridPoint(45, 50));
        world.Military.ChangeWanted(tower, SoldierType.Bowman, 2);
        world.Military.ChangeWanted(tower, SoldierType.Swordsman, -1);
        Run(world, 3_000, stopWhen: () => world.Military.Occupants(tower).Count(s => s is not null) == 2);
        var enemy = world.SpawnSoldier(new GridPoint(46, 40), SoldierType.Bowman, player: 1)!; // 6 fields away; cannot break the door

        Run(world, 3_000, stopWhen: () => enemy.IsDead);

        Assert.True(enemy.IsDead);
        Assert.Equal(2, world.Military.Occupants(tower).Count(s => s is not null));
    }

    [Fact]
    public void Swordsmen_walk_faster_than_pikemen_and_pikemen_faster_than_bowmen()
    {
        int TicksFor(SoldierType type)
        {
            var world = new World(new GameMap(80), TestConfig.NoTools);
            var soldier = world.SpawnSoldier(new GridPoint(10, 40), type)!;
            Assert.True(world.WalkTo(soldier, new GridPoint(70, 40)));
            int ticks = 0;
            while (soldier.Position != new GridPoint(70, 40) && ticks < 10_000)
            {
                world.Step();
                ticks++;
            }
            return ticks;
        }
        int sword = TicksFor(SoldierType.Swordsman), pike = TicksFor(SoldierType.Pikeman), bow = TicksFor(SoldierType.Bowman);

        Assert.InRange(bow / (double)sword, 1.07, 1.13); // 10 % faster
        Assert.InRange(bow / (double)pike, 1.03, 1.07); // 5 % faster
    }

    [Fact]
    public void Bowmen_in_a_tower_shoot_further_than_on_the_ground()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools);
        var config = world.Military.Config;
        int ground = config.Soldiers[SoldierType.Bowman].Range;
        Assert.True(config.TowerRange > ground);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(30, 40))!;
        SpawnSoldiers(world, SoldierType.Bowman, 1, new GridPoint(35, 50));
        world.Military.ChangeWanted(tower, SoldierType.Bowman, 1);
        world.Military.ChangeWanted(tower, SoldierType.Swordsman, -1);
        Run(world, 3_000, stopWhen: () => world.Military.Occupants(tower).Any(s => s is { SoldierType: SoldierType.Bowman }));
        // A geologist (does not fight back, stands still) beyond the ground range, within the tower range.
        var geologist = world.Spawn(new GridPoint(30 + config.TowerRange, 40), Profession.Geologist, player: 1)!;
        Assert.InRange(PathFinder.Distance(tower.Position, geologist.Position), ground + 1, config.TowerRange);

        Run(world, 3_000, stopWhen: () => geologist.IsDead);

        Assert.True(geologist.IsDead);
    }

    [Fact]
    public void Attackers_take_an_enemy_tower_with_its_land()
    {
        var map = new GameMap(120);
        var world = new World(map, TestConfig.NoTools);
        var enemyTower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(80, 60))!;
        var enemyMill = world.PlaceBuilding(Romans["mill"], 1, new GridPoint(95, 70))!;
        world.SpawnSoldier(enemyTower.Door, SoldierType.Swordsman, player: 1);
        Run(world, 2_000, stopWhen: () => world.Military.IsOccupied(enemyTower));
        Assert.Equal(1, map.OwnerAt(new GridPoint(100, 60)));

        var ownTower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(45, 60), force: true)!;
        SpawnSoldiers(world, SoldierType.Swordsman, 5, new GridPoint(52, 68));
        Run(world, 3_000, stopWhen: () => world.Military.Occupants(ownTower).Count(s => s is not null) == 1 && world.Settlers.Count(s => s.Player == 0 && !s.HasTask && !s.IsInside) == 4);
        int sent = world.Military.Attack(0, enemyTower);
        Assert.Equal(4, sent); // the one in the own tower stays

        Run(world, 20_000, stopWhen: () => enemyTower.Player == 0);

        Assert.Equal(0, enemyTower.Player);
        Assert.True(world.Military.IsOccupied(enemyTower));
        Assert.Equal(1, world.Military.Captures);
        Assert.Equal(0, map.OwnerAt(new GridPoint(100, 60)));
        Assert.False(enemyMill.Exists, "the enemy mill stood on land that changed sides");
        Assert.DoesNotContain(world.Settlers, s => s.Player == 1 && s.Profession == Profession.Soldier);
    }

    [Fact]
    public void Idle_soldiers_take_an_enemy_tower_within_sight_on_their_own()
    {
        var world = new World(new GameMap(100), TestConfig.NoTools);
        var enemyTower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(50, 50))!;
        world.SpawnSoldier(enemyTower.Door, SoldierType.Swordsman, player: 1);
        Run(world, 2_000, stopWhen: () => world.Military.IsOccupied(enemyTower));
        var door = enemyTower.Door;
        var far = world.SpawnSoldier(new GridPoint(door.X - 30, door.Y), SoldierType.Swordsman, level: 3)!;
        Run(world, 200);
        Assert.False(far.HasTask, "a tower out of sight is left alone");

        var near = Enumerable.Range(0, 3).Select(i => world.SpawnSoldier(new GridPoint(door.X - 10 + i, door.Y + 2), SoldierType.Swordsman, level: 3)!).ToList();
        Run(world, 20_000, stopWhen: () => enemyTower.Player == 0);

        Assert.Equal(0, enemyTower.Player);
        Assert.Equal(1, world.Military.Captures);
        Assert.Contains(near, s => s.IsInside);
    }

    [Fact]
    public void A_soldier_goes_for_a_nearer_enemy_tower_before_a_farther_enemy_soldier()
    {
        var world = new World(new GameMap(100), TestConfig.NoTools);
        var enemyTower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(40, 50))!;
        world.SpawnSoldier(enemyTower.Door, SoldierType.Swordsman, player: 1);
        Run(world, 2_000, stopWhen: () => world.Military.IsOccupied(enemyTower));
        var door = enemyTower.Door;
        var start = new GridPoint(door.X + 4, door.Y);
        var soldier = world.SpawnSoldier(start, SoldierType.Swordsman, level: 3)!;
        world.SpawnSoldier(new GridPoint(door.X + 14, door.Y), SoldierType.Swordsman, player: 1);

        Run(world, 60);

        Assert.True(PathFinder.Distance(soldier.Position, door) < PathFinder.Distance(start, door), "it heads for the door");
    }

    [Fact]
    public void Soldiers_moving_past_an_enemy_tower_take_it_unless_the_move_is_forced()
    {
        foreach (bool forced in new[] { false, true })
        {
            var world = new World(new GameMap(100), TestConfig.NoTools);
            var enemyTower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(50, 40))!;
            world.SpawnSoldier(enemyTower.Door, SoldierType.Swordsman, player: 1);
            Run(world, 2_000, stopWhen: () => world.Military.IsOccupied(enemyTower));
            var y = enemyTower.Door.Y + 8;
            var own = Enumerable.Range(0, 3).Select(i => world.SpawnSoldier(new GridPoint(10 + i, y), SoldierType.Swordsman, level: 3)!).ToList();

            world.Military.Move(own, new GridPoint(90, y), forced);
            Run(world, 20_000, stopWhen: () => enemyTower.Player == 0 || own.All(s => !s.HasTask));

            Assert.Equal(forced ? 1 : 0, enemyTower.Player);
        }
    }

    [Fact]
    public void A_defender_goes_back_into_its_place_after_winning()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 40))!;
        world.SpawnSoldier(new GridPoint(50, 50), SoldierType.Swordsman, level: 3);
        Run(world, 3_000, stopWhen: () => world.Military.IsOccupied(tower));
        var attacker = world.SpawnSoldier(new GridPoint(60, 60), SoldierType.Swordsman, player: 1)!;
        var enemyTower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(65, 25), force: true)!;
        world.Military.Attack(1, tower);

        Run(world, 10_000, stopWhen: () => attacker.IsDead && world.Military.Occupants(tower).Any(s => s is not null));

        Assert.True(attacker.IsDead);
        Assert.Equal(0, tower.Player);
        Assert.Single(world.Military.Occupants(tower), s => s is not null);
        Assert.NotNull(enemyTower);
    }

    [Fact]
    public void Defenders_stay_inside_until_the_door_is_broken_then_one_stands_in_it()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools);
        var tower = world.PlaceBuilding(Romans["big_tower"], 0, new GridPoint(40, 40))!;
        Assert.True(world.Military.ChangeWanted(tower, SoldierType.Swordsman, 1));
        SpawnSoldiers(world, SoldierType.Swordsman, 2, new GridPoint(45, 50));
        Run(world, 3_000, stopWhen: () => world.Military.Occupants(tower).Count(s => s is not null) == 2);

        var attacker = world.SpawnSoldier(new GridPoint(tower.Door.X + 5, tower.Door.Y + 5), SoldierType.Swordsman, level: 3, player: 1)!;
        world.Military.Attack([attacker], tower);
        Run(world, 2_000, stopWhen: () => world.Military.DoorGuard(tower) is not null);

        var guard = world.Military.DoorGuard(tower);
        Assert.NotNull(guard);
        Assert.Equal(0, world.Military.DoorHealth(tower), 0);
        Assert.Single(world.Military.Occupants(tower), s => s is not null); // the other one waits inside
        // It steps out in front of the door and stays there.
        var post = world.Military.FightPoint(tower);
        Assert.Equal(1, PathFinder.Distance(post, tower.Door));
        Run(world, 100, stopWhen: () => guard.Position == post && guard.State != SettlerState.Stepping);
        Assert.Equal(post, guard.Position);
        Run(world, 2_000, stopWhen: () => guard.IsDead || attacker.IsDead);
        Assert.Equal(post, guard.Position);
    }

    [Fact]
    public void Bowmen_do_not_attack_buildings_neither_by_order_nor_on_their_own()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 40))!;
        world.SpawnSoldier(new GridPoint(45, 50), SoldierType.Swordsman);
        Run(world, 3_000, stopWhen: () => world.Military.IsOccupied(tower));
        var start = new GridPoint(tower.Door.X + 10, tower.Door.Y + 10); // the door within sight
        var bowman = world.SpawnSoldier(start, SoldierType.Bowman, player: 1)!;
        Run(world, 200);
        Assert.False(bowman.HasTask);

        Assert.Equal(0, world.Military.Attack([bowman], tower));
        Assert.Equal(0, world.Military.Attack(1, tower));
        Run(world, 400);

        Assert.False(bowman.HasTask);
        Assert.True(PathFinder.Distance(bowman.Position, tower.Door) >= 9, "the bowman stays where it was");
        Assert.Equal(world.Military.Config.DoorHealth, world.Military.DoorHealth(tower));
    }

    [Fact]
    public void Bowmen_step_back_from_enemies_nearer_than_their_minimum_range_and_never_shoot_them()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools);
        var stats = world.Military.Config.Soldiers[SoldierType.Bowman];
        Assert.True(stats.MinRange > 1);
        var bowman = world.SpawnSoldier(new GridPoint(40, 40), SoldierType.Bowman)!;
        // A geologist (does not fight back, stands still) right next to it.
        var geologist = world.Spawn(new GridPoint(41, 40), Profession.Geologist, player: 1)!;

        int nearestShot = int.MaxValue;
        for (int t = 0; t < 3_000 && !geologist.IsDead; t++)
        {
            long shot = bowman.LastShotTick;
            world.Step();
            if (bowman.LastShotTick != shot)
                nearestShot = Math.Min(nearestShot, PathFinder.Distance(bowman.Position, geologist.Position));
        }

        Assert.True(geologist.IsDead);
        Assert.InRange(nearestShot, stats.MinRange, stats.Range);
    }

    [Theory]
    [InlineData("tower", 2)]
    [InlineData("castle", 1)]
    public void Only_a_few_melee_attackers_fight_at_the_door_and_pikemen_go_right_up_to_it(string name, int fighters)
    {
        var world = new World(new GameMap(100), TestConfig.NoTools);
        var building = world.PlaceBuilding(Romans[name], 0, new GridPoint(40, 40))!;
        world.SpawnSoldier(new GridPoint(45, 55), SoldierType.Swordsman, level: 3);
        Run(world, 3_000, stopWhen: () => world.Military.IsOccupied(building));
        var attackers = new List<Settler>();
        for (int i = 0; i < 4; i++)
            attackers.Add(world.SpawnSoldier(new GridPoint(building.Door.X + 8 + i, building.Door.Y + 8), SoldierType.Pikeman, player: 1)!);
        world.Military.Attack(attackers, building);

        int most = 0, longest = 0;
        for (int t = 0; t < 8_000 && building.Player == 0; t++)
        {
            world.Step();
            var fighting = attackers.Where(a => !a.IsDead && a.Activity == Activity.Fighting).ToList();
            most = Math.Max(most, fighting.Count);
            longest = Math.Max(longest, fighting.Select(a => a.ActivityTicks).DefaultIfEmpty(0).Max());
            var post = world.Military.FightPoint(building);
            Assert.All(fighting, a => Assert.Equal(1, PathFinder.Distance(a.Position, post)));
        }

        Assert.Equal(1, building.Player);
        Assert.Equal(fighters, most);
        Assert.True(longest > 100, "the fighting animation runs on instead of starting over every tick");
    }

    [Fact]
    public void Door_fight_fields_come_from_the_config_unless_one_is_not_walkable()
    {
        var points = new DoorPoints(new GridPoint(1, 2), [new GridPoint(2, 3), new GridPoint(1, 3), new GridPoint(0, 3)]);
        var config = MilitaryConfig.Load() with
        {
            DoorPoints = new Dictionary<(Civilization, string), DoorPoints> { [(Civilization.Roman, "tower")] = points },
        };
        var world = new World(new GameMap(80), TestConfig.NoTools, config);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 40))!;
        world.SpawnSoldier(new GridPoint(45, 55), SoldierType.Swordsman);
        Run(world, 3_000, stopWhen: () => world.Military.IsOccupied(tower));

        Assert.Equal(tower.Door + points.Guard, world.Military.FightPoint(tower));
        Assert.Equal([tower.Door + new GridPoint(2, 3), tower.Door + new GridPoint(1, 3)], world.Military.DoorSpots(tower)); // 2 for a tower

        var walled = new World(new GameMap(80), TestConfig.NoTools, config with
        {
            DoorPoints = new Dictionary<(Civilization, string), DoorPoints> { [(Civilization.Roman, "tower")] = points with { Guard = new GridPoint(0, -1) } },
        });
        var other = walled.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 40))!;
        walled.SpawnSoldier(new GridPoint(45, 55), SoldierType.Swordsman);
        Run(walled, 3_000, stopWhen: () => walled.Military.IsOccupied(other));
        Assert.Equal(other.Door + new GridPoint(1, 1), walled.Military.FightPoint(other)); // inside the tower: found automatically
    }

    [Fact]
    public void A_bowman_in_the_broken_door_shoots_far_but_only_melee_at_the_door_can_fight_it()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 40))!;
        world.Military.ChangeWanted(tower, SoldierType.Bowman, 1);
        world.Military.ChangeWanted(tower, SoldierType.Swordsman, -1);
        world.SpawnSoldier(new GridPoint(45, 50), SoldierType.Bowman, level: 3);
        Run(world, 3_000, stopWhen: () => world.Military.IsOccupied(tower));
        var archer = world.SpawnSoldier(new GridPoint(tower.Door.X + 7, tower.Door.Y + 7), SoldierType.Bowman, level: 3, player: 1)!; // beyond the minimum range, near enough to keep the door broken
        var breaker = world.SpawnSoldier(new GridPoint(tower.Door.X + 7, tower.Door.Y + 6), SoldierType.Swordsman, player: 1)!;
        world.Military.Attack([archer, breaker], tower);
        Run(world, 3_000, stopWhen: () => world.Military.DoorGuard(tower) is not null);
        var guard = world.Military.DoorGuard(tower)!;
        Assert.Equal(SoldierType.Bowman, guard.SoldierType);
        world.Military.Move([breaker], new GridPoint(70, 70), forced: true);
        Run(world, 40);
        float guardHealth = guard.Health, archerHealth = archer.Health;

        Run(world, 400, stopWhen: () => archer.IsDead);

        Assert.Equal(world.Military.FightPoint(tower), guard.Position);
        Assert.True(archer.IsDead || archer.Health < archerHealth, "the guard shoots from the door");
        Assert.Equal(guardHealth, guard.Health); // bowmen cannot hurt it
    }

    [Fact]
    public void The_attacker_that_strikes_the_last_defender_down_takes_the_building()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 40))!;
        world.SpawnSoldier(new GridPoint(45, 50), SoldierType.Swordsman);
        Run(world, 3_000, stopWhen: () => world.Military.IsOccupied(tower));
        var attackers = new List<Settler>();
        for (int i = 0; i < 3; i++)
            attackers.Add(world.SpawnSoldier(new GridPoint(tower.Door.X + 6 + i, tower.Door.Y + 6), SoldierType.Swordsman, level: 3, player: 1)!);
        world.Military.Attack(attackers, tower);
        Settler? guard = null;
        Run(world, 3_000, stopWhen: () => (guard ??= world.Military.DoorGuard(tower)) is not null);
        Assert.NotNull(guard);
        Run(world, 3_000, stopWhen: () => guard.IsDead);
        Assert.True(guard.IsDead);

        // Taken in the same tick: the killer is inside at once.
        Assert.Equal(1, tower.Player);
        var killer = world.Military.Occupants(tower).Single(s => s is not null)!;
        Assert.Contains(killer, attackers);
        Assert.True(killer.IsInside);
        Assert.Equal(tower.Door, killer.Position);
        Run(world, 1);
        Assert.All(attackers.Where(a => a != killer && !a.IsDead), a => Assert.False(a.HasTask));
    }

    [Fact]
    public void A_taken_tower_stands_when_the_neighbouring_enemy_tower_is_taken_too()
    {
        var map = new GameMap(120);
        var world = new World(map, TestConfig.NoTools);
        var first = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(60, 60))!;
        var second = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(72, 60))!;
        world.SpawnSoldier(first.Door, SoldierType.Swordsman, player: 1);
        world.SpawnSoldier(second.Door, SoldierType.Swordsman, player: 1);
        Run(world, 3_000, stopWhen: () => world.Military.IsOccupied(first) && world.Military.IsOccupied(second));
        Assert.Equal(1, map.OwnerAt(first.Position));

        var attackers = Enumerable.Range(0, 4)
            .Select(i => world.SpawnSoldier(new GridPoint(40 + i, 70), SoldierType.Swordsman, level: 3)!).ToList();
        world.Military.Attack(attackers, first);
        Run(world, 20_000, stopWhen: () => first.Player == 0);
        Assert.Equal(0, first.Player);
        Assert.Equal(0, map.OwnerAt(first.Position)); // its own ground, although the second tower still covers it

        world.Military.Attack(attackers.Where(a => !a.IsDead && !a.IsInside), second);
        Run(world, 20_000, stopWhen: () => second.Player == 0);

        Assert.Equal(0, second.Player);
        Assert.True(first.Exists, "the tower taken first went down with the land of the second");
        Assert.Equal(0, first.Player);
    }

    [Fact]
    public void A_calm_door_takes_its_guard_back_in_and_repairs_itself_evenly()
    {
        var world = new World(new GameMap(80), TestConfig.NoTools);
        var config = world.Military.Config;
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 40))!;
        world.SpawnSoldier(new GridPoint(45, 50), SoldierType.Swordsman, level: 3);
        Run(world, 3_000, stopWhen: () => world.Military.IsOccupied(tower));
        var post = world.Military.FightPoint(tower);
        var attacker = world.SpawnSoldier(new GridPoint(tower.Door.X + 5, tower.Door.Y + 5), SoldierType.Swordsman, level: 3, player: 1)!;

        // Break the door, then run off as soon as its guard stands in front of it.
        void BreakAndLeave()
        {
            world.Military.Attack([attacker], tower);
            Run(world, 2_000, stopWhen: () => world.Military.DoorGuard(tower) is { } g && g.Position == post && g.State != SettlerState.Stepping);
            Assert.Equal(0, world.Military.DoorHealth(tower));
            world.Military.Move([attacker], new GridPoint(70, 70), forced: true);
            Run(world, 400, stopWhen: () => PathFinder.Distance(attacker.Position, post) > config.DoorCalmRadius);
        }

        BreakAndLeave();
        Run(world, config.DoorCalmTicks - 10);
        Assert.Equal(0, world.Military.DoorHealth(tower));
        Assert.NotNull(world.Military.DoorGuard(tower)); // still calm for less than 5 s
        Run(world, 20);
        Assert.InRange(world.Military.DoorHealth(tower), 0.01f, 3);
        Run(world, 40, stopWhen: () => world.Military.DoorGuard(tower) is null);
        Assert.Null(world.Military.DoorGuard(tower));
        Assert.Single(world.Military.Occupants(tower), s => s is not null);

        // Soon after the repair began, a single hit breaks it again.
        Assert.True(world.Military.DoorHealth(tower) < config.Damage(SoldierType.Swordsman, 3) / 2);
        BreakAndLeave();

        Run(world, config.DoorCalmTicks + config.DoorRegenTicks / 2);
        Assert.Equal(config.DoorHealth / 2, world.Military.DoorHealth(tower), config.DoorHealth * 0.03f); // half after 15 s
        Run(world, config.DoorRegenTicks / 2 + 10);
        Assert.Equal(config.DoorHealth, world.Military.DoorHealth(tower));
    }

    private static void Spawn(World world, int count, GridPoint near)
    {
        for (int i = 0; i < count; i++)
            Assert.NotNull(world.Spawn(new GridPoint(near.X + i, near.Y), Profession.Carrier));
    }

    [Fact]
    public void Combat_strength_starts_at_a_share_per_player_away_from_own_land_and_rises_with_gold()
    {
        var map = new GameMap(60);
        var world = new World(map, TestConfig.NoTools) { PlayerCount = 2, Peoples = [Civilization.Roman, Civilization.Egyptian] };
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 60; x++)
                map.SetOwner(new GridPoint(x, y), y < 15 ? 0 : 1);

        Assert.Equal(0.5f, world.Military.CombatStrength(0));
        Assert.Equal(1f, world.Military.CombatStrengthAt(0, new GridPoint(20, 5)));    // own land
        Assert.Equal(0.5f, world.Military.CombatStrengthAt(0, new GridPoint(20, 20))); // enemy land
        Assert.Equal(0.5f, world.Military.CombatStrengthAt(0, new GridPoint(20, 40))); // neutral land

        world.AddLooseGoods(new GridPoint(20, 5), Material.Gold, 8);
        world.AddLooseGoods(new GridPoint(20, 40), Material.Gold, 8); // not on own land
        world.AddLooseGoods(new GridPoint(22, 5), Material.Gems, 8);  // Romans have no use for gems
        world.Step();
        Assert.Equal(0.5308f, world.Military.CombatStrength(0), 4); // 2.6 bars per point from 50 %

        // Egyptians: gold counts half, gems double.
        world.AddLooseGoods(new GridPoint(20, 20), Material.Gold, 4);
        world.AddLooseGoods(new GridPoint(22, 20), Material.Gems, 3);
        world.Step();
        Assert.Equal(0.5308f, world.Military.CombatStrength(1), 4); // 4 × 0.5 + 3 × 2

        // No upper limit: 466 bars buy 100 %, then 20 bars per point.
        for (int x = 0; x < 50; x++)
        {
            world.AddLooseGoods(new GridPoint(5 + x, 8), Material.Gold, 8);
            world.AddLooseGoods(new GridPoint(5 + x, 10), Material.Gold, 8);
        }
        world.Step();
        Assert.Equal(1.171f, world.Military.CombatStrength(0), 4); // 808 bars: 100 % + 342 / 20
    }

    [Fact]
    public void Each_further_percent_of_combat_strength_costs_more()
    {
        var strength = new CombatStrengthConfig(10, [1, 2, 4], new Dictionary<Material, float>(), new Dictionary<Civilization, IReadOnlyDictionary<Material, float>>());

        Assert.Equal(0.05f, strength.Raise(0, 5), 4);
        Assert.Equal(0.10f, strength.Raise(0, 10), 4);
        Assert.Equal(0.15f, strength.Raise(0, 20), 4);  // 10 + 5 × 2
        Assert.Equal(0.25f, strength.Raise(0, 50), 4);  // 10 + 20 + 5 × 4 (the last band goes on)
        Assert.Equal(1.375f, strength.Raise(0, 500), 4);   // no upper limit: 20 + 470 / 4
        Assert.Equal(0.3f, strength.Raise(0.3f, 0), 4);
        // The bands count from 0 %: a lower start buys more with the same goods.
        Assert.Equal(0.125f, strength.Raise(0.05f, 10), 4); // 5 × 1, then 5 / 2
        Assert.Equal(0.20f, strength.Raise(0.15f, 10), 4);  // 5 × 2
    }

    [Fact]
    public void Combat_strength_starts_by_the_number_of_players_as_listed()
    {
        var strength = CombatStrengthConfig.None with { StartPercent = [100, 50, 42, 37] };

        Assert.Equal(1f, strength.Start(1), 4);
        Assert.Equal(0.42f, strength.Start(3), 4);
        Assert.Equal(0.37f, strength.Start(4), 4);
        Assert.Equal(0.37f, strength.Start(9), 4); // more players than listed: the last value
        Assert.Equal(0.25f, CombatStrengthConfig.None.Start(4), 4); // nothing listed: 1 / players
    }

    [Fact]
    public void A_soldier_on_own_land_beats_an_equal_one_from_away()
    {
        var map = new GameMap(60);
        var world = new World(map, TestConfig.NoTools) { PlayerCount = 2 };
        for (int y = 0; y < 60; y++)
            for (int x = 0; x < 60; x++)
                map.SetOwner(new GridPoint(x, y), 1);
        var guest = world.SpawnSoldier(new GridPoint(20, 30), SoldierType.Swordsman, level: 1)!;
        var host = world.SpawnSoldier(new GridPoint(26, 30), SoldierType.Swordsman, level: 1, player: 1)!;

        Run(world, 3_000, stopWhen: () => guest.IsDead || host.IsDead);

        Assert.True(guest.IsDead, "half the combat strength away from own land");
        Assert.False(host.IsDead);
    }

    [Fact]
    public void Holding_soldiers_chase_nobody_but_strike_enemies_within_reach()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools);
        var holder = world.SpawnSoldier(new GridPoint(20, 30), SoldierType.Swordsman)!;
        var archer = world.SpawnSoldier(new GridPoint(20, 40), SoldierType.Bowman)!;
        Assert.Equal(2, world.Military.Hold([holder, archer]));
        // An enemy in sight but out of reach: the swordsman stays, the bowman shoots from where he stands.
        var enemy = world.SpawnSoldier(new GridPoint(26, 34), SoldierType.Pikeman, level: 3, player: 1)!;
        world.Military.Hold([enemy]);
        float health = enemy.Health;
        Run(world, 300);
        Assert.Equal(new GridPoint(20, 30), holder.Position);
        Assert.Equal(new GridPoint(20, 40), archer.Position);
        Assert.True(enemy.Health < health, "the bowman shoots within his range");
        Assert.True(world.Military.Holds(holder));
        Assert.Null(world.Military.MoveTarget(holder));

        // Next to it, the swordsman strikes.
        var other = world.SpawnSoldier(new GridPoint(21, 30), SoldierType.Swordsman, player: 1)!;
        Run(world, 600, stopWhen: () => other.IsDead);
        Assert.True(other.IsDead);
        Assert.Equal(new GridPoint(20, 30), holder.Position);

        // A new order ends the hold.
        world.Military.Move([holder], new GridPoint(10, 30));
        Assert.False(world.Military.Holds(holder));
    }

    [Fact]
    public void A_tower_does_not_call_in_soldiers_holding_their_position()
    {
        var world = new World(new GameMap(70), TestConfig.NoTools);
        world.PlaceBuilding(Romans["tower"], 0, new GridPoint(30, 30));
        var soldier = world.SpawnSoldier(new GridPoint(36, 36), SoldierType.Swordsman)!;
        world.Military.Hold([soldier]);
        Run(world, 600);
        Assert.False(soldier.IsInside);
        Assert.Equal(new GridPoint(36, 36), soldier.Position);
        world.Military.Move([soldier], soldier.Position);
        Run(world, 1200, stopWhen: () => soldier.IsInside);
        Assert.True(soldier.IsInside, "free again, it is called in");
    }

    [Fact]
    public void Pikemen_take_half_of_every_hit_rounded_down()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools) { PlayerCount = 2 };
        var pikeman = world.SpawnSoldier(new GridPoint(20, 30), SoldierType.Pikeman)!;
        var swordsman = world.SpawnSoldier(new GridPoint(23, 30), SoldierType.Swordsman, player: 1)!;
        world.SpawnSoldier(new GridPoint(20, 40), SoldierType.Bowman, player: 1);
        // Nobody's land: half the combat strength with two players (sword 12 → 6 → 3, bow 4 → 2 → 1, spear 9 → 4.5).
        float strength = world.Military.CombatStrength(1);
        var config = world.Military.Config;
        float sword = MathF.Floor(config.Damage(SoldierType.Swordsman, 1) * strength / 2);
        float bow = MathF.Floor(config.Damage(SoldierType.Bowman, 1) * strength / 2);
        var taken = new HashSet<float>();
        var dealt = new HashSet<float>();
        float pikemanHealth = pikeman.Health, swordsmanHealth = swordsman.Health;

        Run(world, 3_000, stopWhen: () =>
        {
            if (pikeman.Health < pikemanHealth)
                taken.Add(pikemanHealth - pikeman.Health);
            if (swordsman.Health < swordsmanHealth)
                dealt.Add(swordsmanHealth - swordsman.Health);
            (pikemanHealth, swordsmanHealth) = (pikeman.Health, swordsman.Health);
            return pikeman.IsDead || swordsman.IsDead;
        });

        Assert.Equal(new HashSet<float> { sword, bow }, taken.Select(t => (float)Math.Round(t, 3)).ToHashSet());
        Assert.Equal([config.Damage(SoldierType.Pikeman, 1) * strength], dealt.Select(d => (float)Math.Round(d, 3)));
    }

    [Fact]
    public void Soldiers_sent_into_a_lake_walk_to_its_shore()
    {
        var map = new GameMap(70);
        for (int y = 30; y < 50; y++)
        for (int x = 30; x < 50; x++)
            map.SetTerrain(new GridPoint(x, y), TerrainType.Water);
        var world = new World(map, TestConfig.NoTools);
        SpawnSoldiers(world, SoldierType.Swordsman, 3, new GridPoint(10, 40));
        var soldiers = world.Settlers.Where(s => s.Profession == Profession.Soldier).ToList();

        // Near the west shore: they gather on it, next to the shore field nearest to the target.
        world.Military.Move(soldiers, new GridPoint(32, 40));
        Run(world, 3_000, stopWhen: () => soldiers.All(s => s.Destination is null && s.Position.X >= 25));

        // Idle soldiers keep a field apart.
        Assert.All(soldiers, s => Assert.InRange(PathFinder.Distance(s.Position, new GridPoint(29, 40)), 0, 4));
        Assert.All(soldiers, s => Assert.True(s.Position.X < 30 || s.Position.Y < 30 || s.Position.Y >= 50));
    }

    [Fact]
    public void Waypoints_are_walked_in_turn_after_the_order_and_a_fourth_replaces_the_last()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools);
        var soldier = world.SpawnSoldier(new GridPoint(10, 30), SoldierType.Swordsman)!;
        world.Military.Move([soldier], new GridPoint(20, 30));
        world.Military.AddWaypoint([soldier], new GridPoint(20, 40));
        world.Military.AddWaypoint([soldier], new GridPoint(30, 40));
        world.Military.AddWaypoint([soldier], new GridPoint(30, 30));
        world.Military.AddWaypoint([soldier], new GridPoint(40, 30));
        Assert.Equal([new GridPoint(20, 40), new GridPoint(30, 40), new GridPoint(40, 30)], world.Military.Waypoints(soldier));

        var stops = new[] { new GridPoint(20, 30), new GridPoint(20, 40), new GridPoint(30, 40), new GridPoint(40, 30) };
        int reached = 0;
        Run(world, 5_000, stopWhen: () =>
        {
            if (reached < stops.Length && PathFinder.Distance(soldier.Position, stops[reached]) <= 1)
                reached++;
            Assert.False(PathFinder.Distance(soldier.Position, new GridPoint(30, 30)) == 0, "the replaced waypoint is not walked to");
            return !soldier.HasTask;
        });
        Assert.Equal(stops.Length, reached);
        Assert.Empty(world.Military.Waypoints(soldier));
    }

    [Fact]
    public void A_waypoint_without_an_order_is_walked_to_at_once()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools);
        var soldier = world.SpawnSoldier(new GridPoint(10, 30), SoldierType.Swordsman)!;
        world.Military.AddWaypoint([soldier], new GridPoint(25, 30));
        Assert.Equal(new GridPoint(25, 30), world.Military.MoveTarget(soldier));
        Run(world, 2_000, stopWhen: () => !soldier.HasTask);
        Assert.InRange(PathFinder.Distance(soldier.Position, new GridPoint(25, 30)), 0, 1);
    }

    [Fact]
    public void Patrolling_soldiers_walk_to_and_fro_until_another_order()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools);
        var soldier = world.SpawnSoldier(new GridPoint(10, 30), SoldierType.Swordsman)!;
        world.Military.Patrol([soldier], new GridPoint(25, 30));
        Assert.Equal(new GridPoint(10, 30), world.Military.PatrolBack(soldier));

        // There, back, and there again.
        int legs = 0;
        var next = new GridPoint(25, 30);
        Run(world, 6_000, stopWhen: () =>
        {
            if (soldier.Position == next)
            {
                legs++;
                next = next == new GridPoint(25, 30) ? new GridPoint(10, 30) : new GridPoint(25, 30);
            }
            return legs == 3;
        });
        Assert.Equal(3, legs);
        Assert.True(soldier.HasTask);

        world.Military.Move([soldier], new GridPoint(15, 40));
        Assert.Null(world.Military.PatrolBack(soldier));
    }

    [Fact]
    public void Patrolling_soldiers_fight_enemies_on_the_way_and_patrol_on()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools);
        var soldier = world.SpawnSoldier(new GridPoint(10, 30), SoldierType.Swordsman, level: 3)!;
        world.Military.Patrol([soldier], new GridPoint(40, 30));
        var enemy = world.SpawnSoldier(new GridPoint(25, 33), SoldierType.Swordsman, player: 1)!;
        world.Military.Hold([enemy]);

        Run(world, 3_000, stopWhen: () => enemy.IsDead);
        Assert.True(enemy.IsDead);
        Assert.NotNull(world.Military.PatrolBack(soldier));
        Run(world, 3_000, stopWhen: () => soldier.Position == new GridPoint(40, 30));
        Assert.Equal(new GridPoint(40, 30), soldier.Position);
    }

    [Fact]
    public void Waypoints_and_patrols_are_kept_in_a_saved_game()
    {
        var world = new World(new GameMap(60), TestConfig.NoTools);
        var walker = world.SpawnSoldier(new GridPoint(10, 20), SoldierType.Swordsman)!;
        var patrol = world.SpawnSoldier(new GridPoint(10, 40), SoldierType.Pikeman)!;
        world.Military.Move([walker], new GridPoint(20, 20));
        world.Military.AddWaypoint([walker], new GridPoint(30, 25), forced: true);
        world.Military.Patrol([patrol], new GridPoint(30, 40));
        Run(world, 50);

        var stream = new MemoryStream();
        Homestead.Simulation.Persistence.Snapshot.Write(stream, world);
        stream.Position = 0;
        var loaded = Homestead.Simulation.Persistence.Snapshot.Read<World>(stream);
        var loadedWalker = loaded.Settlers.Single(s => s.Id == walker.Id);
        var loadedPatrol = loaded.Settlers.Single(s => s.Id == patrol.Id);
        Assert.Equal([new GridPoint(30, 25)], loaded.Military.Waypoints(loadedWalker));
        Assert.Equal(new GridPoint(10, 40), loaded.Military.PatrolBack(loadedPatrol));
    }

    private static void SpawnSoldiers(World world, SoldierType type, int count, GridPoint near)
    {
        for (int i = 0; i < count; i++)
            Assert.NotNull(world.SpawnSoldier(new GridPoint(near.X + i, near.Y), type));
    }

    /// <summary>Wounds a soldier at once (the health is the simulation's own).</summary>
    private static void SetHealth(Settler soldier, float health) => typeof(Settler).GetProperty(nameof(Settler.Health))!.SetValue(soldier, health);

    private static void Run(World world, int ticks, Func<bool>? stopWhen = null)
    {
        for (int t = 0; t < ticks && stopWhen?.Invoke() != true; t++)
            world.Step();
    }
}
