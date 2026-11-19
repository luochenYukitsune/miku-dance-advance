# 图形性能与多核调度优化方案（2026-11-19 修订）

2026-11-19 用项目内的便携 `gh` 核对上游 `luochenYukitsune/miku-dance-advance`。GitHub 的 `main`、发布标签 `v0.2.2` 和本地方案提交 `cd27054` 的父提交均为 `2abe9a9`。目标分支在本地工作树 `wt-perf-plan`。代码位置以 `2abe9a9` 为准。第 5.9 节测了 Rust 打包，第 5.10 节用真实 PMX 验证了共享物理、GPU 读回与 HEVC 导出。

范围和已定的前提：

- 四类症状都要处理：播放帧率低、界面卡顿和跟手差、导出慢、打开和跳帧慢。
- 光追 GI 一并纳入。
- **预览和导出都改用 GPU 蒙皮**（用户 2026-11-18 决定）。GPU 结果和现在的 CPU 结果不要求逐字节一致，验收方式见第 4 节。
- 不动 Classic 引擎、MMD 原版场景光照和已认可的画面效果。不加画质开关来换速度，也不靠降画质过测试。

## 1. 结论

先用一条完成帧的时间线区分 UI 求值、CPU 录制、GPU 执行与输出等待。当前实现有以下可验证的开销。

1. **动画、IK、物理和场景合并都在 UI 线程上执行。** 播放由 UI 线程上 16ms 的 `DispatcherTimer` 推进，取帧快照也在 UI 线程。模拟一慢，面板、时间轴和输入就一起卡。
2. **大范围变形会让多段处理重复访问顶点。** CPU 蒙皮后，场景拼装、描边、包围盒和上传均有复用路径。全身运动时应同时量变形顶点数、实际重建的块数与上传字节；仅改灯光或局部表情的帧可作对照。
3. **常规预览的快照要等交换链许可。** 非 GI 路径缺少当前硬件 GI 配置可用的提前录制机会。用同帧时序检查这段等待能否与 CPU 工作重叠。
4. **D3D12 层有几处常见的低效用法。** 输出和深度目标等资源每帧新建，围栏等待用 `Thread.Sleep(1)` 轮询，所有工作都挤在一个 Direct 队列和一条命令列表上。
5. **导出完全串行。** 模拟、渲染、同步读回、写管道逐帧依次执行，每帧还要新分配一整帧大小的 `byte[]`。
6. **并行各管各的。** 蒙皮、合并、描边、包围盒、GI 写入各自调用 `Parallel.For`，上限分别是 4、16 或 `ProcessorCount`，都抢同一个线程池，没有统一的优先级和预算。
7. **打开和跳帧时有同步等待。** 贴图串行解码；首帧时才上传贴图、编译着色器（每次编译启动一个 `dxc.exe` 进程）；跳帧后要做物理预热。

播放时优先验证 UI 求值与顶点变形是否占据关键路径；导出单独看 GPU、读回与编码之间的空闲。先选可以独立对照的改动，顺序见第 8 节。

## 2. 现状证据

| 问题 | 位置 | 说明 |
|---|---|---|
| UI 线程推进播放 | `src/Mmd.Modern.App/EditorShellWorkspace.cs:11`、`:243-267` | 16ms `DispatcherTimer`，`PlaybackTick` 里调用 `session.AdvancePreview` |
| UI 线程取快照 | `src/Mmd.Modern.App/EditorViewportHost.cs:322-347`、`:521-528` | 等到交换链许可后，以 Background 优先级投递到 UI 线程执行 `CaptureRenderSnapshot` |
| 只有 GI 能提前录制 | `src/Mmd.Modern.App/EditorViewportHost.cs:297-299` | `CanRecordAheadLocked` 要求硬件光追 GI，并且关闭时序超分 |
| CPU 蒙皮、每帧新数组 | `src/Mmd.Modern.Animation/CharacterAnimator.cs:19-20`、`:288-306` | 每个顶点 32 字节；3.2 万顶点以上才并行，默认 4 路 |
| CPU 描边位置 | `src/Mmd.Modern.Rendering.D3D12/D3D12OutlinePositionCache.cs:30`、`:80` | 同引用可直接返回；宽度与模型基值一致时按变更范围检查，并可复用输出块 |
| CPU 包围盒归约 | `src/Mmd.Modern.Rendering.D3D12/D3D12VisibilityBoundsCache.cs:306`、`:453` | 已有增量失效、稀疏扫描和按材质并行归约；用 `LastScannedIndices`、`LastRebuiltLeaves` 区分局部与整材质重算 |
| GI 几何准备 | `src/Mmd.Modern.Rendering.D3D12/D3D12HardwareRayGi.cs:339`、`:373`、`:432` | 已支持从三角形缓冲在 GPU 上提取位置、CPU 备选路径及 BLAS refit。分别计量三角形属性上传、位置提取与加速结构构建 |
| 每帧新建渲染目标 | `src/Mmd.Modern.Rendering.D3D12/D3D12FrameRenderer.cs:372-381` | 输出和深度每帧 `CreateCommittedResource`；Exposure、AlphaResolve、Diffusion、Fsr1、CharacterTemporalInputs 等 pass 也每帧新建资源和描述符堆 |
| 每帧重建 SRV | `src/Mmd.Modern.Rendering.D3D12/D3D12FrameRenderer.cs:390-406` | 所有贴图的 SRV 每帧重新创建 |
| 围栏轮询 | `src/Mmd.Modern.Rendering.D3D12/D3D12GpuRuntime.cs:248-256` | `Thread.Sleep(1)` 循环。实测围栏完成至 CPU 恢复的延迟分布，平台计时精度和系统负载都会影响结果 |
| 单队列、单命令列表 | `src/Mmd.Modern.Rendering.D3D12/D3D12GpuRuntime.cs:204-207` | 一个 Direct 队列，3 个录制槽，每帧一条命令列表 |
| 贴图无 mip | `src/Mmd.Modern.Rendering.D3D12/D3D12GpuCache.cs:222`、`:251` | `mipLevels:1` |
| 导出串行 | `src/Mmd.Modern.ViewOutput/ExportCoordinator.cs:7`、`:93-102`；`src/Mmd.Modern.Application/OfflineFrameRunner.cs:132` | 一次只持有一帧 |
| 同步读回、每帧分配 | `src/Mmd.Modern.Rendering.D3D12/D3D12FrameRenderer.cs:593`、`:622` | 每帧新建读回缓冲，提交后马上等待，再新分配 `byte[]` |
| 编码管道逐行写 | `src/Mmd.Modern.ViewOutput.FFmpeg/FfmpegFrameSink.cs:45-60` | RGBA 原始数据，颜色空间转换交给 ffmpeg 在 CPU 上做 |
| 着色器编译走进程 | `src/Mmd.Modern.Rendering.D3D12/DxcShaderCompiler.cs:207-224` | 有内存和磁盘缓存，但缓存未命中时每次编译都要启动一个进程 |
| 贴图串行解码 | `src/Mmd.Modern.Formats/PmxSceneAdapter.cs:28` | 每个模型按顺序逐张加载贴图 |
| 物理单线程 | `native/Mmd.Modern.Bullet/mmd_bullet.cpp:211` | `stepSimulation(seconds, 0)`，由托管侧按 120Hz 驱动 |
| 运行时没有调优 | 全部 `*.csproj`、`Directory.Build.props` | 没有 GC 模式、线程优先级、计时器精度、ReadyToRun 等设置 |

