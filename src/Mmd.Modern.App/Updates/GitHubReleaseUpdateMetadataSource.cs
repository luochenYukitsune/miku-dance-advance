using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Mmd.Modern.App.Updates;

internal sealed class HttpsOnlyRedirectHandler : DelegatingHandler
{
    private const int MaxRedirects = 5;

    internal HttpsOnlyRedirectHandler() : base(CreateDefaultInnerHandler()) { }

    internal HttpsOnlyRedirectHandler(HttpMessageHandler innerHandler) : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsHttpsUri(request.RequestUri))
            throw new InvalidOperationException("Update requests require an absolute HTTPS URI.");

        var currentUri = request.RequestUri!;
        for (int redirectCount = 0; ; redirectCount++)
        {
            using var attempt = CloneRequest(request, currentUri);
            var response = await base.SendAsync(attempt, cancellationToken).ConfigureAwait(false);
            if (!IsRedirectStatus(response.StatusCode) || response.Headers.Location is not { } location)
                return response;

            var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (!IsHttpsUri(nextUri))
            {
                response.Dispose();
                throw new HttpRequestException($"Refused non-HTTPS redirect target '{nextUri}'.", null, response.StatusCode);
            }

            response.Dispose();
            if (redirectCount >= MaxRedirects)
                throw new HttpRequestException("Exceeded maximum HTTPS update redirects.", null, response.StatusCode);

            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
                throw new HttpRequestException("Refused redirect for non-idempotent update request.", null, response.StatusCode);

            currentUri = nextUri;
        }
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source, Uri target)
    {
        var clone = new HttpRequestMessage(source.Method, target);
        foreach (var header in source.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (clone.Headers.UserAgent.Count == 0)
            clone.Headers.UserAgent.Add(new ProductInfoHeaderValue("MikuDanceAdvance-Updater", "1.0"));

        return clone;
    }

    private static bool IsRedirectStatus(HttpStatusCode code) => code is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsHttpsUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo);

    private static SocketsHttpHandler CreateDefaultInnerHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };
}

public sealed class GitHubReleaseUpdateMetadataSource : IStartupUpdateMetadataSource
{
    public const int MaximumReleaseApiBytes = 512 * 1024;
    public const int MaximumAtomFeedBytes = 256 * 1024;
    public const int MaximumChecksumsBytes = 64 * 1024;

    private const string CanonicalRepoPrefix =
        "https://github.com/" + StartupUpdateConfiguration.GitHubRepository + "/";
    private const string CanonicalRepoDownloadPrefix = CanonicalRepoPrefix + "releases/download/";
    private const string CanonicalRepoTagPrefix = CanonicalRepoPrefix + "releases/tag/";
    private const string AtomNamespace = "http://www.w3.org/2005/Atom";

    private readonly HttpClient httpClient;
    private readonly StartupUpdateOptions options;
    private readonly Uri atomEndpoint;
    private readonly string architecture;

    public GitHubReleaseUpdateMetadataSource(
        HttpClient httpClient,
        StartupUpdateOptions? options = null,
        string? architecture = null)
        : this(httpClient, options, null, architecture) { }

    internal GitHubReleaseUpdateMetadataSource(
        HttpClient httpClient,
        StartupUpdateOptions? options,
        Uri? atomEndpoint,
        string? architecture = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.options = options ?? StartupUpdateConfiguration.CreateProductionOptions();
        this.atomEndpoint = atomEndpoint is { IsAbsoluteUri: true } && IsHttpsUri(atomEndpoint)
            ? atomEndpoint
            : StartupUpdateConfiguration.GitHubReleasesAtomEndpoint;
        this.architecture = NormalizeArchitecture(architecture);
    }

    public static IReadOnlyList<Uri> ResolveReleaseDownloadCandidates(
        Uri primaryUrl,
        IReadOnlyList<Uri>? mirrorBases = null)
    {
        ArgumentNullException.ThrowIfNull(primaryUrl);
        if (!IsHttpsUri(primaryUrl)) return Array.Empty<Uri>();

        var candidates = new List<Uri> { primaryUrl };
        if (!IsCanonicalRepoDownloadUri(primaryUrl)) return candidates;

        foreach (var mirrorBase in mirrorBases ?? StartupUpdateConfiguration.DefaultDownloadMirrorBases)
        {
            if (TryBuildMirrorUri(mirrorBase, primaryUrl, out var mirrored) && !candidates.Contains(mirrored))
                candidates.Add(mirrored);
        }

        return candidates;
    }

