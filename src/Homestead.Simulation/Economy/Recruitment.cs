using Homestead.Simulation.Pathfinding;

namespace Homestead.Simulation.Economy;

/// <summary>
/// Turns idle carriers into specialists. A trade that needs a tool (see <see cref="ProductionConfig.Tools"/>)
/// first sends the carrier to a pile holding one; the tool is reserved there so no carrier takes it away.
/// </summary>
/// <remarks>
/// The carrier becomes the specialist the moment it picks up the tool (or right away if the trade needs
/// none). Watchdog: no progress towards the tool for <see cref="StallTicks"/>, or the tool pile gone, ends
/// the recruitment as failed and the carrier stays a carrier.
/// </remarks>
public sealed class Recruitment
{
    public const int StallTicks = 400;

    private static readonly Share[] AllShares = Enum.GetValues<Share>();

    private readonly World _world;
    private readonly ProductionConfig _config;
    private readonly List<Recruit> _active = new();
    private readonly Dictionary<(int Player, Share Share), double> _shares = new();

    internal Recruitment(World world, ProductionConfig config)
    {
        _world = world;
        _config = config;
    }

    /// <summary>
    /// A share the player set (Siedler › Berufe): carriers kept at least, diggers/builders at most, of its
    /// carriers and specialists; the defaults come from <see cref="ProductionConfig"/>.
    /// </summary>
    public double ShareOf(int player, Share share) => _shares.TryGetValue((player, share), out double value) ? value : share switch
    {
        Share.Carriers => _config.KeptCarriers,
        Share.Diggers => _config.MaxDiggers,
        _ => _config.MaxBuilders,
    };

    /// <summary>The most the share may take with the other two as they are (the three together stay within 100 %).</summary>
    public double BudgetOf(int player, Share share)
    {
        double others = 0;
        foreach (var other in AllShares)
            if (other != share)
                others += ShareOf(player, other);
        return Math.Max(0, Math.Round(1 - others, 2));
    }

    /// <summary>Sets a share (clamped to 0–1, rounded to whole percent) so the three together stay within 100 %.</summary>
    public void SetShare(int player, Share share, double value) =>
        _shares[(player, share)] = Math.Min(Math.Round(Math.Clamp(value, 0, 1), 2), BudgetOf(player, share));

    /// <summary>The player's carriers and specialists (soldiers, priests, donkeys and war machines not counted), the base of the shares.</summary>
    public int Civilians(int player) =>
        _world.Settlers.Count(s => s.Player == player && s.Profession is not (Profession.Soldier or Profession.Priest or Profession.Donkey or Profession.WarMachine));

    /// <summary>Most diggers or builders the player may have (<see cref="Share.Diggers"/>, <see cref="Share.Builders"/>), at least one.</summary>
    public int MaxOf(int player, Share share) => Math.Max(1, (int)(ShareOf(player, share) * Civilians(player) + 1e-9));

    /// <summary>Recruitments whose carrier is still on the way to its tool.</summary>
    public IReadOnlyList<Recruit> Active => _active;

    /// <summary>
    /// Whether one more carrier of the player may become a specialist or soldier: a share of all its
    /// carriers and specialists (<see cref="ProductionConfig.KeptCarriers"/>) always stays a carrier.
    /// Carriers on their way to fetch a tool count as specialists already.
    /// </summary>
    /// <param name="reserved">Carriers already promised to a conversion that has not happened yet.</param>
    public bool MayConvert(int player, int reserved = 0) => Convertible(player) > reserved;

    /// <summary>How many more carriers of the player may become specialists or soldiers (see <see cref="MayConvert"/>).</summary>
    public int Convertible(int player)
    {
        int carriers = 0, specialists = 0;
        var recruited = _active.Count == 0 ? null : _active.Select(r => r.Settler).ToHashSet();
        foreach (var s in _world.Settlers)
        {
            if (s.Player != player || s.Profession is Profession.Soldier or Profession.Priest or Profession.Donkey or Profession.WarMachine)
                continue;
            if (s.IsCarrier && recruited?.Contains(s) != true)
                carriers++;
            else
                specialists++;
        }
        // Each conversion moves one settler from carriers to specialists; the total stays the same.
        return Math.Max(0, carriers - (int)Math.Ceiling(ShareOf(player, Share.Carriers) * (carriers + specialists) - 1e-9));
    }

    public Material? ToolFor(string? trade) => trade is not null && _config.Tools.TryGetValue(trade, out var tool) ? tool : null;

    /// <summary>Whether a tool of this kind is lying in a pile, not promised to anyone.</summary>
    public bool ToolAvailable(Material tool, int player) => _world.Piles.Any(p => p.Material == tool && p.Available > 0 && _world.BelongsTo(p, player));

