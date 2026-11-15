# Bundled FFmpeg

Windows packages contain FFmpeg and ffprobe. Normal installations need no
separate encoder setup. The source repository stores acquisition metadata.

`manifest.json` pins the binary archive and both executable SHA256 values.
`scripts/fetch-ffmpeg.ps1` verifies this exact pair for automated builds.
The MDA Release mirror is tried first, with the pinned upstream build as a
fallback. Both URLs must yield the same archive hash.
Software H.264, HEVC, AOM AV1 and SVT-AV1 encoding with AAC audio were checked.
Hardware encoder availability depends on the device.

This build is GPL-3.0-or-later, including GPL dependencies; it is not LGPL-only.
FFmpeg source, dependency sources, upstream recipes, patches and build records
accompany the installer under Assets in
[MDA Releases](https://github.com/luochenYukitsune/miku-dance-advance/releases).
Download both dependency-sources ZIPs and the build-materials ZIP. Instructions
and per-source hashes are included in the latter. Dependencies retain their
own licenses; MDA's license does not replace them.

Upstream: <https://github.com/FFmpeg-Builds/FFmpeg-Builds>.
Minimum supported Windows version for this FFmpeg build: Windows 10 22H2.
