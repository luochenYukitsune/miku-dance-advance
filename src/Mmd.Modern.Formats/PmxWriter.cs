using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Mmd.Modern.Formats;

/// <summary>Writes represented PMX 2.0/2.1 data without changing encodings, indices or source objects.</summary>
public static class PmxWriter
{
    /// <summary>
    /// Prepares and validates the entire model before writing at the current stream position.
    /// Leaves the stream open. A subsequent destination I/O failure may still partially write.
    /// </summary>
    public static void Write(Stream stream, PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(model);
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        byte[] bytes = ToBytes(model);
        stream.Write(bytes);
    }

    /// <summary>
    /// Validates the model and configured byte budget before streaming at the current position.
    /// Supports nonseekable output and leaves the caller stream open and unflushed. Destination
    /// I/O failure may partially write. The caller must not mutate model collections during this
    /// two-pass operation. No serialized whole-file buffer or second model copy is created.
    /// </summary>
    public static void Write(Stream stream, PmxModel model, PmxWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        options.Validate();
        new Serializer(model, null, options.MaxOutputBytes).Serialize();
        new Serializer(model, stream, options.MaxOutputBytes).Serialize();
    }

    /// <summary>Serializes all represented sections. Rejects data a conditional PMX field cannot retain.</summary>
    public static byte[] ToBytes(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        using var buffer = new MemoryStream();
        new Serializer(model, buffer, 512L * 1024 * 1024).Serialize();
        // Retain the legacy independent reader validation before returning bytes.
        buffer.Position = 0;
        _ = PmxReader.Read(buffer);
        return buffer.ToArray();
    }

    private sealed class Serializer
    {
        private const int MaxElements = 100_000_000;
        private const int MaxStringBytes = 64 * 1024 * 1024;
        private readonly PmxModel _model;
        private readonly Stream? _output;
        private readonly long _maxOutputBytes;
        private long _position;
        private readonly Encoding _encoding;
        private readonly PmxHeader _header;
        private string _field = "model";

        public Serializer(PmxModel model, Stream? output, long maxOutputBytes)
        {
            _model = model;
            _output = output;
            _maxOutputBytes = maxOutputBytes;
            _header = model.Header ?? throw new PmxFormatException("header", 0, "header is null");
            _encoding = _header.TextEncoding switch
            {
                PmxTextEncoding.Utf8 => new UTF8Encoding(false, true),
                PmxTextEncoding.Utf16LittleEndian => new UnicodeEncoding(false, false, true),
                _ => throw new PmxFormatException("header.textEncoding", 9, "expected UTF-8 or UTF-16LE"),
            };
        }

        public void Serialize()
        {
            // Validate top-level counts up front, including forward-reference destinations.
            ListCount(_model.Vertices, "vertices"); ListCount(_model.Faces, "faces");
            ListCount(_model.Textures, "textures"); ListCount(_model.Materials, "materials");
            ListCount(_model.Bones, "bones"); ListCount(_model.Morphs, "morphs");
            ListCount(_model.DisplayFrames, "displayFrames"); ListCount(_model.RigidBodies, "rigidBodies");
            ListCount(_model.Joints, "joints"); ListCount(_model.SoftBodies, "softBodies");
            Header();
            Text(_model.Name, "model.name");
            Text(_model.EnglishName, "model.englishName");
            Text(_model.Comment, "model.comment");
            Text(_model.EnglishComment, "model.englishComment");
            Vertices();
            Faces();
            Count(_model.Textures, "textures");
            for (int i = 0; i < _model.Textures.Count; i++) Text(_model.Textures[i], $"textures[{i}]");
            Materials();
            Bones();
            Morphs();
            DisplayFrames();
            RigidBodies();
            Joints();
            if (_header.IsVersion21) SoftBodies();
            else Require(_model.SoftBodies is { Count: 0 }, "softBodies", "requires PMX 2.1");
        }

