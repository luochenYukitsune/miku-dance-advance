# MMD核心：动画求值

QDEF已实现刚性dual-quaternion蒙皮，10项含Saba/GLM原生数值对照；拒绝缩放/剪切/反射矩阵。BDEF/SDEF保持现有实现，GPU消费属于渲染模块。

最新[IK／物理联动](../../docs/stages/2026-10-20-ik-physics-coupling.md)：FixedAxis中立元数据、Mmd单轴前半投影／后半一般求解和继续迭代、PhysicsDrivenBoneIndices快照及末端父骨抑制；Saba路径保留。25组原函数受控输入与C#最大矩阵差1.7881393e-7，不能推出完整原版等价。45新增包含真实Bepu联动／微角度测试，全工程1163非GPU通过；详细剩余以本阶段为准。

最新[骨骼顺序](../../docs/stages/2026-10-20-bone-schedule.md)：内部[MmdBoneSchedule](MmdBoneSchedule.cs)编译PMX层／IK后重建列表，MmdConstraintEvaluator区分prepare与工作位移、重读IK及物理反馈源local。PMX适配器明确Mmd；旧直接构造默认Saba，调度不依赖限角选项。[15新增用例](../../tests/Mmd.Modern.Tests/BoneEvaluationScheduleTests.cs)、1118非GPU及12例原BA200函数数值对照通过。世界快照／外亲晚pass／IK内部刷新／物理抑制等仍不完全等价。

[项目](Mmd.Modern.Animation.csproj) 仅依赖Core；[CharacterAnimator](CharacterAnimator.cs)／CharacterRig／MotionClip／BezierCurve处理骨骼与BDEF1/2/4／SDEF，MorphPose和CharacterPhysicsSample传递表情／反馈，FramePlaybackClock独立管理播放时间。[MmdConstraintEvaluator](MmdConstraintEvaluator.cs)／RigConstraints处理IK、append与前后阶段。

验证在 `tests/Mmd.Modern.Tests` 的CharacterAnimation／CharacterIk／AppendRotation／CharacterMorph／OutsideParent／UvMorph及OriginalLocalAppend等测试。最新[局部追加／跨阶段平移](../../docs/stages/2026-10-21-append-local.md)确认MMD调度的追加源和阶段工作量，显式Saba local分支保留；早期[动画](../../docs/stages/2026-10-19-animation.md)、[IK](../../docs/stages/2026-10-19-ik-physics.md)、[PMM](../../docs/stages/2026-10-19-pmm.md)保留历史范围。保留Saba MIT许可；物理控制工作量／动态祖先、更多IK／append／物理及late／嵌套外亲的原版验收见[核心清单](../../docs/CORE_COMPLETION.md)，不称任意动作或原版逐位一致。

测试入口：[CharacterAnimationTests](../../tests/Mmd.Modern.Tests/CharacterAnimationTests.cs)、[CharacterIkTests](../../tests/Mmd.Modern.Tests/CharacterIkTests.cs)、[OutsideParentAnimationTests](../../tests/Mmd.Modern.Tests/OutsideParentAnimationTests.cs)。

此前[IK稳定性小修](../../docs/stages/2026-10-20-ik-stability.md)在CCD增量与求逆使用完整Animated＋Append基底，单轴有效弯曲作为初始累计角，处理q/-q半圈端点；Mmd策略以atan2(cross长度,dot)保留小角修正，Saba策略保原acos及其对照。无跨帧求解缓存、自动锁脚或VMD改写。[23个新用例](../../tests/Mmd.Modern.Tests/CharacterIkAppendStabilityTests.cs)包含三轴完整姿态、父变换、两节腿固定脚端和乱序采样，当时1078非GPU通过。

最新[PMX多轴限角](../../docs/stages/2026-10-20-ik-limits.md)新增内部[MmdIkRotationLimits](MmdIkRotationLimits.cs)，只在Mmd一般分支按原版选择三种Euler顺序／奇异角封顶／半程反射，Saba旧策略保留。参考固定MIT babylon-mmd源码，完整许可[LICENSE.BabylonMmd.txt](LICENSE.BabylonMmd.txt)随构建／发布复制；无额外运行时依赖。[25新用例](../../tests/Mmd.Modern.Tests/CharacterIkMmdLimitsTests.cs)、1103非GPU及21例原生Saba／柴郡数值回归通过。本轮没有原EXE／GPU运行；完整solver、调度及Bullet兼容仍未完成。
