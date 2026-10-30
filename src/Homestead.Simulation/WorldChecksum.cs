using Homestead.Simulation.Map;

namespace Homestead.Simulation;

/// <summary>
/// A 64-bit checksum (FNV-1a) of what the simulation is about: tick, settlers, buildings, piles, map and mana.
/// Two worlds that simulate alike have the same checksum on every tick, whatever the machine; a lockstep game
/// compares them between the clients to notice a desync, and the determinism check compares them between platforms.
/// Lists are taken in their own order, which is part of the state.
/// </summary>
public static class WorldChecksum
{
    public static ulong Of(World world)
    {
        var hash = new Fnv();
        hash.Add(world.Tick);
        foreach (var s in world.Settlers)
        {
            hash.Add(s.Id);
            hash.Add(s.Player);
            hash.Add((int)s.Profession);
            hash.Add(s.Trade);
            hash.Add(s.Position);
            hash.Add(s.StepProgress);
            hash.Add(s.Carrying is { } carrying ? (int)carrying : -1);
            hash.Add(BitConverter.SingleToInt32Bits(s.Health));
            hash.Add(s.SoldierType is { } type ? (int)type : -1);
            hash.Add(s.Level);
            hash.Add(s.IsInside ? 1 : 0);
            hash.Add(s.HasTask ? 1 : 0);
        }
        foreach (var b in world.Buildings)
        {
            hash.Add(b.Id);
            hash.Add(b.Definition.Name);
            hash.Add(b.Player);
            hash.Add((int)b.State);
            hash.Add(b.Exists ? 1 : 0);
            foreach (var pile in b.Piles)
                hash.Add(pile.Count);
        }
        foreach (var p in world.Piles)
        {
            hash.Add(p.Id);
            hash.Add(p.Material is { } material ? (int)material : -1);
            hash.Add(p.Count);
            hash.Add(p.Position);
        }
        if (world.Map is { } map)
            for (int y = 0; y < map.Size; y++)
            for (int x = 0; x < map.Size; x++)
            {
                var point = new GridPoint(x, y);
                hash.Add(map.ObjectCodeAt(point));
                hash.Add(map.HeightAt(point));
                hash.Add(map.OwnerAt(point) ?? -1);
            }
        for (int player = 0; player < world.PlayerCount; player++)
            hash.Add(BitConverter.DoubleToInt64Bits(world.Magic.Mana(player)));
        return hash.Value;
    }

    private struct Fnv()
    {
        public ulong Value { get; private set; } = 14695981039346656037;

        public void Add(long value)
        {
            for (int i = 0; i < 8; i++, value >>= 8)
                Value = (Value ^ (byte)value) * 1099511628211;
        }

        public void Add(GridPoint point)
        {
            Add(point.X);
            Add(point.Y);
        }

        public void Add(string? text)
        {
            Add(text?.Length ?? -1);
            foreach (char c in text ?? "")
                Add(c);
        }
    }
}
