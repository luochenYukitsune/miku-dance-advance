# MMD核心：共享合同与基础数学

[项目](Mmd.Modern.Core.csproj) 不引用GUI或GPU库。入口是 [SceneSnapshot](SceneSnapshot.cs)、[OriginalFrameScene](OriginalFrameScene.cs)／[OriginalFrameOptions](OriginalFrameOptions.cs)；包含材质、toon、相机、阴影／可见性与RayGi场景／BVH中立数据。旧 [Model.cs](Model.cs) 的PMD读取／蒙皮与PmdSceneAdapter保留。

新增SceneLightingSnapshot／ViewportOverlaySnapshot／EngineShowcaseScene属于原生光源／Demo阶段，不能继承旧基线的完整验收。验证在 `tests/Mmd.Modern.Tests` 的Core／Scene／Original／RayGi／Lighting／EngineShowcase相关测试；已验范围与证据见 [总索引](../../docs/SOURCE_INDEX.md#mmd核心)。PMD尾部、一般相机／原版整帧金标及完整格式范围仍有缺口。

测试入口：[CoreTests](../../tests/Mmd.Modern.Tests/CoreTests.cs)、[SceneSnapshotTests](../../tests/Mmd.Modern.Tests/SceneSnapshotTests.cs)、[EngineShowcaseTests](../../tests/Mmd.Modern.Tests/EngineShowcaseTests.cs)。
