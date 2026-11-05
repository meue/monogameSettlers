using System.Reflection;
using System.Text.Json.Nodes;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Commands;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Lockstep;
using Homestead.Simulation.Magic;
using Homestead.Simulation.Map.Generation;

namespace Homestead.Simulation.Tests;

/// <summary>What other computers send cannot be trusted: no value of theirs may break this one, or all of them apart.</summary>
public class HardeningTests
{
    private static GameSetup Humans(int count, int seed = 2) => new()
    {
        Map = MapChoice.Generated(new RandomMapSettings(Players: Math.Max(2, count)), seed),
        Players = Enumerable.Range(0, count).Select(_ => new PlayerSetup(Human: true)).ToArray(),
    };

    private static World WorldOf(GameSetup setup) => setup.Start(setup.Map.Open("")).World;

    // ----- Texts

    [Theory]
    [InlineData("Anna", "Anna")]
    [InlineData("  Anna \n", "Anna")]
    [InlineData("An\r\nna\tB\0", "AnnaB")]
    [InlineData("a\u202Eb\u200Bc", "abc")] // bidirectional override, zero width space
    [InlineData("Grüße 😀", "Grüße 😀")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Texts_lose_what_garbles_a_list_or_breaks_the_font(string? text, string clean) =>
        Assert.Equal(clean, PlayerText.Clean(text));

    [Fact]
    public void Unpaired_surrogates_go() // the font throws on them
    {
        Assert.Equal("ab", PlayerText.Clean("a\ud800b\udc00"));
        Assert.Equal("a", PlayerText.Clean("a\ud800"));
    }

    [Fact]
    public void Cutting_a_text_never_splits_a_surrogate_pair()
    {
        Assert.Equal("ab", PlayerText.Clean("ab😀", 3));
        Assert.Equal("ab😀", PlayerText.Clean("ab😀", 4));
        Assert.Equal("a\ufffd", PlayerText.Drawable("a\ud800"));
        Assert.Equal("a😀", PlayerText.Drawable("a😀"));
    }

    [Fact]
    public void Chat_is_cleaned_and_in_a_multiplayer_game_a_flood_stops()
    {
        var world = WorldOf(Humans(2));
        world.Commands.Enqueue(Enumerable.Range(0, 30).Select(i => new Say(1, $"line\n{i}")));
        world.Commands.Enqueue([new Say(0, "still heard")]);
        world.Step();

        Assert.Equal(ChatLog.FloodLines + 1, world.Chat.Messages.Count);
        Assert.Equal("line0", world.Chat.Messages[0].Text);
        Assert.Equal("still heard", world.Chat.Messages[^1].Text);
    }

    // ----- Commands with any values

    private static readonly int[] Numbers = [0, 1, 2, 3, -1, 7, 1000, int.MaxValue, int.MinValue];

    /// <summary>A value of the type: often a sensible one, as often anything JSON can carry.</summary>
    private static object? Any(Type declared, Random random, World world, IReadOnlyList<BuildingDefinition> definitions)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        bool sensible = random.Next(2) == 0;
        int Id()
        {
            if (!sensible)
                return Numbers[random.Next(Numbers.Length)];
            return random.Next(2) == 0 && world.Buildings.Count > 0
                ? world.Buildings[random.Next(world.Buildings.Count)].Id
                : world.Settlers.Count > 0 ? world.Settlers[random.Next(world.Settlers.Count)].Id : 0;
        }
        if (type == typeof(int))
            return Id();
        if (type == typeof(long))
            return random.Next(2) == 0 ? 100L : long.MinValue;
        if (type == typeof(double))
            return new[] { 0, 0.5, 1, -1, 2, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue }[random.Next(9)];
        if (type == typeof(bool))
            return random.Next(2) == 0;
        if (type == typeof(string))
            return new[] { null, "", "sawmill", "construction", "\n\ud800", new string('x', 5000) }[random.Next(6)];
        if (type == typeof(int[]))
            return random.Next(6) switch
            {
                0 => null,
                1 => [],
                2 => new int[5000],
                _ => Enumerable.Range(0, 20).Select(_ => Id()).ToArray(),
            };
        if (type == typeof(GridPoint))
        {
            int size = world.Map!.Size;
            return sensible ? new GridPoint(random.Next(size), random.Next(size)) : new GridPoint(Numbers[random.Next(Numbers.Length)], Numbers[random.Next(Numbers.Length)]);
        }
        if (type == typeof(BuildingDefinition))
            return random.Next(4) == 0 ? null : definitions[random.Next(definitions.Count)];
        if (type == typeof(Spell))
            return random.Next(3) == 0 ? null : new Spell(sensible ? Enum.GetValues<SpellKind>()[random.Next(Enum.GetValues<SpellKind>().Length)] : (SpellKind)999, 0, 0);
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return sensible ? values.GetValue(random.Next(values.Length)) : Enum.ToObject(type, 9999);
        }
        throw new NotSupportedException($"no values for {type}: add them, a command with such a field must be guarded too");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void No_command_with_any_values_throws_or_breaks_the_world(int seed)
    {
        var world = WorldOf(Humans(2, seed));
        // Alone, so that nothing catches an exception here: every one of them is a hole in the guard or the command.
        world.Multiplayer = false;
        var definitions = Enum.GetValues<Civilization>().SelectMany(c => BuildingCatalog.Load(c).All).ToArray();
        var random = new Random(seed);
        var constructors = CommandCodec.CommandTypes.Select(t => t.GetConstructors().Single(c => c.GetParameters().Length > 0)).ToArray();

        for (int round = 0; round < 150; round++)
        {
            foreach (var constructor in constructors)
            {
                var values = constructor.GetParameters().Select(p => Any(p.ParameterType, random, world, definitions)).ToArray();
                if (random.Next(3) > 0)
                    values[0] = random.Next(2); // mostly a real player, for the commands to get past the first check
                var command = (GameCommand)constructor.Invoke(values);
                if (command is not Cheat { Kind: CheatKind.MissionLost or CheatKind.MissionWon })
                    world.Commands.Issue(command);
            }
            for (int i = 0; i < 5; i++)
                world.Step();
        }
        Assert.True(world.Tick > 0);
    }

