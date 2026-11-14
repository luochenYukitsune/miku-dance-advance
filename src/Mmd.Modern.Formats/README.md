# MMD核心：格式读取与适配

[项目](Mmd.Modern.Formats.csproj) 依赖Core。实现 [PmxReader](PmxReader.cs)／PmxModel、PMX→中立场景／原风格／完整帧／描边适配、[VmdReader](VmdReader.cs)／VmdMotion及IK开关、[PmmReader](PmmReader.cs)／PmmDocument和原始文字／偏移保留。旧PMD基础读取位于Core的Model.cs。

[Formats.Tests](../../tests/Mmd.Modern.Formats.Tests/Mmd.Modern.Formats.Tests.csproj) 覆盖读取边界及适配，PMMv2另有固定nanoem MIT C oracle；见 [PMM阶段](../../docs/stages/2026-10-19-pmm.md)。VPD读写与VMD0001/0002六段保留／写回已验；VMD编码显式CP932／936，VmdWriteOptions允许仅覆盖导出模型头；PMD工程模型与PMD／PMX／PMMv2写回已集成；PMMv1与编辑会话→PMM映射仍未完成；读取metadata不等于播放或编辑全部实现。完整导航见 [总索引](../../docs/SOURCE_INDEX.md#mmd核心)。

VmdEncodedName保留严格解码验证的原始名称payload，默认写回原字节；可显式关闭PreserveOriginalNames严格转码。VmdReadOptions支持名称配置及骨骼／表情候选唯一匹配，歧义报错。保真不含NUL后填充；不扩展.NET严格编码表。

PMX读取默认输入预算为10 GiB（用户最新选择）；`PmxReadOptions.MaxInputBytes`可显式调整，原有单参数`Read(Stream)`／`Read(string)`签名保留。流预算从当前位置计，非seek输入用自动删除临时文件做有界spool，最多探测预算外1字节，避免整份输入额外占用托管内存及`MemoryStream`的Int32容量限制。调用方流不归读取器所有；输入字节限制不等于解析后模型对象总内存承诺。正式GUI设置页将调用此配置接口；PMA包及资源预算独立设置。

大资源PMA使用`PmxWriter.Write(Stream, PmxModel, PmxWriteOptions)`，`MaxOutputBytes`为long、默认10 GiB；先计数校验再流式写入，支持非seek目标、不关闭调用方流。旧`ToBytes`／双参数`Write`保留512 MiB预算与原字节数组行为。调用期间模型数据不得被并发修改；目标I/O失败可能留下部分输出，PMA在私有临时文件中完成后才发布。
