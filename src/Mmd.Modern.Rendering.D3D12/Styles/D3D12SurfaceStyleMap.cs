using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>
/// Renderer-owned immutable surface style and plugin draw map in the frame's material order.
/// "mmd" materials retain the baseline MMD shaders, root signature, and lighting pipeline unchanged.
/// </summary>
public sealed class D3D12SurfaceStyleMap
{
    public const string DefaultStyleId = "mmd";

    public IReadOnlyList<string> StyleIds { get; }
    public IReadOnlyList<D3D12CharacterMaterialStyle> CharacterMaterials { get; }
    public IReadOnlyDictionary<string, D3D12PluginStyleRegistration> RegisteredStyles { get; }
    public int Count => StyleIds.Count;
    public bool IsDefault { get; }

    /// <summary>Material roles supplied by the host view layer (manual tags first, guessed fallback).
    /// Null disables automatic per-role stylized shading derivation.</summary>
    public IReadOnlyList<D3D12CharacterMaterialRole>? StylizedRoles { get; init; }

    private SceneTexture?[][]? capturedPluginTextures;
    private string?[][]? capturedMikkFingerprints;
    private Vector3[]? capturedRawRadialSeeds;

    public D3D12SurfaceStyleMap(
        IEnumerable<string> styleIds,
        IEnumerable<D3D12CharacterMaterialStyle>? characterMaterials = null,
        IEnumerable<D3D12PluginStyleRegistration>? registeredStyles = null)
        : this(styleIds, characterMaterials, D3D12PluginStyleRegistration.BuildDictionary(registeredStyles))
    {
    }

    public D3D12SurfaceStyleMap(
        IEnumerable<string> styleIds,
        IEnumerable<D3D12CharacterMaterialStyle>? characterMaterials,
        D3D12StylePresetCatalog? catalog)
        : this(styleIds, characterMaterials, catalog?.Plugins)
    {
    }

    public D3D12SurfaceStyleMap(
        IEnumerable<string> styleIds,
        IEnumerable<D3D12CharacterMaterialStyle>? characterMaterials,
        IReadOnlyDictionary<string, D3D12PluginStyleRegistration>? registeredStyles)
    {
        ArgumentNullException.ThrowIfNull(styleIds);
        var ids = styleIds.ToArray();
        foreach (string id in ids)
            D3D12StylePresetDefinition.ValidateIdentifier(id, nameof(styleIds));
        StyleIds = Array.AsReadOnly(ids);

        CharacterMaterials = Array.AsReadOnly(characterMaterials?.Select(material =>
            material is { PmxMaterialTreatment: { } treatment }
                ? material with { PmxMaterialTreatment = treatment with { } }
                : material).ToArray()
            ?? Enumerable.Repeat(new D3D12CharacterMaterialStyle(), ids.Length).ToArray());

        RegisteredStyles = registeredStyles is null
            ? new ReadOnlyDictionary<string, D3D12PluginStyleRegistration>(new Dictionary<string, D3D12PluginStyleRegistration>(StringComparer.Ordinal))
            : new ReadOnlyDictionary<string, D3D12PluginStyleRegistration>(new Dictionary<string, D3D12PluginStyleRegistration>(registeredStyles, StringComparer.Ordinal));

        IsDefault = ids.All(static id => string.Equals(id, DefaultStyleId, StringComparison.Ordinal))
            && CharacterMaterials.All(static m => m.PluginDraw is null);
    }

    public bool IsDefaultStyle(int materialIndex)
        => string.Equals(StyleIds[materialIndex], DefaultStyleId, StringComparison.Ordinal);

    internal bool HasAnyPluginSpace2 => Enumerable.Range(0, Count).Any(i => !IsDefaultStyle(i));

    internal D3D12PluginDrawData? GetPluginDraw(int materialIndex)
        => IsDefaultStyle(materialIndex) ? null : CharacterMaterials[materialIndex].PluginDraw;

    internal D3D12PluginStyleRegistration? ResolvePlugin(int materialIndex)
        => IsDefaultStyle(materialIndex) ? null
            : RegisteredStyles.TryGetValue(StyleIds[materialIndex], out var reg) ? reg : null;

    internal D3D12PluginShaderModule? ResolveShaderModule(int materialIndex)
    {
        if (IsDefaultStyle(materialIndex)) return null;
        var reg = ResolvePlugin(materialIndex);
        if (CharacterMaterials[materialIndex].PluginDraw is { } draw)
        {
            if (reg?.ShaderModules is not null && reg.ShaderModules.TryGetValue(draw.ShaderModuleId, out var found))
                return found;
            throw new NotSupportedException($"Shader module '{draw.ShaderModuleId}' is not registered for style '{StyleIds[materialIndex]}'.");
        }
        if (reg?.ShaderModules is not null && reg.ShaderModules.TryGetValue("default", out var defaultModule))
            return defaultModule;
        return D3D12StyleShaderAssembler.DefaultStylizedHostModule;
    }

