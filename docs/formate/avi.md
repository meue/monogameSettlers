# Videos (AVI)

The campaigns' cutscenes are stored as `.DAT` in `S3/ADD/001` (The Settlers III) and `S3QOTA/ADD/1` (Amazons).
They are ordinary RIFF AVI files: 320 × 240, 25 fps, video **Intel Indeo 5** (`IV50`), audio uncompressed PCM
(16-bit stereo, 22,050 Hz). Video and audio are interleaved in `LIST rec ` blocks (video `00dc`, audio `01wb`).

Reading: `Homestead.Assets.Video.AviFile` (header `avih`: µs per frame, frame count, width, height; `strf` of the audio stream:
`WAVEFORMATEX`; `ReadAudio` concatenates all `NNwb` chunks). The client decodes the video via an
installed **ffmpeg** (separate process, `VideoPlayer`); without ffmpeg the video is skipped.

## The Settlers III (`S3/ADD/001`)

Name `AA_VV_NN`: `01_00_00` game intro (260 s); `02_VV_NN` campaign, people `VV` (01 Asians, 02 Egyptians, 03 Romans),
`NN` 01 intro, 02/03 cutscenes, 04 finale (~90 s). `03_01_01–03` are the same size as `02_01_01/02/04`.
