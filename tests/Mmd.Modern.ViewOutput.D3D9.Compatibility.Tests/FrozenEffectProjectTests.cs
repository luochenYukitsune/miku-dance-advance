using System.Numerics;
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

public sealed class FrozenEffectProjectTests : IDisposable
{
    private readonly string directory = Path.Combine(Mmd.Modern.TestSupport.TestWorkspace.Root("compat/20261106/frozen557/tests"), Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly Guid Model = new("51851d0c-e1fe-477b-ac55-608caa47b001");
    private static readonly Guid Accessory = new("51851d0c-e1fe-477b-ac55-608caa47b002");
    private static readonly Guid NoEffect = new("51851d0c-e1fe-477b-ac55-608caa47b003");

    [Fact]
    public void FullPlanAndEveryNullableSeedRoundTripWithoutReadingOriginalsOrDuplicatingTreeAssets()
    {
        using var project = Project(); var roots = Roots(); var tree = Tree(roots.Roots);
        var state = D3D9FrozenEffectProject.Capture(roots, tree, Token);
        var baseOptions = D3D9EffectResourceTree.CreateSaveOptions(project, tree,
            D3D9EmmPma.CreateSaveOptions(project, D3D9EmmPma.Capture(roots.ImportPlan, [])));
        var options = D3D9FrozenEffectProject.CreateSaveOptions(project, state, baseOptions, Token);
        Assert.Equal(baseOptions.AdditionalResources.Keys, options.AdditionalResources.Keys);
        Assert.Equal(roots.Roots.Count, state.Origins.Count);
        Assert.Equal(new[] { Accessory, Model, NoEffect }, state.RootSeeds.Select(seed => seed.ObjectId));
        Assert.Null(state.RootSeeds[^1].OriginAbsoluteFxPath);
        var package = new Package(options);
        var emm = D3D9EmmPma.ReadPackage(package)!;
        var restoredTree = D3D9EffectResourceTree.ReadPackage(package, cancellationToken: Token)!;
        int reads = package.Reads;
        var restored = D3D9FrozenEffectProject.ReadPackage(package, emm, restoredTree, Token)!;
        Assert.Equal(reads, package.Reads);
        Assert.Same(emm.ImportPlan, restored.ImportPlan);
        Assert.Equal(SeedRows(state), SeedRows(restored));
        Assert.Equal(RootRows(state), RootRows(restored));
        Assert.Equal(OriginRows(state), OriginRows(restored));
        Assert.Contains(restored.Origins, origin => origin.ObjectId is null && origin.RecordKey == "Pmd9");
        var owner = Assert.Single(restored.Origins, origin => origin.EmmSection == "Effect@Target");
        Assert.Equal(new EmmOwnerReference("Pmd0", 1, "Parent"), owner.SectionOwner);
        // Re-save uses only detached state and the existing graph resource copies.
        var again = D3D9FrozenEffectProject.CreateSaveOptions(project, restored, baseOptions, Token);
        Assert.Equal(options.OptionalExtensions[D3D9FrozenEffectProject.ExtensionName].GetRawText(),
            again.OptionalExtensions[D3D9FrozenEffectProject.ExtensionName].GetRawText());
        Assert.All(baseOptions.AdditionalResources, pair => Assert.Equal(pair.Value.ToArray(), again.AdditionalResources[pair.Key].ToArray()));
    }

    [Fact]
    public void DifferentRootSpellingPreservesProvenanceWhileSharingWindowsFileIdentity()
    {
        using var project = Project();
        var roots = D3D9EffectProjectRoots.Create(Plan("[Effect]\nDefault=none\nPmd0=MAIN.fx"),
            request => request.Object.ObjectId == Model ? @"C:\frozen\main.fx" : null, Token);
        var tree = Tree(roots.Roots);
        Assert.Single(tree.DependencyGraph!.Nodes);
        var state = D3D9FrozenEffectProject.Capture(roots, tree, Token);
        var options = D3D9FrozenEffectProject.CreateSaveOptions(project, state, cancellationToken: Token);
        var restored = D3D9FrozenEffectProject.ReadPackage(new Package(options), D3D9EmmPma.Capture(roots.ImportPlan, []), tree, Token)!;
        Assert.Equal(RootRows(state), RootRows(restored));
        Assert.Equal(new[] { @"C:\frozen\main.fx", @"C:\frozen\MAIN.fx" }, restored.Roots.Select(root => root.OriginAbsolutePath));
    }

    [Fact]
    public void RestoredDecisionsSupplyTheExistingRuntimeProjection()
    {
        using var project = Project();
        var plan = Plan(Effects.Replace("Pmd9=unmatched.fx", "", StringComparison.Ordinal));
        var roots = D3D9EffectProjectRoots.Create(plan,
            request => request.Object.ObjectId == NoEffect ? null : @"C:\frozen\implicit.fx", Token);
        var tree = Tree(roots.Roots); var state = D3D9FrozenEffectProject.Capture(roots, tree, Token);
        var options = D3D9FrozenEffectProject.CreateSaveOptions(project, state, cancellationToken: Token);
        var restored = D3D9FrozenEffectProject.ReadPackage(new Package(options), D3D9EmmPma.Capture(plan, []), tree, Token)!;
        var map = new D3D9EffectResourceMap(tree.DependencyGraph!.Nodes.Select((node, index) =>
            new D3D9EffectResourceBinding(node.OriginAbsolutePath, $@"G:\never-materialized\f{index}.bin")));
        var runtime = D3D9EmmRuntimeConfiguration.Create(restored.ImportPlan, restored.RootSeeds, map);
        Assert.Equal(D3D9EmmRuntimeEffectKind.None, runtime.Objects.Single(value => value.ObjectId == NoEffect).RootSeed.Kind);
        Assert.Equal(@"C:\frozen\implicit.fx", runtime.Objects.Single(value => value.ObjectId == Model).RootSeed.OriginAbsolutePath);
    }

    [Fact]
    public void NoEffectsStillFreezesEveryNullDecisionAndDoesNotRequireAFakeDependencyGraph()
    {
        using var project = Project(); var plan = Plan("[Effect]\nDefault=none\nPmd0=none");
        var roots = D3D9EffectProjectRoots.Create(plan, _ => null, Token);
        var state = D3D9FrozenEffectProject.Capture(roots, null, Token);
        Assert.Empty(state.Roots); Assert.All(state.RootSeeds, seed => Assert.Null(seed.OriginAbsoluteFxPath));
        var emm = D3D9EmmPma.Capture(plan, []);
        var options = D3D9FrozenEffectProject.CreateSaveOptions(project, state,
            D3D9EmmPma.CreateSaveOptions(project, emm), Token);
        var restored = D3D9FrozenEffectProject.ReadPackage(new Package(options), emm, null, Token)!;
        Assert.Equal(SeedRows(state), SeedRows(restored));
        Assert.Null(restored.EffectTree);
        Assert.Throws<InvalidDataException>(() => D3D9FrozenEffectProject.ReadPackage(new Package(options), emm, Tree(Roots().Roots), Token));
    }

    [Fact]
    public void LegacyPackageHasNoFrozenDecisionsAndDoesNotInferOrReadAnySource()
    {
        var package = new Package(new());
        Assert.Null(D3D9FrozenEffectProject.ReadPackage(package, null, null, Token));
        Assert.Equal(0, package.Reads);
    }

    [Theory]
    [InlineData("missing-emm")]
    [InlineData("missing-tree")]
    [InlineData("unrelated-tree")]
    [InlineData("provenance-drift")]
    [InlineData("changed-binding")]
    [InlineData("changed-emm")]
    public void CrossExtensionDriftRejectsBeforeAnyPackageResourceAccess(string damage)
    {
        using var project = Project(); var roots = Roots(); var tree = Tree(roots.Roots);
        var state = D3D9FrozenEffectProject.Capture(roots, tree, Token);
        var options = D3D9FrozenEffectProject.CreateSaveOptions(project, state, cancellationToken: Token);
        D3D9EmmPmaState? emm = D3D9EmmPma.Capture(roots.ImportPlan, []);
        D3D9EffectResourceTree? selectedTree = tree;
        switch (damage)
        {
            case "missing-emm": emm = null; break;
            case "missing-tree": selectedTree = null; break;
            case "unrelated-tree": selectedTree = Tree([new D3D9EffectDependencyRoot(@"C:\frozen\unrelated.fx")]); break;
            case "provenance-drift": selectedTree = Tree(roots.Roots.Select(root => new D3D9EffectDependencyRoot(root.OriginAbsolutePath,
                root.Provenance with { Description = "changed" }))); break;
            case "changed-binding": emm = D3D9EmmPma.Capture(Plan(Effects, Model, "different.pmx"), []); break;
            case "changed-emm": emm = D3D9EmmPma.Capture(Plan(Effects.Replace("override.fx", "different.fx", StringComparison.Ordinal)), []); break;
            default: throw new InvalidOperationException();
        }
        var package = new Package(options);
        Assert.Throws<InvalidDataException>(() => D3D9FrozenEffectProject.ReadPackage(package, emm, selectedTree, Token));
        Assert.Equal(0, package.Reads);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("unknown")]
    [InlineData("missing-required")]
    [InlineData("duplicate-json")]
    [InlineData("seed-duplicate")]
    [InlineData("seed-missing")]
    [InlineData("seed-kind")]
    [InlineData("seed-id")]
    [InlineData("seed-null-to-file")]
    [InlineData("binding-order")]
    [InlineData("root-missing")]
    [InlineData("root-path")]
    [InlineData("root-provenance")]
    [InlineData("origin-index")]
    [InlineData("origin-kind")]
    [InlineData("origin-value")]
    [InlineData("origin-record")]
    [InlineData("origin-object")]
    [InlineData("origin-subset")]
    [InlineData("origin-owner")]
    [InlineData("origin-missing")]
    [InlineData("descriptor-budget")]
    [InlineData("metadata-budget")]
    public void InvalidMetadataCannotForgeAClosureOrDiscardANullSeed(string damage)
    {
        using var project = Project(); var roots = Roots(); var tree = Tree(roots.Roots);
        var state = D3D9FrozenEffectProject.Capture(roots, tree, Token);
        var options = D3D9FrozenEffectProject.CreateSaveOptions(project, state, cancellationToken: Token);
        var node = JsonNode.Parse(options.OptionalExtensions[D3D9FrozenEffectProject.ExtensionName].GetRawText())!.AsObject();
        var seeds = node["rootSeeds"]!.AsArray(); var origins = node["origins"]!.AsArray();
        var named = origins.Single(value => value!["emmSection"]?.GetValue<string>() == "Effect@Target")!;
        switch (damage)
        {
            case "schema": node["schemaVersion"] = 99; break;
            case "unknown": node["portable"] = true; break;
            case "missing-required": seeds[^1]!.AsObject().Remove("originAbsoluteFxPath"); break;
            case "duplicate-json": break;
            case "seed-duplicate": seeds[1] = seeds[0]!.DeepClone(); break;
            case "seed-missing": seeds.RemoveAt(seeds.Count - 1); break;
            case "seed-kind": seeds[0]!["isModel"] = true; break;
            case "seed-id": seeds[0]!["objectId"] = Guid.NewGuid(); break;
            case "seed-null-to-file": seeds[^1]!["originAbsoluteFxPath"] = @"C:\frozen\implicit.fx"; break;
            case "binding-order": (node["bindings"]![0], node["bindings"]![1]) = (node["bindings"]![1]!.DeepClone(), node["bindings"]![0]!.DeepClone()); break;
            case "root-missing": node["roots"]!.AsArray().RemoveAt(0); break;
            case "root-path": node["roots"]![0]!["originAbsolutePath"] = @"C:\frozen\other.fx"; break;
            case "root-provenance": node["roots"]![0]!["provenance"]!["nativeObjectId"] = 42; break;
            case "origin-index": origins[0]!["rootIndex"] = 1; break;
            case "origin-kind": origins[0]!["kind"] = 999; break;
            case "origin-value": named["originalValue"] = "other.fx"; break;
            case "origin-record": named["recordKey"] = "Acs0"; break;
            case "origin-object": named["objectId"] = Accessory; break;
            case "origin-subset": named["subset"] = 3; break;
            case "origin-owner": named["sectionOwner"]!["parentTargetLabel"] = "Other"; break;
            case "origin-missing": origins.RemoveAt(0); break;
            case "descriptor-budget": node["rootSeeds"] = new JsonArray(Enumerable.Range(0, D3D9FrozenEffectProject.MaxDescriptors + 1).Select(_ => (JsonNode?)null).ToArray()); break;
            case "metadata-budget": node["huge"] = new string('a', D3D9FrozenEffectProject.MaxMetadataBytes); break;
            default: throw new InvalidOperationException();
        }
        string raw = node.ToJsonString();
        if (damage == "duplicate-json") raw = raw.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        using var json = JsonDocument.Parse(raw);
        var package = new Package(options with { OptionalExtensions = new Dictionary<string, JsonElement>
            { [D3D9FrozenEffectProject.ExtensionName] = json.RootElement.Clone() } });
        Assert.Throws<InvalidDataException>(() => D3D9FrozenEffectProject.ReadPackage(package,
            D3D9EmmPma.Capture(roots.ImportPlan, []), tree, Token));
        Assert.Equal(0, package.Reads);
    }

    [Fact]
    public void CaptureRequiresTheMatchingCompleteSchemaThreeRootGraph()
    {
        var roots = Roots();
        Assert.Throws<InvalidDataException>(() => D3D9FrozenEffectProject.Capture(roots, null, Token));
        Assert.Throws<InvalidDataException>(() => D3D9FrozenEffectProject.Capture(roots,
            Tree(roots.Roots.Reverse()), Token));
        Assert.Throws<OperationCanceledException>(() => D3D9FrozenEffectProject.Capture(roots, Tree(roots.Roots), new(true)));
        Assert.Empty(typeof(D3D9FrozenEffectProjectState).GetConstructors());
    }

    [Fact]
    public void SaveOptionsRespectSelectionCollisionsAndNeutralBudgetsWithoutAddingBlobs()
    {
        using var project = Project(); var roots = Roots(); var state = D3D9FrozenEffectProject.Capture(roots, Tree(roots.Roots), Token);
        using var excludedJson = JsonDocument.Parse("{}"); var excluded = excludedJson.RootElement; excludedJson.Dispose();
        var caller = new PmaSaveOptions
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { ["org.example.excluded"] = excluded, ["org.example.keep"] = JsonSerializer.SerializeToElement(7) },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { ["render-resources/skip/a.bin"] = new byte[64], ["render-resources/keep.bin"] = new byte[] { 9 } },
            ContentSelection = new(["org.example.excluded"], ["render-resources/skip/"]),
            ValidationOptions = new() { MaxEntryBytes = 1, MaxExpandedBytes = 1, MaxFiles = 1 }
        };
        var options = D3D9FrozenEffectProject.CreateSaveOptions(project, state, caller, Token);
        Assert.Single(options.AdditionalResources); Assert.Equal(2, options.OptionalExtensions.Count);
        Assert.Equal(7, options.OptionalExtensions["org.example.keep"].GetInt32());
        Assert.Throws<ArgumentException>(() => D3D9FrozenEffectProject.CreateSaveOptions(project, state, options, Token));
        Assert.Throws<ArgumentException>(() => D3D9FrozenEffectProject.CreateSaveOptions(project, state, new()
        { OptionalExtensions = new Dictionary<string, JsonElement> { [D3D9FrozenEffectProject.ExtensionName.ToUpperInvariant()] = JsonSerializer.SerializeToElement(0) } }, Token));
        Assert.Throws<InvalidDataException>(() => D3D9FrozenEffectProject.CreateSaveOptions(project, state, new()
        { ContentSelection = new([D3D9FrozenEffectProject.ExtensionName]) }, Token));
        Assert.Throws<InvalidDataException>(() => D3D9FrozenEffectProject.CreateSaveOptions(project, state,
            caller with { ContentSelection = null }, Token));
        Assert.Throws<InvalidDataException>(() => D3D9FrozenEffectProject.CreateSaveOptions(project, state, new()
        { AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { ["render-resources/CON.bin"] = new byte[] { 1 } } }, Token));
    }

