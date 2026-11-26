using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia.Threading;
using Mmd.Modern.App.Updates;

namespace Mmd.Modern.App;

internal sealed partial class EditorStartupApplication
{
    private StartupUpdateService updateService = new(StartupUpdateConfiguration.DefaultOptions, GetCurrentProductVersion());
    private IStartupUpdateUpgradeHandler? upgradeHandler;
    private bool explicitUpdateServiceSupplied;

    internal static void StartWithUpdates(Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop,
        StartupUpdateService updates, IStartupUpdateUpgradeHandler? upgrade = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        Current = new EditorStartupApplication(desktop)
        {
            updateService = updates,
            upgradeHandler = upgrade,
            explicitUpdateServiceSupplied = true
        };
    }

    private static string GetCurrentProductVersion() =>
        typeof(EditorStartupApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";

    private async Task<bool> ReviewStartupUpdateAsync(EditorPreferences preferences)
    {
        HttpClient? ownedHttpClient = null;
        var downloading = false;
        var userDeclinedInstall = false;
        try
        {
            StartupUpdateService activeService;
            IStartupUpdateUpgradeHandler? activeHandler;

            if (explicitUpdateServiceSupplied)
            {
                activeService = updateService;
                activeHandler = upgradeHandler;
            }
            else if (configuration is not null || !preferences.AutoCheckUpdates)
            {
                // Controlled/test hosts and users who disabled auto-check stay offline at startup.
                return true;
            }
            else
            {
                var productionOptions = StartupUpdateConfiguration.CreateProductionOptions();
                ownedHttpClient = new HttpClient(new HttpsOnlyRedirectHandler(), disposeHandler: true);
                activeService = new StartupUpdateService(
                    productionOptions,
                    GetCurrentProductVersion(),
                    () => new GitHubReleaseUpdateMetadataSource(ownedHttpClient, productionOptions),
                    new FileStartupUpdateSkipStore(Path.Combine(EditorUserDataPaths.Current.ConfigDirectory, "skipped-update.txt")));
                activeHandler = new StartupUpdateDownloadUpgradeHandler(
                    ownedHttpClient,
                    confirmInstaller: async (verified, cancellation) =>
                    {
                        Volatile.Write(ref downloading, false);
                        var consented = await Dispatcher.UIThread.InvokeAsync(
                            () => startup.ConfirmUpdateInstallationAsync(verified.Release, cancellation));
                        if (!consented) Volatile.Write(ref userDeclinedInstall, true);
                        return consented;
                    },
                    launchInstaller: StartVerifiedInstallerAsync,
                    resolveCandidateUrls: uri => GitHubReleaseUpdateMetadataSource.ResolveReleaseDownloadCandidates(
                        uri, productionOptions.DownloadMirrorBases));
            }

            if (!activeService.IsConfigured) return true;
            startup.SetUpdateMessage(EditorLocalization.Text("Mda.Text.Update.Checking"));
            var result = await activeService.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            if (result is not { Status: StartupUpdateStatus.Available, Release: { } release })
            {
                startup.ResetUpdateSurface();
                return true;
            }

            var choice = await startup.ReviewUpdateAsync(release, activeHandler is not null, token);
            token.ThrowIfCancellationRequested();
            if (choice == EditorStartupUpdateDecision.SkipVersion)
            {
                try { await activeService.SkipVersionAsync(release.Version, token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("MDA update skip could not be saved: {0}", error.GetType().Name);
                    startup.SetUpdateMessage(EditorLocalization.Text("Mda.Text.Update.SkipNotSaved"));
                }
            }
            else if (choice == EditorStartupUpdateDecision.Upgrade && activeHandler is { } handler)
            {
                startup.SetUpdateBusy(true);
                startup.SetUpdateMessage(EditorLocalization.Text("Mda.Text.Update.Upgrading"));
                Volatile.Write(ref downloading, true);
                var progress = new Progress<StartupUpdateProgress>(update =>
                {
                    if (closed || lifetimeExited || !Volatile.Read(ref downloading) || update.TotalBytes is not > 0) return;
                    void UpdateUi()
                    {
                        if (closed || lifetimeExited || !Volatile.Read(ref downloading) || update.TotalBytes is not > 0) return;
                        int percent = (int)Math.Clamp(update.DownloadedBytes * 100L / update.TotalBytes.Value, 0, 100);
                        double receivedMb = update.DownloadedBytes / (1024d * 1024d);
                        double totalMb = update.TotalBytes.Value / (1024d * 1024d);
                        startup.SetUpdateMessage(EditorLocalization.Format(
                            "Mda.Text.Update.Downloading",
                            percent,
                            receivedMb.ToString("0.0", CultureInfo.CurrentCulture),
                            totalMb.ToString("0.0", CultureInfo.CurrentCulture)));
                    }
                    if (Dispatcher.UIThread.CheckAccess()) UpdateUi();
                    else Dispatcher.UIThread.Post(UpdateUi);
                });

                try
                {
                    // Invoked only after the user chooses Upgrade; the handler verifies the package
                    // and awaits explicit installer confirmation before Process.Start runs.
                    var applied = await handler.UpgradeAsync(release, progress, token);
                    token.ThrowIfCancellationRequested();
                    if (applied.InstallerStarted)
                    {
                        desktop.Shutdown(0);
                        return false;
                    }

                    Volatile.Write(ref downloading, false);
                    if (!closed && !lifetimeExited) startup.SetUpdateBusy(false);
                    var declined = Volatile.Read(ref userDeclinedInstall)
                        || string.Equals(applied.Message, "Installer launch was declined.", StringComparison.Ordinal);
                    if (!declined && !closed && !lifetimeExited)
                    {
                        await startup.ShowUpdateFailureAsync(release, EditorLocalization.Text("Mda.Text.Update.Failed"), token);
                        token.ThrowIfCancellationRequested();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    Trace.TraceWarning("MDA update handoff failed: {0}", error.GetType().Name);
                    Volatile.Write(ref downloading, false);
                    if (!closed && !lifetimeExited)
                    {
                        startup.SetUpdateBusy(false);
                        await startup.ShowUpdateFailureAsync(release, EditorLocalization.Text("Mda.Text.Update.Failed"), token);
                        token.ThrowIfCancellationRequested();
                    }
                }
                finally
                {
                    Volatile.Write(ref downloading, false);
                    if (!closed && !lifetimeExited) startup.SetUpdateBusy(false);
                }
            }
            startup.ResetUpdateSurface();
            return true;
        }
        finally
        {
            ownedHttpClient?.Dispose();
        }
    }

    private static Task<bool> StartVerifiedInstallerAsync(VerifiedStartupUpdateInstaller verified, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo(verified.Path) { UseShellExecute = true });
        return Task.FromResult(process is not null);
    }
}
