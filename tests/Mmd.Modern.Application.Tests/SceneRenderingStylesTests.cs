using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneRenderingStylesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "render-styles-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public SceneRenderingStylesTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void LegacyPmaWithoutExtensionUsesMmdAndNoOverrides()
    {
        using var project = Fixture(out _, out _, out _);
        string path = Save(project);
        Assert.False(Manifest(path)["optionalExtensions"]!.AsObject().ContainsKey(SceneRenderingStyles.ExtensionName));
        using var loaded = Load(path);
        Assert.True(loaded.Session.HasSupportedRenderingStyles);
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.Mmd, loaded.Session.RenderingStyles.DefaultStyle);
        Assert.Empty(loaded.Session.RenderingStyles.ModelOverrides);
        Assert.All(loaded.Session.Objects, o => Assert.Equal<RenderingPresetReference>(RenderingStyleId.Mmd, loaded.Session.GetRenderingStyle(o.Id)));
    }

    [Fact]
    public void SameSourceInstancesUseStableIdsAndInheritWithoutChangingModelRole()
    {
        using var project = Fixture(out var first, out var second, out _);
        Assert.Equal(project.Objects[0].SourcePath, project.Objects[1].SourcePath);
        var roles = project.Objects.Select(o => o.Role).ToArray();
        project.EditRenderingStyles(edit =>
        {
            edit.DefaultStyle = RenderingStyleId.Genshin;
            edit.ModelOverrides[first] = RenderingStyleId.StarRail;
            edit.ModelOverrides[second] = RenderingStyleId.WutheringWaves;
        }, Token);
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.StarRail, project.GetRenderingStyle(first));
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.WutheringWaves, project.GetRenderingStyle(second));
        project.SetModelRenderingStyle(first, null, Token);
        project.SetDefaultRenderingStyle(RenderingStyleId.Endfield, Token);
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.Endfield, project.GetRenderingStyle(first));
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.WutheringWaves, project.GetRenderingStyle(second));
        Assert.Equal(roles, project.Objects.Select(o => o.Role));
        Assert.Throws<ArgumentException>(() => project.GetRenderingStyle(Guid.NewGuid()));
    }

    [Fact]
    public void StyleEditsUndoRedoRetainExactFrameClockAndPhysicsOwner()
    {
        using var project = Fixture(out var first, out _, out var physics, sharedPhysics: true);
        project.Seek(12, Token); project.Advance(1d / 60, Token); var frame = project.Frame; int creates = physics.Creates;
        project.SetModelRenderingStyle(first, RenderingStyleId.Genshin, Token);
        Assert.True(project.Undo(Token));
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.Mmd, project.GetRenderingStyle(first));
        Assert.True(project.Redo(Token));
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.Genshin, project.GetRenderingStyle(first));
        Assert.Same(frame, project.Frame); Assert.Equal(12.5, project.CurrentFrame);
        Assert.Equal(creates, physics.Creates);
        Assert.Equal(0, physics.Disposals);
    }

    [Fact]
    public void MixedOrdinaryAndStyleHistoryRecreatesOnlyOrdinaryCandidates()
    {
        using var project = Fixture(out var first, out _, out var physics, sharedPhysics: true);
        project.SetDefaultRenderingStyle(RenderingStyleId.Genshin, Token);
        int initial = physics.Creates;
        project.Edit(edit => edit.Composition.SetTransform(first, Vector3.UnitX, Quaternion.Identity, 1), Token);
        Assert.Equal(initial + 1, physics.Creates);
        project.SetModelRenderingStyle(first, RenderingStyleId.StarRail, Token);
        project.Seek(20, Token); project.Advance(1d / 60, Token);
        double currentFrame = project.CurrentFrame;
        var moved = project.Frame;
        Assert.True(project.Undo(Token)); Assert.Same(moved, project.Frame);
        Assert.True(project.Undo(Token)); Assert.Equal(initial + 2, physics.Creates);
        var original = project.Frame;
        Assert.Equal(Vector3.Zero, project.Objects.Single(o => o.Id == first).Position);
        Assert.Equal(currentFrame, project.CurrentFrame);
        Assert.True(project.Undo(Token)); Assert.Same(original, project.Frame);
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.Mmd, project.GetRenderingStyle(first));
        Assert.True(project.Redo(Token)); Assert.Same(original, project.Frame);
        Assert.True(project.Redo(Token)); Assert.Equal(initial + 3, physics.Creates);
        Assert.Equal(Vector3.UnitX, project.Objects.Single(o => o.Id == first).Position);
        Assert.Equal(currentFrame, project.CurrentFrame);
        var movedAgain = project.Frame;
        Assert.True(project.Redo(Token)); Assert.Same(movedAgain, project.Frame);
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.StarRail, project.GetRenderingStyle(first));
    }

    [Fact]
    public void InvalidCancelledAndEscapedStyleCandidatesCannotPublishOrClearRedo()
    {
        using var project = Fixture(out var first, out _, out var physics, sharedPhysics: true);
        project.SetDefaultRenderingStyle(RenderingStyleId.Genshin, Token); project.Undo(Token);
        var before = project.Frame; int creates = physics.Creates;
        Assert.Throws<ArgumentException>(() => project.SetModelRenderingStyle(Guid.NewGuid(), RenderingStyleId.Endfield, Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => project.SetDefaultRenderingStyle((RenderingStyleId)900, Token));
        Assert.Throws<InvalidOperationException>(() => project.EditRenderingStyles(_ => throw new InvalidOperationException("candidate failed"), Token));
        using var cancelled = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => project.EditRenderingStyles(edit =>
        { edit.DefaultStyle = RenderingStyleId.Endfield; cancelled.Cancel(); }, cancelled.Token));
        Assert.Same(before, project.Frame); Assert.True(project.CanRedo);
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.Mmd, project.GetRenderingStyle(first)); Assert.Equal(creates, physics.Creates);
        SceneRenderingStylesEdit? escaped = null;
        project.EditRenderingStyles(edit => { escaped = edit; edit.ModelOverrides[first] = RenderingStyleId.StarRail; }, Token);
        escaped!.ModelOverrides[first] = RenderingStyleId.Endfield;
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.StarRail, project.GetRenderingStyle(first));
    }

    [Fact]
    public void NoOpStyleChangeKeepsRedoAndRemovingObjectRemovesItsOverride()
    {
        using var project = Fixture(out var first, out var second, out _);
        project.SetModelRenderingStyle(first, RenderingStyleId.Endfield, Token);
        project.Undo(Token); project.SetDefaultRenderingStyle(RenderingStyleId.Mmd, Token);
        Assert.True(project.CanRedo); project.Redo(Token);
        project.Edit(edit => edit.RemoveObject(first), Token);
        Assert.False(project.RenderingStyles.ModelOverrides.ContainsKey(first));
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.Mmd, project.GetRenderingStyle(second));
        project.Undo(Token); Assert.Equal<RenderingPresetReference>(RenderingStyleId.Endfield, project.GetRenderingStyle(first));
    }

    [Fact]
    public void PortablePmaMoveReopenResavePreservesTypedChoicesAndCanonicalIds()
    {
        using var project = Fixture(out var first, out var second, out _);
        project.EditRenderingStyles(edit => { edit.DefaultStyle = RenderingStyleId.Endfield;
            edit.ModelOverrides[first] = RenderingStyleId.Genshin; edit.ModelOverrides[second] = RenderingStyleId.StarRail; }, Token);
        string path = Save(project), moved = Path.Combine(directory, "moved.pma"); File.Move(path, moved);
        File.Delete(project.Objects[0].SourcePath);
        using (var loaded = Load(moved))
        {
            Assert.Equal<RenderingPresetReference>(RenderingStyleId.Genshin, loaded.Session.GetRenderingStyle(first));
            Assert.Equal<RenderingPresetReference>(RenderingStyleId.StarRail, loaded.Session.GetRenderingStyle(second));
            PmaProjectStore.Save(moved, loaded.Session, cancellationToken: Token);
        }
        var extension = Manifest(moved)["optionalExtensions"]![SceneRenderingStyles.ExtensionName]!;
        Assert.Equal(1, extension["version"]!.GetValue<int>());
        Assert.Equal("endfield", extension["defaultStyle"]!.GetValue<string>());
        Assert.Equal("genshin", extension["modelOverrides"]![first.ToString("D")]!.GetValue<string>());
        Assert.Equal("star-rail", extension["modelOverrides"]![second.ToString("D")]!.GetValue<string>());
        using var reopened = Load(moved);
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.Endfield, reopened.Session.RenderingStyles.DefaultStyle);
        Assert.Equal(project.Objects.Select(o => o.Id), reopened.Session.Objects.Select(o => o.Id));
    }

    [Theory]
    [InlineData("future-version")]
    [InlineData("future-field")]
    public void FutureSelectionSchemaIsPreservedWithDiagnosticsAndCannotMasqueradeAsMmd(string variant)
    {
        using var project = Fixture(out var first, out _, out _);
        string path = Save(project);
        var payload = new JsonObject { ["version"] = 1, ["defaultStyle"] = "mmd", ["modelOverrides"] = new JsonObject() };
        if (variant == "future-version") payload["version"] = 2;
        payload["modelOverrides"]![first.ToString("D")] = "org.example.custom-preset";
        if (variant == "future-field") payload["parameters"] = new JsonObject { ["future"] = new JsonArray(1, 2, 3) };
        RewriteExtension(path, payload);
        using (var loaded = Load(path))
        {
            Assert.False(loaded.Session.HasSupportedRenderingStyles);
            Assert.NotEmpty(loaded.Session.RenderingStyleDiagnostics);
            Assert.Contains(loaded.Diagnostics, d => d.Contains(SceneRenderingStyles.ExtensionName, StringComparison.Ordinal));
            Assert.Throws<NotSupportedException>(() => loaded.Session.GetRenderingStyle(first));
            Assert.Throws<NotSupportedException>(() => loaded.Session.SetDefaultRenderingStyle(RenderingStyleId.Mmd, Token));
            Assert.False(loaded.Session.CanUndo);
            var frame = loaded.Session.Frame;
            Assert.Throws<InvalidDataException>(() => loaded.Session.Edit(edit => edit.RemoveObject(first), Token));
            Assert.Same(frame, loaded.Session.Frame); Assert.False(loaded.Session.CanUndo);
            PmaProjectStore.Save(path, loaded.Session, cancellationToken: Token);
        }
        Assert.True(JsonNode.DeepEquals(payload, Manifest(path)["optionalExtensions"]![SceneRenderingStyles.ExtensionName]));
    }

    [Fact]
    public void FutureSchemaWithDifferentFieldsRemainsOpaqueAndResavable()
    {
        using var project = Fixture(out _, out _, out _);
        string path = Save(project);
        var payload = new JsonObject { ["version"] = 2, ["selection"] = new JsonObject { ["futureReference"] = "studio.Future" } };
        RewriteExtension(path, payload);
        using (var loaded = Load(path))
        {
            Assert.False(loaded.Session.HasSupportedRenderingStyles);
            Assert.Contains(loaded.Diagnostics, d => d.Contains("unsupported version 2", StringComparison.Ordinal));
            Assert.Throws<NotSupportedException>(() => _ = loaded.Session.RenderingStyles);
            PmaProjectStore.Save(path, loaded.Session, cancellationToken: Token);
        }
        Assert.True(JsonNode.DeepEquals(payload, Manifest(path)["optionalExtensions"]![SceneRenderingStyles.ExtensionName]));
    }

    [Fact]
    public void CustomPresetReferencesLoadWithoutRendererAndRoundTripExactlyWithoutDefinitions()
    {
        using var project = Fixture(out var first, out var second, out _);
        string path = Save(project);
        var payload = new JsonObject { ["version"] = 1, ["defaultStyle"] = "org.example.Studio_Look-2",
            ["modelOverrides"] = new JsonObject { [first.ToString("D")] = "org.example.MyPreset-7", [second.ToString("D")] = "mmd" } };
        RewriteExtension(path, payload);
        using (var loaded = Load(path))
        {
            Assert.True(loaded.Session.HasSupportedRenderingStyles);
            Assert.Empty(loaded.Session.RenderingStyleDiagnostics); Assert.Empty(loaded.Diagnostics);
            Assert.Equal("org.example.Studio_Look-2", loaded.Session.RenderingStyles.DefaultStyle.StableId);
            Assert.Equal("org.example.MyPreset-7", loaded.Session.GetRenderingStyle(first).StableId);
            Assert.Equal("mmd", loaded.Session.GetRenderingStyle(second).StableId);
            loaded.Session.SetModelRenderingStyle(second, null, Token);
            Assert.Equal("org.example.Studio_Look-2", loaded.Session.GetRenderingStyle(second).StableId);
            loaded.Session.Undo(Token);
            PmaProjectStore.Save(path, loaded.Session, cancellationToken: Token);
        }
        // The application assembly has no renderer dependency: selections remain metadata on machines without that preset.
        var actual = Manifest(path)["optionalExtensions"]![SceneRenderingStyles.ExtensionName]!;
        Assert.True(JsonNode.DeepEquals(payload, actual));
        Assert.Equal(new[] { "defaultStyle", "modelOverrides", "version" }, actual.AsObject().Select(p => p.Key).Order());
    }

    [Fact]
    public void LegacyPackageCanAdoptCustomReferencesAndResetWithoutRetainingAnExtension()
    {
        using var project = Fixture(out var first, out var second, out _);
        string path = Save(project);
        using (var loaded = Load(path))
        {
            loaded.Session.SetDefaultRenderingStyle(new RenderingPresetReference("studio.Default_v3"), Token);
            loaded.Session.SetModelRenderingStyle(first, new RenderingPresetReference("studio.Personal_v1"), Token);
            PmaProjectStore.Save(path, loaded.Session, cancellationToken: Token);
        }
        using (var reopened = Load(path))
        {
            Assert.Equal("studio.Personal_v1", reopened.Session.GetRenderingStyle(first).StableId);
            Assert.Equal("studio.Default_v3", reopened.Session.GetRenderingStyle(second).StableId);
            reopened.Session.SetDefaultRenderingStyle(RenderingStyleId.Mmd, Token);
            reopened.Session.SetModelRenderingStyle(first, null, Token);
            PmaProjectStore.Save(path, reopened.Session, cancellationToken: Token);
        }
        Assert.False(Manifest(path)["optionalExtensions"]!.AsObject().ContainsKey(SceneRenderingStyles.ExtensionName));
    }

    [Fact]
    public void CustomPresetTransactionsAndMixedHistoryKeepReferencesAndLivePhysics()
    {
        using var project = Fixture(out var first, out _, out var physics, sharedPhysics: true);
        var global = new RenderingPresetReference("org.example.Custom_Default");
        var local = new RenderingPresetReference("org.example.Custom_Object");
        project.SetDefaultRenderingStyle(global, Token);
        var frame = project.Frame; int creates = physics.Creates;
        project.SetModelRenderingStyle(first, local, Token);
        Assert.Same(frame, project.Frame); Assert.Equal(creates, physics.Creates);
        project.Edit(edit => edit.Composition.SetTransform(first, Vector3.UnitY, Quaternion.Identity, 1), Token);
        project.SetModelRenderingStyle(first, null, Token);
        Assert.Equal(global, project.GetRenderingStyle(first));
        var moved = project.Frame;
        project.Undo(Token); Assert.Equal(local, project.GetRenderingStyle(first)); Assert.Same(moved, project.Frame);
        project.Undo(Token); Assert.Equal(local, project.GetRenderingStyle(first)); Assert.Equal(creates + 2, physics.Creates);
        var restored = project.Frame;
        project.Undo(Token); Assert.Equal(global, project.GetRenderingStyle(first)); Assert.Same(restored, project.Frame);
        project.Redo(Token); Assert.Equal(local, project.GetRenderingStyle(first)); Assert.Same(restored, project.Frame);
    }

    [Fact]
    public void DefaultReferenceCannotCommitOrClearRedo()
    {
        using var project = Fixture(out var first, out _, out _);
        project.SetDefaultRenderingStyle(new RenderingPresetReference("org.example.custom"), Token); project.Undo(Token);
        var before = project.Frame;
        Assert.Throws<ArgumentException>(() => project.SetDefaultRenderingStyle(default(RenderingPresetReference), Token));
        Assert.Throws<ArgumentException>(() => project.SetModelRenderingStyle(first, default(RenderingPresetReference), Token));
        Assert.Throws<ArgumentException>(() => project.EditRenderingStyles(edit => edit.ModelOverrides[Guid.Empty] = RenderingStyleId.Mmd, Token));
        Assert.Same(before, project.Frame); Assert.True(project.CanRedo);
        Assert.Equal("mmd", project.GetRenderingStyle(first).StableId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("../preset")]
    [InlineData("preset:one")]
    [InlineData("自定义")]
    public void InvalidPresetReferenceIsRejectedAtConstructionAndBeforeHostLoad(string value)
    {
        Assert.Throws<ArgumentException>(() => new RenderingPresetReference(value));
        using var project = Fixture(out _, out _, out _);
        string path = Save(project);
        RewriteExtension(path, new JsonObject { ["version"] = 1, ["defaultStyle"] = value, ["modelOverrides"] = new JsonObject() });
        int calls = 0;
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Load(path, Path.Combine(directory, "cache"),
            (p, t) => { calls++; return LoadModel(p, t); }, new CountingPhysics(), cancellationToken: Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PresetReferenceIsBoundedAndOrdinalWithoutNormalization()
    {
        var upper = new RenderingPresetReference("org.example.Custom");
        var lower = new RenderingPresetReference("org.example.custom");
        Assert.NotEqual(upper, lower); Assert.Equal("org.example.Custom", upper.StableId);
        Assert.Equal(128, new RenderingPresetReference(new string('a', 128)).StableId.Length);
        Assert.Throws<ArgumentException>(() => new RenderingPresetReference(new string('a', 129)));
        Assert.Throws<ArgumentNullException>(() => new RenderingPresetReference(null!));
    }

    [Theory]
    [InlineData("malformed-ref")]
    [InlineData("unknown-object")]
    [InlineData("empty-object")]
    [InlineData("invalid-object")]
    public void FutureSchemaStillRejectsMalformedUnderstoodReferencesBeforeHost(string variant)
    {
        using var project = Fixture(out _, out _, out _);
        string path = Save(project);
        var payload = new JsonObject { ["version"] = 2, ["defaultStyle"] = "org.example.custom", ["modelOverrides"] = new JsonObject() };
        if (variant == "malformed-ref") payload["defaultStyle"] = "has space";
        if (variant == "unknown-object") payload["modelOverrides"]![Guid.NewGuid().ToString("D")] = "mmd";
        if (variant == "empty-object") payload["modelOverrides"]![Guid.Empty.ToString("D")] = "mmd";
        if (variant == "invalid-object") payload["modelOverrides"]!["not-a-guid"] = "mmd";
        RewriteExtension(path, payload); int calls = 0;
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Load(path, Path.Combine(directory, "cache"),
            (p, t) => { calls++; return LoadModel(p, t); }, new CountingPhysics(), cancellationToken: Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("unknown-object")]
    [InlineData("empty-object")]
    [InlineData("invalid-object")]
    [InlineData("invalid-version")]
    [InlineData("string-version")]
    [InlineData("invalid-style")]
    [InlineData("missing-default")]
    [InlineData("null-overrides")]
    public void MalformedKnownStyleExtensionRejectsBeforeHostAndPreservesOpenSession(string variant)
    {
        using var project = Fixture(out _, out _, out _); var frame = project.Frame;
        string path = Save(project);
        var payload = new JsonObject { ["version"] = 1, ["defaultStyle"] = "mmd", ["modelOverrides"] = new JsonObject() };
        if (variant == "unknown-object") payload["modelOverrides"]![Guid.NewGuid().ToString("D")] = "genshin";
        if (variant == "empty-object") payload["modelOverrides"]![Guid.Empty.ToString("D")] = "mmd";
        if (variant == "invalid-object") payload["modelOverrides"]!["not-a-guid"] = "mmd";
        if (variant == "invalid-version") payload["version"] = 0;
        if (variant == "string-version") payload["version"] = "one";
        if (variant == "invalid-style") payload["defaultStyle"] = 42;
        if (variant == "missing-default") payload.Remove("defaultStyle");
        if (variant == "null-overrides") payload["modelOverrides"] = null;
        RewriteExtension(path, payload); int calls = 0;
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Load(path, Path.Combine(directory, "cache"),
            (p, t) => { calls++; return LoadModel(p, t); }, new CountingPhysics(), cancellationToken: Token));
        Assert.Equal(0, calls); Assert.Same(frame, project.Frame); Assert.False(project.CanUndo);
    }

    [Fact]
    public void InvalidSaveOnlyExtensionPreservesPreviousPackageAndLiveStyleState()
    {
        using var project = Fixture(out var first, out _, out _);
        project.SetDefaultRenderingStyle(RenderingStyleId.Genshin, Token);
        string path = Save(project); byte[] before = File.ReadAllBytes(path);
        using var malformed = JsonDocument.Parse("{\"version\":1,\"defaultStyle\":\"mmd\",\"modelOverrides\":null}");
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Save(path, project, new()
        { OptionalExtensions = new Dictionary<string, JsonElement> { [SceneRenderingStyles.ExtensionName] = malformed.RootElement } }, Token));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal<RenderingPresetReference>(RenderingStyleId.Genshin, project.GetRenderingStyle(first));
    }

    [Fact]
    public void PmmPreflightNamesRenderingStyleLossBeforeWriting()
    {
        using var project = Fixture(out var first, out _, out _);
        project.SetModelRenderingStyle(first, RenderingStyleId.WutheringWaves, Token);
        var report = PmaPmmCompatibility.Inspect(project);
        Assert.Contains(report.Issues, i => i.Code == "rendering-styles");
        Assert.True(report.RecommendPma); Assert.Throws<PmaPmmExportException>(() => project.ExportPmm());
    }

    [Fact]
    public void LegacySceneSaveRejectsStyleLossWhilePmaSaveAndResetRemainAvailable()
    {
        using var project = Fixture(out var first, out _, out _);
        string scenePath = Path.Combine(directory, "legacy.mmdscene"); project.Save(scenePath, cancellationToken: Token);
        byte[] original = File.ReadAllBytes(scenePath);
        project.SetModelRenderingStyle(first, RenderingStyleId.Genshin, Token);
        Assert.Throws<NotSupportedException>(() => project.Save(scenePath, cancellationToken: Token));
        Assert.Equal(original, File.ReadAllBytes(scenePath));
        string package = Save(project);
        using var loaded = Load(package);
        loaded.Session.SetModelRenderingStyle(first, null, Token);
        var report = PmaPmmCompatibility.Inspect(loaded.Session);
        Assert.DoesNotContain(report.Issues, i => i.Code is "rendering-styles" or "optional-extensions");
        PmaProjectStore.Save(package, loaded.Session, cancellationToken: Token);
        Assert.False(Manifest(package)["optionalExtensions"]!.AsObject().ContainsKey(SceneRenderingStyles.ExtensionName));
    }

    [Fact]
    public void SaveOnlyStyleOverrideDoesNotChangeLiveOrFollowingDefaultSave()
    {
        using var project = Fixture(out var first, out _, out _);
        project.SetDefaultRenderingStyle(RenderingStyleId.Genshin, Token);
        string path = Path.Combine(directory, "save-only.pma");
        using var payload = JsonDocument.Parse("{\"version\":1,\"defaultStyle\":\"wuthering-waves\",\"modelOverrides\":{}}");
        PmaProjectStore.Save(path, project, new() { OptionalExtensions = new Dictionary<string, JsonElement>
        { [SceneRenderingStyles.ExtensionName] = payload.RootElement } }, Token);
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.Genshin, project.GetRenderingStyle(first));
        using var loaded = Load(path); Assert.Equal<RenderingPresetReference>(RenderingStyleId.WutheringWaves, loaded.Session.GetRenderingStyle(first));
        PmaProjectStore.Save(path, project, cancellationToken: Token);
        Assert.Equal("genshin", Manifest(path)["optionalExtensions"]![SceneRenderingStyles.ExtensionName]!["defaultStyle"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(RenderingStyleId.Mmd, "mmd")]
    [InlineData(RenderingStyleId.Genshin, "genshin")]
    [InlineData(RenderingStyleId.StarRail, "star-rail")]
    [InlineData(RenderingStyleId.WutheringWaves, "wuthering-waves")]
    [InlineData(RenderingStyleId.Endfield, "endfield")]
    public void EveryKnownStyleUsesItsStableNonlocalizedId(RenderingStyleId style, string stableId)
    {
        using var project = Fixture(out var first, out _, out _);
        Assert.Equal(stableId, SceneRenderingStyles.StableId(style));
        // Explicit MMD still overrides a non-MMD global default.
        project.EditRenderingStyles(edit => { edit.DefaultStyle = RenderingStyleId.Endfield; edit.ModelOverrides[first] = style; }, Token);
        string path = Save(project);
        Assert.Equal(stableId, Manifest(path)["optionalExtensions"]![SceneRenderingStyles.ExtensionName]!["modelOverrides"]![first.ToString("D")]!.GetValue<string>());
        using var loaded = Load(path); Assert.Equal<RenderingPresetReference>(style, loaded.Session.GetRenderingStyle(first));
    }

    private string Save(SceneProjectSession project)
    { string path = Path.Combine(directory, "project.pma"); PmaProjectStore.Save(path, project, cancellationToken: Token); return path; }
    private PmaLoadedProject Load(string path) => PmaProjectStore.Load(path, Path.Combine(directory, "cache"), LoadModel, new CountingPhysics(), cancellationToken: Token);
    private static JsonNode Manifest(string path)
    { using var zip = ZipFile.OpenRead(path); using var input = zip.GetEntry("manifest.json")!.Open(); return JsonNode.Parse(input)!; }
    private static void RewriteExtension(string path, JsonNode payload)
    {
        var manifest = Manifest(path); manifest["optionalExtensions"]![SceneRenderingStyles.ExtensionName] = payload.DeepClone();
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update); zip.GetEntry("manifest.json")!.Delete();
        using var output = new StreamWriter(zip.CreateEntry("manifest.json").Open()); output.Write(manifest.ToJsonString());
    }

    private SceneProjectSession Fixture(out Guid first, out Guid second, out CountingPhysics physics, bool sharedPhysics = false)
    {
        var model = PmmMotionAdapterTests.Pmx(["root"]);
        // The existing minimal format fixture has no geometry; this test still needs a valid native PMA actor.
        model = new PmxModel { Header = model.Header, Name = "actor", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(), Faces = [new(0, 1, 2)], Textures = [],
            Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 1, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = model.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [] };
        string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var layout = new SceneCompositionEditor(); first = layout.Add("one", source, ModelRole.Character, Rest(model)).Id;
        second = layout.Add("two", source, ModelRole.Environment, Rest(model)).Id;
        var asset = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = Vector3.UnitX }] });
        physics = new();
        return new(layout, new(), [new(first, model, asset.Motion, PhysicsEnabled: false, Asset: asset), new(second, model, asset.Motion, PhysicsEnabled: false, Asset: asset)], physics,
            new() { Physics = new() { SharedWorld = sharedPhysics } }, initialFrame: 10, cancellationToken: Token);
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
        new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(path); var model = PmxReader.Read(input); return new(model, Rest(model)); }
    private sealed class CountingPhysics : IProjectPhysicsBackendProvider
    {
        public int Creates, Disposals;
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) { Creates++; return new EmptyBackend(() => Disposals++); }
    }
    private sealed class EmptyBackend(Action dispose) : ICharacterPhysicsMutableBackend
    {
        public string Name => "style test"; public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> input) { }
        public void SetKinematicTarget(int index, Matrix4x4 world, float seconds) => throw new NotSupportedException();
        public Matrix4x4 GetBodyWorldTransform(int index) => throw new NotSupportedException(); public void Step(float seconds) { } public void Dispose() => dispose();
        public void SetGravity(Vector3 gravity) { } public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) => throw new NotSupportedException();
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 world) => throw new NotSupportedException();
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => throw new NotSupportedException();
        public void RemoveJoint(int index) => throw new NotSupportedException(); public void RemoveBody(int index) => throw new NotSupportedException();
        public void ResetBody(int index, Matrix4x4 world) => throw new NotSupportedException(); public void SetBodyKinematic(int index, bool state) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
