using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmxImpulseMorphAdapterTests
{
    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, -1)]
    [InlineData(.01f, 0)]
    [InlineData(.499f, 0)]
    [InlineData(.5f, 1)]
    [InlineData(.749f, 1)]
    [InlineData(.75f, 2)]
    [InlineData(1, 2)]
    [InlineData(float.MaxValue, 2)]
    public void FlipChoosesOneDiscreteOffsetWithoutMultiplyingItsValue(float weight, int selected)
    {
        var adapter = new PmxMorphAdapter(Model([Position("x", Vector3.UnitX), Position("y", Vector3.UnitY),
            Position("z", Vector3.UnitZ), Flip("flip", new PmxMorph.GroupOffset(0, 2), new(1, 3), new(2, 4))]));
        var pose = adapter.Evaluate(new Dictionary<int, float> { [3] = weight });
        if (selected < 0) Assert.Null(pose);
        else Assert.Equal(selected switch { 0 => Vector3.UnitX * 2, 1 => Vector3.UnitY * 3, _ => Vector3.UnitZ * 4 },
            Assert.IsType<MorphPose>(pose).VertexDeltas[0]);
        Assert.Equal(3, adapter.SupportedMorphIndices["flip"]);
    }

    [Fact]
    public void FlipAndGroupComposeWithSeparatePathsAndZeroSelectedOffsetsAreInactive()
    {
        var adapter = new PmxImpulseMorphAdapter(Model([Impulse("leaf", new PmxMorph.ImpulseOffset(0, true, Vector3.UnitX, Vector3.UnitY)),
            Flip("flip", new PmxMorph.GroupOffset(0, .25f), new(0, 2)), Group("group", new PmxMorph.GroupOffset(1, .75f), new(0, -1))]));
        var sample = adapter.Evaluate(new Dictionary<int, float> { [0] = 3, [2] = 1 });
        Assert.Equal(3, sample.Count);
        Assert.Equal(new Vector3(3, 0, 0), sample[0].TranslationVelocity);
        Assert.Equal(new Vector3(2, 0, 0), sample[1].TranslationVelocity);
        Assert.Equal(new Vector3(-1, 0, 0), sample[2].TranslationVelocity);
        Assert.All(sample, item => Assert.True(item.Local));
        Assert.Throws<NotSupportedException>(() => ((IList<PmxRigidBodyImpulse>)sample)[0] = default);
        Assert.Empty(adapter.Evaluate(new Dictionary<int, float>()));
        var zero = new PmxImpulseMorphAdapter(Model([Impulse("leaf", new PmxMorph.ImpulseOffset(0, false, Vector3.One, Vector3.One)), Flip("zero", new PmxMorph.GroupOffset(0, 0))]));
        Assert.Empty(zero.Evaluate(new Dictionary<int, float> { [1] = 1 }));
    }

    [Fact]
    public void ImpulseSnapshotsPreserveBodySpaceAndAngularComponentsWithoutMutation()
    {
        PmxMorph.ImpulseOffset[] offsets = [new(0, true, new(2, 4, 6), new(8, 10, 12)), new(0, false, new(-2, 3, 1), Vector3.UnitZ)];
        var adapter = new PmxImpulseMorphAdapter(Model([Impulse("impulse", offsets)]));
        offsets[0] = new(0, false, new(100), new(100));
        var sample = adapter.Evaluate(new Dictionary<int, float> { [0] = -.5f });
        Assert.Equal(new PmxRigidBodyImpulse(0, true, new(-1, -2, -3), new(-4, -5, -6)), sample[0]);
        Assert.Equal(new PmxRigidBodyImpulse(0, false, new(1, -1.5f, -.5f), new(0, 0, -.5f)), sample[1]);
        Assert.Equal(sample, adapter.Evaluate(new Dictionary<int, float> { [0] = -.5f }));
        Assert.Equal(0, adapter.SupportedMorphIndices["impulse"]);
    }

    [Fact]
    public void MixedFlipGroupCyclesAndInvalidUnselectedEdgesRejectBeforeSampling()
    {
        Assert.Throws<ArgumentException>(() => new PmxImpulseMorphAdapter(Model([Flip("self", new PmxMorph.GroupOffset(0, 0))])));
        Assert.Throws<ArgumentException>(() => new PmxImpulseMorphAdapter(Model([Flip("a", new PmxMorph.GroupOffset(1, 0)), Group("b", new PmxMorph.GroupOffset(0, 0))])));
        Assert.Throws<ArgumentException>(() => new PmxImpulseMorphAdapter(Model([Flip("bad", new PmxMorph.GroupOffset(-1, 1))])));
        Assert.Throws<ArgumentException>(() => new PmxImpulseMorphAdapter(Model([Flip("bad", new PmxMorph.GroupOffset(0, float.NaN))])));
    }

    [Fact]
    public void InvalidInactiveImpulseOffsetsAndOverflowRejectWithoutContaminatingLaterSamples()
    {
        Assert.Throws<ArgumentException>(() => new PmxImpulseMorphAdapter(Model([Impulse("bad", new PmxMorph.ImpulseOffset(-1, false, Vector3.One, Vector3.Zero))])));
        Assert.Throws<ArgumentException>(() => new PmxImpulseMorphAdapter(Model([Impulse("bad", new PmxMorph.ImpulseOffset(1, false, Vector3.One, Vector3.Zero))])));
        Assert.Throws<ArgumentException>(() => new PmxImpulseMorphAdapter(Model([Impulse("bad", new PmxMorph.ImpulseOffset(0, false, new(float.NaN), Vector3.Zero))])));
        Assert.Throws<ArgumentException>(() => new PmxImpulseMorphAdapter(Model([Impulse("bad", new PmxMorph.ImpulseOffset(0, false, Vector3.Zero, new(float.PositiveInfinity)))])));
        var adapter = new PmxImpulseMorphAdapter(Model([Impulse("big", new PmxMorph.ImpulseOffset(0, false, new(float.MaxValue), Vector3.Zero))]));
        Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [0] = 2 }));
        Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [0] = float.NaN }));
        Assert.Empty(adapter.Evaluate(new Dictionary<int, float>()));
    }

    static PmxMorph Position(string name, Vector3 value) => new()
    { Name = name, EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Position, PositionOffsets = [new(0, value)] };
    static PmxMorph Flip(string name, params PmxMorph.GroupOffset[] offsets) => new()
    { Name = name, EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Flip, FlipOffsets = offsets };
    static PmxMorph Group(string name, params PmxMorph.GroupOffset[] offsets) => new()
    { Name = name, EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Group, GroupOffsets = offsets };
    static PmxMorph Impulse(string name, params PmxMorph.ImpulseOffset[] offsets) => new()
    { Name = name, EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Impulse, ImpulseOffsets = offsets };

    static PmxModel Model(PmxMorph[] morphs)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        var m = PmxReader.Read(stream);
        return new()
        {
            Header = m.Header, Name = m.Name, EnglishName = m.EnglishName, Comment = m.Comment, EnglishComment = m.EnglishComment,
            Vertices = m.Vertices, Faces = m.Faces, Textures = m.Textures, Materials = m.Materials, Bones = m.Bones, Morphs = morphs,
            DisplayFrames = [], RigidBodies = m.RigidBodies.Take(1).ToArray(), Joints = [], SoftBodies = []
        };
    }
}
