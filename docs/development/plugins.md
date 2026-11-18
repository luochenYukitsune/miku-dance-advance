---
title: 插件 API 1 开发规范
---

[文档首页](../index.md)

# MDA 插件 API 1 开发规范

调试插件前可从 [GitHub Releases](https://github.com/luochenYukitsune/miku-dance-advance/releases) 下载安装对应版本的 MDA，插件接口行为以目标宿主版本为准。

## 1. 适用范围与运行方式

当前插件契约版本为 `API 1`（`apiVersion: 1`），开放 `editor.commands` 编辑器命令扩展点；暂不提供自定义界面（UI）控件、渲染管线扩展或物理引擎扩展。

插件启用后与 MDA 主程序在同一进程、相同系统权限下运行，不设安全沙箱。受 .NET 程序集加载机制限制，停用插件时宿主会撤回其注册的扩展命令并调用释放方法，但已加载进进程的插件 DLL 通常会保留到主程序退出。

---

## 2. 清单文件 `mda-plugin.json`

每个插件包根目录必须提供 `mda-plugin.json`（UTF-8 JSON，文件大小上限 `64 KiB`，嵌套深度上限 `12`）。清单解析时不允许出现未定义字段或重复字段。

### 2.1 字段定义

| 字段 | 类型 | 必填 | 格式与约束 |
|---|---|---|---|
| `schemaVersion` | 整数 | 是 | 固定为 `1` |
| `id` | 字符串 | 是 | 长度 `1–128`，以小写字母开头，仅允许小写字母、数字、`.`、`_`、`-` |
| `name` | 字符串 | 是 | 插件显示名称，最长 `200` 个字符 |
| `version` | 字符串 | 是 | `2` 至 `4` 段纯数字版本号（如 `1.0.0`） |
| `entryAssembly` | 字符串 | 是 | 入口 `.dll` 在插件目录内的相对路径（不能为绝对路径或带盘符） |
| `entryType` | 字符串 | 是 | 实现 `IMdaPlugin` 的完整类型名（含命名空间） |
| `apiVersion` | 整数 | 是 | 目标插件 API 版本，当前为 `1` |
| `minimumHostVersion` | 字符串 | 是 | 最低兼容的 MDA 版本号（`2` 至 `4` 段数字，如 `0.1.0`） |
| `dependencies` | 对象数组 | 否 | 最多 `64` 项；每项包含必填的 `id`、`minimumVersion` 和选填的 `maximumVersion`；无依赖时可传 `[]` 或省略 |
| `capabilities` | 字符串数组 | 否 | 最多 `64` 项；API 1 当前仅支持 `"editor.commands"` |

### 2.2 示例清单

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

---

## 3. 接口与生命周期

### 3.1 插件入口 `IMdaPlugin`

`entryType` 指定的类型需实现 `Mmd.Modern.Plugins.Contracts.IMdaPlugin`（继承 `IAsyncDisposable`）：

- `ValueTask StartAsync(IPluginContext context, CancellationToken cancellationToken)`：插件启用时调用。可通过 `context.PluginId` 与 `context.Host`（含 `ApplicationVersion`、`ApiVersion`、`Language`）读取宿主环境信息，并注册命令扩展：
  ```csharp
  context.RegisterExtension("editor.commands", "hello", command);
  ```
- `ValueTask StopAsync(CancellationToken cancellationToken)`：插件停用时调用，用于停止后台任务。
- `ValueTask DisposeAsync()`：在 `StopAsync` 之后释放插件持有的资源。

当插件停用，或在 `StartAsync` 执行期间抛出异常时，宿主会自动撤回该插件已注册的全部扩展项。

### 3.2 命令接口 `IPluginCommand`

注册到 `"editor.commands"` 的对象需实现 `IPluginCommand`：

- `string Title`：在插件窗口命令列表中显示的标题；
- `string? Description`：可选的命令说明；
- `ValueTask<PluginCommandResult> ExecuteAsync(CancellationToken cancellationToken)`：点击执行命令时调用，返回 `new PluginCommandResult("执行结果提示")` 向界面反馈消息。

---

## 4. 项目配置、构建与 ZIP 打包

### 4.1 共享契约引用

插件项目引用 `Mmd.Modern.Plugins.Contracts` 时，必须在 `.csproj` 中将引用设为 `Private=false`（不复制契约 DLL 到输出目录），这样插件加载后才能与宿主共享同一个契约程序集和接口类型身份。

### 4.2 发布构建

以仓库内置的 `examples/Mda.HelloPlugin` 为例，在仓库根目录执行：

```powershell
dotnet publish examples/Mda.HelloPlugin/Mda.HelloPlugin.csproj -c Release -o ./plugin-output
```

构建完成后，将 `plugin-output` 中的 `mda-plugin.json`、`Mda.HelloPlugin.dll` 及随附的 `.deps.json` 等文件打成 `.zip` 压缩包即可导入测试。

### 4.3 ZIP 包结构与大小限制

- **目录结构**：可以在 ZIP 根目录直接放置 `mda-plugin.json` 和插件文件，也可以用一个顶层文件夹包住全部插件文件；采用单顶层文件夹时，文件夹外不要再混放其他同级文件。
- **体积与数量限制**：ZIP 内最多 `2048` 个条目，单个文件解压后不超过 `64 MiB`，全部文件解压总大小不超过 `512 MiB`，`mda-plugin.json` 不超过 `64 KiB`。
- **导入与更新**：在“帮助 → 插件”中选择 `.zip` 或把 `.zip` 直接拖进窗口时，宿主只做结构校验与解包，不执行任何插件代码，导入后需在列表中手动启用。若本地已存在相同 `id` 的插件，导入会直接拒绝，不会覆盖已有版本。

[查看完整示例插件源码](https://github.com/luochenYukitsune/miku-dance-advance/tree/main/examples/Mda.HelloPlugin) · [查看共享契约定义](https://github.com/luochenYukitsune/miku-dance-advance/blob/main/src/Mmd.Modern.Plugins.Contracts/PluginContracts.cs)
