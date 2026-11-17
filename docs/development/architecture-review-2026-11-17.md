# 六模块架构审查（2026-11-17）

只读审查，基于合并提交 `6a04868` 之后的 `integration/lighting-source-20261117`。没有改代码。证据给出的是文件和行号，行号以审查时的版本为准。

## 结论

项目之间的依赖方向基本守住了：核心不依赖界面、GPU 或引擎，两个引擎互不引用，业务模块不依赖插件加载器，视图模块不自己推进动画或物理时钟。

问题集中在三处：
1. **GUI（`Mmd.Modern.App`）承担了太多。** 它直接引用两个引擎的具体类型，里面还有大量兼容模式的业务逻辑。
2. **“中立”的地方混进了引擎专属内容。** 核心里有 D3D12 的 GPU 结构体和功能检测；视图的中立接口里有 FSR、XeSS、DLSS 这类厂商选项。
3. **遗留项目还在。** Veldrid 旧渲染器跟着安装包一起发布，Effects 和 Scheduling 两个项目没有被产品使用。

## 各模块状态

| 模块 | 状态 | 说明 |
|---|---|---|
| MMD 核心 | 依赖干净，内容有越界 | 不引用 GUI、GPU 或引擎；但 Core 里有 D3D12 的 GPU 结构体和功能规则（见 A2） |
| 现代引擎 | 干净 | `Rendering.D3D12` 只依赖 Core 和 Vortice |
| 兼容引擎 | 依赖干净，归属有疑问 | 不引用任何现代类型，会主动拒绝现代灯光、GI 和 AA；但 EMM 格式和 PMC 编解码放在了引擎和视图项目里（见 B3） |
| 视图输出 | 方向对，接口有泄漏 | 两个引擎都实现了中立接口；但中立选项里带着厂商专属功能（见 A3） |
| GUI | **主要问题所在** | 见 A1、B1、B2 |
| 插件 | 干净 | 只有 App 引用加载器；契约项目没有任何依赖；文档明确说明它不是沙箱 |

## A. 应当整改（违反分层约定）

**A1. GUI 直接使用现代引擎的类型**（App 中有 20 个文件，其中 10 个是真正的违规）
- 材质绑定配置 `D3D12MaterialBindingProfile` 被直接当作工程文档模型使用：
  - `EditorProjectWorkspace.cs`（工作区数据、撤销快照、校验）
  - `EditorProjectPersistence.cs:35`（两种工程格式共用的保存接口里也带着它）
  - `EditorModelPropertiesViewModel.cs`（直接用 `D3D12CharacterMaterialRole`、`D3D12FaceSdfEncoding` 等引擎枚举）
  - `EditorMaterialInspectorViewModel.cs`、`EditorModelPropertiesWindow.axaml.cs`
- 功能探测在界面里直接创建 D3D12 渲染器：
  - `EditorPreviewSettingsViewModel.cs:40-173`（GI、FSR、XeSS、DLSS 探测）
  - `EditorOutputCapabilities.cs:17`
  - `EditorWindow.Authoring.cs:64`
- 界面代码直接匹配引擎的异常类型 `D3D12GiMemoryBudgetExceededException`：`EditorShellActivity.cs:19`、`EditorPreviewFeedback.cs:15`。
- `ExportGpuResourceProvider.cs` 直接调用 Vortice 的 DXGI 接口查询显存。
- 整改：
  - 在视图或应用层定义中立的“材质绑定配置”，D3D12 的对应关系只放在 `ViewOutput.D3D12` 里。
  - 给 `IEditorRenderEngine` 加功能探测和配置校验方法，返回中立的能力描述。
  - 新增中立的“显存预算超出”错误，以及显存采样接口。

**A2. 核心里有 D3D12 的专属内容**
- `Core/RayGiScene.cs:8-34`、`Core/RayGiBvh.cs` 里有 `RayGi*Gpu` GPU 内存布局结构体，只有 D3D12 和测试在用。
- `Core/LightingPolicy.cs` 写着 D3D12 的功能等级、着色器模型版本、WARP 拒绝等规则。
- 整改：GPU 结构体和 BVH 打包移到 `Rendering.D3D12`；核心只保留中立的 GI 选项，引擎用中立标志报告自己是否可用。

