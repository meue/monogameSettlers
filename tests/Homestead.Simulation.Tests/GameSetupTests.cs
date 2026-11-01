using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Magic;
using Homestead.Simulation.Map.Generation;

namespace Homestead.Simulation.Tests;

public class GameSetupTests
{
    private static GameSetup Setup(int seed = 7) => new()
    {
        Map = MapChoice.Generated(new RandomMapSettings(Players: 3), 4),
        Seed = seed,
        Players =
        [
            new PlayerSetup(Human: true, Color: 2),
            new PlayerSetup(People: Civilization.Asian, Team: 1, Hardcore: true),
            new PlayerSetup(Team: 1, Character: "general"),
        ],
        Positions = [null, 0, null],
        Settings = GameSettings.Defaults(ProductionConfig.Load(), MagicConfig.Load()) with { Depletion = new Depletion(50, 0, 25), SpellCostIncrease = 10 },
    };

    private static GameStart Start(GameSetup setup) => setup.Start(setup.Map.Open(mapDirectory: ""));

    private static void Seconds(World world, int seconds)
    {
        for (int t = 0; t < seconds * World.TicksPerSecond; t++)
            world.Step();
    }

    [Fact]
    public void The_same_setup_gives_the_same_game_also_after_sending_it_as_json()
    {
        var setup = Setup();
        string json = setup.ToJson();
        var received = GameSetup.FromJson(json);
        Assert.Equal(json, received.ToJson());

        var here = Start(setup);
        var there = Start(received);
        Assert.Equal(here.Positions, there.Positions);
        Assert.Equal(Enumerable.Range(0, 3).Select(here.World.PeopleOf), Enumerable.Range(0, 3).Select(there.World.PeopleOf));
        Assert.Equal(here.World.AiPlayers.Select(a => a.Character.Id), there.World.AiPlayers.Select(a => a.Character.Id));
        Seconds(here.World, 90);
        Seconds(there.World, 90);
        Assert.Equal(WorldChecksum.Of(here.World), WorldChecksum.Of(there.World));
    }

    [Fact]
    public void The_world_is_set_up_as_the_setup_says()
    {
        var start = Start(Setup());
        var world = start.World;
        Assert.Equal(7, world.Seed);
        // Humans play themselves; the others are computer players with the character asked for.
        Assert.Equal([1, 2], world.AiPlayers.Select(a => a.Player));
        Assert.Equal("general", world.AiPlayers.Single(a => a.Player == 2).Character.Id);
        Assert.Equal(Civilization.Asian, world.PeopleOf(1));
        Assert.True(world.IsHardcore(1));
        Assert.False(world.IsHardcore(0));
        Assert.True(world.AreAllies(1, 2));
        Assert.False(world.AreAllies(0, 1));
        Assert.IsType<ConquestGoals>(world.Goals);
        // Start position 2 (map player 1) is the human's; the others drawn from what is left.
        Assert.Equal(1, start.Positions![0]);
        Assert.Equal([0, 1, 2], start.Positions.Order());
        Assert.Equal(new Depletion(50, 0, 25), world.Production.Config.Depletion);
        Assert.Equal(10, world.Magic.Config.CostIncrease);
    }

    [Fact]
    public void With_more_than_one_human_cheats_and_the_ai_chat_do_nothing()
    {
        var alone = Start(Setup()).World;
        Assert.False(alone.Multiplayer);
        var start = Start(Setup() with { Players = [new PlayerSetup(Human: true), new PlayerSetup(Human: true), new PlayerSetup()] });
        var world = start.World;
        Assert.True(world.Multiplayer);
        var at = world.Buildings.First(b => b.Player == 0).Door;
        int settlers = world.Settlers.Count;
        var cheat = world.Commands.Issue(new Commands.Cheat(0, Commands.CheatKind.Swordsmen, at));
        var reply = world.Commands.Issue(new Commands.AiReply(2, Commands.AiReplyKind.Assault, 0));
        world.Step();
        Assert.Equal(settlers, world.Settlers.Count);
        Assert.Contains(world.Commands.Applied, a => a.Command == cheat && a.Result is null);
        Assert.Contains(world.Commands.Applied, a => a.Command == reply && a.Result is false);
    }

    [Fact]
    public void Each_draw_has_its_own_random_numbers()
    {
        // Other start positions do not change the peoples drawn.
        var setup = Setup() with { Players = [new PlayerSetup(Human: true), new PlayerSetup(), new PlayerSetup()] };
        var moved = setup with { Positions = [2, 1, 0] };
        Assert.Equal(Enumerable.Range(0, 3).Select(Start(setup).World.PeopleOf), Enumerable.Range(0, 3).Select(Start(moved).World.PeopleOf));
    }

    [Fact]
    public void An_original_map_is_found_by_its_hash()
    {
        string directory = Directory.CreateTempSubdirectory("homestead-maps").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Multi"));
            string first = Path.Combine(directory, "Multi", "first.map");
            string second = Path.Combine(directory, "second.MAP");
            File.WriteAllText(first, "first");
            File.WriteAllText(second, "second");

            var choice = MapChoice.Original(first, directory);
            Assert.Equal("Multi/first.map", choice.MapFile);
            Assert.Equal(first, choice.Locate(directory));
            // Somewhere else (another installation, renamed) it is found by the hash; another file of that name is not it.
            Assert.Equal(second, (choice with { Hash = MapChoice.HashOf(second) }).Locate(directory));
            Assert.Throws<FileNotFoundException>(() => (choice with { Hash = "00" }).Locate(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