    internal D3D12StyleShaderHookMask EffectiveHookMask(int materialIndex)
        => ResolveShaderModule(materialIndex) is { } module
            ? D3D12StyleShaderAssembler.ResolveEffectiveHooks(module, CharacterMaterials[materialIndex].PluginDraw)
            : D3D12StyleShaderHookMask.None;

    internal D3D12StyleGeometryRequirements EffectiveGeometryRequirements(int materialIndex)
        => ResolveShaderModule(materialIndex) is { } module
            ? module.GeometryRequirements
            : D3D12StyleGeometryRequirements.None;

    internal bool HasGeometryRequirement(D3D12StyleGeometryRequirements flag)
        => Enumerable.Range(0, Count).Any(i => EffectiveGeometryRequirements(i).HasFlag(flag));

    public void Validate(OriginalFrameScene frame) => Validate(frame, null);

    public void Validate(OriginalFrameScene frame, RayGiOptions? rayGiOptions)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (CharacterMaterials.Count != Count || Count != frame.Materials.Count || Count != frame.Surface.Materials.Count
            || Count != frame.Surface.Snapshot.Materials.Count
            || (frame.Outline is not null && Count != frame.Outline.Materials.Count))
            throw new ArgumentException("One surface style entry per frame flag, surface binding and scene material is required.", nameof(frame));
        if (StylizedRoles is not null && StylizedRoles.Count != Count)
            throw new ArgumentException("StylizedRoles length must match material count.", nameof(StylizedRoles));

        foreach (var plugin in RegisteredStyles.Values)
            plugin.Validate();

