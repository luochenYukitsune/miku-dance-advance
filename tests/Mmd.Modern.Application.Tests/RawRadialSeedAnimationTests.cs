using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

/// <summary>Additional CPU seam witnesses; no native solver or GPU parity is claimed.</summary>
public sealed class RawRadialSeedAnimationTests
{
    [Fact]
    public void VertexMaterialAndAdditionalUvMorphCopiesKeepTheSameRestSeeds()
    {
        var original = PmxSoftBodyTests.MaterialFixture(); var header = original.Header;
        var model = new PmxModel
        {
            Header = new() { Version = header.Version, HeaderDataSize = 8, TextEncoding = header.TextEncoding,
                AdditionalUvCount = 1, VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1,
                BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = original.Name, EnglishName = "", Comment = "", EnglishComment = "", Textures = original.Textures,
            Faces = original.Faces, Materials = original.Materials, Bones = original.Bones, DisplayFrames = [],
            RigidBodies = [], Joints = [], SoftBodies = [],
            Vertices = original.Vertices.Select(vertex => new PmxVertex { Position = vertex.Position,
                Normal = vertex.Normal, Uv = vertex.Uv, AdditionalUvs = [new(1, 2, 3, 4)],
                WeightType = vertex.WeightType, BoneIndices = vertex.BoneIndices, BoneWeights = vertex.BoneWeights,
                EdgeScale = vertex.EdgeScale }).ToArray(),
            Morphs = [new() { Name = "position", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Position,
                          PositionOffsets = [new(0, Vector3.UnitZ)] },
                      new() { Name = "material", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Material,
                          MaterialOffsets = [new(-1, PmxMaterialMorphOperation.Add, new(-.4f, -.2f, 0, 0), Vector3.Zero,
                              0, Vector3.Zero, Vector4.Zero, 0, Vector4.Zero, Vector4.Zero, Vector4.Zero)] },
                      new() { Name = "uv", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.AdditionalUv1,
                          UvOffsets = [new(0, new(2, 4, 6, 8))] }]
        };
        var rest = BoundRest(model); var seeds = rest.Surface.Snapshot.RawRadialSeeds!;
        var saved = seeds.Vertices.ToArray();
        var motion = new VmdMotion { MorphKeyframes = new[] { "position", "material", "uv" }.SelectMany(name =>
            new VmdMorphKeyframe[] { new() { MorphName = name }, new() { MorphName = name, Frame = 10, Weight = 1 } }).ToArray() };
        using var controller = new CharacterAnimationController(model, motion, rest.Surface, rest.Outline!);
        var half = controller.SampleAt(5).Surface.Snapshot;
        Assert.Same(seeds, half.RawRadialSeeds);
        Assert.Equal(rest.Surface.Snapshot.Vertices[0].Position + new Vector3(0, 0, .5f), half.Vertices[0].Position);
        Assert.Equal(new Vector4(.8f, .9f, 1, 1), half.Materials[0].Diffuse);
        Assert.Equal(new Vector4(2, 4, 6, 8), half.AdditionalUvs!.Vertices[0].Uv1);
        var disabled = controller.SampleAt(5, enableMorphs: false).Surface.Snapshot;
        Assert.Same(seeds, disabled.RawRadialSeeds);
        Assert.Equal(rest.Surface.Snapshot.Vertices[0].Position, disabled.Vertices[0].Position);
        Assert.Equal(Vector4.One, disabled.Materials[0].Diffuse);
        Assert.Equal(new Vector4(1, 2, 3, 4), disabled.AdditionalUvs!.Vertices[0].Uv1);
        Assert.Equal(saved, seeds.Vertices.ToArray());
        Assert.Equal(new Vector4(2, 4, 6, 8), half.AdditionalUvs.Vertices[0].Uv1);
    }

    [Fact]
    public void SoftAndFrozenOutsideParentOutputsKeepRestSeedsWithoutAnotherSolverStep()
    {
        var model = PmxSoftBodyTests.MaterialFixture(); var rest = BoundRest(model);
        var seeds = rest.Surface.Snapshot.RawRadialSeeds!; var saved = seeds.Vertices.ToArray();
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 60 }] };
        using var controller = new CharacterAnimationController(model, motion, rest.Surface, rest.Outline!);
        controller.SetOutsideParents(new Dictionary<int, Matrix4x4> { [-1] = Matrix4x4.Identity });
        var soft = new CountingSoftBackend(); controller.EnableSoftBodies(soft, 0);
        var moved = controller.Advance(60, .1);
        Assert.True(soft.StepCount > 0);
        Assert.True(moved.Surface.Snapshot.Vertices[4].Position.Y < rest.Surface.Snapshot.Vertices[4].Position.Y);
        Assert.NotEqual(rest.Surface.Snapshot.Vertices[4].Normal, moved.Surface.Snapshot.Vertices[4].Normal);
        Assert.Same(seeds, moved.Surface.Snapshot.RawRadialSeeds);
        int steps = soft.StepCount, resets = soft.ResetCount, samples = soft.SampleCount;
        Assert.Same(moved.Surface, controller.SampleAt(60).Surface);
        Assert.Equal(samples, soft.SampleCount); Assert.Equal(steps, soft.StepCount);
        var frozen = controller.RefreshFrozenOutsideParents(new Dictionary<int, Matrix4x4>
            { [-1] = Matrix4x4.CreateTranslation(1, 0, 0) });
        Assert.Equal(moved.Surface.Snapshot.Vertices[4].Position + Vector3.UnitX,
            frozen.Surface.Snapshot.Vertices[4].Position);
        Assert.Same(seeds, frozen.Surface.Snapshot.RawRadialSeeds);
        Assert.Equal(steps, soft.StepCount); Assert.Equal(resets, soft.ResetCount);
        samples = soft.SampleCount;
        Assert.Same(frozen.Surface, controller.SampleAt(60).Surface);
        Assert.Equal(samples, soft.SampleCount); Assert.Equal(steps, soft.StepCount);
        Assert.Equal(saved, seeds.Vertices.ToArray());
        Assert.Equal(saved, moved.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
    }

    [Fact]
    public void NestedCompositionBakesOnlyTheNewRemainingWorldDirection()
    {
        var rest = BoundRest(EditedMotionProjectTests.Model());
        var firstWorld = Matrix4x4.CreateScale(-2, 3, .5f) * Matrix4x4.CreateRotationZ(.43f)
            * Matrix4x4.CreateTranslation(7, 8, 9);
        var first = Combine(Place(rest, firstWorld));
        var remaining = Matrix4x4.CreateRotationX(-.24f) * Matrix4x4.CreateScale(1.2f, .7f, 2)
            * Matrix4x4.CreateTranslation(-100, 400, 200);
        var nested = Combine(Place(first, remaining)).Surface.Snapshot;
        var raw = rest.Surface.Snapshot.RawRadialSeeds!.Vertices;
        for (int i = 0; i < raw.Length; i++)
            Assert.InRange(Vector3.Distance(Direction(Direction(raw[i], firstWorld), remaining),
                nested.RawRadialSeeds!.Vertices[i]), 0, 2e-6f);
        Assert.Equal(Matrix4x4.Identity, nested.World);
    }

    private static OriginalFrameScene BoundRest(PmxModel model)
    {
        var rest = EditedMotionProjectTests.Rest(model);
        var seeds = new SceneRawRadialSeeds(Enumerable.Range(0, model.Vertices.Count)
            .Select(index => new Vector3(.3f + index * .01f, -.7f + index * .02f, .2f)).ToArray());
        return new(new(rest.Surface.Snapshot.WithRawRadialSeeds(seeds), rest.Surface.Materials, rest.Surface.Profile),
            rest.Outline, rest.OutlineOptions, rest.Options, rest.Materials);
    }
    private static OriginalFrameScene Place(OriginalFrameScene input, Matrix4x4 world)
    {
        var source = input.Surface.Snapshot;
        var snapshot = new SceneSnapshot(source.Vertices, source.Indices, source.Materials, source.Textures,
            world, source.Camera, source.LightDirection, source.AdditionalUvs, source.RawRadialSeeds);
        return new(new(snapshot, input.Surface.Materials, input.Surface.Profile), input.Outline, input.OutlineOptions,
            input.Options, input.Materials);
    }
    private static OriginalFrameScene Combine(params OriginalFrameScene[] frames) =>
        ProjectSceneAssembler.Combine(frames, Vector3.One, Vector3.UnitY);
    private static Vector3 Direction(Vector3 value, Matrix4x4 matrix) => new(
        value.X * matrix.M11 + value.Y * matrix.M21 + value.Z * matrix.M31,
        value.X * matrix.M12 + value.Y * matrix.M22 + value.Z * matrix.M32,
        value.X * matrix.M13 + value.Y * matrix.M23 + value.Z * matrix.M33);

    // Same small mutable solver contract as ControllerSoftBodyTests, with counters for cached capture checks.
    // It exercises transport through soft/frozen publication, not Bullet numerical compatibility.
    private sealed class CountingSoftBackend : ISoftBodyBackend
    {
        private int[] ids = []; private Vector3[] positions = [];
        public int ResetCount, StepCount, SampleCount;
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(IReadOnlyList<SoftBodyDefinition> definitions, IReadOnlyList<PhysicsBodyDefinition> colliders)
            => ids = definitions.SelectMany(definition => definition.VertexIndices).ToArray();
        public void Reset(ReadOnlySpan<Vector3> targets, ReadOnlySpan<Matrix4x4> rigids)
        { positions = targets.ToArray(); ResetCount++; }
        public void Step(float seconds, ReadOnlySpan<Vector3> targets, ReadOnlySpan<Matrix4x4> rigids)
        { StepCount++; foreach (int id in ids) positions[id] -= new Vector3(0, seconds, 0); }
        public IReadOnlyList<SoftBodySample> Sample()
        { SampleCount++; return [new(ids, ids.Select(id => positions[id]).ToArray(), ids.Select(_ => Vector3.UnitX).ToArray())]; }
        public void SetGravity(Vector3 gravity) { }
        public void Dispose() { }
    }
}
