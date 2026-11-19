using System.Buffers.Binary;
using System.Collections;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class PmmImportAssetSharingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-pmm-sharing-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SamePreparedSourceSharesOnlyWithinOneImportAndRequiresTheSameModelIdentity()
    {
        using var fixture = ResolvedOutputCameraFixture.Playback();
        var prepared = WritePrepared(fixture.Plan.Models[0], "one", ["color.png"]);
        var loader = new EditorPmmAssetLoader();
        var first = loader.Load(prepared, TestContext.Current.CancellationToken);
        Assert.Same(first, loader.Load(prepared with { Path = Path.Combine(directory, "one", ".", "model.pmx") }, TestContext.Current.CancellationToken));
        var nextImport = new EditorPmmAssetLoader().Load(prepared, TestContext.Current.CancellationToken);
        Assert.NotSame(first, nextImport); Assert.NotSame(Texture(first), Texture(nextImport));
        var replacement = prepared with { Model = TexturedModel(prepared.Model, ["color.png"]) };
        var changedIdentity = loader.Load(replacement, TestContext.Current.CancellationToken);
        Assert.NotSame(first, changedIdentity); Assert.Same(replacement.Model, changedIdentity.Model);
    }

    [Fact]
    public void EqualRelativeTextureNamesUnderDifferentModelDirectoriesNeverAlias()
    {
        using var fixture = ResolvedOutputCameraFixture.Playback();
        var prepared = WritePrepared(fixture.Plan.Models[0], "red", ["color.png"], [255, 0, 0, 255]);
        string secondDirectory = Path.Combine(directory, "blue"); Directory.CreateDirectory(secondDirectory);
        File.WriteAllBytes(Path.Combine(secondDirectory, "color.png"), Png([0, 0, 255, 255]));
        var secondPrepared = prepared with { Path = Path.Combine(secondDirectory, "model.pmx") };
        var loader = new EditorPmmAssetLoader();
        var red = loader.Load(prepared, TestContext.Current.CancellationToken); var blue = loader.Load(secondPrepared, TestContext.Current.CancellationToken);
        Assert.NotSame(red, blue); Assert.NotSame(Texture(red), Texture(blue));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Texture(red).Rgba.ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Texture(blue).Rgba.ToArray());
    }

    [Fact]
    public void CanonicalLocalFileCanShareAcrossDifferentModelsWithoutBypassingContainment()
    {
        using var fixture = ResolvedOutputCameraFixture.Playback();
        var prepared = WritePrepared(fixture.Plan.Models[0], "same", ["color.png"]);
        var loader = new EditorPmmAssetLoader(); var first = loader.Load(prepared, TestContext.Current.CancellationToken);
        var other = prepared with { Path = Path.Combine(directory, "same", "second.pmx"),
            Model = TexturedModel(prepared.Model, [".\\color.png"]) };
        var second = loader.Load(other, TestContext.Current.CancellationToken);
        Assert.NotSame(first, second); Assert.Same(Texture(first), Texture(second));
        // This resolves to a file already cached by the first model, but escapes this
        // model's subdirectory. A cache hit cannot authorize that invalid reference: the
        // texture degrades to missing (the model still loads) and the cached image is not shared.
        string nested = Path.Combine(directory, "same", "nested"); Directory.CreateDirectory(nested);
        var invalid = prepared with { Path = Path.Combine(nested, "third.pmx"),
            Model = TexturedModel(prepared.Model, ["../color.png"]) };
        var degraded = loader.Load(invalid, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(degraded.Source.Surface.Snapshot.Textures, texture => ReferenceEquals(texture, Texture(first)));
        Assert.Same(first, loader.Load(prepared, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CancellationAfterOneDecodedTexturePublishesNoPartialAssetAndCanRetry()
    {
        using var fixture = ResolvedOutputCameraFixture.Playback();
        var prepared = WritePrepared(fixture.Plan.Models[0], "cancel", ["first.png", "second.png"]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var materialList = new CancelOnSecondMaterial(prepared.Model.Materials, cancellation);
        prepared = prepared with { Model = TexturedModel(prepared.Model, prepared.Model.Textures, materialList) };
        var loader = new EditorPmmAssetLoader();
        Assert.ThrowsAny<OperationCanceledException>(() => loader.Load(prepared, cancellation.Token));
        var retried = loader.Load(prepared, TestContext.Current.CancellationToken);
        Assert.Equal(2, retried.Source.Surface.Snapshot.Textures.Count);
        Assert.All(retried.Source.Surface.Snapshot.Textures, texture => Assert.Equal(new byte[] { 32, 64, 128, 255 }, texture.Rgba.ToArray()));
        Assert.Same(retried, loader.Load(prepared, TestContext.Current.CancellationToken));
        // Even an existing cached asset is subject to the caller's cancellation.
        Assert.ThrowsAny<OperationCanceledException>(() => loader.Load(prepared, cancellation.Token));
    }

    [Fact]
    public void FailedTextureDecodeDoesNotCacheAnIncompleteAssetOrTheFailedTexture()
    {
        using var fixture = ResolvedOutputCameraFixture.Playback();
        var prepared = WritePrepared(fixture.Plan.Models[0], "repair", ["first.png", "second.png"]);
        string broken = Path.Combine(directory, "repair", "second.png"); File.WriteAllBytes(broken, [1, 2, 3]);
        var loader = new EditorPmmAssetLoader();
        // The broken texture degrades to missing instead of failing the model; that degraded asset is not cached.
        var degraded = loader.Load(prepared, TestContext.Current.CancellationToken);
        Assert.Single(degraded.Source.Surface.Snapshot.Textures);
        File.WriteAllBytes(broken, Png([220, 180, 60, 255]));
        var fixedAsset = loader.Load(prepared, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 220, 180, 60, 255 }, fixedAsset.Source.Surface.Snapshot.Textures[1].Rgba.ToArray());
        Assert.Same(fixedAsset, loader.Load(prepared, TestContext.Current.CancellationToken));
    }

    private PreparedPmmModel WritePrepared(PreparedPmmModel source, string folder, IReadOnlyList<string> textures, byte[]? rgba = null)
    {
        string root = Path.Combine(directory, folder); Directory.CreateDirectory(root);
        foreach (string texture in textures) File.WriteAllBytes(Path.Combine(root, texture), Png(rgba ?? [32, 64, 128, 255]));
        var model = TexturedModel(source.Model, textures); string path = Path.Combine(root, "model.pmx");
        File.WriteAllBytes(path, PmxWriter.ToBytes(model));
        // The source PMM and motion identities come from the established prepared fixture;
        // only task-owned PMX and texture files are written for the loader under test.
        return source with { Path = path, Model = model };
    }

    private static PmxModel TexturedModel(PmxModel source, IReadOnlyList<string> textures, IReadOnlyList<PmxMaterial>? materials = null) => new()
    {
        Header = source.Header, Name = source.Name, EnglishName = source.EnglishName,
        Comment = source.Comment, EnglishComment = source.EnglishComment, Vertices = source.Vertices,
        Faces = source.Faces, Textures = textures, Bones = source.Bones, Morphs = source.Morphs,
        DisplayFrames = source.DisplayFrames, RigidBodies = source.RigidBodies, Joints = source.Joints, SoftBodies = source.SoftBodies,
        Materials = materials ?? Enumerable.Range(0, textures.Count).Select(index => new PmxMaterial
        {
            Name = "texture-" + index, EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero,
            SpecularPower = 1, Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.BothFaces,
            EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = index, SphereTextureIndex = -1,
            SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1,
            Memo = "", FaceVertexCount = index == 0 ? source.Faces.Count * 3 : 0
        }).ToArray()
    };

    private static SceneTexture Texture(SceneAnimationModelAsset asset) => Assert.Single(asset.Source.Surface.Snapshot.Textures);

    private sealed class CancelOnSecondMaterial(IReadOnlyList<PmxMaterial> source, CancellationTokenSource cancellation) : IReadOnlyList<PmxMaterial>
    {
        private bool cancelled;
        public int Count => source.Count;
        public PmxMaterial this[int index]
        {
            get { if (index == 1 && !cancelled) { cancelled = true; cancellation.Cancel(); } return source[index]; }
        }
        public IEnumerator<PmxMaterial> GetEnumerator() => source.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Independent tiny PNG encoding, matching SceneTextureLoaderTests' fixture method.
    private static byte[] Png(byte[] rgba)
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, 1); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8; header[9] = 6; Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) { zlib.WriteByte(0); zlib.Write(rgba); }
        Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", []); return output.ToArray();
    }
    private static void Chunk(Stream output, string kind, byte[] data)
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes(kind), .. data]; Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length); output.Write(number); output.Write(bytes);
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) { crc ^= value; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
