// PMD layout reference: saba @29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332,
// PMDFile.h/cpp, Copyright (c) 2016-2017 benikabocha, MIT License.
using System.Numerics;
using System.Text;

namespace Mmd.Modern.Core;

public sealed record PmdIkChain(int BoneIndex, int TargetBoneIndex, ushort Iterations, float AngleLimit, int[] Links);
public readonly record struct PmdMorphVertex(uint Index, Vector3 Position);
public sealed record PmdMorph(string Name, byte Type, PmdMorphVertex[] Vertices)
{ public string EnglishName { get; init; } = ""; }
public sealed record PmdBoneDisplayFrame(string Name, ushort[] Bones)
{ public string EnglishName { get; init; } = ""; }
public sealed record PmdRigidBody(string Name, int BoneIndex, byte Group, ushort CollisionMask, byte Shape,
    Vector3 Size, Vector3 Position, Vector3 Rotation, float Mass, float TranslationDamping,
    float RotationDamping, float Repulsion, float Friction, byte Operation);
public sealed record PmdJoint(string Name, int RigidBodyA, int RigidBodyB, Vector3 Position, Vector3 Rotation,
    Vector3 TranslationLower, Vector3 TranslationUpper, Vector3 RotationLower, Vector3 RotationUpper,
    Vector3 TranslationSpring, Vector3 RotationSpring);