    [Fact]
    public void Only_the_professions_of_the_menu_can_be_made_from_carriers()
    {
        var world = WorldOf(Humans(2));
        int soldiers = world.Settlers.Count(s => s.Profession == Profession.Soldier);
        world.Commands.Enqueue([new ConvertCarriers(0, 5, Profession.Soldier, null), new ConvertCarriers(0, 5, Profession.Priest, null)]);
        world.Step();

        Assert.All(world.Commands.Applied, a => Assert.Same(CommandQueue.Refused, a.Result));
        Assert.Equal(soldiers, world.Settlers.Count(s => s.Profession == Profession.Soldier));
        world.Commands.Enqueue([new ConvertCarriers(0, 1, Profession.Scout, null)]);
        world.Step();
        Assert.Equal(1, Assert.Single(world.Commands.Applied).Result is ValueTuple<int, int, int[]> r ? r.Item1 + r.Item2 : 0);
    }

    [Fact]
    public void Market_orders_only_reach_markets()
    {
        var world = WorldOf(Humans(2));
        var other = world.Buildings.First(b => b.Player == 0 && !world.Trade.IsTradingPost(b));
        world.Commands.Enqueue([new SetTradeAmount(0, other.Id, Material.Plank, 5)]);
        world.Step();
        Assert.Equal(0, world.Trade.Remaining(other, Material.Plank));
    }

    /// <summary>Hands on what a computer sends, with something added to its input the honest program would never write.</summary>
    private sealed class Tampered(ILockstepLink link, Func<long, IReadOnlyList<string>> extra) : ILockstepLink
    {
        public void Send(string message)
        {
            var input = JsonNode.Parse(message)!.AsObject();
            foreach (string command in extra(input["turn"]!.GetValue<long>()))
                input["commands"]!.AsArray().Add(JsonNode.Parse(command));
            link.Send(input.ToJsonString());
        }

