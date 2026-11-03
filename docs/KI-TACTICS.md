# KI-Taktiken und Charaktere

Ideensammlung, wie die Computergegner herausfordernder, taktischer und überraschender werden.
Grundlage ist die bestehende KI (`src/Homestead.Simulation/Ai/`, Werte in `data/ai.json`).

## Begriffe

Ein einheitliches Vokabular – in Gesprächen, Doku, Code und Daten:

| Begriff | Englisch (Code, `ai.json`) | Bedeutung |
|---|---|---|
| **Charakter** | character (`AiCharacter`) | *wie* eine KI spielt: eine Liste von Charakterzügen samt Werten |
| **Charakterzug** | trait (`AiTrait`, `Ai/Traits/`) | ein abgeschlossener Baustein des Verhaltens, hängt an Entscheidungspunkten |
| **Entscheidungspunkt** | decision point (Methode in `AiTrait`) | feste Stelle im Denkablauf der KI, an der Charakterzüge entscheiden dürfen |
| **eingebautes Verhalten** | built-in behavior | was die KI ohne Charakterzug tut (Wirtschaft, Verteidigung, Guerilla, Feldzug, Besatzung) |
| **Schwierigkeit** | difficulty | *wie gut* eine KI spielt (Leicht, Mittel, Schwer) – unabhängig vom Charakter |
| **Kennung** | id | englischer Bezeichner in `ai.json` und Code, z. B. `pincer`, `general` |
| **Name** | – | deutscher Anzeigename in `data/ui.json` (`aiCharacters`, `aiTraits`), z. B. „Zange und Finte“, „Feldherr“ |

## Grundgedanke: Charakterzüge und Charaktere

Die KI bleibt ein Spieler mit denselben Befehlen wie ein Mensch. Neues Verhalten kommt aber nicht
fest verdrahtet in `AiPlayer`, sondern als **Charakterzug**: ein abgeschlossener Baustein mit eigener
Kennung, eigenen Werten und klar umrissenem Einsatzort. Ein **Charakter** ist eine Liste von
Charakterzügen samt Werten. Jedes Volk bekommt einen oder mehrere Charaktere; pro Partie wird einer
davon gewählt.

```
Volk ──(einer davon je Partie)──> Charakter ──> Charakterzüge (+ Werte)
```

### Was die Grundlage bleibt

Nicht alles soll abschaltbar sein. Ohne folgendes Grundverhalten wäre eine KI kaputt, nicht anders:

- Wirtschaft nach Build-Order, Wohnraum, Expansion zu Rohstoffen
- Verteidigung zuerst (Feinde im eigenen Land werden immer bekämpft)
- Kein Abriss eines Militärgebäudes, dessen Land eigene Gebäude trägt (seit Abriss Land
  ungeschützt zurücklässt, würde die KI sich sonst selbst verwundbar machen)

Das eingebaute Verhalten (Guerilla, Feldzug bei 1,5:1, Besatzung voll) ist der Charakter **Standard**
(`standard`, ohne Charakterzüge). Die Bewachung nach Bedrohung ist bereits ein Charakterzug
(`guardByThreat`) und beim Standard ausgeschaltet: Dessen untätige Soldaten bleiben stehen, wo sie sind.

### Entscheidungspunkte

Charakterzüge hängen sich an feste Stellen im Denkablauf der KI. Jede Stelle legt fest, wie mehrere
Charakterzüge zusammenwirken:

| Entscheidungspunkt | Wann | Zusammenwirken mehrerer Charakterzüge |
|---|---|---|
| **Besatzung** (`GarrisonLimit`, umgesetzt) | jede Militär-Runde, je eigenem Militärgebäude | Charakterzüge begrenzen die Besatzung; die niedrigste Grenze gilt (mindestens 1) |
| **Angriffsziele** | Planung eines Feldzugs | Charakterzüge bewerten Ziele (Punkte addieren sich), bestes Ziel gewinnt |
| **Truppenführung** (`LeadCampaign`, umgesetzt) | während eines Feldzugs | einer führt den Trupp (z. B. Zange *oder* Hinterhalt) |
| **Gefechtsführung** (`LeadStrike`, umgesetzt) | Beginn eines Strikes (Guerilla, Verteidigung), dann jede Militär-Runde | einer führt den Strike (z. B. Schlachtordnung); ohne ihn gehen alle zugleich rein |
| **Bewachung** (`Guard`, umgesetzt) | jede Militär-Runde, für die übrigen untätigen Soldaten | der erste, der sie aufstellt, entscheidet; ohne ihn bleiben sie stehen |
| **Turmplätze** | Expansion | Charakterzüge bewerten Bauplätze, Punkte addieren sich |
| **Zeitpunkt** (`AllowCampaign`, umgesetzt) | jede Militär-Runde, vor der Planung eines Feldzugs | jeder Charakterzug kann einen neuen Feldzug zurückhalten |
| **Zauber** | Priester am Kampf | einer entscheidet Zauber und Moment |
| **Build-Order** (`ArrangeBuildOrder`, umgesetzt) | einmal beim Start | jeder Charakterzug ordnet die Build-Order, die der vorige hinterließ |
| **Besorgungen** (`RunErrands`, umgesetzt) | jede Militär-Runde, nach der Bewachung | jeder Charakterzug schickt eigene Leute los (z. B. Späher zum Stehlen) |
| **Schwellen** | überall, wo ein Verhältnis entscheidet | Modifikator auf Werte wie `attack.ratio` |

