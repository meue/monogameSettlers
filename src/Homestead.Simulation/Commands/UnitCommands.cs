using Homestead.Simulation.Magic;

namespace Homestead.Simulation.Commands;

/// <summary>
/// The right click with soldiers or war machines: on an enemy building (not forced) they attack it – soldiers storm a
/// military building, war machines fire at it; bowmen alone take no such order –, otherwise (or if nobody can) they walk
/// there (forced: without fighting on the way). Result: (how many, whether they attack).
/// </summary>
public sealed record MoveUnits(int Player, int[] Units, GridPoint Target, bool Forced) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        var units = Own(world, Units);
        var soldiers = units.Where(s => s.Profession == Profession.Soldier).ToList();
        var machines = units.Where(s => s.Profession == Profession.WarMachine).ToList();
        if (!Forced && world.BuildingAt(Target) is { } target && target.Player != Player
            && (soldiers.Count > 0 ? world.Military.Attack(soldiers, target) : 0) + world.WarMachines.Attack(machines, target) is > 0 and var attacking)
            return (attacking, true);
        if (soldiers.Count > 0)
            world.Military.Move(soldiers, Target, Forced);
        if (machines.Count > 0)
            world.WarMachines.Move(machines, Target);
        return (soldiers.Count + machines.Count, false);
    }
}

/// <summary>All the player's free soldiers attack the building (key F). Result: how many attack.</summary>
public sealed record AttackWithAll(int Player, int Building) : GameCommand(Player)
{
    internal override object? Apply(World world) =>
        AnyBuilding(world, Building) is { } target && target.Player != Player ? world.Military.Attack(Player, target) : 0;
}

/// <summary>A waypoint after the current order (Shift+right click).</summary>
public sealed record AddWaypoint(int Player, int[] Units, GridPoint Target, bool Forced) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        var soldiers = Own(world, Units).Where(s => s.Profession == Profession.Soldier).ToList();
        world.Military.AddWaypoint(soldiers, Target, Forced);
        return soldiers.Count;
    }
}

/// <summary>Patrol between where the soldiers stand and the point (Alt+right click).</summary>
public sealed record PatrolUnits(int Player, int[] Units, GridPoint Target) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        var soldiers = Own(world, Units).Where(s => s.Profession == Profession.Soldier).ToList();
        world.Military.Patrol(soldiers, Target);
        return soldiers.Count;
    }
}

/// <summary>„Position halten“. Result: how many hold.</summary>
public sealed record HoldPosition(int Player, int[] Units) : GameCommand(Player)
{
    internal override object? Apply(World world) => world.Military.Hold(Own(world, Units));
}

/// <summary>Geologists search there, scouts and pioneers walk there. Result: how many were sent.</summary>
public sealed record SendSpecialists(int Player, int[] Units, GridPoint Target) : GameCommand(Player)
{
    internal override object? Apply(World world) => world.Geology.Send(Own(world, Units), Target);
}

/// <summary>Alt+right click with scouts or pioneers: steal there, take land there. Result: (stealing, claiming).</summary>
public sealed record SpecialistsWorkAt(int Player, int[] Units, GridPoint Target) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        var units = Own(world, Units);
        return (world.Theft.StealAt(units, Target), world.Pioneering.ClaimAt(units, Target));
    }
}

/// <summary>The scouts' button: steal from the nearest enemy goods. Result: how many go.</summary>
public sealed record Steal(int Player, int[] Units) : GameCommand(Player)
{
    internal override object? Apply(World world) => world.Theft.Steal(Own(world, Units));
}

/// <summary>The pioneers' button: take land where they stand. Result: how many work.</summary>
public sealed record ClaimLand(int Player, int[] Units) : GameCommand(Player)
{
    internal override object? Apply(World world) => world.Pioneering.Claim(Own(world, Units));
}

/// <summary>War machines go back to their works for ammunition. Result: how many go.</summary>
public sealed record ReloadMachines(int Player, int[] Units) : GameCommand(Player)
{
    internal override object? Apply(World world) => world.WarMachines.Reload(Own(world, Units));
}

/// <summary>Priests (or any one settler, without a profession of its own to follow) walk there. Result: how many walk.</summary>
public sealed record WalkSettlers(int Player, int[] Units, GridPoint Target) : GameCommand(Player)
{
    internal override object? Apply(World world) =>
        Own(world, Units).Count(s => world.CommandTarget(s, Target) is { } to && world.WalkTo(s, to));
}

/// <summary>Priests cast the spell at the point. Result: the <see cref="CastResult"/>.</summary>
public sealed record CastSpell(int Player, int[] Priests, GridPoint Target, Spell? Spell) : GameCommand(Player)
{
    internal override object? Apply(World world) => world.Magic.Cast(Own(world, Priests), Target, Spell);
}

/// <summary>Units go aboard an own ferry. Result: how many board.</summary>
public sealed record BoardShip(int Player, int[] Units, int Ship) : GameCommand(Player)
{
    internal override object? Apply(World world) =>
        OwnShips(world, [Ship]) is [var ship] ? world.Shipping.Board(Own(world, Units), ship) : 0;
}

/// <summary>Ships sail there. Result: how many sail.</summary>
public sealed record MoveShips(int Player, int[] Ships, GridPoint Target) : GameCommand(Player)
{
    internal override object? Apply(World world) => world.Shipping.Move(OwnShips(world, Ships), Target);
}

/// <summary>Ships put their passengers ashore. Result: how many go ashore.</summary>
public sealed record UnloadShips(int Player, int[] Ships) : GameCommand(Player)
{
    internal override object? Apply(World world) => OwnShips(world, Ships).Sum(world.Shipping.Unload);
}
