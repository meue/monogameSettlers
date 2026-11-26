# Third-Party Notices

monoSettlers itself is licensed under the [MIT License](LICENSE.md). This file lists the code and data taken from
other projects and the libraries included in the release packages, with their licenses.

## JSettlers / settlers-remake

https://github.com/paulwedeck/settlers-remake

Used as a reference for the original file formats (GFX images, maps, sounds) and terrain rendering.
The C# code was written independently; the following data tables and rules were ported:

- Torso/shadow alignment rules (`src/Homestead.Assets/Gfx/GfxFile.cs`, from `AdvancedDatFileReader` and the
  `ShadowMapping*` classes; the building shadows of the Egyptians, Asians and Amazons are paired by us)
- Landscape type mapping and image numbers (`src/Homestead.Client/Terrain/TerrainKind.cs`,
  from `ELandscapeType` and `OriginalLandscape`)
- Building definitions for all four civilizations (`data/buildings/*.json`: footprints, doors,
  piles, image references), converted from `jsettlers.common/.../buildings/*.xml` by
  `tools/data-import/convert_jsettlers_buildings.py`
- Original map codes for buildings, piles and settlers (`OriginalScenario.cs`, `Material.cs`), pile and
  carrier graphics per material (`EconomyRenderer.cs`, `SettlerRenderer.cs`), from `EOriginalMap*Type`,
  `EMaterialType` and the movables tables; worker walk/work animations of all trades (`SettlerRenderer.cs`,
  from `movables*.txt`); grain image sequence (`MapObjectCatalog.cs`, from `MapObjectDrawer`); flag and work area mark images
  and the work circle metric (`EconomyRenderer.cs`, `PathFinder.InCircle`, from `MapObjectDrawer`, `MainGrid`, `MapCircle`)
- Soldier places of military buildings (`occupiers` in `data/buildings/*.json`), soldier and border
  post images (`SettlerRenderer.cs`, `EconomyRenderer.cs`), original settler type codes (`OriginalScenario.cs`,
  from `EOriginalMapSettlersType`), tools per trade (`data/production.json`, from `EMovableType`)
- Start equipment per start level for multiplayer maps (`data/start.json`, from `EMapStartResources`)
- Settler animations of all four peoples (`data/settlers.json`: type, action, carried material and
  direction to file, sequence and frames, with JSettlers' priority rules in `SettlerAnimations.cs`), converted
  from `movables*.txt` by `tools/data-import/convert_jsettlers_movables.py`
- Geologist sign images per resource and their lifetime (`EconomyRenderer.cs`, `data/production.json`, from
  `MapObjectDrawer`, `RessourceSignMapObject`, `GeologistMovable`)
- Dust cloud of a building coming down, its duration and the share of construction material left behind
  (`EconomyRenderer.cs`, `WorldEconomy.cs`, `data/production.json`, from `MapObjectDrawer`, `Building`, `Constants`);
  the striking carrier's animation order (`SettlerRenderer.cs`, from `BearerMovable`'s homeless behaviour)
- Build menu categories and their buildings (`data/ui.json`, from `EBuildingsCategory`)
- Layout of the sound file and the numbers of the sounds for strikes, shots and deaths (`src/Homestead.Assets/Sound/SoundFile.cs`,
  `docs/formate/snd.md`, `data/sound.json`, from `SoundManager` and `MapObjectDrawer`)
- Original map object names (`docs/formate/map.md`, from `EOriginalMapObjectType`)
- Terrain texture atlas layout, transition table and triangle texture orientation
  (`src/Homestead.Client/Terrain/TerrainAtlas.cs`, `TerrainMesh.cs`, from `Background` and `ETextureOrientation`)

```
The MIT License (MIT)

Copyright (c) 2015

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Libraries in the release packages

The release packages (`tar.gz`/`zip`) are self-contained and include these libraries, pulled in as NuGet packages.
None of them is part of this repository's source code.

### MonoGame

https://github.com/MonoGame/MonoGame — `MonoGame.Framework.DesktopGL` 3.8.6, Microsoft Public License (MS-PL),
Copyright (c) the MonoGame Team. License text: https://github.com/MonoGame/MonoGame/blob/develop/LICENSE.txt

### SDL2

https://www.libsdl.org — native library `SDL2` from `MonoGame.Library.SDL` 2.32.10, zlib License.

```
Copyright (C) 1997-2025 Sam Lantinga <slouken@libsdl.org>

