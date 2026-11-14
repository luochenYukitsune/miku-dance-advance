using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12ViewSceneLightingCacheTests
{
    private static readonly Dictionary<Guid, CharacterMorphChannels> Morphs = [];
    private static readonly PmmLightPose Light = new(
        new(BitConverter.Int32BitsToSingle(int.MinValue), BitConverter.Int32BitsToSingle(0x3eaaaaab), .75f),
        new(.75f, -1.25f, .125f));

    [Fact]
    public void RepeatedSampledLightDifferentFromSourceReusesResolvedSurfaceAndGiGeometry()
    {
        var source = Source();
        Assert.NotEqual(-Light.Direction, source.Surface.Snapshot.LightDirection);
        Assert.NotEqual(Light.Color, source.Surface.Profile.LightAmbient);
        var first = Resolve(source, Light);
        var transport = RayGiScene.FromOriginal(first);
        for (int i = 0; i < 4; i++)
        {
            var repeated = Resolve(source, Light);
            Assert.Same(first.Surface, repeated.Surface);
            Assert.Same(first.Surface.Snapshot, repeated.Surface.Snapshot);
            Assert.Equal(transport.GeometryIdentity, RayGiScene.FromOriginal(repeated).GeometryIdentity);
            Assert.Equal(transport.TexelIdentity, RayGiScene.FromOriginal(repeated).TexelIdentity);
            AssertLight(repeated, Light);
        }
        Assert.NotSame(source.Surface.Snapshot, first.Surface.Snapshot);
        Assert.Equal(new Vector3(.2f, .8f, -.3f), source.Surface.Snapshot.LightDirection);
        Assert.Equal(new Vector3(.13f), source.Surface.Profile.LightAmbient);
    }

    [Fact]
    public void ChangingOnlyResolvedCameraRetainsLightingGeometryAndUpdatesCameraOptions()
    {
        var source = Source();
        var a = source.Surface.Snapshot.Camera;
        var b = a with { Eye = a.Eye + new Vector3(2, 1, 0) };
        var first = Resolve(source, Light, a);
        var second = Resolve(source, Light, b);
        Assert.Same(first.Surface, second.Surface);
        Assert.Same(first.Surface.Snapshot, second.Surface.Snapshot);
        Assert.Equal(RayGiScene.FromOriginal(first).GeometryIdentity, RayGiScene.FromOriginal(second).GeometryIdentity);
        Assert.Equal(a, first.Options!.CameraOverride);
        Assert.Equal(b, second.Options!.CameraOverride);
        Assert.NotEqual(first.Options.SphereRotationOverride, second.Options.SphereRotationOverride);
        AssertLight(first, Light);
        AssertLight(second, Light);
        Assert.Equal(a, source.Surface.Snapshot.Camera);
    }

    [Fact]
    public void ChangedLightDirectionAndRgbReplaceOnlyTheLastResultWithoutMutatingPriorOutput()
    {
        var source = Source();
        var first = Resolve(source, Light);
        var turned = Light with { Direction = new(-.25f, -.5f, 2) };
        var second = Resolve(source, turned);
        Assert.NotSame(first.Surface, second.Surface);
        AssertLight(first, Light);
        AssertLight(second, turned);
        var recolored = turned with { Color = new(.125f, .625f, .875f) };
        var third = Resolve(source, recolored);
        Assert.NotSame(second.Surface, third.Surface);
        AssertLight(third, recolored);
        AssertLight(second, turned);
        AssertLight(first, Light);
        AssertLight(Resolve(source, Light), Light);
    }

    [Fact]
    public void EqualValuedSignedZeroRgbStillPreservesTheExactRequestedLightBits()
    {
        var source = Source();
        var positiveZero = Light with { Color = new(0, Light.Color.Y, Light.Color.Z) };
        var first = Resolve(source, positiveZero);
        var second = Resolve(source, Light);
        AssertLight(first, positiveZero);
        AssertLight(second, Light);
        AssertLight(first, positiveZero);
    }

    [Fact]
    public void ChangedBindingsOrProfileOnTheSameOriginalSnapshotDoNotReuseStaleStyle()
    {
        var source = Source();
        var first = Resolve(source, Light);
        var binding = source.Surface.Materials[0] with { ToonShadowColor = new(.1f, .2f, .3f), DoubleSided = true };
        var rebound = WithSurface(source, new(source.Surface.Snapshot, [binding], source.Surface.Profile));
        var second = Resolve(rebound, Light);
        Assert.NotSame(first.Surface, second.Surface);
        Assert.Equal(binding, second.Surface.Materials[0]);
        Assert.Equal(source.Surface.Materials[0], first.Surface.Materials[0]);
        var profile = rebound.Surface.Profile with { TextureSampling = OriginalTextureSampling.PointClamp,
            SphereRotation = Matrix4x4.CreateRotationY(.35f) };
        var restyled = WithSurface(rebound, new(rebound.Surface.Snapshot, rebound.Surface.Materials, profile));
        var third = Resolve(restyled, Light);
        Assert.NotSame(second.Surface, third.Surface);
        Assert.Equal(profile.TextureSampling, third.Surface.Profile.TextureSampling);
        Assert.Equal(profile.SphereRotation, third.Surface.Profile.SphereRotation);
        AssertLight(third, Light);
        AssertLight(first, Light);
    }

    [Fact]
    public void EqualDetachedStyleStateOnTheSameOriginalSnapshotCanReuseTheResolvedSurface()
    {
        var source = Source();
        var first = Resolve(source, Light);
        var copied = WithSurface(source, new(source.Surface.Snapshot,
            source.Surface.Materials.Select(value => value with { }), source.Surface.Profile with { }));
        Assert.NotSame(source.Surface, copied.Surface);
        Assert.Same(first.Surface, Resolve(copied, Light).Surface);
    }

    [Fact]
    public void EqualValuedSignedZeroInSourceRotationRetainsTheRequestedMatrixBits()
    {
        var source = Source();
        var first = Resolve(source, Light);
        var rotation = source.Surface.Profile.SphereRotation;
        rotation.M12 = BitConverter.Int32BitsToSingle(int.MinValue);
        var changed = WithSurface(source, new(source.Surface.Snapshot, source.Surface.Materials,
            source.Surface.Profile with { SphereRotation = rotation }));
        var second = Resolve(changed, Light);
        Assert.NotSame(first.Surface, second.Surface);
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(second.Surface.Profile.SphereRotation.M12));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(first.Surface.Profile.SphereRotation.M12));
    }

    [Fact]
    public void EqualValuedSignedZeroInSourceToonColorRetainsTheRequestedBindingBits()
    {
        var source = Source();
        var binding = source.Surface.Materials[0] with { ToonShadowColor = new(0, .25f, .5f) };
        var positive = WithSurface(source, new(source.Surface.Snapshot, [binding], source.Surface.Profile));
        var first = Resolve(positive, Light);
        var changedBinding = binding with { ToonShadowColor = new(BitConverter.Int32BitsToSingle(int.MinValue), .25f, .5f) };
        var changed = WithSurface(source, new(source.Surface.Snapshot, [changedBinding], source.Surface.Profile));
        var second = Resolve(changed, Light);
        Assert.NotSame(first.Surface, second.Surface);
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(second.Surface.Materials[0].ToonShadowColor.X));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(first.Surface.Materials[0].ToonShadowColor.X));
    }

    [Theory]
    [InlineData("diffuse-multiply")]
    [InlineData("diffuse-add")]
    [InlineData("sphere-multiply")]
    [InlineData("sphere-add")]
    [InlineData("toon-multiply")]
    [InlineData("toon-add")]
    public void EqualValuedSignedZeroInEachTextureFactorRetainsTheRequestedBits(string channel)
    {
        var source = Source();
        OriginalMaterialTextureFactors Factors(float zero)
        {
            var factors = OriginalMaterialTextureFactors.Identity;
            var factor = channel.StartsWith("diffuse", StringComparison.Ordinal) ? factors.Diffuse
                : channel.StartsWith("sphere", StringComparison.Ordinal) ? factors.Sphere : factors.Toon;
            if (channel.EndsWith("multiply", StringComparison.Ordinal))
            {
                var vector = factor.Multiply; vector.Z = zero; factor = factor with { Multiply = vector };
            }
            else
            {
                var vector = factor.Add; vector.Z = zero; factor = factor with { Add = vector };
            }
            return channel.StartsWith("diffuse", StringComparison.Ordinal) ? factors with { Diffuse = factor }
                : channel.StartsWith("sphere", StringComparison.Ordinal) ? factors with { Sphere = factor } : factors with { Toon = factor };
        }
        float Component(OriginalStyleMaterialBinding binding)
        {
            var factor = channel.StartsWith("diffuse", StringComparison.Ordinal) ? binding.TextureFactors.Diffuse
                : channel.StartsWith("sphere", StringComparison.Ordinal) ? binding.TextureFactors.Sphere : binding.TextureFactors.Toon;
            return channel.EndsWith("multiply", StringComparison.Ordinal) ? factor.Multiply.Z : factor.Add.Z;
        }
        var binding = source.Surface.Materials[0] with { TextureFactors = Factors(0) };
        var first = Resolve(WithSurface(source, new(source.Surface.Snapshot, [binding], source.Surface.Profile)), Light);
        var changedBinding = binding with { TextureFactors = Factors(BitConverter.Int32BitsToSingle(int.MinValue)) };
        var second = Resolve(WithSurface(source, new(source.Surface.Snapshot, [changedBinding], source.Surface.Profile)), Light);
        Assert.NotSame(first.Surface, second.Surface);
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(Component(second.Surface.Materials[0])));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(Component(first.Surface.Materials[0])));
    }

    [Fact]
    public void LastResolvedEntryDoesNotKeepAnUnreferencedSourceOrResultAlive()
    {
        var references = UnreferencedCachedSurfaces();
        for (int i = 0; i < 4 && (references.Source.IsAlive || references.Resolved.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(references.Source.IsAlive);
        Assert.False(references.Resolved.IsAlive);
    }

    [Theory]
    [InlineData("vertices")]
    [InlineData("world")]
    [InlineData("topology")]
    [InlineData("texture")]
    [InlineData("material")]
    public void ChangedOriginalSnapshotDoesNotReusePriorGeometryOrTextureTransport(string change)
    {
        var source = Source();
        var first = Resolve(source, Light);
        var original = source.Surface.Snapshot;
        var vertices = original.Vertices.ToArray();
        vertices[0] = vertices[0] with { Position = vertices[0].Position + new Vector3(.3f, 0, 0) };
        var snapshot = change switch
        {
            "vertices" => original.WithDeformedVertices(vertices),
            "world" => original.WithWorld(Matrix4x4.CreateTranslation(3, 0, 0)),
            "topology" => new SceneSnapshot(original.Vertices, [2, 1, 0], original.Materials, original.Textures),
            "texture" => new SceneSnapshot(original.Vertices, original.Indices, original.Materials,
                [new SceneTexture("replacement", 1, 1, [9, 8, 7, 255])]),
            "material" => original.WithMaterials(original.Materials.Select(value => value with { Diffuse = new(.3f, .4f, .5f, 1) })),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var changed = WithSurface(source, new(snapshot, source.Surface.Materials, source.Surface.Profile));
        var second = Resolve(changed, Light);
        Assert.NotSame(first.Surface, second.Surface);
        Assert.NotSame(first.Surface.Snapshot, second.Surface.Snapshot);
        var before = RayGiScene.FromOriginal(first);
        var after = RayGiScene.FromOriginal(second);
        Assert.NotEqual(before.GeometryIdentity, after.GeometryIdentity);
        AssertLight(second, Light);
        if (change is "vertices" or "world" or "topology") Assert.NotEqual(before.Triangles[0].P0, after.Triangles[0].P0);
        if (change == "texture")
        {
            Assert.Equal(0xff070809u, after.Texels[1]);
            Assert.Equal(0xff030201u, before.Texels[1]);
        }
        if (change == "material") Assert.NotEqual(before.Materials[0].DiffuseFactor, after.Materials[0].DiffuseFactor);
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(float.NaN, 1f, 0f)]
    [InlineData(float.PositiveInfinity, 1f, 0f)]
    public void CachedValidLightDoesNotHideInvalidNewDirection(float x, float y, float z)
    {
        var source = Source();
        Resolve(source, Light);
        Assert.Throws<ArgumentException>(() => Resolve(source, Light with { Direction = new(x, y, z) }));
    }

    private static OriginalFrameScene Resolve(OriginalFrameScene source, PmmLightPose light, SceneCamera? camera = null)
        => D3D12ViewScene.Resolve(source, null, light, null, Morphs, new(), resolvedCamera: camera);

    private static void AssertLight(OriginalFrameScene frame, PmmLightPose light)
    {
        Bits(-light.Direction, frame.Surface.Snapshot.LightDirection);
        Bits(light.Color, frame.Surface.Profile.LightAmbient);
        Bits(light.Color, frame.Surface.Profile.LightSpecular);
    }

    private static void Bits(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Z), BitConverter.SingleToInt32Bits(actual.Z));
    }

    private static OriginalFrameScene WithSurface(OriginalFrameScene source, OriginalStyleScene surface)
        => new(surface, options: source.Options, materials: source.Materials);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Source, WeakReference Resolved) UnreferencedCachedSurfaces()
    {
        var source = Source();
        var resolved = Resolve(source, Light);
        return (new(source.Surface.Snapshot), new(resolved.Surface));
    }

    private static OriginalFrameScene Source()
    {
        SceneVertex[] vertices = [new(new(-1, -1, 0), Vector3.UnitZ, Vector2.Zero),
            new(new(1, -1, 0), Vector3.UnitZ, Vector2.UnitX), new(new(0, 1, 0), Vector3.UnitZ, Vector2.UnitY)];
        var snapshot = new SceneSnapshot(vertices, [0, 1, 2],
            [new("surface", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1, DiffuseTexture: 0)],
            [new SceneTexture("source", 1, 1, [1, 2, 3, 255])], lightDirection: new(.2f, .8f, -.3f));
        var profile = OriginalStyleProfile.SelectedReference with { LightAmbient = new(.13f), LightSpecular = new(.27f) };
        return new(new(snapshot, [new(new(.25f))], profile));
    }
}

