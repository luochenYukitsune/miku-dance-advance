---
title: 插件 API 1 开发规范
---

[文档首页](../index.md)

# MDA 插件 API 1 开发规范

调试插件前可从 [GitHub Releases](https://github.com/luochenYukitsune/miku-dance-advance/releases) 下载安装对应版本的 MDA，插件接口行为以目标宿主版本为准。

## 1. 适用范围与运行方式

当前插件契约版本为 `API 1`（`apiVersion: 1`），通过零外部依赖的中立契约程序集 `Mmd.Modern.Plugins.Contracts`（仅依赖 BCL 与 `System.Numerics`）开放以下能力：

- `editor.commands`（`PluginCapabilities.EditorCommands`）：编辑器命令扩展点，可在插件管理窗口中触发操作并返回状态提示；
- `rendering.styles`（`PluginCapabilities.RenderingStyles`）：现代 Direct3D 12 渲染引擎风格插件扩展点，允许插件注册完整风格化管线（含自定义材质模式、动态参数描述符、程序化／场景贴图绑定、骨骼追踪与 `space2` HLSL 着色器钩子）。

经典渲染引擎（Direct3D 9）保持原版 MMD／MME 着色行为，不加载或执行现代渲染风格插件。插件只声明并注册中立契约对象，暂不提供任意 WinUI／Avalonia 自定义 UI 控件或物理引擎扩展。

插件启用后与 MDA 主程序在同一进程、相同系统权限下运行，不设安全沙箱。受 .NET 可卸载程序集收集上下文与资源绑定限制，停用插件时宿主会撤回其注册的扩展（含命令与渲染风格，工程中仍在使用的风格将安全回退到基线渲染并可随时重新启用或批量替换）并调用生命周期释放方法。

目录与默认启用约定：随附官方画风插件默认位于程序安装目录 `plugins/<plugin-id>/` 并默认启用（用户停用或重启用状态记录在个人目录 `Plugins/enabled.json`）；用户通过“帮助 → 插件”导入的第三方 `.zip` 插件包解包至个人目录 `Plugins/`，默认处于停用状态，需手动启用。

---

## 2. 清单文件 `mda-plugin.json`

每个插件包根目录必须提供 `mda-plugin.json`（UTF-8 JSON，无 BOM，文件大小上限 `64 KiB`，嵌套深度上限 `12`）。清单解析严格校验字段白名单，不允许出现未定义字段或重复字段。

### 2.1 字段定义

| 字段 | 类型 | 必填 | 格式与约束 |
|---|---|---|---|
| `schemaVersion` | 整数 | 是 | 固定为 `1` |
| `id` | 字符串 | 是 | 长度 `1–128`，以小写字母开头，仅允许小写字母、数字、`.`、`_`、`-` |
| `name` | 字符串 | 是 | 默认显示名称，最长 `200` 个字符 |
| `version` | 字符串 | 是 | `2` 至 `4` 段纯数字版本号（如 `1.0.0`） |
| `entryAssembly` | 字符串 | 是 | 入口 `.dll` 在插件目录内的相对路径（不能为绝对路径或带盘符） |
| `entryType` | 字符串 | 是 | 实现 `IMdaPlugin` 的完整类型名（含命名空间） |
| `apiVersion` | 整数 | 是 | 目标插件 API 版本，当前为 `1` |
| `minimumHostVersion` | 字符串 | 是 | 最低兼容的 MDA 版本号（`2` 至 `4` 段数字，如 `0.1.0`） |
| `dependencies` | 对象数组 | 否 | 最多 `64` 项；每项包含必填 `id`、`minimumVersion` 和选填 `maximumVersion`；无依赖时可传 `[]` 或省略 |
| `capabilities` | 字符串数组 | 否 | 最多 `64` 项；API 1 支持 `"editor.commands"` 与 `"rendering.styles"` |
| `description` | 字符串 | 否 | 默认描述说明，最长 `2048` 个字符，允许换行 |
| `author` | 字符串 | 否 | 作者或维护团队名称，最长 `200` 个字符，不允许换行 |
| `localizedNames` | 语言映射对象 | 否 | 最多 `64` 个语言键（如 `"zh-CN"`、`"en"`、`"ja"`）；值须为非空单行文本，最长 `200` 个字符 |
| `localizedDescriptions` | 语言映射对象 | 否 | 最多 `64` 个语言键；值为对应语言描述，最长 `2048` 个字符，允许换行 |
| `providesStyles` | 字符串数组 | 否 | 最多 `64` 项；声明该插件提供的风格标识（slug），供宿主在停用状态识别并提示“一键启用” |

### 2.2 示例清单

#### 命令插件示例

```json
{
  "schemaVersion": 1,
  "id": "mda.hello",
  "name": "示例插件",
  "version": "1.0.0",
  "entryAssembly": "Mda.HelloPlugin.dll",
  "entryType": "Mda.HelloPlugin.HelloPlugin",
  "apiVersion": 1,
  "minimumHostVersion": "0.1.0",
  "dependencies": [],
  "capabilities": [
    "editor.commands"
  ]
}
```

#### 渲染风格插件示例（参考 `examples/Mda.CustomStylePlugin/mda-plugin.json`）

```json
{
  "schemaVersion": 1,
  "id": "mda.style.custom-ink-cel",
  "name": "水墨赛璐璐（示例）",
  "version": "1.0.0",
  "entryAssembly": "Mda.CustomStylePlugin.dll",
  "entryType": "Mda.CustomStylePlugin.CustomInkCelPlugin",
  "apiVersion": 1,
  "minimumHostVersion": "0.1.0",
  "dependencies": [],
  "capabilities": [
    "rendering.styles"
  ],
  "providesStyles": [
    "custom-ink-cel"
  ],
  "author": "MDA",
  "description": "Official example rendering style plugin.",
  "localizedNames": {
    "zh-CN": "水墨赛璐璐（示例）",
    "en": "Ink Cel (Example)",
    "ja": "水墨セル（例）"
  },
  "localizedDescriptions": {
    "zh-CN": "演示程序化 LUT 纹理、材质模式与自定义 HLSL 钩子。",
    "en": "Demonstrates procedural LUT textures, material modes, and custom HLSL hooks.",
    "ja": "プロシージャル LUT テクスチャ、マテリアルモード、HLSL フックの例。"
  }
}
```

---

## 3. 插件入口、生命周期与扩展点

### 3.1 插件入口 `IMdaPlugin`

`entryType` 指定的类型需实现 `Mmd.Modern.Plugins.Contracts.IMdaPlugin`（继承 `IAsyncDisposable`）：

- `ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)`：插件启用时调用。可通过 `context.PluginId` 与 `context.Host`（含 `ApplicationVersion`、`ApiVersion`、`Language`）读取宿主环境信息，并按能力注册扩展：
  ```csharp
  context.RegisterExtension(PluginCapabilities.EditorCommands, "hello", command);
  context.RegisterExtension(PluginCapabilities.RenderingStyles, "custom-ink-cel", styleProvider);
  ```
- `ValueTask StopAsync(CancellationToken cancellationToken)`：插件停用时调用，用于停止后台任务。
- `ValueTask DisposeAsync()`：在 `StopAsync` 之后释放插件持有的资源。

当插件停用，或在 `StartAsync` 执行期间抛出异常时，宿主会自动撤回该插件已注册的全部扩展项。

### 3.2 命令接口 `IPluginCommand`

注册到 `PluginCapabilities.EditorCommands`（`"editor.commands"`）的对象需实现 `IPluginCommand`：

- `string Title`：在插件窗口命令列表中显示的标题；
- `string? Description`：可选的命令说明；
- `ValueTask<PluginCommandResult> ExecuteAsync(CancellationToken cancellationToken)`：点击执行命令时调用，返回 `new PluginCommandResult("执行结果提示")` 向界面反馈消息。

### 3.3 渲染风格接口 `IRenderingStyleProvider` 与 `IRenderingStyleDefinition`

注册到 `PluginCapabilities.RenderingStyles`（`"rendering.styles"`）的对象需实现 `IRenderingStyleProvider`：

```csharp
public interface IRenderingStyleProvider
{
    IReadOnlyList<IRenderingStyleDefinition> GetStyles();
}
```

每个风格定义需实现 `IRenderingStyleDefinition`，**强烈推荐直接继承抽象基类 `RenderingStyleDefinitionBase`**（内置日语／中文／英语角色名自动识别、默认轮廓线、卡通响应与材质处理参数基线）：

- **基础元数据**：`StyleId`、`DisplayName`、`Description`（`LocalizedText?`）、`Author`、`Version`、`SupportsMaterialRoleGuessing`。
- **多语言文本 `LocalizedText`**：支持 `LocalizedText.From(defaultText, zhCn: ..., en: ..., ja: ...)`；宿主面板、模型属性窗口和校验错误会根据界面语言调用 `.Resolve(language)` 自动切换，必要时回退到 `DefaultText`。
- **动态可组合材质模式 `MaterialModes`（`IReadOnlyList<StyleMaterialModeDescriptor>`）**：
  - `ModeId`、`DisplayName`、`Description`、`AllowedRoles`（限定适配 `Body`/`Face`/`Eye`/`Hair`）、`LegacyAliases`；
  - `Parameters`（`IReadOnlyList<StyleParameterDescriptor>`）：模型属性窗口根据 `StyleParameterKind`（`Float`、`Int`、`Bool`、`Enum` + `StyleEnumOption`、`Color3`、`Color4`、`Vector2`、`Vector3`、`Vector4`、`TextureSlot`、`BoneName`）动态生成对应控件与重置默认值能力；
  - `TextureInputs`（`IReadOnlyList<StyleTextureSlotDescriptor>`）：声明局部贴图槽位（`SlotKey`、`DisplayName`、`Required`、`Encoding`、`MipRule`、可选 `SupportsSecondaryTexture` / `SecondaryDisplayName`），宿主按槽位生成模型贴图选择器并在保存时记录纹理指纹保护；
  - `SupportsBoneTracking` + `StyleBoneAxisDefaults`：启用后 UI 自动呈现“固定世界方向采样（`FixedWorld`）”与“绑定骨骼每帧实时跟随（`SelectedBone`）”切换。
- **全局参数 `StyleParameters`** 与默认角色参数：`DefaultOutline`、`DefaultToonResponse`、`DefaultStylizedShading`、`DefaultFaceEye`、`DefaultRoleTreatments`。
- **关键生命周期回调**：
  - `GetShaderModules()`：返回本风格使用的全部 `StyleShaderModule`；
  - `ClassifyMaterial(StyleMaterialInspectionContext)`：返回 `StyleMaterialClassification`（解析材质角色与基础参数覆盖）；
  - `ValidateMaterial(StyleMaterialValidationContext)`：校验当前材质绑定、必填槽位、参数范围或源纹理尺寸要求，失败时通过 `StyleValidationResult.Error(string message, LocalizedText? localizedError)` 返回局部诊断信息，模型属性界面会立即展示多语言校验提示；
  - `GetSceneTextureRequests(StyleMaterialBindingContext)` / `RequiresSourceGeometry(StyleMaterialBindingContext)` / `GetBoneTransformRequest(StyleMaterialBindingContext)`：声明需要解析的场内贴图（含 `StyleTextureEncoding`、`StyleTextureMipRule`、`ExpectedWidth`/`Height` 与区分必填/可选槽位的 `StyleSceneTextureRequest.IsOptional`）、是否需要宿主提取源几何与 MikkTSpace 切线数据（`RequiresSourceGeometry`，默认 `false`），以及需按帧求值的骨骼本地前向／上方向；
  - `BuildMaterialDrawData(StyleDrawContext)`：构建逐材质 `StyleDrawData`（目标 `ShaderModuleId`、常量缓冲区字节、贴图绑定、结构化缓冲区绑定与按需覆盖参数）。

---

## 4. Direct3D 12 `space2` 着色器契约与 HLSL 钩子

为确保自定义画风可以无损融入宿主 MMD 原版着色、GI 间接光、原生灯光阴影及风格化调节管线，D3D12 渲染器为插件分配了独立的根签名空间 **`space2`** 与预处理宏注入机制（详见 `D3D12StyleShaderAssembler` 与 `StyleResourceLimits`）。

### 4.1 统一 `space2` 资源绑定 ABI

单个 Draw 的资源配额受 `StyleResourceLimits` 约束：

| 寄存器 / 空间 | 契约类型与上限 | 说明与规则 |
|---|---|---|
| `b0, space2` | 常量缓冲区 `ConstantBufferData`（上限 `4096` 字节） | 在 HLSL 中声明为 `cbuffer ... : register(b0, space2)`；C# 端使用 `StyleConstantBufferBuilder` 按 HLSL `cbuffer` 16 字节寄存器对齐规则序列化标量、向量与 `Matrix4x4`（建议末尾调用 `.PadTo16()`） |
| `t0..t11, space2` | 最多 `12` 个 `Texture2D<float4>`（`StyleResourceLimits.MaxTextureSlots = 12`） | 通过 `StyleTextureBinding(SlotIndex, SceneTextureIndex, InlineTexture, Encoding, MipRule)` 绑定；支持引用场景内模型纹理（`SceneTextureIndex >= 0`）或内嵌 CPU 纹理资产 `StyleInlineTexture`（支持 `Rgba8Unorm`、`Rgba8UnormSrgb`、`Rgba16Float`、`Rgba32Float`、`R8Unorm`），可分别配置 `StyleTextureEncoding`（`Linear` / `SrgbColor`）与 `StyleTextureMipRule`（`FromSource` / `PreserveMips` / `PointNoMip` / `GenerateMips`） |
| `t12..t15, space2` | 最多 `4` 个 `StructuredBuffer<T>`（`StyleResourceLimits.MaxStructuredBufferSlots = 4`） | 通过 `StyleBufferBinding(SlotIndex 0..3, Kind, ...)` 绑定到 `t(12 + SlotIndex), space2`；`StyleBufferKind.MikkTangentCorners` 由宿主自动计算并上传 `StructuredBuffer<MdaTangentCorner>`（含 `WorldPosition`、`WorldNormal`、`Tangent`、`UvParity`），`StyleBufferKind.CustomStructured` 用于自定义结构化缓冲区（如 LUT 表格） |
| `s0..s3, space2` | `4` 个内置静态采样器 | 宿主预声明可直接调用：`MdaLinearWrapSampler`（`s0, space2`）、`MdaLinearClampSampler`（`s1, space2`）、`MdaPointWrapSampler`（`s2, space2`）、`MdaPointClampSampler`（`s3, space2`） |

### 4.2 几何需求标志 `StyleGeometryRequirements`

在 `StyleShaderModule.GeometryRequirements` 中按位组合声明所需几何输入，汇编器将自动开启顶点着色器输入/输出成员与对应宏定义：

| 标志位 | HLSL 宏定义 | 注入行为与可用字段 |
|---|---|---|
| `SubTextureUv` | `#define MDA_STYLE_GEOM_SUB_TEXTURE_UV 1` | 读取顶点 `TEXCOORD1`，写入 `SurfaceOutput.AdditionalUv : TEXCOORD4` |
| `FrontFace` | `#define MDA_STYLE_GEOM_FRONT_FACE 1` | 向像素入口注入 `bool mdaFront : SV_IsFrontFace` 并传入 `FinalEntry*` 钩子 |
| `PrimitiveId` | `#define MDA_STYLE_GEOM_PRIMITIVE_ID 1` | 向像素入口注入 `uint mdaPrimitive : SV_PrimitiveID`（启用 GI 时宿主也会自动提供） |
| `MikkTangentCorners` | `#define MDA_STYLE_GEOM_MIKK_TANGENT_CORNERS 1` | 标记该着色器消费 MikkTSpace 角点切线结构化缓冲区 |
| `RawRadialSeeds` | `#define MDA_STYLE_GEOM_RAW_RADIAL_SEEDS 1` | 读取扩展顶点流 `RAW_RADIAL`，经世界矩阵变换后提供 `SurfaceOutput.RawSeed : TEXCOORD5` |
| `ModelSpacePosition` | `#define MDA_STYLE_GEOM_MODEL_POSITION 1` | 提供模型局部空间顶点位置 `SurfaceOutput.ModelPosition : TEXCOORD6` |
| `ModelSpaceNormal` | `#define MDA_STYLE_GEOM_MODEL_NORMAL 1` | 提供模型局部空间顶点法线 `SurfaceOutput.ModelNormal : TEXCOORD7` |

### 4.3 着色器钩子与掩码 `StyleShaderHookMask`

当 `StyleShaderModule.Hooks` 启用相应标志时，汇编器会定义对应宏并调用插件 HLSL 中实现的同名函数（宿主常量如 `FrameConstants (b0)`、`SurfaceOutput` 以及 `MdaComputeBaseColor`、`MdaComputeAlbedo`、`MdaComputeDiffuseLit`、`MdaPrimaryShadowVisibility` 等辅助函数均在插件源码之前前置声明，可直接调用）：

| `StyleShaderHookMask` 标志 | HLSL 宏定义 | 需实现的 HLSL 函数签名 | 作用阶段 |
|---|---|---|---|
| `LightResponse` | `MDA_STYLE_HOOK_LIGHT_RESPONSE` | `float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility)` | 计算主光源明暗响应系数 `[0,1]`；未启用时使用宿主 `MdaLightResponseDefault` |
| `PaletteFactor` | `MDA_STYLE_HOOK_PALETTE_FACTOR` | `float3 MdaStylePaletteFactor(SurfaceOutput v, float blend, float nDotL, float visibility)` | 返回调色板阴影衰减系数；未启用时使用 `MdaPaletteFactorDefault` |
| `Specular` | `MDA_STYLE_HOOK_SPECULAR` | `float3 MdaStyleSpecular(SurfaceOutput v, float3 n, float3 h, float3 l, float3 baseColor)` | 替换基础高光计算 |
| `AlbedoTransform` | `MDA_STYLE_HOOK_ALBEDO_TRANSFORM` | `float3 MdaStyleAlbedoTransform(SurfaceOutput v, float3 color)` | 在主纹理采样、PMX 饱和度调节后变换基础反照率 |
| `PrimarySurfacePost` | `MDA_STYLE_HOOK_PRIMARY_SURFACE_POST` | `float4 MdaStylePrimarySurfacePost(SurfaceOutput v, float4 color)` | 主表面光照（含可选风格化包装器）完成后的逐像素后处理 |
| `FinalEntryPre` | `MDA_STYLE_HOOK_FINAL_ENTRY_PRE` | `void MdaStyleFinalEntryPre(SurfaceOutput v, uint mdaPrimitive, bool mdaFront)` | 在 GI / 原生多光源合成之前执行前置状态准备 |
| `FinalEntryPost` | `MDA_STYLE_HOOK_FINAL_ENTRY_POST` | `float4 MdaStyleFinalEntryPost(SurfaceOutput v, uint mdaPrimitive, bool mdaFront, float4 color)` | 在所有光照与间接光叠加完成后的最终像素输出修正 |

其他常用管线控制标志：`AllowStylizedShadingWrapper`（允许宿主风格化包装）、`DisableStylizedSphereMaps`（禁用通用风格化环境球叠加）、`HasStyleShaderCode`（有自定义 HLSL 代码时由汇编器自动或显式置位）、`RequiresOutlineColorScale`、`RequiresSkinShadeBlend`、`PreserveDirectLightPalette`（在逐 Draw 覆写时也可通过 `StyleDrawData` 调整）。

---

## 5. 项目配置、构建与 ZIP 打包

### 5.1 共享契约引用与项目文件（关键）

插件项目引用 `Mmd.Modern.Plugins.Contracts` 时，必须在 `.csproj` 中将引用设为 `<Private>false</Private>`（并且不打包其他冲突的宿主程序集），同时启用 `<GenerateDependencyFile>true</GenerateDependencyFile>` 并将 `mda-plugin.json` 复制到输出与发布目录，这样插件在独立加载上下文中运行后，才能与宿主共享同一个契约类型身份（也可写作内联属性 `Private="false"`，参见 `examples/Mda.CustomStylePlugin/Mda.CustomStylePlugin.csproj`）：

```xml
<PropertyGroup>
  <Version>1.0.0</Version>
  <GenerateDependencyFile>true</GenerateDependencyFile>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="..\..\src\Mmd.Modern.Plugins.Contracts\Mmd.Modern.Plugins.Contracts.csproj">
    <Private>false</Private>
    <ExcludeAssets>runtime</ExcludeAssets>
  </ProjectReference>
  <None Update="mda-plugin.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
</ItemGroup>
```

### 5.2 发布构建与参考实现

仓库内置了官方示例与完整风格插件实现，开发时可直接对照：

- **命令插件示例**：`examples/Mda.HelloPlugin`
- **第三方纯契约风格插件示例**：`examples/Mda.CustomStylePlugin`（仅依赖 `Mmd.Modern.Plugins.Contracts`，完整演示清单多语言字段、内联 LUT 纹理、材质模式、参数校验与 `space2` HLSL 钩子）
- **随附风格插件参考实现**：`src/Mda.StylePlugin.Genshin`、`src/Mda.StylePlugin.StarRail`、`src/Mda.StylePlugin.WutheringWaves`、`src/Mda.StylePlugin.Endfield`

在仓库根目录执行发布命令构建示例插件：

```powershell
dotnet publish examples/Mda.HelloPlugin/Mda.HelloPlugin.csproj -c Release -o ./plugin-output
dotnet publish examples/Mda.CustomStylePlugin/Mda.CustomStylePlugin.csproj -c Release -o ./style-plugin-output
```

构建完成后，确保输出目录中包含 `mda-plugin.json`、`Mda.CustomStylePlugin.dll` 与 `.deps.json`（请勿附带重复的 `Mmd.Modern.Plugins.Contracts.dll`），压缩为 `.zip` 即可导入 MDA。

### 5.3 ZIP 包结构与安全限制

- **目录结构**：可以在 ZIP 根目录直接放置 `mda-plugin.json` 和插件文件，也可以用一个顶层文件夹包住全部插件文件；采用单顶层文件夹时，文件夹外不要再混放其他同级文件。
- **体积与数量限制**：ZIP 内最多 `2048` 个条目，单个文件解压后不超过 `64 MiB`，全部文件解压总大小不超过 `512 MiB`，`mda-plugin.json` 不超过 `64 KiB`。
- **导入、停用与工程兼容性**：在“帮助 → 插件”中选择 `.zip` 或把 `.zip` 直接拖进窗口时，宿主只做结构校验与解包，不执行任何插件代码，导入后需在列表中手动启用。若本地已存在相同 `id` 的插件，导入会直接拒绝，不会覆盖已有版本。打开引用了已停用插件风格的工程时，界面不会丢失原工程数据，而是允许一键启用本地插件或通过风格替换弹窗迁移到当前可用的风格。

[查看自定义风格插件示例](https://github.com/luochenYukitsune/miku-dance-advance/tree/main/examples/Mda.CustomStylePlugin) · [查看命令示例插件源码](https://github.com/luochenYukitsune/miku-dance-advance/tree/main/examples/Mda.HelloPlugin) · [查看插件契约定义](https://github.com/luochenYukitsune/miku-dance-advance/tree/main/src/Mmd.Modern.Plugins.Contracts)
