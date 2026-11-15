using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Pfim;
using SkiaSharp;

namespace Mmd.Modern.App;

/// <summary>Read-only model texture decoding to top-left, straight-alpha RGBA8. Skia handles
/// ordinary images (including BMP sphere maps); Pfim 0.11.4 handles TGA and DDS colour images.</summary>
public static class EditorTextureLoader
{
    // This is the existing SceneTexture contract, independent of PMX/PMA input byte budgets.
    private const int MaximumDimension = 4096;
    /// <summary>Decoded RGBA allowed for one model's textures. One texture is capped at 4096² (64 MiB), but a model
    /// could list hundreds; without a total, a crafted or oversized model exhausted memory while opening.</summary>
    public const long MaximumModelTextureBytes = 1L << 30;

    /// <summary>Wraps a per-model texture loader so the model's decoded textures stay within
    /// <see cref="MaximumModelTextureBytes"/>. Each distinct returned texture is counted once.
    /// One unusable texture (outside the model folder, absolute path, oversized, undecodable, unreadable, or past
    /// the model budget) degrades to the missing-image fallback, like a missing file, instead of failing the whole
    /// model; <paramref name="failures"/> receives the reason. Cancellation and out-of-memory still propagate.</summary>
    public static Func<string, SceneTexture?> WithModelBudget(Func<string, SceneTexture?> load, long maximumBytes = MaximumModelTextureBytes,
        Action<string>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        long total = 0;
        var counted = new HashSet<SceneTexture>(ReferenceEqualityComparer.Instance);
        void Report(string message)
        {
            if (failures is not null) failures(message);
            else System.Diagnostics.Trace.TraceWarning(message);
        }
        return relative =>
        {
            SceneTexture? texture;
            try { texture = load(relative); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or IOException
                or UnauthorizedAccessException or ArgumentException)
            {
                Report(EditorLocalization.Format("贴图“{0}”未载入，按缺图处理：{1}", relative, error.Message));
                return null;
            }
            if (texture is not null && counted.Add(texture))
            {
                if (total + texture.Rgba.Length > maximumBytes)
                {
                    counted.Remove(texture);
                    Report(EditorLocalization.Format("贴图“{0}”未载入，按缺图处理：{1}", relative,
                        EditorLocalization.Format("模型贴图解码后的总大小超过 {0} MiB 上限。", maximumBytes / (1024 * 1024))));
                    return null;
                }
                total += texture.Rgba.Length;
            }
            return texture;
        };
    }

    public static SceneTexture? Load(string modelDirectory, string relative, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string path = ResolveLocalPath(modelDirectory, relative);
        if (!File.Exists(path)) return null;

        using var input = File.OpenRead(path);
        return Decode(input, relative, token);
    }

    internal static SceneTexture Decode(ReadOnlyMemory<byte> bytes, string authoredToken, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var input = new MemoryStream(bytes.ToArray(), writable: false);
        return Decode(input, authoredToken, token);
    }

