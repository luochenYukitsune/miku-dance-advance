using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.App;

/// <summary>Detached Classic document data. The workspace owns this value; renderer bindings borrow it
/// and own their own runtime leases. Original EMM paths and loaded model cache paths remain distinct.</summary>
internal sealed partial class EditorClassicDocumentState : IEditorPreparedProjectMetadata
{
    private sealed record ModelIdentity(string ActualSourcePath, OriginalFrameScene Source, PmxModel? Model);
    private sealed record AccessoryIdentity(byte StoredId, string ActualSourcePath, string Sha256,
        OriginalFrameScene Source, XTextDocument? Document, PmmAccessorySourceSnapshot? OriginalSource);

    private readonly Dictionary<Guid, ModelIdentity> models;
    private readonly Dictionary<Guid, AccessoryIdentity> accessories;

    internal D3D9EmmPmaState? Emm { get; }
    internal D3D9EffectResourceTree? Tree { get; }
    internal D3D9FrozenEffectProjectState? FrozenEffects { get; }
    internal D3D9ClassicAccessoryAssetsState? AccessoryAssets { get; }
    internal D3D9ClassicEffectsPreparation? Effects { get; }
    public EditorProjectFormat Format => EditorProjectFormat.Classic;

    private EditorClassicDocumentState(SceneProjectSession session, D3D9EmmPmaState? emm,
        D3D9EffectResourceTree? tree, D3D9FrozenEffectProjectState? frozenEffects,
        D3D9ClassicAccessoryAssetsState? accessoryAssets, D3D9ClassicEffectsPreparation? effects,
        bool originalModelPaths)
    {
        ArgumentNullException.ThrowIfNull(session);
        Emm = emm; Tree = tree; FrozenEffects = frozenEffects; AccessoryAssets = accessoryAssets; Effects = effects;
        var authored = session.CaptureAuthoredContent().Motions.ToDictionary(value => value.ObjectId, value => value.Model);
        models = session.Objects.ToDictionary(value => value.Id,
            value => new ModelIdentity(value.SourcePath, value.Source, authored.GetValueOrDefault(value.Id)));
        accessories = session.Accessories.ToDictionary(value => value.ObjectId,
            value => new AccessoryIdentity(value.Asset.Index, value.Asset.Path, value.Asset.Sha256,
                value.Asset.Source, value.Asset.Document, value.Asset.SourceSnapshot));
        ValidateBindings(originalModelPaths);
        ValidateAccessoryAssets(session);
    }

    /// <summary>Retains exactly the metadata validated by the store. Recover does not inspect originals,
    /// and is called only when the package actually has frozen effect decisions.</summary>
    internal static EditorClassicDocumentState FromPrepared(D3D9ClassicPreparedProject prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var effects = prepared.FrozenEffects is not null ? D3D9ClassicEffectsPreparation.Recover(prepared) : null;
        return new(prepared.Package.Session, prepared.Emm, prepared.Tree, prepared.FrozenEffects,
            prepared.AccessoryAssets, effects, originalModelPaths: false);
    }

    /// <summary>Adopts an explicitly captured production import. Its model source paths must identify
    /// the same originals used to plan effects; this method never searches a model's directory.</summary>
    internal static EditorClassicDocumentState Capture(SceneProjectSession session,
        D3D9ClassicEffectsPreparation effects, D3D9ClassicAccessoryAssetsState? accessoryAssets = null)
    {
        ArgumentNullException.ThrowIfNull(effects);
        var state = new EditorClassicDocumentState(session, effects.Emm, effects.Tree, effects.FrozenEffects,
            accessoryAssets, effects, originalModelPaths: true);
        if (session.UnpreparedAccessoryIndices.Count != 0)
            throw new NotSupportedException("Classic import requires every declared accessory to have its prepared original assets.");
        return state;
    }

