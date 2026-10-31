using System.Text.Json;
using System.Text.Json.Nodes;
using Homestead.Assets;

namespace Homestead.Client;

/// <summary>The language of the texts (<see cref="Lang"/>).</summary>
public enum Language { German, English, Polish }

/// <summary>
/// The look of the title and loading screens: <c>Normal</c> the Gold Edition's title and each campaign's loading
/// screen; the others always gold, the original's orange (<c>Classic</c>) or the Amazons' purple.
/// </summary>
public enum Theme { Normal, Gold, Classic, Amazons }

/// <summary>
/// How the game shows itself: the <c>display</c> section of <c>settings.json</c> (<see cref="SettingsFile"/>), written
/// by the settings screen, applied at the start and on "Ok". F11 switches full screen for the session only.
/// </summary>
public sealed record DisplaySettings
{
    private const string Section = "display";

    /// <summary>The system's mouse pointer; otherwise the game draws its own (<see cref="SoftwareCursor"/>).</summary>
    public bool HardwareCursor { get; init; } = true;

    public bool FullScreen { get; init; }

    /// <summary>English until chosen, whatever the system's language.</summary>
    public Language Language { get; init; } = Language.English;

    public Theme Theme { get; init; } = Theme.Normal;

    /// <summary>Loudness of the background music in percent (0 = off).</summary>
    public int MusicVolume { get; init; } = 75;

    /// <summary>Music of the own people only; otherwise all pieces.</summary>
    public bool PeopleMusic { get; init; } = true;

    /// <summary>Loudness of the sound effects in percent (0 = off).</summary>
    public int EffectsVolume { get; init; } = 100;

    /// <summary>Saves the running game into the last slot every 15 minutes and on quitting (off by default: it overwrites that slot).</summary>
    public bool AutoSave { get; init; }

    /// <summary>Plays the original's intro video before the title screen at every start.</summary>
    public bool IntroVideo { get; init; } = true;

    /// <summary>Palette index (<see cref="PlayerColors.Palette"/>) of the color player 1 gets by default on the start screen.</summary>
    public int PreferredColor { get; init; }

    /// <summary>Chat commands (<c>/cheats</c>, <c>/attackme</c> …) work; only in the file, the settings screen keeps it as it is.</summary>
    public bool Cheats { get; init; }

    /// <summary>The settings in the file (default: the user's); missing or unreadable values keep their default.</summary>
    public static DisplaySettings Load(string? path = null)
    {
        var settings = new DisplaySettings();
        if (SettingsFile.ReadSection(Section, path) is not { ValueKind: JsonValueKind.Object } display)
            return settings;
        bool Flag(string name, bool fallback) =>
            display.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
        int Percent(string name, int fallback) =>
            display.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int percent)
                ? Math.Clamp(percent, 0, 100)
                : fallback;
        return settings with
        {
            MusicVolume = Percent("musicVolume", settings.MusicVolume),
            PeopleMusic = Flag("peopleMusic", settings.PeopleMusic),
            EffectsVolume = Percent("effectsVolume", settings.EffectsVolume),
            HardwareCursor = Flag("hardwareCursor", settings.HardwareCursor),
            FullScreen = Flag("fullScreen", settings.FullScreen),
            AutoSave = Flag("autoSave", settings.AutoSave),
            IntroVideo = Flag("introVideo", settings.IntroVideo),
            Cheats = Flag("cheats", settings.Cheats),
            Language = display.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String
                ? Lang.Parse(language.GetString())
                : settings.Language,
            Theme = display.TryGetProperty("theme", out var theme) && theme.ValueKind == JsonValueKind.String
                    && Enum.TryParse<Theme>(theme.GetString(), ignoreCase: true, out var chosen) && Enum.IsDefined(chosen)
                ? chosen
                : settings.Theme,
            PreferredColor = display.TryGetProperty("preferredColor", out var color) && color.ValueKind == JsonValueKind.String
                ? PlayerColors.IndexOf(color.GetString())
                : settings.PreferredColor,
        };
    }

    /// <summary>Writes the <c>display</c> section into the file (default: the user's), see <see cref="SettingsFile.WriteSection"/>.</summary>
    public void Save(string? path = null) =>
        SettingsFile.WriteSection(Section, new JsonObject
        {
            ["hardwareCursor"] = HardwareCursor,
            ["fullScreen"] = FullScreen,
            ["language"] = Lang.Code(Language),
            ["theme"] = Theme.ToString().ToLowerInvariant(),
            ["musicVolume"] = MusicVolume,
            ["peopleMusic"] = PeopleMusic,
            ["effectsVolume"] = EffectsVolume,
            ["autoSave"] = AutoSave,
            ["introVideo"] = IntroVideo,
            ["preferredColor"] = PlayerColors.Choices[PreferredColor].Id,
            ["cheats"] = Cheats,
        }, path);
}