历史实测（0.1.24–0.1.28，PR01 单角色，i7-10875H 和 RTX 2080 Super Max-Q；当前 `main` 没有重测，只用来判断量级）：

- 物理世界步进约占播放推进耗时的 57%，物理准备约占 25%。
- 完整姿态（含 4 路蒙皮）约 0.85ms；表面包围盒归约约 1.2ms，描边包围盒约 2.6ms。
- GI 三角形上传约 67MiB，约 7.8ms。
- 不开 GI 时播放约 24fps，硬件 GI 16 采样时约 11.5fps。

**旧标签的处理。** [更新日志](../../CHANGELOG.md) 已记录 0.1.28 的共享物理准备列表、重复 Bullet 模式调用消除和包围盒块复用，以及 0.1.29 的场景拼装、表情姿态复用和分块多核蒙皮。到 `v0.2.2`，`D3D12FrameRenderer.GetGeometry` 也会对有变更记录的场景走按槽局部更新。下轮保留这些路径作对照，分别统计静止、局部编辑和全身运动的命中率与字节数。未命中占主导才值得改数据生产端。

## 3. 参考的成熟做法

| 来源 | 做法 | 在本方案里的对应 |
|---|---|---|
| Unreal Engine | 游戏线程、渲染线程、RHI 线程三级流水线，游戏线程最多领先渲染一帧；GPU Skin Cache 用 compute 做一次蒙皮，光栅、运动矢量和光追 BLAS 都读这一份结果；RDG 管理临时资源并做显存别名复用 | 第 5.3 节模拟线程、第 5.2 节 GPU 蒙皮、第 5.1 节临时资源池 |
| Unity | 主线程加 Job System，按依赖图调度工作线程；独立渲染线程；可选 compute 蒙皮 | 第 5.5 节统一调度 |
| Frostbite FrameGraph（GDC 2017） | 按帧声明 pass 和资源，自动推导生命周期并复用显存 | 第 5.1 节临时资源池，做简化版即可 |
| Naughty Dog（GDC 2015） | 以帧为单位的多帧流水线，各阶段在不同帧上并行，靠严格的数据所有权避免加锁 | 第 5.3 节：本项目的不可变快照已经具备这个条件 |
| Blender | 依赖图并行求值；物理点缓存（point cache），跳帧时直接读缓存 | 第 5.7 节物理缓存（待定） |
| NVIDIA、AMD 的 D3D12 实践指南 | 不要每帧创建资源；用事件等待围栏；上传走 Copy 队列；独立的计算工作放到 async compute | 第 5.1 节、第 5.6 节 |
| OBS、UE Movie Render Queue | 多块读回缓冲轮转，编码在独立线程；在 GPU 上先转换成 YUV 再读回 | 第 5.4 节 |

## 4. GPU 蒙皮的验收标准（已定：预览和导出都用 GPU）