internal static class PmdTailReader
{
    public static PmdModel Read(BinaryReader r, PmdModel model)
    {
        // Prefix-only files produced by the original project fixture remain readable,
        // but advertise that no animation tail was present.
        if (Remaining(r) == 0) return model;
        int Bone(ushort raw, bool none = false)
        {
            if (none && raw == ushort.MaxValue) return -1;
            if (raw >= model.Bones.Length) throw new InvalidDataException("PMD tail bone index outside array.");
            return raw;
        }
        int ikCount = Count(r, r.ReadUInt16(), 11, 65535);
        var iks = new PmdIkChain[ikCount];
        var controllers = new HashSet<int>();
        for (int i = 0; i < iks.Length; i++)
        {
            int bone = Bone(r.ReadUInt16()), target = Bone(r.ReadUInt16()); byte links = r.ReadByte();
            ushort iterations = r.ReadUInt16(); float angle = Number(r);
            if (angle < 0 || !controllers.Add(bone)) throw new InvalidDataException("Invalid or duplicate PMD IK controller.");
            Count(r, links, 2, 255); var chain = new int[links];
            for (int j = 0; j < links; j++) chain[j] = Bone(r.ReadUInt16());
            iks[i] = new(bone, target, iterations, angle, chain);
        }
        int morphCount = Count(r, r.ReadUInt16(), 25, 65535);
        var morphs = new PmdMorph[morphCount];
        for (int i = 0; i < morphCount; i++)
        {
            string name = Text(r, 20); uint count = r.ReadUInt32(); byte type = r.ReadByte();
            if (type > 4 || (i == 0 ? type != 0 : type == 0)) throw new InvalidDataException("PMD base morph must be first and unique.");
            var vertices = new PmdMorphVertex[Count(r, count, 16, 20_000_000)];
            for (int j = 0; j < vertices.Length; j++)
            {
                uint index = r.ReadUInt32();
                if (index >= (i == 0 ? model.Vertices.Length : morphs[0].Vertices.Length)) throw new InvalidDataException("PMD morph vertex index outside base.");
                vertices[j] = new(index, Vector(r));
            }
            morphs[i] = new(name, type, vertices);
        }
        var morphDisplay = new ushort[Count(r, r.ReadByte(), 2, 255)];
        for (int i = 0; i < morphDisplay.Length; i++)
        {
            morphDisplay[i] = r.ReadUInt16();
            if (morphDisplay[i] == 0 || morphDisplay[i] >= morphCount) throw new InvalidDataException("Invalid PMD morph display index.");
        }
        int frameCount = Count(r, r.ReadByte(), 50, 255);
        var frames = new PmdBoneDisplayFrame[frameCount + 1];
        var names = new string[frameCount + 1]; names[0] = "センター";
        for (int i = 1; i < names.Length; i++) names[i] = Text(r, 50);
        var frameBones = Enumerable.Range(0, names.Length).Select(_ => new List<ushort>()).ToArray();
        int displayCount = Count(r, r.ReadUInt32(), 3, 20_000_000);
        for (int i = 0; i < displayCount; i++)
        {
            ushort bone = (ushort)Bone(r.ReadUInt16()); byte frame = r.ReadByte();
            if (frame >= names.Length) throw new InvalidDataException("Invalid PMD bone display frame.");
            frameBones[frame].Add(bone);
        }
        for (int i = 0; i < frames.Length; i++) frames[i] = new(names[i], frameBones[i].ToArray());
        model = model with { HasAnimationTail = true, IkChains = iks, Morphs = morphs, MorphDisplay = morphDisplay, BoneDisplayFrames = frames };
        if (Remaining(r) == 0) return model;
        byte english = r.ReadByte();
        if (english > 1) throw new InvalidDataException("Invalid PMD English extension flag.");
        if (english == 1)
        {
            string name = Text(r, 20), comment = Text(r, 256);
            var bones = (ModelBone[])model.Bones.Clone();
            for (int i = 0; i < bones.Length; i++) bones[i] = bones[i] with { EnglishName = Text(r, 20) };
            for (int i = 1; i < morphs.Length; i++) morphs[i] = morphs[i] with { EnglishName = Text(r, 20) };
            for (int i = 1; i < frames.Length; i++) frames[i] = frames[i] with { EnglishName = Text(r, 50) };
            model = model with { HasEnglishExtension = true, EnglishName = name, EnglishComment = comment, Bones = bones };
        }
        if (Remaining(r) == 0) return model;
        var toons = new string[10];
        for (int i = 0; i < toons.Length; i++) toons[i] = Text(r, 100);
        model = model with { HasToonExtension = true, ToonTextures = toons };
        if (Remaining(r) == 0) return model;
        var bodies = new PmdRigidBody[Count(r, r.ReadUInt32(), 83, 100_000)];
        for (int i = 0; i < bodies.Length; i++)
        {
            string name = Text(r, 20); int bone = Bone(r.ReadUInt16(), true); byte group = r.ReadByte(); ushort mask = r.ReadUInt16(); byte shape = r.ReadByte();
            Vector3 size = Vector(r), pos = Vector(r), rot = Vector(r);
            float mass = Number(r), linear = Number(r), angular = Number(r), repulsion = Number(r), friction = Number(r); byte mode = r.ReadByte();
            if (group > 15 || shape > 2 || mode > 2 || size.X < 0 || size.Y < 0 || size.Z < 0 || mass < 0 || linear < 0 || angular < 0 || repulsion < 0 || friction < 0)
                throw new InvalidDataException("Invalid PMD rigid body.");
            bodies[i] = new(name, bone, group, mask, shape, size, pos, rot, mass, linear, angular, repulsion, friction, mode);
        }
        model = model with { HasPhysicsExtension = true, RigidBodies = bodies };
        if (Remaining(r) == 0) return model;
        var joints = new PmdJoint[Count(r, r.ReadUInt32(), 124, 100_000)];
        int Body(uint raw)
        {
            if (raw == uint.MaxValue) return -1;
            if (raw >= bodies.Length) throw new InvalidDataException("Invalid PMD joint body index.");
            return (int)raw;
        }
        for (int i = 0; i < joints.Length; i++)
            joints[i] = new(Text(r, 20), Body(r.ReadUInt32()), Body(r.ReadUInt32()), Vector(r), Vector(r), Vector(r), Vector(r), Vector(r), Vector(r), Vector(r), Vector(r));
        byte[] unknown = r.ReadBytes(Count(r, (uint)Remaining(r), 1, 64 * 1024 * 1024));
        return model with { Joints = joints, UnknownTailData = unknown, UnparsedTailBytes = unknown.Length };
    }

    private static long Remaining(BinaryReader r) => r.BaseStream.Length - r.BaseStream.Position;
    private static int Count(BinaryReader r, uint count, int stride, int maximum)
    {
        if (count > maximum || (long)count * stride > Remaining(r)) throw new InvalidDataException("Invalid PMD tail count or truncated section.");
        return (int)count;
    }
    private static float Number(BinaryReader r)
    { float value = r.ReadSingle(); return float.IsFinite(value) ? value : throw new InvalidDataException("Non-finite PMD tail value."); }
    private static Vector3 Vector(BinaryReader r) => new(Number(r), Number(r), Number(r));
    private static string Text(BinaryReader r, int width)
    {
        byte[] bytes = r.ReadBytes(width); if (bytes.Length != width) throw new EndOfStreamException();
        int end = Array.IndexOf(bytes, (byte)0);
        try { return CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!.GetString(bytes, 0, end < 0 ? width : end); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid PMD CP932 text.", error); }
    }
}
