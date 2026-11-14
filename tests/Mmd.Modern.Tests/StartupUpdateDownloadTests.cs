using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.App.Updates;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class StartupUpdateDownloadTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("test installer bytes; never executable");
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public void ConstructionRequiresConsentAndLaunchAndDoesNotTouchNetworkOrDisk()
    {
        using var fixture = new DownloadFixture();
        _ = fixture.Create();
        Assert.Equal(0, fixture.Http.Requests);
        Assert.False(Directory.Exists(fixture.Directory));
        Assert.Throws<ArgumentNullException>(() => new StartupUpdateDownloadUpgradeHandler(fixture.Client,
            null!, (_, _) => Task.FromResult(true)));
        Assert.Throws<ArgumentNullException>(() => new StartupUpdateDownloadUpgradeHandler(fixture.Client,
            (_, _) => Task.FromResult(true), null!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifiedDownloadIsOfferedBeforeLaunchAndOnlySuccessfulLaunchRetainsFile(bool includeSize)
    {
        using var fixture = new DownloadFixture();
        var order = new List<string>();
        VerifiedStartupUpdateInstaller? offered = null;
        var progress = new RecordingProgress();
        var handler = fixture.Create(async (installer, token) =>
        {
            order.Add("consent");
            offered = installer;
            Assert.Equal(Payload, await File.ReadAllBytesAsync(installer.Path, token));
            Assert.Equal(Hash(Payload), installer.Sha256);
            Assert.Equal(Payload.LongLength, installer.Size);
            Assert.Equal("0.1.9", installer.Release.Version);
            Assert.EndsWith(".exe", installer.Path);
            Assert.Equal([installer.Path], System.IO.Directory.GetFiles(fixture.Directory));
            return true;
        }, (installer, _) =>
        {
            order.Add("launch");
            Assert.Same(offered, installer);
            return Task.FromResult(true);
        });

        var result = await handler.UpgradeAsync(Release(size: includeSize ? Payload.LongLength : null), progress, TestToken);

        Assert.True(result.InstallerStarted);
        Assert.Equal(["consent", "launch"], order);
        Assert.NotNull(offered);
        Assert.True(File.Exists(offered.Path));
        Assert.Equal(Payload.LongLength, progress.Values[^1].DownloadedBytes);
        Assert.Equal(Payload.LongLength, progress.Values[^1].TotalBytes);
        // The handler has released its launch-time read handle.
        File.Delete(offered.Path);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("size-short")]
    [InlineData("size-long")]
    [InlineData("stream-size-short")]
    [InlineData("stream-size-long")]
    [InlineData("budget")]
    [InlineData("empty")]
    [InlineData("http-error")]
    [InlineData("insecure-final-url")]
    public async Task FailedVerificationNeverRequestsConsentOrLaunchesAndDeletesOwnedPartial(string failure)
    {
        using var fixture = new DownloadFixture();
        var release = Release();
        long budget = 1024;
        switch (failure)
        {
            case "checksum": release = Release(hash: new string('0', 64)); break;
            case "size-short": release = Release(size: Payload.LongLength - 1); break;
            case "size-long": release = Release(size: Payload.LongLength + 1); break;
            case "stream-size-short":
            case "stream-size-long":
                fixture.Http.Content = () => new StreamContent(new UnknownLengthStream(Payload));
                release = Release(size: Payload.LongLength + (failure == "stream-size-short" ? -1 : 1));
                break;
            case "budget": budget = Payload.LongLength - 1; break;
            case "empty": fixture.Http.Content = () => new ByteArrayContent([]); break;
            case "http-error": fixture.Http.Status = HttpStatusCode.ServiceUnavailable; break;
            case "insecure-final-url": fixture.Http.FinalUri = new("http://updates.test.invalid/setup.exe"); break;
        }
        var handler = fixture.Create(maximumBytes: budget);

        Assert.False((await handler.UpgradeAsync(release, cancellationToken: TestToken)).InstallerStarted);
        Assert.Equal(0, fixture.ConsentCalls);
        Assert.Equal(0, fixture.LaunchCalls);
        fixture.AssertNoFiles();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("http")]
    [InlineData("credentials")]
    [InlineData("bad-hash")]
    [InlineData("zero-size")]
    [InlineData("over-budget")]
    public async Task InvalidPackageIsRejectedBeforeNetworkOrDisk(string failure)
    {
        using var fixture = new DownloadFixture();
        var release = failure switch
        {
            "missing" => new StartupUpdateRelease("0.1.9", "test"),
            "http" => Release(url: new("http://updates.test.invalid/setup.exe")),
            "credentials" => Release(url: new("https://user:secret@updates.test.invalid/setup.exe")),
            "bad-hash" => Release(hash: "not a SHA256"),
            "zero-size" => Release(size: 0),
            _ => Release(size: 1025)
        };
        var handler = fixture.Create(maximumBytes: 1024);

        Assert.False((await handler.UpgradeAsync(release, cancellationToken: TestToken)).InstallerStarted);
        Assert.Equal(0, fixture.Http.Requests);
        Assert.False(Directory.Exists(fixture.Directory));
        Assert.Equal(0, fixture.ConsentCalls);
        Assert.Equal(0, fixture.LaunchCalls);
    }

    [Fact]
    public async Task UnknownLengthStreamCannotExceedBudgetOrOverwriteUnrelatedFile()
    {
        using var fixture = new DownloadFixture();
        System.IO.Directory.CreateDirectory(fixture.Directory);
        var unrelated = System.IO.Path.Combine(fixture.Directory, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep", TestToken);
        fixture.Http.Content = () => new StreamContent(new UnknownLengthStream(Payload));
        var handler = fixture.Create(maximumBytes: Payload.LongLength - 1);

        Assert.False((await handler.UpgradeAsync(Release(), cancellationToken: TestToken)).InstallerStarted);
        Assert.Equal([unrelated], System.IO.Directory.GetFiles(fixture.Directory));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated, TestToken));
        Assert.Equal(0, fixture.ConsentCalls);
        Assert.Equal(0, fixture.LaunchCalls);
    }

    [Fact]
    public async Task CancelingDuringStreamRemovesPartialWithoutOfferingInstaller()
    {
        using var fixture = new DownloadFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        fixture.Http.Content = () => new StreamContent(new UnknownLengthStream(Payload));
        var handler = fixture.Create();
        var progress = new RecordingProgress(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.UpgradeAsync(Release(), progress, cancellation.Token));

        fixture.AssertNoFiles();
        Assert.Equal(0, fixture.ConsentCalls);
        Assert.Equal(0, fixture.LaunchCalls);
    }

    [Fact]
    public async Task DownloadTimeoutNeverOffersInstallerAndCleansPartial()
    {
        using var fixture = new DownloadFixture();
        fixture.Http.Content = () => new StreamContent(new BlockingStream());
        var handler = fixture.Create(downloadTimeout: TimeSpan.FromMilliseconds(50));

        Assert.False((await handler.UpgradeAsync(Release(), cancellationToken: TestToken)
            .WaitAsync(TimeSpan.FromSeconds(3), TestToken)).InstallerStarted);

        fixture.AssertNoFiles();
        Assert.Equal(0, fixture.ConsentCalls);
        Assert.Equal(0, fixture.LaunchCalls);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("consent-failure")]
    [InlineData("launch-false")]
    [InlineData("launch-failure")]
    public async Task RejectedConsentOrFailedLaunchDeletesVerifiedFile(string outcome)
    {
        using var fixture = new DownloadFixture();
        var launches = 0;
        var handler = fixture.Create((_, _) => outcome == "consent-failure" ?
            Task.FromException<bool>(new InvalidOperationException()) : Task.FromResult(outcome != "reject"), (_, _) =>
        {
            launches++;
            return outcome == "launch-failure" ? Task.FromException<bool>(new InvalidOperationException()) : Task.FromResult(false);
        });

        Assert.False((await handler.UpgradeAsync(Release(), cancellationToken: TestToken)).InstallerStarted);

        Assert.Equal(outcome.StartsWith("launch", StringComparison.Ordinal) ? 1 : 0, launches);
        fixture.AssertNoFiles();
    }

    [Fact]
    public async Task CancellationWhileConsentIsOpenPreventsLaunchAndDeletesVerifiedFile()
    {
        using var fixture = new DownloadFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var handler = fixture.Create((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(true);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.UpgradeAsync(Release(), cancellationToken: cancellation.Token));

        Assert.Equal(0, fixture.LaunchCalls);
        fixture.AssertNoFiles();
    }

    [Fact]
    public async Task VerifiedInstallerCannotBeModifiedWhileConsentOrLaunchRunsOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DownloadFixture();
        var consents = 0;
        var launches = 0;
        var handler = fixture.Create((installer, _) =>
        {
            consents++;
            Assert.Throws<IOException>(() => File.WriteAllBytes(installer.Path, [0]));
            return Task.FromResult(true);
        }, (installer, _) =>
        {
            launches++;
            Assert.Throws<IOException>(() => File.Delete(installer.Path));
            return Task.FromResult(false);
        });

        Assert.False((await handler.UpgradeAsync(Release(), cancellationToken: TestToken)).InstallerStarted);
        Assert.Equal(1, consents);
        Assert.Equal(1, launches);
        fixture.AssertNoFiles();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static StartupUpdateRelease Release(long? size = null, string? hash = null, Uri? url = null) => new(
        "0.1.9", "test update", Package: new(url ?? new("https://updates.test.invalid/setup.exe"), hash ?? Hash(Payload), size));

    private sealed class DownloadFixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mda-download-tests-" + Guid.NewGuid().ToString("N"));
        internal StubHttpHandler Http { get; } = new();
        internal HttpClient Client { get; }
        internal int ConsentCalls { get; private set; }
        internal int LaunchCalls { get; private set; }
        internal DownloadFixture() => Client = new(Http);
        internal StartupUpdateDownloadUpgradeHandler Create(
            Func<VerifiedStartupUpdateInstaller, CancellationToken, Task<bool>>? consent = null,
            Func<VerifiedStartupUpdateInstaller, CancellationToken, Task<bool>>? launch = null,
            long maximumBytes = 1024, TimeSpan? downloadTimeout = null) => new(Client,
                consent ?? ((_, _) => { ConsentCalls++; return Task.FromResult(false); }),
                launch ?? ((_, _) => { LaunchCalls++; return Task.FromResult(true); }),
                Directory, maximumBytes, downloadTimeout);
        internal void AssertNoFiles() => Assert.True(!System.IO.Directory.Exists(Directory) ||
            System.IO.Directory.GetFiles(Directory).Length == 0);
        public void Dispose()
        {
            Client.Dispose();
            if (!System.IO.Directory.Exists(Directory)) return;
            foreach (var file in System.IO.Directory.GetFiles(Directory)) File.Delete(file);
            System.IO.Directory.Delete(Directory);
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        internal Func<HttpContent> Content { get; set; } = () => new ByteArrayContent(Payload);
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal Uri? FinalUri { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = Content(),
                RequestMessage = FinalUri is null ? request : new HttpRequestMessage(HttpMethod.Get, FinalUri) });
        }
    }

    private sealed class RecordingProgress(Action<StartupUpdateProgress>? report = null) : IProgress<StartupUpdateProgress>
    {
        internal List<StartupUpdateProgress> Values { get; } = [];
        public void Report(StartupUpdateProgress value) { Values.Add(value); report?.Invoke(value); }
    }

    private class UnknownLengthStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream input = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }

    private sealed class BlockingStream() : UnknownLengthStream([])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
