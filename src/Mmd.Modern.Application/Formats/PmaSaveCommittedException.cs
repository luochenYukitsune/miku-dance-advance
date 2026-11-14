using System.Runtime.ExceptionServices;

namespace Mmd.Modern.Application;

/// <summary>The validated project package was atomically published, but releasing temporary or
/// retained resources failed. The destination already contains the saved project.</summary>
public sealed class PmaSaveCommittedException : IOException
{
    public string DestinationPath { get; }

    internal PmaSaveCommittedException(string destinationPath, Exception cleanupException)
        : base($"The project was saved to '{destinationPath}', but resource cleanup failed.", cleanupException)
        => DestinationPath = destinationPath;
}

internal static class PmaSaveCleanup
{
    // Every cleanup is attempted. A publication or capture error remains the primary error;
    // otherwise a completed publication must remain distinguishable from a failed save.
    internal static void Complete(Exception? primary, string? committedDestination, params Action[] cleanup)
    {
        Exception? firstCleanup = null;
        foreach (var action in cleanup)
        {
            try { action(); }
            catch (Exception exception)
            {
                if (primary is not null) Attach(primary, exception);
                else if (firstCleanup is null) firstCleanup = exception;
                else Attach(firstCleanup, exception);
            }
        }
        if (firstCleanup is null) return;
        if (committedDestination is not null) throw new PmaSaveCommittedException(committedDestination, firstCleanup);
        ExceptionDispatchInfo.Capture(firstCleanup).Throw();
    }

    private static void Attach(Exception primary, Exception cleanup)
    {
        const string key = "PmaSaveCleanupException";
        string available = key;
        for (int suffix = 1; primary.Data.Contains(available); suffix++) available = key + "." + suffix;
        primary.Data[available] = cleanup;
    }
}
