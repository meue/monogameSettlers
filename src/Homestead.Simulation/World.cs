using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Events;
using Homestead.Simulation.Magic;
using Homestead.Simulation.Map;
using Homestead.Simulation.Military;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation;

/// <summary>
/// Simulation state, advanced in fixed ticks independent of the frame rate.
/// </summary>
/// <remarks>
/// Settler movement: each settler owns the field it stands on and reserves the field it steps to.
/// It gives up the field it leaves as soon as the step starts, so a follower can step in right behind it.
/// When the next field is taken, conflicts are resolved in this order, so nobody can get stuck:
/// <list type="number">
/// <item>Two settlers wanting each other's field swap places.</item>
/// <item>An idle settler in the way is asked to step aside.</item>
/// <item>After <see cref="ReplanAfterTicks"/> the waiting settler looks for a detour around the field,
/// but at most once per <see cref="DetourCooldownTicks"/>, so it cannot circle around a crowd forever
/// (found by the logistics stress test: carriers circling a storehouse full of busy colleagues).</item>
/// <item>After <see cref="PassThroughAfterTicks"/> it walks through (watchdog; counted in <see cref="PassThroughs"/>).</item>
/// </list>
/// </remarks>
public sealed partial class World
{
    public const int TicksPerSecond = 20;

    /// <summary>Ticks for one grid step on plain ground (0.4 s).</summary>
    public const int StepTicks = 8;

    /// <summary>Step progress per tick on plain ground (see <see cref="Settler.StepProgress"/>).</summary>
    public const int BaseStepSpeed = 100;

    /// <summary>Progress a step takes: <see cref="StepTicks"/> on plain ground, fewer on trails, more on mountains.</summary>
    public const int StepUnits = StepTicks * BaseStepSpeed;

    public const int ReplanAfterTicks = 6;
    public const int PassThroughAfterTicks = 40;
    public const int DetourCooldownTicks = 40;

    /// <summary>A detour may be at most this many steps longer than the blocked path.</summary>
    public const int MaxDetourSteps = 12;

    private const int NoOccupant = -1;

    private readonly List<Settler> _settlers = new();
    private readonly Dictionary<int, Settler> _settlersById = new();
    private int _nextSettlerId;
    private readonly int[] _occupant;

    /// <summary>Mana, temples, priests and spells.</summary>
    public Magic.Magic Magic { get; }

    /// <summary>People of each player (index = player), where known (loaded maps); Romans otherwise.</summary>
    public IReadOnlyList<Civilization> Peoples { get; set; } = [];

    /// <summary>Players in the game (loaded maps); 0 where unknown, which leaves the combat strength at 100 % everywhere.</summary>
    public int PlayerCount { get; set; }

    public Civilization PeopleOf(int player) => player < Peoples.Count ? Peoples[player] : Civilization.Roman;

    /// <summary>Players with the <see cref="Hardcore"/> head start (set before the game starts).</summary>
    public HashSet<int> HardcorePlayers { get; } = new();

    public bool IsHardcore(int player) => HardcorePlayers.Contains(player);

    /// <summary>Team per player (index = player; set before the game starts): 1–4, 0 or missing: no team, against everybody.</summary>
    public List<int> Teams { get; } = new();

    public int TeamOf(int player) => player >= 0 && player < Teams.Count ? Teams[player] : 0;

    /// <summary>Whether the players are on the same team (a player is its own ally): they do not fight, take land or steal from each other.</summary>
    public bool AreAllies(int a, int b) => a == b || TeamOf(a) != 0 && TeamOf(a) == TeamOf(b);

    /// <summary>Full hit points of the player's soldiers of the type and level.</summary>
    public float MaxHealth(int player, SoldierType type, int level) =>
        Military.Config.MaxHealth(type, level) * (IsHardcore(player) ? Hardcore.HealthFactor : 1);

    /// <summary>Units of material the construction pile asks of the player.</summary>
    public int ConstructionCost(int player, PileDefinition pile) =>
        IsHardcore(player) ? Hardcore.Cost(pile.Required) : pile.Required;

    /// <summary>What the game's chance is drawn from: the same seed plays the same game.</summary>
    public int Seed { get; }

