using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Magic;

namespace Homestead.Simulation.Lockstep;

/// <summary>The world's checksums when a turn began (before its commands), to compare between the computers.</summary>
public sealed record StateCheck(long Turn, WorldChecksums Checksums);

/// <summary>
/// A player's commands for a turn, sent to the server (also none: the others must know there are none); now and then with
/// the checksums of the turn it was sent in (<see cref="LockstepOptions.CheckTurns"/>).
/// </summary>
public sealed record TurnInput(int Player, long Turn, IReadOnlyList<GameCommand> Commands, StateCheck? Check = null);

/// <summary>A player's <see cref="StateCheck"/>, handed on by the server.</summary>
public sealed record PlayerCheck(int Player, StateCheck Check);

/// <summary>
/// Every player's commands for a turn, from the server: by player, each player's in the order issued; with the checks the
/// turn's inputs carried, by player, and the furthest turn any player has sent an input for (<paramref name="Newest"/>: how
/// far ahead the fastest computer is, so a slower one can catch up).
/// </summary>
public sealed record TurnBundle(long Turn, IReadOnlyList<GameCommand> Commands, IReadOnlyList<PlayerCheck>? Checks = null, long Newest = 0);

/// <summary>
/// The network format of commands (JSON): every <see cref="GameCommand"/> type by its name in <c>$type</c>, found by
/// reflection (new commands need no code here), properties in camel case, enums as text, the <see cref="GameCommand.Sequence"/>
/// included. A building type goes as people and name and comes back as the definition of <paramref name="catalogs"/> (the
/// game's, so every computer applies the same one); a spell goes as its kind (the world knows the rest).
/// </summary>
public sealed class CommandCodec
{
    /// <summary>Every command type, by name.</summary>
    public static readonly IReadOnlyList<Type> CommandTypes = typeof(GameCommand).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(GameCommand)) && !t.IsAbstract)
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .ToArray();

    private readonly Dictionary<Civilization, BuildingCatalog> _catalogs;
    private readonly string? _dataDirectory;
    private readonly JsonSerializerOptions _options;

    /// <param name="catalogs">The game's building catalogs; peoples missing are loaded from <paramref name="dataDirectory"/>.</param>
    public CommandCodec(IEnumerable<BuildingCatalog>? catalogs = null, string? dataDirectory = null)
    {
        _catalogs = (catalogs ?? []).GroupBy(c => c.Civilization).ToDictionary(g => g.Key, g => g.First());
        _dataDirectory = dataDirectory;
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(GameCommand))
                return;
            info.PolymorphismOptions = new JsonPolymorphismOptions
            {
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            };
            foreach (var type in CommandTypes)
                info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, type.Name));
        });
        _options = new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowOutOfOrderMetadataProperties = true,
            // Enums by name only: a number could be any value, none of the enum's.
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false), new DefinitionConverter(this), new SpellConverter() },
        };
    }

    public string Encode(GameCommand command) => JsonSerializer.Serialize(command, _options);

    public GameCommand Decode(string json) => JsonSerializer.Deserialize<GameCommand>(json, _options) ?? throw new JsonException("no command");

    public string Encode(TurnInput input) => JsonSerializer.Serialize(input, _options);

    public TurnInput DecodeInput(string json) => JsonSerializer.Deserialize<TurnInput>(json, _options) ?? throw new JsonException("no turn input");

    public string Encode(TurnBundle bundle) => JsonSerializer.Serialize(bundle, _options);

    /// <summary>
    /// A bundle from the server. Its commands and checks come from other computers, as they sent them: one that cannot be read is
    /// left out (the same on every computer, they have the same program and data) instead of ending the game for all.
    /// </summary>
    /// <exception cref="JsonException">Not a bundle at all.</exception>
    public TurnBundle DecodeBundle(string json)
    {
        var wire = JsonSerializer.Deserialize<WireBundle>(json, _options) ?? throw new JsonException("no turn bundle");
        var commands = new List<GameCommand>(wire.Commands?.Length ?? 0);
        foreach (var command in wire.Commands ?? [])
            if (TryRead(() => JsonSerializer.Deserialize<GameCommand>(command, _options)) is { } read)
                commands.Add(read);
        var checks = wire.Checks?.Select(c => TryRead(() => JsonSerializer.Deserialize<PlayerCheck>(c, _options)))
            .Where(c => c is { Check.Checksums: not null }).Select(c => c!).ToArray();
        return new TurnBundle(wire.Turn, commands, checks, wire.Newest);
    }

    private sealed record WireBundle(long Turn, JsonElement[]? Commands, JsonElement[]? Checks, long Newest);

    /// <summary>What was read; null for anything broken (unknown type, missing or wrong fields, unknown buildings, bad text …).</summary>
    private static T? TryRead<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private BuildingDefinition Definition(Civilization people, string name)
    {
        if (!Enum.IsDefined(people))
            throw new JsonException("no such people");
        if (!_catalogs.TryGetValue(people, out var catalog))
            _catalogs[people] = catalog = BuildingCatalog.Load(people, _dataDirectory);
        return catalog.Find(name) ?? throw new JsonException($"no building '{name}' for {people}");
    }

    private sealed class DefinitionConverter(CommandCodec codec) : JsonConverter<BuildingDefinition>
    {
        private sealed record Wire(Civilization People, string Name);

        public override BuildingDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<Wire>(ref reader, options) is { } wire
                ? codec.Definition(wire.People, wire.Name)
                : throw new JsonException("no building type");

        public override void Write(Utf8JsonWriter writer, BuildingDefinition value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new Wire(value.Civilization, value.Name), options);
    }

    /// <summary>A spell as its kind: the priests' people's spell of that kind is cast (<see cref="Magic.Magic.Cast"/>).</summary>
    private sealed class SpellConverter : JsonConverter<Spell>
    {
        public override Spell Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<SpellKind>(ref reader, options), Cost: 0, Radius: 0);

        public override void Write(Utf8JsonWriter writer, Spell value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Kind, options);
    }
}
