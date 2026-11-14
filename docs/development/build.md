---
title: 从源码构建
---

[文档首页](../index.md)

# MikuDance Advance (MDA) 开发者构建说明

如果只是使用软件，无需自行编译：请到 [GitHub Releases](https://github.com/luochenYukitsune/miku-dance-advance/releases) 下载 Windows x64 安装包。本页面向准备修改源码或自行构建的开发者。

本文档面向开发者说明 MikuDance Advance（MDA）公开源码的构建结构、已验证范围、原生依赖边界以及构建注意事项。

---

## 1. 技术栈与目标平台

| 维度 | 说明 |
|---|---|
| **目标平台** | Windows x64 |
| **核心语言与框架** | C# / .NET 10 |
| **界面框架** | Avalonia 11 |
| **持续集成（CI）状态** | 公开自动 CI 暂未上线 |

---

## 2. 公开源码包含内容与排除项

### 2.1 仓库包含内容
公开源码快照中包含：
- 托管代码源码（C# / .NET 10）
- 原生模块源码（Native 源码）
- 许可证与合规声明文件

### 2.2 未上传的外部二进制与依赖
出于分发合规与仓库体积控制，以下文件**不上传**至公开源码仓库：
- 原版 MMD Toon 贴图资源
- DirectX 运行库二进制文件
- `FFmpeg` 可执行文件或二进制库
- 可选的硬件厂商 SDK 二进制文件

---

## 3. 托管构建与已验证测试状态

### 3.1 当前验证基线
在清洁的公开源码快照下，已完成以下无原生依赖（无 Native）的托管构建与测试验证：

| 验证项目 | 状态 / 结果 |
|---|---|
| **完整托管代码构建** | 已验证通过（0 警告、0 错误） |
| **Formats 模块测试** | `339` 项无 Native 检查通过 |
| **Scheduling 模块测试** | `84` 项无 Native 检查通过 |
| **Plugins 模块测试** | `39` 项无 Native 检查通过 |

### 3.2 重要边界说明
- **托管编译不等同于完整运行**：通过托管代码编译（例如直接执行 `dotnet build`）**并不代表**完整物理模拟或传统引擎（Classic）已具备可运行条件，也**不等同于**完成了完整的原生组件与 GPU 渲染验收。
- **不直接生成可分发安装包**：构建完整的发布版以及启用传统引擎（Classic）需要额外配置原生依赖与外部运行组件，单纯完成托管构建无法直接生成可分发的完整安装包。

---

## 4. 构建命令说明

### 4.1 宿主与主工程构建命令
先安装 [global.json](https://github.com/luochenYukitsune/miku-dance-advance/blob/main/global.json) 固定的 .NET SDK 10.0.102。在仓库根目录的 PowerShell 中执行：

```powershell
./scripts/dotnet.ps1 -DotNetArguments @('restore','Mmd.Modern.slnx','--locked-mode')
./scripts/dotnet.ps1 -DotNetArguments @('build','Mmd.Modern.slnx','-c','Release','--no-restore')
```

首次还原需要访问 NuGet。包装脚本会将缓存和构建输出放在仓库外的 `../.dev-cache/modern-dotnet/`；主程序输出为其中的 `bin/Mmd.Modern.App/Release/net10.0/`。完整安装包还需准备原生库、编码器及适用的分发材料，不能只把托管输出压缩后当作安装版。

### 4.2 已验证的示例插件发布命令
仓库内置的 `Mda.HelloPlugin` 示例项目已完成实际构建验证。可在仓库根目录执行以下命令进行发布：

```bash
dotnet publish examples/Mda.HelloPlugin/Mda.HelloPlugin.csproj -c Release -o ./plugin-output
```

---

## 5. 原生依赖与 Bullet 物理库构建

### 5.1 Bullet 物理库构建脚本
项目中的两个 Bullet 原生物理库需通过仓库内已有的 Python 脚本进行构建：
- `scripts/build-bullet.py`
- `scripts/build-bullet-soft.py`

### 5.2 编译器路径参数要求
- **必须显式指定 `--compiler`**：调用上述两个脚本时，**必须明确传入 `--compiler` 参数**并将其指向开发者本机的编译器路径。
- **切勿依赖脚本默认路径**：脚本内保留的默认编译器路径为旧本机私有路径，在其他开发环境中不可依赖。
- **已验证编译器**：[Zig](https://ziglang.org/download/) 0.16.0。下面的编译器路径需替换为你的实际路径；`--compiler` 接收 `zig.exe`，不是普通 C++ 编译器的可互换入口。

```powershell
python scripts/build-bullet.py --compiler C:/tools/zig/zig.exe --output ../.dev-cache/bullet-cluster-bounds-20261114/rigid/mmd_bullet.dll
python scripts/build-bullet-soft.py --compiler C:/tools/zig/zig.exe --output ../.dev-cache/bullet-cluster-bounds-20261114/soft/mmd_bullet_soft.dll
```

两个脚本会校验构建输入，并在输出目录保存清单。生成后重新构建托管项目即可按既有项目配置复制库。Classic 原生桥接及可选切线模块另有各自构建入口，见仓库的 [Classic 原生目录](https://github.com/luochenYukitsune/miku-dance-advance/tree/main/native/Mmd.Modern.D3D9.Compatibility)与 [MikkTSpace 目录](https://github.com/luochenYukitsune/miku-dance-advance/tree/main/native/Mmd.Modern.Mikktspace)。这些构建不包含原版 MMD 资源或 DirectX 运行库。

---

## 6. 许可与合规提醒

在构建、修改或分发本项目源码与衍生构建产物前，请务必仔细阅读仓库根目录下的以下文件，确保遵守开源许可与第三方组件声明要求：
- [LICENSE](https://github.com/luochenYukitsune/miku-dance-advance/blob/main/LICENSE)
- [LICENSING.md](https://github.com/luochenYukitsune/miku-dance-advance/blob/main/LICENSING.md)
- [THIRD_PARTY_NOTICES.md](https://github.com/luochenYukitsune/miku-dance-advance/blob/main/THIRD_PARTY_NOTICES.md)
