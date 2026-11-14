using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmProjectImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pmm-import-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AllModelsArePreparedBeforeCommitAndAnyMissingReferenceBlocksCommit()
    {
        Directory.CreateDirectory(root);
        string modelPath = Path.Combine(root, "model.pmx");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"), modelPath);
        var good = new PmmModel { Index = 7, Path = new(0, "model.pmx", []), Visible = true, InitialModelKeyframe = new() { Visible = true } };
        var absent = new PmmModel { Index = 19, Path = new(0, "missing.pmx", []) };
        var document = new PmmDocument { Models = [good, absent], CurrentFrame = 1271, OutputWidth = 1920, OutputHeight = 1080 };
        var plan = PmmProjectImport.Prepare(document, Path.Combine(root, "project.pmm"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(plan.CanCommit);
        Assert.Same(document, plan.Document);
        Assert.Equal(1271, plan.CurrentFrame);
        Assert.Equal(1920, plan.OutputWidth);
        Assert.Equal(1080, plan.OutputHeight);
        Assert.Equal(modelPath, Assert.Single(plan.Models).Path);
        Assert.Equal(19, Assert.Single(plan.UnresolvedAssets).ModelIndex);
        Assert.Same(good, plan.Models[0].Source);
        Assert.True(PmmProjectImport.Prepare(new() { Models = [good] }, Path.Combine(root, "project.pmm"), cancellationToken: TestContext.Current.CancellationToken).CanCommit);
    }

    [Fact]
    public void InvalidModelAndCancellationNeverProduceACommitReadyCandidate()
    {
        Directory.CreateDirectory(root);
        string broken = Path.Combine(root, "broken.pmx");
        File.WriteAllText(broken, "broken");
        var document = new PmmDocument { Models = [new() { Path = new(0, broken, []) }] };
        var plan = PmmProjectImport.Prepare(document, Path.Combine(root, "project.pmm"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(plan.CanCommit);
        Assert.Equal(PmmAssetResolutionStatus.Invalid, Assert.Single(plan.UnresolvedAssets).Resolution.Status);
        Assert.NotEmpty(plan.UnresolvedAssets[0].Error!);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PmmProjectImport.Prepare(document, Path.Combine(root, "project.pmm"), cancellationToken: cancellation.Token));
    }

    [Fact]
    public void SavedPoseRetainsUnregisteredChangesSeparatelyFromTimeline()
    {
        var pmm = new PmmModel
        {
            BoneNames = [new(0, "root", [])],
            InitialBoneKeyframes = [new() { Translation = System.Numerics.Vector3.Zero, Rotation = System.Numerics.Quaternion.Identity, Interpolation = Enumerable.Repeat((byte)20, 16).ToArray() }],
            BoneStates = [new(0, new(10, 20, 30), System.Numerics.Quaternion.Identity, true, false, 0)],
        };
        var model = PmmMotionAdapterTests.Pmx(["root"]);
        Assert.Equal(System.Numerics.Vector3.Zero, Assert.Single(PmmMotionAdapter.Convert(pmm, model).BoneKeyframes).Translation);
        Assert.Equal(new(10, 20, 30), Assert.Single(PmmMotionAdapter.ConvertSavedPose(pmm, model).BoneKeyframes).Translation);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
