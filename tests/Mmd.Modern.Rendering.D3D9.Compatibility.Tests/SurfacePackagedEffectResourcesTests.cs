using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

// Reflection keeps the pre-implementation run behavioral: missing new contracts fail assertions,
// rather than preventing the test assembly from compiling. No native DLL or GPU is loaded.
public sealed class SurfacePackagedEffectResourcesTests
{
    [Fact]
    public void BindingCanonicalizesWindowsPathsWithoutOpeningEitherLocation()
    {
        var binding = Binding(@"z:/missing/./effects/../shared/root.fx", @"g:/missing/cache/./root.fx");
        Assert.Equal(@"Z:\missing\shared\root.fx", Property<string>(binding, "OriginAbsolutePath"));
        Assert.Equal(@"G:\missing\cache\root.fx", Property<string>(binding, "MaterializedAbsolutePath"));
        Assert.All(binding.GetType().GetProperties(), property => Assert.False(property.CanWrite));
    }

    [Theory]
    [InlineData("relative.fx")]
    [InlineData("C:relative.fx")]
    [InlineData("C:\\")]
    [InlineData("C:\\folder\\")]
    [InlineData("C:/folder/")]
    [InlineData("C:\\folder\\..")]
    [InlineData("\\root.fx")]
    [InlineData("\\\\server\\share\\root.fx")]
    [InlineData("\\\\?\\C:\\root.fx")]
    [InlineData("C:\\..\\escape.fx")]
    [InlineData("C:\\safe\\..\\..\\escape.fx")]
    [InlineData("C:\\bad\0.fx")]
    [InlineData("C:\\root.fx:stream")]
    [InlineData("C:\\bad?name.fx")]
    [InlineData("C:\\bad|name.fx")]
    [InlineData("C:\\folder.\\root.fx")]
    [InlineData("C:\\folder \\root.fx")]
    [InlineData("C:\\NUL.fx")]
    public void BothIdentitiesRejectNoncanonicalOrAmbiguousWindowsSyntax(string invalid)
    {
        Assert.Throws<ArgumentException>(() => Binding(invalid, @"G:\cache\root.fx"));
        Assert.Throws<ArgumentException>(() => Binding(@"Z:\source\root.fx", invalid));
    }

    [Fact]
    public void UnicodeAndPerPathBudgetsAreStrictWithoutFileIo()
    {
        var invalidUnicode = @"Z:\source\" + (char)0xd800 + ".fx";
        Assert.Throws<ArgumentException>(() => Binding(invalidUnicode, @"G:\cache\root.fx"));
        Assert.Throws<ArgumentException>(() => Binding(@"Z:\source\root.fx", invalidUnicode));
        var oversized = @"Z:\" + new string('\u8a9e', 10923);
        Assert.Throws<ArgumentException>(() => Binding(oversized, @"G:\cache\root.fx"));
        Assert.Throws<ArgumentException>(() => Binding(@"Z:\source\root.fx", oversized));
    }

