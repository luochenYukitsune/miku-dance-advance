// 测试的临时／验证文件根目录。
// 原先各测试写死 G:/MDA-Work；换机器后没有 G 盘就会失败。
// 优先用环境变量 MDA_WORK_ROOT；其次是已存在的 G:/MDA-Work（原开发机）；
// 都没有时退回系统临时目录下的 MDA-Work。
internal static class MdaWorkRoot
{
    private const string LegacyRoot = "G:/MDA-Work";

    public static string Root { get; } = Resolve();

    public static string Path(string relative) => System.IO.Path.Combine(Root, relative);

    private static string Resolve()
    {
        string? configured = Environment.GetEnvironmentVariable("MDA_WORK_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return System.IO.Path.GetFullPath(configured);
        if (Directory.Exists(LegacyRoot))
            return LegacyRoot;
        return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MDA-Work");
    }
}