        public IReadOnlyList<string> Receive() => link.Receive();
    }

    public static readonly TheoryData<string> BrokenCommands = new()
    {
        """{"$type":"Say","player":1}""",
        """{"$type":"Say","player":1,"text":null}""",
        """{"$type":"Say","player":1,"text":42}""",
        """{"$type":"SetStored","player":1,"material":9999,"stored":true}""",
        """{"$type":"SetStored","player":1,"material":"PLANK,STONE","stored":true}""",
        """{"$type":"SetStored","player":1,"material":"NOTHING","stored":true}""",
        """{"$type":"MoveUnits","player":1,"units":null,"target":{"x":1,"y":2},"forced":false}""",
        """{"$type":"MoveUnits","player":1,"units":[1],"target":{"x":-5,"y":100000},"forced":false}""",
        """{"$type":"MoveUnits","player":1,"units":[1],"forced":false}""",
        """{"$type":"PlaceSite","player":1,"definition":{"people":"Roman","name":"no such building"},"at":{"x":1,"y":2}}""",
        """{"$type":"PlaceSite","player":1,"definition":{"people":77,"name":"tower"},"at":{"x":1,"y":2}}""",
        """{"$type":"PlaceSite","player":1,"definition":null,"at":{"x":1,"y":2}}""",
        """{"$type":"CastSpell","player":1,"priests":[1],"target":{"x":1,"y":2},"spell":12345}""",
        """{"$type":"SetTradeRoute","player":1,"market":1,"stop":99,"point":null}""",
        """{"$type":"SetDistribution","player":1,"material":"PLANK","target":"x","share":1e999}""",
        """{"$type":"GameCommand","player":1}""",
        """{"$type":"ChangeOrdered","player":1,"product":"PLANK","delta":"lots"}""",
    };

    [Theory]
    [MemberData(nameof(BrokenCommands))]
    public void A_broken_command_is_left_out_on_every_computer_and_the_game_goes_on(string command)
    {
        var setup = Humans(2);
        var options = new LockstepOptions();
        var relay = new LocalRelay([0, 1], firstTurn: options.DelayTurns);
        var worlds = new List<World>();
        var sessions = Enumerable.Range(0, 2).Select(player =>
        {
            var start = setup.Start(setup.Map.Open(""));
            worlds.Add(start.World);
            var link = player == 1 ? new Tampered(relay.LinkOf(1), turn => turn == 5 ? [command] : []) : relay.LinkOf(0);
            return new LockstepSession(start.World, player, new CommandCodec(start.Report.Catalogs), link, options);
        }).ToArray();

        for (int round = 0; round < 80; round++)
            foreach (var session in sessions)
                session.TryStep();

        Assert.All(worlds, w => Assert.Equal(80, w.Tick));
        Assert.All(sessions, s => Assert.Null(s.Desync));
        Assert.All(sessions, s => Assert.Null(s.Failure));
        Assert.Equal(WorldChecksum.Of(worlds[0]), WorldChecksum.Of(worlds[1]));
    }

    [Fact]
    public void A_bundle_of_nonsense_stops_this_game_and_nothing_else()
    {
        foreach (string message in new[] { "not json", "[]", """{"turn":"x"}""", """{"turn":99999999}""" })
        {
            var link = new Scripted(message);
            var setup = Humans(1);
            var start = setup.Start(setup.Map.Open(""));
            var session = new LockstepSession(start.World, 0, new CommandCodec(start.Report.Catalogs), link);

            Assert.False(session.TryStep());
            Assert.NotNull(session.Failure);
        }
    }

    private sealed class Scripted(params string[] messages) : ILockstepLink
    {
        private string[] _messages = messages;

        public void Send(string message) { }

        public IReadOnlyList<string> Receive()
        {
            var messages = _messages;
            _messages = [];
            return messages;
        }
    }

