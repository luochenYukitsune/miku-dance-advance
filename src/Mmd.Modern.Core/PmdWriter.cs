// PMD 1.0 layout reference: Saba PMDFile.h/cpp @29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332,
// MIT, Copyright (c) 2016-2017 benikabocha. Optional block order cross-checked against
// nanoem MIT component ext/mutable.c:8641-8737 @30acffaa29f5d2eb9e997d69418f2e4b97b5894f.
// No nanoem editor-host code is included; full Saba license: third_party/saba/LICENCE.txt.
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;

namespace Mmd.Modern.Core;

/// <summary>Writes the represented PMD 1.0 model with strict CP932 strings and no implicit truncation.
/// The reader normalizes normals and does not retain original string padding; arbitrary source-byte
/// preservation is therefore not promised. Unknown bytes following the joint section are retained.</summary>
public static class PmdWriter
{
    /// <summary>Validates in a private buffer before touching the destination, then writes at its current
    /// position and leaves it open. A destination I/O failure can still cause a partial write.</summary>
    public static void Write(Stream stream, PmdModel model)
    {
        ArgumentNullException.ThrowIfNull(stream); ArgumentNullException.ThrowIfNull(model);
        if (!stream.CanWrite) throw new ArgumentException("PMD stream must be writable.", nameof(stream));
        stream.Write(ToBytes(model));
    }

    public static byte[] ToBytes(PmdModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        using var serializer = new Serializer(model);
        return serializer.Serialize();
    }

