# SND format (`SND/Siedler3_00.dat`) and music

Status: verified against the GOG Ultimate Collection, cross-checked against JSettlers (`jsettlers.graphics/.../sound/SoundManager`).
All values little endian. Parser: `src/Homestead.Assets/Sound/SoundFile.cs`.

## Files
- `Siedler3_00.dat` (~11.9 MB): all sounds. Letter case is inconsistent.
- `siedler3_01.dat` (~160 KB): same header, contains only a few recordings. Not used.

## Header
| Offset | Content |
|--------|--------|
| 0x00 | Signature `44 15 01 00 02 00 00 00 00 00 00 00 1C 00 00 00` |
| 0x10 | File size |
| 0x14 | `FF FF FF FF` |
| 0x18 | `0x1C` |
| 0x1C, 0x20 | unknown |
| 0x24 | Table: one offset per sound to its variant list |

The table ends where the first variant list begins: (first offset − 0x24) / 4 = **119** sounds
(JSettlers reads 118).

## Variant list
`int32 count`, then per variant the `int32 offset` of the recording. The game picks one at random.

## Recording
| Offset | Content |
|--------|--------|
| 0 | Length of the whole entry (header + data; the next recording follows directly) |
| 4 | WAVEFORMAT: format 1 (PCM, `uint16`), channels 1 (`uint16`) |
| 8 | Sample rate 22050 |
| 12 | Bytes per second 44100 |
| 16 | Block size 2 (`uint16`), bits 16 (`uint16`) |
| 20 | Samples, signed 16 bit; (length − 20) / 2 of them (odd lengths: last byte left over) |

## Usage
Bold: used in-game (`data/sound.json`). "By ear": recognised in the original by the project owner, otherwise from JSettlers.

| No. | Sound | No. | Sound |
|-----|----------|-----|----------|
| **0** | **woodcutter** | **36** | **falling tree** |
| **1** | **builder** | **37/38** | **iron smelter** (by ear), also gold smelter |
| **2** | **digger** | 39 | pigs |
| **3** | **stonecutter** | 40/41 | donkeys |
| **5** | **sawmill** | 42 | mill |
| **6/7** | **smith** (tools/weapons) | 45 | charcoal burner coughs |
| **8** | **farmer sowing** (by ear); 9, 12 farmer | **47/48** | **order/share up/down** (by ear) |
| 13 | fire | **54** | **switch on/off** (by ear) |
| 14 | butcher | **57** | **command to soldiers** (by ear; 58/59 same recording) |
| 15–17 | fisherman (**16** used) | 62 | click |
| 20 | shipyard | **67** | **desert** (ambience) |
| **21** | **hospital: healing** (by ear) | **68** | **sea** (ambience) |
| **22** | **work area moved** (by ear) | **69/70** | **birds** (ambience) |
| **24** | geologist; **variants 1–3: large deposit** (by ear) | **71** | **river** (ambience) |
| **25** | **variant 1: pioneer sets a border post** (by ear; JSettlers: bowman) | **72** | **swamp** (bursting bubble) |
| **51** | **click on the demolish icon** ("crack", by ear) | | |
| **30** | **swordsman** (strike) | **73/74** | **wind** (ambience, mountains) |
| 31 | ? | **75** | **casting** (by ear; JSettlers: thunder) |
| **32** | **Egyptian bow shot** (by ear) | 76 | rain |
| **33** | **bowman** (shot, except Egyptians; by ear) | 80 | "Ihr werdet angegriffen" |
| **34** | **pikeman** (thrust) | 85 | arrow shot |
| **35** | **death** (9 variants) | 86–90 | cannon? (87: short dull "clonk") |
| **113** | **death of an Amazon** (4 variants) | **93** | **building demolished/destroyed** (by ear) |
| 117 | bees | 106 | tool missing |
| | | **109** | **event: attack** (by ear) |
| | | **110** | **event: all others** (by ear) |

## Music
`MUSIC/Track02.ogg` … `Track14.ogg` (Ogg Vorbis, the former CD audio tracks; not present in every edition).
Which track belongs to which people is documented nowhere – the mapping lives in `data/sound.json` (`music.tracks`).
