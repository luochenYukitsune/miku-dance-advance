using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.Smoke;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Mmd.Modern.ViewOutput.FFmpeg;
using Mmd.Modern.ViewOutput.Images;

internal static class RealModelCapture
{
    public static async Task<object> RunAsync(string modelPath, string output, FfmpegToolchain? tools, bool compareFsr1 = false)
    {
        string path = Path.GetFullPath(modelPath), root = Path.GetDirectoryName(path)!;
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = Hash(path) };
        var textureLoads = new List<object>();
        SceneTexture? Load(string relative)
        {
            var image = SceneTextureLoader.Load(root, relative);
            textureLoads.Add(new { relative, loaded = image is not null, width = image?.Width, height = image?.Height });
            if (image is not null) sources[Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)))] = Hash(Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar))));
            return image;
        }
        var model = PmxReader.Read(path);
        if (model.Materials.Any(material => material.SphereMode == PmxSphereMode.SubTexture))
            throw new NotSupportedException("The real-PMX static stage does not implement additional-UV sphere subtextures.");
        var adapted = PmxOriginalStyleAdapter.FromRestPose(model, Load);
        var snapshot = adapted.Scene.Snapshot;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (uint index in snapshot.Indices) { var point = snapshot.Vertices[(int)index].Position; min = Vector3.Min(min, point); max = Vector3.Max(max, point); }
        var center = (min + max) * .5f; float span = Math.Max(.1f, Math.Max(max.Y - min.Y, Math.Max(max.X - min.X, max.Z - min.Z)));
        var camera = new SceneCamera(center + new Vector3(0,0,-span*2), center, Vector3.UnitY, span*1.2f, .01f, span*5);
        var surface = new OriginalStyleScene(snapshot.WithCamera(camera), adapted.Scene.Materials, adapted.Scene.Profile);
        var scene = new OriginalFrameScene(surface, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(new(center,1), Matrix4x4.Identity, camera.Eye, 30, 1, false), materials: PmxOriginalFrameAdapter.FromMaterials(model));
        if (model.Bones.Count == 0) throw new NotSupportedException("The animated real-model stage requires one PMX bone.");
        ValueTask<SceneAnimationSession> CreateSession(CancellationToken token)
        {
            var layout = new SceneCompositionEditor(); var actor = layout.Add(model.Name, path, ModelRole.Character, scene);
            var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = model.Bones[0].Name }, new() { BoneName = model.Bones[0].Name, Frame = 30 }] };
            return ValueTask.FromResult(new SceneAnimationSession(layout.Objects,
                [new(actor.Id, model, motion, IkEnabled: false, PhysicsEnabled: false)],
                _ => throw new InvalidOperationException("The short real-model stage explicitly disables physics."), cancellationToken: token));
        }
        ExportCoordinator Coordinator() => new(CreateSession, (request, token) =>
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<IViewRenderer>(new FramedRenderer(request,camera)); });
        var stillResult = await Coordinator().RunAsync(new(new(1920,1080,new(30),0,0)), new PngFrameSink(Path.Combine(output,"real-pmx.png")));
        object? spatialComparison = null;
        if (compareFsr1)
        {
            var comparisonPlan = new OfflineFramePlan(1920, 1080, new(30), 0, 0);
            var frame = new OfflineFrame(comparisonPlan.GetSample(0), 1920, 1080, scene, null, null, null);
            var captures = new List<object>();
            foreach (bool enabled in new[] { false, true })
            {
                var quality = new ViewQualityOptions(Antialiasing: FrameAntialiasing.Smaa1X,
                    SmaaQuality: SmaaQuality.High, SpatialSamples: 1,
                    Upscaling: new() { Mode = enabled ? ViewUpscalingMode.Fsr1 : ViewUpscalingMode.Off });
                var captureRequest = new ViewRenderRequest(1920, 1080, quality);
                await using var renderer = new D3D12ViewRenderer(captureRequest);
                using var pixels = await renderer.CaptureAsync(frame);
                if (renderer.IsWarp) throw new InvalidOperationException("FSR1 comparison requires real GPU hardware.");
                if (enabled && renderer.Statistics is not { SpatialUpscaling: D3D12SpatialUpscalingMode.Fsr1,
                    UpscalingInputWidth: 1280, UpscalingInputHeight: 720, UpscalingOutputWidth: 1920, UpscalingOutputHeight: 1080 })
                    throw new InvalidOperationException("The FSR1 comparison did not execute at 1280x720 input and 1920x1080 output.");
                string file = enabled ? "real-pmx-fsr1-quality.png" : "real-pmx-native-smaa.png";
                await using var sink = new PngFrameSink(Path.Combine(output, file));
                await sink.BeginAsync(new(comparisonPlan, quality));
                await sink.WriteAsync(comparisonPlan.GetSample(0), pixels); await sink.CompleteAsync();
                captures.Add(new { file, quality, renderer.AdapterName, renderer.IsWarp, renderer.Statistics,
                    rgbaSha256 = Convert.ToHexString(SHA256.HashData(pixels.Pixels.Span)) });
            }
            spatialComparison = new { captures, scope = "Explicit FSR1 spatial fallback comparison only; no FSR3/4 or DLSS execution claim." };
        }
        VideoValidation? video = null;
        if (tools is not null)
        {
            var sink = new FfmpegFrameSink(tools, new(Path.Combine(output,"real-pmx-hevc.mp4"), VideoEncoder.HevcSoftware)
            { RateControl = VideoRateControl.ConstantQuality, Quality = 23, Speed = VideoSpeed.Balanced });
            await Coordinator().RunAsync(new(new(1920,1080,new(30000,1001),0,2)), sink);
            video = sink.Validation;
        }
        bool unchanged = sources.All(source => Hash(source.Key) == source.Value);
        if (!unchanged) throw new IOException("A read-only source asset changed during the stage capture.");
        return new { model = path, model.Name, vertices = model.Vertices.Count, materials = model.Materials.Count,
            stillResult, video, spatialComparison,
            sourceAssetsUnchanged = true, sources, textureLoads, adapted.Diagnostics,
            scope = "PMX through core actor/VMD 0/30 session; static-pose motion; 1920x1080 PNG and optional short HEVC constant-quality 23/balanced video; local images; common toon fallback as reported; IK/physics off; no original-EXE equivalence claim." };
    }
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }

    private sealed class FramedRenderer(ViewRenderRequest request, SceneCamera camera) : IViewRenderer
    {
        private readonly D3D12ViewRenderer inner = new(request);
        public ViewRendererCapabilities Capabilities => inner.Capabilities;
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        {
            var scene = frame.Scene;
            var framed = new OriginalFrameScene(scene.Surface, scene.Outline, scene.OutlineOptions,
                new(OriginalAlphaBlendMode.Normal,true,false,CameraOverride:camera), scene.Materials, scene.Lighting);
            return inner.CaptureAsync(frame with { Scene = framed },cancellationToken);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
