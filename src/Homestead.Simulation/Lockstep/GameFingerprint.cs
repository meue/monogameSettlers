using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Homestead.Simulation.Buildings;

namespace Homestead.Simulation.Lockstep;

/// <summary>
/// What must be equal on every computer of a lockstep game, checked when joining: the network format
/// (<see cref="CurrentProtocol"/>), the program (version with commit, from the git tags) and the game data – every file of
/// <c>data/</c> the simulation can read (all but the client's own: texts, menus, sounds, animations, chat, campaign), each
/// by its SHA-256. A computer with another <c>production.json</c> would simulate another game from the first tick on.
/// Line ends (CRLF/LF) and a byte order mark do not count: a checkout on Windows must not differ from one on Linux.
/// </summary>
public sealed record GameFingerprint(int Protocol, string Version, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>The network format's version (<see cref="CommandCodec"/>, turn messages, handshake): raise it on every change.</summary>
    public const int CurrentProtocol = 2;

    /// <summary>Files and folders of <c>data/</c> only the client reads (relative, '/' as separator).</summary>
    public static readonly IReadOnlyList<string> ClientOnly =
        ["lang/", "ui.json", "sound.json", "settlers.json", "ai-chat.json", "campaign.json"];

    /// <summary>One hash over all files: equal fingerprints have equal ones (to show, e.g. in a lobby).</summary>
    public string Data => Hash(Encoding.UTF8.GetBytes(string.Concat(Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value}\n"))));

    /// <summary>This program's and this data directory's fingerprint (default: the one next to the program).</summary>
    public static GameFingerprint Of(string? dataDirectory = null)
    {
        string directory = dataDirectory ?? BuildingCatalog.DefaultDataDirectory;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!ClientOnly.Any(c => c.EndsWith('/') ? relative.StartsWith(c, StringComparison.Ordinal) : relative == c))
                files[relative] = Hash(Normalized(File.ReadAllBytes(path)));
        }
        return new GameFingerprint(CurrentProtocol, ProgramVersion, files);
    }

    /// <summary>The simulation's version: the git tag's (MinVer) with the commit, e.g. <c>0.4.0-alpha.0.12+1604922…</c>.</summary>
    public static string ProgramVersion { get; } =
        typeof(World).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>What differs from <paramref name="other"/> (developer English, for the log); empty: the same game.</summary>
    public IReadOnlyList<string> Differences(GameFingerprint other)
    {
        var differences = new List<string>();
        if (Protocol != other.Protocol)
            differences.Add($"network protocol {Protocol} ≠ {other.Protocol}");
        if (Version != other.Version)
            differences.Add($"program version {Version} ≠ {other.Version}");
        foreach (string file in Files.Keys.Union(other.Files.Keys).Order(StringComparer.Ordinal))
            if (!Files.TryGetValue(file, out string? mine))
                differences.Add($"data/{file} missing here");
            else if (!other.Files.TryGetValue(file, out string? theirs))
                differences.Add($"data/{file} missing there");
            else if (mine != theirs)
                differences.Add($"data/{file} differs");
        return differences;
    }

    /// <summary>
    /// What is wrong with a fingerprint someone sent (empty: nothing): it is only self-reported, so this says nothing about the
    /// sender's program – but it goes on to others (lobby, setup, refusals), so it must be small and harmless.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        if (Version is null || Files is null)
            return ["incomplete fingerprint"];
        if (Version.Length > 200 || Version.Any(c => c is < ' ' or > '~'))
            return ["bad program version"];
        if (Files.Count > 256)
            return ["too many data files"];
        foreach (var (file, hash) in Files)
            if (file is null or { Length: 0 or > 128 } || file.Contains("..", StringComparison.Ordinal) || file.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '.' or '_' or '-'))
                || !IsHash(hash))
                return ["bad data file entry"];
        return [];
    }

    /// <summary>Whether the text is a SHA-256 as this program writes it.</summary>
    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    public bool Matches(GameFingerprint other) => Differences(other).Count == 0;

    public string ToJson() => JsonSerializer.Serialize(this);

    public static GameFingerprint FromJson(string json) =>
        JsonSerializer.Deserialize<GameFingerprint>(json) ?? throw new JsonException("no fingerprint");

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The file without a UTF-8 byte order mark and with LF line ends.</summary>
    private static byte[] Normalized(byte[] bytes)
    {
        int start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        var normalized = new List<byte>(bytes.Length);
        for (int i = start; i < bytes.Length; i++)
            if (bytes[i] != '\r' || i + 1 >= bytes.Length || bytes[i + 1] != '\n')
                normalized.Add(bytes[i]);
        return normalized.ToArray();
    }
}

/// <summary>A game set up on another program or other data than this computer's: it would desync from the first tick.</summary>
public sealed class IncompatibleGameException(IReadOnlyList<string> differences)
    : Exception("incompatible game: " + string.Join("; ", differences))
{
    public IReadOnlyList<string> Differences { get; } = differences;
}
