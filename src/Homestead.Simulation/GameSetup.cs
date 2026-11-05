using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Homestead.Assets.Maps;
using Homestead.Simulation.Ai;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Lockstep;
using Homestead.Simulation.Magic;
using Homestead.Simulation.Map.Generation;

namespace Homestead.Simulation;

/// <summary>
/// One player of a game setup: <c>null</c> as people or color (palette index) means drawn; team 1–4 (0: none, against
/// everybody); a human plays itself, every other player is a computer player (<paramref name="Character"/> from
/// <c>ai.json</c>, null: drawn by the people's weights).
/// </summary>
public sealed record PlayerSetup(Civilization? People = null, int? Color = null, int Team = 0, bool Hardcore = false, bool Human = false,
    string? Character = null);

/// <summary>
/// The map of a game setup: a random map (<paramref name="RandomMap"/> and <paramref name="RandomSeed"/>; the generator makes
/// the same one everywhere) or an original map, which every computer has in its own installation: found by its
/// <paramref name="Hash"/> (SHA-256 of the file), first at <paramref name="MapFile"/> (relative to the map directory, '/' as separator).
/// </summary>
public sealed record MapChoice(string? MapFile = null, string? Hash = null, RandomMapSettings? RandomMap = null, int RandomSeed = 0)
{
    public static MapChoice Original(string path, string mapDirectory) =>
        new(MapFile: Path.GetRelativePath(mapDirectory, path).Replace(Path.DirectorySeparatorChar, '/'), Hash: HashOf(path));

    public static MapChoice Generated(RandomMapSettings settings, int seed) => new(RandomMap: settings, RandomSeed: seed);

    public static string HashOf(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// Whether the path can name a map file below a map directory: relative, without <c>..</c>, a <c>.map</c> file. It comes from
    /// another computer, and <see cref="Locate"/> reads what it names; anything else stays out (<c>/dev/zero</c> has no end).
    /// </summary>
    public static bool IsSafeMapFile(string? path) =>
        path is { Length: > 0 and <= 260 } && path.EndsWith(".map", StringComparison.OrdinalIgnoreCase) && !path.Contains('\\') && !path.Contains(':')
        && !path.StartsWith('/') && !path.Any(char.IsControl) && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    /// <summary>Largest file taken for a map when looking for one by its hash (the original maps have a few megabytes).</summary>
    private const long MaxMapFileLength = 64L * 1024 * 1024;

    /// <summary>The original map's file in this installation: at its path if the hash matches, else any map file with the hash.</summary>
    /// <exception cref="FileNotFoundException">No map file with the hash.</exception>
    public string Locate(string mapDirectory)
    {
        if (MapFile is null || Hash is null)
            throw new InvalidOperationException("not an original map");
        if (!IsSafeMapFile(MapFile))
            throw new FileNotFoundException("not a path of a map file", MapFile);
        string expected = Path.Combine(mapDirectory, MapFile.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(expected) && new FileInfo(expected) is { Length: > 0 and <= MaxMapFileLength } && HashOf(expected) == Hash)
            return expected;
        return Directory.EnumerateFiles(mapDirectory, "*", SearchOption.AllDirectories)
                   .Where(f => Path.GetExtension(f).Equals(".map", StringComparison.OrdinalIgnoreCase))
                   .Order(StringComparer.Ordinal)
                   .FirstOrDefault(f => new FileInfo(f) is { Length: > 0 and <= MaxMapFileLength } && HashOf(f) == Hash)
               ?? throw new FileNotFoundException($"map {MapFile} ({Hash}) not found", MapFile);
    }

    public OriginalMap Open(string mapDirectory) =>
        RandomMap is { } settings ? RandomMapGenerator.Generate(settings, RandomSeed) : OriginalMap.Open(Locate(mapDirectory));
}

/// <summary>The world of a game setup and how it came out.</summary>
/// <param name="Positions">Start position (map player) per player, the drawn ones included; null: as the map says.</param>
public sealed record GameStart(World World, OriginalScenario.LoadReport Report, IReadOnlyList<int>? Positions);

/// <summary>
/// What a free game starts from, as plain data (JSON, <see cref="ToJson"/>): every computer that starts the same setup gets the
/// same world – the map, every player's people, start position, color and computer character drawn from <see cref="Seed"/>,
/// the game values from <see cref="Settings"/>. In a multiplayer game the server hands it out.
/// </summary>
public sealed record GameSetup
{
    public required MapChoice Map { get; init; }

