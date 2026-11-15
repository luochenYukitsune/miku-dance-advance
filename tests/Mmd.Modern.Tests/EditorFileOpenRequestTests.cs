using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Controls;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorFileOpenRequestTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private readonly string directory = Path.Combine(Mmd.Modern.TestSupport.TestWorkspace.Root("settings/20261113/associations648/unit"), Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(".pma", PmaProjectStore.Format)]
    [InlineData(".pmc", PmcProjectStore.Format)]
    public void NativeFileUsesValidatedPackageIdentityBeforeChoosingAnEngine(string extension, string format)
    {
        string path = Package("中文 空格" + extension, format);
        var request = EditorFileOpenRequest.Prepare(["--open", path], new(), TestContext.Current.CancellationToken)!;
        Assert.Equal(Path.GetFullPath(path), request.FullPath);
        Assert.Equal(EditorFileOpenKind.Project, request.Kind);
        Assert.Equal(format, request.ProjectFormat!.PackageFormat);
        Assert.False(Directory.Exists(Path.Combine(directory, "cache")));
    }

    [Fact]
    public void RenamingAClassicPackageDoesNotSelectTheModernEngine()
    {
        string path = Package("renamed.pma", PmcProjectStore.Format);
        Assert.Throws<NotSupportedException>(() => EditorFileOpenRequest.Prepare([path], new(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClassicFileCannotConfirmModernOrSilentlyFallbackWhenComponentIsAbsent()
    {
        var absent = EditorClassicRuntimeInstallation.Inspect(directory);
        IReadOnlyList<EditorRenderEngineChoice>? choices = null;
        var result = await EditorStartupApplication.ChooseRenderEngineAsync(null,
            (offered, _) => { choices = offered; return Task.FromResult<string?>(null); }, TestContext.Current.CancellationToken,
            absent, requiredProjectFormat: EditorProjectFormat.Classic);
        Assert.Null(result);
        Assert.False(Assert.Single(choices!, value => value.Id == "modern").IsAvailable);
        Assert.False(Assert.Single(choices!, value => value.Id == "compatibility").IsAvailable);
        await Assert.ThrowsAsync<NotSupportedException>(() => EditorStartupApplication.ChooseRenderEngineAsync(null,
            (_, _) => Task.FromResult<string?>("modern"), TestContext.Current.CancellationToken,
            absent, requiredProjectFormat: EditorProjectFormat.Classic));
    }

    [Theory]
    [InlineData(".pmm", "Pmm")]
    [InlineData(".pmx", "Model")]
    [InlineData(".pmd", "Model")]
    [InlineData(".vmd", "Motion")]
    [InlineData(".vpd", "Pose")]
    public void OptionalTypesReachTheExistingImportKind(string extension, string kind)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "input" + extension);
        File.WriteAllText(path, "Existing format importer validates contents.");
        var request = EditorFileOpenRequest.Prepare([path], new(), TestContext.Current.CancellationToken)!;
        Assert.Equal(kind, request.Kind.ToString());
        Assert.Null(request.ProjectFormat);
    }

    [Fact]
    public async Task InvalidArgumentsOrMissingFilesDoNotStartAnEmptySuccessfulOpen() => await session.Dispatch(() =>
    {
        Assert.Null(EditorFileOpenRequest.Prepare([], new(), TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => EditorFileOpenRequest.Prepare(["--open"], new(), TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => EditorFileOpenRequest.Prepare(["a.pma", "b.pma"], new(), TestContext.Current.CancellationToken));
        Assert.Throws<FileNotFoundException>(() => EditorFileOpenRequest.Prepare([Path.Combine(directory, "missing.pmc")], new(), TestContext.Current.CancellationToken));
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact(Timeout = 30000)]
    public async Task AssociatedNativeFileActuallyOpensInTheExistingEditorOperation() => await session.Dispatch(async () =>
    {
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "model.pmx");
        var model = EditorRecoveryPmaIntegrationTests.Model();
        File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var preferences = new EditorPreferences { AutoSaveEnabled = false, RecoveryDirectory = Path.Combine(directory, "recovery") };
        using var shell = new EditorShellViewModel();
        shell.Workspace.PackageCacheDirectory = Path.Combine(directory, "cache");
        shell.Workspace.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source, token), ModelRole.Prop);
        string package = Path.Combine(directory, "双击 打开.pma");
        shell.Workspace.Save(package, token);
        shell.Workspace.New();
        var window = new EditorWindow(false, preferences);
        window.FindControl<EditorViewportHost>("SceneViewport")!.IsVisible = false;
        window.DataContext = shell;
        try
        {
            var request = EditorFileOpenRequest.Prepare(["--open", package], preferences, token)!;
            await window.OpenAssociatedFileAsync(request).WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.Equal(Path.GetFullPath(package), shell.Workspace.ProjectPath);
            Assert.Single(shell.Workspace.Motions);
            Assert.False(shell.Workspace.IsDirty);
        }
        finally { window.DataContext = null; window.Close(); }
        return true;
    }, TestContext.Current.CancellationToken);

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
                format, version = 1, minimumReaderVersion = 1, producer = "association-route-test", projectId = Guid.NewGuid(),
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
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
