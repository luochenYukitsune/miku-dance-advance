using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>One selectable row of the project-default style selector. The five built-in rows are
/// renderer-owned presets; a retained row keeps an unknown project reference visible without rewriting it.</summary>
public sealed class EditorStyleInspectorChoice : INotifyPropertyChanged
{
    private string label;
    public EditorStyleInspectorChoice(string stableId, RenderingPresetReference reference, string label)
    { StableId = stableId; Reference = reference; this.label = label; }
    public string StableId { get; }
    public RenderingPresetReference Reference { get; }
    public string Label
    {
        get => label;
        internal set
        {
            if (string.Equals(label, value, StringComparison.Ordinal)) return;
            label = value; PropertyChanged?.Invoke(this, new(nameof(Label)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Label;
}

/// <summary>Project-default style metadata page. It only reads the existing session selection; the write is
/// one validated workspace transaction in the host, so switching pages or opening this page never dirties a project.</summary>
public sealed class EditorStyleInspectorViewModel : INotifyPropertyChanged
{
    private static readonly (RenderingStyleId Style, string LabelKey)[] BuiltIns =
    [
        (RenderingStyleId.Mmd, "Mda.Text.Context.Inspector.Style.Option.Mmd"),
        (RenderingStyleId.Genshin, "Mda.Text.Context.Inspector.Style.Option.Genshin"),
        (RenderingStyleId.StarRail, "Mda.Text.Context.Inspector.Style.Option.StarRail"),
        (RenderingStyleId.WutheringWaves, "Mda.Text.Context.Inspector.Style.Option.WutheringWaves"),
        (RenderingStyleId.Endfield, "Mda.Text.Context.Inspector.Style.Option.Endfield"),
    ];

    private SceneProjectSession? session;
    private readonly ObservableCollection<EditorStyleInspectorChoice> options = [];
    private RenderingPresetReference? defaultStyle, modelStyle;
    private bool modelOverride, unsupported, modelSelected, refreshing;
    private bool allowEdit, hasProject;
    private bool built;
    private SceneProjectSession? builtSession;
    private Guid? builtModelId;
    private bool builtHasProject, builtUnsupported;
    private int selectedIndex = -1;
    private string status = "", modelStatus = "", notice = "", diagnostics = "";
    private bool hasStatus, hasNotice;

    public EditorStyleInspectorViewModel() => Observe(this, static model => model.RefreshLocalization());

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<EditorStyleInspectorChoice> Options => options;
    internal bool IsRefreshing => refreshing;
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (refreshing || value == selectedIndex || value < 0 || value >= options.Count) return;
            selectedIndex = value;
            Changed();
            Changed(nameof(SelectedChoice));
            Changed(nameof(SelectedReference));
        }
    }
    /// <summary>Bind the actual row identity. Collection reconciliation and resource refreshes must not
    /// turn the control's transient clear into an authored choice.</summary>
    public EditorStyleInspectorChoice? SelectedChoice
    {
        get => (uint)selectedIndex < (uint)options.Count ? options[selectedIndex] : null;
        set
        {
            if (refreshing || value is null) return;
            for (int index = 0; index < options.Count; index++)
            {
                if (!ReferenceEquals(options[index], value)) continue;
                SelectedIndex = index;
                return;
            }
        }
    }
    public RenderingPresetReference? SelectedReference => SelectedChoice?.Reference;
    /// <summary>False for an empty project, a busy host, or a retained unsupported style schema.</summary>
    public bool CanEdit => allowEdit && hasProject && !unsupported && options.Count > 0;
    public bool HasSelectedModel => modelSelected;
    public bool IsModelInherited => modelSelected && !modelOverride;
    public string Status => status;
    public bool HasStatus => hasStatus;
    public string ModelStatus => modelStatus;
    public string Notice => notice;
    public bool HasNotice => hasNotice;

    /// <summary>Update presentation from the current session metadata. Options are rebuilt only when the
    /// actual selection changed, so ordinary selection/frame/theme/language refreshes do not churn the selector.</summary>
    public void Refresh(SceneProjectSession? value, Guid? selectedModel, bool canEditValue, bool hasProjectValue)
    {
        bool wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            session = value; allowEdit = canEditValue; hasProject = hasProjectValue;
            if (NeedsBuild(selectedModel))
            {
                // A new project/selection/default invalidates an older apply message; a commit reports its own after.
                status = "";
                hasStatus = false;
                Build(selectedModel);
                built = true; builtSession = session; builtModelId = selectedModel;
                builtHasProject = hasProject; builtUnsupported = unsupported;
            }
            // A rejected/cancelled commit leaves the session unchanged: follow the authoritative reference.
            selectedIndex = defaultStyle is { } actualDefault
                ? options.ToList().FindIndex(item => item.Reference == actualDefault) : -1;
            Publish();
        }
        finally { refreshing = wasRefreshing; }
    }

    private bool NeedsBuild(Guid? selectedModel)
    {
        if (!built || !ReferenceEquals(builtSession, session) || builtModelId != selectedModel
            || builtHasProject != hasProject) return true;
        if (session is null || !hasProject) return false;
        bool supported = session.HasSupportedRenderingStyles;
        if (supported == builtUnsupported) return true;
        if (!supported) return false;
        var styles = session.RenderingStyles;
        if (styles.DefaultStyle != defaultStyle) return true;
        bool hasOverride = false;
        RenderingPresetReference overridden = default;
        if (selectedModel is { } id && styles.ModelOverrides.TryGetValue(id, out var value))
        {
            hasOverride = true;
            overridden = value;
        }
        return hasOverride != modelOverride || (hasOverride && overridden != modelStyle);
    }

    public void ReportStatus(string message)
    {
        status = message ?? "";
        hasStatus = status.Length > 0;
        Changed(nameof(Status));
        Changed(nameof(HasStatus));
    }

    private void Build(Guid? selectedModel)
    {
        var list = new List<EditorStyleInspectorChoice>();
        defaultStyle = null; modelStyle = null; modelOverride = false;
        modelSelected = false; unsupported = false; diagnostics = "";
        if (session is not null && hasProject)
        {
            if (!session.HasSupportedRenderingStyles)
            {
                unsupported = true;
                diagnostics = string.Join(" ", session.RenderingStyleDiagnostics);
            }
            else
            {
                try
                {
                    var styles = session.RenderingStyles;
                    defaultStyle = styles.DefaultStyle;
                    foreach (var (style, key) in BuiltIns)
                        list.Add(ExistingChoice(RenderingPresetReference.BuiltIn(style), Text(key)));
                    if (!IsBuiltIn(styles.DefaultStyle))
                        list.Add(ExistingChoice(styles.DefaultStyle,
                            Format("Mda.Text.Context.Inspector.Style.RetainedOption", styles.DefaultStyle.StableId)));
                    if (selectedModel is { } id && session.Objects.Any(item => item.Id == id))
                    {
                        modelSelected = true;
                        modelStyle = styles.DefaultStyle;
                        if (styles.ModelOverrides.TryGetValue(id, out var overridden))
                        {
                            modelOverride = true;
                            modelStyle = overridden;
                        }
                    }
                }
                catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException)
                {
                    unsupported = true; diagnostics = error.Message;
                    defaultStyle = null; modelStyle = null; modelOverride = false; modelSelected = false;
                    list.Clear();
                }
            }
        }
        // Keep ItemsSource and unchanged rows stable through a synchronous SelectionChanged commit.
        // Replacing a list here clears the ComboBox while it is still processing the user's selection.
        for (int index = options.Count - 1; index >= 0; index--)
            if (!list.Contains(options[index])) options.RemoveAt(index);
        for (int index = 0; index < list.Count; index++)
        {
            int previous = options.IndexOf(list[index]);
            if (previous < 0) options.Insert(index, list[index]);
            else if (previous != index) options.Move(previous, index);
        }
        selectedIndex = defaultStyle is { } selected ? list.FindIndex(item => item.Reference == selected) : -1;
    }

