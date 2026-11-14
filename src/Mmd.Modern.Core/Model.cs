using System.Numerics;
using System.Text;

namespace Mmd.Modern.Core;

public readonly record struct ModelVertex(Vector3 Position, Vector3 Normal, Vector2 Uv, ushort Bone0, ushort Bone1, float Weight0)
{ public byte EdgeFlag { get; init; } }
public sealed record ModelMaterial(Vector4 Diffuse, Vector3 Specular, Vector3 Ambient, float Shininess, string Texture, int StartIndex, int IndexCount)
{ public byte ToonIndex { get; init; } = 255; public byte EdgeFlag { get; init; } }
public sealed record ModelBone(string Name, int Parent, Vector3 Position)
{
    public int TailIndex { get; init; } = -1;
    public byte Type { get; init; }
    public ushort IkParent { get; init; }
    public string EnglishName { get; init; } = "";
}
public sealed record PmdModel(string Name, ModelVertex[] Vertices, ushort[] Indices, ModelMaterial[] Materials, ModelBone[] Bones, long UnparsedTailBytes)
{
    public string Comment { get; init; } = "";
    public string EnglishName { get; init; } = "";
    public string EnglishComment { get; init; } = "";
    public bool HasAnimationTail { get; init; }
    public bool HasEnglishExtension { get; init; }
    public bool HasToonExtension { get; init; }
    public bool HasPhysicsExtension { get; init; }
    public PmdIkChain[] IkChains { get; init; } = [];
    public PmdMorph[] Morphs { get; init; } = [];
    public ushort[] MorphDisplay { get; init; } = [];
    /// <summary>Slot zero is the implicit center frame; file entries start at one.</summary>
    public PmdBoneDisplayFrame[] BoneDisplayFrames { get; init; } = [];
    public string[] ToonTextures { get; init; } = Enumerable.Range(1, 10).Select(i => $"toon{i:00}.bmp").ToArray();
    public PmdRigidBody[] RigidBodies { get; init; } = [];
    public PmdJoint[] Joints { get; init; } = [];
    public byte[] UnknownTailData { get; init; } = [];
}

public static class PmdReader
{
    public static PmdModel Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("PMD requires a readable seekable stream.", nameof(stream));
        if (stream.Length - stream.Position > 512L * 1024 * 1024) throw new InvalidDataException("PMD exceeds the 512 MiB input limit.");
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(3).AsSpan().SequenceEqual("Pmd"u8) || reader.ReadSingle() != 1.0f)
                throw new InvalidDataException("Expected PMD 1.0 header.");
            var name = ReadText(reader, 20);
            string comment = ReadText(reader, 256);
            int vertexCount = Count(reader, reader.ReadUInt32(), 38, 65535, "vertices");
            var vertices = new ModelVertex[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var position = ReadVector3(reader); var normal = ReadVector3(reader);
                var uv = new Vector2(Number(reader), Number(reader));
                ushort b0 = reader.ReadUInt16(), b1 = reader.ReadUInt16();
                byte weight = reader.ReadByte(), edge = reader.ReadByte();
                if (weight > 100 || normal.LengthSquared() < 1e-12f) throw new InvalidDataException("Invalid PMD weight or normal.");
                vertices[i] = new(position, Vector3.Normalize(normal), uv, b0, b1, weight / 100f) { EdgeFlag = edge };
            }
            int indexCount = Count(reader, reader.ReadUInt32(), 2, 20_000_000, "indices");
            if (indexCount % 3 != 0) throw new InvalidDataException("Triangle index count must be divisible by three.");
            var indices = new ushort[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                indices[i] = reader.ReadUInt16();
                if (indices[i] >= vertexCount) throw new InvalidDataException("Index outside vertex array.");
            }
            int materialCount = Count(reader, reader.ReadUInt32(), 70, 100_000, "materials");
            var materials = new ModelMaterial[materialCount];
            int start = 0;
            for (int i = 0; i < materialCount; i++)
            {
                var diffuse = new Vector4(ReadVector3(reader), Number(reader));
                float shine = Number(reader);
                var specular = ReadVector3(reader); var ambient = ReadVector3(reader);
                byte toon = reader.ReadByte(), edge = reader.ReadByte();
                if (toon != 255 && toon > 9) throw new InvalidDataException("Invalid PMD toon index.");
                uint count = reader.ReadUInt32();
                if (count % 3 != 0 || count > indexCount - start) throw new InvalidDataException("Material spans exceed indices.");
                materials[i] = new(diffuse, specular, ambient, shine, ReadText(reader, 20), start, (int)count) { ToonIndex = toon, EdgeFlag = edge };
                start += (int)count;
            }
            if (start != indexCount) throw new InvalidDataException("Material spans do not cover indices.");
            int boneCount = Count(reader, reader.ReadUInt16(), 39, 65535, "bones");
            var bones = new ModelBone[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                var boneName = ReadText(reader, 20); ushort rawParent = reader.ReadUInt16();
                int parent = rawParent == ushort.MaxValue ? -1 : rawParent;
                ushort tail = reader.ReadUInt16(); byte type = reader.ReadByte(); ushort ikParent = reader.ReadUInt16();
                if (parent < -1 || parent >= boneCount || parent == i) throw new InvalidDataException("Invalid bone parent.");
                if (type > 9) throw new InvalidDataException("Invalid PMD bone type.");
                bones[i] = new(boneName, parent, ReadVector3(reader)) { TailIndex = tail == ushort.MaxValue ? -1 : tail, Type = type, IkParent = ikParent };
            }
            foreach (var vertex in vertices)
                if (vertex.Bone0 >= boneCount || vertex.Bone1 >= boneCount) throw new InvalidDataException("Invalid vertex bone.");
            var model = new PmdModel(name, vertices, indices, materials, bones, 0) { Comment = comment };
            model = PmdTailReader.Read(reader, model);
            Skinning.CreateSkinMatrices(model);
            return model;
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated PMD.", error); }
    }

    private static int Count(BinaryReader reader, uint count, int stride, int maximum, string label)
    {
        if (count > maximum || (long)count * stride > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException($"Invalid {label} count {count}.");
        return (int)count;
    }
    private static float Number(BinaryReader reader)
    {
        float value = reader.ReadSingle();
        return float.IsFinite(value) ? value : throw new InvalidDataException("Non-finite PMD number.");
    }
    private static Vector3 ReadVector3(BinaryReader reader) => new(Number(reader), Number(reader), Number(reader));
    private static string ReadText(BinaryReader reader, int count)
    {
        var data = reader.ReadBytes(count);
        if (data.Length != count) throw new EndOfStreamException();
        int end = Array.IndexOf(data, (byte)0);
        // PMD uses CP932. The provider ships in the .NET 10 shared runtime.
        return CodePagesEncodingProvider.Instance.GetEncoding(932)!.GetString(data, 0, end < 0 ? count : end);
    }
}

