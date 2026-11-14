using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorStartupRenderingTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrdinaryStartupWaitsForExplicitSelectionAndRejectsIncompleteCompatibility()
    {
        var selection = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var displayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<EditorRenderEngineChoice>? offered = null;
        var choosing = EditorStartupApplication.ChooseRenderEngineAsync(null,
            (choices, _) => { offered = choices; displayed.SetResult(); return selection.Task; }, Token);
        Assert.False(choosing.IsCompleted);
        await displayed.Task.WaitAsync(Token);
        Assert.NotNull(offered);
        Assert.True(Assert.Single(offered, choice => choice.Id == "modern").IsAvailable);
        var compatibility = Assert.Single(offered, choice => choice.Id == "compatibility");
        Assert.False(compatibility.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(compatibility.UnavailableReason));
        selection.SetResult("modern");
        Assert.Same(EditorModernRenderEngine.Instance, await choosing);
    }

    [Theory]
    [InlineData("compatibility")]
    [InlineData("unknown")]
    public async Task AnUnavailableOrUnknownConfirmedIdNeverFallsBackToAnotherEngine(string id)
        => await Assert.ThrowsAsync<NotSupportedException>(() => EditorStartupApplication.ChooseRenderEngineAsync(null,
            (_, _) => Task.FromResult<string?>(id), Token));

    [Fact]
    public async Task ClosingTheSelectionDoesNotChooseAnEngine()
        => Assert.Null(await EditorStartupApplication.ChooseRenderEngineAsync(null, (_, _) => Task.FromResult<string?>(null), Token));

    [Fact]
    public async Task CancellationAfterTheDialogReturnsDoesNotCreateAnEditor()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EditorStartupApplication.ChooseRenderEngineAsync(null,
            (_, _) => { cancellation.Cancel(); return Task.FromResult<string?>("modern"); }, cancellation.Token));
    }

    [Fact]
    public async Task IsolatedHostsCanExerciseTheRealSelectionWithoutLoadingUserPreferences() => await session.Dispatch(async () =>
    {
        var configuration = new EditorStartupConfiguration(new(), Path.Combine(Path.GetTempPath(), "mda-selection-isolated-cache"))
            { PromptForRenderEngine = true };
        bool shown = false;
        var selected = await EditorStartupApplication.ChooseRenderEngineAsync(configuration,
            (_, _) => { shown = true; return Task.FromResult<string?>(null); }, Token);
        Assert.True(shown);
        Assert.Null(selected);
        Assert.False(configuration.PersistPreferences);
        return true;
    }, Token);

    [Fact]
    public async Task ControlledHostsKeepTheirExplicitEngineWithoutOpeningASelectionWindow() => await session.Dispatch(async () =>
    {
        var configuration = new EditorStartupConfiguration(new(), Path.Combine(Path.GetTempPath(), "mda-selection-host-cache"));
        var engine = await EditorStartupApplication.ChooseRenderEngineAsync(configuration,
            (_, _) => throw new InvalidOperationException("An explicit host must not open a modal selection."), Token);
        Assert.Same(configuration.RenderEngine, engine);
        var pose = new PmmCameraPose(new(0, 10, 0), System.Numerics.Vector3.Zero, -45, 30, true);
        using var shell = new EditorShellViewModel(pose, engine!);
        Assert.Same(engine, shell.Workspace.RenderEngine);
        Assert.True(shell.HasCameraPose);
        Assert.Equal(pose, shell.CameraPose);
        return true;
    }, Token);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
