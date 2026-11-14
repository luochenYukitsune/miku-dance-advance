// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// VPDFile.cpp grammar and xyzw component order; bounded strict decoding and diagnostics are local adaptations.
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Mmd.Modern.Formats;

/// <summary>Reads VPD bones and the trailing Saba Morph extension, leaving the stream open.</summary>
public static class VpdReader
{
    /// <summary>
    /// Reads from the current position without coordinate conversion or quaternion normalization.
    /// Auto recognizes a UTF8 BOM, then tries strict UTF8 and finally CP932. Specify CP932
    /// explicitly for files whose bytes happen to be valid in both encodings.
    /// </summary>
    public static VpdPose Read(Stream stream, VpdReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("VPD input stream must be readable.", nameof(stream));
        options ??= new();
        if (options.MaxInputBytes < 0) throw new ArgumentOutOfRangeException(nameof(options), "Input limit must be nonnegative.");
        if (options.MaxElementCount < 0) throw new ArgumentOutOfRangeException(nameof(options), "Element limit must be nonnegative.");
        if (!Enum.IsDefined(options.Encoding)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown text encoding.");

        using var input = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (input.Length + read > options.MaxInputBytes)
                throw new VpdFormatException("input.length", 0, "input byte limit exceeded");
            input.Write(buffer, 0, read);
        }
        string text = VpdText.Decode(input.GetBuffer().AsSpan(0, (int)input.Length), options.Encoding);
        var lines = new PoseLines(text);
        if (lines.ReadHeader() != "Vocaloid Pose Data file")
            throw new VpdFormatException("header.signature", 1, "expected 'Vocaloid Pose Data file'");

        string parent = Statement(lines.Next("header.parentFileName"), "header.parentFileName");
        VpdText.ValidateName(parent, "header.parentFileName", lines.LineNumber);
        PoseLine countLine = lines.Next("bones.count");
        string countText = Statement(countLine, "bones.count");
        if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count > options.MaxElementCount)
            throw new VpdFormatException("bones.count", countLine.Number, "invalid bone count or element limit exceeded");

