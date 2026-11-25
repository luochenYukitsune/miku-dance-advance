using System.Security.Cryptography;

namespace Mmd.Modern.App.Updates;

/// <summary>A verified local package, offered to explicit installer consent before launch.</summary>
public sealed record VerifiedStartupUpdateInstaller(StartupUpdateRelease Release, string Path, string Sha256, long Size);

/// <summary>
/// Optional upgrade handoff; construction performs no I/O and startup does not configure this handler.
/// The caller owns the HttpClient and must use a handler that forbids HTTPS-to-HTTP redirects.
/// SHA256 verifies the package against trusted metadata; it does not authenticate its publisher.
/// Consent must represent an explicit user decision. The launch callback must return true promptly
/// after starting the supplied installer, without waiting for its exit or throwing after it starts.
/// A successful launch retains the package for the installer; its caller then owns eventual cleanup.
/// </summary>
public sealed class StartupUpdateDownloadUpgradeHandler : IStartupUpdateUpgradeHandler
{
    public const long DefaultMaximumPackageBytes = 1024L * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Func<VerifiedStartupUpdateInstaller, CancellationToken, Task<bool>> confirmInstaller;
    private readonly Func<VerifiedStartupUpdateInstaller, CancellationToken, Task<bool>> launchInstaller;
    private readonly string downloadDirectory;
    private readonly long maximumPackageBytes;
    private readonly TimeSpan downloadTimeout;
    private readonly Func<Uri, IReadOnlyList<Uri>>? resolveCandidateUrls;

