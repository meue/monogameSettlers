# Mehrspieler (Lockstep)

Stand 31.10.2026, Branch `feature/lockstep` (abgezweigt von `fix/mac-trackpad-clicks`, beide noch nicht in `main`).

## Entscheidungen

- **Lockstep**: Jeder Rechner simuliert die ganze Welt selbst; übers Netz gehen nur die Befehle der Spieler.
- **Server** als einfacher Relay (sammelt Befehle je Tick, verteilt sie an alle), als Docker-Image zum Selbsthosten,
  vielleicht zusätzlich ein öffentlicher Server. Kosten im Blick behalten (Lockstep braucht kaum Bandbreite).
- **Karte** über den Seed/Hash der Zufallskarte (Name wie `random:4:mirror:11`, Generierung ist deterministisch);
  Originalkarten: Hash der Kartendatei (jeder hat seine eigene Kopie).
- **Einstellungen** (Rohstoffverbrauch `depletion` und übrige Spielwerte aus `GameSettings`) liefert der Server, nicht die
  `settings.json` der einzelnen Spieler.
- **Computergegner bleiben**: Sie laufen deterministisch auf jedem Rechner mit, kein Netzwerkaufwand (auch gemischt, z. B. 2 Menschen gegen 2 KIs).
- **Im Mehrspieler vorerst aus**: eigene KI (Taste I), KI-Chat über Claude (API-Schlüssel, Netzwerkantworten), Vorspulen (F12), Cheats.

## Erledigt

### Determinismus (Commit 21ec216, e10f25d)
- Prüfung der Simulation: Zufall nur über Seeds (`World.Seed`), keine Uhr, keine Threads, kein `GetHashCode` in der Logik,
  keine kulturabhängige Sortierung, kein SIMD/FMA. Grundrechenarten und `Math.Sqrt` sind nach IEEE überall gleich.
- Einziges Risiko waren `Math.Sin/Cos/Atan2` (C-Bibliothek der Plattform) im Zufallskartengenerator und in `PincerTrait`:
  ersetzt durch `DeterministicMath` (nur Grundrechenarten, ~1e-12 genau). Neue Winkelfunktionen in der Simulation nur darüber.
- `WorldChecksum.Of(world)`: FNV-1a über Tick, Siedler, Gebäude, Stapel, Karte, Mana – für die Desync-Erkennung.
- `DeterminismTests`: KI-Spiel auf `random:4:mirror:11`, 25 min, Prüfsumme je Minute. Build-Job `determinism` rechnet es auf
  Linux x64, Windows x64 und macOS ARM, `determinism-compare` vergleicht. **Ergebnis: alle drei bitgenau gleich.**
  Der Build läuft auf Branches nur manuell (Actions → Build → Run workflow).

### Befehlsschicht (Commit 1604922)
- Der Client ändert die Welt nur noch über Befehle: `src/Homestead.Simulation/Commands/` – `GameCommand`-Records (reine Daten:
  Spieler, IDs von Siedlern/Gebäuden/Schiffen, Punkte, Enums, Zahlen; Gebäudetyp als `BuildingDefinition`, eindeutig über Volk + Name;
  Zauber als `Spell`-Record). Jeder Befehl wirkt nur auf Eigenes seines Spielers.
- `World.Commands` (`CommandQueue`): `Issue` vergibt `Sequence`; `World.Step` wendet als Erstes alle wartenden in dieser Reihenfolge
  an, Ergebnisse in `Applied` bis zum nächsten Schritt. `ApplyWhilePaused` nur für das pausierte Einzelspiel. Wartende Befehle stehen im Spielstand.
- Client: `MapGame.Issue(befehl, danach)` (Ergebnis → Meldung, Klang, Auswahl; `TakeCommandResults` nach jedem `Step`), Menüs, Gebäude-Panel
  und KI-Chat über den Delegaten `IssueCommand` (`IssueReported`).
- Ausnahmen (direkt auf die Welt, im Mehrspieler aus oder ohne Einfluss): Spielaufbau, Capture-/Demo-Code, eigene KI (Taste I),
  Cheats-Menü läuft schon über `Cheat`-Befehle, Statistik-Messpunkt beim Öffnen (`GameHistory.Sample`) und Kalender-Ereignisse
  (`Events.Announce`) – beides liest die Spiellogik nicht.
- Tests: `CommandTests` (Reihenfolge und Ergebnisse, Eigentum, Spielstand).

### Lokaler Spieler
- `MapGame._local` statt fest Spieler 0: Befehle, Auswahl (Siedler, Schiffe, Rahmen, Doppelklick), Gebäude-Panel, Menüs, Fog of War (`Vision`),
  Minimap, Steuergruppen (`ControlGroups(player)`), Event-Buttons, Zauber, KI-Chat (`AiChat.Player`), Startscreen, Capture-Szenarien
  (`DemoEconomy … player:`). Alle übrigen Spieler einer freien Partie sind KI; Missionen immer `MissionGoals.Player` (0).
