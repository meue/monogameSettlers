using Homestead.Simulation.Buildings;
using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Economy;

/// <summary>
/// Matches offers (loose, output and storehouse piles) with requests (input piles with space) and hands
/// the transports to idle carriers: for each unit first the kind of target, then the nearest one
/// (see <see cref="ChooseTarget"/>).
/// </summary>
/// <remarks>
/// Every transport reserves a unit at the source and space at the target, so no two carriers go
/// for the same unit. A job can fail at any point; the watchdog rules guarantee that goods and
/// carriers never get lost (a construction site with the highest priority, <see cref="Building.HasPriority"/>, is served first and
/// takes material from carriers bringing it elsewhere, see <see cref="FreeForPriority"/>):
/// <list type="bullet">
/// <item>Source gone or unreachable before pick-up: the job is cancelled, reservations released.</item>
/// <item>Target gone or unreachable while carrying: the carrier drops the good where it stands,
/// where it becomes a loose pile and is offered again.</item>
/// <item>A carrier that makes no progress towards its destination for <see cref="StallTicks"/>
/// (e.g. two carriers taking turns pushing each other away) is treated the same way.</item>
/// </list>
/// </remarks>
public sealed class Logistics
{
    public const int PickUpTicks = 8;
    public const int DropTicks = 8;

    /// <summary>Ticks without getting closer to the destination before the watchdog steps in (20 s).</summary>
    public const int StallTicks = 400;

    /// <summary>Requests are matched every few ticks, not every tick.</summary>
    public const int DispatchInterval = 5;

    private readonly World _world;
    private readonly Dictionary<int, TransportJob> _jobs = new();
    private readonly Random _random;

    internal Logistics(World world)
    {
        _world = world;
        _random = new Random(7 + world.Seed);
    }

    public IReadOnlyCollection<TransportJob> Jobs => _jobs.Values;

    public int Delivered { get; private set; }

    public int Cancelled { get; private set; }

    /// <summary>Goods a carrier had to put down on the way (target gone, unreachable, timeout).</summary>
    public int DroppedOnTheWay { get; private set; }

    private readonly Dictionary<string, int> _problems = new();

    /// <summary>Why jobs were cancelled or goods dropped, with counts (for debugging and tests).</summary>
    public IReadOnlyDictionary<string, int> Problems => _problems;

    public TransportJob? JobOf(Settler settler) => _jobs.GetValueOrDefault(settler.Id);

    internal void Tick()
    {
        foreach (var job in _jobs.Values.ToList())
            Advance(job);

        if (_world.Tick % DispatchInterval == 0)
            Dispatch();
    }

    private void Dispatch()
    {
        var urgent = FreeForPriority();
        var idleCarriers = _world.Settlers
            .Where(s => s.IsAvailableCarrier && !s.IsStriking && !_jobs.ContainsKey(s.Id))
            .ToList();
        if (idleCarriers.Count == 0)
            return;
        // Goods higher in the player's transport order are matched first (Waren › Warentransport).
        var ranks = new Dictionary<int, Dictionary<Material, int>>();
        int Rank(Pile pile)
        {
            // Loose goods on no one's land (no territory rules): the order of a player with carriers free.
            int player = pile.Owner?.Player ?? (_world.Map is { } map ? map.OwnerAt(pile.Position) : null) ?? idleCarriers[0].Player;
            if (!ranks.TryGetValue(player, out var rank))
                ranks[player] = rank = TransportOrder(player).Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);
            return rank.GetValueOrDefault(pile.Material!.Value);
        }
        // Goods a site with the highest priority lacks before all others.
        var offers = _world.Piles.Where(p => p.Available > 0 && p.Material is not null)
            .OrderBy(p => urgent.Any(u => u.Material == p.Material && _world.BelongsTo(p, u.Player)) ? 0 : 1)
            .ThenBy(Rank).ThenBy(p => p.Id).ToList();
        // A stopped construction site gets nothing.
        var requests = _world.Piles.Where(p => p.Space > 0 && p.Owner is { Exists: true } owner
                                               && !(owner.IsPaused && owner.State != BuildingState.Finished)).ToList();
        if (offers.Count == 0 || requests.Count == 0)
            return;