        private void Header()
        {
            Require(_header.Version is 2.0f or 2.1f, "header.version", "expected exactly PMX 2.0 or 2.1");
            Require(_header.HeaderDataSize == 8, "header.dataSize", "expected 8 settings");
            Require(_header.AdditionalUvCount is >= 0 and <= 4, "header.additionalUvCount", "expected 0-4");
            int[] widths = [_header.VertexIndexSize, _header.TextureIndexSize, _header.MaterialIndexSize,
                _header.BoneIndexSize, _header.MorphIndexSize, _header.RigidBodyIndexSize];
            string[] names = ["vertexIndexSize", "textureIndexSize", "materialIndexSize", "boneIndexSize",
                "morphIndexSize", "rigidBodyIndexSize"];
            for (int i = 0; i < widths.Length; i++)
                Require(widths[i] is 1 or 2 or 4, "header." + names[i], "expected 1, 2 or 4");
            B((byte)'P'); B((byte)'M'); B((byte)'X'); B((byte)' ');
            F(_header.Version); B(8); B((byte)_header.TextEncoding); B((byte)_header.AdditionalUvCount);
            foreach (int width in widths) B((byte)width);
        }

        private void Vertices()
        {
            Count(_model.Vertices, "vertices");
            for (int i = 0; i < _model.Vertices.Count; i++)
            {
                _field = $"vertices[{i}]";
                PmxVertex v = Item(_model.Vertices[i]);
                Require(v.AdditionalUvs is not null && v.AdditionalUvs.Count == _header.AdditionalUvCount,
                    _field + ".additionalUvs", "channel count differs from header");
                Require(v.BoneIndices is { Count: 4 } && v.BoneWeights is { Count: 4 },
                    _field + ".boneWeights", "exactly four bone indices and weights are required");
                Require(v.WeightType is >= PmxVertexWeightType.Bdef1 and <= PmxVertexWeightType.Qdef,
                    _field + ".weightType", "unknown skinning type");
                Require(v.WeightType != PmxVertexWeightType.Qdef || _header.IsVersion21,
                    _field + ".weightType", "QDEF requires PMX 2.1");
                V(v.Position); V(v.Normal); V(v.Uv);
                foreach (Vector4 uv in v.AdditionalUvs!) V(uv);
                B((byte)v.WeightType);
                int slots = v.WeightType switch
                {
                    PmxVertexWeightType.Bdef1 => 1,
                    PmxVertexWeightType.Bdef2 or PmxVertexWeightType.Sdef => 2,
                    _ => 4,
                };
                for (int slot = 0; slot < slots; slot++)
                    Index(v.BoneIndices![slot], _header.BoneIndexSize, _model.Bones.Count, $"{_field}.boneIndices[{slot}]");
                for (int slot = slots; slot < 4; slot++)
                    Require(v.BoneIndices![slot] == -1 && v.BoneWeights![slot] == 0,
                        _field + ".boneWeights", "unused bone slots must contain index -1 and weight 0");
                if (slots == 1)
                    Require(v.BoneWeights![0] == 1, _field + ".boneWeights[0]", "BDEF1 weight is implicitly 1");
                else if (slots == 2)
                {
                    F(v.BoneWeights![0]);
                    Require(v.BoneWeights[1] == 1f - v.BoneWeights[0], _field + ".boneWeights[1]",
                        "BDEF2/SDEF second weight must equal 1 minus first weight");
                }
                else foreach (float weight in v.BoneWeights!) F(weight);
                if (v.WeightType == PmxVertexWeightType.Sdef) { V(v.SdefC); V(v.SdefR0); V(v.SdefR1); }
                else Require(v.SdefC == default && v.SdefR0 == default && v.SdefR1 == default,
                    _field + ".sdef", "non-SDEF vertices cannot retain SDEF vectors");
                F(v.EdgeScale);
            }
        }

        private void Faces()
        {
            _field = "faces";
            int faces = ListCount(_model.Faces, _field);
            Require(faces <= MaxElements / 3, "faces.count", "face-index count exceeds safety limit");
            I(faces * 3);
            for (int i = 0; i < faces; i++)
            {
                _field = $"faces[{i}]";
                PmxTriangle f = _model.Faces[i];
                Index(f.A, _header.VertexIndexSize, _model.Vertices.Count, _field + ".a", unsigned: true);
                Index(f.B, _header.VertexIndexSize, _model.Vertices.Count, _field + ".b", unsigned: true);
                Index(f.C, _header.VertexIndexSize, _model.Vertices.Count, _field + ".c", unsigned: true);
            }
        }

