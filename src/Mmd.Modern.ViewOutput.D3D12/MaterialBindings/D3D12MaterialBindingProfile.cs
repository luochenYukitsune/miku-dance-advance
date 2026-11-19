using System.Collections.ObjectModel;
using System.Text;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>Explicit source-scene texture ordinal and immutable content guard. It is neither a PMX
/// texture ordinal nor a composed-frame slot; digest equality never selects a different slot.</summary>
public sealed record D3D12MaterialBindingProfileTexture(int SourceSceneTextureOrdinal, int Width, int Height,
    SceneTextureKind Kind, string RgbaSha256)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(SourceSceneTextureOrdinal);
        if (Width is < 1 or > 4096 || Height is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(Width), "Texture dimensions must be in [1,4096].");
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        D3D12MaterialBindingProfile.ValidateDigest(RgbaSha256, nameof(RgbaSha256));
    }
}

/// <summary>
/// One explicit source-local material choice. Plugin bindings stay dormant outside their selected
/// pipeline; <see cref="TextureGuards"/> guard source-local texture slots across save/load round-trips.
/// </summary>
public sealed record D3D12MaterialBindingProfileRow : IEquatable<D3D12MaterialBindingProfileRow>
{
    private readonly IReadOnlyList<StyleMaterialBindingData> pluginBindings = Array.Empty<StyleMaterialBindingData>();
    private readonly IReadOnlyList<D3D12MaterialBindingProfileTexture> textureGuards = Array.Empty<D3D12MaterialBindingProfileTexture>();

    public int LocalMaterialIndex { get; init; }
    public D3D12CharacterMaterialRole Role { get; init; }
    public bool ExplicitRole { get; init; }
    public float FaceNormalShadeStrength { get; init; }
    public float FaceGeometricShadowStrength { get; init; }
    public float EyeGeometricShadowStrength { get; init; }
    public D3D12PmxMaterialTreatment? PmxMaterialTreatment { get; init; }

    public IReadOnlyList<StyleMaterialBindingData> PluginBindings
    {
        get => pluginBindings;
        init => pluginBindings = DetachPluginBindings(value);
    }

    public IReadOnlyList<D3D12MaterialBindingProfileTexture> TextureGuards
    {
        get => textureGuards;
        init => textureGuards = DetachTextureGuards(value);
    }

    public D3D12MaterialBindingProfileRow(
        int localMaterialIndex,
        D3D12CharacterMaterialRole role = D3D12CharacterMaterialRole.Body,
        float faceNormalShadeStrength = .35f,
        float faceGeometricShadowStrength = 1f,
        float eyeGeometricShadowStrength = .2f,
        D3D12PmxMaterialTreatment? pmxMaterialTreatment = null,
        IEnumerable<StyleMaterialBindingData>? pluginBindings = null,
        IEnumerable<D3D12MaterialBindingProfileTexture>? textureGuards = null,
        bool explicitRole = true)
    {
        LocalMaterialIndex = localMaterialIndex;
        Role = role;
        ExplicitRole = explicitRole;
        FaceNormalShadeStrength = faceNormalShadeStrength;
        FaceGeometricShadowStrength = faceGeometricShadowStrength;
        EyeGeometricShadowStrength = eyeGeometricShadowStrength;
        PmxMaterialTreatment = pmxMaterialTreatment;
        PluginBindings = DetachPluginBindings(pluginBindings);
        TextureGuards = DetachTextureGuards(textureGuards);
    }

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(LocalMaterialIndex);
        if (!Enum.IsDefined(Role))
            throw new ArgumentOutOfRangeException(nameof(Role), "Unknown character material role.");
        if (!float.IsFinite(FaceNormalShadeStrength) || FaceNormalShadeStrength is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(FaceNormalShadeStrength), "Face normal shade strength must be finite and in [0,1].");
        if (!float.IsFinite(FaceGeometricShadowStrength) || FaceGeometricShadowStrength is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(FaceGeometricShadowStrength), "Face geometric shadow strength must be finite and in [0,1].");
        if (!float.IsFinite(EyeGeometricShadowStrength) || EyeGeometricShadowStrength is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(EyeGeometricShadowStrength), "Eye geometric shadow strength must be finite and in [0,1].");

