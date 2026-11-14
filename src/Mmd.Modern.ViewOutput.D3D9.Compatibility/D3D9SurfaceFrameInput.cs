using System.Numerics;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>Reuses the capture encoder with an already evaluated preview sample; never advances a clock.</summary>
internal static class D3D9SurfaceFrameInput
{
    internal static OfflineFrame FromPreview(PreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!double.IsFinite(frame.SourceFrame) || frame.SourceFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(frame), "Source frame must be finite and nonnegative.");
        // Preserve the exact binary double, including fractional and subnormal samples.
        // No output FPS rounding or additional animation/physics step is introduced here.
        var source = ExactTime(frame.SourceFrame);
        return new(new(0, new(0, 1), source, source), frame.Width, frame.Height,
            frame.Scene, frame.Camera, frame.Light, frame.SelfShadow)
        {
            ResolvedCamera = frame.ResolvedCamera, CameraParentTransform = frame.CameraParentTransform,
            MorphChannels = frame.MorphChannels, RenderingStyles = frame.RenderingStyles, SceneInputs = frame.SceneInputs,
            ObjectMaterialInputs = frame.ObjectMaterialInputs,
            Accessories = frame.Accessories, MixedDrawOrder = frame.MixedDrawOrder,
            AccessoryIndexAfterModels = frame.AccessoryIndexAfterModels,
            UnpreparedAccessoryIndices = frame.UnpreparedAccessoryIndices,
            AccessoryParentBoneWorldMatrices = frame.AccessoryParentBoneWorldMatrices
        };
    }

    private static OfflineFrameTime ExactTime(double value)
    {
        if (value == 0) return new(0, 1);
        long bits = BitConverter.DoubleToInt64Bits(value);
        int exponent = (int)((bits >> 52) & 0x7ff);
        var mantissa = new BigInteger(bits & 0x000f_ffff_ffff_ffffL);
        int shift;
        if (exponent == 0) shift = -1074;
        else { mantissa += BigInteger.One << 52; shift = exponent - 1023 - 52; }
        return shift >= 0 ? new(mantissa << shift, 1) : new(mantissa, BigInteger.One << -shift);
    }
}
