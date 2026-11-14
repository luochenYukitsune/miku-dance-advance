namespace Mmd.Modern.Core;

public static class FrameImage
{
    public static byte[] ToBmp(RenderedFrame frame)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        int dataSize = checked(frame.Width * frame.Height * 4);
        if (frame.Rgba.Length != dataSize) throw new ArgumentException("RGBA buffer size mismatch.", nameof(frame));
        writer.Write((ushort)0x4d42); writer.Write(54 + dataSize); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(frame.Width); writer.Write(-frame.Height);
        writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0); writer.Write(dataSize);
        writer.Write(2835); writer.Write(2835); writer.Write(0); writer.Write(0);
        for (int i = 0; i < dataSize; i += 4)
        {
            writer.Write(frame.Rgba[i + 2]); writer.Write(frame.Rgba[i + 1]); writer.Write(frame.Rgba[i]); writer.Write(frame.Rgba[i + 3]);
        }
        return stream.ToArray();
    }
}
