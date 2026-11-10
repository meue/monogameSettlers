namespace Homestead.Simulation;

/// <summary>A line said in the global chat: by whom and at which tick.</summary>
public readonly record struct ChatMessage(long Tick, int Player, string Text);

/// <summary>
/// The game's global chat (<see cref="World.Chat"/>): what the players said, in order, saved with the game. Every player may
/// write; the client shows new lines as they come (<see cref="Messages"/> only grows). Lines are <see cref="PlayerText.Clean"/>ed:
/// they may come from any computer.
/// </summary>
public sealed class ChatLog
{
    /// <summary>The longest line kept; longer ones are cut.</summary>
    public const int MaxLength = 200;

    /// <summary>Lines a player may say per <see cref="FloodSeconds"/> in a multiplayer game; the log is saved and kept by every computer.</summary>
    public const int FloodLines = 10;

    public const int FloodSeconds = 10;

    private readonly List<ChatMessage> _messages = new();

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Adds what <paramref name="player"/> said (trimmed, cut at <see cref="MaxLength"/>); blank text says nothing.</summary>
    public ChatMessage? Say(int player, string text, long tick)
    {
        text = PlayerText.Clean(text, MaxLength);
        if (text.Length == 0)
            return null;
        var message = new ChatMessage(tick, player, text);
        _messages.Add(message);
        return message;
    }

    /// <summary>Whether the player has said <see cref="FloodLines"/> lines within the last <see cref="FloodSeconds"/> already.</summary>
    public bool IsFlooding(int player, long tick)
    {
        int lines = 0;
        for (int i = _messages.Count - 1; i >= 0 && _messages[i].Tick > tick - FloodSeconds * World.TicksPerSecond; i--)
            if (_messages[i].Player == player && ++lines >= FloodLines)
                return true;
        return false;
    }
}
