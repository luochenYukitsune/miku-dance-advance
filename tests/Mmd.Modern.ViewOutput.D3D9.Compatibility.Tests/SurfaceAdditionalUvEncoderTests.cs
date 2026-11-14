using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed partial class SurfaceFrameEncoderTests
{
    [Fact]
    public void AdditionalUvVersionOnlyAppendsToTheLegacyBodyAndBindsTheWholePacket()
    {
        using var legacyProject = UvProject([null], out var legacyEncoder);
        using var uvProject = UvProject([UvStream(1, 10)], out var uvEncoder);
        var legacy = legacyEncoder.Serialize(Capture(legacyProject, legacyEncoder), Request, Vector4.One);
        var uvFrame = Capture(uvProject, uvEncoder);
        var extended = uvEncoder.Serialize(uvFrame, Request, Vector4.One);
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(legacy.Frame.AsSpan(8)));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(extended.Frame.AsSpan(8)));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(extended.Visual.AsSpan(8)));
        var legacyBody = legacy.Frame.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(legacyBody.AsSpan(8), 2);
        Assert.Equal(legacyBody, extended.Frame.AsSpan(0, legacy.Frame.Length).ToArray());
        Assert.Equal(legacy.Visual.AsSpan(0, 20).ToArray(), extended.Visual.AsSpan(0, 20).ToArray());
        Assert.Equal(legacy.Visual.AsSpan(52).ToArray(), extended.Visual.AsSpan(52).ToArray());
        Assert.Equal(SHA256.HashData(extended.Frame), extended.Visual.AsSpan(20, 32).ToArray());
        Assert.Equal(legacy.Frame.Length + 8 + 3 * 64, extended.Frame.Length);
        Assert.Equal(0, uvEncoder.Encode(uvFrame, Request, Vector4.One).SourceFrame);
    }

    [Fact]
    public void AdditionalUvCopiesAllFourFullVectorsIncludingUndeclaredHigherChannels()
    {
        var stream = UvStream(1, 10);
        using var project = UvProject([stream], out var encoder);
        var packets = encoder.Serialize(Capture(project, encoder), Request, Vector4.One);
        var tail = ReadUvTail(packets.Frame);
        Assert.Equal(1, tail.ChannelCount);
        Assert.Equal(stream.Vertices.ToArray(), tail.Vertices);
        Assert.NotEqual(Vector4.Zero, tail.Vertices[0].Uv4);
        Assert.NotEqual(tail.Vertices[0].Uv1.Z, tail.Vertices[0].Uv1.W);
    }

    [Fact]
    public void AdditionalUvMixedStreamsFollowComposedVertexOrderAndAbsentSourcesGetZeroRows()
    {
        var first = UvStream(1, 10); var third = UvStream(4, 30);
        using var project = UvProject([first, null, third], out var encoder);
        var frame = Capture(project, encoder);
        var tail = ReadUvTail(encoder.Serialize(frame, Request, Vector4.One).Frame);
        Assert.Equal(4, tail.ChannelCount);
        Assert.Equal(first.Vertices.ToArray().Concat(new SceneAdditionalUvVertex[3]).Concat(third.Vertices.ToArray()), tail.Vertices);
        Assert.Equal(9, tail.Vertices.Length);
        Assert.NotEqual(Matrix4x4.Identity, frame.SceneInputs[project.Objects[0].Id].SourceGeometry!.World);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AdditionalUvRejectsWrongVertexOrderAbsentSourceValuesOrChannelDeclaration(int corruption)
    {
        using var project = UvProject([UvStream(1, 10), null], out var encoder);
        var frame = Capture(project, encoder);
        var values = frame.Scene.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray();
        int channels = 1;
        if (corruption == 0) (values[0], values[1]) = (values[1], values[0]);
        if (corruption == 1) values[3] = values[0];
        if (corruption == 2) channels = 4;
        var bad = WithCombinedUvs(frame, corruption == 3 ? null : new(channels, values));
        Assert.Throws<ArgumentException>(() => encoder.Encode(bad, Request, Vector4.One));
    }

    [Fact]
    public void AdditionalUvRejectsAComposedStreamWhenAllSourceStreamsAreAbsent()
    {
        using var project = UvProject([null], out var encoder);
        var frame = Capture(project, encoder);
        Assert.Throws<ArgumentException>(() => encoder.Encode(WithCombinedUvs(frame, UvStream(1, 0)), Request, Vector4.One));
    }

    [Fact]
    public void AdditionalUvMorphValuesAreTheFrozenEvaluatedSampleAndEncodingDoesNotAdvanceIt()
    {
        var model = UvModel(); var rest = UvRest(model);
        var layout = new SceneCompositionEditor(); var actor = layout.Add("uv-actor", "uv-actor.pmx", ModelRole.Unspecified, rest);
        var motion = new VmdMotion { MorphKeyframes = [new() { MorphName = "uv", Weight = 0 }, new() { MorphName = "uv", Frame = 10, Weight = 1 }] };
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, motion, PhysicsEnabled: false)], Provider());
        var encoder = new D3D9SurfaceFrameEncoder([new(actor.Id, FirstId, actor.SourcePath, actor.Name, ["root"])]);
        project.Seek(2, TestContext.Current.CancellationToken);
        var before = project.Frame; double clock = project.CurrentFrame;
        var frame = Capture(project, encoder);
        var frozen = frame.Scene.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray();
        Assert.Equal(new Vector4(10.4f, 10.8f, 11.2f, 11.6f), frozen[0].Uv1);
        Assert.Equal(frozen, ReadUvTail(encoder.Serialize(frame, Request, Vector4.One).Frame).Vertices);
        _ = encoder.Encode(frame, Request, Vector4.One);
        Assert.Same(before, project.Frame); Assert.Equal(clock, project.CurrentFrame);
        project.Seek(8, TestContext.Current.CancellationToken);
        Assert.Equal(frozen, ReadUvTail(encoder.Serialize(frame, Request, Vector4.One).Frame).Vertices);
    }

    [Fact]
    public void AdditionalUvHiddenActorDoesNotKeepAChannelAndReturnsWithItsOriginalId()
    {
        var model = UvModel(); var layout = new SceneCompositionEditor();
        var first = layout.Add("uv-actor", "uv-actor.pmx", ModelRole.Unspecified, UvRest(model));
        var second = layout.Add("plain", "plain.pmx", ModelRole.Unspecified, Triangle("plain", Vector3.UnitZ, Matrix4x4.Identity));
        var motion = new VmdMotion { IkKeyframes = [new() { Frame = 0, Visible = true }, new() { Frame = 1, Visible = false }, new() { Frame = 2, Visible = true }] };
        using var project = new SceneProjectSession(layout, new(), [new(first.Id, model, motion, PhysicsEnabled: false)], Provider());
        var encoder = new D3D9SurfaceFrameEncoder([new(first.Id, FirstId, first.SourcePath, first.Name, ["root"]), new(second.Id, SecondId, second.SourcePath, second.Name)]);
        project.Seek(1, TestContext.Current.CancellationToken);
        var hidden = encoder.Serialize(Capture(project, encoder), Request, Vector4.One);
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(hidden.Frame.AsSpan(8)));
        Assert.Equal(new[] { FirstId, SecondId }, ParseFrame(hidden.Frame).ObjectIds);
        Assert.Equal(new[] { SecondId }, ParseFrame(hidden.Frame).MaterialIds);
        project.Seek(2, TestContext.Current.CancellationToken);
        var shown = encoder.Serialize(Capture(project, encoder), Request, Vector4.One);
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(shown.Frame.AsSpan(8)));
        Assert.Equal(new[] { FirstId, SecondId }, ParseFrame(shown.Frame).MaterialIds);
        Assert.Equal(1, ReadUvTail(shown.Frame).ChannelCount);
        Assert.Equal(new SceneAdditionalUvVertex[3], ReadUvTail(shown.Frame).Vertices.AsSpan(3).ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SubTextureUsesVis3AndPreservesItsAlreadyResolvedAdditiveFlag(bool diffuse, bool additive)
    {
        using var project = UvProject([UvStream(1, 10)], out var encoder, diffuse);
        var frame = WithSubTexture(Capture(project, encoder), 0, diffuse, additive, 1);
        var packets = encoder.Serialize(frame, Request, Vector4.One);
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(packets.Frame.AsSpan(8)));
        Assert.Equal(3U, BinaryPrimitives.ReadUInt32LittleEndian(packets.Visual.AsSpan(8)));
        Assert.Equal((uint)(1 | (additive ? 2 : 0)), BinaryPrimitives.ReadUInt32LittleEndian(packets.Visual.AsSpan(140)));
        Assert.Equal(128 + 132 + 3 * 24, packets.Visual.Length);
        Assert.Equal(SHA256.HashData(packets.Frame), packets.Visual.AsSpan(20, 32).ToArray());
        Assert.Equal(0, encoder.Encode(frame, Request, Vector4.One).SourceFrame);
    }

    [Theory]
    [InlineData(false, 0.5f, 1U)]
    [InlineData(true, 0.5f, 0U)]
    [InlineData(true, 1f, 1U)]
    public void SubTextureToonAlphaMarksOnlyTheDiffuseTwoMapTechniqueTransparent(bool diffuse, float toonAlpha, uint expectedOpaque)
    {
        using var project = UvProject([UvStream(1, 10)], out var encoder, diffuse);
        var frame = WithSubTexture(Capture(project, encoder), 0, diffuse, false, toonAlpha);
        var packets = encoder.Serialize(frame, Request, Vector4.One);
        Assert.Equal(expectedOpaque, BinaryPrimitives.ReadUInt32LittleEndian(packets.Visual.AsSpan(140)));
        Assert.Equal(toonAlpha, BinaryPrimitives.ReadSingleLittleEndian(packets.Visual.AsSpan(188)));
    }

    [Fact]
    public void SubTextureDoesNotTreatAnotherActorsUvStreamAsItsOwnAuthoredUv1()
    {
        using var project = UvProject([null, UvStream(1, 10)], out var encoder);
        var frame = WithSubTexture(Capture(project, encoder), 0, true, false, 1);
        Assert.Throws<NotSupportedException>(() => encoder.Encode(frame, Request, Vector4.One));
    }

    [Fact]
    public void NonSubTextureBindingDoesNotSetTheAdditiveFlagOrUpgradeVis2()
    {
        using var project = UvProject([UvStream(1, 10)], out var encoder);
        var frame = Capture(project, encoder);
        var bindings = frame.Scene.Surface.Materials.Select(value => value with { SubTextureAdditive = true }).ToArray();
        var scene = new OriginalFrameScene(new(frame.Scene.Surface.Snapshot, bindings, frame.Scene.Surface.Profile),
            frame.Scene.Outline, frame.Scene.OutlineOptions, frame.Scene.Options, frame.Scene.Materials, frame.Scene.Lighting, frame.Scene.EditorOverlay);
        var packets = encoder.Serialize(frame with { Scene = scene }, Request, Vector4.One);
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(packets.Visual.AsSpan(8)));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(packets.Visual.AsSpan(140)));
    }

    private static OfflineFrame WithSubTexture(OfflineFrame frame, int materialIndex, bool diffuse, bool additive, float toonAlpha)
    {
        var snapshot = frame.Scene.Surface.Snapshot;
        Assert.Equal(diffuse, snapshot.Materials[materialIndex].DiffuseTexture >= 0);
        var bindings = frame.Scene.Surface.Materials.ToArray();
        bindings[materialIndex] = bindings[materialIndex] with
        {
            SphereMode = OriginalSphereMode.SubTexture, SphereTexture = materialIndex, SubTextureAdditive = additive,
            TextureFactors = OriginalMaterialTextureFactors.Identity with { Toon = new(new(1, 1, 1, toonAlpha), Vector4.Zero) }
        };
        return frame with { Scene = new(new(snapshot, bindings, frame.Scene.Surface.Profile),
            frame.Scene.Outline, frame.Scene.OutlineOptions, frame.Scene.Options, frame.Scene.Materials, frame.Scene.Lighting, frame.Scene.EditorOverlay) };
    }

    private static SceneProjectSession UvProject(SceneAdditionalUvs?[] streams, out D3D9SurfaceFrameEncoder encoder, bool diffuse = true)
    {
        var layout = new SceneCompositionEditor();
        foreach (var (stream, index) in streams.Select((stream, index) => (stream, index)))
        {
            var source = Triangle("uv-" + index, new(2, 3, 4), Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(index * 4, 0, 0));
            var snapshot = source.Surface.Snapshot.WithAdditionalUvs(stream);
            if (!diffuse) snapshot = snapshot.WithMaterials(snapshot.Materials.Select(material => material with { DiffuseTexture = -1 }));
            layout.Add("uv-" + index, "uv-" + index + ".pmx", ModelRole.Unspecified,
                new(new(snapshot, source.Surface.Materials, source.Surface.Profile), source.Outline, source.OutlineOptions, source.Options, source.Materials));
        }
        encoder = new(layout.Objects.Select((item, index) => new D3D9SurfaceObjectBinding(item.Id, (ulong)index + 1, item.SourcePath, item.Name)));
        return new(layout, new(), [], Provider());
    }

    private static SceneAdditionalUvs UvStream(int channels, float seed) => new(channels, Enumerable.Range(0, 3)
        .Select(i => new SceneAdditionalUvVertex(new(seed + i, seed + i + .25f, seed + i + .5f, seed + i + .75f),
            new(seed + i + 10, seed + i + 11, seed + i + 12, seed + i + 13),
            new(seed + i + 20, seed + i + 21, seed + i + 22, seed + i + 23),
            new(seed + i + 30, seed + i + 31, seed + i + 32, seed + i + 33))).ToArray());

    private static OfflineFrame WithCombinedUvs(OfflineFrame frame, SceneAdditionalUvs? stream) => frame with
    {
        Scene = new(new(frame.Scene.Surface.Snapshot.WithAdditionalUvs(stream), frame.Scene.Surface.Materials, frame.Scene.Surface.Profile),
            frame.Scene.Outline, frame.Scene.OutlineOptions, frame.Scene.Options, frame.Scene.Materials, frame.Scene.Lighting, frame.Scene.EditorOverlay)
    };

    private static (int ChannelCount, SceneAdditionalUvVertex[] Vertices) ReadUvTail(byte[] packet)
    {
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24)));
        int start = packet.Length - 8 - count * 64;
        Assert.True(start >= 208);
        int channels = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(start)));
        Assert.Equal((uint)count, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(start + 4)));
        Vector4 Read(int offset) => new(BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset)),
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 4)), BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 8)),
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 12)));
        return (channels, Enumerable.Range(0, count).Select(i =>
            new SceneAdditionalUvVertex(Read(start + 8 + i * 64), Read(start + 24 + i * 64), Read(start + 40 + i * 64), Read(start + 56 + i * 64))).ToArray());
    }

    private static OriginalFrameScene UvRest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 1));

    private static PmxModel UvModel()
    {
        var model = Model();
        return new()
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 1,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = model.Name, EnglishName = "", Comment = "", EnglishComment = "", Textures = model.Textures,
            Vertices = model.Vertices.Select(vertex => new PmxVertex { Position = vertex.Position, Normal = vertex.Normal, Uv = vertex.Uv,
                AdditionalUvs = [new(10)], WeightType = vertex.WeightType, BoneIndices = vertex.BoneIndices, BoneWeights = vertex.BoneWeights, EdgeScale = vertex.EdgeScale }).ToArray(),
            Faces = model.Faces, Materials = model.Materials, Bones = model.Bones,
            Morphs = [new() { Name = "uv", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.AdditionalUv1, UvOffsets = [new(0, new(2, 4, 6, 8))] }],
            DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
    }
}
