using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

/// <summary>Maps explicit project purpose to neutral rendering metadata without changing source assets or saved roles.</summary>
public static class SceneModelShadingPolicy
{
    public static ModelRole ResolveRole(EditableSceneObject item, IReadOnlyDictionary<string, ModelPurpose>? modelRoles = null)
        => ResolvePurpose(item, modelRoles).Role;

    public static ModelPurpose ResolvePurpose(EditableSceneObject item, IReadOnlyDictionary<string, ModelPurpose>? modelRoles = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        // Presence matters: an explicit Unspecified purpose overrides an older object role too.
        var resolved = modelRoles is not null && modelRoles.TryGetValue(item.Key, out var purpose) ? purpose : new ModelPurpose(item.Role);
        if (!Enum.IsDefined(resolved.Role)) throw new ArgumentOutOfRangeException(nameof(modelRoles));
        SceneLightingEditor.ValidatePurpose(item.Key, resolved.Role, resolved.EnvironmentIndoor);
        return resolved;
    }

    public static OriginalFrameScene Apply(OriginalFrameScene frame, ModelRole role)
        => Apply(frame, new ModelPurpose(role));

    public static OriginalFrameScene Apply(OriginalFrameScene frame, ModelPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(purpose);
        var role = purpose.Role;
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        SceneLightingEditor.ValidatePurpose("render-purpose", role, purpose.EnvironmentIndoor);
        bool sceneLighting = role == ModelRole.Environment && purpose.EnvironmentIndoor == true;
        bool directPalette = role == ModelRole.Character;
        RayGiToonResponse? response = role switch
        {
            ModelRole.Character => RayGiToonResponse.PreservePaletteBands,
            ModelRole.Environment => RayGiToonResponse.Continuous,
            _ => null,
        };
        if (response is null)
            return !frame.UsesSceneLighting && !frame.Materials.Any(flags => flags.PreserveDirectLightPalette) ? frame
                : new(frame.Surface, frame.Outline, frame.OutlineOptions, frame.Options,
                    frame.Materials.Select(flags => flags with { UseSceneLighting = false, PreserveDirectLightPalette = false }),
                    frame.Lighting, frame.EditorOverlay);
        // Role governs GI treatment, while the selected rendering style owns main-light and outline overrides.
        CharacterToonResponse? MainLight(OriginalFrameMaterialFlags flags) => role == ModelRole.Character
            ? flags.CharacterToonResponse : null;
        CharacterOutlineStyle? Outline(OriginalFrameMaterialFlags flags) => role == ModelRole.Character
            ? flags.CharacterOutlineStyle : null;
        if (frame.Materials.All(flags => flags.GiToonResponseOverride == response
            && flags.CharacterToonResponse == MainLight(flags) && flags.CharacterOutlineStyle == Outline(flags)
            && flags.UseSceneLighting == sceneLighting && flags.PreserveDirectLightPalette == directPalette)) return frame;
        return new(frame.Surface, frame.Outline, frame.OutlineOptions, frame.Options,
            frame.Materials.Select(flags => flags with { GiToonResponseOverride = response, CharacterToonResponse = MainLight(flags),
                CharacterOutlineStyle = Outline(flags), UseSceneLighting = sceneLighting,
                PreserveDirectLightPalette = directPalette }), frame.Lighting, frame.EditorOverlay);
    }
}