[Trait("Category", "Gpu")]
public sealed class D3D12ViewSceneLightingCacheGpuTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void UnchangedAndCameraOnlyFramesReuseGiBuffersWhileNewLightMatchesFreshResolution()
    {
        var original = D3D12RayGiTests.Bounce();
        var mesh = original.Surface.Snapshot;
        var textured = new SceneSnapshot(mesh.Vertices, mesh.Indices,
            mesh.Materials.Select(value => value with { DiffuseTexture = 0 }),
            [new SceneTexture("shared", 1, 1, [200, 100, 50, 255])], camera: mesh.Camera, lightDirection: mesh.LightDirection);
        var source = new OriginalFrameScene(new(textured, original.Surface.Materials, original.Surface.Profile),
            options: original.Options, materials: original.Materials);
        var light = new PmmLightPose(new(.6f), new(.5f, -1, .5f));
        var gi = new RayGiOptions(LightingMode.HardwareRayTracing, Strength: 4, SamplesPerPixel: 16,
            Seed: 17, MaxRayDistance: 50, RayEpsilon: .001f, Composition: RayGiComposition.ToonPalette,
            Denoiser: RayGiDenoiser.Off);
        using var session = new D3D12FrameSession();
        var support = session.QueryRayGiCapabilities();
        Assert.True(support.HardwareAvailable, support.UnavailableReason ?? $"{session.AdapterName}: DXR unavailable");
        OriginalFrameScene Resolve(PmmLightPose selected, SceneCamera? camera = null)
            => D3D12ViewScene.Resolve(source, null, selected, null, new Dictionary<Guid, CharacterMorphChannels>(),
                new(), resolvedCamera: camera);
        var first = Resolve(light);
        var initial = session.Capture(first, 47, 35, rayGiOptions: gi);
        Assert.Equal(1, session.RayGiStatistics!.AccelerationBuilds);
        var triangles = CachedBuffer(session, "gi:triangles");
        var texels = CachedBuffer(session, "gi:texels");
        var repeated = Resolve(light);
        Assert.Equal(initial.Rgba, session.Capture(repeated, 47, 35, rayGiOptions: gi).Rgba);
        Assert.Equal(0, session.RayGiStatistics!.AccelerationBuilds);
        Assert.Equal(0, session.RayGiStatistics.AccelerationUpdates);
        Assert.Same(triangles, CachedBuffer(session, "gi:triangles"));
        Assert.Same(texels, CachedBuffer(session, "gi:texels"));

        var camera = mesh.Camera with { Eye = mesh.Camera.Eye + new Vector3(.25f, 0, 0) };
        var moved = Resolve(light, camera);
        var movedImage = session.Capture(moved, 47, 35, rayGiOptions: gi);
        Assert.Same(first.Surface, moved.Surface);
        Assert.Equal(0, session.RayGiStatistics!.AccelerationBuilds);
        Assert.Equal(0, session.RayGiStatistics.AccelerationUpdates);
        Assert.Same(triangles, CachedBuffer(session, "gi:triangles"));
        Assert.Same(texels, CachedBuffer(session, "gi:texels"));

        var changedLight = light with { Color = new(.1f, .3f, .5f), Direction = new(-.25f, -1, .75f) };
        var changed = Resolve(changedLight, camera);
        var actual = session.Capture(changed, 47, 35, rayGiOptions: gi);
        Assert.NotEqual(movedImage.Rgba, actual.Rgba);
        var freshSurface = new OriginalStyleScene(source.Surface.Snapshot.WithLighting(-changedLight.Direction),
            source.Surface.Materials, source.Surface.Profile with { LightAmbient = changedLight.Color, LightSpecular = changedLight.Color });
        var fresh = new OriginalFrameScene(freshSurface, options: changed.Options, materials: changed.Materials);
        using var oracle = new D3D12FrameSession();
        Assert.Equal(oracle.Capture(fresh, 47, 35, rayGiOptions: gi).Rgba, actual.Rgba);
    }

    private static ID3D12Resource CachedBuffer(D3D12FrameSession session, string name)
    {
        var renderer = (D3D12FrameRenderer)typeof(D3D12FrameSession).GetField("renderer", Private)!.GetValue(session)!;
        var cache = (D3D12GpuCache)typeof(D3D12FrameRenderer).GetField("cache", Private)!.GetValue(renderer)!;
        var entries = (Dictionary<string, D3D12RetainedResource>)typeof(D3D12GpuCache).GetField("replaceableObjects", Private)!.GetValue(cache)!;
        return (ID3D12Resource)entries[name].Value;
    }
}
