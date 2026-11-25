using System.Text.Json;
using Homestead.Simulation.Ai;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Map;
using Xunit.Abstractions;

namespace Homestead.Simulation.Tests;

/// <summary>What the chat with the computer players rests on: the situation it judges by and the challenge it may take up.</summary>
public class AiChatTests(ITestOutputHelper output)
{
    private static readonly BuildingCatalog Romans = BuildingCatalog.Load(Civilization.Roman);

    /// <summary>
    /// Player 1 (index 0, the AI) with a tower at 40,80 and five swordsmen outside; player 2 with a tower at 40,130 held by one
    /// and a big army far off at 140,20 – too strong as a whole for a campaign of the AI's own.
    /// </summary>
    private static (World World, AiPlayer Ai, Building EnemyTower) Standoff(int idle = 5, int farArmy = 12)
    {
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 80), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        for (int i = 0; i < idle; i++)
            world.SpawnSoldier(new GridPoint(44 + i, 84), SoldierType.Swordsman);
        var enemyTower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(40, 130), force: true)!;
        world.SpawnSoldier(enemyTower.Door, SoldierType.Swordsman, player: 1);
        for (int i = 0; i < farArmy; i++)
            world.SpawnSoldier(new GridPoint(130 + i % 6, 20 + i / 6), SoldierType.Swordsman, player: 1);
        var ai = new AiPlayer(world, Romans, 0, character: "standard");
        world.AiPlayers.Add(ai);
        return (world, ai, enemyTower);
    }

    [Fact]
    public void A_challenge_starts_a_campaign_against_the_challenger_though_its_whole_army_is_stronger()
    {
        var (world, ai, enemyTower) = Standoff();
        Run(world, 20 * World.TicksPerSecond);
        Assert.Null(ai.CampaignAgainst);

        Assert.True(ai.Challenge(1));
        Assert.Equal(1, ai.ChallengedBy);
        Run(world, 2 * World.TicksPerSecond);
        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Null(ai.ChallengedBy);
        Assert.Equal(1, ai.CampaignAgainst);
        Assert.Contains(ai.Log, l => l.Contains("takes up the challenge of player 2"));
        // An ordinary campaign from here: it gathers, then attacks the tower.
        Run(world, 90 * World.TicksPerSecond, () => ai.CampaignAttacking);
        Assert.True(ai.CampaignAttacking);
        Assert.Contains(world.Settlers, s => s.Player == 0 && world.Military.AttackTarget(s) == enemyTower);
        // No second challenge while it runs.
        Assert.False(ai.Challenge(1));
    }

    [Fact]
    public void A_campaign_ends_when_an_ally_takes_the_target()
    {
        // A save: the human took the Egyptian tower two allies were attacking; their campaigns went on against the
        // human's tower for good, and every later "attack them" in the chat was promised but refused.
        var (world, ai, enemyTower) = Standoff();
        world.PlayerCount = 3;
        world.Teams.AddRange([1, 0, 1]);
        Assert.True(ai.Challenge(1));
        Run(world, 90 * World.TicksPerSecond, () => ai.CampaignAttacking);
        Assert.True(ai.CampaignAttacking);

        typeof(Building).GetProperty(nameof(Building.Player))!.SetValue(enemyTower, 2); // as taken by player 3
        Run(world, 5 * World.TicksPerSecond);
        Assert.Null(ai.CampaignAgainst);
        Assert.Contains(ai.Log, l => l.Contains("player 3 took the tower"));
        Assert.DoesNotContain(world.Settlers, s => s.Player == 0 && world.Military.AttackTarget(s) == enemyTower);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_offensive_marches_on_from_target_to_target_and_joins_the_allied_front(bool allyAtFarTower)
    {
        // Player 1 (the AI) and player 3 on one team against player 2: towers at 40,90 and 40,125 (35 fields apart, within
        // offensive.range), a third one far off at 130,140 – taken only when player 3's soldiers stand there.
        var map = new GameMap(200);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 3 };
        world.Teams.AddRange([1, 0, 1]);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 40), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        for (int i = 0; i < 12; i++)
            world.SpawnSoldier(new GridPoint(44 + i % 6, 46 + i / 6), SoldierType.Swordsman);
        var near = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(40, 90), force: true)!;
        var next = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(40, 125), force: true)!;
        var far = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(130, 140), force: true)!;
        foreach (var enemy in new[] { near, next, far })
            world.SpawnSoldier(enemy.Door, SoldierType.Swordsman, player: 1);
        var ai = new AiPlayer(world, Romans, 0, character: "standard");
        world.AiPlayers.Add(ai);

        Assert.True(ai.StartOffensive(1));
        Assert.Equal(1, ai.OffensiveAgainst);
        Assert.False(ai.StartOffensive(1));
        Assert.Contains("offensive through enemy land", AiSituation.Build(world, 2).Players[0].Doing);
        Run(world, 900 * World.TicksPerSecond, () => next.Player == 0);
        // Player 3's soldiers fighting at the far tower when the second one falls: the offensive comes along.
        var ally = allyAtFarTower ? world.SpawnSoldier(new GridPoint(126, 142), SoldierType.Swordsman, player: 2) : null;
        Run(world, 900 * World.TicksPerSecond, () => ai.OffensiveAgainst is null);
        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Null(ai.OffensiveAgainst);
        Assert.Contains(ai.Log, l => l.Contains("no target left within reach"));
        Assert.Equal(0, near.Player);
        Assert.Equal(0, next.Player);
        Assert.Equal(allyAtFarTower ? 0 : 1, far.Player);
    }

    [Fact]
    public void An_offensive_ends_with_the_first_retreat()
    {
        // Too few against a strong garrison: it gives up or retreats, and the offensive is over.
        var (world, ai, enemyTower) = Standoff(idle: 2);
        Assert.True(ai.StartOffensive(1));
        Run(world, 30 * World.TicksPerSecond, () => ai.CampaignAgainst is not null);
        Assert.Equal(1, ai.CampaignAgainst);
        for (int i = 0; i < 8; i++)
            world.SpawnSoldier(new GridPoint(40 + i % 4, 136 + i / 4), SoldierType.Swordsman, player: 1);
        Run(world, 300 * World.TicksPerSecond, () => ai.OffensiveAgainst is null);
        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Null(ai.OffensiveAgainst);
        Assert.Contains(ai.Log, l => l.Contains("after 1 campaigns: retreated") || l.Contains("after 1 campaigns: gave up a campaign"));
        Assert.Equal(1, enemyTower.Player);
    }

    [Fact]
    public void A_skirmisher_takes_up_a_challenge_far_beyond_its_reach_with_half_its_idle_fighters()
    {
        // The skirmisher only campaigns within 60 fields of its land (attack.maxDistance); a challenge goes anyway (a save:
        // the Amazons promised an attack 422 fields away twice and sent nobody).
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 2 };
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 20), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        for (int i = 0; i < 8; i++)
            world.SpawnSoldier(new GridPoint(42 + i, 26), SoldierType.Swordsman);
        var enemyTower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(40, 150), force: true)!;
        world.SpawnSoldier(enemyTower.Door, SoldierType.Swordsman, player: 1);
        var ai = new AiPlayer(world, Romans, 0, character: "skirmisher");
        world.AiPlayers.Add(ai);
        Run(world, 20 * World.TicksPerSecond);
        Assert.Null(ai.CampaignAgainst);
        Assert.True(ai.CanTakeUpChallenge(1));

        Assert.True(ai.Challenge(1));
        Run(world, 2 * World.TicksPerSecond);
        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Equal(1, ai.CampaignAgainst);
        string gathers = Assert.Single(ai.Log, l => l.Contains("gathers"));
        int count = int.Parse(gathers.Split("gathers ")[1].Split(' ')[0]);
        Assert.InRange(count, 4, 8);
        Assert.False(ai.CanTakeUpChallenge(1));
    }

    [Fact]
    public void The_situation_tells_when_a_computer_player_cannot_attack_and_keeps_umlauts()
    {
        var (armed, _, _) = Standoff();
        Run(armed, 2 * World.TicksPerSecond);
        Assert.Null(AiSituation.Build(armed, 1).Players.Single(p => p.Player == 1).CannotAttackSpeaker);
        // Nobody but the garrison: nothing to attack with.
        var (world, ai, _) = Standoff(idle: 0);
        Run(world, 2 * World.TicksPerSecond);
        world.Say(1, "Ätsch, du traust dich nicht!");
        Assert.False(ai.CanTakeUpChallenge(1));
        Assert.True(AiSituation.Build(world, 1).Players.Single(p => p.Player == 1).CannotAttackSpeaker);
        Assert.Contains("Ätsch", AiSituation.Describe(world, 1));
    }

    [Fact]
    public void An_ally_cannot_challenge()
    {
        var (world, ai, _) = Standoff();
        world.Teams.AddRange([1, 1]);
        Assert.False(ai.Challenge(1));
        Assert.Null(ai.ChallengedBy);
    }

    [Fact]
    public void The_situation_tells_strength_against_the_speaker_distance_and_what_the_ai_is_up_to()
    {
        var (world, ai, _) = Standoff();
        Run(world, 2 * World.TicksPerSecond);
        world.Say(1, "Come and get me, coward!");
        ai.Challenge(1);

        var report = AiSituation.Build(world, speaker: 1);
        Assert.Equal(2, report.Speaker);
        var computer = report.Players.Single(p => p.Player == 1);
        var human = report.Players.Single(p => p.Player == 2);
        Assert.Equal("standard", computer.Character);
        Assert.Null(human.Character);
        Assert.Equal(6, computer.Soldiers.Total);
        Assert.Equal(13, human.Soldiers.Swordsmen);
        Assert.Equal(0.5, computer.PowerVsSpeaker!.Value, 2);
        Assert.Null(computer.Soldiers.Bowmen);
        Assert.Null(computer.Team);
        Assert.Null(computer.Defeated);
        Assert.Null(human.PowerVsSpeaker);
        Assert.InRange(computer.DistanceToSpeaker!.Value, 40, 60);
        Assert.Equal("about to attack player 2", computer.Doing);
        Assert.Equal(1, computer.MilitaryBuildings);
        Assert.True(computer.Land > 0);
        Assert.Equal([new AiSituation.ChatLine(0, 2, "Come and get me, coward!")], report.Chat);

        string json = AiSituation.Describe(world, 1);
        output.WriteLine(json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("speaker").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("players").GetArrayLength());
    }

    [Fact]
    public void A_truce_calls_the_campaign_off_and_holds_until_it_runs_out()
    {
        // Without the far army the AI campaigns against the tower on its own.
        var (world, ai, _) = Standoff(farArmy: 0);
        Run(world, 30 * World.TicksPerSecond, () => ai.CampaignAgainst is not null);
        Assert.Equal(1, ai.CampaignAgainst);

        Assert.True(ai.Truce(1, 60 * World.TicksPerSecond));
        Assert.Null(ai.CampaignAgainst);
        Assert.Contains("truce with player 2", AiSituation.Build(world, 1).Players[0].Doing);
        Run(world, 50 * World.TicksPerSecond);
        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.Null(ai.CampaignAgainst);
        Run(world, 40 * World.TicksPerSecond, () => ai.CampaignAgainst is not null);
        Assert.Equal(1, ai.CampaignAgainst);
        Assert.Contains(ai.Log, l => l.Contains("truce with player 2 over: time is up"));
    }

    [Fact]
    public void Enemies_on_the_own_land_break_a_truce()
    {
        var (world, ai, _) = Standoff(farArmy: 0);
        Assert.True(ai.Truce(1, 300 * World.TicksPerSecond));
        Run(world, 5 * World.TicksPerSecond);
        world.SpawnSoldier(new GridPoint(40, 90), SoldierType.Swordsman, player: 1);
        Run(world, 10 * World.TicksPerSecond);
        Assert.Null(ai.TruceLeft(1));
        Assert.Contains(ai.Log, l => l.Contains("over: its soldiers stand on the own land"));
    }

    [Fact]
    public void A_truce_or_help_needs_the_right_side()
    {
        var (world, ai, _) = Standoff();
        Assert.False(ai.Help(1, 600));
        world.Teams.AddRange([1, 1]);
        Assert.False(ai.Truce(1, 600));
    }

    [Fact]
    public void Help_goes_to_the_ally_fights_the_enemies_there_and_comes_home()
    {
        // Player 1 (the AI) and player 2 on one team; an enemy of player 3 stands on player 2's land.
        var map = new GameMap(160);
        var world = new World(map, TestConfig.NoTools) { TerritoryRules = true, PlayerCount = 3 };
        world.Teams.AddRange([1, 1, 0]);
        var tower = world.PlaceBuilding(Romans["tower"], 0, new GridPoint(40, 40), force: true)!;
        world.SpawnSoldier(tower.Door, SoldierType.Swordsman);
        for (int i = 0; i < 6; i++)
            world.SpawnSoldier(new GridPoint(44 + i, 46), SoldierType.Swordsman);
        var allyTower = world.PlaceBuilding(Romans["tower"], 1, new GridPoint(40, 110), force: true)!;
        world.SpawnSoldier(allyTower.Door, SoldierType.Swordsman, player: 1);
        var ai = new AiPlayer(world, Romans, 0, character: "standard");
        world.AiPlayers.Add(ai);
        Run(world, 5 * World.TicksPerSecond);
        var intruder = world.SpawnSoldier(new GridPoint(50, 116), SoldierType.Swordsman, player: 2)!;
        Assert.True(ai.CanHelp(1));

        Assert.True(ai.Help(1, 90 * World.TicksPerSecond));
        Assert.Equal(1, ai.Helping);
        Assert.Equal(3, ai.HelperCount);
        Assert.False(ai.CanHelp(1));
        Assert.Contains("helping player 2 with 3 soldiers", AiSituation.Build(world, 1).Players[0].Doing);
        Run(world, 80 * World.TicksPerSecond, () => intruder.IsDead);
        foreach (string line in ai.Log)
            output.WriteLine(line);
        Assert.True(intruder.IsDead);
        // They stay on the ally's land for the time agreed, then go home.
        Assert.Equal(1, ai.Helping);
        Run(world, 100 * World.TicksPerSecond, () => ai.Helping is null);
        Assert.Null(ai.Helping);
        Assert.Contains(ai.Log, l => l.Contains("helpers come home from player 2"));
    }

    private static void Run(World world, int ticks, Func<bool>? stopWhen = null)
    {
        for (int t = 0; t < ticks && stopWhen?.Invoke() != true; t++)
            world.Step();
    }
}
