using Homestead.Simulation.Buildings;
using Homestead.Simulation.Lockstep;
using Homestead.Simulation.Map.Generation;

namespace Homestead.Simulation.Tests;

public class GameFingerprintTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("homestead-data").FullName;

    public GameFingerprintTests()
    {
        // A copy of the game data, to change.
        foreach (string file in Directory.EnumerateFiles(BuildingCatalog.DefaultDataDirectory, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(_data, Path.GetRelativePath(BuildingCatalog.DefaultDataDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose() => Directory.Delete(_data, recursive: true);

    private string DataFile(string relative) => Path.Combine(_data, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void The_same_program_and_data_match()
    {
        var here = GameFingerprint.Of(_data);
        Assert.True(here.Matches(GameFingerprint.Of()));
        Assert.Contains("production.json", here.Files.Keys);
        Assert.Contains("buildings/roman.json", here.Files.Keys);
        Assert.DoesNotContain(here.Files.Keys, f => f.StartsWith("lang/") || f == "ui.json");
        Assert.Equal(GameFingerprint.CurrentProtocol, here.Protocol);
        Assert.NotEqual("unknown", here.Version);
        // Sent over the network, it stays the same.
        var back = GameFingerprint.FromJson(here.ToJson());
        Assert.True(here.Matches(back));
        Assert.Equal(here.Data, back.Data);
    }

    [Fact]
    public void Line_ends_and_a_byte_order_mark_do_not_count()
    {
        var before = GameFingerprint.Of(_data);
        string path = DataFile("production.json");
        string text = File.ReadAllText(path).Replace("\r\n", "\n");
        File.WriteAllText(path, text.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Empty(before.Differences(GameFingerprint.Of(_data)));
    }

    [Fact]
    public void Other_game_data_differ_texts_and_menus_do_not()
    {
        var before = GameFingerprint.Of(_data);
        File.AppendAllText(DataFile("lang/de.json"), " ");
        File.AppendAllText(DataFile("ui.json"), " ");
        Assert.True(before.Matches(GameFingerprint.Of(_data)));

        File.AppendAllText(DataFile("production.json"), " ");
        File.Delete(DataFile("buildings/handmade.json"));
        File.WriteAllText(DataFile("new.json"), "{}");
        var after = GameFingerprint.Of(_data);
        Assert.Equal(["data/buildings/handmade.json missing there", "data/new.json missing here", "data/production.json differs"],
            before.Differences(after).Order());
        Assert.NotEqual(before.Data, after.Data);
        Assert.Equal(["program version 1.0 ≠ " + before.Version, $"network protocol 99 ≠ {GameFingerprint.CurrentProtocol}"],
            (before with { Protocol = 99, Version = "1.0" }).Differences(before).Order().Reverse());
    }

    [Fact]
    public void A_game_set_up_on_other_data_does_not_start()
    {
        var setup = new GameSetup { Map = MapChoice.Generated(new RandomMapSettings(Players: 2), 1), Players = [new PlayerSetup(Human: true), new PlayerSetup()], Fingerprint = GameFingerprint.Of(_data) };
        var map = setup.Map.Open("");
        Assert.NotNull(setup.Start(map, _data).World);

        File.AppendAllText(DataFile("military.json"), " ");
        var refused = Assert.Throws<IncompatibleGameException>(() => setup.Start(map, _data));
        Assert.Equal(["data/military.json differs"], refused.Differences);
        // Sent as JSON, the setup still knows what it needs.
        Assert.Throws<IncompatibleGameException>(() => GameSetup.FromJson(setup.ToJson()).Start(map, _data));
    }
}
