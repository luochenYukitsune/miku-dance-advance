using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Mmd.Modern.Application;
using Mmd.Modern.Plugins;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Inspects unavailable rendering style references across the project, offers one-click activation
/// for discovered disabled plugins, and builds a validated undoable replacement plan.</summary>
internal sealed class EditorStyleReplacementDialog : Window
{
    private sealed record TargetStyleChoice(string StableId, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly EditorProjectWorkspace workspace;
    private readonly EditorPluginService? pluginService;
    private readonly Dictionary<string, string> selectedReplacements = new(StringComparer.Ordinal);
    private readonly StackPanel rowsPanel = new() { Spacing = 10 };
    private readonly TextBlock summaryText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock statusText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button cancelButton = new() { Name = "CancelStyleReplacementButton", IsCancel = true, MinWidth = 88 };
    private readonly Button confirmButton = new() { Name = "ConfirmStyleReplacementButton", IsDefault = true, MinWidth = 112 };
    private D3D12ViewRenderingStyleReplacementInspection? currentInspection;
    private D3D12ViewRenderingStyleReplacementPlan? currentPlan;
    private bool busy;

    private EditorStyleReplacementDialog(Window owner, EditorProjectWorkspace workspace, EditorPluginService? pluginService)
    {
        this.workspace = workspace;
        this.pluginService = pluginService;

        Name = "StyleReplacementDialog";
        Title = Text("Mda.Text.StyleReplacement.Title");
        Width = 680;
        MinWidth = 560;
        MaxHeight = 760;
        SizeToContent = SizeToContent.Height;
        Padding = new Thickness(22);
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = owner.ActualThemeVariant;
        FontFamily = owner.FontFamily;
        FontSize = 13;
        UseLayoutRounding = true;
        EditorDialogTheme.Apply(this, owner);

        summaryText.Text = Text("Mda.Text.StyleReplacement.Subtitle");
        statusText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("EditorMuted"));

        cancelButton.Content = Text("Mda.Text.StyleReplacement.Cancel");
        cancelButton.Classes.Add("quiet");
        cancelButton.Click += (_, _) => Close(false);

        confirmButton.Classes.Add("primary");
        confirmButton.Click += OnConfirmClick;

        var scroll = new ScrollViewer
        {
            MaxHeight = 420,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rowsPanel
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, confirmButton }
        };

        var footer = new StackPanel
        {
            Spacing = 10,
            Children = { statusText, actions }
        };

        var root = new StackPanel
        {
            Spacing = 14,
            Children = { summaryText, scroll, footer }
        };