        var pathFinder = _world.PathFinder!;
        // Kept up to date as carriers are given jobs, and the requests by material: with hundreds of piles
        // and carriers, working them out anew for every offer took long.
        var idleOf = new Dictionary<int, int>();
        foreach (var carrier in idleCarriers)
            idleOf[carrier.Player] = idleOf.GetValueOrDefault(carrier.Player) + 1;
        var players = idleOf.Keys.ToHashSet();
        var requestsFor = new Dictionary<Material, List<Pile>>();
        foreach (var offer in offers)
        {
            var material = offer.Material!.Value;
            if (!requestsFor.TryGetValue(material, out var wanting))
                requestsFor[material] = wanting = requests.Where(r => r.MayAccept(material)).ToList();
            while (offer.Available > 0 && idleCarriers.Count > 0)
            {
                if (ChooseTarget(offer, wanting, players) is not { } target)
                    break;
                int player = target.Owner!.Player;
                // The target is chosen; the goods come from the pile of that material nearest to it.
                var source = offers
                    .Where(o => o.Available > 0 && o.Material == material && o.Owner != target.Owner && _world.BelongsTo(o, player)
                                && Allowed(o, target, material))
                    .OrderBy(o => PathFinder.Distance(o.Position, target.Position))
                    .FirstOrDefault(o => _world.Reachable(player, o.Position, target.Position)) ?? offer;
                var carrier = idleCarriers
                    .Where(c => c.Player == player && _world.Reachable(c, source.Position))
                    .MinBy(c => PathFinder.Distance(c.Position, source.Position));
                if (carrier is null)
                    break;
                idleCarriers.Remove(carrier);
                if (--idleOf[player] == 0)
                    players.Remove(player);
                Start(carrier, source, target);
            }
            if (idleCarriers.Count == 0)
                break;
        }
    }

    /// <summary>A construction site's pile while the site has the highest priority (<see cref="Building.HasPriority"/>).</summary>
    private static bool IsUrgent(Pile pile) =>
        pile.Role == PileRole.Request && pile.Owner is { Exists: true, HasPriority: true, IsPaused: false } site && site.State != BuildingState.Finished;

    /// <summary>
    /// What construction sites with the highest priority lack of a material beyond what lies ready on the player's land
    /// is taken from carriers bringing it to the player's other construction sites, only as many as are lacking, the
    /// nearest to the site first: those on their way to pick it up let it be, those carrying it put it down where they
    /// are (in the original every carrier drops everything). One stopped on its way to the site is made up for the
    /// next time. Returns the players' materials such a site lacks.
    /// </summary>
    private HashSet<(int Player, Material Material)> FreeForPriority()
    {
        var lacking = new Dictionary<(int Player, Material Material), (int Units, GridPoint Site)>();
        foreach (var pile in _world.Piles)
            if (pile.Space > 0 && IsUrgent(pile) && pile.AcceptedMaterial is { } material)
            {
                var key = (pile.Owner!.Player, material);
                lacking[key] = (lacking.GetValueOrDefault(key).Units + pile.Space, pile.Position);
            }
        foreach (var ((player, material), (units, site)) in lacking)
        {
            int missing = units - _world.Piles.Where(p => p.Material == material && _world.BelongsTo(p, player)).Sum(p => p.Available);
            if (missing <= 0)
                continue;
            var others = _jobs.Values
                .Where(j => j.Material == material && j.Carrier.Player == player && j.Phase is TransportPhase.ToSource or TransportPhase.ToTarget
                            && j.Target.Role == PileRole.Request && j.Target.Owner is { HasPriority: false, State: not BuildingState.Finished })
                .OrderBy(j => j.Phase == TransportPhase.ToSource ? 0 : 1).ThenBy(j => PathFinder.Distance(j.Carrier.Position, site))
                .Take(missing).ToList();
            foreach (var job in others)
            {
                if (job.Phase == TransportPhase.ToSource)
                    Cancel(job, "for a priority site");
                else
                    Fail(job, "for a priority site");
            }
        }
        return lacking.Keys.ToHashSet();
    }

    private readonly Dictionary<int, List<Material>> _transportOrder = new();
    private readonly Dictionary<int, HashSet<Material>> _stored = new();

    /// <summary>
    /// The order in which the player's carriers take goods, highest first (Waren › Warentransport):
    /// every good, defaults from <see cref="ProductionConfig.TransportOrder"/>.
    /// </summary>
    public IReadOnlyList<Material> TransportOrder(int player) => OrderOf(player);

    private List<Material> OrderOf(int player)
    {
        if (!_transportOrder.TryGetValue(player, out var order))
        {
            var defaults = _world.Production.Config.TransportOrder;
            _transportOrder[player] = order = defaults.Concat(Enum.GetValues<Material>().Except(defaults)).ToList();
        }
        return order;
    }

    /// <summary>Moves a good up (negative) or down (positive) in the player's transport order, by that many places (clamped).</summary>
    public void MoveInTransportOrder(int player, Material material, int places)
    {
        var order = OrderOf(player);
        int from = order.IndexOf(material);
        int to = (int)Math.Clamp((long)from + places, 0, order.Count - 1);
        order.RemoveAt(from);
        order.Insert(to, material);
    }

    /// <summary>Whether the player's carriers bring the good to storehouses (Waren › Warentransport; defaults from <c>storage.materials</c>).</summary>
    public bool IsStored(int player, Material material) =>
        _stored.TryGetValue(player, out var stored) ? stored.Contains(material) : _world.Production.Config.IsStored(material);

    public void SetStored(int player, Material material, bool stored)
    {
        if (!_stored.TryGetValue(player, out var set))
            _stored[player] = set = Enum.GetValues<Material>().Where(m => _world.Production.Config.IsStored(m)).ToHashSet();
        if (stored)
            set.Add(material);
        else
            set.Remove(material);
    }

    private readonly Dictionary<int, Material> _specializations = new();

    /// <summary>
    /// The one good a storehouse takes, if the player specialized it (the storehouse's panel); then the
    /// general rule (<see cref="IsStored"/>) does not apply to it. Null: the general rule.
    /// </summary>
    public Material? SpecializationOf(Building storehouse) =>
        _specializations.TryGetValue(storehouse.Id, out var material) ? material : null;

    /// <summary>Specializes the storehouse on one good, or (null) gives it the general rule again.</summary>
    public void SetSpecialization(Building storehouse, Material? material)
    {
        if (material is { } m)
            _specializations[storehouse.Id] = m;
        else
            _specializations.Remove(storehouse.Id);
    }

    /// <summary>Whether carriers bring this good to that storehouse: its specialization, else the player's rule.</summary>
    public bool StoresHere(Building storehouse, Material material) =>
        SpecializationOf(storehouse) is { } only ? only == material : IsStored(storehouse.Player, material);

    /// <summary>The distribution target standing for all construction sites (Waren › Verteilung: „Baustelle“).</summary>
    public const string Construction = "construction";

    private readonly Dictionary<(int Player, Material Material, string Target), double> _shares = new();

    /// <summary>
    /// The player's share of a material for a target (a building name or <see cref="Construction"/>; 0–1,
    /// the bar in Waren › Verteilung). Defaults from <see cref="ProductionConfig.Distribution"/>.
    /// </summary>
    public double ShareOf(int player, Material material, string target) =>
        _shares.TryGetValue((player, material, target), out double share) ? share
        : _world.Production.Config.Distribution.TryGetValue(material, out var defaults) ? defaults.GetValueOrDefault(target)
        : 1;

    /// <summary>Targets with shares a player may have (far more than goods × buildings using them): a share is kept for any name.</summary>
    private const int MaxShares = 1024;

    /// <summary>Whether a share of the target can be set: at most so many targets, of a sensible name's length.</summary>
    public bool TakesShare(int player, Material material, string target) =>
        target.Length <= 64 && (_shares.ContainsKey((player, material, target)) || _shares.Keys.Count(k => k.Item1 == player) < MaxShares);

    /// <summary>Sets a share (clamped to 0–1).</summary>
    public void SetShare(int player, Material material, string target, double share) =>
        _shares[(player, material, target)] = Math.Clamp(share, 0, 1);

    /// <summary>
    /// Where a unit of the offer goes. Among the targets that want it now – construction sites (as one,
    /// <see cref="Construction"/>) and each type of building using it – one is drawn by the player's
    /// shares (<see cref="ShareOf"/>; share 0 gets nothing, unless the player set every target of the
    /// material to 0), then the nearest target of that kind with room; the storehouse only when no one
    /// else wants it. A construction site with the highest priority comes before all of them.
    /// </summary>
    private Pile? ChooseTarget(Pile offer, List<Pile> requests, HashSet<int> players)
    {
        var material = offer.Material!.Value;
        var candidates = requests.Where(r => r.Space > 0 && r.Accepts(material) && r.Owner != offer.Owner
                                             && players.Contains(r.Owner!.Player) && _world.BelongsTo(offer, r.Owner.Player)
                                             && Allowed(offer, r, material)).ToList();
        if (candidates.Count == 0)
            return null;
        // A construction site with the highest priority before everyone, whatever the shares.
        if (Nearest(candidates.Where(IsUrgent)) is { } urgent)
            return urgent;

        var users = candidates.Where(c => c.Role != PileRole.Storage).ToList();
        if (users.Count > 0 && !AllZero(users[0].Owner!.Player, material))
            users = users.Where(u => Share(u.Owner!.Player, TargetOf(u)) > 0).ToList();
        if (users.Count > 0)
        {
            int player = users[0].Owner!.Player;
            var types = users.Select(TargetOf).Distinct().ToList();
            string type = DrawType(types, t => Share(player, t));
            if (Nearest(users.Where(u => TargetOf(u) == type)) is { } drawn)
                return drawn;
            if (Nearest(users) is { } nearest)
                return nearest;
        }
        return Nearest(candidates.Where(c => c.Role == PileRole.Storage));

        Pile? Nearest(IEnumerable<Pile> targets) => targets
            .OrderBy(t => PathFinder.Distance(offer.Position, t.Position))
            .FirstOrDefault(t => _world.Reachable(t.Owner!.Player, offer.Position, t.Position));

        static string TargetOf(Pile request) => request.Owner!.State == BuildingState.Finished ? request.Owner.Definition.Name : Construction;

        // Markets and harbors get what the player sends there (their panel), not by the shares of Waren › Verteilung.
        double Share(int player, string target) =>
            (_world.Production.Config.Trade ?? TradeRules.Default) is var trade && (target == trade.Market || target == trade.Harbor)
                ? 1 : ShareOf(player, material, target);
    }

    private bool Allowed(Pile offer, Pile request, Material material)
    {
        if (request.Role == PileRole.Request && request.Owner is { State: BuildingState.Finished } owner && _world.Shipping.IsDockyard(owner))
            return _world.Shipping.Wants(request, material);
        if (request.Role == PileRole.Request && request.Owner is { State: BuildingState.Finished } works && _world.WarMachines.IsWorks(works))
            return _world.WarMachines.Wants(request, material);
        if (request.Role == PileRole.Trade)
            return _world.Trade.Wants(request, material) && !(request.Material is null && request.Owner!.Piles.Any(o =>
                o != request && o.Role == PileRole.Trade && o.Material == material && o.Space > 0));
        if (request.Role != PileRole.Storage)
            return true;
        if (!StoresHere(request.Owner!, material))
            return false;
        // Storehouse to storehouse would only shuffle goods around.
        if (offer.Role == PileRole.Storage)
            return false;
        // An empty storehouse pile starts a new material only if no pile there holds it with room
        // left; otherwise one material spreads over all piles and the storehouse is full.
        return !(request.Material is null && request.Owner!.Piles.Any(o =>
            o != request && o.Role == PileRole.Storage && o.Material == material && o.Space > 0));
    }

    /// <summary>Whether the player gave every target of the material share 0 (then they all get it alike).</summary>
    private bool AllZero(int player, Material material) =>
        _world.Production.Config.Distribution.TryGetValue(material, out var defaults)
        && defaults.Keys.Concat(_shares.Keys.Where(k => k.Player == player && k.Material == material).Select(k => k.Target))
            .All(target => ShareOf(player, material, target) <= 0);

    /// <summary>A target type drawn by share; types with share 0 only when all have 0.</summary>
    private string DrawType(List<string> types, Func<string, double> share)
    {
        double total = types.Sum(share);
        if (total <= 0)
            return types[_random.Next(types.Count)];
        double roll = _random.NextDouble() * total;
        foreach (string type in types)
        {
            roll -= share(type);
            if (roll < 0)
                return type;
        }
        return types[^1];
    }

    private void Start(Settler carrier, Pile source, Pile target)
    {
        var material = source.Material!.Value;
        source.Reserved++;
        target.Incoming++;
        target.Material ??= material;

        var job = new TransportJob(carrier, source, target, material, _world.Tick);
        _jobs[carrier.Id] = job;
        carrier.HasTask = true;
        if (!Walk(job, source.Position))
            Cancel(job, "source unreachable");
    }

    private void Advance(TransportJob job)
    {
        var carrier = job.Carrier;
        if (_world.IsStranded(carrier))
        {
            Fail(job, "land lost");
            return;
        }
        // Walking phases only: picking up and dropping are short and bounded anyway.
        if (job.Phase is TransportPhase.ToSource or TransportPhase.ToTarget && Stalled(job))
        {
            Fail(job, $"no progress in {job.Phase}");
            return;
        }

        switch (job.Phase)
        {
            case TransportPhase.ToSource:
                if (!_world.HasPile(job.Source) || job.Source.Count == 0)
                {
                    Cancel(job, "source gone");
                }
                else if (carrier.Destination is null && carrier.State != SettlerState.Stepping)
                {
                    if (carrier.Position == job.Source.Position)
                        BeginWork(job, TransportPhase.PickingUp);
                    else if (!Walk(job, job.Source.Position))
                        Cancel(job, "source unreachable");
                }
                break;

            case TransportPhase.PickingUp:
                if (--job.WorkTicksLeft > 0)
                    break;
                carrier.IsWorking = false;
                carrier.Activity = Activity.None;
                if (!_world.HasPile(job.Source) || job.Source.Count == 0)
                {
                    Cancel(job, "source gone");
                    break;
                }
                _world.TakeFromPile(job.Source);
                carrier.Carrying = job.Material;
                job.Phase = TransportPhase.ToTarget;
                if (!TargetUsable(job))
                    Fail(job, "target gone");
                else if (!Walk(job, job.Target.Position))
                    Fail(job, "target unreachable");
                break;

            case TransportPhase.ToTarget:
                if (!TargetUsable(job))
                {
                    Fail(job, "target gone");
                }
                else if (carrier.Destination is null && carrier.State != SettlerState.Stepping)
                {
                    if (carrier.Position == job.Target.Position)
                        BeginWork(job, TransportPhase.Dropping);
                    else if (!Walk(job, job.Target.Position))
                        Fail(job, "target unreachable");
                }
                break;

            case TransportPhase.Dropping:
                if (--job.WorkTicksLeft > 0)
                    break;
                carrier.IsWorking = false;
                carrier.Activity = Activity.None;
                if (!TargetUsable(job))
                {
                    Fail(job, "target gone");
                    break;
                }
                job.Target.Incoming--;
                job.Target.Count++;
                job.Target.Material = job.Material;
                if (job.Target.Role == PileRole.Trade)
                    _world.Trade.Arrived(job.Target, job.Material);
                carrier.Carrying = null;
                carrier.HasTask = false;
                _jobs.Remove(carrier.Id);
                Delivered++;
                break;
        }
    }

    /// <summary>Sends the carrier off and restarts progress tracking for the new destination.</summary>
    private bool Walk(TransportJob job, GridPoint destination)
    {
        if (!_world.WalkTo(job.Carrier, destination))
            return false;
        job.WalkingTo = destination;
        job.BestRemaining = int.MaxValue;
        job.LastProgress = _world.Tick;
        return true;
    }

    /// <summary>
    /// Progress = remaining path to the job's destination got shorter than ever before. Detours make
    /// it longer for a while, which is fine; walking somewhere else does not count at all.
    /// </summary>
    private bool Stalled(TransportJob job)
    {
        var carrier = job.Carrier;
        int remaining = carrier.Destination == job.WalkingTo ? carrier.RemainingPath.Count() : int.MaxValue;
        if (remaining < job.BestRemaining)
        {
            job.BestRemaining = remaining;
            job.LastProgress = _world.Tick;
        }
        return _world.Tick - job.LastProgress > StallTicks;
    }

    private bool TargetUsable(TransportJob job) => job.Target.Owner?.Exists != false && _world.HasPile(job.Target);

    private void BeginWork(TransportJob job, TransportPhase phase)
    {
        job.Phase = phase;
        job.WorkTicksLeft = phase == TransportPhase.PickingUp ? PickUpTicks : DropTicks;
        job.Carrier.IsWorking = true;
        job.Carrier.Activity = phase == TransportPhase.PickingUp ? Activity.PickingUp : Activity.Dropping;
        job.Carrier.ActivityTicks = 0;
    }

    /// <summary>Before pick-up: release everything, the carrier is free again.</summary>
    private void Cancel(TransportJob job, string reason)
    {
        if (job.Phase is TransportPhase.ToSource or TransportPhase.PickingUp)
            job.Source.Reserved = Math.Max(0, job.Source.Reserved - 1);
        ReleaseTarget(job);
        job.Carrier.IsWorking = false;
        job.Carrier.Activity = Activity.None;
        StopWalking(job.Carrier);
        job.Carrier.HasTask = false;
        _jobs.Remove(job.Carrier.Id);
        Cancelled++;
        _problems[reason] = _problems.GetValueOrDefault(reason) + 1;
    }

    /// <summary>Any phase: cancel, and if the carrier holds the good, put it down here.</summary>
    private void Fail(TransportJob job, string reason)
    {
        if (job.Carrier.Carrying is not { } material)
        {
            Cancel(job, reason);
            return;
        }
        ReleaseTarget(job);
        job.Carrier.IsWorking = false;
        job.Carrier.Activity = Activity.None;
        job.Carrier.Carrying = null;
        StopWalking(job.Carrier);
        _world.DropGoods(job.Carrier.Position, material);
        job.Carrier.HasTask = false;
        _jobs.Remove(job.Carrier.Id);
        DroppedOnTheWay++;
        _problems["dropped: " + reason] = _problems.GetValueOrDefault("dropped: " + reason) + 1;
    }

    private static void ReleaseTarget(TransportJob job)
    {
        if (job.Target.Incoming > 0)
            job.Target.Incoming--;
        job.Target.ResetMaterialIfEmpty();
    }

    private void StopWalking(Settler settler)
    {
        if (settler.Destination is not null)
            _world.WalkTo(settler, settler.StepTarget);
    }
}

public enum TransportPhase
{
    ToSource,
    PickingUp,
    ToTarget,
    Dropping,
}

public sealed class TransportJob
{
    internal TransportJob(Settler carrier, Pile source, Pile target, Material material, long started)
    {
        Carrier = carrier;
        Source = source;
        Target = target;
        Material = material;
        Started = started;
    }

    public Settler Carrier { get; }
    public Pile Source { get; }
    public Pile Target { get; }
    public Material Material { get; }
    public long Started { get; }
    internal GridPoint WalkingTo { get; set; }
    internal int BestRemaining { get; set; }
    internal long LastProgress { get; set; }
    public TransportPhase Phase { get; internal set; } = TransportPhase.ToSource;
    internal int WorkTicksLeft { get; set; }
}
