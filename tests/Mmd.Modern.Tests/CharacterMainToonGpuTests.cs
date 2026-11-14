using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Actual GPU checks for the explicitly selected modern character response.
/// The root executes these serially; source compilation alone is not a passing result.</summary>
[Trait("Category", "Gpu")]
public sealed class CharacterMainToonGpuTests
{
    private const int ScanWidth = 425, ScanHeight = 81;
    private const float ScanStep = .245f, FirstX = -4.9f;
    private static readonly CharacterToonResponse Response = new(.5f, .1f);

    [Fact]
    public void FlatNormalScanKeepsOriginalEndpointsAndHasANarrowButGenuinelySoftTransition()
    {
        using var session = new D3D12FrameSession();
        Assert.False(session.IsWarp);
        var original = ScanFixture(false);
        var selected = ScanFixture(true);
        var continuous = session.Capture(original, ScanWidth, ScanHeight);
        var shaped = session.Capture(selected, ScanWidth, ScanHeight);
        int originalMiddle = 0, shapedMiddle = 0;
        for (int i = 0; i <= 40; i++)
        {
            float c = i / 40f;
            float t = Math.Clamp((c - .45f) / .1f, 0, 1);
            float weight = t * t * (3 - 2 * t);
            byte[] before = ScanPixel(continuous, original, i, .45f);
            byte[] after = ScanPixel(shaped, selected, i, .45f);
            float oldExpected = 255 * .6f * (.25f + .75f * c);
            float newExpected = 255 * .6f * (.25f + .75f * weight);
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.InRange(Math.Abs(before[channel] - oldExpected), 0, 1.1f);
                Assert.InRange(Math.Abs(after[channel] - newExpected), 0, 1.1f);
            }
            if (before[0] > 40 && before[0] < 151) originalMiddle++;
            if (after[0] > 40 && after[0] < 151) shapedMiddle++;
        }
        Assert.True(originalMiddle > 30);
        Assert.InRange(shapedMiddle, 3, 5);
        Assert.Equal(ScanPixel(continuous, original, 0, .45f), ScanPixel(shaped, selected, 0, .45f));
        Assert.Equal(ScanPixel(continuous, original, 40, .45f), ScanPixel(shaped, selected, 40, .45f));
        byte dark = ScanPixel(shaped, selected, 17, .45f)[0];
        byte lit = ScanPixel(shaped, selected, 23, .45f)[0];
        byte lowSoft = ScanPixel(shaped, selected, 19, .45f)[0];
        byte highSoft = ScanPixel(shaped, selected, 21, .45f)[0];
        Assert.True(dark < lowSoft && lowSoft < highSoft && highSoft < lit,
            "Both sides of the narrow transition must contain intermediate values, not a hard step.");
        AssertCoverage(continuous, shaped);
    }

    [Fact]
    public void MixedMaterialsKeepEveryInactivePixelExactAndDisablingRestoresTheWholeLegacyFrame()
    {
        using var session = new D3D12FrameSession();
        var original = ScanFixture(false);
        var mixed = ScanFixture(true);
        var before = session.Capture(original, ScanWidth, ScanHeight);
        var after = session.Capture(mixed, ScanWidth, ScanHeight);
        int changed = 0, inactiveOpaque = 0;
        int inactiveY = Project(original, new(0, -.45f, 0), ScanWidth, ScanHeight).Y;
        for (int y = 0; y < ScanHeight; y++)
        for (int x = 0; x < ScanWidth; x++)
        {
            int offset = (y * ScanWidth + x) * 4;
            // The two rows are separated by a gap. Select the inactive half using
            // projection rather than assuming the API's framebuffer orientation.
            bool inactiveHalf = inactiveY > ScanHeight / 2 ? y >= ScanHeight / 2 : y < ScanHeight / 2;
            if (inactiveHalf)
            {
                Assert.Equal(before.Rgba.AsSpan(offset, 4).ToArray(), after.Rgba.AsSpan(offset, 4).ToArray());
                if (before.Rgba[offset + 3] != 0) inactiveOpaque++;
            }
            else if (!before.Rgba.AsSpan(offset, 4).SequenceEqual(after.Rgba.AsSpan(offset, 4))) changed++;
        }
        Assert.True(inactiveOpaque > 500, "The control must include an actually rasterized inactive material row.");
        Assert.True(changed > 500, "The selected character material must visibly use the new response.");
        AssertCoverage(before, after);
        Assert.Equal(before.Rgba, session.Capture(original, ScanWidth, ScanHeight).Rgba);
        long readbacks = session.Runtime.Readbacks;
        using var rendered = session.Render(mixed, ScanWidth, ScanHeight);
        Assert.Equal(0, rendered.Statistics.ReadbackCount);
        Assert.Equal(readbacks, session.Runtime.Readbacks);
    }

    [Theory]
    [InlineData(LightingMode.SoftwareRayTracing)]
    [InlineData(LightingMode.HardwareRayTracing)]
    public void ActualGiUsesTheShapedPaletteBudgetAndPreservesAlphaAndVisibleOutline(LightingMode mode)
    {
        using var session = new D3D12FrameSession();
        var support = session.QueryRayGiCapabilities();
        Assert.True(mode == LightingMode.HardwareRayTracing ? support.HardwareAvailable : support.SoftwareAvailable,
            support.UnavailableReason);
        Assert.False(session.IsWarp);
        const int size = 81;
        var frame = GiFixture();
        var gi = new RayGiOptions(mode, Strength: 20, SamplesPerPixel: 128, Seed: 17,
            MaxRayDistance: 50, RayEpsilon: .001f, DirectionalLightDirection: Vector3.UnitZ,
            DirectionalIrradiance: new(3), Composition: RayGiComposition.ToonPalette);
        var off = session.Capture(frame, size, size);
        var on = session.Capture(frame, size, size, rayGiOptions: gi);
        int center = (size / 2 * size + size / 2) * 4;
        // Flat receiver c=.525 is inside the narrow soft edge: shaped c=.84375.
        // Its remaining palette contrast is substantially smaller than the old c=.525 budget.
        const float shapedWeight = .84375f;
        float diffuseLit = .8f * (192 / 255f);
        float expectedOff = 255 * diffuseLit * (.1f + .9f * shapedWeight);
        float maximumFill = 255 * diffuseLit * .9f * (1 - shapedWeight) * .25f;
        int redFill = on.Rgba[center] - off.Rgba[center];
        Assert.True(redFill >= 2, "The bounded response must still contain actual traced indirect illumination.");
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.InRange(Math.Abs(off.Rgba[center + channel] - expectedOff), 0, 1.1f);
            Assert.InRange(on.Rgba[center + channel] - off.Rgba[center + channel], 0, maximumFill + 1.1f);
            Assert.InRange(Math.Abs((on.Rgba[center + channel] - off.Rgba[center + channel]) - redFill), 0, 1);
        }
        AssertCoverage(off, on);
        int outlinePixels = 0;
        for (int offset = 0; offset < off.Rgba.Length; offset += 4)
        {
            if (off.Rgba[offset] != 0 || off.Rgba[offset + 1] != 0 || off.Rgba[offset + 2] != 0 || off.Rgba[offset + 3] != 255) continue;
            outlinePixels++;
            Assert.Equal(off.Rgba.AsSpan(offset, 4).ToArray(), on.Rgba.AsSpan(offset, 4).ToArray());
        }
        Assert.True(outlinePixels > 5, "The independent edge-only quad must be visible in this fixture.");
        Assert.Equal(off.Rgba, session.Capture(frame, size, size, rayGiOptions: gi with { Strength = 0 }).Rgba);
        Assert.Equal(off.Rgba, session.Capture(frame, size, size).Rgba);
        long readbacks = session.Runtime.Readbacks;
        using var rendered = session.Render(frame, size, size, rayGiOptions: gi);
        Assert.Equal(0, rendered.Statistics.ReadbackCount);
        Assert.Equal(readbacks, session.Runtime.Readbacks);
        Assert.Equal(mode, session.RayGiStatistics!.Effective);
    }

    private static OriginalFrameScene ScanFixture(bool modern)
    {
        var vertices = new List<SceneVertex>(); var indices = new List<uint>();
        var materials = new List<SceneMaterial>(); var bindings = new List<OriginalStyleMaterialBinding>();
        var flags = new List<OriginalFrameMaterialFlags>();
        for (int row = 0; row < 2; row++)
        for (int i = 0; i <= 40; i++)
        {
            float x = FirstX + i * ScanStep, y = row == 0 ? .45f : -.45f;
            float nDotL = i / 120f;
            var normal = new Vector3(nDotL, 0, -MathF.Sqrt(1 - nDotL * nDotL));
            uint start = (uint)vertices.Count; int offset = indices.Count;
            vertices.AddRange([new(new(x-.105f,y-.3f,0),normal,Vector2.Zero),new(new(x+.105f,y-.3f,0),normal,Vector2.Zero),
                new(new(x+.105f,y+.3f,0),normal,Vector2.Zero),new(new(x-.105f,y+.3f,0),normal,Vector2.Zero)]);
            indices.AddRange(new uint[] {0,2,1,0,3,2}.Select(index => start + index));
            materials.Add(new($"row {row} normal {i}",offset,6,new(.6f,.6f,.6f,1),Vector3.Zero,Vector3.Zero,1));
            bindings.Add(new(new(.25f)));
            flags.Add(new(false,false,false) { CharacterToonResponse = modern && row == 0 ? Response : null });
        }
        var snapshot = new SceneSnapshot(vertices.ToArray(),indices.ToArray(),materials,[],
            camera:new(new(0,0,-6),Vector3.Zero,Vector3.UnitY,2.2f,.1f,30),lightDirection:Vector3.UnitX);
        return new(new(snapshot,bindings,new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),
            options:new(OriginalAlphaBlendMode.Normal,true,false,
                // Explicitly keep all receiver projections inside; outside the map the
                // legacy shader deliberately skips toon, which is a different contract.
                SelfShadow:new(Matrix4x4.CreateScale(.1f,.5f,.1f),32,32,false,false,
                    OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual)),materials:flags);
    }

    private static OriginalFrameScene GiFixture()
    {
        var source = D3D12GiTextureFactorTests.Fixture();
        var old = source.Surface.Snapshot; var vertices = old.Vertices.ToArray().ToList();
        const float nDotL = .175f;
        var normal = new Vector3(nDotL,0,-MathF.Sqrt(1-nDotL*nDotL));
        for (int i = 0; i < 4; i++) vertices[i] = vertices[i] with { Normal = normal };
        vertices.AddRange([new(new(1.05f,-.25f,-.1f),-Vector3.UnitZ,Vector2.Zero),new(new(1.55f,-.25f,-.1f),-Vector3.UnitZ,Vector2.Zero),
            new(new(1.55f,.25f,-.1f),-Vector3.UnitZ,Vector2.Zero),new(new(1.05f,.25f,-.1f),-Vector3.UnitZ,Vector2.Zero)]);
        var materials = old.Materials.ToArray().ToList();
        materials[0] = materials[0] with { Diffuse = new(.8f,.8f,.8f,1) };
        materials.Add(new("visible edge only",12,6,Vector4.One,Vector3.Zero,Vector3.Zero,1));
        var mesh = new SceneSnapshot(vertices.ToArray(),old.Indices.ToArray().Concat(new uint[] {8,9,10,8,10,11}).ToArray(),
            materials,old.Textures,old.World,old.Camera,old.LightDirection);
        var surface = new OriginalStyleScene(mesh,[new(new(.1f)),source.Surface.Materials[1],new(Vector3.One)],
            new(Vector3.One,Vector3.Zero,Matrix4x4.Identity,OriginalTextureSampling.PointClamp));
        var edge = vertices.Select((v,i) => new OriginalOutlineVertex(v.Position,Vector3.Zero,OriginalOutlineVertexMode.Expand,1,i/4));
        var outline = new OriginalOutlineData(edge,[new(0,Vector4.Zero,false),new(0,Vector4.Zero,false),new(1,new(0,0,0,1))]);
        return new(surface,outline,new(new(0,0,0,1),Matrix4x4.Identity,old.Camera.Eye,30,20),
            source.Options! with { SelfShadow = new(Matrix4x4.CreateScale(.2f,.2f,.1f),32,32,false,false,
                OriginalTextureSampling.PointClamp,OriginalDepthComparison.LessEqual) },
            [new(false,false,false) { CharacterToonResponse = Response, GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands },
             new(false,false,false),new(false,false,false,SurfaceEnabled:false)]);
    }

    private static (int X,int Y) Project(OriginalFrameScene scene,Vector3 position,int width,int height)
    {
        var clip = Vector4.Transform(new Vector4(position,1),scene.Surface.Snapshot.Camera.ViewProjection(width,height));
        return ((int)((clip.X/clip.W*.5f+.5f)*width),(int)((.5f-clip.Y/clip.W*.5f)*height));
    }

    private static byte[] ScanPixel(RenderedFrame frame,OriginalFrameScene scene,int index,float y)
    {
        var point = Project(scene,new(FirstX+index*ScanStep,y,0),ScanWidth,ScanHeight);
        return frame.Rgba.AsSpan((point.Y*ScanWidth+point.X)*4,4).ToArray();
    }

    private static void AssertCoverage(RenderedFrame original,RenderedFrame selected)
    {
        for (int i = 0; i < original.Rgba.Length; i += 4)
        {
            Assert.Equal(original.Rgba[i+3],selected.Rgba[i+3]);
            if (original.Rgba[i+3] == 0)
                Assert.Equal(original.Rgba.AsSpan(i,4).ToArray(),selected.Rgba.AsSpan(i,4).ToArray());
        }
    }
}