    public StartupUpdateDownloadUpgradeHandler(HttpClient client,
        Func<VerifiedStartupUpdateInstaller, CancellationToken, Task<bool>> confirmInstaller,
        Func<VerifiedStartupUpdateInstaller, CancellationToken, Task<bool>> launchInstaller,
        string? downloadDirectory = null, long maximumPackageBytes = DefaultMaximumPackageBytes,
        TimeSpan? downloadTimeout = null, Func<Uri, IReadOnlyList<Uri>>? resolveCandidateUrls = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(confirmInstaller);
        ArgumentNullException.ThrowIfNull(launchInstaller);
        if (maximumPackageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPackageBytes));
        var timeout = downloadTimeout ?? TimeSpan.FromMinutes(10);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        this.client = client;
        this.confirmInstaller = confirmInstaller;
        this.launchInstaller = launchInstaller;
        this.downloadDirectory = Path.GetFullPath(downloadDirectory ?? Path.Combine(Path.GetTempPath(), "MDA", "Updates"));
        this.maximumPackageBytes = maximumPackageBytes;
        this.downloadTimeout = timeout;
        this.resolveCandidateUrls = resolveCandidateUrls;
    }

    public async Task<StartupUpdateUpgradeResult> UpgradeAsync(StartupUpdateRelease release,
        IProgress<StartupUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        cancellationToken.ThrowIfCancellationRequested();
        if (release.Package is not { } package || !StartupUpdateService.IsHttps(package.Url) ||
            package.Size is <= 0 || package.Size > maximumPackageBytes || package.Sha256 is not { Length: 64 } ||
            !package.Sha256.All(Uri.IsHexDigit))
            return new(false, "The update has no valid installer package.");

        var candidates = BuildCandidateUris(package.Url);
        if (candidates.Count == 0)
            return new(false, "The update has no valid installer package.");

        string? ownedPath = null;
        FileStream? verifiedFile = null;
        var installerStarted = false;
        try
        {
            // Neither a URL basename nor feed-supplied text determines a local path.
            Directory.CreateDirectory(downloadDirectory);
            using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overallTimeout.CancelAfter(downloadTimeout);

            VerifiedStartupUpdateInstaller? verified = null;
            Exception? lastDownloadError = null;
            foreach (var candidateUrl in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                overallTimeout.Token.ThrowIfCancellationRequested();
                if (ownedPath is not null)
                {
                    DeleteOwnedFile(ownedPath);
                    ownedPath = null;
                }

                try
                {
                    var partialPath = Path.Combine(downloadDirectory, Guid.NewGuid().ToString("N") + ".partial");
                    await using (var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        // Only claim ownership after CreateNew succeeded; never remove a preexisting file.
                        ownedPath = partialPath;
                        await DownloadAsync(candidateUrl, package, destination, progress, overallTimeout.Token)
                            .ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var installerPath = Path.ChangeExtension(ownedPath, ".exe");
                    File.Move(ownedPath, installerPath);
                    ownedPath = installerPath;

                    // Reopen and verify the actual launch file. On Windows this read handle prevents writes,
                    // replacement and deletion throughout the consent prompt and process-start callback.
                    verifiedFile = new FileStream(installerPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (verifiedFile.Length == 0 || verifiedFile.Length > maximumPackageBytes ||
                        (package.Size is { } expectedSize && verifiedFile.Length != expectedSize))
                        throw new InvalidDataException("The installer size does not match.");
                    var digest = await SHA256.HashDataAsync(verifiedFile, cancellationToken).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(package.Sha256)))
                        throw new InvalidDataException("The installer checksum does not match.");

                    verified = new VerifiedStartupUpdateInstaller(release, installerPath,
                        Convert.ToHexString(digest), verifiedFile.Length);
                    lastDownloadError = null;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || overallTimeout.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    lastDownloadError = error;
                    if (verifiedFile is not null)
                    {
                        await verifiedFile.DisposeAsync().ConfigureAwait(false);
                        verifiedFile = null;
                    }
                    if (ownedPath is not null)
                    {
                        DeleteOwnedFile(ownedPath);
                        ownedPath = null;
                    }
                }
            }

            if (verified is null)
            {
                return lastDownloadError is OperationCanceledException
                    ? new(false, "The installer download timed out.")
                    : new(false, "The installer could not be downloaded, verified or started.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!await confirmInstaller(verified, cancellationToken).ConfigureAwait(false))
                return new(false, "Installer launch was declined.");
            cancellationToken.ThrowIfCancellationRequested();
            installerStarted = await launchInstaller(verified, cancellationToken).ConfigureAwait(false);
            return new(installerStarted, installerStarted ? null : "The installer was not started.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, "The installer download timed out."); }
        catch (Exception) { return new(false, "The installer could not be downloaded, verified or started."); }
        finally
        {
            if (verifiedFile is not null) await verifiedFile.DisposeAsync().ConfigureAwait(false);
            if (!installerStarted && ownedPath is not null) DeleteOwnedFile(ownedPath);
        }
    }

    private IReadOnlyList<Uri> BuildCandidateUris(Uri primaryUrl)
    {
        var raw = resolveCandidateUrls?.Invoke(primaryUrl) ?? [primaryUrl];
        var result = new List<Uri>(raw.Count);
        foreach (var uri in raw)
        {
            if (StartupUpdateService.IsHttps(uri) && !result.Contains(uri))
                result.Add(uri);
        }
        return result;
    }

    private async Task DownloadAsync(Uri candidateUrl, StartupUpdatePackage package, FileStream destination,
        IProgress<StartupUpdateProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(candidateUrl, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } finalUri && !StartupUpdateService.IsHttps(finalUri))
            throw new InvalidDataException("The installer response must use HTTPS.");
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > maximumPackageBytes ||
            (package.Size is { } expectedSize && declaredLength is { } actualSize && expectedSize != actualSize))
            throw new InvalidDataException("The installer size does not match.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long downloaded = 0;
        var limit = package.Size ?? maximumPackageBytes;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (count > limit - downloaded) throw new InvalidDataException("The installer exceeds its size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            downloaded += count;
            progress?.Report(new(downloaded, package.Size ?? declaredLength));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (downloaded == 0 || (package.Size is { } expected && downloaded != expected) ||
            (declaredLength is { } declared && downloaded != declared) ||
            !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(package.Sha256)))
            throw new InvalidDataException("The installer verification failed.");
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteOwnedFile(string path)
    {
        try { File.Delete(path); }
        // A transient external lock may prevent cleanup; never reuse that file on a later attempt.
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
