# Homestead

Privater Nachbau von „Die Siedler 3" in MonoGame/C#. Ziele, Roadmap: [docs/PROJEKT.md](docs/PROJEKT.md).
Kommunikation Deutsch; Code und Kommentare Englisch.
Spieltexte nie im Code: `data/lang/de.json`, `en.json` und `pl.json` (verschachtelt, Schlüssel mit Punkten, Platzhalter `{0}`), Zugriff `Lang.T("schlüssel", args)`
im Client; Namen aus Daten nach ID (`buildings.<name>`, `goods.<GOOD>`, `peoples.<People>`, `soldiers.types.*`, `spells.<Kind>`, `events.texts.<id>`,
`ai.characters/traits.*`, `panel.tabs/menus.*`, `buildingStatus.*`). Fehlt Englisch → Deutsch → Schlüssel (Konsole „Missing text“). `LanguageTests`:
gleiche Schlüssel/Platzhalter, jeder wörtliche Schlüssel im Client, jede Daten-ID. Entwickler-Hinweise (`_message`, Fenstertitel, Konsole) englisch, unübersetzt.

## Verbindliche Regeln
- Keine Original-Assets (GFX, SND, Karten, Musik, exportierte PNGs) in Repo oder Build. Prüf-Exporte nur in temporäre Verzeichnisse.
  Einzige Ausnahme: README-Screenshots in `docs/screenshots/` (in `.gitignore` freigegeben).
