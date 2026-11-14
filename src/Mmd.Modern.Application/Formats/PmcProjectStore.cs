namespace Mmd.Modern.Application;

/// <summary>Classic native project storage identity. Shares the neutral ZIP64, SHA256, immutable
/// snapshot and atomic publication path with PMA without interpreting renderer extensions.</summary>
public static class PmcProjectStore
{
    public const string Format = "mmd-classic-pmc";
    public const int Version = 1;

    public static void Save(string path, SceneProjectSession project, PmaSaveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = CaptureSnapshot(project, options, cancellationToken);
        snapshot.SaveAndDispose(path, cancellationToken);
    }

    /// <summary>Captures authored neutral state and opaque resources without advancing animation or physics.
    /// The detached snapshot retains PMC identity and requires a .pmc destination.</summary>
    public static PmaSaveSnapshot CaptureSnapshot(SceneProjectSession project, PmaSaveOptions? options = null,
        CancellationToken cancellationToken = default)
        => PmaProjectStore.CaptureSnapshot(project, options, cancellationToken, PmaPackageKind.Classic);

    public static PmaLoadedProject Load(string path, string cacheDirectory,
        Func<string, CancellationToken, SceneAnimationModelAsset> loadModel, IProjectPhysicsBackendProvider physics,
        PmaLoadOptions? options = null, CancellationToken cancellationToken = default)
        => PmaProjectStore.Load(path, cacheDirectory, loadModel, physics, options, cancellationToken, PmaPackageKind.Classic);
}