- **参照**：CPU 蒙皮保留，用作测试参照，同时继续给软体和 Classic 使用，不删除。
- **顶点**：同一姿态下，GPU 和 CPU 的每个顶点位置误差不超过 1e-4 单位（1 单位约 8cm），法线夹角误差不超过 0.01°，UV 不超过 1e-6。
- **画面**：固定测试场景（含 SDEF、QDEF、顶点表情、UV 表情、描边、贴得很近的面片）在 GPU 和 CPU 两条路径下出图，逐像素比较。阈值先用实测确定，再写进测试，之后不放宽。
- **已有测试**：逐字节比较 CPU 结果的测试继续保留，只用来验证 CPU 路径。新增的 GPU 路径测试使用上面的容差。
- **显式选择**：带软体的模型继续走 CPU 蒙皮。这是按模型显式选定的路径，界面和诊断里能看到，不是静默回退。

## 5. 方案

### 5.0 第 0 阶段：建立测量基线（先做）

不先量就无法判断收益，也容易重复前几轮“范围重叠、不能下结论”的情况。

- 固定测试场景：1 个、3 个、5 个角色；GI 关和开；960×544 和 1920×1080。模型用本地外部验证模型目录，不提交，也不上传。
- 指标：
  - 播放：完成帧率，帧时间 P50 和 P95。
  - 界面：UI 线程繁忙占比，输入到画面的延迟（复用现有 `InteractionDiagnostics`）。
  - GPU：每个 pass 的 GPU 时间（复用 `D3D12CaptureTiming` 的时间戳查询，加 PIX 标记）。
  - 打开和跳帧：首帧时间，跳帧到出新画面的时间。
  - 导出：每秒完成帧数，CPU 和 GPU 占用。
- 只走无头方式，产物放在 `MDA_WORK_ROOT/perf/<日期>/<任务>/`，本机 RTX 5060 串行使用 GPU。
- 每个阶段完成后用同一套场景和指标对照，ABBA 排列，至少跑两轮。

先从单角色播放、三角色共享物理播放和同工程离线导出各取一段固定时间轴。同帧关联 `InteractionDiagnostics`、`PoseEvaluationMetrics`、录制统计、提交围栏与 GPU 时间戳。父计时已包含子阶段，跨帧重叠的工作也分别标记。输入延迟以同一输入对应的首次 Present 提交为软件端点；UI 回调只说明消息被处理。冷启动和缓存命中分开报告。固定驱动、电源模式、采样与分辨率，记录采集自身开销。进入实现的条件是瓶颈出现在目标场景的关键路径，对照差值超过重复运行间的波动。

### 5.1 第 1 阶段：D3D12 层低风险修正（只改 `Rendering.D3D12`）

1. **围栏等待改成事件**：`SetEventOnCompletion` 加 `WaitHandle`，替换 `Thread.Sleep(1)` 轮询，并保留取消支持。
2. **临时资源池**。沿用 `D3D12FrameDescriptorHeaps` 已有的三个围栏保护槽，先池化 `$depth`，再选分配次数最高的中间纹理。按尺寸、格式、采样、资源标志及用途匹配，保存资源状态。最后一次使用的围栏完成后方可覆盖；对 `$output` 还须等 `LastGpuOutput` 的外部消费者释放或提供完成信号。录制中止与 resize 分别回收未提交资源和旧代资源。用延迟消费旧画面的测试检查提前覆盖。
3. **贴图 SRV 复用**。在现有每槽 shader-visible 堆中缓存槽位的贴图身份、视图格式和堆代数，仅在内容或布局变化时写入。堆扩容后重填；同一提交内不同 GI 采样引用的槽位继续独立。用 `TextureBindingMilliseconds` 和每帧 SRV 写入数验收。
4. **读回缓冲复用**：按尺寸缓存读回缓冲，不再每次新建（导出流水线见 5.4）。

验收：画面逐字节不变（这一阶段不改任何着色计算）；每帧 `FrameOwnedAllocations` 和 `CreateCommittedResource` 次数在稳态下降到 0 附近；帧时间不能变差。

### 5.2 第 2 阶段：GPU 蒙皮（Core 扩展接口，现代引擎实现）

**数据约定（Core）**
- 角色快照在“已变形顶点”之外，增加一条“变形输入”通道：骨骼蒙皮矩阵、SDEF 和 QDEF 需要的数据、顶点表情和 UV 表情的稀疏增量，加上不可变的绑定姿态引用。
- 绑定姿态和权重每个模型只上传一次。
- Core 只描述数据，不出现任何 GPU 类型。
- 在现代引擎选择 GPU 蒙皮的情况下，Core 跳过逐顶点蒙皮和合并拷贝，只求骨骼和表情。Classic 和软体模型仍拿到已变形的顶点。

**现代引擎（`Rendering.D3D12`、`ViewOutput.D3D12`）**
- 每个角色一个 compute pass：先加表情增量，再做 BDEF、SDEF、QDEF 蒙皮，输出到场景顶点缓冲里对应的区段。同时保留上一帧的结果，供运动矢量、时序抗锯齿和超分使用。
- 描边在同一个 pass 里按法线挤出，或者在描边顶点着色器里读蒙皮结果，取代 `D3D12OutlinePositionCache`。
- 剔除先试保守包围盒。用 CPU 变形顶点作包含关系对照，覆盖表情极值、SDEF/QDEF、骨骼缩放及描边扩张；无法保证包住时沿用精确归约，或对该批次关闭剔除。
- GI 分两步迁移。先让 BLAS 使用蒙皮后的位置与模型索引，几何描述和 refit 兼容键随拓扑更新；再让命中着色获取正确的法线、UV、材质与图元映射。现用的 `RayGiTriangleGpu` 为 160 字节，去掉位置提取仅节省其中一部分工作。分别记录动态上传字节、refit / rebuild 次数和 GPU 时间。

