# GFX format (`GFX/siedler3_NN.<mask>.dat`)

Status: verified against the GOG Ultimate Collection, cross-checked against JSettlers
(`jsettlers.graphics/.../image/reader`). All values little endian.

## Files
- Each number comes in two colour variants: `.7c003e01f.dat` = RGB555, `.f8007e01f.dat` = RGB565
  (suffix = colour masks R/G/B). Content otherwise identical. We use 565.
- Letter case of the names is inconsistent (`Siedler3_` vs. `siedler3_`).
- **No. 18 is not a graphic**, but a Windows DLL (`MZ`). Skipped, not analysed.
- **Nos. 07/08/09** have no images but a text table (block `0x1904` directly after the header), see below.

Rough contents (visually confirmed: 00, 12, 13; the rest guessed from table sizes and JSettlers):

| No. | Content |
|-----|--------|
| 00 | Terrain textures (235) |
| 01 | General settler animations, with shadows |
| 02, 03, 14, 24, 34, 44 | GUI graphics |
| 10/20/30/40 | Settlers per people (Romans/Egyptians/Asians/Amazons), with torso and shadow |
| 11/21/31/41 | Further settler animations per people |
| 12/22/32/42 | Military per people: war machine, soldiers (JSettlers: sequences 14–29) |
| 13/23/33/43 | Buildings per people (Romans: sequence 0–45, frame 0 = finished, 1 = construction site), then effects (fire, smoke, magic) |
| 15/25/35/45 | Only animation tables (`0x21702`) |

### Individual finds
- File 01, sequence 26 (33 images, 64 × 36): turquoise sparkle dots, images 0/1 and 30–32 empty, in between growing, then fading –
  the glitter on the water (`WaveLayer.DrawGlitter`).
- Beehive (Amazon beekeeper): no image of its own found (searched in files 01, 41, 43, 46). The straw skep exists only on the back
  of the beekeeper (file 41, sequence 287 walking, 291–296 placing). As a substitute, the hive shows 41/291 image 0 (the beekeeper placing it).
- File 43, sequence 33: Amazon marketplace – image 0 the round square, image 1 the golden statue on top (finished = both on top of each other),
  images 2/3 the same as a construction site.

### War machines (visually confirmed, `warMachines` in `data/ui.json`)
JSettlers does not know them; finds from the files themselves. Directions always SW, W, NW, NE, E, SE.

