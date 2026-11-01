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

    private sealed class Link(LocalRelay owner, int player) : ILockstepLink
    {
        private readonly List<string> _inbox = new();

        public void Send(string message)
        {
            foreach (string bundle in owner._relay.Receive(player, message))
                foreach (var link in owner._links.Values)
                    link._inbox.Add(bundle);
        }

        public IReadOnlyList<string> Receive()
        {
            var messages = _inbox.ToList();
            _inbox.Clear();
            return messages;
        }
    }
}
