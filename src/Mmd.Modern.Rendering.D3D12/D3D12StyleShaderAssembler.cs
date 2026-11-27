using System.Collections.Concurrent;
using System.Text;
using Mmd.Modern.Core;
using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>
/// Composes the host MMD/GI/Native/Stylized/PMX-Treatment HLSL contract with plugin shader modules
/// using universal space2 registers and structured preprocessor-guarded hook functions.
/// Baseline "mmd" rendering continues to use <see cref="OriginalFrameShaders"/> directly.
/// </summary>
internal static class D3D12StyleShaderAssembler
{
    private const ShaderFlags CompileFlags = ShaderFlags.EnableStrictness | ShaderFlags.IeeeStrictness;

    private readonly record struct VertexCacheKey(
        D3D12StyleGeometryRequirements Geometry,
        bool NativeLights,
        bool Gi,
        bool SubTexture);

    private readonly record struct PixelCacheKey(
        string StyleId,
        string ModuleId,
        string ContentHash,
        D3D12StyleShaderHookMask EffectiveHooks,
        D3D12StyleGeometryRequirements EffectiveGeometry,
        string EntryPoint,
        bool NativeLights,
        bool Gi,
        bool TextureMorph,
        bool SubTexture,
        bool CharacterToon,
        bool PreserveDirectLightPalette);

    private static readonly ConcurrentDictionary<VertexCacheKey, byte[]> VertexCache = new();
    private static readonly ConcurrentDictionary<PixelCacheKey, byte[]> PixelCache = new();

    internal static D3D12PluginShaderModule DefaultStylizedHostModule { get; } = new(
        ModuleId: "__host_stylized",
        HlslSource: null,
        Hooks: D3D12StyleShaderHookMask.AllowStylizedShadingWrapper
            | D3D12StyleShaderHookMask.DisableStylizedSphereMaps
            | D3D12StyleShaderHookMask.RequiresOutlineColorScale
            | D3D12StyleShaderHookMask.RequiresSkinShadeBlend,
        GeometryRequirements: D3D12StyleGeometryRequirements.None);

    internal static D3D12StyleShaderHookMask ResolveEffectiveHooks(
        D3D12PluginShaderModule module,
        D3D12PluginDrawData? drawData)
    {
        var hooks = drawData?.HookMaskOverride ?? module.EffectiveHooks;
        if (!string.IsNullOrWhiteSpace(module.HlslSource))
            hooks |= D3D12StyleShaderHookMask.HasStyleShaderCode;
        if (drawData?.DisableStylizedSphereMaps is { } disableSphere)
        {
            if (disableSphere) hooks |= D3D12StyleShaderHookMask.DisableStylizedSphereMaps;
            else hooks &= ~D3D12StyleShaderHookMask.DisableStylizedSphereMaps;
        }
        else if (module.DisableStylizedSphereMaps && drawData?.HookMaskOverride is null)
        {
            hooks |= D3D12StyleShaderHookMask.DisableStylizedSphereMaps;
        }
        if (drawData?.PreserveDirectLightPalette == true)
            hooks |= D3D12StyleShaderHookMask.PreserveDirectLightPalette;
        else if (drawData?.PreserveDirectLightPalette == false)
            hooks &= ~D3D12StyleShaderHookMask.PreserveDirectLightPalette;
        return hooks;
    }

    internal static D3D12StyleGeometryRequirements ResolveEffectiveGeometry(
        D3D12PluginShaderModule module,
        bool frameSubTexture)
    {
        var geom = module.GeometryRequirements;
        if (frameSubTexture)
            geom |= D3D12StyleGeometryRequirements.SubTextureUv;
        return geom;
    }

