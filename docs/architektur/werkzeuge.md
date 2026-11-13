# Tools (`tools/`)

- `tools/Homestead.Arena` – AI against AI without a window: `dotnet tools/Homestead.Arena/bin/Release/net9.0/Homestead.Arena.dll
  [map… | S3QOTA/MIS_M/Z/Z10.MAP | random:4:mirror:7] [--all] [--minutes 30] [--every 5] [--peoples amazon,asian] [--characters ,general] [--seed 1] [--log] [--notes] [--profile]
  [--until-decided] [--csv <file>]`. Maps by name (under `Map/`) or by path in the installation (missions);
  trait decisions immediately (all of them with `--notes`); `--profile`: ms per tick and part (`World.StepProfile`);
  `--seed` gives the peoples and the world (`World.Seed`) their randomness: same seed, same match. Output per player: land (free, water share), buildings,
  mana, priests, settlers, housing/strikers, stocks, stalled construction sites. `--until-decided`: ends when only one player has military; `--csv` appends results.
- `tools/trait-tournament.py run|report` – tournament of AI characters/traits via the Arena (every pairing, map, people, both starting positions;
  parallel, resumable); `--preset traits|peoples`, `egyptian:hothead` only with its own people, `--against`, `--vary path=a/b/c`;
  results in `tournament-results/<preset>.csv` (not in the repo).
- `tools/Homestead.AssetTool` – `scan`, `export`, `sheet` (PNG), `frames` (all images of a few sequences, one per row), `maps`, `resources`, `minimap`.
