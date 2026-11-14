using System.Numerics;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput.D3D12;

internal static class D3D12ViewScene
{
    private static readonly ConditionalWeakTable<SceneSnapshot, ResolvedLightingCache> LightingCache = new();
    private sealed class ResolvedLightingCache
    {
        private sealed record Entry(OriginalStyleScene Source, PmmLightPose Light, OriginalStyleScene Resolved);
        private readonly object gate = new();
        private Entry? last;

        internal OriginalStyleScene Get(OriginalStyleScene source, PmmLightPose light)
        {
            lock (gate)
            {
                if (last is { } entry && SameBits(entry.Light.Color, light.Color) && SameBits(entry.Light.Direction, light.Direction)
                    && SameStyle(entry.Source, source)) return entry.Resolved;
                // Reuse only this last immutable result. A metadata wrapper otherwise gives GI a
                // fresh snapshot identity and rebuilds unchanged transport on every presentation.
                var resolved = new OriginalStyleScene(source.Snapshot.WithLighting(-light.Direction), source.Materials,
                    source.Profile with { LightAmbient = light.Color, LightSpecular = light.Color });
                last = new(source, light, resolved);
                return resolved;
            }
        }

        private static bool SameStyle(OriginalStyleScene a, OriginalStyleScene b)
        {
            if (ReferenceEquals(a, b)) return true;
            var ap = a.Profile; var bp = b.Profile;
            if (!ReferenceEquals(ap, bp) && (ap.TextureSampling != bp.TextureSampling
                || !SameBits(ap.LightAmbient, bp.LightAmbient) || !SameBits(ap.LightSpecular, bp.LightSpecular)
                || !SameBits(ap.SphereRotation, bp.SphereRotation))) return false;
            if (a.Materials.Count != b.Materials.Count) return false;
            for (int i = 0; i < a.Materials.Count; i++)
            {
                var am = a.Materials[i]; var bm = b.Materials[i];
                if (ReferenceEquals(am, bm)) continue;
                if (am.SphereTexture != bm.SphereTexture || am.SphereMode != bm.SphereMode || am.DoubleSided != bm.DoubleSided
                    || am.SubTextureAdditive != bm.SubTextureAdditive || !SameBits(am.ToonShadowColor, bm.ToonShadowColor)
                    || !SameFactor(am.TextureFactors.Diffuse, bm.TextureFactors.Diffuse)
                    || !SameFactor(am.TextureFactors.Sphere, bm.TextureFactors.Sphere)
                    || !SameFactor(am.TextureFactors.Toon, bm.TextureFactors.Toon)) return false;
            }
            return true;
        }

        private static bool SameFactor(OriginalTextureFactor a, OriginalTextureFactor b)
            => SameBits(a.Multiply, b.Multiply) && SameBits(a.Add, b.Add);
        private static bool SameBits(float a, float b)
            => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        private static bool SameBits(Vector3 a, Vector3 b)
            => SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z);
        private static bool SameBits(Vector4 a, Vector4 b)
            => SameBits(a.X, b.X) && SameBits(a.Y, b.Y) && SameBits(a.Z, b.Z) && SameBits(a.W, b.W);
        private static bool SameBits(Matrix4x4 a, Matrix4x4 b)
            => SameBits(a.M11, b.M11) && SameBits(a.M12, b.M12) && SameBits(a.M13, b.M13) && SameBits(a.M14, b.M14)
                && SameBits(a.M21, b.M21) && SameBits(a.M22, b.M22) && SameBits(a.M23, b.M23) && SameBits(a.M24, b.M24)
                && SameBits(a.M31, b.M31) && SameBits(a.M32, b.M32) && SameBits(a.M33, b.M33) && SameBits(a.M34, b.M34)
                && SameBits(a.M41, b.M41) && SameBits(a.M42, b.M42) && SameBits(a.M43, b.M43) && SameBits(a.M44, b.M44);
    }

    public static OriginalFrameScene Resolve(OriginalFrameScene scene, PmmCameraPose? cameraPose, PmmLightPose? lightPose,
        VmdSelfShadowPose? shadowPose, IReadOnlyDictionary<Guid, CharacterMorphChannels> morphs, ViewQualityOptions quality,
        SceneCamera? resolvedCamera = null, Matrix4x4? cameraParentTransform = null)
    {
        // Evaluated material factors and additional UVs are canonical on the composed surface.
        // Actor-local auxiliary dictionaries cannot safely address combined vertex indices.
        var surface = scene.Surface;
        var options = quality.ApplyTo(scene.Options);
        var outline = scene.OutlineOptions;
        if (resolvedCamera is not null || cameraPose is not null)
        {
            if (resolvedCamera is null && cameraPose is { ParentModelIndex: >= 0 })
                throw new NotSupportedException("A parented camera requires a resolved parent transform before rendering.");
            var camera = resolvedCamera ?? cameraPose!.Value.CreateCamera(cameraParentTransform);
            var rotation = camera.LeftHanded ? Matrix4x4.CreateLookAtLeftHanded(camera.Eye, camera.Target, camera.Up) : Matrix4x4.CreateLookAt(camera.Eye, camera.Target, camera.Up);
            rotation.M41 = rotation.M42 = rotation.M43 = 0;
            options = options with { CameraOverride = camera, SphereRotationOverride = rotation };
            if (outline is not null) outline = outline with { CameraPosition = camera.Eye, FovDegrees = camera.VerticalFovDegrees ?? cameraPose?.Fov ?? outline.FovDegrees };
        }
        if (lightPose is { } light)
            surface = LightingCache.GetValue(surface.Snapshot, static _ => new()).Get(surface, light);
        bool sceneShadow = scene.UsesSceneLighting && !surface.Snapshot.IsEmpty
            && scene.Materials.Any(flags => flags.UseSceneLighting && flags.ReceiveSelfShadow);
        if (shadowPose is { Mode: 0 }) options = options with { SelfShadow = null };
        else if (sceneShadow)
        {
            // Resolve after the sampled main light above. Fit the complete composed world geometry,
            // including camera-invisible walls, rather than using the original camera-projective warp.
            // An explicitly disabled existing generator stays disabled.
            if (options.SelfShadow is not { Generate: false })
            {
                var selected = options.SelfShadow ?? new OriginalSelfShadowOptions(Matrix4x4.Identity, 2048, 2048,
                    true, false, OriginalTextureSampling.LinearClamp, OriginalDepthComparison.LessEqual);
                options = options with { SelfShadow = selected with {
                    LightViewProjection = ModernDirectionalShadowProjection.Fit(surface.Snapshot), Generate = true, PartialFar = false } };
            }
        }
        else if (shadowPose is { } shadow)
        {
            if (cameraPose is not { } shadowCamera || lightPose is not { } shadowLight)
                throw new NotSupportedException("Animated original self-shadow requires sampled original camera and raw light fields; a resolved world-space eye alone is insufficient.");
            if (resolvedCamera is not null && (shadowCamera.ParentModelIndex < 0 || cameraParentTransform is not null)
                && resolvedCamera != shadowCamera.CreateCamera(cameraParentTransform))
                throw new NotSupportedException("Resolved camera differs from the sampled original camera. Supply a validated explicit projection instead of guessing its original shadow space.");
            options = options with { SelfShadow = PmmOriginalSelfShadowAdapter.Create(shadowCamera, shadowLight,
                shadow, surface.Snapshot.World, options.SelfShadow, cameraParentTransform) };
        }
        return new(surface, scene.Outline, outline, options, scene.Materials, scene.Lighting, scene.EditorOverlay);
    }
}
