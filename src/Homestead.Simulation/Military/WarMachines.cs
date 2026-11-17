using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Military;

/// <summary>What a war machine works makes: a machine, or one load of ammunition for one.</summary>
public enum WarWork
{
    Machine,
    Ammo,
}

/// <summary>
/// A war machine's shot on its way (a catapult's stone, a ballista's bolt, a cannon ball; the gong's sound arrives at once):
/// it lands at <see cref="Tick"/> + <see cref="FlightTicks"/>, on the target building when it hits, beside it when not.
/// </summary>
public sealed record MachineShot(Settler Machine, Building Target, GridPoint From, GridPoint To, long Tick, int FlightTicks, bool Hits)
{
    public long Lands => Tick + FlightTicks;
}

/// <summary>Where a shot landed at <paramref name="Tick"/>: on <paramref name="Building"/> when it hit, else on the ground (for the eye).</summary>
public sealed record MachineImpact(GridPoint Position, Building? Building, long Tick);

/// <summary>
/// War machines (Kriegsmaschinen), one kind per people (<see cref="WarMachineRules.Kinds"/>): Romans the catapult, Egyptians
/// the ballista, Asians the cannon, Amazons the gong. The player orders machines and loads of ammunition at the people's
/// works (<see cref="Order"/>); carriers bring the material (<see cref="Wants"/>), the worker hammers it together in front of
/// the door unit by unit (<see cref="Production"/>, <see cref="IAssembly"/>). A machine drives out with a full load
/// (<see cref="WarMachineKind.Shots"/>), loads go on a stock at the works.
/// </summary>
/// <remarks>
/// Machines are driven like soldiers (<see cref="Move"/>) and fire at enemy military buildings only (<see cref="Attack"/>;
/// idle ones at any within range): every <see cref="WarMachineRules.CooldownTicks"/> a shot that hits with
/// <see cref="WarMachineKind.HitChance"/>. Every hit takes one of the building's <see cref="WarMachineRules.HitsOf"/>; the
/// last brings it down and its soldiers walk out alive. A machine without shots drives back to its works to load
/// (<see cref="Reload"/>). Melee soldiers fight machines like soldiers (they do not strike back); bowmen cannot hurt them.
/// </remarks>
public sealed class WarMachines : IAssembly
{
    private readonly World _world;
    private readonly Random _random;
    private readonly Dictionary<int, Works> _works = new();
    private readonly Dictionary<int, Job> _jobs = new();
    private readonly Dictionary<int, int> _hits = new();
    private readonly Dictionary<int, long> _mendedAt = new();
    private readonly List<MachineShot> _shots = new();
    private readonly List<MachineImpact> _impacts = new();

    /// <summary>How long a landed shot stays in <see cref="Impacts"/>.</summary>
    public const int ImpactTicks = World.TicksPerSecond;

    internal WarMachines(World world)
    {
        _world = world;
        _random = new Random(2203 + world.Seed);
    }

    public WarMachineRules Rules => _world.Military.Config.WarMachines;

    /// <summary>The people's war machine, if it has one.</summary>
    public WarMachineKind? KindOf(Civilization people) => Rules.Kinds.GetValueOrDefault(people);

    /// <summary>The war machine's kind (its people's).</summary>
    public WarMachineKind? KindOf(Settler machine) => KindOf(machine.People ?? _world.PeopleOf(machine.Player));

    /// <summary>Whether buildings of the name are a people's war machine works.</summary>
    public bool IsWorks(string building) => Rules.Kinds.Values.Any(k => k.Works == building);

    public bool IsWorks(Building building) => IsWorks(building.Definition.Name);

    /// <summary>Shots on their way.</summary>
    public IReadOnlyList<MachineShot> Shots => _shots;

    /// <summary>Shots that landed within the last <see cref="ImpactTicks"/>.</summary>
    public IReadOnlyList<MachineImpact> Impacts => _impacts;

    /// <summary>The player's war machines.</summary>
    public IEnumerable<Settler> Of(int player) =>
        _world.Settlers.Where(s => s.Profession == Profession.WarMachine && s.Player == player && !s.IsDead);

    // ---- the works ----

    /// <summary>What a work of the kind costs at the works.</summary>
    public IReadOnlyDictionary<Material, int> CostOf(Building works, WarWork work) =>
        KindFor(works) is { } kind ? work == WarWork.Machine ? kind.Cost : kind.Ammo : new Dictionary<Material, int>();

    /// <summary>Orders a works takes at once: far more than a game can use, the queue is kept and saved by every computer.</summary>
    private const int MaxOrders = 100;

