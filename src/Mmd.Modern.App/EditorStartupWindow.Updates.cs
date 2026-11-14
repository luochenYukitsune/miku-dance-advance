using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.App.Controls;
using Mmd.Modern.App.Updates;

namespace Mmd.Modern.App;

internal enum EditorStartupUpdateDecision { Continue, SkipVersion, Upgrade }

public sealed partial class EditorStartupWindow
{
    private TaskCompletionSource<EditorStartupUpdateDecision>? updateReview;
    private CancellationTokenRegistration updateReviewCancellation;
    private CancellationToken updateReviewToken;
    private StartupUpdateRelease? offeredUpdate;
    private bool updateHooksInstalled;
    private bool updateCanUpgrade;
    private bool updateIsBusy;
    private bool updateIsInstallConfirmation;
    private string? updateMessage;

    /// <summary>Wait for an explicit choice on this startup surface; never launch external content.</summary>
    internal Task<EditorStartupUpdateDecision> ReviewUpdateAsync(StartupUpdateRelease release, bool canUpgrade,
        CancellationToken cancellationToken) => BeginUpdateReview(release, canUpgrade, false, cancellationToken);

    /// <summary>Request explicit consent after download and verification; this surface never starts an installer.</summary>
    internal Task<bool> ConfirmUpdateInstallationAsync(StartupUpdateRelease release, CancellationToken cancellationToken) =>
        AwaitInstallationConsentAsync(BeginUpdateReview(release, true, true, cancellationToken), cancellationToken);

    private async Task<bool> AwaitInstallationConsentAsync(Task<EditorStartupUpdateDecision> review, CancellationToken cancellationToken)
    {
        var decision = await review;
        cancellationToken.ThrowIfCancellationRequested();
        return decision == EditorStartupUpdateDecision.Upgrade && !windowClosed;
    }

