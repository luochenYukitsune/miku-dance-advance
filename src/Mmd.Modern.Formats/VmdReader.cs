// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// VMDFile.cpp/.h section ordering and record layouts. This port validates input,
// preserves raw records, and reports non-playback sections explicitly.

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Mmd.Modern.Formats;

/// <summary>Reads legacy VMD and VMD0002 tracks without taking ownership of the input stream.</summary>
public static class VmdReader
{
    private const int MaxElementCount = 2_000_000;
    private const long MaxInputBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Reads from the current stream position. Optional camera, light, shadow and IK
    /// sections are retained and reported in diagnostics; non-character sections are not played.
    /// </summary>
    public static VmdMotion Read(Stream stream, VmdReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Encoding encoding = VmdEncoding.Create(options?.AnsiCodePage ?? 932);
        var names = new VmdNameProfile(options);
        if (!stream.CanRead)
            throw new ArgumentException("VMD input stream must be readable.", nameof(stream));

        var reader = new VmdBinaryReader(stream, encoding, names);
        Span<byte> signature = stackalloc byte[30];
        reader.ReadBytes(signature, "header.signature");
        int nul = signature.IndexOf((byte)0);
        ReadOnlySpan<byte> version = nul < 0 ? signature : signature[..nul];
        // nanoem's MIT component uses the same six block parsers for both signatures;
        // only the target model name width differs (nanoem_p.h VMD_TARGET_MODEL_NAME_LENGTH_V1/V2).
        int modelNameWidth;
        if (version.SequenceEqual("Vocaloid Motion Data 0002"u8)) modelNameWidth = 20;
        else if (version.SequenceEqual("Vocaloid Motion Data file"u8)) modelNameWidth = 10;
        else
            throw new VmdFormatException("header.version", 0,
                "unsupported VMD signature/version; expected 'Vocaloid Motion Data 0002' or 'Vocaloid Motion Data file'");

        VmdEncodedName modelName = reader.ReadName(modelNameWidth, "header.modelName", VmdNameKind.Model);
        int boneCount = reader.ReadCount("bones", 111);
        var bones = new VmdBoneKeyframe[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            string field = $"bones[{i}]";
            VmdEncodedName name = reader.ReadName(15, field + ".name", VmdNameKind.Bone);
            uint frame = reader.ReadUInt32(field + ".frame");
            Vector3 translation = reader.ReadVector3(field + ".translation");
            long rotationOffset = reader.Position;
            Quaternion rotation = new(
                reader.ReadSingle(field + ".rotation.x"), reader.ReadSingle(field + ".rotation.y"),
                reader.ReadSingle(field + ".rotation.z"), reader.ReadSingle(field + ".rotation.w"));
            // Do not normalize here: retain the original finite xyzw values for compatibility decisions.
            // MMD itself writes all-zero quaternions for some untouched keys and plays them as rest.
            if (rotation.X == 0 && rotation.Y == 0 && rotation.Z == 0 && rotation.W == 0)
            {
                rotation = Quaternion.Identity;
                reader.Repair(field + ".rotation", rotationOffset, "zero quaternion read as identity");
            }
            byte[] interpolation = new byte[64];
            reader.ReadBytes(interpolation, field + ".interpolation");
            bones[i] = new VmdBoneKeyframe
            {
                BoneName = name.Value, OriginalName = name, Frame = frame, Translation = translation, Rotation = rotation,
                Interpolation = interpolation,
            };
        }

        IReadOnlyList<VmdMorphKeyframe> morphs = Array.Empty<VmdMorphKeyframe>();
        if (reader.TryReadCount("morphs", 23, out int morphCount))
        {
            var records = new VmdMorphKeyframe[morphCount];
            for (int i = 0; i < morphCount; i++)
            {
                string field = $"morphs[{i}]";
                VmdEncodedName name = reader.ReadName(15, field + ".name", VmdNameKind.Morph);
                records[i] = new VmdMorphKeyframe
                {
                    MorphName = name.Value, OriginalName = name,
                    Frame = reader.ReadUInt32(field + ".frame"),
                    Weight = reader.ReadSingle(field + ".weight"),
                };
            }
            morphs = Array.AsReadOnly(records);
        }

        var diagnostics = new List<string>();
        IReadOnlyList<VmdCameraKeyframe> cameraKeys = Array.Empty<VmdCameraKeyframe>();
        if (reader.TryReadCount("cameras", 61, out int cameras))
        {
            var records = new VmdCameraKeyframe[cameras];
            for (int i = 0; i < cameras; i++)
            {
                string field = $"cameras[{i}]";
                uint frame = reader.ReadUInt32(field + ".frame");
                float distance = reader.ReadSingle(field + ".distance");
                Vector3 interest = reader.ReadVector3(field + ".interest");
                Vector3 rotation = reader.ReadVector3(field + ".rotation");
                byte[] curve = new byte[24];
                reader.ReadBytes(curve, field + ".interpolation");
                uint viewAngle = reader.ReadUInt32(field + ".viewAngle");
                bool orthographic = reader.ReadFlag(field + ".perspective");
                records[i] = new VmdCameraKeyframe
                {
                    Frame = frame, Distance = distance, Interest = interest, Rotation = rotation,
                    Interpolation = curve, ViewAngle = viewAngle, Perspective = !orthographic,
                };
            }
            cameraKeys = Array.AsReadOnly(records);
            Report(diagnostics, "camera", cameras);
        }
        IReadOnlyList<VmdLightKeyframe> lightKeys = Array.Empty<VmdLightKeyframe>();
        if (reader.TryReadCount("lights", 28, out int lights))
        {
            var records = new VmdLightKeyframe[lights];
            for (int i = 0; i < lights; i++)
            {
                string field = $"lights[{i}]";
                records[i] = new VmdLightKeyframe
                {
                    Frame = reader.ReadUInt32(field + ".frame"),
                    Color = reader.ReadVector3(field + ".color"),
                    Direction = reader.ReadVector3(field + ".position"),
                };
            }
            lightKeys = Array.AsReadOnly(records);
            Report(diagnostics, "light", lights);
        }
        IReadOnlyList<VmdSelfShadowKeyframe> shadowKeys = Array.Empty<VmdSelfShadowKeyframe>();
        if (reader.TryReadCount("shadows", 9, out int shadows))
        {
            var records = new VmdSelfShadowKeyframe[shadows];
            for (int i = 0; i < shadows; i++)
            {
                string field = $"shadows[{i}]";
                uint frame = reader.ReadUInt32(field + ".frame");
                long offset = reader.Position;
                byte mode = reader.ReadByte(field + ".type");
                if (mode > 2)
                    throw new VmdFormatException(field + ".type", offset, "shadow type must be 0, 1 or 2");
                records[i] = new VmdSelfShadowKeyframe
                {
                    Frame = frame, Mode = mode, Distance = reader.ReadSingle(field + ".distance"),
                };
            }
            shadowKeys = Array.AsReadOnly(records);
            Report(diagnostics, "shadow", shadows);
        }
        IReadOnlyList<VmdIkKeyframe> ikKeys=Array.Empty<VmdIkKeyframe>();
        if (reader.TryReadCount("ik", 9, out int ikCount))
        {
            long totalIkBones = 0;
            var records=new VmdIkKeyframe[ikCount];
            for (int i = 0; i < ikCount; i++)
            {
                string field = $"ik[{i}]";
                uint frame=reader.ReadUInt32(field + ".frame");
                bool visible=reader.ReadFlag(field + ".show");
                int count = reader.ReadCount(field + ".bones", 21);
                totalIkBones += count;
                if(totalIkBones>MaxElementCount)throw new VmdFormatException(field+".bones.count",reader.Position-4,"aggregate IK switch count exceeds safety limit");
                var switches=new VmdIkSwitch[count];
                for (int j = 0; j < count; j++)
                {
                    VmdEncodedName name=reader.ReadName(20, $"{field}.bones[{j}].name", VmdNameKind.Ik);
                    bool enabled=reader.ReadFlag($"{field}.bones[{j}].enabled");
                    switches[j]=new(name.Value,enabled) { OriginalName = name };
                }
                records[i]=new(){Frame=frame,Visible=visible,Switches=switches};
            }
            ikKeys=Array.AsReadOnly(records);
            if (ikCount > 0)
                diagnostics.Add($"Read {ikCount} IK keyframe(s), {totalIkBones} bone toggle(s); visibility is separate from IK state.");
        }

        reader.SkipTrailing();
        diagnostics.AddRange(reader.Repairs);
        return new VmdMotion
        {
            ModelName = modelName.Value, OriginalModelName = modelName,
            BoneKeyframes = Array.AsReadOnly(bones),
            MorphKeyframes = morphs,
            CameraKeyframes = cameraKeys,
            LightKeyframes = lightKeys,
            SelfShadowKeyframes = shadowKeys,
            IkKeyframes = ikKeys,
            Diagnostics = diagnostics.AsReadOnly(),
        };
    }

