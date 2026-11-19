using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Mmd.Modern.Plugins;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.ViewOutput.D3D12;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Installed plugin controls. Commands are invoked through the neutral contract;
/// rows retain identities and text, never unloaded plugin instances.</summary>
internal sealed class EditorPluginsWindow : Window
{
    private readonly EditorPluginService service;
    private readonly EditorProjectWorkspace? workspace;
    private readonly ListBox plugins = new() { Name = "InstalledPlugins" };
    private readonly ComboBox commands = new() { Name = "PluginCommands", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox details = new() { Name = "PluginDetails", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock empty = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock result = new() { Name = "PluginOperationResult", TextWrapping = TextWrapping.Wrap };
    private readonly Button enable = new() { Name = "EnablePluginButton" }, disable = new() { Name = "DisablePluginButton" };
    private readonly Button rescan = new() { Name = "RescanPluginsButton" }, run = new() { Name = "RunPluginCommandButton" };
    private readonly Button import = new() { Name = "ImportPluginButton" };
    private readonly Button directory = new(), close = new() { Name = "ClosePluginsButton" };
    private readonly TextBlock intro = new() { TextWrapping = TextWrapping.Wrap }, commandLabel = new();
    private readonly TextBlock importHint = new() { Name = "PluginImportHint", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .8 };
    private readonly TextBlock heading = new() { FontSize = 22, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock installedLabel = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock pluginName = new() { Name = "SelectedPluginName", FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock pluginState = new() { Name = "SelectedPluginState", FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel pluginBadges = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock sceneWarningText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Border sceneWarningBanner = new() { CornerRadius = new(4), Padding = new(10, 8), BorderThickness = new(1), IsVisible = false };
    private readonly TextBlock pluginDescription = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock providedStylesHeader = new() { FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock providedStylesText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock noCommands = new() { Name = "NoPluginCommands", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Expander locationExpander = new() { Name = "PluginDirectoryExpander" };
    private bool busy;

    internal EditorPluginsWindow(Window owner, EditorPluginService service, EditorProjectWorkspace? workspace = null)
    {
        this.service = service;
        this.workspace = workspace;
        Width = 860; Height = 620; MinWidth = 680; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; RequestedThemeVariant = owner.ActualThemeVariant;
        FontFamily = owner.FontFamily; FontSize = 14; FontWeight = FontWeight.Normal;
        UseLayoutRounding = true; EditorDialogTheme.Apply(this, owner);
        // 颜色全部取共享对话框样式里的动态资源，切换深浅主题时自动跟随。
        foreach (var text in new[] { intro, importHint, pluginState, noCommands, providedStylesText })
        { text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("EditorMuted")); text.Opacity = 1; }
        intro.FontSize = 13;
        details.FontSize = 11; details.BorderThickness = new(0); details.Padding = new(0);
        details.Background = Brushes.Transparent; details.Bind(TemplatedControl.ForegroundProperty, this.GetResourceObservable("EditorMuted"));
        sceneWarningBanner.Bind(Border.BackgroundProperty, this.GetResourceObservable("EditorInputSurface"));
        sceneWarningBanner.Bind(Border.BorderBrushProperty, this.GetResourceObservable("MdaDanger"));
        sceneWarningBanner.Child = sceneWarningText;
        foreach (var button in new[] { enable, disable, rescan, run, directory, close, import }) button.MinWidth = 80;
        plugins.ItemTemplate = new FuncDataTemplate<PluginInfo>((plugin, _) =>
        {
            if (plugin is null) return null;
            string lang = CurrentLanguage;
            var badgeRow = new WrapPanel { Orientation = Orientation.Horizontal };
            badgeRow.Children.Add(CreateBadge(service.IsBundledDefaultPlugin(plugin.Id) ? Text("Source.Bundled") : Text("Source.UserImported")));
            if (plugin.Capabilities is not null)
            {
                foreach (string cap in plugin.Capabilities)
                    badgeRow.Children.Add(CreateBadge(CapabilityLabel(cap)));
            }
            var subline = new TextBlock { Text = $"{plugin.Version} · {StateText(plugin.State)}", FontSize = 12, Opacity = .8 };
            return new StackPanel
            {
                Spacing = 4, Margin = new(6), Children =
                {
                    new TextBlock { Text = plugin.GetDisplayName(lang), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    subline,
                    badgeRow
                }
            };
        });
        var root = new Grid { Margin = new(20), RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 14 };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 20 };
        var description = new StackPanel { Spacing = 8 };
        description.Children.Add(heading); description.Children.Add(intro); description.Children.Add(importHint);
        header.Children.Add(description); header.Children.Add(import); Grid.SetColumn(import, 1);
        import.VerticalAlignment = VerticalAlignment.Top;
        root.Children.Add(header);
        var location = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        location.Children.Add(new TextBox { Name = "PluginInstallationDirectory", Text = service.InstallationDirectory, IsReadOnly = true });
        location.Children.Add(directory); Grid.SetColumn(directory, 1);
        locationExpander.Content = location;
        var body = new Grid { ColumnDefinitions = new("250,*"), ColumnSpacing = 16, MinHeight = 240 };
        var list = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 10 };
        var listHeader = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        rescan.MinWidth = 64; rescan.FontSize = 12; rescan.Padding = new(8, 5);
        listHeader.Children.Add(installedLabel); listHeader.Children.Add(rescan); Grid.SetColumn(rescan, 1);
        list.Children.Add(listHeader);
        list.Children.Add(plugins); Grid.SetRow(plugins, 1); list.Children.Add(empty); Grid.SetRow(empty, 1);
        empty.Margin = new(20); empty.TextAlignment = TextAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center;
        empty.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("EditorMuted"));
        body.Children.Add(list);
        var right = new Grid { RowDefinitions = new("Auto,Auto,*,Auto,Auto"), RowSpacing = 10 };
        right.Children.Add(pluginName);
        var stateAndBadges = new StackPanel { Spacing = 6, Children = { pluginState, pluginBadges } };
        right.Children.Add(stateAndBadges); Grid.SetRow(stateAndBadges, 1);

        var detailContent = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                sceneWarningBanner,
                pluginDescription,
                new StackPanel { Spacing = 3, Children = { providedStylesHeader, providedStylesText } },
                details
            }
        };
        var detailScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = detailContent
        };
        right.Children.Add(detailScroll); Grid.SetRow(detailScroll, 2);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(enable); actions.Children.Add(disable);
        right.Children.Add(actions); Grid.SetRow(actions, 3);
        var commandPanel = new StackPanel { Spacing = 8 };
        commandPanel.Children.Add(commandLabel);
        var commandRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        commandRow.Children.Add(commands); commandRow.Children.Add(run); Grid.SetColumn(run, 1);
        commandPanel.Children.Add(commandRow); commandPanel.Children.Add(noCommands);
        right.Children.Add(commandPanel); Grid.SetRow(commandPanel, 4);
        var detailSurface = new Border { Padding = new(16), Child = right, CornerRadius = new(4) };
        detailSurface.Bind(Border.BackgroundProperty, this.GetResourceObservable("EditorPanelSurface"));
        body.Children.Add(detailSurface); Grid.SetColumn(detailSurface, 1);
        root.Children.Add(body); Grid.SetRow(body, 1);
        root.Children.Add(result); Grid.SetRow(result, 2);
        close.HorizontalAlignment = HorizontalAlignment.Right; close.IsCancel = true;
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 20 };
        footer.Children.Add(locationExpander); footer.Children.Add(close); Grid.SetColumn(close, 1);
        root.Children.Add(footer); Grid.SetRow(footer, 3);
        Content = root;
        plugins.SelectionChanged += (_, _) => RefreshSelection();
        commands.SelectionChanged += (_, _) => RefreshAvailability();
        enable.Click += async (_, _) => await PerformAsync(() => service.SetEnabledAsync(SelectedId(), true));
        disable.Click += async (_, _) => await PerformAsync(() => service.SetEnabledAsync(SelectedId(), false));
        rescan.Click += async (_, _) => await PerformAsync(() => service.RefreshAsync());
        import.Click += async (_, _) =>
        {
            if (busy) return;
            await PerformAsync(async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Text("Import"), AllowMultiple = true,
                    FileTypeFilter = [new FilePickerFileType(Text("PackageType")) { Patterns = ["*.zip"] }]
                });
                if (files.Count == 0) return new(true, Text("ImportHint"));
                string?[] paths;
                try { paths = files.Select(file => file.TryGetLocalPath()).ToArray(); }
                finally { foreach (var file in files) file.Dispose(); }
                if (paths.Any(path => path is null)) return new(false, Text("InvalidPackage"));
                return await ImportPackagesCoreAsync(paths.Select(path => path!).ToArray());
            });
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        run.Click += async (_, _) =>
        {
            if (commands.SelectedItem is not CommandChoice choice) return;
            await PerformAsync(async () =>
            {
                var output = await service.RunCommandAsync(choice.PluginId, choice.RegistrationId);
                return new(true, output.Message);
            });
        };
        directory.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(service.InstallationDirectory); Process.Start(new ProcessStartInfo(service.InstallationDirectory) { UseShellExecute = true }); }
            catch (Exception error) { result.Text = error.Message; }
        };
        close.Click += (_, _) => Close();
        Closing += (_, args) => { if (busy) { args.Cancel = true; result.Text = Text("Wait"); } };
        EditorLocalization.Observe(this, static window => window.Refresh());
        result.Text = service.Diagnostic ?? "";
        Refresh();
    }

    private string SelectedId() => (plugins.SelectedItem as PluginInfo)?.Id ?? "";
    private static string Text(string suffix) => EditorLocalization.Text("Mda.Text.Plugins." + suffix);
    private static string StateText(PluginState state) => state switch
    {
        PluginState.Active => Text("Active"), PluginState.Failed => Text("Failed"),
        PluginState.Incompatible => Text("Incompatible"), _ => Text("Disabled")
    };

    private void Refresh()
    {
        Title = heading.Text = Text("Title"); intro.Text = Text("Intro"); empty.Text = Text("Empty");
        installedLabel.Text = Text("Installed"); locationExpander.Header = Text("Folder");
        import.Content = Text("Import"); importHint.Text = Text("ImportHint");
        directory.Content = Text("OpenDirectory"); enable.Content = Text("Enable"); disable.Content = Text("Disable");
        rescan.Content = Text("Rescan"); commandLabel.Text = Text("Commands"); run.Content = Text("Run"); close.Content = Text("Close");
        providedStylesHeader.Text = Text("ProvidedStylesLabel");
        string selected = SelectedId();
        var items = service.Manager.Plugins.ToArray();
        plugins.ItemsSource = items;
        plugins.SelectedItem = items.FirstOrDefault(plugin => plugin.Id == selected) ?? items.FirstOrDefault();
        empty.IsVisible = items.Length == 0;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        var selected = plugins.SelectedItem as PluginInfo;
        string lang = CurrentLanguage;
        pluginName.Text = selected?.GetDisplayName(lang) ?? Text("Select");
        if (selected is null)
        {
            pluginState.Text = "";
            pluginBadges.Children.Clear();
            pluginDescription.Text = "";
            providedStylesText.Text = "";
            sceneWarningBanner.IsVisible = false;
            details.Text = "";
        }
        else
        {
            string authorPart = !string.IsNullOrWhiteSpace(selected.Author)
                ? " · " + Format("Mda.Text.Plugins.AuthorLabel", selected.Author)
                : "";
            pluginState.Text = $"{selected.Version} · {StateText(selected.State)}{authorPart}";

            pluginBadges.Children.Clear();
            pluginBadges.Children.Add(CreateBadge(service.IsBundledDefaultPlugin(selected.Id) ? Text("Source.Bundled") : Text("Source.UserImported")));
            if (selected.Capabilities is not null)
            {
                foreach (string cap in selected.Capabilities)
                    pluginBadges.Children.Add(CreateBadge(CapabilityLabel(cap)));
            }

            string? desc = selected.GetDescription(lang);
            pluginDescription.Text = !string.IsNullOrWhiteSpace(desc) ? desc : Text("NoDescription");

            var styleCatalog = service.BuildActiveStyleCatalog(lang);
            if (selected.ProvidedStyles is { Count: > 0 } provided)
            {
                var styleNames = provided.Select(styleId =>
                {
                    string? localized = styleCatalog.FindStyle(styleId)?.DisplayName?.Resolve(lang);
                    return string.IsNullOrWhiteSpace(localized) || string.Equals(localized, styleId, StringComparison.Ordinal)
                        ? styleId
                        : $"{localized} ({styleId})";
                });
                providedStylesText.Text = string.Join(", ", styleNames);
            }
            else
            {
                providedStylesText.Text = Text("NoProvidedStyles");
            }

            UpdateSceneInUseWarning(selected, styleCatalog, lang);
            details.Text = string.IsNullOrWhiteSpace(selected.Diagnostic)
                ? selected.Id
                : $"{selected.Id}\n\n{selected.Diagnostic}";
        }
        ToolTip.SetTip(details, selected?.Directory);
        try
        {
            var prior = commands.SelectedItem as CommandChoice;
            var choices = service.Commands.Where(extension => extension.PluginId == selected?.Id)
                .Select(extension => new CommandChoice(extension.PluginId, extension.RegistrationId, extension.Value.Title)).ToArray();
            commands.ItemsSource = choices;
            commands.SelectedItem = choices.FirstOrDefault(choice => choice.RegistrationId == prior?.RegistrationId) ?? choices.FirstOrDefault();
        }
        catch (Exception error) { commands.ItemsSource = Array.Empty<CommandChoice>(); result.Text = error.Message; }
        noCommands.Text = Text(selected?.State == PluginState.Active ? "NoCommands" : "EnableForCommands");
        noCommands.IsVisible = commands.ItemCount == 0;
        commands.IsVisible = run.IsVisible = commands.ItemCount > 0;
        RefreshAvailability();
    }

    private void UpdateSceneInUseWarning(PluginInfo selected, D3D12ViewStyleCatalog styleCatalog, string lang)
    {
        if (selected.State != PluginState.Active || workspace?.Session is not { HasSupportedRenderingStyles: true } session)
        {
            sceneWarningBanner.IsVisible = false;
            return;
        }

        var referencedIds = new HashSet<string>(StringComparer.Ordinal) { session.RenderingStyles.DefaultStyle.StableId };
        foreach (var overridden in session.RenderingStyles.ModelOverrides.Values)
            referencedIds.Add(overridden.StableId);

        var usedStyles = new List<string>();
        foreach (string styleId in referencedIds)
        {
            bool providedByPlugin = selected.ProvidedStyles?.Contains(styleId, StringComparer.Ordinal) == true
                || string.Equals(service.FindPluginForStyle(styleId)?.Id, selected.Id, StringComparison.Ordinal);
            if (!providedByPlugin)
                continue;
            string? localized = styleCatalog.FindStyle(styleId)?.DisplayName?.Resolve(lang);
            usedStyles.Add(string.IsNullOrWhiteSpace(localized) ? styleId : localized);
        }

        if (usedStyles.Count == 0)
        {
            sceneWarningBanner.IsVisible = false;
            return;
        }

        sceneWarningText.Text = Format("Mda.Text.Plugins.SceneInUseWarning", string.Join(", ", usedStyles));
        sceneWarningBanner.IsVisible = true;
    }

    private Border CreateBadge(string label)
    {
        var border = new Border
        {
            CornerRadius = new(4),
            Padding = new(6, 2),
            Margin = new(0, 2, 6, 2),
            BorderThickness = new(1),
            Child = new TextBlock { Text = label, FontSize = 11 }
        };
        border.Bind(Border.BackgroundProperty, this.GetResourceObservable("EditorInputSurface"));
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("EditorBorder"));
        return border;
    }

    private static string CapabilityLabel(string capability) => capability switch
    {
        PluginCapabilities.RenderingStyles => Text("Capability.RenderingStyles"),
        PluginCapabilities.EditorCommands => Text("Capability.EditorCommands"),
        _ => capability
    };

    private void RefreshAvailability()
    {
        var selected = plugins.SelectedItem as PluginInfo;
        plugins.IsEnabled = commands.IsEnabled = directory.IsEnabled = close.IsEnabled = !busy;
        enable.IsEnabled = !busy && selected is not null && selected.State is PluginState.Disabled or PluginState.Failed;
        disable.IsEnabled = !busy && selected?.State == PluginState.Active;
        rescan.IsEnabled = import.IsEnabled = !busy; run.IsEnabled = !busy && commands.SelectedItem is CommandChoice;
    }

    private void OnDragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = !busy && GetPackagePaths(args) is not null && args.DragEffects.HasFlag(DragDropEffects.Copy)
            ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs args)
    {
        var paths = GetPackagePaths(args); // Freeze the platform transfer before awaiting.
        args.Handled = true;
        if (busy || paths is null || !args.DragEffects.HasFlag(DragDropEffects.Copy)) { args.DragEffects = DragDropEffects.None; return; }
        args.DragEffects = DragDropEffects.Copy;
        await ImportPackagesAsync(paths);
    }

    private static string[]? GetPackagePaths(DragEventArgs args)
    {
        var files = args.DataTransfer.TryGetFiles()?.Take(33).ToArray();
        if (files is null || files.Length is 0 or > 32 || files.Any(file => file is not IStorageFile)) return null;
        var paths = files.Select(file => file.TryGetLocalPath()).ToArray();
        return paths.All(path => path is not null && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            ? paths.Select(path => path!).ToArray() : null;
    }

    internal Task ImportPackagesAsync(IReadOnlyList<string> paths) => PerformAsync(() => ImportPackagesCoreAsync(paths));

    private async Task<PluginOperationResult> ImportPackagesCoreAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count is 0 or > 32 || paths.Any(path => !Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
            return new(false, Text("InvalidPackage"));
        var messages = new List<string>();
        bool succeeded = true;
        foreach (string path in paths)
        {
            var priorIds = service.Manager.Plugins.Select(plugin => plugin.Id).ToHashSet(StringComparer.Ordinal);
            var output = await service.ImportAsync(path);
            succeeded &= output.Succeeded;
            messages.Add($"{Path.GetFileName(path)}: {output.Diagnostic ?? Text(output.Succeeded ? "Imported" : "Failed")}");
            var added = service.Manager.Plugins.FirstOrDefault(plugin => !priorIds.Contains(plugin.Id));
            if (added is not null) { Refresh(); plugins.SelectedItem = added; }
        }
        return new(succeeded, string.Join(Environment.NewLine, messages));
    }

    private async Task PerformAsync(Func<Task<PluginOperationResult>> operation)
    {
        if (busy) return;
        busy = true; result.Text = Text("Working"); RefreshAvailability();
        try
        {
            var output = await operation();
            result.Text = output.Diagnostic ?? Text(output.Succeeded ? "Done" : "Failed");
        }
        catch (Exception error) { result.Text = error.Message; }
        finally { busy = false; Refresh(); }
    }

    private sealed record CommandChoice(string PluginId, string RegistrationId, string Title)
    { public override string ToString() => Title; }
}
