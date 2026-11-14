using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit user classification; never inferred from names or painted albedo.</summary>
public sealed record D3D12GeneratedStyleMaterialSelection(int LocalMaterialIndex, D3D12GeneratedPmxMapOptions Options);

/// <summary>Detached generated inputs, portable profile and reproducible editable provenance.</summary>
public sealed class D3D12GeneratedStyleMaterialResult
{
    private readonly D3D12GeneratedStyleMaterialPreparation.Manifest manifest;
    public OriginalFrameScene SourceRest { get; }
    public D3D12MaterialBindingProfile Profile { get; }
    public string PipelineId => manifest.PipelineId;
    public byte[] ManifestBytes => D3D12GeneratedStyleMaterialPreparation.Encode(manifest);

    internal D3D12GeneratedStyleMaterialResult(OriginalFrameScene frame, D3D12MaterialBindingProfile profile,
        D3D12GeneratedStyleMaterialPreparation.Manifest manifest)
    { SourceRest = frame; Profile = profile; this.manifest = manifest; }

    /// <summary>Imports an edited numerical/color image into a named generated slot only.
    /// Original source images, coverage and the previous result are immutable.</summary>
    public D3D12GeneratedStyleMaterialResult WithEditedMap(int localMaterialIndex, string mapName, SceneTexture replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        int material = Array.FindIndex(manifest.Materials, value => value.LocalMaterialIndex == localMaterialIndex);
        if (material < 0) throw new ArgumentOutOfRangeException(nameof(localMaterialIndex));
        var entry = manifest.Materials[material];
        int map = Array.FindIndex(entry.Outputs, value => value.Name == mapName);
        if (map < 0) throw new ArgumentException("Select a named generated map from this material.", nameof(mapName));
        var old = entry.Outputs[map];
        if (replacement.Kind != SceneTextureKind.Image || replacement.Width != old.Guard.Width || replacement.Height != old.Guard.Height)
            throw new ArgumentException("An edited map must retain generated Image dimensions.", nameof(replacement));
        var textures = SourceRest.Surface.Snapshot.Textures.ToArray();
        textures[old.Guard.SourceSceneTextureOrdinal] = replacement;
        var frame = D3D12GeneratedEndfieldMaterialPreparation.ReplaceTextures(SourceRest, textures);
        var layout = D3D12MaterialBindingLayout.Capture(frame);
        var entries = manifest.Materials.ToArray(); var outputs = entry.Outputs.ToArray();
        outputs[map] = old with { Guard = layout.CaptureTexture(old.Guard.SourceSceneTextureOrdinal), UserEdited = true };
        entries[material] = entry with { Outputs = outputs };
        var rows = Profile.Materials.Select(row => RefreshGuards(row, layout));
        var profile = new D3D12MaterialBindingProfile(Profile.StableId, Profile.Name, layout.Fingerprint, rows, layout.Version, Profile.OutlineWidths);
        return new(frame, profile, manifest with { Materials = entries });
    }