        Content = root;
        Opened += (_, _) => cancelButton.Focus();
        RebuildState();
    }

    public static Task<bool> ShowAsync(Window owner, EditorProjectWorkspace workspace, EditorPluginService? pluginService)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(workspace);
        var dialog = new EditorStyleReplacementDialog(owner, workspace, pluginService);
        return dialog.ShowDialog<bool>(owner);
    }

    private (D3D12ViewStyleCatalog StyleCatalog, D3D12StylePresetCatalog PresetCatalog) GetLiveCatalogs()
    {
        var styleCatalog = pluginService?.BuildActiveStyleCatalog(CurrentLanguage) ?? workspace.StyleCatalog;
        var presetCatalog = D3D12ViewRenderingStyles.MergePresetCatalog(workspace.PresetCatalog, styleCatalog);
        return (styleCatalog, presetCatalog);
    }

    private void RebuildState()
    {
        rowsPanel.Children.Clear();
        currentInspection = null;
        currentPlan = null;

        var session = workspace.Session;
        if (session is null || !session.HasSupportedRenderingStyles)
        {
            confirmButton.Content = Text("Mda.Text.StyleReplacement.Close");
            confirmButton.IsEnabled = true;
            cancelButton.IsVisible = false;
            statusText.Text = Text("Mda.Text.StyleReplacement.AllResolved");
            return;
        }

        var (styleCatalog, presetCatalog) = GetLiveCatalogs();
        currentInspection = D3D12ViewRenderingStyleReplacements.Inspect(session, presetCatalog);
        var objectNames = session.Objects.ToDictionary(item => item.Id, item => item.Name);
        var availableChoices = BuildAvailableChoices(currentInspection.AvailableStyles, styleCatalog);
        var validTargets = availableChoices.Select(c => c.StableId).ToHashSet(StringComparer.Ordinal);

        foreach (string key in selectedReplacements.Keys.ToArray())
        {
            if (!validTargets.Contains(selectedReplacements[key]))
                selectedReplacements.Remove(key);
        }

        if (currentInspection.MissingStyles.Count == 0)
        {
            var resolvedBanner = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = new TextBlock
                {
                    Text = Text("Mda.Text.StyleReplacement.AllResolved"),
                    TextWrapping = TextWrapping.Wrap
                }
            };
            resolvedBanner.Bind(Border.BackgroundProperty, this.GetResourceObservable("EditorPanelSurface"));
            rowsPanel.Children.Add(resolvedBanner);
            confirmButton.Content = Text("Mda.Text.StyleReplacement.Close");
            confirmButton.IsEnabled = !busy;
            cancelButton.IsVisible = false;
            statusText.Text = "";
            return;
        }

        cancelButton.IsVisible = true;
        confirmButton.Content = Text("Mda.Text.StyleReplacement.Confirm");

        string lang = CurrentLanguage;
        foreach (var missing in currentInspection.MissingStyles)
        {
            rowsPanel.Children.Add(BuildMissingStyleCard(missing, availableChoices, objectNames, lang));
        }

        RefreshPlanState();
    }

    private Border BuildMissingStyleCard(
        D3D12ViewRenderingStyleReference missing,
        IReadOnlyList<TargetStyleChoice> availableChoices,
        IReadOnlyDictionary<Guid, string> objectNames,
        string lang)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1)
        };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("EditorPanelSurface"));
        card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("EditorBorder"));

        var headerRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        headerRow.Children.Add(new TextBlock
        {
            Text = missing.Preset.StableId,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (missing.IsDefault)
            badges.Children.Add(CreateBadge(Text("Mda.Text.StyleReplacement.DefaultStyleBadge")));
        int modelCount = Math.Max(missing.ExplicitObjectIds.Count, missing.AffectedObjectIds.Count);
        if (modelCount > 0)
        {
            var countBadge = CreateBadge(Format("Mda.Text.StyleReplacement.ModelCountBadge", modelCount));
            var affectedNames = missing.ExplicitObjectIds
                .Concat(missing.AffectedObjectIds)
                .Distinct()
                .Where(objectNames.ContainsKey)
                .Select(id => objectNames[id])
                .ToArray();
            if (affectedNames.Length > 0)
                ToolTip.SetTip(countBadge, string.Join(", ", affectedNames));
            badges.Children.Add(countBadge);
        }
        headerRow.Children.Add(badges);
        Grid.SetColumn(badges, 1);

        var cardStack = new StackPanel { Spacing = 8 };
        cardStack.Children.Add(headerRow);

        var discoveredPlugin = pluginService?.FindPluginForStyle(missing.Preset.StableId);
        var detailRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        string detailMessage = discoveredPlugin is { State: PluginState.Disabled }
            ? Format("Mda.Text.StyleReplacement.PluginDisabledDetail", discoveredPlugin.GetDisplayName(lang))
            : !string.IsNullOrWhiteSpace(discoveredPlugin?.Diagnostic)
                ? discoveredPlugin.Diagnostic!
                : Text("Mda.Text.StyleReplacement.NotInstalledDetail");
        var detailText = new TextBlock
        {
            Text = detailMessage,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        detailText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("EditorMuted"));
        detailRow.Children.Add(detailText);

        if (discoveredPlugin is { State: PluginState.Disabled } && pluginService is not null)
        {
            var enableBtn = new Button
            {
                Content = Text("Mda.Text.Context.Inspector.Style.EnablePluginAndApply"),
                IsEnabled = !busy,
                Padding = new Thickness(10, 4)
            };
            enableBtn.Classes.Add("action");
            enableBtn.Click += async (_, _) => await EnablePluginAsync(missing.Preset.StableId);
            detailRow.Children.Add(enableBtn);
            Grid.SetColumn(enableBtn, 1);
        }
        cardStack.Children.Add(detailRow);

        var targetRow = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        var targetLabel = new TextBlock
        {
            Text = Text("Mda.Text.StyleReplacement.TargetLabel"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var targetCombo = new ComboBox
        {
            ItemsSource = availableChoices,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = !busy
        };
        if (selectedReplacements.TryGetValue(missing.Preset.StableId, out string? existingTarget))
            targetCombo.SelectedItem = availableChoices.FirstOrDefault(c => string.Equals(c.StableId, existingTarget, StringComparison.Ordinal));
        targetCombo.SelectionChanged += (_, _) =>
        {
            if (targetCombo.SelectedItem is TargetStyleChoice selected)
                selectedReplacements[missing.Preset.StableId] = selected.StableId;
            else
                selectedReplacements.Remove(missing.Preset.StableId);
            RefreshPlanState();
        };

        targetRow.Children.Add(targetLabel);
        targetRow.Children.Add(targetCombo);
        Grid.SetColumn(targetCombo, 1);
        cardStack.Children.Add(targetRow);

        card.Child = cardStack;
        return card;
    }

    private Border CreateBadge(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11 };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            BorderThickness = new Thickness(1),
            Child = label
        };
        badge.Bind(Border.BorderBrushProperty, this.GetResourceObservable("EditorBorder"));
        return badge;
    }

    private IReadOnlyList<TargetStyleChoice> BuildAvailableChoices(
        IReadOnlyList<D3D12StylePresetResult> available,
        D3D12ViewStyleCatalog styleCatalog)
    {
        string lang = CurrentLanguage;
        var list = new List<TargetStyleChoice>(available.Count);
        foreach (var preset in available)
        {
            if (preset.StableId is not { Length: > 0 } stableId)
                continue;
            string label;
            if (string.Equals(stableId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            {
                label = Text("Mda.Text.Context.Inspector.Style.Option.Mmd");
            }
            else if (styleCatalog.FindStyle(stableId) is { } def)
            {
                string? localized = def.DisplayName?.Resolve(lang);
                label = string.IsNullOrWhiteSpace(localized) ? stableId : $"{localized} ({stableId})";
            }
            else
            {
                label = stableId;
            }
            list.Add(new TargetStyleChoice(stableId, label));
        }
        return list;
    }

    private void RefreshPlanState()
    {
        if (workspace.Session is not { } session || !session.HasSupportedRenderingStyles)
        {
            currentPlan = null;
            confirmButton.IsEnabled = false;
            return;
        }

        var (_, presetCatalog) = GetLiveCatalogs();
        currentPlan = D3D12ViewRenderingStyleReplacements.Prepare(session, presetCatalog, selectedReplacements);
        confirmButton.IsEnabled = !busy && currentPlan.CanConfirm;
        statusText.Text = currentPlan.CanConfirm
            ? ""
            : Format("Mda.Text.StyleReplacement.UnresolvedHint", currentPlan.UnresolvedStyles.Count);
    }

    private async Task EnablePluginAsync(string styleId)
    {
        if (busy || pluginService is null) return;
        busy = true;
        cancelButton.IsEnabled = false;
        confirmButton.IsEnabled = false;
        try
        {
            var outcome = await pluginService.EnablePluginForStyleAsync(styleId);
            if (!outcome.Succeeded)
                statusText.Text = outcome.Diagnostic ?? "";
            RebuildState();
        }
        catch (Exception error)
        {
            statusText.Text = error.Message;
        }
        finally
        {
            busy = false;
            cancelButton.IsEnabled = true;
            confirmButton.IsEnabled = currentInspection?.MissingStyles.Count == 0 || (currentPlan?.CanConfirm ?? false);
        }
    }

    private void OnConfirmClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (busy) return;
        if (currentInspection is { MissingStyles.Count: 0 })
        {
            Close(true);
            return;
        }
        if (currentPlan is not { CanConfirm: true } plan)
            return;

        try
        {
            workspace.ApplyRenderingStyleReplacements(plan);
            Close(true);
        }
        catch (Exception error)
        {
            statusText.Text = error.Message;
        }
    }
}
