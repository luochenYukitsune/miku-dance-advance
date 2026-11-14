# ViewOutput executable verification

Run the built executable with a new output directory, optionally followed by absolute FFmpeg and ffprobe executable paths. Append `--pmx <absolute-model.pmx>` for an additional read-only real-model 1920x1080 PNG and, when tools are supplied, a short HEVC constant-quality 23/balanced clip. The real-model branch passes a static-pose VMD with keys at 0 and 30 through the existing actor session and offline runner; IK and physics are explicitly off. This harness has no product GUI.

The default self-authored moving PMX triangle exercises existing core session sampling, a direct-Capture pixel comparison, PNG and rational-time sequence output, an empty scene, transparent-background rejection, and a hidden native swapchain with present/resize and zero routine readbacks. When tools are supplied, raw AVI and software H.264/HEVC/AV1 clips are encoded, probed and decoded through the normal output coordinator. JSON records the actual GPU adapter and WARP state.

Add `--fsr1` to explicitly exercise the existing FSR1 spatial fallback through the neutral view quality request. This mode requires real hardware, verifies separate input/output sizes and exact direct-backend capture pixels, and checks resize with zero presentation readbacks. With `--pmx`, it also writes `real-pmx-native-smaa.png` and `real-pmx-fsr1-quality.png`: both use SMAA High and one spatial sample, with native 1920×1080 versus FSR1 Quality 1280×720 reconstructed to 1920×1080. JSON retains their settings, hashes and actual GPU statistics. This fallback does not satisfy the separate latest FSR SDK or DLSS integration work.

D3D12 capture preserves the established opaque display bytes. Its legacy blend target does not provide validated straight-alpha output over a transparent background, so those capture requests are explicitly rejected. PNG sinks independently preserve supplied straight RGBA alpha, row stride and sRGB metadata. No full-pipeline gamma conversion is introduced.

The backend reports its existing 4096-pixel dimension limit. Animated camera/light tracks use the existing neutral adapters. Animated self-shadow modes/distances, parented cameras without resolved parent transforms, additional UV channels and nondefault texture/sphere/toon morph factors are rejected explicitly. Geometry and material morphs continue through existing core evaluation. Video is silent; HDR, high bit depths and GPU encoder interoperability remain outside this stage.

PNG encoding reuses the runtime zlib implementation ([dotnet/runtime, MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)) with the [W3C PNG](https://www.w3.org/TR/png-3/) RGBA8 chunk/CRC layout. IDAT buffering is 64KiB; neither the whole compressed image nor a sequence manifest is retained in memory. The real-model loader links the existing Smoke `SceneTextureLoader` source and the existing SkiaSharp 2.88.9 dependency; the product ViewOutput and D3D12 adapter assemblies do not depend on App, Avalonia or SkiaSharp.


## Real-character temporal FSR evidence

Run `ViewOutput.Stage <new-output-directory> --temporal-fsr --pmx <absolute-read-only-model.pmx>` with `MMD_FSR_LOADER_PATH` pointing to the installed official `amd_fidelityfx_loader_dx12.dll`. This branch runs only the temporal PMX harness; omit FFmpeg paths and `--fsr1`.

The harness loads the existing PMX texture/toon/material/outline adapters, frames the full model using a perspective camera, and translates its immutable world transform over eight frames in one retained D3D12 session. It captures 1920×1080 native and FSR Quality endpoints (`real-pmx-native-{00,07}.png`, `real-pmx-fsr-temporal-{00,07}.png`); the middle six temporal frames stay on the GPU without application readback. It validates actual hardware/provider, 1280×720 input, 1920×1080 output, first-frame-only reset, continuous submitted history, visible model geometry, changed endpoints and exactly two temporal readbacks. `verification.json` records provider, per-frame jitter/history/resource statistics, image hashes/differences, texture diagnostics and before/after source-asset SHA checks. No model or texture is modified or copied into source control.

This stage exercises rigid world motion of real PMX rest geometry, not VMD/bone/physics animation. It preserves reported common-toon fallbacks and does not establish original-MMD pixel equivalence. GI/post effects are off. The product's current unsupported temporal combinations remain explicitly rejected.

## Real-character temporal XeSS evidence

Run `ViewOutput.Stage <new-output-directory> --temporal-xess --pmx <absolute-read-only-model.pmx>` with `MMD_XESS_RUNTIME_PATH` pointing to the installed official `libxess.dll`. This reuses the same eight-frame perspective 1080p world-motion harness, native endpoints, readback guards and read-only source SHA checks as the FSR path. It writes `real-pmx-xess-temporal-{00,07}.png` and `real-pmx-native-{00,07}.png`. XeSS render dimensions come from its actual initialized SDK query and are checked against submitted frame diagnostics; no FSR ratios are assumed, and no AMD provider ID is invented.

The pinned Intel SDK bundle release is **3.0.2**; that bundle version is distinct from the SR runtime version returned by `xessGetVersion` (observed **2.0.2** on this RTX2080). Per-frame JSON retains the actual runtime/driver/init/input-resolution query and selected provider. This switch is exclusive with `--fsr1` and `--temporal-fsr`; it is an evidence harness, not a GUI or full original-MMD equivalence test.
