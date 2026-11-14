using Mmd.Modern.Application;

namespace Mmd.Modern.App;

internal sealed partial class EditorClassicDocumentState
{
    internal EditorClassicToonPackage? Toons { get; private init; }

    /// <summary>Attach exact already-used original common/default images to detached document metadata.
    /// The selected provider is needed only when an image is not already owned by this document.</summary>
    internal EditorClassicDocumentState WithToonResources(SceneProjectSession session,
        EditorClassicToonResources? resources, CancellationToken token = default)
        => WithToons(session, EditorClassicToonPackage.Capture(session, resources, Toons, token));

    internal EditorClassicDocumentState WithToons(SceneProjectSession session, EditorClassicToonPackage? toons)
    {
        ValidateSession(session);
        toons?.Validate(session);
        return new(session, Emm, Tree, FrozenEffects, AccessoryAssets, Effects, originalModelPaths: false)
        { Toons = toons };
    }

    private PmaSaveOptions CreateToonSaveOptions(SceneProjectSession session, PmaSaveOptions options, CancellationToken token)
    {
        if (Toons is not null) return Toons.CreateSaveOptions(session, options, token);
        // Legacy packages may be inspected without this extension. New production documents that use
        // original shared/default slots must explicitly capture their encoded image provenance first.
        var authored = session.CaptureAuthoredContent().Motions;
        if (authored.Any(actor => actor.Model.Materials.Any(material => material.FaceVertexCount > 0
            && (material.ToonMode == Mmd.Modern.Formats.PmxToonMode.Common
                || material.ToonMode == Mmd.Modern.Formats.PmxToonMode.Separate && material.ToonTextureIndex == -1))))
            throw new InvalidDataException("Capture the used original Classic toon image bytes before saving a portable PMC document.");
        return options;
    }
}
