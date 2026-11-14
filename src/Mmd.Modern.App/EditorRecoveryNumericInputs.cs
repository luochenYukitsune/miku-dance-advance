using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Mmd.Modern.App.Controls;

namespace Mmd.Modern.App;

/// <summary>Unparsed numeric text has no Value and must be preserved separately from typed drafts.
/// Only named editing fields participate; a checkpoint cannot address arbitrary window controls.</summary>
internal static class EditorRecoveryNumericInputs
{
    internal static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "CurrentFrameInput", "MoveAnimationKeysFrame", "BonePositionX", "BonePositionY", "BonePositionZ",
        "BoneRotationX", "BoneRotationY", "BoneRotationZ", "MorphWeight",
        "CameraCenterX", "CameraCenterY", "CameraCenterZ", "CameraRotationX", "CameraRotationY", "CameraRotationZ",
        "CameraDistance", "CameraFov", "LightIntensity", "LightColorR", "LightColorG", "LightColorB",
        "LightDirectionX", "LightDirectionY", "LightDirectionZ"
    };

    internal static ImmutableSortedDictionary<string, string> Capture(EditorWindow window)
    {
        var result = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var input in Inputs(window))
        {
            if (input.IsInputValid || !HasContext(window, input.Name!)) continue;
            result.Add(input.Name!, input.Text ?? "");
        }
        return result.ToImmutable();
    }

    internal static void Restore(EditorWindow window, IReadOnlyDictionary<string, string> values)
    {
        window.UpdateLayout();
        foreach (var input in Inputs(window))
            if (values.TryGetValue(input.Name!, out string? text) && HasContext(window, input.Name!))
                input.SetCurrentValue(NumericUpDown.TextProperty, text);
    }

    private static IEnumerable<EditorNumericUpDown> Inputs(EditorWindow window) => window.GetVisualDescendants()
        .OfType<EditorNumericUpDown>().Where(input => input.Name is { } name && Names.Contains(name));

    private static bool HasContext(EditorWindow window, string name)
    {
        if (window.DataContext is not EditorShellViewModel shell) return false;
        if (name.StartsWith("Bone", StringComparison.Ordinal)) return shell.Animation.IsBoneSelected;
        if (name == "MorphWeight") return shell.Animation.IsMorphSelected;
        return true;
    }
}
