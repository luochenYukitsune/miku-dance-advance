# 开发工具：帧查看器与引擎演示

2026-11-17 起，这里存放原来放在编辑器（App）里的开发工具，编辑器安装包不再包含它们：

- **帧查看器**：`--frame-viewer`。包括 FrameViewport*、D3D12ViewportHost、NativeLightControls、SceneObjectControls。用 `scripts/run-frame-viewer.ps1` 启动。
- **引擎演示**：`--engine-demo`，直接双击 exe 时的默认模式。包括 EngineDemo*。
- **旧版 Veldrid PMD 预览**：`--legacy-preview`。包括 MainWindow、MainWindowViewModel。用 `scripts/run-app.ps1` 启动。

本项目引用 App，复用其视口辅助和渲染管线；App 不引用本项目。下文为原说明。


[项目](Mmd.Modern.Demo.csproj) 只有薄 [Program](Program.cs)，复用App的 `--engine-demo` 模式与实际渲染／物理，不复制引擎。原创场景在Core的EngineShowcaseScene、控制器在Application、窗口在App。静止光追和运动物理分两模式，不能解释为动态角色GI已实现。

用项目根 [scripts/publish-demo.ps1](../../scripts/publish-demo.ps1) 发布到外围 `D:/desktop/mmopensource/deliverables/EngineDemo`，源码检查点在 `D:/desktop/mmopensource/deliverables/Source`。本阶段EngineShowcase定向测试与根UI／GPU集成验证独立于旧963 PMM基线；具体验收见 [当前工作](../../docs/CURRENT_WORK.md)。Demo可运行不等于完整MMD编辑器已完成。

测试入口：[EngineShowcaseTests](../../tests/Mmd.Modern.Tests/EngineShowcaseTests.cs)，核心实现入口：[EngineShowcaseScene](../Mmd.Modern.Core/EngineShowcaseScene.cs)／[EngineShowcaseController](../Mmd.Modern.Application/EngineShowcaseController.cs)。
