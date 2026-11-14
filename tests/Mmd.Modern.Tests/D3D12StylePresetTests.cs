using System.Numerics;
using System.Text;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12StylePresetTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Environment.GetEnvironmentVariable("MMD_STYLE_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "style-preset-tests"), Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuiltInMmdRetainsParametersAndAllOrdinaryStylesStateTheirFiniteScope()
    {
        var catalog = D3D12StylePresetCatalog.BuiltIn;
        Assert.Equal(5, catalog.List().Count);
        var mmd = catalog.Find("mmd");
        Assert.True(mmd.IsAvailable);
        Assert.Equal(D3D12StylePresetStatus.Available, mmd.Status);
        Assert.Equal("mmd", mmd.Definition!.PipelineId);
        Assert.Null(mmd.Definition.CharacterOutlineStyle);
        Assert.Null(mmd.Definition.CharacterToonResponse);
        var genshin = catalog.Find("genshin");
        Assert.True(genshin.IsAvailable);
        Assert.Equal("genshin", genshin.Definition!.PipelineId);
        Assert.Contains("ordinary PMX basic body", genshin.Diagnostic);
        Assert.Contains("character-only", genshin.Diagnostic);
        Assert.Contains("Dedicated face/hair", genshin.Diagnostic);
        Assert.Contains("not implemented", genshin.Diagnostic);
        foreach (string id in new[] { "wuthering-waves", "endfield" })
        {
            var result = catalog.Find(id);
            Assert.Equal(D3D12StylePresetStatus.Available, result.Status);
            Assert.Equal(id, result.Definition!.PipelineId);
            Assert.True(result.IsAvailable);
            Assert.NotEmpty(result.Diagnostic);
        }
        Assert.Equal(D3D12StylePresetStatus.Missing, catalog.Find("custom-not-installed").Status);
        Assert.Equal(D3D12StylePresetStatus.Missing, catalog.Find("MMD").Status);
    }

    [Fact]
    public void SaveAsNewNeverMutatesBuiltInOrPreviouslySavedPresetAndSurvivesStoreReopen()
    {
        var store = new D3D12StylePresetStore(directory);
        var original = D3D12StylePresetCatalog.BuiltIn.Find("mmd").Definition!;
        var first = store.SaveAsNew(original, "我的描边");
        string firstPath = Path.Combine(directory, first.StableId + ".json");
        byte[] firstBytes = File.ReadAllBytes(firstPath);
        var second = store.SaveAsNew("我的描边", "mmd", new(.32f, new Vector4(.1f, .2f, .3f, .4f)), new(.6f, .12f));
        Assert.NotEqual(first.StableId, second.StableId);
        Assert.StartsWith("custom-", first.StableId);
        Assert.StartsWith("custom-", second.StableId);
        Assert.Equal("mmd", original.StableId);
        Assert.Null(original.CharacterOutlineStyle);
        Assert.Null(first.CharacterOutlineStyle);
        Assert.Null(original.CharacterToonResponse);
        Assert.Null(first.CharacterToonResponse);
        Assert.Equal(firstBytes, File.ReadAllBytes(firstPath));
        Assert.Equal(2, Directory.GetFiles(directory, "*.json").Length);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        var loaded = new D3D12StylePresetStore(directory).Load();
        Assert.Equal(7, loaded.List().Count);
        var restored = loaded.Find(second.StableId);
        Assert.True(restored.IsAvailable);
        Assert.Equal(second.Name, restored.Definition!.Name);
        Assert.Equal(second.CharacterOutlineStyle, restored.Definition.CharacterOutlineStyle);
        Assert.Equal(second.CharacterToonResponse, restored.Definition.CharacterToonResponse);
        Assert.Equal(D3D12StylePresetDefinition.CurrentSchemaVersion, restored.Definition.SchemaVersion);
        // An already loaded catalog owns detached values; a later on-disk edit does not mutate it.
        File.WriteAllText(Path.Combine(directory, second.StableId + ".json"), "{}");
        Assert.Equal(second.CharacterOutlineStyle, loaded.Find(second.StableId).Definition!.CharacterOutlineStyle);
        Assert.Equal(D3D12StylePresetStatus.InvalidData, store.Load().Find(second.StableId).Status);
    }

    [Fact]
    public void ExplicitNullParametersRoundTripOriginalMaterialBehavior()
    {
        var store = new D3D12StylePresetStore(directory);
        var saved = store.SaveAsNew("Original material parameters", "mmd", null, null);
        var restored = store.Load().Find(saved.StableId).Definition!;
        Assert.Null(restored.CharacterOutlineStyle);
        Assert.Null(restored.CharacterToonResponse);
    }

    [Fact]
    public void MissingFutureSchemaAndUnknownPipelineHaveDistinctOutcomes()
    {
        Write("custom-future", """{"format":"mmd-modern-d3d12-style-preset","schemaVersion":2,"stableId":"custom-future","futureField":{}}""");
        Write("custom-other", Json("custom-other", pipeline: "another-renderer"));
        Write("custom-other-two", Json("custom-other-two", pipeline: "future-surface"));
        var loaded = new D3D12StylePresetStore(directory).Load();
        Assert.Equal(D3D12StylePresetStatus.Missing, loaded.Find("custom-missing").Status);
        Assert.Equal(D3D12StylePresetStatus.IncompatibleSchema, loaded.Find("custom-future").Status);
        Assert.Null(loaded.Find("custom-future").Definition);
        Assert.Equal(D3D12StylePresetStatus.UnsupportedPipeline, loaded.Find("custom-other").Status);
        Assert.Equal("another-renderer", loaded.Find("custom-other").Definition!.PipelineId);
        Assert.Equal(D3D12StylePresetStatus.UnsupportedPipeline, loaded.Find("custom-other-two").Status);
        Assert.All(loaded.List().Where(p => p.StableId is "custom-future" or "custom-other" or "custom-other-two"),
            p => Assert.False(p.IsAvailable));
    }

    [Theory]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-nested")]
    [InlineData("unknown-field")]
    [InlineData("invalid-color")]
    [InlineData("invalid-width")]
    [InlineData("invalid-toon")]
    [InlineData("wrong-case")]
    [InlineData("comments")]
    [InlineData("trailing-comma")]
    [InlineData("null-name")]
    [InlineData("missing-parameters")]
    [InlineData("excess-depth")]
    public void CurrentSchemaRejectsAmbiguousOrInvalidData(string kind)
    {
        string json = Json("custom-bad");
        json = kind switch
        {
            "duplicate-root" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            "duplicate-nested" => json.Replace("\"width\":0.18", "\"width\":0.18,\"width\":0.3"),
            "unknown-field" => json.Replace("\"pipelineId\":\"mmd\"", "\"pipelineId\":\"mmd\",\"deviceDllPath\":\"outside.dll\""),
            "invalid-color" => json.Replace("\"color\":null", "\"color\":[0,0,2,1]"),
            "invalid-width" => json.Replace("\"width\":0.18", "\"width\":-1"),
            "invalid-toon" => json.Replace("\"transitionWidth\":0.1", "\"transitionWidth\":0"),
            "wrong-case" => json.Replace("\"name\"", "\"Name\""),
            "comments" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":/*ignored?*/1"),
            "trailing-comma" => json[..^1] + ",}",
            "null-name" => json.Replace("\"name\":\"Example\"", "\"name\":null"),
            "missing-parameters" => """{"format":"mmd-modern-d3d12-style-preset","schemaVersion":1,"stableId":"custom-bad","name":"Example","pipelineId":"mmd"}""",
            "excess-depth" => new string('[', 20) + "0" + new string(']', 20),
            _ => throw new InvalidOperationException(),
        };
        Write("custom-bad", json);
        var result = new D3D12StylePresetStore(directory).Load().Find("custom-bad");
        Assert.Equal(D3D12StylePresetStatus.InvalidData, result.Status);
        Assert.Null(result.Definition);
    }

    [Fact]
    public void ByteBudgetInvalidUtf8AndMismatchedIdentityCannotEnterCatalog()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "custom-large.json"), new byte[D3D12StylePresetStore.MaxPresetBytes + 1]);
        File.WriteAllBytes(Path.Combine(directory, "custom-utf8.json"), [0xff, 0xfe, 0x7b, 0x7d]);
        Write("custom-mismatch", Json("custom-different"));
        Write("mmd", Json("mmd", pipeline: "star-rail"));
        var catalog = new D3D12StylePresetStore(directory).Load();
        Assert.True(catalog.Find("mmd").IsAvailable);
        Assert.Equal("mmd", catalog.Find("mmd").Definition!.PipelineId);
        Assert.Equal(4, catalog.List().Count(p => p.Status == D3D12StylePresetStatus.InvalidData));
        Assert.Equal(D3D12StylePresetStatus.Missing, catalog.Find("custom-different").Status);
        foreach (string id in new[] { "custom-large", "custom-utf8", "custom-mismatch" })
            Assert.Equal(D3D12StylePresetStatus.InvalidData, catalog.Find(id).Status);
    }

    [Fact]
    public void MissingDirectoryIsReadOnlyAndDirectoryBudgetIsBounded()
    {
        var store = new D3D12StylePresetStore(directory);
        Assert.Equal(5, store.Load().List().Count);
        Assert.False(Directory.Exists(directory));
        Directory.CreateDirectory(directory);
        for (int i = 0; i <= D3D12StylePresetStore.MaxLocalPresets; i++) Write("custom-" + i, "{}");
        var loaded = store.Load();
        Assert.Equal(6, loaded.List().Count);
        Assert.Equal(D3D12StylePresetStatus.CapacityExceeded, loaded.List()[^1].Status);
        Assert.Equal(D3D12StylePresetStatus.CapacityExceeded, loaded.Find("custom-0").Status);
        Assert.Throws<InvalidOperationException>(() => store.SaveAsNew("Extra", "mmd", null, null));
        Assert.Equal(D3D12StylePresetStore.MaxLocalPresets + 1, Directory.GetFiles(directory, "*.json").Length);
    }

    [Fact]
    public void UnpublishedTemporaryFilesAndNestedDirectoriesAreNotCatalogEntries()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "partial.json.tmp"), "{");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllText(Path.Combine(directory, "nested", "custom-hidden.json"), Json("custom-hidden"));
        var loaded = new D3D12StylePresetStore(directory).Load();
        Assert.Equal(5, loaded.List().Count);
        Assert.Equal(D3D12StylePresetStatus.Missing, loaded.Find("custom-hidden").Status);
    }

    [Fact]
    public void LockedWriterFailsWithoutPublishingOrChangingExistingPreset()
    {
        var store = new D3D12StylePresetStore(directory);
        var saved = store.SaveAsNew("Saved", "mmd", new(), new());
        byte[] original = File.ReadAllBytes(Path.Combine(directory, saved.StableId + ".json"));
        using (var locked = new FileStream(Path.Combine(directory, ".write.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => new D3D12StylePresetStore(directory).SaveAsNew("Busy", "mmd", new(), new()));
        Assert.Single(Directory.GetFiles(directory, "*.json"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, saved.StableId + ".json")));
    }

    [Fact]
    public void AFileInPlaceOfTheLibraryDirectoryReportsIoFailureAndPreservesBuiltIns()
    {
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "not-a-directory"); File.WriteAllText(file, "do not overwrite");
        var loaded = new D3D12StylePresetStore(file).Load();
        Assert.True(loaded.Find("mmd").IsAvailable);
        Assert.Equal(D3D12StylePresetStatus.IoFailure, loaded.Find("custom-any").Status);
        Assert.Equal("do not overwrite", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("with space")]
    [InlineData("D:\\path")]
    [InlineData("风格")]
    public void StableIdentifiersDoNotAcceptPathsOrUnboundedNames(string invalid)
    {
        Assert.Throws<ArgumentException>(() => D3D12StylePresetCatalog.BuiltIn.Find(invalid));
        Assert.Throws<ArgumentException>(() => new D3D12StylePresetDefinition(invalid, "Example", "mmd", null, null));
        Assert.Throws<ArgumentException>(() => new D3D12StylePresetDefinition("example", "Example", invalid, null, null));
    }

    [Fact]
    public void InvalidSaveDoesNotCreateStorageAndDoesNotCopyUnsupportedSchema()
    {
        var store = new D3D12StylePresetStore(directory);
        Assert.Throws<ArgumentException>(() => store.SaveAsNew(" ", "mmd", null, null));
        Assert.Throws<ArgumentException>(() => store.SaveAsNew("Invalid", "mmd", new(float.NaN), null));
        Assert.Throws<ArgumentException>(() => store.SaveAsNew("Invalid", "mmd", null, new(.5f, 0)));
        var future = new D3D12StylePresetDefinition("future", "Future", "mmd", null, null, schemaVersion: 2);
        Assert.Throws<NotSupportedException>(() => store.SaveAsNew(future, "Copy future"));
        Assert.Throws<ArgumentException>(() => new D3D12StylePresetDefinition(new string('x', 129), "Example", "mmd", null, null));
        Assert.Throws<ArgumentException>(() => new D3D12StylePresetDefinition("example", "\ud800", "mmd", null, null));
        Assert.False(Directory.Exists(directory));
    }

    private void Write(string id, string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), json, new UTF8Encoding(false, true));
    }

    private static string Json(string id, string pipeline = "mmd") => JsonSerializer.Serialize(new
    {
        format = "mmd-modern-d3d12-style-preset", schemaVersion = 1, stableId = id, name = "Example", pipelineId = pipeline,
        characterOutlineStyle = new { width = .18f, color = (float[]?)null, enabled = true },
        characterToonResponse = new { threshold = .5f, transitionWidth = .1f },
    });

    public void Dispose()
    {
        string cache = Path.GetFullPath(Environment.GetEnvironmentVariable("MMD_STYLE_PRESET_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "style-preset-tests"));
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Preset test cleanup must stay within its test cache.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
