using Homestead.Simulation.Lockstep;

namespace Homestead.Server;

/// <summary>
/// The server as a web application: <c>/ws</c> takes the computers' WebSockets (<see cref="Hub"/>), <c>/</c> answers with a line
/// of status. The lockstep values come from the configuration (<c>Lockstep__TurnTicks</c>, <c>Lockstep__DelayTurns</c>,
/// <c>Lockstep__CheckTurns</c> as environment variables; defaults: <see cref="LockstepOptions"/>).
/// </summary>
public static class ServerApp
{
    /// <param name="urls">Where to listen (e.g. <c>http://127.0.0.1:0</c> in tests); null: as configured.</param>
    public static WebApplication Create(string[] args, string? urls = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (urls is not null)
            builder.WebHost.UseUrls(urls);
        var defaults = new LockstepOptions();
        var lockstep = builder.Configuration.GetSection("Lockstep");
        builder.Services.AddSingleton(new LockstepOptions(
            lockstep.GetValue("TurnTicks", defaults.TurnTicks),
            lockstep.GetValue("DelayTurns", defaults.DelayTurns),
            lockstep.GetValue("CheckTurns", defaults.CheckTurns)));
        builder.Services.AddSingleton<Hub>();

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/", (Hub hub) =>
            $"Homestead server {GameFingerprint.ProgramVersion}, protocol {GameFingerprint.CurrentProtocol}: {hub.Status}\n");
        app.Map("/ws", async (HttpContext context, Hub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(socket, context.RequestAborted);
        });
        return app;
    }
}
