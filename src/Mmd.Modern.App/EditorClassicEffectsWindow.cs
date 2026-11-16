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
    private readonly EditorClassicDocumentState original;
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
        Background = owner.Background; Foreground = owner.Foreground; UseLayoutRounding = true;
        EditorClassicDialogStyle.Apply(owner, this, objects, assignments);
        var root = new Grid { Margin = new(20), RowDefinitions = new("Auto,Auto,*,Auto,Auto"), RowSpacing = 12 };
        root.Children.Add(new TextBlock { Text = T("AssignmentHelp"), TextWrapping = TextWrapping.Wrap });
        sections.ItemsSource = draft.EffectSections.Select(value => value.Name).ToArray(); sections.SelectedIndex = 0;
        root.Children.Add(sections); Grid.SetRow(sections, 1);
        var lists = new Grid { ColumnDefinitions = new("200,*"), ColumnSpacing = 12 };
        objects.ItemsSource = rows;
        lists.Children.Add(objects); lists.Children.Add(assignments); Grid.SetColumn(assignments, 1);
        root.Children.Add(lists); Grid.SetRow(lists, 2);
        assignments.ItemTemplate = new FuncDataTemplate<AssignmentRow>((row, _) => row is null ? null : new Grid
        {
            ColumnDefinitions = new("150,*,Auto"), ColumnSpacing = 12, Margin = new(4, 6), Children =
            {
                new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                At(new TextBlock { Text = row.Effect, TextTrimming = TextTrimming.CharacterEllipsis }, 1),
                At(new TextBlock { Text = row.Visibility, FontSize = 12 }, 2)
            }
        });
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        Add("SetEffect", async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = T("SetEffect"), AllowMultiple = false,
                FileTypeFilter = [new("MMEffect") { Patterns = ["*.fx"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) EditEffect(path);
        });
        Add("NoEffect", () => { EditEffect("none"); return Task.CompletedTask; });
        Add("Reset", () => { EditEffect(null); return Task.CompletedTask; });
        Add("Show", () => { EditShown(true); return Task.CompletedTask; });
        Add("Hide", () => { EditShown(false); return Task.CompletedTask; });
        Add("InheritVisibility", () => { EditShown(null); return Task.CompletedTask; });
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
            try { await workspace.ApplyClassicAssignmentsAsync(draft, false); preparing = false; Close(true); }
            catch (Exception error) { status.Text = error.Message; }
            finally { preparing = false; apply.IsEnabled = objects.IsEnabled = sections.IsEnabled = actions.IsEnabled = cancel.IsEnabled = true; }
        };
        footer.Children.Add(cancel); footer.Children.Add(apply); root.Children.Add(footer); Grid.SetRow(footer, 4);
        Content = root;
        Closing += (_, args) => { if (preparing) args.Cancel = true; };
        sections.SelectionChanged += (_, _) => RefreshRows(); objects.SelectionChanged += (_, _) => RefreshRows();
        objects.SelectedIndex = rows.Length > 0 ? 0 : -1;
        if (rows.Length == 0) status.Text = T("NoObjects");

        void Add(string key, Func<Task> action)
        {
            var button = new Button { Content = T(key), Margin = new(0, 0, 8, 6), Padding = new(12, 6) };
            button.Click += async (_, _) => { try { await action(); } catch (Exception error) { status.Text = error.Message; } };
            actions.Children.Add(button);
        }
    }

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
