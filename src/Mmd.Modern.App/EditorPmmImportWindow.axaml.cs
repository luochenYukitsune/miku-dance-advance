using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Mmd.Modern.Application;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Explicit per-instance role confirmation over a detached PMM plan. No filename heuristics or implicit defaults.</summary>
public partial class EditorPmmImportWindow : Window
{
    private static string[] RoleLabels => [Text("角色"), Text("Mda.Text.Context.Room.IndoorChoice"),
        Text("Mda.Text.Context.Room.MmdEnvironmentChoice"), Text("道具"), Text("未指定")];
    private static readonly ModelPurpose[] Purposes = [new(ModelRole.Character), new(ModelRole.Environment, true),
        new(ModelRole.Environment, false), new(ModelRole.Prop), new(ModelRole.Unspecified)];
    private readonly Dictionary<int, ComboBox> roleBoxes = [];
    private readonly HashSet<int> editedPurposeRows = [];
    private readonly List<Action> rowLocalizations = [];
    private readonly Dictionary<int, string> displayNameOverrides = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly bool classic;
    private bool busy, closed, refreshingLocalization;
    private Func<string>? operationError;
    private string importStatusSource = "确认并准备成功后替换当前工程。取消保留原工程。";
    private PmmProjectImportPlan plan;
    public PmmProjectImportPlan Plan => plan;
    public PmmImportOptions ImportOptions { get; private set; }
    public IReadOnlyDictionary<int, string> DisplayNameOverrides => new Dictionary<int, string>(displayNameOverrides);

    public EditorPmmImportWindow()
    {
        plan = null!; ImportOptions = new(); InitializeComponent();
        Observe(this, static window => window.RefreshLocalization());
    }

    public EditorPmmImportWindow(PmmProjectImportPlan plan, PmmImportOptions? options = null)
        : this(plan, options, classic: false) { }