    private Task<EditorStartupUpdateDecision> BeginUpdateReview(StartupUpdateRelease release, bool canUpgrade,
        bool installConfirmation, CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(release);
        cancellationToken.ThrowIfCancellationRequested();
        if (windowClosed) return Task.FromCanceled<EditorStartupUpdateDecision>(new CancellationToken(true));
        if (updateReview is not null) throw new InvalidOperationException("An update choice is already pending.");

        if (!updateHooksInstalled)
        {
            updateHooksInstalled = true;
            Closed += (_, _) => CancelUpdateReview(updateReview, new CancellationToken(true));
            EditorLocalization.Observe(this, static window => window.RefreshUpdateLocalizedText());
        }

        var completion = new TaskCompletionSource<EditorStartupUpdateDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        updateReview = completion;
        updateReviewToken = cancellationToken;
        offeredUpdate = release;
        updateCanUpgrade = canUpgrade;
        updateIsInstallConfirmation = installConfirmation;
        updateIsBusy = false;
        updateMessage = null;
        StopAnimation();
        copyFeedbackTimer.Stop();
        this.FindControl<StackPanel>("BrandContent")!.IsVisible = false;
        this.FindControl<StackPanel>("FailureContent")!.IsVisible = false;
        this.FindControl<Grid>("FailureActions")!.IsVisible = false;
        this.FindControl<Grid>("NormalRail")!.IsVisible = true;
        this.FindControl<Grid>("StartupUpdateContent")!.IsVisible = true;
        Width = 560;
        Height = 420;
        RefreshUpdateLocalizedText();
        RefreshUpdateActions();
        // Register after showing the offer. A token already cancelled during the setup posts
        // cleanup to the UI thread; it cannot re-enter the setup or complete a later offer.
        updateReviewCancellation = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() => CancelUpdateReview(completion, cancellationToken)));
        this.FindControl<Button>(canUpgrade && !installConfirmation ? "StartupUpdateUpgrade" : "StartupUpdateContinue")!.Focus();
        return completion.Task;
    }

    /// <summary>Return to the original startup geometry before continuing or showing a failure.</summary>
    internal void ResetUpdateSurface()
    {
        Dispatcher.UIThread.VerifyAccess();
        CompleteUpdateReview(EditorStartupUpdateDecision.Continue);
        offeredUpdate = null;
        updateIsInstallConfirmation = false;
        updateIsBusy = false;
        updateMessage = null;
        RefreshUpdateLocalizedText();
        this.FindControl<Grid>("StartupUpdateContent")!.IsVisible = false;
        this.FindControl<StackPanel>("BrandContent")!.IsVisible = true;
        this.FindControl<StackPanel>("FailureContent")!.IsVisible = false;
        this.FindControl<Grid>("FailureActions")!.IsVisible = false;
        this.FindControl<Grid>("NormalRail")!.IsVisible = true;
        this.FindControl<TextBlock>("StartupStatus")!.Text = EditorLocalization.Text("正在准备编辑器…");
        Width = 440;
        Height = 284;
        this.FindControl<LoadingDots>("StartupDots")!.IsActive = !windowClosed;
    }

    internal void SetUpdateBusy(bool busy)
    {
        Dispatcher.UIThread.VerifyAccess();
        updateIsBusy = busy;
        RefreshUpdateActions();
        this.FindControl<LoadingDots>("StartupDots")!.IsActive = busy && !windowClosed;
        RefreshUpdateLocalizedText();
    }

    /// <param name="message">Already localized application text; release notes remain separate.</param>
    internal void SetUpdateMessage(string message)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(message);
        updateMessage = message;
        if (offeredUpdate is null)
        {
            this.FindControl<TextBlock>("StartupStatus")!.Text = message;
            return;
        }
        RefreshUpdateLocalizedText();
    }

    private void RefreshUpdateLocalizedText()
    {
        string titleKey = updateIsInstallConfirmation ? "Mda.Text.Update.InstallTitle" : "Mda.Text.Update.Title";
        this.FindControl<TextBlock>("StartupUpdateTitle")!.Text = EditorLocalization.Text(titleKey);
        this.FindControl<Button>("StartupUpdateContinue")!.Content = EditorLocalization.Text(updateIsInstallConfirmation
            ? "Mda.Text.Update.InstallLater" : "Mda.Text.Update.Continue");
        this.FindControl<Button>("StartupUpdateSkip")!.Content = EditorLocalization.Text("Mda.Text.Update.Skip");
        this.FindControl<Button>("StartupUpdateSkip")!.IsVisible = !updateIsInstallConfirmation;
        this.FindControl<Button>("StartupUpdateUpgrade")!.Content = EditorLocalization.Text(updateIsInstallConfirmation
            ? "Mda.Text.Update.Install" : "Mda.Text.Update.Upgrade");
        if (offeredUpdate is null)
        {
            if (updateMessage is not null) this.FindControl<TextBlock>("StartupStatus")!.Text = updateMessage;
            return;
        }
        this.FindControl<TextBlock>("StartupUpdateVersion")!.Text =
            EditorLocalization.Format("Mda.Text.Update.Version", offeredUpdate.Version);
        this.FindControl<SelectableTextBlock>("StartupUpdateNotes")!.Text =
            string.IsNullOrWhiteSpace(offeredUpdate.Summary)
                ? EditorLocalization.Text("Mda.Text.Update.NotesUnavailable")
                : offeredUpdate.Summary;
        var message = this.FindControl<TextBlock>("StartupUpdateMessage")!;
        message.Text = updateMessage ?? (updateIsInstallConfirmation
            ? EditorLocalization.Text("Mda.Text.Update.InstallConfirmation")
            : updateCanUpgrade ? string.Empty : EditorLocalization.Text("Mda.Text.Update.UpgradeUnavailable"));
        message.IsVisible = !string.IsNullOrEmpty(message.Text);
        this.FindControl<TextBlock>("StartupStatus")!.Text = EditorLocalization.Text(updateIsBusy
            ? "Mda.Text.Update.Upgrading" : titleKey);
    }

    private void RefreshUpdateActions()
    {
        bool canChoose = updateReview is not null && !updateIsBusy && !windowClosed;
        this.FindControl<Button>("StartupUpdateContinue")!.IsEnabled = canChoose;
        this.FindControl<Button>("StartupUpdateSkip")!.IsEnabled = canChoose && !updateIsInstallConfirmation;
        this.FindControl<Button>("StartupUpdateUpgrade")!.IsEnabled = canChoose && updateCanUpgrade;
    }

    private void ContinueStartupUpdateClicked(object? sender, RoutedEventArgs args)
    {
        if (!updateIsBusy) CompleteUpdateReview(EditorStartupUpdateDecision.Continue);
    }

    private void SkipStartupUpdateClicked(object? sender, RoutedEventArgs args)
    {
        if (!updateIsBusy && !updateIsInstallConfirmation) CompleteUpdateReview(EditorStartupUpdateDecision.SkipVersion);
    }

    private void UpgradeStartupUpdateClicked(object? sender, RoutedEventArgs args)
    {
        if (updateCanUpgrade && !updateIsBusy) CompleteUpdateReview(EditorStartupUpdateDecision.Upgrade);
    }

    private void CompleteUpdateReview(EditorStartupUpdateDecision decision)
    {
        if (updateReview is not { } completion) return;
        // Cancellation can be requested before its posted UI cleanup runs. A queued click
        // must not turn that cancelled operation into installer consent.
        if (windowClosed || updateReviewToken.IsCancellationRequested)
        {
            CancelUpdateReview(completion, updateReviewToken.IsCancellationRequested ? updateReviewToken : new CancellationToken(true));
            return;
        }
        updateReview = null;
        updateReviewCancellation.Dispose();
        updateReviewCancellation = default;
        updateReviewToken = default;
        RefreshUpdateActions();
        completion.TrySetResult(decision);
    }

    private void CancelUpdateReview(TaskCompletionSource<EditorStartupUpdateDecision>? completion, CancellationToken token)
    {
        if (completion is null || !ReferenceEquals(updateReview, completion)) return;
        updateReview = null;
        updateReviewCancellation.Dispose();
        updateReviewCancellation = default;
        updateReviewToken = default;
        RefreshUpdateActions();
        completion.TrySetCanceled(token);
    }
}
