using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class ClassicAccessoryAssetsTests : IDisposable
{
    private readonly string directory = Path.Combine("G:/MDA-Work/compat/20261110/screen608/tests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void TwoOriginsKeepRawCombinedTokensOriginalBytesAndIndependentIdentitiesAfterSourcesDisappear()
    {
        var first = Accessory("one", 19, "nested/shared.bmp*same.spa", [1, 2, 3], [4, 5]);
        var second = Accessory("two", 3, "nested/shared.bmp*same.spa", [8, 9], [10, 11, 12]);
        var state = D3D9ClassicAccessoryAssets.Capture([second, first], Decode, cancellationToken: Token);
        Assert.Equal(new byte[] { 3, 19 }, state.Accessories.Select(value => value.Asset.Index));
        Assert.Equal(new byte[] { 8, 9 }, BytesFor(state, second.ObjectId, "nested/shared.bmp"));
        var package = Package.From(state);
        Directory.Move(directory, directory + "-away");
        var restored = D3D9ClassicAccessoryAssets.ReadPackage(package, Decode, Token)!;
        Assert.Equal(state.Accessories.Select(value => (value.ObjectId, value.Asset.Index, value.Asset.Path, value.Asset.Sha256)),
            restored.Accessories.Select(value => (value.ObjectId, value.Asset.Index, value.Asset.Path, value.Asset.Sha256)));
        Assert.Equal("nested/shared.bmp*same.spa", restored.Accessories[1].Asset.Document!.Materials[0].Material.Texture);
        Assert.Equal(first.Asset.RequirePortableSource().CopyOriginalBytes(), restored.Accessories[1].Asset.RequirePortableSource().CopyOriginalBytes());
        Assert.Equal(new byte[] { 1, 2, 3 }, BytesFor(restored, first.ObjectId, "nested/shared.bmp"));
        Assert.Equal(new byte[] { 4, 5 }, BytesFor(restored, first.ObjectId, "same.spa"));
        // The second package is constructed only from state-owned bytes after the first reader expires.
        package.Disposed = true;
        var again = D3D9ClassicAccessoryAssets.ReadPackage(Package.From(restored), Decode, Token)!;
        Assert.Equal(restored.ResourcePaths, again.ResourcePaths);
        Assert.All(restored.ResourcePaths, path => Assert.Equal(restored.CopyResourceBytes(path), again.CopyResourceBytes(path)));
    }

    [Theory]
    [InlineData("movie.avi", true)]
    [InlineData("a.bmp*b.bmp", false)]
    [InlineData("a.bmp**a.sph", false)]
    public void UnsupportedOrMalformedTokensFailBeforeAnyTextureDecoder(string token, bool unsupported)
    {
        var item = Accessory("invalid", 2, token, null, null);
        int calls = 0;
        var error = Record.Exception(() => D3D9ClassicAccessoryAssets.Capture([item], (bytes, raw) => { calls++; return Decode(bytes, raw); }, cancellationToken: Token));
        if (unsupported) Assert.IsType<NotSupportedException>(error); else Assert.IsType<InvalidDataException>(error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ScreenMarkerKeepsOriginalXWithoutAFileOrTextureDependency()
    {
        var item = Accessory("screen", 7, "prefixscreen.bmpSuffix", null, null);
        int decodes = 0;
        var state = D3D9ClassicAccessoryAssets.Capture([item], (bytes, token) =>
            { decodes++; return Decode(bytes, token); }, cancellationToken: Token);
        Assert.Equal(0, decodes); Assert.Empty(state.AccessoryTextures[item.ObjectId]);
        Assert.Single(state.ResourcePaths);
        var package = Package.From(state);
        Directory.Move(directory, directory + "-away");
        var restored = D3D9ClassicAccessoryAssets.ReadPackage(package, (_, _) =>
            throw new InvalidOperationException("screen marker must never decode a file"), Token)!;
        Assert.Empty(restored.AccessoryTextures[item.ObjectId]);
        Assert.Equal("prefixscreen.bmpSuffix", restored.Accessories[0].Asset.Document!.Materials[0].Material.Texture);
        Assert.Equal(item.Asset.RequirePortableSource().CopyOriginalBytes(), restored.Accessories[0].Asset.RequirePortableSource().CopyOriginalBytes());
        package.Disposed = true;
        var again = D3D9ClassicAccessoryAssets.ReadPackage(Package.From(restored), Decode, Token)!;
        Assert.Equal(restored.ResourcePaths, again.ResourcePaths);
        Assert.Equal(restored.CopyResourceBytes(restored.ResourcePaths[0]), again.CopyResourceBytes(again.ResourcePaths[0]));
    }

    [Theory]
    [InlineData("screen.bmp", 3U, null, null)]
    [InlineData("a/screen.bmp.bak", 3U, null, null)]
    [InlineData("SCREEN.BMP", 0U, "SCREEN.BMP", null)]
    [InlineData("screen.bmp*sphere.spa", 5U, "screen.bmp", "sphere.spa")]
    public void ScreenRecognitionUsesOriginalCaseSensitiveSingleTokenGrammar(string token, uint type, string? primary, string? sphere)
        => Assert.Equal(new D3D9AccessoryTextureToken(type, primary, sphere), D3D9AccessoryTextureToken.Parse(token));

    [Fact]
    public void MissingDependencyInvalidDecoderAndProvenanceDriftNeverPublishPartialState()
    {
        var missing = Accessory("missing", 2, "nested/shared.bmp*same.spa", [1], null);
        Assert.ThrowsAny<Exception>(() => D3D9ClassicAccessoryAssets.Capture([missing], Decode, cancellationToken: Token));
        var valid = Accessory("valid", 3, "nested/shared.bmp", [2], null);
        Assert.Throws<InvalidDataException>(() => D3D9ClassicAccessoryAssets.Capture([valid], (_, _) => null!, cancellationToken: Token));
        var changed = valid with { Asset = valid.Asset with { Sha256 = new string('0', 64) } };
        Assert.Throws<InvalidDataException>(() => D3D9ClassicAccessoryAssets.Capture([changed], Decode, cancellationToken: Token));
        Assert.Throws<InvalidDataException>(() => D3D9ClassicAccessoryAssets.Capture([valid, valid with { ObjectId = Guid.NewGuid() }], Decode, cancellationToken: Token));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("digest")]
    [InlineData("token")]
    [InlineData("path")]
    [InlineData("duplicate")]
    public void MalformedMetadataRejectsBeforeTextureDecode(string kind)
    {
        var state = D3D9ClassicAccessoryAssets.Capture([Accessory("valid", 8, "nested/shared.bmp", [2], null)], Decode, cancellationToken: Token);
        var package = Package.From(state);
        var metadata = JsonNode.Parse(package.Metadata.GetRawText())!;
        var asset = metadata["assets"]![0]!;
        if (kind == "version") metadata["schemaVersion"] = 900;
        if (kind == "digest") asset["source"]!["sha256"] = new string('0', 64);
        if (kind == "token") asset["materialTokens"]![0] = "other.bmp";
        if (kind == "path") asset["textures"]![0]!["resource"]!["path"] = "render-resources/elsewhere.bin";
        if (kind == "duplicate") metadata["assets"]!.AsArray().Add(asset.DeepClone());
        package.Metadata = JsonSerializer.SerializeToElement(metadata);
        int calls = 0;
        Assert.Throws<InvalidDataException>(() => D3D9ClassicAccessoryAssets.ReadPackage(package, (bytes, raw) => { calls++; return Decode(bytes, raw); }, Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ResourceTamperDecoderFailureAndCancellationAreExplicit()
    {
        var state = D3D9ClassicAccessoryAssets.Capture([Accessory("valid", 8, "nested/shared.bmp", [2], null)], Decode, cancellationToken: Token);
        var package = Package.From(state);
        string texture = package.ResourcePaths.Last(); package.Resources[texture] = [99];
        Assert.Throws<InvalidDataException>(() => D3D9ClassicAccessoryAssets.ReadPackage(package, Decode, Token));
        Assert.Throws<InvalidDataException>(() => D3D9ClassicAccessoryAssets.ReadPackage(Package.From(state), (_, _) => null!, Token));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => D3D9ClassicAccessoryAssets.ReadPackage(Package.From(state), Decode, cancelled.Token));
    }

    private SceneProjectAccessory Accessory(string origin, byte index, string rawToken, byte[]? plain, byte[]? sphere)
    {
        string root = Path.Combine(directory, origin); Directory.CreateDirectory(Path.Combine(root, "nested"));
        if (plain is not null) File.WriteAllBytes(Path.Combine(root, "nested/shared.bmp"), plain);
        if (sphere is not null) File.WriteAllBytes(Path.Combine(root, "same.spa"), sphere);
        var x = Encoding.ASCII.GetBytes("xof 0303txt 0032\nMesh { 3; 0;0;0;, 1;0;0;, 0;1;0;; 1; 3;0,1,2;; MeshMaterialList { 1; 1; 0;; Material { 1;1;1;1;; 1; 0;0;0;; 0;0;0;; TextureFilename { \"" + rawToken + "\"; } } } }");
        string path = Path.Combine(root, "object.x"); File.WriteAllBytes(path, x);
        return new(Guid.NewGuid(), PreparedPmmAccessory.FromOriginalBytes(index, path, x, cancellationToken: Token));
    }
    private static SceneTexture Decode(ReadOnlyMemory<byte> bytes, string rawToken)
        => new(rawToken + ":" + Convert.ToHexString(bytes.Span), 1, 1, new byte[] { bytes.Span[0], 0, 0, 255 });
    private static byte[] BytesFor(D3D9ClassicAccessoryAssetsState state, Guid id, string token)
        => Convert.FromHexString(state.AccessoryTextures[id][token].Name.Split(':')[1]);

    private sealed class Package : IPmaPackageResources
    {
        public bool Disposed { get; set; }
        public JsonElement Metadata { get; set; }
        public Dictionary<string, byte[]> Resources { get; } = new(StringComparer.Ordinal);
        public string Format => PmcProjectStore.Format;
        public Guid ProjectId => Guid.Empty;
        public IReadOnlyDictionary<string, JsonElement> OptionalExtensions => new Dictionary<string, JsonElement> { [D3D9ClassicAccessoryAssets.ExtensionName] = Metadata };
        public IReadOnlyList<string> ResourcePaths => Resources.Keys.ToArray();
        public byte[] ReadResource(string path, long maxBytes = long.MaxValue)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            var bytes = Resources[path]; if (bytes.Length > maxBytes) throw new InvalidDataException(); return bytes.ToArray();
        }
        public static Package From(D3D9ClassicAccessoryAssetsState state)
        {
            var result = new Package { Metadata = state.ToPersistenceCapture().Payload };
            foreach (string path in state.ResourcePaths) result.Resources.Add(path, state.CopyResourceBytes(path));
            return result;
        }
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath("G:/MDA-Work/compat/20261110/screen608/tests/"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(full)) Directory.Delete(full, true);
        if (Directory.Exists(full + "-away")) Directory.Delete(full + "-away", true);
    }
}
