using System.Security.Cryptography;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12GeneratedPmxMaterialMapsTests
{
    [Fact]
    public void GeneratedMapsAreDeterministicLabelledSnapshotsAndNeverChangeSource()
    {
        byte[] pixels = Pattern();
        var source = new SceneTexture("original.png", 16, 16, pixels);
        byte[] original = source.Rgba.ToArray();
        var options = new D3D12GeneratedPmxMapOptions(MapSize: 32);
        var first = D3D12GeneratedPmxMaterialMaps.Generate(source, options, TestContext.Current.CancellationToken);
        var second = D3D12GeneratedPmxMaterialMaps.Generate(source, options, TestContext.Current.CancellationToken);

        Assert.Equal(original, source.Rgba.ToArray());
        Assert.Equal(options, first.Options);
        Assert.True(first.IsGenerated);
        Assert.Equal("mda-pmx-aux-v1", first.GeneratorRevision);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), first.SourceRgbaSha256);
        foreach (var pair in new[] { (first.Normal, second.Normal), (first.P, second.P), (first.Matcap, second.Matcap) })
        {
            Assert.StartsWith("GENERATED/", pair.Item1.Name);
            Assert.Equal(SceneTextureKind.Image, pair.Item1.Kind);
            Assert.Equal(32, pair.Item1.Width);
            Assert.Equal(32, pair.Item1.Height);
            Assert.Equal(pair.Item1.Name, pair.Item2.Name);
            Assert.Equal(pair.Item1.Rgba.ToArray(), pair.Item2.Rgba.ToArray());
        }
        pixels.AsSpan().Fill(0);
        Assert.Equal(original, source.Rgba.ToArray());
    }

    [Fact]
    public void DefaultNormalIsFlatEvenForHighContrastSource()
    {
        var maps = D3D12GeneratedPmxMaterialMaps.Generate(Source(), new(MapSize: 16), TestContext.Current.CancellationToken);
        for (int i = 0; i < maps.Normal.Rgba.Length; i += 4)
            Assert.Equal(new byte[] { 128, 128, 255, 255 }, maps.Normal.Rgba.Slice(i, 4).ToArray());
    }

    [Fact]
    public void OptionalLuminanceHeightProducesNormalsAndRepeatsAtTextureSeam()
    {
        var maps = D3D12GeneratedPmxMaterialMaps.Generate(Source(), new(MapSize: 16, HeightFromLuminanceStrength: 2), TestContext.Current.CancellationToken);
        byte[] pixels = maps.Normal.Rgba.ToArray();
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4] != 128 || pixels[i * 4 + 1] != 128);
        // Source increases left-to-right; at x=0 the periodic left neighbor is the brightest column.
        Assert.True(pixels[0] > 128);
        Assert.True(pixels[4] < 128);
    }

    [Fact]
    public void HiddenTransparentRgbCannotAffectGeneratedMapsOrAdjacentNormals()
    {
        byte[] first = Pattern();
        int hidden = (7 * 16 + 7) * 4;
        first[hidden + 3] = 1;
        byte[] second = (byte[])first.Clone();
        second[hidden] = 255; second[hidden + 1] = 0; second[hidden + 2] = 255;
        var options = new D3D12GeneratedPmxMapOptions(MapSize: 16, HeightFromLuminanceStrength: 2);
        var a = D3D12GeneratedPmxMaterialMaps.Generate(new("a", 16, 16, first), options, TestContext.Current.CancellationToken);
        var b = D3D12GeneratedPmxMaterialMaps.Generate(new("b", 16, 16, second), options, TestContext.Current.CancellationToken);
        Assert.Equal(a.Normal.Rgba.ToArray(), b.Normal.Rgba.ToArray());
        Assert.Equal(a.P.Rgba.ToArray(), b.P.Rgba.ToArray());
        Assert.Equal(a.Matcap.Rgba.ToArray(), b.Matcap.Rgba.ToArray());
        foreach (int pixel in new[] { 7 * 16 + 7, 7 * 16 + 6, 7 * 16 + 8, 6 * 16 + 7, 8 * 16 + 7 })
            Assert.Equal(new byte[] { 128, 128, 255, 255 }, a.Normal.Rgba.Slice(pixel * 4, 4).ToArray());
    }

    [Theory]
    [InlineData(D3D12GeneratedPmxSurfaceKind.Cloth, 20)]
    [InlineData(D3D12GeneratedPmxSurfaceKind.Coated, 77)]
    [InlineData(D3D12GeneratedPmxSurfaceKind.Metal, 204)]
    [InlineData(D3D12GeneratedPmxSurfaceKind.Hair, 51)]
    public void ExplicitSurfaceKindSetsMaskWithoutInferringFromSource(D3D12GeneratedPmxSurfaceKind kind, byte expected)
    {
        var maps = D3D12GeneratedPmxMaterialMaps.Generate(Source(), new(kind, MapSize: 16), TestContext.Current.CancellationToken);
        for (int i = 0; i < maps.P.Rgba.Length; i += 4)
            Assert.Equal(new byte[] { expected, 0, 255, 0 }, maps.P.Rgba.Slice(i, 4).ToArray());
        var explicitMask = D3D12GeneratedPmxMaterialMaps.Generate(Source(), new(kind, MapSize: 16, HighlightMask: .6f), TestContext.Current.CancellationToken);
        Assert.Equal(153, explicitMask.P.Rgba[0]);
    }

    [Fact]
    public void MatcapIsSpatiallyVaryingOriginalGaussianAndRoleWidthsDiffer()
    {
        var cloth = D3D12GeneratedPmxMaterialMaps.Generate(Source(), new(MapSize: 32), TestContext.Current.CancellationToken);
        var metal = D3D12GeneratedPmxMaterialMaps.Generate(Source(), new(D3D12GeneratedPmxSurfaceKind.Metal, MapSize: 32), TestContext.Current.CancellationToken);
        int center = (10 * 32 + 10) * 4, far = (31 * 32 + 31) * 4;
        Assert.True(cloth.Matcap.Rgba[center] > cloth.Matcap.Rgba[far]);
        Assert.True(cloth.Matcap.Rgba[far] > metal.Matcap.Rgba[far]);
        for (int i = 0; i < cloth.Matcap.Rgba.Length; i += 4)
        {
            Assert.Equal(cloth.Matcap.Rgba[i], cloth.Matcap.Rgba[i + 1]);
            Assert.Equal(cloth.Matcap.Rgba[i], cloth.Matcap.Rgba[i + 2]);
            Assert.Equal(255, cloth.Matcap.Rgba[i + 3]);
        }
        var overridden = D3D12GeneratedPmxMaterialMaps.Generate(Source(), new(D3D12GeneratedPmxSurfaceKind.Metal, MapSize: 32, MatcapWidth: .6f), TestContext.Current.CancellationToken);
        Assert.Equal(cloth.Matcap.Rgba.ToArray(), overridden.Matcap.Rgba.ToArray());
    }

    public static IEnumerable<object[]> InvalidOptions()
    {
        yield return [new D3D12GeneratedPmxMapOptions((D3D12GeneratedPmxSurfaceKind)99)];
        foreach (int size in new[] { 0, 8, 17, 2048 }) yield return [new D3D12GeneratedPmxMapOptions(MapSize: size)];
        foreach (float strength in new[] { -1, 2.01f, float.NaN, float.PositiveInfinity })
            yield return [new D3D12GeneratedPmxMapOptions(HeightFromLuminanceStrength: strength)];
        foreach (float mask in new[] { -.01f, 1.01f, float.NaN, float.NegativeInfinity })
            yield return [new D3D12GeneratedPmxMapOptions(HighlightMask: mask)];
        foreach (float width in new[] { 0, .029f, 1.01f, float.NaN, float.PositiveInfinity })
            yield return [new D3D12GeneratedPmxMapOptions(MatcapWidth: width)];
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void InvalidSettingsAreRejected(D3D12GeneratedPmxMapOptions options)
        => Assert.Throws<ArgumentOutOfRangeException>(() => D3D12GeneratedPmxMaterialMaps.Generate(Source(), options, TestContext.Current.CancellationToken));

    [Fact]
    public void CancellationIsObservedBeforeMapAllocation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => D3D12GeneratedPmxMaterialMaps.Generate(Source(), new(MapSize: 1024), cancellation.Token));
    }

    [Fact]
    public void ProceduralToonSourceIsRejectedRatherThanLabelledAsAnOriginalImage()
    {
        var source = new SceneTexture("toon", 16, 16, Pattern(), SceneTextureKind.IndexedProceduralToon);
        Assert.Throws<ArgumentException>(() => D3D12GeneratedPmxMaterialMaps.Generate(source, new(), TestContext.Current.CancellationToken));
        var softToon = new SceneTexture("soft-toon", 16, 16, Pattern(), SceneTextureKind.ModernSoftToon);
        Assert.Throws<ArgumentException>(() => D3D12GeneratedPmxMaterialMaps.Generate(softToon, new(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NullInputsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => D3D12GeneratedPmxMaterialMaps.Generate(null!, new(), TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => D3D12GeneratedPmxMaterialMaps.Generate(Source(), null!, TestContext.Current.CancellationToken));
    }

    private static SceneTexture Source() => new("original", 16, 16, Pattern());
    private static byte[] Pattern()
    {
        byte[] pixels = new byte[16 * 16 * 4];
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
        {
            int i = (y * 16 + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(x * 17);
            pixels[i + 3] = 255;
        }
        return pixels;
    }
}