    internal static string ResolveLocalPath(string modelDirectory, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        string normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new InvalidDataException(EditorLocalization.Format("贴图路径必须是模型目录内的相对路径：{0}", relative));
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDirectory)) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(folder, normalized));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(folder, comparison))
            throw new InvalidDataException(EditorLocalization.Format("贴图路径超出模型目录：{0}", relative));
        return path;
    }

    private static SceneTexture Decode(Stream input, string relative, CancellationToken token)
    {
        try
        {
            string extension = Path.GetExtension(relative).ToLowerInvariant();
            if (extension is ".tga" or ".dds") return LoadPfim(input, relative, extension == ".dds", token);
            // Probe before handing the stream to Skia, whose failed codec creation may close it.
            // Sphere maps can carry DDS bytes under .sph/.spa rather than a codec extension.
            Span<byte> magic = stackalloc byte[4];
            bool dds = input.Read(magic) == magic.Length && magic.SequenceEqual("DDS "u8);
            input.Position = 0;
            if (dds) return LoadPfim(input, relative, true, token);
            using var codec = SKCodec.Create(input);
            if (codec is null)
                throw new NotSupportedException(EditorLocalization.Format("无法识别贴图格式：{0}", relative));
            ValidateDimensions(codec.Info.Width, codec.Info.Height);
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            if (bitmap.GetPixels() == IntPtr.Zero) throw new InvalidDataException(EditorLocalization.Text("无法分配贴图像素。"));
            SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
            if (result != SKCodecResult.Success) throw new InvalidDataException(EditorLocalization.Format("贴图数据不完整或无效（{0}）。", result));
            byte[] rgba = new byte[checked(info.Width * info.Height * 4)];
            for (int row = 0; row < info.Height; row++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(IntPtr.Add(bitmap.GetPixels(), row * bitmap.RowBytes), rgba, row * info.Width * 4, info.Width * 4);
            }
            return new(relative, info.Width, info.Height, rgba);
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            throw new InvalidDataException(EditorLocalization.Format("无法载入贴图“{0}”：{1}", relative, error.Message), error);
        }
    }

    private static SceneTexture LoadPfim(Stream input, string name, bool dds, CancellationToken token)
    {
        var config = new PfimConfig(decompress: true, applyColorMap: true);
        bool flipX = false, flipY = false, opaque = false;
        if (dds) opaque = ValidateDds(input);
        else
        {
            // Pfim treats right-hand origins as BottomLeft, and its TopLeft RLE decoder
            // omits stride padding. Decode all origins through its BottomLeft branch,
            // then compensate during the BGRA/RGBA copy without modifying source bytes.
            input.Position = 17;
            int descriptor = input.ReadByte();
            if (descriptor < 0) throw new EndOfStreamException(EditorLocalization.Text("TGA 文件头不完整。"));
            input.Position = 0;
            var header = new TargaHeader(input, config);
            ValidateDimensions(header.Width, header.Height);
            bool gray = header.ImageType is TargaHeader.TargaImageType.UncompressedBW or TargaHeader.TargaImageType.RunLengthBW;
            bool indexed = header.ImageType is TargaHeader.TargaImageType.UncompressedColorMap or TargaHeader.TargaImageType.RunLengthColorMap;
            bool trueColor = header.ImageType is TargaHeader.TargaImageType.UncompressedTrueColor or TargaHeader.TargaImageType.RunLengthTrueColor;
            if ((!gray && !indexed && !trueColor) || (gray && header.PixelDepthBits != 8)
                || (indexed && (!header.HasColorMap || header.PixelDepthBits != 8 || header.ColorMapOrigin != 0 || header.ColorMapDepthBits is not (24 or 32)))
                || (trueColor && header.PixelDepthBits is not (24 or 32)))
                throw new NotSupportedException(EditorLocalization.Text("TGA 当前支持 8 位灰度／索引色和 24／32 位真彩色；此像素类型不受支持。"));
            if ((descriptor & 0xc0) != 0) throw new NotSupportedException(EditorLocalization.Text("不支持交错存储的 TGA。"));
            bool compressed = header.ImageType is TargaHeader.TargaImageType.RunLengthBW
                or TargaHeader.TargaImageType.RunLengthColorMap or TargaHeader.TargaImageType.RunLengthTrueColor;
            ValidateTargaPayload(input, header.Width, header.Height, header.PixelDepthBytes, compressed, token);
            flipX = header.Orientation is TargaHeader.TargaOrientation.BottomRight or TargaHeader.TargaOrientation.TopRight;
            flipY = header.Orientation is TargaHeader.TargaOrientation.TopLeft or TargaHeader.TargaOrientation.TopRight;
            opaque = !indexed && header.PixelDepthBits == 32 && (descriptor & 15) == 0;
        }
        input.Position = 0;
        token.ThrowIfCancellationRequested();
        using var targaInput = dds ? null : new BottomLeftTargaStream(input);
        using var image = dds ? (IImage)Dds.Create(input, config) : Targa.Create(targaInput!, config);
        ValidateDimensions(image.Width, image.Height);
        int bytes = image.Format switch
        {
            ImageFormat.Rgb8 => 1,
            ImageFormat.Rgb24 => 3,
            ImageFormat.Rgba32 => 4,
            _ => throw new NotSupportedException(EditorLocalization.Format("不支持此贴图像素类型：{0}。", image.Format))
        };
        long required = (long)(image.Height - 1) * image.Stride + image.Width * bytes;
        if (image.Compressed || image.Stride < image.Width * bytes || image.DataLen < required || image.Data.LongLength < required)
            throw new InvalidDataException(EditorLocalization.Text("解码后的贴图行跨度或像素长度无效。"));
        byte[] rgba = new byte[checked(image.Width * image.Height * 4)];
        for (int y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            int sourceRow = (flipY ? image.Height - 1 - y : y) * image.Stride;
            for (int x = 0; x < image.Width; x++)
            {
                int source = sourceRow + (flipX ? image.Width - 1 - x : x) * bytes;
                int target = (y * image.Width + x) * 4;
                // Pfim's Rgb24/Rgba32 data are BGR/BGRA, as its official Skia sample documents.
                rgba[target] = image.Data[source + (bytes == 1 ? 0 : 2)];
                rgba[target + 1] = image.Data[source + (bytes == 1 ? 0 : 1)];
                rgba[target + 2] = image.Data[source];
                rgba[target + 3] = bytes == 4 && !opaque ? image.Data[source + 3] : (byte)255;
            }
        }
        return new(name, image.Width, image.Height, rgba);
    }

    private static bool ValidateDds(Stream input)
    {
        var header = new DdsHeader(input);
        ValidateDimensions(header.Width, header.Height);
        if (header.Depth > 1 || header.Caps2 != 0 || header.MipMapCount > 13)
            throw new NotSupportedException(EditorLocalization.Text("DDS 当前只支持普通二维颜色贴图，不支持立方体／体积纹理。"));
        int blockBytes = header.PixelFormat.FourCC switch
        {
            CompressionAlgorithm.D3DFMT_DXT1 => 8,
            CompressionAlgorithm.D3DFMT_DXT3 or CompressionAlgorithm.D3DFMT_DXT5 => 16,
            CompressionAlgorithm.None or CompressionAlgorithm.DX10 => 0,
            _ => throw new NotSupportedException(EditorLocalization.Format("DDS 压缩类型不受支持：{0}。", header.PixelFormat.FourCC))
        };
        int bytes = checked((int)header.PixelFormat.RGBBitCount / 8);
        bool opaque = header.PixelFormat.ABitMask == 0;
        if (header.PixelFormat.FourCC == CompressionAlgorithm.DX10)
        {
            var dx10 = new DdsHeaderDxt10(input);
            if ((uint)dx10.ResourceDimension != 3 || dx10.ArraySize != 1 || (dx10.MiscFlag & 4) != 0
                || (dx10.MiscFlags2 & 7) is not (0 or 1 or 3))
                throw new NotSupportedException(EditorLocalization.Text("DDS 数组、立方体或预乘／自定义 Alpha 类型不受支持。"));
            // Only formats whose byte interpretation matches the RGBA8 scene contract.
            uint format = (uint)dx10.DxgiFormat;
            blockBytes = format switch
            {
                71 or 72 => 8, // BC1 UNorm / sRGB
                74 or 75 or 77 or 78 => 16, // BC2 / BC3 UNorm / sRGB
                28 or 29 or 87 or 91 or 93 => 0, // RGBA / BGRA / BGRX
                _ => throw new NotSupportedException(EditorLocalization.Format("DDS DXGI 像素类型不受支持：{0}。", dx10.DxgiFormat))
            };
            bytes = 4;
            opaque = format == 93 || (dx10.MiscFlags2 & 7) == 3;
        }
        else if (blockBytes == 0)
        {
            var pixel = header.PixelFormat;
            bool gray = pixel.RGBBitCount == 8 && pixel.PixelFormatFlags.HasFlag(DdsPixelFormatFlags.Luminance)
                && pixel.RBitMask == 0xff && pixel.GBitMask == 0 && pixel.BBitMask == 0 && pixel.ABitMask == 0;
            bool rgb = pixel.RGBBitCount is 24 or 32 && pixel.PixelFormatFlags.HasFlag(DdsPixelFormatFlags.Rgb)
                && pixel.GBitMask == 0xff00
                && ((pixel.RBitMask == 0xff0000 && pixel.BBitMask == 0xff) || (pixel.RBitMask == 0xff && pixel.BBitMask == 0xff0000))
                && (pixel.ABitMask == 0 || bytes == 4 && pixel.ABitMask == 0xff000000);
            if (!gray && !rgb) throw new NotSupportedException(EditorLocalization.Text("DDS 当前支持 8 位灰度及 24／32 位 RGB 颜色像素。"));
        }
        else opaque = false; // BC1/2/3 alpha is decoded by Pfim, independent of legacy RGB masks.
        if (blockBytes == 0 && header.PitchOrLinearSize != 0 && header.PitchOrLinearSize != header.Width * bytes)
            throw new NotSupportedException(EditorLocalization.Text("DDS 文件的自定义输入行跨度不受当前解码器支持。"));
        int width = (int)header.Width, height = (int)header.Height;
        long payload = 0;
        for (int level = 0; level < Math.Max(1, header.MipMapCount); level++)
        {
            payload += blockBytes > 0 ? (long)((width + 3) / 4) * ((height + 3) / 4) * blockBytes : (long)width * height * bytes;
            width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
        }
        if (input.Length - input.Position < payload) throw new EndOfStreamException(EditorLocalization.Text("DDS 像素或 mipmap 数据不完整。"));
        return opaque;
    }

    // Structural length validation only. Pfim remains the sole TGA palette/RLE pixel decoder;
    // its buffered RLE decoder can otherwise read zero-filled scratch bytes past truncated data.
    private static void ValidateTargaPayload(Stream input, int width, int height, int bytes, bool rle, CancellationToken token)
    {
        if (!rle)
        {
            if (input.Length - input.Position < (long)width * height * bytes)
                throw new EndOfStreamException(EditorLocalization.Text("TGA 像素数据不完整。"));
            return;
        }
        for (int row = 0; row < height; row++)
        {
            token.ThrowIfCancellationRequested();
            int remaining = width;
            while (remaining > 0)
            {
                int packet = input.ReadByte();
                if (packet < 0) throw new EndOfStreamException(EditorLocalization.Text("TGA RLE 数据不完整。"));
                int count = (packet & 127) + 1;
                if (count > remaining) throw new InvalidDataException(EditorLocalization.Text("TGA RLE 数据包超出当前行。"));
                long length = (packet < 128 ? count : 1) * bytes;
                if (input.Length - input.Position < length) throw new EndOfStreamException(EditorLocalization.Text("TGA RLE 像素数据不完整。"));
                input.Position += length;
                remaining -= count;
            }
        }
    }

    private static void ValidateDimensions(long width, long height)
    {
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension)
            throw new InvalidDataException(EditorLocalization.Format("贴图尺寸必须在 1..{0} 像素内。", MaximumDimension));
    }

    // Virtualize only the two origin bits in the TGA header. Pfim remains the sole
    // palette/RLE pixel decoder; the underlying stream stays read-only and open.
    private sealed class BottomLeftTargaStream(Stream source) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            long start = source.Position;
            int read = source.Read(buffer);
            if (start <= 17 && start + read > 17) buffer[(int)(17 - start)] &= 0xcf;
            return read;
        }
        public override int ReadByte()
        {
            long position = source.Position;
            int value = source.ReadByte();
            return position == 17 && value >= 0 ? value & 0xcf : value;
        }
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
