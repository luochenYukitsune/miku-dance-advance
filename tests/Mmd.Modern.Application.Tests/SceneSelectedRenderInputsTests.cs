using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneSelectedRenderInputsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void OmittedRequestsKeepTheOriginalSceneAndEmptyMetadataWithoutRequiringAController()
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        _ = layout.Add("static", "static.pmx", ModelRole.Character, Rest(model, [Texture("a")]));
        using var project = new SceneProjectSession(layout, new(), [], new PhysicsProvider(), cancellationToken: Token);
        var scene = project.Frame; var captured = project.CaptureRenderSnapshot();
        Assert.Same(scene, captured.Scene); Assert.Same(SceneObjectRenderInputs.Empty, captured.SceneInputs);
        Assert.Same(SceneObjectRenderInputs.Empty, project.CaptureRenderSnapshot([]).SceneInputs);
        Assert.Equal(0, captured.SourceFrame); Assert.Equal("mmd", Assert.Single(captured.RenderingStyles).Preset.StableId);
    }

    [Fact]
    public void RequestsDetachTheirIndicesAndCoalesceDuplicatesButRejectNegativeSlots()
    {
        int[] bones = [2, 0, 2]; int[] textures = [1, 1, 0];
        var request = new SceneObjectRenderRequest(Guid.NewGuid(), bones, textures);
        bones[0] = 10; textures[0] = 10;
        Assert.Equal(new[] { 2, 0 }, request.BoneIndices); Assert.Equal(new[] { 1, 0 }, request.LocalTextureIndices);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)request.BoneIndices).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<int>)request.LocalTextureIndices).Clear());
        Assert.Throws<ArgumentException>(() => new SceneObjectRenderRequest(Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneObjectRenderRequest(Guid.NewGuid(), [-1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneObjectRenderRequest(Guid.NewGuid(), localTextureIndices: [-1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextureMappingUsesReferenceIdentityThroughDeduplicationAndCompositionOrder(bool reverse)
    {
        var model = Model(); var shared = Texture("same"); var uniqueA = Texture("same"); var equalButDistinctB = Texture("same");
        var frameA = Rest(model, [uniqueA, shared]); var frameB = Rest(model, [shared, equalButDistinctB]);
        var layout = new SceneCompositionEditor();
        var first = layout.Add(reverse ? "B" : "A", "first.pmx", ModelRole.Character, reverse ? frameB : frameA);
        var second = layout.Add(reverse ? "A" : "B", "second.pmx", ModelRole.Character, reverse ? frameA : frameB);
        var a = reverse ? second : first; var b = reverse ? first : second;
        using var project = new SceneProjectSession(layout, new(), [], new PhysicsProvider(), cancellationToken: Token);
        var capture = project.CaptureRenderSnapshot([new(a.Id, localTextureIndices: [0, 1]), new(b.Id, localTextureIndices: [0, 1])]);
        Assert.Equal(3, capture.Scene.Surface.Snapshot.Textures.Count);
        var textures = capture.Scene.Surface.Snapshot.Textures;
        Assert.Same(uniqueA, textures[capture.SceneInputs[a.Id].TextureIndices[0]]);
        Assert.Same(shared, textures[capture.SceneInputs[a.Id].TextureIndices[1]]);
        Assert.Same(shared, textures[capture.SceneInputs[b.Id].TextureIndices[0]]);
        Assert.Same(equalButDistinctB, textures[capture.SceneInputs[b.Id].TextureIndices[1]]);
        Assert.Equal(capture.SceneInputs[a.Id].TextureIndices[1], capture.SceneInputs[b.Id].TextureIndices[0]);
        Assert.NotEqual(capture.SceneInputs[a.Id].TextureIndices[0], capture.SceneInputs[b.Id].TextureIndices[1]);
        Assert.All(capture.SceneInputs.Values, inputs => Assert.Empty(inputs.BoneWorldMatrices));
    }

    [Fact]
    public void HiddenInputsDoNotConsumeAbsentTexturesOrValidateDeferredBoundsAndRemapWhenShown()
    {
        var model = Model(); var layout = new SceneCompositionEditor(); var firstTexture = Texture("first"); var secondTexture = Texture("second");
        var first = layout.Add("first", "first.pmx", ModelRole.Character, Rest(model, [firstTexture]));
        var second = layout.Add("second", "second.pmx", ModelRole.Character, Rest(model, [secondTexture]));
        using var project = new SceneProjectSession(layout, new(),
            [new(first.Id, model, Motion(hide: true), PhysicsEnabled: false)], new PhysicsProvider(), cancellationToken: Token);
        SceneObjectRenderRequest[] requests = [new(first.Id, [0], [0]), new(second.Id, localTextureIndices: [0])];
        var visible = project.CaptureRenderSnapshot(requests); Assert.Equal(1, visible.SceneInputs[second.Id].TextureIndices[0]);
        project.Seek(5, Token); var hidden = project.CaptureRenderSnapshot(requests);
        Assert.False(hidden.SceneInputs[first.Id].Visible); Assert.Empty(hidden.SceneInputs[first.Id].BoneWorldMatrices);
        Assert.Empty(hidden.SceneInputs[first.Id].TextureIndices); Assert.Equal(0, hidden.SceneInputs[second.Id].TextureIndices[0]);
        Assert.Same(secondTexture, hidden.Scene.Surface.Snapshot.Textures[0]);
        Assert.False(project.CaptureRenderSnapshot([new(first.Id, [999], [999])]).SceneInputs[first.Id].Visible);
        project.Seek(10, Token); var shown = project.CaptureRenderSnapshot(requests);
        Assert.True(shown.SceneInputs[first.Id].Visible); Assert.Single(shown.SceneInputs[first.Id].BoneWorldMatrices);
        Assert.Equal(1, shown.SceneInputs[second.Id].TextureIndices[0]);
        Assert.Equal(0, hidden.SceneInputs[second.Id].TextureIndices[0]); // Older samples remain detached.
        Assert.Throws<ArgumentOutOfRangeException>(() => project.CaptureRenderSnapshot([new(first.Id, [999], [999])]));
    }

    [Fact]
    public void OnlySelectedSolvedBonesAreCopiedAndPreviousSamplesRemainImmutable()
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, Rest(model, []));
        layout.SetTransform(actor.Id, new(7, 3, -2), Quaternion.CreateFromAxisAngle(Vector3.UnitX, .3f), 2);
        SceneActorMotion[] motions = [new(actor.Id, model, Motion(), PhysicsEnabled: false)];
        using var project = new SceneProjectSession(layout, new(), motions, new PhysicsProvider(), cancellationToken: Token);
        using var oracle = new SceneAnimationSession(layout.Objects, motions, _ => throw new InvalidOperationException(), cancellationToken: Token);
        var first = project.CaptureRenderSnapshot([new(actor.Id, [1])]); var before = first.SceneInputs[actor.Id].BoneWorldMatrices[1];
        Near(oracle.GetBoneWorldMatrices(actor.Id)[1], before);
        Assert.Equal(new[] { 1 }, first.SceneInputs[actor.Id].BoneWorldMatrices.Keys);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<int, Matrix4x4>)first.SceneInputs[actor.Id].BoneWorldMatrices).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<int, int>)first.SceneInputs[actor.Id].TextureIndices).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<Guid, SceneObjectRenderInputs>)first.SceneInputs).Clear());
        project.Seek(10, Token); oracle.Seek(10, Token);
        var second = project.CaptureRenderSnapshot([new(actor.Id, [1])]);
        Near(oracle.GetBoneWorldMatrices(actor.Id)[1], second.SceneInputs[actor.Id].BoneWorldMatrices[1]);
        Assert.NotEqual(before, second.SceneInputs[actor.Id].BoneWorldMatrices[1]);
        Assert.Equal(before, first.SceneInputs[actor.Id].BoneWorldMatrices[1]); Assert.Equal(0, first.SourceFrame); Assert.Equal(10, second.SourceFrame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedPlacedSourceCaptureSharesFrozenGeometryAndMatchesComposedWorld(bool animated)
    {
        var model = Model(); var rest = Rest(model, []); var layout = new SceneCompositionEditor();
        var actor = layout.Add("placed", "placed.pmx", ModelRole.Character, rest);
        layout.SetTransform(actor.Id, new(7, -3, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .43f), 2);
        using var project = new SceneProjectSession(layout, new(),
            animated ? [new(actor.Id, model, Motion(), PhysicsEnabled: false)] : [],
            new PhysicsProvider(), cancellationToken: Token);
        if (animated) project.Seek(4, Token);
        SceneObjectRenderRequest[] requests = [new(actor.Id, includeSourceGeometry: true)];
        var first = project.CaptureRenderSnapshot(requests).SceneInputs[actor.Id].SourceGeometry!;
        var second = project.CaptureRenderSnapshot(requests).SceneInputs[actor.Id].SourceGeometry!;
        var originalVertices = first.Vertices.ToArray();
        Assert.Equal(animated, first.HasDynamicVertices);
        Assert.Equal(first.World, second.World);
        Assert.True(first.Vertices.Overlaps(second.Vertices, out int vertexOffset)); Assert.Equal(0, vertexOffset);
        Assert.True(first.Indices.Overlaps(second.Indices, out int indexOffset)); Assert.Equal(0, indexOffset);
        for (int index = 0; index < first.Vertices.Length; index++)
            Assert.InRange(Vector3.Distance(Vector3.Transform(first.Vertices[index].Position, first.World),
                project.Frame.Surface.Snapshot.Vertices[index].Position), 0, 1e-5f);
        if (animated) project.Seek(9, Token);
        Assert.Equal(originalVertices, first.Vertices.ToArray());
        Assert.False(project.IsFaulted);
    }

    [Fact]
    public void OutsideParentAndObjectPlacementUseTheExistingSolvedWorldExactlyOnce()
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        var child = layout.Add("child", "child.pmx", ModelRole.Character, Rest(model, []));
        var parent = layout.Add("parent", "parent.pmx", ModelRole.Character, Rest(model, []));
        layout.SetTransform(child.Id, new(8, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f), 2);
        layout.SetTransform(parent.Id, new(-3, 5, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.2f), .5f);
        SceneActorMotion[] motions = [new(child.Id, model, Motion(), PhysicsEnabled: false), new(parent.Id, model, Motion(), PhysicsEnabled: false)];
        SceneOutsideParentBinding[] bindings = [new(child.Id, 0, parent.Id, 1, Matrix4x4.CreateRotationY(.2f) * Matrix4x4.CreateTranslation(0, 3, 1))];
        using var project = new SceneProjectSession(layout, new(), motions, new PhysicsProvider(), outsideParents: bindings, cancellationToken: Token);
        using var oracle = new SceneAnimationSession(layout.Objects, motions, _ => throw new InvalidOperationException(), outsideParents: bindings, cancellationToken: Token);
        project.Seek(10, Token); oracle.Seek(10, Token);
        var snapshot = project.CaptureRenderSnapshot([new(child.Id, [1]), new(parent.Id, [1])]);
        Near(oracle.GetBoneWorldMatrices(child.Id)[1], snapshot.SceneInputs[child.Id].BoneWorldMatrices[1]);
        Near(oracle.GetBoneWorldMatrices(parent.Id)[1], snapshot.SceneInputs[parent.Id].BoneWorldMatrices[1]);
        Assert.Same(project.Frame, snapshot.Scene); Assert.Equal(project.CurrentFrame, snapshot.SourceFrame);
        Assert.NotEqual(oracle.GetBoneWorldMatrices(child.Id)[1] * layout.Objects[0].World, snapshot.SceneInputs[child.Id].BoneWorldMatrices[1]);
    }

    [Fact]
    public void InvalidRequestsRejectWithoutChangingThePublishedFrameOrClock()
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, Rest(model, [Texture("a")]));
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, Motion(), PhysicsEnabled: false)], new PhysicsProvider(), cancellationToken: Token);
        project.Seek(3, Token); var before = project.Frame;
        Assert.Throws<ArgumentException>(() => project.CaptureRenderSnapshot([new(Guid.NewGuid())]));
        var request = new SceneObjectRenderRequest(actor.Id);
        Assert.Throws<ArgumentException>(() => project.CaptureRenderSnapshot([request, request]));
        Assert.Throws<ArgumentException>(() => project.CaptureRenderSnapshot([null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => project.CaptureRenderSnapshot([new(actor.Id, [3])]));
        Assert.Throws<ArgumentOutOfRangeException>(() => project.CaptureRenderSnapshot([new(actor.Id, localTextureIndices: [1])]));
        Assert.Same(before, project.Frame); Assert.Equal(3, project.CurrentFrame); Assert.False(project.IsFaulted);
    }

    [Fact]
    public void StaticActorsSupportSelectedTexturesButNeverInventASolvedSkeleton()
    {
        var model = Model(); var layout = new SceneCompositionEditor(); var texture = Texture("map");
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, Rest(model, [texture]));
        using var project = new SceneProjectSession(layout, new(), [], new PhysicsProvider(), cancellationToken: Token);
        Assert.Throws<InvalidOperationException>(() => project.CaptureRenderSnapshot([new(actor.Id, [0])]));
        var capture = project.CaptureRenderSnapshot([new(actor.Id, localTextureIndices: [0])]);
        Assert.Empty(capture.SceneInputs[actor.Id].BoneWorldMatrices); Assert.Equal(0, capture.SceneInputs[actor.Id].TextureIndices[0]);
        Assert.False(Assert.Single(project.Actors).HasMotion); Assert.False(project.IsFaulted);
    }

    [Fact]
    public void CaptureDoesNotStepPhysicsAndRejectsFaultedOwnersUntilRecovery()
    {
        var model = Model(physics: true); var layout = new SceneCompositionEditor(); var provider = new PhysicsProvider();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, Rest(model, []));
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, Motion())], provider, cancellationToken: Token);
        project.Advance(1d / 30, Token); var backend = Assert.Single(provider.Backends); int steps = backend.Steps;
        var frame = project.Frame; var first = project.CaptureRenderSnapshot([new(actor.Id, [0])]);
        var second = project.CaptureRenderSnapshot([new(actor.Id, [0])]);
        Assert.Equal(steps, backend.Steps); Assert.Same(frame, second.Scene);
        Assert.Equal(first.SceneInputs[actor.Id].BoneWorldMatrices[0], second.SceneInputs[actor.Id].BoneWorldMatrices[0]);
        backend.FailStep = true;
        Assert.Throws<SceneActorAnimationException>(() => project.Advance(1d / 30, Token));
        Assert.True(project.IsFaulted); Assert.Throws<InvalidOperationException>(() => project.CaptureRenderSnapshot([new(actor.Id, [0])]));
        Assert.Throws<InvalidOperationException>(() => project.CaptureRenderSnapshot());
        backend.FailStep = false; project.Seek(0, Token);
        Assert.Single(project.CaptureRenderSnapshot([new(actor.Id, [0])]).SceneInputs[actor.Id].BoneWorldMatrices);
    }

    [Fact]
    public async Task OfflineCaptureCarriesEachSelectedPoseAtItsGeometryClockAndDetachesTheRequestList()
    {
        var model = Model(); var layout = new SceneCompositionEditor(); var texture = Texture("map");
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, Rest(model, [texture]));
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, Motion(), PhysicsEnabled: false)], new PhysicsProvider(), cancellationToken: Token);
        var requests = new List<SceneObjectRenderRequest> { new(actor.Id, [1], [0]) }; var frames = new List<OfflineFrame>();
        var result = await OfflineFrameRunner.RunProjectAsync(project, new(2, 2, new(120), 0, 1), (frame, _) =>
        {
            requests.Clear(); var expected = project.CaptureRenderSnapshot([new(actor.Id, [1], [0])]);
            Assert.Equal(expected.SourceFrame, frame.Sample.SampledSourceFrame.ToDouble()); Assert.Same(expected.Scene, frame.Scene);
            Assert.Equal(expected.SceneInputs[actor.Id].BoneWorldMatrices[1], frame.SceneInputs[actor.Id].BoneWorldMatrices[1]);
            Assert.Same(texture, frame.Scene.Surface.Snapshot.Textures[frame.SceneInputs[actor.Id].TextureIndices[0]]);
            frames.Add(frame); return ValueTask.CompletedTask;
        }, cancellationToken: Token, sceneInputRequests: requests);
        Assert.Equal(5, result.CompletedFrames); Assert.Equal(5, frames.Count);
        Assert.NotEqual(frames[0].SceneInputs[actor.Id].BoneWorldMatrices[1], frames[^1].SceneInputs[actor.Id].BoneWorldMatrices[1]);
        Assert.Equal(0, frames[0].Sample.SampledSourceFrame.ToDouble());
    }

    [Fact]
    public async Task OfflineInvalidInputsAreRejectedBeforeSeeking()
    {
        var model = Model(); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, Rest(model, []));
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, Motion(), PhysicsEnabled: false)], new PhysicsProvider(), cancellationToken: Token);
        project.Seek(4, Token); var before = project.Frame; int calls = 0;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => OfflineFrameRunner.RunProjectAsync(project, new(2, 2, new(30), 0, 1),
            (_, _) => { calls++; return ValueTask.CompletedTask; }, cancellationToken: Token, sceneInputRequests: [new(actor.Id, [99])]));
        Assert.Equal(0, calls); Assert.Equal(4, project.CurrentFrame); Assert.Same(before, project.Frame);
    }

    private static SceneTexture Texture(string name) => new(name, 1, 1, [20, 40, 60, 255]);
    private static OriginalFrameScene Rest(PmxModel model, SceneTexture[] textures)
    {
        var rest = EditedMotionProjectTests.Rest(model); var source = rest.Surface.Snapshot;
        var scene = new SceneSnapshot(source.Vertices, source.Indices, source.Materials, textures, source.World, source.Camera, source.LightDirection, source.AdditionalUvs);
        return new(new(scene, rest.Surface.Materials, rest.Surface.Profile), rest.Outline, rest.OutlineOptions, rest.Options, rest.Materials, rest.Lighting);
    }
    private static VmdMotion Motion(bool hide = false) => new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 10, Translation = new(10, 0, 0) },
            new() { BoneName = "head" }, new() { BoneName = "head", Frame = 10, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .8f) }],
        IkKeyframes = hide ? [new() { Visible = true }, new() { Frame = 5, Visible = false }, new() { Frame = 10, Visible = true }] : []
    };
    private static PmxModel Model(bool physics = false)
    {
        var basis = EditedMotionProjectTests.Model();
        return new()
        {
            Header = basis.Header, Name = basis.Name, EnglishName = "", Comment = "", EnglishComment = "", Textures = [],
            Faces = basis.Faces, Materials = basis.Materials, Vertices = basis.Vertices, Morphs = [], DisplayFrames = [], Joints = [], SoftBodies = [],
            Bones = [basis.Bones[0], new() { Name = "head", EnglishName = "", Position = Vector3.UnitY, ParentIndex = 0, DeformDepth = 1, Flags = PmxBoneFlags.None },
                new() { Name = "unused", EnglishName = "", Position = Vector3.UnitX, ParentIndex = 0, DeformDepth = 1, Flags = PmxBoneFlags.None }],
            RigidBodies = physics ? [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
                Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }] : []
        };
    }
    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        float[] a = [expected.M11,expected.M12,expected.M13,expected.M14,expected.M21,expected.M22,expected.M23,expected.M24,
            expected.M31,expected.M32,expected.M33,expected.M34,expected.M41,expected.M42,expected.M43,expected.M44];
        float[] b = [actual.M11,actual.M12,actual.M13,actual.M14,actual.M21,actual.M22,actual.M23,actual.M24,
            actual.M31,actual.M32,actual.M33,actual.M34,actual.M41,actual.M42,actual.M43,actual.M44];
        for (int index = 0; index < a.Length; index++) Assert.InRange(MathF.Abs(a[index] - b[index]), 0, 2e-4f);
    }
    private sealed class PhysicsProvider : IProjectPhysicsBackendProvider
    {
        public List<SceneAnimationSessionTests.FallingBackend> Backends { get; } = [];
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
        { var backend = new SceneAnimationSessionTests.FallingBackend(); Backends.Add(backend); return backend; }
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new NotSupportedException();
    }
}
