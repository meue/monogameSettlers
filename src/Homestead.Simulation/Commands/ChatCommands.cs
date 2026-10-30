using Homestead.Simulation.Military;

namespace Homestead.Simulation.Commands;

/// <summary>A chat line of the player (or of a computer player, in the voice the AI chat gave it).</summary>
public sealed record Say(int Player, string Text) : GameCommand(Player)
{
    internal override object? Apply(World world) => world.Say(Player, Text);
}

/// <summary>
/// What a computer player (<see cref="GameCommand.Player"/>) does on the AI chat's word: take up a challenge from
/// <see cref="Target"/> (all in: assault him), a truce with him, or help him as an ally. Result: whether it does.
/// </summary>
public sealed record AiReply(int Player, AiReplyKind Kind, int Target, long Ticks = 0) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        if (world.AiPlayers.FirstOrDefault(a => a.Player == Player) is not { } ai)
            return false;
        switch (Kind)
        {
            case AiReplyKind.Challenge:
                return ai.Challenge(Target);
            case AiReplyKind.Assault:
                ai.Assault(Target);
                return ai.AssaultOn == Target;
            case AiReplyKind.Truce:
                return ai.Truce(Target, Ticks);
            default:
                return ai.Help(Target, Ticks);
        }
    }
}

public enum AiReplyKind { Challenge, Assault, Truce, Help }

/// <summary>Cheats (only with cheats on; never in a multiplayer game).</summary>
public sealed record Cheat(int Player, CheatKind Kind, GridPoint At = default) : GameCommand(Player)
{
    internal override object? Apply(World world)
    {
        int Spawn(int count, Func<Settler?> spawn) => Enumerable.Range(0, count).Count(_ => spawn() is not null);
        switch (Kind)
        {
            case CheatKind.Swordsmen or CheatKind.Bowmen or CheatKind.Pikemen:
                var type = Kind == CheatKind.Swordsmen ? SoldierType.Swordsman : Kind == CheatKind.Bowmen ? SoldierType.Bowman : SoldierType.Pikeman;
                return Spawn(5, () => world.SpawnSoldier(At, type, world.Magic.SoldierLevel(Player, type), Player));
            case CheatKind.Priest:
                return Spawn(1, () => world.Magic.SpawnPriest(At, Player));
            case CheatKind.Geologists:
                return Spawn(2, () => world.Spawn(At, Profession.Geologist, Player));
            case CheatKind.Scouts:
                return Spawn(2, () => world.Spawn(At, Profession.Scout, Player));
            case CheatKind.Pioneers:
                return Spawn(2, () => world.Spawn(At, Profession.Pioneer, Player));
            case CheatKind.Carriers:
                return Spawn(5, () => world.Spawn(At, Profession.Carrier, Player));
            case CheatKind.WarMachines:
                return Spawn(2, () => world.WarMachines.Spawn(At, Player, world.PeopleOf(Player)));
            case CheatKind.Mana:
                world.Magic.AddMana(Player, 50);
                return world.Magic.Mana(Player);
            case CheatKind.MissionWon or CheatKind.MissionLost:
                if (world.Goals is null)
                    return false;
                world.Goals.Decide(Kind == CheatKind.MissionWon ? MissionOutcome.Won : MissionOutcome.Lost, world.Tick);
                return true;
            default:
                // Every computer opponent still in the game assaults the player with everything. Result: how many.
                int attackers = 0;
                foreach (var ai in world.AiPlayers.Where(ai => ai.Player != Player && world.Goals?.IsDefeated(ai.Player) != true))
                {
                    ai.Assault(Player);
                    if (ai.AssaultOn == Player)
                        attackers++;
                }
                return attackers;
        }
    }
}

public enum CheatKind { Swordsmen, Bowmen, Pikemen, Priest, Geologists, Scouts, Pioneers, Carriers, WarMachines, Mana, MissionWon, MissionLost, AttackMe }
