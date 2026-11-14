using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>A loaded Classic package and the detached traditional metadata validated before host loading.
/// The caller owns <see cref="Package"/> and must dispose it; the metadata does not own package resources.</summary>
public sealed class D3D9ClassicPreparedProject
{
    public PmaLoadedProject Package { get; }
    public D3D9EmmPmaState? Emm { get; }
    public D3D9EffectResourceTree? Tree { get; }
    public D3D9FrozenEffectProjectState? FrozenEffects { get; }
    public D3D9ClassicAccessoryAssetsState? AccessoryAssets { get; }

    internal D3D9ClassicPreparedProject(PmaLoadedProject package, D3D9EmmPmaState? emm,
        D3D9EffectResourceTree? tree, D3D9FrozenEffectProjectState? frozenEffects,
        D3D9ClassicAccessoryAssetsState? accessoryAssets)
    { Package = package; Emm = emm; Tree = tree; FrozenEffects = frozenEffects; AccessoryAssets = accessoryAssets; }
}

/// <summary>Traditional-engine project profile over the neutral PMC container. Excludes modern-only authored
/// selections and opaque data without changing the live session. Dependency discovery and EMM/FX schema
/// interpretation remain separate traditional-engine responsibilities.</summary>
public static class D3D9ClassicProjectStore
{
    // These identifiers define the Classic format boundary. Naming an excluded opaque namespace does not
    // import a modern renderer, its algorithms or any FX execution semantics into the shared core.
    private static readonly string[] ExcludedExtensionNames =
    [
        "org.mmd-modern.rendering-styles",
        "org.mda.model-material-bindings"
    ];
    private static readonly string[] ExcludedResourcePrefixes =
    [
        "render-resources/material-bindings/",
        "render-resources/generated-materials/",
        "render-resources/generated-styles/"
    ];

    /// <summary>Prepares save options before renderer codecs perform their resource-budget checks. Additional
    /// caller exclusions are retained; the Classic profile cannot be relaxed. All other caller options survive.
    /// This prepares immutable constraints only; the neutral capture later freezes model and resource contents.</summary>
    public static PmaSaveOptions CreateSaveOptions(PmaSaveOptions? options = null)
    {
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.ValidationOptions);
        var selection = Selection(options.ContentSelection, options.ValidationOptions.ContentSelection);
        return options with
        {
            ContentSelection = selection,
            ValidationOptions = options.ValidationOptions with { ContentSelection = selection }
        };
    }

    public static void Save(string path, SceneProjectSession project, PmaSaveOptions? options = null,
        CancellationToken cancellationToken = default)
        => PmcProjectStore.Save(path, project, CreateSaveOptions(options), cancellationToken);

    /// <summary>Captures a detached Classic projection without seeking animation or stepping physics.
    /// Retained resources and caller overrides pass through the same exclusions before staging.</summary>
    public static PmaSaveSnapshot CaptureSnapshot(SceneProjectSession project, PmaSaveOptions? options = null,
        CancellationToken cancellationToken = default)
        => PmcProjectStore.CaptureSnapshot(project, CreateSaveOptions(options), cancellationToken);

    /// <summary>Rejects excluded Classic-profile content and malformed traditional metadata before caller
    /// preflight, model callbacks or physics construction. Does not execute effects or materialize their files.</summary>
    public static PmaLoadedProject Load(string path, string cacheDirectory,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel, IProjectPhysicsBackendProvider physics,
        PmaLoadOptions? options = null, CancellationToken cancellationToken = default)
        => LoadPrepared(path, cacheDirectory, loadModel, physics, options, cancellationToken).Package;

    /// <summary>Freezes EMM and the effect-resource tree once after neutral archive validation and before
    /// caller preflight or any host loading. Frozen effect assignments, when present, are cross-validated
    /// against both. Legacy packages remain readable without inventing assignments from original files.</summary>
    public static D3D9ClassicPreparedProject LoadPrepared(string path, string cacheDirectory,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel, IProjectPhysicsBackendProvider physics,
        PmaLoadOptions? options = null, CancellationToken cancellationToken = default)
        => LoadPreparedWithAccessories(path, cacheDirectory, loadModel, physics, null, options, cancellationToken);

    /// <summary>The same validated Classic load, with an explicit decoder for packaged accessory images.
    /// Decoding receives frozen bytes and authored tokens; no original texture path is opened.</summary>
    public static D3D9ClassicPreparedProject LoadPreparedWithAccessories(string path, string cacheDirectory,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel, IProjectPhysicsBackendProvider physics,
        Func<ReadOnlyMemory<byte>, string, SceneTexture>? decodeAccessoryTexture,
        PmaLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var callerPreflight = options.PackagePreflight;
        var callerAccessories = options.AccessoryPreparation;
        D3D9EmmPmaState? emm = null;
        D3D9EffectResourceTree? tree = null;
        D3D9FrozenEffectProjectState? frozenEffects = null;
        D3D9ClassicAccessoryAssetsState? accessories = null;
        var package = PmcProjectStore.Load(path, cacheDirectory, loadModel, physics,
            options with
            {
                ContentSelection = Selection(options.ContentSelection),
                AccessoryPreparation = (resources, token) => accessories is not null
                    ? accessories.ToPersistenceCapture() : callerAccessories?.Invoke(resources, token),
                PackagePreflight = (resources, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    emm = D3D9EmmPma.ReadPackage(resources);
                    token.ThrowIfCancellationRequested();
                    tree = D3D9EffectResourceTree.ReadPackage(resources, cancellationToken: token);
                    token.ThrowIfCancellationRequested();
                    frozenEffects = D3D9FrozenEffectProject.ReadPackage(resources, emm, tree, token);
                    token.ThrowIfCancellationRequested();
                    accessories = D3D9ClassicAccessoryAssets.ReadPackage(resources,
                        decodeAccessoryTexture ?? MissingAccessoryDecoder, cancellationToken: token);
                    if (accessories is not null && callerAccessories is not null)
                        throw new ArgumentException("Classic accessory metadata and a caller accessory preparation cannot both own restored accessories.", nameof(options));
                    token.ThrowIfCancellationRequested();
                    callerPreflight?.Invoke(resources, token);
                }
            }, cancellationToken);
        return new(package, emm, tree, frozenEffects, accessories);
    }

    private static SceneTexture MissingAccessoryDecoder(ReadOnlyMemory<byte> bytes, string token)
        => throw new NotSupportedException("A texture decoder is required to restore the Classic accessory image: " + token);

    private static PmaContentSelection Selection(params PmaContentSelection?[] selections)
        => new(ExcludedExtensionNames.Concat(selections.OfType<PmaContentSelection>().SelectMany(value => value.ExcludedExtensionNames)),
            ExcludedResourcePrefixes.Concat(selections.OfType<PmaContentSelection>().SelectMany(value => value.ExcludedResourcePrefixes)),
            retainNativeLighting: false, retainModelPurposes: false, retainRenderingStyles: false);
}
