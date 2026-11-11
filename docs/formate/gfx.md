# GFX-Format (`GFX/siedler3_NN.<maske>.dat`)

Stand: an der GOG Ultimate Collection verifiziert, abgeglichen mit JSettlers
(`jsettlers.graphics/.../image/reader`). Alle Werte little endian.

## Dateien
- Jede Nummer liegt in zwei Farbvarianten vor: `.7c003e01f.dat` = RGB555, `.f8007e01f.dat` = RGB565
  (Suffix = Farbmasken R/G/B). Inhalt sonst gleich. Wir nutzen 565.
- Groß-/Kleinschreibung der Namen ist uneinheitlich (`Siedler3_` vs. `siedler3_`).
- **Nr. 18 ist keine Grafik**, sondern eine Windows-DLL (`MZ`). Wird übersprungen, nicht analysiert.
- **Nr. 07/08/09** haben keine Bilder, aber eine Texttabelle (Block `0x1904` direkt hinter dem Header), siehe unten.

Grobe Belegung (visuell bestätigt: 00, 12, 13; Rest Vermutung anhand der Tabellengrößen und JSettlers):

| Nr. | Inhalt |
|-----|--------|
| 00 | Landschaftstexturen (235) |
| 01 | Siedler-Animationen allgemein, mit Schatten |
| 02, 03, 14, 24, 34, 44 | GUI-Grafiken |
| 10/20/30/40 | Siedler je Volk (Römer/Ägypter/Asiaten/Amazonen), mit Torso und Schatten |
| 11/21/31/41 | weitere Siedler-Animationen je Volk |
| 12/22/32/42 | Militär je Volk: Kriegsmaschine, Soldaten (JSettlers: Sequenz 14–29) |
| 13/23/33/43 | Gebäude je Volk (Römer: Sequenz 0–45, Frame 0 = fertig, 1 = Baustelle), danach Effekte (Feuer, Rauch, Magie) |
| 15/25/35/45 | nur Animationstabellen (`0x21702`) |

### Einzelne Fundstellen
- Datei 01, Sequenz 26 (33 Bilder, 64 × 36): türkise Glitzerpunkte, Bild 0/1 und 30–32 leer, dazwischen wachsend, dann vergehend –
  das Glitzern auf dem Wasser (`WaveLayer.DrawGlitter`).
- Bienenstock (Amazonen-Imker): kein eigenes Bild gefunden (gesucht in Datei 01, 41, 43, 46). Den Strohkorb gibt es nur auf dem Rücken
  der Imkerin (Datei 41, Sequenz 287 laufend, 291–296 aufstellend). Ersatzweise zeigt der Stock 41/291 Bild 0 (die Imkerin beim Aufstellen).
- Datei 43, Sequenz 33: Amazonen-Marktplatz – Bild 0 der runde Platz, Bild 1 die goldene Statue darauf (fertig = beide übereinander),
  Bild 2/3 dasselbe als Baustelle.

### Kriegsmaschinen (visuell bestätigt, `warMachines` in `data/ui.json`)
JSettlers kennt sie nicht; Fundstellen aus den Dateien selbst. Richtungen immer SW, W, NW, NE, O, SO.

| Volk | Maschine (Datei 12/22/32) | Werkstatt (Gebäudedatei, GUI-Icon) | Icons (GUI der Volksdatei, je drei Größen) |
|------|------|------|------|
| Römer | Katapult, Datei 12: Seq. 0 Körper (6 Bilder, je Richtung), 1 Räder (216 = 36 je Richtung, über dem Körper), 2–7 Schuss (24 Bilder), 8 Stein (12, dreht sich) | 13/27, Icon 14/96 | 234 Katapult, 237 gedrückt, 240 Munition (Steine), 243 gedrückt |
| Ägypter | Balliste, Datei 22: wie Römer, Schuss 15 Bilder, 8–13 Bolzen je Richtung (3 Bilder) | 23/31, Icon 24/99 | 228/231/234/237 |
| Asiaten | Kanone, Datei 32: wie Römer, Schuss 18 Bilder, 8 Kugel (12) | 33/34, Icon 34/111; Pulvermacherei 33/28, Icon 34/105 | 243/246/249/252 |
| Amazonen | Gong, Datei 42: Seq. 0 fährt wie ein Siedler läuft (6 × 12), 1–6 Gongschlag (39) | 43/32, Icon 44/102 | 237/240 (243/246: Steine, ungenutzt) |

