using System.Text;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class EffectDependencyFileSourceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dependency-source-" + Guid.NewGuid().ToString("N"));
    public EffectDependencyFileSourceTests() => Directory.CreateDirectory(directory);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static D3D9EffectDependencySourceRequest Root(string path) =>
        new(null, new(Encoding.UTF8.GetBytes(path), 65001), D3D9EffectDependencyRequestKind.Root);

    [Fact]
    public void RequestedFilesStayLeasedThenRemainDetachedAfterSourcesMove()
    {
        string fx = Path.Combine(directory, "main.fx"), resource = Path.Combine(directory, "only.bin");
        File.WriteAllBytes(fx, [1, 2]); File.WriteAllBytes(resource, [3, 4]);
        File.WriteAllBytes(Path.Combine(directory, "unrequested.bin"), [5]);
        D3D9EffectDependencySnapshot frozen;
        using (var source = new D3D9EffectDependencyFileSource(new(MaximumFiles: 2)))
        {
            source.Capture(Root(fx), Token);
            frozen = source.Capture(new(fx, new(Encoding.UTF8.GetBytes("only.bin"), 65001),
                D3D9EffectDependencyRequestKind.ResourceName), Token);
            Assert.Throws<IOException>(() => File.WriteAllBytes(resource, [9]));
            Assert.Same(frozen, source.Capture(Root(resource), Token));
        }
        Directory.Move(directory, directory + "-moved");
        directoryMoved = true;
        Assert.Equal(new byte[] { 3, 4 }, frozen.CopyBytes());
        var exposed = frozen.CopyBytes(); exposed[0] = 99;
        Assert.Equal(new byte[] { 3, 4 }, frozen.CopyBytes());
    }

    [Fact]
    public void CompleteCp932ReferenceIsDecodedBeforeSeparatingDirectories()
    {
        string fx = Path.Combine(directory, "main.fx"), include = Path.Combine(directory, "ソ.inc");
        File.WriteAllBytes(include, [7]);
        var cp932 = CodePagesEncodingProvider.Instance.GetEncoding(932)!;
        byte[] raw = cp932.GetBytes("ソ.inc");
        Assert.Equal(0x5c, raw[1]);
        using var source = new D3D9EffectDependencyFileSource();
        var frozen = source.Capture(new(fx, new(raw, 932), D3D9EffectDependencyRequestKind.IncludeLocal), Token);
        Assert.Equal(include, frozen.OriginAbsolutePath, ignoreCase: true);
        Assert.Equal(new byte[] { 7 }, frozen.CopyBytes());
    }

    [Fact]
    public void MissingFileAbortsCaptureAndReleasesEarlierLeases()
    {
        string fx = Path.Combine(directory, "main.fx"); File.WriteAllBytes(fx, [1]);
        using var source = new D3D9EffectDependencyFileSource(); source.Capture(Root(fx), Token);
        var error = Assert.Throws<D3D9EffectDependencyException>(() => source.Capture(
            new(fx, new(Encoding.UTF8.GetBytes("missing.inc"), 65001), D3D9EffectDependencyRequestKind.IncludeSystem), Token));
        Assert.Equal(D3D9EffectDependencyStatus.Incomplete, error.Status);
        Assert.Equal(Path.Combine(directory, "missing.inc"), error.CandidateOriginAbsolutePath);
        Assert.Throws<ObjectDisposedException>(() => source.Capture(Root(fx), Token));
        File.WriteAllBytes(fx, [2]);
    }

    [Fact]
    public void InvalidEncodingAndDriveRelativePathsCannotOpenFiles()
    {
        using var source = new D3D9EffectDependencyFileSource();
        var encoding = Assert.Throws<D3D9EffectDependencyException>(() => source.Capture(
            new(null, new(new byte[] { 0xff }, 65001), D3D9EffectDependencyRequestKind.Root), Token));
        Assert.Equal(D3D9EffectDependencyStatus.Unsupported, encoding.Status);
        using var other = new D3D9EffectDependencyFileSource();
        var path = Assert.Throws<D3D9EffectDependencyException>(() => other.Capture(Root("C:relative.fx"), Token));
        Assert.Equal(D3D9EffectDependencyStatus.Incomplete, path.Status);
    }

    private bool directoryMoved;
    public void Dispose() => Directory.Delete(directoryMoved ? directory + "-moved" : directory, recursive: true);
}
