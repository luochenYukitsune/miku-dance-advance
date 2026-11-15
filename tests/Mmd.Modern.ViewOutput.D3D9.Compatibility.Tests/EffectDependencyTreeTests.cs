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

public sealed class EffectDependencyTreeTests : IDisposable
{
    private const string Main = @"C:\unavailable-source\main.fx";
    private const string Other = @"D:\unavailable-source\same.fx";
    private const string Include = @"C:\unavailable-source\ソ.inc";
    private const string AbsoluteInclude = @"D:\shared-source\absolute.inc";
    private const string Image = @"Q:\unavailable-assets\色.png";
    private readonly string directory = Path.Combine(Mmd.Modern.TestSupport.TestWorkspace.Root("compat/20261106/dependency549/tests"), Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void CrossDriveOriginsWithSameDigestHaveStableDistinctLocalIdentities()
    {
        var result = Collect(); var closure = Assert.IsType<D3D9EffectDependencyClosure>(result.Closure);
        var tree = D3D9EffectResourceTree.FromClosure(closure, diagnostics: result.Diagnostics, cancellationToken: Token);
        var graph = Assert.IsType<D3D9EffectResourceTreeDependencyGraph>(tree.DependencyGraph);
        Assert.Null(tree.OriginAbsoluteRoot);
        Assert.Equal(5, graph.Nodes.Count);
        Assert.Equal(2, tree.RequiredRelativeFiles.Count);
        var first = Assert.Single(graph.Nodes, node => node.OriginAbsolutePath == Main);
        var second = Assert.Single(graph.Nodes, node => node.OriginAbsolutePath == Other);
        Assert.NotEqual(first.ResourceId, second.ResourceId);
        Assert.NotEqual(first.RelativePath, second.RelativePath);
        Assert.Equal(tree.CopyFileBytes(first.RelativePath), tree.CopyFileBytes(second.RelativePath));
        Assert.Equal(tree.Files.Single(file => file.RelativePath == first.RelativePath).Sha256,
            tree.Files.Single(file => file.RelativePath == second.RelativePath).Sha256);
        Assert.All(tree.Files, file => Assert.Matches("^f[0-9]{5}\\.bin$", file.RelativePath));
        Assert.Equal(closure.Roots.Select(root => root.Provenance), graph.Roots.Select(root => root.Provenance));
        var fresh = D3D9EffectResourceTree.FromClosure(Collect().Closure!, cancellationToken: Token);
        Assert.Equal(graph.Nodes, fresh.DependencyGraph!.Nodes);
        byte[] exposed = tree.CopyFileBytes(first.RelativePath); exposed[0] = 0;
        Assert.Equal(Encoding.UTF8.GetBytes("identical fx"), tree.CopyFileBytes(first.RelativePath));
    }

    [Fact]
    public void SchemaThreeRoundTripsFullGraphRawBytesProfilesProvenanceAndDiagnosticsWithoutSourceAccess()
    {
        using var project = Project(); var result = Collect();
        var tree = D3D9EffectResourceTree.FromClosure(result.Closure!, diagnostics: result.Diagnostics, cancellationToken: Token);
        var options = D3D9EffectResourceTree.CreateSaveOptions(project, tree);
        var package = Package(options);
        Assert.Equal(3, package.Metadata.GetProperty("schemaVersion").GetInt32());
        var restored = Assert.IsType<D3D9EffectResourceTree>(D3D9EffectResourceTree.ReadPackage(package, cancellationToken: Token));
        Assert.Equal(tree.Files.Count, package.Reads);
        AssertGraphEqual(tree.DependencyGraph!, restored.DependencyGraph!);
        foreach (var file in tree.Files) Assert.Equal(tree.CopyFileBytes(file.RelativePath), restored.CopyFileBytes(file.RelativePath));
        // Mutating a returned reference byte copy cannot alter the re-save graph or source snapshot.
        restored.DependencyGraph!.Edges.First(edge => edge.Kind == D3D9EffectDependencyEdgeKind.IncludeLocal).Reference.CopyBytes()[0] ^= 0xff;
        var resaved = D3D9EffectResourceTree.CreateSaveOptions(project, restored);
        Assert.Equal(options.OptionalExtensions[D3D9EffectResourceTree.ExtensionName].GetRawText(),
            resaved.OptionalExtensions[D3D9EffectResourceTree.ExtensionName].GetRawText());
        Assert.All(options.AdditionalResources, resource => Assert.Equal(resource.Value.ToArray(), resaved.AdditionalResources[resource.Key].ToArray()));
    }

    [Theory]
    [InlineData("missing-resource")]
    [InlineData("unknown-edge-target")]
    [InlineData("unknown-edge-profile")]
    [InlineData("include-parent")]
    [InlineData("missing-root-edge")]
    [InlineData("required-root")]
    [InlineData("bad-main-profile")]
    [InlineData("bad-function-profile")]
    [InlineData("duplicate-origin")]
    [InlineData("invalid-reference")]
    [InlineData("failed-diagnostic")]
    [InlineData("unknown-field")]
    public void InvalidGraphRejectsBeforeReadingAnyResource(string damage)
    {
        using var project = Project(); var result = Collect();
        var options = D3D9EffectResourceTree.CreateSaveOptions(project,
            D3D9EffectResourceTree.FromClosure(result.Closure!, diagnostics: result.Diagnostics, cancellationToken: Token));
        var metadata = JsonNode.Parse(options.OptionalExtensions[D3D9EffectResourceTree.ExtensionName].GetRawText())!.AsObject();
        var resources = options.AdditionalResources.ToDictionary(pair => pair.Key, pair => pair.Value);
        var edges = metadata["edges"]!.AsArray();
        JsonObject Edge(D3D9EffectDependencyEdgeKind kind) => edges.First(edge => edge!["kind"]!.GetValue<int>() == (int)kind)!.AsObject();
        switch (damage)
        {
            case "missing-resource": resources.Remove(resources.Keys.First()); break;
            case "unknown-edge-target": Edge(D3D9EffectDependencyEdgeKind.ResourceName)["targetResourceId"] = "missing"; break;
            case "unknown-edge-profile": Edge(D3D9EffectDependencyEdgeKind.IncludeLocal)["profileId"] = "missing"; break;
            case "include-parent": Edge(D3D9EffectDependencyEdgeKind.IncludeLocal)["parentOriginAbsolutePath"] = Image; break;
            case "missing-root-edge": edges.RemoveAt(0); break;
            case "required-root": metadata["requiredRelativeFiles"]!.AsArray().RemoveAt(0); break;
            case "bad-main-profile": metadata["profiles"]![0]!["macros"]![0]!["value"] = "wrong"; break;
            case "bad-function-profile": metadata["profiles"]!.AsArray().Last()!["flags"] = 0x1000; break;
            case "duplicate-origin": metadata["nodes"]![1]!["originAbsolutePath"] = metadata["nodes"]![0]!["originAbsolutePath"]!.GetValue<string>(); break;
            case "invalid-reference": Edge(D3D9EffectDependencyEdgeKind.IncludeLocal)["reference"]!["codePage"] = 0; break;
            case "failed-diagnostic": metadata["diagnostics"]![0]!["hResult"] = -1; break;
            case "unknown-field": metadata["graphIsActuallyPortable"] = true; break;
            default: throw new InvalidOperationException();
        }
        var package = new TreePackage(JsonSerializer.SerializeToElement(metadata), resources);
        Assert.Throws<InvalidDataException>(() => D3D9EffectResourceTree.ReadPackage(package, cancellationToken: Token));
        Assert.Equal(0, package.Reads);
    }

    [Fact]
    public void ByteLengthShaAndAdapterBudgetsAreEnforcedBeforeAnyMaterialization()
    {
        using var project = Project(); var result = Collect();
        var tree = D3D9EffectResourceTree.FromClosure(result.Closure!, cancellationToken: Token);
        var options = D3D9EffectResourceTree.CreateSaveOptions(project, tree);
        var resources = options.AdditionalResources.ToDictionary(pair => pair.Key, pair => pair.Value);
        string key = resources.Keys.First(); resources[key] = new byte[resources[key].Length];
        var badBytes = new TreePackage(options.OptionalExtensions[D3D9EffectResourceTree.ExtensionName], resources);
        Assert.Throws<InvalidDataException>(() => D3D9EffectResourceTree.ReadPackage(badBytes, cancellationToken: Token));
        Assert.Equal(1, badBytes.Reads);
        var budget = Package(options);
        Assert.Throws<InvalidDataException>(() => D3D9EffectResourceTree.ReadPackage(budget, new() { MaxPathBytes = 1 }, Token));
        Assert.Equal(0, budget.Reads);
        Assert.Throws<InvalidDataException>(() => D3D9EffectResourceTree.FromClosure(result.Closure!, new() { MaxFiles = 1 }, cancellationToken: Token));
        Assert.Throws<OperationCanceledException>(() => D3D9EffectResourceTree.FromClosure(result.Closure!, cancellationToken: new(true)));
    }

    [Fact]
    public void MaterializationUsesExplicitCrossDriveMapAndOwnsEveryDistinctLeasedFile()
    {
        Directory.CreateDirectory(directory); using var project = Project(); var result = Collect();
        var options = D3D9EffectResourceTree.CreateSaveOptions(project, D3D9EffectResourceTree.FromClosure(result.Closure!, cancellationToken: Token));
        var restored = D3D9EffectResourceTree.ReadPackage(Package(options), cancellationToken: Token)!;
        string root;
        using (var owner = restored.Materialize(directory, Token))
        {
            root = owner.RootDirectory; var map = owner.CreateResourceMap();
            Assert.Equal(5, map.Bindings.Count);
            Assert.Equal(5, map.Bindings.Select(binding => binding.MaterializedAbsolutePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains(map.Bindings, binding => binding.OriginAbsolutePath == AbsoluteInclude);
            Assert.Contains(map.Bindings, binding => binding.OriginAbsolutePath == Image);
            foreach (var node in restored.DependencyGraph!.Nodes)
            {
                var binding = Assert.Single(map.Bindings, binding => binding.OriginAbsolutePath == node.OriginAbsolutePath);
                Assert.Equal(owner.GetAbsolutePath(node.RelativePath), binding.MaterializedAbsolutePath);
                Assert.Equal(restored.CopyFileBytes(node.RelativePath), File.ReadAllBytes(binding.MaterializedAbsolutePath));
                Assert.Throws<IOException>(() => File.WriteAllBytes(binding.MaterializedAbsolutePath, [9]));
            }
            // This is the actual native-session map/asset shape, with no original files present.
            using var nativeLease = D3D9ObjectFxAssets.Open(new([], map), owner.CompleteDependencyPaths);
            Assert.Throws<IOException>(() => owner.Dispose());
        }
        Assert.False(Directory.Exists(root));
    }

    private static D3D9EffectDependencyResult Collect()
    {
        var source = new MemorySource(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Main] = Encoding.UTF8.GetBytes("identical fx"), [Other] = Encoding.UTF8.GetBytes("identical fx"),
            [Include] = [0x81, 0x40, 0, 255], [AbsoluteInclude] = [7, 8, 9], [Image] = [66, 77, 0, 255]
        });
        var inspector = new Inspector();
        var result = new D3D9EffectDependencyCollector(source, inspector).Collect(
            [new(Main, new(Guid.Parse("a51ed885-425f-405b-ac41-005bf49b2152"), 2, "Effect@hidden", 4, "hidden owner", 0xfedcba9876543210)),
             new(Main, new(Guid.Parse("93cd00c2-0f91-423e-bac3-762f97c3a31d"), 1, "Object", 9, "second owner", 42)),
             new(Other, new(Description: "cross-drive root"))], Token);
        Assert.Equal(D3D9EffectDependencyStatus.Complete, result.Status); Assert.NotNull(result.Closure);
        return result;
    }

    private static readonly D3D9EffectDependencyCompilerIdentity Identity = new("fixed-test-host", new string('a', 64), 64, 932, new string('b', 64));
    private static D3D9EffectDependencyProfile[] Profiles() =>
    [new("effect-mip-off", 0x10c0, [new("_INDEX", "PSIZE15")]),
     new("effect-mip-on", 0x10c0, [new("_INDEX", "PSIZE15"), new("MME_MIPMAP", "")]),
     new("function:make:tx_1_0", 0, [], "make", "tx_1_0")];
    private sealed class Inspector : ID3D9EffectDependencyInspector
    {
        public D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
            Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> include, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (topFx.OriginAbsolutePath != Main) return new(D3D9EffectDependencyStatus.Complete, Identity, Profiles(), []);
            include(new("effect-mip-off", false, null, 1, Ref("ソ.inc", 932)));
            include(new("effect-mip-on", true, Include, 2, Ref(AbsoluteInclude)));
            return new(D3D9EffectDependencyStatus.Complete, Identity, Profiles(),
                [new("effect-mip-off", D3D9EffectDependencyReferenceKind.Resource, Ref(Image), "unusedTexture", "ResourceName"),
                 new("effect-mip-on", D3D9EffectDependencyReferenceKind.Effect, Ref(Other), "rt", "DefaultEffect", 7, "*", Ref(" *= 元の行 ", 932)),
                 new("effect-mip-on", D3D9EffectDependencyReferenceKind.Symbol, Ref("hide"), "rt", "DefaultEffect", 8, "owner", Ref(" owner=hide "), D3D9EffectDependencySymbol.Hide),
                 new("effect-mip-on", D3D9EffectDependencyReferenceKind.TextureFunction, Ref("make"), "generated", "Function", FunctionEntryPoint: "make", FunctionTarget: "tx_1_0")],
                [new("compiler-warning", "informational warning", "effect-mip-on", "rt", "DefaultEffect", new([0x81, 0, 0xff], 932), 1)]);
        }
    }
    private sealed class MemorySource(Dictionary<string, byte[]> files) : ID3D9EffectDependencySource
    {
        public D3D9EffectDependencySnapshot Capture(D3D9EffectDependencySourceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = EncodingFor(request.Reference.CodePage).GetString(request.Reference.CopyBytes());
            string origin;
            if (text.Length >= 3 && text[1] == ':') origin = text;
            else
            {
                string baseOrigin = request.BaseOriginAbsolutePath ?? throw new InvalidDataException("A relative test reference requires a base origin.");
                origin = baseOrigin[..(baseOrigin.LastIndexOf('\\') + 1)] + text;
            }
            origin = D3D9EffectDependencySnapshot.CanonicalizeOrigin(origin);
            if (!files.TryGetValue(origin, out var bytes)) throw new InvalidDataException("Unexpected test source reference: " + origin);
            return new(origin, bytes);
        }
    }
    private static D3D9EffectDependencyReference Ref(string text, int codePage = 65001) =>
        new(EncodingFor(codePage).GetBytes(text), codePage);
    private static Encoding EncodingFor(int codePage) => codePage == 65001 ? Encoding.UTF8 : CodePagesEncodingProvider.Instance.GetEncoding(codePage)!;
    private static void AssertGraphEqual(D3D9EffectResourceTreeDependencyGraph expected, D3D9EffectResourceTreeDependencyGraph actual)
    {
        Assert.Equal(expected.Nodes, actual.Nodes); Assert.Equal(expected.CompilerIdentity, actual.CompilerIdentity);
        Assert.Equal(expected.Roots.Select(root => (root.OriginAbsolutePath, root.Provenance)), actual.Roots.Select(root => (root.OriginAbsolutePath, root.Provenance)));
        Assert.Equal(expected.Profiles.Count, actual.Profiles.Count);
        for (int index = 0; index < expected.Profiles.Count; index++)
        {
            var a = expected.Profiles[index]; var b = actual.Profiles[index];
            Assert.Equal((a.Id, a.Flags, a.EntryPoint, a.Target), (b.Id, b.Flags, b.EntryPoint, b.Target)); Assert.Equal(a.Macros, b.Macros);
        }
        Assert.Equal(expected.Edges.Count, actual.Edges.Count);
        for (int index = 0; index < expected.Edges.Count; index++)
        {
            var a = expected.Edges[index]; var b = actual.Edges[index];
            Assert.Equal(a with { Reference = b.Reference, RawRow = b.RawRow }, b); AssertReference(a.Reference, b.Reference); AssertReference(a.RawRow, b.RawRow);
        }
        Assert.Equal(expected.Diagnostics.Count, actual.Diagnostics.Count);
        for (int index = 0; index < expected.Diagnostics.Count; index++)
        {
            var a = expected.Diagnostics[index]; var b = actual.Diagnostics[index];
            Assert.Equal(a with { Reference = b.Reference, RawMessage = b.RawMessage }, b); AssertReference(a.Reference, b.Reference); AssertReference(a.RawMessage, b.RawMessage);
        }
    }
    private static void AssertReference(D3D9EffectDependencyReference? expected, D3D9EffectDependencyReference? actual)
    {
        if (expected is null) { Assert.Null(actual); return; }
        Assert.NotNull(actual); Assert.Equal(expected.CodePage, actual.CodePage); Assert.Equal(expected.CopyBytes(), actual.CopyBytes());
    }
    private SceneProjectSession Project()
    {
        // Reuse the existing effect-tree tests' valid one-triangle, no-motion fixture. An empty
        // composition is not an admissible project and would fail before exercising the adapter.
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "dependency tree fixture", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
            { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor();
        composition.Add("actor", path, ModelRole.Unspecified, Rest(model));
        return new(composition, new(), [], new NoPhysics(), cancellationToken: Token);
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1, Eligible: false),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject obj) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
    private static TreePackage Package(PmaSaveOptions options) => new(options.OptionalExtensions[D3D9EffectResourceTree.ExtensionName], options.AdditionalResources);
    private sealed class TreePackage(JsonElement metadata, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> resources) : IPmaPackageResources
    {
        public JsonElement Metadata => metadata;
        public int Reads { get; private set; }
        public string Format => PmcProjectStore.Format;
        public Guid ProjectId => Guid.Empty;
        public IReadOnlyDictionary<string, JsonElement> OptionalExtensions { get; } = new Dictionary<string, JsonElement> { [D3D9EffectResourceTree.ExtensionName] = metadata };
        public IReadOnlyList<string> ResourcePaths { get; } = resources.Keys.ToArray();
        public byte[] ReadResource(string path, long maxBytes = long.MaxValue)
        {
            Reads++; var bytes = resources[path]; if (bytes.Length > maxBytes) throw new InvalidDataException(); return bytes.ToArray();
        }
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath((Mmd.Modern.TestSupport.TestWorkspace.Root("compat/20261106/dependency549/tests") + "/")), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
