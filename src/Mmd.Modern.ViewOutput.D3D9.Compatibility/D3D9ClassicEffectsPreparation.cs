using Mmd.Modern.Application;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>No partial preparation is exposed when a required original dependency cannot be captured.</summary>
public sealed class D3D9ClassicEffectsCaptureResult
{
    internal D3D9ClassicEffectsCaptureResult(D3D9EffectDependencyStatus status,
        D3D9ClassicEffectsPreparation? preparation, IEnumerable<D3D9EffectDependencyDiagnostic> diagnostics)
    { Status = status; Preparation = preparation; Diagnostics = Array.AsReadOnly(diagnostics.ToArray()); }

    public D3D9EffectDependencyStatus Status { get; }
    public D3D9ClassicEffectsPreparation? Preparation { get; }
    public IReadOnlyList<D3D9EffectDependencyDiagnostic> Diagnostics { get; }
}

/// <summary>Traditional effects only: combines the existing EMM planner, official-compiler dependency
/// collector, opaque PMC codecs and frozen runtime projection. Model, accessory and media persistence
/// remain owned by their existing adapters. Construction does not execute effects or create a device.</summary>
public sealed partial class D3D9ClassicEffectsPreparation
{
    private const string TreeResourcePrefix = "render-resources/traditional/effect-tree/";

    private D3D9ClassicEffectsPreparation(D3D9EmmPmaState emm, D3D9FrozenEffectProjectState frozenEffects)
    { Emm = emm; FrozenEffects = frozenEffects; }

    public D3D9EmmPmaState Emm { get; }
    public D3D9FrozenEffectProjectState FrozenEffects { get; }
    public D3D9EffectResourceTree? Tree => FrozenEffects.EffectTree;

    /// <summary>Discovers originals once, retaining every live object's positive or null implicit choice.
    /// The resolver is the pinned host's implicit-selection adapter; capture is not an existence probe.</summary>
    public static D3D9ClassicEffectsCaptureResult Capture(EmmImportPlan plan,
        D3D9EffectImplicitRootResolver implicitResolver, ID3D9EffectDependencyInspector inspector,
        D3D9EffectDependencyLimits? limits = null, D3D9EffectResourceTreeLimits? treeLimits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        var roots = D3D9EffectProjectRoots.Create(plan, implicitResolver, cancellationToken);
        D3D9EffectResourceTree? tree = null;
        IReadOnlyList<D3D9EffectDependencyDiagnostic> diagnostics = [];
        if (roots.Roots.Count != 0)
        {
            using var source = new D3D9EffectDependencyFileSource(limits);
            var collected = new D3D9EffectDependencyCollector(source, inspector, limits)
                .Collect(roots.Roots, cancellationToken);
            if (collected.Status != D3D9EffectDependencyStatus.Complete)
                return new(collected.Status, null, collected.Diagnostics);
            tree = D3D9EffectResourceTree.FromClosure(collected.Closure!, treeLimits,
                collected.Diagnostics, cancellationToken);
            diagnostics = collected.Diagnostics;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var emm = D3D9EmmPma.Capture(plan, []);
        var frozen = D3D9FrozenEffectProject.Capture(roots, tree, cancellationToken);
        return new(D3D9EffectDependencyStatus.Complete, new(emm, frozen), diagnostics);
    }

    /// <summary>Give converted objects explicit no-effect assignments without discovering or opening
    /// neighbouring FX files. The synthetic EMM path supplies only a lexical context.</summary>
    public static D3D9ClassicEffectsPreparation CreateUnassigned(string absoluteEmmPath,
        IEnumerable<EmmObjectBinding> bindings, CancellationToken cancellationToken = default)
    {
        var document = EmmReader.Read("[Info]\nVersion=3\n[Object]\n[Effect]\nDefault=none\n"u8);
        var plan = EmmImportPlan.Create(document, absoluteEmmPath, bindings, Path.GetDirectoryName(absoluteEmmPath));
        var roots = D3D9EffectProjectRoots.Create(plan, _ => null, cancellationToken);
        return new(D3D9EmmPma.Capture(plan, []), D3D9FrozenEffectProject.Capture(roots, null, cancellationToken));
    }

    /// <summary>Uses only validated package metadata and bytes. Legacy packages without frozen choices
    /// need an explicit import; this method never re-runs implicit discovery against original directories.</summary>
    public static D3D9ClassicEffectsPreparation Recover(D3D9ClassicPreparedProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Emm is null || project.FrozenEffects is null)
            throw new NotSupportedException("This Classic package has no frozen effect assignments; import its effects explicitly before execution.");
        return new(project.Emm, project.FrozenEffects);
    }

