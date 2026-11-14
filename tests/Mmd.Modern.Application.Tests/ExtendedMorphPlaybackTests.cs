using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class ExtendedMorphPlaybackTests
{
    [Fact]
    public void AdditionalUvOnlyMotionPublishesFractionalImmutableChannelsThroughSession()
    {
        var model = Model(); var rest = EditedMotionProjectTests.Rest(model);
        var layout = new SceneCompositionEditor(); var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, rest);
        var motion = new VmdMotion { MorphKeyframes = [new() { MorphName = "uv", Weight = 0 }, new() { MorphName = "uv", Frame = 10, Weight = 1 }] };
        using var session = new SceneAnimationSession(layout.Objects, [new(actor.Id, model, motion, PhysicsEnabled: false)], _ => throw new InvalidOperationException());
        session.Advance(2.5 / 30, TestContext.Current.CancellationToken);
        var channels = session.GetMorphChannels(actor.Id);
        Assert.Equal(new Vector4(1.5f, 3, 4.5f, 6), channels.AdditionalUvs[0].Uv1);
        session.SetActorOptions(actor.Id, true, false, false, TestContext.Current.CancellationToken);
        Assert.Equal(new Vector4(1, 2, 3, 4), session.GetMorphChannels(actor.Id).AdditionalUvs[0].Uv1);
        Assert.Equal(new Vector4(1.5f, 3, 4.5f, 6), channels.AdditionalUvs[0].Uv1);
    }

    [Fact]
    public void TextureFactorsFollowMotionAndResetWithoutAccumulation()
    {
        var model = Model(); var rest = EditedMotionProjectTests.Rest(model);
        var motion = new VmdMotion { MorphKeyframes = [new() { MorphName = "texture", Weight = .5f }] };
        using var controller = new CharacterAnimationController(model, motion, rest.Surface, rest.Outline!);
        controller.Sample(0);
        Assert.Equal(new Vector4(.25f), controller.MorphChannels.TextureFactors[0].Diffuse.Add);
        controller.Sample(0, enableMorphs: false);
        Assert.Equal(PmxMaterialTextureFactors.Identity, controller.MorphChannels.TextureFactors[0]);
        controller.Sample(0);
        Assert.Equal(new Vector4(.25f), controller.MorphChannels.TextureFactors[0].Diffuse.Add);
    }

    static PmxModel Model()
    {
        var m = EditedMotionProjectTests.Model(); var h = m.Header;
        return new() { Header = new() { Version = h.Version, HeaderDataSize = 8, TextEncoding = h.TextEncoding,
            AdditionalUvCount = 1, VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = m.Name, EnglishName = "", Comment = "", EnglishComment = "", Bones = m.Bones, Faces = m.Faces, Textures = m.Textures,
            Materials = m.Materials, DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
            Vertices = m.Vertices.Select(v => new PmxVertex { Position = v.Position, Normal = v.Normal, Uv = v.Uv,
                AdditionalUvs = [new(1, 2, 3, 4)], WeightType = v.WeightType, BoneIndices = v.BoneIndices, BoneWeights = v.BoneWeights, EdgeScale = v.EdgeScale }).ToArray(),
            Morphs = [new() { Name = "uv", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.AdditionalUv1, UvOffsets = [new(0, new(2, 4, 6, 8))] },
                new() { Name = "texture", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Material,
                    MaterialOffsets = [new(-1, PmxMaterialMorphOperation.Add, Vector4.Zero, Vector3.Zero, 0, Vector3.Zero, Vector4.Zero, 0,
                        new(.5f), Vector4.Zero, Vector4.Zero)] }] };
    }
}
