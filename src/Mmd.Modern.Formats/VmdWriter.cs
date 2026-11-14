// VMD layout reference: saba VMDFile.h/.cpp, MIT, Copyright (c) 2016-2017 benikabocha.
// Serialization cross-check: nanoem component ext/mutable.c (MIT, Copyright (c) 2015-2023 hkrn).
// Independent managed implementation: preserves all raw curve bytes and record order.

using System.Numerics;
using System.Text;

namespace Mmd.Modern.Formats;

/// <summary>Writes legacy VMD or VMD0002 without normalizing values, sorting records or owning the destination.</summary>
public static class VmdWriter
{
    private const int MaxElementCount = 2_000_000;
    private const long MaxOutputBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Validates and prepares the entire motion before writing at the current stream position.
    /// Invalid motion data leaves the destination untouched; destination I/O failures may partially write.
    /// </summary>
    public static void Write(Stream stream, VmdMotion motion, VmdWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
            throw new ArgumentException("VMD output stream must be writable.", nameof(stream));
        byte[] bytes = ToBytes(motion, options);
        stream.Write(bytes);
    }

    /// <summary>Returns a complete VMD file, including all six section counts; defaults to VMD0002.</summary>
    public static byte[] ToBytes(VmdMotion motion, VmdWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(motion);
        int version = options?.Version ?? 2;
        if (version is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(options), version, "VMD export version must be 1 or 2.");
        int modelNameWidth = version == 1 ? 10 : 20;
        Encoding encoding = VmdEncoding.Create(options?.AnsiCodePage ?? 932);
        // Snapshot the outer lists because callers may have supplied mutable collections.
        VmdBoneKeyframe[] bones = Snapshot(motion.BoneKeyframes, "bones");
        VmdMorphKeyframe[] morphs = Snapshot(motion.MorphKeyframes, "morphs");
        VmdCameraKeyframe[] cameras = Snapshot(motion.CameraKeyframes, "cameras");
        VmdLightKeyframe[] lights = Snapshot(motion.LightKeyframes, "lights");
        VmdSelfShadowKeyframe[] shadows = Snapshot(motion.SelfShadowKeyframes, "shadows");
        VmdIkKeyframe[] ik = Snapshot(motion.IkKeyframes, "ik");

        long switches = 0;
        foreach (VmdIkKeyframe key in ik)
        {
            switches += key.Switches.Count;
            if (switches > MaxElementCount)
                throw new ArgumentException("Aggregate VMD IK switch count exceeds safety limit.", nameof(motion));
        }
        long length = 54 + modelNameWidth + 111L * bones.Length + 23L * morphs.Length + 61L * cameras.Length
            + 28L * lights.Length + 9L * shadows.Length + 9L * ik.Length + 21L * switches;
        if (length > MaxOutputBytes)
            throw new ArgumentException($"VMD output exceeds safety limit {MaxOutputBytes} bytes.", nameof(motion));

        // A private fixed-size buffer makes preparation independent of the destination and bounds allocation.
        byte[] bytes = new byte[(int)length];
        using var buffer = new MemoryStream(bytes, writable: true);
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        WriteName(writer, version == 1 ? "Vocaloid Motion Data file" : "Vocaloid Motion Data 0002", 30, "header.signature", encoding);
        WriteName(writer, options?.ModelNameOverride ?? motion.ModelName, modelNameWidth, "header.modelName", encoding,
            options?.ModelNameOverride is null ? motion.OriginalModelName : null, options?.PreserveOriginalNames != false);
        writer.Write((uint)bones.Length);
        for (int i = 0; i < bones.Length; i++)
        {
            VmdBoneKeyframe key = bones[i];
            string field = $"bones[{i}]";
            WriteName(writer, key.BoneName, 15, field + ".name", encoding, key.OriginalName, options?.PreserveOriginalNames != false);
            writer.Write(key.Frame);
            WriteVector3(writer, key.Translation, field + ".translation");
            Quaternion rotation = key.Rotation;
            if (rotation.X == 0 && rotation.Y == 0 && rotation.Z == 0 && rotation.W == 0)
                throw new ArgumentException("VMD zero quaternion has no rotation.", field + ".rotation");
            WriteSingle(writer, rotation.X, field + ".rotation.x");
            WriteSingle(writer, rotation.Y, field + ".rotation.y");
            WriteSingle(writer, rotation.Z, field + ".rotation.z");
            WriteSingle(writer, rotation.W, field + ".rotation.w");
            WriteInterpolation(writer, key.Interpolation, 64, field + ".interpolation");
        }

        writer.Write((uint)morphs.Length);
        for (int i = 0; i < morphs.Length; i++)
        {
            VmdMorphKeyframe key = morphs[i];
            string field = $"morphs[{i}]";
            WriteName(writer, key.MorphName, 15, field + ".name", encoding, key.OriginalName, options?.PreserveOriginalNames != false);
            writer.Write(key.Frame);
            WriteSingle(writer, key.Weight, field + ".weight");
        }

        writer.Write((uint)cameras.Length);
        for (int i = 0; i < cameras.Length; i++)
        {
            VmdCameraKeyframe key = cameras[i];
            string field = $"cameras[{i}]";
            writer.Write(key.Frame);
            WriteSingle(writer, key.Distance, field + ".distance");
            WriteVector3(writer, key.Interest, field + ".interest");
            WriteVector3(writer, key.Rotation, field + ".rotation");
            WriteInterpolation(writer, key.Interpolation, 24, field + ".interpolation");
            writer.Write(key.ViewAngle);
            writer.Write((byte)(key.Perspective ? 0 : 1));
        }

        writer.Write((uint)lights.Length);
        for (int i = 0; i < lights.Length; i++)
        {
            VmdLightKeyframe key = lights[i];
            string field = $"lights[{i}]";
            writer.Write(key.Frame);
            WriteVector3(writer, key.Color, field + ".color");
            WriteVector3(writer, key.Direction, field + ".direction");
        }

        writer.Write((uint)shadows.Length);
        for (int i = 0; i < shadows.Length; i++)
        {
            VmdSelfShadowKeyframe key = shadows[i];
            string field = $"shadows[{i}]";
            writer.Write(key.Frame);
            if (key.Mode > 2)
                throw new ArgumentException("VMD shadow mode must be 0, 1 or 2.", field + ".mode");
            writer.Write(key.Mode);
            WriteSingle(writer, key.Distance, field + ".distance");
        }

        writer.Write((uint)ik.Length);
        for (int i = 0; i < ik.Length; i++)
        {
            VmdIkKeyframe key = ik[i];
            writer.Write(key.Frame);
            writer.Write((byte)(key.Visible ? 1 : 0));
            writer.Write((uint)key.Switches.Count);
            for (int j = 0; j < key.Switches.Count; j++)
            {
                VmdIkSwitch item = key.Switches[j];
                WriteName(writer, item.BoneName, 20, $"ik[{i}].bones[{j}].name", encoding, item.OriginalName, options?.PreserveOriginalNames != false);
                writer.Write((byte)(item.Enabled ? 1 : 0));
            }
        }
        return bytes;
    }

