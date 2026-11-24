// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
//
// PMX 2.0 / 2.1 reader ported from saba's PMXFile.cpp. Behavioural notes:
//   * every read is bounds-checked and failures carry the PMX field path and
//     byte offset instead of silently yielding zero;
//   * BDEF2/SDEF weights are normalized to (w, 1-w) and BDEF1 to 1, matching
//     what saba's PMXModel does at skinning time;
//   * saba's QDEF weight read indexes weights[4] (out of bounds); this port
//     reads the four QDEF weights defined by the PMX format.

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Mmd.Modern.Formats;

/// <summary>Reads PMX 2.0 / PMX 2.1 model files.</summary>
public static class PmxReader
{
    private const int MaxElementCount = 100_000_000;
    private const int MaxStringByteLength = 64 * 1024 * 1024;

    private static readonly Encoding Utf8Strict = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Encoding Utf16LeStrict = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);

    /// <summary>Reads a PMX model from the current position of <paramref name="stream"/>.</summary>
    public static PmxModel Read(Stream stream)
        => Read(stream, new());

    /// <summary>Reads a PMX model using an explicit input-byte budget, leaving the caller's stream open.</summary>
    public static PmxModel Read(Stream stream, PmxReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return ReadCore(stream, filePath: null, options);
    }

    /// <summary>Reads a PMX model from a file.</summary>
    public static PmxModel Read(string path)
        => Read(path, new());

    /// <summary>Reads a PMX model file using an explicit input-byte budget.</summary>
    public static PmxModel Read(string path, PmxReadOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        using FileStream stream = File.OpenRead(path);
        return ReadCore(stream, path, options);
    }

    private static PmxModel ReadCore(Stream stream, string? filePath, PmxReadOptions options)
    {
        if (!stream.CanRead) throw new ArgumentException("PMX input stream must be readable.", nameof(stream));
        if (stream.CanSeek && stream.Length - stream.Position > options.MaxInputBytes)
            throw new PmxFormatException("input.length", 0, $"input exceeds configured byte limit {options.MaxInputBytes}", filePath);
        // The parser needs to know where the file ends (to enforce complete
        // version-specific sections and to refuse truncated input); buffer
        // non-seekable streams once rather than guessing.
        Stream source = stream;
        FileStream? buffered = null;
        try
        {
            if (!stream.CanSeek)
            {
                // A seekable temporary file avoids MemoryStream's Int32 capacity
                // ceiling and retaining a second input-sized managed allocation.
                buffered = new FileStream(Path.Combine(Path.GetTempPath(), "mda-pmx-" + Guid.NewGuid().ToString("N") + ".tmp"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
                    FileOptions.DeleteOnClose | FileOptions.SequentialScan);
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    long remaining = options.MaxInputBytes - buffered.Length;
                    // Read at most one byte beyond the limit, without overflowing
                    // when a host explicitly supplies Int64.MaxValue as its budget.
                    int requested = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
                    int count = stream.Read(buffer, 0, requested);
                    if (count == 0) break;
                    if (count > remaining)
                        throw new PmxFormatException("input.length", buffered.Length,
                            $"input exceeds configured byte limit {options.MaxInputBytes}", filePath);
                    buffered.Write(buffer, 0, count);
                }
                buffered.Position = 0;
                source = buffered;
            }
            var reader = new PmxBinaryReader(source, filePath);
            return ReadModel(reader);
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    private static PmxModel ReadModel(PmxBinaryReader reader)
    {
        PmxHeader header = ReadHeader(reader);

        string name = reader.ReadString(header.TextEncoding, "model.name");
        string englishName = reader.ReadString(header.TextEncoding, "model.englishName");
        string comment = reader.ReadString(header.TextEncoding, "model.comment");
        string englishComment = reader.ReadString(header.TextEncoding, "model.englishComment");

        IReadOnlyList<PmxVertex> vertices = ReadVertices(reader, header, out long[] vertexBoneOffsets);
        IReadOnlyList<PmxTriangle> faces = ReadFaces(reader, header, vertices.Count);
        IReadOnlyList<string> textures = ReadTextures(reader, header);
        IReadOnlyList<PmxMaterial> materials = ReadMaterials(
            reader, header, textures.Count, (long)faces.Count * 3);
        IReadOnlyList<PmxBone> bones = ReadBones(reader, header, out _);
        ValidateVertexBones(reader, header, vertices, vertexBoneOffsets, bones.Count);
        var impulseReferences = new List<ImpulseReference>();
        IReadOnlyList<PmxMorph> morphs = ReadMorphs(
            reader, header, vertices.Count, materials.Count, bones.Count, impulseReferences);
        IReadOnlyList<PmxDisplayFrame> displayFrames = ReadDisplayFrames(
            reader, header, bones.Count, morphs.Count);
        IReadOnlyList<PmxRigidBody> rigidBodies = ReadRigidBodies(reader, header, bones.Count);
        foreach (ImpulseReference reference in impulseReferences)
        {
            CheckIndex(reader, reference.Index, rigidBodies.Count,
                $"morphs[{reference.Morph}].impulseOffsets[{reference.Element}].rigidBodyIndex",
                reference.Offset, allowNone: false);
        }

        IReadOnlyList<PmxJoint> joints = ReadJoints(reader, header, rigidBodies.Count);
        IReadOnlyList<PmxSoftBody> softBodies = header.IsVersion21
            ? ReadSoftBodies(reader, header, vertices.Count, materials.Count, rigidBodies.Count)
            : Array.Empty<PmxSoftBody>();
        if (!reader.IsAtEnd)
        {
            throw reader.Error("model.end", reader.Position,
                $"unexpected trailing data after PMX {header.Version} sections");
        }

        return new PmxModel
        {
            Header = header,
            Name = name,
            EnglishName = englishName,
            Comment = comment,
            EnglishComment = englishComment,
            Vertices = vertices,
            Faces = faces,
            Textures = textures,
            Materials = materials,
            Bones = bones,
            Morphs = morphs,
            DisplayFrames = displayFrames,
            RigidBodies = rigidBodies,
            Joints = joints,
            SoftBodies = softBodies,
        };
    }

    private static PmxHeader ReadHeader(PmxBinaryReader reader)
    {
        long magicOffset = reader.Position;
        Span<byte> magic = stackalloc byte[4];
        reader.ReadBytes(magic, "header.magic");
        if (magic[0] != (byte)'P' || magic[1] != (byte)'M' ||
            magic[2] != (byte)'X' || magic[3] != (byte)' ')
        {
            throw reader.Error(
                "header.magic",
                magicOffset,
                "expected the four-byte PMX signature 'PMX '");
        }

        long versionOffset = reader.Position;
        float version = reader.ReadSingle("header.version");
        if (!float.IsFinite(version) ||
            (MathF.Abs(version - 2.0f) > 1e-5f && MathF.Abs(version - 2.1f) > 1e-5f))
        {
            throw reader.Error(
                "header.version",
                versionOffset,
                $"unsupported PMX version {version}; expected 2.0 or 2.1");
        }

        long dataSizeOffset = reader.Position;
        byte dataSize = reader.ReadByte("header.dataSize");
        if (dataSize != 8)
        {
            throw reader.Error(
                "header.dataSize",
                dataSizeOffset,
                $"unsupported header setting count {dataSize}; PMX 2.0/2.1 require 8");
        }

        long encodingOffset = reader.Position;
        byte encodingValue = reader.ReadByte("header.textEncoding");
        PmxTextEncoding encoding = encodingValue switch
        {
            0 => PmxTextEncoding.Utf16LittleEndian,
            1 => PmxTextEncoding.Utf8,
            _ => throw reader.Error(
                "header.textEncoding",
                encodingOffset,
                $"unknown text encoding {encodingValue}; expected 0 (UTF-16LE) or 1 (UTF-8)"),
        };

        long additionalUvOffset = reader.Position;
        byte additionalUvCount = reader.ReadByte("header.additionalUvCount");
        if (additionalUvCount > 4)
        {
            throw reader.Error(
                "header.additionalUvCount",
                additionalUvOffset,
                $"additional UV count {additionalUvCount} is outside 0-4");
        }

        int vertexIndexSize = ReadIndexSize(reader, "header.vertexIndexSize");
        int textureIndexSize = ReadIndexSize(reader, "header.textureIndexSize");
        int materialIndexSize = ReadIndexSize(reader, "header.materialIndexSize");
        int boneIndexSize = ReadIndexSize(reader, "header.boneIndexSize");
        int morphIndexSize = ReadIndexSize(reader, "header.morphIndexSize");
        int rigidBodyIndexSize = ReadIndexSize(reader, "header.rigidBodyIndexSize");

        return new PmxHeader
        {
            Version = version,
            HeaderDataSize = dataSize,
            TextEncoding = encoding,
            AdditionalUvCount = additionalUvCount,
            VertexIndexSize = vertexIndexSize,
            TextureIndexSize = textureIndexSize,
            MaterialIndexSize = materialIndexSize,
            BoneIndexSize = boneIndexSize,
            MorphIndexSize = morphIndexSize,
            RigidBodyIndexSize = rigidBodyIndexSize,
        };
    }

    private static int ReadIndexSize(PmxBinaryReader reader, string field)
    {
        long offset = reader.Position;
        byte size = reader.ReadByte(field);
        if (size is not (1 or 2 or 4))
        {
            throw reader.Error(field, offset, $"index width {size} is not 1, 2 or 4");
        }

        return size;
    }

    private static IReadOnlyList<PmxVertex> ReadVertices(
        PmxBinaryReader reader, PmxHeader header, out long[] boneOffsets)
    {
        int addUvCount = header.AdditionalUvCount;
        int minimumBytes = 32 + addUvCount * 16 + 1 + 1 + 4;
        int count = reader.ReadCount("vertices", minimumBytes);
        var vertices = new PmxVertex[count];
        boneOffsets = new long[count];

        for (int i = 0; i < count; i++)
        {
            string field = $"vertices[{i}]";
            Vector3 position = reader.ReadVector3(field + ".position");
            Vector3 normal = reader.ReadVector3(field + ".normal");
            Vector2 uv = reader.ReadVector2(field + ".uv");

            var additionalUvs = new Vector4[addUvCount];
            for (int channel = 0; channel < addUvCount; channel++)
            {
                additionalUvs[channel] = reader.ReadVector4($"{field}.additionalUv[{channel}]");
            }

            long weightTypeOffset = reader.Position;
            byte weightTypeValue = reader.ReadByte(field + ".weightType");
            if (weightTypeValue > (byte)PmxVertexWeightType.Qdef)
            {
                throw reader.Error(
                    field + ".weightType",
                    weightTypeOffset,
                    $"unknown vertex weight type {weightTypeValue}; expected 0-4");
            }

            var weightType = (PmxVertexWeightType)weightTypeValue;
            boneOffsets[i] = reader.Position;
            var boneIndices = new[] { -1, -1, -1, -1 };
            var boneWeights = new float[4];
            Vector3 sdefC = default;
            Vector3 sdefR0 = default;
            Vector3 sdefR1 = default;

            switch (weightType)
            {
                case PmxVertexWeightType.Bdef1:
                    boneIndices[0] = reader.ReadSignedIndex(
                        header.BoneIndexSize, field + ".boneIndices[0]");
                    boneWeights[0] = 1f;
                    break;

                case PmxVertexWeightType.Bdef2:
                    boneIndices[0] = reader.ReadSignedIndex(
                        header.BoneIndexSize, field + ".boneIndices[0]");
                    boneIndices[1] = reader.ReadSignedIndex(
                        header.BoneIndexSize, field + ".boneIndices[1]");
                    boneWeights[0] = reader.ReadSingle(field + ".boneWeights[0]");
                    boneWeights[1] = 1f - boneWeights[0];
                    break;

                case PmxVertexWeightType.Bdef4:
                    for (int slot = 0; slot < 4; slot++)
                    {
                        boneIndices[slot] = reader.ReadSignedIndex(
                            header.BoneIndexSize, $"{field}.boneIndices[{slot}]");
                    }

                    for (int slot = 0; slot < 4; slot++)
                    {
                        boneWeights[slot] = reader.ReadSingle($"{field}.boneWeights[{slot}]");
                    }

                    break;

                case PmxVertexWeightType.Sdef:
                    boneIndices[0] = reader.ReadSignedIndex(
                        header.BoneIndexSize, field + ".boneIndices[0]");
                    boneIndices[1] = reader.ReadSignedIndex(
                        header.BoneIndexSize, field + ".boneIndices[1]");
                    boneWeights[0] = reader.ReadSingle(field + ".boneWeights[0]");
                    boneWeights[1] = 1f - boneWeights[0];
                    sdefC = reader.ReadVector3(field + ".sdefC");
                    sdefR0 = reader.ReadVector3(field + ".sdefR0");
                    sdefR1 = reader.ReadVector3(field + ".sdefR1");
                    break;

                case PmxVertexWeightType.Qdef:
                    for (int slot = 0; slot < 4; slot++)
                    {
                        boneIndices[slot] = reader.ReadSignedIndex(
                            header.BoneIndexSize, $"{field}.boneIndices[{slot}]");
                    }

                    for (int slot = 0; slot < 4; slot++)
                    {
                        boneWeights[slot] = reader.ReadSingle($"{field}.boneWeights[{slot}]");
                    }

                    break;

                default:
                    throw reader.Error(
                        field + ".weightType",
                        weightTypeOffset,
                        $"unhandled vertex weight type {weightType}");
            }

            float edgeScale = reader.ReadSingle(field + ".edgeScale");

            vertices[i] = new PmxVertex
            {
                Position = position,
                Normal = normal,
                Uv = uv,
                AdditionalUvs = additionalUvs,
                WeightType = weightType,
                BoneIndices = boneIndices,
                BoneWeights = boneWeights,
                SdefC = sdefC,
                SdefR0 = sdefR0,
                SdefR1 = sdefR1,
                EdgeScale = edgeScale,
            };
        }

        return vertices;
    }

    private static void ValidateVertexBones(
        PmxBinaryReader reader,
        PmxHeader header,
        IReadOnlyList<PmxVertex> vertices,
        long[] boneOffsets,
        int boneCount)
    {
        for (int i = 0; i < vertices.Count; i++)
        {
            PmxVertex vertex = vertices[i];
            int serializedSlots = vertex.WeightType switch
            {
                PmxVertexWeightType.Bdef1 => 1,
                PmxVertexWeightType.Bdef2 or PmxVertexWeightType.Sdef => 2,
                _ => 4,
            };
            for (int slot = 0; slot < serializedSlots; slot++)
            {
                CheckIndex(reader, vertex.BoneIndices[slot], boneCount,
                    $"vertices[{i}].boneIndices[{slot}]",
                    boneOffsets[i] + (long)slot * header.BoneIndexSize);
            }
        }
    }

    private static IReadOnlyList<PmxTriangle> ReadFaces(
        PmxBinaryReader reader,
        PmxHeader header,
        int vertexCount)
    {
        long countOffset = reader.Position;
        int indexCount = reader.ReadCount("faces", header.VertexIndexSize);
        if (indexCount % 3 != 0)
        {
            throw reader.Error(
                "faces.count",
                countOffset,
                $"face index count {indexCount} is not a multiple of 3");
        }

        var faces = new PmxTriangle[indexCount / 3];
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            int a = ReadVertexIndex(
                reader, header, vertexCount, $"faces[{faceIndex}].vertexIndices[0]");
            int b = ReadVertexIndex(
                reader, header, vertexCount, $"faces[{faceIndex}].vertexIndices[1]");
            int c = ReadVertexIndex(
                reader, header, vertexCount, $"faces[{faceIndex}].vertexIndices[2]");
            faces[faceIndex] = new PmxTriangle(a, b, c);
        }

        return faces;
    }

    private static int ReadVertexIndex(
        PmxBinaryReader reader,
        PmxHeader header,
        int vertexCount,
        string field)
    {
        long offset = reader.Position;
        int index = reader.ReadVertexIndexValue(header.VertexIndexSize, field);
        if (index < 0 || index >= vertexCount)
        {
            throw reader.Error(
                field,
                offset,
                $"vertex index {index} is outside [0, {vertexCount})");
        }

        return index;
    }

    private static IReadOnlyList<string> ReadTextures(PmxBinaryReader reader, PmxHeader header)
    {
        int count = reader.ReadCount("textures", minimumElementBytes: 4);
        var textures = new string[count];
        for (int i = 0; i < count; i++)
        {
            textures[i] = reader.ReadString(header.TextEncoding, $"textures[{i}]");
        }

        return textures;
    }

    private static IReadOnlyList<PmxMaterial> ReadMaterials(
        PmxBinaryReader reader,
        PmxHeader header,
        int textureCount,
        long faceIndexCount)
    {
        long countOffset = reader.Position;
        int count = reader.ReadCount("materials", minimumElementBytes: 43);
        var materials = new PmxMaterial[count];
        long assignedIndices = 0;
        long lastFaceCountOffset = countOffset;

        for (int i = 0; i < count; i++)
        {
            string field = $"materials[{i}]";
            string name = reader.ReadString(header.TextEncoding, field + ".name");
            string englishName = reader.ReadString(header.TextEncoding, field + ".englishName");
            Vector4 diffuse = reader.ReadVector4(field + ".diffuse");
            Vector3 specular = reader.ReadVector3(field + ".specular");
            float specularPower = reader.ReadSingle(field + ".specularPower");
            Vector3 ambient = reader.ReadVector3(field + ".ambient");

            var drawMode = (PmxDrawModeFlags)reader.ReadByte(field + ".drawMode");

            Vector4 edgeColor = reader.ReadVector4(field + ".edgeColor");
            float edgeSize = reader.ReadSingle(field + ".edgeSize");

            long textureOffset = reader.Position;
            int textureIndex = reader.ReadSignedIndex(header.TextureIndexSize, field + ".textureIndex");
            CheckIndex(reader, textureIndex, textureCount, field + ".textureIndex", textureOffset);

            long sphereOffset = reader.Position;
            int sphereTextureIndex = reader.ReadSignedIndex(
                header.TextureIndexSize, field + ".sphereTextureIndex");
            CheckIndex(
                reader, sphereTextureIndex, textureCount,
                field + ".sphereTextureIndex", sphereOffset);

            long sphereModeOffset = reader.Position;
            byte sphereModeValue = reader.ReadByte(field + ".sphereMode");
            if (sphereModeValue > (byte)PmxSphereMode.SubTexture)
            {
                throw reader.Error(
                    field + ".sphereMode",
                    sphereModeOffset,
                    $"unknown sphere mode {sphereModeValue}; expected 0-3");
            }

            var sphereMode = (PmxSphereMode)sphereModeValue;

            long toonModeOffset = reader.Position;
            byte toonModeValue = reader.ReadByte(field + ".toonMode");
            if (toonModeValue > (byte)PmxToonMode.Common)
            {
                throw reader.Error(
                    field + ".toonMode",
                    toonModeOffset,
                    $"unknown toon mode {toonModeValue}; expected 0 (separate) or 1 (common)");
            }

            var toonMode = (PmxToonMode)toonModeValue;
            int toonTextureIndex;
            if (toonMode == PmxToonMode.Separate)
            {
                long toonOffset = reader.Position;
                toonTextureIndex = reader.ReadSignedIndex(
                    header.TextureIndexSize, field + ".toonTextureIndex");
                CheckIndex(
                    reader, toonTextureIndex, textureCount,
                    field + ".toonTextureIndex", toonOffset);
            }
            else
            {
                toonTextureIndex = reader.ReadByte(field + ".toonTextureIndex");
            }

            string memo = reader.ReadString(header.TextEncoding, field + ".memo");

            long faceCountOffset = reader.Position;
            int faceVertexCount = reader.ReadInt32(field + ".faceVertexCount");
            if (faceVertexCount < 0 || faceVertexCount % 3 != 0)
            {
                throw reader.Error(
                    field + ".faceVertexCount",
                    faceCountOffset,
                    $"face vertex count {faceVertexCount} must be a nonnegative multiple of 3");
            }

            assignedIndices += faceVertexCount;
            if (assignedIndices > faceIndexCount)
            {
                throw reader.Error(field + ".faceVertexCount", faceCountOffset,
                    $"material range ends at {assignedIndices}, beyond the {faceIndexCount} face indices");
            }

            lastFaceCountOffset = faceCountOffset;

            materials[i] = new PmxMaterial
            {
                Name = name,
                EnglishName = englishName,
                Diffuse = diffuse,
                Specular = specular,
                SpecularPower = specularPower,
                Ambient = ambient,
                DrawMode = drawMode,
                EdgeColor = edgeColor,
                EdgeSize = edgeSize,
                TextureIndex = textureIndex,
                SphereTextureIndex = sphereTextureIndex,
                SphereMode = sphereMode,
                ToonMode = toonMode,
                ToonTextureIndex = toonTextureIndex,
                Memo = memo,
                FaceVertexCount = faceVertexCount,
            };
        }

        if (assignedIndices != faceIndexCount)
        {
            string field = count == 0 ? "materials.count" : $"materials[{count - 1}].faceVertexCount";
            throw reader.Error(field, lastFaceCountOffset,
                $"material ranges cover {assignedIndices} of {faceIndexCount} face indices");
        }

        return materials;
    }

    private static IReadOnlyList<PmxBone> ReadBones(
        PmxBinaryReader reader,
        PmxHeader header,
        out int bonesCount)
    {
        bonesCount = reader.ReadCount("bones", minimumElementBytes: 25);
        var bones = new PmxBone[bonesCount];

        for (int i = 0; i < bonesCount; i++)
        {
            string field = $"bones[{i}]";
            string name = reader.ReadString(header.TextEncoding, field + ".name");
            string englishName = reader.ReadString(header.TextEncoding, field + ".englishName");
            Vector3 position = reader.ReadVector3(field + ".position");

            long parentOffset = reader.Position;
            int parentIndex = reader.ReadSignedIndex(header.BoneIndexSize, field + ".parentIndex");
            CheckIndex(reader, parentIndex, bonesCount, field + ".parentIndex", parentOffset);

            int deformDepth = reader.ReadInt32(field + ".deformDepth");
            var flags = (PmxBoneFlags)reader.ReadUInt16(field + ".flags");

            Vector3 positionOffset = default;
            int linkBoneIndex = -1;
            if ((flags & PmxBoneFlags.TargetShowMode) != 0)
            {
                long linkOffset = reader.Position;
                linkBoneIndex = reader.ReadSignedIndex(header.BoneIndexSize, field + ".linkBoneIndex");
                CheckIndex(reader, linkBoneIndex, bonesCount, field + ".linkBoneIndex", linkOffset);
            }
            else
            {
                positionOffset = reader.ReadVector3(field + ".positionOffset");
            }

            int appendBoneIndex = -1;
            float appendWeight = 0f;
            if ((flags & (PmxBoneFlags.AppendRotate | PmxBoneFlags.AppendTranslate)) != 0)
            {
                long appendOffset = reader.Position;
                appendBoneIndex = reader.ReadSignedIndex(
                    header.BoneIndexSize, field + ".appendBoneIndex");
                CheckIndex(reader, appendBoneIndex, bonesCount, field + ".appendBoneIndex", appendOffset);
                appendWeight = reader.ReadSingle(field + ".appendWeight");
            }

            Vector3 fixedAxis = default;
            if ((flags & PmxBoneFlags.FixedAxis) != 0)
            {
                fixedAxis = reader.ReadVector3(field + ".fixedAxis");
            }

            Vector3 localXAxis = default;
            Vector3 localZAxis = default;
            if ((flags & PmxBoneFlags.LocalAxis) != 0)
            {
                localXAxis = reader.ReadVector3(field + ".localXAxis");
                localZAxis = reader.ReadVector3(field + ".localZAxis");
            }

            int externalParentKey = 0;
            if ((flags & PmxBoneFlags.DeformOuterParent) != 0)
            {
                externalParentKey = reader.ReadInt32(field + ".externalParentKey");
            }

            int ikTargetBoneIndex = -1;
            int ikIterationCount = 0;
            float ikLimit = 0f;
            IReadOnlyList<PmxIkLink> ikLinks = Array.Empty<PmxIkLink>();
            if ((flags & PmxBoneFlags.Ik) != 0)
            {
                long ikTargetOffset = reader.Position;
                ikTargetBoneIndex = reader.ReadSignedIndex(
                    header.BoneIndexSize, field + ".ikTargetBoneIndex");
                CheckIndex(
                    reader, ikTargetBoneIndex, bonesCount,
                    field + ".ikTargetBoneIndex", ikTargetOffset);

                long iterationOffset = reader.Position;
                ikIterationCount = reader.ReadInt32(field + ".ikIterationCount");
                if (ikIterationCount < 0)
                {
                    throw reader.Error(
                        field + ".ikIterationCount",
                        iterationOffset,
                        $"negative IK iteration count {ikIterationCount}");
                }

                ikLimit = reader.ReadSingle(field + ".ikLimit");

                int linkCount = reader.ReadCount(field + ".ikLinks", minimumElementBytes: 2);
                var links = new PmxIkLink[linkCount];
                for (int linkIndex = 0; linkIndex < linkCount; linkIndex++)
                {
                    string linkField = $"{field}.ikLinks[{linkIndex}]";
                    long linkBoneOffset = reader.Position;
                    int linkBone = reader.ReadSignedIndex(
                        header.BoneIndexSize, linkField + ".boneIndex");
                    CheckIndex(
                        reader, linkBone, bonesCount,
                        linkField + ".boneIndex", linkBoneOffset);

                    long limitOffset = reader.Position;
                    byte enableLimitValue = reader.ReadByte(linkField + ".enableLimit");
                    if (enableLimitValue > 1)
                    {
                        throw reader.Error(
                            linkField + ".enableLimit",
                            limitOffset,
                            $"invalid IK limit flag {enableLimitValue}; expected 0 or 1");
                    }

                    bool enableLimit = enableLimitValue != 0;
                    Vector3 limitMin = default;
                    Vector3 limitMax = default;
                    if (enableLimit)
                    {
                        limitMin = reader.ReadVector3(linkField + ".limitMin");
                        limitMax = reader.ReadVector3(linkField + ".limitMax");
                    }

                    links[linkIndex] = new PmxIkLink(linkBone, enableLimit, limitMin, limitMax);
                }

                ikLinks = links;
            }

            bones[i] = new PmxBone
            {
                Name = name,
                EnglishName = englishName,
                Position = position,
                ParentIndex = parentIndex,
                DeformDepth = deformDepth,
                Flags = flags,
                PositionOffset = positionOffset,
                LinkBoneIndex = linkBoneIndex,
                AppendBoneIndex = appendBoneIndex,
                AppendWeight = appendWeight,
                FixedAxis = fixedAxis,
                LocalXAxis = localXAxis,
                LocalZAxis = localZAxis,
                ExternalParentKey = externalParentKey,
                IkTargetBoneIndex = ikTargetBoneIndex,
                IkIterationCount = ikIterationCount,
                IkLimit = ikLimit,
                IkLinks = ikLinks,
            };
        }

        return bones;
    }

    private static IReadOnlyList<PmxMorph> ReadMorphs(
        PmxBinaryReader reader,
        PmxHeader header,
        int vertexCount,
        int materialCount,
        int boneCount,
        List<ImpulseReference> impulseReferences)
    {
        int morphCount = reader.ReadCount("morphs", minimumElementBytes: 14);
        var morphs = new PmxMorph[morphCount];

        for (int i = 0; i < morphCount; i++)
        {
            string field = $"morphs[{i}]";
            string name = reader.ReadString(header.TextEncoding, field + ".name");
            string englishName = reader.ReadString(header.TextEncoding, field + ".englishName");
            byte controlPanel = reader.ReadByte(field + ".controlPanel");

            long typeOffset = reader.Position;
            var type = (PmxMorphType)reader.ReadByte(field + ".type");
            IReadOnlyList<PmxMorph.GroupOffset> groupOffsets = [];
            IReadOnlyList<PmxMorph.PositionOffset> positionOffsets = [];
            IReadOnlyList<PmxMorph.BoneOffset> boneOffsets = [];
            IReadOnlyList<PmxMorph.UvOffset> uvOffsets = [];
            IReadOnlyList<PmxMorph.MaterialOffset> materialOffsets = [];
            IReadOnlyList<PmxMorph.GroupOffset> flipOffsets = [];
            IReadOnlyList<PmxMorph.ImpulseOffset> impulseOffsets = [];

            // Each kind has a fixed wire size once the header's index widths are known.
            // Validate that many bytes remain before allocating the typed offset array.
            T[] ReadOffsets<T>(string collection, int recordBytes, Func<string, int, T> read)
            {
                int count = reader.ReadCount(field, recordBytes);
                var offsets = new T[count];
                for (int element = 0; element < count; element++)
                    offsets[element] = read($"{field}.{collection}[{element}]", element);
                return offsets;
            }

            PmxMorph.GroupOffset[] ReadGroupOffsets(string collection) =>
                ReadOffsets(collection, header.MorphIndexSize + sizeof(float), (path, _) =>
                    new PmxMorph.GroupOffset(ReadMorphIndex(reader, header, morphCount, path + ".morphIndex"),
                        reader.ReadSingle(path + ".weight")));

            switch (type)
            {
                case PmxMorphType.Group:
                    groupOffsets = ReadGroupOffsets("groupOffsets");
                    break;
                case PmxMorphType.Flip:
                    flipOffsets = ReadGroupOffsets("flipOffsets");
                    break;
                case PmxMorphType.Position:
                    positionOffsets = ReadOffsets("positionOffsets", header.VertexIndexSize + 3 * sizeof(float),
                        (path, _) => new PmxMorph.PositionOffset(
                            ReadVertexIndex(reader, header, vertexCount, path + ".vertexIndex"),
                            reader.ReadVector3(path + ".position")));
                    break;
                case PmxMorphType.Bone:
                    boneOffsets = ReadOffsets("boneOffsets", header.BoneIndexSize + 7 * sizeof(float),
                        (path, _) => ReadMorphBoneOffset(reader, header, boneCount, path));
                    break;
                case PmxMorphType.Uv:
                case PmxMorphType.AdditionalUv1:
                case PmxMorphType.AdditionalUv2:
                case PmxMorphType.AdditionalUv3:
                case PmxMorphType.AdditionalUv4:
                    uvOffsets = ReadOffsets("uvOffsets", header.VertexIndexSize + 4 * sizeof(float),
                        (path, _) => new PmxMorph.UvOffset(
                            ReadVertexIndex(reader, header, vertexCount, path + ".vertexIndex"),
                            reader.ReadVector4(path + ".uv")));
                    break;
                case PmxMorphType.Material:
                    materialOffsets = ReadOffsets("materialOffsets", header.MaterialIndexSize + 1 + 28 * sizeof(float),
                        (path, _) => ReadMorphMaterialOffset(reader, header, materialCount, path));
                    break;
                case PmxMorphType.Impulse:
                    impulseOffsets = ReadOffsets("impulseOffsets", header.RigidBodyIndexSize + 1 + 6 * sizeof(float),
                        (path, element) => ReadMorphImpulseOffset(reader, header, i, element, path, impulseReferences));
                    break;
                default:
                    throw reader.Error(field + ".type", typeOffset,
                        $"unsupported morph type {(byte)type}; expected 0-10");
            }

            morphs[i] = new PmxMorph
            {
                Name = name,
                EnglishName = englishName,
                ControlPanel = controlPanel,
                Type = type,
                GroupOffsets = groupOffsets,
                PositionOffsets = positionOffsets,
                BoneOffsets = boneOffsets,
                UvOffsets = uvOffsets,
                MaterialOffsets = materialOffsets,
                FlipOffsets = flipOffsets,
                ImpulseOffsets = impulseOffsets,
            };
        }

        return morphs;
    }

    private static PmxMorph.BoneOffset ReadMorphBoneOffset(
        PmxBinaryReader reader, PmxHeader header, int boneCount, string field)
    {
        long boneOffset = reader.Position;
        int boneIndex = reader.ReadSignedIndex(
            header.BoneIndexSize, field + ".boneIndex");
        CheckIndex(reader, boneIndex, boneCount, field + ".boneIndex", boneOffset);
        Vector3 position = reader.ReadVector3(field + ".position");
        Vector4 rotation = reader.ReadVector4(field + ".rotation");
        return new(
            boneIndex, position, new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W));
    }

    private static PmxMorph.MaterialOffset ReadMorphMaterialOffset(
        PmxBinaryReader reader, PmxHeader header, int materialCount, string field)
    {
        long materialOffset = reader.Position;
        int materialIndex = reader.ReadSignedIndex(
            header.MaterialIndexSize, field + ".materialIndex");
        CheckIndex(
            reader, materialIndex, materialCount,
            field + ".materialIndex", materialOffset);

        long operationOffset = reader.Position;
        byte operationValue = reader.ReadByte(field + ".operation");
        if (operationValue > (byte)PmxMaterialMorphOperation.Add)
        {
            throw reader.Error(
                field + ".operation",
                operationOffset,
                $"unknown material morph operation {operationValue}; expected 0 or 1");
        }

        var operation = (PmxMaterialMorphOperation)operationValue;
        Vector4 diffuse = reader.ReadVector4(field + ".diffuse");
        Vector3 specular = reader.ReadVector3(field + ".specular");
        float specularPower = reader.ReadSingle(field + ".specularPower");
        Vector3 ambient = reader.ReadVector3(field + ".ambient");
        Vector4 edgeColor = reader.ReadVector4(field + ".edgeColor");
        float edgeSize = reader.ReadSingle(field + ".edgeSize");
        Vector4 textureFactor = reader.ReadVector4(field + ".textureFactor");
        Vector4 sphereTextureFactor = reader.ReadVector4(
            field + ".sphereTextureFactor");
        Vector4 toonTextureFactor = reader.ReadVector4(
            field + ".toonTextureFactor");

        return new(
            materialIndex,
            operation,
            diffuse,
            specular,
            specularPower,
            ambient,
            edgeColor,
            edgeSize,
            textureFactor,
            sphereTextureFactor,
            toonTextureFactor);
    }

    private static PmxMorph.ImpulseOffset ReadMorphImpulseOffset(
        PmxBinaryReader reader, PmxHeader header, int morphIndex, int element,
        string field, List<ImpulseReference> impulseReferences)
    {
        long rigidBodyOffset = reader.Position;
        int rigidBodyIndex = reader.ReadSignedIndex(
            header.RigidBodyIndexSize, field + ".rigidBodyIndex");
        if (rigidBodyIndex < 0)
        {
            throw reader.Error(
                field + ".rigidBodyIndex",
                rigidBodyOffset,
                $"invalid rigid body index {rigidBodyIndex}");
        }

        impulseReferences.Add(new(morphIndex, element, rigidBodyIndex, rigidBodyOffset));

        long localOffset = reader.Position;
        byte localValue = reader.ReadByte(field + ".local");
        if (localValue > 1)
        {
            throw reader.Error(
                field + ".local",
                localOffset,
                $"invalid impulse local flag {localValue}; expected 0 or 1");
        }

        Vector3 translationVelocity = reader.ReadVector3(
            field + ".translationVelocity");
        Vector3 rotationTorque = reader.ReadVector3(
            field + ".rotationTorque");
        return new(
            rigidBodyIndex,
            localValue != 0,
            translationVelocity,
            rotationTorque);
    }

    private static int ReadMorphIndex(
        PmxBinaryReader reader,
        PmxHeader header,
        int morphCount,
        string field)
    {
        long offset = reader.Position;
        int index = reader.ReadSignedIndex(header.MorphIndexSize, field);
        if (index < 0 || index >= morphCount)
        {
            throw reader.Error(field, offset, $"morph index {index} is outside [0, {morphCount})");
        }

        return index;
    }

    private static IReadOnlyList<PmxDisplayFrame> ReadDisplayFrames(
        PmxBinaryReader reader,
        PmxHeader header,
        int boneCount,
        int morphCount)
    {
        int frameCount = reader.ReadCount("displayFrames", minimumElementBytes: 13);
        var frames = new PmxDisplayFrame[frameCount];

        for (int i = 0; i < frameCount; i++)
        {
            string field = $"displayFrames[{i}]";
            string name = reader.ReadString(header.TextEncoding, field + ".name");
            string englishName = reader.ReadString(header.TextEncoding, field + ".englishName");

            long typeOffset = reader.Position;
            byte typeValue = reader.ReadByte(field + ".type");
            if (typeValue > (byte)PmxDisplayFrameType.Special)
            {
                throw reader.Error(
                    field + ".type",
                    typeOffset,
                    $"unknown display frame type {typeValue}; expected 0 or 1");
            }

            var type = (PmxDisplayFrameType)typeValue;
            int targetCount = reader.ReadCount(field + ".targets", minimumElementBytes: 2);
            var targets = new PmxDisplayTarget[targetCount];

            for (int targetIndex = 0; targetIndex < targetCount; targetIndex++)
            {
                string targetField = $"{field}.targets[{targetIndex}]";
                long targetTypeOffset = reader.Position;
                byte targetTypeValue = reader.ReadByte(targetField + ".type");
                if (targetTypeValue > (byte)PmxDisplayTargetType.Morph)
                {
                    throw reader.Error(
                        targetField + ".type",
                        targetTypeOffset,
                        $"unknown display target type {targetTypeValue}; expected 0 (bone) or 1 (morph)");
                }

                var targetType = (PmxDisplayTargetType)targetTypeValue;
                long indexOffset = reader.Position;
                int index;
                if (targetType == PmxDisplayTargetType.Bone)
                {
                    index = reader.ReadSignedIndex(header.BoneIndexSize, targetField + ".index");
                    CheckIndex(reader, index, boneCount, targetField + ".index", indexOffset);
                }
                else
                {
                    index = reader.ReadSignedIndex(header.MorphIndexSize, targetField + ".index");
                    CheckIndex(reader, index, morphCount, targetField + ".index", indexOffset);
                }

                targets[targetIndex] = new PmxDisplayTarget(targetType, index);
            }

            frames[i] = new PmxDisplayFrame
            {
                Name = name,
                EnglishName = englishName,
                Type = type,
                Targets = targets,
            };
        }

        return frames;
    }

    private static IReadOnlyList<PmxRigidBody> ReadRigidBodies(
        PmxBinaryReader reader,
        PmxHeader header,
        int boneCount)
    {
        int count = reader.ReadCount("rigidBodies", minimumElementBytes: 47);
        var rigidBodies = new PmxRigidBody[count];

        for (int i = 0; i < count; i++)
        {
            string field = $"rigidBodies[{i}]";
            string name = reader.ReadString(header.TextEncoding, field + ".name");
            string englishName = reader.ReadString(header.TextEncoding, field + ".englishName");

            long boneOffset = reader.Position;
            int boneIndex = reader.ReadSignedIndex(header.BoneIndexSize, field + ".boneIndex");
            CheckIndex(reader, boneIndex, boneCount, field + ".boneIndex", boneOffset);

            long groupOffset = reader.Position;
            byte group = reader.ReadByte(field + ".group");
            if (group > 15)
            {
                throw reader.Error(
                    field + ".group",
                    groupOffset,
                    $"rigid body group {group} is outside 0-15");
            }

            ushort collisionGroup = reader.ReadUInt16(field + ".collisionGroup");

            long shapeOffset = reader.Position;
            byte shapeValue = reader.ReadByte(field + ".shape");
            if (shapeValue > (byte)PmxRigidBodyShape.Capsule)
            {
                throw reader.Error(
                    field + ".shape",
                    shapeOffset,
                    $"unknown rigid body shape {shapeValue}; expected 0-2");
            }

            var shape = (PmxRigidBodyShape)shapeValue;
            Vector3 shapeSize = reader.ReadVector3(field + ".shapeSize");
            Vector3 translation = reader.ReadVector3(field + ".translation");
            Vector3 rotation = reader.ReadVector3(field + ".rotation");
            float mass = reader.ReadSingle(field + ".mass");
            float translationDamping = reader.ReadSingle(field + ".translationDamping");
            float rotationDamping = reader.ReadSingle(field + ".rotationDamping");
            float repulsion = reader.ReadSingle(field + ".repulsion");
            float friction = reader.ReadSingle(field + ".friction");

            long operationOffset = reader.Position;
            byte operationValue = reader.ReadByte(field + ".operation");
            if (operationValue > (byte)PmxRigidBodyOperation.DynamicAndBoneMerge)
            {
                throw reader.Error(
                    field + ".operation",
                    operationOffset,
                    $"unknown rigid body operation {operationValue}; expected 0-2");
            }

            rigidBodies[i] = new PmxRigidBody
            {
                Name = name,
                EnglishName = englishName,
                BoneIndex = boneIndex,
                Group = group,
                CollisionGroup = collisionGroup,
                Shape = shape,
                ShapeSize = shapeSize,
                Translation = translation,
                Rotation = rotation,
                Mass = mass,
                TranslationDamping = translationDamping,
                RotationDamping = rotationDamping,
                Repulsion = repulsion,
                Friction = friction,
                Operation = (PmxRigidBodyOperation)operationValue,
            };
        }

        return rigidBodies;
    }

    private static IReadOnlyList<PmxJoint> ReadJoints(
        PmxBinaryReader reader,
        PmxHeader header,
        int rigidBodyCount)
    {
        int count = reader.ReadCount("joints", minimumElementBytes: 40);
        var joints = new PmxJoint[count];

        for (int i = 0; i < count; i++)
        {
            string field = $"joints[{i}]";
            string name = reader.ReadString(header.TextEncoding, field + ".name");
            string englishName = reader.ReadString(header.TextEncoding, field + ".englishName");

            long typeOffset = reader.Position;
            byte typeValue = reader.ReadByte(field + ".type");
            if (typeValue > (byte)PmxJointType.Hinge)
            {
                throw reader.Error(
                    field + ".type",
                    typeOffset,
                    $"unknown joint type {typeValue}; expected 0-5");
            }

            long bodyAOffset = reader.Position;
            int rigidBodyAIndex = reader.ReadSignedIndex(
                header.RigidBodyIndexSize, field + ".rigidBodyAIndex");
            CheckIndex(
                reader, rigidBodyAIndex, rigidBodyCount,
                field + ".rigidBodyAIndex", bodyAOffset);

            long bodyBOffset = reader.Position;
            int rigidBodyBIndex = reader.ReadSignedIndex(
                header.RigidBodyIndexSize, field + ".rigidBodyBIndex");
            CheckIndex(
                reader, rigidBodyBIndex, rigidBodyCount,
                field + ".rigidBodyBIndex", bodyBOffset);

            Vector3 translation = reader.ReadVector3(field + ".translation");
            Vector3 rotation = reader.ReadVector3(field + ".rotation");
            Vector3 translationLowerLimit = reader.ReadVector3(field + ".translationLowerLimit");
            Vector3 translationUpperLimit = reader.ReadVector3(field + ".translationUpperLimit");
            Vector3 rotationLowerLimit = reader.ReadVector3(field + ".rotationLowerLimit");
            Vector3 rotationUpperLimit = reader.ReadVector3(field + ".rotationUpperLimit");
            Vector3 springTranslationFactor = reader.ReadVector3(field + ".springTranslationFactor");
            Vector3 springRotationFactor = reader.ReadVector3(field + ".springRotationFactor");

            joints[i] = new PmxJoint
            {
                Name = name,
                EnglishName = englishName,
                Type = (PmxJointType)typeValue,
                RigidBodyAIndex = rigidBodyAIndex,
                RigidBodyBIndex = rigidBodyBIndex,
                Translation = translation,
                Rotation = rotation,
                TranslationLowerLimit = translationLowerLimit,
                TranslationUpperLimit = translationUpperLimit,
                RotationLowerLimit = rotationLowerLimit,
                RotationUpperLimit = rotationUpperLimit,
                SpringTranslationFactor = springTranslationFactor,
                SpringRotationFactor = springRotationFactor,
            };
        }

        return joints;
    }

    private static IReadOnlyList<PmxSoftBody> ReadSoftBodies(
        PmxBinaryReader reader,
        PmxHeader header,
        int vertexCount,
        int materialCount,
        int rigidBodyCount)
    {
        int count = reader.ReadCount("softBodies", minimumElementBytes: 16);
        var softBodies = new PmxSoftBody[count];

        for (int i = 0; i < count; i++)
        {
            string field = $"softBodies[{i}]";
            string name = reader.ReadString(header.TextEncoding, field + ".name");
            string englishName = reader.ReadString(header.TextEncoding, field + ".englishName");

            long typeOffset = reader.Position;
            byte typeValue = reader.ReadByte(field + ".type");
            if (typeValue > (byte)PmxSoftBodyType.Rope)
            {
                throw reader.Error(
                    field + ".type",
                    typeOffset,
                    $"unknown soft body type {typeValue}; expected 0 or 1");
            }

            long materialOffset = reader.Position;
            int materialIndex = reader.ReadSignedIndex(header.MaterialIndexSize, field + ".materialIndex");
            CheckIndex(
                reader, materialIndex, materialCount,
                field + ".materialIndex", materialOffset);

            byte group = reader.ReadByte(field + ".group");
            ushort collisionGroup = reader.ReadUInt16(field + ".collisionGroup");
            var flags = (PmxSoftBodyFlags)reader.ReadByte(field + ".flags");
            int blinkLength = reader.ReadInt32(field + ".bLinkLength");
            int clusterCount = reader.ReadInt32(field + ".clusterCount");
            float totalMass = reader.ReadSingle(field + ".totalMass");
            float collisionMargin = reader.ReadSingle(field + ".collisionMargin");
            int aeroModel = reader.ReadInt32(field + ".aeroModel");

            var config = new PmxSoftBodyConfig(
                reader.ReadSingle(field + ".config.vcf"),
                reader.ReadSingle(field + ".config.dp"),
                reader.ReadSingle(field + ".config.dg"),
                reader.ReadSingle(field + ".config.lf"),
                reader.ReadSingle(field + ".config.pr"),
                reader.ReadSingle(field + ".config.vc"),
                reader.ReadSingle(field + ".config.df"),
                reader.ReadSingle(field + ".config.mt"),
                reader.ReadSingle(field + ".config.chr"),
                reader.ReadSingle(field + ".config.khr"),
                reader.ReadSingle(field + ".config.shr"),
                reader.ReadSingle(field + ".config.ahr"));

            var clusterConfig = new PmxSoftBodyClusterConfig(
                reader.ReadSingle(field + ".cluster.srhrCl"),
                reader.ReadSingle(field + ".cluster.skhrCl"),
                reader.ReadSingle(field + ".cluster.sshrCl"),
                reader.ReadSingle(field + ".cluster.srSpltCl"),
                reader.ReadSingle(field + ".cluster.skSpltCl"),
                reader.ReadSingle(field + ".cluster.ssSpltCl"));

            var iterations = new PmxSoftBodyIterations(
                reader.ReadInt32(field + ".iterations.v"),
                reader.ReadInt32(field + ".iterations.p"),
                reader.ReadInt32(field + ".iterations.d"),
                reader.ReadInt32(field + ".iterations.c"));

            var materialConfig = new PmxSoftBodyMaterialConfig(
                reader.ReadSingle(field + ".material.lst"),
                reader.ReadSingle(field + ".material.ast"),
                reader.ReadSingle(field + ".material.vst"));

            int anchorCount = reader.ReadCount(field + ".anchors", minimumElementBytes: 2);
            var anchors = new PmxSoftBodyAnchor[anchorCount];
            for (int anchorIndex = 0; anchorIndex < anchorCount; anchorIndex++)
            {
                string anchorField = $"{field}.anchors[{anchorIndex}]";
                long rigidBodyOffset = reader.Position;
                int rigidBodyIndex = reader.ReadSignedIndex(
                    header.RigidBodyIndexSize, anchorField + ".rigidBodyIndex");
                CheckIndex(
                    reader, rigidBodyIndex, rigidBodyCount,
                    anchorField + ".rigidBodyIndex", rigidBodyOffset);

                int vertexIndex = ReadVertexIndex(
                    reader, header, vertexCount, anchorField + ".vertexIndex");

                long nearModeOffset = reader.Position;
                byte nearModeValue = reader.ReadByte(anchorField + ".nearMode");
                if (nearModeValue > 1)
                {
                    throw reader.Error(
                        anchorField + ".nearMode",
                        nearModeOffset,
                        $"invalid near mode {nearModeValue}; expected 0 or 1");
                }

                anchors[anchorIndex] = new PmxSoftBodyAnchor(
                    rigidBodyIndex, vertexIndex, nearModeValue != 0);
            }

            int pinCount = reader.ReadCount(field + ".pinVertexIndices", minimumElementBytes: 1);
            var pins = new int[pinCount];
            for (int pinIndex = 0; pinIndex < pinCount; pinIndex++)
            {
                pins[pinIndex] = ReadVertexIndex(
                    reader, header, vertexCount, $"{field}.pinVertexIndices[{pinIndex}]");
            }

            softBodies[i] = new PmxSoftBody
            {
                Name = name,
                EnglishName = englishName,
                Type = (PmxSoftBodyType)typeValue,
                MaterialIndex = materialIndex,
                Group = group,
                CollisionGroup = collisionGroup,
                Flags = flags,
                BLinkLength = blinkLength,
                ClusterCount = clusterCount,
                TotalMass = totalMass,
                CollisionMargin = collisionMargin,
                AeroModel = aeroModel,
                Config = config,
                ClusterConfig = clusterConfig,
                Iterations = iterations,
                MaterialConfig = materialConfig,
                Anchors = anchors,
                PinVertexIndices = pins,
            };
        }

        return softBodies;
    }

    private static void CheckIndex(
        PmxBinaryReader reader,
        int index,
        int count,
        string field,
        long offset,
        bool allowNone = true)
    {
        int minimum = allowNone ? -1 : 0;
        if (index < minimum || index >= count)
        {
            throw reader.Error(field, offset, $"index {index} is outside [{minimum}, {count})");
        }
    }

    private readonly record struct ImpulseReference(int Morph, int Element, int Index, long Offset);

    /// <summary>Bounds-checked little-endian PMX primitive reader.</summary>
    private sealed class PmxBinaryReader
    {
        private readonly Stream _stream;
        private readonly long _length;
        private long _position;

        public PmxBinaryReader(Stream stream, string? filePath)
        {
            _stream = stream;
            FilePath = filePath;
            if (!stream.CanSeek)
            {
                throw new ArgumentException("PMX reader requires a seekable stream.", nameof(stream));
            }

            _length = stream.Length - stream.Position;
            if (_length < 0)
            {
                throw new ArgumentException("Stream position is past the end of the stream.", nameof(stream));
            }
        }

        public string? FilePath { get; }

        public long Position => _position;

        public bool IsAtEnd => _position >= _length;

        public PmxFormatException Error(string field, long offset, string detail) =>
            new(field, offset, detail, FilePath);

        public void ReadBytes(Span<byte> destination, string field)
        {
            long offset = _position;
            ReadExact(destination, field, offset);
        }

        public byte ReadByte(string field)
        {
            long offset = _position;
            Span<byte> buffer = stackalloc byte[1];
            ReadExact(buffer, field, offset);
            return buffer[0];
        }

        public ushort ReadUInt16(string field)
        {
            long offset = _position;
            Span<byte> buffer = stackalloc byte[2];
            ReadExact(buffer, field, offset);
            return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        }

        public int ReadInt32(string field)
        {
            long offset = _position;
            Span<byte> buffer = stackalloc byte[4];
            ReadExact(buffer, field, offset);
            return BinaryPrimitives.ReadInt32LittleEndian(buffer);
        }

        public float ReadSingle(string field)
        {
            long offset = _position;
            Span<byte> buffer = stackalloc byte[4];
            ReadExact(buffer, field, offset);
            return BinaryPrimitives.ReadSingleLittleEndian(buffer);
        }

        public Vector2 ReadVector2(string field)
        {
            long offset = _position;
            Span<byte> buffer = stackalloc byte[8];
            ReadExact(buffer, field, offset);
            return new Vector2(
                BinaryPrimitives.ReadSingleLittleEndian(buffer),
                BinaryPrimitives.ReadSingleLittleEndian(buffer[4..]));
        }

        public Vector3 ReadVector3(string field)
        {
            long offset = _position;
            Span<byte> buffer = stackalloc byte[12];
            ReadExact(buffer, field, offset);
            return new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(buffer),
                BinaryPrimitives.ReadSingleLittleEndian(buffer[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(buffer[8..]));
        }

        public Vector4 ReadVector4(string field)
        {
            long offset = _position;
            Span<byte> buffer = stackalloc byte[16];
            ReadExact(buffer, field, offset);
            return new Vector4(
                BinaryPrimitives.ReadSingleLittleEndian(buffer),
                BinaryPrimitives.ReadSingleLittleEndian(buffer[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(buffer[8..]),
                BinaryPrimitives.ReadSingleLittleEndian(buffer[12..]));
        }

        // Only vertex indices use unsigned byte/ushort encodings. In particular,
        // FF and FFFF are ordinary vertex values rather than the -1 sentinel.
        public int ReadVertexIndexValue(int indexSize, string field) => indexSize switch
        {
            1 => ReadByte(field),
            2 => ReadUInt16(field),
            4 => ReadInt32(field),
            _ => throw Error(field, _position, $"unsupported index width {indexSize}"),
        };

        public int ReadSignedIndex(int indexSize, string field) => indexSize switch
        {
            1 => unchecked((sbyte)ReadByte(field)),
            2 => unchecked((short)ReadUInt16(field)),
            4 => ReadInt32(field),
            _ => throw Error(field, _position, $"unsupported index width {indexSize}"),
        };

        public int ReadCount(string field, int minimumElementBytes)
        {
            long offset = _position;
            int count = ReadInt32(field + ".count");
            if (count < 0)
            {
                throw Error(field + ".count", offset, $"negative element count {count}");
            }

            if (count > MaxElementCount)
            {
                throw Error(
                    field + ".count",
                    offset,
                    $"element count {count} exceeds the safety limit {MaxElementCount}");
            }

            long remaining = _length - _position;
            if (minimumElementBytes > 0 && count > remaining / minimumElementBytes)
            {
                throw Error(
                    field + ".count",
                    offset,
                    $"declares {count} element(s) of at least {minimumElementBytes} byte(s), " +
                    $"but only {remaining} byte(s) remain");
            }

            return count;
        }

        public string ReadString(PmxTextEncoding encoding, string field)
        {
            long offset = _position;
            int byteLength = ReadInt32(field + ".length");
            if (byteLength < 0)
            {
                throw Error(field + ".length", offset, $"negative string length {byteLength}");
            }

            if (byteLength > MaxStringByteLength)
            {
                throw Error(
                    field + ".length",
                    offset,
                    $"string length {byteLength} exceeds the safety limit {MaxStringByteLength}");
            }

            if (byteLength == 0)
            {
                return string.Empty;
            }

            byte[] bytes = new byte[byteLength];
            ReadBytes(bytes, field + ".data");

            try
            {
                if (encoding == PmxTextEncoding.Utf16LittleEndian)
                {
                    if ((byteLength & 1) != 0)
                    {
                        throw Error(
                            field,
                            offset,
                            $"UTF-16LE string has odd byte length {byteLength}");
                    }

                    return Utf16LeStrict.GetString(bytes);
                }

                return Utf8Strict.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new PmxFormatException(
                    field,
                    offset,
                    $"string is not valid {(encoding == PmxTextEncoding.Utf16LittleEndian ? "UTF-16LE" : "UTF-8")}",
                    FilePath,
                    exception);
            }
        }

        private void ReadExact(Span<byte> destination, string field, long offset)
        {
            long remaining = _length - _position;
            if (destination.Length > remaining)
            {
                throw Error(
                    field,
                    offset,
                    $"unexpected end of data: need {destination.Length} byte(s), " +
                    $"only {remaining} remain");
            }

            try
            {
                _stream.ReadExactly(destination);
            }
            catch (EndOfStreamException exception)
            {
                throw new PmxFormatException(
                    field,
                    offset,
                    $"unexpected end of data while reading {destination.Length} byte(s)",
                    FilePath,
                    exception);
            }

            _position += destination.Length;
        }
    }
}