    /// <summary>Computer players; each acts for its own player in every tick.</summary>
    public List<Ai.AiPlayer> AiPlayers { get; } = new();

    /// <summary>The players' orders, applied at the start of the next step (see <see cref="Commands.GameCommand"/>).</summary>
    public Commands.CommandQueue Commands { get; } = new();

    private GameEvents? _events;

    /// <summary>Messages to the players (attacks, …); created on first use in games saved before there were any.</summary>
    public GameEvents Events => _events ??= new GameEvents(this, EventConfig.Load());

    /// <summary>The global chat every player may write to.</summary>
    public ChatLog Chat { get; } = new();

    /// <summary>Says <paramref name="text"/> in the global chat as <paramref name="player"/>, at the current tick.</summary>
    public ChatMessage? Say(int player, string text) => Chat.Say(player, text, Tick);

    /// <param name="seed">Varies everything left to chance (0: the same game as always).</param>
    public World(GameMap? map = null, ProductionConfig? production = null, MilitaryConfig? military = null, MagicConfig? magic = null, int seed = 0,
        EventConfig? events = null)
    {
        Seed = seed;
        _events = new GameEvents(this, events ?? (map is null ? EventConfig.None : EventConfig.Load()));
        Map = map;
        Logistics = new Logistics(this);
        Construction = new Construction(this);
        var config = production ?? (map is null ? ProductionConfig.None : ProductionConfig.Load());
        Recruitment = new Recruitment(this, config);
        Production = new Production(this, config);
        Statistics = new ProductionStatistics(this);
        Idling = new Idling(this);
        Housing = new Housing(this, config);
        Geology = new Geology(this, config);
        Military = new Military.Military(this, military ?? MilitaryConfig.Load());
        Magic = new Magic.Magic(this, magic ?? (map is null ? MagicConfig.None : MagicConfig.Load()));
        if (map is not null)
        {
            PathFinder = new PathFinder(map);
            _occupant = new int[map.PointCount];
            Array.Fill(_occupant, NoOccupant);
        }
        else
        {
            _occupant = [];
        }
        Trails = new Trails(this, config.Walking);
    }

    public static TimeSpan TickDuration { get; } = TimeSpan.FromSeconds(1.0 / TicksPerSecond);

    public GameMap? Map { get; }

    /// <summary>Trails trodden by carriers, and the walking speed by ground.</summary>
    public Trails Trails { get; }

    public PathFinder? PathFinder { get; }

    /// <summary>Scripted walkers (walk PoC), independent of the map.</summary>
    public List<Walker> Walkers { get; } = new();

    public IReadOnlyList<Settler> Settlers => _settlers;

    public long Tick { get; private set; }

    /// <summary>How often the watchdog had to let a settler walk through another one.</summary>
    public int PassThroughs { get; private set; }

    /// <summary>How often a waiting settler switched to a detour.</summary>
    public int Detours { get; private set; }

    /// <summary>
    /// Time spent per part of <see cref="Step"/> (Stopwatch ticks: summed up and the longest single tick)
    /// while not null; for finding what makes long games slow. Set to an empty dictionary to start measuring.
    /// </summary>
    public Dictionary<string, (long Total, long Max)>? StepProfile { get; set; }

    /// <summary>Victory and defeat: of a campaign mission (<see cref="MissionGoals"/>) or a free game (<see cref="ConquestGoals"/>); null without.</summary>
    public IGameGoals? Goals { get; set; }

