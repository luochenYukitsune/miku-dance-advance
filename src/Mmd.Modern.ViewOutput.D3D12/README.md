# D3D12 View Adapter (`Mmd.Modern.ViewOutput.D3D12`)

`Mmd.Modern.ViewOutput.D3D12` bridges the GUI/host-neutral application layer and `Mmd.Modern.Rendering.D3D12` while keeping GPU handles, Vortice types, and HLSL shader details isolated inside the D3D12 backend.

## Plugin-Driven Style Architecture

- [D3D12StylePluginAdapter.cs](D3D12StylePluginAdapter.cs): `D3D12ViewStyleCatalog` collects loaded `IRenderingStyleDefinition` plugins from `Mmd.Modern.Plugins.Contracts`, translates style defaults (`DefaultOutline`, `DefaultToonResponse`, `DefaultStylizedShading`) and `StyleShaderModule` entries (with deterministic SHA-256 `ContentHash` over HLSL + defines) into `D3D12PluginStyleRegistration` items, and merges them into `D3D12StylePresetCatalog.WithPlugins(...)`.
- [D3D12ViewPluginMaterialBindings.cs](D3D12ViewPluginMaterialBindings.cs): Generic `D3D12ViewPluginMaterialBinding(ObjectId, LocalMaterialIndex, StyleMaterialBindingData)` replaces hardcoded style-specific bindings. Multiple `StyleMaterialBindingData` records may target one source-local material, stay detached from caller-mutated dictionaries, emit minimal `SceneObjectRenderRequest` requests (`GetSceneTextureRequests` / `GetBoneTransformRequest` without guessing bone names or loading unrequested textures), and validate against the active `IRenderingStyleDefinition` before GPU submission.
- [D3D12ViewCharacterMaterials.cs](D3D12ViewCharacterMaterials.cs): Holds `D3D12ViewCharacterMaterial` overrides (`Role`, `ExplicitRole`, face/eye shadow strengths, `ToonResponse`, `PmxTreatment`, `StylizedShading`, `PreserveDirectLightPalette`) per object and source-local material index.
- [D3D12ViewRenderingStyles.cs](D3D12ViewRenderingStyles.cs): Preflights `SceneRenderingStyles`, classifies materials via `IRenderingStyleDefinition.ClassifyMaterial`, resolves scene texture remaps and evaluated bone transforms from `SceneSnapshot` + `SceneObjectRenderRequest`, calls `IRenderingStyleDefinition.BuildMaterialDrawData` to produce `D3D12PluginDrawData`, and builds the `D3D12SurfaceStyleMap` frame override map.
- [D3D12ViewShaderPrewarm.cs](D3D12ViewShaderPrewarm.cs): Derives `D3D12ShaderStyleWarmupVariant` compilation targets dynamically from the active styles and shader modules registered in `D3D12ViewStyleCatalog` and `D3D12StylePresetCatalog`.
- [D3D12PreviewAdaptiveSampling.cs](D3D12PreviewAdaptiveSampling.cs): Tracks structural material/plugin draw layout keys across history samples so camera and skeletal motion do not reset temporal GI accumulation.

## Renderers, Material Bindings & Persistence

- [D3D12ViewRenderer.cs](D3D12ViewRenderer.cs) & [D3D12RealtimeViewRenderer.cs](D3D12RealtimeViewRenderer.cs): Offline capture/export and realtime HWND presentation adapters. Both accept optional `D3D12ViewStyleCatalog`, `D3D12ViewCharacterMaterial` overrides, `D3D12ViewPluginMaterialBinding` entries, `styleGlobalParameters`, and explicit `boneIndicesByObject` maps, keeping MMD (`mmd`) as the lossless default baseline.
- [MaterialBindings/D3D12MaterialBindingProfile.cs](MaterialBindings/D3D12MaterialBindingProfile.cs) & [MaterialBindings/D3D12MaterialBindingProfileJson.cs](MaterialBindings/D3D12MaterialBindingProfileJson.cs): Portable, bounded Schema v14 model material mapping codec (`pluginBindings` + source-scene `textureGuards`), with lossless read-and-upgrade support for legacy Schema v1..v13 profiles.
- [MaterialBindings/D3D12MaterialBindingProfileResolver.cs](MaterialBindings/D3D12MaterialBindingProfileResolver.cs), [MaterialBindings/D3D12MaterialBindingPma.cs](MaterialBindings/D3D12MaterialBindingPma.cs), & [MaterialBindings/D3D12GeneratedStyleMaterialPreparation.cs](MaterialBindings/D3D12GeneratedStyleMaterialPreparation.cs): Validates source layout fingerprints and image SHA-256 guards even when a plugin style is dormant or unloaded, resolves active `StyleMaterialBindingData` payloads cleanly, and preserves `.pma` persistence and generated texture provenance.

## Image Quality, AA, Upscaling & Temporal State

- `D3D12ViewQuality`: Maps `ViewQualityOptions` (SMAA presets, spatial sample counts 1..32, FSR1 spatial upscaling + RCAS sharpness, temporal FSR / XeSS / DLSS upscaling) independently of the source scene.
- `D3D12ViewTemporalClock`: Tracks independent capture/preview temporal history, resetting only on time discontinuity, camera cuts, seeks, or explicit resets.
