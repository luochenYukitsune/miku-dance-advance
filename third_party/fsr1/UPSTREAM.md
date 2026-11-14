# AMD FidelityFX Super Resolution 1

Source: https://github.com/GPUOpen-Effects/FidelityFX-FSR

Pinned commit: `a21ffb8f6c13233ba336352bdff293894c706575`.

`ffx_a.h`, `ffx_fsr1.h` and `LICENSE.txt` are unchanged upstream bytes (the latter is named `license.txt` upstream). MIT license. The native D3D12 wrapper uses the official non-packed FP32 EASU and RCAS functions; the wrapper supplies clamp sampling, constants and fullscreen raster entry points. RCAS loads clamp coordinates explicitly because Texture.Load bypasses sampler address modes. The existing display color values are preserved without an added gamma conversion. Transparent background reconstruction and arbitrary MME graph combinations are rejected before recording.