**不变的部分**
- Classic 引擎和软体模型继续走 CPU。
- 保留刚体与骨骼的固定步交互顺序；耦合软体还读取蒙皮位置，继续使用 CPU 结果。
- 导出和预览走同一条 GPU 路径（这是已定的前提）。

验收按第 4 节核对；同时检查 3 角色和 5 角色的 CPU 帧时间、GI 动态属性上传与 BLAS 几何更新。分开报告位置和完整三角形属性的传输量。

### 5.3 第 3 阶段：模拟移出 UI 线程（Application 和 App）

目标是 UI 线程只处理输入、布局和面板，播放时的动画和物理在专用的模拟线程上运行。

1. **播放所有权租约**：开始播放时，UI 把工程会话的求值权交给模拟线程。播放期间，UI 的编辑操作通过命令队列送到模拟线程按顺序执行（或者按现有规则禁止），暂停时再把求值权交回。这样不用一次改掉 `SceneProjectSession` 的单线程约定，风险可控。
2. **时钟**：模拟线程用高精度计时，或者以音频时钟为准推进源帧，不再依赖 16ms 的 `DispatcherTimer`。30fps 源时间轴和 120Hz 物理网格保持不变。
3. **发布不可变帧**：模拟线程每推进一次就发布一份不可变快照（现在的快照本来就是不可变的），视口渲染线程取最新的一份，UI 只读用于显示的少量状态（当前帧号、选中骨骼等）。
4. **三级流水线**。从关闭时序超分的非 GI 预览开始，模拟最多领先一个可展示快照，交换链仍保持 `MaximumFrameLatency = 1`。丢弃或取消的录制不得提交上一帧姿态或时序历史。跳帧、resize、相机切换均应使旧记录失效；GI 和时序超分逐模式验证后再扩大范围。
5. **跟手**：拖动骨骼或灯光时，直接以最新输入采样，跳过排队中的旧帧。现有的 `poseFrameAdmission` 逻辑保留。

验收：
- 播放时 UI 线程繁忙占比明显下降，面板和时间轴操作不掉帧。
- 输入延迟 P95 不变差。
- 跳帧、Undo、暂停的行为和现在一致，相关测试全部通过。
- 导出不受影响：导出本来就独占会话，不走 UI 线程。

### 5.4 第 4 阶段：导出流水线（ViewOutput 和 ViewOutput.FFmpeg）

1. **多帧在途**：模拟第 N+1 帧和 GPU 渲染第 N 帧重叠进行。读回用 2 到 3 块缓冲轮转，围栏完成后再映射。
2. **独立编码线程**：编码写入放到单独的线程，通过有界通道传递帧。帧缓冲用池复用，不再每帧新分配 `byte[]`。
3. **GPU 颜色转换**：可选在 GPU 上直接转成 NV12 或 YUV420 再读回。1080p 下每帧读回量从约 8MB 降到约 3MB，ffmpeg 也省掉 CPU 上的颜色转换。转换矩阵和范围要和现在 ffmpeg 的结果一致，用测试核对。
4. **确定性**：模拟顺序、120Hz 网格、输出帧序号都不变，只让不同阶段重叠执行；取消和失败时等所有在途帧结束后再清理。

验收：同一工程导出的视频逐帧和现在一致（GPU 蒙皮带来的差异按第 4 节单独核对）；每秒完成帧数提高；取消、编码失败、显存不足等错误路径的测试全部通过。

### 5.5 第 5 阶段：统一调度和 CPU 侧细化

1. **先在一个调用链试共享预算**。由 Application 的播放会话持有每帧预算，传给角色求值与蒙皮分块；外层已用完配额时内层顺序执行，调用线程参与工作。`Mmd.Modern.Scheduling` 目前承担语义时间与回调顺序。预览先比较 1、2、4、8 个工作者与当前默认值，记录就绪到开工的 P95、同步等待和输入延迟；导出可独立选择吞吐最好的预算。
2. **物理**：
   - 互相没有关联的独立物理世界（不共享世界的模型）可以并行步进。
   - 共享世界内部的并行化需要 Bullet 的多线程构建（`BT_THREADSAFE` 加 `btDiscreteDynamicsWorldMt`），会改变求解顺序，可能影响结果的确定性。这一项只做评估，不直接上线，需要另外决定。
3. **GC 和运行时**：
   - 第 2、4 阶段之后，每帧大块分配会少很多。再评估播放期间使用 `GCLatencyMode.SustainedLowLatency`。
   - 评估 ReadyToRun 是否能缩短启动时间。
   - 保持普通线程优先级做基线。仅当跟踪显示就绪线程长期得不到运行时，再单独实验提高优先级，复测输入与音频延迟。

### 5.6 第 6 阶段：GPU 进阶（含光追）

