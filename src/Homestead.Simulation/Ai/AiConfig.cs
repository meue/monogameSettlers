using System.Text.Json;
using System.Text.Json.Nodes;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Map;

namespace Homestead.Simulation.Ai;

/// <summary>
/// AI balancing, loaded from <c>data/ai.json</c>. Its <c>civilizations</c> section may override any value
/// per people (objects are merged, other values replaced).
/// </summary>
public sealed record AiConfig
{
    public required int ThinkTicks { get; init; }
    public required MilitaryRules Military { get; init; }

    /// <summary>The AI's soldiers outside buildings (see <c>military</c> in ai.json).</summary>
    /// <param name="RetreatRatio">A running strike is called off when the strikers fall below this times the enemy's strength.</param>
    /// <param name="HinterlandDepth">
    /// Guerilla against enemies just standing around (not attacking the land): none deeper than this in their own
    /// player's land, counted from its edge (0: anywhere).
    /// </param>
    /// <param name="ReinforceRadius">
    /// Enemy soldiers outside the buildings within this distance of a strike's target count as its defence too:
    /// slightly scattered soldiers come to help (0: only the group and those on the way).
    /// </param>
    /// <param name="TowerFireRatio">
    /// Guerilla (not defence) only where the bowmen of enemy military buildings reaching the target or the way
    /// are at most this times the enemies' strength there: they cannot be struck back, every loss to them is
    /// for nothing (0: no limit). A running guerilla strike whose enemies retreat under such fire is called off.
    /// </param>
    public sealed record MilitaryRules(int ThinkTicks, int GroupRadius, int StrikeRange, double StrikeRatio, int ThreatRange, int GuardDistance,
        double RetreatRatio = 1.0, int HinterlandDepth = 0, int ReinforceRadius = 0, double TowerFireRatio = 0);

    public required AttackRules Attack { get; init; }

    /// <summary>Wounded soldiers in small fights (see <c>military.wounded</c> in ai.json); null in games saved before: <see cref="WoundedRules.Default"/>.</summary>
    public WoundedRules? Wounded { get; init; }

    /// <param name="MaxSoldiers">Only in fights of at most this many own soldiers (strike or campaign).</param>
    /// <param name="WithdrawHealth">A fighting soldier below this share of its full health withdraws (0: never).</param>
    /// <param name="RejoinHealth">A withdrawn soldier healed to this share comes back.</param>
    /// <param name="HoldRatio">Only while the others still fighting have this times the enemy's strength without it.</param>
    /// <param name="NeedRatio">The withdrawn come back when those fighting fall below this times the enemy's strength.</param>
    /// <param name="MopUpRatio">… or when the enemy has at most this times their strength left (finishing it off is safe).</param>
    /// <param name="RestDistance">How far from the enemies they wait (beyond sight: they do not fight again on their own).</param>
    public sealed record WoundedRules(int MaxSoldiers, double WithdrawHealth, double RejoinHealth, double HoldRatio, double NeedRatio,
        double MopUpRatio, int RestDistance)
    {
        public static readonly WoundedRules Default = new(10, 0.35, 0.8, 0.8, 0.6, 0.25, 21);
    }

    /// <summary>Hospitals for the wounded (see <c>hospital</c> in ai.json); null in games saved before: <see cref="HospitalRules.Default"/>.</summary>
    public HospitalRules? Hospital { get; init; }

    /// <param name="Wounded">A hospital is built once more than this many wounded soldiers are outside none's call range (0: never).</param>
    /// <param name="WoundedHealth">Wounded: below this share of the full health.</param>
    /// <param name="SendDistance">Idle wounded soldiers walk to the door of an own hospital at most this far away.</param>
    /// <param name="Spacing">No further hospital within this many fields of an own one, sites too (0 in games saved before: the default).</param>
    /// <param name="Max">At most this many hospitals, sites too (0 in games saved before: the default).</param>
    public sealed record HospitalRules(int Wounded, double WoundedHealth, int SendDistance, int Spacing = 120, int Max = 3)
    {
        public static readonly HospitalRules Default = new(5, 0.8, 60);

