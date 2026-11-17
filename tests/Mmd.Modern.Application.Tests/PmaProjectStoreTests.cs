using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmaProjectStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pma-tests-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PmaProjectStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void SinglePackageMovesWithoutSourcesAndRetainsEditedPmxLightsAndMotion()
    {
        using var project = Fixture(out var model, out var id);
        var original = File.ReadAllBytes(project.Objects[0].SourcePath);
        var edited = Copy(model, "edited metadata");
        string path = Path.Combine(directory, "scene.pma");
        PmaProjectStore.Save(path, project, new() { ModelOverrides = new Dictionary<Guid, PmxModel> { [id] = edited } }, Token);
        string moved = Path.Combine(directory, "moved"); Directory.CreateDirectory(moved);
        string package = Path.Combine(moved, "renamed.pma"); File.Move(path, package);
        File.Delete(project.Objects[0].SourcePath); File.Delete(Path.Combine(directory, "texture.png"));
        using var loaded = PmaProjectStore.Load(package, Path.Combine(directory, "cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
        Assert.Equal(project.Objects.Select(o => o.Id), loaded.Session.Objects.Select(o => o.Id));
        Assert.Equal(2, loaded.Session.Lighting.Lights.Count);
        Assert.Equal(2560, loaded.Session.Settings.Width);
        Assert.Equal(30, loaded.Session.CurrentFrame);
        Assert.Equal(project.Frame.Surface.Snapshot.Vertices, loaded.Session.Frame.Surface.Snapshot.Vertices);
        using (var modelInput = File.OpenRead(loaded.Session.Objects[0].SourcePath))
            Assert.Equal("edited metadata", PmxReader.Read(modelInput).Comment);
        var version = Assert.Single(loaded.Models);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(loaded.CacheDirectory, version.OriginalSourcePath)));
        Assert.NotEqual(version.OriginalModelPath, version.EditedModelPath);
        using var archive = ZipFile.OpenRead(package);
        Assert.All(archive.Entries, e => Assert.True(e.FullName == "manifest.json" || e.FullName.StartsWith("blobs/", StringComparison.Ordinal)));
        Assert.Equal(archive.Entries.Count, archive.Entries.Select(e => e.FullName).Distinct().Count());
    }

    [Fact]
    public void FullNativeAuthoringCapacityAndThirtyTwoEnabledLightsSurvivePmaReopen()
    {
        using var project = Fixture(out _, out _);
        var group = new SceneLightGroup(Guid.NewGuid(), "窗光组", Intensity: 2);
        var lights = Enumerable.Range(0, SceneLightingSnapshot.MaxLights).Select(index => new NativeSceneLight(Guid.NewGuid(),
            "window " + index, (SceneLightKind)(index % 3), new(index, 1, 2), Quaternion.Identity, Vector3.One,
            Intensity: index + 1, Enabled: index < SceneLightingSnapshot.MaxActiveLights, GroupId: group.Id)).ToArray();
        project.Edit(edit => edit.Lighting.ReplaceSnapshot(new(lights, [group]), lights[^1].Id), Token);
        string path = Path.Combine(directory, "capacity.pma"); PmaProjectStore.Save(path, project, cancellationToken: Token);
        using var reopened = PmaProjectStore.Load(path, Path.Combine(directory, "capacity-cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
        Assert.Equal(lights, reopened.Session.Lighting.Lights);
        Assert.Equal(group, Assert.Single(reopened.Session.Lighting.Groups));
        Assert.Equal(32, reopened.Session.Frame.Lighting!.EffectiveLights.Count(light => light.Enabled));
        Assert.Equal(256, reopened.Session.Frame.Lighting.EffectiveLights[^1].Intensity);
    }

    [Fact]
    public void AreaAndRetainedInactiveAreaSizeSurviveDetachedPmaCaptureAndHistory()
    {
        using var project = Fixture(out _, out _);
        var area = project.Lighting.Lights[0] with { Kind = SceneLightKind.Area, AreaSize = new(8.123456f, 12.654321f) };
        var point = project.Lighting.Lights[1] with { Kind = SceneLightKind.Point, AreaSize = new(2, 3) };
        project.Edit(edit => edit.Lighting.ReplaceSnapshot(new([area, point]), area.Id), Token);
        using var capture = PmaProjectStore.CaptureSnapshot(project, cancellationToken: Token);
        project.Edit(edit => edit.Lighting.Update(area with { AreaSize = new(1, 1) }), Token);
        Assert.True(project.Undo(Token)); Assert.Equal(area, project.Lighting.Lights[0]);
        Assert.True(project.Redo(Token));
        string path = Path.Combine(directory, "area.pma"); capture.Save(path, Token);
        using var reopened = PmaProjectStore.Load(path, Path.Combine(directory, "area-cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
        Assert.Equal(new[] { area, point }, reopened.Session.Lighting.Lights);
        Assert.NotEqual(project.Lighting.Lights[0].AreaSize, reopened.Session.Lighting.Lights[0].AreaSize);
    }

    [Fact]
    public void DetachedPmaCapturePreservesGlobalLightingIndependentOfLaterEdits()
    {
        using var project = Fixture(out _, out _); var settings = new SceneLightingSettings(3.123456f, -7.25f);
        project.Edit(edit => edit.Lighting.UpdateSettings(settings), Token);
        using var capture = PmaProjectStore.CaptureSnapshot(project, cancellationToken: Token);
        project.Edit(edit => edit.Lighting.UpdateSettings(new(1, 2)), Token);
        Assert.True(project.Undo(Token)); Assert.Equal(settings, project.Frame.Lighting!.Settings);
        Assert.True(project.Redo(Token));
        string path = Path.Combine(directory, "global-lighting.pma"); capture.Save(path, Token);
        using var reopened = PmaProjectStore.Load(path, Path.Combine(directory, "global-lighting-cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
        Assert.Equal(settings, reopened.Session.Lighting.Settings); Assert.Equal(settings, reopened.Session.Frame.Lighting!.Settings);
        Assert.NotEqual(project.Lighting.Settings, reopened.Session.Lighting.Settings);
        Assert.Contains(PmaPmmCompatibility.Inspect(reopened.Session).Issues, issue => issue.Code == "native-lighting-settings");
    }

    [Fact]
    public void DetachedPmaCapturePreservesGroupsAndMembershipAfterLiveEditsAndSourceRemoval()
    {
        using var project = Fixture(out _, out _);
        var group = new SceneLightGroup(Guid.NewGuid(), "人物柔光", Enabled: false, Intensity: 2.5f);
        project.Edit(edit =>
        {
            edit.Lighting.AddGroup(group);
            edit.Lighting.AssignToGroup(edit.Lighting.Lights.Select(light => light.Id), group.Id);
            edit.Lighting.Update(edit.Lighting.Lights[1] with { Enabled = false, Intensity = 3 });
        }, Token);
        var authored = project.Lighting.Lights.ToArray();
        using var capture = PmaProjectStore.CaptureSnapshot(project, cancellationToken: Token);
        project.Edit(edit => edit.Lighting.RemoveGroup(group.Id), Token);
        string path = Path.Combine(directory, "grouped.pma"); capture.Save(path, Token);
        File.Delete(project.Objects[0].SourcePath); File.Delete(Path.Combine(directory, "texture.png"));
        using var loaded = PmaProjectStore.Load(path, Path.Combine(directory, "group-cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
        Assert.Equal(group, Assert.Single(loaded.Session.Lighting.Groups));
        Assert.Equal(authored, loaded.Session.Lighting.Lights);
        Assert.All(loaded.Session.Frame.Lighting!.EffectiveLights, light => Assert.False(light.Enabled));
        Assert.True(loaded.Session.Lighting.Lights[0].Enabled); Assert.False(loaded.Session.Lighting.Lights[1].Enabled);
        loaded.Session.Edit(edit => edit.Lighting.UpdateGroup(group with { Enabled = true }), Token);
        Assert.True(loaded.Session.Frame.Lighting!.EffectiveLights[0].Enabled);
        Assert.False(loaded.Session.Frame.Lighting.EffectiveLights[1].Enabled);
        Assert.Empty(project.Lighting.Groups);
        string resaved = Path.Combine(directory, "grouped-resaved.pma"); PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
        string layoutCopy = Path.Combine(directory, "grouped-selection.mmdscene");
        loaded.Session.Save(layoutCopy, new() { CopyModelAssets = false }, Token);
        using (var json = JsonDocument.Parse(File.ReadAllBytes(layoutCopy)))
            Assert.Equal(authored[1].Id, json.RootElement.GetProperty("layout").GetProperty("selectedLightId").GetGuid());
        using var reopened = PmaProjectStore.Load(resaved, Path.Combine(directory, "group-cache-2"), LoadModel, new NoPhysics(), cancellationToken: Token);
        Assert.Equal(loaded.Session.Lighting.Groups, reopened.Session.Lighting.Groups);
        Assert.Equal(authored, reopened.Session.Lighting.Lights);
    }

    [Fact]
    public void AnEmptyAuthoredLightGroupIsStillReportedAsNonrepresentableByPmm()
    {
        using var project = Fixture(out _, out _);
        project.Edit(edit =>
        {
            foreach (var light in edit.Lighting.Lights.ToArray()) edit.Lighting.Remove(light.Id);
            edit.Lighting.AddGroup(new(Guid.NewGuid(), "预留灯组"));
        }, Token);
        Assert.Contains(PmaPmmCompatibility.Inspect(project).Issues, issue => issue.Code == "native-light-groups");
        Assert.Throws<PmaPmmExportException>(() => project.ExportPmm());
    }

    [Fact]
    public void OptionalUnknownExtensionAndProjectIdSurviveSessionResave()
    {
        using var project = Fixture(out _, out _);
        using var extension = JsonDocument.Parse("{\"version\":17,\"nested\":[\"未来\",3]}");
        string path = Path.Combine(directory, "extensions.pma");
        PmaProjectStore.Save(path, project, new() { OptionalExtensions = new Dictionary<string, JsonElement> { ["org.example.future"] = extension.RootElement } }, Token);
        Guid firstId;
        using (var loaded = PmaProjectStore.Load(path, Path.Combine(directory, "cache"), LoadModel, new NoPhysics(), cancellationToken: Token))
        {
            firstId = loaded.ProjectId;
            PmaProjectStore.Save(path, loaded.Session, cancellationToken: Token);
        }
        using var reopened = PmaProjectStore.Load(path, Path.Combine(directory, "cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
        Assert.Equal(firstId, reopened.ProjectId);
        Assert.True(JsonElement.DeepEquals(extension.RootElement, reopened.OptionalExtensions["org.example.future"]));
        Assert.Contains(PmaPmmCompatibility.Inspect(reopened.Session).Issues, issue => issue.Code == "optional-extensions");
    }

    [Theory]
    [InlineData("required")]
    [InlineData("traversal")]
    [InlineData("hash")]
    [InlineData("duplicate")]
    [InlineData("budget")]
    public void InvalidPackageRejectsBeforeHostLoader(string corruption)
    {
        using var project = Fixture(out _, out _);
        string path = Path.Combine(directory, "invalid.pma"); PmaProjectStore.Save(path, project, cancellationToken: Token);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!; JsonNode node;
            using (var reader = new StreamReader(entry.Open())) node = JsonNode.Parse(reader.ReadToEnd())!;
            if (corruption == "required") node["requiredExtensions"] = new JsonArray("org.example.unknown");
            if (corruption == "traversal") node["files"]![0]!["path"] = "../escape";
            if (corruption == "hash") node["files"]![0]!["sha256"] = new string('0', 64);
            entry.Delete(); using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write(node.ToJsonString());
            if (corruption == "duplicate") { using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write("{}"); }
        }
        int calls = 0;
        Assert.ThrowsAny<Exception>(() => PmaProjectStore.Load(path, Path.Combine(directory, "cache"), (p, t) => { calls++; return LoadModel(p, t); }, new NoPhysics(),
            corruption == "budget" ? new() { MaxExpandedBytes = 1 } : null, Token));
        Assert.Equal(0, calls);
        Assert.Empty(Directory.Exists(Path.Combine(directory, "cache")) ? Directory.GetDirectories(Path.Combine(directory, "cache")) : []);
    }

    [Theory]
    [InlineData("sources/CON.pmx")]
    [InlineData("sources/nul.bin")]
    [InlineData("sources/AuX.json")]
    [InlineData("assets/COM1/model.pmx")]
    [InlineData("assets/LPT9/model.pmx")]
    public void ReservedWindowsDevicePathsRejectBeforeExtraction(string logicalPath)
    {
        using var project = Fixture(out _, out _);
        string path = Path.Combine(directory, "device.pma");
        PmaProjectStore.Save(path, project, cancellationToken: Token);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!; JsonNode node;
            using (var reader = new StreamReader(entry.Open())) node = JsonNode.Parse(reader.ReadToEnd())!;
            node["files"]![0]!["path"] = logicalPath;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(node.ToJsonString());
        }
        string cache = Path.Combine(directory, "device-cache"); int calls = 0;
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Load(path, cache,
            (p, t) => { calls++; return LoadModel(p, t); }, new NoPhysics(), cancellationToken: Token));
        Assert.Equal(0, calls); Assert.False(Directory.Exists(cache));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedProjectSectionRejectsBeforeExtractionOrJsonAllocation(bool upperCasePath)
    {
        using var project = Fixture(out _, out _);
        string path = Path.Combine(directory, "oversized-project.pma");
        PmaProjectStore.Save(path, project, cancellationToken: Token);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var manifestEntry = zip.GetEntry("manifest.json")!; JsonNode node;
            using (var reader = new StreamReader(manifestEntry.Open())) node = JsonNode.Parse(reader.ReadToEnd())!;
            var file = node["files"]!.AsArray().Single(item => item!["path"]!.GetValue<string>() == "project.mmdscene")!;
            if (upperCasePath) file["path"] = "PROJECT.MMDSCENE";
            var oldBlob = zip.GetEntry("blobs/" + file["sha256"]!.GetValue<string>())!;
            byte[] bytes = new byte[SceneAnimationProjectStore.MaxDocumentBytes + 1]; Array.Fill(bytes, (byte)' ');
            int originalLength = checked((int)oldBlob.Length);
            using (var input = oldBlob.Open()) input.ReadExactly(bytes.AsSpan(0, originalLength));
            oldBlob.Delete();
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using (var output = zip.CreateEntry("blobs/" + hash).Open()) output.Write(bytes);
            file["sha256"] = hash; file["length"] = bytes.Length;
            manifestEntry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(node.ToJsonString());
        }
        string cache = Path.Combine(directory, "oversized-cache"); int calls = 0;
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Load(path, cache,
            (p, t) => { calls++; return LoadModel(p, t); }, new NoPhysics(), cancellationToken: Token));
        Assert.Equal(0, calls); Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void FailedAndCancelledSavePreserveExistingPackage()
    {
        using var project = Fixture(out _, out _); string path = Path.Combine(directory, "safe.pma");
        PmaProjectStore.Save(path, project, cancellationToken: Token); byte[] before = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PmaProjectStore.Save(path, project, cancellationToken: cancellation.Token));
        File.Delete(Path.Combine(directory, "texture.png"));
        Assert.ThrowsAny<IOException>(() => PmaProjectStore.Save(path, project, cancellationToken: Token));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void PmmPreflightListsActualLossesAndRecommendsPma()
    {
        using var project = Fixture(out _, out var id);
        project.Edit(e => e.Composition.SetTransform(id, Vector3.Zero, Quaternion.Identity, 2), Token);
        var report = PmaPmmCompatibility.Inspect(project);
        Assert.False(report.CanExport); Assert.True(report.RecommendPma);
        Assert.Contains(report.Issues, i => i.Code == "native-lights");
        Assert.Contains(report.Issues, i => i.Code == "model-transform" && i.ObjectId == id);
        Assert.Throws<PmaPmmExportException>(() => project.ExportPmm());
        Assert.Throws<PmaPmmExportException>(() => project.SavePmm(Path.Combine(directory, "refused.pmm"), cancellationToken: Token));
        Assert.False(File.Exists(Path.Combine(directory, "refused.pmm")));
    }

    [Fact]
    public void FullRichPmxSectionsSurviveTypedOverrideAndSecondSave()
    {
        using var fixtureInput = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        var original = PmxReader.Read(fixtureInput);
        foreach (string texture in original.Textures.Where(t => t.Length != 0))
        {
            string dependency = Path.GetFullPath(texture.Replace('\\', '/'), directory);
            Directory.CreateDirectory(Path.GetDirectoryName(dependency)!); File.WriteAllBytes(dependency, [7, 8, 9]);
        }
        string source = Path.Combine(directory, "rich.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(original));
        var composition = new SceneCompositionEditor(); var actor = composition.Add("rich", source, ModelRole.Character, Rest(original));
        using var project = new SceneProjectSession(composition, new(), [], new NoPhysics(), cancellationToken: Token);
        var edited = Copy(original, "full-section-edit");
        string path = Path.Combine(directory, "rich.pma");
        PmaProjectStore.Save(path, project, new() { ModelOverrides = new Dictionary<Guid, PmxModel> { [actor.Id] = edited } }, Token);
        byte[] originalBytes = File.ReadAllBytes(source);
        using (var loaded = PmaProjectStore.Load(path, Path.Combine(directory, "cache"), LoadModel, new NoPhysics(), cancellationToken: Token))
        {
            using var input = File.OpenRead(loaded.Session.Objects[0].SourcePath); var actual = PmxReader.Read(input);
            Assert.Equal(PmxWriter.ToBytes(Copy(edited, edited.Comment, actual.Textures)), PmxWriter.ToBytes(actual));
            PmaProjectStore.Save(path, loaded.Session, cancellationToken: Token);
        }
        using var reopened = PmaProjectStore.Load(path, Path.Combine(directory, "cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(reopened.CacheDirectory, Assert.Single(reopened.Models).OriginalSourcePath)));
        using var reopenedInput = File.OpenRead(reopened.Session.Objects[0].SourcePath);
        var final = PmxReader.Read(reopenedInput);
        Assert.Equal(PmxWriter.ToBytes(Copy(edited, edited.Comment, final.Textures)), PmxWriter.ToBytes(final));
    }

    [Fact]
    public void FailedHostLoadCleansCandidateAndDoesNotMutateExistingProject()
    {
        using var project = Fixture(out _, out _); string path = Path.Combine(directory, "failed-load.pma");
        PmaProjectStore.Save(path, project, cancellationToken: Token);
        var frame = project.Frame;
        Assert.Throws<SceneProjectAssetException>(() => PmaProjectStore.Load(path, Path.Combine(directory, "cache"),
            (_, _) => throw new IOException("test loader failure"), new NoPhysics(), cancellationToken: Token));
        Assert.Same(frame, project.Frame);
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "cache")));
    }

    [Fact]
    public void PreflightAcceptsOrdinaryPmxAndDoesNotInventLossFromItsFormat()
    {
        using var project = Fixture(out _, out _);
        project.Edit(edit =>
        {
            foreach (var light in edit.Lighting.Lights.ToArray()) edit.Lighting.Remove(light.Id);
            edit.Settings = new();
        }, Token);
        var report = PmaPmmCompatibility.Inspect(project);
        Assert.True(report.CanExport); Assert.False(report.RecommendPma); Assert.Null(report.Recommendation);
        Assert.Single(project.ExportPmm().Models);
        project.SavePmm(Path.Combine(directory, "plain.pmm"), cancellationToken: Token);
        Assert.True(File.Exists(Path.Combine(directory, "plain.pmm")));
    }

    [Theory]
    [InlineData("motion")]
    [InlineData("texture")]
    public void TamperedReferencesWithValidOuterHashesRejectBeforeHostLoader(string kind)
    {
        using var project = Fixture(out _, out _); string path = Path.Combine(directory, "references.pma");
        PmaProjectStore.Save(path, project, cancellationToken: Token);
        if (kind == "motion") RewritePackagedFile(path, "project.mmdscene", bytes =>
        {
            var node = JsonNode.Parse(bytes)!; node["motions"]![0]!["assetPath"] = "../external.vmd";
            return Encoding.UTF8.GetBytes(node.ToJsonString());
        });
        else
        {
            using (var zip = ZipFile.OpenRead(path))
            using (var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open()))
            {
                var manifest = JsonNode.Parse(reader.ReadToEnd())!;
                string modelPath = manifest["models"]![0]!["editedModelPath"]!.GetValue<string>();
                // Release read handles before editing the archive on Windows.
                reader.Dispose(); zip.Dispose();
                RewritePackagedFile(path, modelPath, bytes =>
                {
                    var model = PmxReader.Read(new MemoryStream(bytes));
                    return PmxWriter.ToBytes(Copy(model, model.Comment, ["../external.png"]));
                });
            }
        }
        int calls = 0;
        Assert.ThrowsAny<Exception>(() => PmaProjectStore.Load(path, Path.Combine(directory, "cache"),
            (p, t) => { calls++; return LoadModel(p, t); }, new NoPhysics(), cancellationToken: Token));
        Assert.Equal(0, calls);
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "cache")));
    }

    [Fact]
    public void SaveValidationBudgetsFailAtomicallyAndAreExplicitlyConfigurable()
    {
        using var project = Fixture(out _, out _); string path = Path.Combine(directory, "limits.pma");
        PmaProjectStore.Save(path, project, cancellationToken: Token); byte[] before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => PmaProjectStore.Save(path, project,
            new() { CacheDirectory = Path.Combine(directory, "save-cache"), ValidationOptions = new() { MaxPackageBytes = 1 } }, Token));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "save-cache")));
    }

    [Fact]
    public void RepeatedSaveAndSaveAsKeepTheUnreloadedSessionsProjectIdentity()
    {
        using var project = Fixture(out _, out _);
        string first = Path.Combine(directory, "identity.pma"), second = Path.Combine(directory, "identity-copy.pma");
        PmaProjectStore.Save(first, project, cancellationToken: Token); Guid id = ReadId(first);
        PmaProjectStore.Save(first, project, cancellationToken: Token); Assert.Equal(id, ReadId(first));
        PmaProjectStore.Save(second, project, cancellationToken: Token); Assert.Equal(id, ReadId(second));
        static Guid ReadId(string path)
        {
            using var zip = ZipFile.OpenRead(path); using var input = zip.GetEntry("manifest.json")!.Open();
            using var document = JsonDocument.Parse(input); return document.RootElement.GetProperty("projectId").GetGuid();
        }
    }

    [Fact]
    public void PmaCacheModelsCannotBeExportedAsPermanentPmmReferences()
    {
        using var project = Fixture(out _, out _); MakePlain(project);
        string package = Path.Combine(directory, "cache-owner.pma"), destination = Path.Combine(directory, "cache-ref.pmm");
        PmaProjectStore.Save(package, project, cancellationToken: Token);
        string extractedModel;
        using (var loaded = PmaProjectStore.Load(package, Path.Combine(directory, "cache"), LoadModel, new NoPhysics(), cancellationToken: Token))
        {
            extractedModel = loaded.Session.Objects[0].SourcePath;
            var report = PmaPmmCompatibility.Inspect(loaded.Session);
            Assert.Contains(report.Issues, i => i.Code == "temporary-model-source" && i.ObjectId == loaded.Session.Objects[0].Id);
            Assert.Throws<PmaPmmExportException>(() => loaded.Session.ExportPmm());
            Assert.Throws<PmaPmmExportException>(() => loaded.Session.SavePmm(destination, cancellationToken: Token));
            Assert.False(File.Exists(destination));
        }
        Assert.False(File.Exists(extractedModel));
        // The permanent source session remains exportable after the temporary owner is closed.
        Assert.True(PmaPmmCompatibility.Inspect(project).CanExport);
        project.SavePmm(destination, cancellationToken: Token);
    }

    [Theory]
    [InlineData("materials")]
    [InlineData("bones")]
    [InlineData("rigid-bodies")]
    public void SameTopologyLivePmxEditsCannotSilentlyExportTheUneditedSource(string section)
    {
        using var project = Fixture(out var sourceModel, out var id); MakePlain(project);
        if (section == "rigid-bodies") sourceModel = ChangeModel(sourceModel, node => node["RigidBodies"] = JsonSerializer.SerializeToNode(new[]
        {
            new PmxRigidBody { Name = "static", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = Vector3.One, Translation = Vector3.Zero, Rotation = Vector3.Zero,
                Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Static }
        }, ModelJson));
        File.WriteAllBytes(project.Objects[0].SourcePath, PmxWriter.ToBytes(sourceModel));
        var edited = ChangeModel(sourceModel, node =>
        {
            if (section == "materials") node["Materials"]![0]!["Diffuse"]!["X"] = .25f;
            if (section == "bones") node["Bones"]![0]!["Flags"] = (int)PmxBoneFlags.AllowRotate;
            if (section == "rigid-bodies") node["RigidBodies"]![0]!["Mass"] = 7f;
        });
        project.Edit(edit => edit.Motions[0] = edit.Motions[0] with { Model = edited }, Token);
        var report = PmaPmmCompatibility.Inspect(project);
        var issue = Assert.Single(report.Issues, i => i.Code == "model-attributes" && i.ObjectId == id);
        Assert.Contains(section, issue.Message);
        Assert.Throws<PmaPmmExportException>(() => project.ExportPmm());
        Assert.Throws<PmaPmmExportException>(() => project.SavePmm(Path.Combine(directory, "live-edit.pmm"), cancellationToken: Token));
        string package = Path.Combine(directory, "live-edit.pma"); PmaProjectStore.Save(package, project, cancellationToken: Token);
        using var loaded = PmaProjectStore.Load(package, Path.Combine(directory, "cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
        using var input = File.OpenRead(loaded.Session.Objects[0].SourcePath); var reopened = PmxReader.Read(input);
        Assert.Equal(PmxWriter.ToBytes(Copy(edited, edited.Comment, reopened.Textures)), PmxWriter.ToBytes(reopened));
        Assert.Equal(PmxWriter.ToBytes(sourceModel), File.ReadAllBytes(project.Objects[0].SourcePath));
    }

    [Fact]
    public void PerSaveModelOverridesDoNotMutateTheLiveProjectOrFutureSaves()
    {
        using var project = Fixture(out var original, out var id); MakePlain(project);
        string first = Path.Combine(directory, "override.pma"), second = Path.Combine(directory, "unmodified.pma");
        PmaProjectStore.Save(first, project, new() { ModelOverrides = new Dictionary<Guid, PmxModel> { [id] = Copy(original, "save-only") } }, Token);
        Assert.True(PmaPmmCompatibility.Inspect(project).CanExport);
        PmaProjectStore.Save(second, project, cancellationToken: Token);
        using var firstLoad = PmaProjectStore.Load(first, Path.Combine(directory, "cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
        using var secondLoad = PmaProjectStore.Load(second, Path.Combine(directory, "cache"), LoadModel, new NoPhysics(), cancellationToken: Token);
        using var firstInput = File.OpenRead(firstLoad.Session.Objects[0].SourcePath);
        using var secondInput = File.OpenRead(secondLoad.Session.Objects[0].SourcePath);
        Assert.Equal("save-only", PmxReader.Read(firstInput).Comment);
        Assert.Equal(original.Comment, PmxReader.Read(secondInput).Comment);
    }

    private static readonly JsonSerializerOptions ModelJson = new() { IncludeFields = true };
    private static PmxModel ChangeModel(PmxModel model, Action<JsonNode> change)
    {
        var node = JsonSerializer.SerializeToNode(model, ModelJson)!; change(node);
        return node.Deserialize<PmxModel>(ModelJson)!;
    }
    private static void MakePlain(SceneProjectSession project) => project.Edit(edit =>
    {
        foreach (var light in edit.Lighting.Lights.ToArray()) edit.Lighting.Remove(light.Id);
        edit.Settings = new();
    }, Token);

    private static void RewritePackagedFile(string path, string logicalPath, Func<byte[], byte[]> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var manifestEntry = zip.GetEntry("manifest.json")!; JsonNode manifest;
        using (var reader = new StreamReader(manifestEntry.Open())) manifest = JsonNode.Parse(reader.ReadToEnd())!;
        var file = manifest["files"]!.AsArray().Single(f => f!["path"]!.GetValue<string>() == logicalPath)!;
        string oldHash = file["sha256"]!.GetValue<string>(); byte[] bytes;
        using (var input = zip.GetEntry("blobs/" + oldHash)!.Open())
        using (var buffer = new MemoryStream()) { input.CopyTo(buffer); bytes = change(buffer.ToArray()); }
        string newHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        file["sha256"] = newHash; file["length"] = bytes.Length;
        if (!manifest["files"]!.AsArray().Any(f => f!["sha256"]!.GetValue<string>() == oldHash)) zip.GetEntry("blobs/" + oldHash)!.Delete();
        if (zip.GetEntry("blobs/" + newHash) is null)
        { using var output = zip.CreateEntry("blobs/" + newHash).Open(); output.Write(bytes); }
        manifestEntry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(manifest.ToJsonString());
    }

    private SceneProjectSession Fixture(out PmxModel model, out Guid id)
    {
        var template = PmmMotionAdapterTests.Pmx(["root"]);
        model = new PmxModel
        {
            Header = template.Header, Name = "actor", EnglishName = "", Comment = "original", EnglishComment = "",
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex
            { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = ["texture.png"], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One,
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, EdgeColor = Vector4.Zero, EdgeSize = 1, DrawMode = PmxDrawModeFlags.None,
                TextureIndex = 0, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = template.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        File.WriteAllBytes(Path.Combine(directory, "texture.png"), [1, 2, 3, 4]);
        var layout = new SceneCompositionEditor(); var actor = layout.Add("角色", source, ModelRole.Character, Rest(model)); id = actor.Id;
        var lights = new SceneLightingEditor();
        lights.Add(new(Guid.NewGuid(), "one", SceneLightKind.Point, Vector3.UnitY, Quaternion.Identity, Vector3.One));
        lights.Add(new(Guid.NewGuid(), "two", SceneLightKind.Point, Vector3.UnitX, Quaternion.Identity, Vector3.One));
        var motion = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = Vector3.UnitX }] });
        return new(layout, lights, [new(actor.Id, model, motion.Motion, PhysicsEnabled: false, Asset: motion)], new NoPhysics(),
            new() { Width = 2560, Height = 1440 }, initialFrame: 30, cancellationToken: Token);
    }
    private static PmxModel Copy(PmxModel m, string comment, IReadOnlyList<string>? textures = null) => new() { Header = m.Header, Name = m.Name, EnglishName = m.EnglishName,
        Comment = comment, EnglishComment = m.EnglishComment, Vertices = m.Vertices, Faces = m.Faces, Textures = textures ?? m.Textures,
        Materials = m.Materials, Bones = m.Bones, Morphs = m.Morphs, DisplayFrames = m.DisplayFrames, RigidBodies = m.RigidBodies, Joints = m.Joints, SoftBodies = m.SoftBodies };
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
        new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static SceneAnimationModelAsset LoadModel(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); using var input = File.OpenRead(path); var model = PmxReader.Read(input); return new(model, Rest(model)); }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings s, EditableSceneObject o) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings s) => throw new InvalidOperationException();
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