    private static void Report(List<string> diagnostics, string section, int count)
    {
        if (count > 0)
            diagnostics.Add($"Validated {count} {section} keyframe(s); not played by character runtime.");
    }

    private sealed class VmdBinaryReader
    {
        private readonly Stream _stream;
        private readonly long? _length;
        private readonly Encoding _encoding;
        private readonly VmdNameProfile _names;
        public long Position { get; private set; }

        public VmdBinaryReader(Stream stream, Encoding encoding, VmdNameProfile names)
        {
            _stream = stream;
            _encoding = encoding;
            _names = names;
            if (stream.CanSeek)
            {
                _length = stream.Length - stream.Position;
                if (_length < 0)
                    throw new ArgumentException("Stream position is past the end of the stream.", nameof(stream));
                if (_length > MaxInputBytes)
                    throw new VmdFormatException("motion.length", 0, $"input exceeds safety limit {MaxInputBytes} bytes");
            }
        }

        public void ReadBytes(Span<byte> bytes, string field, long? errorOffset = null)
        {
            long offset = errorOffset ?? Position;
            if (bytes.Length > MaxInputBytes - Position)
                throw new VmdFormatException(field, offset, $"input exceeds safety limit {MaxInputBytes} bytes");
            if (_length is long length && bytes.Length > length - Position)
                throw new VmdFormatException(field, offset,
                    $"unexpected end of data: need {bytes.Length} byte(s), only {length - Position} remain");
            try
            {
                _stream.ReadExactly(bytes);
            }
            catch (EndOfStreamException exception)
            {
                throw new VmdFormatException(field, offset,
                    $"unexpected end of data while reading {bytes.Length} byte(s)", exception);
            }
            Position += bytes.Length;
        }

