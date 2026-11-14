using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Declared numerical data channel; RGBA coverage is never inferred as face lighting.</summary>
public enum D3D12FaceSdfChannel { Red }

/// <summary>The explicitly declared numerical layout, independent of PMX coverage.</summary>
public enum D3D12FaceSdfEncoding { GrayRed, EndfieldRgbCmM, GenshinSoftFace }

/// <summary>Explicit UV0 numerical FaceSDF already included in the composed frame texture table.
/// Head axes are final world directions, not positions. The caller owns compatible face UVs
/// and explicitly declares linear numerical data. GenshinSoftFace uses cubic B-spline
/// repeat over the independent LinearWrap fetch sampler, RGB luminance plus mask G/A;
/// its Transition/Offset mean Softness/ShadowPosition. GrayRed reads only R and
/// requires explicitly compatible gray masks; it never infers another layout.
/// No reference texture, filename classifier or second animation evaluator is supplied.</summary>
public sealed record D3D12FaceSdfBinding(
    int CombinedTextureIndex,
    Vector3 HeadWorldForward,
    Vector3 HeadWorldRight,
    float Transition = 1f,
    float Offset = 1f,
    D3D12FaceSdfChannel Channel = D3D12FaceSdfChannel.Red,
    OriginalTextureSampling Sampling = OriginalTextureSampling.LinearWrap,
    D3D12FaceSdfEncoding Encoding = D3D12FaceSdfEncoding.GrayRed,
    int SecondaryCombinedTextureIndex = -1)
{
    /// <summary>Explicit Genshin soft face shape mapped to the PMX lit/dark palette,
    /// with audited shape defaults. This does not supply the source soft/sharp color palette.</summary>
    public static D3D12FaceSdfBinding GenshinSoft(int combinedTextureIndex, int secondaryCombinedTextureIndex,
        Vector3 headWorldForward, Vector3 headWorldRight, float softness = .001f, float shadowPosition = .55f)
        => new(combinedTextureIndex, headWorldForward, headWorldRight, softness, shadowPosition,
            Encoding: D3D12FaceSdfEncoding.GenshinSoftFace, SecondaryCombinedTextureIndex: secondaryCombinedTextureIndex);

    internal bool HasSecondaryImage => Encoding is D3D12FaceSdfEncoding.EndfieldRgbCmM or D3D12FaceSdfEncoding.GenshinSoftFace;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(CombinedTextureIndex);
        if (!Enum.IsDefined(Encoding))
            throw new ArgumentOutOfRangeException(nameof(Encoding), "Unknown numerical FaceSDF encoding.");
        if (Encoding == D3D12FaceSdfEncoding.GrayRed && SecondaryCombinedTextureIndex != -1)
            throw new ArgumentException("GrayRed FaceSDF has no secondary numerical image.", nameof(SecondaryCombinedTextureIndex));
        if (HasSecondaryImage)
            ArgumentOutOfRangeException.ThrowIfNegative(SecondaryCombinedTextureIndex);
        if (Channel != D3D12FaceSdfChannel.Red)
            throw new NotSupportedException("The legacy channel selector must remain Red; packed layouts are declared by Encoding.");
        if (Sampling != OriginalTextureSampling.LinearWrap)
            throw new NotSupportedException("FaceSDF UV0 requires an independent linear-repeat sampler.");
        if (!float.IsFinite(Transition) || Transition > 100f
            || Transition < (Encoding == D3D12FaceSdfEncoding.GenshinSoftFace ? 0f : .001f))
            throw new ArgumentOutOfRangeException(nameof(Transition), "FaceSDF transition must be finite in [.001,100], or [0,100] for Genshin softness. Star Rail uses .15/transition; Wuthering and Endfield use transition directly.");
        if (!float.IsFinite(Offset) || Offset is < -2f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(Offset), "FaceSDF offset must be finite in [-2,2].");
        _ = OrthonormalBasis();
    }

    internal (Vector3 Forward, Vector3 Right) OrthonormalBasis()
    {
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        if (!Finite(HeadWorldForward) || !Finite(HeadWorldRight)
            || !float.IsFinite(HeadWorldForward.LengthSquared()) || !float.IsFinite(HeadWorldRight.LengthSquared())
            || HeadWorldForward.LengthSquared() < 1e-12f || HeadWorldRight.LengthSquared() < 1e-12f)
            throw new ArgumentException("FaceSDF head directions must be finite and nonzero.");
        var forward = Vector3.Normalize(HeadWorldForward);
        var right = Vector3.Normalize(HeadWorldRight);
        right -= forward * Vector3.Dot(forward, right);
        if (right.LengthSquared() < 1e-8f)
            throw new ArgumentException("FaceSDF head forward and right directions cannot be parallel.");
        return (forward, Vector3.Normalize(right));
    }

    internal static bool ConsumedEquivalent(D3D12FaceSdfBinding? a, D3D12FaceSdfBinding? b, bool compareTextureIndex = true)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Encoding == b.Encoding
            && (!compareTextureIndex || (a.CombinedTextureIndex == b.CombinedTextureIndex
                && a.SecondaryCombinedTextureIndex == b.SecondaryCombinedTextureIndex)) && a.Transition == b.Transition
            && a.Offset == b.Offset && a.Channel == b.Channel && a.Sampling == b.Sampling
            && a.OrthonormalBasis() == b.OrthonormalBasis();
    }
}