    [Fact]
    public void Too_many_commands_for_one_input_go_out_over_several()
    {
        var codec = new CommandCodec();
        var setup = Humans(1);
        var world = WorldOf(setup);
        var relay = new LocalRelay([0], firstTurn: 2);
        var session = new LockstepSession(world, 0, codec, relay.LinkOf(0));
        int count = TurnRelay.MaxCommands * 2 + 5;
        for (int i = 0; i < count; i++)
            session.Issue(new Say(0, $"line {i}"));
        session.Issue(new MoveUnits(0, new int[1_000_000], default, false)); // beyond any input: cannot go

        for (int tick = 0; tick < 40; tick++)
            session.TryStep();

        Assert.Equal(0, relay.Relay.Refused);
        Assert.Equal(count, world.Chat.Messages.Count);
        Assert.Equal("line 0", world.Chat.Messages[0].Text);
        Assert.Equal($"line {count - 1}", world.Chat.Messages[^1].Text);
    }

    // ----- The relay

    private static string Input(CommandCodec codec, int player, long turn, params GameCommand[] commands) =>
        codec.Encode(new TurnInput(player, turn, commands));

    private static StateCheck Check(long turn, ulong piles) => new(turn, new WorldChecksums(turn * 2, 1, 2, piles, 4, 5));

    [Fact]
    public void A_player_gone_is_no_longer_waited_for()
    {
        var codec = new CommandCodec();
        var relay = new TurnRelay([0, 1, 2], firstTurn: 2);
        Assert.Empty(relay.Receive(0, Input(codec, 0, 2, new Say(0, "a"))));
        Assert.Empty(relay.Receive(2, Input(codec, 2, 2, new Say(2, "c"))));
        Assert.Equal([1], relay.Missing);

        var bundle = codec.DecodeBundle(Assert.Single(relay.Remove(1)));

        Assert.Equal(2, bundle.Turn);
        Assert.Equal(["a", "c"], bundle.Commands.Cast<Say>().Select(s => s.Text));
        Assert.Empty(relay.Remove(1)); // only once
        Assert.Empty(relay.Receive(1, Input(codec, 1, 3)));
        Assert.Empty(relay.Receive(0, Input(codec, 0, 3)));
        Assert.Single(relay.Receive(2, Input(codec, 2, 3)));
    }

    [Fact]
    public void What_a_gone_player_sent_ahead_does_not_reach_the_others()
    {
        var codec = new CommandCodec();
        var relay = new TurnRelay([0, 1], firstTurn: 2);
        relay.Receive(1, Input(codec, 1, 2, new Say(1, "too late")));
        relay.Remove(1);

        var bundle = codec.DecodeBundle(Assert.Single(relay.Receive(0, Input(codec, 0, 2))));
        Assert.Empty(bundle.Commands);
    }

    [Fact]
    public void Inputs_against_the_rules_are_refused()
    {
        var codec = new CommandCodec();
        var relay = new TurnRelay([0, 1], firstTurn: 2);
        void Refused(string input)
        {
            int before = relay.Refused;
            Assert.Empty(relay.Receive(0, input));
            Assert.Equal(before + 1, relay.Refused);
        }

        Refused("{ not json");
        Refused("""{"player":0,"turn":2,"player":1,"commands":[]}""");
        Refused("""{"player":0,"turn":"two","commands":[]}""");
        Refused("""{"player":0,"turn":2}""");
        Refused(Input(codec, 0, 1));                         // handed out already
        Refused(Input(codec, 0, 1000));                      // too far ahead
        Refused(Input(codec, 1, 2));                         // someone else's
        Refused("""{"player":0,"turn":2,"commands":[],"check":7}""");
        Refused("""{"player":0,"turn":2,"commands":[],"check":{"turn":0,"checksums":{"tick":"x"}}}""");
        Refused(codec.Encode(new TurnInput(0, 3, [], Check(0, 1)))); // the check of another turn than it was sent in
        Refused(Input(codec, 0, 2, Enumerable.Repeat<GameCommand>(new Say(0, "x"), TurnRelay.MaxCommands + 1).ToArray()));
        Refused(Input(codec, 0, 2, new Say(0, new string('x', TurnRelay.MaxInputBytes))));
        Assert.Empty(relay.Receive(0, Input(codec, 0, 2)));  // nothing of that counted
        Refused(Input(codec, 0, 2));                         // the second of a turn
    }

