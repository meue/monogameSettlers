# Capture (`--capture <dir>`)

Offscreen PNGs for inspection, only into temporary directories (no original assets in the repo).

- **Capture** (`--capture <dir>`, offscreen PNGs, temporary directories only): ignores input, without VSync, 4× speed; prints counters and
  building states at the end. Environment variables `HOMESTEAD_CAPTURE_…`:
  `ZOOM=4`; `EVERY=<n>` (a PNG every n frames, otherwise 120); `SIZE=1920x1080` (otherwise 960×600); `SCALING=<Sharp|Linear|CatmullRom|Lanczos2>`;
  `FOCUS=<building>` (`site`: fresh construction site; `temple`: small temple with mana; military buildings such as `castle`: fully garrisoned);
  `TOOLTIP=<button>` (with `FOCUS`: that button of the building panel – `pause`, `priority`, `workArea` or `demolish` – as if under the mouse, if the building has it; captures cannot hover); `FOG=1`;


## Screens before the game
- **Intro video**: `HOMESTEAD_CAPTURE_START=intro` → `video.png` (100th frame).
- **Title screen**: `HOMESTEAD_CAPTURE_START=title` → `title.png` (`title:readme`/`title:contribution`/`title:licenses` with popup, `title:load` → `load.png` (list of save games), `title:loadError` with a load error).
- **Loading screen**: `HOMESTEAD_CAPTURE_START=loading[:settlers|missionCd|amazons]` → `loading.png`.
- **Campaign**: `HOMESTEAD_CAPTURE_START=campaign` (Mission CD: `missionCd`; Amazons: `amazons`, sides `amazons`/`peoples`; `HOMESTEAD_CAPTURE_HOVER=<side>` as if under the mouse) → `campaign.png`, `campaign:<people>` → `video.png` (100th frame), `campaign:<people>:briefing` → `briefing.png`, `campaign:<people>:choose` → people selected (with progress `missions.png`), `campaign:<people>:won|lost` → mission 1 started, decided immediately, game running (banner); `…:won:next`/`…:lost:next` → leaving the statistics, `briefing.png` of the second or back to the first mission; a number at the end takes that mission (`won:next:8` → victory video, `video.png`) (with a temporary `HOMESTEAD_SAVE_DIR`!).
- **Multiplayer**: `HOMESTEAD_CAPTURE_START=multiplayer` → `multiplayer.png` (`multiplayer:create`: start screen for opening a game; `multiplayer:lobby`: sample lobby without a server); `HOMESTEAD_CAPTURE_SERVER=ws://…/ws`: connects, opens a game (player 1 human, player 2 AI, random map), starts it and plays it via the server (without the demo economy).
- **Settings**: `HOMESTEAD_CAPTURE_START=settings[:resources|economy|military|display]` → `settings.png`.
- **Installation dialog**: `HOMESTEAD_CAPTURE_DIALOG=<png>` (see `CLAUDE.md`, Installing the original).
