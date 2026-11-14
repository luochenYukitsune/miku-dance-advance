using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

// Reflection keeps RED behavioral while the public opt-in seam is absent. No DLL or GPU is used.
public sealed class SurfaceSceneFxSessionTests
{
    [Fact]
    public void SceneConfigurationRequiresMappedResourcesAndCopiesAssignmentsThroughExistingValidation()
    {
        var rows = new[] { new D3D9ObjectFxAssignment(91, -1, D3D9ObjectFxAssignmentMode.File, @"G:\cache\root.fx") };
        var map = Resources();
        var configuration = Configuration(rows, map);
        rows[0] = new(92, -1, D3D9ObjectFxAssignmentMode.Standard);
        Assert.Equal(91UL, Assert.Single(Property<IReadOnlyList<D3D9ObjectFxAssignment>>(configuration, "Assignments")).ObjectId);
        Assert.Same(map, Property<D3D9EffectResourceMap>(configuration, "Resources"));
        Assert.Throws<ArgumentException>(() => Configuration(Array.Empty<D3D9ObjectFxAssignment>(), new(Array.Empty<D3D9EffectResourceBinding>())));
        Assert.Throws<ArgumentNullException>(() => Configuration(Array.Empty<D3D9ObjectFxAssignment>(), null));
        Assert.Throws<ArgumentException>(() => Configuration(new[] { new D3D9ObjectFxAssignment(91, -1,
            D3D9ObjectFxAssignmentMode.File, @"G:\missing.fx") }, map));
        Assert.Throws<ArgumentException>(() => Configuration(new[] { rows[0], rows[0] }, map));
    }

    [Fact]
    public void SceneSnapshotCopiesBothPacketsAndDeclaresOnlyNativeFrameFeatures()
    {
        var packets = SurfaceSessionTests.Packets();
        var snapshot = Snapshot(packets.Frame, packets.Visual);
        Array.Fill(packets.Frame, (byte)0);
        Array.Fill(packets.Visual, (byte)0);
        Assert.Equal(3, (int)snapshot.Scope);
        Assert.Equal(1281UL, snapshot.RequiredFeatures);
        Assert.True(snapshot.FrameBytes.AsSpan(0, 8).SequenceEqual("MDA9FRM1"u8));
        Assert.True(snapshot.VisualBytes.AsSpan(0, 8).SequenceEqual("MDA9VIS1"u8));
        var uv = SurfaceAdditionalUvSessionTests.UvPackets();
        Assert.Equal(1283UL, Snapshot(uv.Frame, uv.Visual).RequiredFeatures);
    }

    [Theory]
    [InlineData(1023UL, true)] // missing scene effects
    [InlineData(1535UL, true)] // missing resource map
    [InlineData(1791UL, true)] // missing object FX
    [InlineData(2047UL, false)]
    public async Task SceneAdmissionRequiresAllConfigurationFeaturesAndExportBeforeDeviceCreation(ulong features, bool export)
    {
        var probe = new Probe { Features = features, SceneExport = export };
        await Assert.ThrowsAsync<NotSupportedException>(() => Create(probe, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "query", "unload" }, probe.Calls.Select(call => call.Name));
    }

