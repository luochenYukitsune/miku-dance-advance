using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D12;
using SkiaSharp;

namespace Mmd.Modern.Smoke;

/// <summary>Independent ModernSoft demonstration; the existing mesh/effect acceptance paths keep their inputs.</summary>
public static class ModernToonSmokeCli
{
    public static int Run(string directory, string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        string path = Path.GetFullPath(modelPath), modelDirectory = Path.GetDirectoryName(path)!;
        string output = Path.GetFullPath(directory);
        Directory.CreateDirectory(output);
        var sources = new List<SourceIdentity>();
        var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textureLoads = new List<object>();
        var loadErrors = new List<string>();
        void Source(string sourcePath, string role)
        {
            var info = new FileInfo(sourcePath);
            sources.Add(new(role, info.FullName, info.Length, Hash(File.ReadAllBytes(sourcePath))));
        }
        Source(path, "user selected PMX, read only; never copied or modified");
        SceneTexture? Load(string relative)
        {
            try
            {
                var texture = SceneTextureLoader.Load(modelDirectory, relative);
                textureLoads.Add(new { relative, status = texture is null ? "missing or unsupported" : "loaded",
                    width = texture?.Width, height = texture?.Height,
                    rgbaSha256 = texture is null ? null : Hash(texture.Rgba) });
                if (texture is not null)
                {
                    string normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    string fullPath = Path.GetFullPath(Path.Combine(modelDirectory, normalized));
                    if (loadedPaths.Add(fullPath)) Source(fullPath, "user image, read only; never copied or modified");
                }
                return texture;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
            {
                loadErrors.Add($"{relative}: {e.Message}");
                textureLoads.Add(new { relative, status = "error", error = e.Message });
                return null;
            }
        }

        PmxModel model;
        using (var input = File.OpenRead(path)) model = PmxReader.Read(input);
        var adapted = PmxSceneAdapter.FromRestPose(model, Load);
        var source = AutoFrame(adapted.Snapshot);
        var originalMaterials = source.Materials.ToArray();
        var materialMapped = SceneAppearance.Apply(source, SceneAppearanceMode.MaterialMapped);
        var modernSoft = SceneAppearance.Apply(source);
        Require(ReferenceEquals(source, materialMapped), "material-mapped must preserve the input snapshot");
        Require(source.Vertices.SequenceEqual(modernSoft.Vertices) && source.Indices.SequenceEqual(modernSoft.Indices)
            && source.World == modernSoft.World && ReferenceEquals(source.Camera, modernSoft.Camera)
            && source.LightDirection == modernSoft.LightDirection, "geometry, normals, mesh UVs, camera, world and light must be identical");
        Require(originalMaterials.SequenceEqual(source.Materials), "source materials must remain unchanged");
        for (int i = 0; i < source.Textures.Count; i++)
            Require(ReferenceEquals(source.Textures[i], modernSoft.Textures[i]), "all original texture objects must remain unchanged");
        for (int i = 0; i < source.Materials.Count; i++)
        {
            var before = source.Materials[i]; var after = modernSoft.Materials[i];
            Require(before == after with { ToonTexture = before.ToonTexture, ToonUvTransform = before.ToonUvTransform },
                "all non-toon material fields must be identical");
            if (before.ToonTexture >= 0 && source.Textures[before.ToonTexture].Kind == SceneTextureKind.Image)
                Require(ReferenceEquals(before, after), "explicit image toon and its UV must be preserved");
        }
        Require(ReferenceEquals(modernSoft, SceneAppearance.Apply(modernSoft)), "appearance must be idempotent");

        var frames = new List<object>();
        using var renderer = new D3D12MeshRenderer();
        RenderedFrame Capture(string name, SceneSnapshot scene)
        {
            var frame = renderer.Render(scene, 961, 1081);
            byte[] bmp = FrameImage.ToBmp(frame);
            File.WriteAllBytes(Path.Combine(output, name + ".bmp"), bmp);
            using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            Marshal.Copy(frame.Rgba, 0, bitmap.GetPixels(), frame.Rgba.Length);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            byte[] pngBytes = png.ToArray();
            File.WriteAllBytes(Path.Combine(output, name + ".png"), pngBytes);
            frames.Add(new { name, frame.Width, frame.Height, frame.Backend, frame.Device, frame.DrawCalls,
                vertices = renderer.LastVertexCount, indices = renderer.LastIndexCount,
                uploadedTextures = renderer.LastUploadedTextures, nonBackgroundPixels = NonBackground(frame),
                rawSha256 = Hash(frame.Rgba), bmpSha256 = Hash(bmp), pngSha256 = Hash(pngBytes),
                debugMessages = renderer.LastDebugMessages });
            Require(renderer.LastDebugMessages.Count == 0, "D3D12 validation messages must be empty");
            return frame;
        }
        var baseline = Capture("material-mapped", materialMapped);
        var soft = Capture("modern-soft", modernSoft);
        int changed = 0, changedMask = 0;
        for (int i = 0; i < baseline.Rgba.Length; i += 4)
        {
            if (!baseline.Rgba.AsSpan(i, 4).SequenceEqual(soft.Rgba.AsSpan(i, 4))) changed++;
            if (IsBackground(baseline.Rgba, i) != IsBackground(soft.Rgba, i)) changedMask++;
        }
        int replacedProcedural = source.Materials.Count(m => m.ToonTexture >= 0
            && source.Textures[m.ToonTexture].Kind == SceneTextureKind.IndexedProceduralToon);
        int replacedMissing = source.Materials.Count(m => m.ToonTexture < 0);
        int preservedImages = source.Materials.Count(m => m.ToonTexture >= 0
            && source.Textures[m.ToonTexture].Kind == SceneTextureKind.Image);
        Require(NonBackground(baseline) > 10000, "selected model must produce visible geometry");
        Require(replacedProcedural + replacedMissing > 0 && changed > 100, "ModernSoft control must visibly change more than 100 pixels");
        Require(changedMask == 0, "background and silhouette mask must be unchanged");
        Require(baseline.DrawCalls == soft.DrawCalls && baseline.DrawCalls == source.Materials.Count(m => m.IndexCount > 0),
            "both styles must draw the same material geometry");
        Require(baseline.Rgba.AsSpan().SequenceEqual(renderer.Render(materialMapped, 961, 1081).Rgba), "same-device material-mapped repeat must be exact");
        Require(soft.Rgba.AsSpan().SequenceEqual(renderer.Render(modernSoft, 961, 1081).Rgba), "same-device modern-soft repeat must be exact");
        using (var fresh = new D3D12MeshRenderer())
        {
            Require(baseline.Rgba.AsSpan().SequenceEqual(fresh.Render(materialMapped, 961, 1081).Rgba), "fresh-device material-mapped repeat must be exact");
            Require(soft.Rgba.AsSpan().SequenceEqual(fresh.Render(modernSoft, 961, 1081).Rgba), "fresh-device modern-soft repeat must be exact");
        }
        foreach (var asset in sources)
            Require(new FileInfo(asset.Path).Length == asset.Bytes && Hash(File.ReadAllBytes(asset.Path)) == asset.Sha256,
                "original model/image identities must remain unchanged");

        byte[] ramp = SceneAppearance.ModernSoftToon.Rgba.ToArray();
        int[] levels = Enumerable.Range(0, 256).Select(i => (int)ramp[i * 4]).ToArray();
        var camera = source.Camera;
        var report = new
        {
            success = true, defaultAppearance = "modern-soft", defaultFrame = "modern-soft.png",
            renderer = "actual D3D12 indexed mesh, depth and diffuse/toon GPU sampling",
            renderer.AdapterName, renderer.IsWarp, featureLevel = renderer.FeatureLevel.ToString(),
            os = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription,
            sources, textureLoads, textureLoadErrors = loadErrors, loadedImageCount = loadedPaths.Count,
            diagnostics = adapted.Diagnostics, frames,
            materials = new { total = source.Materials.Count, replaced = replacedProcedural + replacedMissing,
                replacedProcedural, replacedMissing, preservedImages,
                preservedOther = source.Materials.Count - replacedProcedural - replacedMissing - preservedImages },
            materialMapping = source.Materials.Select((m, i) => new { index = i, m.Name,
                beforeToon = m.ToonTexture, afterToon = modernSoft.Materials[i].ToonTexture,
                beforeKind = m.ToonTexture < 0 ? "Missing" : source.Textures[m.ToonTexture].Kind.ToString(),
                afterKind = modernSoft.Textures[modernSoft.Materials[i].ToonTexture].Kind.ToString(),
                beforeUv = Uv(m.ToonUvTransform), afterUv = Uv(modernSoft.Materials[i].ToonUvTransform) }).ToArray(),
            ramp = new { name = SceneAppearance.ModernSoftToon.Name, formula = ".55 + .45 * t*t*(3-2*t), t=i/255, rounded RGBA8",
                entries = 256, rgbaSha256 = Hash(ramp), darkByte = levels[0], lightByte = levels[^1],
                maxAdjacentByteChange = levels.Zip(levels.Skip(1), (a, b) => b - a).Max(), distinctLevels = levels.Distinct().Count() },
            checks = new { changedStylePixels = changed, changedBackgroundMaskPixels = changedMask,
                sameDeviceMaterialMappedExact = true, sameDeviceModernSoftExact = true,
                freshDeviceMaterialMappedExact = true, freshDeviceModernSoftExact = true,
                geometryCameraWorldLightAndNonToonMaterialFieldsIdentical = true,
                originalTextureObjectsPreserved = true, sourceSnapshotUnchanged = true,
                appearanceIdempotent = true, originalSourceFilesUnchanged = true,
                vertexSha256 = Hash(MemoryMarshal.AsBytes(source.Vertices)), indexSha256 = Hash(MemoryMarshal.AsBytes(source.Indices)) },
            camera = new { eye = V3(camera.Eye), target = V3(camera.Target), up = V3(camera.Up),
                camera.OrthographicHeight, camera.Near, camera.Far },
            lightDirection = V3(source.LightDirection), effectsEnabled = false,
            originalExecutableRequired = false, visibleWindowCreated = false,
            limitations = new[] { "Modern style comparison; changed colours measure style scope, not an original compatibility error rate",
                "Static PMX rest pose; no animation, morphs, physics, sphere maps, edges or shadows",
                "Cutout only; no alpha blending/sorting, MME, RT GI or swapchain",
                "Avalonia Veldrid preview does not yet sample toon; App default is unchanged" }
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "report.json"), json);
        Console.WriteLine(json);
        return 0;
    }

