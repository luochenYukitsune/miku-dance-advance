using System.Numerics;
using Mmd.Modern.Demo;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class FrameViewportPmmV1Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "viewport-pmm-v1-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(root, "scene.pmm");
    public FrameViewportPmmV1Tests()
    {
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "pmm-v1-one-model.pmm"), Project);
        var pmd = new PmdModel("model",
            [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero, 0, 0, 1), new(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero, 0, 0, 1), new(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero, 0, 0, 1)],
            [0, 1, 2], [new(Vector4.One, Vector3.Zero, Vector3.Zero, 1, "", 0, 3)], [new("bone", -1, Vector3.Zero)], 0)
        {
            HasAnimationTail = true, BoneDisplayFrames = [new("\u30bb\u30f3\u30bf\u30fc", [])],
            Morphs = [new("morph", 0, [])], IkChains = [new(0, 0, 1, .5f, [0])]
        };
        File.WriteAllBytes(Path.Combine(root, "character.pmd"), PmdWriter.ToBytes(pmd));
    }
    [Fact]
    public void V1CommitUsesViewportShadowDefaultAndKeepsRawMetadataAndActiveProjectOnFailure()
    {
        using var vm = new FrameViewportViewModel(FrameViewportOptions.Parse(["--frame-viewer"]));
        vm.SelfShadowEnabled = false;
        using var candidate = vm.PrepareProject(Project, cancellationToken: TestContext.Current.CancellationToken);
        vm.CommitProject(candidate);
        Assert.True(vm.HasProject); Assert.True(vm.SelfShadowEnabled);
        Assert.False(vm.ProjectDocument!.SelfShadow.Enabled); Assert.Equal(1, vm.ProjectDocument.Version);
        Assert.Contains("PMMv1", vm.ProjectSummary);
        Assert.Equal(File.ReadAllBytes(Project), PmmWriter.ToBytes(vm.ProjectDocument));
        var active = vm.ProjectDocument;
        File.Move(Path.Combine(root, "character.pmd"), Path.Combine(root, "hidden.pmd"));
        Assert.Throws<InvalidDataException>(() => vm.PrepareProject(Project, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Same(active, vm.ProjectDocument); Assert.True(vm.SelfShadowEnabled);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V2CommitContinuesToUseStoredShadowControl(bool enabled)
    {
        var original = PmmProjectReader.Read(Project, cancellationToken: TestContext.Current.CancellationToken);
        var v2 = new PmmDocument
        {
            Version = 2, AnsiCodePage = 936, OutputWidth = original.OutputWidth, OutputHeight = original.OutputHeight,
            CameraFov = original.CameraFov, PanelStates = new byte[7], Models = original.Models,
            Camera = original.Camera, Light = original.Light, CurrentFrame = original.CurrentFrame,
            Settings = new() { EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64],
                CameraLookAtModelIndex = -1, CameraLookAtBoneIndex = -1, PreferredFps = 30 },
            SelfShadow = new() { Enabled = enabled }
        };
        File.WriteAllBytes(Project, PmmWriter.ToBytes(v2, new() { PreserveOriginalStringBytes = false }));
        using var vm = new FrameViewportViewModel(FrameViewportOptions.Parse(["--frame-viewer"]));
        vm.SelfShadowEnabled = !enabled;
        using var candidate = vm.PrepareProject(Project, cancellationToken: TestContext.Current.CancellationToken);
        vm.CommitProject(candidate);
        Assert.Equal(enabled, vm.SelfShadowEnabled); Assert.Equal(enabled, vm.ProjectDocument!.SelfShadow.Enabled);
        Assert.Contains("PMMv2", vm.ProjectSummary);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
