using Homestead.Simulation.Buildings;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Lockstep;
using Homestead.Simulation.Magic;
using Homestead.Simulation.Map.Generation;

namespace Homestead.Simulation.Tests;

public class LockstepTests
{
    private static readonly BuildingCatalog Romans = BuildingCatalog.Load(Civilization.Roman);

    /// <summary>A value of the type for a command's constructor (the first property, the player, gets one too).</summary>
    private static object? Sample(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type switch
        {
            _ when type == typeof(int) => 3,
            _ when type == typeof(long) => 40L,
            _ when type == typeof(double) => 0.1 + 0.2, // not exactly 0.3: doubles must come back to the last bit
            _ when type == typeof(bool) => true,
            _ when type == typeof(string) => "Grüße – \"Ave\"",
            _ when type == typeof(int[]) => new[] { 1, 5, 9 },
            _ when type == typeof(GridPoint) => new GridPoint(17, 23),
            _ when type == typeof(BuildingDefinition) => Romans["tower"],
            _ when type == typeof(Spell) => new Spell(SpellKind.Freeze, 30, 6),
            _ when type.IsEnum => Enum.GetValues(type).GetValue(Enum.GetValues(type).Length - 1),
            _ => throw new NotSupportedException($"no sample for {type}: add one (and check the codec can send it)"),
        };
    }

    [Fact]
    public void Every_command_goes_over_the_network_and_comes_back_the_same()
    {
        var codec = new CommandCodec([Romans]);
        Assert.True(CommandCodec.CommandTypes.Count > 30);
        foreach (var type in CommandCodec.CommandTypes)
        {
            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            var command = (GameCommand)constructor.Invoke(constructor.GetParameters().Select(p => Sample(p.ParameterType)).ToArray());
            new World(new Map.GameMap(8)).Commands.Issue(command);
            string json = codec.Encode(command);
            var back = codec.Decode(json);
            Assert.IsType(type, back);
            Assert.Equal(command.Sequence, back.Sequence);
            Assert.Equal(json, codec.Encode(back));
            if (back is PlaceSite site)
                Assert.Same(Romans["tower"], site.Definition); // the game's own definition
        }
    }

    [Fact]
    public void The_server_hands_out_a_turn_once_every_player_has_sent_it()
    {
        var codec = new CommandCodec([Romans]);
        var relay = new TurnRelay([2, 0], firstTurn: 5);
        string Input(int player, long turn, params GameCommand[] commands) => codec.Encode(new TurnInput(player, turn, commands));

        Assert.Empty(relay.Receive(2, Input(2, 6, new Say(2, "six"))));
        Assert.Empty(relay.Receive(0, Input(0, 6)));
        // A command claiming another player is dropped, and so is an input sent for someone else.
        Assert.Empty(relay.Receive(2, Input(2, 5, new Say(0, "forged"), new Say(2, "five"))));
        Assert.Empty(relay.Receive(2, Input(0, 5, new Say(0, "forged"))));
        Assert.Empty(relay.Receive(2, Input(2, 5, new Say(2, "again")))); // the first input of a turn counts
        Assert.Empty(relay.Receive(1, Input(1, 5))); // not a player of the game
        Assert.Empty(relay.Receive(0, "{ not json"));
        var bundles = relay.Receive(0, Input(0, 5, new Say(0, "a"), new Say(0, "b"))).Select(codec.DecodeBundle).ToList();

        Assert.Equal([5L, 6L], bundles.Select(b => b.Turn));
        Assert.Equal(["a", "b", "five"], bundles[0].Commands.Cast<Say>().Select(s => s.Text)); // by player, each in order
        Assert.Equal(["six"], bundles[1].Commands.Cast<Say>().Select(s => s.Text));
        Assert.Equal(1, relay.Dropped);
        Assert.Empty(relay.Receive(2, Input(2, 5))); // handed out already
        Assert.Equal(7, relay.NextTurn);
    }

    /// <summary>
    /// A network with lag: every message takes 0–3 rounds, so messages also overtake each other (bundles may come out of
    /// order). Each round the server takes what reached it.
    /// </summary>
    private sealed class LaggyNetwork(TurnRelay relay, int seed)
    {
        private readonly Random _random = new(seed);
        private readonly List<(int Due, int From, string Message)> _toServer = new();
        private readonly List<(int Due, int To, string Message)> _toPlayers = new();
        private readonly List<int> _players = new();
        private int _round;

        public ILockstepLink LinkOf(int player)
        {
            _players.Add(player);
            return new Link(this, player);
        }

        public void NextRound()
        {
            _round++;
            foreach (var (_, from, message) in _toServer.Where(m => m.Due <= _round).ToList())
                foreach (string bundle in relay.Receive(from, message))
                    foreach (int player in _players)
                        _toPlayers.Add((_round + _random.Next(4), player, bundle));
            _toServer.RemoveAll(m => m.Due <= _round);
        }

