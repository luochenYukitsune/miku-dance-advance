using System.Text.Json.Nodes;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed partial class SceneProjectSession
{
    internal PmaRetainedState? PmaRetention { get; set; }
    internal Guid? PmaProjectIdentity { get; set; }

    /// <summary>Continues an opened package in a new session: the project identity and the package's retained
    /// optional extensions and render resources carry over, as they would if the session had been edited in place.
    /// Used when every model was removed and a new one added, which builds a fresh session. The retained resources
    /// stay owned by the original package; keep it alive while this session can save.</summary>
    public void ContinuePackageFrom(SceneProjectSession source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this)) return;
        PmaRetention ??= source.PmaRetention;
        PmaProjectIdentity ??= source.PmaProjectIdentity ?? source.PmaRetention?.ProjectId;
    }
    internal SceneAnimationSession.SaveSnapshot PmaSnapshot() { RequireIdle(); return animation.CaptureSaveSnapshot(); }
    internal PmmDocument PmaExportRaw(PmmSessionExportOptions? options) { RequireIdle(); return PmmSessionExporter.Export(animation, ProjectPmmOutput(options)); }
    private PmmSessionExportOptions ProjectPmmOutput(PmmSessionExportOptions? options)
    {
        options ??= new();
        return new() { Template = options.Template, TemplateModelIndices = options.TemplateModelIndices,
            StaticModels = options.StaticModels, AnsiCodePage = options.AnsiCodePage,
            OutputWidth = content.Settings.Width, OutputHeight = content.Settings.Height,
            PreferredFps = (float)((double)content.Settings.FpsNumerator / content.Settings.FpsDenominator), OverrideTemplateOutput = true,
            BlackBackgroundEnabled = content.Settings.Background == new System.Numerics.Vector4(0, 0, 0, 1) };
    }
    private void RequirePmmCompatibility(PmmSessionExportOptions? options)
    {
        var report = PmaPmmCompatibility.Inspect(this, options);
        if (!report.CanExport) throw new PmaPmmExportException(report);
    }

    internal PmaProjectInputs CapturePmaSaveInputs(PmaAccessoryPersistenceCapture? accessoryPersistence = null)
    {
        RequireIdle();
        if (accessoryPersistence is null) RequireAccessoryPersistenceSupport();
        else accessoryPersistence.ValidateLive(content.Accessories);
        // All containers below are private copies over the existing immutable authored assets.
        // Capturing does not complete preview leaves, seek, step or construct another session.
        return new(Clone(content.Composition), Clone(content.Lighting), animation.CaptureSaveSnapshot(),
            content.Settings, content.Media.ToArray(), content.RenderingStyles, PmaRetention, PmaProjectIdentity,
            content.Accessories.ToArray());
    }

    internal static PmaModelVersion[] StagePma(string stage, PmaProjectInputs input, PmaSaveOptions options, CancellationToken token)
    {
        var ids = input.Composition.Objects.Select(o => o.Id).ToHashSet();
        if (options.ModelOverrides.Keys.Any(id => !ids.Contains(id))) throw new ArgumentException("PMX override refers to an unknown actor.");
        string path = Path.Combine(stage, "project.mmdscene");
        // The legacy section deliberately has no rendering-style fields; the PMA manifest owns that versioned extension.
        // Both original and edited model spools belong to this configured, private PMA
        // stage. Do not create a second implicit cache at the system drive root.
        var limits = PortableModelAssets.Limits.FromPma(options.ValidationOptions) with { TemporaryDirectory = stage };
        SceneAnimationProjectStore.SaveSnapshot(path, input.Composition, input.Lighting, input.Animation,
            input.Settings, input.Media, new() { PortableLimits = limits }, token);
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        var layout = node["layout"]!;
        var originals = layout["objects"]!.AsArray().ToDictionary(o => o!["id"]!.GetValue<Guid>(), o => o!["sourcePath"]!.GetValue<string>());
        var overrides = input.Animation.Motions.ToDictionary(m => m.ObjectId, m => m.Model);
        foreach (var pair in options.ModelOverrides) overrides[pair.Key] = pair.Value ?? throw new ArgumentException("PMX override cannot be null.");
        using var portable = PortableModelAssets.Prepare(input.Composition.Objects, token, overrides, limits);
        portable.Publish(stage, token);
        layout["portableModels"] = new JsonArray(portable.Hashes.Select(hash => (JsonNode?)JsonValue.Create(hash)).ToArray());
        foreach (var item in layout["objects"]!.AsArray()) item!["sourcePath"] = portable.Paths[item["id"]!.GetValue<Guid>()];
        File.WriteAllText(path, node.ToJsonString());
        var retained = input.Retained?.Models.ToDictionary(m => m.ObjectId) ?? [];
        var models = new List<PmaModelVersion>();
        foreach (var item in input.Composition.Objects)
        {
            token.ThrowIfCancellationRequested();
            if (retained.TryGetValue(item.Id, out var previous))
            {
                PmaProjectStore.CopyRetainedModel(input.Retained!.Directory, stage, previous, token, limits.FileBytes);
                models.Add(previous with { EditedModelPath = portable.Paths[item.Id] });
            }
            else
            {
                string original = "sources/" + item.Id.ToString("N") + Path.GetExtension(item.SourcePath).ToLowerInvariant();
                PmaProjectStore.CopyFile(item.SourcePath, Path.Combine(stage, original), token, limits.FileBytes);
                models.Add(new(item.Id, original, originals[item.Id], portable.Paths[item.Id]));
            }
        }
        return models.ToArray();
    }

    internal static SceneProjectSession LoadPma(string path, Func<string, CancellationToken, SceneAnimationModelAsset> loadModel,
        IProjectPhysicsBackendProvider physics, PortableModelAssets.Limits limits, CancellationToken token,
        PmaAccessoryPersistenceCapture? accessoryPersistence = null)
    {
        ArgumentNullException.ThrowIfNull(physics);
        var prepared = SceneAnimationProjectStore.LoadPma(path, loadModel, physics, limits, token, accessoryPersistence);
        try { return new(prepared, physics, token); }
        catch { prepared.Dispose(); throw; }
    }
}