1. **Async compute**：GI 的降噪、时序累积和空间累积放到计算队列，和光栅部分重叠执行；GPU 蒙皮也可以提前在计算队列上跑。
2. **Copy 队列**：贴图和静态几何的上传放到 Copy 队列，打开工程时在后台完成，不占首帧。
3. **PSO 缓存**：使用 `ID3D12PipelineLibrary` 把管线状态缓存到磁盘，缓存目录跟 DXC 缓存放在一起。
4. **Mipmap**：贴图生成 mip 链可以降低带宽、减少远处闪烁，但会改变画面。要先核对 MMD 原版加载贴图时是否生成 mip，和原版一致才做，单独决定（见第 7 节）。

### 5.7 打开和跳帧

1. **贴图并行解码**：多个模型、多张贴图并行解码，受统一预算约束；解码结果按路径和内容哈希缓存。
2. **着色器预热**：打开工程后在后台按需要的变体提前编译，不等首帧。可以评估改用进程内的 DXC（新增依赖，需要核对许可证并锁定版本），省掉每次启动进程的开销。
3. **首帧不等全部就绪**：先出低优先级资源齐全前的画面，还是保持现在“齐了再出”，属于产品取向，待定。
4. **跳帧的物理预热**：现有的 120Hz 预热沿用 saba 的做法，不改。可以考虑加物理缓存（类似 Blender 的 point cache）：播放过的区间缓存刚体状态，跳到缓存区间内时直接恢复。这会改变跳帧时的物理结果来源，待定。

### 5.8 ECS 试点与多核依赖

**建议先试共享物理的运行时数据布局。** 场景已经使用 `Entry[]`。在 `SceneAnimationSession.Index(Guid)` 仍可见线性查找；`PrepareSharedBoundaryGraph` 已为一次固定时钟操作预建索引、父子顺序和绑定分组，`EvaluateSharedBoundary` 的耦合软体预览还会按角色筛选父绑定、查找父对象。先量这些操作在 120 Hz 边界出现的次数、分配和耗时。只有几个角色时，一次字典查找也可能比顺序扫描贵。[共享物理实现](../../src/Mmd.Modern.Application/SceneAnimationSession.SharedPhysics.cs)

| 试验 | 实现范围 | 如何决定去留 |
|---|---|---|
| A · 复用现有绑定图 | 从已验证的角色顺序派生连续槽号，以数组访问父槽和绑定范围。缓存键包括角色增删、外部亲子关系及控制轨道当前有效区间。每步仍采样父骨矩阵、冻结状态、IK 和物理输入。 | 逐步比较父子姿态、冻结锚点、碰撞与异常顺序。若 A 已消除查找开销，保留 A 即可。 |
| B · 局部 ECS 数据布局 | 以同一个槽号索引热列（本步状态、父槽、姿态偏移）；GUID、工程对象与撤销历史保留在编辑层。拓扑变更在模拟边界发布新版本，旧快照持有旧版。和 A 在同一线程数、相同求值顺序下比较，以隔离布局收益。 | 记录每步分配字节、准备时间和整帧 P95，兼测单角色与频繁编辑。若收益落入噪声或缓存维护拖慢编辑，停止扩展。 |
| C · 按依赖并行 | 在胜出的布局上，按父子拓扑层分发互不依赖的角色准备任务。工作者写私有结果；按原顺序提交目标到同一 Bullet 世界，世界步进一次，再按依赖应用姿态并发布快照。 | 比较 1/2/4/8 工作者；覆盖跨角色挂骨、共享碰撞、耦合软体、取消及工作者错误。检查每个固定步与输出帧序号。 |

