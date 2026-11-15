using System.Collections.Concurrent;
using System.Reflection;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Magic;

namespace Homestead.Simulation.Commands;

/// <summary>
/// Whether a command can be applied at all. In a lockstep game commands come from other computers, which may run anything:
/// a value no honest client makes (a point off the map, a good that does not exist, a list that is null) must not throw on
/// every computer at once. Goes by the commands' fields, so new commands are covered without code here (their own rules:
/// <see cref="GameCommand.IsValid"/>). Reads only the command and the world: every computer comes to the same answer.
/// </summary>
internal static class CommandGuard
{
    /// <summary>Most ids in one command (an honest selection is far smaller; an id list is walked for every id).</summary>
    public const int MaxIds = 2000;

    /// <summary>Longest text in a command (chat is cut shorter, <see cref="ChatLog.MaxLength"/>).</summary>
    public const int MaxTextLength = 2000;

    /// <summary>Largest step of a counter (orders, soldiers wanted): beyond it only overflows could come out.</summary>
    public const int MaxDelta = 100_000;

    private static readonly ConcurrentDictionary<Type, Field[]> Fields = new();

    private sealed record Field(PropertyInfo Property, bool MayBeNull);

    public static bool Accepts(World world, GameCommand command)
    {
        foreach (var field in Fields.GetOrAdd(command.GetType(), FieldsOf))
            if (!Accepts(world, field, field.Property.GetValue(command)))
                return false;
        return command.Player >= 0 && (world.PlayerCount == 0 || command.Player < world.PlayerCount) && command.IsValid(world);
    }

    private static Field[] FieldsOf(Type type)
    {
        var nullability = new NullabilityInfoContext();
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != nameof(GameCommand.Sequence) && p.GetMethod is not null && p.GetIndexParameters().Length == 0)
            .Select(p => new Field(p, nullability.Create(p).ReadState != NullabilityState.NotNull))
            .ToArray();
    }

    private static bool Accepts(World world, Field field, object? value) => value switch
    {
        null => field.MayBeNull,
        Enum e => Enum.IsDefined(e.GetType(), e),
        GridPoint point => world.Map is not { } map || map.Contains(point),
        int[] ids => ids.Length <= MaxIds,
        string text => text.Length <= MaxTextLength,
        double number => double.IsFinite(number),
        int number when field.Property.Name == "Delta" => Math.Abs((long)number) <= MaxDelta,
        Spell spell => Enum.IsDefined(spell.Kind),
        _ => true,
    };
}
