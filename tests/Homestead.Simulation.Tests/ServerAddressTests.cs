using Homestead.Simulation.Lockstep;

namespace Homestead.Simulation.Tests;

public class ServerAddressTests
{
    [Theory]
    [InlineData("localhost:8080", "ws://localhost:8080/ws")]
    [InlineData("  192.168.1.10 ", "ws://192.168.1.10:8080/ws")]
    [InlineData("192.168.1.10:9000", "ws://192.168.1.10:9000/ws")]
    [InlineData("ws://host", "ws://host:8080/ws")]
    [InlineData("ws://host:80", "ws://host/ws")] // port 80 as written, not 8080
    [InlineData("ws://localhost:8080/ws", "ws://localhost:8080/ws")] // as older settings have it
    [InlineData("wss://example.org", "wss://example.org/ws")]
    [InlineData("wss://example.org/game/ws", "wss://example.org/game/ws")]
    [InlineData("[::1]:9000", "ws://[::1]:9000/ws")]
    [InlineData("[::1]", "ws://[::1]:8080/ws")]
    public void A_typed_address_becomes_the_servers_websocket(string typed, string address) =>
        Assert.Equal(address, ServerConnection.AddressOf(typed)!.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("http://host:8080")]
    [InlineData("ws://user:secret@host")]
    [InlineData("ws://host/ws?x=1")]
    [InlineData("host:notaport")]
    public void Nonsense_is_no_address(string? typed) => Assert.Null(ServerConnection.AddressOf(typed));

    [Theory]
    [InlineData("ws://localhost:5000/ws", "localhost:5000")]
    [InlineData("wss://example.org/ws", "wss://example.org")]
    [InlineData("localhost:8080", "localhost:8080")]
    public void Saved_addresses_are_shown_short(string saved, string shown) => Assert.Equal(shown, ServerConnection.ShortAddress(saved));
}
