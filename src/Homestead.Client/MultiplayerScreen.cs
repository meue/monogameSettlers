using System.Text.Json;
using System.Text.Json.Nodes;
using Homestead.Assets.Gfx;
using Homestead.Simulation;
using Homestead.Simulation.Buildings;
using Homestead.Simulation.Lockstep;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Homestead.Client;

/// <summary>What the multiplayer screen asks the game to do.</summary>
public enum MultiplayerAction
{
    None,
    /// <summary>Back to the title screen (the connection is closed).</summary>
    Back,
    /// <summary>Open the start screen to set up a game to open (<see cref="MultiplayerScreen.Create"/> with its setup).</summary>
    Create,
    /// <summary>The host started the game: <see cref="MultiplayerScreen.Started"/> and the connection (<see cref="MultiplayerScreen.TakeConnection"/>).</summary>
    Started,
}

/// <summary>
/// Multiplayer before the game, on the original's list background (<c>missionScreen</c> in <c>data/ui.json</c>): first the
/// name and the server's address (<see cref="MultiplayerSettings"/>, remembered once connected), then the open games of the
/// server (a click joins), then the lobby of the game joined or opened – its seats; the host starts once every seat is taken.
/// Refusals (another program or game data, a full game) are shown with their reasons. Esc goes one step back.
/// </summary>
public sealed class MultiplayerScreen : IDisposable
{
    private const int Width = 800, Height = 600;
    private const int RowHeight = 34;

    private static readonly Color Text = new(240, 225, 190);
    private static readonly Color Gold = new(232, 196, 92);
    private static readonly Color Dim = new(175, 175, 185);
    private static readonly Color Problem = new(240, 140, 110);

    private static readonly Rectangle BackButton = new(680, 555, 97, 25);
    private static readonly Rectangle SecondButton = new(560, 555, 112, 25);
    private static readonly Rectangle ThirdButton = new(440, 555, 112, 25);

    private enum Step { Connect, Browse, Lobby }

    private readonly Texture2D _background, _marble;
    private readonly Rectangle _headingBox, _list;
    private Step _step = Step.Connect;

    // Connecting: the two fields, the one typed into.
    private string _name, _server;
    private int _field;
    private Task<ServerConnection>? _connecting;
    private ServerConnection? _connection;

    private IReadOnlyList<(string Id, string Name, string Host, int Seats, int Taken)> _games = [];
    /// <summary>A seat of the lobby: the player it is, who took it (null: free), the people and color chosen (null: drawn).</summary>
    /// <summary>A player in the lobby: a human seat (<paramref name="Name"/> null while free) or a computer player.</summary>
    private sealed record Seat(int Player, string? Name, Civilization? People, int? Color, bool Computer = false, string? Character = null);

    /// <summary>The lobby as the server tells it: the colors taken include the computer players' fixed ones.</summary>
    private sealed record Lobby(string Name, int Host, int You, IReadOnlyList<Seat> Seats, IReadOnlySet<int> Colors);

    private Lobby? _lobby;

    /// <summary>The order a click on the own people goes through: drawn, then every people.</summary>
    private static readonly Civilization?[] PeopleChoices = [null, .. Enum.GetValues<Civilization>().Cast<Civilization?>()];
    /// <summary>A note under the list: what went wrong, or what is waited for.</summary>
    private string _status = "";
    private bool _statusIsProblem;

    private float _scale = 1;
    private Vector2 _origin;
    private KeyboardState _previousKeys = Keyboard.GetState();
    private MouseState _previousMouse = SdlMouse.GetState();

