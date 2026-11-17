# GUI：Avalonia与视口编排

正式入口为[EditorWindow](EditorWindow.axaml)与[EditorShellViewModel](EditorShellViewModel.cs)，启动脚本`scripts/run-editor.ps1`。当前先完整前端，再接既有Application／ViewOutput；原MMD白色世界、黑白主题、IDE蓝灰操作带、上下文属性与就近操作遵循用户已选方向。[EditorTimeline](Controls/EditorTimeline.cs)管理前端帧尺、游标和输入；[OutputSettingsWindow](OutputSettingsWindow.axaml)编辑独立输出草稿并复用既有验证，不启动实际导出。真实模型／关键帧／播放／保存等业务接线仍待完成；不得将以下旧视口工具能力当成正式GUI已接通。

原生验证入口为`scripts/verify-editor-shell.ps1`，输出到外围`deliverables/GUI/`。当前实现与验证边界见[工作台阶段](../../docs/stages/2026-10-25-gui-workbench.md)与[当前工作](../../docs/CURRENT_WORK.md)。

[EditorExportRange](EditorExportRange.cs)复用工程实际轨道最大帧；EditorShellViewModel可借用宿主SceneProjectSession，在打开输出草稿时读取。新范围默认0至末关键帧、空工程0；自动范围重开更新，手动范围保留。该接入口不代表模型导入或实际输出已连到正式工作台。

[项目](Mmd.Modern.App.csproj) 的Program／App.axaml只启动正式编辑器。2026-11-17起，开发用的帧查看器、引擎演示和旧版 Veldrid 预览（MainWindow）已移到 [Mmd.Modern.Demo](../Mmd.Modern.Demo/README.md)，不再随编辑器发布。视口辅助 LightViewportInteraction、ViewportFrameDispatch 仍在本项目，编辑器与 Demo 共用。