    /// <summary>
    /// Starts turning the best-placed idle carrier into <paramref name="profession"/>/<paramref name="trade"/>
    /// for work near <paramref name="workplace"/>. Null if there is no carrier or no tool to be had now.
    /// </summary>
    internal Recruit? Start(Profession profession, string? trade, GridPoint workplace, int player)
    {
        var pathFinder = _world.PathFinder!;
        // A worker of the trade without a workplace (its building was torn down) comes first: it keeps
        // its trade and tool for good.
        if (trade is not null)
        {
            var jobless = _world.Settlers
                .Where(s => s.Profession == profession && s.Trade == trade && s.Workplace is null && !s.HasTask && !s.IsInside
                            && s.Player == player && _world.Reachable(s, workplace))
                .MinBy(s => PathFinder.Distance(s.Position, workplace));
            if (jobless is not null)
                return new Recruit(jobless, profession, trade, null) { State = RecruitState.Done };
        }

        if (!MayConvert(player))
            return null;
        var carriers = _world.Settlers
            .Where(s => s.IsAvailableCarrier && s.Player == player && _world.Reachable(s, workplace))
            .ToList();
        if (carriers.Count == 0)
            return null;

        if (ToolFor(trade) is not { } tool)
        {
            var settler = carriers.MinBy(s => PathFinder.Distance(s.Position, workplace))!;
            var recruit = new Recruit(settler, profession, trade, null);
            Convert(recruit);
            return recruit;
        }

        // The tool nearest to the workplace, then the carrier nearest to the tool.
        var pile = _world.Piles
            .Where(p => p.Material == tool && p.Available > 0 && _world.BelongsTo(p, player) && _world.Reachable(player, p.Position, workplace))
            .MinBy(p => PathFinder.Distance(p.Position, workplace));
        if (pile is null)
            return null;
        var carrier = carriers.MinBy(s => PathFinder.Distance(s.Position, pile.Position))!;
        var fetching = new Recruit(carrier, profession, trade, pile) { LastProgress = _world.Tick };
        pile.Reserved++;
        carrier.HasTask = true;
        _active.Add(fetching);
        if (carrier.Position != pile.Position && !_world.WalkTo(carrier, pile.Position))
            Fail(fetching);
        return fetching;
    }

    internal void Tick()
    {
        foreach (var recruit in _active.ToList())
            Advance(recruit);
    }

    private void Advance(Recruit r)
    {
        var settler = r.Settler;
        var pile = r.ToolPile!;
        if (!_world.HasPile(pile) || pile.Material != r.Tool || _world.IsStranded(settler))
        {
            Fail(r);
            return;
        }

        if (r.PickingUp)
        {
            if (--r.TicksLeft > 0)
                return;
            settler.IsWorking = false;
            settler.Activity = Activity.None;
            _world.TakeFromPile(pile);
            Convert(r);
            return;
        }

        if (settler.State == SettlerState.Stepping || settler.Destination is not null)
        {
            int remaining = settler.RemainingPath.Count();
            if (remaining < r.BestRemaining)
            {
                r.BestRemaining = remaining;
                r.LastProgress = _world.Tick;
            }
            if (_world.Tick - r.LastProgress > StallTicks)
                Fail(r);
            return;
        }

        if (settler.Position != pile.Position)
        {
            // Walk ended elsewhere: try again; the watchdog above limits retries.
            if (!_world.WalkTo(settler, pile.Position) || _world.Tick - r.LastProgress > StallTicks)
                Fail(r);
            return;
        }

        r.PickingUp = true;
        r.TicksLeft = Production.HandleTicks;
        settler.IsWorking = true;
        settler.Activity = Activity.PickingUp;
        settler.ActivityTicks = 0;
    }

    private void Convert(Recruit r)
    {
        _active.Remove(r);
        var settler = r.Settler;
        settler.Profession = r.Profession;
        settler.Trade = r.Trade;
        settler.Tool = r.Tool;
        settler.HasTask = false;
        r.State = RecruitState.Done;
    }

    /// <summary>The workplace no longer needs the recruit: the carrier stays a carrier.</summary>
    internal void Cancel(Recruit recruit)
    {
        if (_active.Contains(recruit))
            Fail(recruit);
    }

    private void Fail(Recruit r)
    {
        _active.Remove(r);
        var settler = r.Settler;
        if (r.ToolPile is { } pile && !r.PickingUp && _world.HasPile(pile))
            pile.Reserved = Math.Max(0, pile.Reserved - 1);
        settler.HasTask = false;
        settler.IsWorking = false;
        settler.Activity = Activity.None;
        if (settler.Destination is not null)
            _world.WalkTo(settler, settler.StepTarget);
        r.State = RecruitState.Failed;
    }
}

/// <summary>The shares a player sets in Siedler › Berufe (see <see cref="Recruitment.ShareOf"/>).</summary>
public enum Share
{
    Carriers,
    Diggers,
    Builders,
}

public enum RecruitState
{
    FetchingTool,
    Done,
    Failed,
}

/// <summary>A carrier on its way to become a specialist.</summary>
public sealed class Recruit
{
    internal Recruit(Settler settler, Profession profession, string? trade, Pile? toolPile)
    {
        Settler = settler;
        Profession = profession;
        Trade = trade;
        ToolPile = toolPile;
        Tool = toolPile?.Material;
    }

    public Settler Settler { get; }
    public Profession Profession { get; }
    public string? Trade { get; }
    public Pile? ToolPile { get; }
    public Material? Tool { get; }
    public RecruitState State { get; internal set; }
    internal bool PickingUp { get; set; }
    internal int TicksLeft { get; set; }
    internal int BestRemaining { get; set; } = int.MaxValue;
    internal long LastProgress { get; set; }
}
