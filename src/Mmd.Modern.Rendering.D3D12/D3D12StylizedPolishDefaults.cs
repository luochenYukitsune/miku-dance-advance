using System.Numerics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Default polished stylized-rendering parameters applied automatically when modern
/// character styles are active, while keeping baseline "mmd" and Classic mode unchanged.</summary>
public static class D3D12StylizedPolishDefaults
{
    public const float DefaultCastShadowStrength = 0.75f;
    public const float DefaultFaceLightStrength = 0.65f;

    public static D3D12DepthRimOptions DepthRim { get; } = new(
        Enabled: true,
        Strength: 0.25f,
        WidthPixels: 1.45f,
        DepthThreshold: 0.12f,
        LightBias: 0.70f,
        Color: new Vector3(1f, 0.96f, 0.90f));

    public static D3D12BloomOptions Bloom { get; } = new(
        Enabled: true,
        Threshold: 0.88f,
        SoftKnee: 0.16f,
        Intensity: 0.24f,
        Scatter: 0.65f);

    public static D3D12ToneCurveOptions ToneCurve { get; } = new(
        Enabled: true,
        Contrast: 0.08f,
        Shoulder: 0.15f,
        Saturation: 1.06f);
}
