using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Explicit upstream shadow/base-image inputs for the coupled Endfield palette.
/// Inputs share the parent's captured effective UV. Base RGB is decoded before filtering;
/// detail consumes numerical Blue and shadow consumes numerical Red. Alpha is ignored.
/// This does not implement source dynamic lighting or change surface coverage.</summary>
public sealed record D3D12EndfieldBaseImageBinding(
    Vector3 BaseImageLinearRgb,
    Vector3 ModelColor,
    Vector3 ShadowColor,
    Vector3 ColorMultiplier,
    float ShadowFactor = 1,
    float WarmTint = 0,
    float Contrast = 0,
    float Saturation = 1,
    float DetailBlue = 1,
    float ShadowStrength = 1,
    D3D12EndfieldPaletteInputMode BaseInputMode = D3D12EndfieldPaletteInputMode.Uniform,
    int? BaseCombinedTextureIndex = null,
    D3D12MatcapColorEncoding BaseEncoding = D3D12MatcapColorEncoding.LinearRgb,
    D3D12EndfieldPaletteInputMode DetailInputMode = D3D12EndfieldPaletteInputMode.Uniform,
    int? DetailCombinedTextureIndex = null,
    D3D12EndfieldPaletteInputMode ShadowInputMode = D3D12EndfieldPaletteInputMode.Uniform,
    int? ShadowCombinedTextureIndex = null,
    D3D12EndfieldFacingBaseBinding? FacingBase = null,
    D3D12EndfieldDirectionalShadowBinding? DirectionalShadow = null)
{
    internal bool ConsumesBaseTexture => BaseInputMode == D3D12EndfieldPaletteInputMode.Texture;
    internal bool ConsumesDetailTexture => DetailInputMode == D3D12EndfieldPaletteInputMode.Texture;
    internal bool ConsumesShadowTexture => ShadowInputMode == D3D12EndfieldPaletteInputMode.Texture && Math.Clamp(ShadowStrength,0,1)>0;

    public void Validate()
    {
        FacingBase?.Validate(); DirectionalShadow?.Validate();
        if (DirectionalShadow is not null && ShadowInputMode == D3D12EndfieldPaletteInputMode.Texture)
            throw new ArgumentException("Directional shadow and explicit shadow texture are mutually exclusive.", nameof(DirectionalShadow));
        Color(BaseImageLinearRgb,nameof(BaseImageLinearRgb)); Color(ModelColor,nameof(ModelColor));
        Color(ShadowColor,nameof(ShadowColor)); Color(ColorMultiplier,nameof(ColorMultiplier));
        Scalar(ShadowFactor,32,nameof(ShadowFactor)); Scalar(WarmTint,32,nameof(WarmTint));
        Scalar(Contrast,4,nameof(Contrast)); Scalar(Saturation,16,nameof(Saturation));
        Scalar(DetailBlue,16,nameof(DetailBlue)); Scalar(ShadowStrength,32,nameof(ShadowStrength));
        Input(BaseInputMode,BaseCombinedTextureIndex,nameof(BaseInputMode),nameof(BaseCombinedTextureIndex));
        Input(DetailInputMode,DetailCombinedTextureIndex,nameof(DetailInputMode),nameof(DetailCombinedTextureIndex));
        Input(ShadowInputMode,ShadowCombinedTextureIndex,nameof(ShadowInputMode),nameof(ShadowCombinedTextureIndex));
        if (!Enum.IsDefined(BaseEncoding)) throw new ArgumentOutOfRangeException(nameof(BaseEncoding));
        if (BaseInputMode == D3D12EndfieldPaletteInputMode.Uniform && BaseEncoding != D3D12MatcapColorEncoding.LinearRgb)
            throw new ArgumentException("Uniform base RGB must be explicitly linear.",nameof(BaseEncoding));
    }

    internal static bool ConsumedEquivalent(D3D12EndfieldBaseImageBinding? a,D3D12EndfieldBaseImageBinding? b,bool compareTextureIndices=true)
    {
        if (ReferenceEquals(a,b)) return true;
        if (a is null || b is null || a.Contrast!=b.Contrast || a.Saturation!=b.Saturation
            || a.ColorMultiplier!=b.ColorMultiplier || a.BaseInputMode!=b.BaseInputMode
            || a.DetailInputMode!=b.DetailInputMode || (a.FacingBase?.Blend??0)!=(b.FacingBase?.Blend??0)) return false;
        if (a.ConsumesBaseTexture ? a.BaseEncoding!=b.BaseEncoding || compareTextureIndices && a.BaseCombinedTextureIndex!=b.BaseCombinedTextureIndex
            : a.BaseImageLinearRgb!=b.BaseImageLinearRgb) return false;
        if (a.ConsumesDetailTexture ? compareTextureIndices && a.DetailCombinedTextureIndex!=b.DetailCombinedTextureIndex : a.DetailBlue!=b.DetailBlue) return false;
        float ah=Math.Clamp(a.ShadowStrength,0,1),bh=Math.Clamp(b.ShadowStrength,0,1);
        if (ah!=bh) return false;
        if (ah==0) return true;
        return a.ModelColor==b.ModelColor && a.ShadowColor==b.ShadowColor && Math.Clamp(a.WarmTint,0,1)==Math.Clamp(b.WarmTint,0,1)
            && a.DirectionalShadow==b.DirectionalShadow && (a.DirectionalShadow is not null
                || a.ShadowInputMode==b.ShadowInputMode && (a.ConsumesShadowTexture
                    ? !compareTextureIndices || a.ShadowCombinedTextureIndex==b.ShadowCombinedTextureIndex : a.ShadowFactor==b.ShadowFactor));
    }

    private static void Input(D3D12EndfieldPaletteInputMode mode,int? index,string modeName,string indexName)
    {
        if (mode is not (D3D12EndfieldPaletteInputMode.Uniform or D3D12EndfieldPaletteInputMode.Texture))
            throw new ArgumentOutOfRangeException(modeName, "Base-image inputs support only uniform or texture data.");
        if (mode==D3D12EndfieldPaletteInputMode.Texture)
        { if(index is null or <0) throw new ArgumentOutOfRangeException(indexName,"Texture mode requires an explicit nonnegative index."); }
        else if(index is not null) throw new ArgumentException("Uniform mode cannot carry a texture index.",indexName);
    }
    private static void Color(Vector3 value,string name)
    { Scalar(value.X,16,name); Scalar(value.Y,16,name); Scalar(value.Z,16,name); }
    private static void Scalar(float value,float bound,string name)
    { if(!float.IsFinite(value)||value < -bound || value > bound) throw new ArgumentOutOfRangeException(name,$"Input must be finite in [-{bound},{bound}]."); }
}
