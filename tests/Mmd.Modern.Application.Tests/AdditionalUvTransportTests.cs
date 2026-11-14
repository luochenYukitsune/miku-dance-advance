using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class AdditionalUvTransportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ControllerCopiesEvaluatedFractionalVectorsAndDisabledMorphsRestoreBaseline()
    {
        var model = Model(1, 4, 10); var rest = EditedMotionProjectTests.Rest(model);
        var sourceStream = rest.Surface.Snapshot.AdditionalUvs;
        using var controller = new CharacterAnimationController(model, Motion(), rest.Surface, rest.Outline!);
        var sample = controller.SampleAt(2.5).Surface.Snapshot;
        Assert.Equal(4, sample.AdditionalUvs!.ChannelCount);
        Assert.Equal(new Vector4(10.5f, 11, 11.5f, 12), sample.AdditionalUvs.Vertices[0].Uv1);
        Assert.Equal(new Vector4(13), sample.AdditionalUvs.Vertices[0].Uv4);
        Assert.Equal(controller.MorphChannels.AdditionalUvs[0].Uv1, sample.AdditionalUvs.Vertices[0].Uv1);
        Assert.Equal(rest.Surface.Snapshot.Vertices[0].Uv, sample.Vertices[0].Uv);
        var disabled = controller.SampleAt(2.5, enableMorphs: false).Surface.Snapshot;
        Assert.Equal(new Vector4(10), disabled.AdditionalUvs!.Vertices[0].Uv1);
        Assert.Equal(new Vector4(10.5f, 11, 11.5f, 12), sample.AdditionalUvs.Vertices[0].Uv1);
        Assert.Same(sourceStream, rest.Surface.Snapshot.AdditionalUvs); // Never mutate the source frame.
    }

    [Fact]
    public void CombineAndFastUpdateConcatenateUnequalStreamsWithoutTransformingUvOrFillingFromAnotherActor()
    {
        var first = Frame(1, 1, 10); var second = Frame(2, 4, 20); var absent = EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model());
        var snapshot = second.Surface.Snapshot;
        var placedSnapshot = new SceneSnapshot(snapshot.Vertices, snapshot.Indices, snapshot.Materials, snapshot.Textures,
            world: Matrix4x4.CreateScale(7) * Matrix4x4.CreateTranslation(20, 30, 40), additionalUvs: snapshot.AdditionalUvs);
        var placed = new OriginalFrameScene(new(placedSnapshot, second.Surface.Materials, second.Surface.Profile), second.Outline,
            second.OutlineOptions, second.Options, second.Materials);
        var baseline = ProjectSceneAssembler.Combine([first, absent, placed], Vector3.One, Vector3.UnitY);
        var uv = baseline.Surface.Snapshot.AdditionalUvs!;
        Assert.Equal(4, uv.ChannelCount); Assert.Equal(12, uv.Vertices.Length);
        Assert.Equal(new Vector4(10), uv.Vertices[0].Uv1);
        Assert.Equal(default, uv.Vertices[3]);
        Assert.Equal(new Vector4(20), uv.Vertices[6].Uv1);
        Assert.Equal(new Vector4(23), uv.Vertices[6].Uv4);
        Assert.Equal(new Vector4(21), uv.Vertices[9].Uv1);
        Assert.Equal(20, baseline.Surface.Snapshot.Vertices[6].Position.X);
        var next = Frame(2, 4, 40);
        var updated = ProjectSceneAssembler.WithVertices(baseline, [first, absent, next]);
        Assert.Equal(new Vector4(40), updated.Surface.Snapshot.AdditionalUvs!.Vertices[6].Uv1);
        Assert.Equal(new Vector4(20), baseline.Surface.Snapshot.AdditionalUvs!.Vertices[6].Uv1);
        var noStreams = ProjectSceneAssembler.WithVertices(ProjectSceneAssembler.Combine([absent], Vector3.One, Vector3.UnitY), [absent]);
        Assert.Null(noStreams.Surface.Snapshot.AdditionalUvs);
    }

    [Fact]
    public void SessionKeepsActorOffsetsThroughFastRecompositionHideAndRestoreAndPlacement()
    {
        var firstModel = Model(1, 1, 10); var secondModel = Model(2, 4, 20);
        var layout = new SceneCompositionEditor();
        var first = layout.Add("first", "first.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(firstModel));
        var second = layout.Add("second", "second.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(secondModel));
        layout.SetTransform(second.Id, new(100, 0, 0), Quaternion.Identity, 2);
        using var session = new SceneAnimationSession(layout.Objects,
            [new(first.Id, firstModel, Motion(visibleTrack: true), PhysicsEnabled: false),
             new(second.Id, secondModel, Motion(), PhysicsEnabled: false)], _ => throw new InvalidOperationException(), cancellationToken: Token);
        session.Seek(2, Token); var old = session.Frame.Surface.Snapshot;
        Assert.Equal(9, old.AdditionalUvs!.Vertices.Length);
        Assert.Equal(new Vector4(20.4f, 20.8f, 21.2f, 21.6f), old.AdditionalUvs.Vertices[3].Uv1);
        Assert.Equal(100, old.Vertices[3].Position.X);
        session.Seek(3, Token); // Stable topology uses WithVertices rather than Combine.
        Assert.Equal(new Vector4(20.6f, 21.2f, 21.8f, 22.4f), session.Frame.Surface.Snapshot.AdditionalUvs!.Vertices[3].Uv1);
        session.Seek(5, Token);
        Assert.Equal(6, session.Frame.Surface.Snapshot.AdditionalUvs!.Vertices.Length);
        Assert.Equal(new Vector4(21, 22, 23, 24), session.Frame.Surface.Snapshot.AdditionalUvs!.Vertices[0].Uv1);
        session.Seek(10, Token);
        Assert.Equal(9, session.Frame.Surface.Snapshot.AdditionalUvs!.Vertices.Length);
        Assert.Equal(new Vector4(22, 24, 26, 28), session.Frame.Surface.Snapshot.AdditionalUvs!.Vertices[3].Uv1);
        Assert.Equal(new Vector4(20.4f, 20.8f, 21.2f, 21.6f), old.AdditionalUvs.Vertices[3].Uv1);
    }

    [Fact]
    public void CompositionPlacementRetainsSourceStreamAndNoChannelModelsStayOnExistingPath()
    {
        var frame = Frame(1, 4, 30); var layout = new SceneCompositionEditor();
        var item = layout.Add("uv", "uv.pmx", ModelRole.Character, frame);
        layout.SetTransform(item.Id, new(5, 6, 7), Quaternion.Identity, 3);
        Assert.Equal(frame.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray(), layout.Frame.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray());
        var noUv = EditedMotionProjectTests.Model(); var rest = EditedMotionProjectTests.Rest(noUv);
        using var controller = new CharacterAnimationController(noUv,
            new() { BoneKeyframes = [new() { BoneName = "root", Frame = 10 }] }, rest.Surface, rest.Outline!);
        Assert.Null(controller.Sample(5).Surface.Snapshot.AdditionalUvs);
    }

    private static OriginalFrameScene Frame(int triangles, byte channels, float value)
    {
        var model = Model(triangles, channels, value); var rest = EditedMotionProjectTests.Rest(model);
        using var controller = new CharacterAnimationController(model, Motion(), rest.Surface, rest.Outline!);
        var sample = controller.Sample(0);
        return new(sample.Surface, sample.Outline, rest.OutlineOptions, rest.Options, rest.Materials);
    }

    private static VmdMotion Motion(bool visibleTrack = false) => new()
    {
        MorphKeyframes = [new() { MorphName = "uv", Weight = 0 }, new() { MorphName = "uv", Frame = 10, Weight = 1 }],
        IkKeyframes = visibleTrack ? [new() { Frame = 0, Visible = true }, new() { Frame = 5, Visible = false }, new() { Frame = 10, Visible = true }] : []
    };

    private static PmxModel Model(int triangles, byte channels, float value)
    {
        var original = EditedMotionProjectTests.Model(); var header = original.Header;
        return new()
        {
            Header = new() { Version = header.Version, HeaderDataSize = 8, TextEncoding = header.TextEncoding, AdditionalUvCount = channels,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "uv-transport", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Bones = original.Bones,
            Vertices = Enumerable.Range(0, triangles).SelectMany(triangle => original.Vertices.Select(vertex => new PmxVertex
            {
                Position = vertex.Position + new Vector3(triangle * 2, 0, 0), Normal = vertex.Normal, Uv = vertex.Uv,
                AdditionalUvs = Enumerable.Range(0, channels).Select(channel => new Vector4(value + triangle + channel)).ToArray(),
                WeightType = vertex.WeightType, BoneIndices = vertex.BoneIndices, BoneWeights = vertex.BoneWeights, EdgeScale = vertex.EdgeScale
            })).ToArray(),
            Faces = Enumerable.Range(0, triangles).Select(triangle => new PmxTriangle(triangle * 3, triangle * 3 + 1, triangle * 3 + 2)).ToArray(),
            Materials = [new() { Name = "surface", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1,
                Memo = "", FaceVertexCount = triangles * 3 }],
            Morphs = [new() { Name = "uv", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.AdditionalUv1,
                UvOffsets = [new(0, new(2, 4, 6, 8))] }],
            DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
    }
}