    /// <summary>Verifies the complete live set and exact immutable sources captured at adoption/load.
    /// A replacement with the same Guid/path still needs new effects and asset preparation.</summary>
    public void ValidateSession(SceneProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Objects.Count != models.Count || session.Accessories.Count != accessories.Count)
            throw new InvalidDataException("Classic document metadata no longer covers the live model/accessory set. Prepare the changed document explicitly.");
        var authored = session.CaptureAuthoredContent().Motions.ToDictionary(value => value.ObjectId, value => value.Model);
        foreach (var item in session.Objects)
        {
            if (!models.TryGetValue(item.Id, out var expected) || item.SourcePath != expected.ActualSourcePath
                || !ReferenceEquals(item.Source, expected.Source)
                || !ReferenceEquals(authored.GetValueOrDefault(item.Id), expected.Model))
                throw new InvalidDataException("A live model Guid, source path or immutable model/source binding differs from its Classic document preparation.");
        }
        foreach (var item in session.Accessories)
        {
            var asset = item.Asset;
            if (!accessories.TryGetValue(item.ObjectId, out var expected) || asset.Index != expected.StoredId
                || asset.Path != expected.ActualSourcePath || asset.Sha256 != expected.Sha256
                || !ReferenceEquals(asset.Source, expected.Source) || !ReferenceEquals(asset.Document, expected.Document)
                || !ReferenceEquals(asset.SourceSnapshot, expected.OriginalSource))
                throw new InvalidDataException("A live accessory Guid, stored ID or immutable original source differs from its Classic document preparation.");
        }
        ValidateBindings(originalModelPaths: false);
        ValidateAccessoryAssets(session);
        Toons?.Validate(session);
    }

    /// <summary>Legacy metadata stays available for inspection and preservation; execution requires an
    /// explicit frozen positive or null assignment for every live object.</summary>
    internal D3D9ClassicEffectsPreparation RequireEffects(SceneProjectSession session)
    {
        ValidateSession(session);
        if (session.UnpreparedAccessoryIndices.Count != 0)
            throw new NotSupportedException("Classic rendering requires prepared original assets for every declared accessory.");
        return Effects ?? throw new NotSupportedException(
            "This Classic document has no frozen effect assignments. Import its effects explicitly before rendering.");
    }

    /// <summary>Composes existing engine-owned codecs before neutral snapshot capture. Neither this
    /// method nor the Classic store mutates the live session or reopens original FX/accessory sources.</summary>
    internal PmaSaveOptions CreateSaveOptions(SceneProjectSession session, PmaSaveOptions options,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options); token.ThrowIfCancellationRequested();
        ValidateSession(session);
        ArgumentNullException.ThrowIfNull(options.ModelOverrides);
        foreach (var pair in options.ModelOverrides)
            if (!models.TryGetValue(pair.Key, out var expected) || pair.Value is null
                || !ReferenceEquals(pair.Value, expected.Model))
                throw new InvalidDataException("A Classic save-only model override differs from its frozen document model. Prepare the changed document explicitly.");
        if (Effects is not null) options = Effects.CreateSaveOptions(session, options, token);
        else
        {
            // Preserve validated legacy metadata without inventing missing frozen choices. It still
            // cannot execute, and unknown package data remains owned by the neutral retained state.
            options = D3D9ClassicProjectStore.CreateSaveOptions(options);
            if (Emm is not null) options = D3D9EmmPma.CreateSaveOptions(session, Emm, options);
            if (Tree is not null) options = D3D9EffectResourceTree.CreateSaveOptions(session, Tree, options);
        }
        token.ThrowIfCancellationRequested();
        if (AccessoryAssets is not null)
            options = D3D9ClassicAccessoryAssets.CreateSaveOptions(session, AccessoryAssets, options);
        return CreateToonSaveOptions(session, options, token);
    }

    private void ValidateBindings(bool originalModelPaths)
    {
        if (models.Keys.Any(accessories.ContainsKey))
            throw new InvalidDataException("Classic models and accessories require independent object identities.");
        if (Emm is null)
        {
            if (FrozenEffects is not null || Effects is not null)
                throw new InvalidDataException("Frozen Classic effects require their exact EMM import metadata.");
            return;
        }
        if (Effects is not null && (!ReferenceEquals(Effects.Emm, Emm)
            || !ReferenceEquals(Effects.FrozenEffects, FrozenEffects) || !ReferenceEquals(Effects.Tree, Tree)))
            throw new InvalidDataException("Classic effect preparation does not own the retained document metadata.");
        var plan = Emm.ImportPlan;
        if (plan.LiveObjects.Count != models.Count + accessories.Count)
            throw new InvalidDataException("Classic EMM bindings must cover the complete live model/accessory set.");
        foreach (var binding in plan.LiveObjects)
        {
            if (binding.IsModel)
            {
                if (!models.TryGetValue(binding.ObjectId, out var model))
                    throw new InvalidDataException("An EMM model binding has no matching live model Guid/kind.");
                // Restored PMX paths name private package files. Their association with original EMM
                // sources is by the validated package Guid, then guarded by the exact loaded sources.
                if (originalModelPaths && !SameOriginalPath(binding.SourcePath, model.ActualSourcePath, plan))
                    throw new InvalidDataException("An imported model source disagrees with its EMM original-source binding.");
            }
            else
            {
                if (!accessories.TryGetValue(binding.ObjectId, out var accessory)
                    || !SameOriginalPath(binding.SourcePath, accessory.OriginalSource?.OriginalPath ?? accessory.ActualSourcePath, plan))
                    throw new InvalidDataException("An EMM accessory binding disagrees with a live Guid/kind or original X source.");
            }
        }
    }

    private void ValidateAccessoryAssets(SceneProjectSession session)
    {
        ValidateAccessoryAssets(session.Accessories);
        // The neutral session has already validated the complete stored-ID table and parent bindings.
        if (session.UnpreparedAccessoryIndices.Count != 0)
            throw new InvalidDataException("Frozen Classic assets do not cover every declared accessory.");
    }

    private void ValidateAccessoryAssets(IReadOnlyList<SceneProjectAccessory> liveAccessories)
    {
        if (AccessoryAssets is null)
        {
            if (accessories.Count != 0)
                throw new NotSupportedException("Classic document metadata requires complete frozen assets for every live accessory.");
            return;
        }
        if (AccessoryAssets.Accessories.Count != accessories.Count)
            throw new InvalidDataException("Frozen Classic assets must cover the complete live accessory set.");
        foreach (var item in AccessoryAssets.Accessories)
        {
            var source = item.Asset.RequirePortableSource();
            if (!accessories.TryGetValue(item.ObjectId, out var expected) || item.Asset.Index != expected.StoredId
                || item.Asset.Path != expected.ActualSourcePath || item.Asset.Sha256 != expected.Sha256
                || !ReferenceEquals(item.Asset.Source, expected.Source) || !ReferenceEquals(item.Asset.Document, expected.Document)
                || !ReferenceEquals(source, expected.OriginalSource))
                throw new InvalidDataException("Frozen Classic assets disagree with a live accessory's exact original source binding.");
        }
        if (liveAccessories.Count != accessories.Count)
            throw new InvalidDataException("Classic model edits cannot change the frozen accessory table.");
    }

    private static bool SameOriginalPath(string first, string second, EmmImportPlan plan)
        => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(first, plan.PathContext.WorkingDirectory),
            Path.GetFullPath(second, plan.PathContext.WorkingDirectory));
}
