using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmdProjectIntegrationTests : IDisposable
{
    private static readonly SceneProjectSaveOptions ExternalAssets = new() { CopyModelAssets = false };
    private readonly string root = Path.Combine(Path.GetTempPath(), "pmd-project-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public PmdProjectIntegrationTests() => Directory.CreateDirectory(root);

    [Fact]
    public void PmmPmdNameBindingRestoresSavedPoseThenPlaysTimelineWithoutChangingAsset()
    {
        string path = WritePmd("character.PMD"); byte[] before = File.ReadAllBytes(path);
        var actor = Actor(path);
        var plan = PmmProjectImport.Prepare(Document(actor), Path.Combine(root, "scene.pmm"), cancellationToken: Token);
        Assert.True(plan.CanCommit); Assert.Empty(plan.UnresolvedAssets);
        var prepared = Assert.Single(plan.Models);
        // PMM track order is deliberately reversed relative to PMD bones.
        Assert.Equal("child", prepared.Motion.BoneKeyframes[0].BoneName);
        Assert.Equal("root", prepared.SavedPoseMotion.BoneKeyframes[1].BoneName);
        using var playback = new PmmProjectPlayback(plan, new Dictionary<int, OriginalFrameScene> { [0] = Rest(prepared.Model) },
            (_, _) => throw new InvalidOperationException("This fixture has no physics bodies."));
        Assert.True(playback.IsSavedPose);
        AssertNear(new(3, 4, 0), playback.Frame.Surface.Snapshot.Vertices[0].Position);
        playback.Seek(10);
        Assert.False(playback.IsSavedPose);
        AssertNear(new(10, 0, 0), playback.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void RepeatedPmdReferenceUsesTheSameConvertedImmutableModel()
    {
        string path = WritePmd("character.pmd");
        var first = Actor(path); var second = Actor(path, 1);
        var plan = PmmProjectImport.Prepare(Document(first, second), Path.Combine(root, "scene.pmm"), cancellationToken: Token);
        Assert.True(plan.CanCommit); Assert.Equal(2, plan.Models.Count);
        Assert.Same(plan.Models[0].Model, plan.Models[1].Model);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("unknown-trailer")]
    [InlineData("wrong-names")]
    public void BadPmdOrNonMatchingModelIsAnUnresolvedCandidate(string defect)
    {
        string path = WritePmd("character.pmd");
        if (defect == "broken") File.WriteAllText(path, "broken");
        if (defect == "unknown-trailer")
        {
            using var input = File.OpenRead(path); var model = PmdReader.Read(input);
            File.WriteAllBytes(Path.Combine(root, "trailer.pmd"), PmdWriter.ToBytes(model with
            {
                HasAnimationTail = true, HasEnglishExtension = true, HasToonExtension = true,
                HasPhysicsExtension = true, BoneDisplayFrames = [new("センター", [])], UnknownTailData = [0x91], UnparsedTailBytes = 1
            }));
            path = Path.Combine(root, "trailer.pmd");
        }
        var actor = Actor(path, wrongNames: defect == "wrong-names");
        byte[] before = File.ReadAllBytes(path);
        var plan = PmmProjectImport.Prepare(Document(actor), Path.Combine(root, "scene.pmm"), cancellationToken: Token);
        Assert.False(plan.CanCommit); Assert.Empty(plan.Models);
        var unresolved = Assert.Single(plan.UnresolvedAssets);
        Assert.Equal(PmmAssetResolutionStatus.Invalid, unresolved.Resolution.Status);
        Assert.Equal(path, unresolved.Resolution.Path); Assert.False(string.IsNullOrWhiteSpace(unresolved.Error));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void PmdRepairOverrideIsAuthoritativeAndCancellationIsNotSwallowed()
    {
        string path = WritePmd("repaired.pmd"); var document = Document(Actor("missing.pmd"));
        var options = new PmmImportOptions { ModelPathOverrides = new Dictionary<int, string> { [0] = path } };
        Assert.True(PmmProjectImport.Prepare(document, Path.Combine(root, "scene.pmm"), options, Token).CanCommit);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => PmmProjectImport.Prepare(document, Path.Combine(root, "scene.pmm"), options, cancel.Token));
    }

    [Fact]
    public void StaticPmdSceneRoundTripLoadsThroughInjectedConversionAndKeepsRelativePath()
    {
        string path = WritePmd("character.PMD"); byte[] before = File.ReadAllBytes(path);
        var layout = new SceneCompositionEditor(); var actor = layout.Add("PMD", path, ModelRole.Character, Load(path, Token));
        layout.SetTransform(actor.Id, new(2, 3, 4), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f), 2);
        string project = Path.Combine(root, "scene.mmdscene");
        SceneProjectStore.Save(project, layout, new(), ExternalAssets, Token);
        var json = JsonNode.Parse(File.ReadAllText(project))!;
        Assert.Equal("character.PMD", json["objects"]![0]!["sourcePath"]!.GetValue<string>());
        var restored = SceneProjectStore.Load(project, Load, Token);
        Assert.Equal(actor.Id, Assert.Single(restored.Composition.Objects).Id);
        Assert.Equal(layout.Frame.Surface.Snapshot.Vertices, restored.Composition.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void InvalidPmdLoaderFailureKeepsPriorSceneAndProjectUntouched()
    {
        string path = WritePmd("character.pmd"); var layout = new SceneCompositionEditor();
        var actor = layout.Add("PMD", path, ModelRole.Character, Load(path, Token));
        string project = Path.Combine(root, "scene.mmdscene"); SceneProjectStore.Save(project, layout, new(), ExternalAssets, Token);
        byte[] before = File.ReadAllBytes(project); var frame = layout.Frame;
        File.WriteAllText(path, "invalid pmd");
        var error = Assert.Throws<SceneProjectAssetException>(() => SceneProjectStore.Load(project, Load, Token));
        Assert.Equal(actor.Id, error.ObjectId); Assert.Equal(path, error.SourcePath);
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Same(frame, layout.Frame); Assert.Equal(before, File.ReadAllBytes(project));
    }

    private string WritePmd(string name)
    {
        var model = new PmdModel("fixture", [new(Vector3.Zero, Vector3.UnitY, Vector2.Zero, 0, 0, 1),
            new(Vector3.UnitX, Vector3.UnitY, Vector2.UnitX, 0, 0, 1), new(Vector3.UnitZ, Vector3.UnitY, Vector2.UnitY, 0, 0, 1)],
            [0, 1, 2], [new(Vector4.One, Vector3.Zero, Vector3.One, 1, "", 0, 3)],
            [new("root", -1, Vector3.Zero) { Type = 1, TailIndex = 1 }, new("child", 0, Vector3.UnitY) { Type = 1 }], 0);
        string path = Path.Combine(root, name); File.WriteAllBytes(path, PmdWriter.ToBytes(model)); return path;
    }

    private static PmmModel Actor(string path, byte index = 0, bool wrongNames = false) => new()
    {
        Index = index, Visible = true, LastFrame = 10, Path = new(0, path, []),
        BoneNames = [new(0, wrongNames ? "missing-child" : "child", []), new(0, wrongNames ? "missing-root" : "root", [])],
        InitialBoneKeyframes = [new() { Rotation = Quaternion.Identity, Interpolation = Curves() },
            new() { RecordIndex = 1, ObjectIndex = 1, NextIndex = 2, Rotation = Quaternion.Identity, Interpolation = Curves() }],
        BoneKeyframes = [new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, Frame = 10, Translation = new(10, 0, 0), Rotation = Quaternion.Identity, Interpolation = Curves() }],
        BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0), new(0, new(3, 4, 0), Quaternion.Identity, false, false, 0)],
        InitialModelKeyframe = new() { Visible = true }
    };

    private static PmmDocument Document(params PmmModel[] actors) => new()
    {
        Models = actors, OutputWidth = 1280, OutputHeight = 720, CameraFov = 30,
        Settings = new() { PhysicsSimulationMode = 0, CameraLookAtModelIndex = -1 },
        Camera = new() { Position = new(0, 0, -10), Perspective = true,
            InitialKeyframe = new() { Distance = -10, Fov = 30, Perspective = true, ParentModelIndex = -1,
                Interpolation = Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray() } },
        Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY,
            InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } }
    };

    private static byte[] Curves() => Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray();
    private static OriginalFrameScene Load(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); using var input = File.OpenRead(path);
        return Rest(PmdPmxAdapter.Convert(PmdReader.Read(input)));
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static void AssertNear(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"Expected {expected}, actual {actual}");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
