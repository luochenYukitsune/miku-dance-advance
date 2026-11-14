using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class MixedNameCompatibilityTests : IDisposable
{
    private static readonly SceneProjectSaveOptions ExternalAssets = new() { CopyModelAssets = false };
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mixed-name-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public MixedNameCompatibilityTests() => Directory.CreateDirectory(directory);
    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("No bodies.");
    private static byte[] Bytes(string name, int cp)
    { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(cp, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(name); }
    private static VmdMotion Mixed() => new()
    {
        ModelName = "model",
        BoneKeyframes = [new() { BoneName = "中", Frame = 0, OriginalName = new("中", Bytes("中", 936), 936) },
            new() { BoneName = "中", Frame = 20, Translation = new(10, 0, 0), OriginalName = new("中", Bytes("中", 936), 936) }],
        MorphKeyframes = [new() { MorphName = "ｳ", Frame = 0, Weight = .2f, OriginalName = new("ｳ", Bytes("ｳ", 932), 932) }],
        IkKeyframes = [new() { Frame = 0, Switches = [new("中", true) { OriginalName = new("中", Bytes("中", 936), 936) }] }]
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PmmConversionRetainsRawNamesThroughRegisteredAndSavedPose(bool saved)
    {
        var source = new PmmModel
        {
            BoneNames = [new(0, "wrong display", Bytes("带", 936))],
            MorphNames = [new(0, "wrong display", Bytes("ｳ", 932))],
            InitialBoneKeyframes = [new() { RecordIndex = 0, ObjectIndex = 0, Rotation = Quaternion.Identity, Interpolation = Enumerable.Repeat((byte)20, 16).ToArray() }],
            InitialMorphKeyframes = [new() { RecordIndex = 0, ObjectIndex = 0, Weight = .4f }],
            ConstraintBoneIndices = [0], InitialModelKeyframe = new() { RecordIndex = 0, ConstraintEnabled = [true] },
            BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)],
            MorphStates = [new(0, .4f)], ConstraintStates = [new(0, true)]
        };
        var model = PmmMotionAdapterTests.Pmx(["带"], ["ｳ"]);
        var motion = saved ? PmmMotionAdapter.ConvertSavedPose(source, model) : PmmMotionAdapter.Convert(source, model);
        Assert.Equal(Bytes("带", 936), motion.BoneKeyframes[0].OriginalName!.Bytes);
        Assert.Equal(936, motion.IkKeyframes[0].Switches[0].OriginalName!.AnsiCodePage);
        Assert.Equal(Bytes("ｳ", 932), motion.MorphKeyframes[0].OriginalName!.Bytes);
        var asset = SceneMotionAsset.FromMotion(motion);
        Assert.Equal("带", asset.Motion.BoneKeyframes[0].BoneName);
        Assert.Equal("ｳ", asset.Motion.MorphKeyframes[0].MorphName);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("delete")]
    [InlineData("paste")]
    [InlineData("register")]
    [InlineData("curve")]
    public void EveryCharacterTransactionAndHistoryRetainsRawNames(string operation)
    {
        var editor = new MotionEditor(Mixed()); byte[] initial = VmdWriter.ToBytes(editor.ExportMotion());
        var selected = new[] { KeyframeAddress.Bone("中", 20) };
        var plan = operation switch
        {
            "move" => editor.PrepareMove(selected, 1),
            "delete" => editor.PrepareDelete(selected),
            "paste" => editor.PreparePaste(editor.CopySelection(selected), 30),
            "register" => editor.PrepareRegister(new() { BoneKeyframes = [new() { BoneName = "中", Frame = 30 }] }),
            "curve" => editor.PrepareSetInterpolation(selected, BoneInterpolationChannel.X, new(10, 30, 80, 120)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        editor.Apply(plan);
        var asset = SceneMotionAsset.FromMotion(editor.ExportMotion());
        Assert.All(asset.Motion.BoneKeyframes, b => Assert.Equal("中", b.BoneName));
        Assert.Equal("ｳ", asset.Motion.MorphKeyframes[0].MorphName);
        Assert.Equal("中", asset.Motion.IkKeyframes[0].Switches[0].BoneName);
        byte[] edited = VmdWriter.ToBytes(editor.ExportMotion());
        Assert.True(editor.Undo()); Assert.Equal(initial, VmdWriter.ToBytes(editor.ExportMotion()));
        Assert.True(editor.Redo()); Assert.Equal(edited, VmdWriter.ToBytes(editor.ExportMotion()));
    }

    [Theory]
    [InlineData("bone")]
    [InlineData("morph")]
    [InlineData("ik")]
    public void BareRegistrationInheritsOnlyTheExistingTrackNamePayload(string kind)
    {
        var original = new VmdEncodedName("带", Bytes("带", 936), 936);
        var editor = new MotionEditor(new()
        {
            BoneKeyframes = [new() { BoneName = "带", OriginalName = original }],
            MorphKeyframes = [new() { MorphName = "带", OriginalName = original }],
            IkKeyframes = [new() { Switches = [new("带", true) { OriginalName = original }] }]
        });
        var incoming = kind switch
        {
            "bone" => new VmdMotion { BoneKeyframes = [new() { BoneName = "带", Frame = 30 }] },
            "morph" => new VmdMotion { MorphKeyframes = [new() { MorphName = "带", Frame = 30 }] },
            _ => new VmdMotion { IkKeyframes = [new() { Frame = 30, Switches = [new("带", true)] }] }
        };
        editor.Apply(editor.PrepareRegister(incoming));
        var exported = editor.ExportMotion();
        var raw = kind switch
        {
            "bone" => exported.BoneKeyframes.Single(k => k.Frame == 30).OriginalName,
            "morph" => exported.MorphKeyframes.Single(k => k.Frame == 30).OriginalName,
            _ => exported.IkKeyframes.Single(k => k.Frame == 30).Switches[0].OriginalName
        };
        Assert.NotNull(raw); Assert.Equal(original.Bytes, raw.Bytes);
        _ = SceneMotionAsset.FromMotion(exported);
    }

    [Fact]
    public void AmbiguousRawInheritanceFailsBeforeChangingDataOrHistory()
    {
        var editor = new MotionEditor(new()
        {
            BoneKeyframes = [new() { BoneName = "中", OriginalName = new("中", Bytes("中", 936), 936) },
                new() { BoneName = "中", Frame = 20, OriginalName = new("中", Bytes("中", 932), 932) }]
        });
        Assert.Throws<ArgumentException>(() => editor.PrepareRegister(new() { BoneKeyframes = [new() { BoneName = "中", Frame = 30 }] }));
        Assert.Equal(0UL, editor.Revision); Assert.False(editor.CanUndo); Assert.Equal(2, editor.ExportMotion().BoneKeyframes.Count);
    }

    [Fact]
    public void ModelGuidedAssetPersistsProfileAndSeparatesSameHashSamePageDifferentMeanings()
    {
        byte[] bytes = VmdWriter.ToBytes(Mixed());
        var cnModel = EditedMotionProjectTests.Model("中");
        var cnAsset = SceneMotionAsset.FromBytes(bytes, "cn.vmd", new() { BoneNameCandidates = ["中"], MorphNameCandidates = ["ｳ"] });
        var jpAsset = SceneMotionAsset.FromBytes(bytes, "jp.vmd");
        string jpName = jpAsset.Motion.BoneKeyframes[0].BoneName;
        Assert.NotEqual("中", jpName); Assert.Equal(cnAsset.Sha256, jpAsset.Sha256);
        Assert.Equal(cnAsset.AnsiCodePage, jpAsset.AnsiCodePage);
        Assert.NotEmpty(cnAsset.NameOverrides); Assert.Empty(jpAsset.NameOverrides);
        var jpModel = EditedMotionProjectTests.Model(jpName);
        var cnRest = EditedMotionProjectTests.Rest(cnModel); var jpRest = EditedMotionProjectTests.Rest(jpModel);
        string cnPath = Path.Combine(directory, "cn.pmx"), jpPath = Path.Combine(directory, "jp.pmx");
        File.WriteAllText(cnPath, "injected CN fixture"); File.WriteAllText(jpPath, "injected JP fixture");
        var layout = new SceneCompositionEditor(); var lighting = new SceneLightingEditor();
        var cn = layout.Add("CN", cnPath, ModelRole.Character, cnRest); var jp = layout.Add("JP", jpPath, ModelRole.Character, jpRest);
        using var session = new SceneAnimationSession(layout.Objects,
            [new(cn.Id, cnModel, cnAsset.Motion, PhysicsEnabled: false, Asset: cnAsset), new(jp.Id, jpModel, jpAsset.Motion, PhysicsEnabled: false, Asset: jpAsset)],
            NoPhysics, lighting.Snapshot, Token);
        session.Seek(10, Token); var expected = session.Frame.Surface.Snapshot.Vertices.ToArray();
        string path = Path.Combine(directory, "mixed.mmdscene");
        SceneAnimationProjectStore.Save(path, layout, lighting, session, ExternalAssets, Token);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(4, document.RootElement.GetProperty("version").GetInt32());
        Assert.True(document.RootElement.GetProperty("motions")[0].GetProperty("nameOverrides").GetArrayLength() > 0);
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "assets", "motions"), "*.vmd"));
        using var loaded = SceneAnimationProjectStore.Load(path, (p, _) => p == cnPath ? new(cnModel, cnRest) : new(jpModel, jpRest), NoPhysics, Token);
        Assert.Equal(expected, loaded.Animation.Frame.Surface.Snapshot.Vertices);
        var invalid = JsonNode.Parse(File.ReadAllText(path))!;
        invalid["version"] = 3;
        Assert.Throws<JsonException>(() => SceneAnimationProjectStore.Prepare(invalid.ToJsonString(), path,
            (_, _) => throw new InvalidOperationException("Profile version fails before loading models."), NoPhysics, Token));
        invalid["version"] = 4; invalid["motions"]![0]!["nameOverrides"]![0]!["ansiCodePage"] = 65001;
        Assert.Throws<JsonException>(() => SceneAnimationProjectStore.Prepare(invalid.ToJsonString(), path,
            (_, _) => throw new InvalidOperationException("Invalid profile fails before loading models."), NoPhysics, Token));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(directory), temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid temp scope.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