    internal static D3D12MaterialBindingProfileRow RefreshGuards(D3D12MaterialBindingProfileRow row, D3D12MaterialBindingLayout layout)
    {
        D3D12MaterialBindingProfileTexture Guard(D3D12MaterialBindingProfileTexture map) => layout.CaptureTexture(map.SourceSceneTextureOrdinal);
        return row with
        {
            Face = row.Face is { } face ? face with
            {
                Primary = Guard(face.Primary),
                Secondary = face.Secondary is { } secondary ? Guard(secondary) : null
            } : null,
            GenshinHair = row.GenshinHair is { } hair ? hair with { Texture = Guard(hair.Texture) } : null,
            StarMcHair = row.StarMcHair is { } starMcHair ? starMcHair with
            { Highlight = Guard(starMcHair.Highlight), Shadow = Guard(starMcHair.Shadow) } : null,
            StarMcBody = row.StarMcBody is { } b ? b with { Normal = Guard(b.Normal), Detail = Guard(b.Detail), Mask = Guard(b.Mask), Matcap = Guard(b.Matcap) } : null,
            EndfieldHair = row.EndfieldHair is { } endfieldHair ? endfieldHair with { Texture = Guard(endfieldHair.Texture) } : null,
            EndfieldMatcap = row.EndfieldMatcap is { } e ? e with
            {
                Normal = Guard(e.Normal), P = Guard(e.P), Matcap = Guard(e.Matcap),
                Palette = e.Palette is { } palette ? palette with
                {
                    EmissionTexture = palette.EmissionTexture is { } emission ? Guard(emission) : null,
                    ReceivedLightTexture = palette.ReceivedLightTexture is { } received ? Guard(received) : null,
                    BaseImage = palette.BaseImage is { } image ? image with
                    {
                        BaseTexture = image.BaseTexture is { } baseImage ? Guard(baseImage) : null,
                        DetailTexture = image.DetailTexture is { } d ? Guard(d) : null,
                        ShadowTexture = image.ShadowTexture is { } s ? Guard(s) : null
                    } : null
                } : null
            } : null,
            GenshinHairAtlas = row.GenshinHairAtlas is { } a ? a with { Lightmap = Guard(a.Lightmap), Atlas = Guard(a.Atlas) } : null,
            GenshinMetallic = row.GenshinMetallic is { } g ? g with
            {
                Lightmap = Guard(g.Lightmap), Matcap = Guard(g.Matcap),
                Specular = g.Specular is { } specular ? specular with
                { Ramp = specular.Ramp is { } ramp ? Guard(ramp) : null } : null
            } : null
        };
    }
}

