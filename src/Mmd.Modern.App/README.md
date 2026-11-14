# GUI：Avalonia与视口编排

正式入口为[EditorWindow](EditorWindow.axaml)与[EditorShellViewModel](EditorShellViewModel.cs)，启动脚本`scripts/run-editor.ps1`。当前先完整前端，再接既有Application／ViewOutput；原MMD白色世界、黑白主题、IDE蓝灰操作带、上下文属性与就近操作遵循用户已选方向。[EditorTimeline](Controls/EditorTimeline.cs)管理前端帧尺、游标和输入；[OutputSettingsWindow](OutputSettingsWindow.axaml)编辑独立输出草稿并复用既有验证，不启动实际导出。真实模型／关键帧／播放／保存等业务接线仍待完成；不得将以下旧视口工具能力当成正式GUI已接通。

原生验证入口为`scripts/verify-editor-shell.ps1`，输出到外围`deliverables/GUI/`。当前实现与验证边界见[工作台阶段](../../docs/stages/2026-10-25-gui-workbench.md)与[当前工作](../../docs/CURRENT_WORK.md)。

[EditorExportRange](EditorExportRange.cs)复用工程实际轨道最大帧；EditorShellViewModel可借用宿主SceneProjectSession，在打开输出草稿时读取。新范围默认0至末关键帧、空工程0；自动范围重开更新，手动范围保留。该接入口不代表模型导入或实际输出已连到正式工作台。

[项目](Mmd.Modern.App.csproj) 的Program／App.axaml负责启动装配；MainWindow保留旧预览。[FrameViewportWindow](FrameViewportWindow.cs)／FrameViewportViewModel／FrameViewportApplication／D3D12ViewportHost／ViewportFrameDispatch管理原生D3D12窗口与输入。Camera／Capture／Options、Project／Animation／`Animation/FrameViewportAnimationControls.cs`连接应用接口、导入、镜头、播放／seek、物理和截图。

当前NativeLightControls／LightViewportInteraction／FrameViewportLights接拖动／旋转与属性精调，EngineDemoWindow／FrameViewportEngineDemo展示原创小场景；该新增阶段以最终UI／GPU验收为准。主测试中Ui／FrameViewport／ViewportLightInteraction等及 [PMM真实窗口证据](../../docs/stages/2026-10-19-pmm.md)可导航。完整时间轴／编辑／撤销／项目写回／媒体输出未完成；数值算法继续留核心。

测试入口：[FrameViewportTests](../../tests/Mmd.Modern.Tests/FrameViewportTests.cs)、[UiTests](../../tests/Mmd.Modern.Tests/UiTests.cs)、[ViewportLightInteractionTests](../../tests/Mmd.Modern.Tests/ViewportLightInteractionTests.cs)；真实窗口验收与headless测试分别看阶段报告。

主项目已接 [SceneObjectControls](SceneObjectControls.cs)／[FrameViewportSceneObjects](FrameViewportSceneObjects.cs)：角色与场景追加、列表选择、位置／水平旋转／缩放、聚焦及撤销重做。静态首版；逐物体动作、视口物体手柄和场景保存后续。[阶段](../../docs/stages/2026-10-20-scene-composition.md)。
