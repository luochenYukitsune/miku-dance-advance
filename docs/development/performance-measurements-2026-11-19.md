# 真实模型的播放求值、图形读回与导出实测

**第一批值得实现的改动是 D3D12 围栏等待和 FFmpeg 管道批写。** 在同一段 40 帧动作上，第一项缩短同步读回，第二项缩短送帧。三角色播放的主要开销在共享物理世界步进和顶点拼装。增加蒙皮线程对这段物理动作的中位耗时影响很小。

## 测试条件

- 产品源码 `2abe9a9`（`v0.2.2`），本地性能分支。机器为 Core Ultra 7 251HX、18 个逻辑处理器、RTX 5060 Laptop，驱动 32.0.16.1088。工具为工作区内 .NET SDK 10.0.102 / Runtime 10.0.2、项目 Bullet 2.75 原生库和 FFmpeg n9.0.2-57-gd838f9f9ae。
- 只读加载绯英 PMX 及贴图。单角色有 37,757 个顶点、49 个材质、324 个刚体、405 个关节，软体数为 0。用固定的 90 帧头骨旋转轨道，每次推进 1/30 秒。三角色使用同一资产、不同实例和互相错开的位置，物理开启时共享 Bullet 世界，每个源帧走四个 120 Hz 固定步。首次试用模型的第一个根节点没有造成可见运动，被姿态与像素检查拦下，未纳入结果。
- 图形与物理串行测试；无窗口，GPU 会话显式排空。本文的帧耗时不含编辑器输入、交换链 Present 或显示扫描。未覆盖 PR01、耦合软体及长片编码。原始逐帧数据、日志和视频都在本地 `0Yuki/work/2026-11-19/perf-followup/`。

## 图形：围栏等待的收益集中在同步读回

A 为原始 `Thread.Sleep(1)` 轮询。B 对尚未完成且不可取消的等待调用 `SetEventOnCompletion(value, IntPtr.Zero)`，由 D3D12 阻塞到围栏完成。保留原有设备移除检查和可取消路径。两种构建使用独立目录。进程按 A/B/B/A 排列，关闭 .NET 分层编译以控制测量期间的代码切换；每个尺寸再按保留输出、读回、读回、保留输出运行。每组预热 8 帧，记录随后 32 帧。下表取每个变体四批测量的中位数。

| 尺寸与输出 | A · 整批 ms/帧 | B · 整批 ms/帧 | A → B 的绘制调用 P95（ms） |
|---|---:|---:|---:|
| 960×544，仅保留 GPU 图像 | 3.28 | 2.57 | 1.85 → 1.92 |
| 960×544，同步读回 | 15.52 | 4.74 | 14.84 → 3.79 |
| 1920×1080，仅保留 GPU 图像 | 2.90 | 2.89 | 2.41 → 2.27 |
| 1920×1080，同步读回 | 15.51 | 7.46 | 15.65 → 6.49 |

整批时间包含姿态求值、提交和最后一次 GPU 排空；绘制调用 P95 只统计 `Render` 或 `Capture` 的主机调用时长。每个尺寸的最终 RGBA 哈希在两种构建和两种输出模式下一致，保留输出每帧 0 次读回，捕获每帧 1 次。1080p 保留输出的批时间基本持平，因此交互式视口仍需单独测输入到 Present。逐帧统计还显示原版创建帧目标在 1080p 通常约 1 ms，远大于这次场景约 0.02–0.04 ms 的贴图绑定，支持把深度池安排在 SRV 改造之前。[D3D12 围栏 API 的空句柄语义](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12fence-seteventoncompletion)

## 导出：先修读回，再批量写管道

走真实 `ExportCoordinator → D3D12ViewRenderer → FfmpegFrameSink`，960×544、HEVC 软件编码、CQ 23、Balanced、无音轨。每个新进程连做两次 40 帧导出，含 156 个模拟 tick。下表区分新进程的首次导出与同进程再次导出，均计入创建会话、编码器收尾和产物验证。每帧阶段值取第 9–40 帧 P50，再对四次导出取中位数。

