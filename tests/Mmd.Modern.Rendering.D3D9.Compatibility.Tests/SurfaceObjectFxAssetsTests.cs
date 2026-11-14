using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

// Real task-owned filesystem leases with a fake native owner. No native renderer or GPU is loaded.
public sealed class SurfaceObjectFxAssetsTests
{
    [Fact]
    public void DeclaredTreeRemainsInPlaceAndReadOnlyUntilAssetsAreDisposed()
    {
        using var tree = new AssetTree();
        var paths = tree.Paths.ToArray();
        var configuration = tree.Configuration;
        using (var assets = Open(configuration, paths))
        {
            paths[0] = tree.Missing;
            Assert.Same(configuration, assets.Configuration);
            Assert.Equal(tree.Paths, assets.Paths);
            if (assets.Paths is IList mutable) Assert.True(mutable.IsReadOnly);
            AssertTreeLocked(tree);
            Assert.Equal("root includes nested/common.fxh and textures/color.bmp", File.ReadAllText(tree.RootFx));
            Assert.Equal("include", File.ReadAllText(tree.Include));
            Assert.Equal("resource", File.ReadAllText(tree.Resource));
        }
        AssertTreeWritable(tree);
    }

    [Theory]
    [InlineData("relative.fx")]
    [InlineData("C:relative.fx")]
    [InlineData("\\\\server\\share\\file.fx")]
    [InlineData("\\\\?\\C:\\file.fx")]
    [InlineData("C:\\bad\0.fx")]
    [InlineData("C:\\file.fx:stream")]
    [InlineData("C:\\folder.\\file.fx")]
    [InlineData("C:\\folder \\file.fx")]
    [InlineData("C:\\NUL.fx")]
    public void InvalidDeclaredPathIsRejectedBeforeOpeningAnyAsset(string path)
    {
        var configuration = new D3D9ObjectFxConfiguration(Array.Empty<D3D9ObjectFxAssignment>());
        Assert.Throws<ArgumentException>(() => Open(configuration, new[] { path }));
    }

    [Fact]
    public void LoneSurrogateIsRejectedByStrictUtf8WithoutTestDataTransportReplacement()
    {
        var configuration = new D3D9ObjectFxConfiguration(Array.Empty<D3D9ObjectFxAssignment>());
        var path = @"C:\bad" + (char)0xd800 + ".fx";
        Assert.Throws<ArgumentException>(() => Open(configuration, new[] { path }));
    }

