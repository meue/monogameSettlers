using Homestead.Simulation.Map;

namespace Homestead.Simulation;

/// <summary>The checksums of a world's parts at a tick (see <see cref="WorldChecksum"/>): which part differs points to the cause of a desync.</summary>
public sealed record WorldChecksums(long Tick, ulong Settlers, ulong Buildings, ulong Piles, ulong Map, ulong Mana)
{
    /// <summary>The names of the parts that differ from <paramref name="other"/>'s.</summary>
    public IReadOnlyList<string> Differences(WorldChecksums other) =>
        new (string Name, bool Differs)[]
        {
            ("tick", Tick != other.Tick), ("settlers", Settlers != other.Settlers), ("buildings", Buildings != other.Buildings),
            ("piles", Piles != other.Piles), ("map", Map != other.Map), ("mana", Mana != other.Mana),
        }.Where(p => p.Differs).Select(p => p.Name).ToArray();

    /// <summary>All parts in one.</summary>
    public ulong Total
    {
        get
        {
            var hash = new WorldChecksum.Hash();
            foreach (ulong part in new[] { (ulong)Tick, Settlers, Buildings, Piles, Map, Mana })
                hash.Add((long)part);
            return hash.Value;
        }
    }
}

/// <summary>
/// A 64-bit checksum of what the simulation is about: tick, settlers, buildings, piles, map and mana, each part on its own
/// (<see cref="Parts"/>). Two worlds that simulate alike have the same checksum on every tick, whatever the machine; a lockstep
/// game compares them between the computers to notice a desync, and the determinism check compares them between platforms.
/// Lists are taken in their own order, which is part of the state. A Release build takes a few milliseconds on the largest map.
/// </summary>
public static class WorldChecksum
{
    public static ulong Of(World world) => Parts(world).Total;

    public static WorldChecksums Parts(World world)
    {
        var settlers = new Hash();
        foreach (var s in world.Settlers)
        {
            settlers.Add(s.Id);
            settlers.Add(s.Player);
            settlers.Add((int)s.Profession);
            settlers.Add(s.Trade);
            settlers.Add(s.Position);
            settlers.Add(s.StepProgress);
            settlers.Add(s.Carrying is { } carrying ? (int)carrying : -1);
            settlers.Add(BitConverter.SingleToInt32Bits(s.Health));
            settlers.Add(s.SoldierType is { } type ? (int)type : -1);
            settlers.Add(s.Level);
            settlers.Add(s.IsInside ? 1 : 0);
            settlers.Add(s.HasTask ? 1 : 0);
        }
        var buildings = new Hash();
        foreach (var b in world.Buildings)
        {
            buildings.Add(b.Id);
            buildings.Add(b.Definition.Name);
            buildings.Add(b.Player);
            buildings.Add((int)b.State);
            buildings.Add(b.Exists ? 1 : 0);
            foreach (var pile in b.Piles)
                buildings.Add(pile.Count);
        }
        var piles = new Hash();
        foreach (var p in world.Piles)
        {
            piles.Add(p.Id);
            piles.Add(p.Material is { } material ? (int)material : -1);
            piles.Add(p.Count);
            piles.Add(p.Position);
        }
        var map = new Hash();
        if (world.Map is { } grid)
            for (int y = 0; y < grid.Size; y++)
            for (int x = 0; x < grid.Size; x++)
            {
                var point = new GridPoint(x, y);
                // One step per field: object code, height and owner side by side.
                map.Add(((long)grid.ObjectCodeAt(point) << 32) ^ ((long)grid.HeightAt(point) << 16) ^ (long)((grid.OwnerAt(point) ?? -1) & 0xffff));
            }
        var mana = new Hash();
        for (int player = 0; player < world.PlayerCount; player++)
            mana.Add(BitConverter.DoubleToInt64Bits(world.Magic.Mana(player)));
        return new WorldChecksums(world.Tick, settlers.Value, buildings.Value, piles.Value, map.Value, mana.Value);
    }

    /// <summary>
    /// FNV-1a on whole 64-bit words instead of bytes (eight times fewer steps), each step mixed down so high bits reach the
    /// low ones. Not for security: only to notice by chance that two worlds differ.
    /// </summary>
    internal struct Hash()
    {
        public ulong Value { get; private set; } = 14695981039346656037;

        public void Add(long value)
        {
            ulong mixed = (Value ^ (ulong)value) * 1099511628211;
            Value = mixed ^ (mixed >> 29);
        }

        public void Add(GridPoint point) => Add(((long)point.X << 32) ^ (uint)point.Y);

        public void Add(string? text)
        {
            Add(text?.Length ?? -1);
            foreach (char c in text ?? "")
                Add(c);
        }
    }
}
