using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneValidatedLayoutReuseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("Unexpected solver allocation.");
    private static ProjectSettingsBackendProvider Provider(int substeps = 1) => new((_, item) => NoPhysics(item),
        _ => throw new InvalidOperationException("Unexpected shared solver allocation."), poseInitializationSubsteps: substeps);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PublicConstructorRejectsInvalidPlacementBeforeAllocatingAnyBackend(int invalid)
    {
        var model = SharedPhysicsFixture.Model(); var (layout, actor, _) = SharedPhysicsFixture.Layout(model);
        var objects = layout.Objects.ToArray();
        objects[0] = invalid switch
        {
            0 => actor with { Position = new(float.NaN, 0, 0) },
            1 => actor with { Rotation = new(0, 0, 0, 2) },
            2 => actor with { Scale = 0 },
            // Each placement component is finite; composing its world/vertices must still be validated.
            _ => actor with { Scale = float.MaxValue, Position = new(float.MaxValue, 0, 0) }
        };
        int allocations = 0;
        Assert.ThrowsAny<ArgumentException>(() => new SceneAnimationSession(objects,
            [new(actor.Id, model, SharedPhysicsFixture.Motion(30))], _ => { allocations++; return new TrackingSharedBackend(); },
            cancellationToken: Token));
        Assert.Equal(0, allocations);
    }

    [Fact]
    public void PublicAndProjectConstructorsStillRejectSampledSourcesBeforeAllocatingBackends()
    {
        var model = SharedPhysicsFixture.Model(); var rest = SharedPhysicsFixture.Rest(model);
        var snapshot = rest.Surface.Snapshot;
        var sampled = new OriginalFrameScene(new(snapshot.WithDeformedVertices(snapshot.Vertices.ToArray()),
            rest.Surface.Materials, rest.Surface.Profile), rest.Outline, rest.OutlineOptions, materials: rest.Materials);
        var layout = new SceneCompositionEditor(); var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, sampled);
        int allocations = 0;
        ICharacterPhysicsBackend Create() { allocations++; return new TrackingSharedBackend(); }
        var provider = new ProjectSettingsBackendProvider((_, _) => Create(), _ => throw new InvalidOperationException());
        Assert.Throws<ArgumentException>(() => new SceneAnimationSession(layout.Objects,
            [new(actor.Id, model, SharedPhysicsFixture.Motion(30))], _ => Create(), cancellationToken: Token));
        Assert.Throws<ArgumentException>(() => new SceneProjectSession(layout, new(),
            [new(actor.Id, model, SharedPhysicsFixture.Motion(30))], provider, cancellationToken: Token));
        Assert.Equal(0, allocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrozenLayoutNeverSkipsValidationOfExternallyMutablePmx(bool changeVertices)
    {
        var template = SharedPhysicsFixture.Model();
        // Use explicit arrays: a collection expression targeting IReadOnlyList may use a compiler-owned
        // read-only collection and would not exercise the caller-owned mutation boundary.
        var model = new PmxModel
        {
            Header = template.Header, Name = template.Name, EnglishName = template.EnglishName,
            Comment = template.Comment, EnglishComment = template.EnglishComment,
            Vertices = template.Vertices.ToArray(), Faces = template.Faces.ToArray(), Textures = template.Textures,
            Materials = template.Materials, Bones = template.Bones, Morphs = template.Morphs,
            DisplayFrames = template.DisplayFrames, RigidBodies = template.RigidBodies, Joints = template.Joints, SoftBodies = template.SoftBodies
        };
        var (layout, actor, _) = SharedPhysicsFixture.Layout(model);
        int allocations = 0;
        var provider = new ProjectSettingsBackendProvider((_, _) => { allocations++; return new TrackingSharedBackend(); },
            _ => throw new InvalidOperationException(), poseInitializationSubsteps: 1);
        using var source = new SceneProjectSession(layout, new(), [new(actor.Id, model, SharedPhysicsFixture.Motion(30))],
            provider, initialFrame: 7, cancellationToken: Token);
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion> { [actor.Id] = SharedPhysicsFixture.Motion(30, Vector3.UnitX) });
        var before = source.Frame; int originalAllocations = allocations;
        var vertices = (PmxVertex[])model.Vertices; var faces = (PmxTriangle[])model.Faces;
        var originalVertex = vertices[0]; var originalFace = faces[0];
        if (changeVertices) vertices[0] = vertices[1];
        else faces[0] = new(0, 2, 1);
        var error = Assert.Throws<SceneActorAnimationException>(() => request.CreateSession(provider, Token));
        Assert.Contains(changeVertices ? "rest vertices" : "indices", error.InnerException!.Message);
        Assert.Throws<SceneActorAnimationException>(() => source.Seek(7, Token));
        Assert.Equal(originalAllocations, allocations); Assert.Same(before, source.Frame);
        Assert.True(request.IsCurrent(source)); Assert.False(source.CanUndo);
        vertices[0] = originalVertex; faces[0] = originalFace;
        using var restored = request.CreateSession(provider, Token);
        Assert.Equal(originalAllocations + 1, allocations);
    }

    [Fact]
    public void PreviewRequestAndRepeatedSeekMatchPublicEvaluationExactlyWithoutBorrowingMutableEditors()
    {
        var model = SharedPhysicsFixture.Model(physics: false); var (layout, actor, _) = SharedPhysicsFixture.Layout(model);
        var motion = SharedPhysicsFixture.Motion(30, new(3, 2, 1));
        var draft = SharedPhysicsFixture.Motion(30, new(7, -2, 5));
        using var source = new SceneProjectSession(layout, new(), [new(actor.Id, model, motion)], Provider(),
            initialFrame: 13, cancellationToken: Token);
        var original = source.Frame;
        layout.SetTransform(actor.Id, new(100, 200, 300), Quaternion.Identity, 1);
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion> { [actor.Id] = draft });
        using var preview = source.CreatePosePreview(actor.Id, draft, Token);
        using var detached = request.CreateSession(Provider(), Token);
        using var expected = new SceneAnimationSession(source.Objects, [new(actor.Id, model, draft)], NoPhysics,
            initialFrame: 13, cancellationToken: Token);
        EqualFrame(expected.Frame, preview.Frame); EqualFrame(expected.Frame, detached.Frame);
        Assert.Same(original, source.Frame); Assert.Equal(Vector3.Zero, source.Objects[0].Position);
        preview.UpdatePosePreviewMotion(actor.Id, motion, Token);
        EqualFrame(original, preview.Frame);
        source.Seek(13, Token); EqualFrame(original, source.Frame);
        Assert.False(request.IsCurrent(source)); Assert.False(preview.CanReusePosePreview(source));
        Assert.False(source.CanUndo); Assert.False(source.CanRedo);
    }

    [Fact]
    public void NewPlacementAndSourceInvalidateTheLayoutWhileUndoRedoRestoreTheirOwnFrozenSnapshots()
    {
        var model = SharedPhysicsFixture.Model(physics: false); var (layout, actor, other) = SharedPhysicsFixture.Layout(model);
        var motion = SharedPhysicsFixture.Motion(30, new(3, 2, 1));
        using var source = new SceneProjectSession(layout, new(), [new(actor.Id, model, motion)], Provider(),
            initialFrame: 13, cancellationToken: Token);
        var before = source.Frame;
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        Guid added = Guid.Empty;
        source.Edit(edit =>
        {
            edit.Composition.SetTransform(other.Id, new(-5, 2, 9), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f), 2);
            added = edit.Composition.Add("added", "added.pmx", ModelRole.Environment, StaticFrame(6)).Id;
        }, Token);
        var changed = source.Frame;
        using (var expected = new SceneAnimationSession(source.Objects, [new(actor.Id, model, motion)], NoPhysics,
            initialFrame: 13, cancellationToken: Token)) EqualFrame(expected.Frame, changed);
        Assert.Equal(3, source.Objects.Count); Assert.False(request.IsCurrent(source));
        Assert.True(source.Undo(Token)); EqualFrame(before, source.Frame); Assert.Equal(2, source.Objects.Count);
        source.Seek(13, Token); EqualFrame(before, source.Frame);
        Assert.True(source.Redo(Token)); EqualFrame(changed, source.Frame); Assert.Contains(source.Objects, item => item.Id == added);
        using var oldDetached = request.CreateSession(Provider(), Token);
        EqualFrame(before, oldDetached.Frame); Assert.Equal(2, oldDetached.Objects.Count);
    }

    [Fact]
    public void DetachedSessionAvoidsASecondFullStaticGeometryValidationAllocation()
    {
        const int verticesPerObject = 9000;
        var rest = StaticFrame(verticesPerObject); var layout = new SceneCompositionEditor();
        layout.Add("one", "one.pmx", ModelRole.Environment, rest);
        var second = layout.Add("two", "two.pmx", ModelRole.Environment, rest);
        layout.SetTransform(second.Id, Vector3.UnitX, Quaternion.Identity, 1);
        var provider = Provider();
        using var source = new SceneProjectSession(layout, new(), [], provider, cancellationToken: Token);
        var request = source.CapturePosePreviewRequest(new Dictionary<Guid, VmdMotion>());
        using (var warmPublic = new SceneAnimationSession(source.Objects, [], NoPhysics, cancellationToken: Token)) { }
        using (var warmDetached = request.CreateSession(provider, Token)) { }
        long start = GC.GetAllocatedBytesForCurrentThread();
        using var publicSession = new SceneAnimationSession(source.Objects, [], NoPhysics, cancellationToken: Token);
        long publicBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        using var detached = request.CreateSession(provider, Token);
        long detachedBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        EqualFrame(publicSession.Frame, detached.Frame);
        // One validation Compose alone needs at least two owned copies of the 32-byte surface,
        // 36-byte outline and 4-byte index. Allow metadata variance; never assert wall-clock time.
        Assert.True(publicBytes - detachedBytes >= 2L * verticesPerObject * 100,
            $"Expected static validation geometry to be absent: public={publicBytes}, detached={detachedBytes} bytes.");
    }

    private static void EqualFrame(OriginalFrameScene expected, OriginalFrameScene actual)
    {
        Assert.Equal(expected.Surface.Snapshot.Vertices.ToArray(), actual.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(expected.Surface.Snapshot.Indices.ToArray(), actual.Surface.Snapshot.Indices.ToArray());
        Assert.Equal(expected.Surface.Snapshot.Materials, actual.Surface.Snapshot.Materials);
        Assert.Equal(expected.Surface.Materials, actual.Surface.Materials);
        Assert.Equal(expected.Outline!.Vertices, actual.Outline!.Vertices);
        Assert.Equal(expected.Outline.Materials, actual.Outline.Materials);
        Assert.Equal(expected.Materials, actual.Materials);
        Assert.Equal(expected.Surface.Snapshot.World, actual.Surface.Snapshot.World);
    }

    private static OriginalFrameScene StaticFrame(int count)
    {
        var vertices = Enumerable.Range(0, count).Select(index => new SceneVertex(new(index % 3, index % 5, index % 7),
            Vector3.UnitZ, Vector2.Zero)).ToArray();
        var indices = Enumerable.Range(0, count).Select(index => (uint)index).ToArray();
        var snapshot = new SceneSnapshot(vertices, indices, [new("static", 0, count, Vector4.One, Vector3.Zero, Vector3.Zero, 1)]);
        var outline = new OriginalOutlineData(vertices.Select(vertex => new OriginalOutlineVertex(vertex.Position, vertex.Normal,
            OriginalOutlineVertexMode.Expand, 1, 0)), [new OriginalOutlineMaterial(.18f, Vector4.One)]);
        return new(new(snapshot, [new(Vector3.Zero)]), outline, new(Vector4.One, Matrix4x4.Identity, new(0, 0, 10), 30, 1));
    }
}
