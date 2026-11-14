# 验证工具：Smoke与效果扫描

[项目](Mmd.Modern.Smoke.csproj) 是工具，不是第五产品模块。[Program](Program.cs) 分发旧PMD／Veldrid／Bepu冒烟，以及D3D12／Mesh／Toon／OriginalStyle／ModernToon／PostEffect CLI；[MmeScanCli](MmeScanCli.cs) 执行fx解析／计划／语料扫描，SceneTextureLoader为纹理加载工具，也由主测试Compile Link复用。

主验证入口为项目根 `scripts/verify.ps1`；离屏完整帧另用 `scripts/frame-gpu-evidence.ps1`。实际命令／阶段证据见 [总索引](../../docs/SOURCE_INDEX.md#测试项目和验证工具)。扫描成功只证明前端支持范围，不能称任意MME的GPU兼容；运行GPU工具须有实际设备并保留原资产只读。

测试入口：[SceneTextureLoaderTests](../../tests/Mmd.Modern.Tests/SceneTextureLoaderTests.cs)、[OriginalStyleTests](../../tests/Mmd.Modern.Tests/OriginalStyleTests.cs)；完整运行入口：[verify.ps1](../../scripts/verify.ps1)。
