namespace Mmd.Modern.App.Updates;

public sealed record StartupUpdateOptions
{
    // Intentionally unconfigured: startup must not contact a placeholder endpoint.
    public Uri? Endpoint { get; init; }
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public const int MaximumFeedBytes = 64 * 1024;
    public const int MaximumSummaryCharacters = 4000;
}

public static class StartupUpdateConfiguration
{
    public static StartupUpdateOptions DefaultOptions { get; } = new();
}

/// <param name="Summary">Plain text displayed as text, never rendered as HTML or Markdown.</param>
public sealed record StartupUpdateRelease(string Version, string Summary, Uri? ReleasePage = null,
    StartupUpdatePackage? Package = null);
public sealed record StartupUpdatePackage(Uri Url, string Sha256, long? Size = null);
public enum StartupUpdateStatus { Disabled, NoUpdate, SkippedVersion, Available, TimedOut, Unavailable, InvalidMetadata }
public sealed record StartupUpdateCheckResult(StartupUpdateStatus Status, StartupUpdateRelease? Release = null);

public interface IStartupUpdateMetadataSource
{
    Task<StartupUpdateRelease?> GetLatestAsync(Uri endpoint, CancellationToken cancellationToken);
}

public interface IStartupUpdateSkipStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(string version, CancellationToken cancellationToken);
}

// An explicitly chosen upgrade flow owns download, verification and installer consent.
// Checking metadata never invokes this interface or starts a process.
public interface IStartupUpdateUpgradeHandler
{
    Task<StartupUpdateUpgradeResult> UpgradeAsync(StartupUpdateRelease release,
        IProgress<StartupUpdateProgress>? progress = null, CancellationToken cancellationToken = default);
}
public sealed record StartupUpdateProgress(long DownloadedBytes, long? TotalBytes = null);
public sealed record StartupUpdateUpgradeResult(bool InstallerStarted, string? Message = null);
