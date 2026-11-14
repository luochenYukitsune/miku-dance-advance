using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Opt-in scene-light inputs shared by retained captures and native presentation.</summary>
internal static class D3D12SceneLighting
{
    internal static RayGiOptions? ResolveGi(OriginalFrameScene frame, RayGiOptions? options)
    {
        if (!frame.UsesSceneLighting || options is not { Enabled: true, DirectionalIrradiance: null }) return options;
        // The compatibility GI default is a separate artistic irradiance of 3. Scene response instead
        // follows the already sampled actual primary light, so RGB=0 cannot leave a hidden default sun.
        // An explicitly authored GI irradiance remains an explicit, independent lighting input.
        return options with { DirectionalIrradiance = Vector3.Max(frame.Surface.Profile.LightAmbient, Vector3.Zero) };
    }
}
