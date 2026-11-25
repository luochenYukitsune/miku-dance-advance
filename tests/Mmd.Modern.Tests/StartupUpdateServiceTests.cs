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

    [Fact]
    public async Task GitHubReleaseMetadataParsesInsidePrereleaseAndSkipsDrafts()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var json = $$"""
            [
              {
                "tag_name": "v0.9.0-Inside",
                "html_url": "https://github.com/luochenYukitsune/miku-dance-advance/releases/tag/v0.9.0-Inside",
                "draft": true,
                "prerelease": true,
                "body": "Draft release should be ignored",
                "assets": [
                  {
                    "name": "MDA-0.9.0-Inside-win-x64-setup.exe",
                    "browser_download_url": "https://github.com/luochenYukitsune/miku-dance-advance/releases/download/v0.9.0-Inside/MDA-0.9.0-Inside-win-x64-setup.exe",
                    "size": 4096,
                    "digest": "sha256:{{hash}}"
                  }
                ]
              },
              {
                "tag_name": "v0.5.2-Inside",
                "html_url": "https://github.com/luochenYukitsune/miku-dance-advance/releases/tag/v0.5.2-Inside",
                "draft": false,
                "prerelease": true,
                "body": "Inside fixes",
                "assets": [
                  {
                    "name": "MDA-0.5.2-Inside-win-x64-setup.exe",
                    "browser_download_url": "https://github.com/luochenYukitsune/miku-dance-advance/releases/download/v0.5.2-Inside/MDA-0.5.2-Inside-win-x64-setup.exe",
                    "size": 4096,
                    "digest": "sha256:{{hash}}"
                  }
                ]
              }
            ]
            """;

        var options = StartupUpdateConfiguration.CreateProductionOptions();
        using var client = new HttpClient(new ResponseHandler(json));
        var service = new StartupUpdateService(
            options,
            "0.5.1-Inside",
            () => new GitHubReleaseUpdateMetadataSource(client, options, "x64"));

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StartupUpdateStatus.Available, result.Status);
        Assert.NotNull(result.Release);
        Assert.Equal("0.5.2-Inside", result.Release.Version);
        Assert.EndsWith("/MDA-0.5.2-Inside-win-x64-setup.exe", result.Release.Package?.Url.AbsoluteUri);
        Assert.Equal(hash, result.Release.Package?.Sha256);
    }

    [Fact]
    public async Task GitHubReleaseMetadataFallsBackToMirrorAtomAndChecksumsWhenPrimaryFails()
    {
        const string hash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        const string atomXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>tag:github.com,2008:Repository/1/v0.6.0-Inside</id>
                <link rel="alternate" type="text/html" href="https://github.com/luochenYukitsune/miku-dance-advance/releases/tag/v0.6.0-Inside"/>
                <title>v0.6.0-Inside</title>
                <content type="html">&lt;p&gt;Mirror fallback &lt;strong&gt;works&lt;/strong&gt;.&lt;/p&gt;</content>
              </entry>
            </feed>
            """;
        var requestedUris = new List<string>();
        var handler = new RoutingHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requestedUris.Add(url);

            if (url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (url.Equals("https://github.com/luochenYukitsune/miku-dance-advance/releases.atom", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            if (url.Equals("https://gh-proxy.org/https://github.com/luochenYukitsune/miku-dance-advance/releases.atom", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(atomXml, Encoding.UTF8, "application/atom+xml") };
            if (url.EndsWith("/SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{hash} *MDA-0.6.0-Inside-win-x64-setup.exe\n", Encoding.UTF8, "text/plain")
                };
            if (request.Method == HttpMethod.Head && url.EndsWith("/MDA-0.6.0-Inside-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Content.Headers.ContentLength = 8192;
                return head;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var options = StartupUpdateConfiguration.CreateProductionOptions();
        using var client = new HttpClient(handler);
        var service = new StartupUpdateService(
            options,
            "0.5.1-Inside",
            () => new GitHubReleaseUpdateMetadataSource(client, options, "x64"));

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StartupUpdateStatus.Available, result.Status);
        Assert.NotNull(result.Release);
        Assert.Equal("0.6.0-Inside", result.Release.Version);
        Assert.Equal(new Uri("https://github.com/luochenYukitsune/miku-dance-advance/releases/download/v0.6.0-Inside/MDA-0.6.0-Inside-win-x64-setup.exe"),
            result.Release.Package?.Url);
        Assert.Equal(8192, result.Release.Package?.Size);
        Assert.Equal(hash, result.Release.Package?.Sha256);
        Assert.Contains("Mirror fallback", result.Release.Summary);
        Assert.Contains(requestedUris, u => u.StartsWith("https://gh-proxy.org/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GitHubReleaseMetadataRejectsUntrustedDownloadDomainInJson()
    {
        const string json = """
            [
              {
                "tag_name": "v0.7.0-Inside",
                "html_url": "https://github.com/luochenYukitsune/miku-dance-advance/releases/tag/v0.7.0-Inside",
                "draft": false,
                "prerelease": true,
                "assets": [
                  {
                    "name": "MDA-0.7.0-Inside-win-x64-setup.exe",
                    "browser_download_url": "https://evil.example/MDA-0.7.0-Inside-win-x64-setup.exe",
                    "size": 4096,
                    "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                  }
                ]
              }
            ]
            """;

        var options = StartupUpdateConfiguration.CreateProductionOptions();
        using var client = new HttpClient(new ResponseHandler(json));
        var service = new StartupUpdateService(
            options,
            "0.5.1-Inside",
            () => new GitHubReleaseUpdateMetadataSource(client, options, "x64"));

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StartupUpdateStatus.InvalidMetadata, result.Status);
    }

    [Fact]
    public async Task GitHubReleaseMetadataReturnsUnavailableWhenAllEndpointsAndMirrorsFail()
    {
        var options = StartupUpdateConfiguration.CreateProductionOptions();
        using var client = new HttpClient(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var service = new StartupUpdateService(
            options,
            "0.5.1-Inside",
            () => new GitHubReleaseUpdateMetadataSource(client, options, "x64"));

        var result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StartupUpdateStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task HttpsOnlyRedirectHandlerRejectsHttpDowngradeRedirect()
    {
        var inner = new RoutingHandler(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://insecure.example/setup.exe");
            return redirect;
        });

        using var client = new HttpClient(new HttpsOnlyRedirectHandler(inner));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("https://github.com/luochenYukitsune/miku-dance-advance/releases.atom",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ResolveReleaseDownloadCandidatesOnlyWrapsCanonicalRepoUrls()
    {
        var canonical = new Uri("https://github.com/luochenYukitsune/miku-dance-advance/releases/download/v0.5.1-Inside/MDA-0.5.1-Inside-win-x64-setup.exe");
        var candidates = GitHubReleaseUpdateMetadataSource.ResolveReleaseDownloadCandidates(canonical);
        Assert.Equal(canonical, candidates[0]);
        Assert.True(candidates.Count > 1);
        Assert.Contains(candidates, c => c.Host == "ghfast.top");

        var external = new Uri("https://updates.example.com/setup.exe");
        var externalOnly = GitHubReleaseUpdateMetadataSource.ResolveReleaseDownloadCandidates(external);
        Assert.Equal([external], externalOnly);
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