        var bones = new List<VpdBonePose>();
        var boneIndices = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            string field = $"bones[{i}]";
            (int index, string name) = RecordHeader(lines.Next(field + ".index"), "Bone", field, count);
            if (!boneIndices.Add(index))
                throw new VpdFormatException(field + ".index", lines.LineNumber, "duplicate bone index");
            float[] xyz = Numbers(lines.Next(field + ".translation"), 3, field + ".translation");
            PoseLine rotationLine = lines.Next(field + ".rotation");
            float[] xyzw = Numbers(rotationLine, 4, field + ".rotation");
            Quaternion rotation = new(xyzw[0], xyzw[1], xyzw[2], xyzw[3]);
            VpdText.ValidateRotation(rotation, field + ".rotation", rotationLine.Number);
            End(lines.Next(field + ".end"), field + ".end");
            bones.Add(new VpdBonePose
            {
                Index = index, BoneName = name, Translation = new(xyz[0], xyz[1], xyz[2]), Rotation = rotation,
            });
        }

        var morphs = new List<VpdMorphPose>();
        var morphIndices = new HashSet<int>();
        while (lines.TryNext(out PoseLine morphLine))
        {
            string field = $"morphs[{morphs.Count}]";
            if ((long)count + morphs.Count >= options.MaxElementCount)
                throw new VpdFormatException("morphs.count", morphLine.Number, "element limit exceeded");
            (int index, string name) = RecordHeader(morphLine, "Morph", field, null);
            if (!morphIndices.Add(index))
                throw new VpdFormatException(field + ".index", morphLine.Number, "duplicate morph index");
            float weight = Numbers(lines.Next(field + ".weight"), 1, field + ".weight")[0];
            End(lines.Next(field + ".end"), field + ".end");
            morphs.Add(new VpdMorphPose { Index = index, MorphName = name, Weight = weight });
        }
        return new VpdPose { ParentFileName = parent, Bones = bones, Morphs = morphs };
    }

    private static string Statement(PoseLine line, string field)
    {
        string value = line.Text.Trim();
        if (!value.EndsWith(';') || value.AsSpan(0, value.Length - 1).Contains(';'))
            throw new VpdFormatException(field, line.Number, "expected a semicolon-terminated statement");
        return value[..^1].Trim();
    }

    private static (int Index, string Name) RecordHeader(PoseLine line, string kind, string field, int? count)
    {
        int brace = line.Text.IndexOf('{');
        if (!line.Text.StartsWith(kind, StringComparison.Ordinal) || brace < kind.Length ||
            !int.TryParse(line.Text.AsSpan(kind.Length, brace - kind.Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
            count.HasValue && index >= count.Value)
            throw new VpdFormatException(field + ".index", line.Number, $"invalid {kind} record index");
        string name = line.Text[(brace + 1)..];
        VpdText.ValidateName(name, field + ".name", line.Number);
        return (index, name);
    }

    private static float[] Numbers(PoseLine line, int count, string field)
    {
        // Stop after one extra component so a malformed comma-heavy line cannot allocate
        // millions of strings before its component count is rejected.
        string[] values = Statement(line, field).Split(',', count + 1, StringSplitOptions.None);
        if (values.Length != count)
            throw new VpdFormatException(field, line.Number, $"expected {count} comma-separated numbers");
        var result = new float[count];
        for (int i = 0; i < count; i++)
            if (!float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]) || !float.IsFinite(result[i]))
                throw new VpdFormatException(field, line.Number, "expected finite floating point values");
        return result;
    }

    private static void End(PoseLine line, string field)
    {
        if (line.Text.Trim() != "}") throw new VpdFormatException(field, line.Number, "expected closing brace");
    }

    private readonly record struct PoseLine(string Text, int Number);

    private sealed class PoseLines(string text)
    {
        private readonly StringReader reader = new(text);
        public int LineNumber { get; private set; }

        public string? ReadHeader()
        {
            LineNumber++;
            return reader.ReadLine()?.Trim();
        }

        public PoseLine Next(string field)
        {
            if (TryNext(out PoseLine line)) return line;
            throw new VpdFormatException(field, LineNumber + 1, "unexpected end of input");
        }

        public bool TryNext(out PoseLine result)
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                LineNumber++;
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) line = line[..comment];
                if (string.IsNullOrWhiteSpace(line)) continue;
                // Keep name whitespace after '{'; indentation before a statement is insignificant.
                result = new PoseLine(line.TrimStart(), LineNumber);
                return true;
            }
            result = default;
            return false;
        }
    }
}

internal static class VpdText
{
    private static readonly Encoding Cp932 = CreateCp932();
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    private static Encoding CreateCp932()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static string Decode(ReadOnlySpan<byte> bytes, VpdTextEncoding encoding)
    {
        try
        {
            if (encoding == VpdTextEncoding.Cp932) return Cp932.GetString(bytes);
            if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return Utf8.GetString(bytes[3..]);
            if (encoding == VpdTextEncoding.Utf8) return Utf8.GetString(bytes);
            try { return Utf8.GetString(bytes); }
            catch (DecoderFallbackException) { return Cp932.GetString(bytes); }
        }
        catch (DecoderFallbackException exception)
        {
            throw new VpdFormatException("input.encoding", 0, "invalid text encoding", exception);
        }
    }

    public static byte[] Encode(string text, VpdTextEncoding encoding)
    {
        Encoding encoder = encoding switch
        {
            VpdTextEncoding.Cp932 => Cp932,
            VpdTextEncoding.Utf8 => Utf8,
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), "Writing requires an explicit CP932 or UTF8 encoding."),
        };
        try { return encoder.GetBytes(text); }
        catch (EncoderFallbackException exception)
        {
            throw new VpdFormatException("output.encoding", 0, "text cannot be represented by the selected encoding", exception);
        }
    }

    public static void ValidateName(string? name, string field, int line)
    {
        if (name is null || name.Any(char.IsControl) || name.IndexOfAny(['{', '}', ';']) >= 0 || name.Contains("//", StringComparison.Ordinal))
            throw new VpdFormatException(field, line, "name contains a structural character or is null");
    }

    public static void ValidateRotation(Quaternion value, string field, int line)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W) || value == default)
            throw new VpdFormatException(field, line, "expected a finite nonzero quaternion");
    }
}