### Soldaten verteilen

Mehrere Charakterzüge wollen dieselben untätigen Soldaten. Feste Reihenfolge:

1. Verteidigung (Grundverhalten)
2. laufende Trupps behalten ihre Soldaten
3. Charakterzüge nach ihrer Priorität im Charakter (erst genannt = zuerst)
4. Rest: Bewachung (falls der Charakter sie hat)

Ein Charakterzug nimmt nur, was er braucht, und gibt Soldaten zurück, wenn sein Vorhaben endet.

### Daten (`ai.json`)

```json
"traits": {
  "pincer": { "minSoldiers": 4, "feintShare": 0.35, ... }
},
"characters": {
  "standard": { "traits": [] },
  "general":  { "traits": ["pincer", "guardByThreat"], "attack": { "ratio": 1.2 }, "military": { "strikeRange": 40 } }
},
"characterWeights": [["standard", 1]],
"civilizations": {
  "Asian": { "characterWeights": [["trickster", 2], ["rusher", 1]] }
}
```

`traits` enthält die Vorgaben je Charakterzug. Ein Charakter kann sie unter der Kennung des
Charakterzugs überschreiben, außerdem jeden allgemeinen Wert (nach dem des Volks). Die Gewichte
(`characterWeights`, je Volk überschreibbar) bestimmen, wie oft ein Charakter gewählt wird.

### Code

- Basisklasse `AiTrait` mit überschreibbaren Methoden je Entscheidungspunkt (`LeadCampaign`, `Guard`;
  weitere kommen mit den Charakterzügen, die sie brauchen).
- Jeder Charakterzug in eigener Datei unter `Ai/Traits/` (`<Kennung>Trait`, z. B. `PincerTrait`).
- `AiPlayer` fragt an jedem Entscheidungspunkt die Charakterzüge seines Charakters.
- Client und Arena: `--characters ,general` legt den Charakter je Spieler fest.
- Notizen eines Charakterzugs beginnen mit seiner Kennung; Client und Arena zeigen sie sofort an,
  der Client mit deutschem Namen von Charakter und Charakterzug.

## Charakterzüge

Status: alle **offen**. Aufwand grob: S (klein), M (mittel), L (groß).

### Bewachung

**`guardByThreat` – Bewachung nach Bedrohung** · **umgesetzt** (beim `general`, nicht beim `standard`)
Untätige Soldaten bewachen die eigenen Militärgebäude, verteilt nach Stärke und Nähe feindlicher
Trupps (`military.threatRange`); ohne Bedrohung sammeln sie sich am Heimatturm. Wer innerhalb von
`military.guardDistance` seines Postens steht, bleibt. Entscheidungspunkt „Bewachung“ (`Guard`).
Ohne diesen Charakterzug bleiben untätige Soldaten stehen, wo sie sind.

**`forwardBase` – Vorposten** · M · **umgesetzt**
Untätige Soldaten sammeln sich am eigenen Militärgebäude nächst einem feindlichen, als Absprungpunkt
für Guerilla. Sind die feindlichen Trupps in `military.threatRange` stärker (`fallBackRatio`), weichen
sie ohne Kampf ins eigene Land zurück (Gebäude mindestens `fallBackDistance` Felder weiter weg vom
Feind, sonst Heimatturm). Zieht der Feind weiter ins eigene Land, fallen ihm Verteidigung und Guerilla
in den Rücken. Zusammen mit `frontGarrison` (Hinterland dünn, mehr Soldaten frei) ein Zwischenlager
für Überfälle. Steht im Charakter vor `guardByThreat` (beide am Entscheidungspunkt „Bewachung“).

**Türme verteilen** · **umgesetzt**
Ohne Bedrohung (keine feindlichen Soldaten in `military.threatRange` eigener Militärgebäude) verteilen
diese Charakterzüge die untätigen Soldaten gleichmäßig auf ausgewählte Türme; bei Bedrohung übernimmt
der nächste Charakterzug (`guardByThreat`), sie stehen daher im Charakter davor:
- `frontTowers` – Türme zur Front (`skirmisher`): Türme, von denen aus das nächste feindliche
  Militärgebäude ohne Umweg erreichbar ist (Weg höchstens `detour` × Luftlinie + 5) und kein eigenes
  Militärgebäude dazwischen steht.
- `everyOtherTower` – Jeder zweite Turm (`general`, `legion`): von der Front aus jeder zweite – ein Turm
  fällt weg, wenn ein Nachbar (höchstens `neighborFactor` × Abstand zum nächsten Turm) schon gewählt ist.
  Größere Gruppen, die schnell bei den ausgelassenen Nachbarn sind.
- `rotatingTowers` – Wechselnde Türme (`unyielding`): zufällig `share` (30 %) der Türme, alle
  `rotateSeconds` (60) neu gelost (fester Seed der Welt).
Der `hothead` behält alles auf einem Haufen (kein solcher Charakterzug).