public static class Skinning
{
    public static Matrix4x4[] CreateSkinMatrices(PmdModel model, IReadOnlyList<Quaternion>? rotations = null)
    {
        if (rotations is not null && rotations.Count != model.Bones.Length) throw new ArgumentException("One rotation per bone required.", nameof(rotations));
        var world = new Matrix4x4[model.Bones.Length];
        var state = new byte[model.Bones.Length];
        // Iterative traversal avoids a stack overflow for an untrusted deep hierarchy.
        for (int i = 0; i < model.Bones.Length; i++)
        {
            var chain = new List<int>(); int current = i;
            while (current != -1 && state[current] != 2)
            {
                if (state[current] == 1) throw new InvalidDataException("Bone hierarchy cycle.");
                state[current] = 1; chain.Add(current); current = model.Bones[current].Parent;
                if (current < -1 || current >= model.Bones.Length) throw new InvalidDataException("Bone parent outside array.");
            }
            for (int j = chain.Count - 1; j >= 0; j--)
            {
                int index = chain[j]; var bone = model.Bones[index];
                Quaternion rotation = rotations?[index] ?? Quaternion.Identity;
                if (!float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 1e-12f) throw new ArgumentException("Invalid bone rotation.", nameof(rotations));
                var offset = bone.Parent < 0 ? bone.Position : bone.Position - model.Bones[bone.Parent].Position;
                var local = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(offset);
                world[index] = bone.Parent < 0 ? local : local * world[bone.Parent];
                state[index] = 2;
            }
        }
        return model.Bones.Select((bone, i) => Matrix4x4.CreateTranslation(-bone.Position) * world[i]).ToArray();
    }

    public static ModelVertex[] Apply(PmdModel model, IReadOnlyList<Matrix4x4> matrices)
    {
        if (matrices.Count != model.Bones.Length) throw new ArgumentException("One matrix per bone required.", nameof(matrices));
        return model.Vertices.Select(vertex =>
        {
            var a = matrices[vertex.Bone0]; var b = matrices[vertex.Bone1]; float weight = vertex.Weight0;
            var position = Vector3.Transform(vertex.Position, a) * weight + Vector3.Transform(vertex.Position, b) * (1 - weight);
            var normal = Vector3.TransformNormal(vertex.Normal, a) * weight + Vector3.TransformNormal(vertex.Normal, b) * (1 - weight);
            return vertex with { Position = position, Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : vertex.Normal };
        }).ToArray();
    }
}
