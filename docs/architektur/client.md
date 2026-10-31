# Client (`src/Homestead.Client`)

MonoGame-Spiel. Capture-Optionen: [capture.md](capture.md).

- **Titelbildschirm** (`TitleScreen`, zuerst ohne `--map`/`--capture`): Hauptmenü-Bild der Gold Edition (`titleScreen` in `ui.json`, Datei 61 GUI 1,
  daneben glattes Gold), links eingravierte Knöpfe wie im Original (Maße/Farben vom Original-Menü): ganz oben Weiterspielen (lädt `SaveSlots.Latest`, ohne Spielstand blass), abgesetzt Kampagne, Missions-CD, Amazonen-Kampagne (je → Kampagne),
  abgesetzt Einzelspieler (→ Startscreen), Einstellungen (→ Einstellungen), Spiel laden
  (Liste der 8 Slots als `ListScreen`: Name, Datum, Volk; leere blass; Klick lädt, Ladefehler über dem Titel), abgesetzt Readme/Contribution/Lizenzen (Popup auf der
  Original-Meldungsbox Datei 02 GUI 16, `popup` in `titleScreen`: „Im Browser öffnen“ → `README.md`/`CONTRIBUTING.md`/`THIRD_PARTY_NOTICES.md` auf GitHub, Enter/Esc),
  unten abgesetzt Beenden; Esc/„Zum Hauptmenü“ der Statistik/Esc im Startscreen führen hierher.
- **Ladebildschirm** (`LoadingScreen`, `MapGame.ShowLoading`): vor jedem Laden einer Partie (Startscreen, Mission, Spielstand) die drei Helme,
  mindestens 1 s (Echtzeit, ab dem ersten Zeichnen), geladen wird das Spiel schon verdeckt darunter gezeichnet (`WarmFrames`, 3 – erste Frames füllen Atlanten, sonst ruckt die Blende), dann 0,3 s Ausblenden über dem laufenden Spiel (ab dem ersten Blend-Frame) (`loadingScreen` in `ui.json`, 1024 × 768, Randspalten zur Seite gestreckt): Kampagne orange (Datei 02 GUI 28), Missions-CD grün
  (60/0), Amazonen lila mit Torso (48/0), sonst gold (61/0); Thema außer Normal: immer dessen Bild; Captures laden sofort.