| People | Machine (file 12/22/32) | Works (building file, GUI icon) | Icons (GUI of the people's file, three sizes each) |
|------|------|------|------|
| Romans | Catapult, file 12: seq. 0 body (6 images, one per direction), 1 wheels (216 = 36 per direction, above the body), 2–7 firing (24 images), 8 stone (12, rotating) | 13/27, icon 14/96 | 234 catapult, 237 pressed, 240 ammunition (stones), 243 pressed |
| Egyptians | Ballista, file 22: like the Romans, firing 15 images, 8–13 bolt per direction (3 images) | 23/31, icon 24/99 | 228/231/234/237 |
| Asians | Cannon, file 32: like the Romans, firing 18 images, 8 ball (12) | 33/34, icon 34/111; gunpowder maker 33/28, icon 34/105 | 243/246/249/252 |
| Amazons | Gong, file 42: seq. 0 moves like a settler walks (6 × 12), 1–6 gong strike (39) | 43/32, icon 44/102 | 237/240 (243/246: stones, unused) |

## Header
| Offset | Content |
|--------|--------|
| 0 | Signature `04 13 04 00` |
| 33 / 37 | Colour masks (`7c`/`03` = 555, `f8`/`07` = 565) |
| 48 | File length (u32), must match the actual length |
| 52 | 8 × u32 offsets to blocks |

## Blocks
`u32 type, u16 byteCount, u16 count, u32[count] offsets`; for offset tables,
`byteCount == count*4 + 8`.

| Type | Content | Entry points to |
|-----|--------|-------------------|
| `0x1904` | Header end / text table (07–09) | – |
| `0x2412` | Terrain | Bitmap (terrain header) |
| `0x2607` | Palette? (always empty, 4 extra bytes) | – |
| `0x106` | Settlers/buildings (colour) | Sequence |
| `0x3112` | Torso (player colour) | Sequence |
| `0x5982` | Shadow | Sequence |
| `0x11306` | GUI | Bitmap (GUI header) |
| `0x21702` | Animation tables | still unknown |

The torso and shadow tables are often shorter than the settler table and are **not** assigned by index.
Rules adopted from JSettlers (`AdvancedDatFileReader.initialize`), see `GfxFile.AlignLayers`:
- Torso: missing entries are at the beginning (torso `i - (settlers - torsos)`).
- Shadow: at a difference of 7 or 8 they are missing at the beginning, otherwise at the end.
- Special cases, recognised by table sizes: buildings (difference 26, files 13/43), ships (28, file 36),
  Roman specialists (239 entries, file 11).
- Soldiers (files 12/22/32/42, our own mapping by image count, which matches shadow for shadow):
  shadows 0–18 belong to the soldiers from sequence 9 (Romans, Asians), 14 (Egyptians) and 7 (Amazons)
  respectively, shadows 19–25 to the sequences before them (sequence 0, then 2–7; Amazons 0–6). JSettlers shifts Romans/Asians by 8
  and Amazons by 6 – bowmen then get neighbouring shadows, a firing level 3 an empty one.

Individual sequences (visually confirmed):
- File 01, sequences 100–105: flying arrow per direction (SW, W, NW, NE, E, SE), 3 frames: rising, level, falling.
- Files 12/22/32/42: `BOWMAN_L3`/`ACTION1` is followed by a sequence with 22 frames (colour layer empty), then 6 sequences
  of 2 frames each: infantry in the tower window (swordsmen, then pikemen, levels 1–3 each; frame 0 looks left,
  1 right). Romans/Asians 28–33, Egyptians 33–38, Amazons 26–31.
- Bowmen `ACTION1` (24 frames per direction): the arrow leaves the bow after frame 17.
- File 04 (sequences, no GUI table): side panel on the left, 3 frames per sequence for 640/800/1024 pixel width.
  Sequence 0/1: minimap frame (left/right, 136×142, 176×170, 216×224), sequence 2: panel below it
  (136×338, 176×430, 216×544), sequence 3: an 8-pixel-wide strip. Large panel measured out: main tabs y 0–62,
  dark bar for sub-tabs y 64–102, content area x 10–206, y 116–474, five buttons at the bottom.

GUI images (table `0x11306`):
- Building icons (14/24/34/44): three sizes per building in a row (small, medium, large); JSettlers points to the small one.
- File 03, building panel (small/medium/large each): 192–194 hand (resume work), 195–197 gears (highest priority; 378–380 with "!"),
  198–200 collapsing building (demolish), 201–203 rings (work area: red outer, gold, green in the middle), 390–392 hammer with arrow
  (shipyard dock), 393–395 house (stop work).
- File 03, build categories (27×24, 36×32, 45×40 each): 69–71 basic buildings, 72–74 food, 75–77 military,
  78–80 social; 81–92 the same pressed (selected). 51–68: main tabs (Build, Goods, Settlers; 33/44/55 each, pressed from 60).
- File 02, GUI 20 (100×600): blue background texture.

## Sequence
`02 14 00 00 08 00 00`, `u8 frameCount`, then `u32[frameCount]` offsets **relative to the sequence start**.

## Bitmap header
- Settlers/torso/shadow: `u32 0x0c, u16 width, u16 height, s16 offsetX, s16 offsetY`
- Terrain: `u16 width, u16 height, u16 ?`
- GUI: `u16 width, u16 height, u16 ?, u16 ?`

Then padding to the next even **absolute** file position (1 byte padding).

## Pixel data
Row by row, per row a sequence of runs with a u16 control word:
- Bits 0–7: number of following pixels
- Bits 8–14: transparent pixels to skip before them
- Bit 15: last run of the row

Pixels: colour = u16 (555/565), torso = u8 as an index into the player palette (derived from the neighbouring pixels): 0–21 one ramp
dark → light, after that ramps of 9 steps each (0x16–0x1E, 0x1F–0x27, … 0xF7–0xFF), mostly scattered among the first one.
The palette is in none of the data files; we tint each ramp with the player colour according to its step (`GfxColor.TorsoToRgba`).
JSettlers masks 5 bits as brightness – that produces black and white dots.
Shadow = no data (semi-transparent black).
Small terrain tiles use cyan as background/key colour.

## Text tables (files 07–09, block `0x1904`)
`u32 0x1904, u16 byteCount, u16 0, u16 count, u16 languages (8)`, then `languages × count` u32 offsets (absolute in the
file, language by language), followed by the null-terminated texts in Windows-1252 (Polish 1250, Korean 949).
Languages: 0 German, 1 English, 2 Italian, 3 French, 4 Polish, 5 Spanish, 6 Korean, 7 unknown.
Reading: `GfxFile.ReadTexts(language)`.

File 07 (204 texts): 0–23 campaign mission descriptions (8 each: Romans, Egyptians, Asians; mission name,
victory/defeat condition, situation, diary), 24–47 Mission CD, 48–71 Amazons, 72–95 campaign hints
(same order), 96–143 "Tips & Tricks" of the Mission CD and Amazons, 144–202 tutorial. Files 08 (399) and
09 (374): not examined yet.
