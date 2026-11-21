---
title: 从源码构建
---

[文档首页](../index.md)

# 从源码构建 MikuDance Advance

如果只是日常使用软件，直接前往 [GitHub Releases](https://github.com/luochenYukitsune/miku-dance-advance/releases) 下载 `MDA-<版本>-win-x64-setup.exe` 安装即可，不需要自己编译。本文面向准备阅读、修改源码或在本地构建 MDA 的开发者。

---

## 1. 开发环境与仓库组成

MDA 面向 Windows x64 平台开发，托管部分使用 C# / .NET 10 与 Avalonia 11，解决方案入口为仓库根目录的 `Mmd.Modern.slnx`，SDK 版本由 [global.json](https://github.com/luochenYukitsune/miku-dance-advance/blob/main/global.json) 固定为 `.NET SDK 10.0.102`。

公开源码仓库包含：

- 全部 C# 托管模块、官方随附画风插件（`src/Mda.StylePlugin.*`）、示例插件（`examples/Mda.HelloPlugin`、`examples/Mda.CustomStylePlugin`）与单元测试项目源码（`src/`、`tests/`、`examples/`、`tools/`）；
- 原生模块源码：Bullet 刚体与软体物理桥接（`native/Mmd.Modern.Bullet*`）、MikkTSpace 切线计算（`native/Mmd.Modern.Mikktspace`）以及传统引擎 D3D9／MME 兼容层（`native/Mmd.Modern.D3D9.Compatibility`、`compatibility/d3d9-mme-host`）；
- 构建与打包脚本（`scripts/`）、Inno Setup 安装器脚本（`installer/MDA.iss`）及开源许可声明文件。

出于许可合规与仓库体积考虑，外部二进制工具和第三方运行库不直接放进 Git 仓库，包括：

- `ffmpeg.exe` 与 `ffprobe.exe`（Releases 安装包已内置，并提供对应源码与构建材料附件；本地构建时可通过 `scripts/fetch-ffmpeg.ps1` 按 `tools/ffmpeg/manifest.json` 下载固定版本并校验 SHA-256，上游支持基线为 Windows 10 22H2）；
- DirectX 着色器编译器 `dxc.exe`（通过 `scripts/setup-dxc.ps1` 按 `tools/dxc/manifest.json` 拉取并校验）；
- AMD FSR、Intel XeSS、NVIDIA DLSS 超分运行库（按 `tools/upscaling/manifest.json` 拉取官方固定版本并校验签名／哈希，`mmd_dlss` 桥接库使用 MSVC 编译）；
- DirectX D3DX9 运行库二进制与原版 MMD Toon 贴图资源。

---

## 2. 编译托管代码与示例插件

先安装 `.NET SDK 10.0.102`，在仓库根目录打开 PowerShell 执行：

```powershell
./scripts/dotnet.ps1 -DotNetArguments @('restore','Mmd.Modern.slnx','--locked-mode')
./scripts/dotnet.ps1 -DotNetArguments @('build','Mmd.Modern.slnx','-c','Release','--no-restore')
```

首次 `restore` 需要联网访问 NuGet。`scripts/dotnet.ps1` 会把包缓存、临时目录和编译输出统一放在仓库上一级的 `../.dev-cache/modern-dotnet/` 下，主程序输出路径为 `../.dev-cache/modern-dotnet/bin/Mmd.Modern.App/Release/net10.0/`。在常规构建或自包含发布 `Mmd.Modern.App` 时，项目配置会自动构建四套官方随附画风插件并暂存到输出目录的 `plugins/<plugin-id>/`（仅包含 `mda-plugin.json`、插件 `.dll` 与 `.deps.json`，不复制重复的 `Mmd.Modern.Plugins.Contracts.dll`）。

仓库自带的示例插件 `Mda.HelloPlugin` 与第三方画风插件示例 `Mda.CustomStylePlugin` 可分别单独发布：

```powershell
dotnet publish examples/Mda.HelloPlugin/Mda.HelloPlugin.csproj -c Release -o ./plugin-output
dotnet publish examples/Mda.CustomStylePlugin/Mda.CustomStylePlugin.csproj -c Release -o ./style-plugin-output
```

只执行上面的托管构建即可编译全部 C# 项目，并运行不依赖原生 DLL 和显卡的测试项目（如 `Mmd.Modern.Formats.Tests`、`Mmd.Modern.Scheduling.Tests`、`Mmd.Modern.Plugins.Tests`）。要在本地启动完整编辑器、跑刚柔体物理解算、编译 D3D12 着色器或使用传统引擎，还需要把下一节的原生组件准备好。

---

## 3. 编译原生组件与装配运行库

### 3.1 Bullet 刚体与软体物理库

两个 Bullet 原生库使用 [Zig](https://ziglang.org/download/) `0.16.0` 通过仓库内的 Python 脚本编译。调用时请显式传入 `--compiler` 指向本机的 `zig.exe`（脚本里的默认路径是早期开发机的本地路径；`--compiler` 专门接收 `zig.exe`，不能直接替换成 `cl.exe` 或 `clang++.exe`）：

```powershell
python scripts/build-bullet.py --compiler C:/tools/zig/zig.exe --output ../.dev-cache/bullet-cluster-bounds-20261114/rigid/mmd_bullet.dll
python scripts/build-bullet-soft.py --compiler C:/tools/zig/zig.exe --output ../.dev-cache/bullet-cluster-bounds-20261114/soft/mmd_bullet_soft.dll
```

脚本会校验源码输入并在输出目录写入构建清单。两个脚本固定传入 `-target x86_64-windows-gnu -mcpu=baseline`（Zig 默认按构建机 CPU 生成指令，若在支持 AVX-512 等扩展指令的机器上省略这两个参数，编出的 DLL 在不支持对应指令的用户 CPU 上加载时会在初始化阶段报 `0x8007045A` 失败，请保留这两个参数）。DLL 生成后重新执行一次托管构建，项目配置会自动把它们复制到程序输出目录。

### 3.2 DXC、MikkTSpace、超分与传统引擎

本地一键准备测试所需的原生依赖（Bullet、MikkTSpace 与固定版本 DXC）可直接参考或运行 CI 使用的脚本：

- `scripts/ci-native-deps.ps1`：一次编译 Bullet 刚体／软体、MikkTSpace（静态链接 CRT）并下载校验 DXC；
- `scripts/ci-windows-build.ps1`：构建包含原生物理库、DXC、固定版本 FFmpeg 与 .NET 自包含运行时的 Windows x64 发布目录；
- `scripts/ci-stage-upscalers.ps1` 与 `scripts/ci-stage-classic.ps1`：往发布目录装配 FSR／XeSS／DLSS 超分组件（需 MSVC 环境编译 `mmd_dlss.dll`）与传统引擎组件（见 [Classic 原生目录](https://github.com/luochenYukitsune/miku-dance-advance/tree/main/native/Mmd.Modern.D3D9.Compatibility) 和 [MikkTSpace 目录](https://github.com/luochenYukitsune/miku-dance-advance/tree/main/native/Mmd.Modern.Mikktspace)）；
- `scripts/ci-package-installer.ps1`：先调用 `scripts/check-native-portability.py` 检查发布目录内所有原生库的依赖与指令集（确认原生 DLL／EXE 均为 x64、非系统自带依赖均已随包放在同目录，且 Zig 编译的物理与传统引擎库仅使用 x86-64 基线指令），通过后调用 Inno Setup 编译 `installer/MDA.iss`，生成最终的 `MDA-<版本>-win-x64-setup.exe` 安装程序。

---

## 4. GitHub Actions 自动构建与发版

仓库配有两个自动化工作流：

### 4.1 构建与测试（`.github/workflows/build.yml`）

在推送 `main`、提交 PR（纯 `docs/**` 和 `*.md` 改动除外）或手动触发时运行：

1. **文案与清单检查**（Linux）：运行 `scripts/check-localization.py` 检查 `zh-CN`、`en`、`ja` 三语文案键、占位符与界面引用一致，验证 `tools/*/manifest.json` 格式并扫描潜在凭据文本。
2. **编译**（Windows）：编译 Bullet 刚体／软体与 MikkTSpace，拉取 DXC，按锁定依赖还原并编译整个 `Mmd.Modern.slnx`。
3. **非 GPU 测试门禁**（Windows）：分五组并行运行测试（`格式·调度·插件`、`应用与物理`、`输出与编码`、`传统引擎兼容`、`编辑器与渲染`），五组全部作为必过门禁。由于托管运行器没有硬件显卡，`Category=Gpu` 用例和耗时的着色器宏组合穷举编译测试不在此阶段运行。
4. **Windows 构建包与安装验证**：完整打包现代引擎、FFmpeg、DXC、三种超分和传统引擎，先执行 `scripts/check-native-portability.py` 校验原生库运行依赖与 x86-64 基线指令集，编译 Inno Setup 安装包，再通过 `scripts/ci-check-installer-associations.ps1` 实装验证简体中文向导与 Windows 文件关联逻辑，最后上传 `MDA-modern-development-win-x64` 构建产物到 Actions Artifacts。
5. **手动发版与真机 GPU 验收**（可选）：手动触发时勾选 `publish_release` 可直接把当次编译出的双引擎安装包推送到 GitHub Releases（版本号留空时自动按上一版末位 `+1`，可选择预发布版或正式版）；勾选 `run_gpu` 则在带 `gpu` 标签的自托管 Windows 机器上执行真机渲染与超分初始化验收。

### 4.2 发布 GitHub Release（`.github/workflows/release.yml`）

标签和文案检查通过后，Windows 安装包构建、实装验证与同提交 CI 检查并行运行。发布任务从当次运行读取已经实装验证的安装包，并在 CI 门禁通过后上传。普通 CI 也同时启动编译测试和 Windows 打包。两条流程均可复用相同 Zig 版本、构建脚本和 Bullet 源码的原生库缓存。

支持推送 `v*` 标签自动触发，也支持在 Actions 页面手动触发：云端完成双引擎与三种超分安装包编译（含原生库可移植性检查）后，自动核对并复用固定版本 FFmpeg 的对应源码包附件，根据上一版标签以来的提交记录与 `CHANGELOG.md` 生成中英双语更新说明，计算全部附件的 `SHA256SUMS.txt` 并发布到 GitHub Releases。

---

## 5. 开源许可与第三方声明

修改源码或分发构建产物前，请查阅仓库根目录下的许可与声明文件：

- [LICENSE](https://github.com/luochenYukitsune/miku-dance-advance/blob/main/LICENSE)（原创代码采用 AGPL-3.0-only）
- [LICENSING.md](https://github.com/luochenYukitsune/miku-dance-advance/blob/main/LICENSING.md)（原创代码与第三方组件的许可边界说明）
- [THIRD_PARTY_NOTICES.md](https://github.com/luochenYukitsune/miku-dance-advance/blob/main/THIRD_PARTY_NOTICES.md)（第三方开源库声明）
