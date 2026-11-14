# 兼容模式：Veldrid过渡预览

[项目](Mmd.Modern.Rendering.Veldrid.csproj) 的 [VeldridRenderBackend](VeldridRenderBackend.cs) 读取Core的中立SceneSnapshot；[model.hlsl](Shaders/model.hlsl) 和两份SPIR-V随源码保留。构造器目前明确创建D3D11或Vulkan离屏设备，最终复制CPU图片供旧Avalonia预览，`scripts/run-app.ps1 -LegacyPreview`保留旧PMD入口。

历史图与重复冒烟见 [首帧阶段](../../docs/stages/2026-10-18-stage-results.md)／[后处理阶段](../../docs/stages/2026-10-18-postfx-results.md)。库本身支持更多后端不等于工程已经接入；当前没有D3D9完整产品路径或OpenGL设备分支，也不代表现代Vulkan完整帧／RT已完成。完整兼容模式仍在规划中。

验证入口：[Smoke Program](../Mmd.Modern.Smoke/Program.cs)、[verify.ps1](../../scripts/verify.ps1)；旧预览GUI测试见 [UiTests](../../tests/Mmd.Modern.Tests/UiTests.cs)。