    public void Step()
    {
        long mark = StepProfile is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        Commands.Apply(this);
        foreach (var walker in Walkers)
            walker.Tick();

        foreach (var settler in _settlers)
            if (settler.State == SettlerState.Stepping)
                AdvanceStep(settler);

        // Frozen by a spell: finish the current step, then stand still.
        foreach (var settler in _settlers)
            if (settler.WantsToMove && settler.FrozenUntil <= Tick)
                TryStartStep(settler);

        // After a pass-through a settler may stand on a field it does not own; claim it once free.
        if (Map is not null)
            foreach (var settler in _settlers)
                if (settler.State != SettlerState.Stepping && !settler.IsInside)
                    Claim(settler, settler.Position);
        Measure("movement", ref mark);

        if (Map is not null)
        {
            Logistics.Tick();
            Measure("logistics", ref mark);
            Recruitment.Tick();
            Measure("recruitment", ref mark);
            Construction.Tick();
            Measure("construction", ref mark);
            Production.Tick();
            Measure("production", ref mark);
            Military.Tick();
            Measure("military", ref mark);
            Events.Tick();
            Housing.Tick();
            Geology.Tick();
            Theft.Tick();
            Trade.Tick();
            Shipping.Tick();
            WarMachines.Tick();
            Pioneering.Tick();
            Magic.Tick();
            Measure("housing/geology/magic", ref mark);
            Idling.Tick();
            Trails.Tick();
            Measure("idling", ref mark);
            foreach (var ai in AiPlayers)
                if (Goals?.IsDefeated(ai.Player) != true)
                    ai.Tick();
            Measure("ai", ref mark);
            History.Tick();
            Goals?.Tick(this);
        }
        foreach (var settler in _settlers)
            if (settler.IsWorking || settler.IsStriking)
                settler.ActivityTicks++;

        Tick++;
        ClearDustClouds();
    }

    private void Measure(string part, ref long mark)
    {
        if (StepProfile is null)
            return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var (total, max) = StepProfile.GetValueOrDefault(part);
        StepProfile[part] = (total + now - mark, Math.Max(max, now - mark));
        mark = now;
    }

    /// <summary>Places a new settler on the nearest free walkable point.</summary>
    public Settler? Spawn(GridPoint near, Profession profession = Profession.None, int player = 0)
    {
        RequireMap();
        // A war machine wants room for all of it, but takes a single field rather than none.
        var free = (profession == Profession.WarMachine ? NearestFree(near, radius: 10, wide: true) : null) ?? NearestFree(near, radius: 10);
        if (free is not { } position)
            return null;
        var settler = new Settler(_nextSettlerId++, position) { Profession = profession, Player = player };
        // Geologists, scouts and pioneers can be fought: they have health as well.
        if (profession is Profession.Geologist or Profession.Scout or Profession.Pioneer)
            settler.Health = Geology.HealthOf(Military.Config, profession);
        _settlers.Add(settler);
        _settlersById[settler.Id] = settler;
        Claim(settler, position);
        return settler;
    }

    /// <summary>Places a new soldier (map loading, tests, debugging).</summary>
    public Settler? SpawnSoldier(GridPoint near, SoldierType type, int level = 1, int player = 0)
    {
        if (Spawn(near, Profession.Soldier, player) is not { } soldier)
            return null;
        soldier.SoldierType = type;
        soldier.Level = Math.Clamp(level, 1, 3);
        soldier.Health = MaxHealth(player, type, soldier.Level);
        return soldier;
    }

    /// <summary>Orders a settler to walk somewhere. Returns false if the target cannot be reached.</summary>
    public bool WalkTo(Settler settler, GridPoint destination)
    {
        settler.IsStrolling = false;
        // Plan from where the settler will be standing next.
        var from = settler.StepTarget;
        var path = PathFinder!.FindRoute(from, destination, out bool leg, ownLandOf: LandOf(settler), wide: settler.IsWide);
        // A wide one that gets no nearer than where it is cannot go there.
        if (path is null || path.Count == 0 && from != destination)
            return false;
        Follow(settler, path, destination);
        settler.PathIsLeg = leg;
        return true;
    }

    /// <summary>
    /// Where an ordered unit goes for <paramref name="target"/>: the target, or if it cannot get there the reachable
    /// point nearest to it (see <see cref="PathFinder.NearestReachable"/>); null if it can go nowhere.
    /// </summary>
    public GridPoint? CommandTarget(Settler settler, GridPoint target) =>
        PathFinder!.NearestReachable(settler.StepTarget, target, LandOf(settler));

    /// <summary>Whether the player's people can walk from a to b (on its own land with territory rules).</summary>
    internal bool Reachable(int player, GridPoint a, GridPoint b) => PathFinder!.AreConnected(a, b, TerritoryRules ? player : null);

