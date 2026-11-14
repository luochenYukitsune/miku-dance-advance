using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

// Managed contract tests only. No native library, filesystem assets or graphics device are loaded.
public sealed class SurfaceObjectFxSessionTests
{
    [Fact]
    public void ObjectFxNativeRowsAndConfigurationMatchTheX64CAbi()
    {
        var row = RequiredType("NativeObjectFxAssignment");
        var configuration = RequiredType("NativeObjectFxConfiguration");
        Assert.Equal(48, Marshal.SizeOf(row));
        Assert.Equal(32, Marshal.SizeOf(configuration));
        Assert.Equal(8, Marshal.OffsetOf(row, "ObjectId").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf(row, "Subset").ToInt32());
        Assert.Equal(24, Marshal.OffsetOf(row, "Utf8Path").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf(row, "PathLength").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf(configuration, "Assignments").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf(configuration, "AssignmentCount").ToInt32());
    }

    [Fact]
    public void ConfigurationCopiesRowsAndKeepsAssignmentsReadOnly()
    {
        var row = NewAssignment(91, -1, 2, @"D:\fixture\日本語.fx");
        var rows = Array.CreateInstance(row.GetType(), 1);
        rows.SetValue(row, 0);
        var configuration = NewConfiguration(rows);
        rows.SetValue(NewAssignment(92, 0, 1, null), 0);
        var assignments = Assert.IsAssignableFrom<IEnumerable>(configuration.GetType().GetProperty("Assignments")!.GetValue(configuration));
        var copied = Assert.Single(assignments.Cast<object>());
        Assert.Equal(91UL, copied.GetType().GetProperty("ObjectId")!.GetValue(copied));
        Assert.Equal(@"D:\fixture\日本語.fx", copied.GetType().GetProperty("AbsoluteFxPath")!.GetValue(copied));
        Assert.All(copied.GetType().GetProperties(), property => Assert.False(property.CanWrite));
        if (assignments is IList mutable) Assert.True(mutable.IsReadOnly);
    }

    [Theory]
    [InlineData(0UL, -1, 1, null)]
    [InlineData(1UL, -2, 1, null)]
    [InlineData(1UL, -1, 3, null)]
    [InlineData(1UL, -1, 0, "D:\\fixture\\wrong.fx")]
    [InlineData(1UL, -1, 1, "D:\\fixture\\wrong.fx")]
    [InlineData(1UL, -1, 2, null)]
    [InlineData(1UL, -1, 2, "relative.fx")]
    [InlineData(1UL, -1, 2, "D:relative.fx")]
    [InlineData(1UL, -1, 2, "\\\\server\\share\\file.fx")]
    [InlineData(1UL, -1, 2, "D:\\fixture\\bad\0.fx")]
    public void InvalidAssignmentIsRejectedBeforeAnyNativeWork(ulong id, int subset, int mode, string? path)
    {
        var error = Assert.Throws<TargetInvocationException>(() => NewAssignment(id, subset, mode, path));
        Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
    }

    [Fact]
    public void DuplicateStableObjectAndSubsetIsRejectedButWholeAndSubsetOverridesAreAllowed()
    {
        var whole = NewAssignment(91, -1, 2, @"D:\fixture\whole.fx");
        var subset = NewAssignment(91, 0, 1, null);
        var rows = Array.CreateInstance(whole.GetType(), 2);
        rows.SetValue(whole, 0);
        rows.SetValue(subset, 1);
        _ = NewConfiguration(rows);
        rows.SetValue(whole, 1);
        var error = Assert.Throws<TargetInvocationException>(() => NewConfiguration(rows));
        Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
    }

