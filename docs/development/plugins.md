---
title: 插件 API 1 开发规范
---

[文档首页](../index.md)

# MikuDance Advance (MDA) 插件 API 1 开发规范

开发前可从 [GitHub Releases](https://github.com/luochenYukitsune/miku-dance-advance/releases) 获取 MDA 安装包，并查看对应版本的更新说明。插件接口以目标宿主版本为准。

## 1. 概述与能力边界

MDA 插件系统 API 1（`apiVersion: 1`）为开发者提供宿主内命令扩展机制：
- **已开放能力**：当前 API 1 仅开放 `editor.commands`（编辑器命令扩展）。
- **未开放能力**：当前未开放通用自定义界面（UI）、渲染管线扩展或物理引擎扩展。
- **运行模型与限制**：
  - 插件在宿主同一进程、相同权限下运行，**不是安全沙箱**。
  - 受 .NET 程序集加载机制影响，宿主**不能保证插件停用后其 DLL 能够立即从内存中完全卸载**。

---

## 2. 插件清单 (`mda-plugin.json`) 规范

每个插件包必须包含名为 `mda-plugin.json` 的清单文件。清单解析执行严格校验：**禁止包含未知属性或重复属性**。

### 2.1 字段定义与约束

| 字段名 | 类型 | 必填 | 约束说明 |
|---|---|---|---|
| `schemaVersion` | 整数 | 是 | 固定为 `1` |
| `id` | 字符串 | 是 | 必须以小写字母开头，仅允许包含小写字母、数字、点（`.`）、下划线（`_`）和连字符（`-`） |
| `name` | 字符串 | 是 | 插件显示名称 |
| `version` | 字符串 | 是 | 由 `2` 至 `4` 段数字组成的版本号（如 `0.1.0`） |
| `entryAssembly` | 字符串 | 是 | 插件入口 DLL 在压缩包内的相对路径 |
| `entryType` | 字符串 | 是 | 实现 `IMdaPlugin` 接口的完整类型名称（含命名空间） |
| `apiVersion` | 整数 | 是 | 固定为 `1` |
| `minimumHostVersion` | 字符串 | 是 | 最低兼容的 MDA 宿主版本（如 `0.1.0`） |
| `dependencies` | 数组 | 否 | 依赖列表（无依赖时传空数组 `[]`） |
| `capabilities` | 字符串数组 | 否 | 请求的能力列表，API 1 当前仅支持 `["editor.commands"]` |

### 2.2 示例清单 (`mda.hello`)

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

## 3. 核心接口与生命周期

### 3.1 插件入口与生命周期 (`IMdaPlugin`)
插件入口类型（由 `entryType` 指定）必须实现 `IMdaPlugin` 接口，包含以下生命周期方法：
- `StartAsync`：插件启用时调用。在此阶段通过上下文注册扩展能力，例如调用：
  ```csharp
  context.RegisterExtension("editor.commands", "hello", command);
  ```
- `StopAsync`：插件停用时调用。
- `DisposeAsync`：释放插件持有的资源。

> **自动撤销机制**：当插件被停用，或在 `StartAsync` 启动过程中发生失败时，宿主会自动撤销该插件已注册的所有扩展。

### 3.2 命令扩展接口 (`IPluginCommand`)
注册到 `"editor.commands"` 的命令对象需实现 `IPluginCommand` 接口，包含以下成员：
- `Title`：命令显示标题。
- `Description`：命令描述信息。
- `ExecuteAsync`：命令执行入口，返回 `PluginCommandResult`。

---

## 4. 项目配置、构建与打包规范

### 4.1 共享契约引用规则
插件项目在引用 MDA 共享契约程序集（Contracts）时，必须在项目文件（`.csproj`）中将该引用配置为 `Private=false`，**禁止在插件输出包中打包私有的 Contracts DLL 副本**，以确保运行时与宿主共享同一契约类型身份。

### 4.2 构建命令
以仓库内置的 `Mda.HelloPlugin` 示例为例，在仓库根目录执行以下命令进行发布构建：

```bash
dotnet publish examples/Mda.HelloPlugin/Mda.HelloPlugin.csproj -c Release -o ./plugin-output
```

构建成功后，`plugin-output` 中将生成：
- `mda-plugin.json`（插件清单）
- `Mda.HelloPlugin.dll`（入口程序集）
- 对应的 `.deps.json` 及 `.pdb` 文件

将上述输出文件一起压缩为 `.zip` 归档即可用于导入。

### 4.3 ZIP 包结构与配额限制

| 规范项 | 具体要求 |
|---|---|
| **目录层级规则** | 允许两种结构：<br>1. 在 ZIP 根目录直接放置唯一的 `mda-plugin.json` 及相关文件；<br>2. 使用单一顶层文件夹包裹所有插件文件。<br>**严禁**在使用单一顶层文件夹的同时，在包外同级位置放置其他文件。 |
| **条目数量上限** | 最多 `2048` 项 |
| **单文件大小上限** | 单项解压后最大 `64 MiB` |
| **总展开体积上限** | 所有文件展开总容量最大 `512 MiB` |
| **清单文件上限** | `mda-plugin.json` 最大 `64 KiB` |
| **导入与冲突策略** | 导入阶段仅做校验与解包，**不执行任何插件代码**；若本地已存在相同 `id` 的插件，导入操作**不会覆盖**既有插件。 |



[查看完整示例插件源码](https://github.com/luochenYukitsune/miku-dance-advance/tree/main/examples/Mda.HelloPlugin) · [查看共享契约](https://github.com/luochenYukitsune/miku-dance-advance/blob/main/src/Mmd.Modern.Plugins.Contracts/PluginContracts.cs)