- **Kampagne** (`CampaignScreen`, über dem Titelbildschirm): Wahl der Seite je Kampagne (`campaignScreens` in `ui.json`: Bild und Namensfeld
  je Seite, Überschrift; Klick wählt, „Zurück“/Esc → Titel – im Original ohne). Siedler (`settlers`): je Volk, Datei 02 GUI 7, Goldrahmen unter der
  Maus. Missions-CD (`missionCd`): Datei 60 GUI 4 (Schiffbrüchige im Sturm), Namensfelder mit Schwierigkeit (`label`, umbrochen),
  hellere Figuren Datei 60 Sequenz 0. Amazonen (`amazons`): Datei 48 GUI 1, links die Amazonen (`amazons`), rechts das Heer der drei Völker (`peoples`); unter der Maus hellere
  Figuren (Datei 48, Sequenz 2, per Korrelation platziert, `lit`). → Intro-Video der Seite → (Ende oder Klick) Beschreibung der ersten Mission
  (`BriefingScreen` auf dem Startscreen-Hintergrund GUI 8: Missionstext oben mit Bildlauf, Karte im schrägen Fenster mit Kürzeln
  der Spieler an ihren Startpunkten – eigener grün, sonst rot –, Hinweise rechts, Sprachansage dazu; „Zurück“/Esc → Volkswahl,
  „Weiter“/Enter → Mission mit Völkern/Gebäuden der Karte). `data/campaign.json`: je Kampagne `name` (Textschlüssel, im Spielstandnamen) und `sides` mit `name` (Textschlüssel), `intro`,
  `outro` (Sieg-Video) und `missions` (`map`, `text`/`hint` = Einträge der Texttabelle in GFX-Datei 07, Deutsch/Englisch/Polnisch nach Sprache (`Lang.TextIndex`: Spalte 0/1/4 der Texttabelle, Polnisch in Codepage 1250), `voice`,
  `labels` je Kartenspieler – bisher nur Mission 1, aus dem Missionstext – oder `preview` [Datei, Sequenz, Bild] eines fertigen Original-Vorschaubilds
  statt der gerenderten Karte: Amazonen Datei 48 Sequenz 3, Z01–Z12 = 0–11, G01–G12 = 12–23, per Wassermaske zugeordnet); Pfade in der
  Installation, `GameInstallation.FindPath` ohne Groß-/Kleinschreibung. Missions-CD: `S3MCD/MAP/MIS_M/R|E|A` (Texte 24–31 Römer, 32–39 Ägypter, 40–47 Asiaten, Vorschaubilder Datei 60 Sequenz 1 = 0–23), ohne Videos und Sprachansagen
  (`intro`/`outro`/`voice` weggelassen: übersprungen, nicht in der Missionsauswahl). Amazonen-Kampagne: `S3QOTA/MIS_M/Z` (Amazonen, Texte 48–59) und `G`
  (die drei Völker, Spieler 1 wechselt, Texte 60–71), Hinweise +72, Videos `S3QOTA/ADD/1/DATAP0_N` (Intro Amazonen 1, drei Völker 2; Sieg 3 bzw. 5).
  Fortschritt (globaler Spielstand): `CampaignProgress` (`campaign.json` bei den Spielständen, je Kampagne/Seite Zahl geschaffter Missionen);
  Volkswahl mit Fortschritt → Missionsauswahl (`MissionSelectScreen` über `ListScreen` – gemeinsame Liste mit „Spiel laden“ –, Datei 02 GUI 18, `missionScreen` in `ui.json`: zuerst das Intro,
  alle geschafften und die nächste, Namen aus den Missionstexten, nach allen der Abschluss; längere Liste scrollt (Rad, Pfeile, Bildlaufleiste), öffnet bei der neuesten; Klick → Beschreibung, deren „Zurück“ wieder
  hierher, bzw. Video und danach zurück; Esc/„Zurück“ → Volkswahl). Videos über `MapGame.PlayVideo(pfad, danach)`. Laufende Mission `MissionRef` (`settlers/Roman/0`, `amazons/peoples/3`) steht im
  Spielstand (Client-Wert `mission`, Kopf `SaveInfo.Mission` – Listen zeigen „Kampagne Römer, Mission 1“).
  Sieg/Niederlage: `World.Goals` (`MissionGoals`, nur in Missionen, mitgespeichert) prüft sekündlich für Spieler 1: kein eigenes Gebäude
  (auch Baustellen) mehr, nachdem es eins gab, oder keins und keine Siedler (G06 beginnt ohne Gebäude) → verloren, kein fremdes mehr (zerstört/erobert) → gewonnen; einmal entschieden, bleibt es. Missionsspezifische Bedingungen
  aus den Texten (Vorposten halten, Goldmengen …) noch nicht.
  Freie Partien (Startscreen, auch alte Spielstände): `ConquestGoals` (`World.Goals` ist `IGameGoals`) – wer kein fertiges Militärgebäude
  (Turm, großer Turm, Burg) mehr hat, nachdem er eins hatte, ist endgültig besiegt (`IsDefeated`); seine KI denkt nicht mehr (`World` überspringt sie),
  Siedler/Soldaten handeln nur noch von selbst. Spieler 1 besiegt → verloren, alle anderen besiegt → gewonnen (Banner mit `system.wonHint`/`lostHint`, Statistik ohne „Weiter“). Gewonnen: weiß oben „Spiel gewonnen“, Fortschritt sofort gespeichert, weiterspielen
  möglich; Spiel beenden → Statistik („Weiter“) → nächste Missionsbeschreibung über Missionsauswahl und Volkswahl (nach der letzten das Sieg-Video, dann die Auswahl).
  Gewonnen hebt den Fog of War auf (`FogOn`; Cheat-Wahl `_fogChosen` bleibt für die nächste Partie).
  Verloren: „Spiel verloren“, keine Befehle mehr (Auswahl, Bauen, Menüs, Tasten außer Esc/F11/F3/T/Leertaste); Kamera, Minimap, Events und Computer-Menü
  gehen weiter; Esc → Statistik ohne Weiterspielen → dieselbe Missionsbeschreibung.
  Cheat-Menü: „Mission geschafft“/„Mission verloren“.
  `VideoPlayer`: `AviFile` (Homestead.Assets, `docs/formate/avi.md`) liefert Kopf und PCM-Ton, das Bild (Indeo 5) dekodiert ein
  installiertes ffmpeg im eigenen Prozess (`HOMESTEAD_FFMPEG`, neben dem Programm, PATH; sonst übersprungen); Bilder folgen der Uhr ab dem
  ersten Bild/Tonstart, Lautstärke = Effekte; Esc/Leertaste/Enter/Klick bricht ab.
- **Einstellungen** (`SettingsScreen`): Hintergrund Datei 02 GUI 14 (`settingsScreen` in `ui.json`, Marmor daneben); oben in den vier
  Kästchen Reiter mit Icons der Original-Statistikmenüs (Datei 03, dunkel = eingedrückt gewählt, sonst hell; Tab/Shift+Tab): Rohstoffe,
  Wirtschaft (noch leer), Militär, Darstellung; je Einstellung eine Reihe Werte (Rohstoffe: `depletion` Steine/Berge/Fisch
  100/75/50/25/10 %/∞; Militär: Zauberkosten-Anstieg konstant/+10/+25/+50 % → `MagicConfig.CostIncrease`; Darstellung: Hardware-Cursor, Vollbild, Sprache Deutsch/English/Polski (Vorgabe English, unabhängig von der Systemsprache; gilt auch für den Installationsdialog),
  Thema Normal/Gold/Classic/Amazonen (Titelbild und Ladebildschirm; `titleScreen.themes` in `ui.json` mit Gravurfarben, Normal = Gold; `HOMESTEAD_THEME` überstimmt), Musik/Effekte 100/75/50/25 %/aus, Musik je Volk, Bevorzugte Farbe (`DisplaySettings.PreferredColor`, Farbfelder, als Kennung gespeichert), Automatisch speichern
(Ja/Nein, Vorgabe Nein: alle 15 min Echtzeit im Spiel und bei „Spiel beenden“ in Slot 8, nicht nach Verlust, nicht in Captures), Intro-Video (Ja/Nein, Vorgabe Ja: `MapGame.IntroVideo` = `S3/ADD/001/01_00_00.DAT` bei jedem Start vor dem Titelbildschirm, Ende/Klick → Titel) – Musik ohne Wahl nach Systemsprache; `Lang.Use` setzt auch die Kultur;
Titelbild der Gold Edition je Sprache, `background` `{de, en}`; Polnisch ohne eigenes Titelbild (das englische), `HOMESTEAD_LANGUAGE=de|en|pl` überstimmt, z. B. für Captures).
  Ok/Enter speichert `GameSettings` (Abschnitt `game`) und `DisplaySettings` (Client, Abschnitt `display`, sofort angewandt) in
  `~/.config/homestead/settings.json` (`SettingsFile`: je Abschnitt, Rest der Datei bleibt); Abbrechen/Esc verwirft, Standard setzt den offenen
  Reiter zurück (Spielwerte: `production.json`). Hardware-Cursor aus: eigener goldener Pfeil (`SoftwareCursor`, in `EndDraw`, nicht in Captures).
  Neue Partien: `GameSettings.Load(…).Apply(…)` → `OriginalScenario.Load(production:, magic:)`; Spielstände behalten ihre Werte (Arena/Tests: nur `production.json`).
