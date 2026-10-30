using Homestead.Simulation.Economy;
using Homestead.Simulation.Military;

namespace Homestead.Simulation.Commands;

/// <summary>Distribution: the share of a good that goes to one kind of building.</summary>
public sealed record SetDistribution(int Player, Material Material, string Target, double Share) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        world.Logistics.SetShare(Player, Material, Target, Share);
        return null;
    }
}

/// <summary>Transport order: the good moves that many places (int.MinValue/MaxValue: to the top/bottom).</summary>
public sealed record MoveInTransportOrder(int Player, Material Material, int Places) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        world.Logistics.MoveInTransportOrder(Player, Material, Places);
        return null;
    }
}

/// <summary>Whether carriers take the good into storehouses.</summary>
public sealed record SetStored(int Player, Material Material, bool Stored) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        world.Logistics.SetStored(Player, Material, Stored);
        return null;
    }
}

/// <summary>Smiths: one more or less of the product ordered.</summary>
public sealed record ChangeOrdered(int Player, Material Product, int Delta) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        world.Production.ChangeOrdered(Player, Product, Delta);
        return null;
    }
}

/// <summary>Smiths: the product's share.</summary>
public sealed record SetProductionShare(int Player, Material Product, double Share) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        world.Production.SetShare(Player, Product, Share);
        return null;
    }
}

/// <summary>Professions: a share of the carriers (geologists, soldiers …).</summary>
public sealed record SetRecruitmentShare(int Player, Share Share, double Value) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        world.Recruitment.SetShare(Player, Share, Value);
        return null;
    }
}

/// <summary>
/// Professions: carriers become specialists, taken from <see cref="Among"/> in its order if given (those on the screen).
/// Result: (now, later, ids of the settlers converted or converting after their delivery).
/// </summary>
public sealed record ConvertCarriers(int Player, int Count, Profession Profession, int[]? Among) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        var chosen = new List<Settler>();
        IReadOnlyList<Settler>? among = Among is null ? null
            : Among.Select(id => world.Settlers.FirstOrDefault(s => s.Id == id)).OfType<Settler>().ToList();
        var (now, later) = world.Geology.ConvertCarriers(Player, Count, Profession, chosen, among);
        return (now, later, chosen.Select(s => s.Id).ToArray());
    }
}

/// <summary>Professions: pioneers become carriers again. Result: how many.</summary>
public sealed record PioneersToCarriers(int Player, int Count) : GameCommand(Player)
{
    internal override object? Apply(World world) => world.Geology.PioneersToCarriers(Player, Count);
}

/// <summary>Soldiers are trained only as planned, or whenever there are weapons.</summary>
public sealed record SetManualRecruitment(int Player, bool Manual) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        world.Military.SetManualRecruitment(Player, Manual);
        return null;
    }
}

/// <summary>Soldiers planned for manual recruitment, one type more or less.</summary>
public sealed record ChangePlanned(int Player, SoldierType Type, int Delta) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        world.Military.ChangePlanned(Player, Type, Delta);
        return null;
    }
}

/// <summary>A divine upgrade of a soldier type, paid with mana. Result: the <see cref="Magic.UpgradeResult"/>.</summary>
public sealed record UpgradeSoldiers(int Player, SoldierType Type) : GameCommand(Player)
{
    internal override object? Apply(World world) => world.Magic.Upgrade(Player, Type);
}
