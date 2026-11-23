using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed record PreparedPmmModel(PmmModel Source, string Path, PmxModel Model, VmdMotion Motion, VmdMotion SavedPoseMotion)
{
    public ModelPurpose Purpose { get; init; } = new(ModelRole.Unspecified);
    public bool VisibleAt(uint frame)
    {
        bool visible = Source.InitialModelKeyframe.Visible;
        foreach (var key in Motion.IkKeyframes.OrderBy(k => k.Frame))
        {
            if (key.Frame > frame) break;
            visible = key.Visible;
        }
        return visible;
    }
}

public sealed record PmmUnresolvedAsset(int ModelIndex, PmmString Reference, PmmAssetResolution Resolution, string? Error);

/// <summary>A fully prepared candidate. The scene host commits only after this and its rendering preparation succeed.</summary>
public sealed class PmmProjectImportPlan
{
    public PmmDocument Document { get; }
    public string ProjectPath { get; }
    public int CurrentFrame => Document.CurrentFrame;
    /// <summary>Runtime policy kept separate from fields absent in PMM 0001.</summary>
    public PmmEffectivePhysicsSettings EffectivePhysics { get; }
    public int OutputWidth => Document.OutputWidth;
    public int OutputHeight => Document.OutputHeight;
    public IReadOnlyList<PreparedPmmModel> Models { get; }
    public IReadOnlyList<PmmUnresolvedAsset> UnresolvedAssets { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyList<PmmModelRoleSelection> ModelRoleSelections { get; }
    public bool HasUnconfirmedRoles => ModelRoleSelections.Any(row => !row.IsConfirmed);
    public IReadOnlyDictionary<int, ModelPurpose> ModelPurposes { get; }
    public bool CanCommit => UnresolvedAssets.Count == 0 && Models.Count == Document.Models.Count;

    internal PmmProjectImportPlan(PmmDocument document, string projectPath, List<PreparedPmmModel> models,
        List<PmmUnresolvedAsset> unresolved, List<string> diagnostics)
        : this(document, projectPath, models, unresolved, diagnostics, new Dictionary<int, ModelPurpose>()) { }

    private PmmProjectImportPlan(PmmDocument document, string projectPath, List<PreparedPmmModel> models,
        List<PmmUnresolvedAsset> unresolved, List<string> diagnostics, IReadOnlyDictionary<int, ModelPurpose> purposes)
    {
        Document = document; ProjectPath = projectPath; EffectivePhysics = PmmPhysicsSettings.Resolve(document); Models = models.AsReadOnly();
        UnresolvedAssets = unresolved.AsReadOnly(); Diagnostics = diagnostics.AsReadOnly();
        ModelPurposes = new System.Collections.ObjectModel.ReadOnlyDictionary<int, ModelPurpose>(new Dictionary<int, ModelPurpose>(purposes));
        ModelRoleSelections = Array.AsReadOnly(document.Models.Select(source => new PmmModelRoleSelection(source.Index,
            source.Name.Value, source.Path, models.FirstOrDefault(model => model.Source.Index == source.Index)?.Path,
            purposes.TryGetValue(source.Index, out var purpose) ? purpose : null)).ToArray());
    }

    /// <summary>Returns a new candidate with these explicit selections merged. Validation is atomic;
    /// duplicate source paths remain independent because only stored model indices are accepted.</summary>
    public PmmProjectImportPlan WithModelPurposes(IReadOnlyDictionary<int, ModelPurpose> purposes)
    {
        ArgumentNullException.ThrowIfNull(purposes);
        var identities = ModelRoleSelections.Select(row => row.ModelIndex).ToHashSet();
        if (identities.Count != ModelRoleSelections.Count) throw new InvalidDataException("PMM model indices must be unique for role confirmation.");
        var next = new Dictionary<int, ModelPurpose>(ModelPurposes);
        foreach (var pair in purposes)
        {
            if (!identities.Contains(pair.Key)) throw new ArgumentException($"Unknown PMM model index #{pair.Key}.", nameof(purposes));
            ArgumentNullException.ThrowIfNull(pair.Value);
            SceneLightingEditor.ValidatePurpose("pmm-model:" + pair.Key, pair.Value.Role, pair.Value.EnvironmentIndoor);
            next[pair.Key] = pair.Value;
        }
        return new(Document, ProjectPath, Models.Select(model => model with
            { Purpose = next.TryGetValue(model.Source.Index, out var purpose) ? purpose : new(ModelRole.Unspecified) }).ToList(),
            UnresolvedAssets.ToList(), Diagnostics.ToList(), next);
    }

    /// <summary>Atomically confirms all outstanding rows, returning a new candidate only when every model has an answer.</summary>
    public PmmProjectImportPlan ConfirmRoles(IReadOnlyDictionary<int, ModelPurpose> purposes)
        => WithModelPurposes(purposes).RequireConfirmedRoles();

    /// <summary>New interactive hosts call this before commit; legacy Prepare/playback APIs retain their behavior.</summary>
    public PmmProjectImportPlan RequireConfirmedRoles()
    {
        if (HasUnconfirmedRoles) throw new InvalidOperationException("Confirm a role for every PMM model before committing this candidate; Unspecified is an explicit supported choice.");
        return this;
    }
}

/// <summary>Loads read-only model assets and embedded motion without mutating an active project.</summary>
public static class PmmProjectImport
{
    public static PmmProjectImportPlan Prepare(PmmDocument document, string projectPath, PmmImportOptions? options = null, CancellationToken cancellationToken = default)
        => PrepareWithModelLoader(document, projectPath, options, null, cancellationToken);

