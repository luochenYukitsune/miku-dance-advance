using Mmd.Modern.Core;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Mmd.Modern.Smoke;

public static class SceneTextureLoader
{
    /// <summary>Loads local PNG/BMP/JPEG as straight-alpha RGBA8. No colour-space conversion is applied.</summary>
    /// <returns>Null for missing files or unsupported extensions; malformed supported images throw.</returns>
    public static SceneTexture? Load(string modelDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string relative = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("Texture path must be relative to the model directory.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDirectory)) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root, comparison))
            throw new InvalidDataException("Texture path leaves the model directory.");
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".bmp" or ".jpg" or ".jpeg") || !File.Exists(path)) return null;

        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        if (codec is null) throw new InvalidDataException($"Cannot decode texture '{relativePath}'.");
        var header = codec.Info;
        if (header.Width is < 1 or > 4096 || header.Height is < 1 or > 4096)
            throw new InvalidDataException($"Texture '{relativePath}' dimensions must be in 1..4096.");
        var info = new SKImageInfo(header.Width, header.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (bitmap.GetPixels() == IntPtr.Zero) throw new InvalidDataException($"Cannot allocate texture '{relativePath}'.");
        SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
            throw new InvalidDataException($"Texture '{relativePath}' is incomplete or invalid ({result}).");
        byte[] rgba = new byte[checked(header.Width * header.Height * 4)];
        for (int y = 0; y < header.Height; y++)
            Marshal.Copy(IntPtr.Add(bitmap.GetPixels(), y * bitmap.RowBytes), rgba, y * header.Width * 4, header.Width * 4);
        return new SceneTexture(relativePath, header.Width, header.Height, rgba);
    }
}
