using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>An explicit local material choice; neither material names nor image names infer a surface kind.</summary>
public sealed record D3D12GeneratedEndfieldMaterialSelection(int LocalMaterialIndex, D3D12GeneratedPmxMapOptions Options);

/// <summary>Detached derived assets and their ordinary portable material profile. The manifest is a
/// separate PMA additional resource; none of these data are written to the original model or image.</summary>
public sealed class D3D12GeneratedEndfieldMaterialPreparationResult
{
    private readonly D3D12GeneratedEndfieldMaterialPreparation.Provenance[] provenance;
    private readonly byte[] manifestBytes;
    public OriginalFrameScene SourceRest { get; }
    public D3D12MaterialBindingProfile Profile { get; }
    public byte[] ManifestBytes => manifestBytes.ToArray();

    internal D3D12GeneratedEndfieldMaterialPreparationResult(OriginalFrameScene sourceRest,
        D3D12MaterialBindingProfile profile, D3D12GeneratedEndfieldMaterialPreparation.Provenance[] provenance)
    {
        SourceRest = sourceRest; Profile = profile; this.provenance = provenance.ToArray();
        manifestBytes = D3D12GeneratedEndfieldMaterialPreparation.EncodeManifest(sourceRest, profile, provenance);
    }

    /// <summary>Replaces only this result's selected derived slots. Editing regenerates all image
    /// guards and provenance hashes while preserving the source diffuse image and previous result.</summary>
    public D3D12GeneratedEndfieldMaterialPreparationResult WithEditedMaps(int localMaterialIndex,
        SceneTexture? normal = null, SceneTexture? p = null, SceneTexture? matcap = null)
    {
        int index = Array.FindIndex(provenance, entry => entry.LocalMaterialIndex == localMaterialIndex);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(localMaterialIndex), "Select a material prepared by this result.");
        if (normal is null && p is null && matcap is null)
            throw new ArgumentException("Supply at least one edited derived map.");
        var source = SourceRest.Surface.Snapshot;
        var textures = source.Textures.ToArray();
        var entry = provenance[index];
        Replace(entry.NormalOrdinal, normal); Replace(entry.POrdinal, p); Replace(entry.MatcapOrdinal, matcap);
        void Replace(int ordinal, SceneTexture? replacement)
        {
            if (replacement is null) return;
            if (ordinal < entry.FirstGeneratedOrdinal || ordinal >= textures.Length)
                throw new ArgumentException("Only appended generated texture slots can be edited.");
            if (replacement.Kind != SceneTextureKind.Image || replacement.Width != textures[ordinal].Width
                || replacement.Height != textures[ordinal].Height)
                throw new ArgumentException("An edited map must be an Image with the generated map's exact dimensions.");
            textures[ordinal] = replacement;
        }
        var updated = provenance.ToArray();
        updated[index] = entry with
        {
            NormalUserEdited = entry.NormalUserEdited || normal is not null,
            PUserEdited = entry.PUserEdited || p is not null,
            MatcapUserEdited = entry.MatcapUserEdited || matcap is not null
        };
        var frame = D3D12GeneratedEndfieldMaterialPreparation.ReplaceTextures(SourceRest, textures);
        var layout = D3D12MaterialBindingLayout.Capture(frame);
        // A reopened profile can also use a generated slot in its palette/base-image inputs.
        // Refresh the complete selected image graph rather than only the three root guards.
        var rows = Profile.Materials.Select(row => D3D12GeneratedStyleMaterialResult.RefreshGuards(row, layout));
        var profile = new D3D12MaterialBindingProfile(Profile.StableId, Profile.Name, layout.Fingerprint, rows, layout.Version,
            Profile.OutlineWidths, Profile.PipelineId, Profile.GuessMaterialRoles);
        return new(frame, profile, updated);
    }
}

/// <summary>Prepares generated, explicitly labelled auxiliary inputs for the existing Endfield
/// MatCap adapter. This does not reconstruct unavailable original game material data.</summary>
public static class D3D12GeneratedEndfieldMaterialPreparation
{
    /// <summary>Aggregate bytes of the three generated RGBA8 images, before allocating any output.</summary>
    public const long MaxGeneratedBytes = 256L * 1024 * 1024;
    public const int MaxPreparedTextures = 4096;

