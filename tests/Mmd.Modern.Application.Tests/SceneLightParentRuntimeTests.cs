using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneLightParentRuntimeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static NativeSceneLight Light(SceneLightParent? parent = null) => new(Guid.NewGuid(), "follow",
        SceneLightKind.Spot, new(1, 2, 3), Quaternion.CreateFromYawPitchRoll(.2f, -.3f, .4f),
        Vector3.One, Range: 17, Parent: parent);
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, .0001f);
    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("Unexpected solver allocation");

    [Fact]
    public void ResolvingParentsRetainsLocalAuthoringAndGroupsAndReadsEachParentOnlyOnce()
    {
        var parent = new SceneLightParent(Guid.NewGuid(), 3);
        var group = new SceneLightGroup(Guid.NewGuid(), "fill", Intensity: 2);
        var light = Light(parent) with { GroupId = group.Id, Intensity = 3 };
        var second = Light(parent);
        var source = new SceneLightingSnapshot([light, second], [group]);
        var parentWorld = Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationY(.8f) * Matrix4x4.CreateTranslation(3, 4, 5);
        int calls = 0;
        var resolved = source.ResolveParents(value => { calls++; Assert.Equal(parent, value); return parentWorld; });
        Assert.Equal(1, calls); Assert.Same(source, resolved.AuthoredLighting);
        Assert.Equal(light, source.Lights[0]); Assert.Equal(parentWorld, resolved.ParentTransforms[parent]);
        Near(Vector3.Transform(light.Position, parentWorld), resolved.Lights[0].Position);
        Near(Vector3.Transform(light.Direction, Matrix4x4.CreateRotationY(.8f)), resolved.Lights[0].Direction);
        Assert.Null(resolved.Lights[0].Parent); Assert.Equal(17, resolved.Lights[0].Range);
        Assert.Equal(6, resolved.EffectiveLights[0].Intensity);
        var local = SceneLightTransform.ToLocal(resolved.Lights[0], parent, parentWorld);
        Near(light.Position, local.Position); Near(light.Direction, local.Direction); Assert.Equal(parent, local.Parent);
        var unparented = new SceneLightingSnapshot([Light()]);
        Assert.Same(unparented, unparented.ResolveParents(_ => throw new InvalidOperationException()));
        Assert.Empty(source.ParentTransforms);
    }

    [Fact]
    public void SingularAndNonfiniteParentsCannotPublishWorldLightInputs()
    {
        var light = Light(new(Guid.NewGuid()));
        foreach (var matrix in new[] { default(Matrix4x4), Matrix4x4.CreateScale(0),
            Matrix4x4.CreateTranslation(float.PositiveInfinity, 0, 0) })
            Assert.Throws<ArgumentException>(() => new SceneLightingSnapshot([light]).ResolveParents(_ => matrix));
        Assert.Equal(new(1, 2, 3), light.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentAndBoneUseTheSameSolvedFrameAndRetainStaticLocalValuesDuringPlayback(bool bone)
    {
        var model = EditedMotionProjectTests.Model();
        var layout = new SceneCompositionEditor();
        var item = layout.Add("actor", Path.GetFullPath("parent-fixture.pmx"), ModelRole.Character, EditedMotionProjectTests.Rest(model));
        layout.SetTransform(item.Id, new(3, 4, 5), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f), 2);
        item = layout.Objects[0];
        var light = Light(new(item.Id, bone ? 0 : null));
        var lighting = new SceneLightingSnapshot([light]);
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" },
            new() { BoneName = "root", Frame = 30, Translation = new(3, 2, 1), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f) }] };
        using var session = new SceneAnimationSession(layout.Objects, [new(item.Id, model, motion, PhysicsEnabled: false)],
            NoPhysics, lighting, Token);
        session.Seek(15, Token);
        Verify();
        var retained = session.Frame;
        var retainedWorld = Assert.Single(retained.Lighting!.Lights);
        session.Advance(.125, Token);
        Assert.Equal(18.75, session.CurrentFrame); Verify();
        Assert.Same(lighting, retained.Lighting!.AuthoredLighting);
        Assert.Equal(retainedWorld, Assert.Single(retained.Lighting.Lights));
        Assert.Equal(light, Assert.Single(lighting.Lights));
        void Verify()
        {
            var expectedParent = bone ? session.GetBoneWorldMatrices(item.Id)[0] : item.World;
            var actual = Assert.Single(session.Frame.Lighting!.Lights);
            Near(Vector3.Transform(light.Position, expectedParent), actual.Position);
            Near(SceneLightTransform.ToWorld(light, expectedParent).Direction, actual.Direction);
            Assert.Equal(expectedParent, session.Frame.Lighting.ParentTransforms[light.Parent!]);
            Assert.Same(lighting, session.Frame.Lighting.AuthoredLighting); Assert.Equal(17, actual.Range);
        }
    }

    [Fact]
    public void BoneOnlyMotionUpdatesLightEvenWhenComposedGeometryRemainsIdentical()
    {
        var model = EditedMotionProjectTests.Model();
        // The mesh is bound to root, so a separate child bone can move without deforming it.
        model = new PmxModel { Header = model.Header, Name = model.Name, EnglishName = model.EnglishName,
            Comment = model.Comment, EnglishComment = model.EnglishComment, Vertices = model.Vertices,
            Faces = model.Faces, Textures = model.Textures, Materials = model.Materials,
            Bones = [..model.Bones, new() { Name = "lamp", EnglishName = "", Position = Vector3.Zero,
                ParentIndex = 0, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Morphs = model.Morphs, DisplayFrames = model.DisplayFrames, RigidBodies = model.RigidBodies,
            Joints = model.Joints, SoftBodies = model.SoftBodies };
        var layout = new SceneCompositionEditor();
        var item = layout.Add("actor", Path.GetFullPath("lamp-fixture.pmx"), ModelRole.Character, EditedMotionProjectTests.Rest(model));
        var light = Light(new(item.Id, 1));
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "lamp" },
            new() { BoneName = "lamp", Frame = 30, Translation = new(8, 0, 0) }] };
        using var session = new SceneAnimationSession(layout.Objects, [new(item.Id, model, motion, PhysicsEnabled: false)],
            NoPhysics, new([light]), Token);
        var before = session.Frame;
        session.Seek(30, Token);
        Assert.Equal(before.Surface.Snapshot.Vertices, session.Frame.Surface.Snapshot.Vertices);
        Near(light.Position + new Vector3(8, 0, 0), Assert.Single(session.Frame.Lighting!.Lights).Position);
        Near(light.Position, Assert.Single(before.Lighting!.Lights).Position);
    }

    [Fact]
    public void RemovingAParentDetachesItsLightsAtTheDisplayedWorldPoseAndUndoRestoresTheBinding()
    {
        var model = EditedMotionProjectTests.Model(); var rest = EditedMotionProjectTests.Rest(model);
        var layout = new SceneCompositionEditor();
        var parent = layout.Add("parent", Path.GetFullPath("parent.pmx"), ModelRole.Character, rest);
        layout.Add("room", Path.GetFullPath("room.pmx"), ModelRole.Environment, rest);
        layout.SetTransform(parent.Id, new(7, 8, 9), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f), 2);
        var light = Light(new(parent.Id)); var lights = new SceneLightingEditor(); lights.Add(light);
        var settings = new SceneLightingSettings(.8f, -1.5f); lights.UpdateSettings(settings);
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("Unexpected solver"),
            _ => throw new InvalidOperationException("Unexpected shared world"));
        using var project = new SceneProjectSession(layout, lights, [], provider, cancellationToken: Token);
        var displayed = Assert.Single(project.Frame.Lighting!.Lights);
        project.Edit(edit => edit.RemoveObject(parent.Id), Token);
        Assert.Equal(settings, project.Lighting.Settings); Assert.Equal(settings, project.Frame.Lighting!.Settings);
        Assert.Equal(displayed, Assert.Single(project.Lighting.Lights)); Assert.Null(project.Lighting.Lights[0].Parent);
        Assert.True(project.Undo(Token)); Assert.Equal(light, Assert.Single(project.Lighting.Lights));
        Assert.Equal(settings, project.Lighting.Settings);
        Assert.Equal(displayed, Assert.Single(project.Frame.Lighting!.Lights));
        Assert.True(project.Redo(Token)); Assert.Equal(displayed, Assert.Single(project.Lighting.Lights));
        Assert.Equal(settings, project.Frame.Lighting!.Settings);
    }

    [Fact]
    public void InvalidParentsRejectBeforeSolverAllocationAndFailedLightEditRetainsFrameAndHistory()
    {
        var model = EditedMotionProjectTests.Model();
        var layout = new SceneCompositionEditor();
        var item = layout.Add("actor", Path.GetFullPath("invalid-parent.pmx"), ModelRole.Character, EditedMotionProjectTests.Rest(model));
        foreach (var parent in new[] { new SceneLightParent(Guid.NewGuid()), new SceneLightParent(item.Id, 999), new SceneLightParent(item.Id, 0) })
            Assert.Throws<ArgumentException>(() => new SceneAnimationSession(layout.Objects, [], NoPhysics, new([Light(parent)]), Token));
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("Unexpected solver"),
            _ => throw new InvalidOperationException("Unexpected shared world"));
        var light = Light(); var lights = new SceneLightingEditor(); lights.Add(light);
        using var project = new SceneProjectSession(layout, lights, [], provider, cancellationToken: Token);
        var before = project.Frame; var source = project.Lighting;
        Assert.Throws<ArgumentException>(() => project.Edit(edit => edit.Lighting.Update(light with { Parent = new(Guid.NewGuid()) }), Token));
        Assert.Same(before, project.Frame); Assert.Same(source, project.Lighting); Assert.False(project.CanUndo);
        var changed = light with { Parent = new(item.Id) };
        project.Edit(edit => edit.Lighting.Update(changed), Token);
        Assert.Equal(changed, Assert.Single(project.Lighting.Lights)); Assert.Same(before.Surface, project.Frame.Surface);
        Assert.True(project.Undo(Token)); Assert.Equal(light, Assert.Single(project.Lighting.Lights));
        Assert.True(project.Redo(Token)); Assert.Equal(changed, Assert.Single(project.Lighting.Lights));
    }
}
