# 2026-11-24 可维护性审查

基线为 `1cda22f`。统计覆盖 `src/` 下 26 个项目的受版本控制 C# 文件。按方法分支、嵌套和跨模块重复分派筛选后，沿公开入口追踪存档、动画与物理、帧提交、Classic、预览与导出。同时人工检查了原生 Present ABI 和仓库验证脚本。两名独立审查者先从基线源码检查异常与恢复路径，再对照本次改动。以下统计不含测试、生成文件、脚本和原生代码。

## 范围与结果

| Roslyn 语法树统计 | 基线 | 本次 |
| --- | ---: | ---: |
| C# 文件 | 725 | 730 |
| 有实现的成员及本地函数 | 9,363 | 9,421 |
| 分支分数至少 30 的成员 | 115 | 113 |
| 控制结构嵌套至少 6 层的成员 | 30 | 28 |
| 全部显式分支计数 | 30,215 | 30,095 |

分支分数从 1 开始，对条件语句、循环、捕获和过滤、三元表达式、非默认 switch 分支、when 及短路与或各加 1。全局显式分支计数扣除每个成员的起始分。嵌套取同一成员内包含该语句的控制结构层数。本地函数单列，lambda 计入所在成员。行数取成员语法节点首尾行，两端均计入。指标用于找出需要人工检查的入口，不代表缺陷数量。

| 成员 | 行数，基线 → 本次 | 分支分数，基线 → 本次 | 主要变化 |
| --- | ---: | ---: | --- |
| `SceneAnimationSession.EvaluateSharedBoundary` | 493 → 66 | 198 → 29 | 显式区分准备、世界求解、挂接更新及姿态发布。串并行工作共用按角色顺序处理故障的路径。拆出的最大分支仍在串行准备阶段，165 行、80 分。 |
| `D3D9EffectResourceTree.ValidateDependencyMetadata` → `DependencyValidator.Validate` | 192 → 14 | 151 → 3 | 把头、节点、编译配置、边和可达性分别验证，默认效果与 include 复用图遍历。各阶段中最高为 28 分。纹理函数按入口和目标的精确元组索引，去掉逐边扫描全部配置。 |
| `D3D12ViewRenderingStyles.ResolveCore` | 128 → 84 | 16 → 8 | 直接解析和缓存共用材质范围分派，涵盖 MMD、隐藏、非角色、插件与诊断。 |
| `D3D12ViewRenderingStylesCache.BuildBindingsState` | 220 → 158 | 42 → 27 | 与直接解析按相同规则写入各材质槽。混用风格时，备用材质改名会刷新猜测角色。 |
| `D3D12FrameRenderer.RenderCore` | 583 → 561 | 156 → 138 | 输出模式校验集中，提交失败时保留上一成功帧的目标、录制记录及统计。 |

## 行为与验证入口

| 入口 | 需要守住的行为 | 对应用例或检查 |
| --- | --- | --- |
| 项目存档、编辑器撤销 | 分离的动作轨道可经公开保存重读。无显式设置时保存并恢复活动会话的共享物理和耦合软体选择。文档切换后，旧保存完成不得覆盖新文档的路径和修改标记。模型替换只在成功后清理旧的父级挂接历史。 | `SceneAnimationProjectStoreTests`、`EditorProjectPersistenceRoutingTests`、`WorkbenchReplacementUiTests` |
| 共享 Bullet 边界 | 保留父级求值和物理步进顺序、串并行逐帧结果、工作线程全部结束后按确定顺序报告异常。 | `PmmMultiPropParallelParityTests`、`SharedRigidPlaybackTests`、`SceneInteractiveFrozenAttachmentReuseTests` |
| PMX、纹理 | 读入 11 种 morph 时按该记录使用的索引宽度预检。共享预取取消后可重试，后续读取能命中成功缓存。 | `PmxBoundaryRegressionTests` 对照不同材质与 morph 宽度及截断位置。对本地 16 个模型比对读写摘要。 |
| D3D12 材质与帧 | 直接解析和缓存输出一致。仅在校验成功后缓存材质结果，覆盖只改变备用槽 toon 参数的帧和失败重试。呈现回调失败后保留上一次帧诊断，下一次成功帧能继续更新。 | `EditorViewportRecordAheadParityTests`、`D3D12FrameSessionTests`、`D3D12PmxMaterialTreatmentViewTests`、全 GPU 分类用例 |
| Classic | 预览和导出共用请求冻结和绑定。V1/V2 共用一次校验、绘制和结果写入，V2 先冻结叠层。保留两个原生入口及其错误消息。 | `EffectDependencyTreeTests`、同一工具链重建主线和候选 DLL。两者各 31 项原生 ABI 检查，错误消息、计数和三角形像素摘要逐字节一致。 |
| 预览、PNG 和插件 | 中途取消 resize 后，下一帧使用后端已生效的尺寸。清理失败可重试。插件依赖和重复生命周期操作共享处理。 | `PreviewLifecycleTests`、`ImagesTests`、插件生命周期及编辑器集成用例 |
| 导出发布边界 | 渲染失败会保留磁盘旧文件。接收器已发布新文件后，后续清理或最终进度通知失败仍保留原异常，并向用户准确报告文件已写成。 | `ExportCoordinatorTests`、`EditorRenderCleanupTests` 使用真实 PNG 覆盖发布前后两侧。 |
| 验证脚本 | 从解决方案读取测试项目，九个程序集都执行，单个失败会留痕并继续其余项目。 | `scripts/verify.ps1 -SkipGpu`、Windows CI、可选真机 GPU 单独串行执行 |

## 剩余热点

`D3D12FrameRenderer.RenderCore` 仍有 561 行，在帧内协调多组资源和渲染阶段。相邻的 `D3D12TemporalInputPass.RecordCharacter` 有 384 行、111 分，原生数据打包侧 `D3D9SurfaceFrameEncoder.SerializeModels` 有 198 行、101 分，组合缓存 `ProjectSceneAssembler.CompositionCache.Compose` 有 346 行、90 分。这些入口分别涉及资源屏障、时序输入、ABI 字节顺序和场景复用。后续拆分可用逐帧像素、原生序列化字节和缓存复用断言约束各阶段。当前验证覆盖了已执行的场景和硬件配置。
