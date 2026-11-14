using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

internal sealed partial class D3D9SurfaceFrameEncoder
{
    private static (byte[] Frame, byte[] Visual) SerializeClassicMedia(
        (byte[] Frame, byte[] Visual) original, D3D9ClassicMediaInputs? media)
    {
        if (media is null || (media.Layers.Count == 0 && media.ScreenCaptureMode is null)) return original;
        bool screen = media.ScreenCaptureMode is not null;
        long length = checked(original.Frame.LongLength + 28L + (screen ? 4L : 0L) + media.Layers.Sum(layer => 32L + (screen ? 4L : 0L) + layer.Rgba.Length));
        if (length > 1_073_741_824) throw new NotSupportedException("Classic backgrounds exceed the native frame packet budget.");
        using var stream = new MemoryStream(checked((int)length));
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var prefix = original.Frame.ToArray();
            WriteCount(prefix, 8, 6);
            BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(208),
                BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(208)) | 16U);
            writer.Write(prefix); writer.Write("MDA9MED1"u8); U(writer, screen ? 2 : 1);
            writer.Write(BinaryPrimitives.ReadDoubleLittleEndian(prefix.AsSpan(40)));
            writer.Write(BinaryPrimitives.ReadSingleLittleEndian(prefix.AsSpan(48)));
            if (screen) U(writer, (int)media.ScreenCaptureMode!.Value);
            U(writer, media.Layers.Count);
            foreach (var layer in media.Layers)
            {
                U(writer, (int)layer.Kind); if (screen) U(writer, layer.DrawBackground ? 1 : 0); U(writer, layer.Width); U(writer, layer.Height);
                F(writer, layer.Rectangle.Left); F(writer, layer.Rectangle.Top);
                F(writer, layer.Rectangle.Right); F(writer, layer.Rectangle.Bottom);
                U(writer, layer.Rgba.Length); writer.Write(layer.Rgba.Span);
            }
        }
        var frame = stream.ToArray(); var visual = original.Visual.ToArray();
        WriteCount(visual, 8, 7); SHA256.HashData(frame).CopyTo(visual.AsSpan(20));
        return (frame, visual);
    }
}