    [Fact]
    public void EveryFileAssignmentMustBeInTheDeclaredManifest()
    {
        using var tree = new AssetTree();
        Assert.Throws<ArgumentException>(() => Open(tree.Configuration, new[] { tree.Include, tree.Resource }));
        AssertTreeWritable(tree);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DuplicateCaseSeparatorOrDotAliasIsRejected(int kind)
    {
        using var tree = new AssetTree();
        var alias = kind switch
        {
            0 => tree.RootFx.ToUpperInvariant(),
            1 => tree.RootFx.Replace('\\', '/'),
            _ => Path.Combine(tree.DirectoryPath, "nested", "..", "root.fx")
        };
        Assert.Throws<ArgumentException>(() => Open(tree.Configuration, tree.Paths.Append(alias)));
        AssertTreeWritable(tree);
    }

    [Fact]
    public void TwoDeclaredHardlinkNamesCannotLeaseTheSamePhysicalFileTwice()
    {
        using var tree = new AssetTree();
        var alias = Path.Combine(tree.DirectoryPath, "alias.fx");
        Assert.True(CreateHardLink(alias, tree.RootFx, 0), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        Assert.Throws<ArgumentException>(() => Open(tree.Configuration, tree.Paths.Append(alias)));
        AssertTreeWritable(tree);
    }

    [Fact]
    public void ReparseDirectoryOrFileIsRejectedWithoutLeavingEarlierAssetsLocked()
    {
        using var tree = new AssetTree();
        var directoryLink = Path.Combine(tree.DirectoryPath, "linked");
        var fileLink = Path.Combine(tree.DirectoryPath, "linked.fx");
        Directory.CreateSymbolicLink(directoryLink, Path.GetDirectoryName(tree.Include)!);
        File.CreateSymbolicLink(fileLink, tree.RootFx);
        Assert.Throws<ArgumentException>(() => Open(tree.Configuration, tree.Paths.Append(Path.Combine(directoryLink, "common.fxh"))));
        Assert.Throws<ArgumentException>(() => Open(tree.Configuration, tree.Paths.Append(fileLink)));
        AssertTreeWritable(tree);
    }

    [Fact]
    public void RetainedAncestorsCannotBeRenamedWhileTheirDeclaredFilesAreLeased()
    {
        using var tree = new AssetTree();
        using (var assets = Open(tree.Configuration, tree.Paths))
            Assert.ThrowsAny<IOException>(() => Directory.Move(tree.DirectoryPath, tree.DirectoryPath + "-moved"));
        AssertTreeWritable(tree);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingFileOrEnumeratorFailureRollsBackEarlierFileAndAncestorHandles(bool enumerationFails)
    {
        using var tree = new AssetTree();
        if (enumerationFails)
            Assert.Throws<InvalidOperationException>(() => Open(tree.Configuration, FailingManifest(tree.RootFx)));
        else
            Assert.Throws<FileNotFoundException>(() => Open(tree.Configuration, tree.Paths.Append(tree.Missing)));
        AssertTreeWritable(tree);
        var nested = Path.GetDirectoryName(tree.Include)!;
        Directory.Move(nested, nested + "-moved");
        Directory.Move(nested + "-moved", nested);
    }

    [Fact]
    public void ManifestCountIsBoundedEvenForAnUnendingEnumerable()
    {
        var configuration = new D3D9ObjectFxConfiguration(Array.Empty<D3D9ObjectFxAssignment>());
        Assert.Throws<ArgumentException>(() => Open(configuration, Enumerable.Range(0, 65537).Select(i => @"C:\fixture\" + i + ".fx")));
    }

    [Fact]
    public async Task DisposingAssetsPreventsNewSessionsButTwoAdmittedOwnersKeepTheTreeLeased()
    {
        using var tree = new AssetTree();
        using var assets = Open(tree.Configuration, tree.Paths);
        var firstApi = new AssetApi(tree);
        var secondApi = new AssetApi(tree);
        var first = await Create(assets, firstApi, cancellationToken: TestContext.Current.CancellationToken);
        var second = await Create(assets, secondApi, preview: true, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            assets.Dispose();
            AssertTreeLocked(tree);
            var rejected = new AssetApi(tree);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Create(assets, rejected, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(rejected.Calls);
            await first.DisposeAsync();
            AssertTreeLocked(tree);
            await second.DisposeAsync();
            AssertTreeWritable(tree);
            Assert.Same(tree.Configuration, firstApi.Configuration);
            Assert.Same(tree.Configuration, secondApi.Configuration);
            Assert.Equal(new[] { "query", "create", "configure", "destroy", "unload" }, firstApi.Calls);
            Assert.Equal(new[] { "query", "preview", "configure", "destroy", "unload" }, secondApi.Calls);
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestroyOrModuleUnloadFailureKeepsAssetsUntilSuccessfulOwnerRetry(bool unloadFails)
    {
        using var tree = new AssetTree();
        using var assets = Open(tree.Configuration, tree.Paths);
        var api = new AssetApi(tree) { DestroyFailures = unloadFails ? 0 : 1, UnloadFailures = unloadFails ? 1 : 0 };
        var session = await Create(assets, api, cancellationToken: TestContext.Current.CancellationToken);
        assets.Dispose();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisposeAsync().AsTask());
            AssertTreeLocked(tree);
            await session.DisposeAsync();
            AssertTreeWritable(tree);
            Assert.Equal(unloadFails ? 1 : 2, api.Calls.Count(call => call == "destroy"));
            Assert.Equal(unloadFails ? 2 : 1, api.Calls.Count(call => call == "unload"));
        }
        finally { await session.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationFailureOrCancellationRetainsAssetsInInitializationCleanupException(bool cancelled)
    {
        using var tree = new AssetTree();
        using var assets = Open(tree.Configuration, tree.Paths);
        using var cancellation = new CancellationTokenSource();
        var api = new AssetApi(tree) { BlockConfigure = true, FailConfigure = !cancelled, DestroyFailures = 1 };
        var creation = Create(assets, api, cancellationToken: cancellation.Token);
        await api.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        assets.Dispose();
        AssertTreeLocked(tree);
        if (cancelled) cancellation.Cancel();
        api.Release.Set();
        var failure = await Assert.ThrowsAsync<D3D9SurfaceInitializationCleanupException>(() => creation);
        try
        {
            AssertTreeLocked(tree);
            var causes = Assert.IsType<AggregateException>(failure.InnerException);
            if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(causes.InnerExceptions[0]);
            else Assert.Equal("configure failure", causes.InnerExceptions[0].Message);
            Assert.DoesNotContain("unload", api.Calls);
            await failure.DisposeAsync();
            AssertTreeWritable(tree);
        }
        finally { await failure.DisposeAsync(); }
    }

    [Fact]
    public async Task AlreadyCancelledCreationDoesNotAcquireAnAssetReferenceOrStartTheOwner()
    {
        using var tree = new AssetTree();
        using var assets = Open(tree.Configuration, tree.Paths);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var api = new AssetApi(tree);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(assets, api, cancellationToken: cancellation.Token));
        Assert.Empty(api.Calls);
        assets.Dispose();
        AssertTreeWritable(tree);
    }

    [Fact]
    public void AssetsFactoriesHaveDistinctNamesAndRejectInvalidArgumentsWithoutAcquiring()
    {
        using var tree = new AssetTree();
        using var assets = Open(tree.Configuration, tree.Paths);
        var type = AssetsType();
        var capture = typeof(ExperimentalD3D9SurfaceSession).GetMethod("CreateObjectFxWithAssetsAsync", new[]
            { typeof(string), typeof(D3D9SurfaceSessionOptions), type, typeof(CancellationToken) });
        var preview = typeof(ExperimentalD3D9SurfaceSession).GetMethod("CreateObjectFxPreviewWithAssetsAsync", new[]
            { typeof(string), typeof(nint), typeof(D3D9SurfaceSessionOptions), type, typeof(CancellationToken) });
        Assert.NotNull(capture);
        Assert.NotNull(preview);
        Assert.Throws<ArgumentException>(() => Invoke(capture!, null, "relative.dll", new D3D9SurfaceSessionOptions(4, 2), assets.Value, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => Invoke(preview!, null, Path.Combine(tree.DirectoryPath, "host.dll"), (nint)0,
            new D3D9SurfaceSessionOptions(4, 2), assets.Value, CancellationToken.None));
        assets.Dispose();
        AssertTreeWritable(tree);
        Assert.NotNull(typeof(ExperimentalD3D9SurfaceSession).GetMethod("CreateObjectFxAsync", new[]
            { typeof(string), typeof(D3D9SurfaceSessionOptions), typeof(D3D9ObjectFxConfiguration), typeof(CancellationToken) }));
        Assert.NotNull(typeof(ExperimentalD3D9SurfaceSession).GetMethod("CreateObjectFxPreviewAsync", new[]
            { typeof(string), typeof(nint), typeof(D3D9SurfaceSessionOptions), typeof(D3D9ObjectFxConfiguration), typeof(CancellationToken) }));
    }

    private static IEnumerable<string> FailingManifest(string first)
    {
        yield return first;
        throw new InvalidOperationException("enumeration failed");
    }

    private static Type AssetsType()
    {
        var type = typeof(D3D9ObjectFxConfiguration).Assembly.GetType("Mmd.Modern.Rendering.D3D9.Compatibility.D3D9ObjectFxAssets");
        Assert.NotNull(type);
        return type!;
    }

    private static Assets Open(D3D9ObjectFxConfiguration configuration, IEnumerable<string> paths)
    {
        var method = AssetsType().GetMethod("Open", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return new(Invoke(method!, null, configuration, paths)!);
    }

    private static object? Invoke(MethodInfo method, object? target, params object?[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static Task<ExperimentalD3D9SurfaceSession> Create(Assets assets, AssetApi api, bool preview = false,
        CancellationToken cancellationToken = default)
    {
        var name = preview ? "CreateObjectFxPreviewWithAssetsForTestingAsync" : "CreateObjectFxWithAssetsForTestingAsync";
        var method = typeof(ExperimentalD3D9SurfaceSession).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Func<ID3D9SurfaceNativeApi> factory = () => api;
        return (Task<ExperimentalD3D9SurfaceSession>)Invoke(method!, null, preview ?
            new object[] { factory, (nint)1234, new D3D9SurfaceSessionOptions(4, 2), assets.Value, cancellationToken } :
            new object[] { factory, new D3D9SurfaceSessionOptions(4, 2), assets.Value, cancellationToken })!;
    }

    private static void AssertTreeLocked(AssetTree tree)
    {
        foreach (var path in tree.Paths)
        {
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.ThrowsAny<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
            Assert.ThrowsAny<IOException>(() => File.Delete(path));
        }
    }

    private static void AssertTreeWritable(AssetTree tree)
    {
        foreach (var path in tree.Paths)
        {
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) { }
            File.Move(path, path + ".move-check");
            File.Move(path + ".move-check", path);
        }
    }

    private sealed class Assets(object value) : IDisposable
    {
        internal object Value => value;
        internal D3D9ObjectFxConfiguration Configuration => (D3D9ObjectFxConfiguration)value.GetType().GetProperty("Configuration")!.GetValue(value)!;
        internal IReadOnlyList<string> Paths => (IReadOnlyList<string>)value.GetType().GetProperty("CompleteDependencyPaths")!.GetValue(value)!;
        public void Dispose() => ((IDisposable)value).Dispose();
    }

    private sealed class AssetTree : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "MDA-object-assets-" + Guid.NewGuid().ToString("N"));
        internal string RootFx => Path.Combine(DirectoryPath, "root.fx");
        internal string Include => Path.Combine(DirectoryPath, "nested", "common.fxh");
        internal string Resource => Path.Combine(DirectoryPath, "textures", "color.bmp");
        internal string Missing => Path.Combine(DirectoryPath, "missing.fxh");
        internal string[] Paths => new[] { RootFx, Include, Resource };
        internal D3D9ObjectFxConfiguration Configuration { get; }
        internal AssetTree()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Include)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Resource)!);
            File.WriteAllText(RootFx, "root includes nested/common.fxh and textures/color.bmp");
            File.WriteAllText(Include, "include");
            File.WriteAllText(Resource, "resource");
            Configuration = new(new[] { new D3D9ObjectFxAssignment(91, -1, D3D9ObjectFxAssignmentMode.File, RootFx) });
        }
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class AssetApi(AssetTree tree) : ID3D9SurfaceNativeApi
    {
        internal readonly ConcurrentQueue<string> Calls = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal D3D9ObjectFxConfiguration? Configuration;
        internal bool BlockConfigure, FailConfigure;
        internal int DestroyFailures, UnloadFailures;
        public bool SupportsObjectFx => true;
        public bool SupportsPreview => true;
        public NativeAbi QueryAbi() { Calls.Enqueue("query"); return NativeAbi.Valid() with { SupportedFeatures = 387 }; }
        public ulong Create(D3D9SurfaceSessionOptions options) { Calls.Enqueue("create"); return 7; }
        public ulong CreatePreview(nint targetHwnd, D3D9SurfaceSessionOptions options) { Calls.Enqueue("preview"); return 7; }
        public void ConfigureObjectFx(ulong handle, D3D9ObjectFxConfiguration configuration)
        {
            Calls.Enqueue("configure");
            Configuration = configuration;
            AssertTreeLocked(tree);
            if (BlockConfigure)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Configure fake was not released.");
            }
            if (FailConfigure) throw new InvalidOperationException("configure failure");
        }
        public D3D9SurfaceCapture Capture(ulong handle, D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder renderOrder) => throw new NotSupportedException();
        public void Resize(ulong handle, int width, int height) { }
        public void Destroy(ulong handle)
        {
            Calls.Enqueue("destroy");
            AssertTreeLocked(tree);
            if (DestroyFailures-- > 0) throw new InvalidOperationException("destroy retry");
        }
        public void Dispose()
        {
            Calls.Enqueue("unload");
            AssertTreeLocked(tree);
            if (UnloadFailures-- > 0) throw new InvalidOperationException("unload retry");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, nint securityAttributes);
}
