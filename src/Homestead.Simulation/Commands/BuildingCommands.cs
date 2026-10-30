using Homestead.Simulation.Buildings;
using Homestead.Simulation.Economy;
using Homestead.Simulation.Military;

namespace Homestead.Simulation.Commands;

/// <summary>A construction site of the building type at the point. Result: the site's id, or null where it cannot stand.</summary>
public sealed record PlaceSite(int Player, BuildingDefinition Definition, GridPoint At) : GameCommand(Player)
{
    internal override object? Apply(World world) =>
        Definition.Civilization == world.PeopleOf(Player) && world.PlaceConstructionSite(Definition, Player, At) is { } site ? site.Id : null;
}

/// <summary>Tears the building (or site) down.</summary>
public sealed record Demolish(int Player, int Building) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        if (OwnBuilding(world, Building) is not { } building)
            return false;
        world.RemoveBuilding(building);
        return true;
    }
}

/// <summary>Production stops or goes on.</summary>
public sealed record SetPaused(int Player, int Building, bool Paused) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        if (OwnBuilding(world, Building) is { } building)
            world.SetPaused(building, Paused);
        return null;
    }
}

/// <summary>The building is served first, or like the others again.</summary>
public sealed record SetPriority(int Player, int Building, bool Priority) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        if (OwnBuilding(world, Building) is { } building)
            world.SetPriority(building, Priority);
        return null;
    }
}

/// <summary>The middle of a worker's area moves. Result: where it went, or null.</summary>
public sealed record SetWorkCenter(int Player, int Building, GridPoint Target) : GameCommand(Player)
{
    internal override object? Apply(World world) => OwnBuilding(world, Building) is { } building ? world.SetWorkCenter(building, Target) : null;
}

/// <summary>One soldier of the type more or less wanted in the military building. Result: whether it changed.</summary>
public sealed record ChangeWanted(int Player, int Building, SoldierType Type, int Delta) : GameCommand(Player)
{
    internal override object? Apply(World world) => OwnBuilding(world, Building) is { } building && world.Military.ChangeWanted(building, Type, Delta);
}

/// <summary>A storehouse takes only this good (null: every good).</summary>
public sealed record SetSpecialization(int Player, int Building, Material? Material) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        if (OwnBuilding(world, Building) is { } building)
            world.Logistics.SetSpecialization(building, Material);
        return null;
    }
}

/// <summary>A market sends this much of the good (<see cref="Trade.Unlimited"/>: all there is).</summary>
public sealed record SetTradeAmount(int Player, int Market, Material Material, int Amount) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        if (OwnBuilding(world, Market) is { } market)
            world.Trade.SetRemaining(market, Material, Amount);
        return null;
    }
}

/// <summary>A market sends that much more (or less) of the good.</summary>
public sealed record ChangeTradeAmount(int Player, int Market, Material Material, int Delta) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        if (OwnBuilding(world, Market) is { } market)
            world.Trade.ChangeRemaining(market, Material, Delta);
        return null;
    }
}

/// <summary>A market's route: its destination, or a stop on the way (null: removed). Result: whether it was set.</summary>
public sealed record SetTradeRoute(int Player, int Market, int? Stop, GridPoint? Point) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        if (OwnBuilding(world, Market) is not { } market)
            return false;
        return Stop is { } stop ? world.Trade.SetStop(market, stop, Point) : Point is { } destination && world.Trade.SetDestination(market, destination);
    }
}

/// <summary>A shipyard builds a ship of the kind. Result: whether it took the order.</summary>
public sealed record OrderShip(int Player, int Dockyard, ShipKind Kind) : GameCommand(Player)
{
    internal override object? Apply(World world) => OwnBuilding(world, Dockyard) is { } dockyard && world.Shipping.Order(dockyard, Kind);
}

/// <summary>A shipyard's dock at the clicked water. Result: whether it could be placed there.</summary>
public sealed record PlaceDock(int Player, int Dockyard, GridPoint At) : GameCommand(Player)
{
    internal override object? Apply(World world) => OwnBuilding(world, Dockyard) is { } dockyard && world.Shipping.PlaceDock(dockyard, At);
}

/// <summary>A war works builds (or stops building) a machine or ammunition. Result: whether it worked.</summary>
public sealed record OrderWarWork(int Player, int Works, WarWork Work, bool Cancel) : GameCommand(Player)
{
    internal override object? Apply(World world) =>
        OwnBuilding(world, Works) is { } works && (Cancel ? world.WarMachines.Cancel(works, Work) : world.WarMachines.Order(works, Work));
}
