using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed record PmaPmmCompatibilityIssue(string Code, string Message, Guid? ObjectId = null);

public sealed class PmaPmmExportException(PmaPmmCompatibilityReport report)
    : NotSupportedException(string.Join(Environment.NewLine, report.Issues.Select(i => i.Message)) + Environment.NewLine + report.Recommendation)
{
    public PmaPmmCompatibilityReport Report { get; } = report;
}

public sealed class PmaPmmCompatibilityReport
{
    public IReadOnlyList<PmaPmmCompatibilityIssue> Issues { get; }
    public bool CanExport => Issues.Count == 0;
    public bool RecommendPma => !CanExport;
    public string? Recommendation => CanExport ? null : "PMM cannot preserve these features. Save as PMA to retain the complete project.";
    internal PmaPmmCompatibilityReport(IEnumerable<PmaPmmCompatibilityIssue> issues) => Issues = Array.AsReadOnly(issues.ToArray());
}

/// <summary>Backend contract for a future export dialog. Checks actual data and the selected PMM exporter,
/// rather than treating PMX assets as inherently lossy.</summary>
public static class PmaPmmCompatibility
{
    public static PmaPmmCompatibilityReport Inspect(SceneProjectSession project, PmmSessionExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project); options ??= new();
        var state = project.PmaSnapshot();
        var issues = new List<PmaPmmCompatibilityIssue>();
        void Add(string code, string message, Guid? id = null) => issues.Add(new(code, message, id));
        if (state.Lighting.Lights.Count != 0) Add("native-lights", $"{state.Lighting.Lights.Count} native light objects have no PMM representation.");
        if (state.Lighting.Groups.Count != 0) Add("native-light-groups", $"{state.Lighting.Groups.Count} native light groups have no PMM representation.");
        if (state.Lighting.Settings != SceneLightingSettings.Default)
            Add("native-lighting-settings", "Modern environment fill and overall exposure have no PMM representation.");
        if (state.CurrentFrame != Math.Floor(state.CurrentFrame) || state.CurrentFrame > int.MaxValue)
            Add("current-frame", "PMM needs an integer current frame within Int32 range.");
        foreach (var item in state.Objects)
        {
            if (project.PmaRetention is { } retained && IsWithin(retained.Directory, item.SourcePath))
                Add("temporary-model-source", "This model is owned by the open PMA extraction cache. Closing the PMA removes it; export needs permanent model assets before a PMM can reference it.", item.Id);
            if (item.Scale != 1 || Matrix4x4.CreateFromQuaternion(item.Rotation) != Matrix4x4.Identity || item.Source.Surface.Snapshot.World != Matrix4x4.Identity)
                Add("model-transform", "Model scale, rotation or additional source world transform cannot be preserved by PMM model records.", item.Id);
            var model = state.Motions.FirstOrDefault(m => m.ObjectId == item.Id)?.Model ?? options.StaticModels.GetValueOrDefault(item.Id);
            // The low-level PMM writer intentionally permits unresolved references. When the referenced asset
            // exists, verify all represented model data instead of relying on its topology-only safety check.
            if (model is not null && File.Exists(item.SourcePath))
            {
                try
                {
                    string[] changes = ChangedModelSections(item.SourcePath, model);
                    if (changes.Length != 0) Add("model-attributes", "Live model data differs from the PMM-referenced source in: " + string.Join(", ", changes) + ". Save as PMA to preserve these edits.", item.Id);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or PmxFormatException or NotSupportedException or ArgumentException)
                { Add("model-source-unreadable", "Cannot verify the referenced model's represented attributes: " + error.Message, item.Id); }
            }
            if (model is not null && item.Position != Vector3.Zero && (model.RigidBodies.Count != 0
                || model.Bones.Any(b => (b.Flags & (Mmd.Modern.Formats.PmxBoneFlags.AppendTranslate | Mmd.Modern.Formats.PmxBoneFlags.AppendRotate)) != 0)
                || model.Bones.Count == 0 || model.Bones.All(b => b.ParentIndex >= 0)
                || state.OutsideParents.Any(p => p.ObjectId == item.Id || p.ParentObjectId == item.Id)))
                Add("model-placement", "This model's placement translation cannot be baked without changing physics, append or outside-parent animation.", item.Id);
        }
        bool hasUnownedData = state.Motions.Any(m => m.Detachment is not null) || state.PmmControls?.HasReplacementMetadata == true;
        if (hasUnownedData)
            Add("detached-model-data", "PMM cannot retain unbound model data and explicit recovery masks. Save as PMA to preserve all orphan tracks, camera and outside-parent records.");
        foreach (var parent in state.OutsideParents)
            if (parent.Offset != Matrix4x4.Identity) Add("outside-parent-offset", "PMM cannot preserve an independent outside-parent offset matrix.", parent.ObjectId);
        var settings = project.Settings;
        double fps = (double)settings.FpsNumerator / settings.FpsDenominator;
        if (fps != (double)(float)fps)
            Add("output-settings", "PMM's float32 frame rate cannot preserve the project's exact rational output FPS. Save as PMA to retain it.");
        if (settings.Physics.SharedWorld) Add("shared-physics", "Modern shared physics-world selection has no PMM representation.");
        if (settings.Physics.CoupledSoftBodies) Add("coupled-soft-bodies", "Modern coupled rigid/soft-body solver selection has no PMM representation.");
        if (settings.Physics.Backend == ProjectPhysicsBackend.Bepu) Add("physics-backend", "The selected Bepu solver is not available in original PMM playback.");
        if (state.PmmControls is null && (settings.Physics.Gravity != new Vector3(0, -98, 0) || !settings.Physics.GroundEnabled))
            Add("physics-settings", "The selected PMM exporter cannot preserve the project's custom gravity or disabled physics ground.");
        if (!settings.OutlineEnabled || !settings.SelfShadowEnabled || !settings.GroundShadowEnabled)
            Add("render-settings", "Project outline or shadow switches are not transferred by the selected PMM exporter.");
        if (settings.Background != new Vector4(0, 0, 0, 1) && settings.Background != Vector4.One)
            Add("background", "PMM supports only opaque black or white project backgrounds. Save as PMA to retain this color.");
        if (project.Media.Count != 0) Add("media", "Modern packaged media tracks and offsets are not transferred by the selected PMM exporter.");
        if (project.PmaRenderingStyles.NeedsPersistence)
            Add("rendering-styles", "PMM cannot retain the project's global and per-instance rendering style selections. Save as PMA to preserve them.");
        var unknownExtensions = project.PmaRetention?.Extensions.Keys.Where(k => k != SceneRenderingStyles.ExtensionName).ToArray() ?? [];
        if (unknownExtensions.Length > 0) Add("optional-extensions", "PMM cannot retain the project's optional extension data: " + string.Join(", ", unknownExtensions));
        // The existing writer remains the authority for strict naming, templates, topology and authored-track constraints.
        try { if (!hasUnownedData) _ = project.PmaExportRaw(options); }
        catch (NotSupportedException error)
        { Add("pmm-writer", error.Message); }
        catch (PmmFormatException error)
        { Add(error.Message.Contains("cannot be encoded", StringComparison.Ordinal) ? "pmm-name-encoding" : "pmm-writer", error.Message); }
        return new(issues);
    }

    private static bool IsWithin(string directory, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions ModelJson = new() { IncludeFields = true };
    private static string[] ChangedModelSections(string path, PmxModel actual)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 512L * 1024 * 1024) throw new InvalidDataException("Model exceeds the existing 512 MiB writer contract.");
        var source = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pmx" => PmxReader.Read(input),
            ".pmd" => PmdPmxAdapter.Convert(PmdReader.Read(input), Path.GetDirectoryName(path)),
            _ => throw new NotSupportedException("Model reference must be PMX or PMD."),
        };
        // Canonical PMX serialization excludes inactive conditional fields and preserves every represented
        // section. Hashing separately avoids retaining two complete serialized models simultaneously.
        byte[] sourceHash = SHA256.HashData(PmxWriter.ToBytes(source));
        byte[] actualHash = SHA256.HashData(PmxWriter.ToBytes(actual));
        if (sourceHash.AsSpan().SequenceEqual(actualHash)) return [];
        var sections = new List<string>();
        void Compare<T>(string name, T left, T right)
        {
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left, ModelJson), JsonSerializer.SerializeToElement(right, ModelJson))) sections.Add(name);
        }
        Compare("header", source.Header, actual.Header);
        Compare("names/comments", new[] { source.Name, source.EnglishName, source.Comment, source.EnglishComment },
            new[] { actual.Name, actual.EnglishName, actual.Comment, actual.EnglishComment });
        Compare("vertices", source.Vertices, actual.Vertices); Compare("faces", source.Faces, actual.Faces);
        Compare("textures", source.Textures, actual.Textures); Compare("materials", source.Materials, actual.Materials);
        Compare("bones", source.Bones, actual.Bones); Compare("morphs", source.Morphs, actual.Morphs);
        Compare("display-frames", source.DisplayFrames, actual.DisplayFrames); Compare("rigid-bodies", source.RigidBodies, actual.RigidBodies);
        Compare("joints", source.Joints, actual.Joints); Compare("soft-bodies", source.SoftBodies, actual.SoftBodies);
        return sections.Count == 0 ? ["represented model data"] : sections.ToArray();
    }
}
