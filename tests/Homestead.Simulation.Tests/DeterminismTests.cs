using Homestead.Simulation.Ai;
using Homestead.Simulation.Map.Generation;

namespace Homestead.Simulation.Tests;

public class DeterminismTests
{
    private const int Minutes = 25;

    /// <summary>
    /// The same game twice gives the same world, minute by minute. With HOMESTEAD_CHECKSUM_FILE set, the checksums are
    /// written there, one line per minute: the build compares them between Linux, Windows (x64) and macOS (ARM).
    /// </summary>
    [Fact]
    public void A_game_simulates_alike_every_time()
    {
        var first = Play();
        var second = Play();
        Assert.Equal(first, second);
        if (Environment.GetEnvironmentVariable("HOMESTEAD_CHECKSUM_FILE") is { Length: > 0 } file)
            File.WriteAllLines(file, first.Select((checksum, minute) => $"{minute + 1} {checksum:x16}"));
    }

    /// <summary>A random map for four, four computer players fighting it out; the checksum after every game minute.</summary>
    private static List<ulong> Play()
    {
        var map = RandomMapGenerator.Generate(RandomMapSettings.ParseName("random:4:mirror:11")!.Value.Settings, 11);
        var random = new Random(5);
        var (world, report) = OriginalScenario.Load(map, random: random, seed: 5);
        for (int player = 0; player < report.Catalogs.Count; player++)
            world.AiPlayers.Add(new AiPlayer(world, report.Catalogs[player], player, random: random));
        var checksums = new List<ulong>();
        for (int minute = 0; minute < Minutes; minute++)
        {
            for (int t = 0; t < 60 * World.TicksPerSecond; t++)
                world.Step();
            checksums.Add(WorldChecksum.Of(world));
        }
        // Not just building: the armies fought (the AI's tactics count too).
        Assert.True(world.Military.Kills > 20, $"{world.Military.Kills} kills");
        return checksums;
    }
}
