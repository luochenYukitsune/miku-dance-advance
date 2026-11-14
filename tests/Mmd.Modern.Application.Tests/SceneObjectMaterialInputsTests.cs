using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneObjectMaterialInputsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HiddenMaterialInputsMatchVisibleControllerAndKeepLegacyRangesEmpty(bool morphs)
    {
        var model = Model(); var rest = Rest(model); var motion = Motion(hidden: true);
        var layout = new SceneCompositionEditor(); var actor = layout.Add("hidden", "same.pmx", ModelRole.Character, rest);
        layout.SetTransform(actor.Id, new(100, 20, 30), Quaternion.Identity, 2);
        using var project = new SceneProjectSession(layout, new(),
            [new(actor.Id, model, motion, MorphsEnabled: morphs, PhysicsEnabled: false)], new NoPhysics(), cancellationToken: Token);
        using var visible = new CharacterAnimationController(model, Motion(), rest.Surface, rest.Outline!);
        var request = new SceneObjectRenderRequest(actor.Id, [999], [999], includeSourceGeometry: true, includeMaterialInputs: true);

        Assert.False(new SceneObjectRenderRequest(actor.Id).IncludeMaterialInputs);
        Assert.Empty(project.CaptureRenderSnapshot().ObjectMaterialInputs);
        project.Seek(5, Token); project.Advance(.25 / 30, Token);
        var hidden = project.CaptureRenderSnapshot([request]); var input = hidden.ObjectMaterialInputs[actor.Id];
        var oracle = visible.SampleAt(5.25, enableMorphs: morphs);
        Assert.Equal(5.25, input.SourceFrame); Assert.Equal(5.25, input.LocalFrame); Assert.False(input.Visible);
        Assert.Equal(actor.Id, input.ObjectId); Assert.Equal(2, input.MaterialCount);
        Assert.Equal(oracle.Surface.Snapshot.Materials, input.Materials);
        Assert.Equal(oracle.Surface.Materials, input.Bindings);
        Assert.Equal(oracle.Outline.Materials, input.OutlineMaterials);
        Assert.Equal(PmxOriginalFrameAdapter.FromEvaluatedMaterials(model, oracle.Surface, oracle.Outline), input.MaterialFlags);
        Assert.Equal(new[] { "material-0", "material-1" }, input.Materials.Select(value => value.Name));
        Assert.Same(rest.Surface.Snapshot.Textures[0], input.Textures[0]);
        Assert.Empty(hidden.Scene.Surface.Snapshot.Vertices.ToArray()); Assert.Empty(hidden.Scene.Surface.Snapshot.Textures);
        Assert.Empty(hidden.Scene.Materials); Assert.Empty(hidden.Scene.Outline!.Materials);
        Assert.Empty(hidden.SceneInputs[actor.Id].BoneWorldMatrices); Assert.Empty(hidden.SceneInputs[actor.Id].TextureIndices);
        Assert.Null(hidden.SceneInputs[actor.Id].SourceGeometry); Assert.Equal(0, Assert.Single(hidden.RenderingStyles).MaterialCount);
        Assert.Throws<NotSupportedException>(() => ((IList<SceneMaterial>)input.Materials)[0] = input.Materials[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<OriginalStyleMaterialBinding>)input.Bindings)[0] = input.Bindings[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<OriginalOutlineMaterial>)input.OutlineMaterials!)[0] = input.OutlineMaterials![1]);
        Assert.Throws<NotSupportedException>(() => ((IList<OriginalFrameMaterialFlags>)input.MaterialFlags)[0] = input.MaterialFlags[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<SceneTexture>)input.Textures)[0] = new("other", 1, 1, [0, 0, 0, 0]));
        var retained = input.Materials.ToArray(); var retainedBindings = input.Bindings.ToArray();
        project.Seek(10, Token);
        var shown = project.CaptureRenderSnapshot([new(actor.Id, includeMaterialInputs: true)]).ObjectMaterialInputs[actor.Id];
        Assert.True(shown.Visible); Assert.Equal(2, shown.MaterialCount);
        Assert.Equal(retained, input.Materials); Assert.Equal(retainedBindings, input.Bindings);
        if (morphs) Assert.NotEqual(input.Materials[0], shown.Materials[0]);
        project.Seek(50, Token);
        var tail = project.CaptureRenderSnapshot([new(actor.Id, includeMaterialInputs: true)]).ObjectMaterialInputs[actor.Id];
        Assert.Equal(50, tail.SourceFrame); Assert.Equal(10, tail.LocalFrame); Assert.Equal(shown.Materials, tail.Materials);
    }

    [Fact]
    public void StaticAndRepeatedSourcesKeepExactFlagsLocalTextureIdentityAndIndependentInstances()
    {
        var model = Model(); var a = Rest(model); var b = Rest(model); var layout = new SceneCompositionEditor();
        var first = layout.Add("first alias", "same.pmx", ModelRole.Character, a);
        var second = layout.Add("second alias", "same.pmx", ModelRole.Character, b);
        using var project = new SceneProjectSession(layout, new(), [new(second.Id, model, Motion(), PhysicsEnabled: false)],
            new NoPhysics(), cancellationToken: Token);
        project.Seek(5, Token);
        var snapshot = project.CaptureRenderSnapshot([new(first.Id, includeMaterialInputs: true), new(second.Id, includeMaterialInputs: true)]);
        var staticInput = snapshot.ObjectMaterialInputs[first.Id]; var animated = snapshot.ObjectMaterialInputs[second.Id];
        Assert.Equal(0, staticInput.LocalFrame); Assert.Equal(5, staticInput.SourceFrame);
        Assert.Equal(a.Surface.Snapshot.Materials, staticInput.Materials); Assert.Equal(a.Materials, staticInput.MaterialFlags);
        Assert.Equal(a.Surface.Materials, staticInput.Bindings); Assert.Equal(a.Outline!.Materials, staticInput.OutlineMaterials);
        Assert.Same(a.Surface.Profile, staticInput.Profile); Assert.NotEqual(staticInput.Materials[0], animated.Materials[0]);
        Assert.Equal(staticInput.Textures[0].Name, animated.Textures[0].Name); Assert.NotSame(staticInput.Textures[0], animated.Textures[0]);
        Assert.Same(a.Surface.Snapshot.Textures[0], staticInput.Textures[0]); Assert.Same(b.Surface.Snapshot.Textures[0], animated.Textures[0]);
        Assert.Single(project.CaptureRenderSnapshot([new(first.Id, includeMaterialInputs: true), new(second.Id)]).ObjectMaterialInputs);
        Assert.Empty(project.CaptureRenderSnapshot([new(first.Id), new(second.Id)]).ObjectMaterialInputs);
    }

    [Fact]
    public void VisibilityOnlyMotionRetainsTruthfulMaterialsWithoutInventingMorphTracks()
    {
        var model = Model(); var rest = Rest(model); var layout = new SceneCompositionEditor();
        var actor = layout.Add("visibility", "visibility.pmx", ModelRole.Character, rest);
        var motion = new VmdMotion { IkKeyframes = [new() { Visible = false }, new() { Frame = 10, Visible = true }] };
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, motion, PhysicsEnabled: false)],
            new NoPhysics(), cancellationToken: Token);
        var request = new[] { new SceneObjectRenderRequest(actor.Id, includeMaterialInputs: true) };
        project.Seek(5, Token); var hidden = project.CaptureRenderSnapshot(request).ObjectMaterialInputs[actor.Id];
        Assert.False(hidden.Visible); Assert.Equal(rest.Surface.Snapshot.Materials, hidden.Materials);
        project.Seek(10, Token); var shown = project.CaptureRenderSnapshot(request).ObjectMaterialInputs[actor.Id];
        Assert.True(shown.Visible); Assert.Equal(hidden.Materials, shown.Materials); Assert.Equal(2, shown.MaterialCount);
    }

    private static OriginalFrameScene Rest(PmxModel model)
    {
        var value = MaterialMorphAdapterTests.Baseline(model);
        return new(value.Surface, value.Outline, new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
            materials: PmxOriginalFrameAdapter.FromMaterials(model));
    }

    private static PmxModel Model()
    {
        var add = MaterialMorphAdapterTests.Offset(-1, PmxMaterialMorphOperation.Add, new(.04f), new(.02f), .1f)
            with { TextureFactor = new(.2f), SphereTextureFactor = new(.3f), ToonTextureFactor = new(.4f) };
        var multiply = MaterialMorphAdapterTests.Offset(1, PmxMaterialMorphOperation.Multiply, new(.7f), new(.9f), 1.1f)
            with { TextureFactor = new(.8f), SphereTextureFactor = new(.6f), ToonTextureFactor = new(.5f) };
        return MaterialMorphAdapterTests.Model([
            MaterialMorphAdapterTests.Morph("add", add), MaterialMorphAdapterTests.Morph("multiply", multiply),
            new() { Name = "branch", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Group, GroupOffsets = [new(0, .5f), new(1, .25f)] },
            new() { Name = "root", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Group, GroupOffsets = [new(2, 1), new(2, .5f)] },
            new() { Name = "flip", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Flip, FlipOffsets = [new(0, -.2f), new(1, .7f)] }
        ], rig: true);
    }

    private static VmdMotion Motion(bool hidden = false) => new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 10 }],
        MorphKeyframes = [new() { MorphName = "root", Weight = -.5f }, new() { MorphName = "root", Frame = 10, Weight = .75f },
            new() { MorphName = "flip", Weight = .25f }, new() { MorphName = "flip", Frame = 10, Weight = .75f }],
        IkKeyframes = hidden ? [new() { Visible = false }, new() { Frame = 10, Visible = true }] : []
    };

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item) => throw new InvalidOperationException("Unexpected physics construction.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException("Unexpected physics construction.");
    }
}
