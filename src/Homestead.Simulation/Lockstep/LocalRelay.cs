namespace Homestead.Simulation.Lockstep;

/// <summary>
/// The server's <see cref="TurnRelay"/> in this process, one <see cref="ILockstepLink"/> per human player: what a link sends
/// comes back to every link (the sender's too) as the turns complete. For tests, and for games without a network.
/// </summary>
public sealed class LocalRelay
{
    private readonly TurnRelay _relay;
    private readonly Dictionary<int, Link> _links;

    public LocalRelay(IEnumerable<int> players, long firstTurn)
    {
        var all = players.Distinct().Order().ToArray();
        _relay = new TurnRelay(all, firstTurn);
        _links = all.ToDictionary(p => p, p => new Link(this, p));
    }

    public TurnRelay Relay => _relay;

    public ILockstepLink LinkOf(int player) => _links[player];

    /// <summary>The player leaves the game (as the server does it): the others go on, a computer player takes over.</summary>
    public void Remove(int player)
    {
        _links.Remove(player);
        foreach (string bundle in _relay.Remove(player))
            foreach (var link in _links.Values)
                link.Inbox.Add(bundle);
    }

    private sealed class Link(LocalRelay owner, int player) : ILockstepLink
    {
        public readonly List<string> Inbox = new();

        public void Send(string message)
        {
            foreach (string bundle in owner._relay.Receive(player, message))
                foreach (var link in owner._links.Values)
                    link.Inbox.Add(bundle);
        }

        public IReadOnlyList<string> Receive()
        {
            var messages = Inbox.ToList();
            Inbox.Clear();
            return messages;
        }
    }
}
