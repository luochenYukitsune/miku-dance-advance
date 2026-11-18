using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.ViewOutput.FFmpeg.Tests;

public sealed class FfmpegPipelineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(VideoEncoder.RawVideo, false)]
    [InlineData(VideoEncoder.HevcSoftware, true)]
    public async Task OverlappedPipelineAndPaddedStrideMatchSequentialOutputFrameForFrame(VideoEncoder encoder, bool withAudio)
    {
        var tools = Tools();
        string directory = Path.Combine(ArtifactDirectory(), "pipeline-parity-" + encoder + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var plan = new OfflineFramePlan(64, 48, new(30000, 1001), 0, 14);
            var audio = withAudio ? AudioInput(15) : null;
            string extension = encoder == VideoEncoder.RawVideo ? ".avi" : ".mp4";
            string serialPath = Path.Combine(directory, "serial" + extension);
            string pipelinePath = Path.Combine(directory, "pipeline" + extension);

            FfmpegOutputOptions MakeOptions(string path) => new(path, encoder)
            {
                BackgroundRed = 19, BackgroundGreen = 77, BackgroundBlue = 211,
                AudioInput = audio, Speed = VideoSpeed.Fast, Threads = 2
            };

            byte[] referenceRgb24 = new byte[checked((int)plan.FrameCount * plan.Width * 3 * plan.Height)];
            var serialSink = new FfmpegFrameSink(tools, MakeOptions(serialPath));
            var serialCoordinator = new ExportCoordinator(
                ct => ValueTask.FromResult(CreateSession(ct)),
                (_, _) => ValueTask.FromResult<IViewRenderer>(new SyntheticPatternRenderer(paddingBytes: 0, MakeOptions(serialPath), referenceRgb24)))
            { OverlapCapture = false };
            var serialResult = await serialCoordinator.RunAsync(new(plan), serialSink, cancellationToken: Token);

            var pipelineSink = new FfmpegFrameSink(tools, MakeOptions(pipelinePath));
            var pipelineCoordinator = new ExportCoordinator(
                ct => ValueTask.FromResult(CreateSession(ct)),
                (_, _) => ValueTask.FromResult<IViewRenderer>(new SyntheticPatternRenderer(paddingBytes: 64, MakeOptions(pipelinePath), null)))
            { OverlapCapture = true };
            var pipelineResult = await pipelineCoordinator.RunAsync(new(plan), pipelineSink, cancellationToken: Token);

            Assert.Equal(serialResult.CompletedFrames, pipelineResult.CompletedFrames);
            Assert.Equal(serialResult.SimulationTicks, pipelineResult.SimulationTicks);
            Assert.NotNull(serialSink.Validation);
            Assert.NotNull(pipelineSink.Validation);
            Assert.Equal(plan.FrameCount, pipelineSink.Validation!.Frames);
            Assert.InRange(pipelineSink.BufferPoolForTesting!.TotalAllocated, 1, 3);
            Assert.Equal(pipelineSink.BufferPoolForTesting.TotalAllocated, pipelineSink.BufferPoolForTesting.RetainedCount);

            string serialFrameMd5 = await FrameMd5Async(tools.FfmpegPath, serialPath);
            string pipelineFrameMd5 = await FrameMd5Async(tools.FfmpegPath, pipelinePath);
            Assert.Equal(serialFrameMd5, pipelineFrameMd5);

            if (encoder == VideoEncoder.RawVideo)
            {
                byte[] decoded = await DecodeRawRgb24Async(tools.FfmpegPath, pipelinePath);
                Assert.Equal(SHA256.HashData(referenceRgb24), SHA256.HashData(decoded));
            }
            if (withAudio)
            {
                Assert.Equal(serialSink.Validation!.Audio, pipelineSink.Validation!.Audio);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task BoundedChannelAppliesBackpressureAtCapacityAndReturnsAllBuffers()
    {
        var tools = Tools();
        string directory = Path.Combine(ArtifactDirectory(), "backpressure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "backpressure.avi");
            var plan = new OfflineFramePlan(64, 48, new(30), 0, 7);
            using var firstEntered = new ManualResetEventSlim(false);
            using var releaseWriter = new ManualResetEventSlim(false);
            int writeCount = 0;

            await using var sink = new FfmpegFrameSink(tools, new(output, VideoEncoder.RawVideo))
            {
                BeforePipeWriteForTesting = token =>
                {
                    if (Interlocked.Increment(ref writeCount) == 1)
                    {
                        firstEntered.Set();
                        releaseWriter.Wait(token);
                    }
                }
            };
            await sink.BeginAsync(new(plan), Token);
            byte[] pixels = new byte[64 * 48 * 4];
            for (int p = 3; p < pixels.Length; p += 4) pixels[p] = 255;

            // Frame 0 enters the writer thread and blocks in BeforePipeWriteForTesting.
            using (var frame0 = new FrameBufferLease(64, 48, 256, pixels))
                await sink.WriteAsync(plan.GetSample(0), frame0, Token);
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10), Token));

            // Frames 1 and 2 fill the bounded capacity (2 queued + 1 in-flight = 3 buffers max).
            using (var frame1 = new FrameBufferLease(64, 48, 256, pixels))
                await sink.WriteAsync(plan.GetSample(1), frame1, Token);
            using (var frame2 = new FrameBufferLease(64, 48, 256, pixels))
                await sink.WriteAsync(plan.GetSample(2), frame2, Token);

            var pool = sink.BufferPoolForTesting!;
            Assert.Equal(3, pool.TotalAllocated);

            // Frame 3 must block on WaitToWriteAsync until the writer drains a queued buffer.
            using var frame3 = new FrameBufferLease(64, 48, 256, pixels);
            var blockedWrite = sink.WriteAsync(plan.GetSample(3), frame3, Token).AsTask();
            await Task.Delay(80, Token);
            Assert.False(blockedWrite.IsCompleted);

            releaseWriter.Set();
            await blockedWrite.WaitAsync(Token);
            for (long i = 4; i < plan.FrameCount; i++)
            {
                using var frame = new FrameBufferLease(64, 48, 256, pixels);
                await sink.WriteAsync(plan.GetSample(i), frame, Token);
            }
            await sink.CompleteAsync(Token);

            Assert.Equal(plan.FrameCount, sink.Validation!.Frames);
            Assert.Equal(3, pool.TotalAllocated);
            Assert.Equal(pool.TotalAllocated, pool.RetainedCount);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MidExportCancellationUnderBackpressureUnblocksReturnsBuffersAndPreservesDestination()
    {
        var tools = Tools();
        string directory = Path.Combine(ArtifactDirectory(), "cancel-backpressure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "existing.avi");
            byte[] existing = [1, 2, 3, 4, 5];
            await File.WriteAllBytesAsync(output, existing, Token);
            var plan = new OfflineFramePlan(64, 48, new(30), 0, 7);
            using var firstEntered = new ManualResetEventSlim(false);
            int writeCount = 0;

            await using (var sink = new FfmpegFrameSink(tools, new(output, VideoEncoder.RawVideo))
            {
                BeforePipeWriteForTesting = token =>
                {
                    if (Interlocked.Increment(ref writeCount) == 1)
                    {
                        firstEntered.Set();
                        token.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
                    }
                }
            })
            {
                await sink.BeginAsync(new(plan), Token);
                byte[] pixels = new byte[64 * 48 * 4];
                using (var f0 = new FrameBufferLease(64, 48, 256, pixels)) await sink.WriteAsync(plan.GetSample(0), f0, Token);
                Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10), Token));
                using (var f1 = new FrameBufferLease(64, 48, 256, pixels)) await sink.WriteAsync(plan.GetSample(1), f1, Token);
                using (var f2 = new FrameBufferLease(64, 48, 256, pixels)) await sink.WriteAsync(plan.GetSample(2), f2, Token);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
                using var f3 = new FrameBufferLease(64, 48, 256, pixels);
                var write3 = sink.WriteAsync(plan.GetSample(3), f3, cts.Token).AsTask();
                await Task.Delay(50, Token);
                Assert.False(write3.IsCompleted);
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write3.WaitAsync(TimeSpan.FromSeconds(10), Token));
                var pool = sink.BufferPoolForTesting!;
                Assert.Equal(pool.TotalAllocated, pool.RetainedCount);
            }
            Assert.Equal(existing, await File.ReadAllBytesAsync(output, Token));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task EncoderCrashMidPipelinePropagatesFfmpegExceptionReturnsBuffersAndCleansFiles()
    {
        var tools = Tools();
        string directory = Path.Combine(ArtifactDirectory(), "crash-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Combine(directory, "existing.mp4");
            byte[] existing = [9, 8, 7, 6];
            await File.WriteAllBytesAsync(output, existing, Token);
            var plan = new OfflineFramePlan(64, 48, new(30), 0, 10);
            var options = new FfmpegOutputOptions(output, VideoEncoder.HevcSoftware)
            {
                AudioInput = AudioInput(11),
                Speed = VideoSpeed.Fast
            };
            FfmpegFrameSink? sinkRef = null;
            int writeCount = 0;

            await using var sink = new FfmpegFrameSink(tools, options);
            sinkRef = sink;
            sink.BeforePipeWriteForTesting = _ =>
            {
                if (Interlocked.Increment(ref writeCount) == 2)
                {
                    var procWrapper = typeof(FfmpegFrameSink)
                        .GetField("process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .GetValue(sinkRef)!;
                    var proc = (Process)procWrapper.GetType()
                        .GetField("process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .GetValue(procWrapper)!;
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
            };

            await sink.BeginAsync(new(plan), Token);
            byte[] pixels = new byte[64 * 48 * 4];
            var failure = await Assert.ThrowsAsync<FfmpegException>(async () =>
            {
                for (long i = 0; i < plan.FrameCount; i++)
                {
                    using var frame = new FrameBufferLease(64, 48, 256, pixels);
                    await sink.WriteAsync(plan.GetSample(i), frame, Token);
                }
                await sink.CompleteAsync(Token);
            });
            Assert.IsAssignableFrom<IOException>(failure.InnerException);
            Assert.IsType<string>(failure.Data["FfmpegDiagnostic"]);
            var pool = sink.BufferPoolForTesting!;
            Assert.Equal(pool.TotalAllocated, pool.RetainedCount);
            await sink.DisposeAsync();
            Assert.Equal(existing, await File.ReadAllBytesAsync(output, Token));
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
            Assert.Empty(Directory.GetFiles(directory, "*.pcm"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class SyntheticPatternRenderer(int paddingBytes, FfmpegOutputOptions options, byte[]? referenceRgb24Out) : IViewRenderer
    {
        public ViewRendererCapabilities Capabilities => new("test-synthetic", 4096, 4096, true, false);
        public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowBytes = frame.Width * 4;
            int stride = rowBytes + paddingBytes;
            byte[] data = new byte[stride * frame.Height];
            byte[]? rgbaRow = referenceRgb24Out is null ? null : new byte[rowBytes];
            int idx = (int)frame.Sample.Index;
            for (int y = 0; y < frame.Height; y++)
            {
                for (int x = 0; x < frame.Width; x++)
                {
                    int p = y * stride + x * 4;
                    data[p] = (byte)((x * 4 + idx * 17) & 255);
                    data[p + 1] = (byte)((y * 5 + idx * 9) & 255);
                    data[p + 2] = (byte)(((x + y) * 3 + idx * 13) & 255);
                    data[p + 3] = (byte)((x + y + idx) % 4 switch { 0 => 255, 1 => 0, 2 => 128, _ => 64 });
                }
                for (int pad = 0; pad < paddingBytes; pad++)
                    data[y * stride + rowBytes + pad] = (byte)(0xA5 ^ (pad + y + idx));
                if (referenceRgb24Out is not null)
                {
                    FfmpegFrameSink.CompositeRow(data.AsSpan(y * stride, rowBytes), rgbaRow!, options);
                    int dstRow = (idx * frame.Height + y) * frame.Width * 3;
                    for (int x = 0; x < frame.Width; x++)
                    {
                        referenceRgb24Out[dstRow + x * 3] = rgbaRow![x * 4];
                        referenceRgb24Out[dstRow + x * 3 + 1] = rgbaRow[x * 4 + 1];
                        referenceRgb24Out[dstRow + x * 3 + 2] = rgbaRow[x * 4 + 2];
                    }
                }
            }
            return ValueTask.FromResult(new FrameBufferLease(frame.Width, frame.Height, stride, data));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SceneAnimationSession CreateSession(CancellationToken token)
    {
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "test", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "tri", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Morphs = [], DisplayFrames = [], Joints = [], RigidBodies = [], SoftBodies = [],
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray()
        };
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("test", "test.pmx", ModelRole.Character, rest);
        return new(layout.Objects, [new(actor.Id, model, new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30 }] }, PhysicsEnabled: false)],
            _ => throw new InvalidOperationException("No physics in fixture"), cancellationToken: token);
    }

    private static SceneAudioExportInput AudioInput(int framesAt30Fps)
    {
        int sampleCount = framesAt30Fps * 8000 / 30 + 800;
        short[] samples = new short[sampleCount];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(Math.Sin(i * 0.1) * 8000);
        byte[] wav = new byte[44 + samples.Length * 2];
        "RIFF"u8.CopyTo(wav); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1); System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 8000); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 16000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2); System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36)); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)samples.Length * 2);
        for (int i = 0; i < samples.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), samples[i]);
        return new(new(Guid.NewGuid(), SceneMediaKind.Audio, SceneMediaAsset.FromBytes(wav, "tone.wav"), 0, true));
    }

    private static async Task<string> FrameMd5Async(string ffmpegPath, string path)
    {
        ProcessStartInfo info = new(ffmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-v", "error", "-i", path, "-f", "framemd5", "-" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }

    private static async Task<byte[]> DecodeRawRgb24Async(string ffmpegPath, string path)
    {
        ProcessStartInfo info = new(ffmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        using var memory = new MemoryStream();
        var stderr = process.StandardError.ReadToEndAsync(Token);
        await process.StandardOutput.BaseStream.CopyToAsync(memory, Token);
        await process.WaitForExitAsync(Token);
        Assert.True(process.ExitCode == 0, await stderr);
        return memory.ToArray();
    }

    private static FfmpegToolchain Tools()
    {
        string? ffmpeg = Environment.GetEnvironmentVariable("MMD_FFMPEG_PATH"), probe = Environment.GetEnvironmentVariable("MMD_FFPROBE_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(probe), "Set MMD_FFMPEG_PATH and MMD_FFPROBE_PATH to execute real codec tests.");
        return new(ffmpeg!, probe!);
    }

    private static string ArtifactDirectory()
    {
        string directory = Environment.GetEnvironmentVariable("MMD_FFMPEG_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "mmd-ffmpeg-tests");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