    /// <summary>The seed of every draw and the world's own (<see cref="World.Seed"/>).</summary>
    public int Seed { get; init; }

    /// <summary>Per player (entry i: player i); map players beyond the list are computer players with everything drawn.</summary>
    public IReadOnlyList<PlayerSetup> Players { get; init; } = [];

    /// <summary>
    /// Per start position (map player) the player taking it (<c>null</c>: drawn from those left); null as a whole: as the
    /// map says. Only used when there is an entry per map player.
    /// </summary>
    public IReadOnlyList<int?>? Positions { get; init; }

    /// <summary>The game values (depletion, spell costs); null: those of <c>data/</c>.</summary>
    public GameSettings? Settings { get; init; }

    /// <summary>
    /// The program and data every computer must have (a network game's, from the server); null: not checked (a game alone).
    /// </summary>
    public GameFingerprint? Fingerprint { get; init; }

    /// <summary>What is drawn from the seed: each draw has its own random numbers, so one does not shift another.</summary>
    public enum Draw { Peoples, Positions, Colors, Characters }

    public Random RandomFor(Draw draw) => new(unchecked(Seed * 31 + (int)draw));

    /// <summary>The most players a game can have.</summary>
    public const int MaxPlayers = 8;

    /// <summary>
    /// What is wrong with a setup that came over the network (empty: nothing). The server asks when a game is opened, every
    /// computer again before building the world (the server may be anybody's too): one host must not be able to crash the
    /// others or make them run out of memory. Needs no map; what depends on it: <see cref="Problems(int, string?)"/>.
    /// </summary>
    public IReadOnlyList<string> Problems(string? dataDirectory = null)
    {
        var problems = new List<string>();
        if (Map is null)
            problems.Add("no map");
        else if (Map.RandomMap is { } random)
        {
            if (Map.MapFile is not null || Map.Hash is not null)
                problems.Add("a map is random or an original one");
            if (random.Players is < RandomMapSettings.MinPlayers or > RandomMapSettings.MaxPlayers)
                problems.Add("bad number of players on the random map");
            if (!Enum.IsDefined(random.Symmetry) || !Enum.IsDefined(random.Rivers) || !Enum.IsDefined(random.Resources) || !Enum.IsDefined(random.Stones)
                || !Enum.IsDefined(random.Forests) || !Enum.IsDefined(random.Decorations) || !Enum.IsDefined(random.Treasures))
                problems.Add("bad random map option");
            if (Players?.Count > random.Players)
                problems.Add("more players than the random map has");
        }
        else if (!MapChoice.IsSafeMapFile(Map.MapFile) || !GameFingerprint.IsHash(Map.Hash))
            problems.Add("bad map file");
        if (Players is null || Players.Count is 0 or > MaxPlayers || Players.Any(p => p is null))
            problems.Add("bad number of players");
        else
            foreach (var player in Players)
            {
                if (player.People is { } people && !Enum.IsDefined(people))
                    problems.Add("no such people");
                if (player.Color is < 0 or > MaxColor)
                    problems.Add("no such color");
                if (player.Team is < 0 or > MaxTeam)
                    problems.Add("no such team");
                if (player.Character is { } character && !KnownCharacters(dataDirectory).Contains(character))
                    problems.Add("no such computer player");
            }
        if (Positions is { } positions)
        {
            var taken = positions.OfType<int>().ToList();
            if (positions.Count > MaxPlayers || taken.Any(p => p < 0 || p >= positions.Count) || taken.Distinct().Count() < taken.Count)
                problems.Add("bad start positions");
        }
        if (Settings is { } settings
            && (settings.Depletion is null || settings.SpellCostIncrease is < 0 or > 100
                || settings.Depletion.Stone is < 0 or > 100 || settings.Depletion.Mountain is < 0 or > 100 || settings.Depletion.Fish is < 0 or > 100))
            problems.Add("game values out of range");
        if (Fingerprint is { } fingerprint)
            problems.AddRange(fingerprint.Problems());
        return problems.Distinct().ToArray();
    }

    /// <summary><see cref="Problems(string?)"/>, and on a map with <paramref name="mapPlayers"/> players: every human needs a place on it.</summary>
    public IReadOnlyList<string> Problems(int mapPlayers, string? dataDirectory = null)
    {
        var problems = Problems(dataDirectory);
        return Players is not null && Players.Where((p, player) => p is { Human: true } && player >= mapPlayers).Any()
            ? [.. problems, "more human players than the map has"]
            : problems;
    }

