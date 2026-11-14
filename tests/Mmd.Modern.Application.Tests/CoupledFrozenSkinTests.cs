using System.Collections;
using System.Numerics;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class CoupledFrozenSkinTests
{
    [Theory]
    [InlineData(PmxVertexWeightType.Bdef2)]
    [InlineData(PmxVertexWeightType.Sdef)]
    [InlineData(PmxVertexWeightType.Qdef)]
    public void NonuniformFrozenSkinRefreshUpdatesActualBulletNodesFromImmutableBaseline(PmxVertexWeightType kind)
    {
        var model = Model(kind);
        using var controller = Controller(model);
        controller.SetOutsideParents(Parents(Matrix4x4.Identity, Matrix4x4.Identity));
        var backend = new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null, coupledSoftBodies: true);
        controller.EnableCoupledPhysicsAt(backend, 0);
        var before = controller.Advance(60, .1);
        var baseline = before.Surface.Snapshot.Vertices.ToArray();
        var binding = Binding(backend);
        var nativeBaseline = binding.Sample().Single().Positions.ToArray();
        var a = Matrix4x4.CreateRotationZ(.8f) * Matrix4x4.CreateTranslation(-2, 3, 1);
        var b = Matrix4x4.CreateRotationY(-.6f) * Matrix4x4.CreateTranslation(5, 1, -3);
        // Initial skin maps are identity. Skinning the actual deformed material-space
        // points independently is the oracle; each cloth node has different weights.
        using var oracle = Controller(Model(kind, baseline.Select(v => v.Position).ToArray()));
        oracle.SetOutsideParents(Parents(a, b));
        var expected = oracle.SampleAt(60).Surface.Snapshot.Vertices;
        var moved = controller.RefreshFrozenOutsideParents(Parents(a, b));
        foreach (int id in new[] { 4, 5, 6 })
        {
            Near(expected[id].Position, moved.Surface.Snapshot.Vertices[id].Position);
            Assert.Equal(moved.Surface.Snapshot.Vertices[id].Position, moved.Outline.Vertices[id].Position);
        }
        AssertColliderMatchesSurface(binding, moved.Surface.Snapshot.Vertices);
        var first = moved.Surface.Snapshot.Vertices.ToArray();
        for (int i = 0; i < 12; i++)
        {
            var returned = controller.RefreshFrozenOutsideParents(Parents(Matrix4x4.Identity, Matrix4x4.Identity));
            foreach (int id in new[] { 4, 5, 6 }) Assert.Equal(baseline[id], returned.Surface.Snapshot.Vertices[id]);
            Assert.Equal(nativeBaseline, binding.Sample().Single().Positions);
            moved = controller.RefreshFrozenOutsideParents(Parents(a, b));
            AssertColliderMatchesSurface(binding, moved.Surface.Snapshot.Vertices);
        }
        Assert.Equal(first, moved.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(baseline, before.Surface.Snapshot.Vertices.ToArray());
        Assert.Throws<InvalidOperationException>(() => controller.Advance(60, .01));
        controller.ResetPhysics(0);
        controller.Advance(1, .01);
    }

    [Fact]
    public void InvalidFrozenSkinMapPreservesPublishedSceneAndActualCollider()
    {
        using var controller = Controller(Model(PmxVertexWeightType.Bdef2, equalWeights: true));
        controller.SetOutsideParents(Parents(Matrix4x4.Identity, Matrix4x4.Identity));
        var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, null, coupledSoftBodies: true);
        controller.EnableCoupledPhysicsAt(backend, 0);
        var before = controller.Advance(60, .1);
        var binding = Binding(backend);
        var nodes = binding.Sample().Single().Positions.ToArray();
        var worlds = controller.BoneWorldMatrices.ToArray();
        Assert.Throws<NotSupportedException>(() => controller.RefreshFrozenOutsideParents(
            Parents(Matrix4x4.Identity, Matrix4x4.CreateScale(-1, -1, 1))));
        Assert.Same(before.Surface, controller.SampleAt(60).Surface);
        Assert.Equal(worlds, controller.BoneWorldMatrices);
        Assert.Equal(nodes, binding.Sample().Single().Positions);
        var moved = controller.RefreshFrozenOutsideParents(Parents(Matrix4x4.CreateTranslation(2, 0, 0), Matrix4x4.Identity));
        AssertColliderMatchesSurface(binding, moved.Surface.Snapshot.Vertices);
    }

    [Fact]
    public void SharedFrozenGeometryOverflowPreflightsEveryActorBeforePublishingAnyCollider()
    {
        var model = Model(PmxVertexWeightType.Bdef2);
        var layout = new SceneCompositionEditor();
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1));
        var first = layout.Add("valid frozen cloth", "first.pmx", ModelRole.Character, rest);
        var second = layout.Add("overflowing frozen cloth", "second.pmx", ModelRole.Character, rest);
        var parent = layout.Add("moving parent", "parent.pmx", ModelRole.Character, rest);
        var shortMotion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 1 }] };
        // The parent stays still through completion. Its next sample has finite
        // rigid bone transforms, but differing weights overflow the face cross length.
        var parentMotion = new VmdMotion { BoneKeyframes = [
            new() { BoneName = "root" }, new() { BoneName = "root", Frame = 1 },
            new() { BoneName = "root", Frame = 2, Translation = new(2, 0, 0) },
            new() { BoneName = "nested" }, new() { BoneName = "nested", Frame = 1 },
            new() { BoneName = "nested", Frame = 2, Translation = new(1e20f, 0, 0) }] };
        var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, null, coupledSoftBodies: true);
        using var scene = new SceneAnimationSession(layout.Objects,
            [new(first.Id, model, shortMotion), new(second.Id, model, shortMotion), new(parent.Id, model, parentMotion, PhysicsEnabled: false)],
            _ => throw new InvalidOperationException(), outsideParents: [
                new(first.Id, -1, parent.Id, 0, Matrix4x4.Identity), new(first.Id, 1, parent.Id, 0, Matrix4x4.Identity),
                new(second.Id, -1, parent.Id, 0, Matrix4x4.Identity), new(second.Id, 1, parent.Id, 1, Matrix4x4.Identity)],
            sharedPhysicsFactory: () => backend, coupledSoftBodies: true);
        scene.Advance(1d / 30, TestContext.Current.CancellationToken);
        var before = scene.Frame;
        var bindings = Bindings(backend);
        Assert.Equal(2, bindings.Length);
        var original = bindings.Select(binding => binding.Sample().SelectMany(sample => sample.Positions).ToArray()).ToArray();
        long steps = scene.SharedPhysicsStepCount;
        Assert.ThrowsAny<Exception>(() => scene.Advance(1d / 120, TestContext.Current.CancellationToken));
        Assert.Same(before, scene.Frame);
        Assert.Equal(steps, scene.SharedPhysicsStepCount);
        for (int i = 0; i < bindings.Length; i++)
            Assert.Equal(original[i], bindings[i].Sample().SelectMany(sample => sample.Positions));
    }

    [Fact]
    public void SharedStoppedActorRetainsExactProjectedNormalsAfterOwnerCompletion()
    {
        var model = Model(PmxVertexWeightType.Bdef2);
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1));
        var layout = new SceneCompositionEditor();
        var child = layout.Add("short cloth", "short.pmx", ModelRole.Character, rest);
        var parent = layout.Add("moving two-bone parent", "parent.pmx", ModelRole.Character, rest);
        var shortMotion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 1 }] };
        var longMotion = new VmdMotion { BoneKeyframes = [
            new() { BoneName = "root" }, new() { BoneName = "root", Frame = 1 },
            new() { BoneName = "root", Frame = 4, Translation = new(-2, 3, 1), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .8f) },
            new() { BoneName = "nested" }, new() { BoneName = "nested", Frame = 1 },
            new() { BoneName = "nested", Frame = 4, Translation = new(5, 1, -3), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -.6f) }] };
        var backend = new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null, coupledSoftBodies: true);
        int worlds = 0;
        using var scene = new SceneAnimationSession(layout.Objects,
            [new(child.Id, model, shortMotion), new(parent.Id, model, longMotion, PhysicsEnabled: false)],
            _ => throw new InvalidOperationException(), outsideParents: [
                new(child.Id, -1, parent.Id, 0, Matrix4x4.Identity), new(child.Id, 1, parent.Id, 1, Matrix4x4.Identity)],
            sharedPhysicsFactory: () => worlds++ == 0 ? backend : new BulletCharacterPhysicsBackend(new(0, -9.8f, 0), null, coupledSoftBodies: true), coupledSoftBodies: true);
        scene.Advance(1d / 30, TestContext.Current.CancellationToken);
        var stopped = scene.Frame.Surface.Snapshot.Vertices[..model.Vertices.Count].ToArray();
        var binding = Binding(backend);
        scene.Advance(2d / 30, TestContext.Current.CancellationToken);
        var restOutline = Assert.IsType<OriginalOutlineData>(rest.Outline);
        using var parentOracle = new CharacterAnimationController(model, longMotion, rest.Surface, restOutline);
        parentOracle.SampleAt(3);
        var a = parentOracle.BoneWorldMatrices[0]; var b = parentOracle.BoneWorldMatrices[1];
        using var positionOracle = Controller(Model(PmxVertexWeightType.Bdef2, stopped.Select(v => v.Position).ToArray()));
        positionOracle.SetOutsideParents(Parents(a, b));
        var expected = positionOracle.SampleAt(60).Surface.Snapshot.Vertices;
        var actual = scene.Frame.Surface.Snapshot.Vertices;
        var actualOutline = Assert.IsType<OriginalOutlineData>(scene.Frame.Outline);
        foreach (int id in new[] { 4, 5, 6 })
        {
            Near(expected[id].Position, actual[id].Position);
            var map = Matrix4x4.Lerp(a, b, model.Vertices[id].BoneWeights[1]);
            Assert.True(Matrix4x4.Invert(map, out var inverse));
            var normal = Vector3.Normalize(Vector3.TransformNormal(stopped[id].Normal, Matrix4x4.Transpose(inverse)));
            Near(normal, actual[id].Normal);
            Near(actual[id].Position, actualOutline.Vertices[id].Position);
        }
        AssertColliderMatchesSurface(binding, actual);
        Assert.Equal(12, scene.SharedPhysicsStepCount);
        Assert.Equal(1, scene.Actors.Single(actor => actor.ObjectId == child.Id).LocalFrame);
        scene.Seek(0, TestContext.Current.CancellationToken);
        Assert.Throws<ObjectDisposedException>(() => binding.Sample());
    }

    private static ICharacterSoftBodyBinding Binding(BulletCharacterPhysicsBackend backend)
        => Assert.Single(Bindings(backend));

    private static ICharacterSoftBodyBinding[] Bindings(BulletCharacterPhysicsBackend backend)
        => ((IEnumerable)typeof(BulletCharacterPhysicsBackend)
            .GetField("softBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend)!)
            .Cast<ICharacterSoftBodyBinding>().ToArray();

    private static void AssertColliderMatchesSurface(ICharacterSoftBodyBinding binding, ReadOnlySpan<SceneVertex> vertices)
    {
        foreach (var sample in binding.Sample())
            for (int i = 0; i < sample.VertexIndices.Length; i++)
                Near(vertices[sample.VertexIndices[i]].Position, sample.Positions[i]);
    }

    private static Dictionary<int, Matrix4x4> Parents(Matrix4x4 a, Matrix4x4 b) => new() { [-1] = a, [1] = b };
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 3e-5f);
    private static CharacterAnimationController Controller(PmxModel model) => new(model,
        new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 60 }] },
        PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model));

    private static PmxModel Model(PmxVertexWeightType kind, Vector3[]? positions = null, bool equalWeights = false)
    {
        var m = PmxSoftBodyTests.MaterialFixture();
        return new()
        {
            Header = m.Header, Name = m.Name, EnglishName = m.EnglishName, Comment = m.Comment, EnglishComment = m.EnglishComment,
            Vertices = m.Vertices.Select((v, i) => new PmxVertex
            {
                Position = positions?[i] ?? v.Position, Normal = v.Normal, Uv = v.Uv, AdditionalUvs = [],
                WeightType = i < 4 ? PmxVertexWeightType.Bdef1 : kind,
                BoneIndices = i < 4 ? [0, -1, -1, -1] : kind == PmxVertexWeightType.Qdef ? [0, 1, 0, 1] : [0, 1, -1, -1],
                BoneWeights = i < 4 ? [1, 0, 0, 0] : kind == PmxVertexWeightType.Qdef
                    ? [.1f, .2f, .1f + (i - 4) * .1f, .6f - (i - 4) * .1f]
                    : equalWeights ? [.5f, .5f, 0, 0] : [.2f + (i - 4) * .2f, .8f - (i - 4) * .2f, 0, 0],
                SdefC = new(.2f, .7f, -.3f), SdefR0 = new(1, 2, 3), SdefR1 = new(-2, 1, .5f), EdgeScale = v.EdgeScale,
            }).ToArray(),
            Faces = m.Faces, Textures = m.Textures, Materials = m.Materials,
            Bones = PmmMotionAdapterTests.Pmx(["root", "nested"]).Bones,
            Morphs = [], DisplayFrames = m.DisplayFrames, RigidBodies = [], Joints = [], SoftBodies = m.SoftBodies,
        };
    }
}