        public int SpacingOrDefault => Spacing > 0 ? Spacing : Default.Spacing;

        public int MaxOrDefault => Max > 0 ? Max : Default.Max;
    }

    /// <summary>Weapons made in the late game, with a big army (see <c>army</c> in ai.json); null in games saved before: no change.</summary>
    public ArmyRules? Army { get; init; }

    /// <param name="LateSoldiers">Late game from this many own soldiers outside the buildings on (0: never).</param>
    /// <param name="LateShares">The weaponsmiths' shares then (Waren › Herstellung).</param>
    public sealed record ArmyRules(int LateSoldiers, IReadOnlyDictionary<Material, double> LateShares)
    {
        /// <summary>Below this many the late game is over again (a little fewer than it took, so it does not flicker).</summary>
        public int EarlySoldiers => LateSoldiers * 3 / 4;
    }

    /// <summary>Growth beyond the build order (see <c>growth</c> in ai.json); null in games saved before: <see cref="GrowthRules.Default"/>.</summary>
    public GrowthRules? Growth { get; init; }

    /// <param name="WindowTicks">How long the waiting is measured before deciding (0: no growth).</param>
    /// <param name="Starved">A good gets one more producer when its consumers waited for it this share of the time …</param>
    /// <param name="InStock">… and fewer than this many units of it are in stock (otherwise the carriers are short, not the good).</param>
    /// <param name="Busy">Weaponsmiths (barracks) busy this share of the time get one more.</param>
    /// <param name="WeaponsInStock">A further barrack only while at least this many weapons wait in stock.</param>
    public sealed record GrowthRules(int WindowTicks, double Starved, int InStock, double Busy, int WeaponsInStock)
    {
        public static readonly GrowthRules Default = new(120 * World.TicksPerSecond, 0.5, 4, 0.8, 8);
    }

    /// <summary>Priests (see <c>magic</c> in ai.json).</summary>
    public MagicRules Magic { get; init; } = new(6, 3, DefaultUpgradeOrder);

    private static readonly SoldierType[] DefaultUpgradeOrder = [SoldierType.Swordsman, SoldierType.Bowman, SoldierType.Pikeman];

    /// <param name="PriestDistance">How far behind the soldiers (towards home) priests stay.</param>
    /// <param name="MinTargets">A spell is cast where it hits at least this many soldiers.</param>
    /// <param name="UpgradeOrder">Soldier types in the order they are upgraded (to level 2, then the same to level 3).</param>
    /// <param name="TurnRatio">
    /// A fight about to be given up is turned with the people's spell instead while the own soldiers still have this
    /// share of what they need (0: never; null in games saved before: 0.6).
    /// </param>
    public sealed record MagicRules(int PriestDistance, int MinTargets, IReadOnlyList<SoldierType> UpgradeOrder, double? TurnRatio = null);

    /// <summary>Campaigns against enemy military buildings (see <c>attack</c> in ai.json).</summary>
    /// <param name="ArmyRatio">A campaign needs this times the target owner's whole army (0: any army).</param>
    /// <param name="Commit">Share of the spare soldiers left after the force is chosen that come along as well (1: all of them).</param>
    /// <param name="AbortRatio">After gathering the campaign is given up below this times the defence (0: never).</param>
    /// <param name="MaxDistance">Targets at most this far from the own land (0: any distance).</param>
    /// <param name="GarrisonWeight">Per building name: its garrison counts this many times in the defence (missing: 1).</param>
    public sealed record AttackRules(double Ratio, int DefenseRadius, int PathClearance, int GatherTicks, int MinSoldiers,
        double ArmyRatio, double Commit = 0, double AbortRatio = 0, int MaxDistance = 0,
        IReadOnlyDictionary<string, double>? GarrisonWeight = null);

    /// <summary>Offensives the AI chat starts (see <c>offensive</c> in ai.json); null in games saved before: <see cref="OffensiveRules.Default"/>.</summary>
    public OffensiveRules? Offensive { get; init; }

