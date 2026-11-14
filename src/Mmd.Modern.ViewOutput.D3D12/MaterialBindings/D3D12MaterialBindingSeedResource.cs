using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>MDA-owned source-local XYZ float resource, independent of native Vector3 layout.</summary>
internal static class D3D12MaterialBindingSeedResource
{
    internal const string Encoding = "float32-le-vec3";
    internal const string RelativePath = "rest/raw-radial-seeds.f32le";

    internal static int ByteLength(int count)
    {
        if (count < 0) throw new InvalidDataException("Radial seed vertex count must be nonnegative.");
        long bytes = checked(12L * count);
        if (bytes > int.MaxValue) throw new InvalidDataException("Radial seed resource exceeds the supported byte length.");
        return (int)bytes;
    }

    // Hash without allocating the binary payload; the caller reserves every archive budget first.
    internal static string Digest(SceneRawRadialSeeds seeds)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[12];
        foreach (var value in seeds.Vertices) { Write(bytes, value); hash.AppendData(bytes); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static byte[] Encode(SceneRawRadialSeeds seeds)
    {
        var bytes = new byte[ByteLength(seeds.Vertices.Length)];
        for (int i = 0; i < seeds.Vertices.Length; i++) Write(bytes.AsSpan(i * 12, 12), seeds.Vertices[i]);
        return bytes;
    }

    internal static SceneRawRadialSeeds Decode(ReadOnlySpan<byte> bytes, int count)
    {
        if (bytes.Length != ByteLength(count)) throw new InvalidDataException("Radial seed resource has an invalid byte length.");
        // Validate all values before allocating the detached vertex stream.
        for (int offset = 0; offset < bytes.Length; offset += 4)
            if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset, 4))))
                throw new InvalidDataException("Radial seed resource must contain finite float32 values.");
        var values = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            var xyz = bytes.Slice(i * 12, 12);
            values[i] = new(BinaryPrimitives.ReadSingleLittleEndian(xyz), BinaryPrimitives.ReadSingleLittleEndian(xyz[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(xyz[8..]));
        }
        return new(values);
    }

    internal static bool SameBits(SceneRawRadialSeeds a, SceneRawRadialSeeds b)
    {
        if (a.Vertices.Length != b.Vertices.Length) return false;
        for (int i = 0; i < a.Vertices.Length; i++)
        {
            Vector3 x = a.Vertices[i], y = b.Vertices[i];
            if (BitConverter.SingleToInt32Bits(x.X) != BitConverter.SingleToInt32Bits(y.X)
                || BitConverter.SingleToInt32Bits(x.Y) != BitConverter.SingleToInt32Bits(y.Y)
                || BitConverter.SingleToInt32Bits(x.Z) != BitConverter.SingleToInt32Bits(y.Z)) return false;
        }
        return true;
    }

    private static void Write(Span<byte> bytes, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], value.Z);
    }
}
