using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mmd.Modern.App;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class ExportDiagnosticUiTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    [InlineData("ja")]
    public async Task RetainedExportLogHasCopyButtonAndWrappingAiHintBelowIt(string language)
        => await session.Dispatch(() =>
    {
        EditorLocalization.SetLanguage(language);
        var model = new ExportRenderStatusViewModel("fixture.png", 1);
        var window = new ExportRenderStatusWindow(model) { Width = 620, Height = 550 };
        window.Show();
        try
        {
            var error = new IOException("original error"); error.Data["boneIndex"] = 7;
            model.MarkFailed(error); Dispatcher.UIThread.RunJobs();
            var copy = window.FindControl<Button>("CopyErrorButton")!;
            var hint = window.FindControl<TextBlock>("LogAiHint")!;
            Assert.True(model.HasDiagnostics); Assert.Contains("boneIndex", model.CopyableDetails);
            Assert.True(copy.IsVisible); Assert.True(hint.IsVisible);
            Assert.Equal(EditorLocalization.Text("Mda.Text.ExportStatus.LogAiHint"), hint.Text);
            Assert.Equal(TextWrapping.Wrap, hint.TextWrapping);
            var bottom = copy.TranslatePoint(new Point(0, copy.Bounds.Height), window);
            var top = hint.TranslatePoint(default, window);
            Assert.NotNull(bottom); Assert.NotNull(top); Assert.True(top!.Value.Y >= bottom!.Value.Y);
        }
        finally { window.Close(); EditorLocalization.SetLanguage("zh-CN"); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, Token);

    [Fact]
    public async Task CommonErrorPopupPlacesTheSameHintBelowTheCopyLogButton()
        => await session.Dispatch(async () =>
    {
        var owner = new Window { Width = 640, Height = 480 }; owner.Show();
        try
        {
            var operation = (Task)typeof(EditorWindow).GetMethod("ShowActivityDetailsAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, ["fixture failure", "original technical details", owner])!;
            Dispatcher.UIThread.RunJobs();
            var popup = Assert.Single(owner.OwnedWindows);
            var copy = popup.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "CopyOperationLogButton");
            var hint = popup.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "OperationLogAiHint");
            Assert.Equal(EditorLocalization.Text("Mda.Text.ExportStatus.CopyLog"), copy.Content);
            Assert.Equal(EditorLocalization.Text("Mda.Text.ExportStatus.LogAiHint"), hint.Text);
            Assert.True(hint.TranslatePoint(default, popup)!.Value.Y >= copy.TranslatePoint(new Point(0, copy.Bounds.Height), popup)!.Value.Y);
            popup.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("original technical details", popup.GetVisualDescendants().OfType<TextBox>()
                .Single(control => control.Name == "OperationErrorDetails").Text);
            popup.Close(); await operation;
        }
        finally { foreach (var child in owner.OwnedWindows.ToArray()) child.Close(); owner.Close(); Dispatcher.UIThread.RunJobs(); }
        return true;
    }, Token);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