- **Startscreen** (`StartScreen`): auf dem Kartenwahl-Hintergrund des Originals (`startScreen` in `ui.json`, Datei 02 GUI 8,
  im 800×600-Raster auf Fenstergröße skaliert, daneben Marmor; ovale Steinknöpfe, Steinleiste unter der Auswahl, Vorschau im schrägen Fenster): Kartenliste mit Vorschau, Tabs Einzelspieler/Mehrspieler/Eigene (Tab-Taste),
  sortiert nach Spielerzahl (`OriginalMap.ReadPlayerCount` liest nur MapInfo). Tab „Zufallsgenerator“: Einstellungen (Spieler, Symmetrie, Flüsse, Rohstoffe, Steine, Wälder,
  Schätze, King of the Hill; Dekoration nicht wählbar, immer mittel) – Änderung erzeugt mit gleichem Seed neu, „Neu würfeln“/R neuer Seed. Je Spieler (Tabelle feste Zeilenhöhe, mehr Spieler scrollen): Volk (oder Zufall; Kartenvorgaben gelten je Position),
  Farbe (`PlayerColors.Palette`, nach Farbton sortiert, Kennungen `Choices`; Klick öffnet Auswahl darunter mit „?“ und allen Farben, belegte
  durchgestrichen; Spieler 1 vorbelegt mit „Bevorzugte Farbe“ der Einstellungen, Vorgabe Rot; „Zufall“ → `PlayerColors.Assign`), Team (Klick öffnet Auswahl 1–4/„-“, Vorgabe „-“ = keins; `World.Teams`,
  nach dem Laden gesetzt; `TeamOf`, `AreAllies`), Hardcore.
  Mehrspieler-Startpositionen gelost (Vorschau schwarz „?“, Klick weist zu/zurück; `StartScreen.ResolvePositions` → `OriginalScenario.Load(positions:)`).
  Hardcore (`World.HardcorePlayers`, `Hardcore`): +10 % Kampfkraft flach (auch über 100 %), +30 % Bewohner je Wohnhaus, +20 % Soldaten-LP
  (`World.MaxHealth`), −25 % Baumaterial je Stapel (gerundet, mind. 1, `World.ConstructionCost`), Arbeitszeiten × 0,9,
  Start +2 Schwertkämpfer, +5 Bretter, +5 Stämme (`OriginalScenario.Load(hardcore:)`).