    [Fact]
    public void Text_that_cannot_be_written_again_is_refused()
    {
        var relay = new TurnRelay([0], firstTurn: 2);
        Assert.Empty(relay.Receive(0, """{"player":0,"turn":2,"commands":[{"$type":"Say","player":0,"text":"\ud800"}]}"""));
        Assert.Equal(1, relay.Refused);
    }

    [Fact]
    public void Commands_of_no_known_type_do_not_get_through()
    {
        var relay = new TurnRelay([0], firstTurn: 2);
        var bundle = Assert.Single(relay.Receive(0, """{"player":0,"turn":2,"commands":[{"$type":"Nope","player":0},{"player":0},7,null]}"""));

        Assert.Equal(4, relay.Dropped);
        Assert.Empty(new CommandCodec().DecodeBundle(bundle).Commands);
    }

    [Fact]
    public void A_flood_of_inputs_ahead_ends_at_the_limit_of_held_bytes()
    {
        var codec = new CommandCodec();
        var relay = new TurnRelay([0, 1], firstTurn: 2, maxAhead: 1000);
        string line = new('x', 1500);
        for (long turn = 2; turn < 800; turn++)
            relay.Receive(0, Input(codec, 0, turn, Enumerable.Repeat<GameCommand>(new Say(0, line), 10).ToArray()));

        Assert.True(relay.Refused > 400, $"{relay.Refused} refused");
    }