        PmxMaterialTreatment?.Validate();
        if (ExplicitRole)
        {
            if (PmxMaterialTreatment is { HairHighlightStrength: > 0f } && Role != D3D12CharacterMaterialRole.Hair)
                throw new ArgumentException("Generic hair highlights require an explicit Hair material role.", nameof(PmxMaterialTreatment));
            if (PmxMaterialTreatment is not null && Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
                throw new ArgumentException("Ordinary-PMX material treatment requires explicitly selected Body or Hair.", nameof(PmxMaterialTreatment));
        }

        foreach (var binding in PluginBindings)
            _ = D3D12StylePluginAdapter.DetachBindingData(binding);

        var guardsBySlot = new Dictionary<int, D3D12MaterialBindingProfileTexture>();
        foreach (var guard in TextureGuards)
        {
            guard.Validate();
            if (guardsBySlot.TryGetValue(guard.SourceSceneTextureOrdinal, out var existing) && existing != guard)
                throw new ArgumentException($"Conflicting texture guards for source texture ordinal {guard.SourceSceneTextureOrdinal}.", nameof(TextureGuards));
            guardsBySlot[guard.SourceSceneTextureOrdinal] = guard;
        }
    }

    public bool Equals(D3D12MaterialBindingProfileRow? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        if (LocalMaterialIndex != other.LocalMaterialIndex
            || Role != other.Role
            || ExplicitRole != other.ExplicitRole
            || FaceNormalShadeStrength != other.FaceNormalShadeStrength
            || FaceGeometricShadowStrength != other.FaceGeometricShadowStrength
            || EyeGeometricShadowStrength != other.EyeGeometricShadowStrength
            || !Equals(PmxMaterialTreatment, other.PmxMaterialTreatment)
            || PluginBindings.Count != other.PluginBindings.Count
            || TextureGuards.Count != other.TextureGuards.Count)
            return false;

        for (int i = 0; i < PluginBindings.Count; i++)
        {
            if (!D3D12StylePluginAdapter.BindingDataEquivalent(PluginBindings[i], other.PluginBindings[i]))
                return false;
        }

        for (int i = 0; i < TextureGuards.Count; i++)
        {
            if (!Equals(TextureGuards[i], other.TextureGuards[i]))
                return false;
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(LocalMaterialIndex);
        hash.Add(Role);
        hash.Add(ExplicitRole);
        hash.Add(FaceNormalShadeStrength);
        hash.Add(FaceGeometricShadowStrength);
        hash.Add(EyeGeometricShadowStrength);
        hash.Add(PmxMaterialTreatment);
        hash.Add(PluginBindings.Count);
        foreach (var b in PluginBindings)
        {
            hash.Add(b.StyleId, StringComparer.Ordinal);
            hash.Add(b.ModeId, StringComparer.Ordinal);
        }
        hash.Add(TextureGuards.Count);
        foreach (var g in TextureGuards)
            hash.Add(g);
        return hash.ToHashCode();
    }

    private static IReadOnlyList<StyleMaterialBindingData> DetachPluginBindings(IEnumerable<StyleMaterialBindingData>? bindings)
    {
        if (bindings is null) return Array.Empty<StyleMaterialBindingData>();
        var list = bindings.Select(static b =>
        {
            ArgumentNullException.ThrowIfNull(b);
            return D3D12StylePluginAdapter.DetachBindingData(b);
        }).ToArray();
        return list.Length == 0 ? Array.Empty<StyleMaterialBindingData>() : Array.AsReadOnly(list);
    }

    private static IReadOnlyList<D3D12MaterialBindingProfileTexture> DetachTextureGuards(IEnumerable<D3D12MaterialBindingProfileTexture>? guards)
    {
        if (guards is null) return Array.Empty<D3D12MaterialBindingProfileTexture>();
        var list = guards.Select(static g =>
        {
            ArgumentNullException.ThrowIfNull(g);
            g.Validate();
            return g;
        }).ToArray();
        return list.Length == 0 ? Array.Empty<D3D12MaterialBindingProfileTexture>() : Array.AsReadOnly(list);
    }
}

/// <summary>Independent source-local outline multiplier. It does not classify the material or
/// replace its current rendering style's effective outline width.</summary>
public sealed record D3D12MaterialBindingProfileOutline(int LocalMaterialIndex, float Scale = 1f)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(LocalMaterialIndex);
        if (!float.IsFinite(Scale) || Scale is < 0f or > 4f)
            throw new ArgumentOutOfRangeException(nameof(Scale), "Outline scale must be finite and in [0,4].");
    }
}