    private EditorStyleInspectorChoice ExistingChoice(RenderingPresetReference reference, string label)
        => options.FirstOrDefault(item => item.Reference == reference)
            ?? new(reference.StableId, reference, label);

    private void Publish()
    {
        bool wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            foreach (var choice in options)
            {
                if (TryBuiltInLabel(choice.StableId, out string key)) choice.Label = Text(key);
                else choice.Label = Format("Mda.Text.Context.Inspector.Style.RetainedOption", choice.StableId);
            }
            modelStatus = !modelSelected
                ? Text("Mda.Text.Context.Inspector.Style.NoModel")
                : modelOverride
                    ? Format("Mda.Text.Context.Inspector.Style.ModelOverride", StyleLabel(modelStyle))
                    : Format("Mda.Text.Context.Inspector.Style.ModelInherit", StyleLabel(defaultStyle));
            notice = !hasProject
                ? Text("Mda.Text.Context.Inspector.Style.NoProject")
                : unsupported
                    ? Text("Mda.Text.Context.Inspector.Style.UnsupportedNotice")
                        + (diagnostics.Length == 0 ? "" : " " + Format("Mda.Text.Context.Inspector.Style.Diagnostics", diagnostics))
                    : defaultStyle is { } current && !IsBuiltIn(current)
                        ? Format("Mda.Text.Context.Inspector.Style.RetainedNotice", current.StableId)
                        : "";
            hasNotice = notice.Length > 0;
            Changed(nameof(Options)); Changed(nameof(SelectedIndex)); Changed(nameof(SelectedChoice));
            Changed(nameof(SelectedReference)); Changed(nameof(CanEdit)); Changed(nameof(HasSelectedModel));
            Changed(nameof(IsModelInherited)); Changed(nameof(Status)); Changed(nameof(HasStatus));
            Changed(nameof(ModelStatus)); Changed(nameof(Notice)); Changed(nameof(HasNotice));
        }
        finally { refreshing = wasRefreshing; }
    }

    /// <summary>Combo-box labels are resources; the stored selection and session reference stay untouched.</summary>
    private void RefreshLocalization() => Publish();

    private static bool IsBuiltIn(RenderingPresetReference reference) =>
        BuiltIns.Any(item => string.Equals(reference.StableId, SceneRenderingStyles.StableId(item.Style), StringComparison.Ordinal));

    private static bool TryBuiltInLabel(string stableId, out string key)
    {
        foreach (var (style, labelKey) in BuiltIns)
        {
            if (!string.Equals(stableId, SceneRenderingStyles.StableId(style), StringComparison.Ordinal)) continue;
            key = labelKey;
            return true;
        }
        key = "";
        return false;
    }

    private static string StyleLabel(RenderingPresetReference? reference)
    {
        if (reference is not { } value) return "";
        return TryBuiltInLabel(value.StableId, out string key)
            ? Text(key)
            : Format("Mda.Text.Context.Inspector.Style.UnknownStyle", value.StableId);
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