        private void Materials()
        {
            Count(_model.Materials, "materials");
            long assignedIndices = 0;
            for (int i = 0; i < _model.Materials.Count; i++)
            {
                _field = $"materials[{i}]";
                PmxMaterial m = Item(_model.Materials[i]);
                Require(m.SphereMode is >= PmxSphereMode.None and <= PmxSphereMode.SubTexture,
                    _field + ".sphereMode", "unknown sphere mode");
                Require(m.ToonMode is PmxToonMode.Separate or PmxToonMode.Common,
                    _field + ".toonMode", "unknown toon mode");
                Require(m.FaceVertexCount >= 0 && m.FaceVertexCount % 3 == 0,
                    _field + ".faceVertexCount", "face vertex count must be a nonnegative multiple of 3");
                assignedIndices += m.FaceVertexCount;
                Require(assignedIndices <= (long)_model.Faces.Count * 3,
                    _field + ".faceVertexCount", "material range exceeds face indices");
                Names(m.Name, m.EnglishName);
                V(m.Diffuse); V(m.Specular); F(m.SpecularPower); V(m.Ambient);
                Require(_header.IsVersion21 || ((byte)m.DrawMode & 0xE0) == 0,
                    _field + ".drawMode", "vertex color, point and line flags require PMX 2.1");
                B((byte)m.DrawMode); V(m.EdgeColor); F(m.EdgeSize);
                Index(m.TextureIndex, _header.TextureIndexSize, _model.Textures.Count, _field + ".textureIndex");
                Index(m.SphereTextureIndex, _header.TextureIndexSize, _model.Textures.Count, _field + ".sphereTextureIndex");
                B((byte)m.SphereMode); B((byte)m.ToonMode);
                if (m.ToonMode == PmxToonMode.Common)
                {
                    Require(m.ToonTextureIndex is >= 0 and <= 9, _field + ".toonTextureIndex",
                        "common toon must be 0-9");
                    B((byte)m.ToonTextureIndex);
                }
                else Index(m.ToonTextureIndex, _header.TextureIndexSize, _model.Textures.Count, _field + ".toonTextureIndex");
                Text(m.Memo, _field + ".memo"); I(m.FaceVertexCount);
            }
            Require(assignedIndices == (long)_model.Faces.Count * 3,
                "materials.faceVertexCount", "material ranges must cover all face indices");
        }