    [Fact]
    public void A_bundle_of_all_players_at_their_limit_stays_within_what_clients_take()
    {
        var codec = new CommandCodec();
        var players = Enumerable.Range(0, GameSetup.MaxPlayers).ToArray();
        var relay = new TurnRelay(players, firstTurn: 2);
        IReadOnlyList<string> bundles = [];
        foreach (int player in players)
        {
            // As large as an input may be; non-ASCII comes out escaped, six bytes a character.
            string text = new('ü', (TurnRelay.MaxInputBytes - 200) / 6);
            bundles = relay.Receive(player, Input(codec, player, 2, new Say(player, text)));
        }
        Assert.Equal(0, relay.Refused);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(Assert.Single(bundles)) + 100 < ServerMessages.MaxLength);
    }

    [Fact]
    public void A_majority_of_checks_names_the_odd_player_out()
    {
        var codec = new CommandCodec();
        var relay = new TurnRelay([0, 1, 2], firstTurn: 2);
        relay.Receive(0, codec.Encode(new TurnInput(0, 2, [], Check(0, 7))));
        relay.Receive(1, codec.Encode(new TurnInput(1, 2, [], Check(0, 666))));
        relay.Receive(2, codec.Encode(new TurnInput(2, 2, [], Check(0, 7))));

        Assert.Equal([0L], relay.DesyncedTurns);
        Assert.Equal([1], relay.TakeOutOfLine());
        Assert.Empty(relay.TakeOutOfLine());
    }

    [Fact]
    public void Without_a_majority_nobody_is_named()
    {
        var codec = new CommandCodec();
        var relay = new TurnRelay([0, 1], firstTurn: 2);
        relay.Receive(0, codec.Encode(new TurnInput(0, 2, [], Check(0, 7))));
        relay.Receive(1, codec.Encode(new TurnInput(1, 2, [], Check(0, 666))));

        Assert.Equal([0L], relay.DesyncedTurns);
        Assert.Empty(relay.TakeOutOfLine());
    }

    /// <summary>A player lying about its world: on the wire only, the world itself is the others'.</summary>
    private sealed class Lying(ILockstepLink link) : ILockstepLink
    {
        public void Send(string message)
        {
            var input = JsonNode.Parse(message)!.AsObject();
            if (input["check"] is JsonObject check)
                check["checksums"]!["piles"] = 666;
            link.Send(input.ToJsonString());
        }

        public IReadOnlyList<string> Receive() => link.Receive();
    }

    [Fact]
    public void A_liar_among_three_ends_nobodys_game_but_its_own()
    {
        var setup = Humans(3);
        var relay = new LocalRelay([0, 1, 2], firstTurn: 2);
        var sessions = Enumerable.Range(0, 3).Select(player =>
        {
            var start = setup.Start(setup.Map.Open(""));
            return (Player: player, start.World, Session: new LockstepSession(start.World, player, new CommandCodec(start.Report.Catalogs),
                player == 2 ? new Lying(relay.LinkOf(2)) : relay.LinkOf(player)));
        }).ToArray();

        for (int round = 0; round < 100; round++)
            foreach (var computer in sessions)
                computer.Session.TryStep();

        Assert.Null(sessions[0].Session.Desync);
        Assert.Null(sessions[1].Session.Desync);
        Assert.True(sessions[0].Session.ChecksCompared > 0);
        Assert.Equal([2], relay.Relay.TakeOutOfLine());
    }

    // ----- The setup

    public static readonly TheoryData<GameSetup> BadSetups = new()
    {
        Humans(2) with { Players = [] },
        Humans(2) with { Players = null! },
        Humans(2) with { Players = [null!, new PlayerSetup(Human: true)] },
        Humans(2) with { Players = Enumerable.Repeat(new PlayerSetup(Human: true), 9).ToArray() },
        Humans(2) with { Players = Enumerable.Repeat(new PlayerSetup(Human: true), 3).ToArray() }, // the random map has 2
        Humans(2) with { Map = null! },
        Humans(2) with { Map = new MapChoice() },
        Humans(2) with { Map = MapChoice.Generated(new RandomMapSettings(Players: 1000), 1) },
        Humans(2) with { Map = MapChoice.Generated(new RandomMapSettings(Players: 2, Symmetry: (MapSymmetry)77), 1) },
        Humans(2) with { Map = new MapChoice("../../etc/passwd.map", new string('0', 64)) },
        Humans(2) with { Map = new MapChoice("/dev/zero", new string('0', 64)) },
        Humans(2) with { Map = new MapChoice("fine.map", "no hash") },
        Humans(2) with { Players = [new PlayerSetup(Human: true, Color: 64), new PlayerSetup(Human: true)] },
        Humans(2) with { Players = [new PlayerSetup(Human: true, Team: -1), new PlayerSetup(Human: true)] },
        Humans(2) with { Players = [new PlayerSetup(Human: true, People: (Civilization)9), new PlayerSetup(Human: true)] },
        Humans(2) with { Players = [new PlayerSetup(Human: true), new PlayerSetup(Character: "no such character")] },
        Humans(2) with { Positions = [5, null] },
        Humans(2) with { Positions = [-1, null] },
        Humans(2) with { Positions = [0, 0] },
        Humans(2) with { Settings = new GameSettings { Depletion = new(-1, 0, 0), SpellCostIncrease = 0 } },
        Humans(2) with { Settings = new GameSettings { Depletion = new(0, 0, 0), SpellCostIncrease = 1000 } },
        Humans(2) with { Settings = new GameSettings { Depletion = null!, SpellCostIncrease = 0 } },
        Humans(2) with { Fingerprint = new GameFingerprint(1, "1", null!) },
        Humans(2) with { Fingerprint = new GameFingerprint(1, "line\nbreak", new Dictionary<string, string>()) },
        Humans(2) with { Fingerprint = new GameFingerprint(1, "1", new Dictionary<string, string> { ["../x"] = new('0', 64) }) },
        Humans(2) with { Fingerprint = new GameFingerprint(1, "1", new Dictionary<string, string> { ["x.json"] = "short" }) },
    };

    [Theory]
    [MemberData(nameof(BadSetups))]
    public void A_setup_that_could_harm_a_computer_is_refused(GameSetup setup) => Assert.NotEmpty(setup.Problems());

    [Fact]
    public void Honest_setups_pass()
    {
        var map = new MapChoice("Multiplayer/Some Map (8).map", new string('a', 64));
        Assert.Empty((Humans(2) with { Fingerprint = GameFingerprint.Of(), Positions = [null, 1] }).Problems());
        Assert.Empty((Humans(2) with { Map = map }).Problems());
        Assert.Empty((Humans(2) with { Players = [new PlayerSetup(Human: true, Character: "general", Team: 4, Color: 63), new PlayerSetup()] }).Problems());
        Assert.Empty(new GameSetup { Map = map, Players = Enumerable.Repeat(new PlayerSetup(Human: true), 8).ToArray() }.Problems());
    }

    [Fact]
    public void A_human_beyond_the_map_cannot_start()
    {
        var setup = new GameSetup
        {
            Map = new MapChoice("a.map", new string('a', 64)),
            Players = [new PlayerSetup(Human: true), new PlayerSetup(), new PlayerSetup(Human: true)],
        };
        Assert.Empty(setup.Problems(3));
        Assert.NotEmpty(setup.Problems(2));
        var random = Humans(2) with { Fingerprint = GameFingerprint.Of() };
        Assert.Empty(random.Problems(2));
    }

    [Theory]
    [InlineData("../secret.map")]
    [InlineData("a/../../secret.map")]
    [InlineData("/etc/passwd")]
    [InlineData("/secret.map")]
    [InlineData(@"..\secret.map")]
    [InlineData(@"C:\secret.map")]
    [InlineData("secret.txt")]
    [InlineData("a//b.map")]
    public void A_map_path_cannot_leave_the_map_directory(string path)
    {
        string root = Directory.CreateTempSubdirectory("homestead-map-").FullName;
        try
        {
            string directory = Directory.CreateDirectory(Path.Combine(root, "Map")).FullName;
            string secret = Path.Combine(root, "secret.map");
            File.WriteAllText(secret, "secret");

            Assert.False(MapChoice.IsSafeMapFile(path));
            Assert.Throws<FileNotFoundException>(() => new MapChoice(path, MapChoice.HashOf(secret)).Locate(directory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ----- The start message

    private static JsonObject Started(GameSetup setup, int player = 0, LockstepOptions? options = null) => new()
    {
        ["setup"] = JsonNode.Parse(setup.ToJson()), ["player"] = player, ["options"] = ServerMessages.Options(options ?? new LockstepOptions()),
    };

    [Fact]
    public void A_good_game_start_is_read()
    {
        var setup = Humans(2) with { Fingerprint = GameFingerprint.Of() };
        var started = ServerMessages.ParseStarted(Started(setup, 1));
        Assert.Equal(1, started.Player);
    }

    public static readonly TheoryData<string> BadStarts = new()
    {
        "{}",
        """{"setup":7,"player":0}""",
        """{"setup":{},"player":0}""",
        """{"setup":{"Map":null},"player":"me"}""",
    };

    [Theory]
    [MemberData(nameof(BadStarts))]
    public void A_broken_game_start_is_no_crash(string message) =>
        Assert.Throws<InvalidDataException>(() => ServerMessages.ParseStarted(JsonNode.Parse(message)!.AsObject()));

    [Fact]
    public void A_game_start_from_a_hostile_server_is_refused()
    {
        var fingerprint = GameFingerprint.Of();
        var good = Humans(2) with { Fingerprint = fingerprint };
        Assert.Throws<InvalidDataException>(() => ServerMessages.ParseStarted(Started(good with { Fingerprint = null })));
        Assert.Throws<InvalidDataException>(() => ServerMessages.ParseStarted(Started(good, player: 5)));
        Assert.Throws<InvalidDataException>(() => ServerMessages.ParseStarted(Started(good, player: -1)));
        Assert.Throws<InvalidDataException>(() => ServerMessages.ParseStarted(Started(good, options: new LockstepOptions(TurnTicks: 0))));
        Assert.Throws<InvalidDataException>(() => ServerMessages.ParseStarted(Started(good, options: new LockstepOptions(DelayTurns: 1_000_000))));
        Assert.Throws<InvalidDataException>(() => ServerMessages.ParseStarted(Started(good with { Map = new MapChoice("/dev/zero", new string('0', 64)) })));
    }
}