| 独立对照 | 每帧捕获 P50 | 每帧送入 FFmpeg P50 | 首次完整导出 | 同进程再次完整导出 |
|---|---:|---:|---:|---:|
| A → B，仅改变等待方式 | 8.44 → 4.35 ms | 3.09 → 3.55 ms | 1.925 → 1.755 s | 1.282 → 1.174 s |
| B → C，固定新等待方式，仅改变每次写入行数 | 4.19 → 4.19 ms | 3.39 → 0.70 ms | 1.732 → 1.623 s | 1.130 → 1.014 s |

第二组另跑 B/C/C/B。C 保留原有逐像素透明度合成、行跨度与顺序，只把最多 32 行装入租借缓冲后一次写入；544 行由 544 次降至 17 次。16 个视频均通过解码检查，40 个帧的时间戳与解码像素校验逐帧相同，且动作产生了 40 张不同的图像。另以产品默认的分层编译复核 A 与 C，同进程第二次导出为 1.658 与 1.320 s；这组仅用于检查收益方向。

**实施边界。** 批写可以先单独交付。实验构建通过行跨度与透明度、活动写入取消、失败清理、默认 HEVC 输出四项已有测试。围栏实验构建通过五项帧会话与 GI 对照测试。接下来用数千帧长片测吞吐、峰值内存与编码器背压；只有当捕获和写入仍各自造成流水线空闲，再试两块有界读回缓冲。可取消等待需独立设计取消、设备移除和事件柄生命周期。

## 多核与物理：蒙皮路数的收益取决于物理负载

纯 CPU 入口用 `SceneAnimationSession.Advance` 求完整姿态。每个配置新建会话，预热 8 帧，计 32 帧；按正序和倒序各跑一遍。下表给出默认分层编译下已充分运行的倒序轮，单位为 ms/源帧。固定步数与同一物理配置下的末帧顶点哈希已跨路数核对。

| 实例与共享物理 | 1 路 P50 | 4 路 P50 / P95 | 8 路 P50 / P95 | 4 路托管分配中位数 |
|---|---:|---:|---:|---:|
| 单角色，关 | 0.84 | 0.82 / 2.49 | 0.75 / 1.33 | 3.91 MiB/帧 |
| 单角色，开 | 5.44 | 5.17 / 8.88 | 5.39 / 7.92 | 4.78 MiB/帧 |
| 三角色，关 | 4.25 | 4.04 / 6.84 | 3.96 / 5.28 | 15.17 MiB/帧 |
| 三角色，开 | 21.34 | 20.56 / 25.35 | 20.70 / 22.95 | 17.77 MiB/帧 |

首次正序轮受热身影响更大。三角色物理开启时，4 路与 8 路的首次 P50 分别约 19.8 和 19.5 ms。关闭分层编译重复整套测量后，这两组为 41–42 ms 左右；无物理的三角色从 1 路到 4 路则有改善。因此保留当前默认预算，把线程数、运行时设置和场景一同记录。分配是进程所有线程的托管分配增量，不能解读成常驻内存。

**原生步进单独测了什么。** 另起默认配置进程，在已有 `MDA-PoseEvaluation` 事件之外，只在 `Native.Step` 调用前后读计时器。三角色每源帧的四次原生调用合计 P50 为 **12.33 ms**，包含它的 `boundary.world.step` 为 **14.68 ms**，完整求值为 **24.70 ms**。同轮顶点拼装为 **3.43 ms**，物理准备为 **0.90 ms**。这些阶段有嵌套，计时进程的热状态也不同。关闭分层编译时，原生步进仍为 12.45 ms，共享世界阶段增至 23.83 ms。优先做以下两项验证。

