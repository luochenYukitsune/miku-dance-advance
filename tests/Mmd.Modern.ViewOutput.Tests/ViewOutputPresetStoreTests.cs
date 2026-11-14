using System.Text;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.Tests;

public sealed class ViewOutputPresetStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Environment.GetEnvironmentVariable("MMD_VIEW_OUTPUT_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "view-output-preset-tests"), Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndCopyUseNewIdentitiesAndPreserveOldBytesAcrossReopen()
    {
        var store = new ViewOutputPresetStore(directory);
        var preview = new ViewRenderRequest(1280, 720, new(SpatialSamples: 2,
            Upscaling: new() { Mode = ViewUpscalingMode.Fsr1, RcasSharpness = .7f }));
        var export = new ViewExportPreset(3840, 2160, new OfflineFrameRate(60000, 1001),
            new(Gi: new(Strength: 7, SamplesPerPixel: 32), Antialiasing: FrameAntialiasing.Off,
                TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Dlss, DlssQuality = ViewDlssQuality.Quality }));
        var encoding = new ViewOutputEncodingPreset("example-provider", 3,
            "{\"comment\":\"日本語／中文\",\"padding\":\"" + new string('x', 12000) + "\"}");
        var first = store.SaveAsNew("预览与导出", preview, export, encoding, TestContext.Current.CancellationToken);
        string firstPath = Path.Combine(directory, first.StableId + ".json");
        byte[] original = File.ReadAllBytes(firstPath);
        Assert.True(original.Length > 12000);
        var second = store.SaveAsNew(first, "另存副本", TestContext.Current.CancellationToken);

        Assert.StartsWith("custom-", first.StableId);
        Assert.True(Guid.TryParseExact(first.StableId[7..], "N", out _));
        Assert.NotEqual(first.StableId, second.StableId);
        Assert.Equal(original, File.ReadAllBytes(firstPath));
        Assert.Equal(2, Directory.GetFiles(directory, "*.json").Length);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        var catalog = new ViewOutputPresetStore(directory).List(TestContext.Current.CancellationToken);
        Assert.Equal(2, catalog.List().Count);
        var restored = catalog.Find(second.StableId);
        Assert.True(restored.IsAvailable);
        Assert.Equal("另存副本", restored.Definition!.Name);
        Assert.Equal(preview, restored.Definition.Preview);
        Assert.Equal(export, restored.Definition.Export);
        Assert.Equal(encoding.ProviderId, restored.Definition.Encoding!.ProviderId);
        Assert.Equal(encoding.SchemaVersion, restored.Definition.Encoding.SchemaVersion);
        using var expectedData = JsonDocument.Parse(encoding.DataJson);
        using var restoredData = JsonDocument.Parse(restored.Definition.Encoding.DataJson);
        Assert.True(JsonElement.DeepEquals(expectedData.RootElement, restoredData.RootElement));
        Assert.Equal("日本語／中文", restoredData.RootElement.GetProperty("comment").GetString());
        Assert.Equal(first.Name, catalog.Find(first.StableId).Definition!.Name);
        Assert.Equal(ViewOutputPresetStatus.Available, store.Find(first.StableId, TestContext.Current.CancellationToken).Status);
    }

    [Fact]
    public void CatalogIsDetachedAndItsListCannotBeModified()
    {
        var store = new ViewOutputPresetStore(directory);
        var saved = store.SaveAsNew("Original", new(640, 480), new(1920, 1080, new(30)), cancellationToken: TestContext.Current.CancellationToken);
        var catalog = store.List(TestContext.Current.CancellationToken);
        var entries = Assert.IsAssignableFrom<IList<ViewOutputPresetResult>>(catalog.List());
        Assert.True(entries.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => entries.Clear());
        File.WriteAllText(Path.Combine(directory, saved.StableId + ".json"), "{}");
        _ = store.SaveAsNew("Later", new(800, 600), new(1920, 1080, new(60)), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(catalog.List());
        Assert.True(catalog.Find(saved.StableId).IsAvailable);
        Assert.Equal("Original", catalog.Find(saved.StableId).Definition!.Name);
        Assert.Equal(ViewOutputPresetStatus.InvalidData, store.Find(saved.StableId, TestContext.Current.CancellationToken).Status);
    }

    [Fact]
    public void MissingDirectoryIsReadOnlyAndUnpublishedOrNestedFilesAreIgnored()
    {
        var store = new ViewOutputPresetStore(directory);
        Assert.Empty(store.List(TestContext.Current.CancellationToken).List());
        Assert.Equal(ViewOutputPresetStatus.Missing, store.Find("custom-missing", TestContext.Current.CancellationToken).Status);
        Assert.False(Directory.Exists(directory));
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllText(Path.Combine(directory, "partial.json.tmp"), "{");
        File.WriteAllBytes(Path.Combine(directory, "nested", "custom-hidden.json"), Bytes("custom-hidden"));
        Assert.Empty(store.List(TestContext.Current.CancellationToken).List());
        Assert.Equal(ViewOutputPresetStatus.Missing, store.Find("custom-hidden", TestContext.Current.CancellationToken).Status);
    }

    [Fact]
    public void BadFutureOversizedAndMismatchedFilesDoNotPoisonAValidPreset()
    {
        var store = new ViewOutputPresetStore(directory);
        var saved = store.SaveAsNew("Valid", new(640, 480), new(1920, 1080, new(30)), cancellationToken: TestContext.Current.CancellationToken);
        Write("custom-bad", Encoding.UTF8.GetBytes("{}"));
        Write("custom-future", Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Bytes("custom-future"))
            .Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal)));
        Write("custom-large", new byte[ViewOutputPresetStore.MaxPresetBytes + 1]);
        Write("custom-utf8", [0xff, 0xfe, 0x7b, 0x7d]);
        Write("custom-mismatch", Bytes("custom-another"));
        var catalog = store.List(TestContext.Current.CancellationToken);
        Assert.Equal(6, catalog.List().Count);
        Assert.True(catalog.Find(saved.StableId).IsAvailable);
        Assert.Equal(ViewOutputPresetStatus.UnsupportedSchema, catalog.Find("custom-future").Status);
        Assert.Null(catalog.Find("custom-future").Definition);
        foreach (string id in new[] { "custom-bad", "custom-large", "custom-utf8", "custom-mismatch" })
        {
            Assert.Equal(ViewOutputPresetStatus.InvalidData, catalog.Find(id).Status);
            Assert.Null(catalog.Find(id).Definition);
        }
        Assert.Equal(ViewOutputPresetStatus.Missing, catalog.Find("custom-another").Status);
    }

    [Fact]
    public void AFileThatCannotBeReadDoesNotHideOtherPresets()
    {
        var store = new ViewOutputPresetStore(directory);
        var saved = store.SaveAsNew("Valid", new(640, 480), new(1920, 1080, new(30)), cancellationToken: TestContext.Current.CancellationToken);
        Write("custom-locked", Bytes("custom-locked"));
        using var locked = new FileStream(Path.Combine(directory, "custom-locked.json"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        var catalog = store.List(TestContext.Current.CancellationToken);
        Assert.True(catalog.Find(saved.StableId).IsAvailable);
        Assert.Equal(ViewOutputPresetStatus.IoFailure, catalog.Find("custom-locked").Status);
        Assert.Equal(ViewOutputPresetStatus.Missing, catalog.Find("custom-not-installed").Status);
    }

    [Fact]
    public void DirectoryCapacityStopsLoadingAndSavingWithoutChangingExistingFiles()
    {
        Directory.CreateDirectory(directory);
        for (int i = 0; i < ViewOutputPresetStore.MaxLocalPresets; i++) Write("custom-" + i, Bytes("custom-" + i));
        var store = new ViewOutputPresetStore(directory);
        Assert.Equal(ViewOutputPresetStore.MaxLocalPresets, store.List(TestContext.Current.CancellationToken).List().Count);
        Assert.Throws<InvalidOperationException>(() => store.SaveAsNew("Overflow", new(640, 480), new(1920, 1080, new(30)),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Write("custom-overflow", Bytes("custom-overflow"));
        var catalog = store.List(TestContext.Current.CancellationToken);
        Assert.Single(catalog.List());
        Assert.Equal(ViewOutputPresetStatus.CapacityExceeded, catalog.List()[0].Status);
        Assert.Equal(ViewOutputPresetStatus.CapacityExceeded, catalog.Find("custom-0").Status);
        Assert.Equal(ViewOutputPresetStatus.CapacityExceeded, store.Find("custom-missing", TestContext.Current.CancellationToken).Status);
        Assert.Equal(ViewOutputPresetStore.MaxLocalPresets + 1, Directory.GetFiles(directory, "*.json").Length);
    }

    [Fact]
    public void LockedWriterFailsPromptlyAndKeepsExistingBytesAndNoTemporaryFiles()
    {
        var store = new ViewOutputPresetStore(directory);
        var saved = store.SaveAsNew("Existing", new(640, 480), new(1920, 1080, new(30)), cancellationToken: TestContext.Current.CancellationToken);
        string path = Path.Combine(directory, saved.StableId + ".json");
        byte[] original = File.ReadAllBytes(path);
        using (var locked = new FileStream(Path.Combine(directory, ".write.lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => new ViewOutputPresetStore(directory).SaveAsNew(saved, "Busy", TestContext.Current.CancellationToken));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(directory, "*.json"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void CancelledOrInvalidSaveDoesNotCreateStorage()
    {
        var store = new ViewOutputPresetStore(directory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.SaveAsNew("Cancelled", new(640, 480),
            new(1920, 1080, new(30)), cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => store.List(cancellation.Token));
        Assert.Throws<ArgumentException>(() => store.SaveAsNew(" ", new(640, 480), new(1920, 1080, new(30)),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveAsNew("Invalid", new(0, 480), new(1920, 1080, new(30)),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void CancellationPreservesAnExistingLibrary()
    {
        var store = new ViewOutputPresetStore(directory);
        var saved = store.SaveAsNew("Existing", new(640, 480), new(1920, 1080, new(30)), cancellationToken: TestContext.Current.CancellationToken);
        byte[] original = File.ReadAllBytes(Path.Combine(directory, saved.StableId + ".json"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.SaveAsNew(saved, "Cancelled", cancellation.Token));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, saved.StableId + ".json")));
        Assert.Single(Directory.GetFiles(directory, "*.json"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void LibraryPathOccupiedByAFileReportsDirectoryFailureAndIsNeverOverwritten()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "occupied");
        File.WriteAllText(path, "keep");
        var store = new ViewOutputPresetStore(path);
        Assert.Equal(ViewOutputPresetStatus.IoFailure, store.Find("custom-any", TestContext.Current.CancellationToken).Status);
        Assert.Equal(ViewOutputPresetStatus.IoFailure, Assert.Single(store.List(TestContext.Current.CancellationToken).List()).Status);
        Assert.Throws<IOException>(() => store.SaveAsNew("Invalid", new(640, 480), new(1920, 1080, new(30)),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("keep", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("D:\\outside")]
    [InlineData("with space")]
    public void FindNeverTreatsAnIdentifierAsAPath(string invalid)
    {
        var store = new ViewOutputPresetStore(directory);
        Assert.Throws<ArgumentException>(() => store.Find(invalid, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => store.List(TestContext.Current.CancellationToken).Find(invalid));
        Assert.False(Directory.Exists(directory));
    }

    private static byte[] Bytes(string id) => ViewOutputPresetJson.Serialize(
        new(id, "Example", new(640, 480), new(1920, 1080, new(30))));

    private void Write(string id, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, id + ".json"), bytes);
    }

    public void Dispose()
    {
        string cache = Path.GetFullPath(Environment.GetEnvironmentVariable("MMD_VIEW_OUTPUT_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "view-output-preset-tests"));
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Preset test cleanup must stay within its test cache.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
