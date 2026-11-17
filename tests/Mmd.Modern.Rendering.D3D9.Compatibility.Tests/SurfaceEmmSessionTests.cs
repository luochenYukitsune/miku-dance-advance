using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

public sealed class SurfaceEmmSessionTests
{
    private static readonly Guid Model = new("f9cd09d9-ecb9-4794-951d-357cfcf61110");
    private static readonly Guid Accessory = new("f9cd09d9-ecb9-4794-951d-357cfcf61111");

    [Fact]
    public void DedicatedAbiLayoutsKeepEveryFieldAtItsNativeOffset()
    {
        Assert.Equal(16, Marshal.SizeOf<NativeUtf8Span>());
        Assert.Equal(40, Marshal.SizeOf<NativeEmmBinding>());
        Assert.Equal(40, Marshal.SizeOf<NativeEmmRootSeed>());
        Assert.Equal(48, Marshal.SizeOf<NativeEmmEntry>());
        Assert.Equal(120, Marshal.SizeOf<NativeEmmSection>());
        Assert.Equal(80, Marshal.SizeOf<NativeEmmConfiguration>());
        Assert.Equal(24, Marshal.OffsetOf<NativeEmmBinding>(nameof(NativeEmmBinding.ObjectId)).ToInt32());
        Assert.Equal(88, Marshal.OffsetOf<NativeEmmSection>(nameof(NativeEmmSection.OwnerSubset)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<NativeEmmSection>(nameof(NativeEmmSection.Entries)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<NativeEmmConfiguration>(nameof(NativeEmmConfiguration.Resources)).ToInt32());
        Assert.Equal(CallingConvention.Cdecl, typeof(NativeSurfaceExports.ConfigureEmmCall)
            .GetCustomAttributes(typeof(UnmanagedFunctionPointerAttribute), false).Cast<UnmanagedFunctionPointerAttribute>().Single().CallingConvention);
        (NativeAbi.Valid() with { SupportedFeatures = 6143 }).Validate();
        (NativeAbi.Valid() with { SupportedFeatures = 8191 }).Validate();
        Assert.Throws<NotSupportedException>(() => (NativeAbi.Valid() with { SupportedFeatures = 65535 }).Validate());
    }

    [Fact]
    public void IdentityMapRequiresExactlyAllObjectsAndCopiesCallerState()
    {
        var configuration = Configuration();
        var ids = Ids();
        var selected = new D3D9EmmSessionConfiguration(configuration, ids);
        ids[Model] = 777;
        Assert.Equal(91UL, selected.NativeObjectIds[Model]);
        Assert.Throws<ArgumentException>(() => new D3D9EmmSessionConfiguration(configuration, new Dictionary<Guid, ulong> { [Model] = 91 }));
        Assert.Throws<ArgumentException>(() => new D3D9EmmSessionConfiguration(configuration, new Dictionary<Guid, ulong> { [Model] = 91, [Accessory] = 91 }));
        Assert.Throws<ArgumentException>(() => new D3D9EmmSessionConfiguration(configuration, new Dictionary<Guid, ulong> { [Model] = 0, [Accessory] = 92 }));
        ids[Guid.NewGuid()] = 100;
        Assert.Throws<ArgumentException>(() => new D3D9EmmSessionConfiguration(configuration, ids));
    }

    [Fact]
    public void TransportPreservesAbsentNoneShowOwnerKindsAndMaterializedPaths()
    {
        var probe = new Probe();
        using var api = probe.Api();
        var handle = api.Create(new(4, 2));
        api.ConfigureEmm(handle, new(Configuration(), Ids()));
        Assert.Equal(new[] { ("Pmd7", 91UL, 1U), ("Acs2", 92UL, 2U) }, probe.Bindings);
        Assert.Equal(new[] { (91UL, 1U, @"G:\cache\seed.fx"), (92UL, 2U, "") }, probe.Roots);
        var main = probe.Sections[0];
        Assert.Equal(("Effect", "", "", "", -1, 1U, "none"), main.Header);
        Assert.Equal(new[] { ("Pmd7", -1, 2U, ""), ("Pmd7", 2, 1U, "none"), ("Pmd7", 3, 6U, "") }, main.Entries);
        var target = probe.Sections[1];
        Assert.Equal(("Effect@Depth(2)", "Depth", "Acs2", "Parent(1)", -1, 0U, ""), target.Header);
        Assert.Equal(new[] { ("Pmd7", -1, 1U, "hide"), ("Pmd7", 2, 1U, "main_default"),
            ("Acs2", -1, 3U, @"G:\cache\target.fx") }, target.Entries);
        Assert.Equal(new[] { (@"Z:\source\seed.fx", @"G:\cache\seed.fx"), (@"Z:\source\target.fx", @"G:\cache\target.fx") }, probe.Resources);
        Assert.Throws<InvalidOperationException>(() => api.ConfigureEmm(handle, new(Configuration(), Ids())));
    }

    [Theory]
    [InlineData(2047UL, true)]
    [InlineData(6143UL, false)]
    public async Task EmmAdmissionRequiresDedicatedFeatureAndExportBeforeCreatingDevice(ulong features, bool export)
    {
        var probe = new Probe { Features = features, EmmExport = export };
        await Assert.ThrowsAsync<NotSupportedException>(() => ExperimentalD3D9SurfaceSession.CreateEmmForTestingAsync(
            probe.Api, new(4, 2), Configuration(), Ids(), TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "query", "unload" }, probe.Calls.Select(row => row.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmmCaptureAndPreviewUseOneOwnerDistinctScopeAndFrozenSnapshot(bool preview)
    {
        var probe = new Probe();
        var ids = Ids();
        var caller = Environment.CurrentManagedThreadId;
        await using (var session = await (preview
            ? ExperimentalD3D9SurfaceSession.CreateEmmPreviewForTestingAsync(probe.Api, (nint)1234, new(4, 2), Configuration(), ids, TestContext.Current.CancellationToken)
            : ExperimentalD3D9SurfaceSession.CreateEmmForTestingAsync(probe.Api, new(4, 2), Configuration(), ids, TestContext.Current.CancellationToken)))
        {
            ids[Model] = 777;
            var packets = SurfaceSessionTests.Packets();
            var snapshot = D3D9SurfaceSnapshot.CopyConfiguredEmm(packets.Frame, packets.Visual);
            Assert.Equal(4, (int)snapshot.Scope);
            Assert.Equal(5377UL, snapshot.RequiredFeatures);
            Assert.Throws<InvalidOperationException>(() => { _ = preview
                ? session.PresentAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken)
                : (Task)session.CaptureAsync(SurfaceSessionTests.Snapshot(), cancellationToken: TestContext.Current.CancellationToken); });
            if (preview)
            {
                var result = await session.PresentAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(result.ConfiguredEmmMetadata);
                Assert.False(result.ConfiguredSceneFxMetadata);
                Assert.False(result.ConfiguredObjectFxMetadata);
            }
            else
            {
                var result = await session.CaptureAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(result.ConfiguredEmmMetadata);
                Assert.False(result.ConfiguredSceneFxMetadata);
                Assert.False(result.ConfiguredObjectFxMetadata);
            }
            await session.ResizeAsync(4, 2, TestContext.Current.CancellationToken);
            if (preview) await session.PresentAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken);
            else await session.CaptureAsync(snapshot, cancellationToken: TestContext.Current.CancellationToken);
        }
        Assert.Equal(new[] { "query", preview ? "create preview" : "create", "configure EMM", preview ? "present" : "capture", "resize",
            preview ? "present" : "capture", "destroy", "unload" }, probe.Calls.Select(row => row.Name));
        Assert.Single(probe.Calls.Select(row => row.Thread).Distinct());
        Assert.DoesNotContain(caller, probe.Calls.Select(row => row.Thread));
    }

    [Theory]
    [InlineData(false, 16U)]
    [InlineData(false, 33U)]
    [InlineData(true, 20U)]
    [InlineData(true, 52U)]
    public void EmmResultRejectsMixedOrDifferentScopes(bool preview, uint flags)
    {
        var probe = new Probe { ResultFlags = flags };
        using var api = probe.Api();
        var handle = preview ? api.CreatePreview((nint)1234, new(4, 2)) : api.Create(new(4, 2));
        api.ConfigureEmm(handle, new(Configuration(), Ids()));
        var packets = SurfaceSessionTests.Packets();
        var snapshot = D3D9SurfaceSnapshot.CopyConfiguredEmm(packets.Frame, packets.Visual);
        Assert.Throws<InvalidDataException>(() => { if (preview) api.Present(handle, snapshot, 0); else api.Capture(handle, snapshot, 0); });
    }

    [Fact]
    public async Task VisibilityOnlyConfigurationNeedsNoFileResources()
    {
        var configuration = Configuration(visibilityOnly: true);
        using var assets = D3D9EmmRuntimeAssets.Open(configuration);
        Assert.Empty(assets.CompleteDependencyPaths);
        var probe = new Probe();
        await using var session = await ExperimentalD3D9SurfaceSession.CreateEmmWithAssetsForTestingAsync(
            probe.Api, new(4, 2), assets, Ids(), TestContext.Current.CancellationToken);
        Assert.Empty(probe.Resources);
        Assert.All(probe.Roots, row => Assert.Equal("", row.Item3));
        Assert.Equal(("Pmd7", -1, 2U, ""), Assert.Single(Assert.Single(probe.Sections).Entries));
    }

    [Fact]
    public async Task CancelledInitializationRetainsEveryResourceUntilFailedDestroyRetryCompletes()
    {
        var directory = Path.Combine(MdaWorkRoot.Path(@"compat\20261106\emm523"), "cpu-assets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var root = Path.Combine(directory, "seed.fx");
        var target = Path.Combine(directory, "target.fx");
        File.WriteAllText(root, "task-owned seed"); File.WriteAllText(target, "task-owned target");
        D3D9EmmRuntimeAssets? assets = null;
        Task<ExperimentalD3D9SurfaceSession>? creation = null;
        D3D9SurfaceInitializationCleanupException? failure = null;
        var probe = new Probe { BlockConfigure = true, DestroyFailures = 1 };
        try
        {
            assets = D3D9EmmRuntimeAssets.Open(Configuration(root, target));
            using var cancellation = new CancellationTokenSource();
            creation = ExperimentalD3D9SurfaceSession.CreateEmmWithAssetsForTestingAsync(probe.Api, new(4, 2), assets, Ids(), cancellation.Token);
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            cancellation.Cancel(); assets.Dispose();
            Assert.Throws<IOException>(() => File.Open(root, FileMode.Open, FileAccess.Write, FileShare.Read).Dispose());
            Assert.Throws<IOException>(() => File.Open(target, FileMode.Open, FileAccess.Write, FileShare.Read).Dispose());
            probe.Release.Set();
            failure = await Assert.ThrowsAsync<D3D9SurfaceInitializationCleanupException>(() => creation);
            Assert.IsAssignableFrom<OperationCanceledException>(Assert.IsType<AggregateException>(failure.InnerException).InnerExceptions[0]);
            Assert.DoesNotContain(probe.Calls, row => row.Name == "unload");
            Assert.Throws<IOException>(() => File.Open(target, FileMode.Open, FileAccess.Write, FileShare.Read).Dispose());
            await failure.DisposeAsync(); failure = null;
            using (File.Open(root, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
            using (File.Open(target, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
            Assert.Equal(2, probe.Calls.Count(row => row.Name == "destroy"));
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

    private static Dictionary<Guid, ulong> Ids() => new() { [Model] = 91, [Accessory] = 92 };

    private static D3D9EmmRuntimeConfiguration Configuration(string? localRoot = null, string? localTarget = null, bool visibilityOnly = false)
    {
        var text = "[Info]\nVersion=3\n[Object]\nPmd7=C:\\actor.pmx\nAcs2=C:\\stage.x\n[Effect]\nDefault=none\nPmd7.show=false\n" +
            (visibilityOnly ? "" : "Pmd7[2]=none\nPmd7[3].show=true\n[Effect@Depth(2)]\nOwner=Acs2@Parent(1)\nPmd7=hide\nPmd7[2]=main_default\nAcs2=target.fx\nAcs2.show=false\n");
        var document = EmmReader.Read(Encoding.UTF8.GetBytes(text));
        var plan = EmmImportPlan.Create(document, @"Z:\source\mapping.emm", new[] { new EmmObjectBinding(Model, true, @"C:\actor.pmx", 0),
            new EmmObjectBinding(Accessory, false, @"C:\stage.x", 1) }, @"Z:\source");
        return D3D9EmmRuntimeConfiguration.Create(plan,
            new[] { new D3D9EmmRuntimeRootSeed(Model, true, visibilityOnly ? null : @"Z:\source\seed.fx"), new D3D9EmmRuntimeRootSeed(Accessory, false) },
            new(visibilityOnly ? Array.Empty<D3D9EffectResourceBinding>() : new[] { new D3D9EffectResourceBinding(@"Z:\source\seed.fx", localRoot ?? @"G:\cache\seed.fx"),
                new D3D9EffectResourceBinding(@"Z:\source\target.fx", localTarget ?? @"G:\cache\target.fx") }));
    }

    private sealed class Probe
    {
        internal readonly ConcurrentQueue<(string Name, int Thread)> Calls = new();
        internal ulong Features = 6143;
        internal bool EmmExport = true;
        internal bool BlockConfigure;
        internal int DestroyFailures;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal uint? ResultFlags;
        internal (string, ulong, uint)[] Bindings = [];
        internal (ulong, uint, string)[] Roots = [];
        internal (string, string)[] Resources = [];
        internal readonly List<((string, string, string, string, int, uint, string) Header, (string, int, uint, string)[] Entries)> Sections = [];
        private void Record(string name) => Calls.Enqueue((name, Environment.CurrentManagedThreadId));
        internal NativeSurfaceApi Api() => new(1, new()
        {
            QueryAbi = (ref NativeAbi abi, ref NativeError error) => { Record("query"); abi = NativeAbi.Valid() with { SupportedFeatures = Features }; return 0; },
            Create = (in NativeCreate input, out ulong handle, ref NativeError error) => { Record("create"); handle = 7; return 0; },
            CreatePreview = (in NativeCreatePreview input, out ulong handle, ref NativeError error) => { Record("create preview"); handle = 7; return 0; },
            ConfigureEmm = EmmExport ? Configure : null,
            Capture = (ulong handle, in NativeCapture input, ref NativeCaptureResult result, ref NativeError error) =>
            {
                Record("capture"); Assert.Equal(4U, input.Flags); Assert.Equal(5377UL, input.RequiredFeatures);
                result = new() { Size = 80, Version = 1, Width = 4, Height = 2, SourceFrame = 4706, RgbaBytesRequired = 32,
                    Flags = ResultFlags ?? 32, CompletedCaptures = 1 }; return 0;
            },
            Present = (ulong handle, in NativePresent input, ref NativePresentResult result, ref NativeError error) =>
            {
                Record("present"); Assert.Equal(4U, input.Flags); Assert.Equal(5505UL, input.RequiredFeatures);
                result = new() { Size = 80, Version = 1, Width = 4, Height = 2, SourceFrame = 4706,
                    Flags = ResultFlags ?? 36, CompletedPresents = 1 }; return 0;
            },
            Resize = (ulong handle, in NativeResize input, ref NativeError error) => { Record("resize"); return 0; },
            Destroy = (ulong handle, ref NativeError error) => { Record("destroy"); return DestroyFailures-- > 0 ? -9 : 0; }
        }, _ => Record("unload"));

        private int Configure(ulong handle, in NativeEmmConfiguration input, ref NativeError error)
        {
            Record("configure EMM");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.Equal(80U, input.Size); Assert.Equal(1U, input.Version);
            Bindings = Rows<NativeEmmBinding>(input.Bindings, input.BindingCount).Select(row => (Utf8(row.Key), row.ObjectId, row.Kind)).ToArray();
            Roots = Rows<NativeEmmRootSeed>(input.RootSeeds, input.RootSeedCount).Select(row => (row.ObjectId, row.Kind, Utf8(row.EffectPath))).ToArray();
            Resources = Rows<NativeEffectResourceBinding>(input.Resources, input.ResourceCount).Select(row =>
                (Utf8(new() { Data = row.OriginUtf8, Length = row.OriginLength }), Utf8(new() { Data = row.MaterializedUtf8, Length = row.MaterializedLength }))).ToArray();
            foreach (var section in Rows<NativeEmmSection>(input.Sections, input.SectionCount))
                Sections.Add(((Utf8(section.Name), Utf8(section.BaseName), Utf8(section.OwnerKey), Utf8(section.ParentTarget), section.OwnerSubset,
                    section.Flags, Utf8(section.DefaultPath)), Rows<NativeEmmEntry>(section.Entries, section.EntryCount).Select(row =>
                        (Utf8(row.Key), row.Subset, row.Flags, Utf8(row.Path))).ToArray()));
            if (BlockConfigure)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("EMM configure was not released.");
            }
            return 0;
        }
        private static IEnumerable<T> Rows<T>(nint pointer, ulong count) where T : struct => Enumerable.Range(0, checked((int)count))
            .Select(index => Marshal.PtrToStructure<T>(pointer + index * Marshal.SizeOf<T>()));
        private static string Utf8(NativeUtf8Span span)
        {
            if (span.Length == 0) return "";
            var bytes = new byte[checked((int)span.Length)]; Marshal.Copy(span.Data, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes);
        }
    }
}
