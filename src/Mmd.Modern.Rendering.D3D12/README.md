# 现代渲染：D3D12与可选DXR

2026-10-24新增：[普通PMX通用处理](../../docs/stages/2026-10-24-pmx-general-treatment.md)。`D3D12PmxMaterialTreatmentCatalog`提供引擎持有的四族 Body/Hair 配置；本色明度与阴影独立处理，显式Hair可用不依赖专用图的UV切线条带高光。仅实际分类行由View补齐，未分类／Face／Eye／MMD／专用图保留，显式单位处理可退出；模型局部覆盖由strict schema11便携保存。下方旧阶段参数与“未写入PMA”是该阶段范围记录，不是最新接口限制。

2026-10-24：[终末地径向Hair子路径](../../docs/stages/2026-10-24-endfield-hair.md)已验。`D3D12EndfieldHairBinding(CombinedTextureIndex, PaletteColor, Brightness)`消费显式Green与rest seed；slot2不改32字节基础布局，保signed／HDR及原coverage，高光增量独立于PMX specular。仅Endfield＋Hair；完整HA/HB／rim／MatCap未实现。

2026-10-24：[普通 PMX 材质适配](../../docs/stages/2026-10-24-pmx-material-treatment.md)已验。`D3D12CharacterMaterialStyle.PmxMaterialTreatment` 显式调整普通 Body／Hair 的 HSV 饱和度和 toon 过渡宽度；默认／单位值与 MMD 休眠，Face／Eye 及专用图绑定禁止叠加。主光／原生灯／GI 共用，原始图像、alpha、高光、描边保持；不是完整游戏 palette。参数归 renderer／宿主，未写入 PMA 或改变预设默认。GPU、Capture／Present 验证及 1440 对照见报告。

2026-10-23: [Temporal GI + SSAA combination](../../docs/stages/2026-10-23-temporal-ssaa.md) is verified. Each spatial sample owns independent preceding logical-frame history; one Submit publishes the complete set and abort preserves it. Unified driver-aligned memory preflight, soft/DXR reference comparisons and existing ViewOutput Capture/Present are verified. No core, MMD palette or reconstruction-AA rewrite. FSR/XeSS/DLSS temporal reconstruction + SSAA and transparent temporal output remain separate limitations.

画风预设入口在[Styles](Styles/D3D12StylePresetDefinition.cs)：`D3D12StylePresetStore.SaveAsNew`保存新ID、`Load().Find(id)`明确报告可用性；[Applicator](Styles/D3D12StylePresetApplicator.cs)按对象材质范围消费描边／主光参数，源帧不变。PMA只保存引用，预览／导出质量及编码预设归ViewOutput、本机保存。MMD＋原神／星铁／鸣潮／终末地四套有限普通PMX基础均已验，显式Face法线／投影强度共用；[SurfaceStyleMap](Styles/D3D12SurfaceStyleMap.cs)经Session／Presenter逐draw选择实际PSO、双history按消费变化失效，共用设备与资源。见[四族基础](../../docs/stages/2026-10-23-remaining-pmx-styles.md)。

[FaceSdfBinding](Styles/D3D12FaceSdfBinding.cs)已提供**Star／MC专用首片**：显式合并帧SceneTexture索引、Gray Red数值图／LinearWrap、最终世界头轴及族别transition／offset，独立b4/t7/s2复用上传，主光／GI／原生逐光及既有View binding均消费。无图基础输出精确保持，错族／错输入明确拒绝；验证／调用边界见[首片报告](../../docs/stages/2026-10-23-specialized-face.md)。

**前批增量：[显式Eye与终末地packed脸部](../../docs/stages/2026-10-23-eye-packed-face.md)已验。** `D3D12CharacterMaterialRole.Eye`投影强度默认`.2`可调、法线响应保持`1`，保原高光／coverage、拒绝FaceSDF；调用者显式指定眼白／虹膜材质，不按名称分类。终末地`EndfieldRgbCmM`消费RGB SDF＋`cm_M.RG`数值阴影形状，复用当前上传并按需附加t8；不改Gray，也不声称完整palette。低层FaceSDF输入仍是最终世界头轴及合并帧索引；模型本地运输属于独立ViewOutput适配。

**当前：[四画风长批次](../../docs/stages/2026-10-23-four-style-adaptation.md)已技术验证，精确统计见报告。** [原神Soft Face Shape](OriginalGenshinFaceSdfSurfaceShaders.cs)使用RGB亮度＋Face_Shadow.G/A、各图独立尺寸Cubic Repeat数据采样和原soft数值边界，再接PMX亮暗配色；不是完整Soft／Sharp palette或自定义Cast混光。四族模型本地贴图／所选头骨运输由ViewOutput适配已接，Gray／Endfield／Eye沿用，专用输入不切换风格。builtin `mmd`保原输入toon／描边／GI参数，四新族只在主动选择时启用；Character分类本身不注入现代toon。完整palette、眼球／Hair、真实匹配资产视觉验收及GUI／PMA绑定持久化仍缺；原神packed R/B非金属Hair与星铁A／鸣潮双数值图Hair子路径已技术验证，见[Hair接续](../../docs/stages/2026-10-23-star-mc-hair.md)；完整palette／Ramp／MatCap仍缺。终末地ORCO径向Hair合同已核，产品输入流／consumer待实现，见[源码合同](../../docs/stages/2026-10-23-endfield-hair-contract.md)。柴郡无匹配专用图，保普通fallback；头姿变化保守重置，不承诺持续时域积累。