    [Fact]
    public void MapCopiesItsInputAndExposesOnlyReadOnlyBindings()
    {
        var first = Binding(@"Z:\source\root.fx", @"G:\cache\root.fx");
        var input = BindingArray(first);
        var map = Map(input);
        input.SetValue(Binding(@"Z:\source\other.fx", @"G:\cache\other.fx"), 0);
        var rows = Property<IEnumerable>(map, "Bindings");
        Assert.Same(first, Assert.Single(rows.Cast<object>()));
        if (rows is IList mutable) Assert.True(mutable.IsReadOnly);
        Assert.All(map.GetType().GetProperties(), property => Assert.False(property.CanWrite));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MapRejectsCaseSeparatorAndDotAliasesInEachColumn(bool originCollision)
    {
        var first = Binding(@"Z:\source\root.fx", @"G:\cache\root.fx");
        var second = originCollision
            ? Binding(@"z:/source/sub/../ROOT.fx", @"G:\cache\other.fx")
            : Binding(@"Z:\source\other.fx", @"g:/cache/sub/../ROOT.fx");
        Assert.Throws<ArgumentException>(() => Map(BindingArray(first, second)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossColumnOverlapRejectsChainsAndCyclesRegardlessOfRowOrder(bool reverse)
    {
        var a = Binding(@"Z:\source\root.fx", @"G:\cache\root.fx");
        var b = Binding(@"g:/cache/ROOT.fx", @"G:\cache\other.fx");
        Assert.Throws<ArgumentException>(() => Map(BindingArray(reverse ? b : a, reverse ? a : b)));
        var c = Binding(@"G:\cache\root.fx", @"Z:\source\root.fx");
        Assert.Throws<ArgumentException>(() => Map(BindingArray(reverse ? c : a, reverse ? a : c)));
        var identity = Binding(@"G:\cache\identity.fx", @"g:/cache/IDENTITY.fx");
        Assert.Single(Property<IEnumerable>(Map(BindingArray(identity)), "Bindings").Cast<object>());
    }

    [Fact]
    public void MapRejectsNullRowsAndEnforcesRowAndAggregateUtf8Budgets()
    {
        var rowType = RequiredType("D3D9EffectResourceBinding");
        var nullRow = Array.CreateInstance(rowType, 1);
        Assert.Throws<ArgumentNullException>(() => Map(nullRow));
        var excessive = Array.CreateInstance(rowType, 65537);
        for (var i = 0; i < excessive.Length; i++) excessive.SetValue(Binding($@"Z:\source\{i}.fx", $@"G:\cache\{i}.fx"), i);
        Assert.Throws<ArgumentException>(() => Map(excessive));
        var large = Array.CreateInstance(rowType, 300);
        var segment = new string('x', 30000);
        for (var i = 0; i < large.Length; i++) large.SetValue(Binding($@"Z:\{i}\{segment}", $@"G:\{i}\{segment}"), i);
        Assert.Throws<ArgumentException>(() => Map(large));
    }

    [Fact]
    public void ResourceConfigurationRequiresEveryNormalizedFileRootInTheLocalColumn()
    {
        var map = Map(BindingArray(Binding(@"Z:\source\root.fx", @"G:\cache\root.fx")));
        var assignment = new D3D9ObjectFxAssignment(1, -1, D3D9ObjectFxAssignmentMode.File, @"g:/cache/sub/../ROOT.fx");
        var configured = Configuration(new[] { assignment }, map);
        Assert.Same(map, Property<object>(configured, "Resources"));
        Assert.Throws<ArgumentException>(() => Configuration(new[] {
            new D3D9ObjectFxAssignment(1, -1, D3D9ObjectFxAssignmentMode.File, @"Z:\source\root.fx") }, map));
        Assert.Throws<ArgumentException>(() => Configuration(new[] {
            new D3D9ObjectFxAssignment(1, -1, D3D9ObjectFxAssignmentMode.File, @"G:\cache\missing.fx") }, map));
        var legacy = new D3D9ObjectFxConfiguration(new[] { assignment });
        Assert.Null(typeof(D3D9ObjectFxConfiguration).GetProperty("Resources")!.GetValue(legacy));
        Assert.NotNull(typeof(D3D9ObjectFxConfiguration).GetConstructor(new[] { typeof(IEnumerable<D3D9ObjectFxAssignment>) }));
    }

    [Fact]
    public void ConfigurationCapsCombinedCopiedAssignmentAndResourcePathsAt16MiB()
    {
        // Both individual tables are below 16 MiB, but their one-shot native copy is above it.
        var bindings = Array.CreateInstance(RequiredType("D3D9EffectResourceBinding"), 260);
        var segment = new string('x', 32000);
        for (var i = 0; i < bindings.Length; i++)
            bindings.SetValue(Binding($@"Z:\{i}\{segment}", $@"G:\{i}\{segment}"), i);
        var map = Map(bindings);
        _ = Configuration(Array.Empty<D3D9ObjectFxAssignment>(), map);
        var assignments = Enumerable.Range(1, 5).Select(i => new D3D9ObjectFxAssignment((ulong)i, -1,
            D3D9ObjectFxAssignmentMode.File, $@"G:\0\{segment}")).ToArray();
        _ = new D3D9ObjectFxConfiguration(assignments);
        Assert.Throws<ArgumentException>(() => Configuration(assignments, map));
    }

    [Fact]
    public void ResourceRowsAndV2HeaderHaveIndependent48ByteLayoutsAndCdeclExport()
    {
        var row = RequiredType("NativeEffectResourceBinding");
        var config = RequiredType("NativeObjectFxConfigurationV2");
        Assert.Equal(48, Marshal.SizeOf(row));
        Assert.Equal(48, Marshal.SizeOf(config));
        Assert.Equal(8, Marshal.OffsetOf(row, "OriginUtf8").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf(row, "OriginLength").ToInt32());
        Assert.Equal(24, Marshal.OffsetOf(row, "MaterializedUtf8").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf(row, "MaterializedLength").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf(config, "Assignments").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf(config, "AssignmentCount").ToInt32());
        Assert.Equal(24, Marshal.OffsetOf(config, "Resources").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf(config, "ResourceCount").ToInt32());
        var call = RequiredType("NativeSurfaceExports+ConfigureObjectFxV2Call");
        Assert.Equal(CallingConvention.Cdecl, call.GetCustomAttribute<UnmanagedFunctionPointerAttribute>()!.CallingConvention);
        Assert.NotNull(typeof(ID3D9SurfaceNativeApi).GetProperty("SupportsObjectFxResources"));
    }

    [Fact]
    public void ResourceAndSceneFeatureBitsAreRecognizedWhileUnknownBitsStillFail()
    {
        (NativeAbi.Valid() with { SupportedFeatures = 1023 }).Validate();
        (NativeAbi.Valid() with { SupportedFeatures = 2047 }).Validate();
        (NativeAbi.Valid() with { SupportedFeatures = 4095 }).Validate();
        (NativeAbi.Valid() with { SupportedFeatures = 8191 }).Validate();
        Assert.Throws<NotSupportedException>(() => (NativeAbi.Valid() with { SupportedFeatures = 65535 }).Validate());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task PackagedConfigurationRequiresBothFeature512AndV2BeforeCreate(bool feature, bool export)
    {
        var fake = new ResourceApi { Features = feature ? 1023UL : 511UL, SupportsObjectFxResources = export };
        var config = Configuration(Array.Empty<D3D9ObjectFxAssignment>(), Map(BindingArray()));
        await Assert.ThrowsAsync<NotSupportedException>(() => ExperimentalD3D9SurfaceSession.CreateObjectFxForTestingAsync(
            () => fake, new(4, 2), config, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "query", "unload" }, fake.Calls.Select(item => item.Name));
    }

    [Fact]
    public async Task V2OnlyExportWorksAndUsesOneOwnerWithoutChangingFrameScope()
    {
        var fake = new ResourceApi { SupportsObjectFx = false };
        var config = Configuration(Array.Empty<D3D9ObjectFxAssignment>(), Map(BindingArray()));
        var caller = Environment.CurrentManagedThreadId;
        await using (var session = await ExperimentalD3D9SurfaceSession.CreateObjectFxForTestingAsync(
            () => fake, new(4, 2), config, TestContext.Current.CancellationToken))
        {
            await session.CaptureAsync(ConfiguredSnapshot(), cancellationToken: TestContext.Current.CancellationToken);
            await session.ResizeAsync(4, 2, TestContext.Current.CancellationToken);
        }
        Assert.Same(config, fake.Configuration);
        Assert.Equal(new[] { "query", "create", "configure", "capture", "resize", "destroy", "unload" }, fake.Calls.Select(item => item.Name));
        Assert.Single(fake.Calls.Select(item => item.Thread).Distinct());
        Assert.DoesNotContain(caller, fake.Calls.Select(item => item.Thread));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigureV2PinsCompleteCopiedRowsAndReleasesTheChoiceAfterFailure(bool empty)
    {
        var probe = new V2Probe { FailOnce = true };
        var exports = Exports(probe);
        using var api = new NativeSurfaceApi(1, exports, _ => { });
        var rows = empty ? BindingArray() : BindingArray(
            Binding(@"Z:\source\root.fx", @"G:\cache\root.fx"),
            Binding(@"Z:\source\nested\common.fxh", @"G:\cache\nested\common.fxh"));
        var config = Configuration(empty ? Array.Empty<D3D9ObjectFxAssignment>() : new[] {
            new D3D9ObjectFxAssignment(91, -1, D3D9ObjectFxAssignmentMode.File, @"G:\cache\root.fx") }, Map(rows));
        var handle = api.Create(new(4, 2));
        var failure = Assert.Throws<D3D9SurfaceNativeException>(() => api.ConfigureObjectFx(handle, config));
        Assert.Equal(-1, failure.Status);
        api.ConfigureObjectFx(handle, config);
        Assert.Throws<InvalidOperationException>(() => api.ConfigureObjectFx(handle, config));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(2, probe.Calls);
        Assert.Equal(empty ? Array.Empty<string>() : new[] { @"Z:\source\root.fx", @"Z:\source\nested\common.fxh" }, probe.Origins);
        Assert.Equal(empty ? Array.Empty<string>() : new[] { @"G:\cache\root.fx", @"G:\cache\nested\common.fxh" }, probe.Locals);
        Assert.Equal(empty ? Array.Empty<string>() : new[] { @"G:\cache\root.fx" }, probe.FxRoots);
    }

    [Fact]
    public void AssetsRequireEveryMaterializedResourceEvenWhenOnlyTheFxRootIsAssigned()
    {
        using var tree = new ResourceTree();
        var config = tree.Configuration();
        Assert.Throws<ArgumentException>(() => D3D9ObjectFxAssets.Open(config, new[] { tree.RootFx }));
        tree.AssertWritable();
        using (var assets = D3D9ObjectFxAssets.Open(config, tree.Paths)) tree.AssertLocked();
        tree.AssertWritable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackagedResourcesStayLeasedThroughDestroyAndUnloadRetries(bool unloadFails)
    {
        using var tree = new ResourceTree();
        using var assets = D3D9ObjectFxAssets.Open(tree.Configuration(), tree.Paths);
        var fake = new ResourceApi { LockedCheck = tree.AssertLocked, DestroyFailures = unloadFails ? 0 : 1, UnloadFailures = unloadFails ? 1 : 0 };
        var session = await ExperimentalD3D9SurfaceSession.CreateObjectFxWithAssetsForTestingAsync(
            () => fake, new(4, 2), assets, TestContext.Current.CancellationToken);
        assets.Dispose();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisposeAsync().AsTask());
            tree.AssertLocked();
            await session.DisposeAsync();
            tree.AssertWritable();
        }
        finally { await session.DisposeAsync(); }
    }

    private static Type RequiredType(string name)
    {
        var type = typeof(D3D9ObjectFxConfiguration).Assembly.GetType("Mmd.Modern.Rendering.D3D9.Compatibility." + name);
        Assert.NotNull(type);
        return type!;
    }

    private static T Property<T>(object value, string name) => Assert.IsAssignableFrom<T>(value.GetType().GetProperty(name)!.GetValue(value));
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static object Construct(Type type, params object?[] args)
    {
        try { return Activator.CreateInstance(type, args)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static object Binding(string origin, string local) => Construct(RequiredType("D3D9EffectResourceBinding"), origin, local);
    private static Array BindingArray(params object[] bindings)
    {
        var array = Array.CreateInstance(RequiredType("D3D9EffectResourceBinding"), bindings.Length);
        for (var i = 0; i < bindings.Length; i++) array.SetValue(bindings[i], i);
        return array;
    }
    private static object Map(Array bindings) => Construct(RequiredType("D3D9EffectResourceMap"), bindings);
    private static D3D9ObjectFxConfiguration Configuration(D3D9ObjectFxAssignment[] rows, object map) =>
        (D3D9ObjectFxConfiguration)Construct(typeof(D3D9ObjectFxConfiguration), rows, map);
    private static D3D9SurfaceSnapshot ConfiguredSnapshot()
    {
        var packets = SurfaceSessionTests.Packets();
        return D3D9SurfaceSnapshot.CopyConfiguredObjectFx(packets.Frame, packets.Visual);
    }

    private static NativeSurfaceExports Exports(V2Probe probe)
    {
        var exports = new NativeSurfaceExports
        {
            QueryAbi = (ref NativeAbi abi, ref NativeError error) => { abi = NativeAbi.Valid() with { SupportedFeatures = 1023 }; return 0; },
            Create = (in NativeCreate create, out ulong handle, ref NativeError error) => { handle = 7; return 0; },
            Capture = (ulong handle, in NativeCapture capture, ref NativeCaptureResult result, ref NativeError error) => 0,
            Resize = (ulong handle, in NativeResize resize, ref NativeError error) => 0,
            Destroy = (ulong handle, ref NativeError error) => 0
        };
        var property = typeof(NativeSurfaceExports).GetProperty("ConfigureObjectFxV2", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        var method = typeof(V2Probe).GetMethod(nameof(V2Probe.Configure))!.MakeGenericMethod(RequiredType("NativeObjectFxConfigurationV2"));
        property!.SetValue(exports, method.CreateDelegate(property.PropertyType, probe));
        return exports;
    }

    private sealed class V2Probe
    {
        internal int Calls;
        internal bool FailOnce;
        internal string[] Origins = Array.Empty<string>(), Locals = Array.Empty<string>(), FxRoots = Array.Empty<string>();
        public int Configure<T>(ulong handle, in T input, ref NativeError error) where T : struct
        {
            Calls++;
            Assert.Equal(7UL, handle);
            Assert.Equal(48U, Field<uint>(input, "Size"));
            Assert.Equal(2U, Field<uint>(input, "Version"));
            Assert.Equal(0U, Field<uint>(input, "Reserved0") | Field<uint>(input, "Reserved1"));
            GC.Collect(); // Collect before dereferencing: all synchronous ABI buffers must remain pinned.
            var count = checked((int)Field<ulong>(input, "ResourceCount"));
            var pointer = Field<nint>(input, "Resources");
            if (count == 0) Assert.Equal(0, pointer);
            var origins = new List<string>();
            var locals = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure(pointer + 48 * i, RequiredType("NativeEffectResourceBinding"))!;
                Assert.Equal(48U, Field<uint>(row, "Size"));
                Assert.Equal(1U, Field<uint>(row, "Version"));
                Assert.Equal(0U, Field<uint>(row, "Reserved0") | Field<uint>(row, "Reserved1"));
                origins.Add(ReadPath(Field<nint>(row, "OriginUtf8"), Field<ulong>(row, "OriginLength")));
                locals.Add(ReadPath(Field<nint>(row, "MaterializedUtf8"), Field<ulong>(row, "MaterializedLength")));
            }
            var roots = new List<string>();
            var assignmentCount = checked((int)Field<ulong>(input, "AssignmentCount"));
            var assignments = Field<nint>(input, "Assignments");
            if (assignmentCount == 0) Assert.Equal(0, assignments);
            for (var i = 0; i < assignmentCount; i++)
            {
                var row = Marshal.PtrToStructure<NativeObjectFxAssignment>(assignments + 48 * i);
                Assert.Equal(48U, row.Size);
                Assert.Equal(1U, row.Version);
                Assert.Equal(0U, row.Reserved0 | row.Reserved1);
                roots.Add(ReadPath(row.Utf8Path, row.PathLength));
            }
            GC.Collect(); // In-call collection must not invalidate any pinned row or path.
            Origins = origins.ToArray(); Locals = locals.ToArray(); FxRoots = roots.ToArray();
            if (FailOnce && Calls == 1) { error.Status = -1; return -1; }
            return 0;
        }
        private static string ReadPath(nint pointer, ulong length)
        {
            Assert.NotEqual(0, pointer);
            var bytes = new byte[checked((int)length)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            Assert.DoesNotContain((byte)0, bytes);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
    }

    private sealed class ResourceApi : ID3D9SurfaceNativeApi
    {
        internal readonly ConcurrentQueue<(string Name, int Thread)> Calls = new();
        internal ulong Features = 1023;
        internal int DestroyFailures, UnloadFailures;
        internal Action? LockedCheck;
        internal D3D9ObjectFxConfiguration? Configuration;
        public bool SupportsObjectFx { get; init; } = true;
        public bool SupportsObjectFxResources { get; init; } = true;
        private void Record(string name) { LockedCheck?.Invoke(); Calls.Enqueue((name, Environment.CurrentManagedThreadId)); }
        public NativeAbi QueryAbi() { Record("query"); return NativeAbi.Valid() with { SupportedFeatures = Features }; }
        public ulong Create(D3D9SurfaceSessionOptions options) { Record("create"); return 7; }
        public void ConfigureObjectFx(ulong handle, D3D9ObjectFxConfiguration configuration) { Record("configure"); Configuration = configuration; }
        public D3D9SurfaceCapture Capture(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order)
        {
            Record("capture");
            Assert.Equal(257UL, snapshot.RequiredFeatures);
            return new(4, 2, new byte[32], new NativeCaptureResult { Size = 80, Version = 1, Width = 4, Height = 2, SourceFrame = 4706, Flags = 8, RgbaBytesRequired = 32 });
        }
        public void Resize(ulong handle, int width, int height) => Record("resize");
        public void Destroy(ulong handle) { Record("destroy"); if (DestroyFailures-- > 0) throw new InvalidOperationException("destroy retry"); }
        public void Dispose() { Record("unload"); if (UnloadFailures-- > 0) throw new InvalidOperationException("unload retry"); }
    }

    private sealed class ResourceTree : IDisposable
    {
        private readonly string directory = Path.Combine(MdaWorkRoot.Path(@"compat\20261105\resources460\managed-tests"), Guid.NewGuid().ToString("N"));
        internal string RootFx => Path.Combine(directory, "root.fx");
        internal string Include => Path.Combine(directory, "common.fxh");
        internal string[] Paths => new[] { RootFx, Include };
        internal ResourceTree() { Directory.CreateDirectory(directory); File.WriteAllText(RootFx, "root"); File.WriteAllText(Include, "include"); }
        internal D3D9ObjectFxConfiguration Configuration() => SurfacePackagedEffectResourcesTests.Configuration(new[] {
            new D3D9ObjectFxAssignment(1, -1, D3D9ObjectFxAssignmentMode.File, RootFx) }, Map(BindingArray(
                Binding(@"Z:\source\root.fx", RootFx), Binding(@"Z:\source\common.fxh", Include))));
        internal void AssertLocked() { foreach (var path in Paths) Assert.ThrowsAny<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)); }
        internal void AssertWritable() { foreach (var path in Paths) using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read)) { } }
        public void Dispose() { foreach (var path in Paths) File.Delete(path); Directory.Delete(directory); }
    }
}
