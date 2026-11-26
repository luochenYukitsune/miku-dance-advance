using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Mmd.Modern.App.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

public sealed record EditorMorphDockChoice(int Index, string Name, byte Panel);

public partial class EditorMorphDock : UserControl
{
    private EditorShellViewModel? shell;
    private PmxModel? lastModel;
    private VmdMotion? lastMotion;
    private MmdMorphMotionAdapter? sampler;
    private readonly Dictionary<string, int> morphNames = new(StringComparer.Ordinal);
    private EditorMorphDockChoice[][] panelChoices = [[], [], [], []];
    private bool scheduled;
    private IPointer? editingPointer;
    private EditorMorphDockChoice? editingChoice;
    internal IReadOnlyList<EditorMorphDockGroup> Groups { get; }

    public EditorMorphDock()
    {
        InitializeComponent();
        Groups = new[] { "Brow", "Eye", "Mouth", "Other" }.Select(key => new EditorMorphDockGroup(this, key)).ToArray();
        MorphGroups.ItemsSource = Groups;
        AddHandler(PointerPressedEvent, BeginSliderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, args) => EndSliderGesture(args.Pointer), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, args) => EndSliderGesture(args.Pointer), RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += (_, _) => Attach(DataContext as EditorShellViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
        AttachedToVisualTree += (_, _) => Attach(DataContext as EditorShellViewModel);
        EditorLocalization.Observe(this, static dock => { foreach (var group in dock.Groups) group.UpdateLabel(); });
    }

    private void Attach(EditorShellViewModel? value)
    {
        if (ReferenceEquals(value, shell)) return;
        if (shell is not null)
        {
            shell.Animation.PropertyChanged -= AnimationChanged;
            shell.Animation.DraftContentEdited -= DraftContentChanged;
            shell.PropertyChanged -= ShellChanged;
        }
        shell = value;
        if (shell is not null)
        {
            shell.Animation.PropertyChanged += AnimationChanged;
            shell.Animation.DraftContentEdited += DraftContentChanged;
            shell.PropertyChanged += ShellChanged;
        }
        RefreshGroups();
    }

    private void DraftContentChanged(object? sender, EventArgs args) => ScheduleRefresh();

    private void AnimationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(EditorAnimationViewModel.FrameSummary) or nameof(EditorAnimationViewModel.ObjectId)
            or nameof(EditorAnimationViewModel.SelectedMorphIndex) or nameof(EditorAnimationViewModel.MorphWeight)
            or nameof(EditorAnimationViewModel.CanRegister)) ScheduleRefresh();
    }

    private void ShellChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(EditorShellViewModel.IsPlaying) or nameof(EditorShellViewModel.IsBusy) or nameof(EditorShellViewModel.SceneSelectionIndex)) ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        if (scheduled) return;
        scheduled = true;
        Dispatcher.UIThread.Post(() => { scheduled = false; RefreshGroups(); });
    }

    private void BeginSliderGesture(object? sender, PointerPressedEventArgs args)
    {
        if (args.Source is not Visual visual || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var slider = visual as EditorValueSlider ?? visual.FindAncestorOfType<EditorValueSlider>();
        if (slider?.DataContext is not EditorMorphDockGroup { Selected: { } choice }) return;
        editingPointer = args.Pointer;
        editingChoice = choice;
    }

    private void EndSliderGesture(IPointer pointer)
    {
        if (pointer != editingPointer) return;
        editingPointer = null; editingChoice = null;
        ScheduleRefresh();
    }

    private void FilterMorphs(object? sender, RoutedEventArgs args) => RefreshGroups();
    internal void RefreshGroups()
    {
        var animation = shell?.Animation;
        var model = shell?.Browser.SelectedModel?.PmxModel;
        bool changedModel = !ReferenceEquals(lastModel, model);
        if (changedModel)
        {
            editingPointer = null; editingChoice = null;
            var choices = model?.Morphs.Select((morph, index) => new EditorMorphDockChoice(index, morph.Name, morph.ControlPanel))
                .Where(item => item.Panel is >= 1 and <= 4).ToArray() ?? [];
            morphNames.Clear();
            foreach (var choice in choices) morphNames.TryAdd(choice.Name, choice.Index);
            panelChoices = Enumerable.Range(1, 4).Select(panel => choices.Where(choice => choice.Panel == panel).ToArray()).ToArray();
            lastModel = model;
        }
        var motion = animation?.MorphPanelMotion;
        if (changedModel || !ReferenceEquals(motion, lastMotion))
        {
            lastMotion = motion;
            sampler = motion is null || morphNames.Count == 0 ? null : new MmdMorphMotionAdapter(morphNames, motion);
        }
        var weights = sampler?.Evaluate(animation!.CurrentFrame);
        bool filtered = NonzeroMorphs.IsChecked == true;
        bool canRegister = shell is { IsPlaying: false, IsBusy: false, IsAnimationInspector: true }
            && animation is { IsBusy: false, HasActor: true } && animation.ValidateInput?.Invoke() != false;
        for (int i = 0; i < Groups.Count; i++)
        {
            var panel = panelChoices[i];
            var items = filtered ? panel.Where(item => item == editingChoice || Weight(item) != 0).ToArray() : panel;
            Groups[i].Refresh(items, changedModel, animation?.SelectedMorphIndex, Weight, canRegister);
        }
        decimal Weight(EditorMorphDockChoice item) => animation?.CurrentMorphDraft(item.Index)
            ?? EditorAnimationViewModel.SampleMorphWeight(weights?.GetValueOrDefault(item.Index) ?? 0);
    }

    internal bool CanEdit(EditorMorphDockChoice? choice) => choice is not null && shell is { IsPlaying: false, IsBusy: false, SceneSelectionIndex: < 0 }
        && ReferenceEquals(shell.Browser.SelectedModel?.PmxModel, lastModel) && shell.Animation.OwnsMorph(choice.Name);

    internal bool Activate(EditorMorphDockChoice choice)
    {
        if (!CanEdit(choice)) return false;
        var active = shell!;
        active.Browser.HierarchyTab = 1;
        active.Browser.SelectedMorphIndex = choice.Index;
        return active.Animation.SelectedMorphIndex == choice.Index && active.Animation.CanEditMorph;
    }

    internal void ChangeWeight(EditorMorphDockChoice choice, decimal? value)
    {
        if (Activate(choice)) shell!.Animation.MorphWeight = value;
    }

    private void RegisterMorph(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: EditorMorphDockGroup { CanRegister: true, Selected: { } choice } } || !Activate(choice)) return;
        var command = shell!.Animation.RegisterKeyframeCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }
}