    public MultiplayerScreen(GraphicsDevice device, GfxDirectory gfx)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuildingCatalog.DefaultDataDirectory, "ui.json")));
        var look = document.RootElement.GetProperty("missionScreen");
        Texture2D Image(string name)
        {
            var bitmap = gfx.Open(look.GetProperty("file").GetInt32()).ReadGui(look.GetProperty(name).GetInt32());
            var texture = new Texture2D(device, bitmap.Width, bitmap.Height);
            texture.SetData(bitmap.Pixels);
            return texture;
        }
        static Rectangle Box(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32());
        _background = Image("background");
        _marble = Image("marble");
        _headingBox = Box(look.GetProperty("heading"));
        _list = Box(look.GetProperty("list"));
        var settings = MultiplayerSettings.Load();
        _name = settings.Name;
        _server = settings.Server;
    }

    /// <summary>A capture run's game: opened once connected, started once every seat is taken.</summary>
    private GameSetup? _automatic;

    /// <summary>
    /// For capture runs: connects to <paramref name="server"/> at once, opens a game of <paramref name="setup"/> and starts it as
    /// soon as every seat is taken (with one human seat: at once).
    /// </summary>
    public void Automate(string server, GameSetup setup)
    {
        _automatic = setup;
        _name = "Capture";
        _server = server;
        Connect();
    }

    /// <summary>For capture runs: a lobby as a joined player sees it, without a server.</summary>
    public void ShowSampleLobby()
    {
        _step = Step.Lobby;
        _lobby = new Lobby("Spiel von Anna", Host: 0, You: 2,
            [new Seat(0, "Anna", Civilization.Roman, 3), new Seat(1, null, Civilization.Egyptian, 5, Computer: true, Character: "general"),
             new Seat(2, "Ben", Civilization.Amazon, 7), new Seat(3, null, null, null)], new HashSet<int> { 3, 5, 7 });
        Status(Lang.T("multiplayer.waiting"));
    }

    /// <summary>The game the host started (after <see cref="MultiplayerAction.Started"/>).</summary>
    public StartedGame? Started { get; private set; }

    /// <summary>The connection for the game started: the screen lets go of it.</summary>
    public ServerConnection TakeConnection()
    {
        var connection = _connection ?? throw new InvalidOperationException("not connected");
        _connection = null;
        return connection;
    }

    /// <summary>Opens a game with the setup chosen on the start screen; the lobby follows.</summary>
    public void Create(GameSetup setup)
    {
        _connection?.Create(Lang.T("multiplayer.gameName", _name.Trim()), setup);
        Status(Lang.T("multiplayer.opening"));
    }

    /// <summary>The game started cannot be played here (its map is missing): the connection ends, the reason shows.</summary>
    public void Fail(string why) => Disconnect(why, problem: true);

    /// <summary>The start screen was left without a game: still in the list.</summary>
    public void Resume()
    {
        _previousKeys = Keyboard.GetState();
        _previousMouse = SdlMouse.GetState();
    }

    /// <summary>A character typed (the window's text input), into the field typed into.</summary>
    public void Type(char c, Keys key)
    {
        if (_step != Step.Connect || _connecting is not null)
            return;
        ref string field = ref _field == 0 ? ref _name : ref _server;
        if (key == Keys.Back)
        {
            if (field.Length > 0)
                field = field[..^1];
        }
        else if (!char.IsControl(c) && field.Length < (_field == 0 ? 24 : 200))
            field += c;
    }

    private void Status(string text, bool problem = false)
    {
        _status = text;
        _statusIsProblem = problem;
    }

    private Rectangle NameField => new(_list.X + 14, _list.Y + 44, _list.Width - 28, 28);
    private Rectangle ServerField => new(_list.X + 14, _list.Y + 112, _list.Width - 28, 28);

    private Rectangle Row(int index) => new(_list.X + 12, _list.Y + 14 + index * RowHeight, _list.Width - 24, RowHeight - 4);

    private int VisibleRows => (_list.Height - 80) / RowHeight;

    public MultiplayerAction Update(Rectangle view)
    {
        Fit(view);
        var keys = Keyboard.GetState();
        var mouse = SdlMouse.GetState();
        bool Pressed(Keys key) => keys.IsKeyDown(key) && !_previousKeys.IsKeyDown(key);
        bool clicked = mouse.LeftButton == ButtonState.Released && _previousMouse.LeftButton == ButtonState.Pressed;
        bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        bool escape = Pressed(Keys.Escape), enter = Pressed(Keys.Enter), tab = Pressed(Keys.Tab), paste = control && Pressed(Keys.V);
        _previousKeys = keys;
        _previousMouse = mouse;
        var at = ToLayout(mouse.Position);
        bool Click(Rectangle button) => clicked && button.Contains(at);

        if (Poll() is { } action)
            return action;
        switch (_step)
        {
            case Step.Connect:
                if (escape || Click(BackButton))
                    return MultiplayerAction.Back;
                if (_connecting is not null)
                    break;
                if (tab)
                    _field = 1 - _field;
                if (clicked && NameField.Contains(at))
                    _field = 0;
                if (clicked && ServerField.Contains(at))
                    _field = 1;
                if (paste && SdlClipboard.Text() is { } pasted)
                    foreach (char c in pasted)
                        Type(c is '\r' or '\n' or '\t' ? ' ' : c, Keys.None);
                if ((enter || Click(SecondButton)) && CanConnect)
                    Connect();
                break;
            case Step.Browse:
                if (escape || Click(BackButton))
                {
                    Disconnect(Lang.T("multiplayer.disconnected"));
                    break;
                }
                if (Click(SecondButton))
                    _connection!.ListGames();
                if (Click(ThirdButton))
                    return MultiplayerAction.Create;
                for (int index = 0; index < Math.Min(_games.Count, VisibleRows); index++)
                    if (Click(Row(index)))
                    {
                        _connection!.Join(_games[index].Id);
                        Status(Lang.T("multiplayer.joining"));
                    }
                break;
            case Step.Lobby:
                if (escape || Click(BackButton))
                {
                    _connection!.Leave();
                    ToBrowse();
                    break;
                }
                if (_lobby is not { } lobby)
                    break;
                if (lobby.Host == lobby.You && Full && Click(SecondButton))
                    _connection!.Start();
                // The own seat: a click on the people or the color takes the next one (free colors only).
                int own = lobby.Seats.ToList().FindIndex(s => s.Player == lobby.You);
                if (own >= 0 && own < VisibleRows && lobby.Seats[own] is var seat)
                {
                    if (Click(PeopleBox(own)))
                        _connection!.Choose(PeopleChoices[(Array.IndexOf(PeopleChoices, seat.People) + 1) % PeopleChoices.Length], seat.Color);
                    if (Click(ColorBox(own)))
                        _connection!.Choose(seat.People, NextColor(seat.Color, lobby.Colors));
                }
                break;
        }
        return MultiplayerAction.None;
    }

    private bool CanConnect => _name.Trim().Length > 0 && Uri.TryCreate(_server.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "ws" or "wss";

    private bool Full => _lobby is { } lobby && lobby.Seats.All(s => s.Computer || s.Name is not null);

    private Rectangle PeopleBox(int index) => new(Row(index).X + 232, Row(index).Y + 2, 112, RowHeight - 8);

    private Rectangle ColorBox(int index) => new(Row(index).Right - 28, Row(index).Y + 4, 22, 22);

    /// <summary>The color after <paramref name="color"/> among those nobody else has (null: drawn, then the palette in order).</summary>
    private static Civilization? PeopleOf(JsonObject seat) =>
        Enum.TryParse<Civilization>(seat["people"]?.GetValue<string>(), out var people) && Enum.IsDefined(people) ? people : null;

    /// <summary>A seat's color, if it is one of the palette: the list draws by it.</summary>
    private static int? ColorOf(JsonObject seat) =>
        seat["color"]?.GetValue<int>() is { } color && color >= 0 && color < PlayerColors.Palette.Length ? color : null;

    private static int? NextColor(int? color, IReadOnlySet<int> taken)
    {
        var choices = new int?[] { null }.Concat(Enumerable.Range(0, PlayerColors.Palette.Length).Where(c => c == color || !taken.Contains(c)).Cast<int?>())
            .ToArray();
        return choices[(Array.IndexOf(choices, color) + 1) % choices.Length];
    }

    private void Connect()
    {
        Status(Lang.T("multiplayer.connecting"));
        _connecting = ServerConnection.ConnectAsync(new Uri(_server.Trim()), _name.Trim(), GameFingerprint.Of());
    }

    private void Disconnect(string why, bool problem = false)
    {
        if (_connection is { } connection)
            _ = connection.DisposeAsync().AsTask();
        _connection = null;
        _step = Step.Connect;
        _lobby = null;
        Status(why, problem);
    }

    private void ToBrowse()
    {
        _step = Step.Browse;
        _lobby = null;
        Status("");
        _connection?.ListGames();
    }

    /// <summary>The connection's news: connected or not, games, lobby, refusals, the start.</summary>
    private MultiplayerAction? Poll()
    {
        if (_connecting is { IsCompleted: true } connecting)
        {
            _connecting = null;
            if (connecting.IsCompletedSuccessfully)
            {
                _connection = connecting.Result;
                if (_automatic is null)
                    new MultiplayerSettings { Name = _name.Trim(), Server = _server.Trim() }.Save();
                ToBrowse();
                if (_automatic is { } setup)
                    Create(setup);
            }
            else
                Status(connecting.Exception?.InnerException switch
                {
                    ServerRefusedException refused => Lang.T("multiplayer.refused", string.Join("; ", refused.Reasons)),
                    { } e => Lang.T("multiplayer.unreachable", e.Message),
                    null => Lang.T("multiplayer.unreachable", "?"),
                }, problem: true);
        }
        if (_connection is not { } connection)
            return null;
        if (!connection.IsOpen)
        {
            Disconnect(Lang.T("multiplayer.lost", connection.ClosedBecause ?? ""), problem: true);
            return null;
        }
        try
        {
            foreach (var message in connection.Receive())
                if (Take(message, connection) is { } action)
                    return action;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or NullReferenceException
            or InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
        {
            // A server is anybody's: what it says may be nonsense, and that must not end the program.
            Console.WriteLine($"bad message from the server: {e.Message}");
            Disconnect(Lang.T("multiplayer.badServer"), problem: true);
        }
        return null;
    }

    /// <summary>One message of the server, read with its fields as they must be (an exception otherwise, see <see cref="Poll"/>).</summary>
    private MultiplayerAction? Take(JsonObject message, ServerConnection connection)
    {
        static string Text(JsonNode? node, int length) => PlayerText.Clean(node?.GetValue<string>(), length);
        switch (ServerMessages.TypeOf(message))
        {
            case ServerMessages.Games:
                _games = (message["games"] as JsonArray ?? []).OfType<JsonObject>().Select(g => (
                    g["id"]!.GetValue<string>(), Text(g["name"], ServerMessages.MaxGameNameLength), Text(g["host"], ServerMessages.MaxNameLength),
                    g["seats"]!.GetValue<int>(), g["taken"]!.GetValue<int>())).ToArray();
                break;
            case ServerMessages.Lobby:
                _step = Step.Lobby;
                _lobby = new Lobby(Text(message["name"], ServerMessages.MaxGameNameLength), message["host"]!.GetValue<int>(), message["you"]!.GetValue<int>(),
                    (message["seats"] as JsonArray ?? []).OfType<JsonObject>().Select(s => new Seat(s["player"]!.GetValue<int>(),
                            s["name"] is { } name ? Text(name, ServerMessages.MaxNameLength) : null, PeopleOf(s), ColorOf(s)))
                        .Concat((message["computers"] as JsonArray ?? []).OfType<JsonObject>().Select(c => new Seat(c["player"]!.GetValue<int>(),
                            null, PeopleOf(c), ColorOf(c), Computer: true, c["character"] is { } character ? Text(character, ServerMessages.MaxNameLength) : null)))
                        .OrderBy(s => s.Player).ToArray(),
                    (message["colors"] as JsonArray ?? []).Select(c => c!.GetValue<int>()).ToHashSet());
                Status(Full ? Lang.T(_lobby.Host == _lobby.You ? "multiplayer.allThere" : "multiplayer.hostStarts")
                    : Lang.T("multiplayer.waiting"));
                if (_automatic is not null && Full && _lobby.Host == _lobby.You)
                    connection.Start();
                break;
            case ServerMessages.Closed:
                ToBrowse();
                Status(Lang.T("multiplayer.closed"), problem: true);
                break;
            case ServerMessages.Refused:
                Status(Lang.T("multiplayer.refused", string.Join("; ", ServerConnection.Reasons(message))), problem: true);
                break;
            case ServerMessages.Error:
                Status(message["message"] is { } text ? Text(text, 200) : "?", problem: true);
                break;
            case ServerMessages.Started:
                Started = ServerMessages.ParseStarted(message);
                return MultiplayerAction.Started;
        }
        return null;
    }

    public void Draw(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle view)
    {
        Fit(view);
        var page = ToScreen(new Rectangle(0, 0, Width, Height));
        if (page.X > view.X || page.Y > view.Y)
            batch.Draw(_marble, view, new Rectangle(14, 0, _marble.Width - 28, _marble.Height), Color.White);
        batch.Draw(_background, page, Color.White);
        var cursor = SdlMouse.GetState().Position.ToVector2();
        var hover = ToLayout(SdlMouse.GetState().Position);
        string heading = _step == Step.Lobby && _lobby is { } named ? named.Name : Lang.T("multiplayer.title");
        WriteCentered(batch, font, Shorten(font, heading, _headingBox.Width - 8, 13), _headingBox, Gold, 13);

        switch (_step)
        {
            case Step.Connect:
                Write(batch, font, Lang.T("multiplayer.name"), NameField.X, NameField.Y - 20, Gold, 13);
                Field(batch, pixel, font, NameField, _name, _field == 0 && _connecting is null);
                Write(batch, font, Lang.T("multiplayer.server"), ServerField.X, ServerField.Y - 20, Gold, 13);
                Field(batch, pixel, font, ServerField, _server, _field == 1 && _connecting is null);
                Paragraph(batch, font, Lang.T("multiplayer.serverHint"), ServerField.Bottom + 8, Dim, 11);
                TextButton.Draw(batch, pixel, font, ToScreen(SecondButton), Lang.T("multiplayer.connect"), cursor,
                    enabled: CanConnect && _connecting is null, size: Px(13));
                break;
            case Step.Browse:
                if (_games.Count == 0)
                    Write(batch, font, Lang.T("multiplayer.noGames"), _list.X + 20, _list.Y + 20, Dim, 13);
                for (int index = 0; index < Math.Min(_games.Count, VisibleRows); index++)
                {
                    var (_, name, host, seats, taken) = _games[index];
                    var row = Row(index);
                    bool lit = row.Contains(hover);
                    if (lit)
                        batch.Draw(pixel, ToScreen(row), Gold * 0.18f);
                    string note = Lang.T("multiplayer.gameNote", host, taken, seats);
                    float noteWidth = font.Measure(note, Px(12)).X / _scale;
                    Write(batch, font, Shorten(font, name, row.Width - 24 - noteWidth, 14), row.X + 8, row.Y + 7, lit ? Gold : Text, 14);
                    Write(batch, font, note, row.Right - 8 - noteWidth, row.Y + 9, Dim, 12);
                }
                TextButton.Draw(batch, pixel, font, ToScreen(SecondButton), Lang.T("multiplayer.refresh"), cursor, size: Px(13));
                TextButton.Draw(batch, pixel, font, ToScreen(ThirdButton), Lang.T("multiplayer.create"), cursor, size: Px(13));
                break;
            case Step.Lobby when _lobby is { } lobby:
                for (int index = 0; index < Math.Min(lobby.Seats.Count, VisibleRows); index++)
                {
                    var (player, name, people, color, computer, character) = lobby.Seats[index];
                    var row = Row(index);
                    bool own = player == lobby.You;
                    string who = computer
                        ? character is null ? Lang.T("multiplayer.computer") : Lang.T("multiplayer.computerCharacter", Lang.T("ai.characters." + character))
                        : name is null ? Lang.T("multiplayer.free")
                        : name + (player == lobby.Host ? Lang.T("multiplayer.host") : "") + (own ? Lang.T("start.you") : "");
                    Write(batch, font, Lang.T("start.player", player + 1), row.X + 8, row.Y + 7, Gold, 14);
                    Write(batch, font, Shorten(font, who, 140, 14), row.X + 86, row.Y + 7, name is null && !computer ? Dim : own ? Gold : Text, 14);
                    // People and color: the own ones to click through, the others' as chosen.
                    var peopleBox = PeopleBox(index);
                    bool lit = own && peopleBox.Contains(hover);
                    if (own)
                        batch.Draw(pixel, ToScreen(peopleBox), (lit ? Gold * 0.25f : Color.Black * 0.35f));
                    string peopleName = people is { } chosen ? Lang.T("peoples." + chosen) : Lang.T("start.randomPeople");
                    Write(batch, font, Shorten(font, peopleName, peopleBox.Width - 8, 12), peopleBox.X + 4, peopleBox.Y + 6,
                        lit ? Gold : people is null ? Dim : Text, 12);
                    var swatch = ColorBox(index);
                    batch.Draw(pixel, ToScreen(swatch), own && swatch.Contains(hover) ? Gold : Color.Black);
                    var inner = new Rectangle(swatch.X + 2, swatch.Y + 2, swatch.Width - 4, swatch.Height - 4);
                    batch.Draw(pixel, ToScreen(inner), color is { } c ? PlayerColors.Palette[c] : new Color(60, 55, 50));
                    if (color is null)
                        WriteCentered(batch, font, "?", inner, Text, 12);
                }
                if (lobby.Seats.Any(s => s.Player == lobby.You))
                    Paragraph(batch, font, Lang.T("multiplayer.chooseHint"), Row(Math.Min(lobby.Seats.Count, VisibleRows)).Y + 8, Dim, 11);
                if (lobby.Host == lobby.You)
                    TextButton.Draw(batch, pixel, font, ToScreen(SecondButton), Lang.T("multiplayer.start"), cursor, enabled: Full, size: Px(13));
                break;
        }
        if (_status.Length > 0)
            Paragraph(batch, font, _status, _list.Bottom - 70, _statusIsProblem ? Problem : Dim, 12);
        TextButton.Draw(batch, pixel, font, ToScreen(BackButton), Lang.T(_step == Step.Lobby ? "multiplayer.leave" : "campaign.back"), cursor,
            size: Px(13));
    }

    /// <summary>A text field: dark, a gold frame while typed into (with a caret).</summary>
    private void Field(SpriteBatch batch, Texture2D pixel, UiFont font, Rectangle box, string text, bool active)
    {
        batch.Draw(pixel, ToScreen(box), Color.Black * 0.45f);
        if (active)
            foreach (var edge in new[] { new Rectangle(box.X, box.Y, box.Width, 1), new Rectangle(box.X, box.Bottom - 1, box.Width, 1),
                         new Rectangle(box.X, box.Y, 1, box.Height), new Rectangle(box.Right - 1, box.Y, 1, box.Height) })
                batch.Draw(pixel, ToScreen(edge), Gold);
        string shown = text;
        while (shown.Length > 0 && font.Measure(shown + "_", Px(13)).X > (box.Width - 12) * _scale)
            shown = shown[1..];
        bool caret = active && DateTime.Now.Millisecond < 500;
        Write(batch, font, shown + (caret ? "_" : ""), box.X + 6, box.Y + 6, Text, 13);
    }

    /// <summary>A text wrapped to the list's width from <paramref name="y"/> down.</summary>
    private void Paragraph(SpriteBatch batch, UiFont font, string text, float y, Color color, int size)
    {
        foreach (string line in font.Wrap(text, Px(_list.Width - 28), Px(size)))
        {
            Write(batch, font, line, _list.X + 14, y, color, size);
            y += size + 4;
        }
    }

    private void Fit(Rectangle view)
    {
        _scale = Math.Min(view.Width / (float)Width, view.Height / (float)Height);
        _origin = new Vector2(view.X + (view.Width - Width * _scale) / 2, view.Y + (view.Height - Height * _scale) / 2);
    }

    private Rectangle ToScreen(Rectangle r)
    {
        var a = _origin + new Vector2(r.X, r.Y) * _scale;
        var b = _origin + new Vector2(r.Right, r.Bottom) * _scale;
        return new Rectangle((int)MathF.Round(a.X), (int)MathF.Round(a.Y), (int)MathF.Round(b.X) - (int)MathF.Round(a.X),
            (int)MathF.Round(b.Y) - (int)MathF.Round(a.Y));
    }

    private Point ToLayout(Point screen) => ((screen.ToVector2() - _origin) / _scale).ToPoint();

    private int Px(float size) => Math.Max(8, (int)MathF.Round(size * _scale));

    private string Shorten(UiFont font, string text, float width, int size)
    {
        if (font.Measure(text, Px(size)).X <= width * _scale)
            return text;
        while (text.Length > 1 && font.Measure(text + "…", Px(size)).X > width * _scale)
            text = text[..^1];
        return text + "…";
    }

    private void Write(SpriteBatch batch, UiFont font, string text, float x, float y, Color color, int size)
    {
        var at = _origin + new Vector2(x, y) * _scale;
        at = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
        font.Draw(batch, text, at + new Vector2(1, 1), Color.Black * 0.8f, Px(size));
        font.Draw(batch, text, at, color, Px(size));
    }

    private void WriteCentered(SpriteBatch batch, UiFont font, string text, Rectangle box, Color color, int size)
    {
        var measured = font.Measure(text, Px(size)) / _scale;
        Write(batch, font, text, box.Center.X - measured.X / 2, box.Center.Y - measured.Y / 2, color, size);
    }

    /// <summary>Also closes a connection not handed on (back to the title).</summary>
    public void Dispose()
    {
        Disconnect("");
        // Still connecting: closed once through.
        _connecting?.ContinueWith(t => t.Result.DisposeAsync().AsTask(), TaskContinuationOptions.OnlyOnRanToCompletion);
        _connecting = null;
        _background.Dispose();
        _marble.Dispose();
    }
}
