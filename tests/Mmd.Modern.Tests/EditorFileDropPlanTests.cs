using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorFileDropPlanTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private readonly string directory = Path.Combine(MdaWorkRoot.Path("editor/20261113/drop688/unit"), Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DragHintOnlyChecksBoundedSupportedPathsWithoutReadingFiles()
    {
        string[] extensions = [".pma", ".PMC", ".pmm", ".pmx", ".pmd", ".vmd", ".vpd"];
        Assert.All(extensions, extension => Assert.True(EditorFileDropPlan.CanAcceptPaths([Path.Combine(directory, "missing" + extension)])));
        Assert.True(EditorFileDropPlan.CanAcceptPaths(Enumerable.Repeat("missing.pmx", 32).ToArray()));
        Assert.False(EditorFileDropPlan.CanAcceptPaths([]));
        Assert.False(EditorFileDropPlan.CanAcceptPaths(Enumerable.Repeat("missing.pmx", 33).ToArray()));
        Assert.False(EditorFileDropPlan.CanAcceptPaths([" "]));
        Assert.False(EditorFileDropPlan.CanAcceptPaths(["model.pmx", "plugin.zip"]));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void AssetBatchDeduplicatesNormalizedPathsAndMovesModelsBeforeMotionAndPose()
    {
        string motion = Asset("first.vmd"), model = Asset("second.pmx"), pose = Asset("third.vpd"), otherModel = Asset("fourth.pmd"), otherMotion = Asset("fifth.vmd");
        var paths = new List<string> { motion, model, pose, otherModel, otherMotion, Path.Combine(directory, ".", "second.pmx") };
        if (OperatingSystem.IsWindows()) paths.Add(model.ToUpperInvariant());
        var plan = EditorFileDropPlan.Prepare(paths, new(), EditorProjectFormat.Modern, Token);

        Assert.Equal(new[] { model, otherModel, motion, pose, otherMotion }.Select(Path.GetFullPath), plan.Requests.Select(request => request.FullPath));
        Assert.Equal(new[] { EditorFileOpenKind.Model, EditorFileOpenKind.Model, EditorFileOpenKind.Motion, EditorFileOpenKind.Pose, EditorFileOpenKind.Motion },
            plan.Requests.Select(request => request.Kind));
        paths.Clear();
        Assert.Equal(5, plan.Requests.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<EditorFileOpenRequest>)plan.Requests).Clear());
    }

    [Theory]
    [InlineData("first.pma", "second.pmx")]
    [InlineData("first.pma", "second.pmc")]
    [InlineData("first.pmm", "second.vmd")]
    public void ProjectOrPmmCannotBeCombinedWithAnotherFileBeforeAnyRead(string first, string second)
    {
        var error = Assert.Throws<EditorLocalizedArgumentException>(() => EditorFileDropPlan.Prepare(
            [Path.Combine(directory, first), Path.Combine(directory, second)], new(), EditorProjectFormat.Modern, Token));
        Assert.Equal(EditorLocalization.Text("Mda.Text.FileDrop.ProjectAlone"), error.Message);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void RepeatedProjectPathIsOneRequestAndPmmUsesTheExistingKind()
    {
        string project = Package("single.pma", PmaProjectStore.Format);
        var plan = EditorFileDropPlan.Prepare([project, Path.Combine(directory, ".", "single.pma")], new(), EditorProjectFormat.Modern, Token);
        Assert.Equal(EditorProjectFormat.Modern, Assert.Single(plan.Requests).ProjectFormat);
        string pmm = Asset("single.pmm");
        Assert.Equal(EditorFileOpenKind.Pmm, Assert.Single(EditorFileDropPlan.Prepare([pmm], new(), EditorProjectFormat.Classic, Token).Requests).Kind);
    }

    [Theory]
    [InlineData(".pma", PmaProjectStore.Format)]
    [InlineData(".pmc", PmcProjectStore.Format)]
    public void NativePackageIdentityMustMatchTheCurrentEngine(string extension, string format)
    {
        string project = Package("native" + extension, format);
        var expected = extension == ".pma" ? EditorProjectFormat.Modern : EditorProjectFormat.Classic;
        var other = extension == ".pma" ? EditorProjectFormat.Classic : EditorProjectFormat.Modern;
        var request = Assert.Single(EditorFileDropPlan.Prepare([project], new(), expected, Token).Requests);
        Assert.Equal(format, request.ProjectFormat!.PackageFormat);
        var error = Assert.Throws<NotSupportedException>(() => EditorFileDropPlan.Prepare([project], new(), other, Token));
        Assert.Equal(EditorLocalization.Text("Mda.Text.FileOpen.EngineMismatch"), error.Message);
        Assert.False(Directory.Exists(Path.Combine(directory, "cache")));
    }

    [Fact]
    public void NativePreflightKeepsPackageIdentityAndBudgetChecks()
    {
        string renamed = Package("renamed.pma", PmcProjectStore.Format);
        Assert.Throws<NotSupportedException>(() => EditorFileDropPlan.Prepare([renamed], new(), EditorProjectFormat.Modern, Token));
        string project = Package("bounded.pma", PmaProjectStore.Format);
        Assert.Throws<InvalidDataException>(() => EditorFileDropPlan.Prepare([project], new() { PmaPackageMaxBytes = 1 }, EditorProjectFormat.Modern, Token));
    }

    [Fact]
    public void InvalidOrMissingInputsAndCancellationCannotReturnAPartialPlan()
    {
        var error = Assert.Throws<EditorLocalizedArgumentException>(() => EditorFileDropPlan.Prepare([], new(), EditorProjectFormat.Modern, Token));
        Assert.Equal(EditorLocalization.Text("Mda.Text.FileDrop.InvalidSelection"), error.Message);
        string model = Asset("present.pmx");
        Assert.Throws<FileNotFoundException>(() => EditorFileDropPlan.Prepare([model, Path.Combine(directory, "missing.vmd")], new(), EditorProjectFormat.Modern, Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => EditorFileDropPlan.Prepare([model], new(), EditorProjectFormat.Modern, cancelled.Token));
    }

    private string Asset(string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "The existing importer validates asset contents.");
        return path;
    }

    private string Package(string name, string format)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        byte[] project = Encoding.UTF8.GetBytes("{}");
        string hash = Convert.ToHexStringLower(SHA256.HashData(project));
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var stream = zip.CreateEntry("manifest.json").Open())
            JsonSerializer.Serialize(stream, new
            {
                format, version = 1, minimumReaderVersion = 1, producer = "file-drop-plan-test", projectId = Guid.NewGuid(),
                projectPath = "project.mmdscene", projectSchema = SceneAnimationProjectStore.Version,
                files = new[] { new { path = "project.mmdscene", length = project.Length, sha256 = hash } },
                models = Array.Empty<object>(), requiredExtensions = Array.Empty<string>(), optionalExtensions = new { }
            });
        using (var stream = zip.CreateEntry("blobs/" + hash).Open()) stream.Write(project);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Run(session.Dispose).ConfigureAwait(false);
        string root = Path.GetFullPath(MdaWorkRoot.Path("editor/20261113/drop688/unit")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string owned = Path.GetFullPath(directory);
        if (!owned.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Drop-plan test cleanup escaped its owned directory.");
        if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
    }
}
