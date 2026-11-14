using System.Numerics;
using System.Reflection;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class PmmPlaybackValidationTests
{
    [Fact]
    public void ShadowTimelineExtendsPlaybackAndSavedStateDoesNotInventCoverageMode()
    {
        var shadow = new PmmSelfShadow { Enabled = false, Distance = .03f,
            InitialKeyframe = new() { Mode = 1, Distance = .01f },
            Keyframes = [new() { Frame = 35, Mode = 2, Distance = .02f }] };
        using var playback = CreatePlayback(Document([Model()], selfShadow: shadow));
        Assert.Equal(35u, playback.DurationFrames);
        Assert.Equal(new PmmSelfShadowSavedState(false, .03f), playback.SavedSelfShadow);
        Assert.Null(playback.SelfShadowPose);
        playback.Seek(35);
        Assert.Equal(new PmmSelfShadowPose(2, .02f), playback.SelfShadowPose);
        playback.Seek(10);
        Assert.Equal(new PmmSelfShadowPose(1, .01f), playback.SelfShadowPose);
    }

    [Theory]
    [InlineData(99, 30, "parent model")]
    [InlineData(-1, 180, "FOV")]
    public void InvalidFutureCameraIsRejectedBeforeTheSavedProjectCanCommit(int parent, int fov, string message)
    {
        var document = Document([Model()], new()
        {
            RecordIndex = 1, ObjectIndex = 1, Frame = 10, Distance = -10, Fov = fov,
            Perspective = true, ParentModelIndex = parent, Interpolation = CameraCurves()
        });

        var error = Assert.Throws<InvalidDataException>(() => CreatePlayback(document));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void InvalidFutureOutsideParentBoneIsRejectedBeforePartialEvaluationCanMoveTheCamera()
    {
        var dependent = Model(1, withOutsideSlot: true, modelKeys:
        [new() { RecordIndex = 1, ObjectIndex = 1, Frame = 10, Visible = true, OutsideParents = [new(0, 0, 99)] }]);
        var document = Document([Model(), dependent], cameraParent: 0);

        // Both saved bindings are valid. The invalid binding begins only at frame 10, after
        // the camera's parent model would otherwise have been sampled at a different pose.
        var error = Assert.Throws<InvalidDataException>(() => CreatePlayback(document));
        Assert.Contains("bone index", error.Message);
    }

    [Fact]
    public void DependencyCycleBeginningAtAFutureKeyIsRejectedEvenWhenSavedBindingsAreAcyclic()
    {
        var first = Model(0, withOutsideSlot: true, modelKeys:
        [new() { RecordIndex = 1, ObjectIndex = 1, Frame = 10, Visible = true, OutsideParents = [new(0, 1, 0)] }]);
        var second = Model(1, withOutsideSlot: true, modelKeys:
        [new() { RecordIndex = 1, ObjectIndex = 1, Frame = 10, Visible = true, OutsideParents = [new(0, 0, 0)] }]);

        var error = Assert.Throws<InvalidDataException>(() => CreatePlayback(Document([first, second])));
        Assert.Contains("dependency cycle", error.Message);
    }

    [Fact]
    public void VisibleHiddenVisibleTransitionsPreserveDynamicGeometryAndCanRestoreTheSamePose()
    {
        var model = Model(modelKeys:
        [
            new() { RecordIndex = 1, ObjectIndex = 1, Frame = 1, Visible = false, NextIndex = 2 },
            new() { RecordIndex = 2, ObjectIndex = 2, Frame = 2, Visible = true, PreviousIndex = 1 }
        ]);
        using var playback = CreatePlayback(Document([model]));
        Assert.True(playback.IsSavedPose);
        Assert.True(playback.Frame.Surface.Snapshot.HasDynamicVertices);

        playback.Seek(1);
        Assert.False(playback.IsSavedPose);
        Assert.True(playback.Frame.Surface.Snapshot.IsEmpty);
        Assert.Empty(playback.Frame.Surface.Snapshot.Indices.ToArray());
        Assert.Empty(playback.Frame.Surface.Snapshot.Textures);
        Assert.All(playback.Frame.Surface.Snapshot.Materials, material => Assert.Equal(0, material.Diffuse.W));
        Assert.All(playback.Frame.Materials, flags => Assert.False(flags.CastSelfShadow));
        Assert.All(playback.Frame.Outline!.Materials, material => Assert.False(material.DrawEnabled));

        playback.Seek(2);
        Assert.True(playback.Frame.Surface.Snapshot.HasDynamicVertices);
        Assert.Equal(1, Assert.Single(playback.Frame.Surface.Snapshot.Materials).Diffuse.W);
        var visible = playback.Frame.Surface.Snapshot.Vertices.ToArray();
        Assert.NotEqual(Vector3.Zero, visible[0].Position);

        playback.Seek(1);
        playback.Seek(2);
        Assert.True(playback.Frame.Surface.Snapshot.HasDynamicVertices);
        Assert.Equal(visible, playback.Frame.Surface.Snapshot.Vertices.ToArray());
    }

    private static byte[] CameraCurves() => Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray();

    private static PmmModel Model(byte index = 0, bool withOutsideSlot = false, PmmModelKeyframe[]? modelKeys = null) => new()
    {
        Index = index, Visible = true, BoneNames = [new(0, "root", [])],
        InitialBoneKeyframes = [new() { NextIndex = 1, Rotation = Quaternion.Identity, Interpolation = CameraCurves()[..16] }],
        BoneKeyframes = [new() { RecordIndex = 1, ObjectIndex = 1, Frame = 10, Rotation = Quaternion.Identity,
            Translation = new(10, 0, 0), Interpolation = CameraCurves()[..16] }],
        BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)],
        OutsideParentSubjectBoneIndices = withOutsideSlot ? [-1] : [],
        OutsideParentStates = withOutsideSlot ? [new(0, 0, 0, new(0, -1, 0))] : [],
        InitialModelKeyframe = new() { Visible = true, NextIndex = modelKeys is { Length: > 0 } ? modelKeys[0].ObjectIndex : 0,
            OutsideParents = withOutsideSlot ? [new(0, -1, 0)] : [] },
        ModelKeyframes = modelKeys ?? []
    };

    private static PmmDocument Document(PmmModel[] models, PmmCameraKeyframe? futureCamera = null, int cameraParent = -1,
        PmmSelfShadow? selfShadow = null) => new()
    {
        Models = models, CurrentFrame = 0, OutputWidth = 1920, OutputHeight = 1080, CameraFov = 30,
        SelfShadow = selfShadow ?? new(),
        Settings = new() { CameraLookAtModelIndex = cameraParent, CameraLookAtBoneIndex = 0 },
        Camera = new()
        {
            Position = new(0, 0, -10), Perspective = true,
            InitialKeyframe = new() { NextIndex = futureCamera?.ObjectIndex ?? 0, Distance = -10, Fov = 30,
                Perspective = true, ParentModelIndex = cameraParent, Interpolation = CameraCurves() },
            Keyframes = futureCamera is null ? [] : [futureCamera]
        },
        Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY,
            InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } }
    };

    private static PmmProjectPlayback CreatePlayback(PmmDocument document)
    {
        var pmx = Pmx();
        var prepared = document.Models.Select(source => new PreparedPmmModel(source, "synthetic.pmx", pmx,
            PmmMotionAdapter.Convert(source, pmx), PmmMotionAdapter.ConvertSavedPose(source, pmx))).ToList();
        // The import plan constructor is internal; exercise playback without adding a public
        // production entry point or involving filesystem/model resolution in these regressions.
        var plan = (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [document, "synthetic.pmm", prepared, new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
        SceneVertex[] vertices = pmx.Vertices.Select(v => new SceneVertex(v.Position, v.Normal, v.Uv)).ToArray();
        var rest = new OriginalFrameScene(
            new(new(vertices, [0, 1, 2], [new("triangle", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)]), [new(Vector3.One)]),
            new(vertices.Select(v => new OriginalOutlineVertex(v.Position, v.Normal, OriginalOutlineVertexMode.Expand, 0, 0)), [new(0, Vector4.Zero)]),
            new(Vector4.One, Matrix4x4.Identity, new(0, 0, -1), 30, 1));
        return new(plan, document.Models.ToDictionary(model => (int)model.Index, _ => rest),
            (_, _) => throw new InvalidOperationException("These fixtures contain no physics bodies."));
    }

    private static PmxModel Pmx() => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "triangle", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0,1,2)],
        Materials=[new(){Name="triangle",EnglishName="",Diffuse=Vector4.One,Specular=Vector3.Zero,SpecularPower=1,Ambient=Vector3.Zero,
            DrawMode=PmxDrawModeFlags.None,EdgeColor=Vector4.Zero,EdgeSize=0,TextureIndex=-1,SphereTextureIndex=-1,SphereMode=PmxSphereMode.None,
            ToonMode=PmxToonMode.Separate,ToonTextureIndex=-1,Memo="",FaceVertexCount=3}],
        Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
        {
            Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1
        }).ToArray()
    };
}