Die KI wägt Militärbefehle jede Sekunde neu (`military.thinkSeconds` 1): Soldaten auf einem einfachen
Laufbefehl der KI (zum Posten, heim) zählen als untätig, Feldzüge werden schon auf dem Weg zum
Sammelpunkt aufgegeben, wenn die Verteidigung zu stark geworden ist. Guerilla trifft auch feindliche
Geologen (ein Soldat genügt). Zum Ziel einer Guerilla zählen auch feindliche Soldaten in `military.reinforceRadius` (30) – leicht
verstreute kommen zu Hilfe; ohne das scheiterten frühe Guerillas fast immer. Turm-Bogenschützen sind
keine Gegner, die man besiegt, sondern Kosten: Guerilla (nicht Verteidigung) unterbleibt, wo sie mehr als
`military.towerFireRatio` (0,5) × die Stärke der Feinde dort ausmachen (ein einzelner Soldat neben einer
vollen Burg kostete sonst drei Mann für einen); weicht der Feind während einer Guerilla unter die Türme
aus, bricht die KI ab. Geprüft wird vom Mittelpunkt der gewählten Angreifer aus, nicht vom nächsten
(der stand schon hinter der Burg, die übrigen liefen an ihr vorbei). Nach einem Abbruch bleiben dieselben
Feinde 30 s unbehelligt (sonst: Angriff, Abbruch, Angriff … alle paar Sekunden). Ein Soldat, der nur in
seinem eigenen Land läuft (Rekrut zum Sammelpunkt), greift das Land nicht an – für ihn gilt `hinterlandDepth`. `military.hinterlandDepth` (`skirmisher`: 8) unterscheidet Angriffe auf
Land (Feinde laufen oder kämpfen – überall angegriffen) von herumstehenden Feinden, die nur angegriffen
werden, wenn sie höchstens so tief im eigenen Land ihres Besitzers stehen. Zur Stärke eines Ziels
zählen auch die Bogenschützen feindlicher Militärgebäude, die den Weg oder das Ziel erreichen (ein
Soldat neben einer voll besetzten Burg ist nicht allein); ebenso bei Feldzügen die Bogenschützen
benachbarter Gebäude. Wer sich zurückzieht, läuft zum nächsten eigenen Militärgebäude, das nicht näher
am Feind liegt – nicht den ganzen Weg heim.

### Besatzung

**`frontGarrison` – Besatzung nach Frontlage** · M · **umgesetzt** (Charakter `hothead`)
Militärgebäude im Hinterland bekommen 1 Soldaten, Gebäude an der Grenze zum Feind voll, dort mit
Bogenschützen. Ergebnis: deutlich mehr Soldaten frei für Angriffe.
Werte: `frontDistance` (Abstand zu einem feindlichen Militärgebäude, ab dem ein Gebäude „Front“ ist; es bleibt Front bis `frontDistance + 10` ohne feindliches Militärgebäude, damit die Besatzung nicht flackert), `backline` (Besatzung im Hinterland).

**`reinforceThreatened` – Vorab aufstocken** · S
Steht ein feindlicher Trupp in Reichweite eines Grenzturms, wird dessen Besatzung sofort auf voll
gesetzt, bevor der Angriff beginnt.

**`rotateWounded` – Verwundete tauschen** · M
Angeschlagene Soldaten kehren in einen Turm zurück, frische kommen heraus.
*Voraussetzung:* Soldaten heilen derzeit nicht. Erst sinnvoll, wenn sie im Gebäude Gesundheit
zurückgewinnen (eigene Spielregel, für Mensch und KI gleich).
*Eingebaut statt Charakterzug* (`military.wounded`, `AiPlayer.Tactics`): In kleinen Gefechten (≤ `maxSoldiers`
eigene Soldaten) ziehen sich schwer Verwundete aus dem Kampf zurück, solange der Rest standhält, und halten
außer Sicht des Feinds mit „Position halten“; liegt ein eigenes Lazarett in Rufweite, gibt die KI sie stattdessen
frei und schickt sie zu dessen Tür (Haltende ruft das Lazarett nicht).
Untätige Verwundete schickt die KI ebenfalls zum nächsten Lazarett (`hospital.sendDistance`): den schwächsten auf den
Heilplatz (wer dort steht, wird ohne Ruf geheilt), die übrigen warten ein paar Felder vor der Tür – im Gedränge an der
Tür schöben sie sich gegenseitig weg und der Heiler käme nie heraus. Lazarette baut sie bei Bedarf wie Wohnhäuser: mehr als `hospital.wounded` (5) Verwundete außerhalb
der Reichweite aller Lazarette → eins am Militärgebäude nächst ihnen. Sie kommen zurück, wenn sie geheilt sind,
gebraucht werden oder der Feind fast besiegt ist. Der `hothead` zieht nie zurück (`withdrawHealth` 0).

### Truppenführung