    /// <summary>Composes detached save codecs before the neutral snapshot. An explicitly empty effect
    /// project also removes a previously retained tree instead of reviving stale effects on re-save.</summary>
    public PmaSaveOptions CreateSaveOptions(SceneProjectSession project, PmaSaveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options = D3D9ClassicProjectStore.CreateSaveOptions(options);
        if (Tree is null)
        {
            if (options.OptionalExtensions.Keys.Any(key => key.Equals(D3D9EffectResourceTree.ExtensionName, StringComparison.OrdinalIgnoreCase)) ||
                options.AdditionalResources.Keys.Any(key => key.StartsWith(TreeResourcePrefix, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Caller options already supply an effect tree for an empty effect project.", nameof(options));
            var old = options.ContentSelection!;
            var selection = new PmaContentSelection(old.ExcludedExtensionNames.Append(D3D9EffectResourceTree.ExtensionName),
                old.ExcludedResourcePrefixes.Append(TreeResourcePrefix), old.RetainNativeLighting,
                old.RetainModelPurposes, old.RetainRenderingStyles);
            options = options with { ContentSelection = selection,
                ValidationOptions = options.ValidationOptions with { ContentSelection = selection } };
        }
        options = D3D9EmmPma.CreateSaveOptions(project, Emm, options);
        if (Tree is not null) options = D3D9EffectResourceTree.CreateSaveOptions(project, Tree, options);
        return D3D9FrozenEffectProject.CreateSaveOptions(project, FrozenEffects, options, cancellationToken);
    }

    /// <summary>Materializes private files and replays the same compiler requests through their frozen map
    /// before producing the existing runtime configuration. The caller owns the returned lease for the
    /// entire renderer-session lifetime. This CPU preflight is not arbitrary-effect or GPU acceptance.</summary>
    public D3D9ClassicEffectsRuntime PrepareRuntime(string existingAbsoluteCacheDirectory,
        ID3D9EffectDependencyInspector inspector, D3D9EffectDependencyLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        cancellationToken.ThrowIfCancellationRequested();
        D3D9EffectResourceTreeMaterialization? materialization = null;
        try
        {
            D3D9EffectDependencyReplayResult? replay = null;
            var map = new D3D9EffectResourceMap([]);
            if (Tree is not null)
            {
                materialization = Tree.Materialize(existingAbsoluteCacheDirectory, cancellationToken);
                map = materialization.CreateResourceMap();
                replay = D3D9EffectDependencyReplay.Validate(Tree, map, inspector, limits, cancellationToken);
                if (replay.Status == D3D9EffectDependencyReplayStatus.Cancelled)
                    throw new OperationCanceledException("Effect replay was cancelled.", cancellationToken);
                if (!replay.Accepted) throw new D3D9ClassicEffectsReplayException(replay);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = D3D9EmmRuntimeConfiguration.Create(FrozenEffects.ImportPlan, FrozenEffects.RootSeeds, map);
            return new(configuration, replay, materialization);
        }
        catch { materialization?.Dispose(); throw; }
    }
}

/// <summary>Structured compiler/request evidence remains available to the GUI's diagnostic reporter.</summary>
public sealed class D3D9ClassicEffectsReplayException : InvalidOperationException
{
    public D3D9ClassicEffectsReplayException(D3D9EffectDependencyReplayResult result)
        : base("Captured effects cannot be replayed unchanged through their private resource map.") => Result = result;
    public D3D9EffectDependencyReplayResult Result { get; }
}

/// <summary>Owns private effect files. Configuration is borrowed and must not outlive this lease.</summary>
public sealed class D3D9ClassicEffectsRuntime : IDisposable
{
    private readonly object gate = new();
    private D3D9EffectResourceTreeMaterialization? materialization;
    internal D3D9ClassicEffectsRuntime(D3D9EmmRuntimeConfiguration configuration,
        D3D9EffectDependencyReplayResult? replay, D3D9EffectResourceTreeMaterialization? materialization)
    { Configuration = configuration; Replay = replay; this.materialization = materialization; }
    public D3D9EmmRuntimeConfiguration Configuration { get; }
    public D3D9EffectDependencyReplayResult? Replay { get; }
    public void Dispose()
    {
        lock (gate)
        {
            // Renderer/session leases can make deletion fail temporarily. Keep ownership so the caller
            // can release those leases and retry, as with the existing materialization itself.
            materialization?.Dispose();
            materialization = null;
        }
    }
}
