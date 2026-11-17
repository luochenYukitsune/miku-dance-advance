using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmAccessorySourceSnapshotTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void OriginalBytesAreOwnedBeforeCallbacksAndPublicCopiesCannotChangeProvenance()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(XAccessoryTests.Triangle);
        byte[] expected = bytes.ToArray();
        var asset = PreparedPmmAccessory.FromOriginalBytes(7, "part.x", bytes, _ =>
        {
            Array.Fill(bytes, (byte)'!');
            return new SceneTexture("image", 1, 1, new byte[] { 255, 255, 255, 255 });
        }, new() { AnsiCodePage = 65001 }, cancellationToken: Token);

        var snapshot = asset.RequirePortableSource();
        Assert.Equal(expected, snapshot.CopyOriginalBytes());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), snapshot.Sha256);
        Assert.Equal(snapshot.Sha256, asset.Sha256);
        Assert.Equal("part.x", snapshot.OriginalPath);
        Assert.Equal(expected.Length, snapshot.ByteLength);
        Assert.Equal("image.png", Assert.Single(asset.Document!.Materials).Material.Texture);
        Assert.Single(asset.Source.Surface.Snapshot.Textures);
        byte[] copy = snapshot.CopyOriginalBytes();
        Array.Fill(copy, (byte)0);
        Assert.Equal(expected, snapshot.CopyOriginalBytes());
        Assert.Same(snapshot, asset.RequirePortableSource());
    }

    [Fact]
    public void ExplicitEncodingReaderBudgetsAndActualUnitScaleAreRetained()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string texture = "画像.png";
        byte[] bytes = Encoding.GetEncoding(932).GetBytes(XAccessoryTests.Triangle.Replace("image.png", texture));
        var options = new XTextReadOptions
        {
            AnsiCodePage = 932, MaxInputBytes = bytes.Length + 1, MaxVertices = 10,
            MaxTriangleVertices = 30, MaxDepth = 8, MaxTokens = 2048, MaxTokenCharacters = 128,
            MaxTriangulationOperations = 1024, MaxDecompressedBytes = 4096, MaxCompressedChunks = 7
        };
        var asset = PreparedPmmAccessory.FromOriginalBytes(91, "元の配件.x", bytes,
            readOptions: options, unitScale: 2.5f, cancellationToken: Token);
        var snapshot = asset.RequirePortableSource();

        Assert.Same(options, snapshot.ReadOptions);
        Assert.Equal(2.5f, snapshot.UnitScale);
        Assert.Equal(PmmAccessorySourceSnapshot.SupportedParserIdentity, snapshot.ParserIdentity);
        Assert.Equal(PmmAccessorySourceSnapshot.SupportedAdapterIdentity, snapshot.AdapterIdentity);
        Assert.Equal(texture, Assert.Single(asset.Document!.Materials).Material.Texture);
        Assert.Equal(new Vector3(2, 3, 4), asset.Document.Vertices[0].Position);
        Assert.Equal(new Vector3(5, 7.5f, 10), asset.Source.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(asset.Document.Vertices[0].Normal, asset.Source.Surface.Snapshot.Vertices[0].Normal);
        Assert.Equal(bytes, snapshot.CopyOriginalBytes());
    }

    [Fact]
    public void RestoreUsesFrozenBytesAndOriginalIdentityWithoutOpeningTheOriginalPath()
    {
        string unavailable = Path.Combine(MdaWorkRoot.Path("compat/20261107/accessory563"), "never-created-" + Guid.NewGuid().ToString("N"), "part.x");
        byte[] bytes = Encoding.UTF8.GetBytes(XAccessoryTests.Triangle);
        var asset = PreparedPmmAccessory.FromOriginalBytes(7, unavailable, bytes,
            readOptions: new() { AnsiCodePage = 65001 }, unitScale: 1.7f, cancellationToken: Token);
        var snapshot = asset.RequirePortableSource();
        var restored = PreparedPmmAccessory.FromOriginalBytes(asset.Index, snapshot.OriginalPath,
            snapshot.CopyOriginalBytes(), readOptions: snapshot.ReadOptions, unitScale: snapshot.UnitScale,
            cancellationToken: Token);

        Assert.False(File.Exists(unavailable));
        Assert.Equal(asset.Path, restored.Path);
        Assert.Equal(asset.Sha256, restored.Sha256);
        Assert.Equal(asset.Document!.Vertices.ToArray(), restored.Document!.Vertices.ToArray());
        Assert.Equal(asset.Document.Materials.ToArray(), restored.Document.Materials.ToArray());
        Assert.Equal(asset.Source.Surface.Snapshot.Vertices.ToArray(), restored.Source.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(snapshot.CopyOriginalBytes(), restored.RequirePortableSource().CopyOriginalBytes());
    }

    [Fact]
    public void LegacyManualAssetsRemainUsableButCannotClaimPortableProvenance()
    {
        var source = XAccessoryAdapter.FromDocument(XAccessoryTests.Read(XAccessoryTests.Triangle)).Frame;
        var manual = new PreparedPmmAccessory(7, "part.x", new string('a', 64), source, []);
        Assert.Same(source, manual.Source);
        Assert.Null(manual.SourceSnapshot);
        Assert.Throws<NotSupportedException>(() => manual.RequirePortableSource());
    }

    [Fact]
    public void ChangedHashIdentityDocumentOrSourceCannotReuseCanonicalProvenance()
    {
        var asset = PreparedPmmAccessory.FromOriginalBytes(7, "part.x", Encoding.UTF8.GetBytes(XAccessoryTests.Triangle),
            readOptions: new() { AnsiCodePage = 65001 }, cancellationToken: Token);
        var documentCopy = XAccessoryTests.Read(XAccessoryTests.Triangle);
        Assert.Throws<InvalidDataException>(() => (asset with { Sha256 = new string('a', 64) }).RequirePortableSource());
        Assert.Throws<InvalidDataException>(() => (asset with { Path = "other.x" }).RequirePortableSource());
        Assert.Throws<InvalidDataException>(() => (asset with { Document = documentCopy }).RequirePortableSource());
        Assert.Throws<InvalidDataException>(() => (asset with { Document = null }).RequirePortableSource());
        Assert.Throws<InvalidDataException>(() => (asset with { Source = XAccessoryAdapter.FromDocument(asset.Document!).Frame }).RequirePortableSource());
        Assert.Throws<NotSupportedException>(() => (asset with { SourceSnapshot = null }).RequirePortableSource());
        // Identity/diagnostic wrappers do not alter the canonical immutable X conversion.
        Assert.Same(asset.SourceSnapshot, (asset with { Index = 8, Diagnostics = ["binding diagnostic"] }).RequirePortableSource());
    }

    [Fact]
    public void UntrackedWorldAndUnitConversionsCannotBeCapturedAsTheOriginalAdaptation()
    {
        var asset = PreparedPmmAccessory.FromOriginalBytes(7, "part.x", Encoding.UTF8.GetBytes(XAccessoryTests.Triangle),
            readOptions: new() { AnsiCodePage = 65001 }, cancellationToken: Token);
        var customWorld = new OriginalFrameScene(new(asset.Source.Surface.Snapshot.WithWorld(Matrix4x4.CreateTranslation(1, 2, 3)),
            asset.Source.Surface.Materials, asset.Source.Surface.Profile));
        Assert.Throws<InvalidDataException>(() => (asset with { Source = customWorld }).RequirePortableSource());
        var customScale = XAccessoryAdapter.FromDocument(asset.Document!, unitScale: 3).Frame;
        Assert.Throws<InvalidDataException>(() => (asset with { Source = customScale }).RequirePortableSource());
    }

    [Fact]
    public void InputLimitMalformedBytesAndCancellationRejectBeforePublishingAnAsset()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(XAccessoryTests.Triangle);
        Assert.Throws<InvalidDataException>(() => PreparedPmmAccessory.FromOriginalBytes(7, "part.x", bytes,
            readOptions: new() { MaxInputBytes = 32 }, cancellationToken: Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => PreparedPmmAccessory.FromOriginalBytes(7, "part.x", bytes,
            readOptions: new() { MaxInputBytes = -1 }, cancellationToken: Token));
        Assert.Throws<InvalidDataException>(() => PreparedPmmAccessory.FromOriginalBytes(7, "part.x", new byte[16], cancellationToken: Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        int callbacks = 0;
        Assert.Throws<OperationCanceledException>(() => PreparedPmmAccessory.FromOriginalBytes(7, "part.x", bytes,
            _ => { callbacks++; return null; }, cancellationToken: cancelled.Token));
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public void CancellationRaisedByTextureLoadingCannotPublishThePreparedAsset()
    {
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => PreparedPmmAccessory.FromOriginalBytes(7, "part.x",
            Encoding.UTF8.GetBytes(XAccessoryTests.Triangle), _ => { cancellation.Cancel(); return null; },
            cancellationToken: cancellation.Token));
    }
}
