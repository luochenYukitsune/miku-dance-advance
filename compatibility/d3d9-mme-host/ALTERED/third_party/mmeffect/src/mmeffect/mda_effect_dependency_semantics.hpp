// ALTERED MDA: shared, descriptor-only resource semantic dispatch extracted
// from this fixed MIT host's sas_interpreter.cpp. No D3D/COM/device calls.
// Values below are D3DX descriptor numbers, not an alternate FX parser.
#pragma once

#include <string>

namespace mme {

inline constexpr unsigned int kMdaEffectDependencySemanticsVersion = 1;

struct MdaEffectDependencyParameterDesc {
    std::string name;
    std::string semantic;
    unsigned int parameterClass = 0;
    unsigned int parameterType = 0;
    unsigned int rows = 0;
    unsigned int columns = 0;
    unsigned int elements = 0;
    unsigned int annotationCount = 0;
    unsigned int flags = 0;
};

inline unsigned char MdaEffectDependencyLowerAscii(unsigned char value) {
    return (value >= 'A' && value <= 'Z')
               ? static_cast<unsigned char>(value + ('a' - 'A')) : value;
}

inline bool MdaEffectDependencyAsciiEqual(const std::string& left,
                                         const char* right) {
    if (right == nullptr) return left.empty();
    size_t i = 0;
    for (; i < left.size() && right[i] != '\0'; ++i) {
        if (MdaEffectDependencyLowerAscii(static_cast<unsigned char>(left[i])) !=
            MdaEffectDependencyLowerAscii(static_cast<unsigned char>(right[i]))) {
            return false;
        }
    }
    return i == left.size() && right[i] == '\0';
}

// [0x1800B2FA0..0x1800B36A0] the fixed host's 56 semantic entries;
// name table B begins at 0x1800B36A0. D3DX descriptor values are numeric so
// compiler readers need no ID3DXEffect or GPU header to use this contract.
struct MdaEffectDependencySemanticEntry {
    int         id;
    const char* name;
    int         cls;
    int         type;
    int         count;  // expected Rows*Columns (255 = "any")
};

inline constexpr MdaEffectDependencySemanticEntry kMdaDependencySemanticTable[] = {
    {0x00, "World", 2, 3, 16}, {0x01, "WorldInverse", 2, 3, 16},
    {0x02, "WorldTranspose", 2, 3, 16}, {0x03, "WorldInverseTranspose", 2, 3, 16},
    {0x04, "View", 2, 3, 16}, {0x05, "ViewInverse", 2, 3, 16},
    {0x06, "ViewTranspose", 2, 3, 16}, {0x07, "ViewInverseTranspose", 2, 3, 16},
    {0x08, "Projection", 2, 3, 16}, {0x09, "ProjectionInverse", 2, 3, 16},
    {0x0A, "ProjectionTranspose", 2, 3, 16}, {0x0B, "ProjectionInverseTranspose", 2, 3, 16},
    {0x0C, "WorldView", 2, 3, 16}, {0x0D, "WorldViewInverse", 2, 3, 16},
    {0x0E, "WorldViewTranspose", 2, 3, 16}, {0x0F, "WorldViewInverseTranspose", 2, 3, 16},
    {0x10, "ViewProjection", 2, 3, 16}, {0x11, "ViewProjectionInverse", 2, 3, 16},
    {0x12, "ViewProjectionTranspose", 2, 3, 16}, {0x13, "ViewProjectionInverseTranspose", 2, 3, 16},
    {0x14, "WorldViewProjection", 2, 3, 16}, {0x15, "WorldViewProjectionInverse", 2, 3, 16},
    {0x16, "WorldViewProjectionTranspose", 2, 3, 16},
    {0x17, "WorldViewProjectionInverseTranspose", 2, 3, 16},
    {0x18, "Diffuse", 1, 3, 4}, {0x19, "Ambient", 1, 3, 4},
    {0x1A, "Emissive", 1, 3, 4}, {0x1B, "Specular", 1, 3, 4},
    {0x1F, "Position", 1, 3, 4}, {0x20, "Direction", 1, 3, 4},
    {0x1C, "ToonColor", 1, 3, 4}, {0x1D, "EdgeColor", 1, 3, 4},
    {0x1E, "SpecularPower", 1, 3, 255}, {0x21, "ViewportPixelSize", 1, 3, 2},
    {0x22, "Time", 0, 3, 1}, {0x23, "ElapsedTime", 0, 3, 1},
    {0x24, "Time2", 0, 3, 1}, {0x25, "ElapsedTime2", 0, 3, 1},
    {0x26, "RenderColorTarget", 4, 5, 0}, {0x27, "RenderDepthStencilTarget", 4, 5, 0},
    {0x28, "ControlObject", 1, 3, 255}, {0x29, "MaterialTexture", 4, 5, 0},
    {0x2A, "MaterialSphereMap", 4, 5, 0}, {0x2B, "MaterialToonTexture", 4, 5, 0},
    {0x2F, "MousePosition", 1, 3, 2}, {0x30, "LeftMouseDown", 1, 3, 4},
    {0x31, "MiddleMouseDown", 1, 3, 4}, {0x32, "RightMouseDown", 1, 3, 4},
    {0x2D, "AnimatedTexture", 4, 5, 0}, {0x2E, "OffScreenRenderTarget", 4, 5, 0},
    {0x33, "TextureValue", 1, 3, 4},
    {0x4A, "AddingTexture", 1, 3, 4}, {0x4B, "MultiplyingTexture", 1, 3, 4},
    {0x4C, "AddingSphereTexture", 1, 3, 4}, {0x4D, "MultiplyingSphereTexture", 1, 3, 4},
    {0x4E, "GroundShadowColor", 1, 3, 4}
};

// [0x1800B36A0..0x1800B39A8] parameter-name table, 24 entries of 32 bytes
// (matWorld..SphCMul): matched by _stricmp against D3DXPARAMETER_DESC.Name.
inline constexpr MdaEffectDependencySemanticEntry kMdaDependencyNameTable[] = {
    {0x34, "matWorld", 2, 3, 16}, {0x14, "matWorldViewProj", 2, 3, 16},
    {0x35, "matLightViewProj", 2, 3, 16}, {0x36, "matRotate", 2, 3, 16},
    {0x39, "EgColor", 1, 3, 4}, {0x1C, "ToonColor", 1, 3, 4},
    {0x37, "LightDir", 1, 3, 4}, {0x3A, "SpcColor", 1, 3, 4},
    {0x38, "Place", 1, 3, 4}, {0x3B, "DifColor", 1, 3, 4},
    {0x3C, "parthf", 0, 1, 1}, {0x3D, "spadd", 0, 1, 1},
    {0x3E, "transp", 0, 1, 1}, {0x3F, "use_texture", 0, 1, 1},
    {0x40, "use_spheremap", 0, 1, 1}, {0x41, "use_subtexture", 0, 1, 1},
    {0x42, "use_toon", 0, 1, 1}, {0x43, "VertexCount", 0, 2, 1},
    {0x44, "SubsetCount", 0, 2, 1}, {0x45, "opadd", 0, 1, 1},
    {0x46, "TexCAdd", 1, 3, 4}, {0x47, "TexCMul", 1, 3, 4},
    {0x48, "SphCAdd", 1, 3, 4}, {0x49, "SphCMul", 1, 3, 4}
};

// Existing host validation: normalize matrix columns; enforce matrix/vector
// shape; retain table-A exceptions and table-B strictness; reject arrays in
// table A except TextureValue. No texture decoding/device capability checks.
inline bool MdaValidateEffectDependencyParameter(
    const MdaEffectDependencySemanticEntry& e,
    const MdaEffectDependencyParameterDesc& pd, bool strict) {
    int cls = static_cast<int>(pd.parameterClass);
    if (cls == 3) {
        cls = 2;
    }
    int type = static_cast<int>(pd.parameterType);
    int count = static_cast<int>(pd.rows) * static_cast<int>(pd.columns);

    // Shape check (matrix 4 rows, vector 1 row).
    if (cls == 2 && pd.rows != 4) {
        return false;
    }
    if (cls == 1 && pd.rows != 1) {
        return false;
    }

    // Class check with per-semantic exceptions (skipped when strict).
    if (e.cls != cls && !strict) {
        bool ok = false;
        if (e.id == 0x1E) {            // SpecularPower: scalar accepted
            ok = (cls == 0);
        } else if (e.id == 0x28) {     // ControlObject: scalar or matrix
            ok = (cls == 0 || cls == 2);
        } else if (e.cls == 0 && cls == 1) {  // 1-element vector as scalar
            ok = (count == 1);
        }
        if (!ok) {
            return false;
        }
    } else if (e.cls != cls) {
        return false;
    }

    // Type check: TEXTURE accepts TEXTURE2D; ControlObject accepts BOOL.
    if (e.type != type) {
        bool ok = (e.type == 5 && type == 7);
        if (!ok && !strict && e.id == 0x28 && type == 1 && pd.columns == 1) {
            ok = true;
        }
        if (!ok) {
            return false;
        }
    }

    // Element-count check with per-semantic exceptions (skipped when strict).
    if (e.count != 255 && e.count != count) {
        bool ok = false;
        if (!strict) {
            unsigned int ucount = static_cast<unsigned int>(count);
            if (e.id == 0x1E) {
                ok = ((ucount - 1u) <= 3u) && count != 2;  // {1,3,4}
            } else if (e.id == 0x28) {
                ok = ((ucount - 1u) <= 3u) && count != 2 && count != 16;
            } else if (e.id == 0x18 || e.id == 0x19 || e.id == 0x1A ||
                       e.id == 0x1B || e.id == 0x1F || e.id == 0x20 ||
                       e.id == 0x1C || e.id == 0x1D) {
                ok = (e.count == 4 && count == 3);  // float3 for float4
            } else if (e.id == 0x30 || e.id == 0x31 || e.id == 0x32) {
                ok = true;  // mouse-button vectors accept any size
            }
        }
        if (!ok) {
            return false;
        }
    }

    // Arrays are invalid except TextureValue - TABLE A ONLY [0x18000e66c:
    // cmp [desc+0x20 = Elements],0 then id!=0x33 feeds the semantic-table
    // error flag]. The name-table B branch (LABEL_458, 0x18000e88d-0x18000e9e4)
    // has no Elements/Flags gate at all: class/type/count + Rows shape only.
    if (!strict && pd.elements != 0 && e.id != 0x33) {
        return false;
    }
    return true;
}

struct MdaEffectDependencySemanticResolution {
    int semanticId = -1;
    bool invalid = false;
    bool matched = false;  // semantic/name table, rather than texture fallback
    const char* invalidSemanticName = nullptr;
    bool invalidName = false;
};

// Match the same semantic table, name table and plain-texture fallback as
// SasValidateParameters. Invalid descriptors remain invalid even if a texture
// fallback is present; discovery must not declare an invalid profile complete.
inline MdaEffectDependencySemanticResolution MdaResolveEffectDependencySemantic(
    const MdaEffectDependencyParameterDesc& pd) {
    MdaEffectDependencySemanticResolution result;
    for (size_t i = 0; i < sizeof(kMdaDependencySemanticTable) /
                              sizeof(kMdaDependencySemanticTable[0]); ++i) {
        const MdaEffectDependencySemanticEntry& entry = kMdaDependencySemanticTable[i];
        if (!MdaEffectDependencyAsciiEqual(pd.semantic, entry.name)) continue;
        if (pd.parameterClass == 4 &&
            (entry.id == 0x18 || entry.id == 0x19 || entry.id == 0x1A ||
             entry.id == 0x1B || entry.id == 0x1E || entry.id == 0x1F ||
             entry.id == 0x20)) break;
        result.semanticId = entry.id;
        result.matched = true;
        result.invalid = !MdaValidateEffectDependencyParameter(entry, pd, false);
        if (result.invalid) result.invalidSemanticName = entry.name;
        return result;
    }
    for (size_t i = 0; i < sizeof(kMdaDependencyNameTable) /
                              sizeof(kMdaDependencyNameTable[0]); ++i) {
        const MdaEffectDependencySemanticEntry& entry = kMdaDependencyNameTable[i];
        if (!MdaEffectDependencyAsciiEqual(pd.name, entry.name)) continue;
        if (MdaValidateEffectDependencyParameter(entry, pd, true)) {
            result.semanticId = entry.id;
            result.matched = true;
            return result;
        }
        if (entry.id >= 0x3F && entry.id <= 0x42) {
            result.invalid = true;
            result.invalidName = true;
        }
        break;
    }
    if (pd.parameterType == 5 || pd.parameterType == 7 ||
        pd.parameterType == 8 || pd.parameterType == 9) {
        result.semanticId = 0x2C;
    }
    return result;
}

}  // namespace mme