    private sealed record SourceIdentity(string Role, string Path, long Bytes, string Sha256);
    private static SceneSnapshot AutoFrame(SceneSnapshot scene)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (uint index in scene.Indices)
        {
            var position = scene.Vertices[(int)index].Position;
            min = Vector3.Min(min, position); max = Vector3.Max(max, position);
        }
        var center = (min + max) * .5f;
        float span = Math.Max(.1f, Math.Max(max.Y - min.Y, Math.Max(max.X - min.X, max.Z - min.Z)));
        var camera = new SceneCamera(center + new Vector3(0, 0, -span * 2), center, Vector3.UnitY, span * 1.2f, .01f, span * 5);
        return scene.WithCamera(camera);
    }
    private static float[] V3(Vector3 v) => [v.X, v.Y, v.Z];
    private static float[]? Uv(Vector4? v) => v is { } uv ? [uv.X, uv.Y, uv.Z, uv.W] : null;
    private static bool IsBackground(byte[] rgba, int offset) => rgba[offset] == 20 && rgba[offset + 1] == 24 && rgba[offset + 2] == 32 && rgba[offset + 3] == 255;
    private static int NonBackground(RenderedFrame frame)
    {
        int count = 0;
        for (int i = 0; i < frame.Rgba.Length; i += 4) if (!IsBackground(frame.Rgba, i)) count++;
        return count;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