- Original-EXE/-DLLs (auch `GFX/Siedler3_18…dat`, eine DLL) nie dekompilieren, disassemblieren, analysieren. Nur Datenformate.
- Kein Kopierschutz/DRM umgehen.
- JSettlers-Code (MIT) darf übernommen werden → `THIRD_PARTY_NOTICES.md` pflegen.
- Open Source (MIT, `LICENSE.md`), Clean Room; Regeln für Mitwirkende: `CONTRIBUTING.md`, README auf Englisch.
- Kein „Siedler"/„Settlers" in Typnamen (Repo/README: „monoSettlers").

## Aufbau
- `src/Homestead.Assets` – Parser für Originaldateien (GFX, Karten, `Sound/SoundFile`: `SND/Siedler3_00.dat`), ohne MonoGame. Formatdoku: `docs/formate/`.
- `src/Homestead.AssetViewer` – MonoGame-Viewer (DesktopGL), ohne Content-Pipeline.
- `data/` – Spieldaten (keine Original-Assets), neben jedes Programm kopiert. `production.json`: eigenes Balancing
  (Zeiten, Rezepte, Steine je Felsgröße, Erz je Mengenstufe, Regeneration, `depletion`: Verbrauch von Steinen/Bergen/Fisch in %, 0 = unendlich;
  im Spiel über die Einstellungen überschreibbar).
  Gebäudedaten aus JSettlers generiert (`tools/data-import/`) – nie von Hand ändern, sondern `data/buildings/overrides.json`
  (je Volk/Gebäude, `all` für alle; z. B. römische Burg 6 Infanterie + 4 Bogenschützen wie im Original, Minen nur auf Gebirge).
- `tests/Homestead.Simulation.Tests` – xUnit (`dotnet test`); Tests mit Originalkarten bestehen ohne lokale Installation ungeprüft.

### Details je Bereich
Vor Arbeit an einem System die passende Datei lesen und dort nachführen (wie bisher hier: knapp, Werte mit Schlüssel aus `data/`):
- [docs/architektur/simulation.md](docs/architektur/simulation.md) – `src/Homestead.Simulation`, feste Tick-Rate (`World.TicksPerSecond`):
  `Map/` (Gelände, Fog of War), `Pathfinding/`, `World`/`Settler` (Bewegung, Land), `Trails`, `Buildings/` (Bau, Planieren, Abriss),
  `Economy/` (Logistik, Produktion, Wohnraum, Berufe, Handel, Schiffe), `Military/` (Kampf, Kampfkraft, Kriegsmaschinen), `Magic/`,
  `Persistence/` (Spielstände), `Events/`, `SoundCues`, `GameHistory`, `OriginalScenario`, `Map/Generation` (Zufallskarten).
- [docs/architektur/ki.md](docs/architektur/ki.md) – `Ai/`: `AiPlayer`, Wachstum, Militär, Feldzüge, Taktik, Magie, Charaktere/-züge (`data/ai.json`).
- [docs/architektur/client.md](docs/architektur/client.md) – `src/Homestead.Client`: Titel, Kampagne, Einstellungen, Startscreen, Seitenpanel und
  Menüs, Gebäude-Panel, Statistik, Auswahl/Befehle, Tasten, Optionen, Ton, Darstellung.
- [docs/architektur/capture.md](docs/architektur/capture.md) – `--capture` und alle `HOMESTEAD_CAPTURE_…`-Variablen.
- [docs/architektur/werkzeuge.md](docs/architektur/werkzeuge.md) – Arena (KI gegen KI), Turnier, AssetTool.

### Querschnittsregeln
- Sprites nur über `SpriteLibrary` (siehe Grafik-Konventionen); Texte nur über `Lang.T` (siehe oben).
- Captures und Prüf-Exporte nur in temporäre Verzeichnisse; Speichern in Captures nur mit temporärem `HOMESTEAD_SAVE_DIR`.
- Spielstände: `Snapshot` speichert per Reflection, neue Felder brauchen keinen Speichercode (Details: simulation.md, `Persistence/`).
- Tests ohne Werkzeugthema: `TestConfig.NoTools`.

## Kartenobjekte
`GameMap` speichert je Feld den Original-Objektcode (`MapObjectCodes`); Begehbarkeit und Grafik leiten sich daraus ab.
Laufzeit: Baumstumpf 41 (verschwindet nach `trees.stumpSeconds`), Setzling 84, Felsgrößen 115→127 (Geröll).
Bodenbelag (`GroundCover`, nur auf Gras): Ackerland (als Erde, Original-Übergänge zu Gras), Trampelpfad (als Sand, Sand-Gras-Übergänge).
Eigene Anbau-Codes: Getreide 200–207 (207 reif), Wein 208–210, Reis 211–214, Bienenstöcke 215–218 (Bild nicht gefunden, vorerst die aufstellende Imkerin 41/291, `docs/formate/gfx.md`); Steg 219.

## Grafik-Konventionen
- Raster → Pixel: `x*16 - y*8`, `y*9 - höhe*HeightStep` (Zeilen halb versetzt), 6 Nachbarn. Original HeightStep 2, wir 1 (`ScreenProjection.HeightStep`).
- Gelände-Atlas: Texturkoordinaten einen halben Texel innerhalb der Zelle, sonst Linien aus Nachbarzellen.
- Sprites nur über `SpriteLibrary` (gemeinsame Atlas-Seiten, `SpriteAtlas`); keine eigene `Texture2D` je Bild (Draw-Calls).
- Lauf-Sequenzen: 72 Frames = 6 Richtungen à 12 (SW, W, NW, NE, O, SO); Frame 0 stehen, 1–11 Zyklus je Rasterschritt.

## Installation des Originals
`HOMESTEAD_GAME_DIR` oder `gameDirectory` in `~/.config/homestead/settings.json`; der erste gültige gilt (`GameInstallation.TryLocate`).
Bei jedem Start geprüft (`InstallationCheck`: `GFX` mit `RequiredGfx`, `SND` mit `siedler3_00/01.dat`, `Map` mit Karten); sonst
`InstallationDialog` (eigener Prozess `--setup` – zweites Spielfenster im selben Prozess bricht SDL/GL beim Beenden; ohne Originalgrafik):
Pfadfeld (Tippen, Strg+V über SDL), Ordnerbrowser, Suche an üblichen Orten (`Detect`), Prüfung live, Installation auch über/unter dem
gewählten Ordner (`FindRoot`); „Spiel starten“ → `GameInstallation.Remember`. Capture: `HOMESTEAD_CAPTURE_DIALOG=<png>` (übernimmt die gültige).
Lokal: `~/Games/gog/the-settlers-iii-ultimate-collection/drive_c/GOG Games/Settlers 3 Ultimate`.

## Versionen und Pipeline
- Version aus Git-Tags `vX.Y.Z` (MinVer in `Directory.Build.props`; ohne Tag `0.1.0-alpha.0.<n>`), Titelbildschirm unten rechts.
  Regeln: `docs/VERSIONING.md` (unter 1.0: PATCH Fixes/Balancing/Übersetzungen, MINOR Features oder Spielstand-/Settings-Bruch).
- `.github/workflows/build.yml`: Push auf main/PR → Build + Tests, Pakete linux-x64 (`tar.gz`), win-x64 (`zip`) und osx-arm64/osx-x64
  (auf `macos-latest`: `monoSettlers.app`, Programmordner als `Contents/MacOS`, `packaging/macos/Info.plist`, nur ad-hoc signiert,
  `zip` per `ditto`; ungetestet), self-contained, als Artefakte; Tag `v*` → zusätzlich GitHub-Release (`gh release create`, Suffix → Vorabversion).

## Bauen/Starten
- `dotnet build Homestead.sln`
- Programme mit `dotnet <pfad>.dll` bzw. `dotnet run --project …` – der native Apphost findet .NET in der Flatpak-Umgebung von VS Code nicht.