    /// <summary>Soldiers, priests, geologists, scouts, pioneers, donkeys and war machines may go and stay outside the own land.</summary>
    internal static bool RoamsFreely(Settler settler) =>
        settler.Profession is Profession.Soldier or Profession.Priest or Profession.Geologist or Profession.Scout or Profession.Pioneer or Profession.Donkey
            or Profession.WarMachine;

    /// <summary>
    /// Whether the settler can walk to <paramref name="target"/> (see <see cref="LandOf"/>). Never for a
    /// stranded one: it takes no new task before it is back on the own land.
    /// </summary>
    internal bool Reachable(Settler settler, GridPoint target) =>
        !IsStranded(settler) && PathFinder!.AreConnected(settler.Position, target, LandOf(settler));

    /// <summary>Whether a stranded settler can get from where it stands to <paramref name="target"/> (over any land).</summary>
    internal bool CanFleeTo(Settler settler, GridPoint target) => PathFinder!.AreConnected(settler.Position, target, null);

    /// <summary>
    /// Outside on foreign or free land without being allowed there (with territory rules): its land was
    /// lost under its feet. It gives up its task, drops what it carries and flees home (<c>Idling</c>) —
    /// otherwise it would walk to its target straight across the foreign land.
    /// </summary>
    internal bool IsStranded(Settler settler) =>
        TerritoryRules && !RoamsFreely(settler) && !settler.IsInside && Map!.OwnerAt(settler.StepTarget) != settler.Player;

    /// <summary>
    /// Everyone but soldiers and geologists walks on the own land only (with territory rules). Somebody
    /// standing outside it (land lost under its feet) may walk anywhere, so it can come home.
    /// </summary>
    internal int? LandOf(Settler settler) =>
        TerritoryRules && !RoamsFreely(settler) && Map!.OwnerAt(settler.StepTarget) == settler.Player
            ? settler.Player
            : null;

    /// <summary>
    /// Walks a path planned elsewhere (e.g. around fighting soldiers). Like <see cref="WalkTo"/>, the
    /// path starts where the settler will be standing next.
    /// </summary>
    internal void Follow(Settler settler, IReadOnlyList<Direction> path, GridPoint destination)
    {
        settler.IsStrolling = false;
        settler.PathIsLeg = false;
        settler.Path.Clear();
        foreach (var step in path)
            settler.Path.Enqueue(step);
        // Already there (or arriving with the current step): nothing left to do.
        settler.Destination = path.Count > 0 || settler.State == SettlerState.Stepping ? destination : null;
        settler.WaitingTicks = 0;
        if (settler.State == SettlerState.Waiting)
            settler.State = SettlerState.Idle;
    }

    private void AdvanceStep(Settler settler)
    {
        settler.StepProgress += settler.StepSpeed;
        if (settler.StepProgress < StepUnits)
            return;

        var from = settler.Position;
        settler.Position = from.Neighbor(settler.Facing);
        // What is left over goes into the next step (faster ground does not round to whole ticks).
        settler.StepProgress = Math.Min(settler.StepProgress - StepUnits, settler.StepSpeed - 1);
        settler.State = SettlerState.Idle;
        Trails.Stepped(settler);
        Release(settler, from);
        // After a pass-through the field may still belong to the other settler; take it once free.
        Claim(settler, settler.Position);

        if (settler.Path.Count == 0)
        {
            // The end of a leg towards a far destination: on with the next one.
            if (settler.PathIsLeg && settler.Destination is { } destination && destination != settler.Position)
                Replan(settler, avoid: null);
            else
                settler.Destination = null;
        }
    }

