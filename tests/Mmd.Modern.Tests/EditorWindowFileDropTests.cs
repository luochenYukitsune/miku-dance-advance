using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorWindowFileDropTests : IAsyncDisposable
{
    private const string Root = "G:/MDA-Work/editor/20261113/drop688";
    private readonly string directory = Path.Combine(Root, "ui-" + Guid.NewGuid().ToString("N"));
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    [Fact]
    public async Task RawFileDropActuallyOpensANativeProjectThroughTheExistingEditorFlow()
        => await session.Dispatch(async () =>
        {
            string package = CreatePackage();
            var (window, shell) = CreateEditor();
            using (shell)
            try
            {
                Guid emptyDocument = shell.Workspace.DocumentId;
                using var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(package));
                Assert.NotNull(file);
                var data = new DataTransfer();
                data.Add(DataTransferItem.Create(DataFormat.File, file));
                SendRawDrop(window, data);
                await UntilAsync(() => shell.Workspace.ProjectPath == Path.GetFullPath(package)
                    && !window.IsFileDropInProgress);

                Assert.NotEqual(emptyDocument, shell.Workspace.DocumentId);
                var actor = Assert.Single(shell.Workspace.Motions);
                Assert.Equal(23u, Assert.Single(actor.Motion.BoneKeyframes).Frame);
                Assert.False(shell.Workspace.IsDirty);
                Assert.False(shell.IsBusy);
                Assert.Empty(window.OwnedWindows);
                Assert.True(window.FindControl<EditorViewportHost>("SceneViewport")!.IsSuspended);
            }
            finally { CloseEditor(window); }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(25), Token);

    [Fact]
    public async Task CancelingTheFirstModelPurposeStopsAReorderedModelAndMotionBatch()
        => await session.Dispatch(async () =>
        {
            var (window, shell) = CreateEditor();
            using (shell)
            try
            {
                Guid id = AddModel(shell, "existing.pmx");
                shell.Workspace.CommitMotion(id, new()
                {
                    ModelName = "existing",
                    BoneKeyframes = [new() { BoneName = "root", Frame = 17 }]
                });
                shell.RefreshWorkspace(id);
                string model = WriteModel("new-model.pmx");
                string motion = Path.Combine(directory, "must-not-be-read.vmd");
                File.WriteAllText(motion, "An invalid VMD proves cancellation never reaches the next importer.");
                var beforeSession = shell.Workspace.Session;
                Guid beforeDocument = shell.Workspace.DocumentId;
                byte[] beforeMotion = VmdWriter.ToBytes(Assert.Single(shell.Workspace.Motions).Motion);
                long beforeRevision = shell.ContentRevision;

                // Explorer can supply the motion before its model. A model must be
                // offered first, without overwriting the currently selected actor.
                Task<bool> import = window.ImportDroppedFilesAsync([motion, model]);
                var purpose = await WaitForDialogAsync(window);
                Assert.NotNull(purpose.GetVisualDescendants().OfType<Button>()
                    .SingleOrDefault(button => button.Name == "ImportModelPurposeButton"));
                Assert.Contains(purpose.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == Path.GetFileName(model));
                Assert.True(window.IsFileDropInProgress);
                Assert.False(await window.ImportDroppedFilesAsync([model]).WaitAsync(Timeout, Token));
                window.Close();
                Assert.True(window.IsVisible);
                Assert.True(purpose.IsVisible);
                Assert.Same(purpose, Assert.Single(window.OwnedWindows));

                ClickCancel(purpose);
                Assert.False(await import.WaitAsync(Timeout, Token));
                Dispatcher.UIThread.RunJobs();

                Assert.False(window.IsFileDropInProgress);
                Assert.Same(beforeSession, shell.Workspace.Session);
                Assert.Equal(beforeDocument, shell.Workspace.DocumentId);
                Assert.Equal(beforeRevision, shell.ContentRevision);
                var actor = Assert.Single(shell.Workspace.Motions);
                Assert.Equal(id, actor.ObjectId);
                Assert.Equal(beforeMotion, VmdWriter.ToBytes(actor.Motion));
                Assert.Equal(id, shell.Browser.SelectedModel!.ObjectId);
                Assert.Empty(window.OwnedWindows);
                Assert.False(shell.IsBusy);
            }
            finally { CloseEditor(window); }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(25), Token);

    [Fact]
    public async Task ReorderedModelAndMotionBatchCommitsToTheNewlySelectedActor()
        => await session.Dispatch(async () =>
        {
            var (window, shell) = CreateEditor();
            using (shell)
            try
            {
                Guid existingId = AddModel(shell, "existing.pmx");
                shell.Workspace.CommitMotion(existingId, new()
                {
                    ModelName = "existing",
                    BoneKeyframes = [new() { BoneName = "root", Frame = 17 }]
                });
                shell.RefreshWorkspace(existingId);
                byte[] existingMotion = VmdWriter.ToBytes(Assert.Single(shell.Workspace.Motions).Motion);
                Guid document = shell.Workspace.DocumentId;
                string model = WriteModel("new-model.pmx");
                string motion = Path.Combine(directory, "new-model-motion.vmd");
                File.WriteAllBytes(motion, VmdWriter.ToBytes(new()
                {
                    ModelName = EditorRecoveryPmaIntegrationTests.Model().Name,
                    BoneKeyframes = [new() { BoneName = "root", Frame = 31, Translation = new(2, 3, 4) }]
                }));

                Task<bool> import = window.ImportDroppedFilesAsync([motion, model]);
                var purpose = await WaitForDialogAsync(window);
                Assert.Contains(purpose.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == Path.GetFileName(model));
                // The actual dialog defines Prop as its fourth purpose option.
                var prop = Assert.Single(purpose.GetVisualDescendants().OfType<RadioButton>(),
                    button => button.Name == "ModelImportPurpose3");
                Assert.Equal(EditorLocalization.Text("道具"), AutomationProperties.GetName(prop));
                prop.IsChecked = true;
                var apply = Assert.Single(purpose.GetVisualDescendants().OfType<Button>(),
                    button => button.Name == "ImportModelPurposeButton");
                Assert.True(apply.IsEffectivelyEnabled);
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(await import.WaitAsync(Timeout, Token));
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(document, shell.Workspace.DocumentId);
                Assert.Equal(2, shell.Workspace.Motions.Count);
                var original = Assert.Single(shell.Workspace.Motions, actor => actor.ObjectId == existingId);
                Assert.Equal(existingMotion, VmdWriter.ToBytes(original.Motion));
                var added = Assert.Single(shell.Workspace.Motions, actor => actor.ObjectId != existingId);
                Assert.Equal(added.ObjectId, shell.Browser.SelectedModel!.ObjectId);
                Assert.Equal(ModelRole.Prop, shell.Workspace.Session!.GetModelPurpose(added.ObjectId).Role);
                var keyframe = Assert.Single(added.Motion.BoneKeyframes);
                Assert.Equal("root", keyframe.BoneName);
                Assert.Equal(31u, keyframe.Frame);
                Assert.Equal(2, keyframe.Translation.X);
                Assert.Equal(3, keyframe.Translation.Y);
                Assert.Equal(4, keyframe.Translation.Z);
                Assert.True(shell.Workspace.IsDirty);
                Assert.False(window.IsFileDropInProgress);
                Assert.False(shell.IsBusy);
                Assert.Empty(window.OwnedWindows);
            }
            finally { CloseEditor(window); }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(25), Token);

    [Fact]
    public async Task CancelingUnsavedChangesAfterARawProjectDropKeepsTheCurrentDocument()
        => await session.Dispatch(async () =>
        {
            string package = CreatePackage();
            var (window, shell) = CreateEditor();
            using (shell)
            try
            {
                Guid id = AddModel(shell, "unsaved.pmx");
                var beforeSession = shell.Workspace.Session;
                Guid beforeDocument = shell.Workspace.DocumentId;
                Assert.True(shell.Workspace.IsDirty);
                Assert.Null(shell.Workspace.ProjectPath);
                using var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(package));
                Assert.NotNull(file);
                var data = new DataTransfer();
                data.Add(DataTransferItem.Create(DataFormat.File, file));
                SendRawDrop(window, data);

                var unsaved = await WaitForDialogAsync(window);
                Assert.Equal(3, unsaved.GetVisualDescendants().OfType<Button>().Count());
                Assert.True(window.IsFileDropInProgress);
                ClickCancel(unsaved);
                await UntilAsync(() => !window.IsFileDropInProgress);

                Assert.Same(beforeSession, shell.Workspace.Session);
                Assert.Equal(beforeDocument, shell.Workspace.DocumentId);
                Assert.Equal(id, Assert.Single(shell.Workspace.Motions).ObjectId);
                Assert.Null(shell.Workspace.ProjectPath);
                Assert.True(shell.Workspace.IsDirty);
                Assert.False(shell.IsBusy);
                Assert.Empty(window.OwnedWindows);
            }
            finally { CloseEditor(window); }
            return true;
        }, Token).WaitAsync(TimeSpan.FromSeconds(25), Token);

    private (EditorWindow Window, EditorShellViewModel Shell) CreateEditor()
    {
        Directory.CreateDirectory(directory);
        var preferences = new EditorPreferences
        {
            AutoSaveEnabled = false,
            RecoveryDirectory = Path.Combine(directory, "recovery")
        };
        var window = new EditorWindow(false, preferences);
        var viewport = window.FindControl<EditorViewportHost>("SceneViewport")!;
        viewport.IsVisible = false;
        viewport.IsSuspended = true;
        var shell = new EditorShellViewModel();
        shell.Workspace.PackageCacheDirectory = Path.Combine(directory, "editor-cache");
        window.DataContext = shell;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private string CreatePackage()
    {
        using var workspace = new EditorProjectWorkspace
        {
            PackageCacheDirectory = Path.Combine(directory, "package-cache")
        };
        string source = WriteModel("packaged-model.pmx");
        var model = EditorRecoveryPmaIntegrationTests.Model();
        Guid id = workspace.AddModel(source, EditorProjectWorkspace.CreateModelAsset(model, source, Token), ModelRole.Prop);
        workspace.CommitMotion(id, new()
        {
            ModelName = "packaged",
            BoneKeyframes = [new() { BoneName = "root", Frame = 23 }]
        });
        string package = Path.Combine(directory, "dropped project.pma");
        workspace.Save(package, Token);
        return package;
    }

    private Guid AddModel(EditorShellViewModel shell, string name)
    {
        string path = WriteModel(name);
        Guid id = shell.Workspace.AddModel(path,
            EditorProjectWorkspace.CreateModelAsset(EditorRecoveryPmaIntegrationTests.Model(), path, Token), ModelRole.Prop);
        shell.RefreshWorkspace(id);
        return id;
    }

    private string WriteModel(string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, PmxWriter.ToBytes(EditorRecoveryPmaIntegrationTests.Model()));
        return path;
    }

    private static void SendRawDrop(EditorWindow window, DataTransfer data)
    {
        window.UpdateLayout();
        var surface = window.FindControl<Button>("SettingsButton")!;
        Point point = surface.TranslatePoint(new Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2), window)!.Value;
        window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
        window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy);
        window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy);
    }

    private static async Task<Window> WaitForDialogAsync(EditorWindow owner)
    {
        await UntilAsync(() => owner.OwnedWindows.Any(window => window.IsVisible));
        return Assert.Single(owner.OwnedWindows, window => window.IsVisible);
    }

    private static void ClickCancel(Window dialog)
    {
        var cancel = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => button.IsCancel);
        Assert.True(cancel.IsEffectivelyEnabled);
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(Timeout);
        while (!ready())
        {
            await Task.Delay(10, timeout.Token);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void CloseEditor(EditorWindow window)
    {
        window.DataContext = null;
        foreach (var child in window.OwnedWindows.ToArray()) child.Close();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    public async ValueTask DisposeAsync()
    {
        try { await Task.Run(session.Dispose).ConfigureAwait(false); }
        finally
        {
            string fullRoot = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullDirectory = Path.GetFullPath(directory);
            if (!fullDirectory.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("File-drop test cleanup escaped its owned evidence directory.");
            if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, recursive: true);
        }
    }
}