    private const string Effects = """
        [Effect]
        Default=common.fx
        Pmd0=override.fx
        Pmd0.show=false
        Pmd0[0]=common.fx
        Pmd0[1]=none
        Acs0=none
        Acs0.show=false
        Acs0[2]=hidden.fx
        Pmd9=unmatched.fx
        [Effect@Target]
        Owner=Pmd0[1]@Parent
        Pmd0=named.fx
        """;
    private static EmmImportPlan Plan(string effects, Guid? modelId = null, string modelPath = "actor.pmx")
    {
        string text = "[Info]\nVersion=3\n[Object]\nPmd0=actor.pmx\nAcs0=stage.x\nPmd9=unmatched.pmx\n" + effects;
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(932)!;
        var document = EmmReader.Read(encoding.GetBytes(text));
        return EmmImportPlan.Create(document, @"D:\frozen\project.emm",
            [new(modelId ?? Model, true, modelPath, 2), new(Accessory, false, "stage.x", 1), new(NoEffect, true, "extra.pmx", 3)], @"C:\frozen");
    }
    private static D3D9EffectProjectRoots Roots() => D3D9EffectProjectRoots.Create(Plan(Effects),
        request => request.Object.ObjectId == NoEffect ? null : @"C:\frozen\alias\..\implicit.fx", Token);
    private static D3D9EffectResourceTree Tree(IEnumerable<D3D9EffectDependencyRoot> roots)
    {
        var result = new D3D9EffectDependencyCollector(new MemorySource(), new Inspector()).Collect(roots, Token);
        Assert.Equal(D3D9EffectDependencyStatus.Complete, result.Status);
        return D3D9EffectResourceTree.FromClosure(result.Closure!, cancellationToken: Token);
    }
    private sealed class MemorySource : ID3D9EffectDependencySource
    {
        public D3D9EffectDependencySnapshot Capture(D3D9EffectDependencySourceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(Encoding.UTF8.GetString(request.Reference.CopyBytes()), [1, 2, 3]);
        }
    }
    private sealed class Inspector : ID3D9EffectDependencyInspector
    {
        public D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
            Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> include, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(D3D9EffectDependencyStatus.Complete, new("frozen-test-host", new string('a', 64), 64, 932),
                [new("effect-mip-off", 0x10c0, [new("_INDEX", "PSIZE15")]),
                 new("effect-mip-on", 0x10c0, [new("_INDEX", "PSIZE15"), new("MME_MIPMAP", "")])], []);
        }
    }
    private sealed class Package(PmaSaveOptions options) : IPmaPackageResources
    {
        public int Reads { get; private set; }
        public string Format => PmcProjectStore.Format;
        public Guid ProjectId => Guid.Empty;
        public IReadOnlyDictionary<string, JsonElement> OptionalExtensions => options.OptionalExtensions;
        public IReadOnlyList<string> ResourcePaths { get; } = options.AdditionalResources.Keys.ToArray();
        public byte[] ReadResource(string path, long maxBytes = long.MaxValue)
        {
            Reads++; var bytes = options.AdditionalResources[path];
            if (bytes.Length > maxBytes) throw new InvalidDataException(); return bytes.ToArray();
        }
    }
    private static IEnumerable<object> SeedRows(D3D9FrozenEffectProjectState state)
        => state.RootSeeds.Select(seed => (object)(seed.ObjectId, seed.IsModel, seed.OriginAbsoluteFxPath));
    private static IEnumerable<object> RootRows(D3D9FrozenEffectProjectState state)
        => state.Roots.Select(root => (object)(root.OriginAbsolutePath, root.Provenance));
    private static IEnumerable<object> OriginRows(D3D9FrozenEffectProjectState state)
        => state.Origins.Select(origin => (object)(origin.RootIndex, origin.Kind, origin.OriginalValue, origin.OriginAbsolutePath,
            origin.ObjectId, origin.IsModel, origin.RecordKey, origin.EmmSection, origin.Subset, origin.SectionOwner));
    private SceneProjectSession Project()
    {
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "frozen roots", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
            { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        var frame = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1, Eligible: false), materials: PmxOriginalFrameAdapter.FromMaterials(model));
        var composition = new SceneCompositionEditor(); composition.Add("actor", path, ModelRole.Unspecified, frame);
        return new(composition, new(), [], new NoPhysics(), cancellationToken: Token);
    }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject obj) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath((Mmd.Modern.TestSupport.TestWorkspace.Root("compat/20261106/frozen557/tests") + "/")), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