    private void TryStartStep(Settler settler)
    {
        var map = Map!;
        var direction = settler.NextStep!.Value;
        var next = settler.Position.Neighbor(direction);

        // The world may have changed since planning (tree planted, building placed).
        if (settler.IsWide ? !PathFinder!.IsClear(next) : !map.IsWalkable(next))
        {
            Replan(settler, avoid: null);
            return;
        }
        if (settler.IsWide)
        {
            TryStartWideStep(settler, direction, next);
            return;
        }

        int occupantId = _occupant[map.Index(next)];
        if (occupantId == NoOccupant || occupantId == settler.Id)
        {
            StartStep(settler, direction);
            return;
        }

        var other = _settlersById[occupantId];
        bool otherFrozen = other.FrozenUntil > Tick;
        // Nobody swaps with or pushes aside a war machine: it is in the way.
        if (!otherFrozen && !other.IsWide && other.WantsToMove && other.Position.Neighbor(other.NextStep!.Value) == settler.Position)
        {
            Swap(settler, other);
            return;
        }

        if (!otherFrozen && !other.IsWide && IsIdle(other))
        {
            // Pushed aside: it starts at once and frees its field, so the settler need not stop.
            StepAside(other, keepFree: settler.Position);
            if (other.WantsToMove)
                TryStartStep(other);
            if (_occupant[map.Index(next)] == NoOccupant)
            {
                StartStep(settler, direction);
                return;
            }
        }

        Wait(settler, next);
    }

    private static bool IsIdle(Settler settler) =>
        settler.State == SettlerState.Idle && settler.Path.Count == 0 && !settler.IsWorking && !settler.HasTask;

    /// <summary>
    /// A wide settler's step: the three fields it newly covers must be free. Idle settlers there are pushed aside; it
    /// waits for the others (or after the watchdog's time drives through).
    /// </summary>
    private void TryStartWideStep(Settler settler, Direction direction, GridPoint next)
    {
        var map = Map!;
        GridPoint? blocked = null;
        foreach (var p in settler.Body(next))
        {
            int id = _occupant[map.Index(p)];
            if (id == NoOccupant || id == settler.Id)
                continue;
            var other = _settlersById[id];
            if (other.FrozenUntil <= Tick && !other.IsWide && IsIdle(other))
            {
                StepAside(other, keepFree: next);
                if (other.WantsToMove)
                    TryStartStep(other);
            }
            if (_occupant[map.Index(p)] is var holder && holder != NoOccupant && holder != settler.Id)
                blocked ??= p;
        }
        if (blocked is { } field)
            Wait(settler, field);
        else
            StartStep(settler, direction);
    }

    private void StartStep(Settler settler, Direction direction)
    {
        var map = Map!;
        settler.Path.Dequeue();
        settler.Facing = direction;
        // Leftover progress only carries on straight from the step before.
        if (settler.WaitingTicks > 0)
            settler.StepProgress = 0;
        settler.State = SettlerState.Stepping;
        settler.WaitingTicks = 0;
        // A wide one covers where it leaves until it is there.
        if (!settler.IsWide && _occupant[map.Index(settler.Position)] == settler.Id)
            _occupant[map.Index(settler.Position)] = NoOccupant;
        var target = settler.Position.Neighbor(direction);
        settler.StepSpeed = Trails.StepSpeed(target);
        // Soldier types walk at their own speed.
        if (settler.SoldierType is { } type && Military.Config.Soldiers[type].SpeedFactor is var factor and not 1)
            settler.StepSpeed = Math.Max(1, (int)Math.Round(settler.StepSpeed * factor));
        // A scout with stolen goods sneaks.
        if (settler is { Profession: Profession.Scout, Carrying: not null })
            settler.StepSpeed = Math.Max(1, (int)Math.Round(settler.StepSpeed * (Military.Config.Scouts ?? ScoutRules.Default).LoadedSpeed));
        if (settler.Profession == Profession.WarMachine && WarMachines.KindOf(settler) is { Speed: > 0 and not 1 } machine)
            settler.StepSpeed = Math.Max(1, (int)Math.Round(settler.StepSpeed * machine.Speed));
        Claim(settler, target);
    }

    /// <summary>Head-on: both start their step at once and hand over their fields.</summary>
    private void Swap(Settler a, Settler b)
    {
        var map = Map!;
        int fieldA = map.Index(a.Position), fieldB = map.Index(b.Position);
        StartStep(a, a.NextStep!.Value);
        StartStep(b, b.NextStep!.Value);
        _occupant[fieldA] = b.Id;
        _occupant[fieldB] = a.Id;
    }