    /// <summary>
    /// Orders a machine or a load of ammunition at a finished works of the player's people (not ammunition for the gong);
    /// they are made one after the other, in the order given. False if it cannot be ordered.
    /// </summary>
    public bool Order(Building works, WarWork work)
    {
        if (!works.Exists || works.State != BuildingState.Finished || KindFor(works) is not { } kind || work == WarWork.Ammo && kind.Shots <= 0
            || StateOf(works).Queue.Count >= MaxOrders)
            return false;
        StateOf(works).Queue.Add(work);
        return true;
    }

    /// <summary>Takes back the last order of the kind not started yet; false if there is none.</summary>
    public bool Cancel(Building works, WarWork work)
    {
        if (!_works.TryGetValue(works.Id, out var state))
            return false;
        int last = state.Queue.LastIndexOf(work);
        // The first one is being made once a unit of it was taken.
        if (last < 0 || last == 0 && state.Taken.Values.Sum() > 0)
            return false;
        state.Queue.RemoveAt(last);
        return true;
    }

    /// <summary>Orders of the kind at the works not done yet (the current one included).</summary>
    public int Ordered(Building works, WarWork work) => _works.TryGetValue(works.Id, out var state) ? state.Queue.Count(w => w == work) : 0;

    /// <summary>What the works is making now, if anything.</summary>
    public WarWork? Current(Building works) => _works.TryGetValue(works.Id, out var state) && state.Queue.Count > 0 ? state.Queue[0] : null;

    /// <summary>Loads of ammunition lying at the works.</summary>
    public int Loads(Building works) => _works.TryGetValue(works.Id, out var state) ? state.Loads : 0;

    /// <summary>Units of the material for the current work at the works so far: built in, on the way to it or in its pile.</summary>
    public int Delivered(Building works, Material material) =>
        _works.TryGetValue(works.Id, out var state) && state.Queue.Count > 0
            ? state.Taken.GetValueOrDefault(material) + works.Piles.Where(p => p.Role == PileRole.Request && p.Material == material).Sum(p => p.Count)
            : 0;

    /// <summary>How far the current work is (0-1: units built in of all it needs); null without one.</summary>
    public double? Progress(Building works)
    {
        if (Current(works) is not { } work)
            return null;
        int total = CostOf(works, work).Values.Sum();
        return total == 0 ? 1 : (double)_works[works.Id].Built.Values.Sum() / total;
    }

    /// <summary>Whether carriers should bring the material to the works' pile: the current work still needs it.</summary>
    internal bool Wants(Pile request, Material material)
    {
        if (request.Owner is not { } works || !_works.TryGetValue(works.Id, out var state) || state.Queue.Count == 0)
            return false;
        return request.Count + request.Incoming + state.Taken.GetValueOrDefault(material) < CostOf(works, state.Queue[0]).GetValueOrDefault(material);
    }

    Pile? IAssembly.NextUnit(Building works)
    {
        if (!_works.TryGetValue(works.Id, out var state) || state.Queue.Count == 0)
            return null;
        var cost = CostOf(works, state.Queue[0]);
        return works.Piles.FirstOrDefault(p => p.Role == PileRole.Request && p.Count > 0 && p.Material is { } m
                                               && state.Taken.GetValueOrDefault(m) < cost.GetValueOrDefault(m));
    }

    void IAssembly.Take(Building works, Material material, bool back)
    {
        if (_works.TryGetValue(works.Id, out var state))
            state.Taken[material] = state.Taken.GetValueOrDefault(material) + (back ? -1 : 1);
    }

    void IAssembly.Build(Building works, Material material) => Build(works, material);

    private void Build(Building works, Material material)
    {
        if (!_works.TryGetValue(works.Id, out var state) || state.Queue.Count == 0)
            return;
        state.Built[material] = state.Built.GetValueOrDefault(material) + 1;
        if (CostOf(works, state.Queue[0]).All(c => state.Built.GetValueOrDefault(c.Key) >= c.Value))
            Complete(works, state);
    }

    /// <summary>The current work is done: a machine rolls out, or a load goes on the stock.</summary>
    private void Complete(Building works, Works state)
    {
        var work = state.Queue[0];
        state.Queue.RemoveAt(0);
        state.Taken.Clear();
        state.Built.Clear();
        if (work == WarWork.Ammo)
            state.Loads++;
        else
            Make(works);
    }