    public async Task<StartupUpdateRelease?> GetLatestAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IsHttpsUri(endpoint))
            throw new ArgumentException("Update endpoint must use an absolute HTTPS URI.", nameof(endpoint));

        var overallTimeout = options.CheckTimeout > TimeSpan.Zero ? options.CheckTimeout : TimeSpan.FromSeconds(12);
        var attemptTimeout = options.PerAttemptTimeout > TimeSpan.Zero ? options.PerAttemptTimeout : TimeSpan.FromSeconds(4);
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCts.CancelAfter(overallTimeout);
        var token = overallCts.Token;

        Exception? lastFailure = null;
        if (!endpoint.AbsolutePath.EndsWith(".atom", StringComparison.OrdinalIgnoreCase))
        {
            var jsonResult = await TryFetchFromGitHubJsonAsync(endpoint, attemptTimeout, token, cancellationToken)
                .ConfigureAwait(false);
            if (jsonResult.Handled) return jsonResult.Release;
            lastFailure = jsonResult.Error;
        }

        var primaryAtom = endpoint.AbsolutePath.EndsWith(".atom", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : atomEndpoint;

        foreach (var candidate in BuildAtomCandidates(primaryAtom, options.MirrorBases))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var atomBytes = await ReadBoundedBytesAsync(candidate.Uri, MaximumAtomFeedBytes, attemptTimeout, token, cancellationToken)
                    .ConfigureAwait(false);
                var parseResult = ParseHighestAtomEntry(atomBytes);
                if (!parseResult.HadEntries) return null;
                if (parseResult.Best is not { } entry)
                {
                    if (parseResult.RejectedUntrustedUrl)
                        throw new InvalidDataException("Release Atom feed referenced an untrusted URL.");
                    return null;
                }

                var sumsUri = new Uri($"{CanonicalRepoDownloadPrefix}{Uri.EscapeDataString(entry.Tag)}/SHA256SUMS.txt");
                var sumsCandidates = PrioritizeDownloadCandidates(
                    sumsUri, candidate.MirrorBase, options.DownloadMirrorBases, preferMirrorFirst: candidate.MirrorBase is not null);
                var match = await TryDownloadMatchingAsync(
                    sumsCandidates,
                    MaximumChecksumsBytes,
                    attemptTimeout,
                    token,
                    cancellationToken,
                    text => FindInstallerChecksum(text, architecture)).ConfigureAwait(false);
                if (match is null)
                {
                    lastFailure ??= new HttpRequestException("Unable to obtain a matching installer checksum from SHA256SUMS.txt.");
                    continue;
                }

                var installerUri = new Uri($"{CanonicalRepoDownloadPrefix}{Uri.EscapeDataString(entry.Tag)}/{Uri.EscapeDataString(match.Value.FileName)}");
                var headCandidates = PrioritizeDownloadCandidates(
                    installerUri, candidate.MirrorBase, options.DownloadMirrorBases, preferMirrorFirst: candidate.MirrorBase is not null);
                long? packageSize = await TryProbeContentLengthAsync(headCandidates, attemptTimeout, token, cancellationToken)
                    .ConfigureAwait(false);
                if (packageSize is null or <= 0)
                {
                    lastFailure = new HttpRequestException("Unable to determine installer package size.");
                    continue;
                }

                return new StartupUpdateRelease(
                    entry.VersionText,
                    entry.Summary,
                    entry.ReleaseNotesUrl,
                    new StartupUpdatePackage(installerUri, match.Value.Sha256, packageSize.Value));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || overallCts.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception error) when (IsRecoverableFetchFailure(error))
            {
                lastFailure = error;
            }
        }

        throw lastFailure ?? new HttpRequestException("All release endpoints and mirrors failed.");
    }

    private async Task<(bool Handled, StartupUpdateRelease? Release, Exception? Error)> TryFetchFromGitHubJsonAsync(
        Uri endpoint,
        TimeSpan attemptTimeout,
        CancellationToken overallToken,
        CancellationToken callerToken)
    {
        try
        {
            var payload = await ReadBoundedBytesAsync(endpoint, MaximumReleaseApiBytes, attemptTimeout, overallToken, callerToken)
                .ConfigureAwait(false);
            var parsed = await ParseGitHubReleasesJsonAsync(payload, attemptTimeout, overallToken, callerToken)
                .ConfigureAwait(false);
            return (true, parsed, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested || overallToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (IsRecoverableFetchFailure(error))
        {
            return (false, null, error);
        }
    }

    private async Task<StartupUpdateRelease?> ParseGitHubReleasesJsonAsync(
        byte[] payload,
        TimeSpan attemptTimeout,
        CancellationToken overallToken,
        CancellationToken callerToken)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var candidates = new List<JsonReleaseCandidate>();
        bool rejectedUntrustedUrl = false;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
                rejectedUntrustedUrl |= TryCollectJsonRelease(element, candidates);
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            rejectedUntrustedUrl |= TryCollectJsonRelease(root, candidates);
        }
        else
        {
            throw new JsonException("Unexpected GitHub release response format.");
        }

        if (candidates.Count == 0)
        {
            if (rejectedUntrustedUrl)
                throw new InvalidDataException("Release metadata referenced an untrusted URL.");
            return null;
        }

        candidates.Sort(static (left, right) => right.Version.CompareTo(left.Version));

        foreach (var candidate in candidates)
        {
            string? sha256 = candidate.DirectSha256;
            if (sha256 is null && candidate.ChecksumsUrl is not null)
            {
                var sumsCandidates = ResolveReleaseDownloadCandidates(candidate.ChecksumsUrl, options.DownloadMirrorBases);
                sha256 = await TryDownloadMatchingAsync(
                    sumsCandidates,
                    MaximumChecksumsBytes,
                    attemptTimeout,
                    overallToken,
                    callerToken,
                    text => FindChecksumForFile(text, candidate.AssetName)).ConfigureAwait(false);
            }

            if (sha256 is null) continue;

            return new StartupUpdateRelease(
                candidate.VersionText,
                candidate.Summary,
                candidate.ReleaseNotesUrl,
                new StartupUpdatePackage(candidate.DownloadUrl, sha256, candidate.Size));
        }

        return null;
    }

    private bool TryCollectJsonRelease(JsonElement element, List<JsonReleaseCandidate> results)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (element.TryGetProperty("draft", out var draftElement) && draftElement.ValueKind == JsonValueKind.True) return false;

        var tag = ReadOptionalString(element, "tag_name");
        if (!TryNormalizeVersion(tag, out var versionText, out var parsedVersion)) return false;

        var htmlUrlText = ReadOptionalString(element, "html_url");
        if (!Uri.TryCreate(htmlUrlText, UriKind.Absolute, out var htmlUrl) || !IsCanonicalRepoUri(htmlUrl))
            return true;

        var summary = SanitizeSummary(ReadOptionalString(element, "body"));

        if (!element.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return false;

        var installers = new List<(int Rank, string Name, Uri Url, long Size, string? DirectSha256)>();
        Uri? checksumsUrl = null;
        bool sawUntrustedAssetUrl = false;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            var name = ReadOptionalString(asset, "name");
            var urlText = ReadOptionalString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(urlText, UriKind.Absolute, out var parsedUrl))
                continue;

            if (!IsCanonicalRepoDownloadUri(parsedUrl))
            {
                if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)
                    || GetInstallerMatchRank(name, architecture) > 0)
                {
                    sawUntrustedAssetUrl = true;
                }
                continue;
            }

            if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                checksumsUrl = parsedUrl;
                continue;
            }

            int rank = GetInstallerMatchRank(name, architecture);
            if (rank == 0) continue;

            if (!asset.TryGetProperty("size", out var sizeProp)
                || sizeProp.ValueKind != JsonValueKind.Number
                || !sizeProp.TryGetInt64(out long size)
                || size <= 0)
            {
                continue;
            }

            var directSha256 = ParseDigestProperty(ReadOptionalString(asset, "digest"));
            installers.Add((rank, name, parsedUrl, size, directSha256));
        }

        if (installers.Count == 0) return sawUntrustedAssetUrl;

        // Prefer installers that already have a usable checksum source (direct digest or SHA256SUMS.txt),
        // then native architecture over x64 fallback, and direct digest over external checksums lookup.
        var usable = installers
            .Where(item => item.DirectSha256 is not null || checksumsUrl is not null)
            .OrderByDescending(item => item.Rank)
            .ThenByDescending(item => item.DirectSha256 is not null)
            .FirstOrDefault();
        if (usable.Name is null) return false;

        results.Add(new JsonReleaseCandidate(
            parsedVersion,
            versionText,
            htmlUrl,
            summary,
            usable.Name,
            usable.Url,
            usable.Size,
            usable.DirectSha256,
            checksumsUrl));
        return false;
    }

    private static (bool HadEntries, bool RejectedUntrustedUrl, AtomReleaseCandidate? Best) ParseHighestAtomEntry(byte[] atomBytes)
    {
        using var stream = new MemoryStream(atomBytes, writable: false);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaximumAtomFeedBytes
        };

        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        XNamespace atom = AtomNamespace;
        if (document.Root is not { } root || root.Name != atom + "feed")
            throw new XmlException("Expected Atom <feed> root element.");

        AtomReleaseCandidate? best = null;
        bool hadEntries = false;
        bool rejectedUntrustedUrl = false;

        foreach (var entry in root.Elements(atom + "entry"))
        {
            hadEntries = true;
            if (TryReadAtomEntry(entry, atom, out var candidate, out var untrusted))
            {
                if (best is null || candidate.Version.CompareTo(best.Version) > 0)
                    best = candidate;
            }
            else if (untrusted)
            {
                rejectedUntrustedUrl = true;
            }
        }

        return (hadEntries, rejectedUntrustedUrl, best);
    }

    private static bool TryReadAtomEntry(
        XElement entry,
        XNamespace atom,
        out AtomReleaseCandidate candidate,
        out bool rejectedUntrustedUrl)
    {
        candidate = default!;
        rejectedUntrustedUrl = false;
        string? tagFromLink = null;
        Uri? releaseNotesUrl = null;
        bool invalidLinkSeen = false;

        foreach (var link in entry.Elements(atom + "link"))
        {
            var rel = (string?)link.Attribute("rel");
            if (rel is not null && !string.Equals(rel, "alternate", StringComparison.OrdinalIgnoreCase))
                continue;

            var href = (string?)link.Attribute("href");
            if (Uri.TryCreate(href, UriKind.Absolute, out var linkUri)
                && linkUri.AbsoluteUri.StartsWith(CanonicalRepoTagPrefix, StringComparison.OrdinalIgnoreCase)
                && IsCanonicalRepoUri(linkUri))
            {
                releaseNotesUrl = linkUri;
                tagFromLink = Uri.UnescapeDataString(linkUri.AbsoluteUri[CanonicalRepoTagPrefix.Length..].TrimEnd('/'));
                break;
            }

            invalidLinkSeen = true;
        }

        if (invalidLinkSeen && tagFromLink is null)
        {
            rejectedUntrustedUrl = true;
            return false;
        }

        string? tagFromId = null;
        var idText = (string?)entry.Element(atom + "id");
        if (!string.IsNullOrWhiteSpace(idText))
        {
            int slash = idText.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < idText.Length)
                tagFromId = idText[(slash + 1)..].Trim();
        }

        var tag = tagFromLink ?? tagFromId;
        if (!TryNormalizeVersion(tag, out var versionText, out var parsedVersion)) return false;
        if (string.IsNullOrWhiteSpace(tag) || tag.Contains('/') || tag.Contains('\\') || tag.Contains("..")) return false;

        releaseNotesUrl ??= new Uri($"{CanonicalRepoTagPrefix}{Uri.EscapeDataString(tag)}");
        var summary = SanitizeSummary(StripHtmlTags((string?)entry.Element(atom + "content")));
        candidate = new AtomReleaseCandidate(parsedVersion, tag, versionText, releaseNotesUrl, summary);
        return true;
    }

    private async Task<T?> TryDownloadMatchingAsync<T>(
        IReadOnlyList<Uri> candidates,
        int maxBytes,
        TimeSpan attemptTimeout,
        CancellationToken overallToken,
        CancellationToken callerToken,
        Func<string, T?> selector)
    {
        foreach (var uri in candidates)
        {
            overallToken.ThrowIfCancellationRequested();
            try
            {
                var bytes = await ReadBoundedBytesAsync(uri, maxBytes, attemptTimeout, overallToken, callerToken)
                    .ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(bytes);
                var matched = selector(text);
                if (matched is not null) return matched;
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested || overallToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (IsRecoverableFetchFailure(error))
            {
            }
        }

        return default;
    }

    private async Task<long?> TryProbeContentLengthAsync(
        IReadOnlyList<Uri> candidates,
        TimeSpan attemptTimeout,
        CancellationToken overallToken,
        CancellationToken callerToken)
    {
        foreach (var uri in candidates)
        {
            overallToken.ThrowIfCancellationRequested();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(overallToken);
            attemptCts.CancelAfter(attemptTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                if (response.Content.Headers.ContentLength is long length and > 0)
                    return length;
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!overallToken.IsCancellationRequested)
            {
            }
            catch (Exception error) when (IsRecoverableFetchFailure(error))
            {
            }
        }

        return null;
    }

    private async Task<byte[]> ReadBoundedBytesAsync(
        Uri uri,
        int maxBytes,
        TimeSpan attemptTimeout,
        CancellationToken overallToken,
        CancellationToken callerToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(overallToken);
        attemptCts.CancelAfter(attemptTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > maxBytes)
                throw new InvalidDataException("Update metadata exceeded size limit.");

            await using var stream = await response.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int total = 0;

            while (true)
            {
                int read = await stream.ReadAsync(chunk, attemptCts.Token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > maxBytes) throw new InvalidDataException("Update metadata exceeded size limit.");
                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException error) when (!callerToken.IsCancellationRequested && !overallToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out fetching '{uri}'.", error);
        }
    }

    private static IReadOnlyList<(Uri Uri, Uri? MirrorBase)> BuildAtomCandidates(
        Uri primaryAtom,
        IReadOnlyList<Uri>? mirrorBases)
    {
        var list = new List<(Uri Uri, Uri? MirrorBase)>();
        var seen = new HashSet<Uri>();
        if (IsHttpsUri(primaryAtom) && seen.Add(primaryAtom))
            list.Add((primaryAtom, null));

        if (!IsCanonicalRepoUri(primaryAtom))
            return list;

        foreach (var mirrorBase in mirrorBases ?? StartupUpdateConfiguration.DefaultMirrorBases)
        {
            if (TryBuildMirrorUri(mirrorBase, primaryAtom, out var mirrored) && seen.Add(mirrored))
                list.Add((mirrored, mirrorBase));
        }

        return list;
    }

    private static IReadOnlyList<Uri> PrioritizeDownloadCandidates(
        Uri primaryUrl,
        Uri? activeMirrorBase,
        IReadOnlyList<Uri>? downloadMirrorBases,
        bool preferMirrorFirst)
    {
        var defaultOrder = ResolveReleaseDownloadCandidates(primaryUrl, downloadMirrorBases);
        if (!preferMirrorFirst || defaultOrder.Count <= 1) return defaultOrder;

        var reordered = new List<Uri>(defaultOrder.Count);
        if (activeMirrorBase is not null
            && TryBuildMirrorUri(activeMirrorBase, primaryUrl, out var preferredMirror)
            && !reordered.Contains(preferredMirror))
        {
            reordered.Add(preferredMirror);
        }

        for (int i = 1; i < defaultOrder.Count; i++)
        {
            if (!reordered.Contains(defaultOrder[i]))
                reordered.Add(defaultOrder[i]);
        }

        reordered.Add(defaultOrder[0]);
        return reordered;
    }

    private static (string FileName, string Sha256)? FindInstallerChecksum(string sumsText, string arch)
    {
        (string FileName, string Sha256)? fallback = null;
        foreach (var entry in ParseChecksumEntries(sumsText))
        {
            int rank = GetInstallerMatchRank(entry.FileName, arch);
            if (rank == 2) return entry;
            if (rank == 1 && fallback is null) fallback = entry;
        }

        return fallback;
    }

    private static string? FindChecksumForFile(string sumsText, string fileName)
    {
        foreach (var entry in ParseChecksumEntries(sumsText))
        {
            if (string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                return entry.Sha256;
        }

        return null;
    }

    private static IEnumerable<(string FileName, string Sha256)> ParseChecksumEntries(string sumsText)
    {
        using var reader = new StringReader(sumsText);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 67) continue;

            int separator = 0;
            while (separator < trimmed.Length && !char.IsWhiteSpace(trimmed[separator]))
                separator++;

            if (separator != 64 || !TryNormalizeSha256(trimmed[..separator], out var hash)) continue;

            var name = trimmed[separator..].TrimStart().TrimStart('*').Trim();
            if (!IsSafeFileName(name)) continue;

            yield return (name, hash);
        }
    }

    private static int GetInstallerMatchRank(string name, string arch)
    {
        if (!IsSafeFileName(name) || !name.StartsWith("MDA-", StringComparison.OrdinalIgnoreCase))
            return 0;

        string nativeSuffix = $"-win-{arch}-setup.exe";
        if (name.EndsWith(nativeSuffix, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (!string.Equals(arch, "x64", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith("-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

    private static bool IsSafeFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Contains('/')
        && !name.Contains('\\')
        && !name.Contains("..");

    private static string? ParseDigestProperty(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        const string Prefix = "sha256:";
        var candidate = digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[Prefix.Length..]
            : digest;
        return TryNormalizeSha256(candidate, out var normalized) ? normalized : null;
    }

    private static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.Length != 64) return false;
        foreach (char c in trimmed)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }

        normalized = trimmed.ToLowerInvariant();
        return true;
    }

    private static bool TryBuildMirrorUri(Uri? mirrorBase, Uri target, out Uri mirrored)
    {
        mirrored = null!;
        if (!IsHttpsUri(mirrorBase) || !IsHttpsUri(target)) return false;
        string baseText = mirrorBase!.AbsoluteUri.TrimEnd('/');
        return Uri.TryCreate($"{baseText}/{target.AbsoluteUri}", UriKind.Absolute, out mirrored!)
            && IsHttpsUri(mirrored);
    }

    private static bool IsCanonicalRepoUri(Uri uri) =>
        IsHttpsUri(uri)
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.AbsoluteUri.StartsWith(CanonicalRepoPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsCanonicalRepoDownloadUri(Uri uri) =>
        IsCanonicalRepoUri(uri)
        && uri.AbsoluteUri.StartsWith(CanonicalRepoDownloadPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpsUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsRecoverableFetchFailure(Exception error) => error is
        HttpRequestException or
        TaskCanceledException or
        TimeoutException or
        IOException or
        JsonException or
        XmlException;

    private static string NormalizeArchitecture(string? arch)
    {
        if (!string.IsNullOrWhiteSpace(arch))
            return arch.Trim().ToLowerInvariant();

        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };
    }

    private static string? ReadOptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string SanitizeSummary(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();
        return trimmed.Length <= StartupUpdateOptions.MaximumSummaryCharacters
            ? trimmed
            : trimmed[..StartupUpdateOptions.MaximumSummaryCharacters];
    }

    private static string StripHtmlTags(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var sb = new StringBuilder(html.Length);
        bool inTag = false;
        bool lastWasSpace = false;

        foreach (char ch in html)
        {
            if (ch == '<')
            {
                inTag = true;
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            if (ch == '>')
            {
                inTag = false;
                continue;
            }

            if (inTag) continue;

            if (ch is '\r' or '\n')
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append('\n');
                    lastWasSpace = true;
                }
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            sb.Append(ch);
            lastWasSpace = false;
        }

        return WebUtility.HtmlDecode(sb.ToString()).Trim();
    }

    private static bool TryNormalizeVersion(string? raw, out string versionText, out UpdateVersion parsed)
    {
        versionText = string.Empty;
        parsed = default!;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        int plus = trimmed.IndexOf('+');
        if (plus >= 0) trimmed = trimmed[..plus];
        if (!UpdateVersion.TryParse(trimmed, out var version) || version is null) return false;

        versionText = trimmed;
        parsed = version;
        return true;
    }

    private sealed record JsonReleaseCandidate(
        UpdateVersion Version,
        string VersionText,
        Uri ReleaseNotesUrl,
        string Summary,
        string AssetName,
        Uri DownloadUrl,
        long Size,
        string? DirectSha256,
        Uri? ChecksumsUrl);

    private sealed record AtomReleaseCandidate(
        UpdateVersion Version,
        string Tag,
        string VersionText,
        Uri ReleaseNotesUrl,
        string Summary);
}
