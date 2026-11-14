using System.Net;
using System.Text;
using Mmd.Modern.App.Updates;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class StartupUpdateServiceTests
{
    private static readonly Uri Feed = new("https://updates.test.invalid/feed.json");
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DisabledConfigurationDoesNotCreateSourceOrTouchSkipStore()
    {
        var store = new MemorySkipStore { FailOnAccess = true };
        var service = new StartupUpdateService(new(), "0.1.8", () => throw new InvalidOperationException(), store);
        Assert.False(service.IsConfigured);
        var task = service.CheckAsync(TestToken);
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(StartupUpdateStatus.Disabled, (await task).Status);
        await service.SkipVersionAsync("0.1.9", TestToken);
        Assert.Equal(0, store.Accesses);
    }

    [Theory]
    [InlineData("0.1.9", "0.1.8", StartupUpdateStatus.Available)]
    [InlineData("0.1.10", "0.1.9", StartupUpdateStatus.Available)]
    [InlineData("0.1.8.0", "0.1.8", StartupUpdateStatus.NoUpdate)]
    [InlineData("0.1.7", "0.1.8", StartupUpdateStatus.NoUpdate)]
    [InlineData("0.1.9-rc.2", "0.1.9-rc.1", StartupUpdateStatus.Available)]
    [InlineData("0.1.9-rc.2", "0.1.9", StartupUpdateStatus.NoUpdate)]
    [InlineData("0.1.9", "0.1.9-rc.2", StartupUpdateStatus.Available)]
    [InlineData("0.1.9+build.2", "0.1.9+build.1", StartupUpdateStatus.NoUpdate)]
    public async Task OnlyNewerVersionsAreOffered(string remote, string current, StartupUpdateStatus expected)
    {
        var result = await Create(new(remote, "新版修复模型更换。"), current).CheckAsync(TestToken);
        Assert.Equal(expected, result.Status);
        if (expected == StartupUpdateStatus.Available)
        {
            Assert.Equal(remote, result.Release!.Version);
            Assert.Equal("新版修复模型更换。", result.Release.Summary);
        }
    }

    [Fact]
    public async Task SkippingOneVersionPersistsAcrossInstancesAndStillOffersTheNextVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mda-update-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "skip.txt");
        try
        {
            var first = Create(new("0.1.9", "first"), store: new FileStartupUpdateSkipStore(path));
            await first.SkipVersionAsync("0.1.9", TestToken);
            var reopened = Create(new("0.1.9+rebuild", "same"), store: new FileStartupUpdateSkipStore(path));
            Assert.Equal(StartupUpdateStatus.SkippedVersion, (await reopened.CheckAsync(TestToken)).Status);
            var next = Create(new("0.1.10", "next"), store: new FileStartupUpdateSkipStore(path));
            Assert.Equal(StartupUpdateStatus.Available, (await next.CheckAsync(TestToken)).Status);
            Assert.Equal("0.1.9", await File.ReadAllTextAsync(path, TestToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NonCooperativeSourceTimesOutAndLateFailureDoesNotEscape()
    {
        var pending = new TaskCompletionSource<StartupUpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StartupUpdateService(new() { Endpoint = Feed, CheckTimeout = TimeSpan.FromMilliseconds(30) },
            "0.1.8", () => new StubSource(_ => pending.Task), new MemorySkipStore());
        var result = await service.CheckAsync(TestToken).WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(StartupUpdateStatus.TimedOut, result.Status);
        pending.SetException(new HttpRequestException("late failure"));
    }

    [Fact]
    public async Task SourceBlockingBeforeReturningItsTaskCannotBlockStartup()
    {
        using var release = new ManualResetEventSlim();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StartupUpdateService(new() { Endpoint = Feed, CheckTimeout = TimeSpan.FromMilliseconds(30) },
            "0.1.8", () => new StubSource(_ =>
            {
                release.Wait(TestToken);
                returned.TrySetResult();
                return Task.FromResult<StartupUpdateRelease?>(null);
            }), new MemorySkipStore());
        try
        {
            Assert.True(service.IsConfigured);
            Assert.Equal(StartupUpdateStatus.TimedOut,
                (await service.CheckAsync(TestToken).WaitAsync(TimeSpan.FromSeconds(3), TestToken)).Status);
        }
        finally { release.Set(); }
        await returned.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
    }

    [Fact]
    public async Task ClosingStartupCancelsEvenWhenSourceIgnoresCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<StartupUpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var service = new StartupUpdateService(new() { Endpoint = Feed }, "0.1.8", () => new StubSource(_ =>
        {
            started.SetResult();
            return pending.Task;
        }), new MemorySkipStore());
        var check = service.CheckAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        pending.SetResult(new("0.1.9", "late"));
    }

    [Fact]
    public async Task NetworkFailureContinuesStartupWithoutChangingSkipState()
    {
        var store = new MemorySkipStore();
        var service = new StartupUpdateService(new() { Endpoint = Feed }, "0.1.8",
            () => new StubSource(_ => Task.FromException<StartupUpdateRelease?>(new HttpRequestException())), store);
        Assert.Equal(StartupUpdateStatus.Unavailable, (await service.CheckAsync(TestToken)).Status);
        Assert.Equal(0, store.Accesses);
    }

    [Theory]
    [InlineData("")]
    [InlineData("later")]
    [InlineData("0.1.9-rc..1")]
    [InlineData("0.1.9-01")]
    public async Task InvalidVersionsAreIgnored(string version) => Assert.Equal(StartupUpdateStatus.InvalidMetadata,
        (await Create(new(version, "summary")).CheckAsync(TestToken)).Status);

    [Fact]
    public async Task ReleaseSummaryIsPlainTextWithControlCharactersRemovedAndLengthCapped()
    {
        var release = new StartupUpdateRelease("0.1.9", "<b>literal text</b>\0\n" + new string('x', 5000));
        var result = await Create(release).CheckAsync(TestToken);
        Assert.StartsWith("<b>literal text</b>\n", result.Release!.Summary);
        Assert.Equal(StartupUpdateOptions.MaximumSummaryCharacters, result.Release.Summary.Length);
        Assert.DoesNotContain('\0', result.Release.Summary);
    }

    [Fact]
    public async Task JsonFeedOnlyReadsMetadataAndAcceptsPackageForFutureUpgradeHandler()
    {
        var handler = new ResponseHandler("""
            {"version":"0.1.9","summary":"Fixes","releasePage":"https://updates.test.invalid/release",
             "package":{"url":"https://updates.test.invalid/setup.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":123}}
            """);
        using var client = new HttpClient(handler);
        var source = new JsonStartupUpdateMetadataSource(client);
        var result = await new StartupUpdateService(new() { Endpoint = Feed }, "0.1.8", () => source,
            new MemorySkipStore()).CheckAsync(TestToken);
        Assert.Equal(StartupUpdateStatus.Available, result.Status);
        Assert.Equal(123L, result.Release!.Package!.Size);
        Assert.Equal("/setup.exe", result.Release.Package.Url.AbsolutePath);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"version\":\"0.1.9\",\"summary\":null}")]
    [InlineData("{\"version\":\"0.1.9\",\"summary\":\"ok\",\"releasePage\":\"file:///setup.exe\"}")]
    public async Task InvalidJsonMetadataDoesNotOfferUpgrade(string json)
    {
        using var client = new HttpClient(new ResponseHandler(json));
        var result = await new StartupUpdateService(new() { Endpoint = Feed }, "0.1.8",
            () => new JsonStartupUpdateMetadataSource(client), new MemorySkipStore()).CheckAsync(TestToken);
        Assert.Equal(StartupUpdateStatus.InvalidMetadata, result.Status);
    }

    [Fact]
    public async Task OversizedFeedIsRejectedBeforeParsing()
    {
        using var client = new HttpClient(new ResponseHandler(new string('x', StartupUpdateOptions.MaximumFeedBytes + 1)));
        var result = await new StartupUpdateService(new() { Endpoint = Feed }, "0.1.8",
            () => new JsonStartupUpdateMetadataSource(client), new MemorySkipStore()).CheckAsync(TestToken);
        Assert.Equal(StartupUpdateStatus.InvalidMetadata, result.Status);
    }

    private static StartupUpdateService Create(StartupUpdateRelease release, string current = "0.1.8",
        IStartupUpdateSkipStore? store = null) => new(new() { Endpoint = Feed }, current,
            () => new StubSource(_ => Task.FromResult<StartupUpdateRelease?>(release)), store ?? new MemorySkipStore());

    private sealed class StubSource(Func<CancellationToken, Task<StartupUpdateRelease?>> get) : IStartupUpdateMetadataSource
    {
        public Task<StartupUpdateRelease?> GetLatestAsync(Uri endpoint, CancellationToken cancellationToken) => get(cancellationToken);
    }

    private sealed class MemorySkipStore : IStartupUpdateSkipStore
    {
        public int Accesses { get; private set; }
        public bool FailOnAccess { get; init; }
        private string? version;
        public Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            Accesses++;
            if (FailOnAccess) throw new InvalidOperationException();
            return Task.FromResult(version);
        }
        public Task WriteAsync(string value, CancellationToken cancellationToken)
        {
            Accesses++;
            if (FailOnAccess) throw new InvalidOperationException();
            version = value;
            return Task.CompletedTask;
        }
    }

    private sealed class ResponseHandler(string content) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(content, Encoding.UTF8, "application/json") });
        }
    }
}
