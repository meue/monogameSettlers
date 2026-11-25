using Homestead.Simulation.Buildings;

namespace Homestead.Simulation;

/// <summary>
/// What every player had at one moment of the game (see <see cref="GameHistory"/>); the counts of events
/// (kills, losses, buildings captured …) are totals up to then.
/// </summary>
public sealed class HistorySample
{
    public long Tick { get; init; }
    public int Land { get; init; }
    public int Settlers { get; init; }
    public int Carriers { get; init; }
    public int Specialists { get; init; }
    public int Soldiers { get; init; }
    public int Swordsmen { get; init; }
    public int Bowmen { get; init; }
    public int Pikemen { get; init; }
    public int Priests { get; init; }
    public int Buildings { get; init; }
    public int Sites { get; init; }
    public int MilitaryBuildings { get; init; }
    public int Room { get; init; }
    public int Striking { get; init; }
    public int Stock { get; init; }
    public int Produced { get; init; }
    public float CombatStrength { get; init; }
    public int Mana { get; init; }
    public int ManaEarned { get; init; }
    public int Kills { get; init; }
    public int Losses { get; init; }
    public int Captured { get; init; }
    public int Lost { get; init; }
    public int Finished { get; init; }
    public int Destroyed { get; init; }
}

/// <summary>
/// The course of the game for the statistics screen: every <see cref="SampleTicks"/> one
/// <see cref="HistorySample"/> per player, and running totals of what happened (soldiers killed and lost,
/// military buildings captured and lost, buildings finished and destroyed). Goods produced per period are in
/// <see cref="Economy.ProductionStatistics"/>.
/// </summary>
public sealed class GameHistory
{
    /// <summary>A sample every 30 seconds.</summary>
    public const int SampleTicks = 30 * World.TicksPerSecond;

    private readonly World _world;
    private readonly Dictionary<int, List<HistorySample>> _samples = new();
    private readonly Dictionary<int, int> _kills = new(), _losses = new(), _captured = new(), _lost = new(), _finished = new(), _destroyed = new();

    // Between two players (key: by * 256 + victim): soldiers killed and military buildings captured. Lazy: games saved before have none.
    private Dictionary<int, int>? _killsOf, _capturedFrom;

    internal GameHistory(World world)
    {
        _world = world;
    }

    /// <summary>The samples of a player, oldest first (empty for a player without any).</summary>
    public IReadOnlyList<HistorySample> Samples(int player) => _samples.TryGetValue(player, out var list) ? list : [];

    /// <summary>The players with samples.</summary>
    public IEnumerable<int> Players => _samples.Keys.OrderBy(p => p);

    internal void Tick()
    {
        if (_world.Tick % SampleTicks == 0)
            Sample();
    }

    internal void Killed(Settler victim, Settler? by)
    {
        _losses[victim.Player] = _losses.GetValueOrDefault(victim.Player) + 1;
        if (by is not null)
        {
            _kills[by.Player] = _kills.GetValueOrDefault(by.Player) + 1;
            Count(_killsOf ??= new(), by.Player, victim.Player);
        }
    }

    internal void Captured(int winner, int loser)
    {
        _captured[winner] = _captured.GetValueOrDefault(winner) + 1;
        _lost[loser] = _lost.GetValueOrDefault(loser) + 1;
        Count(_capturedFrom ??= new(), winner, loser);
    }

    private static void Count(Dictionary<int, int> counts, int by, int victim) =>
        counts[by * 256 + victim] = counts.GetValueOrDefault(by * 256 + victim) + 1;

    /// <summary>Soldiers of <paramref name="victim"/> killed by soldiers of <paramref name="by"/> so far.</summary>
    public int KillsOf(int by, int victim) => _killsOf?.GetValueOrDefault(by * 256 + victim) ?? 0;

    /// <summary>Military buildings <paramref name="by"/> captured from <paramref name="victim"/> so far.</summary>
    public int CapturedFrom(int by, int victim) => _capturedFrom?.GetValueOrDefault(by * 256 + victim) ?? 0;

    internal void Finished(Building building) => _finished[building.Player] = _finished.GetValueOrDefault(building.Player) + 1;

    internal void Destroyed(Building building) => _destroyed[building.Player] = _destroyed.GetValueOrDefault(building.Player) + 1;

    /// <summary>Takes a sample of every player now (also called when the statistics screen opens; replaces one of the same tick).</summary>
    public void Sample()
    {
        int players = Math.Max(_world.PlayerCount, 1);
        var land = new int[players];
        if (_world.Map is { } map)
            for (int y = 0; y < map.Size; y++)
                for (int x = 0; x < map.Size; x++)
                    if (map.OwnerAt(new GridPoint(x, y)) is { } owner && owner < players)
                        land[owner]++;
        var production = _world.Statistics;
        var materials = Enum.GetValues<Economy.Material>();
        for (int player = 0; player < players; player++)
        {
            var settlers = _world.Settlers.Where(s => s.Player == player && !s.IsDead).ToList();
            var buildings = _world.Buildings.Where(b => b.Exists && b.Player == player).ToList();
            var sample = new HistorySample
            {
                Tick = _world.Tick,
                Land = land[player],
                Settlers = settlers.Count,
                Carriers = settlers.Count(s => s.IsCarrier),
                Soldiers = settlers.Count(s => s.Profession == Profession.Soldier),
                Swordsmen = settlers.Count(s => s.SoldierType == SoldierType.Swordsman),
                Bowmen = settlers.Count(s => s.SoldierType == SoldierType.Bowman),
                Pikemen = settlers.Count(s => s.SoldierType == SoldierType.Pikeman),
                Priests = settlers.Count(s => s.Profession == Profession.Priest),
                Specialists = settlers.Count(s => s.Profession is not (Profession.Carrier or Profession.Soldier or Profession.Priest or Profession.None or Profession.Donkey or Profession.WarMachine)),
                Buildings = buildings.Count(b => b.State == BuildingState.Finished),
                Sites = buildings.Count(b => b.State != BuildingState.Finished),
                MilitaryBuildings = buildings.Count(b => b.State == BuildingState.Finished && b.Definition.Occupiers.Count > 0),
                Room = _world.Housing.Room(player) ?? 0,
                Striking = _world.Housing.Striking(player),
                Stock = materials.Sum(m => _world.InStock(player, m)),
                Produced = materials.Sum(m => production.Total(player, m)),
                CombatStrength = _world.Military.CombatStrength(player),
                Mana = _world.Magic.Mana(player),
                ManaEarned = _world.Magic.ManaEarned(player),
                Kills = _kills.GetValueOrDefault(player),
                Losses = _losses.GetValueOrDefault(player),
                Captured = _captured.GetValueOrDefault(player),
                Lost = _lost.GetValueOrDefault(player),
                Finished = _finished.GetValueOrDefault(player),
                Destroyed = _destroyed.GetValueOrDefault(player),
            };
            if (!_samples.TryGetValue(player, out var list))
                _samples[player] = list = new List<HistorySample>();
            if (list.Count > 0 && list[^1].Tick == sample.Tick)
                list[^1] = sample;
            else
                list.Add(sample);
        }
    }
}
