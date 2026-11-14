# FFmpeg video output

`FfmpegFrameSink` accepts ordered frame leases from the neutral coordinator and pipes packed RGBA into explicitly selected external FFmpeg/ffprobe executables. It composites straight alpha against a chosen background in linear light, then converts sRGB-compatible display input to tagged limited-range BT.709 video. No audio track is generated.

The product default is **H.265 software / constant quality / balanced speed**. H.264, AV1, uncompressed rawvideo and explicit NVENC selections remain available; no encoder silently falls back. Hardware support is demonstrated by encode + probe + full decode, not by appearing in an encoder list.

Typed options expose quality or single-pass target bitrate, codec-mapped speed presets, maximum bitrate and buffer, maximum keyframe interval, thread count, and a limited validated profile/level selection. Target bitrate does not promise an exact file size; scene changes may create additional keyframes. Frame dimensions, rational FPS, source interval and physics pre-roll come from `OfflineFramePlan`.

`FfmpegEncodingPreset.FromOptions` saves the portable encoding fields in ViewOutput's `ffmpeg` schema-1 envelope. `ToOptions(envelope, chosenOutputPath, plan)` strictly restores them and uses the existing encoder validation before execution. The preset omits output/tool paths, runtime timeout and computed metadata; the caller supplies its destination, and the existing default timeout applies unless explicitly changed afterward. Unknown providers/versions and invalid combinations fail without fallback. Presets live in the local ViewOutput library, not PMA.

Rawvideo uses AVI/BGR24; compressed output uses MP4/YUV420P and therefore requires even dimensions. Write to a same-directory unique temporary file, flush the encoder, verify codec/dimensions/FPS/count/color metadata and fully decode before publishing. Cancellation or failure preserves an existing destination. Cleanup errors must not hide the original failure.

`Timeout` defaults to two minutes **per write and per complete operation**, not per whole export. Complete includes final encoding, probing and decoding, so long outputs may require a larger value. Output is 8-bit; HDR, audio muxing, two-pass encoding and GPU zero-copy encoding remain future work.

Set `MMD_FFMPEG_PATH` and `MMD_FFPROBE_PATH` for real codec tests. Actual tool identity, source, hashes, build configuration, license and executed matrix are retained in `artifacts/stage-20261022-view-output/ffmpeg`. See [third-party notices](../../THIRD_PARTY_NOTICES.md) before redistributing a particular tool build.