    /// <param name="Range">The next target at most this far from the last one.</param>
    /// <param name="FrontRange">Or this far from allied soldiers out on enemy land (preferred).</param>
    /// <param name="Ratio">The force against the target's defence.</param>
    public sealed record OffensiveRules(int Range, int FrontRange, double Ratio)
    {
        public static readonly OffensiveRules Default = new(40, 30, 1.0);
    }

    /// <summary>Campaigns out of plain superiority (see <c>superiority</c> in ai.json); null in games saved before: <see cref="SuperiorityRules.Default"/>.</summary>
    public SuperiorityRules? Superiority { get; init; }

    /// <param name="StartTicks">No such campaign before this game time.</param>
    /// <param name="IntervalTicks">The armies are compared this often.</param>
    /// <param name="ArmyRatio">The own whole army against an enemy's whole army (0: never).</param>
    /// <param name="Ratio">The force against the target's defence.</param>
    public sealed record SuperiorityRules(long StartTicks, long IntervalTicks, double ArmyRatio, double Ratio)
    {
        public static readonly SuperiorityRules Default = new(3600L * World.TicksPerSecond, 300L * World.TicksPerSecond, 2, 2);
    }
    public required int MaxSites { get; init; }
    public required int MaxTowerSites { get; init; }
    public required int FreeCarriers { get; init; }
    public required string House { get; init; }

    /// <summary>A living house is wanted while there are no more carriers than this many per production building.</summary>
    public double CarriersPerProductionBuilding { get; init; } = 2;

    /// <summary>
    /// Living houses from big to small, each with the sawmills and stonecutters (finished) it takes:
    /// the first the player has enough of is built, otherwise <see cref="House"/>.
    /// </summary>
    public IReadOnlyList<HouseSize> HouseSizes { get; init; } = [];

    public sealed record HouseSize(string Building, int Sawmills, int Stonecutters);

    /// <summary>A living house is wanted once fewer carriers than this may still become specialists (carrier reserve).</summary>
    public int HouseWhenConvertible { get; init; } = 4;

    /// <summary>A living house is torn down when the living space left over without it is still at least this much.</summary>
    public int SpareRoom { get; init; } = 15;

    /// <summary>A forester is stopped while at least this many trees (saplings too) stand in its work area, restarted below.</summary>
    public int ForesterMaxTrees { get; init; } = 10;

    /// <summary>
    /// A forester is built only for a finished woodcutter with fewer than this many trees (saplings too) in its work area and
    /// no forester near it (a full forest needs none: it would stop at once).
    /// </summary>
    public int ForesterBuildBelowTrees { get; init; } = 15;

    /// <summary>How often the foresters' work areas are looked at.</summary>
    public int ForesterCheckTicks { get; init; } = 10 * World.TicksPerSecond;

    /// <summary>A mine is built only where its chance per run (<see cref="Production.MineChance"/>) is at least this (0-1); otherwise the AI expands.</summary>
    public double MineMinChance { get; init; } = 0.3;

    /// <summary>A finished mine whose chance fell below this (0-1) is torn down, so a new one goes where the ground is richer.</summary>
    public double MineAbandonChance { get; init; } = 0.1;
    public required int StockWhenWaiting { get; init; }
    public required IReadOnlyList<(string Building, int Count)> BuildOrder { get; init; }
    public required string ExpansionBuilding { get; init; }
    public required int MinStoneUnits { get; init; }
    public required int WantedStoneUnits { get; init; }
    public required int MinFreeLand { get; init; }
    public required int MinTrees { get; init; }
    public required IReadOnlyList<ResourceType> Ores { get; init; }

    /// <summary>
    /// Expanded towards only late: once every build order entry before the ore's mine is reached, after
    /// all other needs but plain land (gold for the combat strength).
    /// </summary>
    public IReadOnlyList<ResourceType> LateOres { get; init; } = [];
    public required int SpareSoldiersForLand { get; init; }
    public required int LandStock { get; init; }