**`formation` – Bogenschützen vor, Nahkämpfer dahinter** · M · **umgesetzt** (`legion`, `general`, `skirmisher`)
Entscheidungspunkt „Gefechtsführung“ (`LeadStrike`, Strikes = Guerilla und Verteidigung). Hat ein Strike
Bogenschützen und Nahkämpfer, sind die Feinde vorwiegend zu Fuß (ihre Bogenschützen höchstens
`maxEnemyBowShare` ihrer Stärke) und noch außer Bogenreichweite, gehen die Bogenschützen vor und schießen
aus Reichweite; die Nahkämpfer halten `parkDistance` Felder dahinter, außer Sicht der Feinde. Kommt ein
Feind einem Bogenschützen auf `fallBackDistance` nahe, weichen die Bogenschützen ohne Kampf hinter die
Nahkämpfer zurück und schießen von dort, die Nahkämpfer gehen rein; im weiteren Kampf ebenso (höchstens
alle `fallBackSeconds`, Bogenschützen ohne Ziel rücken dazwischen nach). Nach `screenSeconds` gehen die
Nahkämpfer auf jeden Fall rein. Bogenschützen und geparkte Nahkämpfer nutzen „Position halten“ wie der
Spieler (`Military.Hold`, `Move(…, hold: true)`): nur Feinde in Reichweite, keine Verfolgung, nicht untätig –
also nicht von Gebäuden als Besatzung gerufen.

**`pincer` – Zange und Finte** · L · **umgesetzt** (Charakter `general`)
Zwei Trupps: ein kleiner lockt die Verteidiger an einer Stelle heraus, der Hauptangriff kommt kurz
darauf von der anderen Seite. Die Kampagne bekommt dafür zwei Sammelpunkte.
Umsetzung: `Ai/Traits/PincerTrait.cs`. Finte (`feintShare`) sammelt sich am eigenen Gebäude nächst
dem Ziel, die Hauptgruppe läuft über einen Wegpunkt im Bogen zu einem Bereitstellungspunkt
(`flankAngle`, `flankDistance`, außer Reichweite der Bogenschützen des Ziels); beide bereit → Finte
greift an, `strikeSeconds` später die Hauptgruppe. Nimmt `extraShare` zusätzliche Soldaten mit, gibt
erst unter `retreatRatio` auf. Der Feldherr greift schon bei 1,2:1 an und lässt Guerilla weit vom
eigenen Land sein (`strikeRange` 40), damit Soldaten für Feldzüge übrig bleiben.
Beobachtet (Salomons, 3 Spieler): ab etwa Minute 37 mehrere Zangen; der Gegner zieht oft Soldaten
zusammen, dann bricht der Feldherr ab – gewolltes Verhalten.

**`flanking` – Hinterhalt durch Umgehung** · L
Ein kleiner Trupp läuft in weitem Bogen um das gegnerische Lager, außerhalb der Sicht feindlicher
Soldaten und der Reichweite von Bogenschützen in Türmen, und greift von hinten an. Ziel sind schwach
besetzte Gebäude im Hinterland. Wird eines erobert, nimmt es Land, und die gegnerischen
Gebäude darauf fallen (Regel „ungeschütztes Land“). Damit rechnet ein menschlicher Spieler nicht.
Werte: Truppgröße, Mindestabstand zum Feind auf dem Umweg, höchste Besatzung eines Ziels.
Wird der Trupp entdeckt (Feinde kommen in Sicht), greift er sofort an oder zieht sich zurück.

**`spellTiming` – Zauber im richtigen Moment** · M
Einfrieren unmittelbar vor dem Zusammenstoß, Zerstreuen auf Klumpen vor einem Turm, Bekehren auf
die stärksten Gegner in Reichweite. Heute zaubert die KI, sobald genug Ziele im Radius stehen.

**`summoner` – Warenzauber** · S · **umgesetzt** (`legion`)
Mana über `reserveSpells` (2) Volkszauber hinaus geht in „Waren herbeirufen“. Ein freier Priester zaubert
auf den besten Platz im Umkreis `priestRadius` (12) um sich: eigenes Land, mindestens `edgeDistance` (10)
Felder von dessen Ende (Langfinger, Feinde; Kartenrand zählt nicht), kein feindlicher Soldat draußen in
`soldierRadius` (20), mindestens `minFreeFields` (30) freie Felder im Zauberradius; die meisten freien
Felder gewinnen, dann der nächste zum Priester. Nur, solange kein Priester des Spielers zaubert; alle
`checkSeconds` (5) geprüft. Aufwertungen kosten kein Mana und bleiben unberührt. Entscheidungspunkt „Besorgungen“.

### Schwachstellen ausnutzen

**`exposedTowers` – Entblößte Türme kontern** · M
Zieht der Gegner für einen Angriff Soldaten aus seinen Türmen ab (bis auf einen), werden genau
diese Türme sofort angegriffen. Bewertet Ziele nach Besatzung jetzt, nicht nach Normalbesatzung.

**`landGrab` – Landnahme ohne Kampf** · M
Turmplätze bevorzugen, deren Gebiet ungeschütztes gegnerisches Land überdeckt (z. B. nach einem
Abriss). Sobald der Turm besetzt ist, fallen die gegnerischen Gebäude darauf.

**`economicTargets` – Wirtschaft treffen** · S
Angriffsziele höher bewerten, deren Land Minen, Schmelzen, Waffenschmieden oder Tempel trägt.
Ein Treffer dort kostet den Gegner mehr als ein beliebiger Turm.

### Späher

