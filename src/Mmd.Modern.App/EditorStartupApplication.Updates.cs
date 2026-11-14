using System.Reflection;
using Mmd.Modern.App.Updates;

namespace Mmd.Modern.App;

internal sealed partial class EditorStartupApplication
{
    // Composition seam only. No release host or installer is configured in this revision.
    // A future distribution can supply both through StartWithUpdates without changing the UI
    // or the editor/renderer startup path.
    private StartupUpdateService updateService = new(StartupUpdateConfiguration.DefaultOptions,
        typeof(EditorStartupApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0");
    private IStartupUpdateUpgradeHandler? upgradeHandler;

    internal static void StartWithUpdates(Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop,
        StartupUpdateService updates, IStartupUpdateUpgradeHandler? upgrade = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        Current = new EditorStartupApplication(desktop) { updateService = updates, upgradeHandler = upgrade };
    }

    private async Task<bool> ReviewStartupUpdateAsync()
    {
        // The ordinary unconfigured path completes immediately: no HTTP construction,
        // skip-file read, extra animation frame, timer or minimum splash duration.
        if (!updateService.IsConfigured) return true;
        startup.SetUpdateMessage(EditorLocalization.Text("Mda.Text.Update.Checking"));
        var result = await updateService.CheckAsync(token);
        token.ThrowIfCancellationRequested();
        if (result is not { Status: StartupUpdateStatus.Available, Release: { } release })
        {
            startup.ResetUpdateSurface();
            return true;
        }

        var choice = await startup.ReviewUpdateAsync(release, upgradeHandler is not null, token);
        token.ThrowIfCancellationRequested();
        if (choice == EditorStartupUpdateDecision.SkipVersion)
        {
            try { await updateService.SkipVersionAsync(release.Version, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("MDA update skip could not be saved: {0}", error.GetType().Name);
                startup.SetUpdateMessage(EditorLocalization.Text("Mda.Text.Update.SkipNotSaved"));
            }
        }
        else if (choice == EditorStartupUpdateDecision.Upgrade && upgradeHandler is { } handler)
        {
            startup.SetUpdateBusy(true);
            startup.SetUpdateMessage(EditorLocalization.Text("Mda.Text.Update.Upgrading"));
            try
            {
                // This interface is invoked only after the user chooses Upgrade. The future
                // handler owns package verification and an explicit installation confirmation.
                var applied = await handler.UpgradeAsync(release, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                if (applied.InstallerStarted)
                {
                    desktop.Shutdown(0);
                    return false;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("MDA update handoff failed: {0}", error.GetType().Name);
                startup.SetUpdateMessage(EditorLocalization.Text("Mda.Text.Update.Failed"));
            }
            finally { if (!closed && !lifetimeExited) startup.SetUpdateBusy(false); }
        }
        startup.ResetUpdateSurface();
        return true;
    }
}
