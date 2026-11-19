# 现代渲染：D3D12 与可选 DXR

本项目提供 D3D12 现代渲染引擎与可选 DXR 全局光照，只消费 `Mmd.Modern.Core` 中立快照，不引用或支持 MME / Effects，也不内置任何具体游戏的画风着色器实现。

- 默认内置风格为 `"mmd"`：完整保留原版 PMX 配色、球面贴图、自阴影、Native 光源、ToonPalette GI 与描边行为，根签名仅使用基线寄存器（`b0..b3`、`b0 space1`、`t0..t6`、`s0..s1`）。
- 外部画风插件通过 [Styles/D3D12PluginStyleContracts.cs](Styles/D3D12PluginStyleContracts.cs) 与 [Styles/D3D12StylePresetDefinition.cs](Styles/D3D12StylePresetDefinition.cs) 注册（`D3D12StylePresetCatalog.WithPlugins`），并通过 [Styles/D3D12SurfaceStyleMap.cs](Styles/D3D12SurfaceStyleMap.cs) 逐材质绑定通用着色器模块与绘制数据（`D3D12PluginDrawData`）。
- 通用扩展绑定统一落在 `space2`（`b0 space2` 常量缓冲、`t0..t11 space2` 12 槽贴图桌、`t12..t15 space2` 4 槽结构化缓冲、`s0..s3 space2` 静态采样器），由 [D3D12StyleShaderAssembler.cs](D3D12StyleShaderAssembler.cs) 按需组合结构化 HLSL 钩子（`LightResponse`、`PaletteFactor`、`Specular`、`AlbedoTransform`、`PrimarySurfacePost`、`FinalEntryPre`、`FinalEntryPost`），并按需生成 MikkTSpace 切线角点与径向种子流（[D3D12TangentCornerGeometry.cs](D3D12TangentCornerGeometry.cs)）。
- 普通 PMX 材质的通用风格化明暗（[Styles/D3D12StylizedShading.cs](Styles/D3D12StylizedShading.cs)）与材质调整（[Styles/D3D12PmxMaterialTreatment.cs](Styles/D3D12PmxMaterialTreatment.cs)）及自动描边颜色缩放（[D3D12CharacterOutlinePass.cs](D3D12CharacterOutlinePass.cs)）均为引擎侧与游戏无关的通用能力。
- 主链为 [D3D12FrameSession](D3D12FrameSession.cs) → OriginalFrame / PluginSurface / Outline / Visibility 各 pass → 内置 RayGi / Denoiser / Smaa / FSR / XeSS / DLSS / AlphaResolve → [D3D12SwapchainPresenter](D3D12SwapchainPresenter.cs)。
- 运行时可写路径：DXC / FXC / PSO 缓存优先显式参数、`MMD_DXC_CACHE`、既有工作区 cache；独立安装默认 `LocalApplicationData/MDA/Dxc`。DLSS 日志默认 `LocalApplicationData/MDA/Logs/Dlss`，可用 `MMD_DLSS_LOG_DIRECTORY` 覆盖。