        public byte ReadByte(string field)
        {
            Span<byte> value = stackalloc byte[1];
            ReadBytes(value, field);
            return value[0];
        }

        private const int MaxRepairDiagnostics = 16;
        private int _repairCount;
        public List<string> Repairs { get; } = [];

        /// <summary>Records a tolerated deviation. Only the first few are listed so a file full of them stays bounded.</summary>
        public void Repair(string field, long offset, string message)
        {
            _repairCount++;
            if (Repairs.Count < MaxRepairDiagnostics)
                Repairs.Add($"Repaired {field} at byte {offset}: {message}.");
            else if (Repairs.Count == MaxRepairDiagnostics)
                Repairs.Add("Further repairs omitted.");
        }

        public bool ReadFlag(string field)
        {
            long offset = Position;
            byte flag=ReadByte(field);
            // Any non-zero byte is true for MMD; tools that write 0xFF should not reject the whole motion.
            if (flag > 1)
                Repair(field, offset, $"flag {flag} read as 1");
            return flag!=0;
        }

        public uint ReadUInt32(string field)
        {
            Span<byte> value = stackalloc byte[4];
            ReadBytes(value, field);
            return BinaryPrimitives.ReadUInt32LittleEndian(value);
        }

        public float ReadSingle(string field)
        {
            long offset = Position;
            Span<byte> value = stackalloc byte[4];
            ReadBytes(value, field);
            float result = BinaryPrimitives.ReadSingleLittleEndian(value);
            if (!float.IsFinite(result))
                throw new VmdFormatException(field, offset, "value must be finite");
            return result;
        }

        public Vector3 ReadVector3(string field) => new(
            ReadSingle(field + ".x"), ReadSingle(field + ".y"), ReadSingle(field + ".z"));