    internal static PmmProjectImportPlan PrepareWithModelLoader(PmmDocument document, string projectPath,
        PmmImportOptions? options, Func<string, PmxModel>? modelLoader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(document); ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        options ??= new();
        var resolver = new PmmAssetPathResolver(projectPath, options);
        var models = new List<PreparedPmmModel>();
        var unresolved = new List<PmmUnresolvedAsset>();
        var diagnostics = new List<string>(document.Diagnostics);
        var modelCache = new Dictionary<string, PmxModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in document.Models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.ModelPathOverrides.TryGetValue(source.Index, out string? overridePath);
            var asset = resolver.Resolve(source.Path, overridePath);
            if (asset.Status != PmmAssetResolutionStatus.Resolved)
            {
                unresolved.Add(new(source.Index, source.Path, asset, null));
                continue;
            }
            try
            {
                if (!modelCache.TryGetValue(asset.Path!, out PmxModel? model))
                {
                    if (modelLoader is not null) model = modelLoader(asset.Path!);
                    else
                    {
                        string extension = Path.GetExtension(asset.Path!);
                        if (extension.Equals(".pmx", StringComparison.OrdinalIgnoreCase))
                            model = PmxReader.Read(asset.Path!);
                        else if (extension.Equals(".pmd", StringComparison.OrdinalIgnoreCase))
                        {
                            using var input = File.OpenRead(asset.Path!);
                            model = PmdPmxAdapter.Convert(PmdReader.Read(input), Path.GetDirectoryName(asset.Path));
                        }
                        else throw new NotSupportedException("PMM project character import supports PMX and PMD assets; this model reference is retained for repair.");
                    }
                    modelCache[asset.Path!] = model;
                }
                cancellationToken.ThrowIfCancellationRequested();
                VmdMotion motion = PmmMotionAdapter.Convert(source, model);
                VmdMotion savedPose = PmmMotionAdapter.ConvertSavedPose(source, model);
                models.Add(new(source, asset.Path!, model, motion, savedPose));
                diagnostics.AddRange(motion.Diagnostics.Select(d => $"Model #{source.Index}: {d}"));
                diagnostics.AddRange(savedPose.Diagnostics.Select(d => $"Model #{source.Index}: {d}"));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or PmxFormatException or UnauthorizedAccessException or FormatException or NotSupportedException or ArgumentException)
            {
                unresolved.Add(new(source.Index, source.Path, asset with { Status = PmmAssetResolutionStatus.Invalid }, e.Message));
            }
        }
        if (document.Version == 1)
            diagnostics.Add("PMM 0001 has no physics/gravity controls; application defaults apply: physics On, gravity (0,-98,0), ground On. Absent camera-parent and model edge/self-shadow overrides leave the camera unparented and authored model material settings unchanged. These are runtime policy, not recovered original fields.");
        cancellationToken.ThrowIfCancellationRequested();
        if (document.Accessories.Count > 0)
            diagnostics.Add($"PMM contains {document.Accessories.Count} accessory asset(s); their paths, bindings and tracks remain in project metadata and are not rendered by the current character importer.");
        diagnostics.Add("PMM camera, light, output size, current frame, gravity, self-shadow and media settings remain in Document for the scene host to apply.");
        var plan = new PmmProjectImportPlan(document, Path.GetFullPath(projectPath), models, unresolved, diagnostics.Distinct(StringComparer.Ordinal).ToList());
        return plan.WithModelPurposes(options.ModelPurposes);
    }
}
