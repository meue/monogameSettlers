using System.Text.Json;
using Homestead.Simulation;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Client;

/// <summary>
/// The computer players also speak up unasked (<c>reactions</c> in data/ai-chat.json): goods the own player's donkeys put
/// down on their land (<see cref="World.GoodsHandedOver"/>) are tallied by worth per receiver and answered once the tally
/// reaches <c>giftPleased</c> or falls to <c>giftInsulted</c> (raw logs are worth less than nothing); a military building
/// taken (<c>captureChance</c>) or a big fight within one statistics sample (<c>bigFightSoldiers</c> killed and lost,
/// <c>chance</c>) is answered by chance, at most once per <c>cooldownSeconds</c> per computer player; the own army marching up
/// at an enemy (<c>army</c>: at least <c>soldiers</c> outside on its land or within <c>range</c> of its military buildings,
/// counted once a second; allies are not threatened by it) by its own chance and cooldown, once each time it gets that many. Such a request says what happened instead of a chat line.
/// </summary>
public sealed partial class AiChat
{
    private readonly double _reactionChance, _captureChance;
    private readonly long _reactionCooldownTicks;
    private readonly int _bigFightSoldiers;
    private readonly double _giftPleased, _giftInsulted;
    private readonly IReadOnlyDictionary<Material, double> _giftValues;
    private readonly double _giftOtherValue;
    private readonly Random _random = new();
    private readonly (int Soldiers, int Range, double Chance, long CooldownTicks) _army;

    // Per computer player: whether the own army stood at its land at the last count, and when it last spoke up about one.
    private readonly HashSet<int> _armyThere = new();
    private readonly Dictionary<int, long> _lastArmy = new();
    private long _armyCounted;

    // What happened since the last request, said along with the next one.
    private readonly List<string> _happenings = new();
    private readonly Dictionary<int, long> _lastReaction = new();

    // The game listened to and what of it is already seen.
    private World? _watched;
    private int _eventsSeen;
    private readonly Dictionary<int, int> _samplesSeen = new();
    private readonly Dictionary<int, (double Worth, Dictionary<Material, int> Goods)> _gifts = new();

    private static (int Soldiers, int Range, double Chance, long CooldownTicks) ReadArmy(JsonElement data)
    {
        var army = data.GetProperty("reactions").GetProperty("army");
        return (army.GetProperty("soldiers").GetInt32(), army.GetProperty("range").GetInt32(), army.GetProperty("chance").GetDouble(),
            army.GetProperty("cooldownSeconds").GetInt32() * (long)World.TicksPerSecond);
    }

    private static (double Chance, double Capture, long CooldownTicks, int BigFight, double Pleased, double Insulted, Dictionary<Material, double> Values, double Other)
        ReadReactions(JsonElement data)
    {
        var r = data.GetProperty("reactions");
        var values = new Dictionary<Material, double>();
        double other = 0;
        foreach (var entry in r.GetProperty("giftValues").EnumerateObject())
        {
            if (entry.Name == "other")
                other = entry.Value.GetDouble();
            else if (Enum.TryParse(entry.Name, out Material material))
                values[material] = entry.Value.GetDouble();
        }
        return (r.GetProperty("chance").GetDouble(), r.GetProperty("captureChance").GetDouble(), r.GetProperty("cooldownSeconds").GetInt32() * (long)World.TicksPerSecond,
            r.GetProperty("bigFightSoldiers").GetInt32(), r.GetProperty("giftPleased").GetDouble(), r.GetProperty("giftInsulted").GetDouble(),
            values, other);
    }

    /// <summary>Listens to the game (a new one: starts over with nothing seen).</summary>
    private void Watch(World world)
    {
        if (_watched == world)
            return;
        if (_watched is not null)
            _watched.GoodsHandedOver -= HandedOver;
        _watched = world;
        world.GoodsHandedOver += HandedOver;
        _happenings.Clear();
        _gifts.Clear();
        _lastReaction.Clear();
        _armyThere.Clear();
        _lastArmy.Clear();
        _armyCounted = world.Tick;
        _eventsSeen = world.Events.Count;
        _samplesSeen.Clear();
        foreach (int player in world.History.Players)
            _samplesSeen[player] = world.History.Samples(player).Count;
    }

