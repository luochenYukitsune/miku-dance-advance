namespace Mmd.Modern.App.Updates;

public sealed class StartupUpdateService
{
    private readonly StartupUpdateOptions options;
    private readonly string currentVersion;
    private readonly Func<IStartupUpdateMetadataSource> sourceFactory;
    private readonly IStartupUpdateSkipStore? skipStore;
    public bool IsConfigured => options.Endpoint is not null;

    public StartupUpdateService(StartupUpdateOptions options, string currentVersion,
        Func<IStartupUpdateMetadataSource>? sourceFactory = null, IStartupUpdateSkipStore? skipStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (options.CheckTimeout <= TimeSpan.Zero || options.CheckTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(options), "Update checks require a timeout of at most 30 seconds.");
        this.options = options;
        this.currentVersion = currentVersion;
        this.sourceFactory = sourceFactory ?? (() => new JsonStartupUpdateMetadataSource(DefaultHttpClient.Instance.Value));
        this.skipStore = skipStore;
    }

    public async Task<StartupUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Endpoint is null) return new(StartupUpdateStatus.Disabled);
        if (!IsHttps(options.Endpoint) || !UpdateVersion.TryParse(currentVersion, out _))
            return new(StartupUpdateStatus.InvalidMetadata);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.CheckTimeout);
        // Isolate a provider that blocks before returning its Task as well as one ignoring cancellation.
        var token = timeout.Token;
        var operation = Task.Run(() => CheckCoreAsync(token), CancellationToken.None);
        try { return await operation.WaitAsync(options.CheckTimeout, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelAndObserve(timeout, operation);
            throw;
        }
        catch (TimeoutException)
        {
            CancelAndObserve(timeout, operation);
            return new(StartupUpdateStatus.TimedOut);
        }
        catch (OperationCanceledException)
        { return new(timeout.IsCancellationRequested ? StartupUpdateStatus.TimedOut : StartupUpdateStatus.Unavailable); }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
        { return new(StartupUpdateStatus.InvalidMetadata); }
        catch (Exception) { return new(StartupUpdateStatus.Unavailable); }
    }

    public async Task SkipVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Endpoint is null) return;
        if (!UpdateVersion.TryParse(version, out _)) throw new ArgumentException("Invalid update version.", nameof(version));
        await GetSkipStore().WriteAsync(version, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StartupUpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        var release = await sourceFactory().GetLatestAsync(options.Endpoint!, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (release is null) return new(StartupUpdateStatus.NoUpdate);
        if (!UpdateVersion.TryParse(release.Version, out var remote) || release.Summary is null ||
            (release.ReleasePage is not null && !IsHttps(release.ReleasePage)) ||
            (release.Package is { } package && (!IsHttps(package.Url) || package.Size is <= 0 ||
                package.Sha256 is not { Length: 64 } || !package.Sha256.All(Uri.IsHexDigit))))
            return new(StartupUpdateStatus.InvalidMetadata);
        UpdateVersion.TryParse(currentVersion, out var current);
        if (remote!.CompareTo(current) <= 0) return new(StartupUpdateStatus.NoUpdate);
        var skipped = await GetSkipStore().ReadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (UpdateVersion.TryParse(skipped, out var ignored) && remote.CompareTo(ignored) == 0)
            return new(StartupUpdateStatus.SkippedVersion);
        var summary = new string(release.Summary.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t')
            .Take(StartupUpdateOptions.MaximumSummaryCharacters).ToArray());
        return new(StartupUpdateStatus.Available, release with { Summary = summary });
    }

    private IStartupUpdateSkipStore GetSkipStore() => skipStore ?? new FileStartupUpdateSkipStore(Path.Combine(
        EditorUserDataPaths.Current.UpdateDirectory, "skipped-version.txt"));
    internal static bool IsHttps(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https" } && string.IsNullOrEmpty(uri.UserInfo);
    private static void CancelAndObserve(CancellationTokenSource cancellation, Task operation)
    {
        // Third-party providers can register throwing callbacks; they must not stop normal startup.
        try { cancellation.Cancel(); } catch (AggregateException) { }
        _ = operation.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private static class DefaultHttpClient
    { internal static readonly Lazy<HttpClient> Instance = new(static () => new HttpClient()); }
}

internal sealed record UpdateVersion(Version Numeric, string[] Prerelease) : IComparable<UpdateVersion>
{
    public static bool TryParse(string? text, out UpdateVersion? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 96 || text != text.Trim()) return false;
        var build = text.Split('+');
        if (build.Length > 2 || (build.Length == 2 && !ValidIdentifiers(build[1], false))) return false;
        var parts = build[0].Split('-', 2);
        if (!Version.TryParse(parts[0], out var numeric) || parts[0].Split('.').Any(p =>
            p.Length == 0 || !p.All(char.IsAsciiDigit) || (p.Length > 1 && p[0] == '0'))) return false;
        if (parts.Length == 2 && !ValidIdentifiers(parts[1], true)) return false;
        result = new(new(numeric.Major, numeric.Minor, Math.Max(0, numeric.Build), Math.Max(0, numeric.Revision)),
            parts.Length == 2 ? parts[1].Split('.') : []);
        return true;
    }

    private static bool ValidIdentifiers(string text, bool prerelease) => text.Split('.').All(p => p.Length > 0 &&
        p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        !(prerelease && p.Length > 1 && p[0] == '0' && p.All(char.IsAsciiDigit)));

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        var order = Numeric.CompareTo(other.Numeric);
        if (order != 0) return order;
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
            return other.Prerelease.Length.CompareTo(Prerelease.Length);
        for (var i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
        {
            var left = Prerelease[i]; var right = other.Prerelease[i];
            var ln = left.All(char.IsAsciiDigit); var rn = right.All(char.IsAsciiDigit);
            order = ln && rn ? left.Length.CompareTo(right.Length) : ln != rn ? (ln ? -1 : 1) : 0;
            if (order == 0) order = string.CompareOrdinal(left, right);
            if (order != 0) return order;
        }
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }
}
