# Simulation (`src/Homestead.Simulation`)

Spiellogik ohne MonoGame, feste Tick-Rate (`World.TicksPerSecond`). KI: [ki.md](ki.md).

**Determinismus** (Lockstep-Mehrspieler: jeder Rechner simuliert gleich): Zufall nur `new Random(… + World.Seed)`, keine Uhr, keine Threads,
kein `GetHashCode` in der Logik; Winkelfunktionen nur über `DeterministicMath` (Sin/Cos/Atan2 aus Grundrechenarten – `Math.Sin` & Co. kommen aus
der C-Bibliothek der Plattform), Grundrechenarten und `Math.Sqrt` sind nach IEEE überall gleich. `WorldChecksum.Of` (FNV-1a über Tick, Siedler,
Gebäude, Stapel, Karte, Mana) für Desync-Erkennung; `DeterminismTests` (KI-Spiel auf `random:4:mirror:11`, 25 min, Prüfsumme je Minute) läuft im Build
auf Linux, Windows (x64) und macOS (ARM), `determinism-compare` vergleicht.

- `Map/GameMap`: Gelände, Objekte, Begehbarkeit, Bodenschätze (`ResourceAt`).
  `Map/Vision`: Fog of War je Spieler – Sicht eigener Figuren draußen (Soldaten `Sight`, Späher `scouts.sight` 26, Esel `trade.sight` 3
  – keine Späher –, sonst `SettlerSight`) und Gebäude (`Military.SightOfBuilding`: fertige mit `buildingSight` in `military.json`,
  Aussichtsturm 45; besetzte Militärgebäude Gebietsradius + `militarySightMargin` (3), leere nichts; alle anderen fertigen Gebäude
  `otherBuildingSight` (5), auch Wohnhäuser/Lager; Baustellen nichts); eigenes Land selbst sieht nichts (abgerissen/von Pionieren genommen → Schatten);
  Erkundetes bleibt erkundet; KI nutzt es nicht.
- `Pathfinding/PathFinder`: A*; Erreichbarkeit über `Regions` (führen Begehbarkeitsänderungen nach). Schrittkosten nach Laufzeit
  (`SetStepCosts`: Pfad 8, eben 10, Gebirge 13); Landgebiete je Besitzer.
  Ferne Ziele etappenweise (`FindRoute`, von `WalkTo`/`Replan`; nicht auf eigenes Land Beschränkte über `ExactDistance` (96)): grobe Route über
  `Clusters` (16×16-Zellen, je Zelle ihre zusammenhängenden Teile, Nachbarn gecacht, geänderte Zellen neu), exakt nur bis in den Teil
  `LegNodes` (4) Zellen voraus; `Settler.PathIsLeg` → am Etappenende plant `World` die nächste. Nahe am Ziel exakt.
  Unerreichbares Befehlsziel (Meer, andere Insel): nächster erreichbarer Punkt (`PathFinder.NearestReachable`, Ring für Ring;
  `World.CommandTarget` für Soldaten, Geologen/Späher/Pioniere, Priester).
- `World`/`Settler`: ein Feld je Figur, Tauschen, Ausweichen, Umweg, Watchdog-Durchlauf (Kommentar in `World`).
  `Settler.StepProgress` in `World.StepUnits`; Tempo je Zielfeld: Gebirge ×0,8, Trampelpfad ×1,2 (`walking` in `production.json`);
  dazu Soldaten je Typ `speed` in `military.json` (Schwertkämpfer ×1,1, Pikeniere ×1,05, Bogenschützen ×1); `armor` (Pikeniere 0,5):
  so viel weniger Schaden je Treffer, abgerundet, mindestens 1 (`SoldierStats.Taken`; KI rechnet mit `Toughness`).
  Siedler und Gebäude haben einen Spieler; Logistik/Rekrutierung nur für den eigenen. `World.TerritoryRules` erzwingt eigenes Land
  (Bauplatz samt Tür, Stapel, Bauarbeiterplätze, Wege – außer Soldaten, Priester, Geologen, Späher, Pioniere); Gestrandete fliehen (`Idling`); wer mit Auftrag Land unter den Füßen verliert (`World.IsStranded`), bricht ab, lässt Getragenes fallen und nimmt erst auf eigenem Land Neues an.
- `Trails`: Träger mit Auftrag treten auf Gras Trampelpfade (`GroundCover.Trail`, nach `trailSteps`), verschwinden nach `trailFadeSeconds` (300).
  Ackerland und Pfade halten ein Feld Abstand zu allem außer Gras/planiertem Boden und zueinander (`GameMap.CoverFits`, sonst brechen
  Original-Übergänge). Getreide wird auch auf Pfade gesät und kürzt Pfade ringsum (`Trails.CutAround`); Säen macht Gras im Umkreis
  `farmlandRadius` (1) zu Ackerland, soweit es passt und im Arbeitsbereich liegt.
