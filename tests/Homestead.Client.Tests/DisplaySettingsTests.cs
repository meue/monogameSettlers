using Homestead.Client;

namespace Homestead.Client.Tests;

public class DisplaySettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "homestead-settings-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void A_missing_neuralUpscale_stays_off()
    {
        File.WriteAllText(_path, """{ "display": { "fullScreen": true } }""");

        Assert.False(DisplaySettings.Load(_path).NeuralUpscale);
    }

    [Fact]
    public void An_enabled_neuralUpscale_survives_a_save_load_round_trip()
    {
        File.WriteAllText(_path, """{ "display": { "neuralUpscale": true } }""");

        var loaded = DisplaySettings.Load(_path);
        Assert.True(loaded.NeuralUpscale);

        loaded.Save(_path);
        Assert.True(DisplaySettings.Load(_path).NeuralUpscale);
        Assert.Contains("\"neuralUpscale\": true", File.ReadAllText(_path));
    }

    [Fact]
    public void Saving_the_default_writes_the_key()
    {
        new DisplaySettings().Save(_path);

        Assert.Contains("\"neuralUpscale\": false", File.ReadAllText(_path));
        Assert.False(DisplaySettings.Load(_path).NeuralUpscale);
    }

    public void Dispose()
    {
        File.Delete(_path);
    }
}
