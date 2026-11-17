using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using SkiaSharp;

namespace Mmd.Modern.Demo;

public static class FrameViewportCapture
{
    public static void SavePng(RenderedFrame frame, string path)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int row = 0; row < frame.Height; row++) Marshal.Copy(frame.Rgba, row * frame.Width * 4,
            IntPtr.Add(bitmap.GetPixels(), row * bitmap.RowBytes), frame.Width * 4);
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path); png.SaveTo(output);
    }
}