- `Buildings/`: Definitionen aus `data/buildings/*.json`.
  - Bauplatzprüfung (`CanPlace`, `DoorLeadsOut`, `PlanLevelling`): „gesehen“ per Markenfeld über die Karte (`NewSearch`/`See`), kein HashSet.
  - `Construction`: Planieren (`World.PlanLevelling`, `levelling` in `production.json`): Grundfläche auf mittlere Höhe, ringsum Ring für Ring,
    bis kein Nachbar über `maxSlope` (8) abweicht (am Hang breiter). Aufwand = bewegte Höhe; ungültig über `maxWork` oder wenn ein zu
    änderndes Feld bebaut, anderweitig geplant, unbegehbar oder fremd ist. Planierer: je Gang `heightPerDig`, nie Steigung über `maxSlope`
    (am Hang oben zuerst). Sofort fertige Gebäude planieren sofort. Minen: nur Gebirge, kein Planieren (`levelling: false`, `NeedsLevelling`).
  - Bauarbeiter ohne Material werden frei (untätig, schiebbar, ein Schritt weg, wechseln Baustelle); Plätze nur, solange Material für einen weiteren reicht.
  - Gebäude, die JSettlers nicht hat (Kriegsmaschinen-Werkstätten, Pulvermacherei): `data/buildings/handmade.json` je Volk
    (Grundriss = vom Originalbild bedeckte Rasterpunkte, Tür/Stapel nach dem Bild), vor `overrides.json` geladen.
  - `World.RemoveBuilding`: Abriss/Verlust mit Staubwolke (`DustClouds`), Anteil `demolition.payback` des Baumaterials bleibt liegen.
    Land abgerissener Militärgebäude bleibt beim Spieler, aber ungeschützt (feindliches Militärgebäude nimmt es samt Gebäuden).
  - Abriss: Arbeiter behält Beruf und Werkzeug, übernimmt das nächste Gebäude seines Berufs.
