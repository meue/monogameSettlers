namespace Homestead.Simulation;

/// <summary>Victory and defeat of each player, and which players are out of the game.</summary>
public interface IGameGoals
{
    /// <summary>The player's outcome; open while undecided and for players the goals do not decide.</summary>
    MissionOutcome OutcomeOf(int player);

    /// <summary>The tick the player's outcome was decided; −1 while open.</summary>
    long DecidedAt(int player);

    void Tick(World world);

    /// <summary>Decides the game for a player (also the cheat menu's); a decided one stays as it is.</summary>
    void Decide(int player, MissionOutcome outcome, long tick);

    /// <summary>A defeated player's computer opponent gives up: its settlers and soldiers only go on by themselves.</summary>
    bool IsDefeated(int player);
}