    private static T[] Snapshot<T>(IReadOnlyList<T> values, string field) where T : class
    {
        ArgumentNullException.ThrowIfNull(values, field);
        if (values.Count > MaxElementCount)
            throw new ArgumentException("VMD element count exceeds safety limit.", field);
        T[] snapshot = values.ToArray();
        if (snapshot.Length > MaxElementCount || snapshot.Any(value => value is null))
            throw new ArgumentException("VMD records must be non-null and within the element safety limit.", field);
        return snapshot;
    }

    private static void WriteName(BinaryWriter writer, string value, int width, string field, Encoding encoding,
        VmdEncodedName? original = null, bool preserve = false)
    {
        ArgumentNullException.ThrowIfNull(value, field);
        if (value.Contains('\0'))
            throw new ArgumentException("VMD names cannot contain embedded NUL characters.", field);
        if (preserve && original is not null)
        {
            if (!string.Equals(original.Value, value, StringComparison.Ordinal))
                throw new ArgumentException("VMD original name is stale; clear it or disable preservation after renaming.", field);
            if (original.Bytes.Count > width)
                throw new ArgumentException($"VMD original name exceeds its {width}-byte field.", field);
            Span<byte> preserved = stackalloc byte[width];
            preserved.Clear();
            for (int i = 0; i < original.Bytes.Count; i++) preserved[i] = original.Bytes[i];
            writer.Write(preserved);
            return;
        }
        try
        {
            if (encoding.GetByteCount(value) > width)
                throw new ArgumentException($"VMD name exceeds its {width}-byte CP{encoding.CodePage} field.", field);
            Span<byte> bytes = stackalloc byte[width];
            bytes.Clear();
            encoding.GetBytes(value.AsSpan(), bytes);
            writer.Write(bytes);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException($"VMD name cannot be encoded as CP{encoding.CodePage}.", field, exception);
        }
    }

    private static void WriteSingle(BinaryWriter writer, float value, string field)
    {
        if (!float.IsFinite(value))
            throw new ArgumentException("VMD values must be finite.", field);
        writer.Write(value);
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value, string field)
    {
        WriteSingle(writer, value.X, field + ".x");
        WriteSingle(writer, value.Y, field + ".y");
        WriteSingle(writer, value.Z, field + ".z");
    }

    private static void WriteInterpolation(BinaryWriter writer, IReadOnlyList<byte> value, int width, string field)
    {
        ArgumentNullException.ThrowIfNull(value, field);
        byte[] bytes = value.ToArray();
        if (bytes.Length != width)
            throw new ArgumentException($"VMD interpolation must contain exactly {width} bytes.", field);
        writer.Write(bytes);
    }

}
