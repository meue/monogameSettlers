using System.Net;
using System.Net.WebSockets;
using System.Text;
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

/// <summary>A real server against computers that do not play by the rules; whoever does must not notice.</summary>
public class ServerHardeningTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>A server of its own with the given limits (e.g. <c>Limits:MaxGames=1</c>).</summary>
    private sealed class TestServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TestServer(WebApplication app, Uri address)
        {
            _app = app;
            Address = address;
        }

        public Uri Address { get; }

        public static async Task<TestServer> Start(params string[] settings)
        {
            var app = ServerApp.Create(["--Logging:LogLevel:Default=None", .. settings.Select(s => "--" + s)], "http://127.0.0.1:0");
            await app.StartAsync();
            string http = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new TestServer(app, new Uri(http.Replace("http://", "ws://") + "/ws"));
        }

        public Task<ServerConnection> Connect(string name) => ServerConnection.ConnectAsync(Address, name, GameFingerprint.Of());

        public async Task<ClientWebSocket> Raw(Action<ClientWebSocketOptions>? options = null)
        {
            var socket = new ClientWebSocket();
            options?.Invoke(socket.Options);
            await socket.ConnectAsync(Address, CancellationToken.None);
            return socket;
        }

        public async Task<string> Status()
        {
            using var http = new HttpClient();
            return await http.GetStringAsync(new Uri(Address.ToString().Replace("ws://", "http://").Replace("/ws", "/")));
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    private static GameSetup Setup(int humans) => new()
    {
        Map = MapChoice.Generated(new RandomMapSettings(Players: Math.Max(2, humans)), 6),
        Seed = 12,
        Players = Enumerable.Range(0, humans).Select(_ => new PlayerSetup(Human: true)).ToArray(),
    };

    private static string Hello(string name = "x", JsonNode? fingerprint = null) =>
        ServerMessages.Of(ServerMessages.Hello, new JsonObject { ["name"] = name, ["fingerprint"] = fingerprint ?? JsonNode.Parse(GameFingerprint.Of().ToJson()) });

    private static Task Say(ClientWebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    /// <summary>The next message, null once the server has closed.</summary>
    private static async Task<JsonObject?> Next(ClientWebSocket socket)
    {
        var buffer = new byte[1024 * 1024];
        using var timeout = new CancellationTokenSource(Wait);
        int length = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(length), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            length += result.Count;
            if (result.EndOfMessage)
                return JsonNode.Parse(buffer.AsSpan(0, length))!.AsObject();
        }
    }

    /// <summary>Reads until the server closes; the kinds of what came.</summary>
    private static async Task<List<string?>> UntilClosed(ClientWebSocket socket)
    {
        var kinds = new List<string?>();
        try
        {
            while (await Next(socket) is { } message)
                kinds.Add(ServerMessages.TypeOf(message));
        }
        catch (WebSocketException)
        {
            // Dropped without a close: closed all the same.
        }
        return kinds;
    }

    private static async Task<(ServerConnection Host, ServerConnection[] Guests, string Id)> Lobby(TestServer server, int humans)
    {
        var host = await server.Connect("Host");
        host.Create("Test", Setup(humans));
        string id = (await host.WaitForAsync([ServerMessages.Lobby], Wait))["game"]!.GetValue<string>();
        var guests = new ServerConnection[humans - 1];
        for (int i = 0; i < guests.Length; i++)
        {
            guests[i] = await server.Connect($"Guest {i}");
            guests[i].Join(id);
            await guests[i].WaitForAsync([ServerMessages.Lobby], Wait);
        }
        return (host, guests, id);
    }

    [Fact]
    public async Task A_connection_that_never_says_hello_is_dropped()
    {
        await using var server = await TestServer.Start("Limits:HandshakeSeconds=0.2");
        using var silent = await server.Raw();

        Assert.Equal([ServerMessages.Refused], await UntilClosed(silent));
    }

    [Fact]
    public async Task A_connection_idling_outside_a_game_is_dropped()
    {
        await using var server = await TestServer.Start("Limits:IdleSeconds=0.2");
        using var idle = await server.Raw();
        await Say(idle, Hello());

        Assert.Equal([ServerMessages.Welcome, ServerMessages.Refused], await UntilClosed(idle));
    }

    [Fact]
    public async Task One_address_cannot_take_all_connections()
    {
        await using var server = await TestServer.Start("Limits:MaxConnectionsPerAddress=2");
        using var first = await server.Raw();
        using var second = await server.Raw();

        await Assert.ThrowsAsync<WebSocketException>(() => server.Raw());

        // A place gets free with the connection.
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        WebSocketException? refused = null;
        for (int i = 0; i < 100; i++)
            try
            {
                using var third = await server.Raw();
                refused = null;
                break;
            }
            catch (WebSocketException e)
            {
                refused = e;
                await Task.Delay(20);
            }
        Assert.Null(refused);
    }

    [Fact]
    public async Task An_address_written_into_a_header_counts_only_behind_a_proxy()
    {
        static Action<ClientWebSocketOptions> From(string address) => o => o.SetRequestHeader("X-Forwarded-For", address);

        await using (var direct = await TestServer.Start("Limits:MaxConnectionsPerAddress=1"))
        {
            using var first = await direct.Raw(From("10.0.0.1"));
            await Assert.ThrowsAsync<WebSocketException>(() => direct.Raw(From("10.0.0.2")));
        }
        await using var proxied = await TestServer.Start("Limits:MaxConnectionsPerAddress=1", "Limits:TrustForwardedHeaders=true");
        using var one = await proxied.Raw(From("10.0.0.1"));
        using var other = await proxied.Raw(From("10.0.0.2"));
        await Assert.ThrowsAsync<WebSocketException>(() => proxied.Raw(From("10.0.0.1")));
        // What a client wrote comes first, the proxy adds the address it saw last: that one counts.
        using var spoofed = await proxied.Raw(From("10.0.0.1, 10.0.0.3"));
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    public void Addresses_count_as_themselves(string address, string key) =>
        Assert.Equal(key, ServerLimits.AddressKey(IPAddress.Parse(address)));

    [Fact]
    public void One_ipv6_network_counts_as_one_address()
    {
        Assert.Equal(ServerLimits.AddressKey(IPAddress.Parse("2001:db8:1:2::1")), ServerLimits.AddressKey(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(ServerLimits.AddressKey(IPAddress.Parse("2001:db8:1:2::1")), ServerLimits.AddressKey(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public async Task A_web_page_cannot_use_a_browser_as_a_client()
    {
        await using var server = await TestServer.Start();
        await Assert.ThrowsAsync<WebSocketException>(() => server.Raw(o => o.SetRequestHeader("Origin", "https://example.com")));
    }

    [Fact]
    public async Task A_flood_of_messages_ends_the_connection_and_nobody_elses()
    {
        await using var server = await TestServer.Start("Limits:MessagesPerSecond=10", "Limits:MessageBurst=50");
        await using var honest = await server.Connect("Honest");
        using var flood = await server.Raw();
        await Say(flood, Hello());

        for (int i = 0; i < 400 && flood.State == WebSocketState.Open; i++)
            try
            {
                await Say(flood, ServerMessages.Of(ServerMessages.List));
            }
            catch (WebSocketException)
            {
                break;
            }
        await UntilClosed(flood);

        honest.ListGames();
        await honest.WaitForAsync([ServerMessages.Games], Wait);
    }

    [Fact]
    public async Task Empty_frames_count_too()
    {
        await using var server = await TestServer.Start("Limits:MessagesPerSecond=10", "Limits:MessageBurst=50");
        using var flood = await server.Raw();
        try
        {
            for (int i = 0; i < 500; i++)
                await flood.SendAsync(Array.Empty<byte>(), WebSocketMessageType.Text, endOfMessage: false, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Closed on the way: as it should.
        }
        await UntilClosed(flood);
        Assert.NotEqual(WebSocketState.Open, flood.State);
    }

    private static readonly string Current = "{\"Protocol\":" + GameFingerprint.CurrentProtocol + ",";

    public static readonly TheoryData<string> BadFingerprints = new()
    {
        Current + """ "Version":"1","Files":null}""",
        Current + """ "Version":null,"Files":{}}""",
        Current + """ "Version":"1\n2","Files":{}}""",
        Current + """ "Version":"1","Files":{"..\\evil":"x"}}""",
        """{"Protocol":"text"}""",
    };

    [Theory]
    [MemberData(nameof(BadFingerprints))]
    public async Task A_broken_fingerprint_gets_nowhere(string fingerprint)
    {
        await using var server = await TestServer.Start();
        using var socket = await server.Raw();
        await Say(socket, Hello(fingerprint: JsonNode.Parse(fingerprint)));

        Assert.Equal([ServerMessages.Refused], await UntilClosed(socket));
    }

    [Fact]
    public async Task A_second_hello_changes_nothing()
    {
        await using var server = await TestServer.Start();
        using var socket = await server.Raw();
        await Say(socket, Hello("First"));
        await Next(socket);
        await Say(socket, Hello("Second"));
        Assert.Equal(ServerMessages.Error, ServerMessages.TypeOf((await Next(socket))!));
    }

    [Theory]
    [InlineData("""{"type":"create","setup":{"Map":{"RandomMap":{"Players":100000}},"Players":[{"Human":true}]}}""")]
    [InlineData("""{"type":"create","setup":{"Map":{"MapFile":"../../x.map","Hash":"0000000000000000000000000000000000000000000000000000000000000000"},"Players":[{"Human":true}]}}""")]
    [InlineData("""{"type":"create","setup":{"Map":{"RandomMap":{"Players":2}},"Players":[{"Human":true,"Color":100}]}}""")]
    [InlineData("""{"type":"create","setup":{"Map":{"RandomMap":{"Players":2}},"Players":null}}""")]
    [InlineData("""{"type":"create","setup":{"Map":{"RandomMap":{"Players":2}},"Players":[null]}}""")]
    [InlineData("""{"type":"create","setup":{"Map":{"RandomMap":{"Players":2}},"Positions":[0,0],"Players":[{"Human":true}]}}""")]
    public async Task A_setup_that_could_harm_others_opens_no_game(string create)
    {
        await using var server = await TestServer.Start();
        using var socket = await server.Raw();
        await Say(socket, Hello());
        await Next(socket);
        await Say(socket, create);

        Assert.Equal(ServerMessages.Error, ServerMessages.TypeOf((await Next(socket))!));
        Assert.Contains("0 games", await server.Status());
    }

    [Fact]
    public async Task There_are_only_so_many_games()
    {
        await using var server = await TestServer.Start("Limits:MaxGames=1");
        await using var first = await server.Connect("First");
        await using var second = await server.Connect("Second");
        first.Create("One", Setup(2));
        await first.WaitForAsync([ServerMessages.Lobby], Wait);

        second.Create("Two", Setup(2));
        Assert.Contains("full", (await second.WaitForAsync([ServerMessages.Error], Wait))["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Names_come_out_clean_and_within_their_length()
    {
        await using var server = await TestServer.Start();
        string name = "A\nB‮" + new string('x', 30) + "😀😀";
        await using var host = await server.Connect(name);
        host.Create("Game\r\nLOG FORGED" + new string('y', 50), Setup(2));
        var lobby = await host.WaitForAsync([ServerMessages.Lobby], Wait);

        string game = lobby["name"]!.GetValue<string>();
        string seat = lobby["seats"]![0]!["name"]!.GetValue<string>();
        Assert.Equal(ServerMessages.MaxGameNameLength, game.Length);
        Assert.Equal("GameLOG FORGED", game[..14]);
        Assert.Equal("AB" + new string('x', 30), seat);
    }

    [Fact]
    public async Task A_name_of_emojis_is_cut_between_them()
    {
        await using var server = await TestServer.Start();
        await using var host = await server.Connect(new string('x', 31) + "😀");
        host.Create("Game", Setup(2));
        var lobby = await host.WaitForAsync([ServerMessages.Lobby], Wait);
        Assert.Equal(new string('x', 31), lobby["seats"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_message_with_a_bad_text_is_answered_and_nothing_else_happens()
    {
        await using var server = await TestServer.Start();
        using var socket = await server.Raw();
        await Say(socket, Hello());
        await Next(socket);
        await Say(socket, """{"type":"join","game":"\ud800"}""");
        await Say(socket, """{"type":"join","game":7}""");
        await Say(socket, """{"type":"create","name":"\ud800","setup":{}}""");
        await Say(socket, """{"type":"type","type":"list"}""");
        await Say(socket, ServerMessages.Of(ServerMessages.List));

        for (var message = await Next(socket); ServerMessages.TypeOf(message!) != ServerMessages.Games; message = await Next(socket))
            Assert.Contains(ServerMessages.TypeOf(message!), new[] { ServerMessages.Error, ServerMessages.Refused });
    }

    private static string Input(int player, long turn, string commands = "[]") =>
        "{\"type\":\"input\",\"input\":{\"player\":" + player + ",\"turn\":" + turn + ",\"commands\":" + commands + "}}";

    /// <summary>The game running with a raw guest, so it can send what no client does; the host's <see cref="ServerConnection"/> as the honest one.</summary>
    private static async Task<(ServerConnection Host, ClientWebSocket Guest)> Running(TestServer server)
    {
        var host = await server.Connect("Host");
        host.Create("Test", Setup(2));
        string id = (await host.WaitForAsync([ServerMessages.Lobby], Wait))["game"]!.GetValue<string>();
        var guest = await server.Raw();
        await Say(guest, Hello("Guest"));
        await Next(guest);
        await Say(guest, ServerMessages.Of(ServerMessages.Join, new JsonObject { ["game"] = id }));
        await Next(guest);
        await host.WaitForAsync([ServerMessages.Lobby], Wait);
        host.Start();
        await host.WaitForAsync([ServerMessages.Started], Wait);
        await Next(guest);
        return (host, guest);
    }

    public static readonly TheoryData<string> RuleBreaks = new()
    {
        Input(0, 2),                                                              // someone else's seat
        Input(1, 100000),                                                         // far ahead
        Input(1, 2, """[{"$type":"Say","player":0,"text":"forged"}]"""),          // another player's command
        Input(1, 2, """[{"$type":"Nope","player":1}]"""),                         // no command
        """{"type":"input","input":{"player":1,"turn":2,"commands":[{"$type":"Say","player":1,"text":"\ud800"}]}}""", // cannot be handed on
        """{"type":"input","input":"text"}""".Replace("\"text\"", """{"player":1}"""),
    };

    [Theory]
    [MemberData(nameof(RuleBreaks))]
    public async Task An_input_against_the_rules_costs_the_seat_and_the_game_goes_on(string input)
    {
        await using var server = await TestServer.Start();
        var (host, guest) = await Running(server);
        await using var _ = host;
        using var __ = guest;

        await Say(guest, input);

        Assert.Contains(ServerMessages.Refused, await UntilClosed(guest));
        Assert.Equal(1, (await host.WaitForAsync([ServerMessages.Left], Wait))["player"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_player_leaving_stalls_nobody()
    {
        await using var server = await TestServer.Start();
        var (host, guest) = await Running(server);
        await using var _ = host;
        using var __ = guest;
        await host.Link.AsRaw(Input(0, 2));
        await Task.Delay(50);
        Assert.Empty(host.Link.Receive()); // the guest's input is missing

        guest.Abort();

        await host.WaitForAsync([ServerMessages.Left], Wait);
        for (int i = 0; i < 200 && host.Link.Receive() is { Count: 0 }; i++)
            await Task.Delay(10);
        await host.Link.AsRaw(Input(0, 3));
        IReadOnlyList<string> bundles = [];
        for (int i = 0; i < 200 && (bundles = host.Link.Receive()).Count == 0; i++)
            await Task.Delay(10);
        Assert.Equal(3, Assert.Single(bundles) is { } bundle ? JsonNode.Parse(bundle)!["turn"]!.GetValue<long>() : 0);
    }

    [Fact]
    public async Task A_player_stalling_the_others_is_dropped_after_a_while()
    {
        await using var server = await TestServer.Start("Limits:StallSeconds=0.2");
        var (host, guest) = await Running(server);
        await using var _ = host;
        using var __ = guest;
        await host.Link.AsRaw(Input(0, 2));

        Assert.Contains(ServerMessages.Refused, await UntilClosed(guest));
        await host.WaitForAsync([ServerMessages.Left], Wait);
    }

    [Fact]
    public async Task A_game_nobody_sends_to_is_closed()
    {
        await using var server = await TestServer.Start("Limits:AbandonedSeconds=0.2", "Limits:StallSeconds=0.1");
        var (host, guest) = await Running(server);
        await using var _ = host;
        using var __ = guest;

        await UntilClosed(guest);
        for (int i = 0; i < 300 && !(await server.Status()).Contains(": 0 games"); i++)
            await Task.Delay(10);
        Assert.Contains(": 0 games", await server.Status());
    }

    [Fact]
    public async Task Whoever_lies_about_its_world_is_put_out_by_the_majority()
    {
        await using var server = await TestServer.Start();
        var (host, guests, _) = await Lobby(server, 3);
        await using var a = host;
        await using var b = guests[0];
        await using var liar = guests[1];
        host.Start();
        foreach (var computer in new[] { host, guests[0], guests[1] })
            await computer.WaitForAsync([ServerMessages.Started], Wait);

        string Checked(int player, int piles) => new StateCheck(0, new WorldChecksums(0, 1, 2, (ulong)piles, 4, 5)) is var check
            ? new CommandCodec().Encode(new TurnInput(player, 2, [], check)) : "";
        await host.Link.AsRaw(Checked(0, 7), wrapped: true);
        await guests[0].Link.AsRaw(Checked(1, 7), wrapped: true);
        await guests[1].Link.AsRaw(Checked(2, 666), wrapped: true);

        var left = await host.WaitForAsync([ServerMessages.Left], Wait);
        Assert.Equal(2, left["player"]!.GetValue<int>());
    }

}

internal static class RawLink
{
    /// <summary>Sends a text as it is over the turns' way (<paramref name="wrapped"/>: an input to put into the message first).</summary>
    public static Task AsRaw(this ILockstepLink link, string message, bool wrapped = false)
    {
        var node = JsonNode.Parse(message)!.AsObject();
        link.Send((wrapped ? node : node["input"]!).ToJsonString());
        return Task.CompletedTask;
    }
}
