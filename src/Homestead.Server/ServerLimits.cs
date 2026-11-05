using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Homestead.Server;

/// <summary>
/// What a single computer may use of the server, from the configuration (section <c>Limits</c>, e.g. <c>Limits__MaxGames</c> as an
/// environment variable; the table with reasons: the server's README). Nobody logs in, so these are all there is between a flood
/// and the other players' games; an honest client stays far below every one of them.
/// </summary>
public sealed record ServerLimits
{
    /// <summary>WebSocket connections at once, in all and per address (per /64 network for IPv6, where one computer has plenty).</summary>
    public int MaxConnections { get; init; } = 1000;

    public int MaxConnectionsPerAddress { get; init; } = 16;

    /// <summary>Games at once. A connection opens one at a time.</summary>
    public int MaxGames { get; init; } = 200;

    /// <summary>Seconds a connection has to say hello.</summary>
    public double HandshakeSeconds { get; init; } = 10;

    /// <summary>Seconds without a message before a connection outside a running game is dropped.</summary>
    public double IdleSeconds { get; init; } = 1800;

    /// <summary>
    /// Seconds a running game waits for players while another has sent its turn: then they are dropped and the game goes on.
    /// A computer stops sending while its player reads the statistics, so this must not be short.
    /// </summary>
    public double StallSeconds { get; init; } = 180;

    /// <summary>Seconds without a turn after which a running game nobody sends to is closed.</summary>
    public double AbandonedSeconds { get; init; } = 1800;

    /// <summary>Budget per connection: messages (and frames) per second and the burst on top; lobby requests cost more than turns.</summary>
    public int MessagesPerSecond { get; init; } = 100;

    public int MessageBurst { get; init; } = 1000;

    /// <summary>Budget per connection: bytes received per second and the burst on top (at least the largest message).</summary>
    public int BytesPerSecond { get; init; } = 128 * 1024;

    public int ByteBurst { get; init; } = 1024 * 1024;

    /// <summary>Bytes waiting to go out to a connection; more: it does not read, and is dropped.</summary>
    public int MaxQueuedBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Seconds a single send may take.</summary>
    public double SendTimeoutSeconds { get; init; } = 20;

    /// <summary>
    /// Behind a reverse proxy every connection comes from its address: take the client's from <c>X-Forwarded-For</c>. Only with
    /// such a proxy – without one, anybody can write what they like into that header.
    /// </summary>
    public bool TrustForwardedHeaders { get; init; }

    /// <summary>The key the per-address limit counts by.</summary>
    public static string AddressKey(IPAddress? address)
    {
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? Convert.ToHexString(address.GetAddressBytes().AsSpan(0, 8))
            : address.ToString();
    }
}

/// <summary>A budget refilling over time. One reader at a time.</summary>
internal sealed class TokenBucket(double perSecond, double burst)
{
    private double _tokens = burst;
    private long _last = Stopwatch.GetTimestamp();

    /// <summary>Takes the amount; false if there is not that much left (it stays spent then: flooding on keeps it empty).</summary>
    public bool Take(double amount)
    {
        long now = Stopwatch.GetTimestamp();
        _tokens = Math.Min(burst, _tokens + (now - _last) / (double)Stopwatch.Frequency * perSecond) - amount;
        _last = now;
        return _tokens >= 0;
    }
}

/// <summary>Runs <see cref="Hub.Sweep"/> every second.</summary>
internal sealed class Housekeeping(Hub hub, ILogger<Housekeeping> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stopping))
                try
                {
                    hub.Sweep();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    log.LogError(e, "housekeeping failed");
                }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
