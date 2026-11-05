using Homestead.Simulation.Lockstep;
using Microsoft.AspNetCore.HttpOverrides;

namespace Homestead.Server;

/// <summary>
/// The server as a web application: <c>/ws</c> takes the computers' WebSockets (<see cref="Hub"/>), <c>/</c> answers with a line
/// of status. The lockstep values come from the configuration (<c>Lockstep__TurnTicks</c>, <c>Lockstep__DelayTurns</c>,
/// <c>Lockstep__CheckTurns</c> as environment variables; defaults: <see cref="LockstepOptions"/>), what one computer may use from
/// the section <c>Limits</c> (<see cref="ServerLimits"/>).
/// </summary>
public static class ServerApp
{
    /// <param name="urls">Where to listen (e.g. <c>http://127.0.0.1:0</c> in tests); null: as configured.</param>
    public static WebApplication Create(string[] args, string? urls = null)
    {
        // Settings (appsettings.json) next to the program, wherever it is started from.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        if (urls is not null)
            builder.WebHost.UseUrls(urls);
        var defaults = new LockstepOptions();
        var lockstep = builder.Configuration.GetSection("Lockstep");
        var options = new LockstepOptions(
            lockstep.GetValue("TurnTicks", defaults.TurnTicks),
            lockstep.GetValue("DelayTurns", defaults.DelayTurns),
            lockstep.GetValue("CheckTurns", defaults.CheckTurns));
        if (!options.IsValid)
            throw new InvalidOperationException($"bad lockstep options: {options}");
        var limits = builder.Configuration.GetSection("Limits").Get<ServerLimits>() ?? new ServerLimits();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(limits);
        builder.Services.AddSingleton<Hub>();
        builder.Services.AddHostedService<Housekeeping>();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // Only the small handshake request comes as HTTP.
            kestrel.Limits.MaxConcurrentConnections = limits.MaxConnections;
            kestrel.Limits.MaxConcurrentUpgradedConnections = limits.MaxConnections;
            kestrel.Limits.MaxRequestBodySize = 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 8 * 1024;
            kestrel.Limits.MaxRequestHeaderCount = 32;
            kestrel.Limits.MaxRequestLineSize = 2 * 1024;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });

        var app = builder.Build();
        if (limits.TrustForwardedHeaders)
        {
            // The proxy's own entry counts: it comes last, what a client wrote stands before it. No list of proxies: the
            // setting says there is one, and it alone can reach this server.
            var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
            forwarded.KnownNetworks.Clear();
            forwarded.KnownProxies.Clear();
            app.UseForwardedHeaders(forwarded);
        }
        // Keep-alive pongs missing that long: the other side is gone, whatever TCP thinks.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(30) });
        app.MapGet("/", (Hub hub) =>
            $"Homestead server {GameFingerprint.ProgramVersion}, protocol {GameFingerprint.CurrentProtocol}: {hub.Status}\n");
        app.Map("/ws", async (HttpContext context, Hub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            // The clients are programs: an Origin comes from a browser, sent there by any web page its user happens to open.
            if (context.Request.Headers.Origin.Count > 0)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            string address = ServerLimits.AddressKey(context.Connection.RemoteIpAddress);
            if (!hub.Admit(address))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await hub.RunAsync(socket, context.RequestAborted);
            }
            finally
            {
                hub.Release(address);
            }
        });
        return app;
    }
}