    [Fact]
    public void SceneConfigurePinsCopiedV2TablesAndKeepsFailedChoiceRetryable()
    {
        var probe = new Probe { ConfigureFailures = 1 };
        var source = new[] { new D3D9ObjectFxAssignment(91, -1, D3D9ObjectFxAssignmentMode.File, @"G:\cache\root.fx") };
        var config = Configuration(source, Resources());
        source[0] = new(92, -1, D3D9ObjectFxAssignmentMode.Standard);
        using var api = probe.Api();
        var handle = api.Create(new(4, 2));
        Assert.Throws<D3D9SurfaceNativeException>(() => Configure(api, handle, config));
        Configure(api, handle, config);
        Assert.Throws<InvalidOperationException>(() => Configure(api, handle, config));
        Assert.Throws<InvalidOperationException>(() => api.ConfigureObjectFx(handle, new(Array.Empty<D3D9ObjectFxAssignment>())));
        Assert.Equal(2, probe.Calls.Count(call => call.Name == "configure scene"));
        Assert.Equal(new[] { @"Z:\source\root.fx" }, probe.Origins);
        Assert.Equal(new[] { @"G:\cache\root.fx" }, probe.Locals);
        Assert.Equal(new[] { @"G:\cache\root.fx" }, probe.FxRoots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SceneFramesUseOneOwnerDistinctScopeAndResultAcrossResize(bool preview)
    {
        var probe = new Probe();
        var caller = Environment.CurrentManagedThreadId;
        await using (var session = await Create(probe, preview, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Throws<InvalidOperationException>(() => { _ = Submit(session, SurfaceSessionTests.Snapshot(), preview); });
            var objectPackets = SurfaceSessionTests.Packets();
            Assert.Throws<InvalidOperationException>(() => { _ = Submit(session,
                D3D9SurfaceSnapshot.CopyConfiguredObjectFx(objectPackets.Frame, objectPackets.Visual), preview); });
            if (preview)
            {
                var result = await session.PresentAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(Property<bool>(result, "ConfiguredSceneFxMetadata"));
                Assert.False(result.ConfiguredObjectFxMetadata);
                Assert.False(result.IsolatedHostMetadata);
            }
            else
            {
                var result = await session.CaptureAsync(Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(Property<bool>(result, "ConfiguredSceneFxMetadata"));
                Assert.False(result.ConfiguredObjectFxMetadata);
                Assert.False(result.IsolatedHostMetadata);
            }
            await session.ResizeAsync(4, 2, TestContext.Current.CancellationToken);
            await Submit(session, Snapshot(), preview);
        }
        Assert.Equal(new[] { "query", preview ? "create preview" : "create", "configure scene", preview ? "present" : "capture",
            "resize", preview ? "present" : "capture", "destroy", "unload" }, probe.Calls.Select(call => call.Name));
        Assert.Single(probe.Calls.Select(call => call.Thread).Distinct());
        Assert.DoesNotContain(caller, probe.Calls.Select(call => call.Thread));
    }

    [Theory]
    [InlineData(false, 8U)] // object scope cannot acknowledge a scene capture
    [InlineData(false, 17U)] // isolation cannot accompany scene metadata
    [InlineData(true, 12U)] // object preview cannot acknowledge a scene present
    [InlineData(true, 28U)] // mixed configured scopes
    public void NativeSceneResultsRejectOtherScopeMetadata(bool preview, uint flags)
    {
        var probe = new Probe { ResultFlags = flags };
        using var api = probe.Api();
        var handle = preview ? api.CreatePreview((nint)1234, new(4, 2)) : api.Create(new(4, 2));
        Configure(api, handle, Configuration());
        Assert.Throws<InvalidDataException>(() =>
        {
            if (preview) api.Present(handle, Snapshot(), 0);
            else api.Capture(handle, Snapshot(), 0);
        });
    }

    [Fact]
    public async Task CancelledSceneInitializationRetainsAssetsUntilFailedDestroyRetrySucceeds()
    {
        var directory = Path.Combine(@"G:\MDA-Work\compat\20261106\managed491\cpu-assets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "root.fx");
        File.WriteAllText(path, "task-owned scene resource");
        IDisposable? assets = null;
        D3D9SurfaceInitializationCleanupException? failure = null;
        Task<ExperimentalD3D9SurfaceSession>? creation = null;
        var probe = new Probe { BlockConfigure = true, DestroyFailures = 1 };
        try
        {
            var config = Configuration(new[] { new D3D9ObjectFxAssignment(91, -1, D3D9ObjectFxAssignmentMode.File, path) },
                new(new[] { new D3D9EffectResourceBinding(@"Z:\source\root.fx", path) }));
            assets = (IDisposable)Invoke(RequiredType("D3D9SceneFxAssets").GetMethod("Open")!, null, config, new[] { path })!;
            Assert.Throws<ArgumentException>(() => Invoke(RequiredType("D3D9SceneFxAssets").GetMethod("Open")!, null, config, Array.Empty<string>()));
            using var cancellation = new CancellationTokenSource();
            creation = Create(probe, assets: assets, cancellationToken: cancellation.Token);
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            assets.Dispose();
            Assert.False(creation.IsCompleted);
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read).Dispose());
            probe.Release.Set();
            failure = await Assert.ThrowsAsync<D3D9SurfaceInitializationCleanupException>(() => creation);
            Assert.IsAssignableFrom<OperationCanceledException>(Assert.IsType<AggregateException>(failure.InnerException).InnerExceptions[0]);
            Assert.DoesNotContain(probe.Calls, call => call.Name == "unload");
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read).Dispose());
            await failure.DisposeAsync();
            failure = null;
            using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
            Assert.Single(probe.Calls.Select(call => call.Thread).Distinct());
            Assert.Equal(2, probe.Calls.Count(call => call.Name == "destroy"));
        }
        finally
        {
            probe.Release.Set();
            if (failure is not null) await failure.DisposeAsync();
            else if (creation is not null)
            {
                try { await (await creation).DisposeAsync(); }
                catch (D3D9SurfaceInitializationCleanupException cleanup) { await cleanup.DisposeAsync(); }
                catch (OperationCanceledException) { }
            }
            assets?.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private static Task Submit(ExperimentalD3D9SurfaceSession session, D3D9SurfaceSnapshot snapshot, bool preview) => preview ?
        session.PresentAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken) :
        session.CaptureAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken);

    private static Task<ExperimentalD3D9SurfaceSession> Create(Probe probe, bool preview = false, IDisposable? assets = null,
        CancellationToken cancellationToken = default)
    {
        var name = "CreateSceneFx" + (preview ? "Preview" : "") + (assets is null ? "" : "WithAssets") + "ForTestingAsync";
        var arguments = new List<object?> { (Func<ID3D9SurfaceNativeApi>)probe.Api };
        if (preview) arguments.Add((nint)1234);
        arguments.Add(new D3D9SurfaceSessionOptions(4, 2));
        arguments.Add(assets ?? Configuration());
        arguments.Add(cancellationToken);
        var method = typeof(ExperimentalD3D9SurfaceSession).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (Task<ExperimentalD3D9SurfaceSession>)Invoke(method!, null, arguments.ToArray())!;
    }

    private static D3D9EffectResourceMap Resources() => new(new[] { new D3D9EffectResourceBinding(@"Z:\source\root.fx", @"G:\cache\root.fx") });
    private static object Configuration() => Configuration(new[] { new D3D9ObjectFxAssignment(91, -1,
        D3D9ObjectFxAssignmentMode.File, @"G:\cache\root.fx") }, Resources());
    private static object Configuration(IEnumerable<D3D9ObjectFxAssignment> rows, D3D9EffectResourceMap? resources)
    {
        try { return Activator.CreateInstance(RequiredType("D3D9SceneFxConfiguration"), rows, resources)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static D3D9SurfaceSnapshot Snapshot() { var packets = SurfaceSessionTests.Packets(); return Snapshot(packets.Frame, packets.Visual); }
    private delegate D3D9SurfaceSnapshot CopySnapshot(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> visual);
    private static D3D9SurfaceSnapshot Snapshot(byte[] frame, byte[] visual)
    {
        var method = typeof(D3D9SurfaceSnapshot).GetMethod("CopyConfiguredSceneFx");
        Assert.NotNull(method);
        return method!.CreateDelegate<CopySnapshot>()(frame, visual);
    }
    private static void Configure(NativeSurfaceApi api, ulong handle, object configuration)
    {
        var method = typeof(NativeSurfaceApi).GetMethod("ConfigureSceneFx");
        Assert.NotNull(method);
        Invoke(method!, api, handle, configuration);
    }
    private static Type RequiredType(string name)
    {
        var type = typeof(D3D9SurfaceSnapshot).Assembly.GetType("Mmd.Modern.Rendering.D3D9.Compatibility." + name);
        Assert.NotNull(type);
        return type!;
    }
    private static T Property<T>(object owner, string name)
    {
        var property = owner.GetType().GetProperty(name);
        Assert.NotNull(property);
        return (T)property!.GetValue(owner)!;
    }
    private static object? Invoke(MethodInfo method, object? owner, params object?[] arguments)
    {
        try { return method.Invoke(owner, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private sealed class Probe
    {
        internal readonly ConcurrentQueue<(string Name, int Thread)> Calls = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal ulong Features = 2047;
        internal bool SceneExport = true, BlockConfigure;
        internal int ConfigureFailures, DestroyFailures;
        internal uint? ResultFlags;
        internal string[] Origins = [], Locals = [], FxRoots = [];
        private void Record(string name) => Calls.Enqueue((name, Environment.CurrentManagedThreadId));
        internal NativeSurfaceApi Api()
        {
            var exports = new NativeSurfaceExports
            {
                QueryAbi = (ref NativeAbi abi, ref NativeError error) => { Record("query"); abi = NativeAbi.Valid() with { SupportedFeatures = Features }; return 0; },
                Create = (in NativeCreate input, out ulong handle, ref NativeError error) => { Record("create"); handle = 7; return 0; },
                CreatePreview = (in NativeCreatePreview input, out ulong handle, ref NativeError error) => { Record("create preview"); handle = 7; return 0; },
                ConfigureObjectFx = (ulong handle, in NativeObjectFxConfiguration input, ref NativeError error) => { Record("unexpected object configure"); return 0; },
                ConfigureObjectFxV2 = (ulong handle, in NativeObjectFxConfigurationV2 input, ref NativeError error) => { Record("unexpected object configure V2"); return 0; },
                Capture = (ulong handle, in NativeCapture input, ref NativeCaptureResult result, ref NativeError error) =>
                {
                    Record("capture"); Assert.Equal(3U, input.Flags); Assert.Equal(1281UL, input.RequiredFeatures);
                    result = new() { Size = 80, Version = 1, Width = 4, Height = 2, SourceFrame = 4706, RgbaBytesRequired = 32,
                        Flags = ResultFlags ?? 16, CompletedCaptures = 1 }; return 0;
                },
                Present = (ulong handle, in NativePresent input, ref NativePresentResult result, ref NativeError error) =>
                {
                    Record("present"); Assert.Equal(3U, input.Flags); Assert.Equal(1409UL, input.RequiredFeatures);
                    result = new() { Size = 80, Version = 1, Width = 4, Height = 2, SourceFrame = 4706,
                        Flags = ResultFlags ?? 20, CompletedPresents = 1 }; return 0;
                },
                Resize = (ulong handle, in NativeResize input, ref NativeError error) => { Record("resize"); return 0; },
                Destroy = (ulong handle, ref NativeError error) => { Record("destroy"); return DestroyFailures-- > 0 ? -9 : 0; }
            };
            var property = typeof(NativeSurfaceExports).GetProperty("ConfigureSceneFx", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(property);
            if (SceneExport) property!.SetValue(exports, (NativeSurfaceExports.ConfigureObjectFxV2Call)Configure);
            return new(1, exports, _ => Record("unload"));
        }
        private int Configure(ulong handle, in NativeObjectFxConfigurationV2 input, ref NativeError error)
        {
            Record("configure scene");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.Equal(48U, input.Size); Assert.Equal(2U, input.Version);
            var assignmentPointer = input.Assignments;
            var resourcePointer = input.Resources;
            var rows = Enumerable.Range(0, checked((int)input.AssignmentCount)).Select(index =>
                Marshal.PtrToStructure<NativeObjectFxAssignment>(assignmentPointer + index * 48)).ToArray();
            var resources = Enumerable.Range(0, checked((int)input.ResourceCount)).Select(index =>
                Marshal.PtrToStructure<NativeEffectResourceBinding>(resourcePointer + index * 48)).ToArray();
            Assert.Equal(91UL, Assert.Single(rows).ObjectId);
            FxRoots = rows.Select(row => Utf8(row.Utf8Path, row.PathLength)).ToArray();
            Origins = resources.Select(row => Utf8(row.OriginUtf8, row.OriginLength)).ToArray();
            Locals = resources.Select(row => Utf8(row.MaterializedUtf8, row.MaterializedLength)).ToArray();
            if (BlockConfigure)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Scene configure was not released.");
            }
            return ConfigureFailures-- > 0 ? -1 : 0;
        }
        private static string Utf8(nint pointer, ulong length)
        {
            var bytes = new byte[checked((int)length)]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes);
        }
    }
}