- Sieg/Niederlage je Spieler: `IGameGoals.OutcomeOf(spieler)`, `DecidedAt(spieler)`, `Decide(spieler, …)` (Cheat nur für den eigenen).
  Ältere Spielstände: das Ergebnis wird in der nächsten Sekunde neu bestimmt (Besiegte sind gespeichert).
- Spielstand: Client-Wert `player` (ältere: 0). Zum Ausprobieren: `--player <n>` (1–8) für freie Partien.
- Später im Mehrspieler: `_local` kommt vom Server.

### Gemeinsamer Spielaufbau
- `GameSetup` (Simulation, reine Daten, `ToJson`/`FromJson`, Enums als Text): Karte (`MapChoice`), `Seed`, Spieler (`PlayerSetup`: Volk,
  Farbe als Palettenindex, Team, Hardcore, `Human`, KI-Charakter; `null` = gezogen), Startplätze (je Kartenspieler der Spieler, `null` = gezogen),
  Spielwerte (`GameSettings`; `null` = `data/`). Gleiches Setup → gleiche Welt auf jedem Rechner.
- `MapChoice`: Zufallskarte (Einstellungen + Seed) oder Originalkarte als Pfad relativ zum Kartenordner (`/`) + SHA-256; `Locate` sucht erst
  dort, dann jede `.map` mit dem Hash (jeder hat seine eigene Installation), sonst `FileNotFoundException`.
- `GameSetup.Start(karte)`: zieht Völker, Startplätze und KI-Charaktere aus dem Seed – je Ziehung eigene Zufallszahlen (`RandomFor(Draw)`:
  Völker, Startplätze, Farben, Charaktere), damit eine die andere nicht verschiebt; Weltseed = `Seed`; Teams, Hardcore, `ConquestGoals`,
  eine KI für jeden nicht-menschlichen Spieler. Farben zieht der Client mit `RandomFor(Draw.Colors)` (`PlayerColors.Assign`).
- Client: jede Partie über ein Setup (`MapGame.SoloSetup` → `StartGame`): Startscreen, `--map`, Bild↑/↓, Missionen (danach `MissionGoals`).
  Allein: `Seed` zufällig (Captures 0), Mensch = lokaler Spieler, Spielwerte aus den Einstellungen. Fürs Mehrspieler liefert der Server das Setup.
- Tests: `GameSetupTests` (gleiche Welt auch nach JSON, Aufbau, getrennte Ziehungen, Karte per Hash).

## Offen – Reihenfolge

### 1. Client (vor dem Server)
1. ~~Lokaler Spieler statt fest Spieler 0~~ – erledigt (siehe oben).
2. ~~Gemeinsamer Spielaufbau~~ – erledigt (siehe oben); verteilen tut ihn später der Server.
3. **Befehle für einen Ziel-Tick**: `CommandQueue` nimmt heute nur „nächster Tick“; für Lockstep Ziel-Tick = jetzt + Eingabeverzögerung,
   der Schritt wartet, bis die Befehle aller Spieler für den Tick da sind. Netzwerkformat für die Befehle (polymorph; `BuildingDefinition`
   als Volk + Name, Settler/Gebäude/Schiffe sind schon IDs).
4. **Versions- und Datenprüfung** beim Verbinden: Hash über Programmversion und `data/` (weicht z. B. `production.json` ab, rechnet
   der Rechner sofort ein anderes Spiel).
5. **Desync-Erkennung**: alle paar Sekunden `WorldChecksum` vergleichen; bei Abweichung anhalten und melden (später: Zustand des Hosts
   übernehmen).
6. **Im Mehrspieler sperren**: Taste I, KI-Chat, F12, Cheats; Pause und Speichern gelten für alle (als Befehl/über den Server).

### 2. Server
- Relay in Go (angedacht) oder C#: Lobby, Spielaufbau verteilen, Befehle je Tick sammeln und an alle schicken, Prüfsummen vergleichen.
- Docker-Image zum Selbsthosten.

### 3. Später
- Abbruch/Neuverbindung, Zuschauer, Ranglisten, Accounts (Datenschutz beachten).

## Determinismus-Test erweitern (empfohlen, parallel)
Der Test deckt bisher nur ein KI-Spiel ab: Wirtschaft, Bau, Kampf, KI-Taktik. Nicht abgedeckt: **Spielerbefehle** (die Befehlsschicht
ist neu) und was in 25 min selten vorkommt – **Schiffe, Handel, Kriegsmaschinen, die meisten Zauber**. Vorschlag: ein zweites Szenario,
das eine feste Befehlsfolge abspielt (über `World.Commands`) und diese Systeme auslöst, im selben Build-Job über alle Plattformen.

## Regeln für neuen Code (Simulation)
- Zufall nur `new Random(… + World.Seed)`; keine Uhr, keine Threads, kein `GetHashCode`/`HashCode` in der Logik, Sortierung nur nach
  Zahlen oder ordinal.
- Winkelfunktionen und Co. nur über `DeterministicMath` (bei Bedarf dort ergänzen, nur Grundrechenarten).
- Alles, was ein Spieler auslöst, als `GameCommand` (neuer Record in `Commands/`), nie direkt aus dem Client.