[项目](Mmd.Modern.Rendering.D3D12.csproj) 只消费Core中立快照，不引用或支持MME／Effects。主链为 [D3D12FrameSession](D3D12FrameSession.cs) → OriginalFrame／Style／Outline／Visibility各pass → 内置RayGi／Denoiser／Smaa／FSR／XeSS／DLSS／AlphaResolve → [D3D12SwapchainPresenter](D3D12SwapchainPresenter.cs)。GpuRuntime／GpuCache／RetainedResource／ConstructionScope负责资源与fence；Mesh诊断入口保留；原Offscreen／EffectRenderer实验已归档供旧兼容引擎参考，不参与现代构建。

软BVH与DXR一次漫反弹、ToonPalette、空间降噪、SMAA1x、截图／计时及HLSL宿主全部在本项目。NativeSceneLights／LightGizmoPass提供局部灯；FSR／XeSS完整角色时域已接FrameSession与独立ViewOutput并实测。主测试的D3D12系列含真实GPU类，证据与具体文件导航见 [总索引](../../docs/SOURCE_INDEX.md#现代渲染)。动态VMD＋物理GI、refit和GI＋FSR／XeSS已有实测；时域GI降噪、DLSS实机及GI时域＋SSAA组合已在限定范围实测，透明超分等剩余组合仍有缺口；Vulkan及旧兼容后端后置；可选增强默认Off。

测试入口：[D3D12OriginalFrameTests](../../tests/Mmd.Modern.Tests/D3D12OriginalFrameTests.cs)、[D3D12RayGiTests](../../tests/Mmd.Modern.Tests/D3D12RayGiTests.cs)、[D3D12SmaaTests](../../tests/Mmd.Modern.Tests/D3D12SmaaTests.cs)、[D3D12NativeLightTests](../../tests/Mmd.Modern.Tests/D3D12NativeLightTests.cs)。

AA后端现支持独立SMAA四档质量和SSAA六档样本：

```csharp
var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false,
    Antialiasing: FrameAntialiasing.Smaa1X,
    SmaaQuality: SmaaQuality.Ultra, SpatialSamples: 8);
```

同一逻辑帧按中心投影偏移重画，GPU float4累积解析后再SMAA；1倍走旧路径，默认High／1。统计分别报告真实空间样本／dispatch／解析draw与SMAA draw。不是时域AA或SMAA32x，GUI设置后置。原实现21项定向GPU及995非GPU通过，边界与证据见[阶段](../../docs/stages/2026-10-20-smaa-ssaa.md)。SSAA＋GI、显存预算预检和材质因子光栅／GI消费已通过2372非GPU＋350混合定向，见[材质GI阶段](../../docs/stages/2026-10-22-material-gi.md)；超预算明确拒绝，现代完全不提供MME图入口，MME仅归旧兼容引擎。


2026-10-22：`D3D12Fsr1Pass`提供官方FP32 EASU＋RCAS空间fallback；FSR时域与XeSS角色路径通过设备深度／运动／reactive、颜色往返、描边覆盖、历史提交／取消恢复及多视图验证。RTX2080实际FSR provider3.1.5、XeSS SR2.0.2。显式超分默认NativeAA以保细节，模式整体仍默认Off。厂商运行库显式外置，未安装或不支持不静默切换。DLSS原生链接与隔离实机已验，见[DLSS原生阶段](../../docs/stages/2026-10-23-dlss-native.md)；本轮补FrameSession／ViewOutput产品接线，最新验收结论见[当前工作](../../docs/CURRENT_WORK.md)。见[角色时域阶段](../../docs/stages/2026-10-22-character-temporal.md)及[剩余执行单](../../docs/RENDERING_COMPLETION_PLAN.md)。

`D3D12TemporalUpscalingMode.Dlss`使用独立`DlssQuality`（默认NativeAa）、`LoaderPath`与`DlssSdkDirectory`，也可通过`MMD_DLSS_BRIDGE_PATH`／`MMD_DLSS_SDK_DIRECTORY`配置外置NGX桥和官方组件。输入尺寸只使用实际SDK查询；`QueryDlssCapabilities`会提交原生初始化工作，实际执行仍以成功提交帧的统计为准。线性颜色／深度／运动／reactive／jitter共用，切换、resize、quality／SDK变化失效，abort后重建。SubTexture已通过[共享时域覆盖](../../docs/stages/2026-10-23-subtexture-temporal-aa.md)开放；DLSS仍限已验透视相机，透明背景和叠加AA限制保持，不提供帧生成。

运行时可写路径：DXC缓存优先显式参数、`MMD_DXC_CACHE`、既有工作区cache；独立安装默认`LocalApplicationData/MDA/Dxc`。DLSS日志默认`LocalApplicationData/MDA/Logs/Dlss`，可用`MMD_DLSS_LOG_DIRECTORY`覆盖，不再默认写bridge目录。创建失败明确报错；[实际工作区外验收](../../docs/stages/2026-10-23-runtime-paths.md)不等于干净机器安装器验收。
