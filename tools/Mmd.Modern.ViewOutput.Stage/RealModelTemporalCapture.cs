using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.Smoke;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.Images;

/// <summary>Read-only PMX evidence harness, separate from the product and the static output stage.</summary>
internal static class RealModelTemporalCapture
{
    public static async Task<object> RunAsync(string modelPath, string output, D3D12TemporalUpscalingMode mode = D3D12TemporalUpscalingMode.Fsr)
    {
        const int width = 1920, height = 1080, count = 8;
        if (mode is not (D3D12TemporalUpscalingMode.Fsr or D3D12TemporalUpscalingMode.XeSs))
            throw new ArgumentOutOfRangeException(nameof(mode));
        bool xeSs = mode == D3D12TemporalUpscalingMode.XeSs;
        string providerFile = xeSs ? "xess" : "fsr";
        string runtimeVariable = xeSs ? "MMD_XESS_RUNTIME_PATH" : "MMD_FSR_LOADER_PATH";
        string path = Path.GetFullPath(modelPath), root = Path.GetDirectoryName(path)!;
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = Hash(path) };
        var textureLoads = new List<object>();
        SceneTexture? Load(string relative)
        {
            var image = SceneTextureLoader.Load(root, relative);
            textureLoads.Add(new { relative, loaded = image is not null, width = image?.Width, height = image?.Height });
            if (image is not null)
            {
                string texturePath = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
                sources[texturePath] = Hash(texturePath);
            }
            return image;
        }
        var model = PmxReader.Read(path);
        if (model.Materials.Any(material => material.SphereMode == PmxSphereMode.SubTexture))
            throw new NotSupportedException("This stage does not implement additional-UV sphere subtextures.");
        var adapted = PmxOriginalStyleAdapter.FromRestPose(model, Load);
        var snapshot = adapted.Scene.Snapshot;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (uint index in snapshot.Indices)
        {
            var point = snapshot.Vertices[(int)index].Position;
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        var center = (min + max) * .5f;
        float span = Math.Max(.1f, Math.Max(max.Y - min.Y, Math.Max((max.X - min.X) * height / width, max.Z - min.Z)));
        var camera = new SceneCamera(center + new Vector3(0, 0, -span * 2.2f), center, Vector3.UnitY,
            span * 1.2f, .01f, span * 6, VerticalFovDegrees: 30);
        var outline = PmxOriginalOutlineAdapter.FromRestPose(model);
        var materialFlags = PmxOriginalFrameAdapter.FromMaterials(model);
        var vertices = snapshot.Vertices.ToArray(); var indices = snapshot.Indices.ToArray();
        OriginalFrameScene Frame(int index)
        {
            var world = Matrix4x4.CreateTranslation((index / (float)(count - 1) - .5f) * span * .08f, 0, 0);
            var current = new SceneSnapshot(vertices, indices, snapshot.Materials, snapshot.Textures,
                world, camera, snapshot.LightDirection);
            return new(new(current, adapted.Scene.Materials, adapted.Scene.Profile), outline,
                new(new(center, 1), world, camera.Eye, 30, 1),
                new(OriginalAlphaBlendMode.Normal, true, false, CameraOverride: camera,
                    BackgroundColor: new Vector4(.075f, .085f, .105f, 1)), materialFlags);
        }
        var options = new D3D12TemporalUpscalingOptions
        {
            Mode = mode,
            QualityPreset = D3D12FsrQualityPreset.Quality,
            XeSsQuality = D3D12XeSsQuality.Quality,
            FrameTimeMilliseconds = 1000f / 30,
            LoaderPath = Environment.GetEnvironmentVariable(runtimeVariable)
                ?? throw new InvalidOperationException($"Set {runtimeVariable} to the explicitly installed official SDK runtime.")
        };
        var frames = new List<object>(); var captures = new List<object>();
        var comparisons = new List<object>();
        using var temporal = new D3D12FrameSession();
        using var native = new D3D12FrameSession();
        Require(!temporal.IsWarp && !native.IsWarp, "Real-model temporal evidence requires actual GPU hardware.");
        byte[]? firstTemporal = null, lastTemporal = null;
        for (int index = 0; index < count; index++)
        {
            var frame = Frame(index);
            bool capture = index == 0 || index == count - 1;
            if (capture)
            {
                var pixels = temporal.Capture(frame, width, height, rayGiOptions: RayGiOptions.Off, temporalUpscaling: options);
                string file = $"real-pmx-{providerFile}-temporal-{index:D2}.png";
                await SaveAsync(file, pixels);
                captures.Add(new { file, index, rgbaSha256 = HashPixels(pixels.Rgba), temporal.Statistics });
                if (index == 0) firstTemporal = pixels.Rgba; else lastTemporal = pixels.Rgba;
                var baseline = native.Capture(frame, width, height, rayGiOptions: RayGiOptions.Off);
                Require(ForegroundCount(baseline.Rgba) > 10000, "The real-model native frame contains insufficient visible geometry.");
                string nativeFile = $"real-pmx-native-{index:D2}.png";
                await SaveAsync(nativeFile, baseline);
                captures.Add(new { file = nativeFile, index, rgbaSha256 = HashPixels(baseline.Rgba), native.Statistics });
                comparisons.Add(new { index, nativeFile, temporalFile = file,
                    nativeForegroundPixels = ForegroundCount(baseline.Rgba),
                    temporalForegroundPixels = ForegroundCount(pixels.Rgba),
                    rgbMeanAbsoluteDifference = MeanDifference(baseline.Rgba, pixels.Rgba) });
            }
            else
            {
                using var gpu = temporal.Render(frame, width, height, rayGiOptions: RayGiOptions.Off, temporalUpscaling: options);
                Require(gpu.Statistics.ReadbackCount == 0 && gpu.Statistics.SubmissionCount == 1,
                    "Ordinary temporal frames must remain on the GPU with one submission.");
            }
            var statistics = temporal.Statistics!;
            var state = statistics.TemporalUpscaling ?? throw new InvalidOperationException("No temporal SDK execution was reported.");
            Require(state.Mode == mode && !string.IsNullOrWhiteSpace(state.ProviderName), "Missing selected native SDK execution.");
            if (xeSs)
            {
                var query = state.XeSsDeviceQuery ?? throw new InvalidOperationException("Missing actual XeSS device query.");
                Require(query.Initialized && query.InitializeCode == 0 && !string.IsNullOrWhiteSpace(query.RuntimeVersion),
                    "The XeSS runtime did not report successful actual initialization.");
                Require(state.RenderWidth == query.InputWidth && state.RenderHeight == query.InputHeight,
                    "XeSS input dimensions differ from the actual SDK query.");
                Require(state.RenderWidth is > 0 and < width && state.RenderHeight is > 0 and < height,
                    "XeSS Quality did not report a smaller positive input extent.");
            }
            else
            {
                Require(state.ProviderId != 0, "Missing actual FSR provider identifier.");
                Require(state.RenderWidth == 1280 && state.RenderHeight == 720,
                    "FSR Quality did not execute at the expected separate input size.");
            }
            Require(state.ResetHistory == (index == 0), "Continuous world motion unexpectedly reset the temporal history.");
            Require(state.DisplayWidth == width && state.DisplayHeight == height,
                "Temporal reconstruction did not produce the configured output size.");
            Require(state.SubmittedFrames == index + 1, "The retained session did not advance after submission.");
            frames.Add(new { index, worldTranslationX = (index / (float)(count - 1) - .5f) * span * .08f,
                state.Mode, state.ProviderName, state.ProviderId, state.XeSsDeviceQuery,
                state.RenderWidth, state.RenderHeight, state.DisplayWidth, state.DisplayHeight,
                state.ResetHistory, state.ResetReason, state.SubmittedFrames,
                state.JitterPhase, jitter = new { state.JitterPixels.X, state.JitterPixels.Y },
                statistics.ReadbackCount, statistics.TotalReadbacks, statistics.PendingFrames,
                statistics.LiveFrameResources, statistics.CachedResources, statistics.RecordCpuMilliseconds });
        }
        Require(temporal.Statistics!.TotalReadbacks == 2, "Only the two explicitly captured temporal frames may read back.");
        Require(firstTemporal is not null && lastTemporal is not null && !firstTemporal.AsSpan().SequenceEqual(lastTemporal),
            "The moving temporal sequence produced identical endpoint images.");
        Require(sources.All(source => Hash(source.Key) == source.Value), "A read-only model or texture changed during capture.");
        temporal.WaitForIdle(); native.WaitForIdle();
        return new { model = path, model.Name, vertices = model.Vertices.Count, materials = model.Materials.Count,
            width, height, sequenceFrames = count, fps = 30, temporal.AdapterName, temporal.IsWarp,
            temporal.DebugRequested, temporal.DebugLayerEnabled, temporal.DebugInfoQueueAvailable,
            camera = new { eye = new { camera.Eye.X, camera.Eye.Y, camera.Eye.Z }, camera.VerticalFovDegrees, camera.Near, camera.Far },
            nativeSdk = xeSs
                ? "Intel XeSS SDK bundle 3.0.2; actual SR runtime version queried from libxess.dll in per-frame diagnostics (observed separately as 2.0.2 on RTX2080)"
                : "FidelityFX SDK 2.3 optional runtime; actual provider in per-frame diagnostics",
            sdkBundleVersion = xeSs ? "3.0.2" : "2.3.0",
            options.Mode, options.QualityPreset, options.XeSsQuality, options.FrameTimeMilliseconds, frames, captures, comparisons,
            endpointRgbMeanAbsoluteDifference = MeanDifference(firstTemporal!, lastTemporal!),
            sourceAssetsUnchanged = true, sources, textureLoads, adapted.Diagnostics,
            scope = $"Real PMX rest geometry/materials/toon/outline, eight continuous world-translation frames through retained D3D12FrameSession; perspective 1080p native and {mode} endpoint captures; middle six GPU-only. No VMD/bone/physics animation or original-MMD whole-frame equivalence claim. Common-toon fallback remains explicitly reported. GI and post effects are off." };

        async Task SaveAsync(string file, RenderedFrame pixels)
        {
            using var lease = new FrameBufferLease(width, height, width * 4, pixels.Rgba);
            var plan = new OfflineFramePlan(width, height, new(30), 0, 0);
            await using var sink = new PngFrameSink(Path.Combine(output, file));
            await sink.BeginAsync(new(plan)); await sink.WriteAsync(plan.GetSample(0), lease); await sink.CompleteAsync();
        }
    }
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static string HashPixels(byte[] rgba) => Convert.ToHexString(SHA256.HashData(rgba));
    private static int ForegroundCount(byte[] rgba)
    {
        int count = 0;
        for (int i = 0; i < rgba.Length; i += 4)
            if (Math.Abs(rgba[i] - 19) + Math.Abs(rgba[i + 1] - 22) + Math.Abs(rgba[i + 2] - 27) > 24) count++;
        return count;
    }
    private static double MeanDifference(byte[] a, byte[] b)
    {
        Require(a.Length == b.Length, "Image dimensions differ.");
        long total = 0;
        for (int index = 0; index < a.Length; index++) if (index % 4 != 3) total += Math.Abs(a[index] - b[index]);
        return total / (a.Length / 4d * 3);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
