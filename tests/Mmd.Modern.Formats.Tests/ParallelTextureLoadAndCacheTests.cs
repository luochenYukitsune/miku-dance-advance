using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class ParallelTextureLoadAndCacheTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-tex-cache-" + Guid.NewGuid().ToString("N"));

    public ParallelTextureLoadAndCacheTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void CacheHitsIdenticalFileAndInvalidatesOnLengthOrTimestampChange()
    {
        var ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(directory, "albedo.bin");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        DateTime initialTime = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, initialTime);

        int calls = 0;
        using var cache = new SceneTextureCache(maxParallelism: 2);
        var first = cache.Load(path, "albedo.bin", _ =>
        {
            calls++;
            return new SceneTexture("albedo.bin", 1, 1, [10, 20, 30, 255]);
        }, ct);
        var second = cache.Load(Path.Combine(directory, ".", "albedo.bin"), "albedo.bin", _ =>
        {
            calls++;
            return new SceneTexture("albedo.bin", 1, 1, [99, 99, 99, 255]);
        }, ct);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.Equal(1, cache.DecodeCount);

        // Touch timestamp without changing size -> must invalidate and re-decode.
        File.SetLastWriteTimeUtc(path, initialTime.AddSeconds(5));
        var updatedTimestamp = cache.Load(path, "albedo.bin", _ =>
        {
            calls++;
            return new SceneTexture("albedo.bin", 1, 1, [40, 50, 60, 255]);
        }, ct);
        Assert.Equal(2, calls);
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, updatedTimestamp!.Rgba.ToArray());

        // Change file length with identical timestamp -> must invalidate and re-decode.
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6]);
        File.SetLastWriteTimeUtc(path, initialTime.AddSeconds(5));
        var updatedLength = cache.Load(path, "albedo.bin", _ =>
        {
            calls++;
            return new SceneTexture("albedo.bin", 1, 1, [70, 80, 90, 255]);
        }, ct);
        Assert.Equal(3, calls);
        Assert.Equal(new byte[] { 70, 80, 90, 255 }, updatedLength!.Rgba.ToArray());
    }

    [Fact]
    public void PrefetchRespectsBoundedConcurrencyDerivedFromProcessorCount()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 1), SceneTextureCache.DefaultParallelism);

        const int maxParallel = 2;
        const int fileCount = 8;
        int active = 0;
        int maxObserved = 0;
        using var gate = new ManualResetEventSlim(false);
        using var enteredTwo = new CountdownEvent(maxParallel);
        using var cache = new SceneTextureCache(maxParallelism: maxParallel);

        var paths = new string[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            paths[i] = Path.Combine(directory, $"tex-{i}.bin");
            File.WriteAllBytes(paths[i], [(byte)i]);
        }

        for (int i = 0; i < fileCount; i++)
        {
            int captured = i;
            cache.Prefetch(paths[captured], $"tex-{captured}.bin", token =>
            {
                int now = Interlocked.Increment(ref active);
                while (true)
                {
                    int curMax = Volatile.Read(ref maxObserved);
                    if (now <= curMax || Interlocked.CompareExchange(ref maxObserved, now, curMax) == curMax)
                        break;
                }
                if (enteredTwo.CurrentCount > 0) enteredTwo.Signal();
                gate.Wait(token);
                Interlocked.Decrement(ref active);
                return new SceneTexture($"tex-{captured}.bin", 1, 1, [(byte)(captured * 20), 0, 0, 255]);
            }, ct);
        }

        Assert.True(enteredTwo.Wait(TimeSpan.FromSeconds(5), ct));
        gate.Set();

        for (int i = 0; i < fileCount; i++)
        {
            var tex = cache.Load(paths[i], $"tex-{i}.bin", _ => throw new InvalidOperationException("Unexpected inline decode"), ct);
            Assert.NotNull(tex);
            Assert.Equal((byte)(i * 20), tex.Rgba[0]);
        }

        Assert.InRange(maxObserved, 1, maxParallel);
        Assert.Equal(fileCount, cache.DecodeCount);
    }

    [Fact]
    public void DisposeCancelsBackgroundWorkDropsCacheAndRejectsFutureLoads()
    {
        var ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(directory, "slow.bin");
        File.WriteAllBytes(path, [42]);
        using var started = new ManualResetEventSlim(false);
        using var observedCancel = new ManualResetEventSlim(false);
        var cache = new SceneTextureCache(maxParallelism: 2);

        cache.Prefetch(path, "slow.bin", token =>
        {
            started.Set();
            try { WaitHandle.WaitAny([token.WaitHandle], TimeSpan.FromSeconds(10)); }
            finally { if (token.IsCancellationRequested) observedCancel.Set(); }
            token.ThrowIfCancellationRequested();
            return new SceneTexture("slow.bin", 1, 1, [1, 2, 3, 255]);
        }, ct);

        Assert.True(started.Wait(TimeSpan.FromSeconds(5), ct));
        cache.Dispose();
        Assert.True(observedCancel.IsSet);
        Assert.Throws<ObjectDisposedException>(() => cache.Load(path, "slow.bin", _ => null, ct));
    }

    [Fact]
    public void LoadAfterCancelledPrefetchCachesSuccessfulRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(directory, "retry.bin");
        File.WriteAllBytes(path, [42]);
        using var started = new ManualResetEventSlim();
        using var prefetchCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var cache = new SceneTextureCache(maxParallelism: 1);
        cache.Prefetch(path, "retry.bin", token =>
        {
            started.Set();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return null;
        }, prefetchCancellation.Token);

        Assert.True(started.Wait(TimeSpan.FromSeconds(5), ct));
        prefetchCancellation.Cancel();
        var recovered = cache.Load(path, "retry.bin",
            _ => new SceneTexture("retry.bin", 1, 1, [1, 2, 3, 255]), ct);
        var cached = cache.Load(path, "retry.bin", _ => throw new InvalidOperationException("Unexpected decode"), ct);
        Assert.NotNull(recovered);
        Assert.Same(recovered, cached);
        Assert.Equal(2, cache.DecodeCount);
    }

    [Fact]
    public void MissingAndInlineFailuresAreNeverCachedAcrossRetries()
    {
        var ct = TestContext.Current.CancellationToken;
        string missing = Path.Combine(directory, "absent.png");
        string failing = Path.Combine(directory, "broken.png");
        File.WriteAllBytes(failing, [9, 8, 7]);

        using var cache = new SceneTextureCache(maxParallelism: 2);
        int missingCalls = 0;
        Assert.Null(cache.Load(missing, "absent.png", _ => { missingCalls++; return null; }, ct));
        Assert.Null(cache.Load(missing, "absent.png", _ => { missingCalls++; return null; }, ct));
        Assert.Equal(2, missingCalls);

        int failCalls = 0;
        Assert.Throws<InvalidDataException>(() =>
            cache.Load(failing, "broken.png", _ => { failCalls++; throw new InvalidDataException("corrupt"); }, ct));
        var recovered = cache.Load(failing, "broken.png", _ =>
        {
            failCalls++;
            return new SceneTexture("broken.png", 1, 1, [5, 6, 7, 255]);
        }, ct);
        Assert.Equal(2, failCalls);
        Assert.NotNull(recovered);
    }

    [Fact]
    public void PmxSceneAdapterParallelMatchesSequentialPixelsDiagnosticsAndErrorOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = ReadFixtureModel();
        var model = BuildMultiTextureModel(source,
            ["a.png", "b.png", "missing.png", "toon.bmp"],
            [
                (TextureIndex: 0, ToonMode: PmxToonMode.Separate, ToonIndex: 3),
                (TextureIndex: 2, ToonMode: PmxToonMode.Common, ToonIndex: 0),
                (TextureIndex: 1, ToonMode: PmxToonMode.Separate, ToonIndex: 3),
            ]);

        var expectedPixels = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["a.png"] = [11, 22, 33, 255],
            ["b.png"] = [44, 55, 66, 128],
            ["toon.bmp"] = [77, 88, 99, 255],
        };

        var sequential = PmxSceneAdapter.FromRestPose(model, name =>
            expectedPixels.TryGetValue(name, out var rgba) ? new SceneTexture(name, 1, 1, rgba) : null);

        var parallel = PmxSceneAdapter.FromRestPose(model, (name, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (name == "a.png") Thread.Sleep(20); // Force out-of-order completion.
            return expectedPixels.TryGetValue(name, out var rgba) ? new SceneTexture(name, 1, 1, rgba) : null;
        }, ct, maxParallelism: 4);

        Assert.Equal(sequential.Diagnostics, parallel.Diagnostics);
        Assert.Equal(sequential.Snapshot.Textures.Count, parallel.Snapshot.Textures.Count);
        for (int i = 0; i < sequential.Snapshot.Textures.Count; i++)
        {
            Assert.Equal(sequential.Snapshot.Textures[i].Name, parallel.Snapshot.Textures[i].Name);
            Assert.Equal(sequential.Snapshot.Textures[i].Width, parallel.Snapshot.Textures[i].Width);
            Assert.Equal(sequential.Snapshot.Textures[i].Height, parallel.Snapshot.Textures[i].Height);
            Assert.Equal(sequential.Snapshot.Textures[i].Rgba.ToArray(), parallel.Snapshot.Textures[i].Rgba.ToArray());
        }
        for (int i = 0; i < sequential.Snapshot.Materials.Count; i++)
        {
            Assert.Equal(sequential.Snapshot.Materials[i].DiffuseTexture, parallel.Snapshot.Materials[i].DiffuseTexture);
            Assert.Equal(sequential.Snapshot.Materials[i].ToonTexture, parallel.Snapshot.Materials[i].ToonTexture);
        }

        // Parallel error order must always throw the first failing texture in material request order.
        var parallelFail = Assert.Throws<InvalidDataException>(() => PmxSceneAdapter.FromRestPose(model, (name, _) =>
        {
            if (name == "a.png") { Thread.Sleep(35); throw new InvalidDataException("first-diffuse-corrupt"); }
            if (name == "b.png") throw new InvalidDataException("second-diffuse-corrupt");
            return null;
        }, ct, maxParallelism: 4));
        Assert.Equal("first-diffuse-corrupt", parallelFail.Message);

        // Cancellation parity.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PmxSceneAdapter.FromRestPose(model, (_, token) => { token.ThrowIfCancellationRequested(); return null; }, cts.Token, maxParallelism: 2));
    }

    private static PmxModel ReadFixtureModel()
    {
        using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rich-w1-uv4.pmx"));
        return PmxReader.Read(input);
    }

    private static PmxModel BuildMultiTextureModel(PmxModel source, IReadOnlyList<string> textures,
        IReadOnlyList<(int TextureIndex, PmxToonMode ToonMode, int ToonIndex)> spec)
    {
        int facesPerMat = source.Faces.Count / spec.Count;
        var materials = new PmxMaterial[spec.Count];
        int assignedFaces = 0;
        for (int i = 0; i < spec.Count; i++)
        {
            int count = i == spec.Count - 1 ? source.Faces.Count - assignedFaces : facesPerMat;
            assignedFaces += count;
            var template = source.Materials[0];
            materials[i] = new()
            {
                Name = $"m{i}",
                EnglishName = $"m{i}",
                Diffuse = Vector4.One,
                Specular = Vector3.Zero,
                SpecularPower = 1,
                Ambient = Vector3.Zero,
                DrawMode = template.DrawMode,
                EdgeColor = Vector4.Zero,
                EdgeSize = 0,
                TextureIndex = spec[i].TextureIndex,
                SphereTextureIndex = -1,
                SphereMode = PmxSphereMode.None,
                ToonMode = spec[i].ToonMode,
                ToonTextureIndex = spec[i].ToonIndex,
                Memo = "",
                FaceVertexCount = count * 3,
            };
        }
        return new()
        {
            Header = source.Header, Name = source.Name, EnglishName = source.EnglishName,
            Comment = source.Comment, EnglishComment = source.EnglishComment,
            Vertices = source.Vertices, Faces = source.Faces, Materials = materials,
            Textures = textures, Bones = source.Bones, Morphs = source.Morphs,
            DisplayFrames = source.DisplayFrames, RigidBodies = source.RigidBodies,
            Joints = source.Joints, SoftBodies = source.SoftBodies,
        };
    }

    public void Dispose()
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
    }
}
