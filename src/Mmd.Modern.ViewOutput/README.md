# View and output

Neutral C# orchestration, independent of GUI and concrete GPU bindings. `PreviewSession` owns one scene and realtime renderer; `ExportCoordinator` obtains a separate scene and capture renderer from factories. Never return the live preview's session from an export factory.

- `OfflineFramePlan` / `OfflineFrameRunner` remain in Application and retain rational FPS, requested versus sampled source times, and the existing 120 Hz physics clock.
- `FrameBufferLease` carries top-down RGBA8 display/sRGB-compatible values, row stride and straight alpha. The coordinator owns/disposes it after the awaited sink write; a sink only borrows it. Capture/write are serial, so the coordinator retains at most one CPU frame.
- `IFrameSink` separates preparation, ordered writes, validated publication and abort. PNG stills require one planned frame. PNG sequences publish a new directory with exact rational timestamps in a manifest; existing directories are not overwritten.
- The neutral PNG sink preserves supplied straight alpha. D3D12 has the validated FP16 premultiplied/straight resolve path; transparent temporal-upscaling combinations retain explicit restrictions rather than silently compositing.
- Preview present/resize/pause/close are serialized. GPU presentation stays in an `IRealtimeViewRenderer` adapter and never calls capture for ordinary display.
- `CreateProjectAsync` / `ForProject` carry `SceneProjectSession`'s stable object/style/material-range metadata alongside its existing evaluated frame. `CreatePmaAsync` / `ForPma` additionally retain extracted resources until the renderer closes. These factories own their project; never reuse the live preview owner for export.
- A renderer implementing `IViewRenderingStylePreflight` validates effective selections before preview advances or export begins writing/seeking. Backend-specific replacement inspection and explicit confirmation stay in the adapter; the neutral module neither opens a preset library nor chooses a fallback.

Adapters: [D3D12](../Mmd.Modern.ViewOutput.D3D12/README.md), [FFmpeg](../Mmd.Modern.ViewOutput.FFmpeg/README.md). Scope and evidence: [plan](../../docs/VIEW_OUTPUT_PLAN.md), [current work](../../docs/CURRENT_WORK.md). Formal GUI, audio, HDR and other renderer adapters remain separate work.

`Presets/ViewOutputPresetDefinition` stores independent preview and final-output configuration, with rational FPS and existing GI/AA/upscaling options. `ViewOutputPresetJson` is a strict bounded portable JSON codec; `ViewOutputPresetStore` saves new identities to an explicit local directory and returns detached catalogs with per-file statuses. These presets stay local and are not embedded in PMA. Copy the preset files separately to migrate them.

`definition.Preview` feeds the existing preview factory; `definition.Export.CreateRequest(start, end, preRoll)` feeds the existing export coordinator. Encoding data has an explicit provider/version and is interpreted by that adapter; neutral ViewOutput does not reference FFmpeg or a GPU assembly. Renderer-local style definitions remain in Rendering.D3D12 and PMA stores stable style references. Task destinations/intervals, SDK/tool paths and GPU histories are not portable preset data.