## Header
| Offset | Inhalt |
|--------|--------|
| 0 | Signatur `04 13 04 00` |
| 33 / 37 | Farbmasken (`7c`/`03` = 555, `f8`/`07` = 565) |
| 48 | Dateilänge (u32), muss der echten Länge entsprechen |
| 52 | 8 × u32 Offsets auf Blöcke |

## Blöcke
`u32 typ, u16 byteCount, u16 anzahl, u32[anzahl] offsets`, bei Offset-Tabellen gilt
`byteCount == anzahl*4 + 8`.

| Typ | Inhalt | Eintrag zeigt auf |
|-----|--------|-------------------|
| `0x1904` | Header-Ende / Texttabelle (07–09) | – |
| `0x2412` | Landschaft | Bitmap (Landschafts-Header) |
| `0x2607` | Palette? (immer leer, 4 Byte extra) | – |
| `0x106` | Siedler/Gebäude (Farbe) | Sequenz |
| `0x3112` | Torso (Spielerfarbe) | Sequenz |
| `0x5982` | Schatten | Sequenz |
| `0x11306` | GUI | Bitmap (GUI-Header) |
| `0x21702` | Animationstabellen | noch unbekannt |

Torso- und Schatten-Tabellen sind oft kürzer als die Siedler-Tabelle und **nicht** per Index zugeordnet.
Regeln aus JSettlers (`AdvancedDatFileReader.initialize`) übernommen, siehe `GfxFile.AlignLayers`:
- Torso: fehlende Einträge liegen am Anfang (Torso `i - (siedler - torsos)`).
- Schatten: bei Differenz 7 oder 8 fehlen sie am Anfang, sonst am Ende.
- Sonderfälle, erkannt an den Tabellengrößen: Gebäude (Differenz 26, Dateien 13/43), Schiffe (28, Datei 36),
  römische Spezialisten (239 Einträge, Datei 11).
- Soldaten (Dateien 12/22/32/42, eigene Zuordnung nach Bildanzahl, die Schatten für Schatten übereinstimmt):
  Schatten 0–18 gehören zu den Soldaten ab Sequenz 9 (Römer, Asiaten), 14 (Ägypter) bzw. 7 (Amazonen),
  Schatten 19–25 zu den Sequenzen davor (Sequenz 0, dann 2–7; Amazonen 0–6). JSettlers verschiebt Römer/Asiaten um 8
  und Amazonen um 6 – Bogenschützen bekommen dann Nachbarschatten, schießende Stufe 3 einen leeren.

Einzelne Sequenzen (visuell bestätigt):
- Datei 01, Sequenz 100–105: fliegender Pfeil je Richtung (SW, W, NW, NE, O, SO), 3 Frames: steigend, waagrecht, fallend.
- Dateien 12/22/32/42: hinter `BOWMAN_L3`/`ACTION1` folgt eine Sequenz mit 22 Frames (Farbebene leer), dann 6 Sequenzen
  à 2 Frames: Infanterie im Turmfenster (Schwertkämpfer, dann Pikeniere, je Stufe 1–3; Frame 0 blickt nach links,
  1 nach rechts). Römer/Asiaten 28–33, Ägypter 33–38, Amazonen 26–31.
- Bogenschützen `ACTION1` (24 Frames je Richtung): der Pfeil verlässt den Bogen nach Frame 17.
- Datei 04 (Sequenzen, keine GUI-Tabelle): Seitenpanel links, je Sequenz 3 Frames für 640/800/1024 Pixel Breite.
  Sequenz 0/1: Minimap-Rahmen (links/rechts, 136×142, 176×170, 216×224), Sequenz 2: Panel darunter
  (136×338, 176×430, 216×544), Sequenz 3: 8 Pixel breiter Streifen. Großes Panel ausgemessen: Hauptreiter y 0–62,
  dunkle Leiste für Unterreiter y 64–102, Inhaltsbereich x 10–206, y 116–474, unten fünf Knöpfe.

