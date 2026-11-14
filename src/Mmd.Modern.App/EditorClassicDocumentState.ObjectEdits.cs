using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.App;

internal sealed partial class EditorClassicDocumentState
{
    private EditorClassicDocumentState(SceneProjectEdit edit, D3D9ClassicEffectsPreparation effects,
        D3D9ClassicAccessoryAssetsState? accessoryAssets, EditorClassicToonPackage? toons)
    {
        Emm = effects.Emm; Tree = effects.Tree; FrozenEffects = effects.FrozenEffects;
        Effects = effects; AccessoryAssets = accessoryAssets; Toons = toons;
        var authored = edit.Motions.ToDictionary(value => value.ObjectId, value => value.Model);
        models = edit.Composition.Objects.ToDictionary(value => value.Id,
            value => new ModelIdentity(value.SourcePath, value.Source, authored.GetValueOrDefault(value.Id)));
        accessories = edit.Accessories.ToDictionary(value => value.ObjectId,
            value => new AccessoryIdentity(value.Asset.Index, value.Asset.Path, value.Asset.Sha256,
                value.Asset.Source, value.Asset.Document, value.Asset.SourceSnapshot));
        // Restored unchanged models retain original EMM identities while their actual sources name
        // package-private files. New/replaced sources were explicitly supplied to the engine below.
        ValidateBindings(originalModelPaths: false);
        ValidateAccessoryAssets(edit.Accessories);
    }

    /// <summary>Examines only this transaction's detached immutable inputs. A content-only edit keeps
    /// the existing metadata without IO; changed model sources require explicit engine preparation.</summary>
    internal EditorClassicDocumentState PrepareModelEdit(SceneProjectEdit edit,
        EditorClassicRenderEngine engine, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(edit); token.ThrowIfCancellationRequested();
        var authored = edit.Motions.ToDictionary(value => value.ObjectId, value => value.Model);
        var changed = new HashSet<Guid>();
        foreach (var item in edit.Composition.Objects)
            if (!models.TryGetValue(item.Id, out var old) || item.SourcePath != old.ActualSourcePath ||
                !ReferenceEquals(item.Source, old.Source) || !ReferenceEquals(authored.GetValueOrDefault(item.Id), old.Model))
                changed.Add(item.Id);
        // This increment owns model-set edits only. Existing frozen accessory data may be borrowed
        // unchanged; a new accessory needs its separate original-asset preparation transaction.
        if (edit.Accessories.Count != accessories.Count || edit.Accessories.Any(value =>
                !accessories.TryGetValue(value.ObjectId, out var old) || value.Asset.Index != old.StoredId ||
                value.Asset.Path != old.ActualSourcePath || value.Asset.Sha256 != old.Sha256 ||
                !ReferenceEquals(value.Asset.Source, old.Source) || !ReferenceEquals(value.Asset.Document, old.Document) ||
                !ReferenceEquals(value.Asset.SourceSnapshot, old.OriginalSource)))
            throw new NotSupportedException("Classic model edits must retain every prepared accessory unchanged.");
        if (changed.Count == 0 && edit.Composition.Objects.Count == models.Count) return this;
        if (Effects is null || Emm is null)
            throw new NotSupportedException("Classic model edits require explicitly frozen effect assignments.");
        var previous = Emm.ImportPlan.LiveObjects.ToDictionary(value => value.ObjectId);
        int nextOrder = previous.Values.Select(value => value.RenderOrder).DefaultIfEmpty(-1).Max();
        var bindings = new List<EmmObjectBinding>();
        foreach (var item in edit.Composition.Objects)
        {
            var old = previous.GetValueOrDefault(item.Id);
            // Keep the authored order of every surviving instance. Added objects append after all
            // old model/accessory slots; no Guid or existing draw slot is regenerated.
            int order = old?.RenderOrder ?? checked(++nextOrder);
            bindings.Add(new(item.Id, true, changed.Contains(item.Id) ? item.SourcePath : old!.SourcePath, order));
        }
        bindings.AddRange(edit.Accessories.Select(value => previous[value.ObjectId]));
        var toons = engine.PrepareToons(edit, Toons, token);
        var materialCounts = edit.Composition.Objects.ToDictionary(value => value.Id,
            value => authored.GetValueOrDefault(value.Id)?.Materials.Count ?? value.Source.Surface.Snapshot.Materials.Count);
        foreach (var value in edit.Accessories)
            materialCounts.Add(value.ObjectId, value.Asset.Document?.Materials.Count
                ?? throw new InvalidDataException("Classic model edit validation requires each accessory's original X material table."));
        var effects = engine.PrepareObjectEdit(Effects, bindings, changed, materialCounts, token);
        return new(edit, effects, AccessoryAssets, toons);
    }
}
