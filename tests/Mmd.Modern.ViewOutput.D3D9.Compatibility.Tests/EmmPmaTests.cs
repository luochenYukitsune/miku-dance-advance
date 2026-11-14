using System.Collections;
using System.Buffers;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class EmmPmaTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "emm-pma-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Extension = "org.mda.traditional-emm";
    private const string Root = "render-resources/traditional/emm/";
    public EmmPmaTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void PreparedClassicSelectionSkipsExcludedCallerBytesAndJsonBeforeEmmCopies()
    {
        using var project = Project();
        using var unreadable = new ExcludedMemory();
        using var json = JsonDocument.Parse("{\"excluded\":true}");
        JsonElement removed = json.RootElement; json.Dispose();
        const string path = "render-resources/material-bindings/unused.bin";
        const string name = "org.mda.model-material-bindings";
        var caller = new PmaSaveOptions
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { [name] = removed },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>>
            { [path] = unreadable.Memory, ["render-resources/keep.bin"] = new byte[] { 8 } },
            ValidationOptions = new() { MaxEntryBytes = 8192, MaxExpandedBytes = 65536 }
        };
        var prepared = D3D9ClassicProjectStore.CreateSaveOptions(caller);
        var options = SaveOptions(project, Capture(Plan(project), (@"Z:\absent\main.fx", new byte[] { 1, 2 })), prepared);
        Assert.DoesNotContain(path, options.AdditionalResources.Keys);
        Assert.DoesNotContain(name, options.OptionalExtensions.Keys);
        Assert.Equal(new byte[] { 8 }, options.AdditionalResources["render-resources/keep.bin"].ToArray());
        Assert.Contains(path, caller.AdditionalResources.Keys);
        string package = Path.Combine(directory, "selected.pmc");
        D3D9ClassicProjectStore.Save(package, project, options, Token);
        using var loaded = D3D9ClassicProjectStore.Load(package, Path.Combine(directory, "selected-cache"),
            (modelPath, token) => { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(modelPath); return new(model, Rest(model)); },
            new NoPhysics(), cancellationToken: Token);
        Assert.NotNull(Read(loaded));
        Assert.DoesNotContain(path, loaded.ResourcePaths);
    }

    [Fact]
    public void CompleteRawDocumentInstancesNamedOwnersAndCallerAssetsSurviveNeutralRoundTripAndResave()
    {
        using var project = Project(); var plan = Plan(project);
        byte[] asset = [1, 2, 3, 4]; string original = @"Z:\未存在\effects\include\共通.fx";
        var state = Capture(plan, (original, asset));
        var options = SaveOptions(project, state, new()
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { ["org.example.unknown"] = JsonSerializer.SerializeToElement(new { retained = true }) },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { ["render-resources/unrelated.bin"] = new byte[] { 8 } }
        });
        string package = Package("first"); PmaProjectStore.Save(package, project, options, Token);
        File.Delete(project.Objects[0].SourcePath);
        string resaved = Package("resaved");
        using (var loaded = Load(package))
        {
            Assert.Contains(loaded.Diagnostics, value => value.Contains(Extension, StringComparison.Ordinal));
            Assert.True(loaded.OptionalExtensions["org.example.unknown"].GetProperty("retained").GetBoolean());
            var restored = Read(loaded); Assert.NotNull(restored);
            var again = Get<EmmImportPlan>(restored, "ImportPlan");
            Assert.Equal(plan.Document.CopySourceBytes(), again.Document.CopySourceBytes());
            Assert.Equal(plan.PathContext.OriginalEmmPath, again.PathContext.OriginalEmmPath);
            Assert.Equal(plan.PathContext.OriginalWorkingDirectory, again.PathContext.OriginalWorkingDirectory);
            Assert.Equal(plan.LiveObjects.Select(Binding), again.LiveObjects.Select(Binding));
            Assert.Equal(plan.Matches, again.Matches); Assert.Equal(plan.Diagnostics, again.Diagnostics);
            Assert.Equal(5, again.LiveObjects.Count); Assert.Equal(4, again.Matches.Count);
            Assert.Equal(2, again.Document.EffectSections.Count(value => !value.IsDefault));
            Assert.Equal("Layer", again.Document.EffectSections[^1].Owner!.ParentTargetLabel);
            Assert.False(again.Document.EffectSections[0].Entries[0].Shown);
            Assert.False(again.Document.EffectSections[1].Entries[0].SubsetShown[1]);
            Assert.Contains(again.Document.Rows, row => row.Key == "Unrecognized");
            var restoredAsset = Assert.Single(Get<IEnumerable>(restored, "Assets").Cast<object>());
            Assert.Equal(original, Get<string>(restoredAsset, "OriginalAbsolutePath"));
            Assert.Equal(asset, Copy(restoredAsset));
            PmaProjectStore.Save(resaved, loaded.Session, cancellationToken: Token);
        }
        using var reopened = Load(resaved);
        Assert.Equal(plan.Document.CopySourceBytes(), Get<EmmImportPlan>(Read(reopened)!, "ImportPlan").Document.CopySourceBytes());
        Assert.Contains("render-resources/unrelated.bin", reopened.ResourcePaths);
    }

    [Fact]
    public void ClassicArchiveRetainsOpaqueEmmPayloadAndCallerDependenciesAcrossDefaultResave()
    {
        using var project = Project(); var plan = Plan(project); byte[] bytes = [3, 1, 4, 1, 5];
        string original = @"Z:\not-present\effects\include\shared.fx";
        var options = SaveOptions(project, Capture(plan, (original, bytes)), new()
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { ["org.example.retained"] = JsonSerializer.SerializeToElement(new { value = 7 }) },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { ["render-resources/unrelated.bin"] = new byte[] { 9 } }
        });
        var codec = typeof(PmaProjectStore).Assembly.GetType("Mmd.Modern.Application.PmcProjectStore");
        Assert.NotNull(codec);
        string first = Path.Combine(directory, "classic.pmc"), second = Path.Combine(directory, "classic-resaved.pmc");
        SaveClassic(first, project, options);
        File.Delete(project.Objects[0].SourcePath);
        using (var loaded = LoadClassic(first))
        {
            Assert.Equal("mmd-classic-pmc", Get<string>(loaded, "Format"));
            var state = Read(loaded); Assert.NotNull(state);
            Assert.Equal(plan.Document.CopySourceBytes(), Get<EmmDocument>(state, "Document").CopySourceBytes());
            Assert.Equal(plan.Matches, Get<EmmImportPlan>(state, "ImportPlan").Matches);
            SaveClassic(second, loaded.Session, null);
        }
        using var reopened = LoadClassic(second);
        Assert.Equal("mmd-classic-pmc", Get<string>(reopened, "Format"));
        var restored = Read(reopened); Assert.NotNull(restored);
        var asset = Assert.Single(Get<IEnumerable>(restored, "Assets").Cast<object>());
        Assert.Equal(original, Get<string>(asset, "OriginalAbsolutePath")); Assert.Equal(bytes, Copy(asset));
        Assert.Equal(plan.LiveObjects.Select(Binding), Get<EmmImportPlan>(restored, "ImportPlan").LiveObjects.Select(Binding));
        Assert.Equal(7, reopened.OptionalExtensions["org.example.retained"].GetProperty("value").GetInt32());
        Assert.Equal(new byte[] { 9 }, reopened.ReadResource("render-resources/unrelated.bin"));

        void SaveClassic(string path, SceneProjectSession session, PmaSaveOptions? saveOptions)
            => Invoke(() => codec.GetMethod("Save")!.Invoke(null, [path, session, saveOptions, Token]));
        PmaLoadedProject LoadClassic(string path)
        {
            Func<string, CancellationToken, SceneAnimationModelAsset> loader = (modelPath, token) =>
            { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(modelPath); return new(model, Rest(model)); };
            return (PmaLoadedProject)Invoke(() => codec.GetMethod("Load")!.Invoke(null,
                [path, Path.Combine(directory, "cache"), loader, new NoPhysics(), null, Token]))!;
        }
    }

    [Fact]
    public void CaptureAndReturnedByteCopiesAreDetachedWithoutReadingOriginalPaths()
    {
        using var project = Project(); var plan = Plan(project); byte[] input = [7, 8, 9];
        var state = Capture(plan, (@"Z:\does-not-exist\相対\effect.fx", input)); input[0] = 99;
        var asset = Assert.Single(Get<IEnumerable>(state, "Assets").Cast<object>());
        byte[] copy = Copy(asset); copy[1] = 99;
        Assert.Equal(new byte[] { 7, 8, 9 }, Copy(asset));
        var document = Get<EmmDocument>(state, "Document"); byte[] raw = document.CopySourceBytes(); raw[0] = 99;
        Assert.Equal(plan.Document.CopySourceBytes(), document.CopySourceBytes());
        var options = SaveOptions(project, state); var owned = options.AdditionalResources.Single(value => value.Key.EndsWith(".bin", StringComparison.Ordinal));
        Assert.Equal(new byte[] { 7, 8, 9 }, owned.Value.ToArray());
    }

    [Theory]
    [InlineData("relative.fx")]
    [InlineData("C:drive-relative.fx")]
    [InlineData("C:\\folder\\")]
    [InlineData("C:\\folder\\.")]
    [InlineData("C:\\folder\\..")]
    public void AssetsRequireAbsoluteOriginalFilePaths(string path)
    {
        using var project = Project();
        Assert.Throws<ArgumentException>(() => Capture(Plan(project), (path, new byte[] { 1 })));
    }

    [Fact]
    public void RepeatedAssetPathsRejectCaseAndLexicalAliases()
    {
        using var project = Project();
        Assert.Throws<ArgumentException>(() => Capture(Plan(project),
            (@"C:\tree\include\file.fx", new byte[] { 1 }), (@"c:\TREE\include\.\file.fx", new byte[] { 2 })));
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("expanded")]
    [InlineData("files")]
    public void CallerPmaResourceBudgetsRejectDuringSaveOptionsPreparation(string kind)
    {
        using var project = Project(); var state = Capture(Plan(project), (@"C:\effects\main.fx", new byte[33]));
        var limits = kind switch
        {
            "entry" => new PmaLoadOptions { MaxEntryBytes = 32 },
            "expanded" => new PmaLoadOptions { MaxExpandedBytes = 32 },
            _ => new PmaLoadOptions { MaxFiles = 1 }
        };
        Assert.Throws<InvalidDataException>(() => SaveOptions(project, state, new() { ValidationOptions = limits }));
    }

    [Fact]
    public void ResourceAndExtensionCollisionsRejectWithoutOverwritingCallerInputs()
    {
        using var project = Project(); var state = Capture(Plan(project)); var options = SaveOptions(project, state);
        string existing = Assert.Single(options.AdditionalResources).Key;
        Assert.Throws<ArgumentException>(() => SaveOptions(project, state, new()
        { AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { [existing.ToUpperInvariant()] = new byte[] { 42 } } }));
        Assert.Throws<ArgumentException>(() => SaveOptions(project, state, new()
        { OptionalExtensions = new Dictionary<string, JsonElement> { [Extension] = JsonSerializer.SerializeToElement(new { existing = true }) } }));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("unknown")]
    [InlineData("missing-field")]
    [InlineData("duplicate")]
    [InlineData("depth")]
    [InlineData("sha")]
    [InlineData("length")]
    [InlineData("missing-blob")]
    [InlineData("match-id")]
    [InlineData("match-reason")]
    [InlineData("asset-path")]
    [InlineData("source-path")]
    [InlineData("ansi")]
    [InlineData("binding-field")]
    [InlineData("binding-order")]
    [InlineData("binding-duplicate-id")]
    [InlineData("descriptor-count")]
    [InlineData("asset-original-path")]
    [InlineData("raw-encoding")]
    public void ExplicitAdapterReadRejectsMalformedMetadataResourcesAndForgedMatches(string damage)
    {
        using var project = Project(); var plan = Plan(project);
        var options = SaveOptions(project, Capture(plan, (@"C:\effects\main.fx", new byte[] { 4, 5, 6 })));
        var node = JsonNode.Parse(options.OptionalExtensions[Extension].GetRawText())!.AsObject();
        var resources = options.AdditionalResources.ToDictionary(value => value.Key, value => value.Value);
        switch (damage)
        {
            case "schema": node["schemaVersion"] = 99; break;
            case "unknown": node["unrecognized"] = true; break;
            case "missing-field": node.Remove("ansiCodePage"); break;
            case "depth": node["unrecognized"] = JsonNode.Parse(new string('[', 30) + "0" + new string(']', 30)); break;
            case "sha": node["source"]!["sha256"] = new string('0', 64); break;
            case "length": node["assets"]![0]!["resource"]!["byteLength"] = 2; break;
            case "missing-blob": resources.Remove(node["assets"]![0]!["resource"]!["path"]!.GetValue<string>()); break;
            case "match-id": node["matches"]![0]!["objectId"] = Guid.NewGuid().ToString(); break;
            case "match-reason": node["matches"]![0]!["reason"] = 999; break;
            case "asset-path": node["assets"]![0]!["resource"]!["path"] = "render-resources/unrelated.bin"; break;
            case "source-path": node["source"]!["path"] = Root + "unexpected.emm"; break;
            case "ansi": node["ansiCodePage"] = 65001; break;
            case "binding-field": node["bindings"]![0]!.AsObject().Remove("isModel"); break;
            case "binding-order":
                var rows = node["bindings"]!.AsArray();
                var first = rows[0]!.DeepClone(); rows[0] = rows[1]!.DeepClone(); rows[1] = first;
                break;
            case "binding-duplicate-id": node["bindings"]![1]!["objectId"] = node["bindings"]![0]!["objectId"]!.DeepClone(); break;
            case "descriptor-count": node["assets"] = new JsonArray(new JsonNode?[100_001]); break;
            case "asset-original-path": node["assets"]![0]!["originalAbsolutePath"] = "relative.fx"; break;
            case "raw-encoding":
                byte[] invalid = [0xff, 0xfe, 0]; resources[Root + "raw.emm"] = invalid;
                node["source"]!["byteLength"] = invalid.Length;
                node["source"]!["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(invalid));
                break;
        }
        string json = node.ToJsonString();
        if (damage == "duplicate") json = json.Insert(1, "\"schemaVersion\":1,");
        using var metadata = JsonDocument.Parse(json);
        string path = Package(damage);
        if (damage == "duplicate")
        {
            Assert.Throws<JsonException>(() => PmaProjectStore.Save(path, project, options with
            {
                OptionalExtensions = new Dictionary<string, JsonElement> { [Extension] = metadata.RootElement.Clone() },
                AdditionalResources = resources
            }, Token));
            Assert.False(File.Exists(path));
            return;
        }
        PmaProjectStore.Save(path, project, options with
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { [Extension] = metadata.RootElement.Clone() },
            AdditionalResources = resources
        }, Token);
        using var loaded = Load(path);
        var frame = loaded.Session.Frame;
        Assert.Throws<InvalidDataException>(() => Read(loaded));
        Assert.Same(frame, loaded.Session.Frame);
    }

    [Fact]
    public void MetadataByteBudgetAppliesEvenWhenTheNeutralArchiveAllowsLargerManifest()
    {
        using var project = Project(); var options = SaveOptions(project, Capture(Plan(project)));
        var node = JsonNode.Parse(options.OptionalExtensions[Extension].GetRawText())!.AsObject();
        node["padding"] = new string('a', 16 * 1024 * 1024);
        using var metadata = JsonDocument.Parse(node.ToJsonString());
        var limits = new PmaLoadOptions { MaxManifestBytes = 18 * 1024 * 1024 };
        string path = Package("metadata-byte-budget");
        PmaProjectStore.Save(path, project, options with
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { [Extension] = metadata.RootElement.Clone() },
            ValidationOptions = limits
        }, Token);
        using var loaded = Load(path, limits);
        Assert.Throws<InvalidDataException>(() => Read(loaded));
    }

    [Fact]
    public void AbsentExtensionReturnsNullAndUnsupportedEmmVersionIsRetainedAsData()
    {
        using var project = Project(); string absent = Package("absent");
        PmaProjectStore.Save(absent, project, cancellationToken: Token);
        using (var loaded = Load(absent)) Assert.Null(Read(loaded));
        var plan = Plan(project, version: 77); string retained = Package("unsupported-emm");
        PmaProjectStore.Save(retained, project, SaveOptions(project, Capture(plan)), Token);
        using var reopened = Load(retained);
        var restored = Get<EmmImportPlan>(Read(reopened)!, "ImportPlan");
        Assert.Equal(77, restored.Document.Version); Assert.False(restored.Document.IsSupportedVersion);
        Assert.Equal(plan.Diagnostics, restored.Diagnostics);
    }

    private static (Guid, bool, string, int) Binding(EmmObjectBinding value) => (value.ObjectId, value.IsModel, value.SourcePath, value.RenderOrder);
    private static T Get<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
    private static byte[] Copy(object instance) => (byte[])instance.GetType().GetMethod("CopySourceBytes")!.Invoke(instance, null)!;
    private static Type Codec()
    {
        var type = typeof(D3D9SurfaceObjectBinding).Assembly.GetType("Mmd.Modern.ViewOutput.D3D9.Compatibility.D3D9EmmPma");
        Assert.NotNull(type); return type;
    }
    private static object Capture(EmmImportPlan plan, params (string Path, byte[] Bytes)[] assets)
    {
        var codec = Codec(); var assetType = codec.Assembly.GetType("Mmd.Modern.ViewOutput.D3D9.Compatibility.D3D9EmmPmaAsset")!;
        var array = Array.CreateInstance(assetType, assets.Length);
        for (int i = 0; i < assets.Length; i++)
            array.SetValue(Invoke(() => Activator.CreateInstance(assetType, assets[i].Path, new ReadOnlyMemory<byte>(assets[i].Bytes)))!, i);
        return Invoke(() => codec.GetMethod("Capture")!.Invoke(null, [plan, array]))!;
    }
    private static PmaSaveOptions SaveOptions(SceneProjectSession project, object state, PmaSaveOptions? options = null)
        => (PmaSaveOptions)Invoke(() => Codec().GetMethod("CreateSaveOptions")!.Invoke(null, [project, state, options]))!;
    private static object? Read(PmaLoadedProject project) => Invoke(() => Codec().GetMethod("Read")!.Invoke(null, [project]));
    private static object? Invoke(Func<object?> call)
    {
        try { return call(); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private EmmImportPlan Plan(SceneProjectSession project, int version = 3)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text = $"[Info]\r\nVersion={version}\r\n[Object]\r\nPmd0=models\\角色.pmx\r\nPmd1=models\\角色.pmx\r\nAcs0=effects\\carrier.x\r\nAcs1=effects\\carrier.x\r\n[Effect]\r\nDefault=main_default\r\nPmd0.show=false\r\nPmd0=effects\\main.fx\r\nUnrecognized=retain-me\r\n[Effect@Layer]\r\nOwner=Acs0\r\nPmd0[1]=effects\\sub.fx\r\nPmd0[1].show=false\r\n[Effect@Layer(1)]\r\nOwner=Acs1[0]@Layer\r\nPmd1=none\r\n";
        var document = EmmReader.Read(Encoding.GetEncoding(936).GetBytes(text));
        return EmmImportPlan.Create(document, @"C:\original\工程\project.emm",
        [new(project.Objects[0].Id, true, @"models\角色.pmx", 2), new(project.Objects[1].Id, true, @"models\角色.pmx", 1),
            new(Guid.NewGuid(), false, @"effects\carrier.x", 3), new(Guid.NewGuid(), false, @"effects\carrier.x", 4),
            new(Guid.NewGuid(), true, @"unknown\retained.pmx", 5)], @"C:\original\工程");
    }
    private SceneProjectSession Project()
    {
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "generic fixture", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
            { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor();
        composition.Add("first", source, ModelRole.Environment, Rest(model)); composition.Add("second", source, ModelRole.Environment, Rest(model));
        return new(composition, new(), [], new NoPhysics(), cancellationToken: Token);
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model),
        new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1, Eligible: false),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private string Package(string name) => Path.Combine(directory, name + ".pma");
    private PmaLoadedProject Load(string path, PmaLoadOptions? options = null) => PmaProjectStore.Load(path, Path.Combine(directory, "cache"),
        (modelPath, token) => { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(modelPath); return new(model, Rest(model)); },
        new NoPhysics(), options, cancellationToken: Token);
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject obj) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
    private sealed class ExcludedMemory : MemoryManager<byte>
    {
        public override Memory<byte> Memory => CreateMemory(64 * 1024);
        public override Span<byte> GetSpan() => throw new InvalidOperationException("Excluded content was read before capture.");
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
