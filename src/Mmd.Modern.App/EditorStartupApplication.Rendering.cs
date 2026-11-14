using Avalonia.Threading;

namespace Mmd.Modern.App;

internal sealed partial class EditorStartupApplication
{
    internal static async Task<IEditorRenderEngine?> ChooseRenderEngineAsync(EditorStartupConfiguration? configuration,
        Func<IReadOnlyList<EditorRenderEngineChoice>, CancellationToken, Task<string?>> showSelection,
        CancellationToken token, EditorClassicRuntimeInstallation? classicInstallation = null,
        Func<CancellationToken, Task<EditorClassicToonResourceChoice>>? selectClassicResources = null,
        EditorProjectFormat? requiredProjectFormat = null)
    {
        ArgumentNullException.ThrowIfNull(showSelection);
        token.ThrowIfCancellationRequested();
        if (configuration is not null)
        {
            configuration.Validate();
            if (!configuration.PromptForRenderEngine)
            {
                if (requiredProjectFormat is not null && configuration.RenderEngine.Persistence.Format != requiredProjectFormat)
                    throw new NotSupportedException(EditorLocalization.Text("Mda.Text.FileOpen.EngineMismatch"));
                return configuration.RenderEngine;
            }
        }
        classicInstallation ??= await Task.Run(() => EditorClassicRuntimeInstallation.Inspect(AppContext.BaseDirectory), token);
        var choices = Array.AsReadOnly(new[]
        {
            new EditorRenderEngineChoice("modern", "Mda.Text.RenderEngineSelection.ModernName",
                "Mda.Text.RenderEngineSelection.ModernPurpose", requiredProjectFormat != EditorProjectFormat.Classic,
                requiredProjectFormat == EditorProjectFormat.Classic ? "Mda.Text.FileOpen.RequiresClassic" : null),
            new EditorRenderEngineChoice("compatibility", "Mda.Text.RenderEngineSelection.CompatibilityName",
                "Mda.Text.RenderEngineSelection.CompatibilityPurpose", classicInstallation.IsAvailable && requiredProjectFormat != EditorProjectFormat.Modern,
                requiredProjectFormat == EditorProjectFormat.Modern ? "Mda.Text.FileOpen.RequiresModern" : classicInstallation.UnavailableReason)
        });
        string? selected = await showSelection(choices, token);
        token.ThrowIfCancellationRequested();
        if (selected is null) return null;
        var choice = choices.FirstOrDefault(value => StringComparer.Ordinal.Equals(value.Id, selected));
        if (choice is not { IsAvailable: true })
            throw new NotSupportedException("The selected rendering engine is unavailable.");
        if (selected == "modern") return EditorModernRenderEngine.Instance;
        if (selectClassicResources is null)
            throw new NotSupportedException("Classic startup requires an explicit local original-resource selection.");
        var resourceChoice = await selectClassicResources(token);
        token.ThrowIfCancellationRequested();
        return !resourceChoice.Proceed ? null : classicInstallation.CreateEngine(
            configuration?.PackageCacheDirectory ?? EditorProjectWorkspace.CacheDirectory, resourceChoice.Resources);
    }

    private async Task<string?> ShowRenderEngineSelectionAsync(IReadOnlyList<EditorRenderEngineChoice> choices,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        startup.SetUpdateMessage(EditorLocalization.Text("Mda.Text.RenderEngineSelection.Title"));
        startup.StopAnimation(); // Waiting for the user is not background initialization work.
        var selection = new EditorRenderEngineSelectionWindow(choices);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => selection.Close()));
        try { return await selection.ShowDialog<string?>(startup); }
        finally { if (!closed && !lifetimeExited) startup.ResetUpdateSurface(); }
    }
}
