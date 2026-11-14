# MMD核心：Bepu实验后端

最新[关节锁定修复](../../docs/stages/2026-10-20-physics-locked-joints.md)：线性／角度锁定时不再叠加冲突归零弹簧，保留自由／非锁定范围内弹簧；17真实Bepu用例修复前12失败、修复后全过，全工程1180非GPU通过。仅两处条件，完整Bullet兼容仍未完成。

最新[IK／物理联动](../../docs/stages/2026-10-20-ik-physics-coupling.md)：运动学目标角速度在微小角处使用连续极限2*xyz/dt，避免旧零死区；真实Bepu7个用例覆盖微角、复合旋转、重复目标、分数步、seek、多子步及接触传速。Playback仅把实际Dynamic／DynamicAligned绑定骨传入IK驱动抑制，Kinematic不禁IK；全工程1163非GPU通过。完整Bullet参数／数值仍未完成，继续核关节兼容。

[项目](Mmd.Modern.Physics.Bepu.csproj) 接固定BepuPhysics 2.4.0。[BepuCharacterPhysicsBackend](BepuCharacterPhysicsBackend.cs) 为角色刚体／关节真实step／reset与反馈实现；[BepuPhysicsBackend](BepuPhysicsBackend.cs) 保留基础冒烟。支持球／盒／胶囊、碰撞mask、部分limit／servo／spring。

验证在主测试的Physics／CharacterPhysics系列及当前EngineShowcaseTests；历史角色反馈见 [阶段](../../docs/stages/2026-10-19-physics-feedback.md)。Bepu不是Bullet，Generic6DOF存在twist近似，其他joint／恢复系数／模型间接触及完整原版数值仍有边界。原创Demo为当前新增阶段，须独立验收。

测试入口：[PhysicsTests](../../tests/Mmd.Modern.Tests/PhysicsTests.cs)、[CharacterPhysicsAnimationTests](../../tests/Mmd.Modern.Tests/CharacterPhysicsAnimationTests.cs)、[EngineShowcaseTests](../../tests/Mmd.Modern.Tests/EngineShowcaseTests.cs)。
