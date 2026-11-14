using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class EditedMotionProjectTests : IDisposable
{
    private static readonly SceneProjectSaveOptions ExternalAssets = new() { CopyModelAssets = false };
    private readonly string directory = Path.Combine(Path.GetTempPath(), "edited-motion-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public EditedMotionProjectTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("move")]
    [InlineData("delete")]
    [InlineData("paste")]
    [InlineData("register")]
    [InlineData("curve")]
    public void CharacterEditsAndUndoPreserveCameraLightAndShadowTracks(string operation)
    {
        var source = Motion(); var editor = new MotionEditor(source);
        var selection = new[] { KeyframeAddress.Bone("root", 20) };
        var plan = operation switch
        {
            "move" => editor.PrepareMove(selection, 1),
            "delete" => editor.PrepareDelete(selection),
            "paste" => editor.PreparePaste(editor.CopySelection(selection), 30),
            "register" => editor.PrepareRegister(new() { BoneKeyframes = [new() { BoneName = "root", Frame = 30 }] }),
            "curve" => editor.PrepareSetInterpolation(selection, BoneInterpolationChannel.X, new(10, 30, 80, 120)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        editor.Apply(plan);
        void Verify(VmdMotion motion)
        {
            var camera = Assert.Single(motion.CameraKeyframes);
            Assert.Equal(-30f, camera.Distance); Assert.Equal(new(1, 2, 3), camera.Interest);
            Assert.False(camera.Perspective); Assert.Equal(45u, camera.ViewAngle);
            Assert.Equal(Enumerable.Range(0, 24).Select(i => (byte)i), camera.Interpolation);
            Assert.Equal(new(1, -.5f, 2), Assert.Single(motion.LightKeyframes).Direction);
            Assert.Equal(2, Assert.Single(motion.SelfShadowKeyframes).Mode);
            Assert.Equal(.001f, motion.SelfShadowKeyframes[0].Distance);
        }
        var snapshot = editor.ExportMotion(); Verify(snapshot);
        Assert.NotSame(source.CameraKeyframes[0], snapshot.CameraKeyframes[0]);
        Assert.NotSame(source.CameraKeyframes[0].Interpolation, snapshot.CameraKeyframes[0].Interpolation);
        Verify(SceneMotionAsset.FromMotion(snapshot).Motion);
        Assert.True(editor.Undo()); Verify(editor.ExportMotion());
        Assert.True(editor.Redo()); Verify(editor.ExportMotion());
    }

    [Fact]
    public void EditedMotionAssetsSaveReopenWithCurvesAndAllRetainedSections()
    {
        File.WriteAllText(Path.Combine(directory, "model.pmx"), "fixture; model loading is injected below");
        var model = Model(); var rest = Rest(model); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", Path.Combine(directory, "model.pmx"), ModelRole.Character, rest);
        var lighting = new SceneLightingEditor(); var original = SceneMotionAsset.FromMotion(Motion(), "source.vmd");
        using var session = new SceneAnimationSession(layout.Objects,
            [new(actor.Id, model, original.Motion, PhysicsEnabled: false, Asset: original)], NoPhysics, lighting.Snapshot, Token);
        session.Seek(5, Token); var before = session.Frame.Surface.Snapshot.Vertices.ToArray();
        var editor = new MotionEditor(original.Motion);
        editor.Apply(editor.PrepareSetInterpolation([KeyframeAddress.Bone("root", 20)], BoneInterpolationChannel.X, new(0, 127, 0, 127)));
        var edited = SceneMotionAsset.FromMotion(editor.ExportMotion(), "edited.vmd");
        Assert.NotEqual(original.Sha256, edited.Sha256);
        session.BindMotion(new(actor.Id, model, edited.Motion, PhysicsEnabled: false, Asset: edited), Token);
        var expected = session.Frame.Surface.Snapshot.Vertices.ToArray();
        Assert.NotEqual(before[0].Position, expected[0].Position);
        string path = Path.Combine(directory, "edited.mmdscene");
        SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, Token);
        using var loaded = SceneAnimationProjectStore.Load(path, (_, _) => new(model, rest), NoPhysics, Token);
        Assert.Equal(5, loaded.Animation.CurrentFrame);
        Assert.Equal(expected, loaded.Animation.Frame.Surface.Snapshot.Vertices);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        string assetPath = json.RootElement.GetProperty("motions")[0].GetProperty("assetPath").GetString()!;
        var reopened = SceneMotionAsset.Load(Path.Combine(directory, assetPath), Token);
        Assert.Equal(edited.Sha256, reopened.Sha256);
        Assert.Equal(edited.Motion.BoneKeyframes[1].Interpolation, reopened.Motion.BoneKeyframes[1].Interpolation);
        Assert.False(Assert.Single(reopened.Motion.CameraKeyframes).Perspective);
        Assert.Single(reopened.Motion.LightKeyframes); Assert.Single(reopened.Motion.SelfShadowKeyframes);
        Assert.Equal(original.Motion.BoneKeyframes[1].Interpolation, Motion().BoneKeyframes[1].Interpolation);
    }

    [Fact]
    public void MotionFactoryDetachesInputAndInvalidExportCannotReplaceTheActiveSession()
    {
        var model = Model(); var rest = Rest(model); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", Path.Combine(directory, "model.pmx"), ModelRole.Character, rest);
        var source = Motion(); var input = source.BoneKeyframes.ToArray();
        var asset = SceneMotionAsset.FromMotion(new() { ModelName = "model", BoneKeyframes = input });
        input[1] = new() { BoneName = "root", Frame = 20, Translation = new(999, 0, 0) };
        Assert.Equal(new(10, 0, 0), asset.Motion.BoneKeyframes[1].Translation);
        using var session = new SceneAnimationSession(layout.Objects, [new(actor.Id, model, asset.Motion, PhysicsEnabled: false, Asset: asset)], NoPhysics, cancellationToken: Token);
        session.Seek(5, Token); var frame = session.Frame;
        Assert.ThrowsAny<ArgumentException>(() => SceneMotionAsset.FromMotion(new() { ModelName = "name-too-long-for-vmd-header" }));
        Assert.ThrowsAny<ArgumentException>(() => SceneMotionAsset.FromMotion(source, "wrong.pmx"));
        Assert.Same(frame, session.Frame); Assert.False(session.IsFaulted);
        Assert.Throws<ArgumentNullException>(() => SceneMotionAsset.FromMotion(null!));
    }

    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("No physical bodies in this fixture.");

    [Fact]
    public void ChineseEncodingAndExplicitHeaderOverrideSurviveAssetAndProjectSave()
    {
        File.WriteAllText(Path.Combine(directory, "model.pmx"), "injected model fixture");
        var model = Model("中"); var rest = Rest(model); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", Path.Combine(directory, "model.pmx"), ModelRole.Character, rest);
        var lighting = new SceneLightingEditor();
        var source = new VmdMotion { ModelName = "A model name much longer than the VMD header", BoneKeyframes =
            [new() { BoneName = "中", Frame = 0 }, new() { BoneName = "中", Frame = 20, Translation = new(10, 0, 0) }] };
        var asset = SceneMotionAsset.FromMotion(source, "edited.vmd", new() { AnsiCodePage = 936, ModelNameOverride = "CN" });
        Assert.Equal(936, asset.AnsiCodePage); Assert.Equal("CN", asset.Motion.ModelName);
        Assert.Equal("A model name much longer than the VMD header", source.ModelName);
        using var session = new SceneAnimationSession(layout.Objects, [new(actor.Id, model, asset.Motion, PhysicsEnabled: false, Asset: asset)], NoPhysics, lighting.Snapshot, Token);
        session.Seek(5, Token); var expected = session.Frame.Surface.Snapshot.Vertices.ToArray();
        string path = Path.Combine(directory, "encoded.mmdscene");
        SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, Token);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(3, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(936, json.RootElement.GetProperty("motions")[0].GetProperty("ansiCodePage").GetInt32());
        var invalid = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        invalid["motions"]![0]!["ansiCodePage"] = 65001;
        Assert.Throws<JsonException>(() => SceneAnimationProjectStore.Prepare(invalid.ToJsonString(), path,
            (_, _) => throw new InvalidOperationException("Invalid encoding must fail before model loading."), NoPhysics, Token));
        invalid["motions"]![0]!["ansiCodePage"] = 936;
        invalid["version"] = SceneAnimationProjectStore.Version;
        Assert.Throws<JsonException>(() => SceneAnimationProjectStore.Prepare(invalid.ToJsonString(), path,
            (_, _) => throw new InvalidOperationException("Version 2 cannot declare explicit encoding."), NoPhysics, Token));
        using var loaded = SceneAnimationProjectStore.Load(path, (_, _) => new(model, rest), NoPhysics, Token);
        Assert.Equal(expected, loaded.Animation.Frame.Surface.Snapshot.Vertices);
    }

    [Fact]
    public void SameRawBytesWithDifferentEncodingsShareResourceButNotParsedMotionCache()
    {
        string cnPath = Path.Combine(directory, "cn.pmx"), jpPath = Path.Combine(directory, "jp.pmx");
        File.WriteAllText(cnPath, "CN fixture"); File.WriteAllText(jpPath, "JP fixture");
        var cn = Model("中"); var cnRest = Rest(cn);
        var source = new VmdMotion { ModelName = "CN", BoneKeyframes = [new() { BoneName = "中", Frame = 20, Translation = new(10, 0, 0) }] };
        byte[] bytes = VmdWriter.ToBytes(source, new() { AnsiCodePage = 936 });
        var cnAsset = SceneMotionAsset.FromBytes(bytes, "cn.vmd", new() { AnsiCodePage = 936 });
        var jpAsset = SceneMotionAsset.FromBytes(bytes, "jp.vmd");
        string jpName = jpAsset.Motion.BoneKeyframes[0].BoneName;
        Assert.NotEqual("中", jpName); Assert.Equal(cnAsset.Sha256, jpAsset.Sha256);
        var jp = Model(jpName); var jpRest = Rest(jp); var layout = new SceneCompositionEditor();
        var a = layout.Add("CN", cnPath, ModelRole.Character, cnRest);
        var b = layout.Add("JP", jpPath, ModelRole.Character, jpRest); var lighting = new SceneLightingEditor();
        using var session = new SceneAnimationSession(layout.Objects,
            [new(a.Id, cn, cnAsset.Motion, PhysicsEnabled: false, Asset: cnAsset), new(b.Id, jp, jpAsset.Motion, PhysicsEnabled: false, Asset: jpAsset)],
            NoPhysics, lighting.Snapshot, Token);
        session.Seek(10, Token); var expected = session.Frame.Surface.Snapshot.Vertices.ToArray();
        string path = Path.Combine(directory, "mixed.mmdscene");
        SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, Token);
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "assets", "motions"), "*.vmd"));
        using var loaded = SceneAnimationProjectStore.Load(path, (p, _) => p == cnPath ? new(cn, cnRest) : new(jp, jpRest), NoPhysics, Token);
        Assert.Equal(expected, loaded.Animation.Frame.Surface.Snapshot.Vertices);
    }
    private static VmdMotion Motion() => new()
    {
        ModelName = "model",
        BoneKeyframes = [new() { BoneName = "root", Frame = 0 }, new() { BoneName = "root", Frame = 20, Translation = new(10, 0, 0),
            Interpolation = PmmMotionAdapter.ConvertBoneInterpolation(Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray()) }],
        CameraKeyframes = [new() { Frame = 7, Distance = -30, Interest = new(1, 2, 3), Rotation = new(.1f, -.2f, .3f),
            ViewAngle = 45, Perspective = false, Interpolation = Enumerable.Range(0, 24).Select(i => (byte)i).ToArray() }],
        LightKeyframes = [new() { Frame = 8, Color = Vector3.One, Direction = new(1, -.5f, 2) }],
        SelfShadowKeyframes = [new() { Frame = 9, Mode = 2, Distance = .001f }]
    };
    internal static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    internal static PmxModel Model(string boneName = "root") => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "model", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
        Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
            Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
            SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = boneName, EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
        Morphs = [], DisplayFrames = [], Joints = [], RigidBodies = [], SoftBodies = [],
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
        { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray()
    };
    public void Dispose()
    {
        // This directory is uniquely allocated beneath the configured task temp path.
        string root = Path.GetFullPath(directory); string temp = Path.GetFullPath(Path.GetTempPath());
        if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
