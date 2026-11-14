# 现代渲染：语义时间与帧调度

[项目](Mmd.Modern.Scheduling.csproj) 无GUI／GPU依赖。[SemanticTime](SemanticTime.cs) 和 [SemanticFrame](SemanticFrame.cs) 表达时间推进、固定步、骨／物理／draw回调顺序及错误边界。归属现代渲染调度，不是第五宏模块。

[Scheduling.Tests](../../tests/Mmd.Modern.Scheduling.Tests/Mmd.Modern.Scheduling.Tests.csproj) 的SemanticTime／FrameBoundary／OracleVector共84项在963历史基线中实际执行；O0／O2向量位于该项目Fixtures。向量只覆盖注释所述有限区域，不代表原宿主完整CPU／draw／after-time链全等或整个产品兼容完成。
