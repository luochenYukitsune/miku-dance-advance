using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;

namespace Mmd.Modern.App;

/// <summary>Production Classic document policy. CPU preparation and immutable document data remain
/// separate from renderer bindings; loading never creates a device or executes an effect.</summary>
internal sealed class EditorMmeProjectPersistence(IEditorRenderEngine engine,
    Func<int, SceneTexture?>? loadCommonToon = null, Func<SceneTexture?>? loadDefaultToon = null,
    EditorClassicToonResources? localToonResources = null) : IEditorProjectPersistence
{
    public IEditorRenderEngine Engine => engine;
    public EditorProjectFormat Format => EditorProjectFormat.Classic;

    public void Save(string path, EditorProjectWorkspace workspace, PmaSaveOptions options, CancellationToken token)
    {
        string fullPath = Format.RequirePath(path);
        var session = EditorProjectPersistence.RequireSession(this, workspace);
        var preparedOptions = RequireDocument(workspace).CreateSaveOptions(session, options, token);
        D3D9ClassicProjectStore.Save(fullPath, session, preparedOptions, token);
    }

    public PmaSaveSnapshot CaptureSnapshot(EditorProjectWorkspace workspace, PmaSaveOptions options, CancellationToken token)
    {
        var session = EditorProjectPersistence.RequireSession(this, workspace);
        var preparedOptions = RequireDocument(workspace).CreateSaveOptions(session, options, token);
        return D3D9ClassicProjectStore.CaptureSnapshot(session, preparedOptions, token);
    }

    public EditorProjectLoadedCandidate Load(string path, string cacheDirectory,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel, IProjectPhysicsBackendProvider physics,
        PmaLoadOptions options, CancellationToken token)
    {
        EditorClassicToonPackage? toons = null;
        var callerPreflight = options.PackagePreflight;
        var callerTransform = options.ModelAssetTransform;
        // The neutral store synchronously calls the host loader, then its per-instance transform,
        // in validated layout order. The context supplies the Guid; this path is that same call's PMX.
        string? currentModelPath = null;
        var prepared = D3D9ClassicProjectStore.LoadPreparedWithAccessories(Format.RequirePath(path), cacheDirectory,
            (modelPath, cancellation) =>
            {
                currentModelPath = modelPath;
                var asset = loadModel(modelPath, cancellation);
                return toons is null ? EditorClassicModelAssets.RetainToonTextures(asset, modelPath,
                    cancellation, loadCommonToon, loadDefaultToon) : asset;
            },
            physics, (bytes, authoredToken) => EditorTextureLoader.Decode(bytes, authoredToken, token), options with
            {
                PackagePreflight = (context, cancellation) =>
                {
                    toons = EditorClassicToonPackage.ReadPackage(context, cancellation);
                    callerPreflight?.Invoke(context, cancellation);
                },
                ModelAssetTransform = (context, asset) =>
                {
                    var transformed = callerTransform is null ? asset : callerTransform(context, asset);
                    return toons is null ? transformed : toons.Apply(context.ObjectId, transformed,
                        currentModelPath ?? throw new InvalidDataException("Classic model preparation has no source path."), token);
                }
            }, token);
        var package = prepared.Package;
        try
        {
            token.ThrowIfCancellationRequested();
            EditorSceneAudioInputs.Validate(package.Session.Media);
            var state = EditorClassicDocumentState.FromPrepared(prepared);
            if (state.Emm is null && state.Tree is null && state.FrozenEffects is null
                && package.OptionalExtensions.TryGetValue(PmaProjectStore.ConversionExtensionName, out var marker)
                && marker.ValueKind == JsonValueKind.Object && marker.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int schema) && schema == 1)
            {
                var models = package.Models.ToDictionary(model => model.ObjectId);
                var session = package.Session;
                var bindings = session.Objects.Select((item, index) =>
                    new EmmObjectBinding(item.Id, true, models[item.Id].OriginalSourcePath, index))
                    .Concat(session.Accessories.Select((item, index) => new EmmObjectBinding(item.ObjectId, false,
                        item.Asset.RequirePortableSource().OriginalPath, session.Objects.Count + index)));
                state = state.WithEffects(session, D3D9ClassicEffectsPreparation.CreateUnassigned(
                    Path.ChangeExtension(Path.GetFullPath(path), ".emm"), bindings, token));
            }
            state = toons is not null ? state.WithToons(package.Session, toons)
                : localToonResources is not null ? state.WithToonResources(package.Session, localToonResources, token) : state;
            token.ThrowIfCancellationRequested();
            return new(package, [], state);
        }
        catch (Exception error) { EditorProjectPersistence.DisposeFailedCandidate(package, error); throw; }
    }

    public EditorRecoverySessionState ProjectRecoveryState(EditorRecoverySessionState state)
        => state with { NativeLighting = null };

    public void ValidateRenderContent(EditorProjectWorkspace workspace)
    {
        var session = EditorProjectPersistence.RequireSession(this, workspace);
        _ = RequireDocument(workspace).RequireEffects(session);
    }

    private static EditorClassicDocumentState RequireDocument(EditorProjectWorkspace workspace)
        => workspace.ProjectMetadata as EditorClassicDocumentState ?? throw new NotSupportedException(
            "This Classic document has no prepared production metadata. Import its effects and original accessory assets explicitly.");
}