1. **按物理负载选实验。** 用同一个三角色场景加入跨角色接触，再分别采集约束求解、碰撞检测、托管目标写入与姿态回读。只有工程语义允许独立世界时才做跨世界并行。当前默认配置下，整个共享世界阶段扣去原生步进只余约 2.3 ms 的量级，里面还包括校验与结果整理；仅改 P/Invoke 批量接口可节省的部分更小。
2. **缩小 ECS 试点。** 优先查三角色每帧约 17.8 MiB 分配中，哪些顶点块必须新建、哪些只是从姿态复制到发布快照。用现有修订范围统计脏块覆盖率，比较单骨动作和全身动作，再试按角色连续存储和旧快照租约。验收同时看分配、顶点拼装 P95、完整求值 P95 以及旧快照可读性。该角色无软体，这组数据不能用于推算 Rust 软体打包的整帧贡献。

GI 另用仓库现有 4,096 三角形、96×72 变形夹具，做 16 对真实 GPU 时间戳测量。完整构建路径 P50 为 0.232 ms，已有 refit 路径为 0.083 ms，各对像素一致。该时间区间包括 GBuffer、加速结构和 GI 计算。保留现有 refit，后续在角色场景中量动态三角形上传和着色属性读取。[GI 测量范围与夹具](../../tests/Mmd.Modern.Tests/D3D12RayGiRefitMeasurementTests.cs)

## 复现

从 `0Yuki` 外层工作区启动 PowerShell，加载已有便携环境。在分支独立构建，所有运行目录使用新的工作区内路径。下面是单次基线及实验构建示例；重复测量按上文顺序更换输出目录。脚本用 MSBuild 替换实验构建的两个源文件，产品工作树保持原版实现。[测量入口](../../tools/perf/frame-study/Program.cs) · [实验源码生成脚本](../../tools/perf/frame-study/New-ProbeOverlay.ps1)

```powershell
. ./tools/env.ps1
$root = $env:MDA_WORKSPACE
$evidence = Join-Path $root ('work/2026-11-19/perf-rerun-' + (Get-Date -Format yyyyMMdd-HHmmss))
Set-Location (Join-Path $root 'wt-perf-plan')
$model = (Get-ChildItem (Join-Path $root '06_验证模型') -Recurse -Filter '星穹铁道—绯英2.pmx').FullName
$toolsDir = Join-Path $root '.dev-cache/ffmpeg-i18n/bin'
$env:ModernCacheRoot = Join-Path $root '.dev-cache/perf-rerun-A'
$env:DOTNET_TieredCompilation = '0'
dotnet build tools/perf/frame-study -c Release
$probe = Join-Path $env:ModernCacheRoot 'bin/FrameStudy/Release/net10.0/FrameStudy.dll'
dotnet $probe gpu $model (Join-Path $evidence 'A-gpu')

# B 省略 -BatchPipe；C 启用它。原生步进计时使用 -TraceBullet 和 phases 模式。
$targets = & ./tools/perf/frame-study/New-ProbeOverlay.ps1 -Directory (Join-Path $evidence 'overlay-C') -BatchPipe
$env:ModernCacheRoot = Join-Path $root '.dev-cache/perf-rerun-C'
dotnet build tools/perf/frame-study -c Release "-p:CustomAfterMicrosoftCommonTargets=$targets"
$probe = Join-Path $env:ModernCacheRoot 'bin/FrameStudy/Release/net10.0/FrameStudy.dll'
dotnet $probe export $model (Join-Path $evidence 'C-export') (Join-Path $toolsDir 'ffmpeg.exe') (Join-Path $toolsDir 'ffprobe.exe')
```

运行 `cpu` 或 `phases` 时沿用相同的模型和新目录参数。测产品默认配置时把 `DOTNET_TieredCompilation` 设为 `1`。图形测试保存最终 RGBA 哈希，视频比较用便携 FFmpeg 的 `framemd5` 输出核对顺序与解码帧。
