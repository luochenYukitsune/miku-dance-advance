using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.App;

public sealed partial class EditorProjectWorkspace
{
    internal async Task ApplyClassicAssignmentsAsync(EmmDocument document, bool reloadAll,
        CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var session = Session ?? throw new InvalidOperationException("Open a Classic project first.");
        var before = ProjectMetadata as EditorClassicDocumentState
            ?? throw new InvalidOperationException("The project has no Classic effect metadata.");
        var engine = RenderEngine as EditorClassicRenderEngine
            ?? throw new InvalidOperationException("Effect assignment is available only in Classic mode.");
        var effects = before.RequireEffects(session);
        var counts = session.CaptureAuthoredContent().Motions.ToDictionary(value => value.ObjectId, value => value.Model.Materials.Count);
        foreach (var accessory in session.Accessories) counts.Add(accessory.ObjectId, accessory.Asset.Source.Materials.Count);
        var prepared = await Task.Run(() => engine.PrepareAssignments(effects, document, reloadAll, counts, token), token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Session, session) || !ReferenceEquals(ProjectMetadata, before))
            throw new InvalidOperationException("The project changed while effect dependencies were being prepared. Reopen the assignment window.");
        var candidate = before.WithEffects(session, prepared);
        var mappings = SnapshotMappings();
        undo.Push(new(session, session, false, mappings, SnapshotMappings(), before, candidate, true));
        redo.Clear(); ProjectMetadata = candidate; MarkChanged(true);
    }
}

internal sealed partial class EditorClassicDocumentState
{
    internal EditorClassicDocumentState WithEffects(Mmd.Modern.Application.SceneProjectSession session,
        D3D9ClassicEffectsPreparation effects)
    {
        ValidateSession(session);
        return new(session, effects.Emm, effects.Tree, effects.FrozenEffects, AccessoryAssets,
            effects, originalModelPaths: false) { Toons = Toons };
    }
}

internal sealed partial class EditorClassicRenderEngine
{
    internal D3D9ClassicEffectsPreparation PrepareAssignments(D3D9ClassicEffectsPreparation previous,
        EmmDocument document, bool reloadAll, IReadOnlyDictionary<Guid, int> materialCounts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); runtime.ValidateFiles();
        using var inspector = new D3D9NativeEffectDependencyInspector(runtime.NativeLibraryPath,
            runtime.D3dxLibraryPath, runtime.CompilerLibraryPath);
        var result = previous.PrepareAssignments(document, inspector, reloadAll, materialCounts, token);
        if (result.Preparation is { } prepared) return prepared;
        var error = new InvalidDataException(string.Join(Environment.NewLine,
            result.Diagnostics.Select(value => value.Message).Distinct().Take(12)));
        error.Data["ClassicEffectDiagnostics"] = result.Diagnostics;
        throw error;
    }
}