    [Fact]
    public void ConfiguredSnapshotHasSeparateScopeAndRequiredFeatureWithoutClaimingIsolation()
    {
        var factory = typeof(D3D9SurfaceSnapshot).GetMethod("CopyConfiguredObjectFx", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(factory);
        var copy = factory!.CreateDelegate<SnapshotCopy>();
        var (frame, visual) = SurfaceSessionTests.Packets();
        var snapshot = copy(frame, visual);
        frame[0] = 0;
        visual[0] = 0;
        Assert.Equal((byte)'M', snapshot.FrameBytes[0]);
        Assert.Equal((byte)'M', snapshot.VisualBytes[0]);
        Assert.Equal(257UL, snapshot.RequiredFeatures);
        var scope = typeof(D3D9SurfaceSnapshot).GetProperty("Scope");
        Assert.NotNull(scope);
        Assert.Equal("ConfiguredObjectFx", scope!.GetValue(snapshot)!.ToString());
        Assert.Equal("IsolatedSurface", scope.GetValue(SurfaceSessionTests.Snapshot())!.ToString());
        var uv = SurfaceAdditionalUvSessionTests.UvPackets();
        Assert.Equal(259UL, copy(uv.Frame, uv.Visual).RequiredFeatures);
    }

    [Fact]
    public void ObjectFxCreationIsExplicitAndDoesNotReplaceLegacyCreation()
    {
        var configuration = RequiredType("D3D9ObjectFxConfiguration");
        Assert.NotNull(typeof(ExperimentalD3D9SurfaceSession).GetMethod("CreateObjectFxAsync", new[]
            { typeof(string), typeof(D3D9SurfaceSessionOptions), configuration, typeof(CancellationToken) }));
        Assert.NotNull(typeof(ExperimentalD3D9SurfaceSession).GetMethod("CreateObjectFxPreviewAsync", new[]
            { typeof(string), typeof(nint), typeof(D3D9SurfaceSessionOptions), configuration, typeof(CancellationToken) }));
        Assert.NotNull(typeof(ExperimentalD3D9SurfaceSession).GetMethod("CreateAsync", new[]
            { typeof(string), typeof(D3D9SurfaceSessionOptions), typeof(CancellationToken) }));
        Assert.NotNull(typeof(ID3D9SurfaceNativeApi).GetProperty("SupportsObjectFx"));
        Assert.NotNull(typeof(ID3D9SurfaceNativeApi).GetMethod("ConfigureObjectFx"));
        var export = typeof(NativeSurfaceExports).GetProperty("ConfigureObjectFx", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(export);
        var call = RequiredType("NativeSurfaceExports+ConfigureObjectFxCall");
        Assert.Equal(CallingConvention.Cdecl, call.GetCustomAttribute<UnmanagedFunctionPointerAttribute>()!.CallingConvention);
    }

    [Fact]
    public void StrictUtf8AndNativeConfigurationBudgetsAreValidatedWithoutFileIo()
    {
        Assert.Throws<ArgumentException>(() => new D3D9ObjectFxAssignment(1, -1, D3D9ObjectFxAssignmentMode.File, "D:\\bad\uD800.fx"));
        Assert.Throws<ArgumentException>(() => new D3D9ObjectFxAssignment(1, -1, D3D9ObjectFxAssignmentMode.File, "D:\\" + new string('語', 10923)));
        var valid = new D3D9ObjectFxAssignment(1, -1, D3D9ObjectFxAssignmentMode.Standard);
        Assert.Throws<ArgumentException>(() => new D3D9ObjectFxConfiguration(Enumerable.Range(0, 65537)
            .Select(i => new D3D9ObjectFxAssignment((ulong)i + 1, -1, D3D9ObjectFxAssignmentMode.Standard))));
        Assert.Throws<ArgumentNullException>(() => new D3D9ObjectFxConfiguration(new[] { valid, null! }));
        var largePath = "D:\\" + new string('x', 32764);
        Assert.Throws<ArgumentException>(() => new D3D9ObjectFxConfiguration(Enumerable.Range(0, 513)
            .Select(i => new D3D9ObjectFxAssignment((ulong)i + 1, -1, D3D9ObjectFxAssignmentMode.File, largePath))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigureMarshalsFrozenRowsAndStrictUtf8PathsWithoutNullOrReservedData(bool empty)
    {
        const string path = @"D:\fixture\日本語.fx";
        var calls = 0;
        using var api = new NativeSurfaceApi(1, NativeExports(configure:
            (ulong handle, in NativeObjectFxConfiguration input, ref NativeError error) =>
        {
            calls++;
            Assert.Equal(7UL, handle);
            Assert.Equal(32U, input.Size);
            Assert.Equal(1U, input.Version);
            Assert.Equal(0U, input.Reserved0 | input.Reserved1);
            Assert.Equal(empty ? 0UL : 3UL, input.AssignmentCount);
            if (empty) Assert.Equal(0, input.Assignments);
            else
            {
                var file = Marshal.PtrToStructure<NativeObjectFxAssignment>(input.Assignments);
                Assert.Equal(48U, file.Size);
                Assert.Equal(1U, file.Version);
                Assert.Equal(91UL, file.ObjectId);
                Assert.Equal(-1, file.Subset);
                Assert.Equal(2U, file.Assignment);
                Assert.Equal(0U, file.Reserved0 | file.Reserved1);
                var bytes = new byte[checked((int)file.PathLength)];
                Marshal.Copy(file.Utf8Path, bytes, 0, bytes.Length);
                Assert.Equal(Encoding.UTF8.GetBytes(path), bytes);
                for (var i = 1; i < 3; i++)
                {
                    var row = Marshal.PtrToStructure<NativeObjectFxAssignment>(input.Assignments + 48 * i);
                    Assert.Equal(i - 1, row.Subset);
                    Assert.Equal((uint)(i == 1 ? D3D9ObjectFxAssignmentMode.Standard : D3D9ObjectFxAssignmentMode.Inherit), row.Assignment);
                    Assert.Equal(0, row.Utf8Path);
                    Assert.Equal(0UL, row.PathLength);
                    Assert.Equal(0U, row.Reserved0 | row.Reserved1);
                }
            }
            return 0;
        }), _ => { });
        var rows = new[] { new D3D9ObjectFxAssignment(91, -1, D3D9ObjectFxAssignmentMode.File, path),
            new D3D9ObjectFxAssignment(91, 0, D3D9ObjectFxAssignmentMode.Standard),
            new D3D9ObjectFxAssignment(91, 1, D3D9ObjectFxAssignmentMode.Inherit) };
        var configuration = new D3D9ObjectFxConfiguration(empty ? Array.Empty<D3D9ObjectFxAssignment>() : rows);
        var handle = api.Create(new(4, 2));
        api.ConfigureObjectFx(handle, configuration);
        Assert.Throws<InvalidOperationException>(() => api.ConfigureObjectFx(handle, configuration));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConfiguredNativeCaptureAndPreviewDeclareTheirScopeFeaturesAndMetadata(bool preview, bool uv)
    {
        var captureCalls = 0;
        var presentCalls = 0;
        using var api = new NativeSurfaceApi(1, NativeExports(
            capture: (ulong handle, in NativeCapture input, ref NativeCaptureResult result, ref NativeError error) =>
            {
                captureCalls++;
                Assert.Equal(uv ? 259UL : 257UL, input.RequiredFeatures);
                Assert.Equal(2U, input.Flags);
                result = CaptureResult(10);
                return 0;
            }, present: (ulong handle, in NativePresent input, ref NativePresentResult result, ref NativeError error) =>
            {
                presentCalls++;
                Assert.Equal(uv ? 387UL : 385UL, input.RequiredFeatures);
                Assert.Equal(2U, input.Flags);
                result = PresentResult(14);
                return 0;
            }), _ => { });
        var handle = preview ? api.CreatePreview((nint)1234, new(4, 2)) : api.Create(new(4, 2));
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (preview) api.Present(handle, ConfiguredSnapshot(uv), 0);
            else api.Capture(handle, ConfiguredSnapshot(uv), 0);
        });
        api.ConfigureObjectFx(handle, EmptyConfiguration());
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (preview) api.Present(handle, SurfaceSessionTests.Snapshot(), 0);
            else api.Capture(handle, SurfaceSessionTests.Snapshot(), 0);
        });
        if (preview)
        {
            var output = api.Present(handle, ConfiguredSnapshot(uv), 0);
            Assert.True(output.ConfiguredObjectFxMetadata);
            Assert.False(output.IsolatedHostMetadata);
            Assert.True(output.SoftwareVertexProcessing);
        }
        else
        {
            var output = api.Capture(handle, ConfiguredSnapshot(uv), 0);
            Assert.True(output.ConfiguredObjectFxMetadata);
            Assert.False(output.IsolatedHostMetadata);
            Assert.True(output.SoftwareVertexProcessing);
        }
        Assert.Equal(preview ? 0 : 1, captureCalls);
        Assert.Equal(preview ? 1 : 0, presentCalls);
    }

    [Theory]
    [InlineData(false, 0U)]
    [InlineData(false, 1U)]
    [InlineData(false, 9U)]
    [InlineData(false, 12U)]
    [InlineData(false, 24U)]
    [InlineData(true, 4U)]
    [InlineData(true, 5U)]
    [InlineData(true, 8U)]
    [InlineData(true, 13U)]
    [InlineData(true, 28U)]
    public void ConfiguredNativeResultsRejectIsolationMissingAcceptanceAndUnknownFlags(bool preview, uint flags)
    {
        using var api = new NativeSurfaceApi(1, NativeExports(
            capture: (ulong handle, in NativeCapture input, ref NativeCaptureResult result, ref NativeError error) => { result = CaptureResult(flags); return 0; },
            present: (ulong handle, in NativePresent input, ref NativePresentResult result, ref NativeError error) => { result = PresentResult(flags); return 0; }), _ => { });
        var handle = preview ? api.CreatePreview((nint)1234, new(4, 2)) : api.Create(new(4, 2));
        api.ConfigureObjectFx(handle, EmptyConfiguration());
        Assert.Throws<InvalidDataException>(() =>
        {
            if (preview) api.Present(handle, ConfiguredSnapshot(), 0);
            else api.Capture(handle, ConfiguredSnapshot(), 0);
        });
    }

    [Fact]
    public void NativeConfigureFailurePreservesStatusDoesNotRetryOrConsumeTheConfigurationChoice()
    {
        var calls = 0;
        using var api = new NativeSurfaceApi(1, NativeExports(configure:
            (ulong handle, in NativeObjectFxConfiguration input, ref NativeError error) =>
            {
                if (++calls != 1) return 0;
                error.Status = -1;
                var message = Encoding.UTF8.GetBytes("missing 日本語.fx");
                Marshal.Copy(message, 0, error.Utf8, message.Length);
                return -1;
            }), _ => { });
        var handle = api.Create(new(4, 2));
        var error = Assert.Throws<D3D9SurfaceNativeException>(() => api.ConfigureObjectFx(handle, EmptyConfiguration()));
        Assert.Equal(-1, error.Status);
        Assert.Equal("configure object FX", error.Operation);
        Assert.Equal("missing 日本語.fx", error.Diagnostic);
        Assert.Equal(1, calls);
        api.ConfigureObjectFx(handle, EmptyConfiguration());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerCreatesThenConfiguresBeforeCaptureOrPresentAndNeverReconfiguresAfterResize(bool preview)
    {
        var fake = new ObjectFxApi();
        var configuration = EmptyConfiguration();
        var caller = Environment.CurrentManagedThreadId;
        await using (var session = await CreateConfigured(fake, configuration, preview, TestContext.Current.CancellationToken))
        {
            if (preview) await session.PresentAsync(ConfiguredSnapshot(), cancellationToken: TestContext.Current.CancellationToken);
            else await session.CaptureAsync(ConfiguredSnapshot(), cancellationToken: TestContext.Current.CancellationToken);
            await session.ResizeAsync(8, 4, TestContext.Current.CancellationToken);
            if (preview) await session.PresentAsync(ConfiguredSnapshot(), cancellationToken: TestContext.Current.CancellationToken);
            else await session.CaptureAsync(ConfiguredSnapshot(), cancellationToken: TestContext.Current.CancellationToken);
        }
        Assert.Same(configuration, fake.Configuration);
        Assert.Equal(new[] { "query", preview ? "create preview" : "create", "configure", preview ? "present" : "capture", "resize",
            preview ? "present" : "capture", "destroy", "unload" }, fake.Calls.Select(call => call.Name));
        Assert.Single(fake.Calls.Select(call => call.Thread).Distinct());
        Assert.DoesNotContain(caller, fake.Calls.Select(call => call.Thread));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MissingFeatureOrOptionalExportRejectsConfiguredModeBeforeDeviceCreation(bool feature, bool export)
    {
        var fake = new ObjectFxApi { Abi = NativeAbi.Valid() with { SupportedFeatures = feature ? 387UL : 131UL }, SupportsObjectFx = export };
        await Assert.ThrowsAsync<NotSupportedException>(() => CreateConfigured(fake, EmptyConfiguration(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "query", "unload" }, fake.Calls.Select(call => call.Name));
    }

    [Fact]
    public async Task LegacyCreationIgnoresOptionalConfigureEvenWhenItsDefaultInterfaceImplementationIsUnavailable()
    {
        var fake = new LegacyOnlyApi();
        await using (var session = await ExperimentalD3D9SurfaceSession.CreateForTestingAsync(() => fake, new(4, 2), TestContext.Current.CancellationToken))
            await session.CaptureAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "query", "create", "capture", "destroy", "unload" }, fake.Calls);
        var unsupported = new LegacyOnlyApi();
        await Assert.ThrowsAsync<NotSupportedException>(() => ExperimentalD3D9SurfaceSession.CreateObjectFxForTestingAsync(
            () => unsupported, new(4, 2), EmptyConfiguration(), TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "query", "unload" }, unsupported.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopeMismatchIsRejectedSynchronouslyBeforeTheBoundedQueue(bool preview)
    {
        var fake = new ObjectFxApi { BlockFrame = true };
        var session = await CreateConfigured(fake, EmptyConfiguration(), preview, TestContext.Current.CancellationToken, capacity: 1);
        var active = preview ? (Task)session.PresentAsync(ConfiguredSnapshot(), cancellationToken: TestContext.Current.CancellationToken) :
            session.CaptureAsync(ConfiguredSnapshot(), cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var queued = session.ResizeAsync(8, 4, TestContext.Current.CancellationToken);
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (preview) _ = session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
                else _ = session.CaptureAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken);
            });
            fake.Release.Set();
            await active;
            await queued;
        }
        finally { fake.Release.Set(); await session.DisposeAsync(); }
        Assert.Single(fake.Calls, call => call.Name == (preview ? "present" : "capture"));
        var legacy = new ObjectFxApi();
        await using var legacySession = await ExperimentalD3D9SurfaceSession.CreateForTestingAsync(() => legacy, new(4, 2), TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => { _ = legacySession.CaptureAsync(ConfiguredSnapshot(), cancellationToken: TestContext.Current.CancellationToken); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringConfigureWaitsForNativeReturnThenDestroysOnTheSameOwner(bool preview)
    {
        var fake = new ObjectFxApi { BlockConfigure = true };
        using var cancellation = new CancellationTokenSource();
        var creation = CreateConfigured(fake, EmptyConfiguration(), preview, cancellation.Token);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.False(creation.IsCompleted);
        Assert.DoesNotContain(fake.Calls, call => call.Name is "destroy" or "unload");
        fake.Release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        Assert.Equal(new[] { "query", preview ? "create preview" : "create", "configure", "destroy", "unload" }, fake.Calls.Select(call => call.Name));
        Assert.Single(fake.Calls.Select(call => call.Thread).Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigureFailureAndCancelledInitializationKeepFailedDestroyRetryable(bool cancel)
    {
        var fake = new ObjectFxApi { BlockConfigure = true, FailConfigure = !cancel, DestroyFailures = 1 };
        using var cancellation = new CancellationTokenSource();
        var creation = CreateConfigured(fake, EmptyConfiguration(), cancellationToken: cancellation.Token);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (cancel) cancellation.Cancel();
        fake.Release.Set();
        var failure = await Assert.ThrowsAsync<D3D9SurfaceInitializationCleanupException>(() => creation);
        var causes = Assert.IsType<AggregateException>(failure.InnerException);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(causes.InnerExceptions[0]);
        else Assert.Equal("configure object FX", Assert.IsType<D3D9SurfaceNativeException>(causes.InnerExceptions[0]).Operation);
        Assert.IsType<D3D9SurfaceNativeException>(causes.InnerExceptions[1]);
        Assert.DoesNotContain(fake.Calls, call => call.Name == "unload");
        await failure.DisposeAsync();
        Assert.Equal(2, fake.Calls.Count(call => call.Name == "destroy"));
        Assert.Single(fake.Calls, call => call.Name == "configure");
        Assert.Equal("unload", fake.Calls.Last().Name);
        Assert.Single(fake.Calls.Select(call => call.Thread).Distinct());
    }

    private static D3D9ObjectFxConfiguration EmptyConfiguration() => new(Array.Empty<D3D9ObjectFxAssignment>());

    private static D3D9SurfaceSnapshot ConfiguredSnapshot(bool uv = false)
    {
        var packets = uv ? SurfaceAdditionalUvSessionTests.UvPackets() : SurfaceSessionTests.Packets();
        return D3D9SurfaceSnapshot.CopyConfiguredObjectFx(packets.Frame, packets.Visual);
    }

    private static Task<ExperimentalD3D9SurfaceSession> CreateConfigured(ObjectFxApi fake, D3D9ObjectFxConfiguration configuration,
        bool preview = false, CancellationToken cancellationToken = default, int capacity = 2) => preview ?
        ExperimentalD3D9SurfaceSession.CreateObjectFxPreviewForTestingAsync(() => fake, (nint)1234, new(4, 2, queueCapacity: capacity), configuration, cancellationToken) :
        ExperimentalD3D9SurfaceSession.CreateObjectFxForTestingAsync(() => fake, new(4, 2, queueCapacity: capacity), configuration, cancellationToken);

    private static NativeCaptureResult CaptureResult(uint flags = 8) => new()
    {
        Size = 80, Version = 1, Width = 4, Height = 2, SourceFrame = 4706, Flags = flags,
        RgbaBytesRequired = 32, CompletedCaptures = 1, GeometryGeneration = 1
    };

    private static NativePresentResult PresentResult(uint flags = 12) => new()
    {
        Size = 80, Version = 1, Width = 4, Height = 2, SourceFrame = 4706, Flags = flags,
        CompletedPresents = 1, GeometryGeneration = 1
    };

    private static NativeSurfaceExports NativeExports(NativeSurfaceExports.ConfigureObjectFxCall? configure = null,
        NativeSurfaceExports.CaptureCall? capture = null, NativeSurfaceExports.PresentCall? present = null) => new()
    {
        QueryAbi = (ref NativeAbi abi, ref NativeError error) => { abi = NativeAbi.Valid() with { SupportedFeatures = 387 }; return 0; },
        Create = (in NativeCreate input, out ulong handle, ref NativeError error) => { handle = 7; return 0; },
        CreatePreview = (in NativeCreatePreview input, out ulong handle, ref NativeError error) => { handle = 7; return 0; },
        ConfigureObjectFx = configure ?? ((ulong handle, in NativeObjectFxConfiguration input, ref NativeError error) => 0),
        Capture = capture ?? ((ulong handle, in NativeCapture input, ref NativeCaptureResult result, ref NativeError error) => { result = CaptureResult(); return 0; }),
        Present = present ?? ((ulong handle, in NativePresent input, ref NativePresentResult result, ref NativeError error) => { result = PresentResult(); return 0; }),
        Resize = (ulong handle, in NativeResize input, ref NativeError error) => 0,
        Destroy = (ulong handle, ref NativeError error) => 0
    };

    private sealed class ObjectFxApi : ID3D9SurfaceNativeApi
    {
        internal readonly ConcurrentQueue<(string Name, int Thread)> Calls = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal NativeAbi Abi = NativeAbi.Valid() with { SupportedFeatures = 387 };
        internal D3D9ObjectFxConfiguration? Configuration;
        internal bool BlockConfigure, BlockFrame, FailConfigure;
        internal int DestroyFailures;
        public bool SupportsObjectFx { get; init; } = true;
        public bool SupportsPreview => true;
        private void Record(string name) => Calls.Enqueue((name, Environment.CurrentManagedThreadId));
        public NativeAbi QueryAbi() { Record("query"); return Abi; }
        public ulong Create(D3D9SurfaceSessionOptions options) { Record("create"); return 7; }
        public ulong CreatePreview(nint hwnd, D3D9SurfaceSessionOptions options) { Assert.Equal((nint)1234, hwnd); Record("create preview"); return 7; }
        public void ConfigureObjectFx(ulong handle, D3D9ObjectFxConfiguration configuration)
        {
            Assert.Equal(7UL, handle);
            Record("configure");
            Configuration = configuration;
            if (BlockConfigure) Block();
            if (FailConfigure) throw new D3D9SurfaceNativeException("configure object FX", -1, "invalid immutable root", 0, false);
        }
        public D3D9SurfaceCapture Capture(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order)
        {
            Record("capture");
            if (BlockFrame) Block();
            return new(4, 2, new byte[32], CaptureResult());
        }
        public D3D9SurfacePresentation Present(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order)
        {
            Record("present");
            if (BlockFrame) Block();
            return new(PresentResult());
        }
        public void Resize(ulong handle, int width, int height) => Record("resize");
        public void Destroy(ulong handle)
        {
            Record("destroy");
            if (DestroyFailures-- > 0) throw new D3D9SurfaceNativeException("destroy", -9, "retry cleanup", 0, false);
        }
        public void Dispose() => Record("unload");
        private void Block()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Object FX fake owner was not released.");
        }
    }

    private sealed class LegacyOnlyApi : ID3D9SurfaceNativeApi
    {
        internal readonly ConcurrentQueue<string> Calls = new();
        public NativeAbi QueryAbi() { Calls.Enqueue("query"); return NativeAbi.Valid() with { SupportedFeatures = 387 }; }
        public ulong Create(D3D9SurfaceSessionOptions options) { Calls.Enqueue("create"); return 7; }
        public D3D9SurfaceCapture Capture(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order)
        {
            Calls.Enqueue("capture");
            return new(4, 2, new byte[32], CaptureResult(1));
        }
        public void Resize(ulong handle, int width, int height) => Calls.Enqueue("resize");
        public void Destroy(ulong handle) => Calls.Enqueue("destroy");
        public void Dispose() => Calls.Enqueue("unload");
    }

    private delegate D3D9SurfaceSnapshot SnapshotCopy(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> visual);

    private static Type RequiredType(string name)
    {
        var type = typeof(D3D9SurfaceSnapshot).Assembly.GetType("Mmd.Modern.Rendering.D3D9.Compatibility." + name);
        Assert.NotNull(type);
        return type!;
    }

    private static object NewAssignment(ulong id, int subset, int mode, string? path) =>
        Activator.CreateInstance(RequiredType("D3D9ObjectFxAssignment"), id, subset,
            Enum.ToObject(RequiredType("D3D9ObjectFxAssignmentMode"), mode), path)!;

    private static object NewConfiguration(Array rows) => Activator.CreateInstance(RequiredType("D3D9ObjectFxConfiguration"), new object[] { rows })!;
}
