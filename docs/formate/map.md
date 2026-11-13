# Map format (`Map/**/*.map`)

Status: all 198 maps of the GOG Ultimate Collection readable. Cross-checked against JSettlers
(`jsettlers.logic/.../map/loading/original`). All values little endian.

## Layout
| Offset | Content |
|--------|--------|
| 0 | Checksum (u32) |
| 4 | Version: `0x0A` original, `0x0B` Amazon maps |
| 8 | Chunks up to type 0: `u32 type` (lower 16 bits), `u32 length` incl. 8-byte header, data |

Chunk contents are obfuscated with a rolling XOR key (initial value = chunk type):
`plain = byte ^ key; key = (key << 1) ^ plain` (only the lower 8 bits count).
This is an obfuscation of the data format, not copy protection: there is no key and no
license check; the game and the editor read the maps without any trouble.

## Chunks
| Type | Content |
|-----|--------|
| 1 | Map info: `u32 singlePlayer (1/0), u32 playerCount, u32 startResources` |
| 2 | Players: per player `u32 people, u32 startX, u32 startY, char[33] name` |
| 3 | Teams (not read yet) |
| 4 | Preview image: `u16 size, u16 ?`, then RGB565 |
| 6 | Terrain: `u32 size` (maps are square), then 6 bytes per point |
| 7 | Settlers: `u32 count`, per settler `u8 player, u8 type, u16 x, u16 y` |
| 8 | Buildings: `u32 count`, 12 bytes each: `u8 player, u8 type, u16 x, u16 y, u8 ?`, then garrison as nibbles (high\|low): swordsman 1\|2, bowman 2\|3, swordsman 3\|bowman 1, pikeman 3\|?, pikeman 1\|2 – only meaningful for military buildings, otherwise remnants |
| 9 | Goods piles: `u32 count`, each `u16 x, u16 y, u8 type, u8 amount, 2 bytes ?` |
| 10 | Victory conditions |
| 11/12 | Quest text / quest hint (C strings, Latin-1) |

## Terrain point (chunk 6)
`height, terrain, object, owner, walkability, resource` (last: upper nibble type, lower nibble amount).

Resource types (upper nibble), confirmed by statistics across all maps (`AssetTool resources`):
0 fish (water only), 1 coal, 2 iron ore, 3 gold ore, 4 gems, 5 sulfur (mountains; gems
occasionally also desert). Amount 0–15, overwhelmingly 8 (the editor's default), then 15.
Value 0 = nothing.

Owner byte (index 3): player number from 0, 255 = nobody. The border of the land around military buildings lies
(in the circular measure dx²+dy²−dx·dy) at 931 for the tower, 967 for the large tower, 1101 for the castle –
measured across all maps; some maps additionally assign land without military buildings (editor).

Building types (chunk 8) per JSettlers' `EOriginalMapBuildingType`: 15 tower, 16 large tower, 18 castle (garrison across all
maps mostly 1+2, 3+3, 6+4 infantry+bowmen), 11 iron smelter, 17 mill; temples have their own numbers per people (41–46, 54/55).

Settler types (chunk 7) per JSettlers' `EOriginalMapSettlersType`: 0 carrier, 1 digger, 2 builder,
5/32/35 swordsman level 1/2/3, 8/30/33 bowman, 15/31/34 pikeman, 22 geologist, 36 priest, 40 pioneer, 23 thief/scout (from the campaigns:
Mission CD J1 four scattered individually across the map – "Unsere Kundschafter" –, Amazon campaign G11 57 for stealing).
Order row by row, `index = y * size + x`, y = 0 is the top edge of the map.

Terrain values (group = upper nibble): 0–7 water (depths), 16 grass, 17 mountain edge outside,
20/21/23 desert/moor/mud edge outside, 32/33 mountain/-edge, 35 snow edge outside, 48 sand,
64/65 desert/-edge, 80/81 moor/-edge, 96–99 river, 128/129 snow/-edge, 144/145 mud/-edge.
JSettlers lacks 66–71 and 112 – we handle them by group. 66–71 occur only on "384-2-Vulkanauge",
always together with object code 192 (special terrain of this one map, presumably lava).

## Objects (byte 3 of the terrain point)
Graphics in GFX file 01 (`MapObjectCatalog`). Names from JSettlers' `EOriginalMapObjectType`.
Visually cross-checked against the original on 11 October 2026 – no deviation found (quarry stones too).

| Codes | Object | Sequence / image | Status |
|-------|--------|----------------|--------|
| 1–8, 9–12, 13–20 | large stones, stones, boundary stones | 29 / code−1 | confirmed (8 boundary stones = images 12–19) |
| 21–28 | small stones | 30 / code−21 | plausible |
| 29–33, 34 | wrecks, grave | 28 / code−29 | confirmed |
| 35–67 | plants, mushrooms, stumps, dead trees, cacti, bones, flowers, bushes, reeds | 27 / code−35 | confirmed (33 images, order matches) |
| 68–81 | trees (7 kinds × 2 variants) | 1 + (code−68)/2·3 + (code−68)%2, animated | confirmed (78/79 "Arecaceae" = palms 16/17) |
| 84 | small tree | 22 / 0 | plausible |
| 111–114 | reefs | 30 / code−103 | uncertain |
| 115–127 | quarry stones, 13 sizes | 31 / code−115 | visually confirmed (115 = full, 127 = empty); 127 does not block according to the maps |
| 192 | only "Vulkanauge" | – | unknown |

Trees: three sequences per kind – two swaying animations (17 images) and one for felling/stump/growing (13 images).

## Walkability
Byte 5 of the terrain point, bit 0: 1 = not walkable (values 8/9 rare, meaning of bit 3 open).
We compute walkability ourselves from rules (`GameMap`) because it changes during the game; the byte serves
as a check (`OriginalMapTests`, 99.8% agreement across all maps):
- Terrain: water, moor (+ inner edge), snow (+ inner edge) block; rivers, mud, outer edges do not.
- Objects: trees block their field, quarry stones (115–126) their field plus the six neighbours,
  code 127 (empty stone) and decoration do not block.
- Remaining deviations: terrain around pre-placed buildings (comes with loading the buildings),
  palms on sand (walkable in the original, blocking for us), the map "Vulkanauge".

## Rendering
See `Homestead.Client/Terrain`: two triangles per grid point, height raises the point by 2 px per unit.
Textures from an atlas of the 235 terrain images (GFX 00): continuous textures when all three corners
are the same, otherwise transition tiles per terrain pair. Tables adopted from JSettlers' `Background`.
