using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Plugins;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Deterministic presentation ordering helper shared across style selectors.</summary>
internal static class EditorStyleCatalogOrdering
{
    internal static IReadOnlyList<IRenderingStyleDefinition> OrderStyles(IEnumerable<IRenderingStyleDefinition> styles)
        => styles
            .Where(static style => style is not null && !string.Equals(style.StyleId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            .OrderBy(static style => BundledSortOrder(style.StyleId))
            .ThenBy(static style => style.StyleId, StringComparer.Ordinal)
            .ToArray();

    private static int BundledSortOrder(string styleId) => styleId switch
    {
        "genshin" => 0,
        "star-rail" => 1,
        "wuthering-waves" => 2,
        "endfield" => 3,
        _ => 100
    };
}

/// <summary>One selectable row of the project-default style selector. Rows mirror active style
/// plugins plus the host MMD baseline; a retained row keeps an unknown or unloaded reference visible.</summary>
public sealed class EditorStyleInspectorChoice : INotifyPropertyChanged
{
    private string label;
    private string sourceName = "";
    private string sourceTag = "";
    private string author = "";
    private string version = "";
    private string description = "";
    private bool isRetained;

    public EditorStyleInspectorChoice(string stableId, RenderingPresetReference reference, string label)
    {
        StableId = stableId;
        Reference = reference;
        this.label = label;
    }

    public string StableId { get; }
    public RenderingPresetReference Reference { get; }
    public bool IsBuiltIn => string.Equals(StableId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal);
    public bool IsRetained
    {
        get => isRetained;
        internal set
        {
            if (isRetained == value) return;
            isRetained = value;
            PropertyChanged?.Invoke(this, new(nameof(IsRetained)));
            PropertyChanged?.Invoke(this, new(nameof(StyleSummary)));
        }
    }
    public string? PluginId { get; internal set; }
    public string Label
    {
        get => label;
        internal set
        {
            if (string.Equals(label, value, StringComparison.Ordinal)) return;
            label = value;
            PropertyChanged?.Invoke(this, new(nameof(Label)));
            PropertyChanged?.Invoke(this, new(nameof(StyleSummary)));
        }
    }
    public string SourceName
    {
        get => sourceName;
        internal set
        {
            if (string.Equals(sourceName, value, StringComparison.Ordinal)) return;
            sourceName = value;
            PropertyChanged?.Invoke(this, new(nameof(SourceName)));
            PropertyChanged?.Invoke(this, new(nameof(StyleSummary)));
        }
    }
    public string SourceTag
    {
        get => sourceTag;
        internal set
        {
            if (string.Equals(sourceTag, value, StringComparison.Ordinal)) return;
            sourceTag = value;
            PropertyChanged?.Invoke(this, new(nameof(SourceTag)));
            PropertyChanged?.Invoke(this, new(nameof(HasSourceTag)));
        }
    }
    public bool HasSourceTag => !string.IsNullOrWhiteSpace(SourceTag);
    public string Author
    {
        get => author;
        internal set
        {
            if (string.Equals(author, value, StringComparison.Ordinal)) return;
            author = value;
            PropertyChanged?.Invoke(this, new(nameof(Author)));
        }
    }
    public string Version
    {
        get => version;
        internal set
        {
            if (string.Equals(version, value, StringComparison.Ordinal)) return;
            version = value;
            PropertyChanged?.Invoke(this, new(nameof(Version)));
            PropertyChanged?.Invoke(this, new(nameof(StyleSummary)));
        }
    }
    public string Description
    {
        get => description;
        internal set
        {
            if (string.Equals(description, value, StringComparison.Ordinal)) return;
            description = value;
            PropertyChanged?.Invoke(this, new(nameof(Description)));
        }
    }
    public string StyleSummary => IsRetained
        ? Label
        : string.IsNullOrWhiteSpace(Version) ? $"{Label} · {SourceName}" : $"{Label} ({SourceName} v{Version})";

    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Label;
}

/// <summary>Project-default style metadata page. It reads the live style plugins and session selection;
/// switching pages never dirties a project, while explicit selection executes one workspace transaction.</summary>
public sealed class EditorStyleInspectorViewModel : INotifyPropertyChanged
{
    private EditorProjectWorkspace? workspace;
    private SceneProjectSession? session;
    private EditorPluginService? pluginService;
    private D3D12ViewStyleCatalog activeCatalog = D3D12ViewStyleCatalog.Empty;
    private readonly ObservableCollection<EditorStyleInspectorChoice> options = [];
    private RenderingPresetReference? defaultStyle, modelStyle;
    private bool modelOverride, unsupported, modelSelected, refreshing;
    private bool allowEdit, hasProject;
    private bool isModernEngine = true;
    private bool guessMaterialRoles = true;
    private bool built;
    private SceneProjectSession? builtSession;
    private EditorPluginService? builtPluginService;
    private D3D12ViewStyleCatalog? builtCatalog;
    private string? builtLanguage;
    private Guid? builtModelId;
    private bool builtHasProject, builtUnsupported, builtIsModernEngine = true;
    private int selectedIndex = -1;
    private string status = "", modelStatus = "", notice = "", diagnostics = "";
    private string? missingStyleId, missingStylePluginId;
    private string missingStylePluginName = "";
    private int missingStyleCount;
    private bool hasStatus, hasNotice, canEnableMissingStylePlugin;

    public EditorStyleInspectorViewModel() => Observe(this, static model => model.RefreshLocalization());

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<EditorStyleInspectorChoice> Options => options;
    internal bool IsRefreshing => refreshing;

    internal void AttachWorkspace(EditorProjectWorkspace? owner)
    {
        if (ReferenceEquals(workspace, owner)) return;
        workspace = owner;
        isModernEngine = owner is null || EditorRenderEngineOptions.UsesModernQualityPreferences(owner.RenderEngine);
        guessMaterialRoles = owner?.GuessMaterialRoles ?? true;
        built = false;
    }

    internal void Detach()
    {
        workspace = null;
        pluginService = null;
        activeCatalog = D3D12ViewStyleCatalog.Empty;
        built = false;
        Refresh(null, null, false, false);
    }

    internal void AttachPluginService(EditorPluginService? service)
    {
        if (ReferenceEquals(pluginService, service)) return;
        pluginService = service;
        built = false;
        activeCatalog = service?.CurrentStyleCatalog ?? D3D12ViewStyleCatalog.Empty;
        if (session is { IsDisposed: true })
        {
            session = null;
            builtSession = null;
            hasProject = false;
        }
        if (hasProject || session is not null)
            Refresh(session, builtModelId, allowEdit, hasProject);
        else
            Publish();
    }

    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (refreshing || !CanEdit || value == selectedIndex || value < 0 || value >= options.Count) return;
            selectedIndex = value;
            Changed();
            Changed(nameof(SelectedChoice));
            Changed(nameof(SelectedReference));
            NotifySelectedStyleMetadata();
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
    public bool IsModernEngine => isModernEngine;
    public bool IsClassicEngine => !isModernEngine;
    public string ClassicNotice => Text("Mda.Text.Context.Inspector.Style.ClassicNotice");
    public bool GuessMaterialRoles => guessMaterialRoles;
    public bool CanEditGuessMaterialRoles => allowEdit && hasProject && isModernEngine && !unsupported;
    /// <summary>False for an empty project, Classic engine, a busy host, or a retained unsupported style schema.</summary>
    public bool CanEdit => allowEdit && hasProject && isModernEngine && !unsupported && options.Count > 0;
    public bool HasSelectedModel => modelSelected;
    public bool CanEditSelectedModelStyle => CanEdit && modelSelected;
    public bool CanInheritSelectedModelStyle => CanEditSelectedModelStyle && modelOverride;
    public EditorStyleInspectorChoice? ModelOverrideChoice => modelOverride ? options.FirstOrDefault(choice => choice.Reference == modelStyle) : null;
    public string ModelStylePlaceholder => modelOverride && modelStyle is { } retained
        ? Format("Mda.Text.Context.Inspector.Style.RetainedOption", retained.StableId)
        : Text("Mda.Text.Context.Inspector.Style.InheritSelected");
    public bool IsModelInherited => modelSelected && !modelOverride;
    public string Status => status;
    public bool HasStatus => hasStatus;
    public string ModelStatus => modelStatus;
    public string Notice => notice;
    public bool HasNotice => hasNotice;
    public string SelectedStyleSource => SelectedChoice?.SourceName ?? "";
    public string SelectedStyleSourceTag => SelectedChoice?.SourceTag ?? "";
    public string SelectedStyleAuthor => SelectedChoice?.Author ?? "";
    public string SelectedStyleVersion => SelectedChoice?.Version ?? "";
    public string SelectedStyleDescription => SelectedChoice?.Description ?? "";
    public string SelectedStyleMetaSummary => BuildSelectedStyleMetaSummary();
    public bool HasSelectedStyleMetadata => SelectedChoice is { IsRetained: false };
    public bool HasSelectedStyleAuthor => !string.IsNullOrWhiteSpace(SelectedStyleAuthor);
    public bool HasSelectedStyleVersion => !string.IsNullOrWhiteSpace(SelectedStyleVersion);
    public bool HasSelectedStyleDescription => !string.IsNullOrWhiteSpace(SelectedStyleDescription);
    public bool CanManagePlugins => allowEdit && pluginService is not null;
    public string? MissingStyleId => missingStyleId;
    public string? MissingStylePluginId => missingStylePluginId;
    public string MissingStylePluginName => missingStylePluginName;
    public int MissingStyleCount => missingStyleCount;
    public bool HasMissingStyle => isModernEngine && !unsupported && missingStyleCount > 0;
    public bool HasMissingStylePlugin => !string.IsNullOrWhiteSpace(missingStylePluginId);
    public bool CanEnableMissingStylePlugin => isModernEngine && canEnableMissingStylePlugin;
    public bool CanOpenStyleReplacementDialog => allowEdit && hasProject && isModernEngine && !unsupported && missingStyleCount > 0;

    public Task<PluginOperationResult> EnablePluginForStyleAsync(string styleId, CancellationToken cancellationToken = default)
        => pluginService is null
            ? Task.FromResult(new PluginOperationResult(false, Text("Mda.Text.Context.Inspector.Style.PluginServiceNotReady")))
            : pluginService.EnablePluginForStyleAsync(styleId, cancellationToken);

    public Task<PluginOperationResult> EnableMissingStylePluginAsync(CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(missingStyleId)
            ? Task.FromResult(new PluginOperationResult(false, Text("Mda.Text.Context.Inspector.Style.NoPendingPlugin")))
            : EnablePluginForStyleAsync(missingStyleId, cancellationToken);

    /// <summary>Update presentation from the current session metadata. Options are rebuilt when the
    /// project selection, active plugin catalog, engine mode, or language changes.</summary>
    public void Refresh(SceneProjectSession? value, Guid? selectedModel, bool canEditValue, bool hasProjectValue)
    {
        bool wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            if (value is { IsDisposed: true })
            {
                value = null;
                hasProjectValue = false;
                builtSession = null;
                built = false;
            }
            session = value; allowEdit = canEditValue; hasProject = hasProjectValue;
            isModernEngine = workspace is null || EditorRenderEngineOptions.UsesModernQualityPreferences(workspace.RenderEngine);
            guessMaterialRoles = workspace?.GuessMaterialRoles ?? true;
            activeCatalog = pluginService?.CurrentStyleCatalog ?? D3D12ViewStyleCatalog.Empty;
            if (NeedsBuild(selectedModel))
            {
                // A new project/selection/default invalidates an older apply message; a commit reports its own after.
                status = "";
                hasStatus = false;
                Build(selectedModel);
                foreach (var choice in options)
                    UpdateChoiceMetadata(choice, activeCatalog, CurrentLanguage);
                built = true;
                builtSession = session;
                builtModelId = selectedModel;
                builtHasProject = hasProject;
                builtUnsupported = unsupported;
                builtIsModernEngine = isModernEngine;
                builtPluginService = pluginService;
                builtCatalog = activeCatalog;
                builtLanguage = CurrentLanguage;
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
        if (!built
            || !ReferenceEquals(builtSession, session)
            || builtModelId != selectedModel
            || builtHasProject != hasProject
            || builtIsModernEngine != isModernEngine
            || !ReferenceEquals(builtPluginService, pluginService)
            || !ReferenceEquals(builtCatalog, activeCatalog)
            || !string.Equals(builtLanguage, CurrentLanguage, StringComparison.Ordinal))
            return true;
        if (!isModernEngine || session is not { IsDisposed: false } || !hasProject) return false;
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
        if (isModernEngine && session is { IsDisposed: false } && hasProject)
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
                    var mmdRef = RenderingPresetReference.BuiltIn(RenderingStyleId.Mmd);
                    list.Add(ExistingChoice(mmdRef, Text("Mda.Text.Context.Inspector.Style.Option.Mmd")));
                    foreach (var def in EditorStyleCatalogOrdering.OrderStyles(activeCatalog.Styles))
                    {
                        var reference = new RenderingPresetReference(def.StyleId);
                        list.Add(ExistingChoice(reference, ResolveActiveStyleLabel(def)));
                    }

                    if (!IsKnownActive(styles.DefaultStyle))
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
            string lang = CurrentLanguage;
            var liveCatalog = activeCatalog;

            missingStyleId = null;
            missingStylePluginId = null;
            missingStylePluginName = "";
            missingStyleCount = 0;
            canEnableMissingStylePlugin = false;

            if (isModernEngine && !unsupported && session is { IsDisposed: false } && session.HasSupportedRenderingStyles)
            {
                var styles = session.RenderingStyles;
                var missingDistinct = new List<string>();
                void CollectMissing(RenderingPresetReference reference)
                {
                    if (!IsKnownActive(reference, liveCatalog) && !missingDistinct.Contains(reference.StableId, StringComparer.Ordinal))
                        missingDistinct.Add(reference.StableId);
                }

                CollectMissing(styles.DefaultStyle);
                foreach (var overridden in styles.ModelOverrides.Values)
                    CollectMissing(overridden);

                missingStyleCount = missingDistinct.Count;
                if (defaultStyle is { } currentDefault && !IsKnownActive(currentDefault, liveCatalog))
                    missingStyleId = currentDefault.StableId;
                else if (modelSelected && modelStyle is { } currentModel && !IsKnownActive(currentModel, liveCatalog))
                    missingStyleId = currentModel.StableId;
                else
                {
                    missingStyleId = missingDistinct.FirstOrDefault(id => pluginService?.FindPluginForStyle(id)?.State == PluginState.Disabled)
                        ?? missingDistinct.FirstOrDefault();
                }
            }

            PluginInfo? missingPlugin = missingStyleId is not null ? pluginService?.FindPluginForStyle(missingStyleId) : null;
            if (missingPlugin is not null)
            {
                missingStylePluginId = missingPlugin.Id;
                missingStylePluginName = missingPlugin.GetDisplayName(lang);
                canEnableMissingStylePlugin = allowEdit && isModernEngine && missingPlugin.State == PluginState.Disabled;
            }

            string modelName = modelSelected && session is not null
                ? session.Objects.FirstOrDefault(item => item.Id == builtModelId)?.Name ?? "" : "";
            modelStatus = !modelSelected
                ? Text("Mda.Text.Context.Inspector.Style.NoModel")
                : modelOverride
                    ? FormatModelStyleStatus(modelStyle, modelName, liveCatalog, lang,
                        "Mda.Text.Context.Inspector.Style.ModelOverride",
                        "Mda.Text.Ux.Inspector.Style.ModelOverrideMissing")
                    : FormatModelStyleStatus(defaultStyle, modelName, liveCatalog, lang,
                        "Mda.Text.Context.Inspector.Style.ModelInherit",
                        "Mda.Text.Ux.Inspector.Style.ModelInheritMissing");

            notice = BuildNotice(liveCatalog, missingPlugin, lang);
            hasNotice = notice.Length > 0;

            Changed(nameof(Options)); Changed(nameof(SelectedIndex)); Changed(nameof(SelectedChoice));
            Changed(nameof(SelectedReference)); Changed(nameof(CanEdit)); Changed(nameof(HasSelectedModel));
            Changed(nameof(IsModernEngine)); Changed(nameof(IsClassicEngine)); Changed(nameof(ClassicNotice));
            Changed(nameof(GuessMaterialRoles)); Changed(nameof(CanEditGuessMaterialRoles));
            Changed(nameof(CanEditSelectedModelStyle)); Changed(nameof(CanInheritSelectedModelStyle));
            Changed(nameof(ModelStylePlaceholder)); Changed(nameof(ModelOverrideChoice)); Changed(nameof(IsModelInherited)); Changed(nameof(Status)); Changed(nameof(HasStatus));
            Changed(nameof(ModelStatus)); Changed(nameof(Notice)); Changed(nameof(HasNotice));
            Changed(nameof(MissingStyleId)); Changed(nameof(MissingStylePluginId));
            Changed(nameof(MissingStylePluginName)); Changed(nameof(MissingStyleCount));
            Changed(nameof(HasMissingStyle)); Changed(nameof(HasMissingStylePlugin));
            Changed(nameof(CanEnableMissingStylePlugin)); Changed(nameof(CanOpenStyleReplacementDialog));
            Changed(nameof(CanManagePlugins));
            NotifySelectedStyleMetadata();
        }
        finally { refreshing = wasRefreshing; }
    }

    private string BuildNotice(D3D12ViewStyleCatalog liveCatalog, PluginInfo? missingPlugin, string lang)
    {
        if (!hasProject)
            return Text("Mda.Text.Context.Inspector.Style.NoProject");
        if (!isModernEngine)
            return "";
        if (unsupported)
            return Text("Mda.Text.Context.Inspector.Style.UnsupportedNotice")
                + (diagnostics.Length == 0 ? "" : " " + Format("Mda.Text.Context.Inspector.Style.Diagnostics", diagnostics));
        if (missingStyleId is null)
            return "";

        string baseNotice = missingStyleCount > 1
            ? Format("Mda.Text.Context.Inspector.Style.MissingSummaryNotice", missingStyleCount)
            : Format("Mda.Text.Context.Inspector.Style.RetainedNotice", missingStyleId);
        if (missingPlugin is null)
            return baseNotice;
        if (missingPlugin.State == PluginState.Disabled)
            return baseNotice + " " + Format("Mda.Text.Context.Inspector.Style.MissingPluginDisabled", missingPlugin.GetDisplayName(lang));
        if (!string.IsNullOrWhiteSpace(missingPlugin.Diagnostic))
            return baseNotice + " " + missingPlugin.Diagnostic;
        return baseNotice;
    }

    private void UpdateChoiceMetadata(EditorStyleInspectorChoice choice, D3D12ViewStyleCatalog catalog, string language)
    {
        if (choice.IsBuiltIn)
        {
            choice.IsRetained = false;
            choice.PluginId = null;
            choice.Label = Text("Mda.Text.Context.Inspector.Style.Option.Mmd");
            choice.SourceName = Text("Mda.Text.Context.Inspector.Style.Source.BuiltIn");
            choice.SourceTag = Text("Mda.Text.Context.Inspector.Style.Source.BuiltIn");
            choice.Author = "MDA";
            choice.Version = EditorAboutInfo.Current.Version;
            choice.Description = Text("Mda.Text.Context.Inspector.Style.MmdDescription");
            return;
        }

        if (catalog.FindStyle(choice.StableId) is { } definition)
        {
            var plugin = pluginService?.FindPluginForStyle(choice.StableId);
            bool bundled = plugin is not null && (pluginService?.IsBundledDefaultPlugin(plugin.Id) ?? false);
            choice.IsRetained = false;
            choice.PluginId = plugin?.Id;
            choice.Label = ResolveActiveStyleLabel(definition, language);
            choice.SourceName = plugin?.GetDisplayName(language) ?? choice.StableId;
            choice.SourceTag = bundled
                ? Text("Mda.Text.Context.Inspector.Style.Source.Bundled")
                : Text("Mda.Text.Context.Inspector.Style.Source.Extension");
            choice.Author = !string.IsNullOrWhiteSpace(definition.Author)
                ? definition.Author
                : plugin?.Author ?? "";
            choice.Version = definition.Version?.ToString()
                ?? plugin?.Version.ToString()
                ?? "";
            choice.Description = definition.Description?.Resolve(language)
                ?? plugin?.GetDescription(language)
                ?? "";
            return;
        }

        choice.IsRetained = true;
        var unavailablePlugin = pluginService?.FindPluginForStyle(choice.StableId);
        choice.PluginId = unavailablePlugin?.Id;
        choice.Label = Format("Mda.Text.Context.Inspector.Style.RetainedOption", choice.StableId);
        choice.SourceName = unavailablePlugin?.GetDisplayName(language) ?? Text("Mda.Text.Context.Inspector.Style.Source.Unloaded");
        choice.SourceTag = Text("Mda.Text.Context.Inspector.Style.Source.Unloaded");
        choice.Author = "";
        choice.Version = "";
        choice.Description = "";
    }

    private void NotifySelectedStyleMetadata()
    {
        Changed(nameof(SelectedStyleSource));
        Changed(nameof(SelectedStyleSourceTag));
        Changed(nameof(SelectedStyleAuthor));
        Changed(nameof(SelectedStyleVersion));
        Changed(nameof(SelectedStyleDescription));
        Changed(nameof(SelectedStyleMetaSummary));
        Changed(nameof(HasSelectedStyleMetadata));
        Changed(nameof(HasSelectedStyleAuthor));
        Changed(nameof(HasSelectedStyleVersion));
        Changed(nameof(HasSelectedStyleDescription));
    }

    private string BuildSelectedStyleMetaSummary()
    {
        if (!HasSelectedStyleMetadata) return "";
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(SelectedStyleSourceTag))
            parts.Add(SelectedStyleSourceTag);
        if (!string.IsNullOrWhiteSpace(SelectedStyleVersion))
            parts.Add(SelectedStyleVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? SelectedStyleVersion
                : "v" + SelectedStyleVersion);
        if (!string.IsNullOrWhiteSpace(SelectedStyleAuthor))
            parts.Add(SelectedStyleAuthor);
        return string.Join(" · ", parts);
    }

    private static string FormatModelStyleStatus(
        RenderingPresetReference? reference,
        string modelName,
        D3D12ViewStyleCatalog catalog,
        string language,
        string knownFormatKey,
        string missingFormatKey)
    {
        if (reference is { } value && !IsKnownActive(value, catalog))
            return Format(missingFormatKey, value.StableId, modelName);
        return Format(knownFormatKey, StyleLabel(reference, catalog, language), modelName);
    }

    /// <summary>Combo-box labels are resources; the stored selection and session reference stay untouched.</summary>
    private void RefreshLocalization()
    {
        if (session is { IsDisposed: true })
        {
            Refresh(null, null, false, false);
            return;
        }
        if (hasProject || session is not null)
            Refresh(session, builtModelId, allowEdit, hasProject);
        else
            Publish();
    }

    private bool IsKnownActive(RenderingPresetReference reference)
        => IsKnownActive(reference, activeCatalog);

    private static bool IsKnownActive(RenderingPresetReference reference, D3D12ViewStyleCatalog catalog)
        => string.Equals(reference.StableId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal)
            || catalog.FindStyle(reference.StableId) is not null;

    private static string ResolveActiveStyleLabel(IRenderingStyleDefinition definition, string? language = null)
    {
        string lang = string.IsNullOrWhiteSpace(language) ? CurrentLanguage : language;
        string? resolved = definition.DisplayName?.Resolve(lang);
        return string.IsNullOrWhiteSpace(resolved) ? definition.StyleId : resolved;
    }

    private static string StyleLabel(RenderingPresetReference? reference, D3D12ViewStyleCatalog catalog, string language)
    {
        if (reference is not { } value) return "";
        if (string.Equals(value.StableId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            return Text("Mda.Text.Context.Inspector.Style.Option.Mmd");
        if (catalog.FindStyle(value.StableId) is { } definition)
            return ResolveActiveStyleLabel(definition, language);
        return Format("Mda.Text.Context.Inspector.Style.UnknownStyle", value.StableId);
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
