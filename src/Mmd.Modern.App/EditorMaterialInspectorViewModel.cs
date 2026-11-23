using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput.D3D12;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Read-only projection of the existing model-properties material draft for the right-rail
/// Material page. Material rows, selection and the mapping summary reuse EditorModelPropertiesViewModel;
/// every edit remains in the existing model-properties window and its single workspace transaction.</summary>
public sealed class EditorMaterialInspectorViewModel : INotifyPropertyChanged
{
    private Guid? modelId;
    private string modelName = "";
    private EditorModelPropertiesViewModel? editor;
    private EditorMaterialPropertiesItem? selectedMaterial;
    private SceneProjectSession? builtSession;
    private Guid? builtModelId;
    private OriginalFrameScene? builtSource;
    private RenderingPresetReference? builtStyle;
    private int rememberedIndex;
    private D3D12MaterialBindingProfile? builtProfile;
    private EditorPluginService? pluginService;
    private D3D12ViewStyleCatalog? builtCatalog;
    private string? builtLanguage;
    private bool hasBuild, allowEdit, refreshing;

    internal void AttachPluginService(EditorPluginService? service)
    {
        if (ReferenceEquals(pluginService, service)) return;
        pluginService = service;
        InvalidateBuild();
    }

    public EditorMaterialInspectorViewModel() => Observe(this, static model => model.RefreshLocalization());

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<EditorMaterialPropertiesItem> Materials =>
        editor is null ? Array.Empty<EditorMaterialPropertiesItem>() : editor.Materials;
    public EditorMaterialPropertiesItem? SelectedMaterial
    {
        get => selectedMaterial;
        set
        {
            if (refreshing || ReferenceEquals(selectedMaterial, value)
                || (value is not null && (editor is null || !editor.Materials.Contains(value)))) return;
            selectedMaterial = value;
            if (value is not null) rememberedIndex = value.LocalMaterialIndex;
            if (editor is not null) editor.SelectedMaterial = value;
            Changed();
            Changed(nameof(SelectedMaterialIndex));
            Changed(nameof(SelectedMaterialMapping));
            Changed(nameof(HasSelectedMaterial));
            Changed(nameof(CanEdit));
        }
    }
    public int? SelectedMaterialIndex => selectedMaterial?.LocalMaterialIndex;
    /// <summary>Reuses the existing per-row mapping description text; empty when no row is selected.</summary>
    public string SelectedMaterialMapping => selectedMaterial?.ExistingMapping ?? "";
    /// <summary>The host opens the existing model-properties window on the material tab for this identity.</summary>
    public Guid? ModelObjectId => modelId;
    public bool HasModel => modelId is not null;
    public bool HasNoModel => !HasModel;
    public bool HasMaterials => editor?.HasMaterials == true;
    public bool HasExplicitProfile => builtProfile is not null;
    /// <summary>A selected model without a readable material draft must say so instead of showing blank rows.</summary>
    public bool NeedsMaterialData => HasModel && !HasMaterials;
    public bool HasSelectedMaterial => selectedMaterial is not null;
    public bool CanEdit => allowEdit && HasSelectedMaterial;
    public string ModelSummary => modelId is null ? "" : Format("Mda.Text.Context.Inspector.Material.ModelLabel", modelName);
    public string ProfileSummary => editor?.ProfileSummary ?? (HasModel ? Text("Mda.Text.3e7e2fc4e5d5") : "");

    /// <summary>Refresh page context after selection/workspace/Undo changes; the editor draft is rebuilt only
    /// when the session, selected model or explicit mapping identity changed. While another page is visible the
    /// row draft is dropped and rebuilt on page entry, so model selection during animation editing stays cheap.</summary>
    public void Refresh(EditorProjectWorkspace workspace, Guid? selectedModel, bool canEditValue, bool buildEditor)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        allowEdit = canEditValue && EditorRenderEngineOptions.UsesModernQualityPreferences(workspace.RenderEngine);
        var nextSession = workspace.Session;
        var item = selectedModel is { } id && nextSession is { IsDisposed: false }
            ? nextSession.Objects.FirstOrDefault(value => value.Id == id) : null;
        selectedModel = item?.Id;
        var nextProfile = item is null ? null : workspace.MaterialBindings.GetValueOrDefault(item.Id);
        var nextStyle = item is not null && nextSession!.HasSupportedRenderingStyles
            ? nextSession.GetRenderingStyle(item.Id) : (RenderingPresetReference?)null;
        if (modelId != selectedModel) rememberedIndex = 0;
        modelId = selectedModel;
        modelName = item?.Name ?? "";
        var currentCatalog = pluginService?.CurrentStyleCatalog ?? workspace.StyleCatalog;
        string currentLanguage = CurrentLanguage;
        bool signatureChanged = !hasBuild || !ReferenceEquals(builtSession, nextSession)
            || builtModelId != selectedModel || !ReferenceEquals(builtProfile, nextProfile)
            || !ReferenceEquals(builtSource, item?.Source) || builtStyle != nextStyle
            || !ReferenceEquals(builtCatalog, currentCatalog)
            || !string.Equals(builtLanguage, currentLanguage, StringComparison.Ordinal);
        if (item is null || !buildEditor || nextStyle is null)
        {
            editor = null;
            selectedMaterial = null;
            InvalidateBuild();
        }
        else if (signatureChanged)
        {
            hasBuild = true; builtSession = nextSession; builtModelId = selectedModel; builtProfile = nextProfile;
            builtSource = item.Source; builtStyle = nextStyle; builtCatalog = currentCatalog; builtLanguage = currentLanguage;
            try
            {
                var built = new EditorModelPropertiesViewModel(workspace, item.Id);
                var match = built.Materials.FirstOrDefault(row => row.LocalMaterialIndex == rememberedIndex)
                    ?? built.Materials.FirstOrDefault();
                built.SelectedMaterial = match;
                editor = built;
                selectedMaterial = match;
                rememberedIndex = match?.LocalMaterialIndex ?? 0;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException
                or NotSupportedException or InvalidDataException or KeyNotFoundException)
            {
                editor = null;
                selectedMaterial = null;
            }
        }
        Publish();
    }

    private void InvalidateBuild()
    {
        hasBuild = false;
        builtSession = null;
        builtModelId = null;
        builtSource = null;
        builtStyle = null;
        builtProfile = null;
        builtCatalog = null;
        builtLanguage = null;
    }

    private void Publish()
    {
        // Suppress a ListBox SelectedItem write-back while ItemsSource/selection notifications replace rows;
        // the explicit SelectedMaterial notification below restores the preserved row identity.
        refreshing = true;
        try
        {
            foreach (string name in new[] { nameof(Materials), nameof(SelectedMaterial), nameof(SelectedMaterialIndex),
                nameof(ModelObjectId), nameof(HasModel), nameof(HasNoModel), nameof(HasMaterials), nameof(HasSelectedMaterial), nameof(HasExplicitProfile), nameof(NeedsMaterialData),
                nameof(CanEdit), nameof(ModelSummary), nameof(ProfileSummary), nameof(SelectedMaterialMapping) })
                Changed(name);
        }
        finally { refreshing = false; }
    }

    /// <summary>The reused draft owns its own localization subscription; this page re-publishes its summary text.</summary>
    private void RefreshLocalization() => Publish();

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