**`thief` – Langfinger** · M · **umgesetzt** (`skirmisher`, `hothead`)
Hält `scouts` (2) Späher – freie Träger werden welche, Verluste frühestens nach `replaceSeconds` (90)
ersetzt – und schickt sie zu Warenstapeln am Rand feindlichen Lands: auf fremdem Land höchstens
`edgeDepth` (12; die Stapel eines Lagerhauses direkt an der Grenze liegen gut 8 Felder tief) Felder von dessen Ende, höchstens `maxDistance` (30) vom eigenen Land. Waren nach `ranks`: Gold, Edelsteine, Waffen, Erze zuerst, dann Werkzeuge, dann Nahrung (Brot, Fisch,
Fleisch), dann der Rest; am Stapel nimmt der Späher die beste Ware im Diebesradius. Steht ein
feindlicher Soldat draußen in `soldierRadius` (12, etwas mehr als `scouts.revealRadius`) des Stapels,
kein Versuch; kommt einer dem Späher oder seinem Stapel so nahe, ruft die KI ihn heim und lässt den
Stapel `retrySeconds` (60) in Ruhe. Beladene laufen zum nächsten Feld des eigenen Lands und lassen die
Ware dort fallen. Entscheidungspunkt „Besorgungen“. Gegen eine KI riskant: Sie schöpft Verdacht, sobald
ein Späher auf ihrem Land steht, und schickt einen Soldaten, der ihn aus der Nähe erkennt.

### Gold