    /// <summary>A machine of the works' people rolls out next to its work spot, loaded; null if there is no room.</summary>
    private Settler? Make(Building works)
    {
        var spot = WorkSpot(works) is { } work ? work.Spot.Neighbor(work.Facing) : works.Door;
        return Spawn(spot, works.Player, works.Definition.Civilization);
    }

    /// <summary>
    /// Places a new war machine of the people (works, tests, cheats), with <paramref name="ammo"/> shots (null: loaded); null
    /// without room or without a machine for the people.
    /// </summary>
    public Settler? Spawn(GridPoint near, int player, Civilization people, int? ammo = null)
    {
        if (KindOf(people) is not { } kind || _world.Spawn(near, Profession.WarMachine, player) is not { } machine)
            return null;
        machine.People = people;
        machine.Health = kind.Health;
        machine.Ammo = Math.Clamp(ammo ?? kind.Shots, 0, Math.Max(0, kind.Shots));
        machine.Facing = Direction.SouthEast;
        return machine;
    }

    /// <summary>Makes the works' current work at once, without material (cheats, captures); false as <see cref="Order"/>.</summary>
    public bool Finish(Building works, WarWork work)
    {
        if (!Order(works, work))
            return false;
        var state = _works[works.Id];
        // The new order is last in line: what was ordered before is finished with it.
        while (state.Queue.Count > 0)
        {
            var cost = CostOf(works, state.Queue[0]);
            foreach (var (material, count) in cost)
                state.Built[material] = count;
            Complete(works, state);
        }
        return true;
    }

    /// <summary>Where the worker hammers (in front of the door, facing away from it) and which way he looks.</summary>
    internal (GridPoint Spot, Direction Facing)? WorkSpot(Building works) => (works.Door.Neighbor(Direction.SouthEast), Direction.SouthEast);

    (GridPoint Spot, Direction Facing)? IAssembly.WorkSpot(Building works) => WorkSpot(works);

    int IAssembly.BuildTicks => Rules.BuildTicks;

    string IAssembly.Idle(Building works) => Current(works) is null ? "nothing ordered" : $"waiting for {WorkName(works)} material";

    string IAssembly.Work(Building works) => WorkName(works);

    private string WorkName(Building works) => Current(works) == WarWork.Ammo ? "ammunition" : "war machine";

    private WarMachineKind? KindFor(Building works) =>
        KindOf(works.Definition.Civilization) is { } kind && kind.Works == works.Definition.Name ? kind : null;

    private Works StateOf(Building works)
    {
        if (!_works.TryGetValue(works.Id, out var state))
            _works[works.Id] = state = new Works();
        return state;
    }

    // ---- the machines ----

    /// <summary>
    /// Orders the player's war machines to a point; they spread over free fields around it and fire at enemy military
    /// buildings that come within range once there.
    /// </summary>
    public int Move(IEnumerable<Settler> machines, GridPoint destination)
    {
        var list = Commandable(machines);
        if (list.Count > 0 && _world.CommandTarget(list[0], destination) is { } reachable)
            destination = reachable;
        var spots = FreeSpotsAround(destination, list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var spot = i < spots.Count ? spots[i] : destination;
            var machine = list[i];
            _jobs[machine.Id] = new Job { MoveTo = spot };
            if (!_world.WalkTo(machine, _world.CommandTarget(machine, spot) ?? spot))
                _jobs.Remove(machine.Id);
        }
        return list.Count;
    }

    /// <summary>Orders war machines to fire at an enemy military building: they drive within range and shoot until it falls. Returns how many go.</summary>
    public int Attack(IEnumerable<Settler> machines, Building target)
    {
        if (!IsTarget(target, null))
            return 0;
        var list = Commandable(machines).Where(m => IsTarget(target, m.Player)).ToList();
        foreach (var machine in list)
            _jobs[machine.Id] = new Job { Target = target };
        return list.Count;
    }

    /// <summary>
    /// Sends war machines that use ammunition to the nearest own works of their kind with a load (that one they take, full
    /// shots again). Returns how many go.
    /// </summary>
    public int Reload(IEnumerable<Settler> machines)
    {
        int sent = 0;
        foreach (var machine in Commandable(machines))
        {
            if (KindOf(machine) is not { Shots: > 0 } || ReloadAt(machine) is not { } works)
                continue;
            _jobs[machine.Id] = new Job { Reload = works };
            sent++;
        }
        return sent;
    }