    private void HandedOver(int from, int to, Material material)
    {
        if (from != Player || _watched is not { } world || !IsVoice(world, to))
            return;
        var (worth, goods) = _gifts.TryGetValue(to, out var gift) ? gift : (0, new Dictionary<Material, int>());
        worth += _giftValues.GetValueOrDefault(material, _giftOtherValue);
        goods[material] = goods.GetValueOrDefault(material) + 1;
        _gifts[to] = (worth, goods);
        if (worth < _giftPleased && worth > _giftInsulted)
            return;
        _gifts.Remove(to);
        string list = string.Join(", ", goods.OrderByDescending(g => g.Value).Select(g => $"{g.Value} {g.Key}"));
        string verdict = worth >= _giftPleased ? "a generous gift" : "worthless stuff, raw logs: an insult";
        _happenings.Add($"Player {Player + 1} sent player {to + 1} goods by donkey: {list} ({verdict}). Player {to + 1} answers player {Player + 1}.");
    }

    /// <summary>Once a frame: military buildings taken and big fights since the last look, each answered by chance.</summary>
    private void Notice(World world)
    {
        int players = Math.Max(world.PlayerCount, 1);
        var fresh = Enumerable.Range(0, players).SelectMany(p => world.Events.Since(_eventsSeen, p)).ToList();
        _eventsSeen = world.Events.Count;
        foreach (var captured in fresh.Where(e => e.Definition.Id == "buildingCaptured" && e.Building is not null))
        {
            int winner = captured.Player;
            int? loser = fresh.FirstOrDefault(e => e.Definition.Id == "buildingLost" && e.Building == captured.Building)?.Player;
            string what = captured.Building!.Definition.Name;
            React(world, _captureChance, [winner, .. loser is { } l ? [l] : Array.Empty<int>()],
                $"Player {winner + 1} just took a {what}{(loser is { } from ? $" from player {from + 1}" : "")}.");
        }
        foreach (var ai in world.AiPlayers)
        {
            var samples = world.History.Samples(ai.Player);
            int seen = _samplesSeen.GetValueOrDefault(ai.Player);
            _samplesSeen[ai.Player] = samples.Count;
            if (samples.Count < 2 || samples.Count == seen)
                continue;
            var (now, before) = (samples[^1], samples[^2]);
            int kills = now.Kills - before.Kills, losses = now.Losses - before.Losses;
            if (kills + losses < _bigFightSoldiers)
                continue;
            string outcome = kills >= 2 * losses ? "won a big fight" : losses >= 2 * kills ? "lost a big fight" : "came out of a bloody fight";
            React(world, _reactionChance, [ai.Player], $"Player {ai.Player + 1} {outcome} just now ({kills} enemies killed, {losses} own soldiers lost).");
        }
        if (world.Tick - _armyCounted >= World.TicksPerSecond)
        {
            _armyCounted = world.Tick;
            NoticeArmy(world);
        }
    }

    /// <summary>The own soldiers outside at each computer player's land; reaching <c>army.soldiers</c> is answered by chance.</summary>
    private void NoticeArmy(World world)
    {
        var map = world.Map!;
        var soldiers = world.Settlers.Where(s => s.Player == Player && s.Profession == Profession.Soldier && !s.IsDead && !s.IsInside).ToList();
        foreach (var ai in world.AiPlayers.Where(a => IsVoice(world, a.Player) && !world.AreAllies(Player, a.Player)))
        {
            var military = world.Buildings.Where(b => b.Exists && b.Player == ai.Player && b.Definition.Occupiers.Count > 0)
                .Select(b => b.Position).ToList();
            int there = soldiers.Count(s => map.OwnerAt(s.Position) == ai.Player
                                            || military.Any(m => PathFinder.Distance(m, s.Position) <= _army.Range));
            if (there < _army.Soldiers)
            {
                _armyThere.Remove(ai.Player);
                continue;
            }
            if (!_armyThere.Add(ai.Player) || _random.NextDouble() >= _army.Chance
                || _lastArmy.TryGetValue(ai.Player, out long last) && world.Tick - last < _army.CooldownTicks)
                continue;
            _lastArmy[ai.Player] = world.Tick;
            bool inside = soldiers.Count(s => map.OwnerAt(s.Position) == ai.Player) >= _army.Soldiers / 2;
            _happenings.Add($"Player {Player + 1} marches up with an army of {there} soldiers {(inside ? "onto the land" : "at the border")} of player {ai.Player + 1}.");
        }
    }

    /// <summary>By chance, and not while one of the computer players concerned spoke up lately: says it along with the next request.</summary>
    private void React(World world, double chance, int[] concerned, string happening)
    {
        var voices = concerned.Where(p => IsVoice(world, p)).ToList();
        if (voices.Count == 0 || _random.NextDouble() >= chance
            || voices.Any(p => _lastReaction.TryGetValue(p, out long last) && world.Tick - last < _reactionCooldownTicks))
            return;
        foreach (int p in voices)
            _lastReaction[p] = world.Tick;
        _happenings.Add(happening);
    }
}
