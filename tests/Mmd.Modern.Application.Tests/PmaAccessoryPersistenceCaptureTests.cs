using System.Text;
using System.Text.Json;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmaAccessoryPersistenceCaptureTests
{
    private const string Extension = "org.example.classic-accessories";
    private const string Resource = "render-resources/accessories/a000/source.x";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void TypedCaptureClonesEnvelopeAndBindsOriginalXResourceToCanonicalPreparedAsset()
    {
        var asset = Asset(); var accessory = new SceneProjectAccessory(Guid.NewGuid(), asset);
        using var document = JsonDocument.Parse("{\"version\":1,\"opaque\":\"preserved\"}");
        var items = new List<PmaAccessoryPersistenceItem> { new(accessory, Resource) };
        byte[] bytes = asset.RequirePortableSource().CopyOriginalBytes();
        var resources = new Dictionary<string, ReadOnlyMemory<byte>> { [Resource] = bytes };
        var capture = new PmaAccessoryPersistenceCapture(Extension, document.RootElement, items, resources);
        items.Clear(); resources.Clear(); bytes[0] = 0; document.Dispose();

        Assert.Equal(Extension, capture.ExtensionName);
        Assert.Equal(1, capture.Payload.GetProperty("version").GetInt32());
        Assert.Equal(accessory.ObjectId, Assert.Single(capture.Accessories).ObjectId);
        Assert.Same(asset.SourceSnapshot, Assert.Single(capture.Accessories).Asset.SourceSnapshot);
        Assert.Equal(Resource, Assert.Single(capture.RequiredResourcePaths));
        Assert.Throws<NotSupportedException>(() => ((IList<SceneProjectAccessory>)capture.Accessories).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)capture.RequiredResourcePaths).Clear());
    }

    [Fact]
    public void MissingMismatchedOrNoncanonicalOriginalResourceCannotAuthorizePersistence()
    {
        var accessory = new SceneProjectAccessory(Guid.NewGuid(), Asset());
        using var document = JsonDocument.Parse("{\"version\":1}");
        var empty = new Dictionary<string, ReadOnlyMemory<byte>>();
        Assert.Throws<InvalidDataException>(() => new PmaAccessoryPersistenceCapture(Extension, document.RootElement,
            [new(accessory, Resource)], empty));
        var wrong = new Dictionary<string, ReadOnlyMemory<byte>> { [Resource] = new byte[] { 1, 2, 3 } };
        Assert.Throws<InvalidDataException>(() => new PmaAccessoryPersistenceCapture(Extension, document.RootElement,
            [new(accessory, Resource)], wrong));
        Assert.Throws<InvalidDataException>(() => new PmaAccessoryPersistenceCapture(Extension, document.RootElement,
            [new(accessory, "render-resources/../source.x")], empty));
        var legacy = accessory with { Asset = accessory.Asset with { SourceSnapshot = null } };
        Assert.Throws<NotSupportedException>(() => new PmaAccessoryPersistenceCapture(Extension, document.RootElement,
            [new(legacy, Resource)], wrong));
    }

    [Fact]
    public void AmbiguousGuidStoredIdentityOrSourceResourceIsRejected()
    {
        var accessory = new SceneProjectAccessory(Guid.NewGuid(), Asset());
        var second = new SceneProjectAccessory(Guid.NewGuid(), Asset() with { Index = 91 });
        using var document = JsonDocument.Parse("{\"version\":1}");
        var resources = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            [Resource] = accessory.Asset.RequirePortableSource().CopyOriginalBytes(),
            ["render-resources/accessories/a001/source.x"] = second.Asset.RequirePortableSource().CopyOriginalBytes(),
        };
        Assert.Throws<InvalidDataException>(() => new PmaAccessoryPersistenceCapture(Extension, document.RootElement,
            [new(accessory, Resource), new(accessory, "render-resources/accessories/a001/source.x")], resources));
        Assert.Throws<InvalidDataException>(() => new PmaAccessoryPersistenceCapture(Extension, document.RootElement,
            [new(accessory, Resource), new(second with { Asset = second.Asset with { Index = accessory.Asset.Index } }, "render-resources/accessories/a001/source.x")], resources));
        Assert.Throws<InvalidDataException>(() => new PmaAccessoryPersistenceCapture(Extension, document.RootElement,
            [new(accessory, Resource), new(second, Resource)], resources));
    }

    private static PreparedPmmAccessory Asset() => PreparedPmmAccessory.FromOriginalBytes(7, "original-part.x",
        Encoding.UTF8.GetBytes(XAccessoryTests.Triangle), readOptions: new() { AnsiCodePage = 65001 }, cancellationToken: Token);
}