    /// <summary>The own works of the machine's kind with a load nearest to it, if any.</summary>
    public Building? ReloadAt(Settler machine) =>
        KindOf(machine) is { } kind
            ? _world.Buildings.Where(b => b.Exists && b.Player == machine.Player && b.State == BuildingState.Finished
                                          && b.Definition.Name == kind.Works && Loads(b) > 0)
                .MinBy(b => PathFinder.Distance(machine.Position, b.Door))
            : null;

    /// <summary>The building the machine is ordered to fire at (or fires at by itself), if any.</summary>
    public Building? TargetOf(Settler machine) => _jobs.TryGetValue(machine.Id, out var job) ? job.Target : null;

    /// <summary>Hits the military building has taken from war machines (and not mended yet).</summary>
    public int HitsTaken(Building building) => _hits.GetValueOrDefault(building.Id);

    /// <summary>How badly the building is damaged: hits taken of those it stands, 0 (whole) to below 1.</summary>
    public double DamageOf(Building building) =>
        _hits.TryGetValue(building.Id, out int hits) ? (double)hits / Rules.HitsOf(building.Definition.Name) : 0;

    /// <summary>An existing military building of an enemy of the player (null: of anybody), finished, that war machines fire at.</summary>
    public bool IsTarget(Building building, int? player) =>
        building.Exists && building.State == BuildingState.Finished && building.Definition.Occupiers.Count > 0
        && (player is null || !_world.AreAllies(building.Player, player.Value));

    private List<Settler> Commandable(IEnumerable<Settler> machines) =>
        machines.Where(m => m.Profession == Profession.WarMachine && !m.IsDead).Distinct().ToList();

    /// <summary>
    /// Points with room for a machine nearest to <paramref name="center"/>, one per machine, three fields apart so that they
    /// do not cover each other.
    /// </summary>
    private List<GridPoint> FreeSpotsAround(GridPoint center, int count)
    {
        var pathFinder = _world.PathFinder!;
        var spots = new List<GridPoint>();
        for (int r = 0; r <= 16 && spots.Count < count; r++)
            foreach (var p in PathFinder.Circle(center, r))
                if (spots.Count < count && PathFinder.Distance(center, p) == r && pathFinder.IsClear(p)
                    && _world.OccupantAt(p) is not { Profession: not Profession.WarMachine } && spots.All(s => PathFinder.Distance(s, p) >= 3))
                    spots.Add(p);
        return spots;
    }

    internal void Tick()
    {
        if (_world.Tick % World.TicksPerSecond == 0)
            foreach (var id in _works.Keys.Where(id => !_world.Buildings.Any(b => b.Id == id && b.Exists)).ToList())
                _works.Remove(id); // torn down or lost: its orders and loads go with it
        Land();
        _impacts.RemoveAll(i => i.Tick + ImpactTicks <= _world.Tick);
        if (_world.Tick % World.TicksPerSecond == 0)
            Mend();
        foreach (var machine in _world.Settlers.Where(s => s.Profession == Profession.WarMachine && !s.IsDead).ToList())
            Advance(machine);
    }

    /// <summary>Damaged buildings mend one hit every <see cref="WarMachineRules.RepairTicks"/> after the last hit (or mend).</summary>
    private void Mend()
    {
        foreach (var (id, hits) in _hits.ToList())
        {
            if (_world.Tick - _mendedAt.GetValueOrDefault(id) < Rules.RepairTicks)
                continue;
            _mendedAt[id] = _world.Tick;
            if (hits <= 1)
            {
                _hits.Remove(id);
                _mendedAt.Remove(id);
            }
            else
                _hits[id] = hits - 1;
        }
    }

    private void Advance(Settler machine)
    {
        if (machine.FrozenUntil > _world.Tick)
            return;
        bool standing = machine.State != SettlerState.Stepping && machine.Path.Count == 0;
        if (!_jobs.TryGetValue(machine.Id, out var job))
        {
            // Standing around: fire at whatever enemy military building is within range.
            if (standing && KindOf(machine) is { } kind && HasShots(machine, kind) && NearestTarget(machine, kind) is { } near)
                Fire(machine, near, kind);
            return;
        }
        if (job.Reload is { } works)
        {
            if (!works.Exists || Loads(works) == 0)
                _jobs.Remove(machine.Id);
            else if (PathFinder.Distance(machine.StepTarget, works.Door) <= Rules.ReloadDistance)
            {
                if (!standing)
                    _world.WalkTo(machine, machine.StepTarget);
                else
                {
                    _works[works.Id].Loads--;
                    machine.Ammo = KindOf(machine)?.Shots ?? 0;
                    _jobs.Remove(machine.Id);
                }
            }
            else if (standing && !_world.WalkTo(machine, works.Door))
                _jobs.Remove(machine.Id);
            return;
        }
        if (job.Target is { } target)
        {
            if (!IsTarget(target, machine.Player) || KindOf(machine) is not { } kind || !HasShots(machine, kind))
            {
                _jobs.Remove(machine.Id);
                if (!standing)
                    _world.WalkTo(machine, machine.StepTarget);
                return;
            }
            if (PathFinder.Distance(machine.StepTarget, target.Position) <= kind.Range)
            {
                if (!standing)
                    _world.WalkTo(machine, machine.StepTarget); // in range: stop at the next field
                else
                    Fire(machine, target, kind);
            }
            else if (standing && !_world.WalkTo(machine, _world.CommandTarget(machine, target.Door) ?? target.Door))
                _jobs.Remove(machine.Id);
            return;
        }
        if (standing)
            _jobs.Remove(machine.Id); // there
    }

