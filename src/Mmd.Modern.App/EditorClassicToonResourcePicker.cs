using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Mmd.Modern.App;

/// <summary>One explicit local-resource selection. No saved preference or automatic path probing.</summary>
internal static class EditorClassicToonResourcePicker
{
    internal static async Task<EditorClassicToonResourceChoice> ChooseForStartupAsync(Window owner, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        int choice = await EditorDialogs.ChooseAsync(owner, EditorLocalization.Text("Mda.Text.Classic.Resources.Title"),
            EditorLocalization.Text("Mda.Text.Classic.Resources.StartupPrompt"),
            [EditorLocalization.Text("Mda.Text.Classic.Resources.ChooseLocal"),
                EditorLocalization.Text("Mda.Text.Classic.Resources.PortableOnly"), EditorLocalization.Text("取消")]);
        token.ThrowIfCancellationRequested();
        if (choice == 1) return new(true, null);
        if (choice != 0) return new(false, null);
        var resources = await SelectAsync(owner, token);
        return new(resources is not null, resources);
    }

    internal static async Task<EditorClassicToonResources?> SelectAsync(Window owner, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        token.ThrowIfCancellationRequested();
        if (!owner.StorageProvider.CanPickFolder)
            throw new NotSupportedException(EditorLocalization.Text("Mda.Text.Classic.Resources.PickerUnavailable"));
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = EditorLocalization.Text("Mda.Text.Classic.Resources.PickerTitle"),
            AllowMultiple = false
        });
        token.ThrowIfCancellationRequested();
        if (folders.FirstOrDefault() is not { } selected) return null;
        if (selected.TryGetLocalPath() is not { } path)
            throw new NotSupportedException(EditorLocalization.Text("Mda.Text.Classic.Resources.LocalFolderRequired"));
        return await Task.Run(() => EditorClassicToonResources.Capture(path, token), token);
    }
}

internal sealed record EditorClassicToonResourceChoice(bool Proceed, EditorClassicToonResources? Resources);
