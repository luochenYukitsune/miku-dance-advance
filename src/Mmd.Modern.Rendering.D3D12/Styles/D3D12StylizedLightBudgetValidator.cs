using System.Numerics;
using System.Runtime.CompilerServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Bounds only inputs newly consumed by the four opt-in families' shared diffuse budget.
/// Original MMD, standalone GI, additive decoration and authored specular retain their existing domains.</summary>
internal static class D3D12StylizedLightBudgetValidator
{
    internal static void Validate(OriginalFrameScene frame, D3D12SurfaceStyleMap styles, RayGiOptions? gi)
    {
        if (styles.IsDefault || !D3D12OriginalStyleRenderer.HasNativeLights(frame)) return;
        bool activeGi = gi?.Enabled == true;
        // The Additive entry deliberately keeps the accepted independent composition.
        if (activeGi && gi!.Composition == RayGiComposition.Additive) return;
        for (int index = 0; index < styles.Count; index++)
        {
            if (styles.Materials[index] == D3D12SurfaceStyle.Mmd || !frame.Materials[index].SurfaceEnabled
                || frame.Surface.Snapshot.Materials[index].IndexCount == 0) continue;
            var response = frame.Materials[index].GiToonResponseOverride ?? gi?.ToonResponse;
            float giMaximum = activeGi && gi!.Strength > 0 ? response == RayGiToonResponse.PreservePaletteBands ? .25f : 1 : 0;
            new MaterialDomain(index).Validate(frame, giMaximum, styles.EffectivePmxMaterialTreatment(index));
        }
    }

    private readonly record struct ColorRange(Vector3 Min, Vector3 Max)
    {
        internal static ColorRange Constant(Vector3 value) => new(value, value);
    }

    private sealed class MaterialDomain(int index)
    {
        private NotSupportedException Unsupported() => new($"Stylized native light budget requires finite nonnegative diffuse endpoints, "
            + $"effective toon RGB in [0,1], and finite light products and sums (material {index}).");

        private float Checked(float value) => float.IsFinite(value) ? value : throw Unsupported();

        internal void Validate(OriginalFrameScene frame, float giMaximum, D3D12PmxMaterialTreatment? treatment)
        {
            var style = frame.Surface;
            var material = style.Snapshot.Materials[index];
            var binding = style.Materials[index];
            var host = OriginalStyleMath.ResolveHostColors(material, style.Profile).EgColor;
            var eg = new Vector3(host.X, host.Y, host.Z);
            var toon = D3D12OriginalStyleRenderer.ResolveTextureMorphToon(binding);
            for (int channel = 0; channel < 3; channel++)
                if (Checked(eg[channel]) < 0 || Checked(toon[channel]) is < 0 or > 1) throw Unsupported();

            // An absent diffuse map binds white; MorphDiffuse explicitly bypasses its
            // factors for that slot. Sphere=None never consumes its stored factors.
            var diffuse = material.DiffuseTexture < 0 ? ColorRange.Constant(Vector3.One) : FactorRange(binding.TextureFactors.Diffuse);
            var endpoint = Product(ColorRange.Constant(eg), diffuse);
            var native = Product(ColorRange.Constant(new(material.Diffuse.X, material.Diffuse.Y, material.Diffuse.Z)), diffuse);
            if (treatment is { Saturation: not 1 })
            {
                endpoint = SaturatedRange(endpoint);
                native = SaturatedRange(native);
            }
            if (treatment is { ValueContrast: < 0 })
            {
                // For c<0 and V>=channel, channel*(1-c+c*V) is bounded
                // by channel*(1-c+c*channel). This monotone SDR curve retains
                // exact zero bounds and avoids multiplying untouched HDR ranges.
                endpoint = BrightenedRange(endpoint, treatment.ValueContrast);
                native = BrightenedRange(native, treatment.ValueContrast);
            }
            bool additiveSphere = binding.SphereMode == OriginalSphereMode.Add
                || binding.SphereMode == OriginalSphereMode.SubTexture && binding.SubTextureAdditive && material.DiffuseTexture >= 0;
            bool multiplySphere = binding.SphereMode == OriginalSphereMode.Multiply
                || binding.SphereMode == OriginalSphereMode.SubTexture && !additiveSphere;
            if (multiplySphere)
            {
                var sphere = FactorRange(binding.TextureFactors.Sphere);
                endpoint = Product(endpoint, sphere);
                native = Product(native, sphere);
            }
            for (int channel = 0; channel < 3; channel++)
                if (endpoint.Min[channel] < 0 || native.Min[channel] < 0) throw Unsupported();

            Vector3 total = Vector3.Zero;
            foreach (var light in frame.Lighting!.Lights)
            {
                if (!light.Enabled || light.Intensity == 0 || light.Color == Vector3.Zero) continue;
                // NativeDirect evaluates this scalar independently of colour and diffuse.
                // Its minimum squared distance is .0001; angular response/visibility and
                // remaining palette each lie in [0,1]. A zero budget cannot hide Inf*0.
                float intensity = Checked(ScalarMultiply(light.Intensity, light.Kind == SceneLightKind.Directional ? 1 : 10000));
                for (int channel = 0; channel < 3; channel++)
                {
                    // Match the colour-before-intensity grouping, so a tiny light colour
                    // and huge finite directional intensity remain a supported pair.
                    float colored = Checked(ScalarMultiply(native.Max[channel], light.Color[channel]));
                    float increment = Checked(ScalarMultiply(colored, intensity));
                    total[channel] = Checked(ScalarAdd(total[channel], increment));
                }
            }
            if (giMaximum > 0)
                for (int channel = 0; channel < 3; channel++)
                {
                    float increment = Checked(ScalarMultiply(endpoint.Max[channel], giMaximum));
                    _ = Checked(ScalarAdd(total[channel], increment));
                }
        }