GUI-Bilder (Tabelle `0x11306`):
- Gebäude-Icons (14/24/34/44): je Gebäude drei Größen hintereinander (klein, mittel, groß); JSettlers verweist auf die kleine.
- Datei 03, Gebäude-Panel (je klein/mittel/groß): 192–194 Hand (Arbeit fortsetzen), 195–197 Zahnräder (höchste Priorität; 378–380 mit „!“),
  198–200 einstürzendes Gebäude (Abriss), 201–203 Ringe (Arbeitsbereich: rot außen, gold, grün in der Mitte), 390–392 Hammer mit Pfeil
  (Steg der Werft), 393–395 Haus (Arbeit einstellen).
- Datei 03, Bau-Kategorien (je 27×24, 36×32, 45×40): 69–71 Grundgebäude, 72–74 Nahrung, 75–77 Militär,
  78–80 Soziales; 81–92 dieselben gedrückt (gewählt). 51–68: Hauptreiter (Bauen, Waren, Siedler; je 33/44/55, ab 60 gedrückt).
- Datei 02, GUI 20 (100×600): blaue Hintergrundtextur.

## Sequenz
`02 14 00 00 08 00 00`, `u8 frameAnzahl`, dann `u32[frameAnzahl]` Offsets **relativ zum Sequenzanfang**.

## Bitmap-Header
- Siedler/Torso/Schatten: `u32 0x0c, u16 breite, u16 höhe, s16 offsetX, s16 offsetY`
- Landschaft: `u16 breite, u16 höhe, u16 ?`
- GUI: `u16 breite, u16 höhe, u16 ?, u16 ?`

Danach auf gerade **absolute** Dateiposition auffüllen (1 Byte Padding).

## Pixeldaten
Zeilenweise, pro Zeile eine Folge von Runs mit u16-Steuerwort:
- Bit 0–7: Anzahl folgender Pixel
- Bit 8–14: davor zu überspringende transparente Pixel
- Bit 15: letzter Run der Zeile

Pixel: Farbe = u16 (555/565), Torso = u8 als Index in die Spielerpalette (aus den Nachbarpixeln abgeleitet): 0–21 Rampe
dunkel → hell, danach Rampen à 9 Stufen (0x16–0x1E, 0x1F–0x27, … 0xF7–0xFF), meist verstreut zwischen der ersten.
Die Palette steht in keiner Datendatei; wir tönen jede Rampe mit der Spielerfarbe nach ihrer Stufe (`GfxColor.TorsoToRgba`).
JSettlers maskiert 5 Bit als Helligkeit – das ergibt schwarze und weiße Punkte.
Schatten = keine Daten (halbtransparentes Schwarz).
Kleine Landschaftskacheln nutzen Cyan als Hintergrund/Schlüsselfarbe.

## Texttabellen (Dateien 07–09, Block `0x1904`)
`u32 0x1904, u16 byteCount, u16 0, u16 anzahl, u16 sprachen (8)`, dann `sprachen × anzahl` u32-Offsets (absolut in der
Datei, Sprache für Sprache), dahinter die Texte nullterminiert in Windows-1252 (Polnisch 1250, Koreanisch 949).
Sprachen: 0 Deutsch, 1 Englisch, 2 Italienisch, 3 Französisch, 4 Polnisch, 5 Spanisch, 6 Koreanisch, 7 unbekannt.
Lesen: `GfxFile.ReadTexts(sprache)`.

Datei 07 (204 Texte): 0–23 Missionsbeschreibungen der Kampagne (je 8: Römer, Ägypter, Asiaten; Missionsname,
Sieg-/Niederlagebedingung, Situation, Tagebuch), 24–47 Missions-CD, 48–71 Amazonen, 72–95 Hinweise zur Kampagne
(gleiche Reihenfolge), 96–143 „Tips & Tricks“ der Missions-CD und Amazonen, 144–202 Tutorial. Dateien 08 (399) und
09 (374): noch nicht untersucht.