/// <summary>Four-family data adapters over existing shaders. All generated maps are original MDA
/// approximations with explicit settings, not recovered game textures or automatic material recognition.</summary>
public static class D3D12GeneratedStyleMaterialPreparation
{
    public const string GeneratorRevision = "mda-four-style-aux-v1";
    public const int MaxManifestBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 12
    };

    public static D3D12GeneratedStyleMaterialResult Prepare(OriginalFrameScene sourceRest,
        IEnumerable<D3D12GeneratedStyleMaterialSelection> selections, string pipelineId, string stableId, string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRest); ArgumentNullException.ThrowIfNull(selections);
        if (pipelineId is not ("genshin" or "star-rail" or "wuthering-waves" or "endfield"))
            throw new ArgumentException("Select one of the four modern styles explicitly.", nameof(pipelineId));
        var source = sourceRest.Surface.Snapshot;
        var originalLayout = D3D12MaterialBindingLayout.Capture(sourceRest);
        _ = new D3D12MaterialBindingProfile(stableId, name, originalLayout.Fingerprint, []);
        var selected = selections.Take(4097).ToArray();
        if (selected.Length is 0 or > 4096) throw new ArgumentException("Select 1..4096 materials.", nameof(selections));
        var seen = new HashSet<int>(); long bytes = 0; int count = source.Textures.Count;
        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(item); ArgumentNullException.ThrowIfNull(item.Options);
            item.Options.Validate();
            if ((uint)item.LocalMaterialIndex >= (uint)source.Materials.Count || !seen.Add(item.LocalMaterialIndex))
                throw new ArgumentException("Select existing distinct source-local material indices.", nameof(selections));
            int maps = pipelineId is "star-rail" or "wuthering-waves" ? 4 : pipelineId == "endfield" ? 3 : 2;
            count += maps; bytes += (long)item.Options.MapSize * item.Options.MapSize * 4 * maps;
            if (count > 4096 || bytes > D3D12GeneratedEndfieldMaterialPreparation.MaxGeneratedBytes)
                throw new ArgumentException("Generated materials exceed the texture/pixel budget.", nameof(selections));
            int diffuse = source.Materials[item.LocalMaterialIndex].DiffuseTexture;
            if (diffuse >= 0 && source.Textures[diffuse].Kind != SceneTextureKind.Image)
                throw new ArgumentException("Generation requires an Image diffuse input.", nameof(selections));
        }
        Array.Sort(selected, (a, b) => a.LocalMaterialIndex.CompareTo(b.LocalMaterialIndex));
        var textures = source.Textures.ToList(); var entries = new List<Entry>();
        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int diffuse = source.Materials[item.LocalMaterialIndex].DiffuseTexture;
            var input = diffuse < 0 ? new SceneTexture("white fallback", 1, 1, [255, 255, 255, 255]) : source.Textures[diffuse];
            var generated = D3D12GeneratedPmxMaterialMaps.Generate(input, item.Options, cancellationToken);
            var outputs = new List<Output>();
            void Add(string key, SceneTexture texture)
            {
                int slot = textures.Count; textures.Add(texture);
                outputs.Add(new(key, new(slot, texture.Width, texture.Height, texture.Kind, Digest(texture.Rgba)), false));
            }
            if (pipelineId == "endfield")
            { Add("normal", generated.Normal); Add("p", generated.P); Add("matcap", generated.Matcap); }
            else if (pipelineId is "star-rail" or "wuthering-waves")
            {
                Add("normal", generated.Normal);
                Add("detail", Image("detail-neutral-G", item.Options.MapSize, (_, _) => new(128, 128, 128, 255), cancellationToken));
                byte mask = generated.P.Rgba[0];
                Add("mask", Image("shadow-R-highlight-B", item.Options.MapSize, (_, _) => new(255, 128, mask, 255), cancellationToken));
                Add("matcap", generated.Matcap);
            }
            else
            {
                bool useAtlas = item.Options.Kind is D3D12GeneratedPmxSurfaceKind.Hair or D3D12GeneratedPmxSurfaceKind.Cloth;
                byte metal = item.Options.Kind is D3D12GeneratedPmxSurfaceKind.Metal or D3D12GeneratedPmxSurfaceKind.Coated ? (byte)255 : (byte)0;
                Add("lightmap", Image("Genshin-lightmap", item.Options.MapSize, (_, _) => new(metal, 128, useAtlas ? (byte)224 : (byte)128, 255), cancellationToken));
                if (!useAtlas) Add("matcap", generated.Matcap);
                else
                {
                    Vector3 shade = Vector3.Clamp(sourceRest.Surface.Materials[item.LocalMaterialIndex].ToonShadowColor, new(.25f), Vector3.One);
                    // Authored PMX shadow palette plus an original low-contrast atlas; no plugin pixels.
                    if (shade == Vector3.One) shade = new(.72f, .76f, .84f);
                    Add("atlas", Image("Genshin-generated-day-night-atlas", item.Options.MapSize, (x, y) =>
                    {
                        float t = Math.Clamp((x + .5f) / item.Options.MapSize, 0, 1); t = t * t * (3 - 2 * t);
                        var color = Vector3.Lerp(shade, Vector3.One, t);
                        if (y < item.Options.MapSize / 2) color *= new Vector3(.95f, .96f, 1);
                        return new(Byte(color.X), Byte(color.Y), Byte(color.Z), 255);
                    }, cancellationToken));
                }
            }
            entries.Add(new(item.LocalMaterialIndex, diffuse, generated.SourceRgbaSha256, input.Width, input.Height,
                item.Options, outputs.ToArray()));
        }
        var frame = D3D12GeneratedEndfieldMaterialPreparation.ReplaceTextures(sourceRest, textures);
        var layout = D3D12MaterialBindingLayout.Capture(frame);
        var rows = entries.Select(entry => CreateRow(frame, pipelineId, entry));
        var profile = new D3D12MaterialBindingProfile(stableId, name, layout.Fingerprint, rows, layout.Version);
        return new(frame, profile, new(2, GeneratorRevision, true, pipelineId, stableId, source.Textures.Count, entries.ToArray()));
    }

    private static D3D12MaterialBindingProfileRow CreateRow(OriginalFrameScene frame, string pipeline, Entry entry)
    {
        var source = frame.Surface.Snapshot;
        var eg = OriginalStyleMath.ResolveHostColors(source.Materials[entry.LocalMaterialIndex], frame.Surface.Profile).EgColor;
        Vector3 baseColor = Vector3.Clamp(new(eg.X, eg.Y, eg.Z), Vector3.Zero, Vector3.One);
        bool hair = entry.Options.Kind == D3D12GeneratedPmxSurfaceKind.Hair;
        var role = hair ? D3D12CharacterMaterialRole.Hair : D3D12CharacterMaterialRole.Body;
        D3D12MaterialBindingProfileTexture Map(string name) => entry.Outputs.Single(output => output.Name == name).Guard;
        if (pipeline is "star-rail" or "wuthering-waves")
            return new(entry.LocalMaterialIndex, role, StarMcBody: new(pipeline, Map("normal"), Map("detail"), Map("mask"), Map("matcap"),
                D3D12MatcapColorEncoding.LinearRgb, MetallicSaturation: pipeline == "star-rail" ? .9f : .8f,
                HighlightStrength: hair ? .22f : .16f, ShadowColorStrength: .2f, WarmBandStrength: pipeline == "star-rail" ? .13f : .04f,
                ShadowColor: Vector3.Clamp(frame.Surface.Materials[entry.LocalMaterialIndex].ToonShadowColor, new(.4f), Vector3.One),
                RimStrength: hair ? .04f : .025f, RimColor: new(.8f, .84f, .9f)));
        if (pipeline == "genshin")
        {
            if (entry.Options.Kind is D3D12GeneratedPmxSurfaceKind.Hair or D3D12GeneratedPmxSurfaceKind.Cloth)
                return new(entry.LocalMaterialIndex, role, GenshinHairAtlas: new(Map("lightmap"), Map("atlas"),
                D3D12GenshinAtlasColorEncoding.LinearRgb, 1, 0, .7f, .18f, 1, 1, 0, 0));
            bool metal = entry.Options.Kind is D3D12GeneratedPmxSurfaceKind.Metal or D3D12GeneratedPmxSurfaceKind.Coated;
            return new(entry.LocalMaterialIndex, role, GenshinMetallic: new(Map("lightmap"), Map("matcap"),
                D3D12MatcapColorEncoding.LinearRgb, new(.7f), Vector3.One, new(.85f), EnableMetallics: metal ? .3f : 0));
        }
        Vector3 light = Vector3.Normalize(source.LightDirection);
        return new(entry.LocalMaterialIndex, role, EndfieldMatcap: new(Map("normal"), Map("p"), Map("matcap"),
            D3D12MatcapColorEncoding.LinearRgb, 0, baseColor, Vector3.Zero, NormalStrength: 1,
            LightControlDirection: light, BaselineStrength: .22f, CoupleEndfieldPalette: true,
            Palette: new(Vector3.Zero, LightInfluence: .08f, ReceivedLightInputMode: D3D12EndfieldPaletteInputMode.RendererDiffuse),
            HighlightRange: new(.22f, .12f, new(light, .3f, 1))));
    }

    /// <summary>Strict byte-consistent restore, including generated suffix boundaries; not an authorship signature.</summary>
    public static D3D12GeneratedStyleMaterialResult Restore(OriginalFrameScene sourceRest,
        D3D12MaterialBindingProfile profile, ReadOnlySpan<byte> manifest)
    {
        ArgumentNullException.ThrowIfNull(sourceRest); ArgumentNullException.ThrowIfNull(profile);
        if (manifest.Length is 0 or > MaxManifestBytes) throw new InvalidDataException("Invalid manifest byte budget.");
        Manifest data;
        try { data = JsonSerializer.Deserialize<Manifest>(manifest, Json) ?? throw new InvalidDataException("Missing manifest."); }
        catch (JsonException error) { throw new InvalidDataException("Malformed generated material manifest.", error); }
        if (data.SchemaVersion != 2 || data.GeneratorRevision != GeneratorRevision || !data.IsGenerated || data.ProfileStableId != profile.StableId
            || data.PipelineId is not ("genshin" or "star-rail" or "wuthering-waves" or "endfield") || data.Materials is null
            || data.Materials.Length is 0 or > 4096 || data.Materials.Length != profile.Materials.Count)
            throw new InvalidDataException("Unsupported generated material identity or selection.");
        var source = sourceRest.Surface.Snapshot; var layout = D3D12MaterialBindingLayout.Capture(sourceRest);
        layout.ValidateFingerprint(profile.LayoutFingerprint, profile.LayoutVersion);
        if (data.FirstGeneratedOrdinal < 0 || data.FirstGeneratedOrdinal > source.Textures.Count || source.Textures.Count > 4096)
            throw new InvalidDataException("Invalid generated suffix.");
        for (int i = 0; i < source.Materials.Count; i++)
            if (source.Materials[i].DiffuseTexture >= data.FirstGeneratedOrdinal || source.Materials[i].ToonTexture >= data.FirstGeneratedOrdinal
                || sourceRest.Surface.Materials[i].SphereTexture >= data.FirstGeneratedOrdinal)
                throw new InvalidDataException("Generated slots overlap authored material inputs.");
        int next = data.FirstGeneratedOrdinal, previous = -1; long bytes = 0;
        var sourceHashes = new Dictionary<int, string>();
        foreach (var entry in data.Materials)
        {
            if (entry is null || entry.Options is null || entry.Outputs is null || entry.LocalMaterialIndex <= previous
                || (uint)entry.LocalMaterialIndex >= (uint)source.Materials.Count)
                throw new InvalidDataException("Invalid generated selection.");
            previous = entry.LocalMaterialIndex;
            try { entry.Options.Validate(); } catch (ArgumentException e) { throw new InvalidDataException("Invalid generator options.", e); }
            int diffuse = source.Materials[entry.LocalMaterialIndex].DiffuseTexture;
            if (diffuse != entry.SourceOrdinal || diffuse < -1 || diffuse >= data.FirstGeneratedOrdinal
                || entry.SourceWidth != (diffuse < 0 ? 1 : source.Textures[diffuse].Width)
                || entry.SourceHeight != (diffuse < 0 ? 1 : source.Textures[diffuse].Height)
                || diffuse >= 0 && source.Textures[diffuse].Kind != SceneTextureKind.Image)
                throw new InvalidDataException("Original diffuse input changed.");
            if (!sourceHashes.TryGetValue(diffuse, out string? hash))
            {
                hash = Convert.ToHexString(SHA256.HashData(diffuse < 0 ? new byte[] {255,255,255,255} : source.Textures[diffuse].Rgba));
                sourceHashes.Add(diffuse, hash);
            }
            if (hash != entry.SourceRgbaSha256) throw new InvalidDataException("Original diffuse pixels changed.");
            string[] expected = data.PipelineId switch
            {
                "endfield" => ["normal", "p", "matcap"],
                "genshin" when entry.Options.Kind is D3D12GeneratedPmxSurfaceKind.Hair or D3D12GeneratedPmxSurfaceKind.Cloth => ["lightmap", "atlas"],
                "genshin" => ["lightmap", "matcap"],
                _ => ["normal", "detail", "mask", "matcap"]
            };
            if (entry.Outputs.Length != expected.Length) throw new InvalidDataException("Wrong generated map set.");
            for (int i = 0; i < expected.Length; i++)
            {
                var output = entry.Outputs[i];
                if (output is null || output.Guard is null || output.Name != expected[i] || next >= source.Textures.Count
                    || output.Guard.SourceSceneTextureOrdinal != next || output.Guard.Width != entry.Options.MapSize
                    || output.Guard.Height != entry.Options.MapSize || output.Guard.Kind != SceneTextureKind.Image
                    || layout.CaptureTexture(next) != output.Guard)
                    throw new InvalidDataException("Generated output bytes or slots changed.");
                bytes += source.Textures[next++].Rgba.Length;
            }
            if (bytes > D3D12GeneratedEndfieldMaterialPreparation.MaxGeneratedBytes)
                throw new InvalidDataException("Generated pixel budget exceeded.");
            var row = profile.Materials.SingleOrDefault(value => value.LocalMaterialIndex == entry.LocalMaterialIndex)
                ?? throw new InvalidDataException("Missing profile material.");
            var expectedRow = CreateRow(sourceRest, data.PipelineId, entry);
            // Scalar parameters can be artist-edited; guards and the selected family must stay exact.
            if (row.Role != expectedRow.Role || !SameMaps(row, expectedRow))
                throw new InvalidDataException("Generated map/profile selections disagree.");
        }
        if (next != source.Textures.Count) throw new InvalidDataException("Unaccounted generated suffix.");
        _ = D3D12MaterialBindingProfileResolver.Resolve(profile, sourceRest, Guid.NewGuid(), "mmd");
        return new(sourceRest, profile, data);
    }

    private static bool SameMaps(D3D12MaterialBindingProfileRow a, D3D12MaterialBindingProfileRow b)
    {
        if (b.StarMcBody is { } s) return a.StarMcBody is { } x && x.PipelineId == s.PipelineId && x.Normal == s.Normal && x.Detail == s.Detail && x.Mask == s.Mask && x.Matcap == s.Matcap;
        if (b.EndfieldMatcap is { } e) return a.EndfieldMatcap is { } x && x.Normal == e.Normal && x.P == e.P && x.Matcap == e.Matcap;
        if (b.GenshinHairAtlas is { } h) return a.GenshinHairAtlas is { } x && x.Lightmap == h.Lightmap && x.Atlas == h.Atlas;
        return b.GenshinMetallic is { } g && a.GenshinMetallic is { } y && y.Lightmap == g.Lightmap && y.Matcap == g.Matcap;
    }
    internal static byte[] Encode(Manifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
    private static string Digest(ReadOnlySpan<byte> rgba) => Convert.ToHexString(SHA256.HashData(rgba)).ToLowerInvariant();
    private readonly record struct Pixel(byte R, byte G, byte B, byte A);
    private static byte Byte(float x) => (byte)MathF.Round(Math.Clamp(x, 0, 1) * 255);
    private static SceneTexture Image(string label, int size, Func<int, int, Pixel> pixel, CancellationToken token)
    {
        byte[] rgba = new byte[checked(size * size * 4)];
        for (int y = 0; y < size; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < size; x++)
            { int i = (y * size + x) * 4; var p = pixel(x, y); rgba[i] = p.R; rgba[i + 1] = p.G; rgba[i + 2] = p.B; rgba[i + 3] = p.A; }
        }
        return new("GENERATED/" + GeneratorRevision + "/" + label, size, size, rgba);
    }
    internal sealed record Manifest(int SchemaVersion, string GeneratorRevision, bool IsGenerated, string PipelineId,
        string ProfileStableId, int FirstGeneratedOrdinal, Entry[] Materials);
    internal sealed record Entry(int LocalMaterialIndex, int SourceOrdinal, string SourceRgbaSha256, int SourceWidth, int SourceHeight,
        D3D12GeneratedPmxMapOptions Options, Output[] Outputs);
    internal sealed record Output(string Name, D3D12MaterialBindingProfileTexture Guard, bool UserEdited);
}