public sealed class EditorMorphDockGroup(EditorMorphDock owner, string key) : INotifyPropertyChanged
{
    private EditorMorphDockChoice? selected;
    private decimal? weight;
    private bool updating, editable, canRegister;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Label => EditorLocalization.Text("Mda.Text.Layout.Morph." + key);
    public IReadOnlyList<EditorMorphDockChoice> Choices { get; private set; } = [];
    public EditorMorphDockChoice? Selected
    {
        get => selected;
        set
        {
            if (updating || value is null || value == selected || !Choices.Contains(value)) return;
            selected = value;
            owner.Activate(value);
            owner.RefreshGroups();
        }
    }
    public decimal? Weight
    {
        get => weight;
        set
        {
            if (updating || value == weight || selected is null || !CanEdit) return;
            weight = value; owner.ChangeWeight(selected, value); Changed();
        }
    }
    public bool CanEdit => editable;
    public bool CanRegister => canRegister;

    internal void Refresh(EditorMorphDockChoice[] items, bool newModel, int? active, Func<EditorMorphDockChoice, decimal> getWeight, bool validRegistration)
    {
        updating = true;
        try
        {
            var next = items.FirstOrDefault(item => item.Index == active)
                ?? (newModel ? null : items.FirstOrDefault(item => item == selected)) ?? items.FirstOrDefault();
            bool replaced = !ReferenceEquals(Choices, items) && !Choices.SequenceEqual(items);
            if (replaced) { Choices = items; Changed(nameof(Choices)); }
            if (replaced || next != selected) { selected = next; Changed(nameof(Selected)); }
            decimal? nextWeight = selected is null ? null : getWeight(selected);
            if (weight != nextWeight) { weight = nextWeight; Changed(nameof(Weight)); }
            bool canEdit = owner.CanEdit(selected);
            if (editable != canEdit) { editable = canEdit; Changed(nameof(CanEdit)); }
            bool nextCanRegister = canEdit && validRegistration;
            if (canRegister != nextCanRegister) { canRegister = nextCanRegister; Changed(nameof(CanRegister)); }
        }
        finally { updating = false; }
    }

    internal void UpdateLabel() => Changed(nameof(Label));
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

public sealed partial class EditorAnimationViewModel
{
    internal static decimal SampleMorphWeight(float value) => ToDecimal(value) ?? 0;
    internal VmdMotion MorphPanelMotion => EffectiveMotion;
    internal bool OwnsMorph(string name) => detachment?.MorphNames.Contains(name) != true;
    internal decimal? CurrentMorphDraft(int index) => !playing && objectId is { } id && actorDrafts.TryGetValue((id, CurrentFrame), out var pending)
        && pending.Morphs.TryGetValue(index, out var weight) ? weight : null;
}