已有 `PrepareInitialControllers` 和实验性的 `CompletePreviewLeavesParallel` 可提供并行与串行对照。自定义集合读取可能有副作用，初始化工厂也可修改后续角色；沿用现有资格检查、错误报告顺序和取消语义。预览可丢弃过时的显示快照，每一个物理步及离线输出帧仍须按序完成。Unity 的 archetype chunk 解释了连续存放相同组件的访问优势，Job 依赖解释了何时需要同步。这里分别用 B 和 C 验证这两种收益。[Unity Entities 数据布局](https://docs.unity3d.com/Packages/com.unity.entities@1.0/manual/concepts-archetypes.html) · [Unity Job 依赖](https://docs.unity3d.com/Manual/job-system-job-dependencies.html)

**图形侧也可独立试一次小改动。** `D3D12OriginalStyleRenderer` 的表面循环逐材质计算逆转置之外的部分帧常量、设置 PSO 和描述符表。若 `SceneRecordCpuStatistics` 指向此循环，先把相机矩阵、眼点和归一化灯光方向提到循环外，再试跳过相邻且相同的绑定。保留材质提交、透明混合和描边顺序；用多材质场景比较录制时间、绑定次数、draw 数和逐像素结果。GPU 时间主导时则先量各 pass 与依赖间隙，只有出现可重叠工作才试第 5.6 节的队列拆分。资源池按最后一次 GPU 使用退休的设计参考 D3D12 围栏，后续跨 pass 生命周期可参考 UE RDG。[表面录制代码](../../src/Mmd.Modern.Rendering.D3D12/D3D12OriginalStyleRenderer.cs) · [D3D12 资源与围栏](https://learn.microsoft.com/en-us/windows/win32/direct3d12/fence-based-resource-management) · [Unreal Render Dependency Graph](https://dev.epicgames.com/documentation/en-us/unreal-engine/render-dependency-graph-in-unreal-engine)

### 5.9 Rust 实测：耦合软体的目标点打包

`BulletCharacterPhysicsBackend.SoftBodies.Points` 按组件索引读取蒙皮顶点，检查 XYZ 有限，再把坐标交给原生 Bullet。带软体的模型在第 5.2 节之后仍需要这段 CPU 工作。取单个组件，以三种实现处理相同数据：照现有循环分别写三个 float 的 C#，一次写入 Vector3 的 C#，以及每组件一次 P/Invoke 的 Rust。三者都复用输出区，借此单独测打包；实际产品的数组分配、组件遍历和 Bullet 步进需在集成时另计。[实际入口](../../src/Mmd.Modern.Physics.Bullet/BulletCharacterPhysicsBackend.SoftBodies.cs) · [C# 对照](../../tools/perf/soft-targets/compare.cs) · [Rust 内核](../../tools/perf/soft-targets/pack.rs)

本机 Core Ultra 7 251HX，Windows x64，.NET SDK 10.0.102 / Runtime 10.0.2，Release，分层编译开启，rustc 1.90.0，通用 x64 优化。每次批调用处理表中的全部点，索引固定种子乱序，预热后轮换三种实现的六种先后顺序。每组 24 个批次样本，独立启动三次。表中是三次 P50 的中位数，单位为微秒/组件；Rust 含固定数组和 P/Invoke。有效数据按字节核对，另查越界索引和 NaN。

| 每组件点数 | C# 逐坐标写 | C# 一次写 Vector3 | Rust | Rust 相对改进版 C# 节省 |
|---:|---:|---:|---:|---:|
| 256 | 0.250 | 0.201 | 0.169 | 0.032 µs |
| 4,096 | 4.178 | 3.299 | 3.101 | 0.198 µs |
| 32,768 | 40.873 | 31.775 | 29.669 | 2.106 µs |

**采用决定。** 大组中 C# 改写已节省约 9.1 µs，Rust 在此基础上再省约 2.1 µs。该组两者的批次 P95 跨进程范围分别为 36.1–43.4 µs 和 35.5–55.0 µs，尾部没有稳定优势。先在软体实景记下每步组件数、打包总时间和分配量，再验证托管写法与每组件缓冲复用。仅当残余打包时间仍影响完成帧，才将 Rust 接入相同调用链复测，计入封送、所有权及发布成本。刚体场景另行统计 `SetKinematicTarget` / `GetBodyPose` 的单体跨界次数；若边界成本显著，先在现有 Bullet C++ 桥试按世界批量读写。

交付前另起进程复核一次，32,768 点的三个 P50 依次为 48.006、39.172、36.086 µs。顺序一致，绝对值随运行环境波动。复现时从仓库根在可使用 x64 MSVC 链接器的 PowerShell 中执行。输出依次是点数、实现、批次均值的 P50 与 P95（µs/调用）。

```powershell
$probeDll = Join-Path $env:ModernCacheRoot 'mda_soft_target_probe.dll'
rustc --edition 2021 -C opt-level=3 -C panic=abort --crate-type cdylib tools/perf/soft-targets/pack.rs -o $probeDll
$env:DOTNET_TieredCompilation = '1'
dotnet run -c Release --file tools/perf/soft-targets/compare.cs -- $probeDll
```

### 5.10 真实 PMX 对照与实施取舍

在 RTX 5060 Laptop 上，1080p 的同步读回从 **15.51 降至 7.46 ms/帧**；相同场景仅保留 GPU 输出时为 **2.90 与 2.89 ms/帧**。导出端将 32 行合为一次写入，把 FFmpeg 写入 P50 从 **3.39 降至 0.70 ms/帧**。三角色共享物理时，预热后的默认 .NET 配置下，把蒙皮从 4 路增至 8 路，求值 P50 为 **20.56 与 20.70 ms**。这些结果将无取消令牌的围栏等待和导出批写提前到第一批实现；物理与 GPU 蒙皮继续分开验证。详细对照、尾延迟、复现入口和下一轮可行性见[真实模型测试报告](performance-measurements-2026-11-19.md)。

### 5.11 补充项：阴影、GI 历史、选卡、播放时钟与界面刷新

2026-11-19 补查。平台行为用[平台探针](../../tools/perf/platform-probe/Program.cs)核实，只用来确认机制。各项都不按某一台机器写死阈值、线程数或采样数；验收至少对照两类配置，例如独显台式机，以及混合显卡、混合架构 CPU 的笔记本。

本机探针结果（Core Ultra 7 251HX，6 个性能核加 12 个能效核；RTX 5060 Laptop 加 Intel 核显；每项采样 4 秒）：

| 项目 | 默认 | 进程内调用 `timeBeginPeriod(1)` 后 |
|---|---|---|
| 16ms `DispatcherTimer` 的间隔 P50 / P95 | 29.97 / 32.82 ms，每秒 38.9 次 | 29.93 / 33.10 ms，每秒 38.8 次 |
| `Thread.Sleep(1)` P50 / P95 | 15.52 / 16.11 ms | 1.99 / 2.14 ms |
| 显卡枚举 | `EnumAdapters1` 第一块是 RTX 5060，与高性能偏好顺序一致 | — |

**1. 附加灯阴影（`Rendering.D3D12`）。** 每帧都重画所有投影灯的所有面：聚光和平行光 1 面，点光 6 面，面光 24 面（6 面乘 4 个采样）。每个面都画全部投影材质，不做按面剔除；灯和场景都没动、只转相机时也照样重画（`D3D12NativeSceneLights.cs:143-167`）。
- 先做按面视锥剔除，复用现有材质包围盒，包围盒只允许偏大。
- 再做缓存：灯参数、阴影投影和静态投射物都不变时，复用上一帧的图块，只重画会动的投射物。图集要分成静态深度和动态叠加两层。参考 UE 可移动灯的阴影缓存，以及 Unity HDRP 的缓存阴影和按需更新。
- 验收：阴影图块和最终画面逐字节一致；记录每帧阴影 draw 数和 GPU 时间。

**2. GI 时序历史（`Rendering.D3D12`，光追）。** 现在视角、近远平面、材质、UV 或灯光任一项变化，就清空整帧历史（`D3D12RayGiTemporalPass.cs:75-125`）。MMD 镜头动作常带视角变化，表情也常用材质变形，所以播放时时序累积基本不起作用。默认每像素 16 个采样（`Mmd.Modern.Core/RayGiOptions.cs:20`）。
- 改为逐像素判定：用上一帧投影重投影，按深度和法线判断失效；视角、材质、灯光变化时缩短历史长度或做邻域钳制，不整帧清空。UE（TSR、Lumen）和 Unity HDRP 的时序降噪都是这样处理的。
- 历史能稳定保留以后，预览才能降低每帧采样数。另一个可选项是预览半分辨率追踪，再按深度和法线上采样（Unity HDRP 的 RTGI 有半分辨率选项）。
- 这两步都会改变预览画面，需要用户决定（第 7 节）。导出暂时保持现有的全分辨率和重置规则。

**3. 显卡选择（`Rendering.D3D12`）。** 运行时取 `EnumAdapters1` 返回的第一块硬件显卡（`D3D12GpuRuntime.cs:193-196`，`D3D12MeshRenderer.cs:205`）。混合显卡笔记本工作在混合模式时，第一块通常是核显。
- 改用 `IDXGIFactory6.EnumAdapterByGpuPreference(HighPerformance)`；接口不可用时沿用现有循环，WARP 的现有行为不变。
- 预览和导出都通过 `D3D12GpuRuntime` 创建设备，选卡结果一致；诊断里继续记录显卡名和 LUID。
- 验收：在混合模式的笔记本上选到独显；本机这类第一块已经是独显的机器，结果不变。

**4. 播放时钟（Application、App）。** 实测 16ms 的 `DispatcherTimer` 每秒只触发约 39 次，进程内提高计时精度后也一样。所以不管 GPU 多快，预览姿态每秒最多更新约三四十次。
- 这一点支持第 5.3 节第 2 条：播放推进不再依赖 UI 定时器。
- 在模拟线程完成之前，可以先用视口的帧许可驱动：每拿到一次许可，就按实际经过的时间推进，然后取快照。UI 定时器只保留给界面刷新。
- 验收：在两类配置上，推进间隔都跟随显示刷新或模拟节拍，而不是固定在定时器粒度上。

**5. 时间轴播放头（App）。** `CurrentFrame` 被设成了时间轴的 `AffectsRender`（`Controls/EditorTimeline.cs:105`）。播放时每过一个整数帧，就把全部轨道、刻度文字和关键帧重画一遍。历史实测 8192 个关键帧时重画一次要 21–23ms（150% DPI 下 55–57ms），全在 UI 线程。
- 播放头拆成独立的叠加控件。关键帧层只在轨道、滚动、缩放或选择变化时重画，参考 UE Sequencer 的做法。
- 验收：无头 Skia 合成截图和现在逐像素一致；播放期间关键帧层的 `Render` 次数为 0。

**6. 面板刷新（App）。** 每次 tick 都会调用 `RefreshViewport`（`EditorWindow.axaml.cs:193-215`）。灯光页打开时，它会给每个模型重建一遍骨骼名数组（`EditorWindow.Lighting.cs:145`）；`LoadPose` 每个整数帧都对整份动作的关键帧做线性扫描和字符串比较（`EditorAnimationViewModel.cs:513`）。
- 内容版本没变时跳过重建。关键帧按骨骼建索引。
- 播放期间数值面板限制刷新频率，暂停后立即刷新精确值。Unity 的 Inspector 和 UE 的 Details 面板在运行时也是限频刷新。
- 验收：对比 `InteractionDiagnostics` 里 `lighting.inspector-refresh` 和 `ui.load-pose` 的次数与耗时。

**7. 拖动刻度尺时的跳帧（App 调度，Application 执行）。** 指针每移到一个新帧，就在 UI 线程同步执行一次完整的 `Seek`（`Controls/EditorTimeline.cs:293`，`EditorShellWorkspace.cs:194-199`）。产品设置下每次跳帧都要重建候选会话，并做 120 步物理预热（`EditorWindow.Diagnostics.cs:37`）。
- `SceneProjectSession.Seek` 本来就是先在独立的候选会话上准备、支持取消、成功后再发布。可以把准备移到后台，只保留最新一次请求，取消被替代的旧请求，发布仍在 UI 线程。
- 可选：拖动过程中先显示不含物理预热的姿态，松开后再做完整跳帧。UE Sequencer 和 Blender 拖动时间轴时都不实时模拟物理。这会改变拖动过程中的画面，需要决定（第 7 节）。

**8. 混合架构 CPU（Animation）。** 蒙皮把块按固定轮转分给各个工作线程（`CharacterAnimator.cs:339`），整体耗时会被分到能效核的线程拖住。
- 改成动态领取：用原子计数逐块领取，保留每个线程独占的 QDEF 缓存和按块原序重抛的错误语义。
- 不按机器设定线程数。
- 验收：输出逐字节一致；在混合架构和非混合架构上都比较 P95。

**9. 导出期间的电源策略（App）。** Windows 11 可能对窗口最小化、不可见的进程做执行速度节流，长时间导出时系统也可能进入睡眠。
- 只在导出期间调用 `SetProcessInformation(ProcessPowerThrottling)` 关闭执行速度节流，并用 `SetThreadExecutionState(ES_SYSTEM_REQUIRED)` 阻止睡眠，导出结束后恢复。
- 这是平台策略，放在 App 层；`ViewOutput` 保持中立。
- 验收：在混合架构笔记本上，比较最小化和前台两种状态的导出吞吐；导出结束后恢复默认状态。

复现探针：

```bash
source tools/env.sh
export ModernCacheRoot="$MDA_WORKSPACE/.dev-cache/perf-platform"
dn build wt-perf-plan/tools/perf/platform-probe -c Release
dn "$ModernCacheRoot/bin/PlatformProbe/Release/net10.0/PlatformProbe.dll" "$MDA_WORKSPACE/work/<日期>/perf-explore/platform-<时间>.json"
```

## 6. 模块边界


| 改动 | 所在模块 | 不能越过的边界 |
|---|---|---|
| 围栏、资源池、描述符、Copy 和计算队列、PSO 缓存 | `Rendering.D3D12` | 不改 Core 和 Classic |
| 变形输入通道 | Core（数据）、Animation（产出） | Core 里不出现 GPU 类型 |
| GPU 蒙皮、描边、包围盒、BLAS 输入 | `Rendering.D3D12`、`ViewOutput.D3D12` | Classic 继续使用已变形顶点 |
| 播放所有权租约、模拟线程、命令队列 | Application（会话）、App（只负责交接和显示） | 视图不另外推进动画或物理时钟，模拟时钟只有一个 |
| 导出流水线 | `ViewOutput`、`ViewOutput.FFmpeg` | 中立接口里不出现 D3D12 类型 |
| 统一调度试点 | Application 持有预算，Animation 接收并行度 | 调度契约不依赖 GUI 或引擎 |
| 物理并行 | `Physics.Bullet`、native | 共享世界内部并行要另外决定 |
| 阴影剔除与缓存、GI 历史、选卡 | `Rendering.D3D12` | 不改 Classic；导出的 GI 规则在决定前保持不变 |
| 时间轴图层、面板刷新、刻度尺跳帧调度、导出电源策略 | App | 跳帧仍由 Application 的会话完成，App 只负责调度和显示 |

## 7. 待定决策

1. **并行预算**。建议先在 Application 验证（第 5.5 节）。计入调度成本后仍能降低完成帧 P95，再决定是否推广至其他模块。
2. **Mipmap**：核对原版之后，是否改变贴图采样。
3. **物理缓存**：跳帧时是否允许直接读缓存的物理状态。
4. **进程内 DXC**：是否新增依赖。
5. **首帧策略**：是先出部分画面，还是保持资源齐了再出。
6. **性能目标**：每种测试场景的目标帧率（例如 1 角色 1080p 不开 GI 稳定 60fps，3 角色 30fps），建议在第 0 阶段测完基线后再定。
7. **GI 预览画面**：是否允许逐像素保留时序历史、降低每帧采样数或半分辨率追踪，让预览画面因此变化（第 5.11 节第 2 条）。
8. **拖动刻度尺**：拖动过程中是否先显示不含物理预热的姿态，松开后再完整跳帧（第 5.11 节第 7 条）。

## 8. 建议顺序

| 顺序 | 阶段 | 风险 | 画面变化 |
|---|---|---|---|
| 1 | 第 0 阶段：测量基线 | 无 | 无 |
| 2 | 第 1 阶段先实现不可取消等待的围栏修正；深度池单独测 | 低 | 无 |
| 3 | 第 4 阶段先实现管道批写；复测长片和背压后决定是否引入读回环 | 低；多帧在途另评 | 无 |
| 4 | 第 2 阶段：GPU 蒙皮 | 中 | 浮点级差异，按第 4 节验收 |
| 5 | 第 3 阶段：模拟线程 | 中高 | 无 |
| 6 | 第 5 阶段、第 5.8 节：共享物理数据布局与并行预算分别做对照 | 中 | 无 |
| 7 | 第 6 阶段和 5.7：GPU 进阶、打开和跳帧 | 各项不同 | 见各项 |
| 8 | 第 5.11 节：选卡、阴影剔除和缓存、播放头图层、面板限频、跳帧后台化、动态分块、导出电源策略可以各自独立做；GI 历史等第 7 节决定后再做 | 低到中 | GI 和拖动预览两项有变化，其余没有 |

第 5.9 节的语言实验已经完成。是否集成由真实软体场景决定。其余阶段每次只提交一项可独立验收的改动，记录触发场景、对照基线与停止条件。