        private sealed class Link(LaggyNetwork network, int player) : ILockstepLink
        {
            public void Send(string message) => network._toServer.Add((network._round + network._random.Next(4), player, message));

            public IReadOnlyList<string> Receive()
            {
                var arrived = network._toPlayers.Where(m => m.To == player && m.Due <= network._round).Select(m => m.Message).ToList();
                network._toPlayers.RemoveAll(m => m.To == player && m.Due <= network._round);
                return arrived;
            }
        }
    }

    private sealed class Computer(World world, LockstepSession session)
    {
        public World World { get; } = world;
        public LockstepSession Session { get; } = session;

        /// <summary>The checksum every second, at a turn's beginning (before its commands).</summary>
        public Dictionary<long, ulong> Checksums { get; } = new();
    }

    [Fact]
    public void Two_computers_play_the_same_game_over_a_laggy_network()
    {
        var setup = new GameSetup
        {
            Map = MapChoice.Generated(new RandomMapSettings(Players: 3), 4),
            Seed = 9,
            Players = [new PlayerSetup(Human: true), new PlayerSetup(Human: true), new PlayerSetup()],
        };
        var options = new LockstepOptions(TurnTicks: 2, DelayTurns: 2);
        var relay = new TurnRelay([0, 1], firstTurn: options.DelayTurns);
        var network = new LaggyNetwork(relay, seed: 3);
        var computers = Enumerable.Range(0, 2).Select(player =>
        {
            var start = setup.Start(setup.Map.Open(""));
            var codec = new CommandCodec(start.Report.Catalogs);
            return new Computer(start.World, new LockstepSession(start.World, player, codec, network.LinkOf(player), options));
        }).ToArray();
        Assert.Throws<ArgumentException>(() => computers[0].Session.Issue(new Say(1, "for someone else")));

        const int Ticks = 90 * World.TicksPerSecond;
        var placed = new Dictionary<int, BuildingDefinition>();
        for (int round = 0; computers.Any(c => c.World.Tick < Ticks); round++)
        {
            Assert.True(round < 20 * Ticks, "the game got stuck");
            for (int player = 0; player < 2; player++)
            {
                var (world, session) = (computers[player].World, computers[player].Session);
                // The computers run at different speeds: the first up to 3 ticks a round, the second one every other round.
                int ticks = player == 0 ? round % 4 : round % 2;
                for (int i = 0; i < ticks && world.Tick < Ticks; i++)
                {
                    if (world.Tick % World.TicksPerSecond == 0)
                        computers[player].Checksums.TryAdd(world.Tick, WorldChecksum.Of(world));
                    if (!session.TryStep())
                        break;
                    // Orders given between ticks, as the player clicks.
                    if (world.Tick == 37 + 100 * player)
                        session.Issue(new Say(player, $"hello from {player + 1}"));
                    if (world.Tick == 300 + 7 * player && SpotFor(world, player) is var (definition, at))
                    {
                        placed[player] = definition;
                        session.Issue(new PlaceSite(player, definition, at));
                    }
                }
            }
            network.NextRound();
        }

        // The same world every second, the same chat at the same ticks: issued at tick 37 (turn 18), sent when turn 19
        // begins, for turn 19 + 2 = 21, so applied at tick 42 – on both computers.
        Assert.Equal(computers[0].Checksums, computers[1].Checksums);
        Assert.Equal(Ticks / World.TicksPerSecond, computers[0].Checksums.Count);
        // The computers compared their checksums every second themselves, and found nothing.
        foreach (var computer in computers)
        {
            Assert.Null(computer.Session.Desync);
            Assert.InRange(computer.Session.ChecksCompared, Ticks / World.TicksPerSecond - 3, Ticks / World.TicksPerSecond);
        }
        Assert.Empty(relay.DesyncedTurns);
        var chats = computers.Select(c => c.World.Chat.Messages.Select(m => (m.Tick, m.Player, m.Text)).ToList()).ToArray();
        Assert.Equal(chats[0], chats[1]);
        Assert.Equal([(42L, 0, "hello from 1"), (142L, 1, "hello from 2")], chats[0]);
        Assert.Equal(2, placed.Count);
        foreach (var computer in computers)
            foreach (var (player, definition) in placed)
                Assert.Contains(computer.World.Buildings, b => b.Player == player && b.Definition.Name == definition.Name);
    }

    /// <summary>A free spot for a lumberjack near the player's first building (the same on every computer: the worlds are equal).</summary>
    private static (BuildingDefinition, GridPoint)? SpotFor(World world, int player)
    {
        var home = world.Buildings.First(b => b.Player == player).Position;
        var definition = BuildingCatalog.Load(world.PeopleOf(player))["lumberjack"];
        for (int r = 4; r < 20; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    var at = new GridPoint(home.X + dx, home.Y + dy);
                    if (world.Map!.Contains(at) && world.CanPlace(definition, at, out _, player))
                        return (definition, at);
                }
        return null;
    }