        private void Bones()
        {
            Count(_model.Bones, "bones");
            for (int i = 0; i < _model.Bones.Count; i++)
            {
                _field = $"bones[{i}]";
                PmxBone b = Item(_model.Bones[i]);
                Names(b.Name, b.EnglishName); V(b.Position);
                Index(b.ParentIndex, _header.BoneIndexSize, _model.Bones.Count, _field + ".parentIndex");
                I(b.DeformDepth); U((ushort)b.Flags);
                if (Has(b, PmxBoneFlags.TargetShowMode))
                {
                    Require(b.PositionOffset == default, _field + ".positionOffset", "inactive offset must be zero");
                    Index(b.LinkBoneIndex, _header.BoneIndexSize, _model.Bones.Count, _field + ".linkBoneIndex");
                }
                else
                {
                    Require(b.LinkBoneIndex == -1, _field + ".linkBoneIndex", "inactive link must be -1");
                    V(b.PositionOffset);
                }
                if (Has(b, PmxBoneFlags.AppendRotate | PmxBoneFlags.AppendTranslate))
                {
                    Index(b.AppendBoneIndex, _header.BoneIndexSize, _model.Bones.Count, _field + ".appendBoneIndex"); F(b.AppendWeight);
                }
                else Require(b.AppendBoneIndex == -1 && b.AppendWeight == 0,
                    _field + ".appendBoneIndex", "inactive append data cannot be retained");
                if (Has(b, PmxBoneFlags.FixedAxis)) V(b.FixedAxis);
                else Require(b.FixedAxis == default, _field + ".fixedAxis", "inactive axis must be zero");
                if (Has(b, PmxBoneFlags.LocalAxis)) { V(b.LocalXAxis); V(b.LocalZAxis); }
                else Require(b.LocalXAxis == default && b.LocalZAxis == default,
                    _field + ".localAxis", "inactive axes must be zero");
                if (Has(b, PmxBoneFlags.DeformOuterParent)) I(b.ExternalParentKey);
                else Require(b.ExternalParentKey == 0, _field + ".externalParentKey", "inactive key must be zero");
                if (Has(b, PmxBoneFlags.Ik))
                {
                    Require(b.IkIterationCount >= 0, _field + ".ikIterationCount", "negative IK iteration count");
                    Index(b.IkTargetBoneIndex, _header.BoneIndexSize, _model.Bones.Count, _field + ".ikTargetBoneIndex");
                    I(b.IkIterationCount); F(b.IkLimit); Count(b.IkLinks, _field + ".ikLinks");
                    for (int j = 0; j < b.IkLinks.Count; j++)
                    {
                        PmxIkLink link = b.IkLinks[j];
                        Index(link.BoneIndex, _header.BoneIndexSize, _model.Bones.Count, $"{_field}.ikLinks[{j}].boneIndex");
                        B(link.EnableLimit ? (byte)1 : (byte)0);
                        if (link.EnableLimit) { V(link.LimitMin); V(link.LimitMax); }
                        else Require(link.LimitMin == default && link.LimitMax == default,
                            $"{_field}.ikLinks[{j}]", "inactive limits must be zero");
                    }
                }
                else Require(b.IkTargetBoneIndex == -1 && b.IkIterationCount == 0 && b.IkLimit == 0 &&
                    b.IkLinks is { Count: 0 }, _field + ".ikLinks", "inactive IK data cannot be retained");
            }
        }

