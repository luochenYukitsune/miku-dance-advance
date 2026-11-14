using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmAccessoryPlaybackTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "accessory-playback-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PmmAccessoryPlaybackTests() => Directory.CreateDirectory(folder);
    private PmmProjectPlayback Project(bool additive = false, string? accessoryText = null)
    {
        var pmd = new PmdModel("model", [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero, 0, 0, 1),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero, 0, 0, 1), new(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero, 0, 0, 1)],
            [0, 1, 2], [new(Vector4.One, Vector3.Zero, Vector3.Zero, 1, "", 0, 3)], [new("bone", -1, Vector3.Zero)], 0);
        string modelPath = Path.Combine(folder, "actor.pmd"); File.WriteAllBytes(modelPath, PmdWriter.ToBytes(pmd));
        string accessoryPath = Path.Combine(folder, "part.x"); File.WriteAllText(accessoryPath, accessoryText ?? XAccessoryTests.Triangle);
        var model = PmdPmxAdapter.Convert(pmd);
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, new(0, 0, -20), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
        var layout = new SceneCompositionEditor(); var actor = layout.Add("actor", modelPath, ModelRole.Character, rest);
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "bone" }, new() { BoneName = "bone", Frame = 30, Translation = new(3, 0, 0) }] };
        using var session = new SceneAnimationSession(layout.Objects, [new(actor.Id, model, motion, PhysicsEnabled: false)], _ => throw new InvalidOperationException());
        var original = PmmSessionExporter.Export(session);
        var document = new PmmDocument
        {
            Models = original.Models, PanelStates = original.PanelStates, OutputWidth = original.OutputWidth, OutputHeight = original.OutputHeight,
            Camera = original.Camera, CameraFov = original.CameraFov, Light = original.Light, Gravity = original.Gravity,
            Settings = original.Settings, SelfShadow = original.SelfShadow, AccessoryNames = [new(0, "part", [])],
            Accessories = [new() { Index = 7, Name = new(0, "part", []), Path = new(0, accessoryPath, []), Scale = 1,
                OpacityAndVisibility = 1, ParentModelIndex = 0, ParentBoneIndex = 0, AddBlendEnabled = additive,
                InitialKeyframe = new() { Scale = 1, OpacityAndVisibility = 1, ParentModelIndex = 0, ParentBoneIndex = 0 } }]
        };
        var plan = PmmProjectImport.Prepare(document, Path.Combine(folder, "scene.pmm"), cancellationToken: Token);
        Assert.True(plan.CanCommit);
        return new(plan, new Dictionary<int, OriginalFrameScene> { [0] = rest }, (_, _) => throw new InvalidOperationException());
    }
    [Fact]
    public void PreparedAccessoryUsesTheSameAnimatedBoneClockAndMissingCandidateDoesNotReplaceIt()
    {
        using var project = Project();
        var playback = PmmAccessoryPlayback.Prepare(project, cancellationToken: Token);
        var before = Assert.Single(playback.Sample());
        Vector3 initial = Vector3.Transform(before.Frame.Surface.Snapshot.Vertices[0].Position, before.Frame.Surface.Snapshot.World);
        project.Seek(30);
        var after = Assert.Single(playback.Sample());
        Vector3 current = Vector3.Transform(after.Frame.Surface.Snapshot.Vertices[0].Position, after.Frame.Surface.Snapshot.World);
        Assert.InRange(Vector3.Distance(initial + new Vector3(3, 0, 0), current), 0, 1e-5f);
        var combined = playback.CombineWithProject(accessoriesAfterModels: true);
        Assert.Equal(6, combined.Surface.Snapshot.Vertices.Length);
        File.Move(Path.Combine(folder, "part.x"), Path.Combine(folder, "hidden.x"));
        Assert.Throws<InvalidDataException>(() => PmmAccessoryPlayback.Prepare(project, cancellationToken: Token));
        Assert.Equal(current, Vector3.Transform(Assert.Single(playback.Sample()).Frame.Surface.Snapshot.Vertices[0].Position, after.Frame.Surface.Snapshot.World));
    }
    [Fact]
    public void AdditiveBlendIsExposedAndNormalComposerRejectsIt()
    {
        using var project = Project(additive: true);
        var playback = PmmAccessoryPlayback.Prepare(project, cancellationToken: Token);
        Assert.True(Assert.Single(playback.Sample()).AddBlendEnabled);
        Assert.Throws<NotSupportedException>(() => playback.CombineWithProject(accessoriesAfterModels: true));
    }
    [Fact]
    public void PreparationAndSamplingRetainParsedDocumentAndOriginalTextureToken()
    {
        const string textureToken = "Body.PNG*Effect.SPA";
        string text = XAccessoryTests.Triangle.Replace("image.png", textureToken)
            .Replace("0;0;0;; TextureFilename", "0.03;0.07;0.11;; TextureFilename")
            .Replace("MeshTextureCoords", "MeshVertexColors { 1; 0; 0.2;0.3;0.4;0.5;; } MeshTextureCoords");
        using var project = Project(accessoryText: text);
        var loadedPaths = new List<string>();
        var playback = PmmAccessoryPlayback.Prepare(project, loadTexture: path =>
        {
            loadedPaths.Add(path);
            return new SceneTexture("retained-texture", 1, 1, new byte[] { 255, 255, 255, 255 });
        }, cancellationToken: Token);
        var asset = Assert.Single(playback.Assets);
        var document = Assert.IsType<XTextDocument>(asset.Document);
        var material = Assert.Single(document.Materials).Material;
        Assert.Equal(textureToken, material.Texture);
        Assert.Equal(new Vector3(.03f, .07f, .11f), material.Emissive);
        Assert.Equal(new Vector4(.2f, .3f, .4f, .5f), document.VertexColors[0]);
        Assert.EndsWith(textureToken, Assert.Single(loadedPaths), StringComparison.Ordinal);
        var saved = Assert.IsType<PmmAccessorySampleInputs>(Assert.Single(playback.Sample()).Inputs);
        Assert.Same(document, saved.Document); Assert.True(saved.IsSavedPose);
        Assert.Equal(project.CurrentFrame, saved.SourceFrame); Assert.Same(asset.Source, saved.Source);

        project.Seek(15);
        double clock = project.CurrentFrame;
        var animated = Assert.IsType<PmmAccessorySampleInputs>(Assert.Single(playback.Sample()).Inputs);
        Assert.False(animated.IsSavedPose); Assert.Equal(clock, animated.SourceFrame);
        Assert.Equal(clock, project.CurrentFrame); Assert.Same(document, animated.Document);
        Assert.Same(asset.Source, animated.Source);
        Assert.NotEqual(saved.World, animated.World);
        Assert.Equal(saved.World, saved.SourceGeometry.World);
        Assert.Equal(textureToken, Assert.Single(saved.Document!.Materials).Material.Texture);
    }
    [Fact]
    public void PreparedOriginalBytesRemainAvailableAfterTheSourceIsMoved()
    {
        using var project = Project();
        string path = Path.Combine(folder, "part.x");
        byte[] originalBytes = File.ReadAllBytes(path);
        var asset = Assert.Single(PmmAccessoryPlayback.Prepare(project, cancellationToken: Token).Assets);
        File.Move(path, Path.Combine(folder, "unavailable.x"));
        var snapshot = asset.RequirePortableSource();
        Assert.Equal(path, snapshot.OriginalPath);
        Assert.Equal(originalBytes, snapshot.CopyOriginalBytes());
        Assert.Equal(932, snapshot.ReadOptions.AnsiCodePage);
        Assert.Equal(1f, snapshot.UnitScale);
        var restored = PreparedPmmAccessory.FromOriginalBytes(asset.Index, snapshot.OriginalPath,
            snapshot.CopyOriginalBytes(), readOptions: snapshot.ReadOptions, unitScale: snapshot.UnitScale,
            cancellationToken: Token);
        Assert.Equal(asset.Sha256, restored.RequirePortableSource().Sha256);
        Assert.Equal(asset.Source.Surface.Snapshot.Vertices.ToArray(), restored.Source.Surface.Snapshot.Vertices.ToArray());
    }
    [Fact]
    public void FilePreparationRejectsOversizedInputBeforeCallingTextureLoader()
    {
        using var project = Project();
        int callbacks = 0;
        Assert.Throws<InvalidDataException>(() => PmmAccessoryPlayback.Prepare(project,
            loadTexture: _ => { callbacks++; return null; }, readOptions: new() { MaxInputBytes = 32 },
            cancellationToken: Token));
        Assert.Equal(0, callbacks);
        Assert.Single(PmmAccessoryPlayback.Prepare(project, cancellationToken: Token).Assets);
    }
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