    private void Wait(Settler settler, GridPoint blocked)
    {
        settler.State = SettlerState.Waiting;
        settler.WaitingTicks++;

        if (settler.WaitingTicks == ReplanAfterTicks && Tick - settler.LastDetourTick > DetourCooldownTicks)
        {
            var avoid = new HashSet<int> { Map!.Index(blocked) };
            // Around a war machine: around all of it.
            if (OccupantAt(blocked) is { IsWide: true } machine)
                foreach (var p in machine.Body(machine.Position).Concat(machine.Body(machine.StepTarget)))
                    if (Map.Contains(p))
                        avoid.Add(Map.Index(p));
            Replan(settler, avoid);
        }
        else if (settler.WaitingTicks >= PassThroughAfterTicks)
        {
            // A soldier does not walk through onto its destination while somebody stands there: it stops next to it
            // (orders take that as there). Walking in, a crowd sent to one place piled up on a few fields for good.
            if (settler.Profession == Profession.Soldier && settler.Path.Count == 1 && !settler.PathIsLeg
                && OccupantAt(blocked) is { State: not SettlerState.Stepping, WantsToMove: false })
            {
                settler.Path.Clear();
                settler.Destination = null;
                settler.State = SettlerState.Idle;
                settler.WaitingTicks = 0;
                return;
            }
            PassThroughs++;
            StartStep(settler, settler.NextStep!.Value);
        }
    }

    private void Replan(Settler settler, IReadOnlySet<int>? avoid)
    {
        if (settler.Destination is not { } destination)
        {
            settler.Path.Clear();
            return;
        }

        var path = PathFinder!.FindRoute(settler.Position, destination, out bool leg, avoid, LandOf(settler), settler.IsWide);
        if (path is null)
        {
            // No way around: keep waiting (the watchdog will push through) unless the
            // destination itself became unreachable.
            if (avoid is null)
            {
                settler.Path.Clear();
                settler.Destination = null;
                settler.State = SettlerState.Idle;
            }
            return;
        }
        if (avoid is not null && path.Count > settler.Path.Count + MaxDetourSteps)
            return;

        if (avoid is not null)
        {
            Detours++;
            settler.LastDetourTick = Tick;
        }
        settler.Path.Clear();
        foreach (var step in path)
            settler.Path.Enqueue(step);
        settler.PathIsLeg = leg;
    }