- `Economy/`: Waren, Stapel.
  - Neuer Gebäudestapel (Baustelle, fertig) auf einem losen Stapel: dessen Waren werden daneben abgelegt (`AddPile` → `DropGoods`), nicht überschrieben.
  - `Logistics` mit Watchdog: je Ware unter den aktuellen Abnehmern – Baustellen als einer (`Logistics.Construction`), sonst je Gebäudetyp –
    einer gelost nach Spieler-Anteilen (`ShareOf`/`SetShare`; Vorgaben `distribution` in `production.json`, 0–1, z. B. Nahrung Minen 1 : 0,1,
    Bretter Baustelle 1, Köhlerei/Werft 0,5; gelistete Waren geben ungelisteten Abnehmern 0, ungelistete allen 1; Anteil 0 bekommt nichts,
    außer alle sind 0), dann das nächste Ziel mit Platz; Lager nur, wenn sonst niemand will.
  - Einlagern nur, was der Spieler einlagert (`IsStored`/`SetStored`); Lagerhaus auf eine Ware spezialisierbar
    (`SpecializationOf`/`SetSpecialization`, `StoresHere`, überstimmt die allgemeine Regel); Vorgabe `storage.materials`: Werkzeuge, Waffen, Gold, Edelsteine.
  - Angebote nach Transportreihenfolge (`TransportOrder`/`MoveInTransportOrder`, Vorgabe `storage.transportOrder`); lose Stapel auf niemandes
    Land: Spieler mit freien Trägern. Sonst bleibt Ware am Ausgabestapel (8; voll → Erzeuger wartet, Sammler auch).
  - Baustelle mit höchster Priorität (`Building.HasPriority`): ihre Stapel vor allen Abnehmern (auch Anteilen), ihre Waren vorn in
    der Transportreihenfolge; fehlt mehr, als bereitliegt, geben Träger zu anderen Baustellen nur so viele ab (`Logistics.FreeForPriority`, nächste
    zuerst; auf dem Weg zur Quelle: abgebrochen, beladen: lassen fallen – im Original alle); ausgefallene Lieferungen werden beim nächsten Abgleich ersetzt.
  - `Problems` zählt Abbruchgründe.
  - `Production`: Werkstätten; Schmiede je Takt ein Produkt: Bestelltes, dann erwartetes Werkzeug, sonst gewichtet reihum nach Anteilen
    (`shares`: Werkzeugschmied 0 = nur Bedarf; Waffenschmied Schwert 100 %, Bogen 70 %, Speer 45 %), solange unter `keepInStock` (0 = ohne Grenze);
    Wahl erst mit Eisen und Kohle.
    Minen: Nahrungsstapel max. `mineFood.pileCapacity` (4); erste `mineFood.freeRuns` (8) Gänge je Gebäude gratis; Erfolgschance =
    Resterz im Umkreis gegen `resources.fullAmount` (8) je Feld (leere/fremde Felder = Nieten, jeder Fund senkt sie; `MineChance`); Niete kostet Zeit und Nahrung.
    Sammler: Wasserwerk nur an Flüssen; Bauer, Weinbauer, Reisbauer, Imker nach `crops` – Getreide macht Ackerland (`GroundCover.Farmland`,
    begehbar, bis Abriss/Verlust der Farm), Getreide/Wein/Reis wachsen 600 s wie im Original; Wein am Hang schneller; Reis nur auf Sumpf; Reben/Bienenstöcke wachsen nach.
    Imker: höchstens `maxFields` (12) Stöcke je Imkerei (`Production._fields`; herrenlose im Arbeitsbereich – alte Spielstände – übernimmt sie), neue nur mit
    mindestens einem Baum im Umkreis und `fieldSpacing` (5) Feldern Abstand zu anderen Stöcken (Aufstellen `sowSeconds` 1,2 s = eine Animationsrunde), dort, wo die meisten stehen; Stöcke ohne Bäume ringsum holt sie nach dem Ernten ab (dann neu bei Bäumen, sonst keiner);
    Abriss/Verlust der Imkerei nimmt ihre Stöcke sofort mit. Stock voll in 720 s, mit `idealTrees` (10) ausgewachsenen Bäumen im Umkreis `treeRadius` (5) `1 + treeBonus` (3) × so schnell = 240 s
    (12 Stöcke à ~20 s Erntegang → 3 Honig/min wie im Original; weniger Bäume anteilig).
  - `Healing`: Lazarett-Heiler ruft den schwächsten (wenigste LP, Gleichstand: nächster) verwundeten untätigen eigenen Soldaten im Umkreis
    `hospital.callRadius` (40) auf den Heilplatz vor der Tür (`HealSpotOf`; Haltende nicht); wer still neben Tür, Heilerplatz oder Heilplatz steht,
    wird ohne Ruf geheilt (der schwächste zuerst),
    heilt ihn (ab da nur durch Abriss unterbrechbar; volle LP nach `healSeconds`, wo immer er ist), geht hinein, `callPauseSeconds` Pause.
  - `Housing`: Wohnhäuser entlassen Träger und geben so viel Wohnraum (plus Startbevölkerung, `SettleStartPopulation`). Platz brauchen
    Träger und Arbeiter (auch Planierer/Bauarbeiter – Baustellen rekrutieren höchstens Anteil `Share.Diggers`/`Builders`, mind. je einer),
    nicht Soldaten, Geologen, Späher, Pioniere. Ohne Platz streiken (`Settler.IsStriking`): erst untätige Träger, dann Arbeiter (bleiben drin).
  - `Recruitment`: Träger holen Werkzeug, werden Spezialisten. Tests ohne Werkzeugthema: `TestConfig.NoTools`.
  - `Idling`: Gestrandete Träger/Berufe außerhalb des eigenen Lands fliehen zum nächsten erreichbaren eigenen Feld (`RefugeRadius` 64, sonst
    eigene Gebäudetür); ohne irren sie umher und sterben nach 30–60 s (je Siedler fest per Hash, Geist, Todesgeräusch), zurück auf eigenem Land gerettet.
    Untätige (Träger, Soldaten, Planierer, Bauarbeiter, Geologen, Späher, Pioniere, Arbeitslose) gehen aus dem Weg
    (nach `IdleTicks`; Soldaten sofort, jeden Tick, mit einem Feld Abstand).
  - `Geology`: Geologen laufen wie Soldaten (verteilt auf freie Felder); auf Gebirge suchen sie im Umkreis `geologists.searchRadius`
    (nächstes freies Feld, Gleichstand zufällig), setzen Schilder mit Rohstoff und Menge.
  - Späher (`Profession.Scout`, Grafik `THIEF`): umgewandelt/geschickt wie Geologen (`Geology.Convert(…, Profession.Scout)`), suchen nie.
    Für andere Spieler ein eigener Träger (`Settler.IsRevealedTo`), nicht angegriffen, bis ein feindlicher Soldat draußen auf
    `scouts.revealRadius` (10) herankommt (`Military.RevealScouts`, jeden Tick); erkannt bleibt er, solange der Spieler ihn sieht
    (`Military.Sees`: Sicht eigener Figuren/Gebäude wie Fog of War), dann wieder getarnt. Erkannt: wie Geologe bekämpft
    (auch von Turm-Bogenschützen), KI zählt ihn zu Feindgruppen. KI-Verdacht (`AiPlayer.InspectScouts`): Späher auf ihrem Land → nächster
    untätiger Soldat folgt ihm (sonst ruft sie einen aus dem nächsten Militärgebäude mit mehr als einem Soldaten).
  - `Theft`: Späher stehlen (`Steal`; Alt+Rechtsklick: `StealAt`) die nächste Ware im Umkreis `scouts.stealRadius` (5) – unreservierte Einheit,
    nicht aus eigenen Gebäuden oder losen Waren auf eigenem Land; trägt sie (`Carrying`, geduckt), Tempo `scouts.loadedSpeed` (0,5);
    lässt sie still auf eigenem Land fallen oder wo er stirbt. Mit `StealAt` geschickt, läuft er mit der Ware gleich zurück, wo er beim Befehl stand.
  - `Pioneering`: Pioniere (umgewandelt/geschickt wie Späher; `Claim`, Alt+Rechtsklick `ClaimAt`) nehmen Feld für Feld: nächstes freies oder
    fremdes, nicht geschütztes Feld (`Military.IsProtected`) im Umkreis `pioneers.searchRadius` (10), Gleichstand nächst dem Zielpunkt;
    `claimSeconds` (2) Pfosten, dann gehört ihm das Feld samt unbegehbaren Nachbarn (`Military.TakeLand`, ungeschützte fremde Gebäude fallen);
    am Ufer auch das freie Wasser davor bis `pioneers.waterReach` (6) Felder hinaus (über eigenes Wasser hinweg) – Platz für Steg und Fischer; Grenzpfosten zeichnet der Client nie auf Wasser (Ufer = Grenze).
    Im geschützten Feindland Richtung freies Land, sonst Ende. Vollständig umschlossenes freies Land (nicht Kartenrand/fremdes Land) wird
    auf einmal genommen (bis 4096 Felder). Land ungeschützt.
  - `Trade`: Landhandel. Eselzucht (`trade` in `production.json`: Getreide + Wasser → ein Esel, `Profession.Donkey`, keine Ware) züchtet,
    solange der Spieler weniger als `keepIdle` (4) Esel ohne Auftrag hat. Esel: nicht steuerbar, unsterblich, laufen überall, zählen nicht
    für Wohnraum/Berufe-Anteile. Markt (`market_place`): 6 Stapel `PileRole.Trade` (nie Angebot); je Markt Ziel = Punkt (`Reachable`:
    begehbar und vom Markt aus erreichbar, auf einem Gebäude dessen Tür; neues Ziel löscht die Stopps), optional bis zu 3 Stopps;
    je Ware Menge (`Remaining`, ∞ = `Trade.Unlimited`, sinkt je Ankunft am Markt). Träger bringen nur mit Ziel (Logistik: Markt immer
    Anteil 1, auch aus dem Ziel selbst – mit ∞ eine Endlosschleife wie im Original). Jede Einheit am Markt ruft den nächsten freien Esel;
    er lädt bis `load` (2), läuft die Stopps (Watchdog überspringt), legt am Ziel ab (an der Tür eines eigenen Lagers in dessen Stapel),
    zurück dieselben Stopps rückwärts. Feindlicher Soldat (auch Turm-Bogenschütze) schlägt einen beladenen Esel einmal: Ware fällt,
    Esel kehrt um (`Struck`, Event `donkeyRobbed`, Handel); Abladen auf fremdem Land → `World.GoodsHandedOver(von, an, Ware)` (C#-Event, nicht gespeichert); leere Esel greift niemand an (nur beladene sind `_combatants`). Ziel unerreichbar → mit Ladung heim, neben den Markt.
  - `Shipping`: Werft (`shipping` in `production.json`) braucht erst einen Steg (`Dock`: Uferfeld + Richtung, `dockLength` (4) Felder):
    `PlaceDock` setzt ihn sofort dorthin, wo sein Ende dem Klick am nächsten ist – freies, begehbares eigenes Ufer bis `dockDistance` (20)
    von der Werft, von ihrer Tür erreichbar, Steg über eigenes offenes Wasser (ab dem zweiten Feld ringsum Wasser), neuer ersetzt den alten,
    geht mit der Werft. Gezeichnet als Kartenobjekt `MapObjectCodes.Dock` (219, Datei 1 Sequenz 112 je Feld), begehbar (`GameMap.ComputeWalkable`).
    Schiffbau (`Order`, je Werft eins zugleich, `ships` mit Kosten geraten): Logistik liefert an die Werft nur, was das Schiff noch braucht
    (`Wants`); Schiffbauer (`DOCKWORKER`, Hammer, Bauarbeiter-Grafik; `Production`-Teil `Shipbuilding.cs`) holt je Einheit eine vom Stapel, trägt sie
    zum Stegende, hämmert `buildSeconds` (4); jede Einheit lässt den Rumpf wachsen (`Progress`), die letzte lässt das Schiff zu Wasser
    (`Berth`: zwei Felder hinter dem Stegende, quer zum Steg). Abriss der Werft verwirft den Auftrag. `Launch`: sofort fertig (Captures).
    `Ship` (eigene Liste, keine Siedler): fährt wie ein Soldat gesteuert (`Move`, Breitensuche übers Wasser zum nächsten erreichbaren Punkt),
    nur auf `IsNavigable` (Wasser mit `ShoreClearance` (3) Wasser ringsum, kein Steg), hält `Spacing` (6) zu anderen Schiffen (wartet, dann Umweg),
    `stepSeconds` (0,5) je Feld, Sicht `sight` (16). Fähre (`Ferries.cs`): nimmt `passengers` (7) Einheiten (Soldaten, Priester, Geologen, Späher,
    Pioniere) auf, wenn sie still liegt und begehbares Land (Ufer, Steg) bis `landingDistance` (6) hat (`IsNearLand`); `Board`: jede läuft zum
    nächsten erreichbaren Uferfeld (Soldaten erzwungen), steigt dort ein (`IsInside`, Position = Schiff, fährt mit); neuer Befehl bricht ab.
    `Unload`: alle auf die freien Landfelder nächst dem Schiff (Breitensuche ab dem Ufer). Handelsschiff 0 Plätze. Noch ohne Kampf.
    Seehandel (`SeaTrade.cs`): Hafen (`trade.harbor` = `harbor`, `Trade.IsHarbor`/`IsTradingPost`) wie Markt + Esel – Route, Ziel, Mengen in `Trade`
    (Stapel `PileRole.Trade`, Logistik Anteil 1), braucht einen Steg wie die Werft (`HasDocks`); Routenpunkte auf offenem Wasser, das der Steg
    erreicht (`SeaPoint`: nächstes in derselben Wasserfläche `SeaRegion`, nicht gespeichert; Ziel mit Strand bis `landingDistance`). Jede Ware
    am Hafen ruft das nächste freie Handelsschiff (eins je Hafen zum Laden zugleich): fährt zum Steg, lädt bis `cargoPiles` (3) Stapel à
    `pileSize` (8) einer Ware (`loadSeconds` je Einheit, wartet bis `loadWaitSeconds` auf mehr), fährt die Stopps, legt je Stapel einen losen
    Stapel auf den Strand am Ziel (kein Steg dort), zurück dieselben Stopps. Watchdog: Punkt nach 3 Versuchen nicht erreicht → Stopp übersprungen,
    Ziel → heim, Ladung neben den Steg. Handelsschiffe auf Fahrt nehmen keine Befehle (`HarborOf`, `CargoOf`).
- Teams (`World.Teams`, `AreAllies`; vom Startscreen, gespeichert): Verbündete kämpfen nicht gegeneinander (`MilitaryCombat.IsEnemy`, `Attack`,
  Turmschützen, Kriegsmaschinen `IsTarget`), Feind-Zauber verschonen sie (`Magic.Affects`), Türme/Pioniere nehmen kein verbündetes Land
  (`Claim`, `TakeLand`, `IsClaimable`), Diebe stehlen nicht bei ihnen (`Theft.IsOwn`); `ConquestGoals`: gewonnen, wenn alle Feinde besiegt sind.
  Sicht geteilt: `Vision`, `Military.Sees`, `Magic.SeesBySpell` zählen das ganze Team; ein enttarnter Späher ist dem ganzen Team des Soldaten enttarnt.
- `Military/`: Kaserne bildet an der Tür aus (Träger wird dort im selben Moment Soldat, geht nicht hinein), Rekruten laufen zum Sammelpunkt.
  Militärgebäude rufen nur untätige Soldaten bis `garrisonCallRadius` (100) Felder von der Tür; weiter entfernte bleiben stehen.
  Je Waffe ein Träger zugleich (soweit die Träger-Reserve reicht, Laufende aller Kasernen zählen), jeder wird an der Tür oder direkt daneben ausgebildet. Soll-Besatzung je Typ (`ChangeWanted`: neu 1 Schwertkämpfer; nie 0,
  nie über Infanterie-/Bogenplätze; „+“ bei vollen Infanterieplätzen ersetzt die andere Infanterieart; Überzählige kehren um oder kommen
  heraus, nie der letzte – Ersatz tauscht an der Tür). Besetzte beanspruchen Land; immer ihren eigenen Grund (sonst fiele ein eroberter Turm gleich wieder).
  - `Move`: Zielfelder um den Punkt zuerst frei (niemand steht dort, kein anderer Soldat dorthin geschickt), sonst belegte, dann reihum.
    Soldaten laufen beim letzten Schritt nicht durch einen Stehenden auf ihr Ziel (Watchdog), sondern bleiben davor (gilt als angekommen).
  - `MilitaryCombat`: Gegnersuche über Zellen (`CellSize` 8) der Kämpfer zum Tick-Beginn, je Spieler (nur Zellen der Feinde durchsucht) (`EnemiesNear`, gleiche Reihenfolge wie `EnemiesOf`).
    Untätige Soldaten und solche mit nicht erzwungenem Laufbefehl greifen feindliche Soldaten und besetzte Militärgebäude
    (Tür in Sicht) an, Näheres zuerst; Laufende gehen danach weiter; Besatzung bleibt drin. Nahkämpfer schlagen erst die Tür ein
    (`door`, HP wie Pikenier; 5 s ohne Feind im Umkreis 6 → Wächter geht rein, Tür regeneriert 0 → voll in 30 s).
    Dann je ein Verteidiger unbeweglich vor der Tür (`FightPoint`; Plätze je Volk/Gebäude als Versatz in `door.points`, sonst automatisch,
    vorn zuerst; erst Infanterie, dann Bogenschützen), nur von Nahkämpfern auf den Türplätzen angreifbar (`door.fighters`: Turm/großer Turm 2,
    Burg 1; Pikeniere wie Reichweite 1, Rest wartet). Wer den letzten Verteidiger tötet, ist drin. Wartende Angreifer: freies Feld 2–5 vom Wächterfeld, im selben Tick nie zweimal vergeben (`QueueSpot`);
    alles voll → wo sie stehen bzw. nächstes freies Feld, nie aufs Wächterfeld (sonst Hunderte auf einem Feld). Bogenschützen greifen keine Gebäude an
    (weder von selbst noch auf Befehl – `Attack` übergeht sie; KI-Feldzüge ohne sie) und schießen am Boden nicht unter `minRange` (5):
    zu Nahe → zurückweichen (`SoldierStats.InReach`/`OffReach`), auch Tür-Wächter; vom Turm ohne Mindestreichweite.
    Bogenschützen in Militärgebäuden schießen `towerRangeFactor` (1,69) weiter (`TowerRange`, auch KI-Turmfeuer). Bogenschützen treffen beim Schuss, Pfeil (`Military.Arrows`, `ArrowTicks`) nur Darstellung. Abklingzeit zwischen Schlägen/Schüssen je Soldat (`_nextShot`), nicht je Befehl: ein neuer Befehl mitten im Kampf setzt sie nicht zurück. Nahe am Feind lokale Wegsuche (`combatPathRadius`).
    „Position halten“ (`Military.Hold`, Auswahl-Panel bei Soldaten, an der Stelle von „Land einnehmen“; `Move(…, hold: true)` erst hinlaufen):
    nicht untätig (kein Gebäude ruft ihn), kämpft nur gegen Feinde in Reichweite, verfolgt niemanden, kehrt auf den Platz zurück, bis zum nächsten Befehl
    (`HoldTarget`/`Holds`; das Lazarett ruft Haltende nicht). KI: Verwundete, geparkte Nahkämpfer, Bogenschützen der Schlachtordnung.
    Wegpunkte (`AddWaypoint`, Shift+Rechtsklick): mit Befehl (Laufen, Angriff, eigener Kampf) erst dieser, dann der Reihe nach bis `MaxWaypoints` (3)
    Punkte, ein weiterer ersetzt den letzten (`Fighter.Waypoints`, `Dismiss` macht den nächsten zum Laufbefehl); ohne Befehl (oder haltend/patrouillierend) wie `Move`.
    Patrouille (`Patrol`, Alt+Rechtsklick): zum Punkt und zurück zum Standort, hin und her bis zum nächsten Befehl (`Fighter.PatrolBack`); kämpft
    unterwegs wie beim Laufbefehl, eigenständige Angriffe enden mit der Patrouille weiter (`EndAttack` behält `MoveTo`).
  - Kampfkraft (`CombatStrength`): eigenes Land 100 %, sonst Startwert je Spielerzahl (`startPercent`, im Original gemessen: 1 → 54 % (wie dort, obwohl sinnlos), 2 → 50 %, 4 → 37 %, 8 → 25 %, 16 → 14 %); plus Gold im eigenen Land (nach Landbesitz; Ägypter: Gold
    halb, Edelsteine doppelt), jeder weitere Prozentpunkt kostet nach erreichter Kampfkraft (Bänder `stepPercent` ab 0 %, `unitsPerPercent`, im Original gemessen: bis 50 % 1,2 Barren, ab 90 % 20; niedriger Start steigt schneller), ohne Obergrenze (200 % = doppelter Schaden außerhalb; zwingt Reiche zum Angriff). KI kennt alle Werte und bewertet den Kampfort.
  - Werte: `data/military.json`; Gebietsradien aus Originalkarten gemessen.
  - `WarMachines` (`warMachines` in `military.json`): je Volk eine Maschine (Katapult, Balliste, Kanone, Gong) aus seiner Werkstatt:
    `Order` reiht Maschine oder Ladung Munition ein (Gong ohne; `Cancel` nimmt die letzte nicht begonnene zurück), Logistik liefert nur, was
    der aktuelle Auftrag braucht (`Wants`), der Werftarbeiter hämmert Einheit für Einheit vor der Tür (`IAssembly`, gemeinsam mit der Werft,
    `Shipbuilding.cs`); Ladungen liegen an der Werkstatt (`Loads`). Maschine = `Profession.WarMachine` (Volk in `People`, `Ammo`), zählt nicht
    als Bevölkerung, fährt wie Soldaten (`Move`, Tempo `speed`), belegt ihr Feld und die 6 Nachbarn (`Settler.IsWide`/`Body`: Wegsuche nur über Punkte mit
    6 begehbaren Nachbarn (`PathFinder.IsClear`, exakt, höchstens `WideSearchLimit` Knoten; kommt sie nicht durch, fährt sie so nah wie möglich – im Original
    auch), schiebt Untätige beiseite, niemand tauscht mit ihr, Wartende planen um alle 7 Felder herum, Nahkampf reicht bis an den Rand), feuert nur auf fertige feindliche Militärgebäude (`Attack`; stehende auf alles in
    `range`) alle `cooldownSeconds` (5) mit `hitChance` (Katapult 50 %, Balliste 75 %, Kanone 90 %, Gong 50 % – vom Nutzer), Geschoss
    `MachineShot` (Gong sofort), Einschläge kurz in `Impacts`; jeder Treffer zählt gegen `buildingHits` (`DamageOf`), der letzte reißt es ab
    (Besatzung kommt lebend heraus, Event `buildingDestroyed`); ein Treffer heilt je `repairSeconds` (60) nach dem letzten. `shots` (6) je Ladung; `Reload`: zur nächsten eigenen Werkstatt mit Ladung. Nahkämpfer zerstören Maschinen
    (`health`, Staub statt Geist), Bogenschützen nicht (`EnemiesOf(…, bowman)`). Reichweite, Tempo, LP, Treffer je Gebäude geraten.
    Noch nicht: Berge meiden, KI. Pulvermacherei (Asiaten): Kohle + Schwefel → Schießpulver (`POWDER_MAKER`, ohne Werkzeug).
- `Magic/`: Mana je Spieler. Kleiner Tempel: Wein/Met/Bier/Schnaps → Mana (eine Einheit je `templeSeconds` 35,7 wie im Original, Schnaps 2 Mana); großer Tempel schickt sofort und alle `priestSeconds` einen Priester.
  Zauber wie im Original (Werte aus wiki.siedler3.net „Mana und Magie“): je Volk 8 im Menü (`menu`, Reihenfolge nach den Icons; `own` = Volkszauber
  der KI), je Zauber `cost`, `radius` (0 = ganze Karte), `count`, `seconds`, `factor`; `placeholder` = noch nicht umgesetzt (sichtbar, nicht wirkbar).
  `Cast`: Priester läuft bis `range` (24) heran (`anywhere`: wirkt von der Stelle, unbegrenzt weit – Verstärkung, Waren rufen/versenden; ohne Radius sofort beim Menüklick),
  zaubert nur, wo etwas Betroffenes ist (`Magic.HasTargets`/`Affected`, nächste zuerst bis `count`), kein Kampf nötig; Kosten je Spieler und Zauberart
  mit jedem Wirken `costIncrease` % (25 wie im Original, aufgerundet: 20, 25, 32 …) teurer (`Magic.CostOf`, Menü zeigt den aktuellen Preis); wirkt sofort bei Zauberbeginn (`castSeconds` nur die Geste).
  Einheiten bewegende/verwandelnde Zauber (Bekehren, Verbannen, Verstärkung, Bogen zu Pionieren) nehmen Priester, Soldaten, Späher, Geologen, Pioniere draußen,
  keine Soldaten eines Militärgebäudes (`Military.IsTowerBound`: Besatzung, Tür, unterwegs hinein); Zauber auf Soldaten für eine Zeit (`Settler.Charm`,
  `SpellCharm`: Samuraischwert nur Schwertkämpfer ×2 Tempo ×1,33 Schaden, Angst ×0,5 Schaden auf dem Land des Zaubernden, Stärken ×2 außerhalb des eigenen Lands,
  Schild ×0,67 erlittener Schaden, Verfluchte Pfeile kein Pfeilschaden; Einfrieren `FrozenUntil`) auch die (Turm-Bogenschützen eingefroren schießen nicht);
  erneut gewirkt verlängert, addiert nicht (`Military.DamageOf`/`Taken`). Waren-Zauber (Midas, Fisch zu Fleisch, Stein zu Eisen, Gold zu Stein: bis 40 Einheiten
  im Umkreis, egal wessen, lose/Ausgabe/Lager, nicht reserviert, `World.TakeUnits` → `DropGoods`; Waren rufen: vom eigenen Land zum Priester; Waren
  versenden: die um den Priester zum Ziel; beide unbegrenzte Reichweite). Sicht (`SpellSight`, `Magic.Sights`: Auge Radius 32, Karte enthüllen ganz, 20 s; Fog of War und `Military.Sees`).
  Land (`LandSpells.cs`): Boden wandeln (Grasland, Horus' Hitze, Sumpf trockenlegen – Moor und Schlamm –, Schneeschmelze → Gebirge) über Bodenklassen,
  Ränder je ein Feld wie der Zufallskartengenerator (`Paint`, nicht unter Gebäuden/Stapeln, Ackerland/Pfade, die nicht mehr passen, weg; der Client zeichnet
  `TerrainKinds.Of`, Minimap noch das Original); Rohstoffe halbieren, Fischflut (+1–4 je Wasserfeld, max. 15), Waldbrand (Bäume im Umkreis → Stümpfe,
  nach `FireSpreadTicks` einmal die direkt daneben, Flammen), Waldgunst (ausgewachsene Bäume auf freies Gras), Steinfluch (10–20 Felsen verschiedener Größe, 2 Felder Abstand),
  Wachstum (`Production.Hasten`: Getreide/Wein/Reis spätestens in 180 s reif). Himmlische Geschenke (`SummonGoods`: 3–4 Stapel à 4–8 aus Nahrung, Waffen,
  Werkzeug, Gold, Eisen, Kohle, Erz im Umkreis `SummonSpread`; Ägypter 50 % zusätzlich Steine; Flammen zeitversetzt). Priester angreifbar.
  Alte Spielstände übernehmen die aktuellen Regeln (`Magic.FollowRules`). Zwei Konten: Zauber verbrauchen `Mana`, Aufwertungen prüfen `ManaEarned`
  (`upgrades.manaEarned` je n-ter Aufwertung, Original: 10/30/60/110/170/200). `Upgrade`: braucht fertigen kleinen Tempel; erst alle drei Typen Stufe 2, dann 3;
  gilt nur für danach Ausgebildete. Werte: `data/magic.json`.
- `Commands/` (Lockstep-Vorbereitung): Spielerbefehle als Daten (`GameCommand`-Records: Spieler, IDs von Siedlern/Gebäuden/Schiffen, Punkte,
  Enums, Zahlen; Gebäudetyp als `BuildingDefinition`, über Volk + Name eindeutig), wirken nur auf Eigenes des Spielers. `World.Commands`
  (`CommandQueue`): `Issue` vergibt `Sequence`, `World.Step` wendet als Erstes alle wartenden in dieser Reihenfolge an, Ergebnisse in `Applied` bis
  zum nächsten Schritt; pausiert allein `ApplyWhilePaused`. Gruppen: `UnitCommands` (Rechtsklick `MoveUnits`: feindliches Gebäude angreifen, sonst
  laufen; Wegpunkt, Patrouille, Halten, Spezialisten, Priester, Zauber, Schiffe), `BuildingCommands` (Baustelle, Abriss, Pause, Vorrang,
  Arbeitsbereich, Besatzung, Lager, Markt, Werft, Kriegswerkstatt), `EconomyCommands` (Verteilung, Transport, Schmiede, Berufe, Rekrutierung,
  Aufwertung), `ChatCommands` (`Say`, `AiReply` für den KI-Chat, `Cheat`). Wartende Befehle stehen im Spielstand.
- `Persistence/`: `Snapshot` schreibt den ganzen Objektgraphen per Reflection (alle Felder, auch private, Objektidentität; Typtabelle mit
  Feldnamen – fehlende übersprungen, neue: Sammlungen/Klassen mit parameterlosem Konstruktor leer, nicht-nullbare Referenzen ohne solchen → inkompatibel, sonst Standardwert (nicht der Initialisierer!); Dictionary/HashSet mit innerem Aufbau, Hashes beim Laden neu → Partie läuft
  tickgenau weiter, `SaveTests`). Delegates nur mit Zielen in eigenen Assemblies (Client-Abos werden nach dem Laden neu gesetzt);
  `[NonSerialized]` lässt weg; fremde Typen brechen mit Pfad ab, `JsonElement` als Text. Neue Felder brauchen keinen Speichercode.
  `SaveSlots.Load` rechnet die Begehbarkeit nach (`GameMap.RefreshWalkable`; Stege früherer Builds). Typnamen ohne Assembly-Version aufgelöst; fehlender Typ/Methode → `SaveIncompatibleException`. `SaveLoadFailure.Of`: Art (inkompatibel/beschädigt/unlesbar)
  und Fehlercode `HS-<Art>-<FNV-Hash>`; Titelbildschirm zeigt ihn in der Meldungsbox (`ShowLoadFailure`), Konsole die Ausnahme.
  `SaveGame`: Welt, Kataloge, Karte, Sicht, Client-Werte. `SaveSlots`: 8 Slots `slot N.hsav` in `~/.config/homestead/saves`
  (`HOMESTEAD_SAVE_DIR`), Kopf `SaveInfo` (Karte, Volk, Zeit, Tick) unkomprimiert, Rest gzip, über temporäre Datei.
- `Events/` (`GameEvents`, `World.Events`): Meldungen je Spieler aus `data/events.json` – Kategorie Info/Kampf/Rohstoffe/Handel,
  `cooldownSeconds` je Spieler/Event (0 = immer), Text (`{building}`), an/aus. `Raise(id, spieler, ort, settler/building)`.
  Kampf: `attacked`, `buildingAttacked` (Tür/Wächter), `buildingLost`/`buildingCaptured`. Zustände fertiger Gebäude prüft `Tick` sekündlich,
  meldet nach `afterSeconds` einmal bis Ende: `noStaff`, `noTool`, `noResources`, `mineExhausted` (Chance ≤ `maxChance` %). `Announce`: ohne Ort.
- `ChatLog` (`World.Chat`, `World.Say(spieler, text)`): globaler Chat, Zeilen mit Tick und Spieler in Reihenfolge, getrimmt, leere nichts,
  höchstens `MaxLength` (200) Zeichen; mitgespeichert.
- `SoundCues` (`World.SoundCues`, nicht gespeichert): nur gesammelt, wenn der Client eine Liste setzt (leert sie je Frame); `Strike` bei jedem
  Treffer/Schuss (auch an der Tür, mit Soldatentyp), `Death` in `MilitaryCombat.Kill` (mit Volk des Toten), `TreeFalls` beim Fällen, `BuildingFalls` in
  `RemoveBuilding`, `Spell` bei Zauberbeginn, `Healing` (Lazarett), `Claiming` (Pionier-Pfosten), `GeologistFinds` (mit Menge),
  `Sowing` (mit `Trade`); `Strike` trägt das Volk (Ägypter-Bogen klingt anders).
- `GameHistory` (`World.History`): alle 30 s (`SampleTicks`) je Spieler eine `HistorySample` (Land, Siedler nach Art, Soldaten nach Typ,
  Gebäude, Baustellen, Militärgebäude, Wohnraum, Streikende, Lagerbestand, Produziertes, Kampfkraft, Mana) plus laufende Summen
  (getötet/gefallen, Gebäude erobert/verloren, fertiggestellt/zerstört); für den Statistik-Bildschirm.
- `OriginalScenario`: Karte → Welt (Gebäude, Stapel, Siedler; Siedlertyp 23 = Späher/Dieb, 22 Geologe, 36 Priester, 40 Pionier; Besatzung der
  Militärgebäude aus der Karte sofort drin, `Military.Station`, Soll = Besatzung; ohne Angabe 1 Schwertkämpfer Stufe 1 wie im Original). Karten ohne Gebäude (Mehrspieler, auch Einzelspieler wie Beginner I/III): `StartSetup` gibt jedem Spieler die Ausstattung aus `data/start.json` (Startturm will nur Infanterie, Bogenschützen warten draußen).
- `Map/Generation/RandomMapGenerator`: Zufallskarten als `OriginalMap` (`OriginalMap.Create`), gleiche `RandomMapSettings` + Seed = gleiche Karte,
  Werte `data/randommap.json`, Größe nach Spielerzahl. Eine Landmasse mit zerklüfteter Küste (verzerrtes Rauschen, halb Kreis, halb Raute;
  Abgetrenntes versinkt, abgeschnittene Starts bekommen eine Landbrücke zur Mitte); gespiegelt ein Kreis in Rastergeometrie. Jeder Start gleich: flache Startfläche,
  Berg mit Kohle/Eisen/Gold, Teich mit Bach (Wasserwerke brauchen Flüsse), Wald, Steine. Dazu Gebirge, Seen, Flüsse zum Meer, Sümpfe (mind. einer),
  Wälder, Steinfelder, Erzadern, Fisch, Deko, Schätze (Warenstapel ≥ 32 Felder von Starts; „Viel“: dazu ein Hort voller Stapel Gold/Waffen in der
  Kartenmitte, `hoard`). King of the Hill (`RandomMapSettings.KingOfTheHill`, `kingOfTheHill` in `randommap.json`): Mitte als Hügel mit Hort,
  Bergen zwischen den Durchgängen (volle Adern Gold/Edelsteine/Kohle) und Ring aus unbegehbarem Moor (80/81) mit je einem Durchgang Richtung Start;
  Heimat-Merkmale halten Abstand (`OnHill`). Flüsse als stetig drehender Verlauf in Halbfeld-Schritten (bergab, Mäander per Rauschen), stromab breiter; Grasland leicht uneben. Symmetrie: `Mirror` (Keilfläche; exakt bei 2/3/4/6,
  5 gerundet), `Repeat` (Startkachel `homeRadius` kopiert). Landschaftsbytes zuletzt aus Bodenklassen (Wassertiefe, Strand, Ränder je ein Feld,
  damit jedes Dreieck eine Original-Übergangskachel hat – Merkmale halten Abstand). Starts ohne Verbindung → neu würfeln.
