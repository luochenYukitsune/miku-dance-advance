using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using System.Text;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmAssetPathResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pmm-project-tests", Guid.NewGuid().ToString("N"));

    private string FileAt(string relative)
    {
        string file = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "fixture");
        return file;
    }

    [Fact]
    public void FindsProjectAndMmdRelativePathsAndPreservesRecordedAbsolutePath()
    {
        string project = FileAt("project/scene.pmm"), direct = FileAt("project/models/a.pmx"), mmd = FileAt("mmd/UserFile/b.pmx");
        var resolver = new PmmAssetPathResolver(project, new() { MmdDirectory = Path.Combine(root, "mmd") });
        Assert.Equal(direct, resolver.Resolve(["models/a.pmx"]).Path);
        Assert.Equal(mmd, resolver.Resolve(["UserFile/b.pmx"]).Path);
        Assert.Equal(direct, resolver.Resolve([direct]).Path);
    }

    [Fact]
    public void DuplicateBasenamesAreAmbiguousUntilExplicitOverride()
    {
        string project = FileAt("project/scene.pmm"), first = FileAt("assets/one/a.pmx"), second = FileAt("assets/two/a.pmx");
        var resolver = new PmmAssetPathResolver(project, new() { SearchRoots = [Path.Combine(root, "assets")] });
        var ambiguous = resolver.Resolve(["D:/old/a.pmx"]);
        Assert.Equal(PmmAssetResolutionStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(new[] { first, second }.Order(), ambiguous.Candidates.Order());
        Assert.Null(ambiguous.Path);
        Assert.Equal(second, resolver.Resolve(["D:/old/a.pmx"], second).Path);
    }

    [Fact]
    public void ExistingAlternatelyDecodedPathsMustNotSilentlyPickOne()
    {
        string project = FileAt("project/scene.pmm"), first = FileAt("project/中文.pmx"), second = FileAt("project/日本語.pmx");
        var resolver = new PmmAssetPathResolver(project);
        Assert.Equal(PmmAssetResolutionStatus.Ambiguous, resolver.Resolve([first, second]).Status);
        Assert.Equal(PmmAssetResolutionStatus.Missing, resolver.Resolve(["missing.pmx"]).Status);
        Assert.Equal(PmmAssetResolutionStatus.Missing, resolver.Resolve([first], Path.Combine(root, "absent.pmx")).Status);
    }

    [Fact]
    public void EachRawReferenceCanUseADifferentCodePage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string project = FileAt("project/scene.pmm"), chinese = FileAt("assets/模型库/中文.pmx"), japanese = FileAt("assets/センター.pmx");
        var resolver = new PmmAssetPathResolver(project);
        foreach (var (path, page, wrongPage) in new[] { (chinese, 936, 932), (japanese, 932, 936) })
        {
            byte[] raw = Encoding.GetEncoding(page).GetBytes(path);
            var reference = new PmmString(0, Encoding.GetEncoding(wrongPage).GetString(raw), raw);
            Assert.Equal(path, resolver.Resolve(reference).Path);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