        private void Morphs()
        {
            Count(_model.Morphs, "morphs");
            for (int i = 0; i < _model.Morphs.Count; i++)
            {
                _field = $"morphs[{i}]";
                PmxMorph m = Item(_model.Morphs[i]);
                Names(m.Name, m.EnglishName); B(m.ControlPanel); B((byte)m.Type);
                Require(m.Type is >= PmxMorphType.Group and <= PmxMorphType.Impulse,
                    _field + ".type", "unknown morph type");
                Require(_header.IsVersion21 || m.Type < PmxMorphType.Flip,
                    _field + ".type", "flip/impulse morphs require PMX 2.1");
                bool uv = m.Type is >= PmxMorphType.Uv and <= PmxMorphType.AdditionalUv4;
                Require(m.Type == PmxMorphType.Group || Empty(m.GroupOffsets), _field + ".groupOffsets", "inactive data");
                Require(m.Type == PmxMorphType.Position || Empty(m.PositionOffsets), _field + ".positionOffsets", "inactive data");
                Require(m.Type == PmxMorphType.Bone || Empty(m.BoneOffsets), _field + ".boneOffsets", "inactive data");
                Require(uv || Empty(m.UvOffsets), _field + ".uvOffsets", "inactive data");
                Require(m.Type == PmxMorphType.Material || Empty(m.MaterialOffsets), _field + ".materialOffsets", "inactive data");
                Require(m.Type == PmxMorphType.Flip || Empty(m.FlipOffsets), _field + ".flipOffsets", "inactive data");
                Require(m.Type == PmxMorphType.Impulse || Empty(m.ImpulseOffsets), _field + ".impulseOffsets", "inactive data");
                switch (m.Type)
                {
                    case PmxMorphType.Group:
                        Count(m.GroupOffsets, _field + ".groupOffsets");
                        foreach (var x in m.GroupOffsets) { Index(x.MorphIndex, _header.MorphIndexSize, _model.Morphs.Count, _field + ".morphIndex", allowNone: false); F(x.Weight); }
                        break;
                    case PmxMorphType.Position:
                        Count(m.PositionOffsets, _field + ".positionOffsets");
                        foreach (var x in m.PositionOffsets) { VertexIndex(x.VertexIndex); V(x.Position); }
                        break;
                    case PmxMorphType.Bone:
                        Count(m.BoneOffsets, _field + ".boneOffsets");
                        foreach (var x in m.BoneOffsets) { Index(x.BoneIndex, _header.BoneIndexSize, _model.Bones.Count, _field + ".boneIndex"); V(x.Position); V(x.Rotation); }
                        break;
                    case >= PmxMorphType.Uv and <= PmxMorphType.AdditionalUv4:
                        Count(m.UvOffsets, _field + ".uvOffsets");
                        foreach (var x in m.UvOffsets) { VertexIndex(x.VertexIndex); V(x.Uv); }
                        break;
                    case PmxMorphType.Material:
                        Count(m.MaterialOffsets, _field + ".materialOffsets");
                        foreach (var x in m.MaterialOffsets)
                        {
                            Require(x.Operation is PmxMaterialMorphOperation.Multiply or PmxMaterialMorphOperation.Add,
                                _field + ".operation", "unknown material morph operation");
                            Index(x.MaterialIndex, _header.MaterialIndexSize, _model.Materials.Count, _field + ".materialIndex"); B((byte)x.Operation);
                            V(x.Diffuse); V(x.Specular); F(x.SpecularPower); V(x.Ambient); V(x.EdgeColor); F(x.EdgeSize);
                            V(x.TextureFactor); V(x.SphereTextureFactor); V(x.ToonTextureFactor);
                        }
                        break;
                    case PmxMorphType.Flip:
                        Count(m.FlipOffsets, _field + ".flipOffsets");
                        foreach (var x in m.FlipOffsets) { Index(x.MorphIndex, _header.MorphIndexSize, _model.Morphs.Count, _field + ".morphIndex", allowNone: false); F(x.Weight); }
                        break;
                    case PmxMorphType.Impulse:
                        Count(m.ImpulseOffsets, _field + ".impulseOffsets");
                        foreach (var x in m.ImpulseOffsets)
                        {
                            Index(x.RigidBodyIndex, _header.RigidBodyIndexSize, _model.RigidBodies.Count, _field + ".rigidBodyIndex", allowNone: false);
                            B(x.Local ? (byte)1 : (byte)0); V(x.TranslationVelocity); V(x.RotationTorque);
                        }
                        break;
                }
            }
        }

        private void DisplayFrames()
        {
            Count(_model.DisplayFrames, "displayFrames");
            for (int i = 0; i < _model.DisplayFrames.Count; i++)
            {
                _field = $"displayFrames[{i}]";
                PmxDisplayFrame d = Item(_model.DisplayFrames[i]);
                Require(d.Type is PmxDisplayFrameType.Default or PmxDisplayFrameType.Special,
                    _field + ".type", "unknown display frame type");
                Names(d.Name, d.EnglishName); B((byte)d.Type); Count(d.Targets, _field + ".targets");
                foreach (PmxDisplayTarget t in d.Targets)
                {
                    Require(t.Type is PmxDisplayTargetType.Bone or PmxDisplayTargetType.Morph,
                        _field + ".targetType", "unknown display target type");
                    B((byte)t.Type);
                    Index(t.Index, t.Type == PmxDisplayTargetType.Bone ? _header.BoneIndexSize : _header.MorphIndexSize,
                        t.Type == PmxDisplayTargetType.Bone ? _model.Bones.Count : _model.Morphs.Count, _field + ".index");
                }
            }
        }

