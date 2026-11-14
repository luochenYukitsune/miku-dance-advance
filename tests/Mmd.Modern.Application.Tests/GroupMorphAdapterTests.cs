using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class GroupMorphAdapterTests
{
    [Fact]
    public void NestedGroupsMultiplyRatiosAndSharedLeavesRetainEveryPathAndDirectWeight()
    {
        var adapter = new PmxMorphAdapter(Model([
            Position("leaf", Vector3.UnitX), Group("nested", new PmxMorph.GroupOffset(0, .5f)),
            Group("root", new PmxMorph.GroupOffset(1, 2), new PmxMorph.GroupOffset(0, 3))]));
        var pose = Assert.IsType<MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [2] = .5f, [1] = 2, [0] = -1 }));
        Assert.Equal(new Vector3(2, 0, 0), pose.VertexDeltas[0]);
        Assert.Equal(3, adapter.SupportedMorphCount);
        Assert.Equal(2, adapter.SupportedMorphIndices["root"]);
    }

    [Fact]
    public void GroupTracksBindThroughExistingMotionAdapterAndKeepUnclampedWeights()
    {
        var adapter = new PmxMorphAdapter(Model([Position("leaf", Vector3.UnitX), Group("group", new PmxMorph.GroupOffset(0, -2))]));
        var motion = new MmdMorphMotionAdapter(adapter, new() { MorphKeyframes = [new() { MorphName = "group", Frame = 0, Weight = 3 }] });
        Assert.Equal(1, motion.MatchedTrackCount);
        Assert.Equal(new Vector3(-6, 0, 0), Assert.IsType<MorphPose>(adapter.Evaluate(motion.Evaluate(0))).VertexDeltas[0]);
    }

    [Fact]
    public void GroupExpandsBoneAndUvLeavesInPmxRootAndOffsetOrder()
    {
        var x = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        var y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var adapter = new PmxMorphAdapter(Model([
            Bone("x", Vector3.UnitX, x), Bone("y", Vector3.UnitY, y),
            new() { Name = "uv", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Uv, UvOffsets = [new(0, new(2, 4, 0, 0))] },
            Group("group", new PmxMorph.GroupOffset(1, 1), new PmxMorph.GroupOffset(0, 1), new PmxMorph.GroupOffset(2, .5f))]));
        var pose = Assert.IsType<MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [3] = 1 }));
        Assert.Equal(new Vector3(1, 1, 0), pose.BoneTranslations[0]);
        Assert.Equal(new Vector2(1, 2), pose.VertexUvDeltas[0]);
        Assert.InRange(Vector3.Distance(Vector3.UnitX, Vector3.Transform(Vector3.UnitZ, pose.BoneRotations[0])), 0, 1e-5f);
    }

    [Fact]
    public void ZeroEmptyAndUnsupportedOnlyGroupsDoNotCreateAPoseOrBindAnEmptyName()
    {
        var adapter = new PmxMorphAdapter(Model([Position("leaf", Vector3.UnitX), Group("group", new PmxMorph.GroupOffset(0, 1)), Group("empty"),
            new() { Name = "flip", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Flip, FlipOffsets = [] },
            Group("unsupported", new PmxMorph.GroupOffset(3, 1))]));
        Assert.Null(adapter.Evaluate(new Dictionary<int, float> { [1] = 0, [2] = 1, [3] = 1, [4] = 1 }));
        Assert.False(adapter.SupportedMorphIndices.ContainsKey("empty"));
        Assert.False(adapter.SupportedMorphIndices.ContainsKey("unsupported"));
        Assert.False(adapter.SupportedMorphIndices.ContainsKey("flip"));
    }

    [Fact]
    public void GroupOffsetsAreCopiedAndDuplicateNamesStillBindTheFirstPmxMorph()
    {
        PmxMorph.GroupOffset[] offsets = [new(0, .5f)];
        var adapter = new PmxMorphAdapter(Model([Position("leaf", Vector3.UnitX), Group("same", offsets), Position("same", Vector3.UnitY)]));
        offsets[0] = new(2, 10);
        Assert.Equal(1, adapter.SupportedMorphIndices["same"]);
        Assert.Equal(new Vector3(.5f, 0, 0), Assert.IsType<MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [1] = 1 })).VertexDeltas[0]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InvalidReferencedIndicesAreRejectedEvenInAnInactiveGroup(int index)
        => Assert.Throws<ArgumentException>(() => new PmxMorphAdapter(Model([Position("leaf", Vector3.UnitX), Group("bad", new PmxMorph.GroupOffset(index, 1))])));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonfiniteRatiosAreRejected(float ratio)
        => Assert.Throws<ArgumentException>(() => new PmxMorphAdapter(Model([Position("leaf", Vector3.UnitX), Group("bad", new PmxMorph.GroupOffset(0, ratio))])));

    [Fact]
    public void SelfAndIndirectCyclesAreRejectedIncludingZeroRatioEdges()
    {
        Assert.Throws<ArgumentException>(() => new PmxMorphAdapter(Model([Group("self", new PmxMorph.GroupOffset(0, 0))])));
        Assert.Throws<ArgumentException>(() => new PmxMorphAdapter(Model([Group("a", new PmxMorph.GroupOffset(1, 1)), Group("b", new PmxMorph.GroupOffset(2, 1)), Group("c", new PmxMorph.GroupOffset(0, 1))])));
    }

    [Fact]
    public void OverflowingExpandedWeightsAreRejectedAndSamplingNeverAccumulates()
    {
        var adapter = new PmxMorphAdapter(Model([Position("leaf", Vector3.UnitX), Group("group", new PmxMorph.GroupOffset(0, float.MaxValue))]));
        Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [1] = 2 }));
        var first = Assert.IsType<MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [1] = float.Epsilon }));
        var again = Assert.IsType<MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [1] = float.Epsilon }));
        Assert.Equal(first.VertexDeltas[0], again.VertexDeltas[0]);
        Assert.Null(adapter.Evaluate(new Dictionary<int, float>()));
    }

    [Fact]
    public void VeryDeepGroupsUseAnIterativeTraversal()
    {
        const int count = 10_000;
        var morphs = new PmxMorph[count]; morphs[0] = Position("leaf", Vector3.UnitX);
        for (int i = 1; i < count; i++) morphs[i] = Group($"g{i}", new PmxMorph.GroupOffset(i - 1, 1));
        var adapter = new PmxMorphAdapter(Model(morphs));
        Assert.Equal(Vector3.UnitX, Assert.IsType<MorphPose>(adapter.Evaluate(new Dictionary<int, float> { [count - 1] = 1 })).VertexDeltas[0]);
    }

    [Fact]
    public void ExponentialSharedGraphsHaveABoundedExpansionBudget()
    {
        var morphs = new PmxMorph[25]; morphs[0] = Position("leaf", Vector3.UnitX);
        for (int i = 1; i < morphs.Length; i++) morphs[i] = Group($"g{i}", new PmxMorph.GroupOffset(i - 1, 1), new PmxMorph.GroupOffset(i - 1, 1));
        var adapter = new PmxMorphAdapter(Model(morphs));
        var error = Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [24] = 1 }));
        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaterialGroupsKeepSeparateMultiplyApplicationsForSharedReferences()
    {
        var material = new PmxMorph { Name = "material", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
            MaterialOffsets = [new(0, PmxMaterialMorphOperation.Multiply, new(.5f), Vector3.One, 1, Vector3.One, Vector4.One, 1, Vector4.One, Vector4.One, Vector4.One)] };
        var model = Model([material, Group("group", new PmxMorph.GroupOffset(0, .5f), new PmxMorph.GroupOffset(0, .5f))]);
        var adapter = new PmxMaterialMorphAdapter(model);
        Assert.Equal(1, adapter.SupportedMorphIndices["group"]);
        var surface = PmxOriginalStyleAdapter.FromRestPose(model).Scene;
        var outline = PmxOriginalOutlineAdapter.FromRestPose(model);
        var result = adapter.Apply(surface, outline, new Dictionary<int, float> { [1] = 1 });
        Assert.Equal(new Vector4(.5625f), result.Surface.Snapshot.Materials[0].Diffuse);
        Assert.Equal(Vector4.One, surface.Snapshot.Materials[0].Diffuse);
    }

    [Fact]
    public void MaterialAdapterAlsoRejectsInvalidGroupGraphs()
        => Assert.Throws<ArgumentException>(() => new PmxMaterialMorphAdapter(Model([Group("self", new PmxMorph.GroupOffset(0, 1))])));

    [Fact]
    public void MixedGroupBindsOnceAndDrivesGeometryAndMaterialsThroughTheController()
    {
        var material = new PmxMorph { Name = "fade", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
            MaterialOffsets = [new(0, PmxMaterialMorphOperation.Multiply, new(1, 1, 1, 0), Vector3.One, 1, Vector3.One, Vector4.One, 1, Vector4.One, Vector4.One, Vector4.One)] };
        var model = Model([Position("leaf", Vector3.UnitX), material, Group("mixed", new PmxMorph.GroupOffset(0, 2), new PmxMorph.GroupOffset(1, 1))]);
        var surface = PmxOriginalStyleAdapter.FromRestPose(model).Scene;
        using var controller = new CharacterAnimationController(model, new() { MorphKeyframes = [
            new() { MorphName = "mixed", Frame = 0, Weight = 0 }, new() { MorphName = "mixed", Frame = 10, Weight = 1 }] },
            surface, PmxOriginalOutlineAdapter.FromRestPose(model));
        Assert.Equal(1, controller.MatchedMorphTrackCount);
        Assert.Equal(3, controller.SupportedMorphCount);
        var half = controller.Sample(5);
        Assert.Equal(Vector3.UnitX, half.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(.5f, half.Surface.Snapshot.Materials[0].Diffuse.W);
        var disabled = controller.Sample(5, enableMorphs: false);
        Assert.Equal(Vector3.Zero, disabled.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(1, disabled.Surface.Snapshot.Materials[0].Diffuse.W);
        var repeated = controller.Sample(5);
        Assert.Equal(half.Surface.Snapshot.Vertices[0].Position, repeated.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(.5f, repeated.Surface.Snapshot.Materials[0].Diffuse.W);
    }

    private static PmxMorph Position(string name, Vector3 position) => new()
    { Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position, PositionOffsets = [new(0, position)] };
    private static PmxMorph Bone(string name, Vector3 position, Quaternion rotation) => new()
    { Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Bone, BoneOffsets = [new(0, position, rotation)] };
    private static PmxMorph Group(string name, params PmxMorph.GroupOffset[] offsets) => new()
    { Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Group, GroupOffsets = offsets };

    private static PmxModel Model(IReadOnlyList<PmxMorph> morphs) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "group-test", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
        { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [],
            WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
        Faces = [new(0, 1, 2)], Textures = [], Materials = [new() { Name = "m", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.One,
            SpecularPower = 1, Ambient = Vector3.One, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.One, EdgeSize = 1,
            TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
            ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "bone", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = 0 }],
        Morphs = morphs, DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
    };
}
