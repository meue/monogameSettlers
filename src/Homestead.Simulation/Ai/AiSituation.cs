using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Ai;

/// <summary>
/// The rough situation of the game for the computer players talking in the chat (<c>AiChat</c> in the client): per
/// player its people, character, army, land and buildings, the fights so far and in the last minutes, who it attacks,
/// how strong it is against the speaker and how far apart they are; plus the latest chat lines. Kept small: it goes
/// along with every chat line. Players are numbered
/// from 1 as the game shows them. Only reads the world.
/// </summary>
public static class AiSituation
{
    /// <summary>How far back the trend looks.</summary>
    public const int TrendMinutes = 5;

    /// <summary>The latest chat lines given along.</summary>
    public const int ChatLines = 16;

    private static readonly JsonSerializerOptions Json = new()
    {
        // Umlauts as they are: escaped (\u00e4) the model sometimes writes them back that way.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed record Report(int Minutes, int Speaker, IReadOnlyList<PlayerState> Players, IReadOnlyList<ChatLine> Chat);

    /// <summary>One player; values that say nothing (no team, zero, false, empty) are left out to keep the request small.</summary>
    public sealed record PlayerState(
        int Player, string People, string? Character, IReadOnlyList<string>? Traits, int? Team, bool? Defeated, bool? AllyOfSpeaker,
        Army Soldiers, double? PowerVsSpeaker, int? DistanceToSpeaker, int? EnemySoldiersOnLand,
        int Land, int MilitaryBuildings, int Buildings, Fights? Fights, Trend? Trend, string? Doing, bool? CannotAttackSpeaker, bool? CannotHelpSpeaker);

    /// <summary>Soldiers by type (types without any left out).</summary>
    public sealed record Army(int Total, int? Swordsmen, int? Bowmen, int? Pikemen);

    /// <summary>Running totals since the start of the game (zeros left out).</summary>
    public sealed record Fights(int? Kills, int? Losses, int? BuildingsCaptured, int? BuildingsLost);

    /// <summary>Change against <see cref="TrendMinutes"/> minutes ago (from the statistics' samples; zeros left out).</summary>
    public sealed record Trend(int? Soldiers, int? Land, int? MilitaryBuildings, int? Kills, int? Losses, int? BuildingsCaptured, int? BuildingsLost);

    public sealed record ChatLine(int Minute, int Player, string Text);

    /// <summary>The situation as <paramref name="speaker"/> (0-based) sees it to talk about, as compact JSON.</summary>
    public static string Describe(World world, int speaker) => JsonSerializer.Serialize(Build(world, speaker), Json);

    public static Report Build(World world, int speaker)
    {
        int players = Math.Max(world.PlayerCount, world.AiPlayers.Select(a => a.Player + 1).DefaultIfEmpty(0).Max());
        players = Math.Max(players, speaker + 1);
        var land = new int[players];
        if (world.Map is { } map)
            for (int y = 0; y < map.Size; y++)
                for (int x = 0; x < map.Size; x++)
                    if (map.OwnerAt(new GridPoint(x, y)) is { } owner && owner < players)
                        land[owner]++;
        var soldiers = world.Settlers.Where(s => s.Profession == Profession.Soldier && !s.IsDead).ToList();
        double Power(int player) => soldiers.Count(s => s.Player == player) * world.Military.CombatStrength(player);
        var speakerBuildings = world.Buildings.Where(b => b.Exists && b.Player == speaker).Select(b => b.Position).ToList();
        int ticksBack = TrendMinutes * 60 * World.TicksPerSecond;

        var states = new List<PlayerState>();
        for (int player = 0; player < players; player++)
        {
            var ai = world.AiPlayers.FirstOrDefault(a => a.Player == player);
            var buildings = world.Buildings.Where(b => b.Exists && b.Player == player).ToList();
            var military = buildings.Where(b => b.State == BuildingState.Finished && b.Definition.Occupiers.Count > 0).ToList();
            var own = soldiers.Where(s => s.Player == player).ToList();
            var samples = world.History.Samples(player);
            var latest = samples.Count > 0 ? samples[^1] : null;
            var before = samples.LastOrDefault(s => s.Tick <= world.Tick - ticksBack);
            int? distance = player == speaker || military.Count == 0 || speakerBuildings.Count == 0
                ? null
                : military.Min(b => speakerBuildings.Min(p => PathFinder.Distance(b.Position, p)));
            double power = Power(player), speakerPower = Power(speaker);
            int invaders = world.Map is { } m
                ? soldiers.Count(s => !s.IsInside && !world.AreAllies(s.Player, player) && m.OwnerAt(s.Position) == player)
                : 0;
            var fights = latest is null ? null : new Fights(Some(latest.Kills), Some(latest.Losses), Some(latest.Captured), Some(latest.Lost));
            var trend = latest is null || before is null ? null : new Trend(
                Some(own.Count - before.Soldiers), Some(land[player] - before.Land), Some(military.Count - before.MilitaryBuildings),
                Some(latest.Kills - before.Kills), Some(latest.Losses - before.Losses), Some(latest.Captured - before.Captured),
                Some(latest.Lost - before.Lost));
            states.Add(new PlayerState(
                Player: player + 1,
                People: world.PeopleOf(player).ToString(),
                Character: ai?.Character.Id,
                Traits: ai?.Character.Traits.Count > 0 ? ai.Character.Traits.Select(t => t.Id).ToList() : null,
                Team: Some(world.TeamOf(player)),
                Defeated: world.Goals?.IsDefeated(player) == true ? true : null,
                AllyOfSpeaker: player != speaker && world.AreAllies(player, speaker) ? true : null,
                Soldiers: new Army(own.Count, Some(own.Count(s => s.SoldierType == SoldierType.Swordsman)),
                    Some(own.Count(s => s.SoldierType == SoldierType.Bowman)), Some(own.Count(s => s.SoldierType == SoldierType.Pikeman))),
                PowerVsSpeaker: player == speaker ? null : speakerPower > 0 ? Math.Round(power / speakerPower, 1) : null,
                DistanceToSpeaker: distance,
                EnemySoldiersOnLand: Some(invaders),
                Land: land[player],
                MilitaryBuildings: military.Count,
                Buildings: buildings.Count(b => b.State == BuildingState.Finished),
                Fights: fights == new Fights(null, null, null, null) ? null : fights,
                Trend: trend == new Trend(null, null, null, null, null, null, null) ? null : trend,
                Doing: ai is null ? null : Doing(ai, players),
                CannotAttackSpeaker: ai is not null && player != speaker && !world.AreAllies(player, speaker) && ai.AssaultOn is null
                                     && ai.ChallengedBy is null && ai.CampaignAgainst is null && !ai.CanTakeUpChallenge(speaker) ? true : null,
                CannotHelpSpeaker: ai is not null && player != speaker && world.AreAllies(player, speaker) && !ai.CanHelp(speaker) ? true : null));
        }
        var chat = world.Chat.Messages.TakeLast(ChatLines)
            .Select(c => new ChatLine((int)(c.Tick / (60 * World.TicksPerSecond)), c.Player + 1, c.Text)).ToList();
        return new Report((int)(world.Tick / (60 * World.TicksPerSecond)), speaker + 1, states, chat);
    }

    /// <summary>The count, or null for none (left out of the request).</summary>
    private static int? Some(int count) => count == 0 ? null : count;

    /// <summary>What the computer player is up to with others, in a few words each.</summary>
    private static string? Doing(AiPlayer ai, int players)
    {
        var doing = new List<string>();
        if (ai.AssaultOn is { } assaulted)
            doing.Add($"all-out assault on player {assaulted + 1}");
        else if (ai.ChallengedBy is { } challenger)
            doing.Add($"about to attack player {challenger + 1}");
        else if (ai.CampaignAgainst is { } target)
            doing.Add($"campaign against player {target + 1} ({(ai.CampaignAttacking ? "attacking" : "gathering")})");
        if (ai.OffensiveAgainst is { } offensive)
            doing.Add($"offensive through enemy land, started against player {offensive + 1} (marching on target after target)");
        if (ai.Helping is { } ally)
            doing.Add($"helping player {ally + 1} with {ai.HelperCount} soldiers");
        for (int player = 0; player < players; player++)
            if (ai.TruceLeft(player) is { } left)
                doing.Add($"truce with player {player + 1} ({Math.Max(1, left / (60 * World.TicksPerSecond))} min left)");
        return doing.Count == 0 ? null : string.Join("; ", doing);
    }
}
