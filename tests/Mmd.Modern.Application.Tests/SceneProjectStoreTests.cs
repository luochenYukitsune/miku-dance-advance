using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneProjectStoreTests
{
    private static readonly SceneProjectSaveOptions ExternalAssets = new() { CopyModelAssets = false };
    private readonly string directory = Path.Combine(Path.GetTempPath(), "scene-project-" + Guid.NewGuid().ToString("N"));
    private readonly OriginalFrameScene frame = EngineShowcaseScene.Create().Frame;
    private string ProjectPath => Path.Combine(directory, "layout.mmdscene");
    private readonly SceneCompositionEditor composition = new();
    private readonly SceneLightingEditor lighting = new();

    public SceneProjectStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        string asset = Path.Combine(directory, "assets", "角色.pmx");
        File.WriteAllText(asset, "fixture: resource loader supplied by test");
        var first = composition.Add("角色", asset, ModelRole.Character, frame);
        composition.Add("房间", asset, ModelRole.Environment, frame);
        composition.SetTransform(first.Id, new(3, 2, -1), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f), 1.5f);
        // The lighting editor is currently the authoritative source for user-edited purposes.
        lighting.SetModelRole(first.Key, ModelRole.Prop);
        lighting.SetModelRole(composition.Objects[1].Key, ModelRole.Environment, true);
        lighting.Add(new(Guid.NewGuid(), "顶灯", SceneLightKind.Point, new(0, 4, 0), Quaternion.Identity, new(1, .8f, .5f)));
    }

    [Fact]
    public void SaveReopenPreservesOrderIdsGeometryTransformsPurposesLightsAndSelections()
    {
        var originalIds = composition.Objects.Select(item => item.Id).ToArray();
        SceneProjectStore.Save(ProjectPath, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken);
        string json = File.ReadAllText(ProjectPath);
        Assert.Contains("assets/", json.Replace("\\\\", "/"));
        Assert.DoesNotContain(directory.Replace("\\", "\\\\"), json);
        var loaded = SceneProjectStore.Load(ProjectPath, (_, _) => frame, TestContext.Current.CancellationToken);
        Assert.Equal(originalIds, loaded.Composition.Objects.Select(item => item.Id));
        Assert.Equal(composition.SelectedId, loaded.Composition.SelectedId);
        Assert.Equal(lighting.SelectedId, loaded.Lighting.SelectedId);
        Assert.Equal(lighting.Lights, loaded.Lighting.Lights);
        Assert.Equal(ModelRole.Prop, loaded.Composition.Objects[0].Role);
        Assert.Equal(new ModelPurpose(ModelRole.Environment, true), loaded.Lighting.GetModelRole(loaded.Composition.Objects[1].Key));
        Assert.Equal(composition.Frame.Surface.Snapshot.Vertices, loaded.Composition.Frame.Surface.Snapshot.Vertices);
        Assert.False(loaded.Composition.CanUndo); Assert.False(loaded.Lighting.CanUndo);
        loaded.Composition.SetTransform(originalIds[0], Vector3.Zero, Quaternion.Identity, 1);
        loaded.Composition.Undo();
        Assert.Equal(composition.Objects[0].Position, loaded.Composition.Objects[0].Position);
    }

    [Fact]
    public void ReceiverLayoutKeepsStableInstanceIdsAndRejectsLegacyOrUnknownTargetMetadataBeforeLoading()
    {
        var target = composition.Objects[1].Id;
        var light = lighting.Lights[0] with { Receivers = new(SceneLightReceiverMode.Exclude, [target]) };
        lighting.Update(light);
        var json = SceneProjectStore.Serialize(ProjectPath, composition, lighting);
        Assert.Equal(SceneProjectStore.LightingReceiversVersion, JsonNode.Parse(json)!["version"]!.GetValue<int>());
        var reopened = SceneProjectStore.Prepare(json, ProjectPath, (_, _) => frame, TestContext.Current.CancellationToken);
        Assert.Equal(light, Assert.Single(reopened.Lighting.Lights));
        foreach (int version in Enumerable.Range(1, 6))
        {
            var forged = JsonNode.Parse(json)!; forged["version"] = version;
            Assert.Throws<System.Text.Json.JsonException>(() => SceneProjectStore.Prepare(forged.ToJsonString(), ProjectPath,
                (_, _) => throw new InvalidOperationException("Legacy receiver fields must fail before resource loading."), TestContext.Current.CancellationToken));
        }
        var invalid = JsonNode.Parse(json)!; invalid["lights"]![0]!["receivers"]!["objectIds"]![0] = Guid.NewGuid();
        Assert.Throws<System.Text.Json.JsonException>(() => SceneProjectStore.Prepare(invalid.ToJsonString(), ProjectPath,
            (_, _) => throw new InvalidOperationException("Unknown targets must fail before resource loading."), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ParentedLayoutPreservesLocalTransformRangeAndOptionalGroup()
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "跟随组", false, 2);
        lighting.AddGroup(group);
        var light = lighting.Lights[0] with
        {
            Parent = new(composition.Objects[0].Id, 0), GroupId = group.Id,
            Position = new(2, 3, 4), Rotation = Quaternion.CreateFromYawPitchRoll(.2f, .3f, -.4f), Range = 17,
        };
        lighting.Update(light);
        string json = SceneProjectStore.Serialize(ProjectPath, composition, lighting);
        Assert.Equal(SceneProjectStore.LightingParentsVersion, JsonNode.Parse(json)!["version"]!.GetValue<int>());
        var loaded = SceneProjectStore.Prepare(json, ProjectPath, (_, _) => frame, TestContext.Current.CancellationToken);
        Assert.Equal(light, Assert.Single(loaded.Lighting.Lights));
        Assert.Equal(group, Assert.Single(loaded.Lighting.Groups));
        Assert.Equal(lighting.SelectedId, loaded.Lighting.SelectedId);
        lighting.RemoveGroup(group.Id);
        string ungrouped = SceneProjectStore.Serialize(ProjectPath, composition, lighting);
        var ungroupedLoaded = SceneProjectStore.Prepare(ungrouped, ProjectPath, (_, _) => frame, TestContext.Current.CancellationToken);
        Assert.Empty(ungroupedLoaded.Lighting.Groups);
        Assert.Equal(light with { GroupId = null }, Assert.Single(ungroupedLoaded.Lighting.Lights));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public void LegacyLayoutRejectsParentFieldBeforeLoadingAssets(int version, bool nullParent)
    {
        lighting.Update(lighting.Lights[0] with { Parent = new(composition.Objects[0].Id) });
        var json = JsonNode.Parse(SceneProjectStore.Serialize(ProjectPath, composition, lighting))!;
        json["version"] = version;
        if (version < 3) json.AsObject().Remove("groups");
        if (version is 2 or 4) json["portableModels"] = new JsonArray();
        if (nullParent) json["lights"]![0]!["parent"] = null;
        bool called = false;
        var error = Assert.Throws<System.Text.Json.JsonException>(() => SceneProjectStore.Prepare(json.ToJsonString(), ProjectPath,
            (_, _) => { called = true; return frame; }, TestContext.Current.CancellationToken));
        Assert.Contains("version 5 or 6", error.Message);
        Assert.False(called);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayoutRejectsInvalidParentBeforeLoadingAssets(bool invalidBone)
    {
        lighting.Update(lighting.Lights[0] with { Parent = new(composition.Objects[0].Id) });
        var json = JsonNode.Parse(SceneProjectStore.Serialize(ProjectPath, composition, lighting))!;
        if (invalidBone) json["lights"]![0]!["parent"]!["boneIndex"] = -1;
        else json["lights"]![0]!["parent"]!["objectId"] = Guid.Empty;
        bool called = false;
        Assert.Throws<ArgumentException>(() => SceneProjectStore.Prepare(json.ToJsonString(), ProjectPath,
            (_, _) => { called = true; return frame; }, TestContext.Current.CancellationToken));
        Assert.False(called);
    }

    [Fact]
    public void GroupedLayoutPreservesAuthoringMembershipSelectionAndPurposes()
    {
        var group = new SceneLightGroup(Guid.NewGuid(), "舞台", Enabled: false, Intensity: 2.5f);
        var empty = new SceneLightGroup(Guid.NewGuid(), "预留");
        lighting.AddGroup(group); lighting.AddGroup(empty);
        lighting.AssignToGroup(lighting.Lights[0].Id, group.Id);
        string json = SceneProjectStore.Serialize(ProjectPath, composition, lighting);
        Assert.Equal(SceneProjectStore.LightingGroupsVersion, JsonNode.Parse(json)!["version"]!.GetValue<int>());
        var loaded = SceneProjectStore.Prepare(json, ProjectPath, (_, _) => frame, TestContext.Current.CancellationToken);
        Assert.Equal(lighting.Lights, loaded.Lighting.Lights); Assert.Equal(lighting.Groups, loaded.Lighting.Groups);
        Assert.Equal(lighting.SelectedId, loaded.Lighting.SelectedId); Assert.False(loaded.Lighting.CanUndo);
        Assert.Equal(lighting.ModelRoles.OrderBy(pair => pair.Key), loaded.Lighting.ModelRoles.OrderBy(pair => pair.Key));
        Assert.False(Assert.Single(loaded.Lighting.Snapshot.EffectiveLights).Enabled);
        Assert.True(Assert.Single(loaded.Lighting.Lights).Enabled);
        // No group data is silently accepted under the old static schema identity.
        var downgraded = JsonNode.Parse(json)!; downgraded["version"] = 1;
        bool called = false;
        Assert.Throws<System.Text.Json.JsonException>(() => SceneProjectStore.Prepare(downgraded.ToJsonString(), ProjectPath,
            (_, _) => { called = true; return frame; }, TestContext.Current.CancellationToken));
        Assert.False(called);
        var dangling = JsonNode.Parse(json)!; dangling["groups"] = new JsonArray();
        Assert.Throws<ArgumentException>(() => SceneProjectStore.Prepare(dangling.ToJsonString(), ProjectPath,
            (_, _) => { called = true; return frame; }, TestContext.Current.CancellationToken));
        Assert.False(called);
    }

    [Fact]
    public void SaveAsAndMovedDirectoryResolveAssetsFromDocumentInsteadOfWorkingDirectory()
    {
        string nested = Path.Combine(directory, "projects"); Directory.CreateDirectory(nested);
        string second = Path.Combine(nested, "copy.mmdscene");
        SceneProjectStore.Save(second, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken);
        var paths = new List<string>();
        SceneProjectStore.Load(second, (path, _) => { paths.Add(path); return frame; }, TestContext.Current.CancellationToken);
        Assert.All(paths, path => Assert.Equal(composition.Objects[0].SourcePath, path));
        // Copy a complete project tree; the original directory is no longer needed by the loader.
        string moved = Path.Combine(directory, "relocated");
        Directory.CreateDirectory(Path.Combine(moved, "assets"));
        SceneProjectStore.Save(ProjectPath, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken);
        File.Copy(ProjectPath, Path.Combine(moved, "layout.mmdscene"));
        File.Copy(composition.Objects[0].SourcePath, Path.Combine(moved, "assets", "角色.pmx"));
        var relocated = SceneProjectStore.Load(Path.Combine(moved, "layout.mmdscene"), (path, _) =>
        { Assert.StartsWith(moved, path, StringComparison.OrdinalIgnoreCase); return frame; }, TestContext.Current.CancellationToken);
        Assert.StartsWith(moved, relocated.Composition.Objects[0].SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaterResourceFailureAndCancellationLeaveExistingEditorsAndFileUntouched()
    {
        SceneProjectStore.Save(ProjectPath, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken);
        string original = File.ReadAllText(ProjectPath);
        var cached = composition.Frame; int calls = 0;
        var exception = Assert.Throws<SceneProjectAssetException>(() => SceneProjectStore.Load(ProjectPath, (_, _) =>
        { if (++calls == 2) throw new InvalidDataException("bad PMX"); return frame; }, TestContext.Current.CancellationToken));
        Assert.Equal(composition.Objects[1].Id, exception.ObjectId);
        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Same(cached, composition.Frame); Assert.True(composition.CanUndo); Assert.True(lighting.CanUndo);
        using var cancel = new CancellationTokenSource(); calls = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => SceneProjectStore.Load(ProjectPath, (_, _) =>
        { calls++; cancel.Cancel(); return frame; }, cancel.Token));
        Assert.Equal(1, calls); Assert.Equal(original, File.ReadAllText(ProjectPath));
        Assert.Equal(2, composition.Objects.Count); Assert.Single(lighting.Lights);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("format")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("null-object")]
    [InlineData("empty-id")]
    [InlineData("duplicate-id")]
    [InlineData("selection")]
    [InlineData("scale")]
    [InlineData("rotation")]
    [InlineData("position")]
    [InlineData("role")]
    [InlineData("indoor")]
    [InlineData("path")]
    [InlineData("drive-relative")]
    [InlineData("wrong-type")]
    [InlineData("light-selection")]
    [InlineData("light-duplicate")]
    [InlineData("light-invalid")]
    public void InvalidDocumentsFailBeforeResourceLoading(string defect)
    {
        var json = JsonNode.Parse(SceneProjectStore.Serialize(ProjectPath, composition, lighting))!.AsObject();
        var objects = json["objects"]!.AsArray(); var first = objects[0]!.AsObject();
        switch (defect)
        {
            case "version": json["version"] = 99; break;
            case "format": json["format"] = "other"; break;
            case "unknown": first["unknown"] = 1; break;
            case "missing": first.Remove("position"); break;
            case "null-object": objects[0] = null; break;
            case "empty-id": first["id"] = Guid.Empty; break;
            case "duplicate-id": objects[1]!["id"] = first["id"]!.DeepClone(); break;
            case "selection": json["selectedObjectId"] = Guid.NewGuid(); break;
            case "scale": first["scale"] = 0; break;
            case "rotation": first["rotation"] = new JsonArray(0, 0, 0, 2); break;
            case "position": first["position"] = new JsonArray(0, 0); break;
            case "role": first["purpose"]!["role"] = 99; break;
            case "indoor": first["purpose"]!["environmentIndoor"] = true; break;
            case "path": first["sourcePath"] = " "; break;
            case "drive-relative": first["sourcePath"] = "C:relative.pmx"; break;
            case "wrong-type": first["sourcePath"] = "assets/model.vmd"; break;
            case "light-selection": json["selectedLightId"] = Guid.NewGuid(); break;
            case "light-duplicate": json["lights"]!.AsArray().Add(json["lights"]![0]!.DeepClone()); break;
            case "light-invalid": json["lights"]![0]!["range"] = -1; break;
        }
        bool called = false;
        Assert.ThrowsAny<Exception>(() => SceneProjectStore.Prepare(json.ToJsonString(), ProjectPath, (_, _) => { called = true; return frame; }, TestContext.Current.CancellationToken));
        Assert.False(called);
    }

    [Fact]
    public void DuplicatePropertiesAtAnyDepthAreRejected()
    {
        string json = SceneProjectStore.Serialize(ProjectPath, composition, lighting);
        foreach (string duplicate in new[]
        {
            json.Replace("\"version\": 1", "\"version\": 99, \"version\": 1"),
            json.Replace("\"scale\": 1.5", "\"scale\": 0, \"scale\": 1.5"),
            json.Replace("\"range\": 10", "\"range\": -1, \"range\": 10"),
        })
        {
            Assert.NotEqual(json, duplicate);
            Assert.Throws<System.Text.Json.JsonException>(() => SceneProjectStore.Prepare(duplicate, ProjectPath, (_, _) => frame, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void ActiveGestureUnsupportedExtensionAndDynamicInputsCannotOverwriteExistingData()
    {
        SceneProjectStore.Save(ProjectPath, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken);
        byte[] before = File.ReadAllBytes(ProjectPath);
        lighting.BeginEdit(); lighting.Update(lighting.Lights[0] with { Intensity = 2 });
        Assert.Throws<InvalidOperationException>(() => SceneProjectStore.Save(ProjectPath, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken));
        lighting.CancelEdit();
        string modelPath = composition.Objects[0].SourcePath; string assetBefore = File.ReadAllText(modelPath);
        Assert.Throws<ArgumentException>(() => SceneProjectStore.Save(modelPath, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken));
        var surface = frame.Surface;
        var dynamicFrame = new OriginalFrameScene(new(surface.Snapshot.WithDeformedVertices(surface.Snapshot.Vertices.ToArray()), surface.Materials, surface.Profile), frame.Outline, frame.OutlineOptions);
        var animated = new SceneCompositionEditor(); animated.Add("animated", modelPath, ModelRole.Character, dynamicFrame);
        Assert.Throws<NotSupportedException>(() => SceneProjectStore.Save(ProjectPath, animated, lighting, ExternalAssets, TestContext.Current.CancellationToken));
        var rejected = Assert.Throws<SceneProjectAssetException>(() => SceneProjectStore.Load(ProjectPath, (_, _) => dynamicFrame, TestContext.Current.CancellationToken));
        Assert.IsType<NotSupportedException>(rejected.InnerException);
        Assert.Equal(before, File.ReadAllBytes(ProjectPath)); Assert.Equal(assetBefore, File.ReadAllText(modelPath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void NonInvertiblePlacementCannotReplaceAnExistingValidDocument()
    {
        SceneProjectStore.Save(ProjectPath, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken);
        byte[] before = File.ReadAllBytes(ProjectPath);
        var item = composition.Objects[0];
        composition.SetTransform(item.Id, item.Position, item.Rotation, 1e-20f);
        Assert.Throws<ArgumentException>(() => SceneProjectStore.Save(ProjectPath, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllBytes(ProjectPath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("permission")]
    [InlineData("invalid-data")]
    public void ResourceFailuresExposeTheObjectAndResolvedPath(string failure)
    {
        SceneProjectStore.Save(ProjectPath, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken);
        var error = Assert.Throws<SceneProjectAssetException>(() => SceneProjectStore.Load(ProjectPath, (path, _) =>
        {
            if (failure == "permission") throw new UnauthorizedAccessException("fixture access denied");
            if (failure == "invalid-data") throw new InvalidDataException("fixture malformed content");
            using var input = File.OpenRead(path);
            PmxReader.Read(input); // The deliberately invalid fixture must exercise the actual format exception.
            throw new InvalidOperationException("Malformed PMX unexpectedly parsed.");
        }, TestContext.Current.CancellationToken));
        Assert.Equal(composition.Objects[0].Id, error.ObjectId);
        Assert.Equal(composition.Objects[0].SourcePath, error.SourcePath);
        Assert.IsType(failure switch
        {
            "format" => typeof(PmxFormatException), "permission" => typeof(UnauthorizedAccessException), _ => typeof(InvalidDataException),
        }, error.InnerException);
    }

    [Fact]
    public void ActualPmxAdaptersCanRestoreAndComposeWithoutAnyGuiOrDevice()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx");
        byte[] before = File.ReadAllBytes(path);
        OriginalFrameScene Read(string sourcePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = File.OpenRead(sourcePath); var model = PmxReader.Read(input);
            return new(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
                new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
        }
        var original = new SceneCompositionEditor();
        var source = Read(path, TestContext.Current.CancellationToken);
        original.Add("角色一", path, ModelRole.Character, source);
        var second = original.Add("角色二", path, ModelRole.Character, source);
        original.SetTransform(second.Id, new(20, 0, 0), Quaternion.Identity, 2);
        SceneProjectStore.Save(ProjectPath, original, new(), ExternalAssets, TestContext.Current.CancellationToken);
        var restored = SceneProjectStore.Load(ProjectPath, Read, TestContext.Current.CancellationToken);
        Assert.Equal(original.Frame.Surface.Snapshot.Vertices, restored.Composition.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(original.Frame.Surface.Snapshot.Indices, restored.Composition.Frame.Surface.Snapshot.Indices);
        Assert.Equal(original.Objects.Select(item => item.Id), restored.Composition.Objects.Select(item => item.Id));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void MissingAssetAndFailedReplacementPreserveSavedDataAndCleanTemporaryFiles()
    {
        SceneProjectStore.Save(ProjectPath, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken);
        byte[] before = File.ReadAllBytes(ProjectPath);
        var document = JsonNode.Parse(File.ReadAllText(ProjectPath))!;
        document["objects"]![1]!["sourcePath"] = "missing.pmx";
        var error = Assert.Throws<SceneProjectAssetException>(() => SceneProjectStore.Prepare(document.ToJsonString(), ProjectPath,
            (_, _) => frame, TestContext.Current.CancellationToken));
        Assert.Equal(composition.Objects[1].Id, error.ObjectId); Assert.IsType<FileNotFoundException>(error.InnerException);
        File.SetAttributes(ProjectPath, FileAttributes.ReadOnly);
        try { Assert.ThrowsAny<Exception>(() => SceneProjectStore.Save(ProjectPath, composition, lighting, ExternalAssets, TestContext.Current.CancellationToken)); }
        finally { File.SetAttributes(ProjectPath, FileAttributes.Normal); }
        Assert.Equal(before, File.ReadAllBytes(ProjectPath)); Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

}