        private void RigidBodies()
        {
            Count(_model.RigidBodies, "rigidBodies");
            for (int i = 0; i < _model.RigidBodies.Count; i++)
            {
                _field = $"rigidBodies[{i}]";
                PmxRigidBody r = Item(_model.RigidBodies[i]);
                Require(r.Group <= 15, _field + ".group", "rigid body group must be 0-15");
                Require(r.Shape is >= PmxRigidBodyShape.Sphere and <= PmxRigidBodyShape.Capsule,
                    _field + ".shape", "unknown rigid body shape");
                Require(r.Operation is >= PmxRigidBodyOperation.Static and <= PmxRigidBodyOperation.DynamicAndBoneMerge,
                    _field + ".operation", "unknown rigid body operation");
                Names(r.Name, r.EnglishName); Index(r.BoneIndex, _header.BoneIndexSize, _model.Bones.Count, _field + ".boneIndex");
                B(r.Group); U(r.CollisionGroup); B((byte)r.Shape); V(r.ShapeSize); V(r.Translation); V(r.Rotation);
                F(r.Mass); F(r.TranslationDamping); F(r.RotationDamping); F(r.Repulsion); F(r.Friction); B((byte)r.Operation);
            }
        }

        private void Joints()
        {
            Count(_model.Joints, "joints");
            for (int i = 0; i < _model.Joints.Count; i++)
            {
                _field = $"joints[{i}]";
                PmxJoint j = Item(_model.Joints[i]);
                Require(j.Type is >= PmxJointType.SpringDof6 and <= PmxJointType.Hinge,
                    _field + ".type", "unknown joint type");
                Require(_header.IsVersion21 || j.Type == PmxJointType.SpringDof6,
                    _field + ".type", "extended joint types require PMX 2.1");
                Names(j.Name, j.EnglishName); B((byte)j.Type);
                Index(j.RigidBodyAIndex, _header.RigidBodyIndexSize, _model.RigidBodies.Count, _field + ".rigidBodyAIndex");
                Index(j.RigidBodyBIndex, _header.RigidBodyIndexSize, _model.RigidBodies.Count, _field + ".rigidBodyBIndex");
                V(j.Translation); V(j.Rotation); V(j.TranslationLowerLimit); V(j.TranslationUpperLimit);
                V(j.RotationLowerLimit); V(j.RotationUpperLimit); V(j.SpringTranslationFactor); V(j.SpringRotationFactor);
            }
        }

        private void SoftBodies()
        {
            Count(_model.SoftBodies, "softBodies");
            for (int i = 0; i < _model.SoftBodies.Count; i++)
            {
                _field = $"softBodies[{i}]";
                PmxSoftBody s = Item(_model.SoftBodies[i]);
                Require(s.Type is PmxSoftBodyType.TriMesh or PmxSoftBodyType.Rope,
                    _field + ".type", "unknown soft body type");
                Names(s.Name, s.EnglishName); B((byte)s.Type);
                Index(s.MaterialIndex, _header.MaterialIndexSize, _model.Materials.Count, _field + ".materialIndex");
                B(s.Group); U(s.CollisionGroup); B((byte)s.Flags); I(s.BLinkLength); I(s.ClusterCount);
                F(s.TotalMass); F(s.CollisionMargin); I(s.AeroModel);
                PmxSoftBodyConfig c = s.Config;
                F(c.Vcf); F(c.Dp); F(c.Dg); F(c.Lf); F(c.Pr); F(c.Vc); F(c.Df); F(c.Mt);
                F(c.Chr); F(c.Khr); F(c.Shr); F(c.Ahr);
                PmxSoftBodyClusterConfig cl = s.ClusterConfig;
                F(cl.SrhrCl); F(cl.SkhrCl); F(cl.SshrCl); F(cl.SrSpltCl); F(cl.SkSpltCl); F(cl.SsSpltCl);
                I(s.Iterations.V); I(s.Iterations.P); I(s.Iterations.D); I(s.Iterations.C);
                F(s.MaterialConfig.Lst); F(s.MaterialConfig.Ast); F(s.MaterialConfig.Vst);
                Count(s.Anchors, _field + ".anchors");
                foreach (PmxSoftBodyAnchor a in s.Anchors)
                {
                    Index(a.RigidBodyIndex, _header.RigidBodyIndexSize, _model.RigidBodies.Count, _field + ".rigidBodyIndex");
                    VertexIndex(a.VertexIndex); B(a.NearMode ? (byte)1 : (byte)0);
                }
                Count(s.PinVertexIndices, _field + ".pinVertexIndices");
                foreach (int pin in s.PinVertexIndices) VertexIndex(pin);
            }
        }

