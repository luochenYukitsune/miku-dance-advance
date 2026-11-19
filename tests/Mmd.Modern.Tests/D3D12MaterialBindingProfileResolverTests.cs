using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12MaterialBindingProfileResolverTests
{
    [Theory]
    [InlineData("mmd")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("endfield")]
    public void ExplicitInstanceReuseRetainsLocalOrdinalsAndOnlyActivatesSelectedFamily(string pipeline)
    {
        var source = Fixture();
        var layout = D3D12MaterialBindingLayout.Capture(source);
        var profile = new D3D12MaterialBindingProfile("hair-map", "Explicit map", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Hair,
                 pluginBindings: [TestStylePlugins.GenshinHair(1)],
                 textureGuards: [layout.CaptureTexture(1)]),
             new(1, D3D12CharacterMaterialRole.Eye, eyeGeometricShadowStrength: .15f)]);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var a = D3D12MaterialBindingProfileResolver.Resolve(profile, source, first, pipeline, TestStylePlugins.AllViewCatalog);
        var b = D3D12MaterialBindingProfileResolver.Resolve(profile, source, second, pipeline, TestStylePlugins.AllViewCatalog);
        Assert.Equal(2, a.CharacterMaterials.Count);
        Assert.All(a.CharacterMaterials, value => Assert.Equal(first, value.ObjectId));
        Assert.All(b.CharacterMaterials, value => Assert.Equal(second, value.ObjectId));
        Assert.Equal(a.CharacterMaterials.Select(value => value with { ObjectId = second }), b.CharacterMaterials);
        Assert.Equal(.15f, a.CharacterMaterials[1].EyeGeometricShadowStrength);
        Assert.All(a.CharacterMaterials, value => Assert.Null(value.PmxTreatment));
        Assert.Equal(pipeline == "genshin" ? 1 : 0, a.PluginBindings.Count);
        if (pipeline == "genshin")
        {
            Assert.Equal("hair", a.PluginBindings[0].Binding.ModeId);
            Assert.Equal(1, a.PluginBindings[0].Binding.TextureSlots!["texture"]);
        }
    }

    [Fact]
    public void EveryTextureGuardIsCheckedEvenWhenItsFamilyIsDormant()
    {
        var source = Fixture(); var layout = D3D12MaterialBindingLayout.Capture(source);
        var profile = new D3D12MaterialBindingProfile("map", "map", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Hair,
                 pluginBindings: [TestStylePlugins.GenshinHair(1)],
                 textureGuards: [layout.CaptureTexture(1) with { RgbaSha256 = new('0', 64) }])]);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, source, Guid.NewGuid(), "mmd", TestStylePlugins.AllViewCatalog));
    }

    [Fact]
    public void WrongLayoutUnknownFamilyAndOutOfRangeMaterialAreRejectedBeforeDeviceCreation()
    {
        var source = Fixture(); var layout = D3D12MaterialBindingLayout.Capture(source);
        var profile = new D3D12MaterialBindingProfile("map", "map", layout.Fingerprint, [new(0, D3D12CharacterMaterialRole.Eye)]);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, source, Guid.Empty, "mmd", TestStylePlugins.AllViewCatalog));
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(profile, source, Guid.NewGuid(), "../bad", TestStylePlugins.AllViewCatalog));
        var mismatch = new D3D12MaterialBindingProfile("map", "map", new('0', 64), profile.Materials);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(mismatch, source, Guid.NewGuid(), "mmd", TestStylePlugins.AllViewCatalog));
        var outside = new D3D12MaterialBindingProfile("map", "map", layout.Fingerprint, [new(2, D3D12CharacterMaterialRole.Body)]);
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingProfileResolver.Resolve(outside, source, Guid.NewGuid(), "mmd", TestStylePlugins.AllViewCatalog));
    }

    [Theory]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    public void FixedHeadFaceAndTwoHairImagesBecomeExistingLocalViewBindings(string pipeline)
    {
        var source = Fixture(); var layout = D3D12MaterialBindingLayout.Capture(source);
        var tex1 = layout.CaptureTexture(1);
        var tex0 = layout.CaptureTexture(0);
        var profile = new D3D12MaterialBindingProfile("map", "map", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Face,
                 pluginBindings: [TestStylePlugins.FaceSdf(pipeline, 1)],
                 textureGuards: [tex1]),
             new(1, D3D12CharacterMaterialRole.Hair,
                 pluginBindings: [TestStylePlugins.StarMcHair(pipeline, 1, 0)],
                 textureGuards: [tex1, tex0])]);
        var result = D3D12MaterialBindingProfileResolver.Resolve(profile, source, Guid.NewGuid(), pipeline, TestStylePlugins.AllViewCatalog);
        Assert.Equal(2, result.PluginBindings.Count);
        var face = result.PluginBindings[0];
        Assert.Equal("face-sdf", face.Binding.ModeId);
        Assert.Equal(1, face.Binding.TextureSlots!["primary"]);
        Assert.Equal(StyleBoneTrackingMode.FixedWorld, face.Binding.BoneBinding!.Mode);
        Assert.Equal(-Vector3.UnitZ, face.Binding.BoneBinding.FixedWorldForward);
        Assert.Equal(Vector3.UnitX, face.Binding.BoneBinding.FixedWorldRight);
        var hair = result.PluginBindings[1];
        Assert.Equal("hair", hair.Binding.ModeId);
        Assert.Equal(1, hair.Binding.TextureSlots!["highlight"]);
        Assert.Equal(0, hair.Binding.TextureSlots!["shadow"]);
        var dormant = D3D12MaterialBindingProfileResolver.Resolve(profile, source, Guid.NewGuid(), "mmd", TestStylePlugins.AllViewCatalog);
        Assert.Empty(dormant.PluginBindings);
    }

    [Theory, Trait("Category", "Gpu")]
    [InlineData("genshin")]
    [InlineData("star-rail")]
    [InlineData("wuthering-waves")]
    [InlineData("mmd")]
    public async Task JsonRestoredProfileMatchesExplicitExistingViewRendering(string pipeline)
    {
        var source = Fixture(); var layout = D3D12MaterialBindingLayout.Capture(source);
        var composition = new SceneCompositionEditor();
        var instance = composition.Add("explicit mapping", Path.Combine(Path.GetTempPath(), "mapping-fixture.pmx"), ModelRole.Character, source);
        using var project = new SceneProjectSession(composition, new(), [], new NoPhysics(), cancellationToken: TestContext.Current.CancellationToken);
        project.SetDefaultRenderingStyle(new(pipeline), TestContext.Current.CancellationToken);
        var texture1 = layout.CaptureTexture(1);
        var texture0 = layout.CaptureTexture(0);
        StyleMaterialBindingData[] rowBindings = pipeline switch
        {
            "genshin" => [TestStylePlugins.GenshinHair(1, multiplier: .8f)],
            "star-rail" or "wuthering-waves" => [TestStylePlugins.StarMcHair(pipeline, 1, 0, intensity: .8f)],
            _ => [],
        };
        D3D12MaterialBindingProfileTexture[] rowGuards = pipeline switch
        {
            "genshin" => [texture1],
            "star-rail" or "wuthering-waves" => [texture1, texture0],
            _ => [],
        };
        var profile = new D3D12MaterialBindingProfile("portable-map", "Portable map", layout.Fingerprint,
            [new(0, D3D12CharacterMaterialRole.Hair, pluginBindings: rowBindings, textureGuards: rowGuards),
             new(1, D3D12CharacterMaterialRole.Eye)]);
        profile = D3D12MaterialBindingProfileJson.Decode(D3D12MaterialBindingProfileJson.Encode(profile));
        Guid actor = instance.Id;
        var restored = D3D12MaterialBindingProfileResolver.Resolve(profile, source, actor, pipeline, TestStylePlugins.AllViewCatalog);
        D3D12ViewCharacterMaterial[] manual = [new(actor, 0, D3D12CharacterMaterialRole.Hair), new(actor, 1, D3D12CharacterMaterialRole.Eye)];
        D3D12ViewPluginMaterialBinding[] manualPluginBindings = rowBindings.Select(b => TestStylePlugins.Bind(actor, 0, b)).ToArray();
        var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Smaa1X, SpatialSamples: 2);
        var plan = new OfflineFramePlan(97, 65, new(30), 0, 0);
        await using var expected = new D3D12ViewRenderer(new(97, 65, quality),
            stylePresets: TestStylePlugins.AllPresetCatalog, styleCatalog: TestStylePlugins.AllViewCatalog,
            characterMaterials: manual, pluginBindings: manualPluginBindings);
        await using var actual = new D3D12ViewRenderer(new(97, 65, quality),
            stylePresets: TestStylePlugins.AllPresetCatalog, styleCatalog: TestStylePlugins.AllViewCatalog,
            characterMaterials: restored.CharacterMaterials, pluginBindings: restored.PluginBindings);
        Assert.False(expected.IsWarp); Assert.False(actual.IsWarp);
        var snapshot = project.CaptureRenderSnapshot(expected.SceneInputRequests);
        var frame = new OfflineFrame(plan.GetSample(0),97,65,snapshot.Scene,snapshot.Camera,snapshot.Light,snapshot.SelfShadow)
        { RenderingStyles=snapshot.RenderingStyles, SceneInputs=snapshot.SceneInputs };
        using var a = await expected.CaptureAsync(frame, TestContext.Current.CancellationToken);
        using var b = await actual.CaptureAsync(frame, TestContext.Current.CancellationToken);
        Assert.Equal(a.Pixels.ToArray(), b.Pixels.ToArray());
        Assert.Contains(b.Pixels.ToArray().Where((_, index) => index % 4 == 3), alpha => alpha > 0);
    }

    private static OriginalFrameScene Fixture()
    {
        SceneVertex[] Quad(float y) => [new(new(-3,y-.35f,0),Vector3.UnitX,new(.375f,.5f)),new(new(3,y-.35f,0),Vector3.UnitX,new(.375f,.5f)),
            new(new(3,y+.35f,0),Vector3.UnitX,new(.375f,.5f)),new(new(-3,y+.35f,0),Vector3.UnitX,new(.375f,.5f))];
        var scene = new SceneSnapshot([..Quad(.55f),..Quad(-.55f)], [0,2,1,0,3,2,4,6,5,4,7,6],
            [new("hair",0,6,new(.6f,.4f,.2f,1),new(.6f,.4f,.2f),Vector3.Zero,10),new("eye",6,6,new(.6f,.4f,.2f,1),Vector3.Zero,Vector3.Zero,1)],
            [new("first",1,1,[0,0,0,255]),new("second",1,1,[255,255,255,0])],
            camera:new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,3,.1f,20),lightDirection:Vector3.UnitX);
        var outline = new OriginalOutlineData(scene.Vertices.ToArray().Select(vertex => new OriginalOutlineVertex(vertex.Position, vertex.Normal,
            OriginalOutlineVertexMode.Expand, 0, 0)), [new OriginalOutlineMaterial(0,Vector4.Zero,false),new OriginalOutlineMaterial(0,Vector4.Zero,false)]);
        return new(new(scene,[new(new(.2f)),new(new(.2f))],new(Vector3.One,Vector3.One,Matrix4x4.Identity,OriginalTextureSampling.PointClamp)),outline,
            new(Vector4.Zero,Matrix4x4.Identity,scene.Camera.Eye,30,1),
            options:new(OriginalAlphaBlendMode.Normal,true,false,BackgroundColor:Vector4.Zero));
    }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
}
