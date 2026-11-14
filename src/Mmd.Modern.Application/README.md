# MMD核心：应用接口与会话编排

PortableModelAssets（两Store显式选项、layout v2）、BoneEditingBasis（Local/Global/View→VMD注册）、PmmEditedMotionExporter（模型命名轨道与saved raw local）已验；461应用测试通过。完整Session布局/场景轨道→PMM仍未接。用户已选普通保存默认复制资源，默认值由紧接任务接入；显式外链保留。

可见性/真实空场景、小数帧骨骼、PMM自阴影出口及离线输出Plan/Runner已验。SetGravity沿Controller/Playback进入后端，不重启solver；不等于PMM重力轨道已实现。414应用测试通过；有理FPS输出明确报告最近120Hz已完成采样。

最新集成：PMD工程入口、外亲keep-world/偏移/Undo、v5绑定与v6场景VMD持久化、相机/灯/自阴影编辑、扩展UV/纹理/Flip/Impulse播放出口、VPD原始local及显式烘焙姿态导出。375应用测试通过；不能据此宣称共享物理、编辑后PMM及完整工程已完。

[项目](Mmd.Modern.Application.csproj) 引用Core／Formats／Animation／Physics，不引用Avalonia／Vortice或具体物理解算器。PMX与VMD适配器连接rig／表情／IK／物理；[CharacterAnimationController](CharacterAnimationController.cs)／CharacterPhysicsPlayback管理会话；[PmmProjectImport](PmmProjectImport.cs)／PmmAssetPathResolver／PmmMotionAdapter／PmmProjectPlayback／PmmSceneTracks／ProjectSceneAssembler处理PMMv2事务式导入和多模型／外亲。

当前 [SceneLightingEditor](SceneLightingEditor.cs) 为光源编辑事务／sidecar，EngineShowcaseController为原创Demo编排；新阶段须按当前验收。测试在 [Application.Tests](../../tests/Mmd.Modern.Application.Tests/Mmd.Modern.Application.Tests.csproj) 与主测试的CharacterPhysicsController／PmmScene／MorphAdapter等。已验PMM与缺口见 [阶段](../../docs/stages/2026-10-19-pmm.md)；完整编辑／撤销、原PMM写回和资产重定位仍未完成。

测试入口：[PmmProjectImportTests](../../tests/Mmd.Modern.Application.Tests/PmmProjectImportTests.cs)、[CharacterPhysicsControllerTests](../../tests/Mmd.Modern.Tests/CharacterPhysicsControllerTests.cs)、[SceneLightingEditorTests](../../tests/Mmd.Modern.Application.Tests/SceneLightingEditorTests.cs)。

[SceneCompositionEditor](SceneCompositionEditor.cs) 管理静态PMX物体的稳定ID、选择、变换和撤销；复用ProjectSceneAssembler组合共享帧，不依赖GUI或D3D12。[场景首版](../../docs/stages/2026-10-20-scene-composition.md)。

[SceneProjectStore](SceneProjectStore.cs) 提供静态布局`.mmdscene` v1：`Save/Serialize`捕获有序物体ID、路径、变换、用途和原生灯；`Load/Prepare`通过调用方资源加载函数形成独立候选，全部成功后才交给宿主替换。支持取消、严格版本／字段、相对文档路径、带物体ID的资源错误和失败时旧文件保护。用途在文档每物体仅存一次，以现有LightingEditor用户编辑为权威，没有用途键才回退对象Role；不保存孤立用途提示。仅静态源资产＋布局，不包含动作、相机、物理运行态、画风设置或原PMM写回。[后端阶段](../../docs/stages/2026-10-20-backend-first.md)。

[SceneAnimationSession](SceneAnimationSession.cs)在固定布局快照上独立绑定／替换／清除每角色VMD，管理IK／表情／默认独立物理开关、共享30fps时间、末帧冻结、seek及失败恢复。发布中立帧／不可变角色状态／清理诊断；不依赖Avalonia或具体求解器。[SampleAt／EnablePhysicsAt](CharacterAnimationController.cs)保持小数表情重绑时刻，原uint API保留。[阶段与实际双Bepu测试](../../docs/stages/2026-10-20-multi-actor.md)。GUI未接入。

[SceneMotionAsset](SceneMotionAsset.cs)从同一份私有VMD字节解析Motion并计算SHA256，源文件改删后仍可保存原绑定。持久化绑定使用`SceneActorMotion(..., Asset: asset)`且Motion必须为`asset.Motion`；旧无Asset绑定仍可播放，保存时明确拒绝。[SceneAnimationProjectStore](SceneAnimationProjectStore.cs)的`Save/Load/Prepare`提供`.mmdscene` v2，复制到`assets/motions/<sha>.vmd`并去重，保留布局／灯光／独立动作／三开关及全局小数帧，兼容静态v1。全部VMD先验hash再加载模型，成功候选由调用方替换；`PreparedSceneAnimationProject.Dispose`只释放自己的新动画会话。物理工厂必须为每角色提供新且独占的后端。

本轮60新增用例、1055非GPU回归通过；详见[设计／结果／限制](../../docs/stages/2026-10-20-animation-project.md)与[项目存取测试](../../tests/Mmd.Modern.Application.Tests/SceneAnimationProjectStoreTests.cs)。PMX／纹理外部引用，物理目标姿态重启，宿主重开后保持暂停；不保存相机／渲染设置／任意编辑或求解器历史，不是原PMM写回。

最新[动作写回阶段](../../docs/stages/2026-10-20-motion-writeback.md)：MotionEditor支持骨骼四通道曲线事务／Undo／Redo并保留相机灯光阴影记录；SceneMotionAsset.FromMotion从编辑数据创建验证快照。工程CP932继续v2，非默认CP936使用v3并保存每动作编码；解析缓存以SHA＋编码区分，文件仍按SHA去重。角色编辑保存重开已验，不代表场景轨道播放／编辑或PMM写回完成。

PMM原始名称来源经匹配携入VMD，所有关键帧编辑和历史保留；新登记／粘贴仅继承唯一同类轨道来源。SceneMotionAsset保名称解码配置；工程v4保存非默认逐名称配置，解析缓存含SHA／默认编码／配置，资源文件仍按SHA去重。旧v2／v3读取及候选失败保旧保持，当前GUI入口不新增。