    internal EditorPmmImportWindow(PmmProjectImportPlan plan, PmmImportOptions? options, bool classic) : this()
    {
        this.classic = classic;
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ImportOptions = options ?? new();
        PmmProjectName.Text = Path.GetFileName(plan.ProjectPath);
        ToolTip.SetTip(PmmProjectName, plan.ProjectPath);
        PmmBatchRoleBox.ItemsSource = RoleLabels;
        PmmBatchRoleBox.SelectedIndex = -1;
        PmmBatchRoleBox.SelectionChanged += (_, _) => UpdateReadiness();
        RenderRows();
        Opened += (_, _) =>
        {
            RequestedThemeVariant = Owner?.ActualThemeVariant ?? ThemeVariant.Dark;
            if (Owner is { } owner)
            {
                FontFamily = owner.FontFamily; FontSize = owner.FontSize;
                Resources["OutputFont"] = owner.FontFamily;
            }
        };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); };
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            Close(null); args.Handled = true;
        };
    }

    private void RenderRows()
    {
        roleBoxes.Clear(); editedPurposeRows.Clear(); rowLocalizations.Clear(); PmmModelRows.Children.Clear();
        foreach (var row in plan.ModelRoleSelections)
        {
            var role = new ComboBox
            {
                Name = "PmmRole_" + row.ModelIndex, ItemsSource = RoleLabels,
                SelectedIndex = PurposeIndex(row.Purpose),
                PlaceholderText = Text("请选择用途"), VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(role, Format("模型 #{0} 用途", row.ModelIndex));
            role.SelectionChanged += (_, _) =>
            {
                if (!refreshingLocalization) editedPurposeRows.Add(row.ModelIndex);
                UpdateReadiness();
            };
            roleBoxes.Add(row.ModelIndex, role);
            var repair = new Button
            {
                Name = "PmmRepair_" + row.ModelIndex, Content = Text("重新定位…"), Tag = row.ModelIndex, MinWidth = 100,
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(repair, Format("重新定位模型 #{0}", row.ModelIndex));
            repair.Click += RelinkModel;
            var details = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 16, 0) };
            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(row.Name) ? Text("未命名模型") : row.Name,
                FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTip.SetTip(name, row.Name); details.Children.Add(name);
            var prepared = plan.Models.FirstOrDefault(model => model.Source.Index == row.ModelIndex);
            var originalName = plan.Document.Models.Single(model => model.Index == row.ModelIndex).Name;
            var display = PmmNameResolver.ResolveDisplay(originalName,
                prepared is null ? [] : [prepared.Model.Name, prepared.Model.EnglishName]);
            void RefreshName()
            {
                string value = displayNameOverrides.GetValueOrDefault(row.ModelIndex) ?? display.BoundName ?? row.Name;
                name.Text = string.IsNullOrWhiteSpace(value) ? Text("未命名模型") : value;
                ToolTip.SetTip(name, PmmNameResolver.Describe(originalName));
            }
            RefreshName(); rowLocalizations.Add(RefreshName);
            if (display.BoundName is null && display.Candidates.Select(candidate => candidate.Value).Distinct().Count() > 1)
            {
                var candidates = display.Candidates.ToArray();
                var encoding = new ComboBox
                {
                    Name = "PmmNameEncoding_" + row.ModelIndex,
                    ItemsSource = candidates.Select(candidate => $"CP{candidate.AnsiCodePage}: {candidate.Value}").ToArray(),
                    SelectedIndex = Array.FindIndex(candidates, candidate => displayNameOverrides.GetValueOrDefault(row.ModelIndex) == candidate.Value),
                    HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 500
                };
                void RefreshEncoding()
                {
                    encoding.PlaceholderText = Text("Mda.Text.PmmNameEncoding");
                    AutomationProperties.SetName(encoding, Text("Mda.Text.PmmNameEncoding"));
                }
                RefreshEncoding(); rowLocalizations.Add(RefreshEncoding);
                encoding.SelectionChanged += (_, _) =>
                {
                    if ((uint)encoding.SelectedIndex < (uint)candidates.Length)
                    { displayNameOverrides[row.ModelIndex] = candidates[encoding.SelectedIndex].Value; RefreshName(); }
                };
                var encodingOptions = new Expander { Name = "PmmNameOptions_" + row.ModelIndex,
                    Content = encoding, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
                void RefreshEncodingOptions() => encodingOptions.Header = Text("Mda.Text.PmmNameEncoding");
                RefreshEncodingOptions(); rowLocalizations.Add(RefreshEncodingOptions);
                details.Children.Add(encodingOptions);
            }
            var referenceLabel = PathText(Format("引用：{0}", row.Reference.Value));
            details.Children.Add(referenceLabel);
            var fileLabel = PathText(Format("文件：{0}", row.ResolvedPath ?? Text("未找到模型，请重新定位")));
            details.Children.Add(fileLabel);
            if (prepared is not null)
            {
                var binding = PmmMotionAdapter.InspectBinding(prepared.Source, prepared.Model);
                var bindingLabel = PathText("");
                details.Children.Add(bindingLabel);
                Expander? unmatched = null;
                TextBlock? unmatchedText = null;
                if (binding.UnmatchedBones.Count != 0 || binding.UnmatchedMorphs.Count != 0)
                {
                    unmatchedText = PathText("");
                    unmatched = new Expander { Content = unmatchedText, HorizontalAlignment = HorizontalAlignment.Stretch };
                    details.Children.Add(unmatched);
                }
                void RefreshBinding()
                {
                    SetPathText(bindingLabel, Format("Mda.Text.Context.Pmm.BindingSummary", binding.MatchedBones,
                        binding.TotalBones, binding.MatchedMorphs, binding.TotalMorphs));
                    if (unmatched is not null && unmatchedText is not null)
                    {
                        unmatched.Header = Text("Mda.Text.Context.Pmm.UnmatchedTracks");
                        SetPathText(unmatchedText, Text("Mda.Text.Context.Pmm.UnmatchedNote") + Environment.NewLine
                            + Format("Mda.Text.Context.Pmm.UnmatchedBones", string.Join("、", binding.UnmatchedBones)) + Environment.NewLine
                            + Format("Mda.Text.Context.Pmm.UnmatchedMorphs", string.Join("、", binding.UnmatchedMorphs)));
                    }
                }
                RefreshBinding(); rowLocalizations.Add(RefreshBinding);
            }
            var missing = plan.UnresolvedAssets.FirstOrDefault(asset => asset.ModelIndex == row.ModelIndex);
            TextBlock? errorLabel = null;
            string missingSource = "";
            if (missing is not null)
            {
                missingSource = missing.Resolution.Status switch
                {
                    PmmAssetResolutionStatus.Ambiguous => "找到多个候选，请明确选择文件。",
                    PmmAssetResolutionStatus.Invalid => "模型无法读取，请重新定位。",
                    _ => "模型文件缺失。"
                };
                errorLabel = PathText(missing.Error ?? Text(missingSource));
                details.Children.Add(errorLabel);
            }
            rowLocalizations.Add(() =>
            {
                role.PlaceholderText = Text("请选择用途");
                AutomationProperties.SetName(role, Format("模型 #{0} 用途", row.ModelIndex));
                repair.Content = Text("重新定位…");
                AutomationProperties.SetName(repair, Format("重新定位模型 #{0}", row.ModelIndex));
                RefreshName();
                SetPathText(referenceLabel, Format("引用：{0}", row.Reference.Value));
                SetPathText(fileLabel, Format("文件：{0}", row.ResolvedPath ?? Text("未找到模型，请重新定位")));
                if (errorLabel is not null) SetPathText(errorLabel, missing!.Error ?? Text(missingSource));
            });
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("44,*,228,12,Auto"),
                Margin = new Thickness(0, 12)
            };
            var index = PathText("#" + row.ModelIndex); index.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(index);
            Grid.SetColumn(details, 1); grid.Children.Add(details);
            Grid.SetColumn(role, 2); grid.Children.Add(role);
            Grid.SetColumn(repair, 4); grid.Children.Add(repair);
            var border = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1) };
            border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("OutputBorder"));
            PmmModelRows.Children.Add(border);
        }
        UpdateReadiness();
    }

    private static TextBlock PathText(string text)
    {
        var label = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        label.Classes.Add("caption"); ToolTip.SetTip(label, text); return label;
    }

    private static void SetPathText(TextBlock label, string text)
    { label.Text = text; ToolTip.SetTip(label, text); }

    private void RefreshLocalization()
    {
        if (closed || plan is null) return;
        refreshingLocalization = true;
        try
        {
            int batchRole = PmmBatchRoleBox.SelectedIndex;
            PmmBatchRoleBox.ItemsSource = RoleLabels;
            PmmBatchRoleBox.SelectedIndex = batchRole;
            foreach (var role in roleBoxes.Values)
            {
                int selected = role.SelectedIndex;
                role.ItemsSource = RoleLabels;
                role.SelectedIndex = selected;
            }
            // Relabel in place: no role edits, row reconstruction, or scroll-position changes.
            foreach (var refresh in rowLocalizations) refresh();
            PmmImportStatus.Text = Text(importStatusSource);
        }
        finally { refreshingLocalization = false; }
        UpdateReadiness();
        if (busy && operationError is { } error) SetValidation(error());
    }

    private Dictionary<int, ModelPurpose> CurrentPurposes()
    {
        var result = new Dictionary<int, ModelPurpose>();
        foreach (var pair in roleBoxes)
        {
            int selected = pair.Value.SelectedIndex;
            if (selected < 0 || selected >= Purposes.Length) continue;
            var previous = plan.ModelRoleSelections.Single(row => row.ModelIndex == pair.Key).Purpose;
            // Opening or relocalizing a previously prepared compatibility choice does not
            // turn its nullable legacy tag into a new explicit setting.
            result.Add(pair.Key, previous is not null && !editedPurposeRows.Contains(pair.Key) && selected == PurposeIndex(previous)
                ? previous : Purposes[selected]);
        }
        return result;
    }

    private static int PurposeIndex(ModelPurpose? purpose) => purpose is null ? -1 : purpose.Role switch
    {
        ModelRole.Character => 0,
        ModelRole.Environment => purpose.EnvironmentIndoor == true ? 1 : 2,
        ModelRole.Prop => 3,
        _ => 4
    };

    private void ApplyBatchRole(object? sender, RoutedEventArgs args)
    {
        if (busy || PmmBatchRoleBox.SelectedIndex < 0 || PmmBatchRoleBox.SelectedIndex >= Purposes.Length) return;
        foreach (var pair in roleBoxes)
        {
            // Setting an already-selected value raises no SelectionChanged event, but
            // the explicit batch command still confirms the chosen lighting mode.
            editedPurposeRows.Add(pair.Key);
            pair.Value.SelectedIndex = PmmBatchRoleBox.SelectedIndex;
        }
        UpdateReadiness();
    }

    private void UpdateReadiness()
    {
        if (refreshingLocalization) return;
        int confirmed = roleBoxes.Values.Count(role => role.SelectedIndex >= 0);
        PmmConfirmationSummary.Text = Format("已确认 {0} / {1}", confirmed, roleBoxes.Count);
        PmmApplyBatchButton.IsEnabled = !busy && PmmBatchRoleBox.SelectedIndex >= 0;
        PmmBatchRoleBox.IsEnabled = !busy;
        PmmModelRows.IsEnabled = !busy;
        var support = InspectSupport();
        PmmConfirmImportButton.IsEnabled = !busy && plan.CanCommit && confirmed == roleBoxes.Count && support.Count == 0;
        if (!busy)
        {
            var messages = new List<string>();
            if (!plan.CanCommit) messages.Add(Text("请重新定位缺失或无效模型；导入前需要所有资源均可读取。"));
            if (!classic && CurrentPurposes().Values.Any(purpose => purpose is { Role: ModelRole.Environment, EnvironmentIndoor: true }))
                messages.Add(Text("Mda.Text.Context.Room.BasicLightHelp"));
            messages.AddRange(support);
            if (!classic) messages.AddRange(EditorProjectInterchange.InspectPmmNotices(plan));
            else messages.AddRange(EditorProjectInterchange.InspectPmmUnregisteredOutsideParentNotices(plan));
            if (operationError is { } error) messages.Add(error());
            SetValidation(string.Join(Environment.NewLine, messages));
        }
    }

    private async void RelinkModel(object? sender, RoutedEventArgs args)
    {
        if (busy || sender is not Button { Tag: int modelIndex } button) return;
        if (!StorageProvider.CanOpen) { SetOperationValidation("当前无法打开文件选择器。请保留原工程，稍后重试。"); return; }
        operationError = null; busy = true; button.IsEnabled = false; UpdateReadiness();
        try
        {
            var selected = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = Format("重新定位 PMM 模型 #{0}", modelIndex), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Text("MMD 模型")) { Patterns = ["*.pmx", "*.pmd"] }]
            });
            if (closed || selected.FirstOrDefault() is not { } file) return;
            if (file.TryGetLocalPath() is not { } path) { SetOperationValidation("请选择本机 PMX 或 PMD 模型。"); return; }
            var overrides = new Dictionary<int, string>(ImportOptions.ModelPathOverrides) { [modelIndex] = path };
            var nextOptions = new PmmImportOptions
            {
                MmdDirectory = ImportOptions.MmdDirectory, SearchRoots = ImportOptions.SearchRoots,
                ModelPathOverrides = overrides, ModelPurposes = CurrentPurposes()
            };
            importStatusSource = "正在检查模型资源…"; PmmImportStatus.Text = Text(importStatusSource);
            var token = lifetime.Token;
            var candidate = await Task.Run(() => EditorProjectInterchange.PreparePmm(plan.ProjectPath, nextOptions, token), token);
            if (closed) return;
            plan = candidate; ImportOptions = nextOptions; RenderRows();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException)
        { if (!closed) operationError = () => error.Message; }
        finally
        {
            if (!closed)
            {
                busy = false; button.IsEnabled = true;
                importStatusSource = "确认并准备成功后替换当前工程。取消保留原工程。";
                PmmImportStatus.Text = Text(importStatusSource);
                UpdateReadiness();
            }
        }
    }

    private void ConfirmImport(object? sender, RoutedEventArgs args)
    {
        if (busy) return;
        try
        {
            if (!plan.CanCommit) throw Error("请先重新定位所有缺失或无效的模型。");
            var unsupported = InspectSupport();
            if (unsupported.Count != 0) { operationError = null; UpdateReadiness(); return; }
            var confirmed = plan.ConfirmRoles(CurrentPurposes()).RequireConfirmedRoles();
            ImportOptions = new()
            {
                MmdDirectory = ImportOptions.MmdDirectory, SearchRoots = ImportOptions.SearchRoots,
                ModelPathOverrides = ImportOptions.ModelPathOverrides, ModelPurposes = confirmed.ModelPurposes
            };
            Close(confirmed);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
        { operationError = () => error.Message; SetValidation(error.Message); }
    }

    private void CancelImport(object? sender, RoutedEventArgs args) => Close(null);
    private IReadOnlyList<string> InspectSupport() => classic
        ? EditorProjectInterchange.InspectClassicPmmSupport(plan)
        : EditorProjectInterchange.InspectPmmSupport(plan);
    private void SetOperationValidation(string source)
    { operationError = () => Text(source); SetValidation(operationError()); }
    private void SetValidation(string message)
    {
        PmmValidationMessage.Text = message; PmmValidationMessage.IsVisible = message.Length != 0;
    }
}
