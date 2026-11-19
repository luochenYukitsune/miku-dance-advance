using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.App;

/// <summary>Detached, themed counterpart of MME's object/subset assignment dialog.</summary>
internal sealed class EditorClassicEffectsWindow : Window
{
    private sealed record ObjectRow(string Key, Guid Id, string Name, string[] Materials)
    { public override string ToString() => Name; }
    private sealed record AssignmentRow(string Key, int Subset, string Name, string Effect, string Visibility);
    private readonly EditorProjectWorkspace workspace;
    private EditorClassicDocumentState original;
    private EmmDocument draft;
    private readonly ComboBox sections = new() { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox objects = new() { MinWidth = 160 };
    private readonly ListBox assignments = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button apply;
    private bool preparing;
    internal EmmDocument Draft => draft;
    private static string T(string key) => EditorLocalization.Text("Mda.Text.Mme." + key);

    internal EditorClassicEffectsWindow(Window owner, EditorProjectWorkspace workspace)
    {
        this.workspace = workspace;
        original = workspace.ProjectMetadata as EditorClassicDocumentState
            ?? throw new InvalidOperationException("A prepared Classic project is required.");
        draft = original.Emm?.Document ?? throw new InvalidOperationException("EMM metadata is missing.");
        foreach (var obj in original.Emm.ImportPlan.UnmatchedObjects)
            draft = EmmDocumentEditor.AddObject(draft, obj.IsModel, obj.SourcePath);
        var assignmentPlan = EmmImportPlan.WithAssignments(original.Emm.ImportPlan, draft);
        var session = workspace.Session!;
        var materialNames = session.CaptureAuthoredContent().Motions.ToDictionary(value => value.ObjectId,
            value => value.Model.Materials.Select(material => material.Name).ToArray());
        var labels = session.Objects.ToDictionary(value => value.Id, value => value.Name);
        foreach (var accessory in session.Accessories)
        { materialNames.Add(accessory.ObjectId, accessory.Asset.Source.Surface.Snapshot.Materials.Select(material => material.Name).ToArray()); labels.Add(accessory.ObjectId, Path.GetFileName(accessory.Asset.Path)); }
        var rows = assignmentPlan.Matches.Select(value => new ObjectRow(value.RecordKey, value.ObjectId,
            labels[value.ObjectId], materialNames[value.ObjectId])).ToArray();
        Title = T("Assignment"); Width = 860; Height = 590; MinWidth = 680; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        RequestedThemeVariant = owner.ActualThemeVariant; FontFamily = owner.FontFamily; FontSize = 14;
        UseLayoutRounding = true; EditorDialogTheme.Apply(this, owner);
        var root = new Grid { Margin = new(20), RowDefinitions = new("Auto,Auto,*,Auto,Auto"), RowSpacing = 12 };
        root.Children.Add(new TextBlock { Text = T("AssignmentHelp"), TextWrapping = TextWrapping.Wrap });
        sections.ItemsSource = draft.EffectSections.Select(value => value.Name).ToArray(); sections.SelectedIndex = 0;
        root.Children.Add(sections); Grid.SetRow(sections, 1);
        var lists = new Grid { ColumnDefinitions = new("200,*"), ColumnSpacing = 12 };
        objects.ItemsSource = rows;
        lists.Children.Add(objects);
        var assignmentHeader = new Border
        {
            BorderThickness = new(1),
            CornerRadius = new(4),
            Padding = new(16, 6),
            Child = new Grid
            {
                ColumnDefinitions = new("150,*,80"),
                ColumnSpacing = 12,
                Children =
                {
                    HeaderText("Mda.Text.Ux.Mme.ColumnObject", 0),
                    HeaderText("Mda.Text.Ux.Mme.ColumnEffect", 1),
                    HeaderText("Mda.Text.Ux.Mme.ColumnVisibility", 2)
                }
            }
        };
        assignmentHeader.Bind(Border.BackgroundProperty, this.GetResourceObservable("EditorInputSurface"));
        assignmentHeader.Bind(Border.BorderBrushProperty, this.GetResourceObservable("EditorBorder"));
        var assignmentPane = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 4 };
        assignmentPane.Children.Add(assignmentHeader);
        assignmentPane.Children.Add(assignments); Grid.SetRow(assignments, 1);
        lists.Children.Add(assignmentPane); Grid.SetColumn(assignmentPane, 1);
        root.Children.Add(lists); Grid.SetRow(lists, 2);
        assignments.ItemTemplate = new FuncDataTemplate<AssignmentRow>((row, _) => row is null ? null : new Grid
        {
            ColumnDefinitions = new("150,*,80"), ColumnSpacing = 12, Margin = new(4, 6), Children =
            {
                new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                At(new TextBlock { Text = row.Effect, TextTrimming = TextTrimming.CharacterEllipsis }, 1),
                At(new TextBlock { Text = row.Visibility, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }, 2)
            }
        });
        var effectButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var visibilityButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var groupDivider = new Border
        {
            Width = 1,
            Height = 20,
            Margin = new(10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        groupDivider.Bind(Border.BackgroundProperty, this.GetResourceObservable("EditorBorder"));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(effectButtons);
        actions.Children.Add(groupDivider);
        actions.Children.Add(visibilityButtons);
        Add(effectButtons, "SetEffect", async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = T("SetEffect"), AllowMultiple = false,
                FileTypeFilter = [new("MMEffect") { Patterns = ["*.fx"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) EditEffect(path);
        });
        Add(effectButtons, "NoEffect", () => { EditEffect("none"); return Task.CompletedTask; });
        Add(effectButtons, "Reset", () => { EditEffect(null); return Task.CompletedTask; });
        Add(visibilityButtons, "Show", () => { EditShown(true); return Task.CompletedTask; });
        Add(visibilityButtons, "Hide", () => { EditShown(false); return Task.CompletedTask; });
        Add(visibilityButtons, "InheritVisibility", () => { EditShown(null); return Task.CompletedTask; });
        var controls = new StackPanel { Spacing = 10, Children = { actions, status } };
        root.Children.Add(controls); Grid.SetRow(controls, 3);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 88 }; cancel.Click += (_, _) => Close();
        apply = new Button { Content = T("Apply"), IsDefault = true, MinWidth = 88 };
        apply.Click += async (_, _) =>
        {
            if (!ReferenceEquals(workspace.ProjectMetadata, original)) { status.Text = T("Changed"); return; }
            apply.IsEnabled = false; objects.IsEnabled = sections.IsEnabled = actions.IsEnabled = cancel.IsEnabled = false;
            preparing = true;
            status.Text = T("Preparing");
            try
            {
                // 被替换 FX 留下的命名页有内容（用户改过，或 MME 保存时写满了对象行）时先问用户；
                // 第一次失败不改任何状态，确认后带上“仍删除”重做一次。
                for (bool removeEdited = false; ; )
                {
                    try { await workspace.ApplyClassicAssignmentsAsync(draft, false, default, removeEdited); break; }
                    catch (EmmOffscreenPageConflictException conflict) when (!removeEdited)
                    {
                        string names = string.Join(T("PageSeparator"), conflict.Sections.Select(Label));
                        status.Text = string.Format(T("PagesConflict"), names);
                        if (!await EditorDialogs.ConfirmAsync(this, T("PagesConflictTitle"),
                                string.Format(T("PagesConflictConfirm"), names), T("PagesDeleteAnyway"), destructive: true)) return;
                        if (!ReferenceEquals(workspace.ProjectMetadata, original)) { status.Text = T("Changed"); return; }
                        status.Text = T("Preparing"); removeEdited = true;
                    }
                }
                preparing = false;
                // 指定的 FX 声明了离屏目标时，应用过程会自动增删命名页；有变化就留在窗口里让用户看到新页。
                var applied = (workspace.ProjectMetadata as EditorClassicDocumentState)?.Emm?.Document;
                var before = draft.EffectSections.Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
                var after = applied?.EffectSections.Select(value => value.Name).ToArray() ?? [];
                var added = after.Where(name => !before.Contains(name)).ToArray();
                var removed = before.Where(name => !after.Contains(name)).ToArray();
                if (applied is null || added.Length == 0 && removed.Length == 0) { Close(true); return; }
                original = (EditorClassicDocumentState)workspace.ProjectMetadata!;
                draft = applied;
                string? selected = sections.SelectedItem as string;
                sections.ItemsSource = after;
                sections.SelectedItem = added.FirstOrDefault() ?? (after.Contains(selected) ? selected : after[0]);
                status.Text = string.Join(Environment.NewLine, new[]
                {
                    added.Length == 0 ? null : string.Format(T("PagesAdded"), string.Join(T("PageSeparator"), added.Select(Label))),
                    removed.Length == 0 ? null : string.Format(T("PagesRemoved"), string.Join(T("PageSeparator"), removed.Select(Label)))
                }.Where(text => text is not null));
            }
            catch (EmmOffscreenPageConflictException error)
            { status.Text = string.Format(T("PagesConflict"), string.Join(T("PageSeparator"), error.Sections.Select(Label))); }
            catch (Exception error) { status.Text = error.Message; }
            finally { preparing = false; apply.IsEnabled = objects.IsEnabled = sections.IsEnabled = actions.IsEnabled = cancel.IsEnabled = true; }
        };
        footer.Children.Add(cancel); footer.Children.Add(apply); root.Children.Add(footer); Grid.SetRow(footer, 4);
        Content = root;
        Closing += (_, args) => { if (preparing) args.Cancel = true; };
        sections.SelectionChanged += (_, _) => RefreshRows(); objects.SelectionChanged += (_, _) => RefreshRows();
        objects.SelectedIndex = rows.Length > 0 ? 0 : -1;
        if (rows.Length == 0) status.Text = T("NoObjects");

        TextBlock HeaderText(string key, int column)
        {
            var text = new TextBlock
            {
                Text = EditorLocalization.Text(key),
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("EditorMuted"));
            return At(text, column);
        }

        void Add(Panel container, string key, Func<Task> action)
        {
            var button = new Button { Content = T(key), Padding = new(10, 0) };
            button.Click += async (_, _) => { try { await action(); } catch (Exception error) { status.Text = error.Message; } };
            container.Children.Add(button);
        }
    }

    private static string Label(string section) => section.StartsWith("Effect@", StringComparison.Ordinal) ? section["Effect@".Length..] : section;

    private static TControl At<TControl>(TControl control, int column) where TControl : Control
    { Grid.SetColumn(control, column); return control; }

    private void RefreshRows()
    {
        if (objects.SelectedItem is not ObjectRow obj || sections.SelectedItem is not string section) { assignments.ItemsSource = null; return; }
        int selected = assignments.SelectedIndex;
        var entry = draft.EffectSections.Single(value => value.Name == section).Entries.FirstOrDefault(value => value.ObjectKey == obj.Key);
        var rows = new List<AssignmentRow> { Row(-1, obj.Name, entry?.EffectPath, entry?.Shown) };
        for (int i = 0; i < obj.Materials.Length; i++) rows.Add(Row(i, i + "  " + obj.Materials[i],
            entry?.SubsetPaths.GetValueOrDefault(i), entry is not null && entry.SubsetShown.TryGetValue(i, out bool shown) ? shown : null));
        assignments.ItemsSource = rows; assignments.SelectedIndex = Math.Clamp(selected, 0, rows.Count - 1);
        AssignmentRow Row(int subset, string name, string? effect, bool? shown) => new(obj.Key, subset, name,
            effect ?? T("Inherited"), shown is null ? T("Inherited") : T(shown.Value ? "Shown" : "Hidden"));
    }

    private void EditEffect(string? path)
    {
        if (assignments.SelectedItem is not AssignmentRow row || sections.SelectedItem is not string section) return;
        draft = EmmDocumentEditor.SetEffect(draft, section, row.Key, row.Subset, path); status.Text = T("Draft"); RefreshRows();
    }
    private void EditShown(bool? shown)
    {
        if (assignments.SelectedItem is not AssignmentRow row || sections.SelectedItem is not string section) return;
        draft = EmmDocumentEditor.SetShown(draft, section, row.Key, row.Subset, shown); status.Text = T("Draft"); RefreshRows();
    }
}
