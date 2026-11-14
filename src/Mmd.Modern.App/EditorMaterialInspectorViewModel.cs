using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
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
    private D3D12MaterialBindingProfile? builtProfile;
    private bool hasBuild, allowEdit, refreshing;

    public EditorMaterialInspectorViewModel() => Observe(this, static model => model.RefreshLocalization());

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<EditorMaterialPropertiesItem> Materials =>
        editor is null ? Array.Empty<EditorMaterialPropertiesItem>() : editor.Materials;
    public EditorMaterialPropertiesItem? SelectedMaterial
    {
        get => selectedMaterial;
        set
        {
            if (refreshing || ReferenceEquals(selectedMaterial, value)) return;
            selectedMaterial = value;
            if (editor is not null) editor.SelectedMaterial = value;
            Changed();
            Changed(nameof(SelectedMaterialIndex));
            Changed(nameof(SelectedMaterialMapping));
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
    /// <summary>A selected model without a readable material draft must say so instead of showing blank rows.</summary>
    public bool NeedsMaterialData => HasModel && !HasMaterials;
    public bool CanEdit => allowEdit && editor is not null && editor.HasMaterials;
    public string ModelSummary => modelId is null ? "" : Format("Mda.Text.Context.Inspector.Material.ModelLabel", modelName);
    public string ProfileSummary => editor?.ProfileSummary ?? (HasModel ? Text("Mda.Text.3e7e2fc4e5d5") : "");

    /// <summary>Refresh page context after selection/workspace/Undo changes; the editor draft is rebuilt only
    /// when the session, selected model or explicit mapping identity changed. While another page is visible the
    /// row draft is dropped and rebuilt on page entry, so model selection during animation editing stays cheap.</summary>
    public void Refresh(EditorProjectWorkspace workspace, Guid? selectedModel, bool canEditValue, bool buildEditor)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        allowEdit = canEditValue;
        var nextSession = workspace.Session;
        var nextProfile = selectedModel is { } model && nextSession is not null
            ? workspace.MaterialBindings.GetValueOrDefault(model) : null;
        bool sameModel = modelId == selectedModel;
        int keepIndex = sameModel ? selectedMaterial?.LocalMaterialIndex ?? 0 : 0;
        modelId = selectedModel;
        modelName = selectedModel is { } id && nextSession is not null
            ? nextSession.Objects.FirstOrDefault(item => item.Id == id)?.Name ?? "" : "";
        bool signatureChanged = !hasBuild || !ReferenceEquals(builtSession, nextSession)
            || builtModelId != selectedModel || !ReferenceEquals(builtProfile, nextProfile);
        if (selectedModel is not null && nextSession is not null && signatureChanged)
        {
            // Older/future style schemas stay loadable but the existing material draft cannot read them.
            // The style page reports that diagnostic; this page stays read-only with no rows.
            if (!nextSession.HasSupportedRenderingStyles)
            {
                editor = null;
                selectedMaterial = null;
                InvalidateBuild();
            }
            else if (!buildEditor)
            {
                editor = null;
                selectedMaterial = null;
                InvalidateBuild();
            }
            else
            {
                hasBuild = true; builtSession = nextSession; builtModelId = selectedModel; builtProfile = nextProfile;
                try
                {
                    var built = new EditorModelPropertiesViewModel(workspace, selectedModel.Value);
                    var match = built.Materials.FirstOrDefault(item => item.LocalMaterialIndex == keepIndex)
                        ?? built.Materials.FirstOrDefault();
                    built.SelectedMaterial = match;
                    editor = built;
                    selectedMaterial = match;
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException
                    or NotSupportedException or InvalidDataException or KeyNotFoundException)
                {
                    editor = null;
                    selectedMaterial = null;
                }
            }
        }
        else if (selectedModel is null || nextSession is null)
        {
            editor = null;
            selectedMaterial = null;
            InvalidateBuild();
        }
        Publish();
    }

    private void InvalidateBuild()
    {
        hasBuild = false;
        builtSession = null;
        builtModelId = null;
        builtProfile = null;
    }

    private void Publish()
    {
        // Suppress a ListBox SelectedItem write-back while ItemsSource/selection notifications replace rows;
        // the explicit SelectedMaterial notification below restores the preserved row identity.
        refreshing = true;
        try
        {
            foreach (string name in new[] { nameof(Materials), nameof(SelectedMaterial), nameof(SelectedMaterialIndex),
                nameof(ModelObjectId), nameof(HasModel), nameof(HasNoModel), nameof(HasMaterials), nameof(NeedsMaterialData),
                nameof(CanEdit), nameof(ModelSummary), nameof(ProfileSummary), nameof(SelectedMaterialMapping) })
                Changed(name);
        }
        finally { refreshing = false; }
    }

    /// <summary>The reused draft owns its own localization subscription; this page re-publishes its summary text.</summary>
    private void RefreshLocalization() => Publish();

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
