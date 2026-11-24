namespace Mmd.Modern.App;

/// <summary>The four compact right-rail editor pages. The page is user intent, not a selection mirror.</summary>
public enum EditorInspectorPage
{
    Animation = 0,
    Style = 1,
    Material = 2,
    Lighting = 3,
}

/// <summary>Independent right-inspector page state. Selection, frame, theme, language and workspace refreshes
/// update page content so they never steal a page the user chose; only an explicit edit entry navigates.</summary>
public sealed partial class EditorShellViewModel
{
    private EditorInspectorPage inspectorPage = EditorInspectorPage.Style;

    public EditorStyleInspectorViewModel StyleInspector { get; } = new();
    public EditorMaterialInspectorViewModel MaterialInspector { get; } = new();
    internal EditorPluginService? PluginService { get; private set; }

    internal void AttachPluginService(EditorPluginService? service)
    {
        PluginService = service;
        Workspace.AttachPluginService(service);
        StyleInspector.AttachWorkspace(Workspace);
        StyleInspector.AttachPluginService(service);
        MaterialInspector.AttachPluginService(service);
        RefreshInspectorPages();
    }

    internal void OnPluginsChanged()
    {
        RenderBindingRevision++;
        RefreshInspectorPages();
        ViewStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public EditorInspectorPage InspectorPage => inspectorPage;
    public int RightInspectorPageIndex
    {
        get => (int)inspectorPage;
        set
        {
            // Older recovery files and shortcuts use index zero for the former animation page.
            if (value == (int)EditorInspectorPage.Animation ||
                value == (int)EditorInspectorPage.Material && !SupportsModernLightingWorkspace)
                value = (int)EditorInspectorPage.Style;
            if (value < (int)EditorInspectorPage.Animation || value > (int)EditorInspectorPage.Lighting
                || (int)inspectorPage == value) return;
            inspectorPage = (EditorInspectorPage)value;
            OnPropertyChanged(nameof(InspectorPage));
            OnPropertyChanged(nameof(RightInspectorPageIndex));
            OnPropertyChanged(nameof(IsAnimationPage));
            OnPropertyChanged(nameof(IsStylePage));
            OnPropertyChanged(nameof(IsMaterialPage));
            OnPropertyChanged(nameof(IsLightingPage));
            OnPropertyChanged(nameof(IsModernLightingPage));
            OnPropertyChanged(nameof(WorkspacePageIndex));
        }
    }
    public bool IsAnimationPage => inspectorPage == EditorInspectorPage.Animation;
    public bool IsStylePage => inspectorPage == EditorInspectorPage.Style;
    public bool IsMaterialPage => inspectorPage == EditorInspectorPage.Material;
    public bool IsLightingPage => inspectorPage == EditorInspectorPage.Lighting;
    public bool SupportsModernLightingWorkspace => Workspace.ProjectFormat == EditorProjectFormat.Modern;
    public bool IsModernLightingPage => IsLightingPage && SupportsModernLightingWorkspace;
    public int WorkspacePageIndex => IsModernLightingPage ? 1 : 0;

    /// <summary>Explicit navigation entry used by edit actions and direct light interaction.</summary>
    public void ShowInspectorPage(EditorInspectorPage page) => RightInspectorPageIndex = (int)page;

    /// <summary>Refresh page context from the current workspace and selection. Page choice is untouched.</summary>
    internal void RefreshInspectorPages()
    {
        // A shell constructed before Avalonia initialization (headless tooling) has no resource catalog yet;
        // page context is filled again by the next selection/workspace refresh after the host attaches.
        if (Avalonia.Application.Current is null) return;
        StyleInspector.AttachWorkspace(Workspace);
        StyleInspector.Refresh(Workspace.Session, Browser.SelectedModel?.ObjectId, !IsBusy, HasProject);
        // The material row draft is only materialized for the visible page; other pages keep selection cheap.
        MaterialInspector.Refresh(Workspace, Browser.SelectedModel?.ObjectId, !IsBusy, InspectorPage == EditorInspectorPage.Material);
    }
}