        for (int i = 0; i < Count; i++)
        {
            var material = CharacterMaterials[i];
            if (material is null)
                throw new ArgumentException("Character material settings cannot contain null entries.", nameof(CharacterMaterials));
            material.Validate();

            if (IsDefaultStyle(i))
            {
                if (material.PluginDraw is not null)
                    throw new NotSupportedException("Built-in 'mmd' style does not accept plugin draw data.");
                continue;
            }

            if (frame.Options is null)
                throw new NotSupportedException("Non-MMD surface shader styles require explicit modern frame options.");

            if (!RegisteredStyles.ContainsKey(StyleIds[i]))
                throw new NotSupportedException($"Surface style '{StyleIds[i]}' is not registered.");

            var module = ResolveShaderModule(i)!;
            var geom = module.GeometryRequirements;
            var snapshot = frame.Surface.Snapshot;

            if (geom.HasFlag(D3D12StyleGeometryRequirements.SubTextureUv) && snapshot.AdditionalUvs is null)
                throw new ArgumentException("Plugin shader requires SubTextureUv (UV1 stream), which is absent from the snapshot.", nameof(frame));

            if (geom.HasFlag(D3D12StyleGeometryRequirements.RawRadialSeeds))
                _ = D3D12TangentCornerGeometry.GetOrComputeRawRadialSeeds(snapshot, out _);

            if (material.PluginDraw is { } draw)
            {
                if (draw.TreatmentOverride is { HairHighlightStrength: > 0 } && material.Role != D3D12CharacterMaterialRole.Hair)
                    throw new ArgumentException("Generic hair highlights require an explicit Hair material role.", nameof(CharacterMaterials));

                if (draw.TextureBindings is not null)
                {
                    foreach (var tex in draw.TextureBindings)
                    {
                        if (tex.SceneTextureIndex >= snapshot.Textures.Count)
                            throw new ArgumentOutOfRangeException(nameof(CharacterMaterials),
                                $"Plugin texture index {tex.SceneTextureIndex} for slot {tex.SlotIndex} is outside the actual frame texture table.");
                    }
                }

                if (draw.BufferBindings is not null)
                {
                    foreach (var buf in draw.BufferBindings)
                    {
                        if (buf.Kind == D3D12PluginBufferKind.MikkTangentCorners && buf.Data.IsEmpty)
                        {
                            int uvChannel = D3D12TangentCornerGeometry.ResolveUvChannel(buf);
                            _ = D3D12TangentCornerGeometry.Build(snapshot, i, uvChannel);
                        }
                    }
                }
            }

            if (EffectivePmxMaterialTreatment(i) is not null)
            {
                var effectiveToon = EffectiveToonResponse(i, frame.Materials[i].CharacterToonResponse)
                    ?? new CharacterToonResponse();
                CharacterToonMath.Validate(effectiveToon);
            }
        }
    }

    public D3D12PmxMaterialTreatment? EffectivePmxMaterialTreatment(int materialIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(materialIndex, Count);
        if (IsDefaultStyle(materialIndex)) return null;
        var material = CharacterMaterials[materialIndex];
        if (material.Role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair))
            return null;

        D3D12PmxMaterialTreatment? treatment = material.PluginDraw?.TreatmentOverride;
        if (treatment is null
            && (material.PluginDraw is null || material.PluginDraw.StylizedShadingOverride is not null)
            && EffectiveHookMask(materialIndex).HasFlag(D3D12StyleShaderHookMask.AllowStylizedShadingWrapper))
            treatment = material.PmxMaterialTreatment;

        return treatment is not null
            && (treatment.Saturation != 1 || treatment.TransitionWidthScale != 1
                || treatment.ValueContrast != 0 || treatment.ShadeGamma != 1 || treatment.HairHighlightStrength != 0)
            ? treatment
            : null;
    }

    public D3D12StylizedShading? EffectiveStylizedShading(int materialIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(materialIndex, Count);
        if (IsDefaultStyle(materialIndex)) return null;
        if (!EffectiveHookMask(materialIndex).HasFlag(D3D12StyleShaderHookMask.AllowStylizedShadingWrapper))
            return null;
        return ResolveStylizedShadingCandidate(materialIndex);
    }

    internal D3D12StylizedShading? EffectiveOutlineStylizedShading(int materialIndex)
    {
        if (EffectiveStylizedShading(materialIndex) is { } surface)
            return surface;
        if (IsDefaultStyle(materialIndex)) return null;
        if (!EffectiveHookMask(materialIndex).HasFlag(D3D12StyleShaderHookMask.RequiresOutlineColorScale))
            return null;
        return ResolveStylizedShadingCandidate(materialIndex);
    }

    private D3D12StylizedShading? ResolveStylizedShadingCandidate(int materialIndex)
    {
        var material = CharacterMaterials[materialIndex];
        if (material.PluginDraw?.StylizedShadingOverride is { } drawOverride)
            return drawOverride;
        if (material.StylizedShading is { } explicitShading)
            return explicitShading;
        if (StylizedRoles is { } roles && ResolvePlugin(materialIndex)?.DefaultStylizedShading is { } pluginDefault)
            return D3D12StylizedShadingCatalog.ResolveForRole(pluginDefault, roles[materialIndex]);
        return null;
    }

    internal CharacterToonResponse? EffectiveToonResponse(int materialIndex, CharacterToonResponse? response)
    {
        if (IsDefaultStyle(materialIndex)) return response;
        response ??= CharacterMaterials[materialIndex].ToonResponse;
        if (EffectivePmxMaterialTreatment(materialIndex) is not { } treatment) return response;
        response ??= new CharacterToonResponse();
        return response with { TransitionWidth = response.TransitionWidth * treatment.TransitionWidthScale };
    }

    public float EffectiveNormalShadeStrength(int materialIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(materialIndex, Count);
        if (IsDefaultStyle(materialIndex)) return 1;
        if (!EffectiveHookMask(materialIndex).HasFlag(D3D12StyleShaderHookMask.RequiresSkinShadeBlend))
            return 1;
        return CharacterMaterials[materialIndex].Role == D3D12CharacterMaterialRole.Face
            ? CharacterMaterials[materialIndex].FaceNormalShadeStrength
            : 1;
    }

    public float EffectiveGeometricShadowStrength(int materialIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(materialIndex, Count);
        if (IsDefaultStyle(materialIndex)) return 1;
        if (!EffectiveHookMask(materialIndex).HasFlag(D3D12StyleShaderHookMask.RequiresSkinShadeBlend))
            return 1;
        var material = CharacterMaterials[materialIndex];
        return material.Role switch
        {
            D3D12CharacterMaterialRole.Face => material.FaceGeometricShadowStrength,
            D3D12CharacterMaterialRole.Eye => material.EyeGeometricShadowStrength,
            _ => 1,
        };
    }

    internal bool EffectivePreserveDirectLightPalette(int materialIndex, bool frameFlag)
    {
        if (IsDefaultStyle(materialIndex)) return frameFlag;
        if (CharacterMaterials[materialIndex].PluginDraw?.PreserveDirectLightPalette is { } explicitValue)
            return explicitValue;
        if (EffectiveHookMask(materialIndex).HasFlag(D3D12StyleShaderHookMask.PreserveDirectLightPalette))
            return true;
        return CharacterMaterials[materialIndex].PreserveDirectLightPalette;
    }

    internal D3D12SurfaceStyleMap CaptureResolvedInputs(OriginalFrameScene frame)
    {
        var snapshot = frame.Surface.Snapshot;
        bool hasTextures = Enumerable.Range(0, Count).Any(i => GetPluginDraw(i)?.TextureBindings is { Count: > 0 });
        bool hasMikk = Enumerable.Range(0, Count).Any(i =>
            GetPluginDraw(i)?.BufferBindings?.Any(static b => b.Kind == D3D12PluginBufferKind.MikkTangentCorners && b.Data.IsEmpty) == true);
        bool hasRadial = HasGeometryRequirement(D3D12StyleGeometryRequirements.RawRadialSeeds);
        if (!hasTextures && !hasMikk && !hasRadial) return this;

        var captured = new D3D12SurfaceStyleMap(StyleIds, CharacterMaterials, RegisteredStyles)
        {
            StylizedRoles = StylizedRoles,
        };

        if (hasTextures)
        {
            captured.capturedPluginTextures = new SceneTexture?[Count][];
            for (int i = 0; i < Count; i++)
            {
                if (GetPluginDraw(i)?.TextureBindings is not { Count: > 0 } bindings) continue;
                var list = new SceneTexture?[bindings.Count];
                for (int j = 0; j < bindings.Count; j++)
                {
                    int idx = bindings[j].SceneTextureIndex;
                    if (idx >= 0) list[j] = snapshot.Textures[idx];
                }
                captured.capturedPluginTextures[i] = list;
            }
        }

        if (hasMikk)
        {
            captured.capturedMikkFingerprints = new string?[Count][];
            for (int i = 0; i < Count; i++)
            {
                if (GetPluginDraw(i)?.BufferBindings is not { Count: > 0 } buffers) continue;
                var list = new string?[buffers.Count];
                for (int j = 0; j < buffers.Count; j++)
                {
                    if (buffers[j].Kind == D3D12PluginBufferKind.MikkTangentCorners && buffers[j].Data.IsEmpty)
                    {
                        int uvChannel = D3D12TangentCornerGeometry.ResolveUvChannel(buffers[j]);
                        list[j] = D3D12TangentCornerGeometry.Build(snapshot, i, uvChannel).AppearanceFingerprint;
                    }
                }
                captured.capturedMikkFingerprints[i] = list;
            }
        }

        if (hasRadial)
            captured.capturedRawRadialSeeds = D3D12TangentCornerGeometry.GetOrComputeRawRadialSeeds(snapshot, out _).ToArray();

        return captured;
    }

    /// <summary>Null and all-"mmd" default maps select the same baseline. Other maps compare styles,
    /// shader modules, CBV bytes, texture/buffer bindings, and resolved inputs.</summary>
    public static bool Equivalent(D3D12SurfaceStyleMap? a, D3D12SurfaceStyleMap? b)
    {
        if (ReferenceEquals(a, b)) return true;
        bool aDefault = a is null || a.IsDefault, bDefault = b is null || b.IsDefault;
        if (aDefault || bDefault) return aDefault && bDefault;
        if (a!.Count != b!.Count) return false;
        if (!a.StyleIds.SequenceEqual(b.StyleIds, StringComparer.Ordinal)) return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (a.EffectiveStylizedShading(i) != b.EffectiveStylizedShading(i)
                || a.EffectiveOutlineStylizedShading(i) != b.EffectiveOutlineStylizedShading(i)
                || a.EffectiveNormalShadeStrength(i) != b.EffectiveNormalShadeStrength(i)
                || a.EffectiveGeometricShadowStrength(i) != b.EffectiveGeometricShadowStrength(i)
                || !D3D12PmxMaterialTreatment.ConsumedEquivalent(a.EffectivePmxMaterialTreatment(i), b.EffectivePmxMaterialTreatment(i))
                || a.EffectiveToonResponse(i, null) != b.EffectiveToonResponse(i, null)
                || a.ResolvePlugin(i)?.DefaultToonResponse != b.ResolvePlugin(i)?.DefaultToonResponse
                || a.CharacterMaterials[i].PreserveDirectLightPalette != b.CharacterMaterials[i].PreserveDirectLightPalette)
                return false;

            var modA = a.ResolveShaderModule(i);
            var modB = b.ResolveShaderModule(i);
            if (!ReferenceEquals(modA, modB))
            {
                if (modA is null || modB is null
                    || !string.Equals(modA.ModuleId, modB.ModuleId, StringComparison.Ordinal)
                    || !string.Equals(modA.EffectiveContentHash, modB.EffectiveContentHash, StringComparison.Ordinal))
                    return false;
            }

            if (a.EffectiveHookMask(i) != b.EffectiveHookMask(i)
                || a.EffectiveGeometryRequirements(i) != b.EffectiveGeometryRequirements(i))
                return false;

            if (!PluginDrawEquivalent(a, b, i))
                return false;
        }

        if (a.capturedRawRadialSeeds is not null || b.capturedRawRadialSeeds is not null)
        {
            if (a.capturedRawRadialSeeds is null || b.capturedRawRadialSeeds is null) return false;
            if (!a.capturedRawRadialSeeds.AsSpan().SequenceEqual(b.capturedRawRadialSeeds)) return false;
        }

        return true;
    }

    private static bool IsBareDefaultDraw(D3D12PluginDrawData? draw, D3D12PluginStyleRegistration? reg)
    {
        if (draw is null) return true;
        if (!string.Equals(draw.ShaderModuleId, "default", StringComparison.Ordinal))
            return false;
        if (!draw.ConstantBufferData.IsEmpty
            || (draw.TextureBindings is { Count: > 0 })
            || (draw.BufferBindings is { Count: > 0 })
            || draw.HookMaskOverride is not null
            || draw.StylizedShadingOverride is not null
            || draw.TreatmentOverride is not null
            || draw.ExtraSurfaceParams is not null
            || draw.PreserveDirectLightPalette is not null)
            return false;
        if (draw.DisableStylizedSphereMaps is { } explicitDisable)
        {
            var defaultModule = reg?.ShaderModules is not null && reg.ShaderModules.TryGetValue("default", out var m) ? m : null;
            if (defaultModule is null || explicitDisable != defaultModule.DisableStylizedSphereMaps)
                return false;
        }
        return true;
    }

    private static bool PluginDrawEquivalent(D3D12SurfaceStyleMap a, D3D12SurfaceStyleMap b, int index)
    {
        var drawA = a.GetPluginDraw(index);
        var drawB = b.GetPluginDraw(index);
        if (IsBareDefaultDraw(drawA, a.ResolvePlugin(index))) drawA = null;
        if (IsBareDefaultDraw(drawB, b.ResolvePlugin(index))) drawB = null;
        if (drawA is null || drawB is null) return ReferenceEquals(drawA, drawB);

        if (!ReferenceEquals(drawA, drawB))
        {
            if (!string.Equals(drawA.ShaderModuleId, drawB.ShaderModuleId, StringComparison.Ordinal)
                || drawA.HookMaskOverride != drawB.HookMaskOverride
                || drawA.DisableStylizedSphereMaps != drawB.DisableStylizedSphereMaps
                || drawA.ExtraSurfaceParams != drawB.ExtraSurfaceParams
                || drawA.PreserveDirectLightPalette != drawB.PreserveDirectLightPalette
                || !drawA.ConstantBufferData.Span.SequenceEqual(drawB.ConstantBufferData.Span))
                return false;
        }

        var texA = drawA.TextureBindings;
        var texB = drawB.TextureBindings;
        int texCountA = texA?.Count ?? 0, texCountB = texB?.Count ?? 0;
        if (texCountA != texCountB) return false;
        if (texCountA > 0)
        {
            var capTexA = a.capturedPluginTextures?[index];
            var capTexB = b.capturedPluginTextures?[index];
            bool compareIndices = capTexA is null || capTexB is null;
            for (int j = 0; j < texCountA; j++)
            {
                if (!D3D12PluginTextureBinding.Equivalent(texA![j], texB![j], compareIndices))
                    return false;
                if (!compareIndices && !SameTextureBytes(capTexA![j], capTexB![j]))
                    return false;
            }
        }

        var bufA = drawA.BufferBindings;
        var bufB = drawB.BufferBindings;
        int bufCountA = bufA?.Count ?? 0, bufCountB = bufB?.Count ?? 0;
        if (bufCountA != bufCountB) return false;
        if (bufCountA > 0)
        {
            var capMikkA = a.capturedMikkFingerprints?[index];
            var capMikkB = b.capturedMikkFingerprints?[index];
            for (int j = 0; j < bufCountA; j++)
            {
                if (!D3D12PluginBufferBinding.Equivalent(bufA![j], bufB![j]))
                    return false;
                if (!string.Equals(capMikkA?[j], capMikkB?[j], StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    private static bool SameTextureBytes(SceneTexture? left, SceneTexture? right)
        => ReferenceEquals(left, right)
            || (left is not null && right is not null
                && left.Width == right.Width && left.Height == right.Height
                && left.Rgba.SequenceEqual(right.Rgba));
}
