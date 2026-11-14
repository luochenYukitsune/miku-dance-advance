using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Mmd.Modern.Smoke;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SceneTextureLoaderTests : IDisposable
{
    private readonly string directory;
    public SceneTextureLoaderTests()
    {
        string cache = Environment.GetEnvironmentVariable("MMD_TEXTURE_TEST_ROOT")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "texture-test-inputs"));
        directory = Path.Combine(cache, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public void PngDecodesExactRgbaAndKeepsUnpremultipliedAlpha()
    {
        Directory.CreateDirectory(Path.Combine(directory, "贴图"));
        byte[] rgba = [240, 32, 16, 128, 5, 160, 220, 255];
        File.WriteAllBytes(Path.Combine(directory, "贴图", "角色.png"), Png(2, 1, rgba));
        var texture = SceneTextureLoader.Load(directory, ".\\贴图\\角色.png");
        Assert.NotNull(texture);
        Assert.Equal(2, texture.Width); Assert.Equal(1, texture.Height);
        Assert.Equal(rgba, texture.Rgba.ToArray());
    }

    [Fact]
    public void BmpDecodesBottomUpBgrRowsIncludingPadding()
    {
        var bmp = new byte[62];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        bmp[54] = 255; bmp[60] = 255; // Bottom blue, top red (BGR).
        File.WriteAllBytes(Path.Combine(directory, "rows.bmp"), bmp);
        var texture = SceneTextureLoader.Load(directory, "rows.bmp");
        Assert.NotNull(texture);
        Assert.Equal(new byte[] {255,0,0,255, 0,0,255,255}, texture.Rgba.ToArray());
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("..\\outside.png")]
    [InlineData("C:\\outside.png")]
    public void AssetMustBeRelativeAndInsideModelDirectory(string path) =>
        Assert.Throws<InvalidDataException>(() => SceneTextureLoader.Load(directory, path));

    [Fact]
    public void MissingAndUnsupportedAssetsAreExplicitlyUnavailable()
    {
        Assert.Null(SceneTextureLoader.Load(directory, "missing.png"));
        File.WriteAllText(Path.Combine(directory, "data.fx"), "not an image");
        Assert.Null(SceneTextureLoader.Load(directory, "data.fx"));
    }

    [Fact]
    public void CorruptAndTruncatedImagesAreRejected()
    {
        File.WriteAllBytes(Path.Combine(directory, "bad.png"), [1, 2, 3, 4]);
        Assert.Throws<InvalidDataException>(() => SceneTextureLoader.Load(directory, "bad.png"));
        byte[] full = Png(1, 1, [100, 50, 25, 255]);
        File.WriteAllBytes(Path.Combine(directory, "short.png"), full[..35]);
        Assert.Throws<InvalidDataException>(() => SceneTextureLoader.Load(directory, "short.png"));
    }

    [Fact]
    public void OversizeHeaderIsRejectedBeforeLargePixelAllocation()
    {
        File.WriteAllBytes(Path.Combine(directory, "large.png"), Png(4097, 1, []));
        Assert.Throws<InvalidDataException>(() => SceneTextureLoader.Load(directory, "large.png"));
    }

    // Independent tiny PNG writer: decoder under test does not generate its own fixtures.
    private static byte[] Png(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] {137,80,78,71,13,10,26,10});
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            if (rgba.Length != 0) for (int row = 0; row < height; row++)
            { zlib.WriteByte(0); zlib.Write(rgba.AsSpan(row * width * 4, width * 4)); }
        }
        Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", []);
        return output.ToArray();
    }
    private static void Chunk(Stream output, string kind, byte[] data)
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes(kind), .. data];
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length); output.Write(number); output.Write(bytes);
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
    }
    public void Dispose()
    {
        // Keep independently generated inputs in the test artifact tree for inspection.
    }
}
