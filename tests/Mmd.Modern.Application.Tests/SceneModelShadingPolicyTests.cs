using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneModelShadingPolicyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void IdentityAndMixedCompositionResolveOnlyExplicitModelPurpose()
    {
        var source = EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model());
        var layout = new SceneCompositionEditor();
        layout.Add("room.pmx", "character-name.pmx", ModelRole.Character, source);
        var identity = layout.Frame;
        Assert.Same(source.Surface, identity.Surface);
        Assert.Equal(RayGiToonResponse.PreservePaletteBands, Assert.Single(identity.Materials).GiToonResponseOverride);
        Assert.Null(Assert.Single(identity.Materials).CharacterToonResponse);
        Assert.Null(Assert.Single(identity.Materials).CharacterOutlineStyle);
        Assert.Null(Assert.Single(source.Materials).GiToonResponseOverride);
        Assert.Null(Assert.Single(source.Materials).CharacterToonResponse);
        Assert.Null(Assert.Single(source.Materials).CharacterOutlineStyle);
        layout.Add("character.pmx", "room-name.pmx", ModelRole.Environment, source);
        Assert.Equal(new RayGiToonResponse?[] { RayGiToonResponse.PreservePaletteBands, RayGiToonResponse.Continuous },
            layout.Frame.Materials.Select(flags => flags.GiToonResponseOverride));
        Assert.Equal(new CharacterToonResponse?[] { null, null },
            layout.Frame.Materials.Select(flags => flags.CharacterToonResponse));
        Assert.Equal(new CharacterOutlineStyle?[] { null, null },
            layout.Frame.Materials.Select(flags => flags.CharacterOutlineStyle));
    }

    [Theory]
    [InlineData(ModelRole.Unspecified)]
    [InlineData(ModelRole.Prop)]
    public void UnspecifiedAndPropRetainOriginalFlagsAndGlobalInheritance(ModelRole role)
    {
        var source = EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model());
        Assert.Same(source, SceneModelShadingPolicy.Apply(source, role));
        var flags = new OriginalFrameMaterialFlags(true, false, true, false, true, true)
            { GiToonResponseOverride = RayGiToonResponse.Continuous, CharacterToonResponse = new(.6f, .2f) };
        var custom = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions, materials: [flags]);
        Assert.Same(custom, SceneModelShadingPolicy.Apply(custom, role));
        Assert.Equal(flags with { GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands, PreserveDirectLightPalette = true },
            Assert.Single(SceneModelShadingPolicy.Apply(custom, ModelRole.Character).Materials));
    }

    [Fact]
    public void UnknownMaterialResponseIsRejected()
    {
        var source = EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model());
        Assert.Throws<ArgumentException>(() => new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions,
            materials: [new OriginalFrameMaterialFlags(false, false, false) { GiToonResponseOverride = (RayGiToonResponse)123 }]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnimatedMixedRolesSurviveAdvancePauseSeekAndPreserveEvaluatedFlags(bool placed)
    {
        var model = EditedMotionProjectTests.Model(); var raw = EditedMotionProjectTests.Rest(model);
        var toon = new CharacterToonResponse(.6f, .2f);
        var outline = new CharacterOutlineStyle(.3f, new(.2f, .3f, .4f, .8f), false);
        var rest = new OriginalFrameScene(raw.Surface, raw.Outline, raw.OutlineOptions,
            materials: raw.Materials.Select(flags => flags with { CharacterToonResponse = toon, CharacterOutlineStyle = outline }));
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, rest);
        var room = layout.Add("room", "room.pmx", ModelRole.Environment, rest);
        if (placed) layout.SetTransform(actor.Id, new(7, 3, -2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f), 2);
        var motion = MovingMotion();
        using var session = new SceneAnimationSession(layout.Objects,
            [new(actor.Id, model, motion.Motion, PhysicsEnabled: false, Asset: motion)],
            _ => throw new InvalidOperationException("No physics in shading fixture."), cancellationToken: Token);
        void Check()
        {
            Assert.Equal(new RayGiToonResponse?[] { RayGiToonResponse.PreservePaletteBands, RayGiToonResponse.Continuous },
                session.Frame.Materials.Select(flags => flags.GiToonResponseOverride));
            Assert.Null(session.Frame.Materials[1].CharacterToonResponse);
            Assert.Same(toon, session.Frame.Materials[0].CharacterToonResponse);
            Assert.Same(outline, session.Frame.Materials[0].CharacterOutlineStyle);
            Assert.Null(session.Frame.Materials[1].CharacterOutlineStyle);
            Assert.Equal(Assert.Single(raw.Materials), session.Frame.Materials[1] with { GiToonResponseOverride = null, CharacterToonResponse = null, CharacterOutlineStyle = null });
            Assert.Equal(Assert.Single(rest.Materials), session.Frame.Materials[0] with { GiToonResponseOverride = null, PreserveDirectLightPalette = false });
        }
        Check(); session.Advance(.5, Token); Check();
        Assert.True(session.Frame.Surface.Snapshot.HasDynamicVertices);
        var paused = session.Frame; Assert.Same(paused, session.Advance(0, Token)); Check();
        session.Seek(30, Token); Check(); session.Seek(0, Token); Check();
        Assert.Equal(ModelRole.Environment, room.Role);
    }

    [Fact]
    public void ExplicitPurposeIsFrozenAndSurvivesCoupledSeekCandidate()
    {
        var model = EditedMotionProjectTests.Model(); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(model));
        var purposes = new Dictionary<string, ModelPurpose> { [actor.Key] = new(ModelRole.Environment) };
        var motion = MovingMotion();
        using var session = new SceneAnimationSession(layout.Objects,
            [new(actor.Id, model, motion.Motion, PhysicsEnabled: false, Asset: motion)],
            _ => throw new InvalidOperationException("No physics in shading fixture."), cancellationToken: Token,
            coupledSoftBodies: true, modelRoles: purposes);
        purposes[actor.Key] = new(ModelRole.Character);
        session.Seek(15, Token);
        Assert.Equal(RayGiToonResponse.Continuous, Assert.Single(session.Frame.Materials).GiToonResponseOverride);
        Assert.Null(Assert.Single(session.Frame.Materials).CharacterToonResponse);
    }

    [Fact]
    public void ExplicitRoleChangesUndoAndPmaReopenUseExistingPurposeStorage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "shading-role-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var model = EditedMotionProjectTests.Model(); var rest = EditedMotionProjectTests.Rest(model);
            string path = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
            var layout = new SceneCompositionEditor(); var actor = layout.Add("actor", path, ModelRole.Character, rest);
            var motion = MovingMotion();
            using var project = new SceneProjectSession(layout, new(),
                [new(actor.Id, model, motion.Motion, PhysicsEnabled: false, Asset: motion)], new NoPhysics(), cancellationToken: Token);
            Assert.Equal(RayGiToonResponse.PreservePaletteBands, Assert.Single(project.Frame.Materials).GiToonResponseOverride);
            Assert.Null(Assert.Single(project.Frame.Materials).CharacterToonResponse);
            Assert.Null(Assert.Single(project.Frame.Materials).CharacterOutlineStyle);
            project.Seek(15, Token);
            project.Edit(edit => edit.Lighting.SetModelRole(actor.Key, ModelRole.Environment, true), Token);
            Assert.True(project.Frame.UsesSceneLighting);
            Assert.Equal(RayGiToonResponse.Continuous, Assert.Single(project.Frame.Materials).GiToonResponseOverride);
            Assert.Null(Assert.Single(project.Frame.Materials).CharacterToonResponse);
            Assert.Null(Assert.Single(project.Frame.Materials).CharacterOutlineStyle);
            Assert.Equal(ModelRole.Character, Assert.Single(project.Objects).Role);
            Assert.True(project.Undo(Token));
            Assert.False(project.Frame.UsesSceneLighting);
            Assert.Equal(RayGiToonResponse.PreservePaletteBands, Assert.Single(project.Frame.Materials).GiToonResponseOverride);
            Assert.Null(Assert.Single(project.Frame.Materials).CharacterToonResponse);
            Assert.Null(Assert.Single(project.Frame.Materials).CharacterOutlineStyle);
            Assert.True(project.Redo(Token));
            string package = Path.Combine(directory, "roles.pma");
            PmaProjectStore.Save(package, project, cancellationToken: Token);
            using var loaded = PmaProjectStore.Load(package, Path.Combine(directory, "cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
            Assert.Equal(new ModelPurpose(ModelRole.Environment, true), loaded.Session.GetModelPurpose(loaded.Session.Objects[0].Id));
            Assert.True(loaded.Session.Frame.UsesSceneLighting);
            Assert.Equal(15, loaded.Session.CurrentFrame);
            Assert.Equal(RayGiToonResponse.Continuous, Assert.Single(loaded.Session.Frame.Materials).GiToonResponseOverride);
            Assert.Null(Assert.Single(loaded.Session.Frame.Materials).CharacterToonResponse);
            Assert.Null(Assert.Single(loaded.Session.Frame.Materials).CharacterOutlineStyle);
            Assert.Equal(ModelRole.Environment, Assert.Single(loaded.Session.Objects).Role);
            loaded.Session.Seek(30, Token);
            Assert.Equal(RayGiToonResponse.Continuous, Assert.Single(loaded.Session.Frame.Materials).GiToonResponseOverride);
            project.Edit(edit => edit.Lighting.SetModelRole(actor.Key, ModelRole.Unspecified), Token);
            Assert.Null(Assert.Single(project.Frame.Materials).GiToonResponseOverride);
            Assert.Null(Assert.Single(project.Frame.Materials).CharacterToonResponse);
            Assert.Null(Assert.Single(project.Frame.Materials).CharacterOutlineStyle);
        }
        finally
        {
            string root = Path.GetFullPath(directory); string temp = Path.GetFullPath(Path.GetTempPath());
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnvironmentRemovesCharacterMainLightWhileUnspecifiedAndPropPreserveIt()
    {
        var source = EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model());
        var custom = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions,
            materials: source.Materials.Select(flags => flags with
            { CharacterToonResponse = new(.6f, .2f), CharacterOutlineStyle = new(.25f) }));
        var character = SceneModelShadingPolicy.Apply(custom, ModelRole.Character);
        Assert.Same(character, SceneModelShadingPolicy.Apply(character, ModelRole.Character));
        var environment = SceneModelShadingPolicy.Apply(character, ModelRole.Environment);
        Assert.Null(Assert.Single(environment.Materials).CharacterToonResponse);
        Assert.Null(Assert.Single(environment.Materials).CharacterOutlineStyle);
        Assert.Equal(RayGiToonResponse.Continuous, Assert.Single(environment.Materials).GiToonResponseOverride);
        Assert.Same(environment, SceneModelShadingPolicy.Apply(environment, ModelRole.Environment));
        Assert.Equal(Assert.Single(character.Materials) with { PreserveDirectLightPalette = false },
            Assert.Single(SceneModelShadingPolicy.Apply(character, ModelRole.Unspecified).Materials));
        Assert.Equal(Assert.Single(character.Materials) with { PreserveDirectLightPalette = false },
            Assert.Single(SceneModelShadingPolicy.Apply(character, ModelRole.Prop).Materials));
        Assert.Equal(new CharacterToonResponse(.6f, .2f), Assert.Single(character.Materials).CharacterToonResponse);
        Assert.Equal(new CharacterOutlineStyle(.25f), Assert.Single(character.Materials).CharacterOutlineStyle);
        var restoredCharacter = SceneModelShadingPolicy.Apply(environment, ModelRole.Character);
        Assert.Null(Assert.Single(restoredCharacter.Materials).CharacterToonResponse);
        Assert.Null(Assert.Single(restoredCharacter.Materials).CharacterOutlineStyle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitCharacterToonAndOutlineSurviveRoleApplicationIncludingDisabledChoice(bool enabled)
    {
        var source = EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model());
        var style = new CharacterOutlineStyle(.25f, new(.2f, .3f, .4f, .8f), enabled);
        var response = new CharacterToonResponse(.6f, .2f);
        var custom = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions,
            materials: source.Materials.Select(flags => flags with { CharacterOutlineStyle = style, CharacterToonResponse = response }));
        var character = SceneModelShadingPolicy.Apply(custom, ModelRole.Character);
        Assert.Same(style, Assert.Single(character.Materials).CharacterOutlineStyle);
        Assert.Same(response, Assert.Single(character.Materials).CharacterToonResponse);
        Assert.Equal(RayGiToonResponse.PreservePaletteBands, Assert.Single(character.Materials).GiToonResponseOverride);
        Assert.Same(character, SceneModelShadingPolicy.Apply(character, ModelRole.Character));
        Assert.Same(custom, SceneModelShadingPolicy.Apply(custom, ModelRole.Prop));
        Assert.Same(custom, SceneModelShadingPolicy.Apply(custom, ModelRole.Unspecified));
        var environment = SceneModelShadingPolicy.Apply(character, ModelRole.Environment);
        Assert.Null(Assert.Single(environment.Materials).CharacterOutlineStyle);
        Assert.Null(Assert.Single(environment.Materials).CharacterToonResponse);
        Assert.Null(Assert.Single(SceneModelShadingPolicy.Apply(environment, ModelRole.Character).Materials).CharacterOutlineStyle);
        Assert.Equal(style, Assert.Single(custom.Materials).CharacterOutlineStyle);
        Assert.Same(response, Assert.Single(custom.Materials).CharacterToonResponse);
    }

    [Fact]
    public void InvalidCharacterResponseIsRejectedWhenFrameSnapshotIsBuilt()
    {
        var source = EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model());
        Assert.Throws<ArgumentException>(() => new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions,
            materials: [new OriginalFrameMaterialFlags(false, false, false) { CharacterToonResponse = new(.99f, .1f) }]));
    }

    private static SceneMotionAsset MovingMotion() => SceneMotionAsset.FromMotion(new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = Vector3.UnitX }],
    });
    private static SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); using var input = File.OpenRead(path); var model = PmxReader.Read(input);
        return new(model, EditedMotionProjectTests.Rest(model));
    }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
}
