using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

internal sealed partial class D3D9SurfaceFrameEncoder
{
    // Purpose is also used by the modern compositor. The original material table is
    // already captured alongside this accepted frame, so Classic need not invoke a
    // second evaluator or inherit modern role-dependent lighting responses.
    private static OfflineFrame ProjectClassicMaterials(OfflineFrame frame)
    {
        var flags = frame.Scene.Materials.ToArray();
        int cursor = 0;
        foreach (var style in frame.RenderingStyles)
        {
            if (!frame.ObjectMaterialInputs.TryGetValue(style.ObjectId, out var source)
                || source.ObjectId != style.ObjectId || source.Visible != style.Visible)
                throw new InvalidDataException("Classic requires the original material table for every same-sample model.");
            D3D9SurfacePreflight.ValidateClassicSourceFlags(source.MaterialFlags);
            if (style.MaterialStart != cursor || style.MaterialCount != (style.Visible ? source.MaterialCount : 0))
                throw new InvalidDataException("Classic material ranges must cover the visible source tables in order.");
            for (int local = 0; local < style.MaterialCount; local++)
            {
                int index = checked(cursor + local);
                if ((uint)index >= (uint)flags.Length)
                    throw new InvalidDataException("Classic material range exceeds the accepted frame.");
                var authored = source.MaterialFlags[local];
                var composed = flags[index];
                // PMM's model switch can disable self shadow after material morphs.
                // Keep that gate; only the derived modern purpose channels are removed.
                var selected = authored with
                {
                    CastSelfShadow = authored.CastSelfShadow && composed.CastSelfShadow,
                    ReceiveSelfShadow = authored.ReceiveSelfShadow && composed.ReceiveSelfShadow
                };
                var withoutPurpose = composed with
                {
                    LightReceiverObjectId = authored.LightReceiverObjectId,
                    GiToonResponseOverride = authored.GiToonResponseOverride,
                    UseSceneLighting = authored.UseSceneLighting,
                    PreserveDirectLightPalette = authored.PreserveDirectLightPalette,
                    CharacterToonResponse = authored.CharacterToonResponse,
                    CharacterOutlineStyle = authored.CharacterOutlineStyle
                };
                if (selected != withoutPurpose)
                    throw new InvalidDataException("Classic source flags differ from the accepted frame beyond purpose policy or the PMM shadow gate.");
                flags[index] = selected;
            }
            cursor = checked(cursor + style.MaterialCount);
        }
        if (cursor != flags.Length)
            throw new InvalidDataException("Classic source tables do not cover every composed material.");
        var scene = frame.Scene;
        return frame with { Scene = new(scene.Surface, scene.Outline, scene.OutlineOptions, scene.Options,
            flags, scene.Lighting, scene.EditorOverlay) };
    }
}