**A3. 视图中立接口泄漏了厂商功能**
- `ViewOutput/ViewContracts.cs:7-10` 的 `ViewQualityOptions` 带着 `RayGiOptions`、`SmaaQuality`，以及 FSR、XeSS、DLSS 枚举。
- `ViewRendererCapabilities` 没有功能标志，兼容引擎只能在运行时抛异常拒绝这些选项（`D3D9SurfacePreflight.cs:52-55`）。
- 整改：在能力描述里加中立的功能标志（GI、AA、超分、时间重建），按引擎能力校验；厂商专属选项交给 `ViewOutput.D3D12` 自己的类型化选项。

**A4. 开发入口固定使用冻结的 Bepu 物理**
- `FrameViewportAnimation.cs:72,110`、`FrameViewportEngineDemo.cs:24`、`FrameViewportProject.cs:59` 直接创建 Bepu，从 `--frame-viewer` 和 Demo 进入。
- 编辑器主流程是正确的，统一入口默认用 Bullet。
- 整改：这些位置也走 `ProjectPhysicsRuntime`，默认 Bullet。

## B. 兼容模式的业务逻辑放错了位置

**B1. App 里有约 3650 行兼容模式业务代码**，下面这些都不是界面代码：
- **PMC 打包与依赖核对**：`EditorClassicDocumentState*.cs`（309 行）。
- **PMC 保存与读取**：`EditorMmeProjectPersistence.cs`、`EditorClassicProjectPersistence.cs`。
- **PMM 导入时建立 EMM 和配件计划**：在 `EditorProjectInterchange.cs`（480 行）里。
- **EMM 和 Toon 的可撤销编辑事务**：`EditorProjectWorkspace.Classic*.cs`。
- **Toon 编解码与图片冻结**：`EditorClassicToonPackage*.cs`、`EditorClassicToonResources.cs`。
- **原生宿主的组装、DLL 生命周期和运行库查找**：`EditorClassicRenderEngine*.cs`、`EditorCompatibilityRenderEngine.cs`、`EditorClassicRuntimeInstallation.cs` 等。

可以留在 App 的只有对话框、菜单、选择器和引擎选择。

**B2. App 的项目文件同时引用两个引擎**（`Rendering.D3D12` 和 `ViewOutput.D3D9.Compatibility`）。按约定，只有启动装配处可以引用具体实现。

**B3. PMC 的“剔除现代数据”是黑名单**
- 位置在 `ViewOutput.D3D9.Compatibility/D3D9ClassicProjectStore.cs:29-39`，靠列出要删掉的现代扩展和资源路径来剔除。
- 以后新增的现代数据，只要忘了加进名单，就会被带进 PMC。
- 公开的 `PmcProjectStore.Save` 本身没有这层过滤；恢复路径则由 App 自己清空灯光。
- 整改：改成白名单，只保存中立布局加上兼容适配器登记过的扩展。这与“PMC 只排除现代专属设置”的约定一致，也更安全。

**B4. EMM 格式解析和 PMC 编解码放在了渲染和视图项目里**：`Rendering.D3D9.Compatibility/Emm*.cs`，以及 `ViewOutput.D3D9.Compatibility` 下的各类编解码。它们属于兼容模式的“工程与格式”，不属于渲染。

建议的目标结构：
- **新建 `Mmd.Modern.Classic.Application`**：纯 CPU，放 EMM 格式、PMC 编解码、兼容文档状态、PMM 转兼容导入和各类编辑事务。
- **兼容宿主**：放进 `ViewOutput.D3D9.Compatibility`，或新建一个 Classic.Host。
- **App**：只留界面。
- **整改后**：补上边界测试，检查 App 不引用 D3D9 命名空间。

## C. 遗留和孤立项目

