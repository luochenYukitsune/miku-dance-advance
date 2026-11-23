using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmcProjectStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pmc-store-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PmcProjectStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void PublicClassicIdentityIsDistinctAndModernIdentityRemainsUnchanged()
    {
        var type = PmcType();
        Assert.Equal("mmd-classic-pmc", type.GetField("Format")?.GetRawConstantValue());
        Assert.Equal(1, type.GetField("Version")?.GetRawConstantValue());
        Assert.Equal("mmd-modern-pma", PmaProjectStore.Format);
        Assert.Equal(1, PmaProjectStore.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FacadesWriteTheirOwnMagicAndRoundtripAuthoredState(bool classic)
    {
        using var project = Fixture();
        string path = Package("roundtrip", classic);
        Save(classic, path, project, Options(), Token);
        using (var archive = ZipFile.OpenRead(path))
        using (var input = archive.GetEntry("manifest.json")!.Open())
        using (var manifest = JsonDocument.Parse(input))
        {
            Assert.Equal(Format(classic), manifest.RootElement.GetProperty("format").GetString());
            Assert.Equal(1, manifest.RootElement.GetProperty("version").GetInt32());
            Assert.All(archive.Entries, entry => Assert.True(entry.FullName == "manifest.json" || entry.FullName.StartsWith("blobs/", StringComparison.Ordinal)));
        }
        using var loaded = Load(classic, path);
        Assert.Equal(Format(classic), LoadedFormat(loaded));
        Assert.Equal(project.Objects.Select(o => o.Id), loaded.Session.Objects.Select(o => o.Id));
        Assert.Equal(1280, loaded.Session.Settings.Width);
        Assert.Equal(project.Frame.Surface.Snapshot.Vertices, loaded.Session.Frame.Surface.Snapshot.Vertices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongExtensionRejectsSaveAndLoadBeforeHostOrPrivateStage(bool classic)
    {
        using var project = Fixture();
        string path = Package("wrong-extension", !classic);
        byte[] previous = [4, 8, 15, 16, 23, 42]; File.WriteAllBytes(path, previous);
        Assert.Throws<ArgumentException>(() => Save(classic, path, project, Options(), Token));
        var physics = new TrackingPhysics(); int modelCalls = 0, decorationCalls = 0;
        Assert.Throws<ArgumentException>(() => Load(classic, path, (p, t) => { modelCalls++; return LoadAsset(p, t); }, physics,
            new() { ModelAssetTransform = (_, asset) => { decorationCalls++; return asset; } }));
        Assert.Equal(0, modelCalls); Assert.Equal(0, decorationCalls); Assert.Equal(0, physics.Calls);
        Assert.Equal(previous, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(directory, "save-cache")));
        Assert.False(Directory.Exists(Path.Combine(directory, "load-cache")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamedOtherFormatRejectsBeforeModelDecorationPhysicsOrExtraction(bool classic)
    {
        using var project = Fixture();
        string original = Package("source", !classic), renamed = Package("renamed", classic);
        Save(!classic, original, project, Options(), Token); File.Copy(original, renamed);
        var physics = new TrackingPhysics(); int modelCalls = 0, decorationCalls = 0;
        Assert.Throws<NotSupportedException>(() => Load(classic, renamed, (p, t) => { modelCalls++; return LoadAsset(p, t); }, physics,
            new() { ModelAssetTransform = (_, asset) => { decorationCalls++; return asset; } }));
        Assert.Equal(0, modelCalls); Assert.Equal(0, decorationCalls); Assert.Equal(0, physics.Calls);
        Assert.False(Directory.Exists(Path.Combine(directory, "load-cache")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachedSnapshotKeepsItsOriginalFormatAndRejectsOtherExtensionAtomically(bool classic)
    {
        var project = Fixture();
        using var snapshot = Capture(classic, project, Options(), Token);
        project.Dispose();
        string wrong = Package("snapshot-wrong", !classic), correct = Package("snapshot", classic);
        byte[] previous = [8, 7, 6]; File.WriteAllBytes(wrong, previous);
        Assert.Throws<ArgumentException>(() => snapshot.Save(wrong, Token));
        Assert.Equal(previous, File.ReadAllBytes(wrong));
        Assert.False(Directory.Exists(Path.Combine(directory, "save-cache")));
        snapshot.Save(correct, Token);
        using var loaded = Load(classic, correct);
        Assert.Equal(Format(classic), LoadedFormat(loaded));
        Assert.Equal(snapshot.ProjectId, loaded.ProjectId);
        snapshot.Dispose();
        Assert.Throws<ObjectDisposedException>(() => snapshot.Save(correct, Token));
    }

    [Fact]
    public void ClassicNeutralOpaqueExtensionsAndExactResourceBytesSurviveMoveAndRetainedResave()
    {
        using var project = Fixture();
        using var extension = JsonDocument.Parse("{\"version\":27,\"unknown\":{\"text\":\"未来\",\"values\":[null,true,-3.125]},\"opaque\":\"not interpreted\"}");
        byte[] bytes = [0, 255, 1, 128, 13, 10, 0];
        string initial = Package("opaque", true), moved = Package("moved", true), second = Package("resaved", true);
        Save(true, initial, project, Options() with
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { ["org.example.future"] = extension.RootElement },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>>
            { ["render-resources/future/data.bin"] = bytes, ["render-resources/future/copy.bin"] = bytes },
        }, Token);
        File.Move(initial, moved); File.Delete(project.Objects[0].SourcePath);
        Guid projectId;
        using (var loaded = Load(true, moved))
        {
            projectId = loaded.ProjectId;
            Assert.Equal("mmd-classic-pmc", LoadedFormat(loaded));
            Assert.True(JsonElement.DeepEquals(extension.RootElement, loaded.OptionalExtensions["org.example.future"]));
            Assert.Equal(bytes, loaded.ReadResource("render-resources/future/data.bin"));
            var detached = loaded.ReadResource("render-resources/future/data.bin"); detached[0] = 99;
            Save(true, second, loaded.Session, Options(), Token);
        }
        using var reopened = Load(true, second);
        Assert.Equal(projectId, reopened.ProjectId);
        Assert.Equal("mmd-classic-pmc", LoadedFormat(reopened));
        Assert.True(JsonElement.DeepEquals(extension.RootElement, reopened.OptionalExtensions["org.example.future"]));
        Assert.Equal(new[] { "render-resources/future/copy.bin", "render-resources/future/data.bin" }, reopened.ResourcePaths);
        Assert.Equal(bytes, reopened.ReadResource("render-resources/future/data.bin"));
        Assert.Equal(bytes, reopened.ReadResource("render-resources/future/copy.bin"));
        using var archive = ZipFile.OpenRead(second);
        using var manifestInput = archive.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestInput);
        var resources = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Where(file => file.GetProperty("path").GetString()!.StartsWith("render-resources/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, resources.Length);
        Assert.Equal(resources[0].GetProperty("sha256").GetString(), resources[1].GetProperty("sha256").GetString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void EngineConversionPreservesSharedEditsAndRestoresArchivedDataAfterOrdinarySave(bool fromModern, bool oldRelativeLayout)
    {
        using var project = Fixture();
        var light = new NativeSceneLight(Guid.NewGuid(), "fill", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One);
        if (fromModern) project.Edit(edit => edit.Lighting.Add(light), Token);
        string feature = fromModern ? "org.mda.model-material-bindings" : "org.mda.traditional-emm";
        string featurePath = fromModern ? "render-resources/material-bindings/map.png" : "render-resources/traditional/emm/raw.emm";
        using var metadata = JsonDocument.Parse("{\"version\":1,\"custom\":[1,2]}");
        byte[] resource = [3, 1, 4, 1, 5];
        string source = Package("original", !fromModern), target = Package("converted", fromModern);
        Save(!fromModern, source, project, Options() with
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { [feature] = metadata.RootElement, ["org.example.common"] = metadata.RootElement },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { [featurePath] = resource },
        }, Token);
        if (oldRelativeLayout)
            PmaProjectStoreTests.RewritePackagedFile(source, "project.mmdscene", bytes =>
            {
                var document = JsonNode.Parse(bytes)!;
                var layout = document["layout"]!.AsObject();
                Assert.True(layout.Remove("portableModels"));
                layout["version"] = layout["version"]!.GetValue<int>() - 1;
                return [0xef, 0xbb, 0xbf, .. JsonSerializer.SerializeToUtf8Bytes(document)];
            });
        var before = File.ReadAllBytes(source);
        var report = PmaProjectStore.ConvertPackage(source, target, directory, Token,
            prepareMissingClassicToons: _ => DefaultToon(project.Objects[0].Id));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Contains(feature, report.OmittedExtensions);
        Assert.Contains(featurePath, report.OmittedResources);
        string resaved = Package("resaved", fromModern);
        using (var converted = Load(fromModern, target))
        {
            Assert.Empty(converted.Session.Lighting.Lights);
            Assert.DoesNotContain(feature, converted.OptionalExtensions.Keys);
            Assert.DoesNotContain(featurePath, converted.ResourcePaths);
            Assert.Contains("org.example.common", converted.OptionalExtensions.Keys);
            converted.Session.Edit(edit => edit.Settings = edit.Settings with { Width = 1920 }, Token);
            Save(fromModern, resaved, converted.Session, Options(), Token);
        }
        string back = Package("back", !fromModern);
        Assert.True(PmaProjectStore.ConvertPackage(resaved, back, directory, Token,
            prepareMissingClassicToons: _ => DefaultToon(project.Objects[0].Id)).RestoredArchive);
        using var restored = Load(!fromModern, back);
        Assert.Equal(1920, restored.Session.Settings.Width);
        Assert.Equal(resource, restored.ReadResource(featurePath));
        Assert.True(JsonElement.DeepEquals(metadata.RootElement, restored.OptionalExtensions[feature]));
        if (fromModern) Assert.Equal(light, Assert.Single(restored.Session.Lighting.Lights));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReorderedActorsRestoreOnlyWhenEachStillHasItsArchivedModel(bool swapModels)
    {
        using var project = Fixture(separateToon: true);
        using var other = Fixture(separateToon: true, name: "second");
        var firstActor = project.Objects[0];
        var otherActor = other.Objects[0];
        var light = new NativeSceneLight(Guid.NewGuid(), "follow", SceneLightKind.Point, Vector3.One,
            Quaternion.Identity, Vector3.One, Parent: new(firstActor.Id));
        project.Edit(edit =>
        {
            edit.Composition.Add(otherActor.Name, otherActor.SourcePath, ModelRole.Prop, otherActor.Source);
            edit.Lighting.Add(light);
        }, Token);
        var secondId = project.Objects[1].Id;
        string original = Package("original", false), classic = Package("classic", true);
        Save(false, original, project, Options(), Token);
        PmaProjectStore.ConvertPackage(original, classic, directory, Token);

        if (swapModels)
        {
            string edited = Package("swapped", true);
            using var loaded = Load(true, classic);
            // Both the actor IDs and the set of model digests survive, but neither actor owns its original model.
            var versions = loaded.Session.Objects;
            Save(true, edited, loaded.Session, Options() with { ModelOverrides = new Dictionary<Guid, PmxModel>
            {
                [firstActor.Id] = LoadAsset(versions[1].SourcePath, Token).Model,
                [secondId] = LoadAsset(versions[0].SourcePath, Token).Model,
            } }, Token);
            classic = edited;
        }
        PmaProjectStoreTests.RewritePackagedFile(classic, "project.mmdscene", bytes =>
        {
            var document = JsonNode.Parse(bytes)!;
            var actors = document["layout"]!["objects"]!.AsArray();
            var first = actors[0];
            actors.RemoveAt(0);
            actors.Add(first);
            return JsonSerializer.SerializeToUtf8Bytes(document);
        });

        string back = Package("back", false);
        var report = PmaProjectStore.ConvertPackage(classic, back, directory, Token);
        Assert.Equal(!swapModels, report.RestoredArchive);
        Assert.Equal(swapModels, report.DeferredArchive);
        using var result = Load(false, back);
        Assert.Equal(new[] { secondId, firstActor.Id }, result.Session.Objects.Select(actor => actor.Id));
        if (swapModels)
        {
            Assert.Empty(result.Session.Lighting.Lights);
            Assert.All(result.Session.Objects, actor => Assert.Equal(ModelRole.Unspecified, actor.Role));
            Assert.Contains(result.ResourcePaths, path => path.EndsWith("/state.json", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(light, Assert.Single(result.Session.Lighting.Lights));
            Assert.Equal(new[] { ModelRole.Prop, ModelRole.Character }, result.Session.Objects.Select(actor => actor.Role));
        }
    }

    [Fact]
    public void ChangedActorSetDefersRestorationAndKeepsTheArchiveInTheConvertedProject()
    {
        using var project = Fixture();
        var light = new NativeSceneLight(Guid.NewGuid(), "rig", SceneLightKind.Point, Vector3.One,
            Quaternion.Identity, Vector3.One, Parent: new(project.Objects[0].Id));
        project.Edit(edit => edit.Lighting.Add(light), Token);
        string original = Package("actors", false), classic = Package("classic", true);
        Save(false, original, project, Options(), Token);
        PmaProjectStore.ConvertPackage(original, classic, directory, Token,
            prepareMissingClassicToons: _ => DefaultToon(project.Objects[0].Id));
        string changed = Package("edited", true);
        using (var loaded = Load(true, classic))
        {
            var actor = loaded.Session.Objects[0];
            loaded.Session.Edit(edit => edit.Composition.Add("copy", actor.SourcePath, ModelRole.Unspecified, actor.Source), Token);
            Save(true, changed, loaded.Session, Options(), Token);
        }
        string back = Package("deferred", false);
        var report = PmaProjectStore.ConvertPackage(changed, back, directory, Token);
        Assert.False(report.RestoredArchive);
        Assert.True(report.DeferredArchive);
        using var result = Load(false, back);
        Assert.Equal(2, result.Session.Objects.Count);
        Assert.Empty(result.Session.Lighting.Lights);
        Assert.Contains(result.ResourcePaths, path => path.StartsWith("render-resources/project-conversion/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyClassicEditsAreRetainedWhenTheyOverlapTheModernArchive(bool editLights)
    {
        using var project = Fixture(separateToon: true);
        var originalLight = new NativeSceneLight(Guid.NewGuid(), "original", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One);
        var freshLight = originalLight with { Id = Guid.NewGuid(), Name = "current" };
        project.Edit(edit => edit.Lighting.Add(originalLight), Token);
        string original = Package("original", false), classic = Package("classic", true), changed = Package("changed", true);
        Save(false, original, project, Options(), Token);
        var archived = PmaProjectStore.ConvertPackage(original, classic, directory, Token);
        Assert.Equal(1, archived.OmittedModelPurposes);
        using (var loaded = Load(true, classic))
        {
            var actor = loaded.Session.Objects[0];
            loaded.Session.Edit(edit =>
            {
                if (editLights) edit.Lighting.Add(freshLight);
                else edit.Lighting.SetModelRole(actor.Key, ModelRole.Prop);
            }, Token);
            Save(true, changed, loaded.Session, Options(), Token);
        }
        string target = Package("modern", false);
        var report = PmaProjectStore.ConvertPackage(changed, target, directory, Token);
        Assert.False(report.RestoredArchive);
        Assert.True(report.DeferredArchive);
        using var restored = Load(false, target);
        if (editLights) Assert.Equal(freshLight, Assert.Single(restored.Session.Lighting.Lights));
        else Assert.Equal(ModelRole.Prop, restored.Session.Objects[0].Role);
        Assert.Contains(restored.ResourcePaths, path => path.EndsWith("/state.json", StringComparison.Ordinal));
    }

    [Fact]
    public void PackagedSeparateToonConvertsWithoutAskingForOriginalMmdFiles()
    {
        using var project = Fixture(separateToon: true);
        string source = Package("authored", false), target = Package("classic", true);
        Save(false, source, project, Options(), Token);
        File.Delete(Path.Combine(directory, "authored.bmp"));
        PmaProjectStore.ConvertPackage(source, target, directory, Token,
            prepareMissingClassicToons: _ => throw new InvalidOperationException("An authored Toon needs no original MMD files."));
        using var loaded = Load(true, target);
        Assert.DoesNotContain("org.mda.classic-toons", loaded.OptionalExtensions.Keys);
        using var file = File.OpenRead(loaded.Session.Objects[0].SourcePath);
        var model = PmxReader.Read(file);
        Assert.Equal(ToonImage(), File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(loaded.Session.Objects[0].SourcePath)!,
            model.Textures[model.Materials[0].ToonTextureIndex])));
    }

    [Fact]
    public void DefaultToonRequiresAFrozenSourceAndRepeatedConversionsReuseIt()
    {
        using var project = Fixture();
        string original = Package("original", false), classic = Package("classic", true);
        Save(false, original, project, Options(), Token);
        Assert.Throws<NotSupportedException>(() => PmaProjectStore.ConvertPackage(original, classic, directory, Token));
        Assert.False(File.Exists(classic));

        var supplied = DefaultToon(project.Objects[0].Id);
        using var snapshot = Capture(false, project, Options(), Token);
        int calls = 0;
        PmaProjectStore.ConvertSnapshot(snapshot, classic, directory, Token,
            prepareMissingClassicToons: _ => { calls++; return supplied; });
        Assert.Equal(1, calls);
        string modern = Package("modern", false), restored = Package("restored", true);
        PmaProjectStore.ConvertPackage(classic, modern, directory, Token);
        var report = PmaProjectStore.ConvertPackage(modern, restored, directory, Token,
            prepareMissingClassicToons: _ => throw new InvalidOperationException("Archived original Toons should be reused."));
        Assert.True(report.RestoredArchive);
        using var loaded = Load(true, restored);
        Assert.True(JsonElement.DeepEquals(supplied.OptionalExtensions["org.mda.classic-toons"], loaded.OptionalExtensions["org.mda.classic-toons"]));
        var image = Assert.Single(supplied.AdditionalResources);
        Assert.Equal(image.Value.ToArray(), loaded.ReadResource(image.Key));
        using var first = Load(true, classic);
        Assert.Equal(first.ResourcePaths.Count, loaded.ResourcePaths.Count);
    }

    [Fact]
    public void ConversionRefusesExistingTargetsCancellationAndRenamedSourcesWithoutPublishing()
    {
        using var project = Fixture();
        string source = Package("original", false), target = Package("converted", true);
        Save(false, source, project, Options(), Token);
        byte[] previous = [4, 2];
        File.WriteAllBytes(target, previous);
        Assert.Throws<IOException>(() => PmaProjectStore.ConvertPackage(source, target, directory, Token));
        Assert.Equal(previous, File.ReadAllBytes(target));
        File.Delete(target);
        Assert.Throws<OperationCanceledException>(() => PmaProjectStore.ConvertPackage(source, target, directory, new CancellationToken(true)));
        Assert.False(File.Exists(target));
        var disguised = Package("disguised", true);
        File.Copy(source, disguised);
        var output = Package("rejected", false);
        Assert.Throws<NotSupportedException>(() => PmaProjectStore.ConvertPackage(disguised, output, directory, Token));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ClassicSnapshotPinsLoadedResourcesUntilDisposedAfterOwnerCloses()
    {
        using var project = Fixture(); string source = Package("lease-source", true);
        Save(true, source, project, Options() with
        { AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { ["render-resources/data.bin"] = new byte[] { 2, 4, 6 } } }, Token);
        var loaded = Load(true, source); string cache = loaded.CacheDirectory;
        using var snapshot = Capture(true, loaded.Session, Options(), Token);
        Guid id = loaded.ProjectId; loaded.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loaded.ReadResource("render-resources/data.bin"));
        Assert.True(Directory.Exists(cache));
        File.Delete(project.Objects[0].SourcePath);
        string recovered = Package("lease-recovered", true); snapshot.Save(recovered, Token);
        using (var reopened = Load(true, recovered))
        {
            Assert.Equal(id, reopened.ProjectId);
            Assert.Equal("mmd-classic-pmc", LoadedFormat(reopened));
            Assert.Equal(new byte[] { 2, 4, 6 }, reopened.ReadResource("render-resources/data.bin"));
        }
        snapshot.Dispose(); Assert.False(Directory.Exists(cache));
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("project.mmdscene")]
    [InlineData("render-resources/CON.bin")]
    [InlineData("render-resources/dir/../data.bin")]
    public void InvalidOpaqueResourcePathsPreserveExistingClassicPackageWithoutStaging(string logical)
    {
        using var project = Fixture(); string path = Package("invalid-resource", true);
        Save(true, path, project, Options(), Token); byte[] before = File.ReadAllBytes(path);
        string rejectedCache = Path.Combine(directory, "rejected-cache");
        Assert.Throws<InvalidDataException>(() => Save(true, path, project, Options() with
        {
            CacheDirectory = rejectedCache,
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { [logical] = new byte[] { 1 } },
        }, Token));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(Directory.Exists(rejectedCache));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void ClassicCancellationBudgetAndMissingSourcePreserveExistingPackageAndCleanStage()
    {
        using var project = Fixture(); string path = Package("atomic", true);
        Save(true, path, project, Options(), Token); byte[] before = File.ReadAllBytes(path);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Save(true, path, project, Options(), cancelled.Token));
        Assert.Equal(before, File.ReadAllBytes(path));
        using var snapshot = Capture(true, project, Options(), Token);
        Assert.Throws<OperationCanceledException>(() => snapshot.Save(path, cancelled.Token));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Throws<InvalidDataException>(() => Save(true, path, project,
            Options() with { ValidationOptions = new() { MaxPackageBytes = 1 } }, Token));
        Assert.Equal(before, File.ReadAllBytes(path));
        File.Delete(project.Objects[0].SourcePath);
        Assert.ThrowsAny<IOException>(() => snapshot.Save(path, Token));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "save-cache")));
    }

    [Fact]
    public void ClassicCorruptBlobRejectsBeforeHostAndPhysicsAndCreatesNoExtraction()
    {
        using var project = Fixture(); string path = Package("corrupt", true);
        Save(true, path, project, Options() with
        { AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { ["render-resources/data.bin"] = new byte[] { 1, 2, 3 } } }, Token);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            string hash;
            using (var input = archive.GetEntry("manifest.json")!.Open())
            using (var manifest = JsonDocument.Parse(input))
                hash = manifest.RootElement.GetProperty("files").EnumerateArray()
                    .Single(file => file.GetProperty("path").GetString() == "render-resources/data.bin").GetProperty("sha256").GetString()!;
            archive.GetEntry("blobs/" + hash)!.Delete();
            using var output = archive.CreateEntry("blobs/" + hash).Open(); output.Write(new byte[] { 3, 2, 1 });
        }
        var physics = new TrackingPhysics(); int calls = 0;
        Assert.Throws<InvalidDataException>(() => Load(true, path, (p, t) => { calls++; return LoadAsset(p, t); }, physics));
        Assert.Equal(0, calls); Assert.Equal(0, physics.Calls);
        Assert.False(Directory.Exists(Path.Combine(directory, "load-cache")));
    }

    // Reflection keeps the initial RED run executable before the new facade exists, and verifies its public signatures.
    private static Type PmcType()
    {
        var type = typeof(PmaProjectStore).Assembly.GetType("Mmd.Modern.Application.PmcProjectStore");
        Assert.NotNull(type); return type;
    }
    private static MethodInfo PmcMethod(string name) => Assert.Single(PmcType().GetMethods(BindingFlags.Public | BindingFlags.Static), method => method.Name == name);
    private static string? LoadedFormat(PmaLoadedProject project)
    {
        var property = typeof(PmaLoadedProject).GetProperty("Format"); Assert.NotNull(property);
        Assert.False(property.CanWrite); return Assert.IsType<string>(property.GetValue(project));
    }
    private static string Format(bool classic) => classic ? "mmd-classic-pmc" : "mmd-modern-pma";
    private static void Save(bool classic, string path, SceneProjectSession project, PmaSaveOptions? options, CancellationToken token)
    {
        if (!classic) { PmaProjectStore.Save(path, project, options, token); return; }
        PmcMethod("Save").CreateDelegate<Action<string, SceneProjectSession, PmaSaveOptions?, CancellationToken>>()(path, project, options, token);
    }
    private static PmaSaveSnapshot Capture(bool classic, SceneProjectSession project, PmaSaveOptions? options, CancellationToken token)
        => classic ? PmcMethod("CaptureSnapshot").CreateDelegate<Func<SceneProjectSession, PmaSaveOptions?, CancellationToken, PmaSaveSnapshot>>()(project, options, token)
            : PmaProjectStore.CaptureSnapshot(project, options, token);
    private PmaLoadedProject Load(bool classic, string path, Func<string, CancellationToken, SceneAnimationModelAsset>? loader = null,
        IProjectPhysicsBackendProvider? physics = null, PmaLoadOptions? options = null)
    {
        loader ??= LoadAsset; physics ??= new TrackingPhysics(); string cache = Path.Combine(directory, "load-cache");
        return classic ? PmcMethod("Load").CreateDelegate<Func<string, string, Func<string, CancellationToken, SceneAnimationModelAsset>, IProjectPhysicsBackendProvider, PmaLoadOptions?, CancellationToken, PmaLoadedProject>>()
            (path, cache, loader, physics, options, Token) : PmaProjectStore.Load(path, cache, loader, physics, options, Token);
    }
    private string Package(string name, bool classic) => Path.Combine(directory, name + (classic ? ".pmc" : ".pma"));
    private PmaSaveOptions Options() => new() { CacheDirectory = Path.Combine(directory, "save-cache") };
    private static byte[] ToonImage() =>
        Convert.FromBase64String("Qk06AAAAAAAAADYAAAAoAAAAAQAAAAEAAAABABgAAAAAAAQAAAATCwAAEwsAAAAAAAAAAAAAf6D/AA==");

    private static PmaSaveOptions DefaultToon(Guid objectId)
    {
        byte[] image = ToonImage();
        string hash = Convert.ToHexString(SHA256.HashData(image));
        string path = "render-resources/classic-toons/" + hash.ToLowerInvariant() + ".bin";
        var manifest = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 2,
            models = new[] { new { objectId, materials = new[] { new { materialIndex = 0, slot = 0, imageSha256 = hash } } } },
            images = new[] { new { sha256 = hash, authoredToken = "toon0.bmp", resourcePath = path } },
        });
        return new()
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { ["org.mda.classic-toons"] = manifest },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { [path] = image },
        };
    }
    private SceneProjectSession Fixture(bool separateToon = false, string name = "actor")
    {
        var template = PmmMotionAdapterTests.Pmx(["root"]);
        var model = new PmxModel
        {
            Header = template.Header, Name = name, EnglishName = "", Comment = "opaque archive fixture", EnglishComment = "",
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
            { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = separateToon ? ["authored.bmp"] : [], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One,
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, EdgeColor = Vector4.Zero, EdgeSize = 1, DrawMode = PmxDrawModeFlags.None,
                TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                ToonTextureIndex = separateToon ? 0 : -1, Memo = "", FaceVertexCount = 3 }],
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
        };
        if (separateToon) File.WriteAllBytes(Path.Combine(directory, "authored.bmp"), ToonImage());
        string source = Path.Combine(directory, name + ".pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor(); composition.Add(name, source, ModelRole.Character, Rest(model));
        return new(composition, new(), [], new TrackingPhysics(), new() { Width = 1280, Height = 720 }, cancellationToken: Token);
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset LoadAsset(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(path); var model = PmxReader.Read(input); return new(model, Rest(model)); }
    private sealed class TrackingPhysics : IProjectPhysicsBackendProvider
    {
        public int Calls { get; private set; }
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject actor)
        { Calls++; throw new InvalidOperationException("Storage must not create a physics world for this fixture."); }
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
        { Calls++; throw new InvalidOperationException("Storage must not create a physics world for this fixture."); }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