/// <summary>Detached renderer-owned model mapping, independent of GPU objects and evaluated animation.
/// The codec can be used by a portable project or a local library. It contains no paths, image bytes,
/// object identity or frame-global slots. Rest-seed payloads use a separate bounded PMA resource.</summary>
public sealed class D3D12MaterialBindingProfile
{
    public const int CurrentSchemaVersion = 14;
    public const int MaxMaterials = 4096;
    public string StableId { get; }
    public string Name { get; }
    public int SchemaVersion => CurrentSchemaVersion;
    public int LayoutVersion { get; }
    public string LayoutFingerprint { get; }
    public string? PipelineId { get; }
    public bool GuessMaterialRoles { get; }
    public IReadOnlyList<D3D12MaterialBindingProfileRow> Materials { get; }
    public IReadOnlyList<D3D12MaterialBindingProfileOutline> OutlineWidths { get; }

    public D3D12MaterialBindingProfile(
        string stableId,
        string name,
        string layoutFingerprint,
        IEnumerable<D3D12MaterialBindingProfileRow> materials,
        int layoutVersion = 1,
        IEnumerable<D3D12MaterialBindingProfileOutline>? outlineWidths = null,
        string? pipelineId = null,
        bool guessMaterialRoles = true)
    {
        ValidateIdentifier(stableId);
        if (pipelineId is not null)
            ValidateIdentifier(pipelineId);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
            throw new ArgumentException("A profile name must contain 1 to 128 characters without control characters.", nameof(name));
        try { _ = new UTF8Encoding(false, true).GetByteCount(name); }
        catch (EncoderFallbackException error) { throw new ArgumentException("A profile name must be valid Unicode.", nameof(name), error); }
        ValidateDigest(layoutFingerprint, nameof(layoutFingerprint));
        if (layoutVersion != 1) throw new NotSupportedException("Only material layout version 1 is supported.");
        ArgumentNullException.ThrowIfNull(materials);
        var rows = materials.Take(MaxMaterials + 1).ToArray();
        if (rows.Length > MaxMaterials) throw new ArgumentException("A profile exceeds the 4096-material budget.", nameof(materials));
        var selected = new HashSet<int>();
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            row.Validate();
            if (!selected.Add(row.LocalMaterialIndex))
                throw new ArgumentException("A source-local material may be mapped only once in one profile.", nameof(materials));
        }
        var outlines = outlineWidths?.Take(MaxMaterials + 1).ToArray() ?? [];
        if (outlines.Length > MaxMaterials)
            throw new ArgumentException("A profile exceeds the 4096-outline budget.", nameof(outlineWidths));
        selected.Clear();
        foreach (var outline in outlines)
        {
            ArgumentNullException.ThrowIfNull(outline);
            outline.Validate();
            if (!selected.Add(outline.LocalMaterialIndex))
                throw new ArgumentException("A source-local material may have only one outline multiplier in one profile.", nameof(outlineWidths));
        }
        StableId = stableId;
        Name = name;
        LayoutFingerprint = layoutFingerprint;
        LayoutVersion = layoutVersion;
        PipelineId = pipelineId;
        GuessMaterialRoles = guessMaterialRoles;
        Materials = new ReadOnlyCollection<D3D12MaterialBindingProfileRow>(rows);
        OutlineWidths = new ReadOnlyCollection<D3D12MaterialBindingProfileOutline>(outlines);
    }

    internal static void ValidateIdentifier(string id)
    {
        if (id is null || id.Length is < 1 or > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("A profile ID must be 1 to 128 ASCII letters, digits, dots, underscores or hyphens.", nameof(id));
    }

    internal static void ValidateDigest(string value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A fingerprint must be a canonical lowercase SHA256 hex digest.", parameter);
    }
}
