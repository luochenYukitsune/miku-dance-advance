using System.ComponentModel;
using System.Numerics;
using Mmd.Modern.App.Controls;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Mmd.Modern.App;

/// <summary>Main-light authoring hosted by the right-rail Lighting page. The scene view model owns
/// the detached pose and its one-transaction registration; this control only binds to it.</summary>
public sealed partial class EditorMainLightInspector : UserControl
{
    public EditorMainLightInspector() => AvaloniaXamlLoader.Load(this);

    private (EditorSceneInspectorViewModel Model, Vector3 Direction, bool HadDraft, long Revision)? directionEdit;

    internal Func<bool>? DirectionEditStarting { get; set; }
    internal Action<bool>? DirectionEditingChanged { get; set; }

    private void BeginDirectionEdit(object? sender, CancelEventArgs args)
    {
        if (DataContext is not EditorSceneInspectorViewModel { CanEdit: true } model || DirectionEditStarting?.Invoke() == false) { args.Cancel = true; return; }
        directionEdit = (model, model.LightDirectionVector, model.HasLightDraft, model.PoseRevision);
        DirectionEditingChanged?.Invoke(true);
    }

    private void PreviewDirectionEdit(object? sender, Vector3 direction)
    {
        if (directionEdit is not { } edit || sender is not EditorLightDirectionPad pad) return;
        if (DataContext != edit.Model || edit.Revision != edit.Model.PoseRevision || !edit.Model.CanEdit)
        { pad.CancelEdit(); return; }
        edit.Model.LightDirectionVector = direction;
        directionEdit = edit with { Revision = edit.Model.PoseRevision };
    }

    private void CancelDirectionEdit(object? sender, EventArgs args)
    {
        var active = directionEdit;
        directionEdit = null;
        DirectionEditingChanged?.Invoke(false);
        if (active is not { } edit || edit.Revision != edit.Model.PoseRevision) return;
        if (edit.HadDraft) edit.Model.LightDirectionVector = edit.Direction;
        else edit.Model.RevertLightDraft();
    }

    private void CompleteDirectionEdit(object? sender, EventArgs args)
    {
        directionEdit = null;
        DirectionEditingChanged?.Invoke(false);
    }

    private void AimMainLight(object? sender, RoutedEventArgs args)
    {
        if (DataContext is EditorSceneInspectorViewModel model && sender is Button { Tag: string preset }) model.AimLight(preset);
    }

    private void AimMainLightFromCamera(object? sender, RoutedEventArgs args)
    {
        if (DataContext is EditorSceneInspectorViewModel model) model.AimLightFromCamera();
    }

    private void FlipMainLightHemisphere(object? sender, RoutedEventArgs args)
    {
        if (DataContext is EditorSceneInspectorViewModel model) model.FlipMainLightHemisphere();
    }

    private void MainLightColor(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not EditorSceneInspectorViewModel model || sender is not Button { Tag: string color }) return;
        var value = Color.Parse(color);
        model.LightColorR = value.R / 255m; model.LightColorG = value.G / 255m; model.LightColorB = value.B / 255m;
    }
}
