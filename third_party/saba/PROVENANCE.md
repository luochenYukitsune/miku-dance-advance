# Saba reference provenance

- Upstream: https://github.com/benikabocha/saba
- Pinned commit: `29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332` (2023-09-21, "fix mingw build faile")
- License: MIT (`LICENCE.txt`, Copyright (c) 2016-2017 benikabocha)
- Local copy date: 2026-10-17
- Local copy contents: `src/Saba` (Base/Model), `shaders` (viewer GLSL), `examples/resource_dx`, `examples/resource_vulkan`, `mmd2obj.cpp`, upstream README.

## Why this copy exists

Saba is used as a **read-only reference and differential oracle** for MMD file formats, model runtime semantics, IK/morph ordering, Bullet wiring and multi-backend organisation. It is **not** compiled into `Mmd.Modern.*` and is **not** shipped as part of the product.

Deliberately excluded: `viewer/.../resource/mmd/toon01..10.bmp` (MMD data assets), fonts, submodules (glfw/glslang), build trees and binaries. Toon textures are read from the user's own MMD installation instead.

## Usage rules

1. Porting code to C# is allowed under MIT, but every ported file must record the upstream commit and retain the copyright notice.
2. Saba is **not** an authority for MMD-exact behavior. Where saba differs from retained REA evidence or original traces, the original evidence wins.
3. Saba shaders are structural references only. MMD compatibility shading must follow the recovered MMD host formulas and FX resources.
4. Do not copy assets whose rights are not established by saba's MIT license.
5. `THIRD_PARTY_NOTICES.md` in the project root must be updated before any redistribution.

## Upstream modules vs. local use

| Upstream area | Files | Local use |
|---|---|---|
| PMX parse/model | `PMXFile.*`, `PMXModel.*` | Port + cross-check C# `Mmd.Modern.Formats.Pmx` |
| PMD parse/model | `PMDFile.*`, `PMDModel.*` | Port + cross-check C# `Mmd.Modern.Formats.Pmd` |
| VMD/VPD | `VMDFile.*`, `VMDAnimation.*`, `VPDFile.*` | Port + cross-check animation/pose import |
| IK | `MMDIkSolver.*` | Cross-check CCD ordering/limits |
| Physics | `MMDPhysics.*` | Cross-check rigid/joint setup; BulletCompat path |
| Base | `Base/*` | Reference only (C# BCL replaces it) |
| Shaders | `shaders/*`, `examples/*` | Reference only; rewrite in HLSL |