This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

### OpenAL Soft

https://github.com/kcat/openal-soft — native library `libopenal.so` / `openal.dll` from `MonoGame.Library.OpenAL` 1.24.3
(built by https://github.com/MonoGame/MonoGame.Library.OpenAL), GNU Library General Public License, version 2 or later
(LGPL-2.0-or-later). The library is unmodified and loaded dynamically; you may replace it with your own build.
Its complete source code is available at https://github.com/kcat/openal-soft/tree/1.24.3, the license text at
https://github.com/kcat/openal-soft/blob/1.24.3/COPYING.

Portions of OpenAL Soft are licensed under the BSD 3-Clause License:

```
Copyright (c) 2015, Archontis Politis
Copyright (c) 2019, Anis A. Hireche
Copyright (c) 2019, Christopher Robinson
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Spherical-Harmonic-Transform nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### FontStashSharp

https://github.com/FontStashSharp/FontStashSharp — `FontStashSharp.MonoGame` 1.6.1 with `FontStashSharp.Base` and
`FontStashSharp.Rasterizers.StbTrueTypeSharp` 1.2.9, zlib License, Copyright (c) the FontStashSharp Team.
Renders the menu text at runtime from a TrueType font installed on the system (no font is shipped).

### StbImageSharp, StbTrueTypeSharp

https://github.com/StbSharp — `StbImageSharp` 2.30.16, `StbTrueTypeSharp` 1.26.13 (dependencies of MonoGame and
FontStashSharp), public domain (Unlicense), alternatively MIT.

### Anthropic C# SDK

https://github.com/anthropics/anthropic-sdk-csharp — `Anthropic` 13.3.0 with `Microsoft.Extensions.AI.Abstractions` 10.5.1 and
`System.Net.ServerSentEvents` 10.0.1, MIT License, Copyright (c) Anthropic, PBC (the .NET packages: Copyright (c) .NET Foundation and
Contributors). Used only for the optional chat with the computer players (with the player's own API key).

### NVorbis

https://github.com/NVorbis/NVorbis — `NVorbis` 0.10.4 (dependency of MonoGame), MIT License,
Copyright (c) 2020 Andrew Ward.

### Cyotek.Drawing.BitmapFont

https://github.com/cyotek/Cyotek.Drawing.BitmapFont — 2.0.4 (dependency of FontStashSharp), MIT License,
Copyright (c) 2012-2022 Cyotek Ltd.

### ONNX Runtime

https://onnxruntime.ai — `Microsoft.ML.OnnxRuntime` 1.31.1 (managed library and the native `onnxruntime`
libraries for all supported platforms except osx-x64; CPU execution provider everywhere, CoreML on macOS), MIT License,
Copyright (c) Microsoft Corporation. Runs the optional neural sprite upscaling at runtime (see
"Embedded model"); without a native library (osx-x64) the feature switches itself off. The official binaries
include Microsoft's 1DS telemetry by default; the game sets `ORT_DISABLE_TELEMETRY=1` (the opt-out documented
in the package's `Privacy.md`) before ONNX Runtime initializes, so no events, uploader or device identifier
are ever created. License text: https://github.com/microsoft/onnxruntime/blob/main/LICENSE

The Anthropic C# SDK, NVorbis, Cyotek.Drawing.BitmapFont and ONNX Runtime are licensed under the MIT License:

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Embedded model

### Real-ESRGAN realesr-animevideov3 (x4)

`src/Homestead.Client/Neural/realesr-animevideov3-x4.onnx` — embedded unmodified, the network of the optional
neural x2 sprite upscaling (the x4 output is halved to x2 in code). Upstream: the official checkpoint
`realesr-animevideov3.pth` of https://github.com/xinntao/Real-ESRGAN, BSD-3-Clause, Copyright (c) 2021 Xintao Wang.
Conversion (ONNX, opset 17, parity vs PyTorch 125.4 dB per its model card):
https://huggingface.co/skillsafe-ai/realesr-animevideov3 at revision
`185e9142d439d17e3fb99395600fb7d08af09de5`. SHA-256
`78baa685a1a92cac6e14ab2af2a8ad0ef56fa48629563b1414e3a7acc54d86e2`, 2492908 bytes. Not an original game asset:
the upscaling happens in memory at runtime, no shipped asset is derived from the game's files.

```
BSD 3-Clause License

Copyright (c) 2021, Xintao Wang
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
