using Homestead.Simulation.Commands;

namespace Homestead.Client;

/// <summary>
/// Gives an order to the simulation (see <see cref="GameCommand"/>; the client changes the world only so). It takes
/// effect on the next tick; <paramref name="report"/> turns its result into the message to show then (null: none).
/// </summary>
public delegate void IssueCommand(GameCommand command, Func<object?, string?>? report = null);