    public static D3D12GeneratedEndfieldMaterialPreparationResult Prepare(OriginalFrameScene sourceRest,
        IEnumerable<D3D12GeneratedEndfieldMaterialSelection> selections, string stableId, string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRest); ArgumentNullException.ThrowIfNull(selections);
        cancellationToken.ThrowIfCancellationRequested();
        var source = sourceRest.Surface.Snapshot;
        // Capture rejects evaluated/deformed geometry. Never strip a dynamic flag to disguise posed inputs as rest.
        var originalLayout = D3D12MaterialBindingLayout.Capture(sourceRest);
        _ = new D3D12MaterialBindingProfile(stableId, name, originalLayout.Fingerprint, []);
        var selected = selections.Take(D3D12MaterialBindingProfile.MaxMaterials + 1).ToArray();
        if (selected.Length is 0 or > D3D12MaterialBindingProfile.MaxMaterials)
            throw new ArgumentException("Select 1 to 4096 explicit source-local materials.", nameof(selections));
        if ((long)source.Textures.Count + 3L * selected.Length > MaxPreparedTextures)
            throw new ArgumentException("The prepared source cannot exceed 4096 texture slots.", nameof(selections));
        var seen = new HashSet<int>();
        long generatedBytes = 0;
        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item); ArgumentNullException.ThrowIfNull(item.Options);
            item.Options.Validate();
            generatedBytes += (long)item.Options.MapSize * item.Options.MapSize * 12;
            if (generatedBytes > MaxGeneratedBytes)
                throw new ArgumentException("Selected generated maps exceed the aggregate 256 MiB RGBA8 budget.", nameof(selections));
            if ((uint)item.LocalMaterialIndex >= (uint)source.Materials.Count)
                throw new ArgumentOutOfRangeException(nameof(selections), "A selected source-local material does not exist.");
            if (!seen.Add(item.LocalMaterialIndex))
                throw new ArgumentException("Select each material only once.", nameof(selections));
            int ordinal = source.Materials[item.LocalMaterialIndex].DiffuseTexture;
            if (ordinal >= 0 && source.Textures[ordinal].Kind != SceneTextureKind.Image)
                throw new ArgumentException("Auxiliary generation requires an Image diffuse input; procedural toons are not source images.", nameof(selections));
        }
        Array.Sort(selected, (left, right) => left.LocalMaterialIndex.CompareTo(right.LocalMaterialIndex));
        var textures = source.Textures.ToList();
        var provenance = new List<Provenance>();
        var rows = new List<D3D12MaterialBindingProfileRow>();
        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var material = source.Materials[item.LocalMaterialIndex];
            int ordinal = material.DiffuseTexture;
            var input = ordinal < 0 ? new SceneTexture("generated-white-diffuse-input", 1, 1, [255, 255, 255, 255])
                : source.Textures[ordinal];
            var generated = D3D12GeneratedPmxMaterialMaps.Generate(input, item.Options, cancellationToken);
            int start = textures.Count;
            textures.Add(generated.Normal); textures.Add(generated.P); textures.Add(generated.Matcap);
            provenance.Add(new(item.LocalMaterialIndex, ordinal, generated.SourceRgbaSha256,
                generated.GeneratorRevision, generated.Options, source.Textures.Count, start, start + 1, start + 2,
                SourceWidth: input.Width, SourceHeight: input.Height));
        }
        var frame = ReplaceTextures(sourceRest, textures);
        var layout = D3D12MaterialBindingLayout.Capture(frame);
        foreach (var item in provenance)
        {
            var eg = OriginalStyleMath.ResolveHostColors(source.Materials[item.LocalMaterialIndex], sourceRest.Surface.Profile).EgColor;
            var baseColor = Vector3.Clamp(new(eg.X, eg.Y, eg.Z), Vector3.Zero, Vector3.One);
            var light = Vector3.Normalize(source.LightDirection);
            var normalGuard = layout.CaptureTexture(item.NormalOrdinal);
            var pGuard = layout.CaptureTexture(item.POrdinal);
            var matcapGuard = layout.CaptureTexture(item.MatcapOrdinal);

            var slots = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["normal"] = item.NormalOrdinal,
                ["p"] = item.POrdinal,
                ["matcap"] = item.MatcapOrdinal
            });
            var numbers = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["uvChannel"] = 0,
                ["normalStrength"] = 1,
                ["rotationRadians"] = 0,
                ["highlightCoefficient"] = 0,
                ["shadowHardness"] = 0.5,
                ["baselineStrength"] = 0.35
            });
            var bools = new ReadOnlyDictionary<string, bool>(new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["coupleEndfieldPalette"] = false
            });
            var vectors = new ReadOnlyDictionary<string, float[]>(new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                ["baseColor"] = [baseColor.X, baseColor.Y, baseColor.Z],
                ["highlightColor"] = [0f, 0f, 0f],
                ["lightControlDirection"] = [light.X, light.Y, light.Z]
            });
            var strings = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["matcapEncoding"] = "LinearRgb",
                ["generatorRevision"] = "mikk-v1"
            });

            var binding = new StyleMaterialBindingData("endfield", "matcap", slots, numbers, bools, vectors, strings);
            rows.Add(new(
                item.LocalMaterialIndex,
                item.Options.Kind == D3D12GeneratedPmxSurfaceKind.Hair ? D3D12CharacterMaterialRole.Hair : D3D12CharacterMaterialRole.Body,
                pluginBindings: [binding],
                textureGuards: [normalGuard, pGuard, matcapGuard]));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(frame, new(stableId, name, layout.Fingerprint, rows, layout.Version, pipelineId: "endfield"), provenance.ToArray());
    }

    internal static OriginalFrameScene ReplaceTextures(OriginalFrameScene original, IEnumerable<SceneTexture> textures)
    {
        var source = original.Surface.Snapshot;
        var snapshot = new SceneSnapshot(source.Vertices, source.Indices, source.Materials, textures,
            source.World, source.Camera, source.LightDirection, source.AdditionalUvs, source.RawRadialSeeds);
        return new(new(snapshot, original.Surface.Materials, original.Surface.Profile), original.Outline,
            original.OutlineOptions, original.Options, original.Materials, original.Lighting, original.EditorOverlay);
    }

    internal sealed record Provenance(int LocalMaterialIndex, int SourceSceneTextureOrdinal, string SourceRgbaSha256,
        string GeneratorRevision, D3D12GeneratedPmxMapOptions Options, int FirstGeneratedOrdinal,
        int NormalOrdinal, int POrdinal, int MatcapOrdinal, bool NormalUserEdited = false,
        bool PUserEdited = false, bool MatcapUserEdited = false, int SourceWidth = 1, int SourceHeight = 1);

    internal static byte[] EncodeManifest(OriginalFrameScene frame, D3D12MaterialBindingProfile profile,
        IReadOnlyList<Provenance> provenance)
    {
        var layout = D3D12MaterialBindingLayout.Capture(frame);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 2);
            writer.WriteBoolean("isGenerated", true); writer.WriteString("profileStableId", profile.StableId);
            writer.WriteString("meaning", "MDA generated auxiliary data; not original game material maps");
            writer.WriteStartArray("materials");
            foreach (var item in provenance)
            {
                writer.WriteStartObject(); writer.WriteNumber("localMaterialIndex", item.LocalMaterialIndex);
                writer.WriteBoolean("isGenerated", true); writer.WriteBoolean("outputsAreGenerated", true);
                writer.WriteBoolean("outputsUserEdited", item.NormalUserEdited || item.PUserEdited || item.MatcapUserEdited);
                writer.WriteNumber("sourceSceneTextureOrdinal", item.SourceSceneTextureOrdinal);
                writer.WriteBoolean("sourceUsesWhiteFallback", item.SourceSceneTextureOrdinal < 0);
                writer.WriteString("sourceRgbaSha256", item.SourceRgbaSha256);
                writer.WriteNumber("sourceWidth", item.SourceWidth); writer.WriteNumber("sourceHeight", item.SourceHeight);
                writer.WriteString("generatorRevision", item.GeneratorRevision);
                writer.WriteStartObject("options"); writer.WriteString("kind", item.Options.Kind.ToString());
                writer.WriteNumber("mapSize", item.Options.MapSize);
                writer.WriteNumber("heightFromLuminanceStrength", item.Options.HeightFromLuminanceStrength);
                WriteNullable("highlightMask", item.Options.HighlightMask); WriteNullable("matcapWidth", item.Options.MatcapWidth);
                writer.WriteEndObject();
                writer.WriteStartArray("outputs");
                Output("normal", "Numerical linear packed RG tangent normal", item.NormalOrdinal, item.NormalUserEdited);
                Output("p", "Numerical linear Endfield P material gates", item.POrdinal, item.PUserEdited);
                Output("matcap", "Linear RGB generated MatCap shape; alpha is color coverage", item.MatcapOrdinal, item.MatcapUserEdited);
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
            void WriteNullable(string name, float? value)
            { if (value.HasValue) writer.WriteNumber(name, value.Value); else writer.WriteNull(name); }
            void Output(string name, string meaning, int ordinal, bool edited)
            {
                var guard = layout.CaptureTexture(ordinal);
                writer.WriteStartObject(); writer.WriteString("name", name); writer.WriteString("meaning", meaning);
                writer.WriteBoolean("isGenerated", true); writer.WriteBoolean("userEdited", edited);
                writer.WriteNumber("sourceSceneTextureOrdinal", ordinal); writer.WriteNumber("width", guard.Width);
                writer.WriteNumber("height", guard.Height); writer.WriteString("rgbaSha256", guard.RgbaSha256);
                writer.WriteEndObject();
            }
        }
        return stream.ToArray();
    }
}