        private void Names(string name, string englishName)
        {
            Text(name, _field + ".name"); Text(englishName, _field + ".englishName");
        }
        private void Text(string? text, string field)
        {
            Require(text is not null && !text.Contains('\0'), field, "null/NUL text cannot be exported");
            try
            {
                int bytes = _encoding.GetByteCount(text!);
                Require(bytes <= MaxStringBytes, field, "encoded text exceeds 64 MiB safety limit");
                Reserve((long)bytes + 4); I(bytes);
                if (_output is null) _position += bytes;
                else
                {
                    // Preserve Unicode scalar boundaries with bounded scratch storage.
                    Encoder encoder = _encoding.GetEncoder();
                    ReadOnlySpan<char> remaining = text.AsSpan();
                    Span<byte> buffer = stackalloc byte[4096];
                    bool completed;
                    do
                    {
                        encoder.Convert(remaining, buffer, flush: true, out int charsUsed, out int bytesUsed, out completed);
                        WriteBytes(buffer[..bytesUsed]);
                        remaining = remaining[charsUsed..];
                    } while (!completed);
                }
            }
            catch (EncoderFallbackException)
            {
                throw Error(field, "text contains invalid Unicode; no replacement is allowed");
            }
        }
        private void Index(int index, int width, int count, string field, bool unsigned = false, bool allowNone = true)
        {
            long minimum = unsigned || !allowNone ? 0 : -1;
            long maximum = width switch
            {
                1 => unsigned ? byte.MaxValue : sbyte.MaxValue,
                2 => unsigned ? ushort.MaxValue : short.MaxValue,
                _ => int.MaxValue,
            };
            if (index < minimum || index >= count)
                throw Error(field, $"index {index} is outside [{minimum}, {count})");
            if (index > maximum)
                throw Error(field, $"index {index} does not fit {width}-byte encoding");
            if (width == 1) B(unchecked((byte)index));
            else if (width == 2) U(unchecked((ushort)index));
            else I(index);
        }
        private void VertexIndex(int index) => Index(index, _header.VertexIndexSize, _model.Vertices.Count, _field + ".vertexIndex", unsigned: true);
        private int ListCount<T>(IReadOnlyList<T>? list, string field)
        {
            Require(list is not null && list.Count is >= 0 and <= MaxElements, field + ".count", "invalid/null/oversized list");
            return list!.Count;
        }
        private void Count<T>(IReadOnlyList<T>? list, string field) => I(ListCount(list, field));
        private T Item<T>(T? item) where T : class => item ?? throw Error(_field, "null record");
        private static bool Empty<T>(IReadOnlyList<T>? list) => list is { Count: 0 };
        private static bool Has(PmxBone bone, PmxBoneFlags flags) => (bone.Flags & flags) != 0;
        private void V(Vector2 value) { F(value.X); F(value.Y); }
        private void V(Vector3 value) { F(value.X); F(value.Y); F(value.Z); }
        private void V(Vector4 value) { F(value.X); F(value.Y); F(value.Z); F(value.W); }
        private void V(Quaternion value) { F(value.X); F(value.Y); F(value.Z); F(value.W); }
        private void B(byte value) { Span<byte> bytes = stackalloc byte[1]; bytes[0] = value; WriteBytes(bytes); }
        private void U(ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); WriteBytes(bytes); }
        private void I(int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); WriteBytes(bytes); }
        private void F(float value)
        {
            Require(float.IsFinite(value), _field, "non-finite float cannot be exported");
            Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(bytes, value); WriteBytes(bytes);
        }
        private void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            Reserve(bytes.Length);
            _output?.Write(bytes);
            _position += bytes.Length;
        }
        private void Reserve(long bytes)
        {
            if (bytes > _maxOutputBytes - _position)
                throw Error(_field, $"PMX output exceeds configured byte limit {_maxOutputBytes}");
        }
        private void Require(bool condition, string field, string message)
        {
            if (!condition) throw Error(field, message);
        }
        private PmxFormatException Error(string field, string message) => new(field, _position, message);
    }
}
