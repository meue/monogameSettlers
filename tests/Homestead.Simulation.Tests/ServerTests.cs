using System.Text.Json.Nodes;
using Homestead.Server;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Lockstep;
using Homestead.Simulation.Map.Generation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Simulation.Tests;

/// <summary>The real server on a free port of this machine, real WebSocket connections.</summary>
public class ServerTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _server = null!;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        _server = ServerApp.Create(["--Logging:LogLevel:Default=Warning"], "http://127.0.0.1:0");
        await _server.StartAsync();
        string http = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _address = new Uri(http.Replace("http://", "ws://") + "/ws");
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task<ServerConnection> Connect(string name, GameFingerprint? fingerprint = null) =>
        ServerConnection.ConnectAsync(_address, name, fingerprint ?? GameFingerprint.Of());

    private static GameSetup Setup() => new()
    {
        Map = MapChoice.Generated(new RandomMapSettings(Players: 3), 6),
        Seed = 12,
        Players = [new PlayerSetup(Human: true), new PlayerSetup(), new PlayerSetup(Human: true)],
    };

    [Fact]
    public async Task Two_computers_meet_in_the_lobby_and_play_the_same_game()
    {
        await using var host = await Connect("Anna");
        await using var guest = await Connect("Ben");

        host.Create("Test", Setup());
        var lobby = await host.WaitForAsync([ServerMessages.Lobby], Wait);
        string id = lobby["game"]!.GetValue<string>();
        Assert.Equal(0, lobby["you"]!.GetValue<int>());

        guest.ListGames();
        var games = (await guest.WaitForAsync([ServerMessages.Games], Wait))["games"]!.AsArray();
        var listed = Assert.Single(games)!;
        Assert.Equal(("Test", "Anna", 2, 1), (listed["name"]!.GetValue<string>(), listed["host"]!.GetValue<string>(),
            listed["seats"]!.GetValue<int>(), listed["taken"]!.GetValue<int>()));
        guest.Join(id);
        var joined = await guest.WaitForAsync([ServerMessages.Lobby], Wait);
        // The second human player's seat: player 3 (player 2 is a computer player).
        Assert.Equal(2, joined["you"]!.GetValue<int>());
        Assert.Equal(["Anna", "Ben"], joined["seats"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()));

        host.Start();
        var computers = new List<(ServerConnection Connection, World World, LockstepSession Session)>();
        foreach (var connection in new[] { host, guest })
        {
            var started = await connection.WaitForAsync([ServerMessages.Started], Wait);
            var setup = GameSetup.FromJson(started["setup"]!.ToJsonString());
            Assert.NotNull(setup.Fingerprint); // the server's: every computer checks it when starting
            var start = setup.Start(setup.Map.Open(""));
            var session = new LockstepSession(start.World, started["player"]!.GetValue<int>(), new CommandCodec(start.Report.Catalogs),
                connection.Link, ServerMessages.Options(started["options"] as JsonObject));
            computers.Add((connection, start.World, session));
        }
        Assert.Equal([0, 2], computers.Select(c => c.Session.LocalPlayer));
        Assert.Equal([1], computers[0].World.AiPlayers.Select(a => a.Player));

        const int Ticks = 40 * World.TicksPerSecond;
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (computers.Any(c => c.World.Tick < Ticks) && DateTime.UtcNow < until)
        {
            bool stepped = false;
            foreach (var (_, world, session) in computers)
                for (int i = 0; i < 5 && world.Tick < Ticks; i++)
                {
                    if (!session.TryStep())
                        break;
                    stepped = true;
                    if (world.Tick == 50 + 30 * session.LocalPlayer)
                        session.Issue(new Say(session.LocalPlayer, $"hi from {session.LocalPlayer + 1}"));
                }
            if (!stepped)
                await Task.Delay(1);
        }

        Assert.All(computers, c => Assert.Equal(Ticks, c.World.Tick));
        Assert.All(computers, c => Assert.Null(c.Session.Desync));
        Assert.All(computers, c => Assert.True(c.Session.ChecksCompared >= 35, $"{c.Session.ChecksCompared} checks"));
        Assert.Equal(WorldChecksum.Of(computers[0].World), WorldChecksum.Of(computers[1].World));
        var chats = computers.Select(c => c.World.Chat.Messages.Select(m => (m.Tick, m.Player, m.Text)).ToList()).ToArray();
        Assert.Equal(chats[0], chats[1]);
        Assert.Equal(["hi from 1", "hi from 3"], chats[0].Select(c => c.Text));

        // One leaves: the other hears of it.
        await guest.DisposeAsync();
        var left = await host.WaitForAsync([ServerMessages.Left], Wait);
        Assert.Equal(2, left["player"]!.GetValue<int>());
    }

    [Fact]
    public async Task Another_network_protocol_is_turned_away_at_once()
    {
        var refused = await Assert.ThrowsAsync<ServerRefusedException>(() => Connect("Old", GameFingerprint.Of() with { Protocol = 0 }));
        Assert.Contains("network protocol 0", Assert.Single(refused.Reasons));
    }

    [Fact]
    public async Task Joining_needs_the_hosts_program_and_data()
    {
        await using var host = await Connect("Anna");
        host.Create("Test", Setup());
        string id = (await host.WaitForAsync([ServerMessages.Lobby], Wait))["game"]!.GetValue<string>();

        var mine = GameFingerprint.Of();
        var files = mine.Files.ToDictionary(f => f.Key, f => f.Value);
        files["production.json"] = "0";
        await using var other = await Connect("Ben", mine with { Files = files });
        other.Join(id);
        var refused = await other.WaitForAsync([ServerMessages.Refused, ServerMessages.Lobby], Wait);
        Assert.Equal(ServerMessages.Refused, ServerMessages.TypeOf(refused));
        Assert.Equal(["data/production.json differs"], ServerConnection.Reasons(refused));

        await using var newer = await Connect("Cleo", mine with { Version = "9.9.9" });
        newer.Join(id);
        Assert.Contains("program version 9.9.9", Assert.Single(ServerConnection.Reasons(await newer.WaitForAsync([ServerMessages.Refused], Wait))));

        // Not started without every seat taken; the host leaving closes the lobby for the others.
        host.Start();
        Assert.Equal("not every seat is taken", (await host.WaitForAsync([ServerMessages.Error], Wait))["message"]!.GetValue<string>());
        await using var guest = await Connect("Dana");
        guest.Join(id);
        await guest.WaitForAsync([ServerMessages.Lobby], Wait);
        host.Leave();
        Assert.Equal(id, (await guest.WaitForAsync([ServerMessages.Closed], Wait))["game"]!.GetValue<string>());
        guest.ListGames();
        Assert.Empty((await guest.WaitForAsync([ServerMessages.Games], Wait))["games"]!.AsArray());
    }

    [Fact]
    public async Task A_garbled_message_is_answered_and_the_connection_stays()
    {
        await using var client = await Connect("Anna");
        // A number where the game's id belongs, then a wrong kind of message: errors, but still connected.
        var outgoing = typeof(ServerConnection).GetField("_outgoing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(client) as System.Threading.Channels.Channel<string>;
        outgoing!.Writer.TryWrite("{\"type\":\"join\",\"game\":42}");
        outgoing.Writer.TryWrite("{\"type\":\"dance\"}");
        Assert.Equal("unreadable message", (await client.WaitForAsync([ServerMessages.Error], Wait))["message"]!.GetValue<string>());
        Assert.Equal("unknown message 'dance'", (await client.WaitForAsync([ServerMessages.Error], Wait))["message"]!.GetValue<string>());
        client.ListGames();
        await client.WaitForAsync([ServerMessages.Games], Wait);
        Assert.True(client.IsOpen);
    }

    [Fact]
    public async Task The_server_tells_its_state_over_http()
    {
        using var http = new HttpClient();
        string status = await http.GetStringAsync(new Uri(_address.ToString().Replace("ws://", "http://").Replace("/ws", "/")));
        Assert.Contains($"protocol {GameFingerprint.CurrentProtocol}", status);
        Assert.Contains("0 games", status);
    }
}