    /// <summary>Moves an idle settler one step to a free neighbor, preferring one away from <paramref name="keepFree"/>.</summary>
    private void StepAside(Settler settler, GridPoint keepFree)
    {
        var map = Map!;
        GridPoint? best = null;
        int bestDistance = -1;
        for (int d = 0; d < DirectionExtensions.Count; d++)
        {
            var candidate = settler.Position.Neighbor((Direction)d);
            if (candidate == keepFree || !map.IsWalkable(candidate) || _occupant[map.Index(candidate)] != NoOccupant)
                continue;
            int distance = PathFinder.Distance(candidate, keepFree);
            if (distance > bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        if (best is { } target)
            WalkTo(settler, target);
    }

    /// <summary>The nearest walkable point nobody stands on; <paramref name="wide"/>: with room for a war machine, all seven fields free.</summary>
    private GridPoint? NearestFree(GridPoint near, int radius, bool wide = false)
    {
        var map = RequireMap();
        for (int r = 0; r <= radius; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var p = new GridPoint(near.X + dx, near.Y + dy);
            if (PathFinder.Distance(near, p) == r && map.IsWalkable(p) && _occupant[map.Index(p)] == NoOccupant
                && (!wide || PathFinder!.IsClear(p) && Enumerable.Range(0, DirectionExtensions.Count).All(d => _occupant[map.Index(p.Neighbor((Direction)d))] == NoOccupant)))
                return p;
        }
        return null;
    }

    /// <summary>The settler takes the fields it covers at <paramref name="center"/> that nobody else holds.</summary>
    private void Claim(Settler settler, GridPoint center)
    {
        var map = Map!;
        foreach (var p in settler.Body(center))
            if (map.Contains(p) && _occupant[map.Index(p)] == NoOccupant)
                _occupant[map.Index(p)] = settler.Id;
    }

    /// <summary>The settler frees the fields it holds of those it covers at <paramref name="center"/>.</summary>
    private void Release(Settler settler, GridPoint center)
    {
        var map = Map!;
        foreach (var p in settler.Body(center))
            if (map.Contains(p) && _occupant[map.Index(p)] == settler.Id)
                _occupant[map.Index(p)] = NoOccupant;
    }

    /// <summary>
    /// Settlers standing on points that just became blocked (a building placed or levelled over them)
    /// are put on the nearest free field; otherwise they would be walled in for good.
    /// </summary>
    internal void MoveOffBlocked(IReadOnlyCollection<GridPoint> points)
    {
        var map = RequireMap();
        var blocked = points.ToHashSet();
        foreach (var settler in _settlers)
        {
            if (settler.IsInside || !blocked.Contains(settler.Position) && !blocked.Contains(settler.StepTarget))
                continue;
            Release(settler, settler.Position);
            Release(settler, settler.StepTarget);
            if (NearestFree(settler.Position, radius: 12) is not { } free)
                continue;
            var destination = settler.Destination;
            settler.State = SettlerState.Idle;
            settler.StepProgress = 0;
            settler.Path.Clear();
            settler.Destination = null;
            settler.Position = free;
            Claim(settler, free);
            if (destination is { } d && d != free)
                WalkTo(settler, d);
        }
    }

    /// <summary>Puts a settler straight onto a free point (spell), dropping its way.</summary>
    internal bool Teleport(Settler settler, GridPoint to)
    {
        var map = RequireMap();
        if (settler.IsInside || !map.IsWalkable(to) || _occupant[map.Index(to)] != NoOccupant)
            return false;
        Release(settler, settler.Position);
        Release(settler, settler.StepTarget);
        settler.State = SettlerState.Idle;
        settler.StepProgress = 0;
        settler.Path.Clear();
        settler.Destination = null;
        settler.IsStrolling = false;
        settler.Position = to;
        Claim(settler, to);
        return true;
    }

    /// <summary>Whether a settler stands on (or is stepping onto) the point.</summary>
    public bool IsOccupied(GridPoint p) => _occupant[RequireMap().Index(p)] != NoOccupant;

    /// <summary>The settler standing on (or stepping onto) the point, if any.</summary>
    internal Settler? OccupantAt(GridPoint p) =>
        _occupant[RequireMap().Index(p)] is var id and not NoOccupant ? _settlersById[id] : null;

    /// <summary>The settler goes into a building through the door it stands on.</summary>
    internal void EnterBuilding(Settler settler)
    {
        WalkTo(settler, settler.StepTarget);
        int index = RequireMap().Index(settler.Position);
        if (_occupant[index] == settler.Id)
            _occupant[index] = NoOccupant;
        settler.IsInside = true;
    }

    /// <summary>The settler goes into a building at once from wherever it is (taking it by the last strike).</summary>
    internal void EnterBuildingAt(Settler settler, GridPoint door)
    {
        var map = RequireMap();
        foreach (var p in new[] { settler.Position, settler.StepTarget })
            if (_occupant[map.Index(p)] == settler.Id)
                _occupant[map.Index(p)] = NoOccupant;
        settler.State = SettlerState.Idle;
        settler.StepProgress = 0;
        settler.Path.Clear();
        settler.Destination = null;
        settler.IsStrolling = false;
        settler.Position = door;
        settler.IsInside = true;
    }

    /// <summary>The settler comes out of the door (where it went in).</summary>
    internal void LeaveBuilding(Settler settler)
    {
        settler.IsInside = false;
        int index = RequireMap().Index(settler.Position);
        if (_occupant[index] == NoOccupant)
            _occupant[index] = settler.Id;
    }

    /// <summary>The settler dies: it leaves the world and frees its fields.</summary>
    internal void Remove(Settler settler)
    {
        if (settler.IsDead)
            return;
        RequireMap();
        Release(settler, settler.Position);
        Release(settler, settler.StepTarget);
        // A thief drops what he stole where he falls.
        if (settler is { Profession: Profession.Scout, Carrying: { } stolen })
        {
            settler.Carrying = null;
            DropGoods(settler.Position, stolen);
        }
        settler.IsDead = true;
        settler.Path.Clear();
        settler.Destination = null;
        _settlers.Remove(settler);
        _settlersById.Remove(settler.Id);
    }

    private GameMap RequireMap() => Map ?? throw new InvalidOperationException("This world has no map.");
}
