using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>MDA 自有的风格化明暗参数，不需要额外贴图。
/// 阴影色由材质底色自身的色相加深，交界处可加暖色带，受光侧可加硬边缘光，最后做饱和／对比／亮度调色。
/// 这是独立实现的近似，不包含任何游戏或插件的节点、参数表或贴图。</summary>
public sealed record D3D12StylizedShading(
    float ShadowStrength, float ShadowValue, float ShadowHue, Vector3 ShadowTint, float FaceFlatten,
    Vector3 WarmColor, float WarmStrength, float RimEdge, float RimStrength,
    float Saturation, float Contrast, float Brightness,
    float OutlineValue = .5f, float OutlineSaturation = 1.3f)
{
    /// <summary>x 阴影强度（0 关闭），y 阴影明度，z 底色色相加深，w 脸部压平。</summary>
    internal Vector4 Shade => new(ShadowStrength, ShadowValue, ShadowHue, FaceFlatten);
    /// <summary>xyz 阴影色调，w 交界暖带强度。</summary>
    internal Vector4 Tint => new(ShadowTint, WarmStrength);
    /// <summary>xyz 交界暖带颜色，w 边缘光起点（菲涅尔阈值）。</summary>
    internal Vector4 Warm => new(WarmColor, RimEdge);
    /// <summary>x 饱和，y 对比，z 亮度，w 边缘光强度。</summary>
    internal Vector4 Grade => new(Saturation, Contrast, Brightness, RimStrength);

    public void Validate()
    {
        static void Range(float value, float min, float max, string name)
        {
            if (!float.IsFinite(value) || value < min || value > max)
                throw new ArgumentOutOfRangeException(name, $"Stylized shading {name} must be finite and in [{min},{max}].");
        }
        Range(ShadowStrength, 0, 1, nameof(ShadowStrength)); Range(ShadowValue, 0, 1, nameof(ShadowValue));
        Range(ShadowHue, 0, 1, nameof(ShadowHue)); Range(FaceFlatten, 0, 1, nameof(FaceFlatten));
        Range(ShadowTint.X, 0, 1, nameof(ShadowTint)); Range(ShadowTint.Y, 0, 1, nameof(ShadowTint)); Range(ShadowTint.Z, 0, 1, nameof(ShadowTint));
        Range(WarmColor.X, 0, 1, nameof(WarmColor)); Range(WarmColor.Y, 0, 1, nameof(WarmColor)); Range(WarmColor.Z, 0, 1, nameof(WarmColor));
        Range(WarmStrength, 0, 1, nameof(WarmStrength)); Range(RimEdge, 0, 1, nameof(RimEdge)); Range(RimStrength, 0, 1, nameof(RimStrength));
        Range(OutlineValue, .05f, 1, nameof(OutlineValue)); Range(OutlineSaturation, 0, 2, nameof(OutlineSaturation));
        Range(Saturation, 0, 2, nameof(Saturation)); Range(Contrast, -.5f, .5f, nameof(Contrast)); Range(Brightness, .5f, 2, nameof(Brightness));
    }
}

/// <summary>按画风家族和材质角色给出默认的风格化明暗。MMD 返回 null。</summary>
public static class D3D12StylizedShadingCatalog
{
    private static D3D12StylizedShading Body(D3D12SurfaceStyle family) => family switch
    {
        // 原神：中等深度、偏暖琥珀的阴影，几乎无暖边，柔和提亮。
        D3D12SurfaceStyle.GenshinPmx => new(1, .76f, .5f, new(1f, .9f, .84f), 0,
            new(1f, .86f, .72f), .15f, .62f, .2f, 1.08f, .04f, 1.06f, .5f, 1.3f),
        // 星铁：最深、偏冷的阴影，无暖边，最鲜艳、对比最高。
        D3D12SurfaceStyle.StarRailPmx => new(1, .62f, .8f, new(.84f, .87f, 1f), 0,
            new(1f, 1f, 1f), 0, .58f, .32f, 1.2f, .2f, 1f, .38f, 1.5f),
        // 鸣潮：较深的紫灰阴影，明显的暖橙交界带，对比略高。
        D3D12SurfaceStyle.WutheringPmx => new(1, .64f, .6f, new(.9f, .84f, 1f), 0,
            new(1f, .8f, .6f), .6f, .66f, .16f, 1.12f, .14f, 1.03f, .42f, 1.25f),
        // 终末地：浅而平的阴影，暖边，整体偏亮偏平。
        D3D12SurfaceStyle.EndfieldPmx => new(1, .84f, .4f, new(1f, .96f, .94f), 0,
            new(1f, .88f, .74f), .5f, .56f, .26f, 1f, 0, 1.12f, .6f, 1.1f),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    public static D3D12StylizedShading? Resolve(D3D12SurfaceStyle family, D3D12CharacterMaterialRole role)
    {
        if (family is not (D3D12SurfaceStyle.GenshinPmx or D3D12SurfaceStyle.StarRailPmx
            or D3D12SurfaceStyle.WutheringPmx or D3D12SurfaceStyle.EndfieldPmx)) return null;
        var body = Body(family);
        return role switch
        {
            D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair => body,
            // 脸：三渲二的脸几乎不吃法线阴影，只保留很弱的形体；不加边缘光，暖边减半。
            D3D12CharacterMaterialRole.Face => body with { FaceFlatten = .85f, RimStrength = 0, WarmStrength = body.WarmStrength * .5f },
            // 眼：只调色，不改明暗。
            D3D12CharacterMaterialRole.Eye => body with { ShadowStrength = 0, RimStrength = 0, WarmStrength = 0 },
            _ => null,
        };
    }
}
