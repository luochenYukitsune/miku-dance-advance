# PerfBench 无头性能基准工具

对应设计文档第 5.0 节的可重复无头性能测量与 ABBA 对照工具。仅引用公开产品 API，内部统计字段通过反射按名读取并在缺失时跳过，可在未改基线（`c6f86ad`）与合并 `t01`–`t09` 后的集成分支上不改代码直接编译运行。

- **模型与资产**：通过命令行参数传入本地路径（如 `<local-assets>\characters\...`、`<local-assets>\stages\...`、`*.vmd`），绝不拷入仓库。
- **缓存隔离**：每个场景配置独立进程运行；冷打开测试在输出目录下的独立目录设置 `MMD_DXC_CACHE`、`LOCALAPPDATA`、`MMD_SHADER_CACHE`、`MMD_FXC_CACHE`、`MMD_PSO_CACHE`，绝不删除或改写共享 `.dev-cache\dxc-shader-cache`。

## 环境与编译

```powershell
$ErrorActionPreference = 'Stop'
. <workspace>\tools\env.ps1
$env:ModernCacheRoot = "$env:MDA_WORKSPACE\.dev-cache\perf-bench"
$env:TEMP = "$env:ModernCacheRoot\tmp"; $env:TMP = $env:TEMP
$env:MDA_WORK_ROOT = "$env:MDA_WORKSPACE\work\mda-work\perf\20261119\bench"
$env:MMD_FFMPEG_PATH = "$env:MDA_WORKSPACE\.dev-cache\ffmpeg-i18n\bin\ffmpeg.exe"
$env:MMD_FFPROBE_PATH = "$env:MDA_WORKSPACE\.dev-cache\ffmpeg-i18n\bin\ffprobe.exe"
New-Item -ItemType Directory -Force -Path $env:TEMP, $env:MDA_WORK_ROOT | Out-Null

Set-Location "$env:MDA_WORKSPACE\wt-perf-t00-bench"
& $env:MDA_DOTNET_EXE restore tools\perf\bench\PerfBench.csproj --locked-mode
& $env:MDA_DOTNET_EXE build tools\perf\bench\PerfBench.csproj -c Release --no-restore -m:3
```

## 单次命令

```powershell
$bench = "$env:ModernCacheRoot\bin\PerfBench\Release\net10.0\PerfBench.dll"

# 1. 采集机器信息（CPU、逻辑核、能效类、GPU 与驱动、OS、.NET、SourceCommit）
& $env:MDA_DOTNET_EXE $bench machine --out "$env:MDA_WORK_ROOT\machine.json"

# 2. 单场景播放（含打开到首帧、流水线播放、串行 submit-to-fence、跳帧与 GC）
& $env:MDA_DOTNET_EXE $bench play `
  --model "<local-assets>\characters\char1.pmx" `
  --characters 1 --gi off --size 960x544 --warmup 60 --frames 300 --serial-frames 60 --seeks 3 `
  --out "$env:MDA_WORK_ROOT\play.json"

# 3. 冷/热打开到首帧（仅跑到首帧完成；冷跑前设置空缓存目录）
& $env:MDA_DOTNET_EXE $bench open --model "<pmx>" --characters 1 --gi off --size 960x544 --tag cache=cold --out "$env:MDA_WORK_ROOT\open-cold.json"

# 4. 真实 FFmpeg 导出短片段
& $env:MDA_DOTNET_EXE $bench export --model "<pmx>" --characters 1 --gi off --size 960x544 --export-frames 60 --out "$env:MDA_WORK_ROOT\export.json"

# 5. 汇总目录与对比两个结果目录（生成 JSON + Markdown delta 对照表）
& $env:MDA_DOTNET_EXE $bench summarize --dir "$env:MDA_WORK_ROOT\run-a" --out "$env:MDA_WORK_ROOT\run-a\summary"
& $env:MDA_DOTNET_EXE $bench --compare "$env:MDA_WORK_ROOT\run-a" "$env:MDA_WORK_ROOT\run-b" --out "$env:MDA_WORK_ROOT\compare"
```

可选参数：`--stage <场景.pmx>`、`--vmd <动作.vmd>`（可多次传入，未传时自动合成确定性舞蹈动作；短于目标帧数时自动循环）、`--physics independent|shared|off`、`--switch name=value`。

## ABBA 交错驱动脚本（`Run-Matrix.ps1`）

### 1. 快速冒烟（1 角色、GI 关、960×544）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\perf\bench\Run-Matrix.ps1 `
  -Models @("<local-assets>\characters\char1.pmx") `
  -OutDir "$env:MDA_WORK_ROOT\smoke" -Smoke
```

