# MMD核心：可选效果前端

[项目](Mmd.Modern.Effects.csproj) 为纯托管效果前端，不创建GPU设备。FxSourceText／Tokenizer／Preprocessor／ConditionalExpression／[FxParser](FxParser.cs) 读取fx_2_0；EffectScript／EffectLibrary／MmeContract／MmeDiagnostics／[MmeRenderGraph](MmeRenderGraph.cs) 校验及计划资源／pass。`Neo/` 包含C#效果模型、导入、语义绑定及 [NeoEffectRuntime](Neo/NeoEffectRuntime.cs)；`Compilation/` 的 [EffectShaderTranslator](Compilation/EffectShaderTranslator.cs) 生成受支持现代HLSL。

主测试中的MmeEffects／MmeSemanticRegression／NeoEffectImport／NeoEffectRuntime／EffectShaderTranslation／EffectPlanGpuGuard分别验证前端与拒绝边界；实际GPU执行在D3D12项目。受限LightBloom十二遍证据见 [后处理阶段](../../docs/stages/2026-10-18-postfx-results.md)。语料解析／计划成功不代表GPU或任意MME兼容，D3D9／通用语义与L2/L3未完成；插件加载器后期再做。

测试入口：[MmeSemanticRegressionTests](../../tests/Mmd.Modern.Tests/MmeSemanticRegressionTests.cs)、[NeoEffectRuntimeTests](../../tests/Mmd.Modern.Tests/NeoEffectRuntimeTests.cs)、[EffectShaderTranslationTests](../../tests/Mmd.Modern.Tests/EffectShaderTranslationTests.cs)。
