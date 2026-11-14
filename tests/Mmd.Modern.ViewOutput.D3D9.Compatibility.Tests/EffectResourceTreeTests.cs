using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

public sealed class EffectResourceTreeTests : IDisposable
{
    private const string Extension = "org.mda.traditional-effect-tree";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "effect-tree-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public EffectResourceTreeTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void CaptureDiscoversEntireBoundedTreeInOrdinalOrderAndDetachesOriginalBytes()
    {
        string root = Fixture();
        var tree = Capture(root);
        Assert.Equal(new[] { "effect/main.fx", "extra.bin", "shared/共通.fx", "textures/色.bmp" }, Files(tree).Select(PathOf));
        Assert.Equal(new[] { "effect", "empty", "shared", "textures" }, Strings(tree, "Directories"));
        Assert.Equal(new[] { "effect/main.fx" }, Strings(tree, "RequiredRelativeFiles"));
        var bytes = File.ReadAllBytes(Path.Combine(root, "shared", "共通.fx"));
        Assert.Equal(bytes, Copy(tree, "shared/共通.fx"));
        foreach (var file in Files(tree))
        {
            byte[] original = Copy(tree, PathOf(file));
            Assert.Equal((long)original.Length, Get<long>(file, "ByteLength"));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), Get<string>(file, "Sha256"));
        }
        File.WriteAllBytes(Path.Combine(root, "shared", "共通.fx"), [9]);
        var returned = Copy(tree, "shared/共通.fx"); returned[0] ^= 255;
        Assert.Equal(bytes, Copy(tree, "shared/共通.fx"));
        Directory.Move(root, root + "-moved");
        Assert.Equal(bytes, Copy(tree, "shared/共通.fx"));
    }

    [Theory]
    [InlineData("MaxFiles", 3)]
    [InlineData("MaxDirectories", 3)]
    [InlineData("MaxDepth", 1)]
    [InlineData("MaxFileBytes", 1)]
    [InlineData("MaxTotalBytes", 1)]
    [InlineData("MaxPathBytes", 1)]
    public void CaptureRejectsBudgetsBeforeReturningAPartialTree(string name, long value)
    {
        string root = Fixture();
        Assert.Throws<InvalidDataException>(() => Capture(root, Limits(name, value)));
        File.WriteAllBytes(Path.Combine(root, "extra.bin"), [5]);
        Directory.Move(root, root + "-unlocked");
    }

    [Fact]
    public void MissingRequiredFilesEmptyRequiredSetAndPreCancellationReject()
    {
        string root = Fixture();
        Assert.Throws<InvalidDataException>(() => Capture(root, required: ["missing.fx"]));
        Assert.Throws<ArgumentException>(() => Capture(root, required: []));
        Assert.Throws<OperationCanceledException>(() => Capture(root, token: new CancellationToken(true)));
        Directory.Move(root, root + "-unlocked");
    }

    [Theory]
    [InlineData("../outside.fx")]
    [InlineData("effect/../extra.bin")]
    [InlineData("effect\\main.fx")]
    [InlineData("/effect/main.fx")]
    [InlineData("effect//main.fx")]
    [InlineData("effect/./main.fx")]
    [InlineData("effect/CON.fx")]
    [InlineData("effect/main.fx:stream")]
    public void RequiredPathsMustAlreadyBeCanonicalPortableRelativePaths(string path)
        => Assert.Throws<ArgumentException>(() => Capture(Fixture(), required: [path]));

    [Fact]
    public void CaptureRejectsHardlinkAliasesAndReparseDirectoriesWithoutFollowingThem()
    {
        string root = Fixture();
        string alias = Path.Combine(root, "alias.fx");
        Assert.True(CreateHardLink(alias, Path.Combine(root, "effect", "main.fx"), 0));
        Assert.Throws<InvalidDataException>(() => Capture(root));
        File.Delete(alias);
        string outside = Path.Combine(directory, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.fx"), "outside");
        string link = Path.Combine(root, "redirect"); Directory.CreateSymbolicLink(link, outside);
        Assert.Throws<InvalidDataException>(() => Capture(root));
        Directory.Delete(link);
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "private.fx")));
    }

    [Fact]
    public void PmcRoundTripMaterializesOriginalLayoutAfterSourceMovesAndPreservesUnknownData()
    {
        string source = Fixture(); var tree = Capture(source);
        using var session = Project();
        var options = SaveOptions(session, tree, new()
        {
            OptionalExtensions = new Dictionary<string, JsonElement> { ["org.example.future"] = JsonSerializer.SerializeToElement(new { value = 17 }) },
            AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { ["render-resources/other.bin"] = new byte[] { 42 } },
            CacheDirectory = Path.Combine(directory, "save-cache")
        });
        Assert.Equal(source, options.OptionalExtensions[Extension].GetProperty("originAbsoluteRoot").GetString());
        string archive = Path.Combine(directory, "tree.pmc"); PmcProjectStore.Save(archive, session, options, Token);
        Directory.Move(source, source + "-moved");
        object restored;
        using (var loaded = Load(archive))
        {
            restored = Read(loaded)!; Assert.NotNull(restored);
            Assert.Equal(17, loaded.OptionalExtensions["org.example.future"].GetProperty("value").GetInt32());
            Assert.Equal(new byte[] { 42 }, loaded.ReadResource("render-resources/other.bin"));
        }
        string cache = Path.Combine(directory, "materialized"); Directory.CreateDirectory(cache);
        using var owner = Materialize(restored, cache);
        string root = Get<string>(owner, "RootDirectory");
        Assert.Equal(cache, Path.GetDirectoryName(root));
        Assert.Equal(4, Strings(owner, "CompleteDependencyPaths").Length);
        foreach (var file in Files(tree))
            Assert.Equal(Copy(tree, PathOf(file)), File.ReadAllBytes(Absolute(owner, PathOf(file))));
        string fx = Absolute(owner, "effect/main.fx");
        string include = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fx)!, "../shared/共通.fx"));
        Assert.Equal(Absolute(owner, "shared/共通.fx"), include);
        Assert.True(Directory.Exists(Path.Combine(root, "empty")));
        Assert.Throws<IOException>(() => File.WriteAllText(fx, "changed"));
        Assert.Throws<IOException>(() => Directory.Move(root, root + "-changed"));
    }

    [Fact]
    public void MaterializedOwnersAreUniqueDisposeOnlyTheirOwnTreeAndRetryWhenExternallyLeased()
    {
        var tree = Capture(Fixture()); string cache = Path.Combine(directory, "cache"); Directory.CreateDirectory(cache);
        string unrelated = Path.Combine(cache, "keep.txt"); File.WriteAllText(unrelated, "keep");
        using var first = Materialize(tree, cache); using var second = Materialize(tree, cache);
        string root = Get<string>(first, "RootDirectory"), other = Get<string>(second, "RootDirectory"); Assert.NotEqual(root, other);
        using (var lease = D3D9ObjectFxAssets.Open(new([]), Strings(first, "CompleteDependencyPaths")))
            Assert.Throws<IOException>(() => first.Dispose());
        Assert.True(Directory.Exists(root)); first.Dispose(); first.Dispose();
        Assert.False(Directory.Exists(root)); Assert.True(Directory.Exists(other)); Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    [Fact]
    public void MaterializationCancellationDoesNotCreatePartialChildren()
    {
        var tree = Capture(Fixture()); string cache = Path.Combine(directory, "cache"); Directory.CreateDirectory(cache);
        Assert.Throws<OperationCanceledException>(() => Materialize(tree, cache, new CancellationToken(true)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
    }

    [Theory]
    [InlineData(16000, 0)]
    [InlineData(1, 65000)]
    public void ExpandedCachePathBudgetsRejectBeforeCreatingAnyTree(int fileCount, int directoryCount)
    {
        using var session = Project(); var captured = Capture(Fixture());
        var original = SaveOptions(session, captured);
        var metadata = JsonNode.Parse(original.OptionalExtensions[Extension].GetRawText())!.AsObject();
        var template = metadata["files"]![0]!.DeepClone().AsObject();
        byte[] content = original.AdditionalResources[template["resourcePath"]!.GetValue<string>()].ToArray();
        var resources = new Dictionary<string, ReadOnlyMemory<byte>>();
        var files = new JsonArray();
        for (int index = 0; index < fileCount; index++)
        {
            string path = "f" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".fx";
            string resource = "render-resources/traditional/effect-tree/file" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".bin";
            var descriptor = template.DeepClone().AsObject(); descriptor["relativePath"] = path; descriptor["resourcePath"] = resource;
            files.Add(descriptor); resources.Add(resource, content);
        }
        metadata["files"] = files;
        metadata["directories"] = new JsonArray(Enumerable.Range(0, directoryCount)
            .Select(index => (JsonNode?)JsonValue.Create("d" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture))).ToArray());
        metadata["requiredRelativeFiles"] = new JsonArray("f00000.fx");
        string archive = Path.Combine(directory, "expanded-" + fileCount + ".pmc");
        PmcProjectStore.Save(archive, session, new()
        {
            CacheDirectory = Path.Combine(directory, "save-cache"),
            OptionalExtensions = new Dictionary<string, JsonElement> { [Extension] = JsonSerializer.SerializeToElement(metadata) },
            AdditionalResources = resources
        }, Token);
        using var loaded = Load(archive); var restored = Read(loaded)!;
        string cache = directory;
        for (int index = 0; index < 6; index++) cache = Path.Combine(cache, new string('x', 190) + index);
        Directory.CreateDirectory(cache);
        Assert.Throws<InvalidDataException>(() => Materialize(restored, cache));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
    }

    [Fact]
    public void ReadWithoutExtensionReturnsNullAndSaveCollisionsReject()
    {
        using var session = Project(); string plain = Path.Combine(directory, "plain.pmc"); PmcProjectStore.Save(plain, session, new() { CacheDirectory = Path.Combine(directory, "save-cache") }, Token);
        using var loaded = Load(plain); Assert.Null(Read(loaded));
        var tree = Capture(Fixture()); var options = SaveOptions(session, tree);
        Assert.Throws<ArgumentException>(() => SaveOptions(session, tree, options));
        var path = options.AdditionalResources.Keys.First();
        Assert.Throws<ArgumentException>(() => SaveOptions(session, tree, new() { AdditionalResources = new Dictionary<string, ReadOnlyMemory<byte>> { [path.Replace("file", "FILE", StringComparison.Ordinal)] = new byte[] { 1 } } }));
        Assert.Throws<InvalidDataException>(() => SaveOptions(session, tree, new() { ValidationOptions = new() { MaxEntryBytes = 1 } }));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("unknown-field")]
    [InlineData("traversal")]
    [InlineData("case-alias")]
    [InlineData("file-directory-conflict")]
    [InlineData("sha")]
    [InlineData("length")]
    [InlineData("missing-blob")]
    [InlineData("required-missing")]
    public void ReadRejectsMalformedTreeMetadataAndResources(string kind)
    {
        using var session = Project(); var tree = Capture(Fixture()); var options = SaveOptions(session, tree);
        var metadata = JsonNode.Parse(options.OptionalExtensions[Extension].GetRawText())!.AsObject();
        var files = metadata["files"]!.AsArray(); var first = files[0]!.AsObject();
        var resources = options.AdditionalResources.ToDictionary(pair => pair.Key, pair => pair.Value);
        switch (kind)
        {
            case "schema": metadata["schemaVersion"] = 99; break;
            case "unknown-field": metadata["unknown"] = true; break;
            case "traversal": first["relativePath"] = "../outside.fx"; break;
            case "case-alias": files[1]!["relativePath"] = first["relativePath"]!.GetValue<string>().ToUpperInvariant(); break;
            case "file-directory-conflict": first["relativePath"] = "shared"; break;
            case "sha": first["sha256"] = new string('0', 64); break;
            case "length": first["byteLength"] = 1; break;
            case "missing-blob": resources.Remove(first["resourcePath"]!.GetValue<string>()); break;
            case "required-missing": metadata["requiredRelativeFiles"] = new JsonArray("absent.fx"); break;
        }
        options = options with { OptionalExtensions = new Dictionary<string, JsonElement> { [Extension] = JsonSerializer.SerializeToElement(metadata) }, AdditionalResources = resources };
        string archive = Path.Combine(directory, kind + ".pmc"); PmcProjectStore.Save(archive, session, options, Token);
        using var loaded = Load(archive); Assert.Throws<InvalidDataException>(() => Read(loaded));
    }

    [Fact]
    public void CaptureFreezesCanonicalOriginAsDetachedIdentityAndWritesStrictSchemaTwo()
    {
        string source = Fixture();
        var tree = Capture(Path.Combine(source, "effect", "..") + Path.DirectorySeparatorChar);
        Assert.Equal(source, Get<string>(tree, "OriginAbsoluteRoot"));
        using var session = Project();
        var options = SaveOptions(session, tree);
        var metadata = options.OptionalExtensions[Extension];
        Assert.Equal(2, metadata.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(source, metadata.GetProperty("originAbsoluteRoot").GetString());
        Assert.Equal(new[] { "directories", "files", "originAbsoluteRoot", "requiredRelativeFiles", "schemaVersion" },
            metadata.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        Directory.Move(source, source + "-moved");
        Assert.Equal(source, Get<string>(tree, "OriginAbsoluteRoot"));
        Assert.Equal(new byte[] { 1, 2, 3 }, Copy(tree, "extra.bin"));
    }

    [Fact]
    public void CaptureCanonicalizesLowercaseDriveBeforeFreezingOrigin()
    {
        string source = Fixture();
        string lowercaseDrive = char.ToLowerInvariant(source[0]) + source[1..];
        Assert.Equal(char.ToUpperInvariant(source[0]) + source[1..], Get<string>(Capture(lowercaseDrive), "OriginAbsoluteRoot"));
    }

    [Fact]
    public void StoredOriginIsNeverOpenedAndResaveRetainsIdentityRatherThanCacheLocation()
    {
        using var session = Project();
        var options = SaveOptions(session, Capture(Fixture()));
        // The stored identity deliberately names an existing regular file, not a directory.
        string origin = Path.Combine(directory, "identity-only.bin");
        File.WriteAllBytes(origin, [19, 23]);
        var metadata = MetadataNode(options); metadata["schemaVersion"] = 2; metadata["originAbsoluteRoot"] = origin;
        var package = Package(options, metadata);
        object tree = ReadPackage(package)!;
        Assert.Equal(origin, Get<string>(tree, "OriginAbsoluteRoot"));
        Assert.Equal(options.AdditionalResources.Count, package.Reads);
        string cache = Path.Combine(directory, "cache"); Directory.CreateDirectory(cache);
        using var owner = Materialize(tree, cache);
        var rewritten = SaveOptions(session, tree);
        Assert.Equal(origin, rewritten.OptionalExtensions[Extension].GetProperty("originAbsoluteRoot").GetString());
        Assert.Equal(new byte[] { 19, 23 }, File.ReadAllBytes(origin));
        Assert.NotEqual(Get<string>(owner, "RootDirectory"), Get<string>(tree, "OriginAbsoluteRoot"));
    }

    [Fact]
    public void MovedSourceTreeCreatesCompleteOriginToPrivateMaterializedMapAndKeepsRelativeLayout()
    {
        string source = Fixture(); var captured = Capture(source);
        using var session = Project(); var options = SaveOptions(session, captured);
        Directory.Move(source, source + "-moved"); Assert.False(Directory.Exists(source));
        object tree = ReadPackage(Package(options, MetadataNode(options)))!;
        string cache = Path.Combine(directory, "cache"); Directory.CreateDirectory(cache);
        using var owner = Materialize(tree, cache);
        object map = ResourceMap(owner);
        var bindings = Get<IEnumerable>(map, "Bindings").Cast<object>().ToArray();
        Assert.Equal(Files(tree).Length, bindings.Length);
        foreach (object file in Files(tree))
        {
            string relative = PathOf(file);
            string origin = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
            object binding = Assert.Single(bindings, row => Get<string>(row, "OriginAbsolutePath") == origin);
            string local = Get<string>(binding, "MaterializedAbsolutePath");
            Assert.Equal(Absolute(owner, relative), local);
            Assert.Contains(local, Strings(owner, "CompleteDependencyPaths"));
            Assert.Equal(Copy(captured, relative), File.ReadAllBytes(local));
            Assert.False(File.Exists(origin));
        }
        string fx = Absolute(owner, "effect/main.fx");
        Assert.Equal(Absolute(owner, "extra.bin"), Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fx)!, "../extra.bin")));
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ResourceMap(owner));
        // A copied immutable map still describes its original identities after the owner releases files.
        Assert.Equal(bindings, Get<IEnumerable>(map, "Bindings").Cast<object>().ToArray());
    }

    [Fact]
    public void LegacySchemaOneReadsMaterializesAndResavesWithoutInventingOriginIdentity()
    {
        using var session = Project(); var options = SaveOptions(session, Capture(Fixture()));
        var metadata = MetadataNode(options); metadata["schemaVersion"] = 1; metadata.Remove("originAbsoluteRoot");
        object tree = ReadPackage(Package(options, metadata))!;
        Assert.Null(Get<object?>(tree, "OriginAbsoluteRoot"));
        var rewritten = SaveOptions(session, tree);
        Assert.Equal(1, rewritten.OptionalExtensions[Extension].GetProperty("schemaVersion").GetInt32());
        Assert.False(rewritten.OptionalExtensions[Extension].TryGetProperty("originAbsoluteRoot", out _));
        object again = ReadPackage(Package(rewritten, MetadataNode(rewritten)))!;
        Assert.Null(Get<object?>(again, "OriginAbsoluteRoot"));
        string cache = Path.Combine(directory, "cache"); Directory.CreateDirectory(cache);
        using var owner = Materialize(again, cache);
        Assert.Equal(Copy(tree, "extra.bin"), File.ReadAllBytes(Absolute(owner, "extra.bin")));
        Assert.Throws<InvalidOperationException>(() => ResourceMap(owner));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("relative")]
    [InlineData("drive-relative")]
    [InlineData("lowercase-drive")]
    [InlineData("root-relative")]
    [InlineData("unc")]
    [InlineData("device")]
    [InlineData("above-drive")]
    [InlineData("dot-segment")]
    [InlineData("forward-slashes")]
    [InlineData("trailing-separator")]
    [InlineData("alternate-stream")]
    [InlineData("reserved-name")]
    [InlineData("nul")]
    [InlineData("too-long")]
    [InlineData("unknown-field")]
    [InlineData("legacy-with-origin")]
    public void InvalidOriginMetadataRejectsBeforeReadingAnyBlob(string damage)
    {
        using var session = Project(); var options = SaveOptions(session, Capture(Fixture()));
        var metadata = MetadataNode(options); metadata["schemaVersion"] = 2; metadata["originAbsoluteRoot"] = @"Q:\identity-only\effects";
        switch (damage)
        {
            case "missing": metadata.Remove("originAbsoluteRoot"); break;
            case "null": metadata["originAbsoluteRoot"] = null; break;
            case "empty": metadata["originAbsoluteRoot"] = ""; break;
            case "relative": metadata["originAbsoluteRoot"] = "effects"; break;
            case "drive-relative": metadata["originAbsoluteRoot"] = @"Q:effects"; break;
            case "lowercase-drive": metadata["originAbsoluteRoot"] = @"q:\effects"; break;
            case "root-relative": metadata["originAbsoluteRoot"] = @"\effects"; break;
            case "unc": metadata["originAbsoluteRoot"] = @"\\server\share\effects"; break;
            case "device": metadata["originAbsoluteRoot"] = @"\\?\Q:\effects"; break;
            case "above-drive": metadata["originAbsoluteRoot"] = @"Q:\..\effects"; break;
            case "dot-segment": metadata["originAbsoluteRoot"] = @"Q:\effects\..\effects"; break;
            case "forward-slashes": metadata["originAbsoluteRoot"] = "Q:/identity-only/effects"; break;
            case "trailing-separator": metadata["originAbsoluteRoot"] = @"Q:\effects\"; break;
            case "alternate-stream": metadata["originAbsoluteRoot"] = @"Q:\effects:stream"; break;
            case "reserved-name": metadata["originAbsoluteRoot"] = @"Q:\NUL\effects"; break;
            case "nul": metadata["originAbsoluteRoot"] = "Q:\\effects\0tail"; break;
            case "too-long": metadata["originAbsoluteRoot"] = "Q:\\" + new string('x', 32767); break;
            case "unknown-field": metadata["unknownOrigin"] = true; break;
            case "legacy-with-origin": metadata["schemaVersion"] = 1; break;
        }
        var package = Package(options, metadata);
        Assert.Throws<InvalidDataException>(() => ReadPackage(package));
        Assert.Equal(0, package.Reads);
    }

    [Fact]
    public void DuplicateOriginPropertyRejectsBeforeReadingAnyBlob()
    {
        using var session = Project(); var options = SaveOptions(session, Capture(Fixture()));
        var metadata = MetadataNode(options); metadata["schemaVersion"] = 2; metadata["originAbsoluteRoot"] = @"Q:\effects";
        string raw = metadata.ToJsonString();
        raw = raw.Insert(raw.Length - 1, ",\"originAbsoluteRoot\":\"Q:\\\\other\"");
        using var parsed = JsonDocument.Parse(raw);
        var package = new TreePackage(parsed.RootElement.Clone(), options.AdditionalResources);
        Assert.Throws<InvalidDataException>(() => ReadPackage(package));
        Assert.Equal(0, package.Reads);
    }

    [Fact]
    public void OriginAwareReadCancellationAndMetadataBudgetLeaveBytesAndSourceUntouched()
    {
        string source = Fixture(); using var session = Project(); var options = SaveOptions(session, Capture(source));
        var original = options.AdditionalResources.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var package = Package(options, MetadataNode(options));
        Assert.Throws<OperationCanceledException>(() => ReadPackage(package, token: new CancellationToken(true)));
        Assert.Equal(0, package.Reads);
        Assert.Throws<InvalidDataException>(() => ReadPackage(package, Limits("MaxMetadataBytes", 1)));
        Assert.Equal(0, package.Reads);
        Assert.All(options.AdditionalResources, pair => Assert.Equal(original[pair.Key], pair.Value.ToArray()));
        Directory.Move(source, source + "-unlocked");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpandedOriginPathBudgetsRejectBeforeReadingAnyBlob(bool totalBudget)
    {
        using var session = Project(); var options = SaveOptions(session, Capture(Fixture()));
        var metadata = MetadataNode(options); metadata["schemaVersion"] = 2;
        int length = totalBudget ? 30_000 : 32_760;
        metadata["originAbsoluteRoot"] = "Q:\\" + string.Join('\\', Enumerable.Repeat(new string('x', 99), length / 100));
        var template = metadata["files"]![0]!.DeepClone().AsObject();
        byte[] content = options.AdditionalResources[template["resourcePath"]!.GetValue<string>()].ToArray();
        var resources = new Dictionary<string, ReadOnlyMemory<byte>>(); var files = new JsonArray();
        for (int index = 0; index < (totalBudget ? 600 : 1); index++)
        {
            string path = "file-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".fx";
            if (!totalBudget) path = new string('f', 100) + ".fx";
            string resource = "render-resources/traditional/effect-tree/file" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".bin";
            var descriptor = template.DeepClone().AsObject(); descriptor["relativePath"] = path; descriptor["resourcePath"] = resource;
            files.Add(descriptor); resources.Add(resource, content);
        }
        metadata["directories"] = new JsonArray(); metadata["files"] = files;
        metadata["requiredRelativeFiles"] = new JsonArray(files[0]!["relativePath"]!.GetValue<string>());
        var package = new TreePackage(JsonSerializer.SerializeToElement(metadata), resources);
        Assert.Throws<InvalidDataException>(() => ReadPackage(package));
        Assert.Equal(0, package.Reads);
    }

    private static JsonObject MetadataNode(PmaSaveOptions options)
        => JsonNode.Parse(options.OptionalExtensions[Extension].GetRawText())!.AsObject();
    private static TreePackage Package(PmaSaveOptions options, JsonObject metadata)
        => new(JsonSerializer.SerializeToElement(metadata), options.AdditionalResources);
    private static object? ReadPackage(IPmaPackageResources package, object? limits = null, CancellationToken? token = null)
        => Invoke(() => Codec().GetMethod("ReadPackage")!.Invoke(null, [package, limits, token ?? Token]));
    private static object ResourceMap(IDisposable owner)
    {
        var method = owner.GetType().GetMethod("CreateResourceMap"); Assert.NotNull(method);
        return Invoke(() => method.Invoke(owner, null))!;
    }
    private sealed class TreePackage(JsonElement metadata, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> resources) : IPmaPackageResources
    {
        public int Reads { get; private set; }
        public string Format => PmcProjectStore.Format;
        public Guid ProjectId => Guid.Empty;
        public IReadOnlyDictionary<string, JsonElement> OptionalExtensions { get; } = new Dictionary<string, JsonElement> { [Extension] = metadata };
        public IReadOnlyList<string> ResourcePaths { get; } = resources.Keys.ToArray();
        public byte[] ReadResource(string path, long maxBytes = long.MaxValue)
        {
            Reads++;
            ReadOnlyMemory<byte> bytes = resources[path];
            if (bytes.Length > maxBytes) throw new InvalidDataException("Test package resource exceeds the requested byte budget.");
            return bytes.ToArray();
        }
    }

    private string Fixture()
    {
        string root = Path.Combine(directory, "source-" + Guid.NewGuid().ToString("N"));
        foreach (string child in new[] { "effect", "shared", "textures", "empty" }) Directory.CreateDirectory(Path.Combine(root, child));
        File.WriteAllBytes(Path.Combine(root, "effect", "main.fx"), Encoding.UTF8.GetBytes("#include \"../shared/共通.fx\"\r\ntexture T < string ResourceName = \"../textures/色.bmp\"; >;"));
        File.WriteAllBytes(Path.Combine(root, "shared", "共通.fx"), [0x81, 0x40, 0, 255, 10]);
        File.WriteAllBytes(Path.Combine(root, "textures", "色.bmp"), [66, 77, 1, 0, 255]);
        File.WriteAllBytes(Path.Combine(root, "extra.bin"), [1, 2, 3]); return root;
    }
    private static Type Codec()
    {
        var type = typeof(D3D9EmmPma).Assembly.GetType("Mmd.Modern.ViewOutput.D3D9.Compatibility.D3D9EffectResourceTree");
        Assert.NotNull(type); return type;
    }
    private static object Capture(string root, object? limits = null, string[]? required = null, CancellationToken? token = null)
        => Invoke(() => Codec().GetMethod("Capture")!.Invoke(null, [root, required ?? ["effect/main.fx"], limits, token ?? Token]))!;
    private static object Limits(string property, long value)
    {
        var type = Codec().Assembly.GetType("Mmd.Modern.ViewOutput.D3D9.Compatibility.D3D9EffectResourceTreeLimits"); Assert.NotNull(type);
        var limits = Activator.CreateInstance(type)!; var member = type.GetProperty(property)!;
        member.SetValue(limits, Convert.ChangeType(value, member.PropertyType, System.Globalization.CultureInfo.InvariantCulture)); return limits;
    }
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
    private static object[] Files(object tree) => Get<IEnumerable>(tree, "Files").Cast<object>().ToArray();
    private static string PathOf(object file) => Get<string>(file, "RelativePath");
    private static string[] Strings(object value, string name) => Get<IEnumerable>(value, name).Cast<string>().ToArray();
    private static byte[] Copy(object tree, string path) => (byte[])Invoke(() => tree.GetType().GetMethod("CopyFileBytes")!.Invoke(tree, [path]))!;
    private static string Absolute(object owner, string path) => (string)Invoke(() => owner.GetType().GetMethod("GetAbsolutePath")!.Invoke(owner, [path]))!;
    private static IDisposable Materialize(object tree, string cache, CancellationToken? token = null)
        => (IDisposable)Invoke(() => tree.GetType().GetMethod("Materialize")!.Invoke(tree, [cache, token ?? Token]))!;
    private static PmaSaveOptions SaveOptions(SceneProjectSession session, object tree, PmaSaveOptions? options = null)
        => (PmaSaveOptions)Invoke(() => Codec().GetMethod("CreateSaveOptions")!.Invoke(null, [session, tree, options]))!;
    private static object? Read(PmaLoadedProject loaded) => Invoke(() => Codec().GetMethod("Read")!.Invoke(null, [loaded, null, Token]));
    private static object? Invoke(Func<object?> call)
    {
        try { return call(); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private SceneProjectSession Project()
    {
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "tree fixture", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
            { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        string path = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        var composition = new SceneCompositionEditor();
        composition.Add("actor", path, ModelRole.Unspecified, Rest(model));
        return new(composition, new(), [], new NoPhysics(), cancellationToken: Token);
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1, Eligible: false),
        materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private PmaLoadedProject Load(string path) => PmcProjectStore.Load(path, Path.Combine(directory, "load-cache"),
        (source, token) => { token.ThrowIfCancellationRequested(); var model = PmxReader.Read(source); return new(model, Rest(model)); },
        new NoPhysics(), cancellationToken: Token);
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject obj) => throw new InvalidOperationException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new InvalidOperationException();
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
    public void Dispose()
    {
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
