using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ViewTextureFactorOutputTests
{
    [Fact]
    public void ViewAdapterAcceptsEvaluatedTextureFactorsWithoutRecomputingThem()
    {
        var (scene, channels) = Fixture();
        var resolved = D3D12ViewScene.Resolve(scene, null, null, null, channels, new());
        Assert.Same(scene.Surface, resolved.Surface);
        Assert.Equal(scene.Surface.Materials[0].TextureFactors, resolved.Surface.Materials[0].TextureFactors);
    }

    [Fact]
    public void ActorLocalUvChannelsDoNotReplaceTheComposedVertexOrderStream()
    {
        var (scene, channels) = Fixture();
        var mesh = scene.Surface.Snapshot;
        var stream = new SceneAdditionalUvs(1, Enumerable.Repeat(
            new SceneAdditionalUvVertex(new(.25f, .75f, 3, 4), default, default, default), mesh.Vertices.Length).ToArray());
        scene = new(new(mesh.WithAdditionalUvs(stream), scene.Surface.Materials, scene.Surface.Profile));
        // The auxiliary packet is actor-local and deliberately has a different count/value.
        // It cannot identify a vertex range in the combined surface and must not overwrite it.
        channels[channels.Keys.Single()] = new([new(Vector4.One, Vector4.Zero, Vector4.Zero, Vector4.Zero)], []);
        var resolved = D3D12ViewScene.Resolve(scene, null, null, null, channels, new());
        Assert.Same(scene.Surface, resolved.Surface);
        Assert.Same(stream, resolved.Surface.Snapshot.AdditionalUvs);
        Assert.Equal(new Vector4(.25f, .75f, 3, 4), resolved.Surface.Snapshot.AdditionalUvs!.Vertices[0].Uv1);
        // Actor-local values also cannot make an invalid SubTexture surface valid.
        Assert.Throws<ArgumentException>(() => new OriginalStyleScene(mesh,
            [scene.Surface.Materials[0] with { SphereMode = OriginalSphereMode.SubTexture }], scene.Surface.Profile));
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task PublicCaptureSamplesCanonicalUv1AndIgnoresConflictingActorLocalValues()
    {
        var source = D3D12OriginalStyleTests.Fixture(OriginalSphereMode.None, 1);
        var mesh = source.Snapshot;
        var material = mesh.Materials[0] with { Diffuse = Vector4.One, Ambient = Vector3.Zero, Specular = Vector3.Zero };
        var textures = new SceneTexture[] {
            new("white diffuse", 1, 1, [255, 255, 255, 255]),
            new("uv1 red green", 2, 1, [255, 0, 0, 255, 0, 255, 0, 255]) };
        OriginalFrameScene Scene(float u)
        {
            var stream = new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(
                new(u, .25f, 11, 13), default, default, default), mesh.Vertices.Length).ToArray());
            var snapshot = new SceneSnapshot(mesh.Vertices, mesh.Indices, [material], textures,
                mesh.World, mesh.Camera, mesh.LightDirection, stream);
            return new(new(snapshot, [new(Vector3.One, 1, OriginalSphereMode.SubTexture, true)], source.Profile),
                options: new(OriginalAlphaBlendMode.Normal, true, false));
        }
        var sample = new OfflineFramePlan(96, 72, new(30), 0, 0).GetSample(0);
        var channels = new Dictionary<Guid, CharacterMorphChannels> {
            [Guid.NewGuid()] = new([new(new(.75f, .25f, 0, 0), default, default, default)], []) };
        var packet = new OfflineFrame(sample, 96, 72, Scene(.25f), null, null, null) { MorphChannels = channels };
        await using var renderer = new D3D12ViewRenderer(new(96, 72));
        using var red = await renderer.CaptureAsync(packet, TestContext.Current.CancellationToken);
        using var green = await renderer.CaptureAsync(packet with { Scene = Scene(.75f) }, TestContext.Current.CancellationToken);
        int center = (36 * 96 + 48) * 4;
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, red.Pixels.Span.Slice(center, 4).ToArray());
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, green.Pixels.Span.Slice(center, 4).ToArray());
        Assert.Equal(1, renderer.Statistics!.ReadbackCount);
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task PublicCaptureConsumesTransportedBindingsAndMatchesDirectFrameReference()
    {
        var (scene, channels) = Fixture();
        var sample = new OfflineFramePlan(96, 72, new(30), 0, 0).GetSample(0);
        var packet = new OfflineFrame(sample, 96, 72, scene, null, null, null) { MorphChannels = channels };
        await using var renderer = new D3D12ViewRenderer(new(96, 72));
        using var reference = new D3D12FrameSession();
        using var output = await renderer.CaptureAsync(packet, TestContext.Current.CancellationToken);
        var expected = new OriginalFrameScene(scene.Surface, options: new ViewQualityOptions().ApplyTo(scene.Options));
        Assert.Equal(reference.Capture(expected, 96, 72).Rgba, output.Pixels.ToArray());
        var identitySurface = new OriginalStyleScene(scene.Surface.Snapshot,
            [scene.Surface.Materials[0] with { TextureFactors = OriginalMaterialTextureFactors.Identity }], scene.Surface.Profile);
        using var identity = await renderer.CaptureAsync(packet with { Scene = new(identitySurface),
            MorphChannels = new Dictionary<Guid, CharacterMorphChannels>() }, TestContext.Current.CancellationToken);
        Assert.NotEqual(identity.Pixels.ToArray(), output.Pixels.ToArray());
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task GiWithNonidentityTextureFactorsMatchesDirectFrameReference()
    {
        var (scene, channels) = Fixture();
        var packet = new OfflineFrame(new OfflineFramePlan(96, 72, new(30), 0, 0).GetSample(0),
            96, 72, scene, null, null, null) { MorphChannels = channels };
        var quality = new ViewQualityOptions(Gi: new(Mode: LightingMode.SoftwareRayTracing));
        await using var renderer = new D3D12ViewRenderer(new(96, 72, quality));
        using var output = await renderer.CaptureAsync(packet, TestContext.Current.CancellationToken);
        using var reference = new D3D12FrameSession();
        var expected = new OriginalFrameScene(scene.Surface, options: quality.ApplyTo(scene.Options));
        Assert.Equal(reference.Capture(expected, 96, 72, rayGiOptions: quality.EffectiveGi).Rgba, output.Pixels.ToArray());
        Assert.Equal(1, renderer.Statistics!.ReadbackCount);
    }

    private static (OriginalFrameScene Scene, Dictionary<Guid, CharacterMorphChannels> Channels) Fixture()
    {
        var source = D3D12OriginalStyleTests.Fixture(OriginalSphereMode.Multiply, 1);
        var factor = new OriginalTextureFactor(new(.25f, .5f, 1, .75f), new(.125f, -.25f, .125f, -.25f));
        var surface = new OriginalStyleScene(source.Snapshot,
            [source.Materials[0] with { TextureFactors = OriginalMaterialTextureFactors.Identity with { Diffuse = factor } }], source.Profile);
        var raw = PmxMaterialTextureFactors.Identity with { Diffuse = new(factor.Multiply, factor.Add) };
        return (new(surface), new() { [Guid.NewGuid()] = new([], [raw]) });
    }
}
