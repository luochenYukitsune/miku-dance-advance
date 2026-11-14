using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using SkiaSharp;

internal static class EditorTextureProbe
{
    private static readonly List<string> Checks = [];
    private static readonly Dictionary<string, string> Hashes = [];
    private static string output = "";
    private static readonly byte[] Pattern = [231, 17, 43, 31, 29, 211, 71, 127, 11, 37, 239, 255, 197, 137, 83, 0, 251, 97, 19, 201, 67, 149, 229, 83];

    public static int Main(string[] args)
    {
        output = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(output);
        try
        {
            Run();
            foreach (var pair in Hashes) Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value, "Source unchanged: " + Path.GetFileName(pair.Key));
            File.WriteAllText(Path.Combine(output, "texture-report.json"), JsonSerializer.Serialize(new { checks = Checks, passed = true }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {Checks.Count} editor texture checks: {output}");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "texture-report.json"), JsonSerializer.Serialize(new { checks = Checks, passed = false, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Run()
    {
        using (var bitmap = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul)))
        {
            Marshal.Copy(Pattern, 0, bitmap.GetPixels(), Pattern.Length);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            Save("original.png", png.ToArray());
            Equal("original.png", Pattern, "PNG preserves straight alpha and RGB bytes");
            Save("sphere.sph", png.ToArray());
            Equal("sphere.sph", Pattern, "Sphere extension uses content-based Skia decoding");
        }
        // A genuine 24-bit BMP with 12-byte rows for a 3-pixel-wide image.
        Save("bitmap.bmp", Bmp());
        Equal("bitmap.bmp", Opaque(Pattern), "BMP padding and bottom-up orientation");
        Save("bitmap.spa", Bmp());
        Equal("bitmap.spa", Opaque(Pattern), "BMP sphere map content under SPA extension");
        foreach (int origin in new[] { 0, 1, 2, 3 })
        {
            Save($"origin-{origin}.tga", Tga(32, origin, false));
            Equal($"origin-{origin}.tga", Pattern, $"TGA BGRA straight alpha and origin {origin}");
            Save($"rle-{origin}.tga", Tga(32, origin, true));
            Equal($"rle-{origin}.tga", Pattern, $"TGA RLE straight alpha and origin {origin}");
            Save($"origin24-{origin}.tga", Tga(24, origin, false));
            Equal($"origin24-{origin}.tga", Opaque(Pattern), $"TGA 24-bit odd-width origin {origin}");
            Save($"rle24-{origin}.tga", Tga(24, origin, true));
            Equal($"rle24-{origin}.tga", Opaque(Pattern), $"TGA 24-bit odd-width RLE origin {origin}");
        }
        Save("stride24.tga", Tga(24, 0, false));
        Equal("stride24.tga", Opaque(Pattern), "TGA 24-bit odd-width decoded stride");
        Save("rle24.tga", Tga(24, 2, true));
        Equal("rle24.tga", Opaque(Pattern), "TGA 24-bit RLE raw packet");
        byte[] opaqueTga = Tga(32, 2, false); opaqueTga[17] = 32;
        Save("opaque.tga", opaqueTga);
        Equal("opaque.tga", Opaque(Pattern), "TGA unused fourth byte remains opaque");
        byte[] grayHeader = Tga(24, 2, false)[..18]; grayHeader[2] = 3; grayHeader[16] = 8;
        Save("gray.tga", [..grayHeader, 17, 67, 129, 177, 213, 251]);
        Equal("gray.tga", new byte[] { 17, 67, 129, 177, 213, 251 }.SelectMany(value => new byte[] { value, value, value, 255 }).ToArray(), "TGA 8-bit grayscale decoded stride");
        byte[] grayRleHeader = grayHeader.ToArray(); grayRleHeader[2] = 11;
        Save("gray-rle.tga", [..grayRleHeader, 2, 17, 67, 129, 2, 177, 213, 251]);
        Equal("gray-rle.tga", new byte[] { 17, 67, 129, 177, 213, 251 }.SelectMany(value => new byte[] { value, value, value, 255 }).ToArray(), "TGA top-left 8-bit RLE stride");
        byte[] paletteHeader = Tga(32, 2, false)[..18]; paletteHeader[1] = 1; paletteHeader[2] = 1;
        paletteHeader[5] = 6; paletteHeader[7] = 32; paletteHeader[16] = 8;
        Save("palette.tga", [..paletteHeader, ..Bgra(Pattern), 0, 1, 2, 3, 4, 5]);
        Equal("palette.tga", Pattern, "TGA indexed colour map and alpha");
        byte[] paletteRleHeader = paletteHeader.ToArray(); paletteRleHeader[2] = 9;
        Save("palette-rle.tga", [..paletteRleHeader, ..Bgra(Pattern), 2, 0, 1, 2, 2, 3, 4, 5]);
        Equal("palette-rle.tga", Pattern, "TGA top-left indexed RLE stride before palette expansion");
        byte[] runTga = Tga(32, 2, false)[..18]; runTga[2] = 10;
        Save("run.tga", [..runTga, 130, 43, 17, 231, 31, 130, 43, 17, 231, 31]);
        Equal("run.tga", Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 231, 17, 43, 31 }).ToArray(), "TGA RLE repeated packet");
        Save("uncompressed.dds", Dds(3, 2, 0, 32, Bgra(Pattern)));
        Equal("uncompressed.dds", Pattern, "DDS BGRA exact straight alpha");
        byte[] rgbaDds = Dds(3, 2, 0, 32, Pattern);
        BitConverter.GetBytes(0xffu).CopyTo(rgbaDds, 92);
        BitConverter.GetBytes(0xff0000u).CopyTo(rgbaDds, 100);
        Save("rgba.dds", rgbaDds);
        Equal("rgba.dds", Pattern, "DDS RGBA masks normalized by Pfim");
        Save("dds-sphere.sph", Dds(3, 2, 0, 32, Bgra(Pattern)));
        Equal("dds-sphere.sph", Pattern, "DDS signature works under sphere extension");
        Save("opaque.dds", Dds(3, 2, 0, 32, Bgra(Pattern), alpha: false));
        Equal("opaque.dds", Opaque(Pattern), "DDS BGRX ignored alpha becomes opaque");
        Save("stride24.dds", Dds(3, 2, 0, 24, Bgra(Pattern).Where((_, index) => index % 4 != 3).ToArray()));
        Equal("stride24.dds", Opaque(Pattern), "DDS 24-bit odd-width decoded stride");
        Save("dx10.dds", Dds10(28, 1, Pattern));
        Equal("dx10.dds", Pattern, "DDS DX10 RGBA bytes normalized by Pfim");
        Save("dx10-bgra.dds", Dds10(87, 1, Bgra(Pattern)));
        Equal("dx10-bgra.dds", Pattern, "DDS DX10 BGRA bytes preserve alpha");
        Save("dx10-bgrx.dds", Dds10(93, 0, Bgra(Pattern)));
        Equal("dx10-bgrx.dds", Opaque(Pattern), "DDS DX10 BGRX unused fourth byte remains opaque");
        byte[] redBlock = [0, 0xf8, 0, 0, 0, 0, 0, 0];
        Save("bc1.dds", Dds(4, 4, FourCc("DXT1"), 0, redBlock));
        Equal("bc1.dds", Repeat(4, 4, [255, 0, 0, 255]), "DDS BC1 decompression and channel order");
        Save("bc1-transparent.dds", Dds(4, 4, FourCc("DXT1"), 0, [0, 0, 0xe0, 0x07, 255, 255, 255, 255]));
        Equal("bc1-transparent.dds", Repeat(4, 4, [0, 0, 0, 0]), "DDS BC1 one-bit transparency preserved");
        Save("bc2.dds", Dds(4, 4, FourCc("DXT3"), 0, [..Enumerable.Repeat((byte)0x88, 8), ..redBlock]));
        Equal("bc2.dds", Repeat(4, 4, [255, 0, 0, 136]), "DDS BC2 explicit alpha remains straight");
        Save("bc3.dds", Dds(4, 4, FourCc("DXT5"), 0, [93, 0, 0, 0, 0, 0, 0, 0, ..redBlock]));
        Equal("bc3.dds", Repeat(4, 4, [255, 0, 0, 93]), "DDS BC3 interpolated alpha remains straight");
        Save("bc1-nonmultiple.dds", Dds(3, 2, FourCc("DXT1"), 0, redBlock));
        Equal("bc1-nonmultiple.dds", Repeat(3, 2, [255, 0, 0, 255]), "DDS non-block-sized output ignores padding");
        Save("dx10-bc1.dds", Dds10(71, 1, redBlock));
        Equal("dx10-bc1.dds", Repeat(3, 2, [255, 0, 0, 255]), "DDS DX10 BC1 decoded stride");
        Save("dx10-bc2.dds", Dds10(74, 1, [..Enumerable.Repeat((byte)0x88, 8), ..redBlock]));
        Equal("dx10-bc2.dds", Repeat(3, 2, [255, 0, 0, 136]), "DDS DX10 BC2 straight alpha and cropped stride");
        Save("dx10-bc3.dds", Dds10(77, 1, [93, 0, 0, 0, 0, 0, 0, 0, ..redBlock]));
        Equal("dx10-bc3.dds", Repeat(3, 2, [255, 0, 0, 93]), "DDS DX10 BC3 straight alpha and cropped stride");
        Require(EditorTextureLoader.Load(output, "missing.tga") is null, "Missing texture returns null");
        Save("unsupported.bin", [1, 2, 3, 4, 5]);
        Invalid("unsupported.bin", "Existing unsupported image fails explicitly");
        Save("truncated.tga", Tga(32, 0, false)[..20]);
        Invalid("truncated.tga", "Truncated TGA fails instead of zero-filling");
        Save("truncated-rle.tga", [..runTga, 130, 43]);
        Invalid("truncated-rle.tga", "Truncated TGA RLE packet fails");
        Save("overflow-rle.tga", [..runTga, 131, 43, 17, 231, 31]);
        Invalid("overflow-rle.tga", "TGA RLE row overflow fails before unsafe decoding");
        Save("invalid-index.tga", [..paletteHeader, ..Bgra(Pattern), 0, 1, 6, 3, 4, 5]);
        Invalid("invalid-index.tga", "TGA out-of-range palette index fails explicitly");
        Save("truncated.dds", Dds(4, 4, FourCc("DXT5"), 0, [1, 2]));
        Invalid("truncated.dds", "Truncated DDS fails explicitly");
        Invalid("../outside.png", "Parent path retains existing containment policy");
        Invalid(Path.Combine(output, "original.png"), "Absolute texture path rejected");
        byte[] giant = Tga(32, 2, false); giant[12] = 1; giant[13] = 16;
        Save("giant.tga", giant);
        Invalid("giant.tga", "Existing SceneTexture 4096 dimension limit checked before allocation");
        Save("premultiplied.dds", Dds(4, 4, FourCc("DXT2"), 0, new byte[16]));
        Invalid("premultiplied.dds", "Premultiplied DDS codec rejected explicitly");
        Save("dx10-premultiplied.dds", Dds10(28, 2, Pattern));
        Invalid("dx10-premultiplied.dds", "DX10 premultiplied alpha rejected explicitly");
        byte[] dx10Pitch = Dds10(28, 1, Pattern); BitConverter.GetBytes(16).CopyTo(dx10Pitch, 20);
        Save("dx10-padded.dds", dx10Pitch);
        Invalid("dx10-padded.dds", "DDS DX10 custom input pitch rejected instead of reading wrong rows");
        Save("float.dds", Dds10(41, 1, Pattern));
        Invalid("float.dds", "DDS float input rejected instead of treating floats as colour bytes");
        byte[] cube = Dds(3, 2, 0, 32, Bgra(Pattern)); cube[112] = 0; cube[113] = 2;
        Save("cube.dds", cube);
        Invalid("cube.dds", "DDS cubemap rejected instead of silently choosing a face");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { EditorTextureLoader.Load(output, "original.png", cancellation.Token); throw new Exception("Cancellation was ignored."); }
        catch (OperationCanceledException) { Checks.Add("Cancellation propagated"); }
    }

    private static void Save(string name, byte[] bytes)
    {
        string path = Path.Combine(output, name); File.WriteAllBytes(path, bytes);
        Hashes.Add(path, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    private static void Equal(string name, byte[] expected, string check)
    {
        SceneTexture image = EditorTextureLoader.Load(output, name) ?? throw new Exception("Texture missing: " + name);
        Require(image.Rgba.SequenceEqual(expected), check);
    }
    private static void Invalid(string name, string check)
    {
        try { EditorTextureLoader.Load(output, name); throw new Exception("Expected explicit failure: " + check); }
        catch (InvalidDataException error) { Require(error.Message.Contains(name, StringComparison.Ordinal), check); }
    }
    private static void Require(bool condition, string check) { if (!condition) throw new Exception(check); Checks.Add(check); }
    private static byte[] Opaque(byte[] data) { byte[] copy = data.ToArray(); for (int i = 3; i < copy.Length; i += 4) copy[i] = 255; return copy; }
    private static byte[] Bgra(byte[] data) { byte[] copy = data.ToArray(); for (int i = 0; i < copy.Length; i += 4) (copy[i], copy[i + 2]) = (copy[i + 2], copy[i]); return copy; }
    private static byte[] Repeat(int width, int height, byte[] pixel) => Enumerable.Range(0, width * height).SelectMany(_ => pixel).ToArray();
    private static uint FourCc(string text) => BitConverter.ToUInt32(Encoding.ASCII.GetBytes(text));
    private static byte[] Tga(int bits, int origin, bool rle)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 0, 0, (byte)(rle ? 10 : 2), 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 2, 0, (byte)bits, (byte)((origin << 4) | (bits == 32 ? 8 : 0)) });
        for (int row = 0; row < 2; row++)
        {
            if (rle) writer.Write((byte)2); // Three raw pixels in each RLE packet.
            for (int column = 0; column < 3; column++)
            {
                int x = (origin & 1) != 0 ? 2 - column : column, y = (origin & 2) != 0 ? row : 1 - row;
                int pixel = (y * 3 + x) * 4;
                writer.Write(Pattern[pixel + 2]); writer.Write(Pattern[pixel + 1]); writer.Write(Pattern[pixel]);
                if (bits == 32) writer.Write(Pattern[pixel + 3]);
            }
        }
        return stream.ToArray();
    }
    private static byte[] Dds(int width, int height, uint fourCc, int bits, byte[] pixels, bool alpha = true)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(FourCc("DDS ")); writer.Write(124); writer.Write(0x1007); writer.Write(height); writer.Write(width);
        writer.Write(fourCc == 0 ? width * bits / 8 : pixels.Length); writer.Write(0); writer.Write(0); writer.Write(new byte[44]);
        writer.Write(32); writer.Write(fourCc != 0 ? 4 : 0x40 | (alpha ? 1 : 0)); writer.Write(fourCc); writer.Write(bits);
        writer.Write(fourCc == 0 ? 0xff0000 : 0); writer.Write(fourCc == 0 ? 0xff00 : 0); writer.Write(fourCc == 0 ? 0xff : 0);
        writer.Write(fourCc == 0 && bits == 32 && alpha ? 0xff000000u : 0); writer.Write(0x1000); writer.Write(new byte[16]); writer.Write(pixels);
        return stream.ToArray();
    }
    private static byte[] Dds10(uint format, uint alphaMode, byte[] pixels)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Dds(3, 2, FourCc("DX10"), 0, [])[..128]);
        writer.Write(format); writer.Write(3); writer.Write(0); writer.Write(1); writer.Write(alphaMode); writer.Write(pixels);
        return stream.ToArray();
    }
    private static byte[] Bmp()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4d42); writer.Write(78); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(3); writer.Write(2); writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(24); writer.Write(new byte[16]);
        for (int y = 1; y >= 0; y--) { for (int x = 0; x < 3; x++) { int i = (y * 3 + x) * 4; writer.Write(Pattern[i + 2]); writer.Write(Pattern[i + 1]); writer.Write(Pattern[i]); } writer.Write(new byte[3]); }
        return stream.ToArray();
    }
}