    [Fact]
    public void A_world_going_its_own_way_is_noticed_by_everybody_and_the_game_stops()
    {
        var setup = new GameSetup
        {
            Map = MapChoice.Generated(new RandomMapSettings(Players: 2), 2),
            Players = [new PlayerSetup(Human: true), new PlayerSetup(Human: true)],
        };
        var options = new LockstepOptions(TurnTicks: 2, DelayTurns: 2, CheckTurns: 10);
        var relay = new LocalRelay([0, 1], firstTurn: options.DelayTurns);
        var computers = Enumerable.Range(0, 2).Select(player =>
        {
            var start = setup.Start(setup.Map.Open(""));
            return new Computer(start.World, new LockstepSession(start.World, player, new CommandCodec(start.Report.Catalogs), relay.LinkOf(player), options));
        }).ToArray();

        for (int round = 0; round < 1000 && computers.Any(c => c.Session.Desync is null); round++)
            foreach (var computer in computers)
            {
                computer.Session.TryStep();
                // Something on the second computer only, between ticks 99 and 100 (in turn 49, checked when turn 50 begins).
                if (computer == computers[1] && computer.World.Tick == 100)
                {
                    var door = computer.World.Buildings.First().Door;
                    Assert.NotNull(Enumerable.Range(2, 10).Select(d => computer.World.AddLooseGoods(new GridPoint(door.X + d, door.Y + d), Material.Plank, 3))
                        .FirstOrDefault(p => p is not null));
                }
            }

        for (int player = 0; player < 2; player++)
        {
            var desync = computers[player].Session.Desync;
            Assert.NotNull(desync);
            Assert.Equal((50L, 100L), (desync.Turn, desync.Tick));
            var other = Assert.Single(desync.Players);
            Assert.Equal(1 - player, other.Player);
            Assert.Equal(["piles"], other.Parts);
            // The game stands still once noticed (two turns' delay later), for good.
            long tick = computers[player].World.Tick;
            Assert.InRange(tick, 100, 100 + (options.DelayTurns + 1) * options.TurnTicks);
            Assert.False(computers[player].Session.TryStep());
            Assert.Equal(tick, computers[player].World.Tick);
        }
        Assert.Equal([50L], relay.Relay.DesyncedTurns);
    }

    [Fact]
    public void A_computer_behind_the_others_knows_how_far()
    {
        var options = new LockstepOptions(TurnTicks: 2, DelayTurns: 4);
        var relay = new LocalRelay([0, 1], firstTurn: options.DelayTurns);
        var fast = new World(new Map.GameMap(16));
        var slow = new World(new Map.GameMap(16));
        var ahead = new LockstepSession(fast, 0, new CommandCodec(), relay.LinkOf(0), options);
        var behind = new LockstepSession(slow, 1, new CommandCodec(), relay.LinkOf(1), options);
        // The first computer runs until it needs the second one's input (turn 4), having sent its inputs up to turn 8.
        while (ahead.TryStep()) { }
        Assert.Equal(4, ahead.Turn);
        // The second one learns of it with its first bundle: three turns behind once at turn 1.
        behind.TryStep();
        behind.TryStep();
        Assert.Equal(1, behind.Turn);
        Assert.Equal(3, behind.Behind);
        ahead.TryStep();
        Assert.Equal(0, ahead.Behind);
        // Caught up, it is behind no more.
        while (behind.Turn < ahead.Turn)
            behind.TryStep();
        Assert.Equal(0, behind.Behind);
    }

    [Fact]
    public void Without_the_turn_from_the_server_the_game_waits()
    {
        var world = new World(new Map.GameMap(16));
        var silent = new SilentLink();
        var session = new LockstepSession(world, 0, new CommandCodec(), silent, new LockstepOptions(TurnTicks: 2, DelayTurns: 3));
        int stepped = 0;
        while (stepped < 100 && session.TryStep())
            stepped++;
        // Turns 0–2 need no inputs (none could have come yet); turn 3 waits for the server.
        Assert.Equal(6, world.Tick);
        Assert.True(session.Waiting);
        Assert.False(session.TryStep());
        Assert.Equal(6, world.Tick);
        // Every turn begun sent its input, even without commands (for turns 3, 4, 5 and 6).
        Assert.Equal([3L, 4L, 5L, 6L], silent.Sent.Select(m => new CommandCodec().DecodeInput(m).Turn));
    }

    private sealed class SilentLink : ILockstepLink
    {
        public List<string> Sent { get; } = new();

        public void Send(string message) => Sent.Add(message);

        public IReadOnlyList<string> Receive() => [];
    }
}
