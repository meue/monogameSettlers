using System.Text.Json;
using System.Text.RegularExpressions;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Magic;

namespace Homestead.Simulation.Tests;

/// <summary>
/// The texts in <c>data/lang</c> (read by the client's <c>Lang</c>): all languages complete and alike, every key the
/// client names literally there, and every id of the data that gets its name from them.
/// </summary>
public partial class LanguageTests
{
    private static readonly string[] Languages = ["de", "en", "pl"];

    private static Dictionary<string, string> Load(string language)
    {
        var table = new Dictionary<string, string>();
        void Flatten(JsonElement element, string prefix)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.StartsWith('_'))
                    continue;
                string key = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
                if (property.Value.ValueKind == JsonValueKind.Object)
                    Flatten(property.Value, key);
                else
                    table[key] = property.Value.GetString()!;
            }
        }
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "lang", language + ".json")));
        Flatten(document.RootElement, "");
        return table;
    }

    private static JsonElement Data(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, file))).RootElement;

    [Fact]
    public void All_languages_have_the_same_texts_with_the_same_placeholders()
    {
        var german = Load("de");
        foreach (var language in Languages.Where(l => l != "de"))
        {
            var other = Load(language);
            Assert.Empty(german.Keys.Except(other.Keys));
            Assert.Empty(other.Keys.Except(german.Keys));
            foreach (var (key, text) in german)
            {
                var placeholders = Placeholder().Matches(text).Select(m => m.Value).Order().Distinct();
                Assert.True(placeholders.SequenceEqual(Placeholder().Matches(other[key]).Select(m => m.Value).Order().Distinct()),
                    $"{language} {key}: placeholders differ");
                Assert.False(string.IsNullOrWhiteSpace(other[key]), $"{language} {key}: empty");
            }
        }
    }

    [Fact]
    public void Every_key_the_client_names_exists()
    {
        string client = Path.Combine(RepositoryRoot(), "src", "Homestead.Client");
        var texts = Load("de");
        var missing = Directory.EnumerateFiles(client, "*.cs")
            .SelectMany(file => LiteralKey().Matches(File.ReadAllText(file)).Select(m => (File: Path.GetFileName(file), Key: m.Groups[1].Value)))
            .Where(k => !texts.ContainsKey(k.Key))
            .Select(k => $"{k.File}: {k.Key}")
            .Distinct()
            .ToList();
        Assert.Equal([], missing);
    }

    [Fact]
    public void Every_name_of_the_data_has_a_text()
    {
        var texts = Load("de");
        var keys = new List<string>();
        foreach (var people in Enum.GetValues<Civilization>())
        {
            keys.Add($"peoples.{people}");
            keys.AddRange(BuildingCatalog.Load(people).All.Select(b => $"buildings.{b.Name}"));
        }
        keys.AddRange(Enum.GetValues<SoldierType>().Select(t => $"soldiers.types.{t}"));
        keys.AddRange(Enum.GetValues<SpellKind>().Select(s => $"spells.{s}"));
        var ui = Data("ui.json");
        keys.AddRange(ui.GetProperty("goodsGrid").GetProperty("goods").EnumerateArray()
            .SelectMany(row => row.EnumerateArray()).Select(g => $"goods.{g.GetProperty("good").GetString()}"));
        foreach (var tab in ui.GetProperty("panelTabs").EnumerateArray())
        {
            keys.Add($"panel.tabs.{tab.GetProperty("id").GetString()}");
            if (tab.TryGetProperty("menus", out var menus))
                keys.AddRange(menus.EnumerateArray().Select(m => $"panel.menus.{m.GetProperty("id").GetString()}"));
        }
        keys.AddRange(ui.GetProperty("status").EnumerateArray().Select(s => $"buildingStatus.{s[1].GetString()}"));
        var events = Data("events.json");
        keys.AddRange(events.GetProperty("events").EnumerateObject().Select(e => $"events.texts.{e.Name}"));
        keys.AddRange(events.GetProperty("categories").EnumerateObject().Select(c => $"events.categories.{c.Name}"));
        keys.AddRange(events.GetProperty("dates").EnumerateArray().Select(d => $"events.dates.{d.GetProperty("date").GetString()}"));
        var ai = Data("ai.json");
        keys.AddRange(ai.GetProperty("characters").EnumerateObject().Where(c => !c.Name.StartsWith('_')).Select(c => $"ai.characters.{c.Name}"));
        keys.AddRange(ai.GetProperty("traits").EnumerateObject().Where(t => !t.Name.StartsWith('_')).Select(t => $"ai.traits.{t.Name}"));

        Assert.Equal([], keys.Distinct().Where(k => !texts.ContainsKey(k)));
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Homestead.sln")))
            folder = folder.Parent;
        return folder?.FullName ?? throw new DirectoryNotFoundException("Homestead.sln not found above the test binaries.");
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex Placeholder();

    // Lang.T("key") or Lang.Find("key") with the whole key as one literal (not "prefix." + id).
    [GeneratedRegex(@"Lang\.(?:T|Find)\(""([A-Za-z0-9_.\-]+)""\s*[,)]")]
    private static partial Regex LiteralKey();
}
