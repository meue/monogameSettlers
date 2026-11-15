using System.Text.Json;
using System.Text.Json.Nodes;
using Homestead.Assets;
using Homestead.Simulation.Lockstep;

namespace Homestead.Client;

/// <summary>The name and server last used for multiplayer games: the <c>multiplayer</c> section of <c>settings.json</c>.</summary>
public sealed record MultiplayerSettings
{
    private const string Section = "multiplayer";

    /// <summary>Host and port only: <see cref="ServerConnection.AddressOf"/> makes the WebSocket address of it.</summary>
    public const string DefaultServer = "localhost:8080";

    public string Name { get; init; } = "";

    public string Server { get; init; } = DefaultServer;

    public static MultiplayerSettings Load(string? path = null)
    {
        var settings = new MultiplayerSettings();
        if (SettingsFile.ReadSection(Section, path) is not { ValueKind: JsonValueKind.Object } section)
            return settings;
        string? Text(string name) =>
            section.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return settings with
        {
            Name = Text("name") ?? settings.Name,
            Server = Text("server") is { } server ? ServerConnection.ShortAddress(server) : settings.Server,
        };
    }

    public void Save(string? path = null) =>
        SettingsFile.WriteSection(Section, new JsonObject { ["name"] = Name, ["server"] = Server }, path);
}