- **Seitenpanel** (`SidePanel`, große Variante, auf Fensterhöhe skaliert; Menüs zeichnen/prüfen Klicks im Panelraum, `SidePanel.ToPanel`):
  - Minimap oben im Originalrahmen (dahinter Schwarz; rechts schmaler Goldrand mit Ranke, Datei 4 Sequenz 3): erkundetes Gelände (`Minimap`; nicht mehr in Sicht: halb so hell, ohne Fremdes),
    eigenes immer weiß, Verbündete türkis (`Minimap.AllyColor`, wie eigenes auch unter Fog; Grenzpfosten auf der Karte bleiben in Spielerfarbe), Gegner immer rot – Gebietsgrenzen und Punkte (`MinimapDot`):
    Soldaten, Priester, Geologen, Pioniere, Späher draußen (fremde unter Fog nur in Sicht, Späher erst enttarnt), Gebäude/Baustellen als größere schwarze Quadrate (besetzte Militärgebäude innen weiß/rot), sichtbarer Ausschnitt als weißer Rahmen. Klick: Kameraflug 0,35 s; Ziehen verschiebt den Rahmen; Scrollen bricht den Flug ab;
    Rechtsklick/Alt+Rechtsklick mit Auswahl: Befehl dorthin wie auf der Karte.
  - Event-Buttons rechts neben dem Computer-Button (`EventButtons`: Info, Kampf, Rohstoffe, Handel; Platz/Bilder in `events.json` `categories`):
    neues Event → Button blinkt `blinkSeconds` mit 1 Hz, leuchtet dann bis gesehen. Klick (neuestes ungesehenes der Art, sonst neuestes) bzw.
    Leertaste (neuestes ungesehenes; gehalten nach 1 s mit 10 Hz) → Event-Seite (`PanelTab.Event`: Kategorie, Text weiß), Kameraflug,
    vier Pfeile `arrowSeconds` auf Gebäude bzw. lebende Figur. `dates`: Grüße an Kalendertagen (MM-dd), einmal im Jahr
    (`~/.config/homestead/events-shown.json`; `HOMESTEAD_EVENT_DATE=12-25` täuscht vor).
  - Info-Feld (`InfoFeed`): weiße Notizen unten links im Spielbereich, neue gleiten von unten ein und schieben ältere hoch (höchstens 6), je 60 s;
    mit Spielerbezug runder Punkt in Spielerfarbe davor (`InfoFeed.Add(…, dot)`);
    Texte `info.*`: KI-Spieler besiegt (`ConquestGoals.IsDefeated`, vor dem Laden Besiegte nicht; seine Farbe), Abriss-Rückfrage am eigenen
    letzten fertigen Militärgebäude „Dies ist der letzte Turm“ (eigene Farbe), gespeichert/automatisch/fehlgeschlagen,
    „Seit n Minuten nicht gespeichert“ alle 30 min Echtzeit seit Speichern/Start/Laden (`MapGame.NoteNews`).
    Chat (`ChatInput`, auch nach Niederlage): Enter öffnet unten eine Eingabezeile („> “, Caret), dann Terminal-Ansicht: letzte 12 Zeilen (auch
    verblasste, Verlauf 100) auf dunklem Grund; die Tasten gehen nur in die Zeile (keine Hotkeys/Kamera/F12, Maus geht weiter); Enter sagt sie als
    Spieler 1 in `World.Chat` (`ChatLog`, gespeichert), Esc verwirft, ↑/↓ frühere Zeilen, Strg+V fügt ein (`SdlClipboard`). Neue Chatzeilen aller
    Spieler kommen als `chat.line` („Spieler n: …“, Punkt in Spielerfarbe) ins Info-Feld; Geladenes nicht.
    Befehle (`/…`, nicht im Chat, `MapGame.RunCommand`) nur mit `"cheats": true` im Abschnitt `display` der `settings.json` (`DisplaySettings.Cheats`,
    nicht im Einstellungsbildschirm); sonst sagt ein zufälliger Computergegner (nicht verbündet, nicht besiegt) `chat.noCheats` im Chat.
    `/cheats` öffnet/schließt das Cheat-Menü (`CheatMenu`: Soldaten je Typ/Stufe, Priester, Geologen, Späher, Pioniere, Träger,
    Kriegsmaschinen, +50 Mana, Mission geschafft/verloren, Fog of War an/aus); `/attackme`: alle nicht besiegten Computergegner `AiPlayer.Assault(0)` (Info `chat.attackMe`); Unbekanntes: `chat.unknown` mit der Liste.
    KI-Chat (`AiChat`, nur mit Schlüssel: Abschnitt `aiChat` der `settings.json` mit `apiKey`, `model` (Standard `claude-haiku-5-5`, ohne Denken;
    andere Modelle denken immer), `effort` (`low`), sonst `ANTHROPIC_API_KEY`; Token je Anfrage auf der Konsole): jede eigene Zeile → im Hintergrund eine Anfrage an die Claude-API (Anthropic-SDK), eine zur Zeit
    (dazwischen Gesagtes geht mit der nächsten). Prompt und Charakterstimmen in `data/ai-chat.json` (System gecacht), Lage `AiSituation`.
    Claude spricht für alle Computergegner über Werkzeuge `say` (→ `World.Say`), `attack` (→ `AiPlayer.Challenge`, `all_in` → `Assault`;
    je Gegner höchstens alle `attackCooldownSeconds`), `truce` (→ `Truce`, bis `truceMaxMinutes`) und `defend` (→ `Help`, bis `helpMaxMinutes`); angewendet im Spiel-Thread, Antworten zu einem anderen Spiel verworfen; Fehler `chat.aiFailed`;
    unaufgefordert (`AiChat.Reactions`, `reactions` in ai-chat.json): Waren, die eigene Esel auf dem Land eines Computergegners abladen
    (`World.GoodsHandedOver`), nach `giftValues` aufsummiert (Gold 10 … Nahrung 2, Rest 0,2, Stamm −1), bei `giftPleased` (60) bzw.
    `giftInsulted` (−8) eine versteckte Anfrage „das ist passiert“; eroberte Militärgebäude (`buildingCaptured`/`buildingLost`, `captureChance` 15 %) und große
    Kämpfe (≥ `bigFightSoldiers` Tote je Statistik-Probe, `chance` 30 %), je Gegner höchstens alle `cooldownSeconds` (90);
    eigene Armee rückt bei einem Gegner an (`army`, Verbündete nicht: ≥ 20 eigene Soldaten draußen in seinem Land oder ≤ 20 Felder vor seinen Militärgebäuden, je Sekunde
    gezählt, beim Erreichen) mit 30 %, je Gegner höchstens alle 10 min; Prompt: Bündnisse halten das ganze Spiel, verbündete Soldaten im Land schützen; Angriffsbefehle mit Ergebnis auf der Konsole; als `\u00e4` geschriebene Umlaute zurückgewandelt.
    Mehrspieler später: nur der Server fragt und verteilt die Ergebnisse als Befehle.
  - Computer-Button unten links (`SystemMenu`, `PanelTab.System`): „Spiel speichern“ (8 Slots mit Karte, Datum, Volk; leere gestrichelt;
    Klick speichert, auch überschreibend), „Spiel beenden“ mit Rückfrage (Ja unten links → Statistik, Nein unten rechts).
  - Hauptreiter Bauen/Waren/Siedler (`PanelTabs`: Original-Buttons Datei 03, aktiver gedrückt; Klick hebt Auswahl auf; Untermenüs `panelTabs` in `ui.json`).
    - Bauen: Baumenü (Kategorien aus `data/ui.json`, Original-Buttons; Klick aufs Icon → Platzieren; eigenes Volk; Zahl fertiger (weiß) und im Bau (orange); Tooltip mit dem Namen nach 1 s Verweilen auf einem Icon, weg beim Verlassen – `BuildMenu.DrawTooltip`, im Bildschirmraum, darf über die Karte ragen).
    - Waren › Lagerbestand (`StockMenu`, `World.InStock`: Lager-, Ausgabe-, lose Stapel; nicht Eingang/Baustellen).
    - Waren › Herstellung (`ManufactureMenu`): je Produkt Bestellung mit Auf/Ab (`Production.Ordered`/`ChangeOrdered`, zuerst gemacht) und
      Anteilsbalken (`ShareBar`: Klick setzt, Ziehen, 5-%-Schritte, nahe Enden 0/100 %).
    - Waren › Verteilung (`DistributionMenu`): Waren mit ≥ 2 Abnehmern (Reihenfolge `distributionMenu`), rechts je Abnehmer Anteil + `ShareBar`.
    - Waren › Warentransport (`TransportMenu`): Transportreihenfolge in Sechserreihen (versetzt); Punkt grün = einlagern, rot = nicht; gewählte
      Ware: einlagern/nicht, ganz hoch, hoch, runter, ganz runter (Bilder `transportMenu`); Icons gleiten.
    - Siedler › Siedlerstatistik (`SettlersOverview`): Wohnraum, Bedarf, alle Siedler, Soldaten, je Art Porträt + Anzahl (Esel 0; ohne Porträt Figur im Rahmen).
    - Siedler › Berufe (`ProfessionsMenu`): Anteile ±5 % (Träger mindestens, Planierer/Bauarbeiter höchstens; `Recruitment.ShareOf`/`SetShare`,
      Vorgaben `keptCarriers`/`maxDiggers`/`maxBuilders` 30/25/25 %); Spezialberufe Geologen, Pioniere, Späher, Pioniere→Träger (Träger-Porträt)
      mit Such-Buttons (alle auf Karte, alle im Bild, nächster reihum mit Kameraflug) und 5/1 umwandeln
      (`Geology.ConvertCarriers` mit `among`: nur Träger im Bild, der Bildmitte nächste zuerst – keiner im Bild: Hinweis; `Geology.PioneersToCarriers`); Umgewandelte sind sofort gewählt (das Menü bleibt offen, `_menuOverSelection`), noch Liefernde kommen nach der Umwandlung dazu.
    - Siedler › Krieger (`SoldiersMenu`): Kampfkraft, göttliche Aufwertung (Mana-Fortschritt, je Typ Waffen-Button, verziert ab Stufe 2),
      automatische/manuelle Rekrutierung mit freien Trägern; manuell je Typ Waffen im Vorrat und geplante Rekruten (⇈ +5, ↑ +1, ↓ −1, ⇊ −5;
      `Military.SetManualRecruitment`/`ChangePlanned`).
    - Siedler › Produktion (`StatisticsMenu`): Diagramm letzte 60 min einer Ware (Einheiten/min, 2-min-Mittel, Skala passt sich an);
      Warenauswahl `GoodsGrid` (mit Lagerbestand geteilt: Icons `goodsGrid` in `ui.json`, Indizes aus JSettlers' EMaterialType, nicht herstellbar dunkel);
      gezählt in `World.Statistics` (`ProductionStatistics`, 30-s-Abschnitte).
  - Gebäude-Klick ersetzt das Baumenü (ohne eigenen Hintergrund): Name, Bild (unskaliert, unten zentriert, beschnitten –
    `EconomyRenderer.DrawBuildingImage`; bei Enge erst Waren weg, dann Bild niedriger), bei eigenen Status, oben rechts Abriss-Icon wie im Original (Datei 03 GUI 199, `buildingInfo` in `ui.json`) mit Rückfrage wie im Original (nur das Gebäudebild bleibt an seinem Platz, Frage unten, Ja/Nein als helle Texte; jede andere Aktion – Klick daneben beim Loslassen, andere Auswahl, Esc – bricht ab);
    oben links wie im Original „Arbeit einstellen“ (Haus, GUI 395)/„Arbeit fortsetzen“ (Hand, 194) für Produktion und Baustellen (`World.SetPaused`:
    Arbeit fertig, dann drinnen; Baustelle: keine Lieferungen/Arbeiter, Anwesende nach ihrem Takt frei), bei Baustellen daneben „Höchste Priorität“
    (Zahnräder 197, gedrückt mit „!“ 380; `World.SetPriority`), Name dann unter den Icons, Hinweistext über ihnen unter der Maus; Militär: Besatzung je Typ (da/Soll, +/−);
    Lagerhaus: alle Waren (`GoodsGrid`; Punkt = allgemeine Regel, Klick spezialisiert – roter Rahmen –, nochmal hebt auf);
    Markt (`BuildingInfo.Market`, „Handel – Land“, ohne Bild; Hafen „Handel – Meer“: Leiste GUI 377 `seaRoute`, Schiff vor ×1 setzt den Steg wie
    der Hammer der Werft, Routenpunkte aufs Wasser, Route ab dem Liegeplatz): Leiste ×1 ×2 ×3 Paket (Datei 03 GUI 374, `market` in `ui.json`; nicht gesetzt
    dunkel): Knopf wählen (weiß gerahmt; Stopps erst mit Ziel; gewählter Stopp nochmal = löschen), dann Linksklick auf die Karte setzt
    ihn (Fahne als Vorschau am Mauszeiger, nur wo Esel hinkommen; Rechtsklick/Esc bricht ab), Route wie der Sammelpunkt der Kaserne; Waren mit Menge darunter, ∞ (386)/0 (389)/Pfeile ±5/±1 für die gewählte Ware; Waren/Baumaterial,
    Minen-Erfolgschance; Arbeitsbereich (Rechtsklick versetzt, `World.SetWorkCenter`, max. `MaxWorkCenterDistance` = 20, sonst nächstes erlaubtes
    Feld; Sammler nehmen das Ziel nächst der Mitte; Kaserne: Sammelpunkt überall, `Building.HasGatheringPoint`, Pfad mit Fahne).
    Werft (`BuildingInfo.Dockyard`, `dockyard` in `ui.json`): links Fähre und Handelsschiff (letzte 12 GUI-Bilder der Volksdatei; mit Steg
    klickbar → Auftrag, der Knopf bleibt eingedrückt (dunkles Bild), beide gesperrt bis zum Stapellauf; unten Baumaterial geliefert/nötig),
    rechts Hammer (Datei 03 GUI 392) → nächster Linksklick ins Wasser setzt den Steg (blasse Vorschau am Mauszeiger; Rechtsklick/Esc bricht ab;
    während des Baus gesperrt).
    Deutsche Namen/Zustände in `data/ui.json`; Text per FontStashSharp mit Systemschrift.
- **Statistik** (`StatisticsScreen`; „Spiel beenden“, nach verlorenem Spiel Esc; Spiel steht still): blauer Marmor (`statisticsBackground`, Datei 02 GUI 29),
  Seiten Übersicht (Tabelle, Landanteil), Wirtschaft, Militär, Waren (jede Ware je Minute), Gesellschaft – Diagramme `Charts` (Linien mit Werten
  unter der Maus, Anteile, Balken) aus `GameHistory`/`ProductionStatistics`; ←/→/Tab blättern; „Weiterspielen“ (Esc; nicht nach „Spiel beenden“),
  „Zum Hauptmenü“.
- **Spielernamen** im Spiel (Statistik, Chat, Niederlage, Trait-Hinweise): `PlayerNames.Of` → `start.playerPeople` („Spieler 2 (Römer)“),
  nie der KI-Charakter (nur noch in der Konsole beim Start). Im `InfoFeed` (Chat, Niederlage) steht der Name in der Spielerfarbe
  (`Add(…, names)`; sehr dunkle Farben aufgehellt), der Rest weiß. Notizen breiter als die Box (höchstens 40 × Schriftgröße) brechen an
  Leerzeichen um (zu lange Wörter werden geteilt), Folgezeilen weiter eingerückt (`Hang`, 1,25 × Zeilenhöhe); Umbruch je Notiz gecacht.
- **Textbuttons** (`TextButton`): Steinknöpfe aus den Menüs vor dem Spiel (`textButton` in `ui.json`, Datei 02 GUI 0, 9-Slice), z. B. Abreißen,
  Anhalten, Ja/Nein, Rekrutierung, Statistik. Besatzung ±: Auf/Ab-Spinner wie in der Herstellung.
- **Auswahl/Befehle**: Rahmen oder Klick wählt eigene Figuren, Vorrang Soldaten > Kriegsmaschinen (nie zusammen) > Priester > Geologen > Späher > Pioniere,
  Träger und andere Berufe nie (Rahmen ohne solche Figuren: das der Mitte nächste Gebäude; Gebäude: weißer Eckrahmen 3 px). Shift fügt hinzu,
  Strg+Klick alle eigenen dieses Berufs im Bild (Soldaten: alle), Alt+Klick dieses Typs im Bild (Soldaten: Typ, Rang egal). Ausgewählte wie im Original: zwei weiße Eckklammern über dem Kopf, dazwischen
  runder Punkt mit schwarzem Rand grün→gelb→rot nach LP (`DrawSelectionMarker`).
  `SelectionPanel` je Art (Typ/Stufe und Volk – Bekehrte zeigen ihres) Porträt (`Portraits`, `ui.json` `portraits`; andere Völker `MapGame.PortraitsOf`) + Anzahl (zu viele: gestaucht, mehr Spalten oder kleiner), unten Kampfkraft – wie Zaubermenü
  im Inhaltsbereich (`BuildMenu.Content`). Strg+1–9 speichert Gruppe (auch Schiffe; jede Figur/jedes Schiff in höchstens einer – sie verlässt ihre alte; Porträt des ersten Mitglieds wie im `SelectionPanel` bzw. Schiffs-Knopfbild, gleich hoch, breitere Bilder (Kriegsmaschine, Schiff) breiter, folgende rücken weiter, oben rechts, gewählte gelb gerahmt; Klick oder 1–9 wählt; ist die Auswahl genau eine Gruppe, kommt per Shift Hinzugefügtes auch in die Gruppe; zweimal binnen 0,5 s: Kameraflug zum Mittelpunkt; Tote, Gebäude Betretende, Bekehrte fallen raus; im Spielstand als Client-Wert `groups`).
  Soldaten: Rechtsklick hinschicken/angreifen, Strg+Rechtsklick erzwungen ohne Kampf. Schiffe (`_ships`, statt Siedlern; Klick, Strg: alle im Bild,
  Rahmen ohne Siedler darin): Rechtsklick fährt hin; Panel: je Art Knopfbild, Name, Anzahl, Fähren „An Bord: n/7“, Handelsschiffe „Ladung: n“, unten „Schiff entladen“ (`BuildingInfo.DrawShips`/`UnloadAt`).
  Gewählte Einheiten: Rechtsklick auf eine eigene Fähre nahe Land → einsteigen; an Deck stehend gezeichnet (`ShipRenderer`: ganzes Schiff, Passagiere hinter
  dem Mast, Vorderteil = Sequenz davor, Segel, Passagiere vor dem Mast). Geologen: Rechtsklick hin, auf Gebirge suchen.
  Priester: Zaubermenü wie im Original statt Auswahl (`SpellMenu`: 8 Zauber je Volk, `menu` in `magic.json` – umgesetzte und Platzhalter mit
  geratenem Namen/Kosten, nicht wirkbar; Symbole `spellMenu` in `ui.json`; Ladebalken Mana/Kosten: unter 1 Verlauf, dann je Ladung ein Streifen,
  hell/dunkel im Wechsel, jeder 1,35× so breit wie der vorige, ganzer Balken = alles Mana (im Original gemessen); oben Name/Kosten unter der Maus bzw. Zielhinweis, unten Mana), Zauber wählen, dann Ziel
  (Zielring rot: wirkt auf niemanden); Rechtsklick hin.
  Träger umwandeln nur über Siedler › Berufe. Linksklick ins Leere wählt ab (Figuren, Gebäude, Menüs) und leert das Panel wie im Original
  (`PanelTab.None`, kein Reiter gedrückt); rechts ziehen verschiebt die Karte.
- **Tasten**: Enter Chat (siehe Info-Feld), Esc bricht Platzieren/Zielen ab, sonst Pause wie P; keine Cheat-Tasten (nur `/cheats`, siehe Info-Feld);
  F Angriff aufs feindliche Militärgebäude unter der Maus, Fog of War (Sicht 1×/s, `FogLayer` blendet weich,
  klingt über `SoftFields` (3) aus; unerkundet schwarz, erkundet dunkel, fremde Figuren nur in Sicht; an/aus nur im Cheat-Menü), F11 Vollbild (nur für die Sitzung; Vorgabe in den Einstellungen),
  F2 Skalierung scharf/linear/Catmull-Rom (Standard)/Lanczos2 (Welt 1:1 in kleine RenderTarget, um den Zoom vergrößert; Markierungen/Panel scharf;
  `Shaders/Upscale.fx` → eingebettetes `.mgfx`, nach Änderung `tools/compile-shaders.sh`, braucht Wine),
  F3 Leistungsanzeige (FPS, ms Simulation/Update/Zeichnen, längster Frame, Draw-Calls; Zeichnen nur, was in den sichtbaren Zeilen und
  waagerecht im Bild liegt – Rand 96 px, Gebäudestreifen nach Pixelbreite bis 320 px, Schiffe 160 px), F12 gehalten 10× Tempo, T Spielzeit, I KI spielt für dich
  (andere Spieler immer KI), L KI-Entscheidungen in die Konsole,
  P/Esc Pause (Banner wie „Spiel gewonnen“; Welt steht, Kamera/Panel/Befehle gehen), Platzieren über das Baumenü (Punkte an passenden Bauplätzen, grün→rot nach Planieraufwand – `World.LevellingEffort` / `levelling.maxWork`; max. 6 ms je Frame,
  `World.BuildSpotEffort`; Vorschau am nächsten Bauplatz; Q/E Gebäude; endet nach Platzieren, Rechtsklick bricht ab).
- **Optionen**: `--scene map` (Standard) / `--scene walk` (Lauf-PoC); `--load <slot>` (1–8; Gelände/Objekte aus der Karte, dann Feld für Feld auf Stand);
  `--map <name>` (Standard: erste Mehrspieler-Karte; `random[:spieler[:none|mirror|repeat[:seed]]]`); `--peoples amazon,roman,…` (offene Völker,
  Spieler i: Eintrag i mod n; sonst zufällig; Kartenvorgaben 0–3 gelten); `--characters ,general` (KI-Charakter je Spieler, Eintrag i → Spieler i+1, leer = gelost).
- **Ton** (`GameAudio`, nicht in Captures): Effekte aus `SND/Siedler3_00.dat` je `SoundCue` (Nummern in `data/sound.json`, am Original erkannt:
  Schwert 30, Bogen 33 – Ägypter 32 –, Speer 34, Tod 35, Amazone 113, Baum fällt 36, Gebäude fällt 93, Zaubern 75, Heilen 21, Pionier 25/1, Geologe ab
  `minAmount` 24/1–3, Aussaat Bauer 8; Tabelle `docs/formate/snd.md`; `SoundRef`: Nummer oder `{sound, variants}`), Variante zufällig, nur in Sicht (Fog), im Bild voll, außerhalb bis `hearing` halbe Bildbreiten leiser,
  links/rechts gepannt, höchstens `maxVoices` gleichzeitig und `perFrame` je Geräusch und Frame. Aus der Bildmitte: Befehl an steuerbare Einheiten (Laufen/Angriff, Suchen, Stehlen, Land einnehmen, Zaubern; `command` 57),
  Event-Meldung (`events`: Kampf 109, sonst 110), Panel über `UiSounds` (`ui`: Auftrag/Anteil/Besatzung/Transportreihenfolge hoch 47/runter 48,
  Schalter wie Rekrutierung, Anhalten, Einlagern, Lager-Spezialisierung 54, Arbeitsbereich versetzt 22, Klick aufs Abriss-Icon 51). `WorkSounds`: Arbeit draußen je `Activity` (`work`: Fällen 0, Planieren 2, Bauen 1, Steinmetz 3,
  Angeln 16) im Takt der Arbeitsanimation (Moment `at` je Runde, `SettlerRenderer.WorkRoundFrames`); Werkstätten (`workshops`: Sägewerk 5,
  Werkzeug-/Waffenschmied 6/7, Eisen-/Goldschmelze 37/38) etwa alle `everySeconds`, solange `Production.IsProducing`. `Ambience` (`ambience` in `sound.json`): Gelände im Bild alle 0,1 s abgetastet (jedes `sampleStep`-te Feld, `margin` über den Rand hinaus
  ausblendend, Unerkundetes still), je Art laut nach Anteil (voll ab `fullShare`, gleitet in `fadeSeconds`): Meer 68, Fluss 71, Wüste 67, Wind 73/74
  (Gebirge, Schnee) als Varianten ohne Pause aneinander (`overlap`), zur Seite des Geländes gepannt; Bienen 117/0 nach Bienenstöcken im Bild (`objects`, jedes Feld gezählt,
  voll ab `fullCount` 6), leise und nur eins zugleich (`overlap` 0; im Original je Stock eins, übersteuert); Vögel 69/70 auf Gras einzeln alle `everySeconds`
  von einem Feld im Bild; läuft in der Pause weiter, außerhalb des Spiels still. `SwampBubbles` (nur Darstellung, nicht gespeichert): Blasen auf grünem Sumpf (`Moor`, nicht braunem
  Schlamm `Mud`) im Bild (je Feld eine alle `bubbles.perFieldSeconds`, nur in Sicht): Datei 1 Sequenz 106 (wächst), dann 107 (sinkt) oder 108 (platzt, Blubbern 72 – leise Aufnahme, `gain` verstärkt sie beim Laden). `MusicPlayer`: `MUSIC/TrackNN.ogg` (NVorbis gestreamt)
  aus `Tracks` (`SoundConfig.TracksFor`: Liste des eigenen Volks, `music.tracks`, nach Gehör zugeordnet; „Musik je Volk“ aus: alle, eigene zuerst),
  neue Liste beginnt mit ihrem ersten Stück, dann gemischt, Pause dazwischen, neue Liste ohne das laufende Stück blendet aus; in Menüs vor dem Spiel still. Musik/Musik je Volk/Effekte in den Einstellungen
  (Darstellung, `DisplaySettings`).
- **Kriegsmaschinen** (`WarMachineRenderer`, `warMachines` in `ui.json`, Fundstellen `docs/formate/gfx.md`): Körper je Richtung, rollende Räder
  darüber, Schuss je Richtung, Geschosse im Bogen; Gong fährt wie ein Siedler läuft. Einschlag: kleine graue Explosion (Datei 13 Seq. 49);
  beschädigte Gebäude rauchen (42, ab halb 43 dunkel) und brennen (41 ab ¼, 40 ab ½, 39 ab ¾ Schaden), bis sie geheilt sind. Auswahl zusammen mit Soldaten (`SelectionKind`, Porträt
  `WAR_MACHINE`), Rechtsklick: hin bzw. Militärgebäude beschießen; Munitionszahl über dem Punkt; „Munition laden“ im Auswahl-Panel.
  Werkstatt-Panel (`BuildingInfo.WarWorks`): Maschine/Munition (Klick +1, Strg+Klick zurück, ×n bestellt), Ladungen, Material des
  aktuellen Auftrags. Ton `machineShot` (geraten), Treffer `machineHit` 88/0. Cheat-Menü: 2 Kriegsmaschinen.
- **Darstellung**: Schiffe (`ShipRenderer`, `ships` in `ui.json`): Datei 36, je Schiff 4 Sequenzen (Deck, Schatten, Vorderteil, ganz ohne Segel)
  à 6 Richtungen, Segel 28–34 mit Spielerfarbe; kleines Schiff = Fähre, großes = Handelsschiff (Römer im Spiel geprüft), Ladung als Stapel an Deck; im Bau der Rumpf von unten wachsend. Wellen (`Terrain/WaveLayer`): lange gerade Dünung läuft diagonal (nach links unten), Wasserecken heben sich bis 4 Höhenstufen (Wasserdreiecke im Bild neu gezeichnet, Licht wie Hänge), Kämme mit hellem Wasser-Tile (Bild 16) überblendet, Vorderseite glänzt, darüber das Original-Glitzern (Datei 01 Seq. 26) als überlappende Kacheln (Abstand ¾ Bild, jede zweite Zeile um einen halben Schritt versetzt, nicht mit den Wellen angehoben), Bild nach der Phase der Dünung an der Kachelmitte – spielt ab, während die Front durchläuft, mehrere hintereinander gleichzeitig (Dünung 18 Felder lang, 1,8 Felder/s); Stärke nach Uferabstand (1 Feld ruhig, dann über 14 voll, weich); Schiffe schaukeln mit (nur Darstellung); stehen in der Pause. Figuren nach Volk; Tote: Geist in Spielerfarbe (Datei 12 Seq. 27, `World.Ghosts`); Zauberwirkungen als Original-Statussymbole über dem Kopf (`StatusIcons`, `statusIcons` in `ui.json`: Datei 1 Sequenz 128–133 – Samuraischwert, Stärken, Angst, Schild, Verfluchte Pfeile, Eingefroren –, mehrere nebeneinander; 134/135 Planieren uneben/sehr uneben, 136–138 Info-Zeichen, noch ungenutzt); Stehende ohne Aufgabe wechseln alle 2–30 s
  die Blickrichtung (nur Darstellung); Turm-Bogenschützen mit Schussanimation im Takt, ohne Schatten (wie im Original), Infanterie im Turm als Fensterbild; Gebäude: Ebene 0, Besatzung, dann weitere Ebenen (Fensterrahmen/Brüstungen davor); in 8-px-Streifen gezeichnet, jeder in der vordersten Sperrfeld-Reihe seiner Spalte (`EconomyRenderer.Strip`, sonst verdeckt ein hohes Gebäude, was seitlich davor steht); Pfeile im flachen Bogen
  (`docs/formate/gfx.md`). `SettlerAnimations` löst Typ/Aktion/Ware/Richtung über `data/settlers.json` (generiert mit
  `tools/data-import/convert_jsettlers_movables.py`, nicht von Hand ändern). Baustellen zeigen Schild und Pfosten.
