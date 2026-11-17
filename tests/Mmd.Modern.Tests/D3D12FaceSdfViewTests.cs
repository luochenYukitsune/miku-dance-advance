using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12FaceSdfViewTests
{
    private const int Width = 129, Height = 97;

    [Theory, Trait("Category", "Gpu")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public async Task ExplicitGrayFaceBindingSurvivesViewSmaaAndSpatialSamplingWithoutChangingBodyOrCoverage(string preset)
    {
        var scene = Fixture();
        var instance = new SceneRenderingStyleBinding(Guid.NewGuid(), new(preset), ModelRole.Character, 0, 2, true);
        var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Smaa1X,
            SmaaQuality: SmaaQuality.High, SpatialSamples: 2);
        var request = new ViewRenderRequest(Width, Height, quality);
        var plan = new OfflineFramePlan(Width, Height, new(30), 0, 2);
        OfflineFrame At(int index) => new(plan.GetSample(index), Width, Height, scene, null, null, null)
        { RenderingStyles = Array.AsReadOnly(new[] { instance }) };
        var token = TestContext.Current.CancellationToken;

        await using var baselineRenderer = new D3D12ViewRenderer(request);
        Assert.False(baselineRenderer.IsWarp);
        Assert.False(string.IsNullOrWhiteSpace(baselineRenderer.AdapterName));
        using var baseline = await baselineRenderer.CaptureAsync(At(0), token);

        // These are explicit frame-table texture indices and already evaluated world axes.
        // No actor-local texture remapping, head-bone evaluation or name inference is claimed.
        async Task<byte[]> CaptureBound(Vector3 right)
        {
            var sdf = new D3D12FaceSdfBinding(0, -Vector3.UnitZ, right,
                preset == "wuthering-waves" ? .1f : 1f);
            D3D12ViewCharacterMaterialBinding[] selected =
                [new(instance.ObjectId, 0, new(D3D12CharacterMaterialRole.Face, 1, 1, sdf))];
            await using var renderer = new D3D12ViewRenderer(request, characterMaterials: selected);
            Assert.False(renderer.IsWarp);
            Assert.Equal(baselineRenderer.AdapterName, renderer.AdapterName);
            using var captured = await renderer.CaptureAsync(At(0), token);
            return captured.Pixels.ToArray();
        }

        byte[] left = await CaptureBound(Vector3.UnitX);
        byte[] right = await CaptureBound(-Vector3.UnitX);
        // Independent oracle: +X light gives center=.5. UV=.375 reads gray64/255;
        // -U with Repeat reads gray192/255. Both audited family ranges place these
        // on opposite plateaus despite unchanged +X geometric normals and diffuse.
        AssertRgb(left, Height / 3, new(.6f, .4f, .2f));
        AssertRgb(right, Height / 3, new(.12f, .08f, .04f));
        // The Face/SDF material skips stylized shading. In the baseline the same
        // material is an unbound Body with stylized shading, so compare only coverage.
        for (int i = 3; i < left.Length; i += 4) Assert.Equal(baseline.Pixels.Span[i], left[i]);

        int changed = 0, covered = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int offset = (y * Width + x) * 4;
            Assert.Equal(baseline.Pixels.Span[offset + 3], left[offset + 3]);
            Assert.Equal(baseline.Pixels.Span[offset + 3], right[offset + 3]);
            if (right[offset + 3] == 255) covered++;
            if (left[offset] != right[offset]) changed++;
        }
        Assert.True(changed > Width * Height / 10, $"Only {changed} pixels consumed the face mask.");
        Assert.True(covered > Width * Height / 3, "The zero-alpha numerical mask must not become surface coverage.");
        // The complete unbound body half, including its AA boundary and background,
        // is byte-identical. The mask cannot leak into the next material draw.
        int bodyOffset = Height / 2 * Width * 4;
        Assert.Equal(baseline.Pixels.Span[bodyOffset..].ToArray(), right.AsSpan(bodyOffset).ToArray());
        using var restored = await baselineRenderer.CaptureAsync(At(1), token);
        Assert.Equal(baseline.Pixels.ToArray(), restored.Pixels.ToArray());
    }

    private static OriginalFrameScene Fixture()
    {
        // Full-width strips avoid unrelated corner-weight differences when comparing
        // AA coverage. The gap isolates the selected face from the unbound body.
        SceneVertex[] Quad(float y) =>
            [new(new(-3, y - .35f, 0), Vector3.UnitX, new(.375f, .5f)),
             new(new(3, y - .35f, 0), Vector3.UnitX, new(.375f, .5f)),
             new(new(3, y + .35f, 0), Vector3.UnitX, new(.375f, .5f)),
             new(new(-3, y + .35f, 0), Vector3.UnitX, new(.375f, .5f))];
        var snapshot = new SceneSnapshot([.. Quad(.55f), .. Quad(-.55f)],
            [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6],
            [new("explicit face", 0, 6, new(.6f, .4f, .2f, 1), Vector3.Zero, Vector3.Zero, 1),
             new("unbound body", 6, 6, new(.6f, .4f, .2f, 1), Vector3.Zero, Vector3.Zero, 1)],
            [new("original asymmetric numerical mask", 4, 1,
                [0, 0, 0, 0, 64, 64, 64, 0, 192, 192, 192, 0, 255, 255, 255, 0])],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20),
            lightDirection: Vector3.UnitX);
        return new(new(snapshot, [new(new(.2f)), new(new(.2f))],
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero));
    }

    private static void AssertRgb(byte[] pixels, int y, Vector3 expected)
    {
        int offset = (y * Width + Width / 2) * 4;
        Assert.InRange(MathF.Abs(pixels[offset] / 255f - expected.X), 0, 1.5f / 255);
        Assert.InRange(MathF.Abs(pixels[offset + 1] / 255f - expected.Y), 0, 1.5f / 255);
        Assert.InRange(MathF.Abs(pixels[offset + 2] / 255f - expected.Z), 0, 1.5f / 255);
        Assert.Equal((byte)255, pixels[offset + 3]);
    }
}