    internal static byte[] CompileVertex(
        D3D12PluginShaderModule module,
        D3D12StyleGeometryRequirements effectiveGeometry,
        bool nativeLights,
        bool gi,
        bool subTexture)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (subTexture) effectiveGeometry |= D3D12StyleGeometryRequirements.SubTextureUv;
        var key = new VertexCacheKey(
            effectiveGeometry & (D3D12StyleGeometryRequirements.SubTextureUv
                | D3D12StyleGeometryRequirements.RawRadialSeeds
                | D3D12StyleGeometryRequirements.ModelSpacePosition
                | D3D12StyleGeometryRequirements.ModelSpaceNormal),
            nativeLights,
            gi,
            subTexture);
        return VertexCache.GetOrAdd(key, static k =>
        {
            string source = AssembleVertexSource(k.Geometry, k.NativeLights, k.Gi, k.SubTexture);
            return D3D12FxcShaderCache.Compile(source, "VSSurface", "PluginStyleVertex.hlsl", "vs_5_1", CompileFlags);
        });
    }

    internal static byte[] CompilePixel(
        string styleId,
        string entryPoint,
        D3D12PluginShaderModule module,
        D3D12StyleShaderHookMask effectiveHooks,
        D3D12StyleGeometryRequirements effectiveGeometry,
        bool nativeLights,
        bool gi,
        bool textureMorph,
        bool subTexture,
        bool characterToon,
        bool preserveDirectLightPalette)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (entryPoint is not ("PSSurface" or "PSSurfaceGi" or "PSSurfaceNative" or "PSSurfaceGiNative"))
            throw new ArgumentOutOfRangeException(nameof(entryPoint));
        if (subTexture)
        {
            effectiveGeometry |= D3D12StyleGeometryRequirements.SubTextureUv;
            textureMorph = true;
        }
        bool directPalette = nativeLights && (preserveDirectLightPalette || effectiveHooks.HasFlag(D3D12StyleShaderHookMask.PreserveDirectLightPalette));
        var key = new PixelCacheKey(
            styleId,
            module.ModuleId,
            module.EffectiveContentHash,
            effectiveHooks,
            effectiveGeometry,
            entryPoint,
            nativeLights,
            gi,
            textureMorph,
            subTexture,
            characterToon,
            directPalette);

        return PixelCache.GetOrAdd(key, k =>
        {
            string source = AssemblePixelSource(
                k.EntryPoint,
                module,
                k.EffectiveHooks,
                k.EffectiveGeometry,
                k.NativeLights,
                k.Gi,
                k.TextureMorph,
                k.SubTexture,
                k.CharacterToon,
                k.PreserveDirectLightPalette);
            string fileName = $"PluginStyle_{SanitizeFileToken(k.StyleId)}_{SanitizeFileToken(k.ModuleId)}.hlsl";
            return D3D12FxcShaderCache.Compile(source, k.EntryPoint, fileName, "ps_5_1", CompileFlags);
        });
    }

    private static string SanitizeFileToken(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.Length == 0 ? "style" : sb.ToString();
    }

    internal static string AssembleVertexSource(
        D3D12StyleGeometryRequirements geometry,
        bool nativeLights,
        bool gi,
        bool subTexture)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("#define MDA_STYLE_TARGET_VS 1");
        AppendGeometryDefines(sb, geometry, gi);
        if (subTexture) sb.AppendLine("#define MDA_HAS_SUB_TEXTURE 1");
        if (nativeLights) sb.AppendLine("#define MDA_HAS_NATIVE_LIGHTS 1");
        if (gi) sb.AppendLine("#define MDA_ENTRY_GI 1");
        AppendCommonHeader(sb);
        AppendVertexDefinitions(sb);
        return PruneInactivePreprocessorBranches(sb.ToString());
    }

    internal static string AssemblePixelSource(
        string entryPoint,
        D3D12PluginShaderModule module,
        D3D12StyleShaderHookMask hooks,
        D3D12StyleGeometryRequirements geometry,
        bool nativeLights,
        bool gi,
        bool textureMorph,
        bool subTexture,
        bool characterToon,
        bool preserveDirectLightPalette)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("#define MDA_STYLE_TARGET_PS 1");
        sb.AppendLine("#define MDA_STYLE_ACTIVE 1");
        if (gi) sb.AppendLine("#define MDA_ENTRY_GI 1");
        if (gi || nativeLights) sb.AppendLine("#define MDA_HAS_GI_RESOURCES 1");
        if (nativeLights) sb.AppendLine("#define MDA_HAS_NATIVE_LIGHTS 1");
        if (textureMorph || subTexture) sb.AppendLine("#define MDA_HAS_TEXTURE_MORPH 1");
        if (subTexture) sb.AppendLine("#define MDA_HAS_SUB_TEXTURE 1");
        if (characterToon) sb.AppendLine("#define MDA_HAS_CHARACTER_TOON 1");
        if (preserveDirectLightPalette && nativeLights) sb.AppendLine("#define MDA_PRESERVE_DIRECT_LIGHT_PALETTE 1");

        AppendHookDefines(sb, hooks);
        AppendGeometryDefines(sb, geometry, gi);

        if (module.Defines is not null)
        {
            foreach (var (macro, val) in module.Defines.OrderBy(static p => p.Key, StringComparer.Ordinal))
                sb.Append("#define ").Append(macro).Append(' ').AppendLine(val);
        }

        AppendCommonHeader(sb);
        AppendGiAndNativeHeaders(sb, gi || nativeLights, nativeLights);
        AppendTextureMorphHeader(sb, textureMorph || subTexture);
        AppendVertexDefinitions(sb);
        AppendPixelHostPrePlugin(sb);

        if (!string.IsNullOrWhiteSpace(module.HlslSource))
        {
            sb.AppendLine("// --- Begin Plugin Shader Module: " + module.ModuleId + " ---");
            sb.AppendLine(module.HlslSource.ReplaceLineEndings("\n"));
            sb.AppendLine("// --- End Plugin Shader Module ---");
        }

        AppendPixelHostPostPlugin(sb, entryPoint, hooks, geometry, gi, nativeLights, preserveDirectLightPalette);
        return PruneInactivePreprocessorBranches(sb.ToString());
    }

    private static string PruneInactivePreprocessorBranches(string source)
    {
        var defined = new Dictionary<string, string>(StringComparer.Ordinal);
        var stack = new Stack<(bool ParentActive, bool BranchActive, bool AnyTaken)>();
        var sb = new StringBuilder(source.Length);
        using var reader = new StringReader(source);
        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                if (TryMatchDirective(trimmed, "ifdef", out string ifdefArg))
                {
                    bool parent = stack.Count == 0 || (stack.Peek().ParentActive && stack.Peek().BranchActive);
                    string sym = ExtractFirstIdentifier(ifdefArg);
                    bool cond = sym.Length > 0 && defined.ContainsKey(sym);
                    stack.Push((parent, parent && cond, cond));
                    continue;
                }
                if (TryMatchDirective(trimmed, "ifndef", out string ifndefArg))
                {
                    bool parent = stack.Count == 0 || (stack.Peek().ParentActive && stack.Peek().BranchActive);
                    string sym = ExtractFirstIdentifier(ifndefArg);
                    bool cond = sym.Length > 0 && !defined.ContainsKey(sym);
                    stack.Push((parent, parent && cond, cond));
                    continue;
                }
                if (TryMatchDirective(trimmed, "if", out string ifExpr))
                {
                    bool parent = stack.Count == 0 || (stack.Peek().ParentActive && stack.Peek().BranchActive);
                    bool cond = parent && EvaluateIfExpression(StripTrailingLineComment(ifExpr), defined);
                    stack.Push((parent, parent && cond, cond));
                    continue;
                }
                if (TryMatchDirective(trimmed, "elif", out string elifExpr))
                {
                    if (stack.Count > 0)
                    {
                        var (parent, _, anyTaken) = stack.Pop();
                        bool cond = !anyTaken && parent && EvaluateIfExpression(StripTrailingLineComment(elifExpr), defined);
                        stack.Push((parent, cond, anyTaken || cond));
                    }
                    continue;
                }
                if (IsBareDirective(trimmed, "else"))
                {
                    if (stack.Count > 0)
                    {
                        var (parent, _, anyTaken) = stack.Pop();
                        stack.Push((parent, parent && !anyTaken, true));
                    }
                    continue;
                }
                if (IsBareDirective(trimmed, "endif"))
                {
                    if (stack.Count > 0)
                        stack.Pop();
                    continue;
                }
            }

            bool active = stack.Count == 0 || (stack.Peek().ParentActive && stack.Peek().BranchActive);
            if (!active)
                continue;

            if (trimmed.StartsWith('#'))
            {
                if (TryMatchDirective(trimmed, "define", out string defArg))
                {
                    string sym = ExtractFirstIdentifier(defArg);
                    if (sym.Length > 0 && !defArg[sym.Length..].TrimStart().StartsWith('('))
                    {
                        string val = StripTrailingLineComment(defArg[sym.Length..]).Trim();
                        defined[sym] = val.Length == 0 ? "1" : val;
                    }
                }
                else if (TryMatchDirective(trimmed, "undef", out string undefArg))
                {
                    string sym = ExtractFirstIdentifier(undefArg);
                    if (sym.Length > 0)
                        defined.Remove(sym);
                }
            }

            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static bool TryMatchDirective(string trimmedLine, string keyword, out string argument)
    {
        argument = "";
        if (!trimmedLine.StartsWith('#'))
            return false;
        var rest = trimmedLine.AsSpan(1).TrimStart();
        if (!rest.StartsWith(keyword, StringComparison.Ordinal))
            return false;
        var tail = rest[keyword.Length..];
        if (tail.Length > 0 && !char.IsWhiteSpace(tail[0]) && tail[0] != '(' && tail[0] != '!')
            return false;
        argument = tail.TrimStart().ToString();
        return true;
    }

    private static bool IsBareDirective(string trimmedLine, string keyword)
    {
        if (!trimmedLine.StartsWith('#'))
            return false;
        var rest = trimmedLine.AsSpan(1).TrimStart();
        if (!rest.StartsWith(keyword, StringComparison.Ordinal))
            return false;
        var tail = StripTrailingLineComment(rest[keyword.Length..].ToString()).Trim();
        return tail.Length == 0;
    }

    private static string ExtractFirstIdentifier(string text)
    {
        int i = 0;
        while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
            i++;
        return text[..i];
    }

    private static string StripTrailingLineComment(string text)
    {
        int idx = text.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? text[..idx] : text;
    }

    private static bool EvaluateIfExpression(string expr, IReadOnlyDictionary<string, string> defined)
    {
        int pos = 0;

        void SkipWs()
        {
            while (pos < expr.Length && char.IsWhiteSpace(expr[pos]))
                pos++;
        }

        bool Match(string token)
        {
            SkipWs();
            if (!expr.AsSpan(pos).StartsWith(token, StringComparison.Ordinal))
                return false;
            pos += token.Length;
            return true;
        }

        long ParseOr()
        {
            long left = ParseAnd();
            while (Match("||"))
            {
                long right = ParseAnd();
                left = (left != 0 || right != 0) ? 1 : 0;
            }
            return left;
        }

        long ParseAnd()
        {
            long left = ParseEquality();
            while (Match("&&"))
            {
                long right = ParseEquality();
                left = (left != 0 && right != 0) ? 1 : 0;
            }
            return left;
        }

        long ParseEquality()
        {
            long left = ParseUnary();
            while (true)
            {
                if (Match("=="))
                    left = left == ParseUnary() ? 1 : 0;
                else if (Match("!="))
                    left = left != ParseUnary() ? 1 : 0;
                else
                    break;
            }
            return left;
        }

        long ParseUnary()
        {
            SkipWs();
            if (pos < expr.Length && expr[pos] == '!')
            {
                if (pos + 1 < expr.Length && expr[pos + 1] == '=')
                    return ParsePrimary();
                pos++;
                return ParseUnary() == 0 ? 1 : 0;
            }
            return ParsePrimary();
        }

        long ParsePrimary()
        {
            SkipWs();
            if (pos >= expr.Length)
                return 0;
            if (expr[pos] == '(')
            {
                pos++;
                long inner = ParseOr();
                Match(")");
                return inner;
            }
            if (char.IsAsciiDigit(expr[pos]))
            {
                int start = pos;
                while (pos < expr.Length && char.IsAsciiDigit(expr[pos]))
                    pos++;
                return long.TryParse(expr.AsSpan(start, pos - start), out long num) ? num : 0;
            }
            if (char.IsAsciiLetter(expr[pos]) || expr[pos] == '_')
            {
                int start = pos;
                while (pos < expr.Length && (char.IsAsciiLetterOrDigit(expr[pos]) || expr[pos] == '_'))
                    pos++;
                string ident = expr[start..pos];
                if (ident == "defined")
                {
                    SkipWs();
                    bool hasParen = Match("(");
                    SkipWs();
                    int s = pos;
                    while (pos < expr.Length && (char.IsAsciiLetterOrDigit(expr[pos]) || expr[pos] == '_'))
                        pos++;
                    string target = expr[s..pos];
                    if (hasParen)
                        Match(")");
                    return target.Length > 0 && defined.ContainsKey(target) ? 1 : 0;
                }
                if (!defined.TryGetValue(ident, out string? raw))
                    return 0;
                return long.TryParse(raw, out long symVal) ? symVal : 1;
            }
            pos++;
            return 0;
        }

        return ParseOr() != 0;
    }

    private static void AppendHookDefines(StringBuilder sb, D3D12StyleShaderHookMask hooks)
    {
        if (hooks.HasFlag(D3D12StyleShaderHookMask.LightResponse)) sb.AppendLine("#define MDA_STYLE_HOOK_LIGHT_RESPONSE 1");
        if (hooks.HasFlag(D3D12StyleShaderHookMask.PaletteFactor)) sb.AppendLine("#define MDA_STYLE_HOOK_PALETTE_FACTOR 1");
        if (hooks.HasFlag(D3D12StyleShaderHookMask.Specular)) sb.AppendLine("#define MDA_STYLE_HOOK_SPECULAR 1");
        if (hooks.HasFlag(D3D12StyleShaderHookMask.AlbedoTransform)) sb.AppendLine("#define MDA_STYLE_HOOK_ALBEDO_TRANSFORM 1");
        if (hooks.HasFlag(D3D12StyleShaderHookMask.PrimarySurfacePost)) sb.AppendLine("#define MDA_STYLE_HOOK_PRIMARY_SURFACE_POST 1");
        if (hooks.HasFlag(D3D12StyleShaderHookMask.FinalEntryPre)) sb.AppendLine("#define MDA_STYLE_HOOK_FINAL_ENTRY_PRE 1");
        if (hooks.HasFlag(D3D12StyleShaderHookMask.FinalEntryPost)) sb.AppendLine("#define MDA_STYLE_HOOK_FINAL_ENTRY_POST 1");
        if (hooks.HasFlag(D3D12StyleShaderHookMask.AllowStylizedShadingWrapper)) sb.AppendLine("#define MDA_STYLE_ALLOW_STYLIZED_WRAPPER 1");
        if (hooks.HasFlag(D3D12StyleShaderHookMask.DisableStylizedSphereMaps)) sb.AppendLine("#define MDA_DISABLE_STYLIZED_SPHERE_MAPS 1");
        if (hooks.HasFlag(D3D12StyleShaderHookMask.HasStyleShaderCode)) sb.AppendLine("#define MDA_HAS_STYLE_SHADER_CODE 1");
    }

    private static void AppendGeometryDefines(StringBuilder sb, D3D12StyleGeometryRequirements geometry, bool gi)
    {
        if (geometry.HasFlag(D3D12StyleGeometryRequirements.SubTextureUv)) sb.AppendLine("#define MDA_STYLE_GEOM_SUB_TEXTURE_UV 1");
        if (geometry.HasFlag(D3D12StyleGeometryRequirements.FrontFace)) sb.AppendLine("#define MDA_STYLE_GEOM_FRONT_FACE 1");
        if (gi || geometry.HasFlag(D3D12StyleGeometryRequirements.PrimitiveId)) sb.AppendLine("#define MDA_STYLE_GEOM_PRIMITIVE_ID 1");
        if (geometry.HasFlag(D3D12StyleGeometryRequirements.MikkTangentCorners)) sb.AppendLine("#define MDA_STYLE_GEOM_MIKK_TANGENT_CORNERS 1");
        if (geometry.HasFlag(D3D12StyleGeometryRequirements.RawRadialSeeds)) sb.AppendLine("#define MDA_STYLE_GEOM_RAW_RADIAL_SEEDS 1");
        if (geometry.HasFlag(D3D12StyleGeometryRequirements.ModelSpacePosition)) sb.AppendLine("#define MDA_STYLE_GEOM_MODEL_POSITION 1");
        if (geometry.HasFlag(D3D12StyleGeometryRequirements.ModelSpaceNormal)) sb.AppendLine("#define MDA_STYLE_GEOM_MODEL_NORMAL 1");
    }

    private static void AppendCommonHeader(StringBuilder sb)
    {
        sb.AppendLine("""
            cbuffer FrameConstants : register(b0) {
                row_major float4x4 World;
                row_major float4x4 NormalWorld;
                row_major float4x4 ViewProjection;
                row_major float4x4 SphereRotation;
                row_major float4x4 LightViewProjection;
                float4 EgColor;
                float4 SpecularPower;
                float4 IncomingLight;
                float4 EyePosition;
                float4 ToonShadowColor;
                float4 Options;
                float4 ShadowOptions;
                float4 GroundDirection;
                float4 PmxMaterialTreatmentParams;
                float4 StylizedShadeParams;
                float4 StylizedTintParams;
                float4 StylizedWarmParams;
                float4 StylizedGradeParams;
                float4 MdaExtraSurfaceParams;
            };
            Texture2D<float4> DiffuseMap : register(t0);
            Texture2D<float4> SphereMap : register(t1);
            Texture2D<float> ShadowMap : register(t2);
            SamplerState SelectedSampler : register(s0);
            SamplerState ShadowSampler : register(s1);

            SamplerState MdaLinearWrapSampler : register(s0, space2);
            SamplerState MdaLinearClampSampler : register(s1, space2);
            SamplerState MdaPointWrapSampler : register(s2, space2);
            SamplerState MdaPointClampSampler : register(s3, space2);

            struct MdaTangentCorner {
                float4 WorldPosition;
                float4 WorldNormal;
                float4 Tangent;
                float4 UvParity;
            };
            """);
    }

    private static void AppendGiAndNativeHeaders(StringBuilder sb, bool hasGiResources, bool hasNative)
    {
        if (hasGiResources)
        {
            sb.AppendLine("""
                cbuffer GiSurfaceConstants : register(b1) {
                    uint GiTriangleBase; uint GiSurfaceWidth; float GiSurfaceStrength; float GiOcclusionStrength;
                    float4 GiAmbientShare;
                };
                StructuredBuffer<float4> SurfaceIndirect : register(t3);
                Texture2D<float4> SurfaceGiPosition : register(t4);
                Texture2D<float4> SurfaceGiNormal : register(t5);
                """);
        }
        if (hasNative)
        {
            sb.AppendLine("""
                struct NativeLight {
                    float4 PositionKind; float4 DirectionRange; float4 ColorIntensity; float4 ConeShadow; float4 AreaRightTile;
                };
                cbuffer NativeLightConstants : register(b2) {
                    uint4 NativeInfo; float4 NativeDiffuse;
                };
                cbuffer NativeSharedLightConstants : register(b0, space1) {
                    float4 NativeAtlasLayout;
                    NativeLight NativeLights[32];
                    row_major float4x4 NativeShadowMatrices[768];
                };
                Texture2D<float> NativeShadowAtlas : register(t6);
                """);
            sb.AppendLine(NativeLightModel.Hlsl);
        }
    }

    private static void AppendTextureMorphHeader(StringBuilder sb, bool hasTextureMorph)
    {
        if (hasTextureMorph)
        {
            sb.AppendLine(OriginalTextureMorphShader.Constants);
        }
        else
        {
            sb.AppendLine("""
                float3 MorphDiffuse(float3 sampleColor) { return sampleColor; }
                float3 MorphSphere(float3 sampleColor, bool additive) { return sampleColor; }
                """);
        }
    }

    private static void AppendVertexDefinitions(StringBuilder sb)
    {
        sb.AppendLine("""
            struct Input {
                float3 Position : POSITION;
                float3 Normal : NORMAL;
                float2 Uv : TEXCOORD0;
            #ifdef MDA_STYLE_GEOM_SUB_TEXTURE_UV
                float4 AdditionalUv : TEXCOORD1;
            #endif
            #ifdef MDA_STYLE_GEOM_RAW_RADIAL_SEEDS
                float3 RawSeed : RAW_RADIAL;
            #endif
            };
            struct SurfaceOutput {
                float4 Position : SV_POSITION;
                float3 Normal : NORMAL;
                float2 Uv : TEXCOORD0;
                float3 WorldPosition : TEXCOORD1;
                float2 SphereProjection : TEXCOORD2;
                float4 LightClip : TEXCOORD3;
            #ifdef MDA_STYLE_GEOM_SUB_TEXTURE_UV
                float2 AdditionalUv : TEXCOORD4;
            #endif
            #ifdef MDA_STYLE_GEOM_RAW_RADIAL_SEEDS
                float3 RawSeed : TEXCOORD5;
            #endif
            #ifdef MDA_STYLE_GEOM_MODEL_POSITION
                float3 ModelPosition : TEXCOORD6;
            #endif
            #ifdef MDA_STYLE_GEOM_MODEL_NORMAL
                float3 ModelNormal : TEXCOORD7;
            #endif
            };
            SurfaceOutput VSSurface(Input v) {
                SurfaceOutput o;
                float4 p = mul(float4(v.Position, 1), World);
                o.Position = mul(p, ViewProjection);
                o.WorldPosition = p.xyz;
                o.Normal = mul(float4(v.Normal, 0), NormalWorld).xyz;
                o.Uv = v.Uv;
                o.SphereProjection = mul(float4(v.Normal, 0), SphereRotation).xy;
                o.LightClip = ShadowOptions.z > 0 ? mul(float4(v.Position, 1), LightViewProjection) : float4(0, 0, 0, 1);
            #ifdef MDA_STYLE_GEOM_SUB_TEXTURE_UV
                o.AdditionalUv = v.AdditionalUv.xy;
            #endif
            #ifdef MDA_STYLE_GEOM_RAW_RADIAL_SEEDS
                o.RawSeed = mul(float4(v.RawSeed, 0), World).xyz;
            #endif
            #ifdef MDA_STYLE_GEOM_MODEL_POSITION
                o.ModelPosition = v.Position;
            #endif
            #ifdef MDA_STYLE_GEOM_MODEL_NORMAL
                o.ModelNormal = v.Normal;
            #endif
                return o;
            }
            """);
    }

    private static void AppendPixelHostPrePlugin(StringBuilder sb)
    {
        sb.AppendLine("""
            float2 MdaGetSphereUv(SurfaceOutput v) {
                float2 defaultUv = float2(.5 * v.SphereProjection.x + .5, -.5 * v.SphereProjection.y + .5);
            #ifdef MDA_HAS_SUB_TEXTURE
                return Options.y >= 3 ? v.AdditionalUv : defaultUv;
            #else
                return defaultUv;
            #endif
            }
            """);
        sb.AppendLine(OriginalFrameShaders.PmxMaterialTreatmentHlsl);
        sb.AppendLine("""
            float MdaSamplePrimaryShadowVisibility(float2 uv, float receiverDepth, float2 screenPos) {
                precise float delta = max(receiverDepth - ShadowMap.Sample(ShadowSampler, uv), 0);
                precise float weighted = delta * (ShadowOptions.y > 0 ? 8000 : 1500);
                if (ShadowOptions.y > 0) weighted = weighted * uv.y;
                precise float centerVis = 1 - saturate(weighted - .3);
                if (ShadowOptions.x <= 0) return centerVis;
                uint sw, sh; ShadowMap.GetDimensions(sw, sh);
                float2 radius = ShadowOptions.x * 1.5 / max(float2(sw, sh), float2(1, 1));
                float angle = frac(52.9829189 * frac(dot(screenPos, float2(0.06711056, 0.00583715)))) * 6.2831853;
                float s = sin(angle), cRot = cos(angle);
                static const float2 kPcf[12] = {
                    float2(-0.326212, -0.405810), float2(-0.840144, -0.073580),
                    float2(-0.695914,  0.457137), float2(-0.203345,  0.620716),
                    float2( 0.962340, -0.194983), float2( 0.473434, -0.480026),
                    float2( 0.519456,  0.767022), float2( 0.185461, -0.893124),
                    float2( 0.507431,  0.064425), float2( 0.896420,  0.412458),
                    float2(-0.321940, -0.932615), float2(-0.791559, -0.597710)
                };
                float sum = centerVis;
                [unroll] for (int t = 0; t < 12; t++) {
                    float2 r = float2(kPcf[t].x * cRot - kPcf[t].y * s, kPcf[t].x * s + kPcf[t].y * cRot);
                    float2 suv = saturate(uv + r * radius);
                    float sd = ShadowMap.SampleLevel(ShadowSampler, suv, 0);
                    float d = max(receiverDepth - sd, 0);
                    float w = d * (ShadowOptions.y > 0 ? 8000 : 1500);
                    if (ShadowOptions.y > 0) w *= suv.y;
                    sum += 1 - saturate(w - .3);
                }
                return sum / 13.0;
            }
            float MdaPrimaryShadowVisibility(SurfaceOutput v) {
                if (ShadowOptions.z <= 0) return 1;
                float3 q = v.LightClip.xyz / v.LightClip.w;
                float2 uv = float2(.5 + .5 * q.x, .5 - .5 * q.y);
                if (any(uv < 0) || any(uv > 1)) return 1;
                return MdaSamplePrimaryShadowVisibility(uv, q.z, v.Position.xy);
            }
            float MdaLightResponseDefault(SurfaceOutput v, float3 n, float3 l, float visibility) {
                float c = min(saturate(dot(n, l) * 3), visibility);
            #ifdef MDA_HAS_CHARACTER_TOON
                if (IncomingLight.w > 0) {
                    float lower = ShadowOptions.w - IncomingLight.w * .5;
                    float t = saturate((c - lower) / IncomingLight.w);
                    c = t * t * (3 - 2 * t);
                }
            #endif
                return c;
            }
            float3 MdaPaletteFactorDefault(float blend) {
                return blend + ToonShadowColor.rgb * (1 - blend);
            }
            // Forward declarations available to plugin shader hooks.
            float3 MdaComputeBaseColor(SurfaceOutput v, bool allowStandardSphereMaps);
            float3 MdaComputeAlbedo(SurfaceOutput v);
            float3 MdaComputeDiffuseLit(SurfaceOutput v);
            #ifdef MDA_HAS_GI_RESOURCES
            bool GiToonPaletteDelta(SurfaceOutput v, float3 incident, out float3 increment);
            #endif
            #ifdef MDA_HAS_NATIVE_LIGHTS
            NativeLight NativeSample(NativeLight light, uint sample);
            float NativeVisibility(uint index, NativeLight light, float3 position, float3 normal, float3 towardLight, float3 worldDx, float3 worldDy, uint sample);
            float3 NativeDirect(SurfaceOutput v);
            #endif
            """);
    }

    private static void AppendPixelHostPostPlugin(
        StringBuilder sb,
        string entryPoint,
        D3D12StyleShaderHookMask hooks,
        D3D12StyleGeometryRequirements geometry,
        bool gi,
        bool nativeLights,
        bool preserveDirectLightPalette)
    {
        if (!hooks.HasFlag(D3D12StyleShaderHookMask.LightResponse))
        {
            sb.AppendLine("""
                float MdaStyleLightResponse(SurfaceOutput v, float3 n, float3 l, float visibility) {
                    return MdaLightResponseDefault(v, n, l, visibility);
                }
                """);
        }
        if (!hooks.HasFlag(D3D12StyleShaderHookMask.PaletteFactor))
        {
            sb.AppendLine("""
                float3 MdaStylePaletteFactor(SurfaceOutput v, float blend, float nDotL, float visibility) {
                    return MdaPaletteFactorDefault(blend);
                }
                """);
        }

        sb.AppendLine("""
            float3 MdaComputeBaseColor(SurfaceOutput v, bool allowStandardSphereMaps) {
                float4 texel = DiffuseMap.Sample(SelectedSampler, v.Uv);
                float4 sphere = Options.y > 0 ? SphereMap.Sample(SelectedSampler, MdaGetSphereUv(v)) : float4(1, 1, 1, 1);
                precise float3 baseColor = PmxMaterialSaturation(EgColor.rgb * MorphDiffuse(texel.rgb));
            #ifdef MDA_STYLE_HOOK_ALBEDO_TRANSFORM
                baseColor = MdaStyleAlbedoTransform(v, baseColor);
            #endif
                if (allowStandardSphereMaps) {
                    if (Options.y == 1) baseColor = baseColor * MorphSphere(sphere.rgb, false);
                    if (Options.y == 2) baseColor = baseColor + MorphSphere(sphere.rgb, true);
                }
            #ifdef MDA_HAS_SUB_TEXTURE
                if (Options.y >= 3) {
                    if (Options.y == 4 && Options.z > 0) baseColor += MorphSphere(sphere.rgb, true);
                    else baseColor *= MorphSphere(sphere.rgb, false);
                }
            #endif
                return baseColor;
            }
            float3 MdaComputeAlbedo(SurfaceOutput v) {
                return MdaComputeBaseColor(v, true);
            }
            float3 MdaComputeDiffuseLit(SurfaceOutput v) {
                precise float3 diffuseLit = PmxMaterialSaturation(EgColor.rgb * MorphDiffuse(DiffuseMap.Sample(SelectedSampler, v.Uv).rgb));
            #ifdef MDA_STYLE_HOOK_ALBEDO_TRANSFORM
                diffuseLit = MdaStyleAlbedoTransform(v, diffuseLit);
            #endif
                bool multiplySphere = Options.y == 1;
            #ifdef MDA_HAS_SUB_TEXTURE
                multiplySphere = multiplySphere || (Options.y >= 3 && (Options.y != 4 || Options.z == 0));
            #endif
                if (multiplySphere)
                    diffuseLit = diffuseLit * MorphSphere(SphereMap.Sample(SelectedSampler, MdaGetSphereUv(v)).rgb, false);
                return diffuseLit;
            }
            float4 MdaPSSurfaceCore(SurfaceOutput v, out float hairBand) {
                float4 texel = DiffuseMap.Sample(SelectedSampler, v.Uv);
                float4 sphere = Options.y > 0 ? SphereMap.Sample(SelectedSampler, MdaGetSphereUv(v)) : float4(1, 1, 1, 1);
                float alpha = EgColor.a * texel.a * sphere.a;
                precise float3 baseColor = PmxMaterialSaturation(EgColor.rgb * MorphDiffuse(texel.rgb));
            #ifdef MDA_STYLE_HOOK_ALBEDO_TRANSFORM
                baseColor = MdaStyleAlbedoTransform(v, baseColor);
            #endif
                if (Options.y == 1) baseColor = baseColor * MorphSphere(sphere.rgb, false);
                if (Options.y == 2) baseColor = baseColor + MorphSphere(sphere.rgb, true);
            #ifdef MDA_HAS_SUB_TEXTURE
                if (Options.y >= 3) {
                    if (Options.y == 4 && Options.z > 0) baseColor += MorphSphere(sphere.rgb, true);
                    else baseColor *= MorphSphere(sphere.rgb, false);
                }
            #endif
                float3 n = normalize(v.Normal);
                float3 l = IncomingLight.xyz;
                float3 h = normalize(l + normalize(EyePosition.xyz - v.WorldPosition));
            #ifdef MDA_STYLE_HOOK_SPECULAR
                float3 highlight = MdaStyleSpecular(v, n, h, l, baseColor);
            #else
                float3 highlight = saturate(pow(max(0, dot(n, h)), SpecularPower.w) * SpecularPower.rgb);
            #endif
                hairBand = 0;
                if (PmxMaterialTreatmentParams.z > 0) highlight += PmxGenericHair(v, n, h, l, baseColor, hairBand);
                float3 q = v.LightClip.xyz / v.LightClip.w;
                float2 uv = float2(.5 + .5 * q.x, .5 - .5 * q.y);
                bool inShadowMap = uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1;
                precise float visibility = 1;
                if (inShadowMap) {
                    visibility = MdaSamplePrimaryShadowVisibility(uv, q.z, v.Position.xy);
                }
                float nDotL = dot(n, l);
                float originalC = inShadowMap ? min(saturate(nDotL * 3), visibility) : 1;
                float c = MdaStyleLightResponse(v, n, l, visibility);
                precise float3 color;
            #ifdef MDA_STYLE_HOOK_PALETTE_FACTOR
                color = baseColor * MdaStylePaletteFactor(v, c, nDotL, visibility) + (inShadowMap ? highlight * originalC : highlight);
            #else
                if (inShadowMap) {
                    if (Options.z > 0) color = baseColor * (c + ToonShadowColor.rgb * (1 - c)) + highlight * originalC;
                    else color = baseColor * c + baseColor * ToonShadowColor.rgb * (1 - c) + highlight * originalC;
                } else {
                    color = baseColor * c + baseColor * ToonShadowColor.rgb * (1 - c) + highlight;
                }
            #endif
            #ifdef MDA_HAS_SUB_TEXTURE
                if (inShadowMap && Options.y >= 3 && Options.z > 0 && originalC < 1) alpha *= originalC + ToonShadowColor.w * (1 - originalC);
            #endif
                if (inShadowMap && Options.w > 0) alpha = .5;
                clip(alpha - Options.x);
                if (inShadowMap) hairBand *= originalC;
                return float4(color, alpha);
            }
            float4 MdaPSSurfaceCore(SurfaceOutput v) {
                float hairBand;
                return MdaPSSurfaceCore(v, hairBand);
            }
            """);

        if (hooks.HasFlag(D3D12StyleShaderHookMask.AllowStylizedShadingWrapper))
            sb.AppendLine(OriginalFrameShaders.StylizedShadingHlsl);

        sb.AppendLine("""
            float4 MdaPSSurface(SurfaceOutput v) {
            #ifdef MDA_STYLE_ALLOW_STYLIZED_WRAPPER
                float4 color = MdaPSSurfaceStylized(v);
            #else
                float4 color = MdaPSSurfaceCore(v);
            #endif
            #ifdef MDA_STYLE_HOOK_PRIMARY_SURFACE_POST
                color = MdaStylePrimarySurfacePost(v, color);
            #endif
                return color;
            }
            """);

        if (gi || nativeLights)
        {
            sb.AppendLine("""
                float3 GiOccludableFill(SurfaceOutput v) {
                    float3 texel = DiffuseMap.Sample(SelectedSampler, v.Uv).rgb;
                #if defined(MDA_STYLE_HOOK_ALBEDO_TRANSFORM)
                    precise float3 fill = MdaStyleAlbedoTransform(v, PmxMaterialSaturation(EgColor.rgb * MorphDiffuse(texel))) * GiAmbientShare.rgb;
                #else
                    precise float3 fill;
                    if (PmxMaterialTreatmentParams.x != 1 || PmxMaterialTreatmentParams.y != 0)
                        fill = PmxMaterialSaturation(EgColor.rgb * MorphDiffuse(texel)) * GiAmbientShare.rgb;
                    else
                        fill = EgColor.rgb * GiAmbientShare.rgb * MorphDiffuse(texel);
                #endif
                    bool multiplySphere = Options.y == 1;
                #ifdef MDA_HAS_SUB_TEXTURE
                    multiplySphere = multiplySphere || (Options.y >= 3 && (Options.y != 4 || Options.z == 0));
                #endif
                    if (multiplySphere)
                        fill = fill * MorphSphere(SphereMap.Sample(SelectedSampler, MdaGetSphereUv(v)).rgb, false);
                    float3 n = normalize(v.Normal);
                    float3 l = IncomingLight.xyz;
                    float nDotL = dot(n, l);
                    float3 q = v.LightClip.xyz / v.LightClip.w;
                    float2 uv = float2(.5 + .5 * q.x, .5 - .5 * q.y);
                    bool inShadowMap = uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1;
                    precise float visibility = 1;
                    if (inShadowMap) {
                        visibility = MdaSamplePrimaryShadowVisibility(uv, q.z, v.Position.xy);
                    }
                    float c = MdaStyleLightResponse(v, n, l, visibility);
                #ifdef MDA_STYLE_HOOK_PALETTE_FACTOR
                    return fill * MdaStylePaletteFactor(v, c, nDotL, visibility);
                #else
                    if (!inShadowMap) return fill * MdaStylePaletteFactor(v, c, nDotL, 1);
                    if (Options.z > 0) return fill * (c + ToonShadowColor.rgb * (1 - c));
                    return fill * c + fill * ToonShadowColor.rgb * (1 - c);
                #endif
                }
                bool GiToonPaletteDelta(SurfaceOutput v, float3 incident, out float3 increment) {
                    increment = 0;
                    if (GiSurfaceStrength == 0 || all(incident == 0)) return false;
                    float3 q = v.LightClip.xyz / v.LightClip.w;
                    float2 uv = float2(.5 + .5 * q.x, .5 - .5 * q.y);
                    precise float visibility = 1;
                    if (uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1) {
                        visibility = MdaSamplePrimaryShadowVisibility(uv, q.z, v.Position.xy);
                    }
                    float3 n = normalize(v.Normal);
                    float3 l = IncomingLight.xyz;
                    float nDotL = dot(n, l);
                    float c = MdaStyleLightResponse(v, n, l, visibility);
                #ifndef MDA_STYLE_HOOK_PALETTE_FACTOR
                    if (c == 1) return false;
                    precise float3 toonFactor = c + ToonShadowColor.rgb * (1 - c);
                #else
                    precise float3 toonFactor = MdaStylePaletteFactor(v, c, nDotL, visibility);
                #endif
                    precise float3 diffuseLit = MdaComputeDiffuseLit(v);
                    if (GiAmbientShare.w == 2) {
                        float luminance = dot(max(incident, 0), float3(.2126, .7152, .0722));
                        float mapped = saturate(GiSurfaceStrength * luminance / 1.0);
                        float fill = floor(mapped * 4 + .5) * .0625;
                        increment = diffuseLit * max(1 - toonFactor, 0) * fill;
                        return any(increment != 0);
                    }
                    increment = diffuseLit * max(1 - toonFactor, 0) * saturate(GiSurfaceStrength * max(incident, 0) / 1.0);
                    return any(increment != 0);
                }
                float4 MdaPSSurfaceGi(SurfaceOutput v, uint primitive) {
                    float4 color = MdaPSSurface(v);
                    int3 pixel = int3((int2)v.Position.xy, 0);
                    float4 receiver = SurfaceGiNormal.Load(pixel);
                    float depth = SurfaceGiPosition.Load(pixel).w;
                    if (color.a >= .999 && receiver.w == (float)(GiTriangleBase + primitive + 1) && depth == v.Position.z) {
                        float4 transport = SurfaceIndirect[pixel.y * GiSurfaceWidth + pixel.x];
                        if (GiOcclusionStrength > 0) color.rgb -= GiOccludableFill(v) * ((1 - transport.a) * GiOcclusionStrength);
                        if (GiAmbientShare.w >= 1) {
                            float3 increment;
                            if (!GiToonPaletteDelta(v, transport.rgb, increment)) return color;
                            color.rgb += increment;
                            return color;
                        }
                        color.rgb += transport.rgb * GiSurfaceStrength;
                    }
                    return color;
                }
                """);
        }

        if (nativeLights)
        {
            sb.AppendLine("""
                #ifdef MDA_STYLE_ALLOW_STYLIZED_WRAPPER
                #define NATIVE_LIGHT_RESPONSE(v, n, l) StylizedNativeResponse(v, n, l)
                #else
                #define NATIVE_LIGHT_RESPONSE(v, n, l) MdaStyleLightResponse(v, n, l, 1)
                #endif
                static const float NativeHighlightKnee = .6;
                float NativeHighlightShoulder(float t) {
                    if (t <= NativeHighlightKnee) return t;
                    float u = (t - NativeHighlightKnee) / (1 - NativeHighlightKnee);
                    return NativeHighlightKnee + (1 - NativeHighlightKnee) * u / (1 + u);
                }
                float NativeSoftAddScale(float3 room, float3 addition) {
                    if (!all(isfinite(room)) || !all(isfinite(addition)) || any(addition < 0)) return 1;
                    float t = 0;
                    if (room.x > 0) t = max(t, addition.x / room.x);
                    if (room.y > 0) t = max(t, addition.y / room.y);
                    if (room.z > 0) t = max(t, addition.z / room.z);
                    if (t <= NativeHighlightKnee || !isfinite(t)) return 1;
                    return NativeHighlightShoulder(t) / t;
                }
                float3 NativeAddSoft(float3 color, float3 addition) {
                    float scale = NativeSoftAddScale(max(1 - color, 0), addition);
                    return scale == 1 ? color + addition : color + scale * addition;
                }
                uint NativePointFace(float3 fromLight) {
                    float3 a = abs(fromLight);
                    if (a.x >= a.y && a.x >= a.z) return fromLight.x >= 0 ? 0 : 1;
                    if (a.y >= a.z) return fromLight.y >= 0 ? 2 : 3;
                    return fromLight.z >= 0 ? 4 : 5;
                }
                NativeLight NativeSample(NativeLight light, uint sample) {
                    if (light.PositionKind.w == 3) {
                        float2 sign = float2((sample & 1) ? 1 : -1, (sample & 2) ? 1 : -1);
                        float3 up = cross(light.AreaRightTile.xyz, light.DirectionRange.xyz);
                        light.PositionKind.xyz += (light.AreaRightTile.xyz * (sign.x * light.ConeShadow.x) + up * (sign.y * light.ConeShadow.y)) * .5773502691896258;
                        light.ColorIntensity.w *= .25;
                        if (light.ConeShadow.z >= 0) light.ConeShadow.z += sample * 6;
                    }
                    return light;
                }
                float NativeVisibility(uint index, NativeLight light, float3 position, float3 normal, float3 towardLight, float3 worldDx, float3 worldDy, uint sample) {
                    if (light.ConeShadow.z < 0 || NativeInfo.w == 0) return 1;
                    uint face = (light.PositionKind.w == 1 || light.PositionKind.w == 3) ? NativePointFace(position - light.PositionKind.xyz) : 0;
                    uint tile = (uint)light.ConeShadow.z + face;
                    float offset = light.PositionKind.w == 0 ? light.ConeShadow.w : max(light.DirectionRange.w * .00015, .0001);
                    float4 clipPosition = mul(float4(position + normal * offset, 1), NativeShadowMatrices[tile]);
                    if (clipPosition.w <= 0) return 1;
                    float3 q = clipPosition.xyz / clipPosition.w;
                    float2 uv = float2(.5 + .5 * q.x, .5 - .5 * q.y);
                    if (any(uv < 0) || any(uv > 1) || q.z < 0 || q.z > 1) return 1;
                    bool area = light.PositionKind.w == 3;
                    int tileSize = area ? 256 : 512;
                    uint atlasTile = (uint)light.AreaRightTile.w + face;
                    uint columns = (uint)NativeAtlasLayout.x;
                    int2 origin = int2(atlasTile % columns, atlasTile / columns) * 512;
                    if (area) origin += int2(sample & 1, sample >> 1) * tileSize;
                    int2 pixel = (int2)(uv * tileSize);
                    float2 receiverDepthGradient = 0;
                    if (NativeInfo.z > 0) {
                        float4 tx = mul(float4(worldDx, 0), NativeShadowMatrices[tile]);
                        float4 ty = mul(float4(worldDy, 0), NativeShadowMatrices[tile]);
                        float3 dx = tx.xyz - q * tx.w, dy = ty.xyz - q * ty.w;
                        float determinant = dx.x * dy.y - dx.y * dy.x;
                        float scale = max(abs(dx.x * dy.y) + abs(dx.y * dy.x), 1e-20);
                        if (abs(determinant) > scale * 1e-5) {
                            float2 gradient = float2(dx.z * dy.y - dx.y * dy.z, dx.x * dy.z - dx.z * dy.x) / determinant;
                            gradient *= float2(2, -2);
                            if (all(isfinite(gradient))) receiverDepthGradient = gradient;
                        }
                    }
                    if (area) {
                        float receiverZ = .001 / max(1 - q.z * .999, 1e-6);
                        float span = min(max(light.ConeShadow.x, light.ConeShadow.y) / light.DirectionRange.w, 4);
                        float searchRadius = clamp(span * tileSize * .25 / max(receiverZ, 1e-4), 1, 24);
                        float blockers = 0, blockerZ = 0;
                        [unroll] for (int by = -1; by <= 1; by++) [unroll] for (int bx = -1; bx <= 1; bx++) {
                            int2 local = clamp((int2)(uv * tileSize + float2(bx, by) * searchRadius), int2(0, 0), int2(tileSize - 1, tileSize - 1));
                            float expected = q.z + dot(receiverDepthGradient, ((float2)local + .5) / tileSize - uv);
                            float depth = NativeShadowAtlas.Load(int3(origin + local, 0));
                            if (depth < expected - .00002) { blockers++; blockerZ += .001 / max(1 - depth * .999, 1e-6); }
                        }
                        if (blockers == 0) return 1;
                        blockerZ /= blockers;
                        float radius = clamp(span * tileSize * .25 * max(receiverZ - blockerZ, 0) / max(receiverZ * blockerZ, 1e-6), 1, 32);
                        float soft = 0;
                        [unroll] for (int tap = 0; tap < 16; tap++) {
                            float angle = tap * 2.399963229728653;
                            float2 offset2 = float2(cos(angle), sin(angle)) * sqrt((tap + .5) / 16) * radius;
                            int2 local = clamp((int2)(uv * tileSize + offset2), int2(0, 0), int2(tileSize - 1, tileSize - 1));
                            float expected = q.z + dot(receiverDepthGradient, ((float2)local + .5) / tileSize - uv);
                            soft += (expected - .00002 <= NativeShadowAtlas.Load(int3(origin + local, 0))) ? 1.0 : 0.0;
                        }
                        return soft / 16;
                    }
                    float visibility = 0;
                    [unroll] for (int y = -1; y <= 1; y++) [unroll] for (int x = -1; x <= 1; x++) {
                        int2 local = clamp(pixel + int2(x, y), int2(0, 0), int2(tileSize - 1, tileSize - 1));
                        int2 p = origin + local;
                        float expected = q.z + dot(receiverDepthGradient, ((float2)local + .5) / tileSize - uv);
                        visibility += (expected - .00002 <= NativeShadowAtlas.Load(int3(p, 0))) ? 1.0 : 0.0;
                    }
                    return visibility / 9;
                }
                float NativeVisibility(uint index, NativeLight light, float3 position, float3 normal, float3 towardLight) {
                    return NativeVisibility(index, light, position, normal, towardLight, 0, 0, 0);
                }
                float3 NativeDirect(SurfaceOutput v) {
                    float3 worldDx = ddx(v.WorldPosition), worldDy = ddy(v.WorldPosition);
                    float3 n = normalize(v.Normal);
                    float3 texel = DiffuseMap.Sample(SelectedSampler, v.Uv).rgb;
                    float3 diffuse = PmxMaterialSaturation(NativeDiffuse.rgb * MorphDiffuse(texel));
                #ifdef MDA_STYLE_HOOK_ALBEDO_TRANSFORM
                    diffuse = MdaStyleAlbedoTransform(v, diffuse);
                #endif
                    bool multiplySphere = Options.y == 1;
                #ifdef MDA_HAS_SUB_TEXTURE
                    multiplySphere = multiplySphere || (Options.y >= 3 && (Options.y != 4 || Options.z == 0));
                #endif
                    if (multiplySphere) diffuse *= MorphSphere(SphereMap.Sample(SelectedSampler, MdaGetSphereUv(v)).rgb, false);
                    float3 result = 0;
                    if (NativeAtlasLayout.z > 0) result = diffuse * NativeAtlasLayout.z;
                    [loop] for (uint i = 0; i < (uint)NativeInfo.x; i++) {
                        if ((NativeInfo.y & (1u << i)) == 0) continue;
                        NativeLight source = NativeLights[i];
                        [loop] for (uint sample = 0; sample < (source.PositionKind.w == 3 ? 4u : 1u); sample++) {
                            NativeLight light = NativeSample(source, sample);
                            float3 delta = light.PositionKind.xyz - v.WorldPosition;
                            float distanceSquared = max(dot(delta, delta), .0001);
                            float3 l = light.PositionKind.w == 0 ? -light.DirectionRange.xyz : delta * rsqrt(distanceSquared);
                            float attenuation = NativeLightAttenuation(light.PositionKind, light.DirectionRange, light.ConeShadow, distanceSquared, l);
                            float toon = NATIVE_LIGHT_RESPONSE(v, n, l);
                            if (toon <= 0 || attenuation <= 0) continue;
                            float visibility = NativeVisibility(i, light, v.WorldPosition, n, l, worldDx, worldDy, sample);
                            result += diffuse * light.ColorIntensity.rgb * (light.ColorIntensity.w * attenuation * toon * visibility);
                        }
                    }
                    return result;
                }
                float4 MdaPSSurfaceNative(SurfaceOutput v) {
                    float4 color = MdaPSSurface(v);
                    color.rgb = NativeAddSoft(color.rgb, NativeDirect(v));
                    return color;
                }
                float4 MdaPSSurfaceGiNative(SurfaceOutput v, uint primitive) {
                    float4 color = MdaPSSurfaceGi(v, primitive);
                    color.rgb = NativeAddSoft(color.rgb, NativeDirect(v));
                    return color;
                }
                """);

            if (preserveDirectLightPalette)
            {
                sb.AppendLine("""
                    float3 CharacterRemainingLightBudget(SurfaceOutput v) {
                        precise float3 diffuseLit = MdaComputeDiffuseLit(v);
                        if (!all(isfinite(diffuseLit)) || any(diffuseLit < 0)
                            || !all(isfinite(ToonShadowColor.rgb)) || any(ToonShadowColor.rgb < 0) || any(ToonShadowColor.rgb > 1)) return -1;
                        float3 q = v.LightClip.xyz / v.LightClip.w;
                        float2 uv = float2(.5 + .5 * q.x, .5 - .5 * q.y);
                        precise float visibility = 1;
                        if (uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1) {
                            visibility = MdaSamplePrimaryShadowVisibility(uv, q.z, v.Position.xy);
                        }
                        float3 n = normalize(v.Normal);
                        float3 l = IncomingLight.xyz;
                        float c = MdaStyleLightResponse(v, n, l, visibility);
                        precise float3 toonFactor = MdaStylePaletteFactor(v, c, dot(n, l), visibility);
                        return diffuseLit * max(1 - toonFactor, 0);
                    }
                    float CharacterAddedLightScale(float3 budget, float3 directIncrement, float3 giIncrement) {
                        precise float3 total = directIncrement + giIncrement;
                        if (any(budget < 0) || !all(isfinite(budget)) || any(directIncrement < 0) || any(giIncrement < 0)
                            || !all(isfinite(total))) return 1;
                        if (all(total <= budget)) return 1;
                        float scale = 1;
                        if (total.x > 0) scale = min(scale, budget.x / total.x);
                        if (total.y > 0) scale = min(scale, budget.y / total.y);
                        if (total.z > 0) scale = min(scale, budget.z / total.z);
                        return scale;
                    }
                    float4 CharacterGiSurfaceBase(SurfaceOutput v, uint primitive, out float3 giIncrement, out bool hasGiIncrement) {
                        giIncrement = 0; hasGiIncrement = false;
                        float4 color = MdaPSSurface(v);
                        int3 pixel = int3((int2)v.Position.xy, 0);
                        float4 receiver = SurfaceGiNormal.Load(pixel);
                        float depth = SurfaceGiPosition.Load(pixel).w;
                        if (color.a >= .999 && receiver.w == (float)(GiTriangleBase + primitive + 1) && depth == v.Position.z) {
                            float4 transport = SurfaceIndirect[pixel.y * GiSurfaceWidth + pixel.x];
                            if (GiOcclusionStrength > 0) color.rgb -= GiOccludableFill(v) * ((1 - transport.a) * GiOcclusionStrength);
                            if (GiAmbientShare.w >= 1) {
                                float3 increment;
                                if (!GiToonPaletteDelta(v, transport.rgb, increment)) return color;
                                giIncrement = increment; hasGiIncrement = true; return color;
                            }
                            giIncrement = transport.rgb * GiSurfaceStrength; hasGiIncrement = true;
                        }
                        return color;
                    }
                    float4 MdaPSSurfaceNativeDirectPalette(SurfaceOutput v) {
                        float4 color = MdaPSSurface(v);
                        float3 directIncrement = NativeDirect(v);
                        if (NativeDiffuse.a <= 0) { color.rgb = NativeAddSoft(color.rgb, directIncrement); return color; }
                        float scale = CharacterAddedLightScale(CharacterRemainingLightBudget(v), directIncrement, 0);
                        if (scale == 1) color.rgb += directIncrement;
                        else color.rgb += scale * directIncrement;
                        return color;
                    }
                    float4 MdaPSSurfaceGiNativeDirectPalette(SurfaceOutput v, uint primitive) {
                        if (NativeDiffuse.a <= 0) {
                            float4 color = MdaPSSurfaceGi(v, primitive);
                            color.rgb = NativeAddSoft(color.rgb, NativeDirect(v));
                            return color;
                        }
                        if (GiAmbientShare.w < 1) {
                            float4 color = MdaPSSurfaceGi(v, primitive);
                            float3 directIncrement = NativeDirect(v);
                            float scale = CharacterAddedLightScale(CharacterRemainingLightBudget(v), directIncrement, 0);
                            if (scale == 1) color.rgb += directIncrement;
                            else color.rgb += scale * directIncrement;
                            return color;
                        }
                        float3 giIncrement; bool hasGiIncrement;
                        float4 color = CharacterGiSurfaceBase(v, primitive, giIncrement, hasGiIncrement);
                        float3 directIncrement = NativeDirect(v);
                        float scale = CharacterAddedLightScale(CharacterRemainingLightBudget(v), directIncrement, giIncrement);
                        if (scale == 1) {
                            if (hasGiIncrement) color.rgb += giIncrement;
                            color.rgb += directIncrement;
                        } else color.rgb += scale * (directIncrement + giIncrement);
                        return color;
                    }
                    """);
            }
        }

        // Emit the selected final entry point with optional SV_PrimitiveID, SV_IsFrontFace, FinalEntryPre, and FinalEntryPost.
        bool passPrimitive = gi || geometry.HasFlag(D3D12StyleGeometryRequirements.PrimitiveId);
        bool passFront = geometry.HasFlag(D3D12StyleGeometryRequirements.FrontFace);
        string innerCall = entryPoint switch
        {
            "PSSurface" => "MdaPSSurface(v)",
            "PSSurfaceGi" => "MdaPSSurfaceGi(v, mdaPrimitive)",
            "PSSurfaceNative" => preserveDirectLightPalette ? "MdaPSSurfaceNativeDirectPalette(v)" : "MdaPSSurfaceNative(v)",
            "PSSurfaceGiNative" => preserveDirectLightPalette ? "MdaPSSurfaceGiNativeDirectPalette(v, mdaPrimitive)" : "MdaPSSurfaceGiNative(v, mdaPrimitive)",
            _ => throw new ArgumentOutOfRangeException(nameof(entryPoint)),
        };

        sb.Append("float4 ").Append(entryPoint).Append("(SurfaceOutput v");
        if (passPrimitive) sb.Append(", uint mdaPrimitive : SV_PrimitiveID");
        if (passFront) sb.Append(", bool mdaFront : SV_IsFrontFace");
        sb.AppendLine(") : SV_Target {");
        if (!passPrimitive) sb.AppendLine("    uint mdaPrimitive = 0;");
        if (!passFront) sb.AppendLine("    bool mdaFront = true;");
        if (hooks.HasFlag(D3D12StyleShaderHookMask.FinalEntryPre))
            sb.AppendLine("    MdaStyleFinalEntryPre(v, mdaPrimitive, mdaFront);");
        sb.Append("    float4 color = ").Append(innerCall).AppendLine(";");
        if (hooks.HasFlag(D3D12StyleShaderHookMask.FinalEntryPost))
            sb.AppendLine("    color = MdaStyleFinalEntryPost(v, mdaPrimitive, mdaFront, color);");
        sb.AppendLine("    return color;");
        sb.AppendLine("}");
    }
}
