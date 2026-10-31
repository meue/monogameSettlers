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

    /// <summary>The original map's file in this installation: at its path if the hash matches, else any map file with the hash.</summary>
    /// <exception cref="FileNotFoundException">No map file with the hash.</exception>
    public string Locate(string mapDirectory)
    {
        if (MapFile is null || Hash is null)
            throw new InvalidOperationException("not an original map");
        string expected = Path.Combine(mapDirectory, MapFile.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(expected) && HashOf(expected) == Hash)
            return expected;
        return Directory.EnumerateFiles(mapDirectory, "*", SearchOption.AllDirectories)
                   .Where(f => Path.GetExtension(f).Equals(".map", StringComparison.OrdinalIgnoreCase))
                   .Order(StringComparer.Ordinal)
                   .FirstOrDefault(f => new FileInfo(f).Length > 0 && HashOf(f) == Hash)
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