    private sealed class Serializer : IDisposable
    {
        private const int MaxBytes = 512 * 1024 * 1024;
        private readonly PmdModel model;
        private readonly MemoryStream buffer = new();
        private readonly BinaryWriter writer;
        private readonly Encoding encoding = CodePagesEncodingProvider.Instance.GetEncoding(932,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;

        internal Serializer(PmdModel model) { this.model = model; writer = new(buffer, Encoding.UTF8, true); }
        private static void Require([DoesNotReturnIf(false)] bool condition, string message)
        { if (!condition) throw new InvalidDataException("PMD write: " + message); }
        private void Reserve(int bytes) => Require(bytes >= 0 && buffer.Position + bytes <= MaxBytes, "file exceeds 512 MiB.");
        private void B(byte value) { Reserve(1); writer.Write(value); }
        private void U16(ushort value) { Reserve(2); writer.Write(value); }
        private void U32(uint value) { Reserve(4); writer.Write(value); }
        private void F(float value) { Require(float.IsFinite(value), "non-finite number."); Reserve(4); writer.Write(value); }
        private void V(Vector3 value) { F(value.X); F(value.Y); F(value.Z); }
        private static int Count<T>(T[] items, int max, string label)
        { Require(items is not null && items.Length <= max, $"invalid {label} count."); return items.Length; }
        private void Text(string value, int width)
        {
            Require(value is not null && !value.Contains('\0'), "text is null or contains NUL.");
            byte[] bytes;
            try { bytes = encoding.GetBytes(value); }
            catch (EncoderFallbackException error) { throw new InvalidDataException("PMD text cannot be encoded as CP932.", error); }
            Require(bytes.Length <= width, $"text exceeds {width} CP932 bytes.");
            Reserve(width); writer.Write(bytes); writer.Write(new byte[width - bytes.Length]);
        }
        private ushort Bone(int value, bool none = false)
        {
            if (none && value == -1) return ushort.MaxValue;
            Require(value >= 0 && value < model.Bones.Length, "bone reference is outside the model.");
            return (ushort)value;
        }
        private uint Body(int value)
        {
            if (value == -1) return uint.MaxValue;
            Require(value >= 0 && value < model.RigidBodies.Length, "joint body reference is outside the model.");
            return (uint)value;
        }

        internal byte[] Serialize()
        {
            ValidateSections();
            Reserve(3); writer.Write("Pmd"u8); F(1); Text(model.Name, 20); Text(model.Comment, 256);
            U32((uint)model.Vertices.Length);
            foreach (var v in model.Vertices)
            {
                V(v.Position); V(v.Normal); F(v.Uv.X); F(v.Uv.Y);
                Require(float.IsFinite(v.Normal.LengthSquared()) && v.Normal.LengthSquared() >= 1e-12f, "invalid vertex normal.");
                U16(Bone(v.Bone0)); U16(Bone(v.Bone1));
                float scaled = v.Weight0 * 100;
                Require(float.IsFinite(scaled) && scaled >= 0 && scaled <= 100 && MathF.Abs(scaled - MathF.Round(scaled)) <= 1e-4f,
                    "vertex weight must be an exact PMD percentage in [0,100].");
                B((byte)MathF.Round(scaled)); B(v.EdgeFlag);
            }
            U32((uint)model.Indices.Length); foreach (ushort index in model.Indices) U16(index);
            U32((uint)model.Materials.Length);
            int start = 0;
            foreach (var m in model.Materials)
            {
                Require(m is not null && m.StartIndex == start && m.IndexCount >= 0 && m.IndexCount % 3 == 0
                    && m.IndexCount <= model.Indices.Length - start, "material ranges must cover the index buffer in order.");
                V(new(m.Diffuse.X, m.Diffuse.Y, m.Diffuse.Z)); F(m.Diffuse.W); F(m.Shininess); V(m.Specular); V(m.Ambient);
                Require(m.ToonIndex is <= 9 or 255, "invalid material toon index.");
                B(m.ToonIndex); B(m.EdgeFlag); U32((uint)m.IndexCount); Text(m.Texture, 20); start += m.IndexCount;
            }
            Require(start == model.Indices.Length, "material ranges do not cover the index buffer.");
            U16((ushort)model.Bones.Length);
            foreach (var bone in model.Bones)
            {
                Require(bone is not null && bone.Type <= 9 && bone.TailIndex is >= -1 and < ushort.MaxValue, "invalid bone metadata.");
                Text(bone.Name, 20); U16(Bone(bone.Parent, true)); U16(bone.TailIndex == -1 ? ushort.MaxValue : (ushort)bone.TailIndex);
                B(bone.Type); U16(bone.IkParent); V(bone.Position);
            }
            if (model.HasAnimationTail) AnimationTail();
            writer.Flush(); buffer.Position = 0;
            _ = PmdReader.Read(buffer); // Independent read path validates references, hierarchy and represented section layout.
            return buffer.ToArray();
        }

        private void ValidateSections()
        {
            Count(model.Vertices, 65535, "vertices"); Count(model.Indices, 20_000_000, "indices");
            Count(model.Materials, 100_000, "materials"); Count(model.Bones, 65535, "bones");
            Count(model.IkChains, 65535, "IK chains"); Count(model.Morphs, 65535, "morphs");
            Count(model.MorphDisplay, 255, "morph display"); Count(model.BoneDisplayFrames, 256, "display frames");
            Count(model.ToonTextures, 10, "toon textures"); Count(model.RigidBodies, 100_000, "rigid bodies");
            Count(model.Joints, 100_000, "joints"); Count(model.UnknownTailData, 64 * 1024 * 1024, "unknown trailer");
            Require(model.Indices.Length % 3 == 0 && model.UnparsedTailBytes == model.UnknownTailData.Length, "index count or unknown trailer size mismatch.");
            Require(!model.HasPhysicsExtension || model.HasToonExtension, "physics extension requires the preceding toon extension.");
            Require(model.HasPhysicsExtension || (model.RigidBodies.Length == 0 && model.Joints.Length == 0 && model.UnknownTailData.Length == 0),
                "omitted physics section cannot discard bodies, joints or trailer.");
            Require(model.HasToonExtension || model.ToonTextures.SequenceEqual(Enumerable.Range(1, 10).Select(i => $"toon{i:00}.bmp")),
                "omitted toon section cannot discard custom toon paths.");
            if (!model.HasAnimationTail)
                Require(!model.HasEnglishExtension && !model.HasToonExtension && !model.HasPhysicsExtension
                    && model.IkChains.Length == 0 && model.Morphs.Length == 0 && model.MorphDisplay.Length == 0 && model.BoneDisplayFrames.Length == 0,
                    "omitted animation tail cannot discard metadata or extensions.");
            Require(model.EnglishName is not null && model.EnglishComment is not null, "English model text is null.");
            if (!model.HasEnglishExtension)
                Require(model.EnglishName.Length == 0 && model.EnglishComment.Length == 0
                    && model.Bones.All(b => b is not null && b.EnglishName == "")
                    && model.Morphs.All(m => m is not null && m.EnglishName == "")
                    && model.BoneDisplayFrames.All(f => f is not null && f.EnglishName == ""),
                    "omitted English extension cannot discard English metadata.");
            if (model.Morphs.Length != 0) Require(model.Morphs[0] is not null && model.Morphs[0].EnglishName == "", "base morph has no English slot.");
            if (model.HasAnimationTail)
                Require(model.BoneDisplayFrames.Length >= 1 && model.BoneDisplayFrames[0] is { Name: "センター", EnglishName: "" },
                    "display slot zero must be the implicit center frame with no English slot.");
        }

        private void AnimationTail()
        {
            U16((ushort)model.IkChains.Length);
            foreach (var ik in model.IkChains)
            {
                Require(ik is not null && ik.AngleLimit >= 0, "invalid IK chain.");
                U16(Bone(ik.BoneIndex)); U16(Bone(ik.TargetBoneIndex)); B((byte)Count(ik.Links, 255, "IK links"));
                U16(ik.Iterations); F(ik.AngleLimit); foreach (int link in ik.Links) U16(Bone(link));
            }
            U16((ushort)model.Morphs.Length);
            foreach (var morph in model.Morphs)
            {
                Require(morph is not null, "null morph.");
                Text(morph.Name, 20); U32((uint)Count(morph.Vertices, 20_000_000, "morph offsets")); B(morph.Type);
                foreach (var offset in morph.Vertices) { U32(offset.Index); V(offset.Position); }
            }
            B((byte)model.MorphDisplay.Length); foreach (ushort index in model.MorphDisplay) U16(index);
            B((byte)(model.BoneDisplayFrames.Length - 1));
            foreach (var frame in model.BoneDisplayFrames.Skip(1)) { Require(frame is not null, "null display frame."); Text(frame.Name, 50); }
            long displayCount = model.BoneDisplayFrames.Sum(f => (long)Count(f.Bones, 20_000_000, "display bones"));
            Require(displayCount <= 20_000_000, "too many display bones."); U32((uint)displayCount);
            for (int i = 0; i < model.BoneDisplayFrames.Length; i++)
                foreach (ushort bone in model.BoneDisplayFrames[i].Bones) { U16(Bone(bone)); B((byte)i); }
            if (model.HasEnglishExtension || model.HasToonExtension)
            {
                B(model.HasEnglishExtension ? (byte)1 : (byte)0);
                if (model.HasEnglishExtension)
                {
                    Text(model.EnglishName, 20); Text(model.EnglishComment, 256);
                    foreach (var bone in model.Bones) Text(bone.EnglishName, 20);
                    foreach (var morph in model.Morphs.Skip(1)) Text(morph.EnglishName, 20);
                    foreach (var frame in model.BoneDisplayFrames.Skip(1)) Text(frame.EnglishName, 50);
                }
            }
            if (!model.HasToonExtension) return;
            Require(model.ToonTextures.Length == 10, "toon extension requires ten paths.");
            foreach (string toon in model.ToonTextures) Text(toon, 100);
            if (!model.HasPhysicsExtension) return;
            U32((uint)model.RigidBodies.Length);
            foreach (var body in model.RigidBodies)
            {
                Require(body is not null, "null rigid body.");
                Text(body.Name, 20); U16(Bone(body.BoneIndex, true)); B(body.Group); U16(body.CollisionMask); B(body.Shape);
                V(body.Size); V(body.Position); V(body.Rotation); F(body.Mass); F(body.TranslationDamping); F(body.RotationDamping);
                F(body.Repulsion); F(body.Friction); B(body.Operation);
            }
            U32((uint)model.Joints.Length);
            foreach (var joint in model.Joints)
            {
                Require(joint is not null, "null joint."); Text(joint.Name, 20); U32(Body(joint.RigidBodyA)); U32(Body(joint.RigidBodyB));
                V(joint.Position); V(joint.Rotation); V(joint.TranslationLower); V(joint.TranslationUpper);
                V(joint.RotationLower); V(joint.RotationUpper); V(joint.TranslationSpring); V(joint.RotationSpring);
            }
            Reserve(model.UnknownTailData.Length); writer.Write(model.UnknownTailData);
        }

        public void Dispose() { writer.Dispose(); buffer.Dispose(); }
    }
}
