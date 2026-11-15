using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Mmd.Modern.Plugins;

namespace Mmd.Modern.App;

/// <summary>Installed plugin controls. Commands are invoked through the neutral contract;
/// rows retain identities and text, never unloaded plugin instances.</summary>
internal sealed class EditorPluginsWindow : Window
{
    private readonly EditorPluginService service;
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
    private readonly TextBlock pluginName = new() { Name = "SelectedPluginName", FontSize = 19, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock pluginState = new() { Name = "SelectedPluginState", FontSize = 12 };
    private readonly TextBlock noCommands = new() { Name = "NoPluginCommands", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Expander locationExpander = new() { Name = "PluginDirectoryExpander" };
    private bool busy;
    private CancellationTokenSource? commandCancellation;

    internal EditorPluginsWindow(Window owner, EditorPluginService service)
    {
        this.service = service;
        Width = 860; Height = 620; MinWidth = 680; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; RequestedThemeVariant = owner.ActualThemeVariant;
        FontFamily = owner.FontFamily; FontSize = 14; FontWeight = FontWeight.Normal;
        Background = owner.Background; Foreground = owner.Foreground;
        UseLayoutRounding = true;
        bool dark = owner.ActualThemeVariant != ThemeVariant.Light;
        IBrush Brush(string key, string fallback) => owner.TryFindResource(key, out var value) && value is IBrush brush
            ? brush : new SolidColorBrush(Color.Parse(fallback));
        var selectedSurface = Brush("EditorSelectedSurface", dark ? "#182633" : "#E3ECF3");
        var panelSurface = Brush("EditorPanelSurface", dark ? "#13171C" : "#FFFFFF");
        var muted = Brush("EditorMuted", dark ? "#ADB7C1" : "#53616F");
        foreach (var text in new[] { intro, importHint, pluginState, noCommands }) { text.Foreground = muted; text.Opacity = 1; }
        intro.FontSize = 13;
        plugins.Background = panelSurface;
        details.FontSize = 12; details.BorderThickness = new(0); details.Padding = new(0);
        details.Background = Brushes.Transparent; details.Foreground = muted; details.MaxHeight = 145;
        plugins.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(":selected"))
        { Setters = { new Setter(ListBoxItem.BackgroundProperty, selectedSurface) } });
        plugins.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>())
        { Setters = { new Setter(ContentPresenter.BackgroundProperty, selectedSurface) } });
        foreach (var button in new[] { enable, disable, rescan, run, directory, close, import })
        { button.MinWidth = 80; button.Padding = new(12, 6); }
        plugins.ItemTemplate = new FuncDataTemplate<PluginInfo>((plugin, _) =>
        {
            if (plugin is null) return null;
            return new StackPanel
            {
                Spacing = 4, Margin = new(6), Children =
                {
                    new TextBlock { Text = plugin.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = $"{plugin.Version} · {StateText(plugin.State)}", FontSize = 12, Opacity = .8 }
                }
            };
        });
        var root = new Grid { Margin = new(20), RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 16 };
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
        var body = new Grid { ColumnDefinitions = new("240,*"), ColumnSpacing = 16, MinHeight = 250 };
        var list = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 10 };
        var listHeader = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        rescan.MinWidth = 64; rescan.FontSize = 12; rescan.Padding = new(8, 5);
        listHeader.Children.Add(installedLabel); listHeader.Children.Add(rescan); Grid.SetColumn(rescan, 1);
        list.Children.Add(listHeader);
        list.Children.Add(plugins); Grid.SetRow(plugins, 1); list.Children.Add(empty); Grid.SetRow(empty, 1);
        empty.Margin = new(16); body.Children.Add(list);
        var right = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto,Auto"), RowSpacing = 10 };
        right.Children.Add(pluginName); right.Children.Add(pluginState); Grid.SetRow(pluginState, 1);
        right.Children.Add(details); Grid.SetRow(details, 2);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(enable); actions.Children.Add(disable);
        right.Children.Add(actions); Grid.SetRow(actions, 4);
        var commandPanel = new StackPanel { Spacing = 8 };
        commandPanel.Children.Add(commandLabel);
        var commandRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        commandRow.Children.Add(commands); commandRow.Children.Add(run); Grid.SetColumn(run, 1);
        commandPanel.Children.Add(commandRow); commandPanel.Children.Add(noCommands);
        right.Children.Add(commandPanel); Grid.SetRow(commandPanel, 5);
        var detailSurface = new Border { Background = panelSurface, Padding = new(18), Child = right };
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
            using var cancellation = new CancellationTokenSource();
            commandCancellation = cancellation;
            try
            {
                await PerformAsync(async () =>
                {
                    try
                    {
                        var output = await service.RunCommandAsync(choice.PluginId, choice.RegistrationId, cancellation.Token);
                        return new(true, output.Message);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    { return new(false, Text("Cancelled")); }
                });
            }
            finally { commandCancellation = null; }
        };
        directory.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(service.InstallationDirectory); Process.Start(new ProcessStartInfo(service.InstallationDirectory) { UseShellExecute = true }); }
            catch (Exception error) { result.Text = error.Message; }
        };
        // While a plugin command runs, Close cancels it, so a command that never returns cannot trap the modal
        // dialog (and with it the editor) open.
        close.Click += (_, _) => { if (commandCancellation is { } running) running.Cancel(); else Close(); };
        Closing += (_, args) =>
        {
            if (!busy) return;
            args.Cancel = true;
            if (commandCancellation is { } running) running.Cancel();
            result.Text = Text("Wait");
        };
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
        string selected = SelectedId();
        plugins.ItemsSource = service.Manager.Plugins;
        plugins.SelectedItem = service.Manager.Plugins.FirstOrDefault(plugin => plugin.Id == selected) ?? service.Manager.Plugins.FirstOrDefault();
        empty.IsVisible = service.Manager.Plugins.Count == 0;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        var selected = plugins.SelectedItem as PluginInfo;
        pluginName.Text = selected?.Name ?? Text("Select");
        pluginState.Text = selected is null ? "" : $"{selected.Version} · {StateText(selected.State)}";
        details.Text = selected is null ? "" : $"{selected.Id}\n\n{selected.Diagnostic}";
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

    private void RefreshAvailability()
    {
        var selected = plugins.SelectedItem as PluginInfo;
        plugins.IsEnabled = commands.IsEnabled = directory.IsEnabled = !busy;
        close.IsEnabled = !busy || commandCancellation is not null;
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
