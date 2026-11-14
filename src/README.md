# 源码入口

2026-10-24：[模型材质mapping与专用图随PMA保存／恢复](../docs/stages/2026-10-24-material-bindings-pma.md)已验；通用画风预设仍由引擎管理。严格范围与后续见[当前工作](../docs/CURRENT_WORK.md)。

2026-10-24：[原生Diffusion最终场景滤镜](../docs/stages/2026-10-24-native-diffusion.md)已验证，默认关闭；场景滤镜在抗锯齿／超分之后，辅助标记保持清晰。完整范围与后续见[当前工作](../docs/CURRENT_WORK.md)。

实时状态、测试数量和下一步见[当前工作](../docs/CURRENT_WORK.md)。本页只做源码导航；[架构图](../docs/architecture-sketch.md)解释边界，[源码总索引](../docs/SOURCE_INDEX.md)解释职责，[SOURCE_INDEX.json](SOURCE_INDEX.json)保存实际逐文件归属、依赖与 SHA。文件被索引不等于目标功能已全部验收。

## 模块导航

- **[MMD核心](../docs/SOURCE_INDEX.md#mmd核心)**：Core 中立合同；Formats、Animation、Physics、Physics.Bullet、Physics.Bepu、Application。核心算法不引用 Avalonia／Vortice；Effects／MME只归旧兼容渲染。
- **[现代渲染](../docs/SOURCE_INDEX.md#现代渲染)**：Rendering.D3D12 设备／完整帧／toon／可选 GI 与 AA；Scheduling 语义时间和帧调度。
- **[兼容模式](../docs/SOURCE_INDEX.md#兼容模式)**：Effects／MME前端及旧渲染参考；Rendering.Veldrid保旧 PMD／离屏预览，D3D9／OpenGL完整产品路径仍有缺口。
- **[GUI](../docs/SOURCE_INDEX.md#gui)**：App 验证窗口及装配，Demo 薄启动入口；正式 GUI 后置。
- **[视图与输出](../docs/SOURCE_INDEX.md#视图与输出)**：ViewOutput中立协调／PNG、ViewOutput.FFmpeg编码、ViewOutput.D3D12适配三个独立项目；复用Application离线时钟与现有GPU，首版已通过实际GPU／四codec输出及集中回归，正式GUI后置。

以上保四大主体模块及独立视图／输出编排。Smoke、`tests/`、PmxScan 与 ViewOutput.Stage 是验证设施，不是新增产品模块；旧FrameGpuEvidence随MME实验保留兼容参考归档，`.slnx`仅作逻辑分组。

## 常用入口

从项目根调用 [run-app.ps1](../scripts/run-app.ps1)、[verify.ps1](../scripts/verify.ps1)，参数示例见[项目 README](../README.md)。GPU 检查会使用设备／窗口，按任务范围选择；多个 MSBuild 不并行运行。

刚体 native ABI 在 [native/Mmd.Modern.Bullet](../native/Mmd.Modern.Bullet/mmd_bullet.h)，独立软体 ABI／world 在 [native/Mmd.Modern.Bullet.SoftBody](../native/Mmd.Modern.Bullet.SoftBody/README.md)，C# 接口同属 Physics.Bullet。软体子集及未支持组合以 native 说明和[核心执行单](../docs/CORE_TASKS.md)为准。

源码检查点：`D:/desktop/mmopensource/deliverables/Source`；Demo 输出：`D:/desktop/mmopensource/deliverables/EngineDemo`；缓存／temp／bin／obj：外围 `D:/desktop/mmopensource/.dev-cache`。新增目录和索引文件名使用英文，许可与锁版保留。

压缩前全文逐字节保存在[源码入口历史快照](../docs/history/source-readme-through-20261021.md)，其中各阶段数量不覆盖当前工作区。
