# MMD核心：物理中立接口与同步

[项目](Mmd.Modern.Physics.csproj) 不依赖具体求解器。[CharacterPhysicsDefinition](CharacterPhysicsDefinition.cs) 定义刚体／关节；ICharacterPhysicsBackend是后端接口；[CharacterPhysicsSession](CharacterPhysicsSession.cs) 处理骨offset、三模式、120Hz固定步／最多10子步与reset同步，保留Saba MIT许可。

实际Bepu实现位于相邻 `Mmd.Modern.Physics.Bepu`；Application编排前骨骼→物理→后骨骼。测试在主测试项目的Physics／CharacterPhysics／PhysicsAffineReflection，证据见 [独立物理](../../docs/stages/2026-10-19-ik-physics.md)／[反馈阶段](../../docs/stages/2026-10-19-physics-feedback.md)。接口与同步移植不等于Bullet数值一致；seek按目标姿态重启，未重演此前轨迹。

测试入口：[CharacterPhysicsTests](../../tests/Mmd.Modern.Tests/CharacterPhysicsTests.cs)、[PhysicsAffineReflectionTests](../../tests/Mmd.Modern.Tests/PhysicsAffineReflectionTests.cs)。
