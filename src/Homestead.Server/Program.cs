using Homestead.Server;

// The multiplayer server: lobby and turn relay for lockstep games (docs/MULTIPLAYER.md). Listens on ASPNETCORE_URLS /
// ASPNETCORE_HTTP_PORTS (the container: 8080); WebSocket at /ws, status at /.
await ServerApp.Create(args).RunAsync();
