using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class CharacterToonVariantTests
{
    private const int Width = 97, Height = 73;
    private static readonly CharacterToonResponse Response = new(.5f, .1f);

    // These are the actual surface entry points selected by GetFramePipelines.
    // SubTexture always enables texture morphs and explicitly rejects GI.
    [Theory]
    [InlineData("PSSurface", false, false, false, false)]
    [InlineData("PSSurface", false, false, true, false)]
    [InlineData("PSSurfaceGi", false, true, false, false)]
    [InlineData("PSSurfaceGi", false, true, true, false)]
    [InlineData("PSSurfaceNative", true, false, false, false)]
    [InlineData("PSSurfaceNative", true, false, true, false)]
    [InlineData("PSSurfaceGiNative", true, true, false, false)]
    [InlineData("PSSurfaceGiNative", true, true, true, false)]
    [InlineData("PSSurface", false, false, true, true)]
    [InlineData("PSSurfaceNative", true, false, true, true)]
    public void EverySupportedSurfaceVariantCompilesToRealShaderBytecode(
        string entry, bool native, bool gi, bool morph, bool subTexture)
    {
        var bytecode = OriginalFrameShaders.CompileCharacterToon(entry, native, gi, morph, subTexture);
        Assert.True(bytecode.Length > 32);
        Assert.Equal(new byte[] { (byte)'D', (byte)'X', (byte)'B', (byte)'C' }, bytecode[..4]);
    }

    [Theory]
    [InlineData(.4f, false)]
    [InlineData(.6f, false)]
    [InlineData(.4f, true)]
    [InlineData(.6f, true)]
    [Trait("Category", "Gpu")]
    public void SubTextureModeThreeUsesRawLightForAlphaWhileItsColourUsesTheNewResponse(float c, bool native)
    {
        using var session = new D3D12FrameSession();
        var original = SubTexture(c, native);
        var selected = WithResponse(original, Response);
        var before = session.Capture(original, Width, Height);
        var after = session.Capture(selected, Width, Height);
        Assert.False(session.IsWarp);
        if (native) Assert.NotNull(session.NativeLightStatistics);

        const float raw = (128 / 255f) * (128 / 255f);
        float expectedAlpha = raw * (c + .2f * (1 - c));
        float mapped = c < .5f ? 0 : 1;
        var map = new Vector3(160 / 255f, 32 / 255f, 16 / 255f);
        var expectedRgb = .5f * map * (mapped + .4f * (1 - mapped)) * expectedAlpha;
        AssertPixel(after, new(expectedRgb, expectedAlpha));
        int center = (Height / 2 * Width + Width / 2) * 4;
        Assert.NotEqual(before.Rgba[center], after.Rgba[center]);
        // Remapping alpha to 0/1 would differ by more than twenty UNorm steps.
        float incorrectAlpha = raw * (mapped + .2f * (1 - mapped));
        Assert.True(Math.Abs(after.Rgba[center + 3] - incorrectAlpha * 255) > 15);
        for (int offset = 3; offset < before.Rgba.Length; offset += 4)
            Assert.Equal(before.Rgba[offset], after.Rgba[offset]);
        Assert.Equal(before.Rgba, session.Capture(original, Width, Height).Rgba);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Gpu")]
    public void SpecularOnlyTextureMorphSurfaceRetainsItsOriginalNonzeroHighlight(bool native)
    {
        using var session = new D3D12FrameSession();
        var mesh = D3D12MeshTests.Quad();
        const float d = .4f / 3;
        var vertices = mesh.Vertices.ToArray();
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = vertices[i] with { Normal = new(MathF.Sqrt(1 - d * d), 0, -d) };
        var material = new SceneMaterial("highlight only", 0, 6, new(0, 0, 0, 1),
            Specular: new(.7f, .4f, .2f), Ambient: Vector3.Zero, Shininess: 0);
        var snapshot = new SceneSnapshot(vertices, mesh.Indices, [material], camera: mesh.Camera, lightDirection: -Vector3.UnitZ);
        var binding = new OriginalStyleMaterialBinding(new(.2f), DoubleSided: true)
        {
            TextureFactors = OriginalMaterialTextureFactors.Identity with
            { Diffuse = new(new(.7f, .8f, .9f, 1), Vector4.Zero) }
        };
        var original = new OriginalFrameScene(new(snapshot, [binding],
            new(Vector3.One, Vector3.One, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero),
            lighting: native ? BackFacingNativeLight() : null);
        var host = OriginalStyleMath.ResolveHostColors(material, original.Surface.Profile);
        Assert.Equal(new Vector4(0, 0, 0, 1), host.EgColor);
        Assert.Equal(new Vector4(.7f, .4f, .2f, .1f), host.SpecularPower);
        var before = session.Capture(original, Width, Height);
        var after = session.Capture(WithResponse(original, Response), Width, Height);
        if (native) Assert.NotNull(session.NativeLightStatistics);
        Assert.True(before.Rgba[(Height / 2 * Width + Width / 2) * 4] > 40,
            "The control must contain a real highlight; mapped c=0 would erase it.");
        Assert.Equal(before.Rgba, after.Rgba);
    }

    [Theory]
    [InlineData(.5f, .2f)]
    [InlineData(.6f, .1f)]
    [Trait("Category", "Gpu")]
    public void TemporalGiComparesResponseValuesAndOnlySubmittedChangesResetHistory(float threshold, float width)
    {
        using var session = new D3D12FrameSession();
        using var control = new D3D12FrameSession();
        using var fresh = new D3D12FrameSession();
        var source = D3D12RayGiTests.Bounce();
        var gi = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: .5f, SamplesPerPixel: 2, Seed: 31,
            MaxRayDistance: 50, RayEpsilon: .001f, Composition: RayGiComposition.ToonPalette,
            Denoiser: RayGiDenoiser.TemporalSpatial);
        var original = WithResponse(source, Response);
        for (ulong count = 1; count <= 2; count++)
        {
            // Equal newly allocated records must retain the submitted history.
            var equal = WithResponse(source, new(.5f, .1f));
            Assert.Equal(control.Capture(original, Width, Height, gi).Rgba,
                session.Capture(equal, Width, Height, gi).Rgba);
            Assert.Equal(count, session.RayGiStatistics!.TemporalHistoryFrames);
            Assert.Equal(count == 1 ? "FirstFrame" : "None", session.RayGiStatistics.TemporalResetReason);
        }
        var changed = WithResponse(source, new(threshold, width));
        long submissions = session.Runtime.Submissions;
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Render(changed, 0, Height, gi));
        Assert.Throws<OperationCanceledException>(() => session.RenderForPresent(changed, Width, Height, gi,
            (_, _) => throw new OperationCanceledException("Do not commit changed response history.")));
        Assert.Equal(submissions, session.Runtime.Submissions);
        Assert.False(session.Runtime.IsRecording);
        Assert.Equal(control.Capture(original, Width, Height, gi).Rgba,
            session.Capture(original, Width, Height, gi).Rgba);
        Assert.Equal(3UL, session.RayGiStatistics!.TemporalHistoryFrames);
        Assert.Equal("None", session.RayGiStatistics.TemporalResetReason);
        Assert.Equal(control.RayGiStatistics!.Seed, session.RayGiStatistics.Seed);

        var reset = session.Capture(changed, Width, Height, gi);
        Assert.Equal("ReceiverResponseChanged", session.RayGiStatistics.TemporalResetReason);
        Assert.True(session.RayGiStatistics.TemporalResetHistory);
        Assert.Equal(1UL, session.RayGiStatistics.TemporalHistoryFrames);
        Assert.Equal(fresh.Capture(changed, Width, Height, gi).Rgba, reset.Rgba);
        using var continued = session.Render(WithResponse(source, new(threshold, width)), Width, Height, gi);
        Assert.Equal(0, continued.Statistics.ReadbackCount);
        Assert.Equal(2UL, session.RayGiStatistics.TemporalHistoryFrames);
        Assert.Equal("None", session.RayGiStatistics.TemporalResetReason);
    }

    private static OriginalFrameScene SubTexture(float c, bool native)
    {
        var mesh = D3D12MeshTests.Quad();
        var vertices = mesh.Vertices.ToArray();
        float d = c / 3;
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = vertices[i] with { Normal = new(MathF.Sqrt(1 - d * d), 0, -d), Uv = new(.9f) };
        var stream = new SceneAdditionalUvs(1, Enumerable.Repeat(
            new SceneAdditionalUvVertex(new(.25f, .25f, 31, 37), default, default, default), 4).ToArray());
        var material = new SceneMaterial("two map alpha", 0, 6, Vector4.One, Vector3.Zero, Vector3.Zero, 1, 0);
        var snapshot = new SceneSnapshot(vertices, mesh.Indices, [material],
            [new("diffuse", 1, 1, [255, 255, 255, 128]), new("uv1", 1, 1, [160, 32, 16, 128])],
            camera: mesh.Camera, lightDirection: -Vector3.UnitZ, additionalUvs: stream);
        var binding = new OriginalStyleMaterialBinding(new(.4f), 1, OriginalSphereMode.SubTexture, true)
        {
            TextureFactors = OriginalMaterialTextureFactors.Identity with
            { Toon = new(new(1, 1, 1, .2f), Vector4.Zero) }
        };
        return new(new(snapshot, [binding], new(new(.5f), Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: Vector4.Zero),
            lighting: native ? BackFacingNativeLight() : null);
    }

    // An active light selects the real native shader/PSO but points away from the quad.
    private static SceneLightingSnapshot BackFacingNativeLight() => new([
        new(Guid.Parse("29585d76-7f79-4e95-8be7-43c8245c1b66"), "away from receiver", SceneLightKind.Directional,
            Vector3.Zero, Quaternion.Identity, Vector3.One, CastShadows: false)]);

    private static OriginalFrameScene WithResponse(OriginalFrameScene source, CharacterToonResponse response) =>
        new(source.Surface, source.Outline, source.OutlineOptions, source.Options,
            source.Materials.Select(flags => flags with { CharacterToonResponse = response }), source.Lighting, source.EditorOverlay);

    private static void AssertPixel(RenderedFrame frame, Vector4 expected)
    {
        int offset = (Height / 2 * Width + Width / 2) * 4;
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(Math.Abs(frame.Rgba[offset + channel] - expected[channel] * 255), 0, 2f);
    }
}
