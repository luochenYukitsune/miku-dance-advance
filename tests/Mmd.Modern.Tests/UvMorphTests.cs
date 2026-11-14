using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;
using static Mmd.Modern.Tests.MorphApplicationTests;

namespace Mmd.Modern.Tests;

public sealed class UvMorphTests
{
    private static PmxMorph Uv(string name, params PmxMorph.UvOffset[] offsets) => new()
    {
        Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Uv, UvOffsets = offsets
    };

    [Fact]
    public void BaseUvOffsetsAddWithUnclampedWeightsAndCopyTheirSource()
    {
        PmxMorph.UvOffset[] source = [new(0, new(.25f, -.5f, 100, -100)), new(0, new(.5f, .25f, 2, 3))];
        var adapter = new PmxMorphAdapter(Model([Uv("a", source), Uv("b", new PmxMorph.UvOffset(0, new(1, .5f, 4, 5)))]));
        source[0] = new(0, new(99));
        Assert.Equal(2, adapter.SupportedMorphCount);
        Assert.Equal(0, adapter.SupportedMorphIndices["a"]);
        var pose = Assert.IsType<MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [0] = -1, [1] = 2 }));
        Assert.Equal(new Vector2(1.25f, 1.25f), pose.VertexUvDeltas[0]);
        Assert.All(pose.VertexDeltas, value => Assert.Equal(Vector3.Zero, value));
        Assert.Null(adapter.Evaluate(new Dictionary<int, float> { [0] = 0 }));
    }

    [Fact]
    public void SharedMorphNameMapInterpolatesUvAndOtherSupportedTracks()
    {
        var adapter = new PmxMorphAdapter(Model([Uv("uv", new PmxMorph.UvOffset(0, new(1, 2, 3, 4)))]));
        var names = new Dictionary<string, int>(adapter.SupportedMorphIndices) { ["material"] = 5 };
        var motion = new MmdMorphMotionAdapter(names, new()
        {
            MorphKeyframes = [Key("uv", 0, 0), Key("uv", 10, 1), Key("material", 0, .75f)]
        });
        Assert.Equal(2, motion.MatchedTrackCount);
        Assert.Equal(.75f, motion.Evaluate(2.5)[5]);
        Assert.Equal(new Vector2(.25f, .5f), Assert.IsType<MorphPose>(adapter.Evaluate(motion.Evaluate(2.5))).VertexUvDeltas[0]);
        Assert.Empty(motion.Diagnostics);
    }

    [Fact]
    public void AdditionalUvChannelsStayExplicitlyUnsupported()
    {
        var adapter = new PmxMorphAdapter(Model([new() { Name = "uv1", EnglishName = "", ControlPanel = 1,
            Type = PmxMorphType.AdditionalUv1, UvOffsets = [new(0, Vector4.One)] }]));
        Assert.Equal(0, adapter.SupportedMorphCount);
        Assert.Contains(adapter.Diagnostics, value => value.Contains("AdditionalUv1", StringComparison.Ordinal));
        var motion = new MmdMorphMotionAdapter(adapter, new() { MorphKeyframes = [Key("uv1", 0, 1)] });
        Assert.False(motion.HasMatchedTracks);
        Assert.Contains(motion.Diagnostics, value => value.Contains("no matching supported morph", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void UvOffsetsRejectOutOfRangeVertexIndices(int index)
        => Assert.Throws<ArgumentException>(() => new PmxMorphAdapter(Model([Uv("uv", new PmxMorph.UvOffset(index, Vector4.One))])));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void UvOffsetsRejectEveryNonFiniteComponent(int component)
    {
        var value = new Vector4(1); value[component] = float.NaN;
        Assert.Throws<ArgumentException>(() => new PmxMorphAdapter(Model([Uv("uv", new PmxMorph.UvOffset(0, value))])));
    }

    [Fact]
    public void PoseCopiesUvDeltasAndRejectsNonFiniteOrMismatchedVertexCounts()
    {
        Vector2[] source = [new(.25f, .5f)];
        var pose = new MorphPose([Vector3.Zero], [], [], source);
        source[0] = new(999);
        Assert.Equal(new Vector2(.25f, .5f), pose.VertexUvDeltas[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<Vector2>)pose.VertexUvDeltas)[0] = Vector2.Zero);
        Assert.Throws<ArgumentException>(() => new MorphPose([Vector3.Zero], [], [], []));
        Assert.Throws<ArgumentException>(() => new MorphPose([Vector3.Zero], [], [], [new(float.NaN, 0)]));
        Assert.Throws<ArgumentException>(() => new MorphPose([Vector3.Zero], [], [], [new(0, float.PositiveInfinity)]));
        var rig = new CharacterRig([new("root", Vector3.Zero, -1)], [new SceneVertex(Vector3.UnitX, Vector3.UnitY, Vector2.Zero)],
            [new(SkinningKind.Bdef1, 0, -1, -1, -1, new(1, 0, 0, 0))]);
        var animator = new CharacterAnimator(rig, new([]));
        var wrong = new CharacterEvaluationOptions(Morphs: new([], [Vector3.Zero], [Quaternion.Identity], []));
        Assert.Throws<ArgumentException>(() => animator.Sample(0, wrong));
        Assert.Throws<ArgumentException>(() => animator.BeginPhysicsSample(0, wrong));
    }

    [Theory]
    [InlineData(SkinningKind.Bdef1)]
    [InlineData(SkinningKind.Bdef2)]
    [InlineData(SkinningKind.Bdef4)]
    [InlineData(SkinningKind.Sdef)]
    public void UvMorphPreservesSkinningGeometryAndPhysicsCompletion(SkinningKind kind)
    {
        var rig = new CharacterRig([new("root", Vector3.Zero, -1)],
            [new SceneVertex(new(1, 2, 3), Vector3.UnitY, new(.125f, .25f))],
            [new(kind, 0, 0, 0, 0, kind == SkinningKind.Bdef1 ? new(1, 0, 0, 0) : new(.5f, .5f, 0, 0))]);
        var animator = new CharacterAnimator(rig, new([new("root", [new(0, new(2, 3, 4),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f), BoneCurves.Linear)])]));
        var baseline = animator.Sample(0);
        var options = new CharacterEvaluationOptions(Morphs: new([Vector3.Zero], [Vector3.Zero], [Quaternion.Identity], [new(.5f, -.75f)]));
        var sample = animator.Sample(0, options);
        Assert.Equal(new Vector2(.625f, -.5f), sample.Vertices[0].Uv);
        Assert.Equal(baseline.Vertices[0].Position, sample.Vertices[0].Position);
        Assert.Equal(baseline.Vertices[0].Normal, sample.Vertices[0].Normal);
        Assert.Equal(baseline.BoneWorldMatrices, sample.BoneWorldMatrices);
        using var physics = animator.BeginPhysicsSample(0, options);
        Assert.Equal(sample.Vertices, physics.Complete(physics.BeforePhysicsBoneWorldMatrices, []).Vertices);
        Assert.Equal(baseline.Vertices, animator.Sample(0).Vertices);
    }

    [Fact]
    public void ControllerInterpolatesUvsKeepsSurfaceOutlineGeometryAndToggleRestoresExactVertices()
    {
        using var controller = Controller(Model([Uv("uv", new PmxMorph.UvOffset(0, new(.5f, -.25f, 9, 8)))]), new()
        {
            MorphKeyframes = [Key("uv", 0, 0), Key("uv", 10, 1)]
        });
        var off = controller.Sample(5, enableMorphs: false);
        var on = controller.Sample(5);
        Assert.Equal(new Vector2(.25f, -.125f), on.Surface.Snapshot.Vertices[0].Uv);
        Assert.Equal(on.Surface.Snapshot.Vertices[0].Position, on.Outline.Vertices[0].Position);
        Assert.Equal(off.Surface.Snapshot.Vertices[0].Position, on.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(off.Surface.Snapshot.Vertices.ToArray(), controller.Sample(5, enableMorphs: false).Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(on.Surface.Snapshot.Vertices.ToArray(), controller.Sample(5).Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(Vector2.Zero, controller.Sample(0).Surface.Snapshot.Vertices[0].Uv);
        Assert.Equal(new Vector2(.05f, -.025f), controller.Advance(1, 0).Surface.Snapshot.Vertices[0].Uv);
        Assert.Equal(new Vector2(.075f, -.0375f), controller.Advance(1.5, 0).Surface.Snapshot.Vertices[0].Uv);
    }
}
