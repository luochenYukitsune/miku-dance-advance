using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmxExtendedMorphTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void UvOnlyAnimationRetainsOutlineGeometryButPublishesCurrentUvValues(int channel)
    {
        var morph=channel<0?new PmxMorph {Name="baseUv",EnglishName="",ControlPanel=0,Type=PmxMorphType.Uv,
            UvOffsets=[new(0,new(.3f,.2f,.1f,.4f))]}:Uv(channel,new PmxMorph.UvOffset(0,new(.3f,.2f,.1f,.4f)));
        var model=Model([morph]);var rest=SharedPhysicsFixture.Rest(model);
        // Keep the skeleton fixed for the UV-only samples, then deliberately move it below.
        var motion=new VmdMotion {BoneKeyframes=[new() {BoneName=model.Bones[0].Name},
                new() {BoneName=model.Bones[0].Name,Frame=2},
                new() {BoneName=model.Bones[0].Name,Frame=4,Translation=new(.5f,0,0)}],
            MorphKeyframes=[new() {MorphName=morph.Name},new() {MorphName=morph.Name,Frame=2,Weight=1}]};
        using var uvController=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        var first=uvController.SampleAt(0);var oldVertices=first.Surface.Snapshot.Vertices.ToArray();
        var oldOutline=first.Outline.Vertices.ToArray();var next=uvController.SampleAt(2);
        Assert.Same(first.Outline.Vertices,next.Outline.Vertices);
        using var fresh=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        var expected=fresh.SampleAt(2);
        Assert.Equal(expected.Surface.Snapshot.Vertices.ToArray(),next.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(expected.Outline.Vertices.ToArray(),next.Outline.Vertices.ToArray());
        if(channel<0)Assert.NotEqual(first.Surface.Snapshot.Vertices[0].Uv,next.Surface.Snapshot.Vertices[0].Uv);
        else Assert.NotEqual(first.Surface.Snapshot.AdditionalUvs!.Vertices[0],next.Surface.Snapshot.AdditionalUvs!.Vertices[0]);
        Assert.Equal(oldVertices,first.Surface.Snapshot.Vertices.ToArray());Assert.Equal(oldOutline,first.Outline.Vertices.ToArray());
        var disabled=uvController.SampleAt(2,true,false);
        Assert.Same(first.Outline.Vertices,disabled.Outline.Vertices);
        var moved=uvController.SampleAt(4);
        Assert.NotSame(first.Outline.Vertices,moved.Outline.Vertices);
        Assert.Equal(first.Outline.Vertices.ToArray(),oldOutline);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ChangingPositionWeightsRetainUnchangedAdditionalUvPreparation(int channels)
    {
        PmxMorph moving=new() {Name="moving",EnglishName="",ControlPanel=0,Type=PmxMorphType.Position,
            PositionOffsets=[new(0,new(.3f,.2f,.1f))]};
        PmxMorph[] morphs=channels==0?[moving]:[moving,Group(new(2,.5f),new(2,.25f)),Uv(3,new PmxMorph.UvOffset(0,Vector4.One))];
        var model=Model(morphs,channels); var rest=SharedPhysicsFixture.Rest(model);
        var motion=new VmdMotion {BoneKeyframes=[new() {BoneName=model.Bones[0].Name}],
            MorphKeyframes=new[] {new VmdMorphKeyframe {MorphName="moving"},
                new VmdMorphKeyframe {MorphName="moving",Frame=16,Weight=.8f}}
                .Concat(channels==0?[]:[new VmdMorphKeyframe {MorphName="group",Weight=.5f}]).ToArray()};
        using var controller=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        var first=controller.SampleAt(0); var firstVertices=first.Surface.Snapshot.Vertices.ToArray();
        var additional=controller.MorphChannels.AdditionalUvs; var additionalCopy=additional.ToArray();
        var next=controller.SampleAt(.25);
        Assert.Same(additional,controller.MorphChannels.AdditionalUvs);
        Assert.Same(first.Surface.Snapshot.AdditionalUvs,next.Surface.Snapshot.AdditionalUvs);
        Assert.NotEqual(firstVertices[0].Position,next.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(additionalCopy,additional); Assert.Equal(firstVertices,first.Surface.Snapshot.Vertices.ToArray());
        using var fresh=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        var expected=fresh.SampleAt(.25);
        Assert.Equal(expected.Surface.Snapshot.Vertices.ToArray(),next.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(fresh.MorphChannels.AdditionalUvs,controller.MorphChannels.AdditionalUvs);
    }

    [Fact]
    public void ChangedAdditionalUvContributionsReplaceTheCachedChannelWithoutChangingOldSnapshots()
    {
        var model=Model([Group(new(1,.5f),new(1,.25f)),Uv(0,new PmxMorph.UvOffset(0,new(1,2,3,4)))]);
        var rest=SharedPhysicsFixture.Rest(model);
        var motion=new VmdMotion {BoneKeyframes=[new() {BoneName=model.Bones[0].Name}],
            MorphKeyframes=[new() {MorphName="group",Weight=.5f},new() {MorphName="group",Frame=4,Weight=1}]};
        using var controller=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        controller.SampleAt(0); var first=controller.MorphChannels.AdditionalUvs; var copy=first.ToArray();
        controller.SampleAt(2); Assert.NotSame(first,controller.MorphChannels.AdditionalUvs);
        using var fresh=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        fresh.SampleAt(2);
        Assert.Equal(fresh.MorphChannels.AdditionalUvs,controller.MorphChannels.AdditionalUvs);
        Assert.Equal(copy,first);
    }

    [Fact]
    public void CachedAdditionalUvPreparationStillRejectsOverflowAndRetainsItsLastValidOutput()
    {
        var model=Model([Uv(0,new PmxMorph.UvOffset(0,new(float.MaxValue)))]);
        var rest=SharedPhysicsFixture.Rest(model);
        var motion=new VmdMotion {BoneKeyframes=[new() {BoneName=model.Bones[0].Name}],
            MorphKeyframes=[new() {MorphName="uv0"},new() {MorphName="uv0",Frame=2,Weight=2}]};
        using var controller=new CharacterAnimationController(model,motion,rest.Surface,rest.Outline!);
        controller.SampleAt(0); var first=controller.MorphChannels.AdditionalUvs; var copy=first.ToArray();
        Assert.Throws<ArgumentException>(()=>controller.SampleAt(2));
        controller.SampleAt(0);
        Assert.Same(first,controller.MorphChannels.AdditionalUvs); Assert.Equal(copy,first);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AdditionalChannelsPreserveAllFourComponentsAndBaselines(int channel)
    {
        var model = Model([Uv(channel, new PmxMorph.UvOffset(0, new(2, 4, 6, 8)))]);
        var adapter = new PmxExtendedMorphAdapter(model);
        var before = adapter.Evaluate(new Dictionary<int, float>());
        var result = adapter.Evaluate(new Dictionary<int, float> { [0] = -.5f });
        for (int c = 0; c < 4; c++)
            Assert.Equal(model.Vertices[0].AdditionalUvs[c] + (c == channel ? new Vector4(-1, -2, -3, -4) : Vector4.Zero), result[0][c]);
        Assert.Equal(model.Vertices[1].AdditionalUvs[channel], result[1][channel]);
        Assert.Equal(model.Vertices[0].AdditionalUvs[channel], before[0][channel]);
        Assert.Equal(result[0], adapter.Evaluate(new Dictionary<int, float> { [0] = -.5f })[0]);
    }

    [Fact]
    public void GroupsRetainSharedPathsAndCallerMutationDoesNotChangeSnapshots()
    {
        PmxMorph.UvOffset[] offsets = [new(0, new(1, 2, 3, 4))];
        var adapter = new PmxExtendedMorphAdapter(Model([Group(new(1, .5f), new(1, .25f)), Uv(3, offsets)]));
        offsets[0] = new(0, new(100));
        var baseline = adapter.Evaluate(new Dictionary<int, float>());
        var result = adapter.Evaluate(new Dictionary<int, float> { [0] = 2 });
        Assert.Equal(baseline[0][3] + new Vector4(1.5f, 3, 4.5f, 6), result[0][3]);
        Assert.Throws<NotSupportedException>(() => ((IList<PmxAdditionalUvVertex>)result)[0] = default);
    }

    [Fact]
    public void UndeclaredChannelsAndInvalidOffsetsRejectBeforeEvaluation()
    {
        Assert.Throws<ArgumentException>(() => new PmxExtendedMorphAdapter(Model([Uv(3, new PmxMorph.UvOffset(0, Vector4.One))], 2)));
        Assert.Throws<ArgumentException>(() => new PmxExtendedMorphAdapter(Model([Uv(0, new PmxMorph.UvOffset(-1, Vector4.One))])));
        Assert.Throws<ArgumentException>(() => new PmxExtendedMorphAdapter(Model([Uv(0, new PmxMorph.UvOffset(0, new(float.NaN)))])));
        var adapter = new PmxExtendedMorphAdapter(Model([Uv(0, new PmxMorph.UvOffset(0, new(float.MaxValue)))]));
        Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [0] = float.NaN }));
        Assert.Throws<ArgumentException>(() => adapter.Evaluate(new Dictionary<int, float> { [0] = 2 }));
    }

    [Fact]
    public void MissingChannelsAreZeroAndUnsupportedMorphsHaveExplicitDiagnostics()
    {
        var model = Model([new() { Name = "flip", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Flip },
            new() { Name = "impulse", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Impulse }], 0);
        var adapter = new PmxExtendedMorphAdapter(model);
        Assert.All(adapter.Evaluate(new Dictionary<int, float> { [0] = 1, [1] = 1 }), item => Assert.Equal(default, item));
        Assert.False(adapter.SupportedMorphIndices.ContainsKey("flip"));
        Assert.Contains(adapter.Diagnostics, d => d.Contains("Impulse", StringComparison.Ordinal));
    }

    [Fact]
    public void TextureFactorsKeepIndependentMultiplyAndAddForAllThreeSamplers()
    {
        var add = Offset(-1, PmxMaterialMorphOperation.Add, new(2, 4, 6, 8), new(1), new(-2));
        var mul = Offset(0, PmxMaterialMorphOperation.Multiply, new(.5f), new(2), new(.25f));
        var model = Model([Material(add), Material(mul)]);
        var adapter = new PmxMaterialMorphAdapter(model);
        var result = adapter.EvaluateTextureFactors(new Dictionary<int, float> { [0] = .5f, [1] = .5f });
        Assert.Equal(new Vector4(.75f), result[0].Diffuse.Multiply);
        Assert.Equal(new Vector4(1, 2, 3, 4), result[0].Diffuse.Add);
        Assert.Equal(new Vector4(1.5f), result[0].Sphere.Multiply);
        Assert.Equal(new Vector4(.5f), result[0].Sphere.Add);
        Assert.Equal(new Vector4(.625f), result[0].Toon.Multiply);
        Assert.Equal(new Vector4(-1), result[0].Toon.Add);
        Assert.Equal(new Vector4(2.5f, 3.5f, 4.5f, 5.5f), result[0].Diffuse.Apply(new(2)));
        Assert.Contains(adapter.Diagnostics, d => d.Contains("selected renderer path", StringComparison.Ordinal));
        var empty = adapter.EvaluateTextureFactors(new Dictionary<int, float>());
        Assert.Equal(Vector4.One, empty[0].Diffuse.Multiply);
        Assert.Equal(Vector4.Zero, empty[0].Diffuse.Add);
        Assert.Throws<NotSupportedException>(() => ((IList<PmxMaterialTextureFactors>)result)[0] = default);
    }

    [Fact]
    public void MaterialSharedGroupPathsAreAppliedSeparatelyAndNegativeWeightsRemainUnclamped()
    {
        var adapter = new PmxMaterialMorphAdapter(Model([Group(new(1, .5f), new(1, .5f)),
            Material(Offset(-1, PmxMaterialMorphOperation.Multiply, new(.5f), Vector4.One, Vector4.One))]));
        var result = adapter.EvaluateTextureFactors(new Dictionary<int, float> { [0] = -2 });
        Assert.Equal(new Vector4(2.25f), result[0].Diffuse.Multiply);
        Assert.Equal(Vector4.Zero, result[0].Diffuse.Add);
        Assert.Throws<ArgumentException>(() => adapter.EvaluateTextureFactors(new Dictionary<int, float> { [0] = float.PositiveInfinity }));
    }

    [Fact]
    public void TextureFactorOverflowRejectsWithoutCorruptingFutureSamples()
    {
        var adapter = new PmxMaterialMorphAdapter(Model([Material(Offset(-1, PmxMaterialMorphOperation.Add,
            new(float.MaxValue), Vector4.Zero, Vector4.Zero))]));
        Assert.Throws<ArgumentException>(() => adapter.EvaluateTextureFactors(new Dictionary<int, float> { [0] = 2 }));
        Assert.Equal(Vector4.Zero, adapter.EvaluateTextureFactors(new Dictionary<int, float>())[0].Diffuse.Add);
    }

    static PmxMorph Uv(int channel, params PmxMorph.UvOffset[] values) => new()
    { Name = "uv" + channel, EnglishName = "", ControlPanel = 0, Type = (PmxMorphType)((int)PmxMorphType.AdditionalUv1 + channel), UvOffsets = values };
    static PmxMorph Group(params PmxMorph.GroupOffset[] offsets) => new()
    { Name = "group", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Group, GroupOffsets = offsets };
    static PmxMorph Material(params PmxMorph.MaterialOffset[] offsets) => new()
    { Name = "material", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Material, MaterialOffsets = offsets };
    static PmxMorph.MaterialOffset Offset(int index, PmxMaterialMorphOperation operation, Vector4 diffuse, Vector4 sphere, Vector4 toon)
        => new(index, operation, operation == PmxMaterialMorphOperation.Multiply ? Vector4.One : Vector4.Zero,
            operation == PmxMaterialMorphOperation.Multiply ? Vector3.One : Vector3.Zero,
            operation == PmxMaterialMorphOperation.Multiply ? 1 : 0,
            operation == PmxMaterialMorphOperation.Multiply ? Vector3.One : Vector3.Zero,
            operation == PmxMaterialMorphOperation.Multiply ? Vector4.One : Vector4.Zero,
            operation == PmxMaterialMorphOperation.Multiply ? 1 : 0, diffuse, sphere, toon);

    static PmxModel Model(PmxMorph[] morphs, int channels = 4)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        var m = PmxReader.Read(stream); var h = m.Header;
        return new()
        {
            Header = new() { Version = h.Version, HeaderDataSize = h.HeaderDataSize, TextEncoding = h.TextEncoding,
                AdditionalUvCount = channels, VertexIndexSize = h.VertexIndexSize, TextureIndexSize = h.TextureIndexSize,
                MaterialIndexSize = h.MaterialIndexSize, BoneIndexSize = h.BoneIndexSize, MorphIndexSize = h.MorphIndexSize,
                RigidBodyIndexSize = h.RigidBodyIndexSize },
            Name = m.Name, EnglishName = m.EnglishName, Comment = m.Comment, EnglishComment = m.EnglishComment,
            Vertices = m.Vertices.Select(v => new PmxVertex { Position = v.Position, Normal = v.Normal, Uv = v.Uv,
                AdditionalUvs = v.AdditionalUvs.Take(channels).ToArray(), WeightType = v.WeightType,
                BoneIndices = v.BoneIndices, BoneWeights = v.BoneWeights, EdgeScale = v.EdgeScale }).ToArray(),
            Faces = m.Faces, Textures = m.Textures, Materials = m.Materials, Bones = m.Bones, Morphs = morphs,
            DisplayFrames = [], RigidBodies = m.RigidBodies, Joints = m.Joints, SoftBodies = m.SoftBodies
        };
    }
}