    /// <summary>Highest color (palette index) and team of a player.</summary>
    public const int MaxColor = 63, MaxTeam = 4;

    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> Characters = new();

    private static IReadOnlySet<string> KnownCharacters(string? dataDirectory) =>
        Characters.GetOrAdd(dataDirectory ?? "", _ => AiConfig.Load(dataDirectory).Characters.Keys.ToHashSet());

    /// <summary>
    /// Builds the world on <paramref name="map"/> (opened from <see cref="Map"/>, or a campaign's): peoples, start positions,
    /// hardcore players and teams as set up, a free game's goals (<see cref="ConquestGoals"/>), a computer player for every
    /// player who is not human.
    /// </summary>
    /// <exception cref="IncompatibleGameException">This computer's program or data differ from <see cref="Fingerprint"/>.</exception>
    public GameStart Start(OriginalMap map, string? dataDirectory = null)
    {
        if (Fingerprint is { } required && GameFingerprint.Of(dataDirectory).Differences(required) is { Count: > 0 } differences)
            throw new IncompatibleGameException(differences);
        int count = map.Players.Count;
        if (Fingerprint is not null && Problems(count, dataDirectory) is { Count: > 0 } problems)
            throw new InvalidGameSetupException(problems);
        var peoples = Enum.GetValues<Civilization>();
        var peopleDraw = RandomFor(Draw.Peoples);
        var chosen = Enumerable.Range(0, Math.Max(1, Math.Max(count, Players.Count)))
            .Select(player => Of(player).People ?? peoples[peopleDraw.Next(peoples.Length)])
            .ToArray();
        int[]? positions = Positions is { } wanted && wanted.Count == count && count > 0 ? ResolvePositions(wanted, RandomFor(Draw.Positions)) : null;
        var production = ProductionConfig.Load(dataDirectory);
        var magic = MagicConfig.Load(dataDirectory);
        var settings = Settings ?? GameSettings.Defaults(production, magic);
        var (world, report) = OriginalScenario.Load(map, dataDirectory, freeChoice: chosen, seed: Seed,
            hardcore: Enumerable.Range(0, Math.Min(count, Players.Count)).Where(p => Players[p].Hardcore),
            positions: positions, production: settings.Apply(production), magic: settings.Apply(magic));
        world.Teams.AddRange(Players.Take(count).Select(p => p.Team));
        world.Goals = new ConquestGoals();
        world.Multiplayer = Players.Count(p => p.Human) > 1;
        var characters = RandomFor(Draw.Characters);
        for (int player = 0; player < report.Catalogs.Count; player++)
            if (!Of(player).Human)
                world.AiPlayers.Add(new AiPlayer(world, report.Catalogs[player], player, character: Of(player).Character, random: characters));
        return new GameStart(world, report, positions);
    }

    private PlayerSetup Of(int player) => player < Players.Count ? Players[player] : new PlayerSetup();

    /// <summary>
    /// Start position per player: the assigned ones, the others drawn from the positions left
    /// (entry i: the map player whose start player i takes).
    /// </summary>
    public static int[] ResolvePositions(IReadOnlyList<int?> chosen, Random random)
    {
        var positions = new int[chosen.Count];
        var assigned = new HashSet<int>();
        for (int position = 0; position < chosen.Count; position++)
            if (chosen[position] is { } player && player < chosen.Count)
            {
                positions[player] = position;
                assigned.Add(player);
            }
        var free = Enumerable.Range(0, chosen.Count).Where(p => chosen[p] is null || chosen[p] >= chosen.Count)
            .OrderBy(_ => random.Next()).ToList();
        foreach (int player in Enumerable.Range(0, chosen.Count).Where(p => !assigned.Contains(p)))
        {
            positions[player] = free[^1];
            free.RemoveAt(free.Count - 1);
        }
        return positions;
    }

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static GameSetup FromJson(string json) => JsonSerializer.Deserialize<GameSetup>(json, Json) ?? throw new JsonException("no game setup");
}

/// <summary>A setup from the network that cannot be played (<see cref="GameSetup.Problems(string?)"/>).</summary>
public sealed class InvalidGameSetupException(IReadOnlyList<string> problems) : Exception("invalid game setup: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