    private static bool HasShots(Settler machine, WarMachineKind kind) => kind.Shots <= 0 || machine.Ammo > 0;

    private Building? NearestTarget(Settler machine, WarMachineKind kind) =>
        _world.Buildings.Where(b => IsTarget(b, machine.Player) && PathFinder.Distance(machine.Position, b.Position) <= kind.Range)
            .MinBy(b => PathFinder.Distance(machine.Position, b.Position));

    /// <summary>A shot at the building once the machine is ready again; the gong's lands at once.</summary>
    private void Fire(Settler machine, Building target, WarMachineKind kind)
    {
        machine.Facing = Military.FacingTowards(machine.Position, target.Position);
        if (_world.Tick < machine.LastShotTick + Rules.CooldownTicks)
            return;
        machine.LastShotTick = _world.Tick;
        if (kind.Shots > 0)
            machine.Ammo--;
        bool hits = _random.NextDouble() < kind.HitChance;
        var to = hits ? target.Position : Miss(target.Position);
        _shots.Add(new MachineShot(machine, target, machine.Position, to, _world.Tick, kind.FlightTicks, hits));
        _world.Cue(new SoundCue(SoundCueKind.MachineShot, machine.Position, machine.Player, People: machine.People));
        _world.Events.Raise("buildingAttacked", target.Player, target.Position, building: target);
        if (kind.FlightTicks <= 0)
            Land();
    }

    /// <summary>Where a miss lands: two or three fields beside the target.</summary>
    private GridPoint Miss(GridPoint target)
    {
        var p = target;
        int steps = 2 + _random.Next(2);
        var direction = (Direction)_random.Next(DirectionExtensions.Count);
        for (int i = 0; i < steps; i++)
            p = p.Neighbor(direction);
        return _world.Map!.Contains(p) ? p : target;
    }

    /// <summary>Shots that land now: a hit takes one of the building's hits, the last brings it down (its soldiers come out).</summary>
    private void Land()
    {
        foreach (var shot in _shots.Where(s => s.Lands <= _world.Tick).ToList())
        {
            _shots.Remove(shot);
            bool hits = shot.Hits && shot.Target.Exists;
            _impacts.Add(new MachineImpact(shot.To, hits ? shot.Target : null, _world.Tick));
            if (!hits)
                continue;
            _world.Cue(new SoundCue(SoundCueKind.MachineHit, shot.Target.Position, shot.Target.Player));
            _mendedAt[shot.Target.Id] = _world.Tick;
            int taken = _hits[shot.Target.Id] = _hits.GetValueOrDefault(shot.Target.Id) + 1;
            if (taken < Rules.HitsOf(shot.Target.Definition.Name))
                continue;
            _hits.Remove(shot.Target.Id);
            _mendedAt.Remove(shot.Target.Id);
            int owner = shot.Target.Player;
            _world.Events.Raise("buildingDestroyed", owner, shot.Target.Position, building: shot.Target);
            _world.RemoveBuilding(shot.Target);
        }
    }

    /// <summary>The machine is gone (destroyed): its orders with it.</summary>
    internal void Forget(Settler machine) => _jobs.Remove(machine.Id);

    private sealed class Works
    {
        public List<WarWork> Queue { get; } = new();
        public Dictionary<Material, int> Taken { get; } = new();
        public Dictionary<Material, int> Built { get; } = new();
        public int Loads { get; set; }
    }

    private sealed class Job
    {
        public GridPoint? MoveTo { get; init; }
        public Building? Target { get; init; }
        public Building? Reload { get; init; }
    }
}