### 2. 基线（`c6f86ad`）vs 集成分支完整 ABBA 矩阵

将 `-BaselineRepo` 指向未改基线 `c6f86ad` 所在工作树，`-CandidateRepo` 指向合并了 `t01`–`t09` 的集成工作树。脚本会自动把 `PerfBench.csproj` 分别针对两个分支的 `src/` 编译到独立缓存，按 `A / B / B / A` 顺序跑完 1/3/5 角色 × GI 关/开 × 960×544/1920×1080 ×（`play` + `open-cold` + `open-warm` + `export`），最终输出各自 `summary.{json,md}` 与 `compare.{json,md}`：

```powershell
$models = @(
  "<local-assets>\characters\char1.pmx",
  "<local-assets>\characters\char2.pmx",
  "<local-assets>\characters\char3.pmx",
  "<local-assets>\characters\char4.pmx",
  "<local-assets>\characters\char5.pmx"
)
$stage = "<local-assets>\stages\stage.pmx"

powershell -NoProfile -ExecutionPolicy Bypass -File tools\perf\bench\Run-Matrix.ps1 `
  -BaselineRepo "$env:MDA_WORKSPACE\wt-perf-baseline" -BaselineName "baseline-c6f86ad" `
  -CandidateRepo "$env:MDA_WORKSPACE\wt-perf-integration" -CandidateName "candidate-integrated" `
  -Models $models -Stage $stage `
  -Characters @(1, 3, 5) -Gi @('off', 'on') -Sizes @('960x544', '1920x1080') `
  -Rounds 2 -OutDir "$env:MDA_WORK_ROOT\compare-c6f86ad-vs-integrated"
```

也可直接传入预先编好的 `-BaselineDll` 与 `-CandidateDll`，或加 `-Vmd @("<motion.vmd>")` 指定外部动作文件。

## 指标说明

表情一致性可在独立的 `play` 轮次加 `--morph-audit morph.bin`。每帧先写完整 CPU 顶点流（含 UV）的 SHA-256，再写变形区间数。每个区间记录起始顶点、顶点数、GPU 表情索引数（-1 表示无表情），存在表情时再写升序索引、位置偏移和 UV 偏移三段原始字节的合并 SHA-256。整数为小端 int32，摘要为 32 个字节。相同模型和动作可直接对比整份文件。此选项会强制生成 CPU 顶点，帧耗时应取关闭审计的单独轮次。

- **姿态求值（`play.poseMs`）**：每源帧 `SceneAnimationSession.Advance` 的 P50 / P95 / P99（含动画、IK、物理、场景合并）。
- **渲染 CPU 录制（`play.renderCallMs` / `play.stat.RecordCpuMilliseconds`）**：宿主 `Render` 调用时长与渲染器统计的录制 CPU 时长。
- **GPU 帧耗时**：若公开 `Statistics` 暴露时间戳字段（`GpuFrameMilliseconds` / `GpuMilliseconds`）则直接读取，否则以串行 `serial.submitToFenceMs`（提交到完整围栏等待）作为 GPU 耗时代理。
- **完成帧与帧率（`play.frameMs` / `play.fps`）**：流水线播放下完整帧间隔 P50 / P95 / P99 与批量实测 FPS。
- **GC（`play.gc.*`）**：窗口期内每帧托管分配字节，以及每百帧 Gen0 / Gen1 / Gen2 回收次数。
- **冷/热打开与跳帧（`open.toFirstFrameMs` / `seek.ms`）**：从工程模型载入、会话/物理初始化、设备创建到首帧完成耗时；跳帧含 `Seek` + 物理稳定 + 渲染完成。
- **导出（`export.run0` / `export.run1`）**：同进程连续两次真实 FFmpeg HEVC 软件编码导出的总耗时、平均帧率及跳过前 1/4 启动区间的稳态帧率。
