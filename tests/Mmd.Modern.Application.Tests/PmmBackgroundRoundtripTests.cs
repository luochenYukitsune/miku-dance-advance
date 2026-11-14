using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmBackgroundRoundtripTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions ModelJson = new() { IncludeFields = true };
    private readonly string directory = Path.Combine(Path.GetTempPath(), "modern-pmm-background-" + Guid.NewGuid().ToString("N"));
    private readonly PmxModel model = EditedMotionProjectTests.Model();

    public PmmBackgroundRoundtripTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "actor.pmx"), PmxWriter.ToBytes(model));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedWhiteAndBlackBackgroundsSurviveSaveReadAndHostReconstruction(bool black)
    {
        using var project = CreateProject(Background(black));
        string path = Path.Combine(directory, "first.pmm");
        Assert.True(PmaPmmCompatibility.Inspect(project).CanExport);
        project.SavePmm(path, cancellationToken: Token);
        var read = Read(path);
        Assert.Equal(black, read.Settings.BlackBackgroundEnabled);

        // The formal host maps the native PMM switch to opaque RGB before constructing its owner.
        using var restored = CreateProject(Background(read.Settings.BlackBackgroundEnabled), read);
        Assert.Equal(project.Settings.Background, restored.Settings.Background);
        Assert.True(PmaPmmCompatibility.Inspect(restored).CanExport);
        string secondPath = Path.Combine(directory, "second.pmm");
        restored.SavePmm(secondPath, cancellationToken: Token);
        Assert.Equal(black, Read(secondPath).Settings.BlackBackgroundEnabled);
    }

    [Theory]
    [InlineData(.2f, .3f, .4f, 1f)]
    [InlineData(1f, 1f, 1f, .5f)]
    public void UnrepresentableRgbOrAlphaRejectsBeforeCreatingAFile(float red, float green, float blue, float alpha)
    {
        using var project = CreateProject(new()
        { BackgroundRed = red, BackgroundGreen = green, BackgroundBlue = blue, BackgroundAlpha = alpha });
        var report = PmaPmmCompatibility.Inspect(project);
        Assert.Contains(report.Issues, issue => issue.Code == "background");
        string path = Path.Combine(directory, "refused.pmm");
        Assert.Throws<PmaPmmExportException>(() => project.SavePmm(path, cancellationToken: Token));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedTemplateBackgroundCanChangeWithoutLosingUnrelatedSettingsOrMutatingItsSource(bool originalBlack)
    {
        using var initial = CreateProject(Background(originalBlack));
        var node = JsonSerializer.SerializeToNode(initial.ExportPmm(), ModelJson)!;
        var settings = node[nameof(PmmDocument.Settings)]!;
        settings[nameof(PmmSettings.HorizontalScroll)] = 31;
        settings[nameof(PmmSettings.HorizontalScrollThumb)] = 7;
        settings[nameof(PmmSettings.LoopEnabled)] = true;
        settings[nameof(PmmSettings.VideoOffsetX)] = 19;
        settings[nameof(PmmSettings.ImageOffsetY)] = -11;
        settings[nameof(PmmSettings.GridAndAxisShown)] = false;
        settings[nameof(PmmSettings.GroundShadowBrightness)] = .37f;
        node[nameof(PmmDocument.UnknownTail)] = new JsonArray(3, 1, 4);
        var template = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(node.Deserialize<PmmDocument>(ModelJson)!)), new() { AnsiCodePage = 932 });
        byte[] templateBytes = PmmWriter.ToBytes(template);

        using var retained = CreateProject(Background(!originalBlack) with { Width = 1280, Height = 720, FpsNumerator = 60 }, template);
        byte[] provenance = PmmWriter.ToBytes(retained.PmmControls!.ExportDocument());
        var result = retained.ExportPmm();
        Assert.Equal(!originalBlack, result.Settings.BlackBackgroundEnabled);
        Assert.Equal(1280, result.OutputWidth); Assert.Equal(720, result.OutputHeight);
        var expectedSettings = JsonSerializer.SerializeToNode(template.Settings, ModelJson)!;
        expectedSettings[nameof(PmmSettings.BlackBackgroundEnabled)] = !originalBlack;
        expectedSettings[nameof(PmmSettings.PreferredFps)] = 60f;
        Assert.True(JsonNode.DeepEquals(expectedSettings, JsonSerializer.SerializeToNode(result.Settings, ModelJson)));
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(template.Gravity, ModelJson), JsonSerializer.SerializeToNode(result.Gravity, ModelJson)));
        Assert.Equal(template.UnknownTail, result.UnknownTail);
        Assert.Equal(templateBytes, PmmWriter.ToBytes(template));
        Assert.Equal(provenance, PmmWriter.ToBytes(retained.PmmControls.ExportDocument()));

        string path = Path.Combine(directory, "retained.pmm");
        retained.SavePmm(path, cancellationToken: Token);
        Assert.Equal(!originalBlack, Read(path).Settings.BlackBackgroundEnabled);

        // Low-level callers without an explicit background override keep the native template switch.
        var actor = Assert.Single(retained.Objects);
        using var rawSession = new SceneAnimationSession(retained.Objects,
            [new(actor.Id, model, PmmMotionAdapter.Convert(template.Models[0], model), PhysicsEnabled: false)],
            _ => throw new InvalidOperationException("Fixture has no physics bodies."),
            cancellationToken: Token, pmmControls: retained.PmmControls);
        var unchanged = PmmSessionExporter.Export(rawSession);
        Assert.Equal(originalBlack, unchanged.Settings.BlackBackgroundEnabled);
        Assert.Equal(template.OutputWidth, unchanged.OutputWidth);
        Assert.Equal(template.Settings.PreferredFps, unchanged.Settings.PreferredFps);
        var backgroundOnly = PmmSessionExporter.Export(rawSession, new()
        { BlackBackgroundEnabled = !originalBlack, OutputWidth = 320, OutputHeight = 240, PreferredFps = 12 });
        Assert.Equal(!originalBlack, backgroundOnly.Settings.BlackBackgroundEnabled);
        Assert.Equal(template.OutputWidth, backgroundOnly.OutputWidth);
        Assert.Equal(template.OutputHeight, backgroundOnly.OutputHeight);
        Assert.Equal(template.Settings.PreferredFps, backgroundOnly.Settings.PreferredFps);
        using var newRawSession = new SceneAnimationSession(retained.Objects,
            [new(actor.Id, model, PmmMotionAdapter.Convert(template.Models[0], model), PhysicsEnabled: false)],
            _ => throw new InvalidOperationException("Fixture has no physics bodies."), cancellationToken: Token);
        Assert.False(PmmSessionExporter.Export(newRawSession).Settings.BlackBackgroundEnabled);
    }

    private SceneProjectSession CreateProject(ProjectSettings settings, PmmDocument? template = null)
    {
        var composition = new SceneCompositionEditor();
        var actor = composition.Add("actor", Path.Combine(directory, "actor.pmx"), ModelRole.Character, EditedMotionProjectTests.Rest(model));
        var motion = template is null ? new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }] }
            : PmmMotionAdapter.Convert(template.Models[0], model);
        var controls = template is null ? null : ScenePmmControlState.Create(template, [new(actor.Id, template.Models[0].Index, model)]);
        return new(composition, new(), [new(actor.Id, model, motion, PhysicsEnabled: false)],
            new NoPhysics(), settings, cancellationToken: Token, pmmControls: controls);
    }

    private static ProjectSettings Background(bool black) => new()
    { BackgroundRed = black ? 0 : 1, BackgroundGreen = black ? 0 : 1, BackgroundBlue = black ? 0 : 1 };

    private static PmmDocument Read(string path)
    { using var input = File.OpenRead(path); return PmmReader.Read(input, new() { AnsiCodePage = 932 }); }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
            => throw new InvalidOperationException("Fixture has no physics bodies.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
            => throw new InvalidOperationException("Fixture has no physics bodies.");
    }

    public void Dispose()
    {
        string root = Path.GetFullPath(directory), temp = Path.GetFullPath(Path.GetTempPath());
        if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid fixture directory.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
