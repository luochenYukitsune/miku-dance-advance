# 原创 PMD 验收素材

`humanoid.pmd` 是先前原生首帧部分的原创生成模型，复制自 `MMD932_20261015_rea/app_shell_rewrite/fixtures/humanoid.pmd`；生成源码为同目录 `fixture.py`。本工程未修改源素材，未使用用户角色资源。

SHA-256：`e6cfa362536086201cf106445c31cbbc249c4017f378b8c474309accf9e6ec21`；170875 bytes；2618 顶点、13440 索引、14 材质、6 骨骼。模型尾部的 base morph、display/toon/physics 元数据暂不解析，读取器返回明确的 `UnparsedTailBytes=43000`。当前使用纯 C# rest pose 与指定骨骼旋转，不引用原生核心或原 EXE。
