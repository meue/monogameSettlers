using System.Globalization;
using System.Text.Json;
using Homestead.Simulation.Buildings;

namespace Homestead.Client;

/// <summary>
/// The texts of the game in the chosen language (<see cref="DisplaySettings.Language"/>): <c>data/lang/de.json</c>,
/// <c>en.json</c> and <c>pl.json</c>, nested objects whose keys join with dots (<c>title.singlePlayer</c>); placeholders <c>{0}</c>, …
/// as in <see cref="string.Format(IFormatProvider, string, object[])"/>, numbers in the language's culture. A text
/// missing in the language comes from German, else the key itself shows (and is reported once on the console).
/// Data names have keys from their ids: <c>buildings.&lt;name&gt;</c>, <c>goods.&lt;GOOD&gt;</c>, <c>peoples.&lt;People&gt;</c>, …
/// </summary>
public static class Lang
{
    private static readonly Dictionary<Language, Dictionary<string, string>> Tables = new();
    private static readonly HashSet<string> Reported = new();

    public static Language Current { get; private set; } = Language.German;

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Current switch
    {
        Language.English => "en-US",
        Language.Polish => "pl-PL",
        _ => "de-DE",
    });

    /// <summary>The language for all texts from now on; numbers and dates follow it (the thread's culture).</summary>
    public static void Use(Language language)
    {
        Current = language;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentCulture = Culture;
    }

    /// <summary>The file name of a language in <c>data/lang</c>.</summary>
    public static string Code(Language language) => language switch
    {
        Language.English => "en",
        Language.Polish => "pl",
        _ => "de",
    };

    /// <summary>The language of a file name / <c>HOMESTEAD_LANGUAGE</c> value (<see cref="Code"/>); unknown: German.</summary>
    public static Language Parse(string? code) => Enum.GetValues<Language>().FirstOrDefault(l => Code(l) == code);

    /// <summary>The language's column in the original's text tables (GFX files 07–09): 0 German, 1 English, 4 Polish.</summary>
    public static int TextIndex(Language language) => language switch
    {
        Language.English => 1,
        Language.Polish => 4,
        _ => 0,
    };

    /// <summary>The Windows code page the original stores the language's texts in.</summary>
    public static int CodePage(Language language) => language == Language.Polish ? 1250 : 1252;

    /// <summary>The text of the key.</summary>
    public static string T(string key)
    {
        if (Find(key) is { } text)
            return text;
        if (Reported.Add(key))
            Console.Error.WriteLine($"Missing text: {key}");
        return key;
    }

    /// <summary>The text of the key with its placeholders filled in.</summary>
    public static string T(string key, params object?[] args) => string.Format(Culture, T(key), args);

    /// <summary>
    /// A map's name for the player: random maps are named in German by the simulation
    /// (<see cref="Homestead.Simulation.Map.Generation.RandomMapGenerator.NameOf"/>, also in saved games).
    /// </summary>
    public static string MapName(string name) =>
        System.Text.RegularExpressions.Regex.Match(name, @"^Zufallskarte (\d+)-([0-9A-F]{8})$") is { Success: true } random
            ? T("start.randomMapName", random.Groups[1].Value, random.Groups[2].Value)
            : name;

    /// <summary>A saved game's name in the lists: a campaign mission's side and number, otherwise its map.</summary>
    public static string SaveName(Homestead.Simulation.Persistence.SaveInfo info) =>
        Homestead.Simulation.Persistence.MissionRef.Parse(info.Mission) is { } mission
        && CampaignConfig.Load().Campaigns.GetValueOrDefault(mission.Campaign)?.GetValueOrDefault(mission.Side) is { } side
            ? T("campaign.saveName", T(CampaignConfig.Load().Names[mission.Campaign]), T(side.Name), mission.Index + 1)
            : MapName(info.Map);

    /// <summary>The text of the key, or null where neither the language nor German has it.</summary>
    public static string? Find(string key) =>
        Table(Current).GetValueOrDefault(key) ?? (Current == Language.German ? null : Table(Language.German).GetValueOrDefault(key));

    /// <summary>All texts of a language, flattened (for checks).</summary>
    public static IReadOnlyDictionary<string, string> Load(Language language) => Table(language);

    private static Dictionary<string, string> Table(Language language)
    {
        lock (Tables)
        {
            if (Tables.TryGetValue(language, out var table))
                return table;
            Tables[language] = table = new Dictionary<string, string>();
            string path = Path.Combine(BuildingCatalog.DefaultDataDirectory, "lang", Code(language) + ".json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                Flatten(document.RootElement, "", table);
            }
            return table;
        }
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> table)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.StartsWith('_'))
                continue;
            string key = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Object)
                Flatten(property.Value, key, table);
            else if (property.Value.ValueKind == JsonValueKind.String)
                table[key] = property.Value.GetString()!;
        }
    }
}
