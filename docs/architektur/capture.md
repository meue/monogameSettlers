# Capture (`--capture <dir>`)

Offscreen-PNGs zur Prüfung, nur in temporäre Verzeichnisse (keine Original-Assets im Repo).

- **Capture** (`--capture <dir>`, offscreen PNGs, nur temporäre Verzeichnisse): ignoriert Eingaben, ohne VSync, 4× Tempo; gibt am Ende Zähler und
  Gebäudezustände aus. Umgebungsvariablen `HOMESTEAD_CAPTURE_…`:
  `ZOOM=4`; `EVERY=<n>` (PNG alle n Frames, sonst 120); `SIZE=1920x1080` (sonst 960×600); `SCALING=<Sharp|Linear|CatmullRom|Lanczos2>`;
  `FOCUS=<gebäude>` (`site`: frische Baustelle; `temple`: kleiner Tempel mit Mana; Militärgebäude wie `castle`: voll besetzt); `FOG=1`;
  `EVENT=1` (springt zu Events); `BATTLE=1` (Stufe-3-Feinde greifen das fokussierte Militärgebäude an, sonst den Startturm; `=big`: zwei Armeen à 30 treffen sich südlich des Starts; `=open`: 40 gegen 40 auf freiem Feld; `=town[:n]`: n (90) gegen n mitten im Dorf; mit `ZOOM` Kamera darauf); `COLORS=2,4` (Palettenfarben je Spieler); `STRIKE=1` (Abriss des fokussierten Gebäudes + 10 Träger ohne Wohnraum);
  `GEOLOGISTS=1`; `PIONEERS=1`; `DOCK=1` (Werft am nächsten offenen Meer, Land dort eigen, mit Steg; `=none` ohne; `=build`: Fähre im Bau, Material daneben; `=ships`: Fähre und
  Handelsschiff fertig, gewählt, als Gruppe 4, losgeschickt (mit `FOCUS=none`); `=ferry`: Fähre am Steg, 8 Soldaten, 7 steigen ein, Fähre gewählt; `=harbor` (mit `FOCUS=harbor`): Hafen mit Steg, Handelsschiff, Waren, Ziel; sonst mit `FOCUS=dockyard`); `MACHINES=1` (eigene Werkstatt mit 2 Ladungen und Auftrag, Material daneben; feindlicher Turm, je Volk eine Maschine beschießt ihn, gewählt, als Gruppe 3;
  mit `FOCUS=<werkstatt>` deren Panel, sonst `FOCUS=none`); `HIVES=1` (vier Bienenstöcke, je Stufe einer, neben dem Start, Kamera darauf); `TRADE=1` (Markt mit Lagertür als Ziel, Stopp, Planken ∞, Steine 12, drei Esel, Eselzucht; mit `FOCUS=market_place`); `SELECT=1` (Soldaten aller Typen/Stufen gewählt, als Gruppe 1, Bogenschützen daraus als Gruppe 2; `=mixed`: auch die von Spieler 2 in der Auswahl); `SAVE=<slot>` (nach 300 Frames; mit `HOMESTEAD_SAVE_DIR` temporär!);
  `TAB=system[:save|quit]` bzw. `goods|settlers[:<untermenü>[:manual|<ware>]]` (z. B. `settlers:soldiers:manual`, `goods:distribution:bread`);
  `STORE=<ware>` (mit `FOCUS=stock`); `PLACE=<gebäude>`; `START=<tab>[:color]` (nur `start.png` des Startscreens, 3: Zufallsgenerator; `:color` mit offener Farbauswahl von Spieler 1, `:team` mit offener Teamauswahl);
  `MAGIC=1` (großer Tempel, Mana-Kette, Priester zaubert in einen Kampf; `=summon`: Himmlische Geschenke; `=<SpellKind>` dieser Zauber des eigenen Volks,
  Land-Zauber auf die freieste Grasfläche, Konsole zeigt den Boden dort);
  `MINUTES=<n>` (Benchmark: KI auch für Spieler 1, n min ohne Zeichnen vorgespult, dann Kameraschwenk über die Karte, PNG alle 1200 Frames);
  `STATS=<frame>` (Statistik öffnet in diesem Frame, alle Seiten als `stats_N.png`, dann Ende);
  `CHAT=<text>` (Spieler 1–3 sagen etwas, Spieler 2 lang genug zum Umbrechen, Chatzeile offen mit dem Text);
  `VIEW=economy|battle` (statt Schwenk: Kamera auf Spieler 1 bzw. folgt dem größten Kampf; `battle` spult mit `MINUTES` bis zu 20 min weiter, bis ≥ 20 Soldaten an einem Kampf sind).


## Bildschirme vor dem Spiel
- **Intro-Video**: `HOMESTEAD_CAPTURE_START=intro` → `video.png` (100. Bild).
- **Titelbildschirm**: `HOMESTEAD_CAPTURE_START=title` → `title.png` (`title:readme`/`title:contribution`/`title:licenses` mit Popup, `title:load` → `load.png` (Liste der Spielstände), `title:loadError` mit Ladefehler).
- **Ladebildschirm**: `HOMESTEAD_CAPTURE_START=loading[:settlers|missionCd|amazons]` → `loading.png`.
- **Kampagne**: `HOMESTEAD_CAPTURE_START=campaign` (Missions-CD: `missionCd`; Amazonen: `amazons`, Seiten `amazons`/`peoples`; `HOMESTEAD_CAPTURE_HOVER=<seite>` wie unter der Maus) → `campaign.png`, `campaign:<volk>` → `video.png` (100. Bild), `campaign:<volk>:briefing` → `briefing.png`, `campaign:<volk>:choose` → Volk gewählt (mit Fortschritt `missions.png`), `campaign:<volk>:won|lost` → Mission 1 gestartet, sofort entschieden, Spiel läuft (Banner); `…:won:next`/`…:lost:next` → Statistik verlassen, `briefing.png` der zweiten bzw. wieder der ersten; Nummer am Ende nimmt diese Mission (`won:next:8` → Sieg-Video, `video.png`) (mit `HOMESTEAD_SAVE_DIR` temporär!).
- **Mehrspieler**: `HOMESTEAD_CAPTURE_START=multiplayer` → `multiplayer.png` (`multiplayer:create`: Startscreen zum Eröffnen; `multiplayer:lobby`: Beispiel-Lobby ohne Server); `HOMESTEAD_CAPTURE_SERVER=ws://…/ws`: verbindet, eröffnet ein Spiel (Spieler 1 Mensch, Spieler 2 KI, Zufallskarte), startet und spielt es über den Server (ohne Demo-Wirtschaft).
- **Einstellungen**: `HOMESTEAD_CAPTURE_START=settings[:resources|economy|military|display]` → `settings.png`.
- **Installationsdialog**: `HOMESTEAD_CAPTURE_DIALOG=<png>` (siehe `CLAUDE.md`, Installation).