| 项目 | 现状 | 建议 |
|---|---|---|
| `Rendering.Veldrid` | 旧的第三套渲染器，只能通过 `--legacy-preview` 打开，但因为 App 引用了它，会跟着安装包发布 | 删掉 `MainWindow`、`MainWindowViewModel` 和 `--legacy-preview`，去掉 App 对它的引用 |
| 帧查看器／引擎演示（`FrameViewport*`、`EngineDemo*`、`D3D12ViewportHost`） | 开发工具，但放在正式 App 里，239 处未本地化的中文大多在这里 | 移到 `Mmd.Modern.Demo` 或 `tools/` |
| `Mmd.Modern.Effects` | 托管的 FX 解析和渲染图，产品代码没有使用；实际执行 FX 的是原生 MME 宿主 | 归入兼容模块，只做诊断用，或者直接归档；绝不能被现代引擎引用 |
| `Mmd.Modern.Scheduling` | 原版 MMD 时间语义的精确复刻，只有它自己的测试在用 | 要用就并入 Animation，不用就归档 |
| `Smoke` | 命令行验证工具，不发布 | 移到 `tools/` |
| `Core/Backends.cs`、`Core/RenderingPolicy.cs` | 旧的后端接口和规划器（OpenGL、Vulkan、D3D11），与现在的两引擎设计不符 | 删除，或移到 legacy 目录 |
| `Core/EngineShowcaseScene.cs`、`RayGiDemoScene.cs` | 演示内容放在核心里 | 移到 Demo |

## D. 小问题

- `Physics.Runtime` 依赖 `Application`，只是因为物理后端设置类型定义在 `Application/ProjectSettings.cs` 里。这两个都属于核心，不算违规；但可以把设置类型挪到 `Mmd.Modern.Physics`，让统一入口只依赖物理本身。
- 30fps 常量定义了三处，还有一个字面量：`OfflineFramePlan.cs:66`、`SceneAnimationSession.cs:30`、`FramePlaybackClock.cs:6`，以及 `SemanticTime.cs:24` 里的 `30f`。应该统一成一个。
- 独立软体在 Bepu 刚体世界里也固定用 Bullet（`ProjectPhysicsRuntime.cs:44-51`）。文档里说明过这一点，但它属于混用求解器，需要用户确认是否接受。

## 做得好的地方（不要改坏）

- **时钟**：
  - 预览按宿主经过的时间，在核心里换算成 30fps 源帧。
  - 导出由 `OfflineFramePlan` 计算采样位置，输出帧率只改变采样密度；物理在固定 120Hz 网格上推进。
  - 两个引擎都不自己推进动画或物理。
- **物理**：唯一入口是 `ProjectPhysicsRuntime`，默认 Bullet，Bepu 必须显式选择，不允许混用刚体求解器。视图模块不直接接触物理。
- **兼容引擎**：会主动拒绝现代灯光、GI、AA 和超分，没有混进现代内容。
- **插件**：契约项目没有任何依赖，加载器只在启动时接入。

## 整改进度

- **第 1 步（2026-11-17 完成）：开发工具迁出编辑器**
  - 帧查看器、引擎演示和旧版 Veldrid 预览（共 24 个文件）从 `Mmd.Modern.App` 移到 `Mmd.Modern.Demo`，自带 `DemoApp` 启动。
  - App 去掉了 `--frame-viewer`、`--legacy-preview` 分支和对 Veldrid 的引用；编辑器的依赖清单里已经没有 Veldrid，也不再携带演示用的 `humanoid.pmd`。
  - Demo 和 Veldrid 在解决方案中归到 `/tools/`；`run-app.ps1`、`run-frame-viewer.ps1` 改为运行 Demo。
  - 没有删除任何功能。`Effects`、`Scheduling`、`Core/Backends.cs` 等还没处理，留到后面按“该去的地方”逐个确认。
  - 源码索引已重新生成，顺带去掉了 51 条早已不存在的 `compatibility/archive` 归档条目。

## 建议的整改顺序

都是大改，每一步完成后都要单独构建并跑相关测试：

1. **去掉遗留项目**（C 表前两行）。风险最小，还能让安装包变小。
2. **PMC 剔除改成白名单**（B3）。这一步直接关系到工程文件的安全。
3. **兼容业务逻辑移出 App**（B1、B2、B4）。
4. **建立中立的材质配置和能力接口**（A1、A3）。会和画风改造交叉，建议排在画风改造之后，或者一起规划。
5. **把 D3D12 内容移出核心**（A2），以及第 D 节的几个小问题。
