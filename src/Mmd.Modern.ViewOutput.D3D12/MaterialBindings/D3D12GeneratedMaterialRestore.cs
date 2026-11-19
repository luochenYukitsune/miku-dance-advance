using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Restores editable generated-map state from a separately restored source rest frame,
/// guarded profile and PMA manifest. The checks establish byte consistency and traceable metadata;
/// a manifest is not a signature or proof of the author's identity.</summary>
public static class D3D12GeneratedMaterialRestore
{
    // Large enough for every valid preparation, whose complete source texture table is limited to 4096 slots.
    public const int MaxManifestBytes = 16 * 1024 * 1024;
    private const string ManifestMeaning = "MDA generated auxiliary data; not original game material maps";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 8
    };

    /// <summary>No model files or GPU resources are accessed. The manifest must describe exactly
    /// this prepared profile, its original diffuse inputs and its appended generated image slots.</summary>
    public static D3D12GeneratedEndfieldMaterialPreparationResult Restore(OriginalFrameScene sourceRest,
        D3D12MaterialBindingProfile profile, ReadOnlySpan<byte> manifest)
    {
        ArgumentNullException.ThrowIfNull(sourceRest);
        ArgumentNullException.ThrowIfNull(profile);
        if (manifest.Length is 0 or > MaxManifestBytes)
            throw Invalid("Generated material manifest exceeds its byte budget or is empty.");
        Manifest data;
        try { data = JsonSerializer.Deserialize<Manifest>(manifest, Json) ?? throw Invalid("Missing manifest."); }
        catch (JsonException error) { throw new InvalidDataException("Malformed generated material manifest.", error); }
        if (data.SchemaVersion is not (1 or 2) || !data.IsGenerated || data.Meaning != ManifestMeaning
            || data.ProfileStableId != profile.StableId)
            throw Invalid("Unsupported generated material schema, identity or meaning.");
        if (data.Materials is null || data.Materials.Length is 0 or > D3D12MaterialBindingProfile.MaxMaterials
            || data.Materials.Length != profile.Materials.Count)
            throw Invalid("Generated material selections must match the complete prepared profile.");

        var source = sourceRest.Surface.Snapshot;
        var layout = D3D12MaterialBindingLayout.Capture(sourceRest);
        layout.ValidateFingerprint(profile.LayoutFingerprint, profile.LayoutVersion);
        int firstGenerated = source.Textures.Count - 3 * data.Materials.Length;
        if (firstGenerated < 0 || source.Textures.Count > D3D12GeneratedEndfieldMaterialPreparation.MaxPreparedTextures)
            throw Invalid("Generated outputs must be an appended, bounded source texture suffix.");
        // An existing authored diffuse/toon/sphere slot must never become editable generated storage.
        for (int i = 0; i < source.Materials.Count; i++)
        {
            var material = source.Materials[i];
            if (material.DiffuseTexture >= firstGenerated || material.ToonTexture >= firstGenerated
                || sourceRest.Surface.Materials[i].SphereTexture >= firstGenerated)
                throw Invalid("Generated slots overlap original material texture inputs.");
        }

        var rows = profile.Materials.ToDictionary(row => row.LocalMaterialIndex);
        var sourceHashes = new Dictionary<int, string>();
        var provenance = new D3D12GeneratedEndfieldMaterialPreparation.Provenance[data.Materials.Length];
        int previousMaterial = -1;
        long generatedBytes = 0;
        for (int index = 0; index < data.Materials.Length; index++)
        {
            var entry = data.Materials[index];
            if (entry is null || entry.LocalMaterialIndex <= previousMaterial
                || (uint)entry.LocalMaterialIndex >= (uint)source.Materials.Count
                || !rows.TryGetValue(entry.LocalMaterialIndex, out var row) || row.EndfieldMatcap is not { } maps)
                throw Invalid("Generated material indices must be unique, sorted and present in the prepared profile.");
            previousMaterial = entry.LocalMaterialIndex;
            if (!entry.IsGenerated || !entry.OutputsAreGenerated
                || entry.GeneratorRevision != D3D12GeneratedPmxMaterialMaps.GeneratorRevision
                || entry.Options is null || !Enum.TryParse<D3D12GeneratedPmxSurfaceKind>(entry.Options.Kind, out var kind)
                || !Enum.IsDefined(kind) || kind.ToString() != entry.Options.Kind)
                throw Invalid("Unsupported generated material flags, options or generator revision.");
            var options = new D3D12GeneratedPmxMapOptions(kind, entry.Options.MapSize,
                entry.Options.HeightFromLuminanceStrength, entry.Options.HighlightMask, entry.Options.MatcapWidth);
            try { options.Validate(); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid generated material options.", error); }
            generatedBytes += (long)options.MapSize * options.MapSize * 12;
            if (generatedBytes > D3D12GeneratedEndfieldMaterialPreparation.MaxGeneratedBytes)
                throw Invalid("Generated maps exceed the aggregate pixel budget.");
            if (row.Role != (kind == D3D12GeneratedPmxSurfaceKind.Hair
                ? D3D12CharacterMaterialRole.Hair : D3D12CharacterMaterialRole.Body))
                throw Invalid("Generated surface kind does not match the explicit profile role.");
            int sourceOrdinal = source.Materials[entry.LocalMaterialIndex].DiffuseTexture;
            if (entry.SourceSceneTextureOrdinal != sourceOrdinal || sourceOrdinal < -1 || sourceOrdinal >= firstGenerated
                || entry.SourceUsesWhiteFallback != (sourceOrdinal == -1))
                throw Invalid("Generated material source ordinal does not match its original diffuse input.");
            if (sourceOrdinal >= 0 && source.Textures[sourceOrdinal].Kind != SceneTextureKind.Image)
                throw Invalid("Generated material source must be an original Image.");
            int width = sourceOrdinal < 0 ? 1 : source.Textures[sourceOrdinal].Width;
            int height = sourceOrdinal < 0 ? 1 : source.Textures[sourceOrdinal].Height;
            if (data.SchemaVersion == 2 && (entry.SourceWidth != width || entry.SourceHeight != height)
                || data.SchemaVersion == 1 && (entry.SourceWidth is not null || entry.SourceHeight is not null))
                throw Invalid("Generated material source dimensions do not match its original input.");
            if (!sourceHashes.TryGetValue(sourceOrdinal, out string? sourceHash))
            {
                sourceHash = sourceOrdinal < 0 ? Convert.ToHexString(SHA256.HashData(new byte[] { 255, 255, 255, 255 }))
                    : source.Textures[sourceOrdinal].RgbaSha256.ToUpperInvariant();
                sourceHashes.Add(sourceOrdinal, sourceHash);
            }
            if (entry.SourceRgbaSha256 != sourceHash)
                throw Invalid("Generated material source RGBA hash does not match the original diffuse input.");

            int start = firstGenerated + 3 * index;
            if (entry.Outputs is null || entry.Outputs.Length != 3)
                throw Invalid("Generated material requires exactly Normal, P and MatCap outputs.");
            ValidateOutput(entry.Outputs[0], "normal", "Numerical linear packed RG tangent normal", start, maps.Normal);
            ValidateOutput(entry.Outputs[1], "p", "Numerical linear Endfield P material gates", start + 1, maps.P);
            ValidateOutput(entry.Outputs[2], "matcap", "Linear RGB generated MatCap shape; alpha is color coverage", start + 2, maps.Matcap);
            if (entry.OutputsUserEdited != entry.Outputs.Any(output => output.UserEdited))
                throw Invalid("Generated material aggregate edit flag is inconsistent.");
            provenance[index] = new(entry.LocalMaterialIndex, sourceOrdinal, sourceHash, entry.GeneratorRevision,
                options, firstGenerated, start, start + 1, start + 2,
                entry.Outputs[0].UserEdited, entry.Outputs[1].UserEdited, entry.Outputs[2].UserEdited, width, height);

            void ValidateOutput(Output? output, string name, string meaning, int ordinal,
                D3D12MaterialBindingProfileTexture selector)
            {
                if (output is null || output.Name != name || output.Meaning != meaning || !output.IsGenerated
                    || output.SourceSceneTextureOrdinal != ordinal || output.Width != options.MapSize
                    || output.Height != options.MapSize || selector.SourceSceneTextureOrdinal != ordinal)
                    throw Invalid("Generated output identity, ordinal or dimensions do not match its profile.");
                var actual = layout.CaptureTexture(ordinal);
                if (actual.Kind != SceneTextureKind.Image || actual.Width != output.Width || actual.Height != output.Height
                    || actual.RgbaSha256 != output.RgbaSha256 || actual != selector)
                    throw Invalid("Generated output kind or RGBA guard does not match its source slot and profile.");
            }
        }
        // Nested selectors may also reference authored images outside the generated
        // suffix. Validate every saved guard before editing is allowed to recapture it.
        try { _ = D3D12MaterialBindingProfileResolver.Resolve(profile, sourceRest, Guid.NewGuid(), "mmd"); }
        catch (ArgumentException error) { throw new InvalidDataException("Generated material profile no longer matches its source inputs.", error); }
        return new(sourceRest, profile, provenance);
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private sealed class Manifest
    {
        public required int SchemaVersion { get; init; }
        public required bool IsGenerated { get; init; }
        public required string ProfileStableId { get; init; }
        public required string Meaning { get; init; }
        public required Material[] Materials { get; init; }
    }

    private sealed class Material
    {
        public required int LocalMaterialIndex { get; init; }
        public required bool IsGenerated { get; init; }
        public required bool OutputsAreGenerated { get; init; }
        public required bool OutputsUserEdited { get; init; }
        public required int SourceSceneTextureOrdinal { get; init; }
        public required bool SourceUsesWhiteFallback { get; init; }
        public required string SourceRgbaSha256 { get; init; }
        public int? SourceWidth { get; init; }
        public int? SourceHeight { get; init; }
        public required string GeneratorRevision { get; init; }
        public required Options Options { get; init; }
        public required Output[] Outputs { get; init; }
    }

    private sealed class Options
    {
        public required string Kind { get; init; }
        public required int MapSize { get; init; }
        public required float HeightFromLuminanceStrength { get; init; }
        public required float? HighlightMask { get; init; }
        public required float? MatcapWidth { get; init; }
    }

    private sealed class Output
    {
        public required string Name { get; init; }
        public required string Meaning { get; init; }
        public required bool IsGenerated { get; init; }
        public required bool UserEdited { get; init; }
        public required int SourceSceneTextureOrdinal { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required string RgbaSha256 { get; init; }
    }
}