    /// <summary>
    /// Largest share of water among the unclaimed points a new tower would take. Spots above it are passed
    /// over; for a needed resource the spot with the least water is taken if none is below.
    /// </summary>
    public double MaxWaterShare { get; init; } = 0.4;

    /// <summary>
    /// Characters by id (see <c>characters</c> in ai.json and docs/KI-TACTICS.md): the traits each plays
    /// with, in order of priority, with their settings (<c>traits</c> defaults merged with the character's own).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TraitSettings>> Characters { get; init; } =
        new Dictionary<string, IReadOnlyList<TraitSettings>> { [StandardCharacter] = [] };

    /// <summary>Which character a player of this people gets, drawn by weight once per game.</summary>
    public IReadOnlyList<(string Character, double Weight)> CharacterWeights { get; init; } = [(StandardCharacter, 1)];

    public const string StandardCharacter = "standard";

    /// <param name="Trait">The trait's id.</param>
    public sealed record TraitSettings(string Trait, JsonElement Settings);

    /// <param name="character">
    /// A character whose own values (all but <c>traits</c> and the trait settings) override the general
    /// ones, after the people's.
    /// </param>
    public static AiConfig Load(string? dataDirectory = null, Civilization? civilization = null, string? character = null)
    {
        string path = Path.Combine(dataDirectory ?? BuildingCatalog.DefaultDataDirectory, "ai.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (civilization is { } people && node["civilizations"]?[people.ToString()] is JsonObject overrides)
            Merge(node, overrides);
        if (character is not null && node["characters"] is JsonObject known && known[character] is null && Variant(known, character) is { } variant)
            known[character] = variant;
        if (character is not null && node["characters"]?[character] is JsonObject own)
        {
            var traits = (own["traits"]?.AsArray() ?? []).Select(t => t!.GetValue<string>()).ToHashSet();
            var values = new JsonObject();
            foreach (var (key, value) in own)
                if (key != "traits" && !traits.Contains(key))
                    values[key] = value?.DeepClone();
            Merge(node, values);
        }
        MoveInBuildOrder(node);
        using var document = JsonDocument.Parse(node.ToJsonString());
        var root = document.RootElement;
        var characters = LoadCharacters(node);
        var expansion = root.GetProperty("expansion");
        var military = root.GetProperty("military");
        var attack = root.GetProperty("attack");
        return new AiConfig
        {
            ThinkTicks = Math.Max(1, (int)Math.Round(root.GetProperty("thinkSeconds").GetDouble() * World.TicksPerSecond)),
            MaxSites = root.GetProperty("maxSites").GetInt32(),
            Attack = new AttackRules(
                attack.GetProperty("ratio").GetDouble(),
                attack.GetProperty("defenseRadius").GetInt32(),
                attack.GetProperty("pathClearance").GetInt32(),
                (int)Math.Round(attack.GetProperty("gatherSeconds").GetDouble() * World.TicksPerSecond),
                attack.GetProperty("minSoldiers").GetInt32(),
                attack.TryGetProperty("armyRatio", out var armyRatio) ? armyRatio.GetDouble() : attack.GetProperty("ratio").GetDouble(),
                attack.TryGetProperty("commit", out var commit) ? commit.GetDouble() : 0,
                attack.TryGetProperty("abortRatio", out var abortRatio) ? abortRatio.GetDouble() : attack.GetProperty("ratio").GetDouble(),
                attack.TryGetProperty("maxDistance", out var maxDistance) ? maxDistance.GetInt32() : 0,
                attack.TryGetProperty("garrisonWeight", out var garrisonWeight)
                    ? garrisonWeight.EnumerateObject().Where(w => !w.Name.StartsWith('_')).ToDictionary(w => w.Name, w => w.Value.GetDouble())
                    : null),
            Military = new MilitaryRules(
                (int)Math.Round(military.GetProperty("thinkSeconds").GetDouble() * World.TicksPerSecond),
                military.GetProperty("groupRadius").GetInt32(),
                military.GetProperty("strikeRange").GetInt32(),
                military.GetProperty("strikeRatio").GetDouble(),
                military.GetProperty("threatRange").GetInt32(),
                military.GetProperty("guardDistance").GetInt32(),
                military.TryGetProperty("retreatRatio", out var retreat) ? retreat.GetDouble() : 1.0,
                military.TryGetProperty("hinterlandDepth", out var hinterland) ? hinterland.GetInt32() : 0,
                military.TryGetProperty("reinforceRadius", out var reinforce) ? reinforce.GetInt32() : 0,
                military.TryGetProperty("towerFireRatio", out var towerFire) ? towerFire.GetDouble() : 0),
            Superiority = root.TryGetProperty("superiority", out var superiority)
                ? new SuperiorityRules((long)Math.Round(superiority.GetProperty("startMinutes").GetDouble() * 60 * World.TicksPerSecond),
                    (long)Math.Round(superiority.GetProperty("intervalSeconds").GetDouble() * World.TicksPerSecond),
                    superiority.GetProperty("armyRatio").GetDouble(), superiority.GetProperty("ratio").GetDouble())
                : SuperiorityRules.Default,
            Offensive = root.TryGetProperty("offensive", out var offensive)
                ? new OffensiveRules(offensive.GetProperty("range").GetInt32(), offensive.GetProperty("frontRange").GetInt32(),
                    offensive.GetProperty("ratio").GetDouble())
                : OffensiveRules.Default,
            Wounded = military.TryGetProperty("wounded", out var wounded)
                ? new WoundedRules(wounded.GetProperty("maxSoldiers").GetInt32(), wounded.GetProperty("withdrawHealth").GetDouble(),
                    wounded.GetProperty("rejoinHealth").GetDouble(), wounded.GetProperty("holdRatio").GetDouble(),
                    wounded.GetProperty("needRatio").GetDouble(), wounded.GetProperty("mopUpRatio").GetDouble(),
                    wounded.GetProperty("restDistance").GetInt32())
                : WoundedRules.Default,
            Hospital = root.TryGetProperty("hospital", out var hospital)
                ? new HospitalRules(hospital.GetProperty("wounded").GetInt32(), hospital.GetProperty("woundedHealth").GetDouble(),
                    hospital.GetProperty("sendDistance").GetInt32(),
                    hospital.TryGetProperty("spacing", out var spacing) ? spacing.GetInt32() : HospitalRules.Default.Spacing,
                    hospital.TryGetProperty("max", out var max) ? max.GetInt32() : HospitalRules.Default.Max)
                : HospitalRules.Default,
            Army = root.TryGetProperty("army", out var army)
                ? new ArmyRules(army.GetProperty("lateSoldiers").GetInt32(),
                    army.GetProperty("lateShares").EnumerateObject().Where(p => !p.Name.StartsWith('_'))
                        .ToDictionary(p => Materials.Parse(p.Name) ?? throw new InvalidDataException($"unknown good '{p.Name}' in army.lateShares"),
                            p => p.Value.GetDouble()))
                : null,
            Growth = root.TryGetProperty("growth", out var growth)
                ? new GrowthRules(growth.GetProperty("windowSeconds").GetInt32() * World.TicksPerSecond, growth.GetProperty("starved").GetDouble(),
                    growth.GetProperty("inStock").GetInt32(), growth.GetProperty("busy").GetDouble(), growth.GetProperty("weaponsInStock").GetInt32())
                : GrowthRules.Default,
            Magic = root.TryGetProperty("magic", out var magic)
                ? new MagicRules(magic.GetProperty("priestDistance").GetInt32(), magic.GetProperty("minTargets").GetInt32(),
                    magic.TryGetProperty("upgradeOrder", out var order)
                        ? order.EnumerateArray().Select(t => Enum.Parse<SoldierType>(t.GetString()!)).ToArray()
                        : DefaultUpgradeOrder,
                    magic.TryGetProperty("turnRatio", out var turn) ? turn.GetDouble() : null)
                : new MagicRules(6, 3, DefaultUpgradeOrder),
            MaxTowerSites = root.GetProperty("maxTowerSites").GetInt32(),
            FreeCarriers = root.GetProperty("freeCarriers").GetInt32(),
            House = root.GetProperty("house").GetString()!,
            HouseWhenConvertible = root.TryGetProperty("houseWhenConvertible", out var convertible) ? convertible.GetInt32() : 4,
            SpareRoom = root.TryGetProperty("spareRoom", out var spare) ? spare.GetInt32() : 15,
            ForesterMaxTrees = root.TryGetProperty("forester", out var forester) && forester.TryGetProperty("maxTrees", out var maxTrees) ? maxTrees.GetInt32() : 10,
            ForesterBuildBelowTrees = root.TryGetProperty("forester", out var foresterBuild) && foresterBuild.TryGetProperty("buildBelowTrees", out var below)
                ? below.GetInt32() : 15,
            ForesterCheckTicks = (int)Math.Round((root.TryGetProperty("forester", out var foresterCheck) && foresterCheck.TryGetProperty("checkSeconds", out var check)
                ? check.GetDouble() : 10) * World.TicksPerSecond),
            MineMinChance = root.TryGetProperty("mines", out var mines) && mines.TryGetProperty("minChance", out var minChance) ? minChance.GetDouble() : 0.3,
            MineAbandonChance = root.TryGetProperty("mines", out var minesAbandon) && minesAbandon.TryGetProperty("abandonChance", out var abandon) ? abandon.GetDouble() : 0.1,
            CarriersPerProductionBuilding = root.TryGetProperty("carriersPerProductionBuilding", out var perBuilding) ? perBuilding.GetDouble() : 2,
            HouseSizes = root.TryGetProperty("houseSizes", out var sizes)
                ? sizes.EnumerateArray().Select(s => new HouseSize(s.GetProperty("building").GetString()!,
                    s.GetProperty("sawmills").GetInt32(), s.GetProperty("stonecutters").GetInt32())).ToArray()
                : [],
            StockWhenWaiting = root.GetProperty("stockWhenWaiting").GetInt32(),
            BuildOrder = root.GetProperty("buildOrder").EnumerateArray()
                .Select(e => (e[0].GetString()!, e[1].GetInt32())).ToArray(),
            ExpansionBuilding = expansion.GetProperty("building").GetString()!,
            MinStoneUnits = expansion.GetProperty("minStoneUnits").GetInt32(),
            WantedStoneUnits = expansion.GetProperty("wantedStoneUnits").GetInt32(),
            MinFreeLand = expansion.GetProperty("minFreeLand").GetInt32(),
            MinTrees = expansion.GetProperty("minTrees").GetInt32(),
            SpareSoldiersForLand = expansion.GetProperty("spareSoldiersForLand").GetInt32(),
            LandStock = expansion.GetProperty("landStock").GetInt32(),
            MaxWaterShare = expansion.TryGetProperty("maxWaterShare", out var waterShare) ? waterShare.GetDouble() : 0.4,
            Ores = expansion.GetProperty("ores").EnumerateArray().Select(o => Enum.Parse<ResourceType>(o.GetString()!)).ToArray(),
            LateOres = expansion.TryGetProperty("lateOres", out var late)
                ? late.EnumerateArray().Select(o => Enum.Parse<ResourceType>(o.GetString()!)).ToArray()
                : [],
            Characters = characters,
            CharacterWeights = root.TryGetProperty("characterWeights", out var weights)
                ? weights.EnumerateArray().Select(w => (w[0].GetString()!, w[1].GetDouble())).ToArray()
                : [(StandardCharacter, 1)],
        };
    }

    /// <summary>The characters with each trait's settings: the trait defaults overridden by the character's values.</summary>
    private static Dictionary<string, IReadOnlyList<TraitSettings>> LoadCharacters(JsonObject root)
    {
        var result = new Dictionary<string, IReadOnlyList<TraitSettings>> { [StandardCharacter] = [] };
        if (root["characters"] is not JsonObject characters)
            return result;
        var defaults = root["traits"] as JsonObject;
        foreach (var (name, value) in characters)
        {
            if (value is not JsonObject character)
                continue;
            var traits = new List<TraitSettings>();
            foreach (var trait in character["traits"]?.AsArray() ?? [])
            {
                string traitId = trait!.GetValue<string>();
                var settings = defaults?[traitId] is JsonObject d ? (JsonObject)d.DeepClone() : new JsonObject();
                if (character[traitId] is JsonObject own)
                    Merge(settings, own);
                using var document = JsonDocument.Parse(settings.ToJsonString());
                traits.Add(new TraitSettings(traitId, document.RootElement.Clone()));
            }
            result[name] = traits;
        }
        return result;
    }

    /// <summary>
    /// A character made up from its id (for trying out combinations, e.g. in the arena):
    /// <c>pincer+guardByThreat</c> is a character of just those traits with their default settings,
    /// <c>skirmisher+forwardBase</c> a character with more traits (in front of its own);
    /// <c>@path=value</c> parts set values on top of a character or such traits, e.g.
    /// <c>general@attack.ratio=1.3@pincer.feintShare=0.5@buildOrderMoves.barrack=-5</c>.
    /// Null if the id names neither a character nor traits.
    /// </summary>
    private static JsonObject? Variant(JsonObject characters, string id)
    {
        var parts = id.Split('@');
        var names = parts[0].Split('+');
        JsonObject result;
        if (characters[names[0]] is JsonObject known)
            result = (JsonObject)known.DeepClone();
        else if (AiTrait.Known.Contains(names[0]) || names[0].Length == 0 && parts.Length > 1)
            result = new JsonObject { ["traits"] = new JsonArray() };
        else
            return null;
        // Traits named after the character come first (before its own, e.g. for the guarding).
        var added = names.Skip(characters[names[0]] is JsonObject ? 1 : 0).Where(n => n.Length > 0).ToList();
        if (added.Any(t => !AiTrait.Known.Contains(t)))
            return null;
        var own = (result["traits"]?.AsArray() ?? []).Select(t => t!.GetValue<string>()).Where(t => !added.Contains(t));
        result["traits"] = new JsonArray(added.Concat(own).Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        foreach (string setting in parts.Skip(1))
        {
            int equals = setting.IndexOf('=');
            if (equals <= 0)
                throw new ArgumentException($"AI character '{id}': '{setting}' is no path=value");
            var path = setting[..equals].Split('.');
            string text = setting[(equals + 1)..];
            JsonNode? value;
            try
            {
                value = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                value = JsonValue.Create(text);
            }
            var at = result;
            foreach (string key in path[..^1])
                at = at[key] as JsonObject ?? (JsonObject)(at[key] = new JsonObject());
            at[path[^1]] = value;
        }
        return result;
    }

    /// <summary>
    /// <c>buildOrderMoves</c> (building: places, negative = earlier): every build order entry of that
    /// building moves that many places, so a character can shift the people's order without its own copy.
    /// </summary>
    private static void MoveInBuildOrder(JsonObject root)
    {
        if (root["buildOrderMoves"] is not JsonObject moves || moves.Count == 0 || root["buildOrder"] is not JsonArray order)
            return;
        var entries = order.Select((entry, index) =>
        {
            int shift = moves[entry![0]!.GetValue<string>()]?.GetValue<int>() ?? 0;
            return (Entry: entry, Key: index + shift + Math.Sign(shift) * 0.5);
        }).OrderBy(e => e.Key).Select(e => e.Entry!.DeepClone()).ToArray();
        root["buildOrder"] = new JsonArray(entries);
    }

    /// <summary>Copies the values of <paramref name="overrides"/> into <paramref name="target"/>, merging objects.</summary>
    private static void Merge(JsonObject target, JsonObject overrides)
    {
        foreach (var (key, value) in overrides)
        {
            if (value is JsonObject inner && target[key] is JsonObject existing)
                Merge(existing, inner);
            else
                target[key] = value?.DeepClone();
        }
    }
}
