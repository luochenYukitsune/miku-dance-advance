using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorRecoveryFormatTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-recovery-format-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClassicCheckpointUsesItsFrozenFormatForStagingAndRestartDiscovery()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Unnamed", Token);
        var metadata = Metadata(classic: true);
        string? staging = null;
        var checkpoint = await store.WriteCheckpointAsync(session, metadata, (path, token) =>
        {
            staging = path;
            return File.WriteAllTextAsync(path, "classic package", token);
        }, Token);
        Assert.EndsWith(".tmp.pmc", staging);
        Assert.EndsWith(".pmc", checkpoint.PackagePath);
        Assert.Equal("mmd-classic-pmc", FormatIdentity(checkpoint));
        session.Dispose();
        var reloaded = Assert.Single(Assert.Single(await new EditorRecoveryStore(store.RootDirectory).ListRecoverableAsync(Token)).Checkpoints);
        Assert.Equal(checkpoint.CheckpointId, reloaded.CheckpointId);
        Assert.Equal("mmd-classic-pmc", FormatIdentity(reloaded));
    }

    [Fact]
    public async Task SchemaOneMissingIdentityMigratesOnlyAnExplicitPmaBasename()
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Legacy", Token);
        var checkpoint = await Write(store, session);
        session.Dispose();
        string jsonPath = Path.ChangeExtension(checkpoint.PackagePath, ".json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath, Token))!.AsObject();
        json["schemaVersion"] = 1;
        json.Remove("projectFormat");
        await File.WriteAllTextAsync(jsonPath, json.ToJsonString(), Token);
        var migrated = Assert.Single(Assert.Single(await store.ListRecoverableAsync(Token)).Checkpoints);
        Assert.Equal("mmd-modern-pma", FormatIdentity(migrated));
        string classicPath = Path.ChangeExtension(checkpoint.PackagePath, ".pmc");
        File.Move(checkpoint.PackagePath, classicPath);
        json["packageFileName"] = Path.GetFileName(classicPath);
        await File.WriteAllTextAsync(jsonPath, json.ToJsonString(), Token);
        Assert.Empty(await store.ListRecoverableAsync(Token));
        Assert.True(File.Exists(classicPath));
    }

    [Theory]
    [InlineData("mmd-modern-pma")]
    [InlineData("unknown-project-format")]
    [InlineData(null)]
    public async Task ClassicIdentityCannotBeMissingUnknownOrDisagreeWithItsExtension(string? identity)
    {
        var store = Store();
        using var session = await store.CreateSessionAsync(null, "Classic", Token);
        var checkpoint = await Write(store, session, classic: true);
        session.Dispose();
        string jsonPath = Path.ChangeExtension(checkpoint.PackagePath, ".json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath, Token))!.AsObject();
        if (identity is null) json.Remove("projectFormat");
        else json["projectFormat"] = identity;
        await File.WriteAllTextAsync(jsonPath, json.ToJsonString(), Token);
        Assert.Empty(await store.ListRecoverableAsync(Token));
        Assert.True(File.Exists(checkpoint.PackagePath));
    }

    [Fact]
    public async Task AbandonedClassicStagingPackagesShareThePersistentByteBudget()
    {
        var store = Store(new(MaxVersions: 5, MaxTotalBytes: 10));
        using var session = await store.CreateSessionAsync(null, "Budget", Token);
        string staging = Path.Combine(store.RootDirectory, session.SessionId.ToString("N"), Guid.NewGuid().ToString("N") + ".tmp.pmc");
        await File.WriteAllTextAsync(staging, "123456", Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteCheckpointAsync(session, Metadata(),
            (path, token) => File.WriteAllTextAsync(path, "123456", token), Token));
        Assert.Equal("123456", await File.ReadAllTextAsync(staging, Token));
    }

    [Theory]
    [InlineData(".PMA")]
    [InlineData(".PMC")]
    [InlineData(".tmp.PMA")]
    [InlineData(".tmp.PMC")]
    public async Task WindowsCaseVariantsCannotBypassTheRecoveryByteBudget(string suffix)
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Store(new(MaxVersions: 5, MaxTotalBytes: 10));
        using var session = await store.CreateSessionAsync(null, "Case budget", Token);
        string existing = Path.Combine(store.RootDirectory, session.SessionId.ToString("N"), Guid.NewGuid().ToString("N") + suffix);
        await File.WriteAllTextAsync(existing, "123456", Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteCheckpointAsync(session, Metadata(),
            (path, token) => File.WriteAllTextAsync(path, "123456", token), Token));
        Assert.Equal("123456", await File.ReadAllTextAsync(existing, Token));
    }

    [Fact]
    public async Task SnapshotCleanupFailureKeepsSavedCheckpointAndAdvancesItsFingerprint()
    {
        var store = Store();
        using var coordinator = new EditorAutoSaveCoordinator(store);
        Guid document = Guid.NewGuid();
        var cleanup = new IOException("capture cleanup failed");
        var result = await coordinator.CheckpointAsync(document, "frozen-state", () => new(Metadata(), new ThrowingOwner(cleanup),
            (path, token) => File.WriteAllText(path, "package")), Token);
        Assert.Equal(EditorAutoSaveStatus.Saved, result.Status);
        Assert.NotNull(result.Checkpoint);
        Assert.Same(cleanup, result.Error);
        Assert.True(File.Exists(result.Checkpoint.PackagePath));
        var unchanged = await coordinator.CheckpointAsync(document, "frozen-state",
            () => throw new InvalidOperationException("The committed state must not be captured again."), Token);
        Assert.Equal(EditorAutoSaveStatus.Unchanged, unchanged.Status);
    }

    [Fact]
    public async Task PublishedCheckpointSurvivesRetentionCleanupFailureAndIsReportedAsCommitted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Store(new(MaxVersions: 1));
        using var session = await store.CreateSessionAsync(null, "Retention", Token);
        var previous = await Write(store, session);
        using var blocker = new FileStream(Path.ChangeExtension(previous.PackagePath, ".json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = await Assert.ThrowsAnyAsync<IOException>(() => Write(store, session));
        Assert.Equal("EditorRecoveryCommittedException", error.GetType().Name);
        var committed = Assert.IsType<EditorRecoveryCheckpoint>(error.GetType().GetProperty("Checkpoint")?.GetValue(error));
        Assert.NotEqual(previous.CheckpointId, committed.CheckpointId);
        Assert.True(File.Exists(committed.PackagePath));
        Assert.True(File.Exists(Path.ChangeExtension(committed.PackagePath, ".json")));
        blocker.Dispose();
        session.Dispose();
        Assert.Equal(committed.CheckpointId, (await store.GetLatestValidCheckpointAsync(session.SessionId, Token))!.CheckpointId);
    }

    [Fact]
    public void PackageIdentityParticipatesInTheFrozenFingerprint()
    {
        Assert.NotEqual(EditorAutoSaveSnapshot.GetFingerprint(Metadata()), EditorAutoSaveSnapshot.GetFingerprint(Metadata(classic: true)));
    }

    private EditorRecoveryStore Store(RecoveryRetentionPolicy? policy = null) => new(Path.Combine(directory, "recovery"), policy);
    private Task<EditorRecoveryCheckpoint> Write(EditorRecoveryStore store, EditorRecoverySession session, bool classic = false) =>
        store.WriteCheckpointAsync(session, Metadata(classic), (path, token) => File.WriteAllTextAsync(path, "package", token), Token);

    // Reflection keeps the RED baseline buildable before the additive descriptor contract lands.
    private static EditorRecoveryCheckpointMetadata Metadata(bool classic = false)
    {
        using var state = JsonDocument.Parse("{\"frame\":4}");
        if (!classic) return new(null, "Unnamed", 1, state.RootElement);
        Type? descriptor = typeof(EditorRecoveryStore).Assembly.GetType("Mmd.Modern.App.EditorProjectFormat");
        Assert.NotNull(descriptor);
        object? format = descriptor.GetProperty("Classic", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        Assert.NotNull(format);
        var constructor = typeof(EditorRecoveryCheckpointMetadata).GetConstructors()
            .SingleOrDefault(candidate => candidate.GetParameters().Any(parameter => parameter.ParameterType == descriptor));
        Assert.NotNull(constructor);
        return (EditorRecoveryCheckpointMetadata)constructor.Invoke([null, "Unnamed", 1L, state.RootElement, 1, format]);
    }

    private static string FormatIdentity(EditorRecoveryCheckpoint checkpoint)
    {
        object? format = checkpoint.GetType().GetProperty("ProjectFormat")?.GetValue(checkpoint);
        Assert.NotNull(format);
        return Assert.IsType<string>(format.GetType().GetProperty("PackageFormat")?.GetValue(format));
    }

    private sealed class ThrowingOwner(Exception error) : IDisposable { public void Dispose() => throw error; }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
