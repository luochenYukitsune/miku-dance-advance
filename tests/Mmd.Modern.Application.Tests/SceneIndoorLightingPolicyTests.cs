using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneIndoorLightingPolicyTests
{
    [Fact]
    public void SessionPurposeReadUsesActualIdentityWithoutEditsOrPhysicsRebuild()
    {
        var layout = new SceneCompositionEditor();
        var room = layout.Add("room", "room.pmx", ModelRole.Environment,
            EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model()));
        var lighting = new SceneLightingEditor(); lighting.SetModelRole(room.Key, ModelRole.Environment, true);
        using var session = new SceneProjectSession(layout, lighting, [], new NoPhysics(), cancellationToken: TestContext.Current.CancellationToken);
        var frame = session.Frame;
        Assert.Equal(new ModelPurpose(ModelRole.Environment, true), session.GetModelPurpose(room.Id));
        Assert.Same(frame, session.Frame);
        Assert.False(session.CanUndo); Assert.False(session.CanRedo);
        Assert.Throws<ArgumentException>(() => session.GetModelPurpose(Guid.Empty));
        Assert.Throws<ArgumentException>(() => session.GetModelPurpose(Guid.NewGuid()));
    }

    [Fact]
    public void FullPurposePreservesNullableIndoorAndExplicitRoleOverride()
    {
        var layout = new SceneCompositionEditor();
        var item = layout.Add("explicit", "source.pmx", ModelRole.Character,
            EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model()));
        Assert.Equal(new ModelPurpose(ModelRole.Character), SceneModelShadingPolicy.ResolvePurpose(item));
        foreach (bool? indoor in new bool?[] { null, false, true })
        {
            var purposes = new Dictionary<string, ModelPurpose> { [item.Key] = new(ModelRole.Environment, indoor) };
            Assert.Equal(purposes[item.Key], SceneModelShadingPolicy.ResolvePurpose(item, purposes));
            Assert.Equal(ModelRole.Environment, SceneModelShadingPolicy.ResolveRole(item, purposes));
        }
        var overridden = new Dictionary<string, ModelPurpose> { [item.Key] = new(ModelRole.Unspecified) };
        Assert.Equal(new ModelPurpose(ModelRole.Unspecified), SceneModelShadingPolicy.ResolvePurpose(item, overridden));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyExplicitIndoorEnvironmentSelectsSceneLightingWithoutChangingAssetInputs(bool? indoor)
    {
        var raw = EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model());
        var source = new OriginalFrameScene(new(raw.Surface.Snapshot.WithMaterials(raw.Surface.Snapshot.Materials
            .Select(m => m with { Ambient = new(.502f), Diffuse = Vector4.One })),
            raw.Surface.Materials.Select(m => m with { ToonShadowColor = Vector3.One }), raw.Surface.Profile),
            raw.Outline, raw.OutlineOptions, raw.Options, raw.Materials);
        var applied = SceneModelShadingPolicy.Apply(source, new ModelPurpose(ModelRole.Environment, indoor));
        Assert.Equal(indoor == true, applied.UsesSceneLighting);
        Assert.Equal(indoor == true, Assert.Single(applied.Materials).UseSceneLighting);
        Assert.Same(source.Surface, applied.Surface);
        Assert.Equal(new Vector3(.502f), Assert.Single(source.Surface.Snapshot.Materials).Ambient);
        Assert.Equal(Vector3.One, Assert.Single(source.Surface.Materials).ToonShadowColor);
        Assert.False(source.UsesSceneLighting);
        Assert.Same(applied, SceneModelShadingPolicy.Apply(applied, new ModelPurpose(ModelRole.Environment, indoor)));
        var reverted = SceneModelShadingPolicy.Apply(applied, new ModelPurpose(ModelRole.Environment, false));
        Assert.False(reverted.UsesSceneLighting);
        Assert.Same(source.Surface, reverted.Surface);
        Assert.Equal(SceneModelShadingPolicy.Apply(source, ModelRole.Environment).Materials, reverted.Materials);
    }

    [Fact]
    public void MixedAnimatedPurposeSurvivesSeekAndDoesNotChangeCharacterInputs()
    {
        var model = EditedMotionProjectTests.Model(); var source = EditedMotionProjectTests.Rest(model);
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, source);
        var room = layout.Add("room", "room.pmx", ModelRole.Environment, source);
        var purposes = new Dictionary<string, ModelPurpose> { [room.Key] = new(ModelRole.Environment, true) };
        using var session = new SceneAnimationSession(layout.Objects, [],
            _ => throw new InvalidOperationException("This fixture does not use physics."), modelRoles: purposes,
            cancellationToken: TestContext.Current.CancellationToken);
        purposes[room.Key] = new(ModelRole.Environment, false);
        foreach (int frame in new[] { 0, 10, 0 })
        {
            session.Seek(frame, TestContext.Current.CancellationToken);
            Assert.False(session.Frame.Materials[0].UseSceneLighting);
            Assert.True(session.Frame.Materials[1].UseSceneLighting);
            Assert.Equal(RayGiToonResponse.PreservePaletteBands, session.Frame.Materials[0].GiToonResponseOverride);
            Assert.Equal(source.Surface.Snapshot.Materials[0].Ambient, session.Frame.Surface.Snapshot.Materials[0].Ambient);
            Assert.Equal(source.Surface.Materials[0].ToonShadowColor, session.Frame.Surface.Materials[0].ToonShadowColor);
            Assert.Equal(ModelRole.Character, actor.Role);
        }
        Assert.False(source.UsesSceneLighting);
    }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
}
