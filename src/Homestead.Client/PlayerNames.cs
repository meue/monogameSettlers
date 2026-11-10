using Homestead.Simulation;

namespace Homestead.Client;

/// <summary>
/// How players are named in the game: a human's name in a multiplayer game, else the number, with the people („Anna (Römer)“,
/// „Spieler 2 (Römer)“), never the AI's character.
/// </summary>
public static class PlayerNames
{
    public static string Of(World world, int player)
    {
        string people = Lang.T("peoples." + world.PeopleOf(player));
        return player >= 0 && player < world.PlayerNames.Count && world.PlayerNames[player] is { Length: > 0 } name
            ? Lang.T("start.namedPeople", PlayerText.Clean(name, GameSetup.MaxNameLength), people)
            : Lang.T("start.playerPeople", player + 1, people);
    }
}