        // HSV preserves V but can raise a formerly small channel. Bound it before
        // sphere/light products; retain signed values because the shader explicitly
        // passes them through and a later signed sphere may change their sign.
        private static ColorRange BrightenedRange(ColorRange source, float contrast)
        {
            var maximum = source.Max;
            for (int channel = 0; channel < 3; channel++)
                if (maximum[channel] is > 0 and < 1)
                    maximum[channel] *= (1 - contrast) + contrast * maximum[channel];
            return new(source.Min, maximum);
        }

        private static ColorRange SaturatedRange(ColorRange source)
        {
            float maximum = MathF.Max(0, MathF.Max(source.Max.X, MathF.Max(source.Max.Y, source.Max.Z)));
            return new(Vector3.Min(source.Min, Vector3.Zero), new Vector3(maximum));
        }

        // The texture is linear UNorm. Checking both ends of its affine colour
        // transform bounds every sampled value without scanning immutable assets.
        // Separate operations also reject overflow later masked by a zero weight.
        private ColorRange FactorRange(OriginalTextureFactor factor)
        {
            float weight = Checked(ScalarAdd(factor.Multiply.W, factor.Add.W));
            float fill = Checked(ScalarAdd(1, -weight));
            Vector3 min = default, max = default;
            for (int channel = 0; channel < 3; channel++)
            {
                float At(float sample)
                {
                    float scaled = Checked(ScalarMultiply(sample, factor.Multiply[channel]));
                    float biased = Checked(ScalarAdd(scaled, factor.Add[channel]));
                    float weighted = Checked(ScalarMultiply(biased, weight));
                    return Checked(ScalarAdd(weighted, fill));
                }
                float a = At(0), b = At(1);
                min[channel] = MathF.Min(a, b); max[channel] = MathF.Max(a, b);
            }
            return new(min, max);
        }

        private ColorRange Product(ColorRange a, ColorRange b)
        {
            Vector3 min = default, max = default;
            for (int channel = 0; channel < 3; channel++)
            {
                float p0 = Checked(ScalarMultiply(a.Min[channel], b.Min[channel]));
                float p1 = Checked(ScalarMultiply(a.Min[channel], b.Max[channel]));
                float p2 = Checked(ScalarMultiply(a.Max[channel], b.Min[channel]));
                float p3 = Checked(ScalarMultiply(a.Max[channel], b.Max[channel]));
                min[channel] = MathF.Min(MathF.Min(p0, p1), MathF.Min(p2, p3));
                max[channel] = MathF.Max(MathF.Max(p0, p1), MathF.Max(p2, p3));
            }
            return new(min, max);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static float ScalarMultiply(float a, float b) => a * b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float ScalarAdd(float a, float b) => a + b;
}
