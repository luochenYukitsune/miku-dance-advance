namespace Mmd.Modern.App;

public partial class EditorWindow
{
    /// <summary>OS file-open uses the same guarded project/import operations as the menu.</summary>
    internal async Task<bool> OpenAssociatedFileAsync(EditorFileOpenRequest request)
    {
        try
        {
            if (_shell is not { IsBusy: false } shell) return false;
            switch (request.Kind)
            {
                case EditorFileOpenKind.Project:
                    if (request.ProjectFormat != shell.Workspace.ProjectFormat)
                        throw new NotSupportedException(EditorLocalization.Text("Mda.Text.FileOpen.EngineMismatch"));
                    return await CheckUnsavedAsync() && await OpenProjectPathAsync(request.FullPath);
                case EditorFileOpenKind.Pmm:
                    return await CheckUnsavedAsync() && await OpenProjectPathAsync(request.FullPath);
                case EditorFileOpenKind.Model:
                    return await ImportModelAsync(request.FullPath);
                case EditorFileOpenKind.Motion:
                case EditorFileOpenKind.Pose:
                    if (shell.Browser.SelectedModel is null)
                    {
                        if (!await EditorDialogs.ConfirmAsync(this, EditorLocalization.Text("载入模型"),
                            EditorLocalization.Format("Mda.Text.FileOpen.NeedsModel", Path.GetFileName(request.FullPath)),
                            EditorLocalization.Text("载入模型"))) return false;
                        if (!await ImportModelAsync(null) || shell.Browser.SelectedModel is null) return false;
                    }
                    return request.Kind == EditorFileOpenKind.Motion
                        ? await ImportMotionAsync(request.FullPath)
                        : await ImportPoseAsync(request.FullPath);
                default: throw new ArgumentOutOfRangeException(nameof(request));
            }
        }
        catch (Exception error) { await ShowErrorAsync(error); }
        return false;
    }
}