        public VmdEncodedName ReadName(int width, string field, VmdNameKind kind)
        {
            long offset = Position;
            Span<byte> bytes = stackalloc byte[width];
            ReadBytes(bytes, field);
            int nul = bytes.IndexOf((byte)0);
            byte[] payload = (nul < 0 ? bytes : bytes[..nul]).ToArray();
            // Explicit overrides and candidate matching see the untouched bytes: a field-filling name can end in a
            // complete character of the alternative code page that only looks truncated in the configured one.
            if (_names.Find(kind, payload) is { } explicitName)
                return explicitName;
            if (_names.CandidatesFor(kind) is { } candidates)
            {
                VmdEncodedName? match = null;
                foreach (int codePage in new[] { _encoding.CodePage, _encoding.CodePage == 932 ? 936 : 932 })
                {
                    string candidate;
                    try { candidate = VmdEncoding.Create(codePage).GetString(payload); }
                    catch (DecoderFallbackException) { continue; }
                    if (!candidates.Contains(candidate)) continue;
                    if (match is not null && !string.Equals(match.Value, candidate, StringComparison.Ordinal))
                        throw new VmdFormatException(field, offset,
                            $"ambiguous model name: CP{match.AnsiCodePage} '{match.Value}' and CP{codePage} '{candidate}' both match; supply an explicit name override");
                    match ??= new(candidate, payload, codePage);
                }
                if (match is not null) return match;
            }
            // MMD truncates names to the fixed field width by bytes, which can split the final double-byte
            // character. Only a name that fills the whole field can be truncated that way; drop that half for the
            // fallback decode only (the encoded name keeps bytes that decode to its value).
            byte[] decoded = payload;
            if (nul < 0 && payload.Length > 0 && EndsWithIncompleteCharacter(payload))
            {
                decoded = payload[..^1];
                Repair(field, offset, "trailing half of a truncated double-byte character dropped");
            }
            try
            {
                return new(_encoding.GetString(decoded), decoded, _encoding.CodePage);
            }
            catch (DecoderFallbackException exception)
            {
                throw new VmdFormatException(field, offset, $"name is not valid CP{_encoding.CodePage}", exception);
            }
        }

        private bool EndsWithIncompleteCharacter(byte[] payload)
        {
            try { _encoding.GetString(payload); return false; }
            catch (DecoderFallbackException) { }
            try { _encoding.GetString(payload, 0, payload.Length - 1); return true; }
            catch (DecoderFallbackException) { return false; }
        }

        public int ReadCount(string section, int minimumBytes)
        {
            long offset = Position;
            return ValidateCount(ReadUInt32(section + ".count"), section, offset, minimumBytes);
        }

        public bool TryReadCount(string section, int minimumBytes, out int count)
        {
            long offset = Position;
            Span<byte> bytes = stackalloc byte[4];
            int first = _stream.ReadByte();
            if (first < 0)
            {
                count = 0;
                return false;
            }
            bytes[0] = (byte)first;
            Position++;
            ReadBytes(bytes[1..], section + ".count", offset);
            count = ValidateCount(BinaryPrimitives.ReadUInt32LittleEndian(bytes), section, offset, minimumBytes);
            return true;
        }

        private int ValidateCount(uint count, string section, long offset, int minimumBytes)
        {
            if (count > MaxElementCount)
                throw new VmdFormatException(section + ".count", offset,
                    $"element count {count} exceeds safety limit {MaxElementCount}");
            long remaining = (_length ?? MaxInputBytes) - Position;
            if (count > remaining / minimumBytes)
                throw new VmdFormatException(section + ".count", offset,
                    $"declares {count} element(s) of at least {minimumBytes} byte(s), only {remaining} byte(s) remain within input bounds");
            return (int)count;
        }

        /// <summary>Some exporters pad files or append private data after the known sections. Those bytes are
        /// not motion data, so they are reported instead of rejecting the file. Reading stays bounded by the input cap.</summary>
        public void SkipTrailing()
        {
            long offset = Position;
            long trailing = 0;
            byte[] buffer = new byte[4096];
            int read;
            while ((read = _stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                trailing += read;
                if (trailing > MaxInputBytes - offset)
                    throw new VmdFormatException("motion.end", offset, $"input exceeds safety limit {MaxInputBytes} bytes");
            }
            Position += trailing;
            if (trailing > 0)
                Repair("motion.end", offset, $"{trailing} trailing byte(s) after known VMD sections ignored");
        }
    }
}