Siehe [Gold als Hebel](#gold-als-hebel). Drei Charakterzüge, je an einem eigenen Entscheidungspunkt.

**`goldRace` – Gold früh sichern** · M
Goldberge (Ägypter: Edelsteine) gelten von Anfang an als Expansionsziel, nicht erst nach der
Build-Order (`lateOres`). Goldmine und -schmelze rücken in der Build-Order nach vorn, sobald Nahrung
und Werkzeuge laufen. Werte: ab welchem Schritt der Build-Order, Gewicht umkämpfter Goldberge.
Entscheidungspunkte: Turmplätze, Bauen.

**`goldGuard` – Gold schützen** · M
Land, auf dem eigenes Gold liegt (Lager, Ausgabestapel der Schmelze), gilt als Front – auch im
Hinterland: Militärgebäude dort voll besetzt, mit Bogenschützen, Wachen nach der Menge Gold statt
nur nach Bedrohung. Neues Lager nur auf Land, das mehrere eigene Türme decken. Wahlweise Gold auf
mehrere Lager verteilen, damit ein Treffer nicht alles kostet (Abwägung: mehr Orte zu verteidigen).
Entscheidungspunkte: Besatzung, Bewachung, Bauplatz Lager.
Auf Schwer in jedem Charakter empfohlen – ohne ihn kann eine einzige Eroberung die KI erledigen.

**`goldRaid` – Gold erobern** · M
Angriffsziele, deren Land gegnerisches Gold trägt, bekommen die höchste Bewertung: Die Eroberung
nimmt dem Gegner Kampfkraft und gibt sie dem Angreifer. Lohnt sich auch bei knapperer Überlegenheit
(eigener Wert für `attack.ratio`), weil der Gewinn die Kräfte sofort verschiebt.
Entscheidungspunkt: Angriffsziele.

### Zeitpunkt und Strategie

**`earlyArmy` – Frühe Armee** · S · **umgesetzt**
Die ersten Einträge von Eisenschmelze, Waffenschmied und Kaserne rücken zusammen direkt hinter die
ersten `after` Einträge (Holz und Stein). Die Startwaren (Eisenerz, Kohle auf mittlerer und hoher
Startstufe) werden sofort Soldaten, statt auf Minen und Nahrung zu warten. Lohnt nur bei einem Charakter, der die
frühen Soldaten auch einsetzt: bei der vorsichtigen Plänklerin kostete er Wirtschaft (gegen Standard
0,43 statt 0,48), beim Hitzkopf brachte er 0,56 statt 0,52. Auch `forwardBase` schwächte die
Plänklerin (0,39): Ihre Soldaten standen an der Front in Reichweite der feindlichen Guerilla. Dafür darf eine Schmelze
auch ohne eigene Mine kommen, wenn ihr Erz auf eigenem Land liegt (gilt für alle).

**`earlyRush` – Früher Ansturm** · M
Mit den Startsoldaten früh einen nahen gegnerischen Turm angreifen, bevor der Gegner Kaserne und
Waffen hat. Danach normale Wirtschaft.

**`powerSpike` – Angriff nach Aufwertung** · S
Angriffe zurückhalten, bis eine Aufwertung fertig ist oder genug Gold für hohe Kampfkraft lagert,
dann gebündelt losschlagen. Gold dafür gezielt sammeln.

**`waves` – Spannungsphasen** · S · **umgesetzt** (Charakter `legion`)
Wechsel aus ruhigen Aufbauphasen und Angriffswellen statt gleichmäßigen Drucks. Werte: `calmSeconds`,
`waveSeconds`, `jitter` (Streuung). Verteidigung und Guerilla laufen in der Ruhephase weiter.

**`multiFront` – Druck an mehreren Stellen** · M
Zwei oder drei Trupps bedrohen gleichzeitig verschiedene Grenzabschnitte. Der Verteidiger muss sich
aufteilen; angegriffen wird, wo er am dünnsten steht.

**`jitter` – Schwellen streuen** · S
Feste Verhältnisse (`attack.ratio`, `strikeRatio` …) werden bei jeder Entscheidung innerhalb
einer Spanne gewürfelt (z. B. 1,3–1,8 statt 1,5). Das Verhalten wird schwerer vorherzusagen.

### Verworfen

**Fokusfeuer** (mehrere Soldaten auf dasselbe Ziel, Verwundete und Bogenschützen zuerst): Der Spieler
kann keine einzelnen Einheiten gezielt angreifen lassen. Die KI soll keine Steuerung bekommen, die
dem Menschen fehlt.

## Stellschrauben ohne Charakterzug

Viele Unterschiede brauchen keinen eigenen Charakterzug, nur andere Werte im Charakter:

| Wert | Wirkung |
|---|---|
| `attack.ratio` | Überlegenheit am Ziel, ab der ein Feldzug startet |
| `attack.armyRatio` | Überlegenheit der ganzen Armee (Vorgabe: `ratio`; 0 = egal → kleine Feldzüge gegen schwache Türme) |
| `attack.commit` | Anteil der übrigen freien Soldaten, der zusätzlich mitzieht (1 = volle Stärke) |
| `attack.abortRatio` | nach dem Sammeln: Abbruch unter diesem Verhältnis (Vorgabe: `ratio`; 0 = nie) |
| `attack.maxDistance` | Ziel höchstens so weit vom eigenen Land (0 = egal; ein Grenzturm steht gut 30 Felder hinter seiner Landgrenze) |
| `attack.minSoldiers`, `attack.defenseRadius` | kleinster Trupp; wie weit Feinde um das Ziel zur Verteidigung zählen |
| `military.strikeRange`, `military.strikeRatio` | Guerilla: wie weit draußen, ab welcher Überlegenheit |
| `military.retreatRatio` | Rückzug darunter (0 = nie) |
| `buildOrderMoves` | Gebäude in der Build-Order des Volks verschieben, z. B. `{ "barrack": -6 }` |
| `maxSites` | gleichzeitige Baustellen |

Zum Ausprobieren ohne Eintrag in `ai.json`: `pincer+waves` (nur diese Charakterzüge) und
`@pfad=wert` (`general@attack.ratio=1.3@buildOrderMoves.barrack=-5`). Das Turnier
(`tools/trait-tournament.py`) variiert Werte mit `--vary attack.ratio=1.2/1.5/2`.

## Volks-Charaktere

Ziel: je Volk ein Charakter, der sich als Gegner eigen anfühlt. Definiert in `ai.json`, je Volk über
`characterWeights` (unter `civilizations`) zugeordnet.

| Volk | Charakter | Idee | Umsetzung |
|---|---|---|---|
| Ägypter | Hitzkopf (`hothead`) | temperamentvoll, greift immer mit voller Stärke an | earlyArmy, frontGarrison, thief; `ratio` 1,2, `commit` 1, Guerilla bei 1,2:1 bis 120 Felder (mit earlyArmy gegen Standard 0,56 statt 0,52, entscheidet mehr Partien) |
| Asiaten | Unbeugsamer (`unyielding`) | gibt niemals auf, zieht sich nie zurück | guardByThreat; `abortRatio` 0,5, `retreatRatio` 0,5 (bei 0 lief er sich tot: 0,35 Punkte gegen Standard, mit 0,5: 0,50), `ratio` 1,4 |
| Römer | Legion (`legion`) | wirtschaftlich stark, greift gebündelt in Wellen an | waves, pincer, guardByThreat, summoner; `minSoldiers` 8, `commit` 0,6; Gold 10 Plätze früher; 5 Baustellen |
| Amazonen | Plänklerin (`skirmisher`) | auf Nummer sicher, schwächt den Gegner mit kleinen Guerillas und Diebstahl an der Grenze | guardByThreat, thief; Feldzug erst bei 2:1, aber ohne Armee-Vergleich ab 2 Soldaten und nur bis 50 Felder vom eigenen Land (`maxDistance`; vom nächsten Turm gemessen griff sie nie an, weil zwei Türme an einer gemeinsamen Grenze schon gut 60 Felder auseinanderstehen; ohne griff sie in Minute 1 einen Turm quer über die Karte an und verlor danach alles); Guerilla bis 150 Felder bei 2:1 (bei 1,3:1 mit Rückzug unter 1,2 kehrten die kleinen Trupps beim ersten Anzeichen von Gefahr um), Rückzug unter 1:1 |

Leitlinie: Spielstil, Abwechslung und Spaß vor exakter Balance. Jedes Volk soll sich als Gegner
erkennbar anders spielen, mit Stärken und ausnutzbaren Schwächen. Beobachtet (Arena, Amazonen gegen
Ägypter, 10 Partien): Der Hitzkopf überrennt auf kleinen Karten in 20–30 Minuten, verpufft der Rush,
verblutet er (am Ende 3–33 gegen 100–220 Soldaten). Gegen Ägypter heißt es also früh überleben, gegen
Amazonen früh angreifen oder sich auf ein hartes spätes Spiel einstellen.

## Charaktere (Entwurf)

| Charakter (Kennung) | Idee | Charakterzüge |
|---|---|---|
| Standard (`standard`) | eingebautes Verhalten | – (Guerilla, Feldzug, volle Besatzung sind eingebaut) |
| Stürmer (`rusher`) | früh und oft angreifen, geringere Überlegenheit reicht | earlyRush, frontGarrison, waves |
| Feldherr (`general`) | geordnete Schlachten | pincer, formation, guardByThreat (umgesetzt); geplant: spellTiming, reinforceThreatened |
| Hinterhältiger (`trickster`) | dort zuschlagen, wo keiner hinschaut | flanking, exposedTowers, goldRaid, frontGarrison, jitter |
| Landnehmer (`landTaker`) | wächst über Grenzen und nimmt Land ohne Kampf | landGrab, frontGarrison, economicTargets |
| Wirtschafter (`merchant`) | ruhig aufbauen, dann mit starken Soldaten zuschlagen | goldRace, goldGuard, powerSpike, spellTiming |

## Schwierigkeit und Charakter

Zwei unabhängige Achsen:

- **Charakter** = *wie* die KI spielt (Stil, Charakterzüge).
- **Schwierigkeit** = *wie gut* sie es tut (Wahrnehmung, Reaktion, Entscheidungsqualität).

Ein Stürmer kann leicht oder schwer sein. Gebaut wird die beste KI (Schwer); leichtere Stufen
bekommen gezielte Abstriche.

### Stellschrauben je Stufe

| Stellschraube | Leicht | Mittel | Schwer |
|---|---|---|---|
| Wahrnehmung | eigenes Land, Späher, Geologen | was sie sieht, dazu Lage von Gebirgen und Steinen | alles |
| Späher/Geologen | ja, zum Finden von Rohstoffen | ja, zum Finden von Gegnern | nein |
| Reaktion | nur auf Angriffe im eigenen Land | auf entdeckte Feinde in der Nähe | auf alle Feinde in der Nähe, vorausschauend |
| Soldaten aufstellen | irgendwo im Land | an Grenztürmen zum Feindland | an Grenztürmen zu feindlichen Truppen |
| Guerilla | nein | ja | ja |
| Charakterzüge | wenige, einfache | die für den Kampf | alle des Charakters |
| Schwellen | eher zögerlich bzw. leichtsinnig | Standard | genau berechnet |
| Denkpause | lang | mittel | kurz |

Charakterzüge können selbst nach Stufe abschwächen (z. B. `flanking` auf Leicht ohne Rücksicht auf
Sichtweite), statt nur an- oder abgeschaltet zu werden.

### Schwer muss gewinnen, nicht nur halten

Eine undurchdringliche Verteidigung macht die KI schwer zu *schlagen*, aber nicht schwer zu *spielen*:
Der Mensch muss nur selbst bunkern, und die Partie endet im Patt. Schwer braucht deshalb auch
einen Weg zum Sieg:

- **Wirtschaft ausspielen:** besser verteilen, früher Gold, Rohstoffe schneller sichern. Gegen eine
  gleich starke Verteidigung gewinnt, wer mehr und stärkere Soldaten nachliefert.
- **Den Verteidiger überdehnen:** Druck an zwei oder drei Stellen zugleich. Auch ein Mensch kann
  nicht überall sein; die KI greift dort an, wo er gerade nicht verteidigt.
- **Geduld und dann ein Schlag:** warten, bis Kampfkraft (Gold) und Aufwertungen klar vorn liegen
  (`powerSpike`), dann gebündelt dort angreifen, wo eine Eroberung Wirtschaft mitreißt
  (`economicTargets`).
- **Magie als Durchbruch:** Einfrieren, Zerstreuen, Bekehren gezielt beim Sturm auf einen Turm.

## Gold als Hebel

Gold ist im Spiel genau dafür da, den Verteidigervorteil zu brechen und in die Offensive zu zwingen.

- **Regel:** Im Feindland kämpft ein Soldat mit 100 % / Spielerzahl. Goldbarren auf dem eigenen Land
  heben das ohne Obergrenze, immer teurer je Schritt. Bei zwei Spielern: 115 Barren für 100 %,
  305 für 130 %, danach 10 je Punkt, 1005 für 200 % (Ägypter: doppelt so viele Barren oder halb so
  viele Edelsteine; `combatStrength` in `military.json`). Über 100 % schlägt ein Soldat auswärts härter
  zu als daheim – wer reich ist, wird in den Angriff gedrängt.
- **Verteidigen braucht kein Gold, Angreifen schon.** Wer mehr Gold hat, kann angreifen; wer keines hat,
  kann nur halten.
- **Gold gehört dem Land:** Gezählt werden die Stapel auf dem Land eines Spielers, gleich, wer sie
  hingelegt hat. Erobert ein Spieler das Land um ein gegnerisches Lager, gehört ihm das Gold dort
  (gewollt). Der Gegner verliert Kampfkraft, der Sieger gewinnt sie – die Partie kann damit
  sofort entschieden sein.
- **Folgen für die KI:**
  - Gold ist ein strategisches Ziel von Anfang an (`goldRace`), nicht Beiwerk am Ende der Build-Order.
  - Eigenes Gold ist der wertvollste Besitz und wird geschützt wie die Front (`goldGuard`).
  - Gegnerisches Gold ist das beste Angriffsziel (`goldRaid`).
- **Risiko:** Der Lawineneffekt ist hier am stärksten. Gewollt ist, dass ein gelungener Schlag auf das
  Gold die Partie dreht; schlecht wäre, wenn er ohne echte Chance zur Gegenwehr geschieht. Deshalb
  muss Gold sichtbar und verteidigbar liegen – für Mensch und KI gleich.

## Dynamik und Patt

### Warum Partien heute im Patt enden

- **Verteidigervorteil:** Im eigenen Land 100 % Kampfkraft, im Feindland 100 % / Spielerzahl. Bei
  zwei Spielern braucht ein Angreifer etwa die doppelte Armee; die KI greift erst bei 1,5:1 an.
- **Allwissende, sofort reagierende KI:** Wachen stehen nach Bedrohung. Wer sich sammelt, wird
  gesehen, bevor er angreift.
- **Gleiche Rechnung auf beiden Seiten:** Zwei gleiche KIs halten sich gegenseitig zurück.
- **Kaum Schwächen:** Die KI stockt selten (Schmelze erst mit eigener Mine, Material für
  Steinmetz zurückgehalten …). Im Original entschieden gerade solche Hänger die Partien.

### Was eine Partie wenden kann (gewollt)

- **Gold-Wettlauf:** Gold hebt die Kampfkraft im Feindland – der eingebaute Ausweg aus dem Patt.
  Wer zuerst nahe 100 % kommt, kann angreifen; darüber muss er es. Goldberge werden umkämpftes Ziel.
- **Rohstoffe gehen aus** (`depletion`): Wer nicht zu neuen Rohstoffen kommt, verliert langsam. Liegen
  sie zwischen den Spielern, wird Kampf nötig.
- **Eroberung mit Folgen:** Fällt ein Turm, fallen die Gebäude auf seinem Land. Ein Treffer an
  Mine, Schmelze oder Waffenschmiede legt eine ganze Kette lahm.
- **Magie:** dreht Kräfteverhältnisse, ohne doppelte Armee.
- **Unterschiedliche Charaktere:** Ein Stürmer gegen einen Wirtschafter geht ins Risiko; einer
  gewinnt früh oder verliert sich.
- **Gewollte Schwächen je Charakter:** Der Landnehmer ist überall dünn besetzt, der Stürmer
  vernachlässigt die Wirtschaft. Etwas zum Ausnutzen – erkennbar statt zufällig.

### Was eine Partie verderben kann

- **Zu schneller Lawineneffekt:** Eine frühe Eroberung kippt die halbe Wirtschaft; die Partie ist in
  Minute 15 entschieden, dauert aber noch eine Stunde.
- **Bunkern:** Wird Verteidigen noch billiger, bauen beide nur Türme und nichts passiert.
- **Allwissenheit wirkt unfair:** Ein Hinterhalt des Menschen gelingt nie. Abhilfe über die
  Wahrnehmung der Schwierigkeitsstufen.
- **Zufällige Hänger:** Stockt die KI durch einen Fehler, entscheidet Glück statt Spiel.
- **Karte entscheidet:** Ist Gold der einzige Hebel, gewinnt, wer zufällig den Goldberg hat.

### Erste Schritte

1. **Messen:** Arena, 5–10 Partien KI gegen KI à 60 Minuten; Kampfkraft, Gold, Eroberungen und
   Verluste mitschreiben. Wie oft endet es im Patt?
2. **Gold früher und gezielt:** Goldberge ansteuern (`goldRace`), Gold schützen (`goldGuard`),
   Gold für Kampfkraft horten und dann zuschlagen (`powerSpike`, `goldRaid`).
3. **Wahrnehmung begrenzen** (Stufen Leicht/Mittel): Reaktion nur auf gesehene Trupps.
4. **Verteidigervorteil** nur mit Arena-Zahlen anfassen, z. B. Abschlag erst tief im Feindland
   voll, an der Grenze schwächer.

## Vorgehen

1. ~~Gerüst für Charakterzüge~~ **umgesetzt**: Charaktere in `ai.json`, Basisklasse `AiTrait` (Entscheidungspunkte
   als überschreibbare Methoden, bisher `LeadCampaign`), heutiges Verhalten = Charakter `standard`
   ohne Charakterzüge. Weitere Entscheidungspunkte kommen mit den Charakterzügen, die sie brauchen.
2. Arena um Messwerte zum Patt erweitern (Kampfkraft, Gold, Eroberungen, Verluste über die Zeit).
3. Charakterzüge einzeln umsetzen, je mit Test und Arena-Vergleich gegen den Standard; zuerst die, die das
   Patt brechen (`goldRace`, `goldGuard`, `goldRaid`, `powerSpike`, `economicTargets`, `multiFront`).
4. Schwierigkeit als zweite Achse: Wahrnehmung, Reaktion, Schwellen, erlaubte Charakterzüge.
5. Charaktere den Völkern zuordnen.
