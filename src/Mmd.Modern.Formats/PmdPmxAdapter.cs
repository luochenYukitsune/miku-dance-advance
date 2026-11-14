// PMD semantics reference: saba PMDFile.h/cpp, PMDModel.cpp and MMDPhysics.cpp,
// @29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT, (c) 2016-2017 benikabocha.
// Legacy rotation inheritance mapping also follows nanoem.c (MIT component),
// @30acffaa29f5d2eb9e997d69418f2e4b97b5894f, (c) 2015-2023 hkrn.
using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Formats;

/// <summary>Converts retained PMD semantics into the existing PMX domain path.
/// Coordinates stay in the MMD convention; the OpenGL-specific Saba Z flip is not copied.</summary>
public static class PmdPmxAdapter
{
    private static readonly string[] CommonToons = Enumerable.Range(1, 10).Select(i => $"toon{i:00}.bmp").ToArray();
    public static PmxModel Convert(PmdModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.UnparsedTailBytes != 0 || source.UnknownTailData.Length != 0)
            throw new NotSupportedException("PMD has an unknown trailer; conversion would discard data.");
        var textures = new List<string>();
        int Texture(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            int existing = textures.FindIndex(x => StringComparer.Ordinal.Equals(x, name));
            if (existing >= 0) return existing;
            textures.Add(name); return textures.Count - 1;
        }
        var materials = source.Materials.Select((m, index) =>
        {
            int diffuse = -1, sphere = -1; PmxSphereMode sphereMode = PmxSphereMode.None;
            foreach (string path in m.Texture.Split('*', StringSplitOptions.RemoveEmptyEntries))
            {
                string extension = Path.GetExtension(path);
                if (extension.Equals(".sph", StringComparison.OrdinalIgnoreCase) || extension.Equals(".spa", StringComparison.OrdinalIgnoreCase))
                {
                    if (sphere >= 0) throw new InvalidDataException("PMD material has multiple sphere textures.");
                    sphere = Texture(path); sphereMode = extension.Equals(".spa", StringComparison.OrdinalIgnoreCase) ? PmxSphereMode.Add : PmxSphereMode.Multiply;
                }
                else
                {
                    if (diffuse >= 0) throw new InvalidDataException("PMD material has multiple diffuse textures.");
                    diffuse = Texture(path);
                }
            }
            PmxToonMode toonMode = PmxToonMode.Separate; int toon = -1;
            if (m.ToonIndex != 255)
            {
                if (m.ToonIndex >= source.ToonTextures.Length) throw new InvalidDataException("Invalid PMD toon index.");
                string name = source.ToonTextures[m.ToonIndex];
                int common = Array.FindIndex(CommonToons,
                    candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (common >= 0)
                { toonMode = PmxToonMode.Common; toon = common; }
                else toon = Texture(name);
            }
            var flags = PmxDrawModeFlags.GroundShadow | PmxDrawModeFlags.CastSelfShadow | PmxDrawModeFlags.ReceiveSelfShadow;
            if (m.EdgeFlag != 0) flags |= PmxDrawModeFlags.DrawEdge;
            if (m.Diffuse.W < 1) flags |= PmxDrawModeFlags.BothFaces;
            return new PmxMaterial
            {
                Name = $"PMD material {index}", EnglishName = "", Diffuse = m.Diffuse, Specular = m.Specular,
                SpecularPower = m.Shininess, Ambient = m.Ambient, DrawMode = flags, EdgeColor = new(0, 0, 0, 1),
                EdgeSize = 1, TextureIndex = diffuse, SphereTextureIndex = sphere, SphereMode = sphereMode,
                ToonMode = toonMode, ToonTextureIndex = toon, Memo = "", FaceVertexCount = m.IndexCount
            };
        }).ToArray();
        var ikByBone = new Dictionary<int, PmdIkChain>();
        foreach (var ik in source.IkChains)
        {
            if (ik.BoneIndex < 0 || ik.BoneIndex >= source.Bones.Length || !ikByBone.TryAdd(ik.BoneIndex, ik))
                throw new InvalidDataException("Invalid or duplicate PMD IK controller.");
        }
        var appendSources = new int[source.Bones.Length]; Array.Fill(appendSources, -1);
        for (int i = 0; i < source.Bones.Length; i++)
        {
            var b = source.Bones[i];
            int parent = b.Type switch { 5 => b.IkParent, 9 => b.TailIndex, _ => -1 };
            if (b.Type is 5 or 9)
            {
                if ((uint)parent >= (uint)source.Bones.Length || parent == i)
                    throw new InvalidDataException("Invalid PMD rotation inheritance source.");
                appendSources[i] = parent;
            }
        }
        // Preserve IK-list order, and make inherited local rotations available before
        // their recipients. Exact original PMD IK/append interleaving still needs a
        // runtime golden comparison; this is a conversion into the shared PMX path.
        var layers = new int[source.Bones.Length];
        for (int i = 0; i < source.IkChains.Length; i++) layers[source.IkChains[i].BoneIndex] = source.Bones.Length + i;
        var recipients = Enumerable.Range(0, source.Bones.Length).Select(_ => new List<int>()).ToArray();
        var incoming = new int[source.Bones.Length];
        for (int i = 0; i < appendSources.Length; i++)
            if (appendSources[i] >= 0) { recipients[appendSources[i]].Add(i); incoming[i]++; }
        var ready = new Queue<int>(Enumerable.Range(0, incoming.Length).Where(i => incoming[i] == 0));
        int visited = 0;
        while (ready.TryDequeue(out int index))
        {
            visited++;
            foreach (int recipient in recipients[index])
            {
                layers[recipient] = Math.Max(layers[recipient], layers[index] + 1);
                if (--incoming[recipient] == 0) ready.Enqueue(recipient);
            }
        }
        if (visited != source.Bones.Length) throw new InvalidDataException("PMD rotation inheritance cycle.");
        var bones = source.Bones.Select((b, i) =>
        {
            PmxBoneFlags flags = PmxBoneFlags.AllowRotate;
            if (b.Type is 1 or 2) flags |= PmxBoneFlags.AllowTranslate;
            if (b.Type is not (3 or 6 or 7 or 9)) flags |= PmxBoneFlags.Visible | PmxBoneFlags.AllowControl;
            bool hasTail = b.Type != 9 && b.TailIndex >= 0 && b.TailIndex < source.Bones.Length;
            if (hasTail) flags |= PmxBoneFlags.TargetShowMode;
            if (appendSources[i] >= 0) flags |= PmxBoneFlags.AppendRotate;
            Vector3 axis = default;
            if (b.Type == 8)
            {
                if (!hasTail || (axis = source.Bones[b.TailIndex].Position - b.Position).LengthSquared() < 1e-12f)
                    throw new InvalidDataException("PMD twist bone requires a nonzero tail axis.");
                axis = Vector3.Normalize(axis); flags |= PmxBoneFlags.FixedAxis;
            }
            ikByBone.TryGetValue(i, out var ik);
            if (ik is not null) flags |= PmxBoneFlags.Ik;
            PmxIkLink Link(int index)
            {
                if ((uint)index >= (uint)source.Bones.Length) throw new InvalidDataException("Invalid PMD IK link.");
                bool knee = source.Bones[index].Name.Contains("ひざ", StringComparison.Ordinal);
                // Saba's right-handed knee range [.5deg,180deg] changes sign
                // when expressed in the unflipped MMD coordinate convention.
                return new(index, knee, knee ? new(-MathF.PI, 0, 0) : default,
                    knee ? new(-MathF.PI / 360, 0, 0) : default);
            }
            return new PmxBone
            {
                Name = b.Name, EnglishName = b.EnglishName, Position = b.Position, ParentIndex = b.Parent,
                DeformDepth = layers[i], Flags = flags, LinkBoneIndex = hasTail ? b.TailIndex : -1, FixedAxis = axis,
                AppendBoneIndex = appendSources[i], AppendWeight = b.Type switch { 5 => 1, 9 => b.IkParent * .01f, _ => 0 },
                IkTargetBoneIndex = ik?.TargetBoneIndex ?? -1, IkIterationCount = ik?.Iterations ?? 0,
                IkLimit = (ik?.AngleLimit ?? 0) * 4, IkLinks = ik?.Links.Select(Link).ToArray() ?? []
            };
        }).ToArray();
        PmdMorph? baseMorph = source.Morphs.Length == 0 ? null : source.Morphs[0];
        if (baseMorph is not null && baseMorph.Type != 0) throw new InvalidDataException("Missing PMD base morph.");
        var positions = source.Vertices.Select(vertex => vertex.Position).ToArray();
        if (baseMorph is not null)
            foreach (var vertex in baseMorph.Vertices)
            {
                if (vertex.Index >= positions.Length || !float.IsFinite(vertex.Position.X)
                    || !float.IsFinite(vertex.Position.Y) || !float.IsFinite(vertex.Position.Z))
                    throw new InvalidDataException("Invalid PMD base morph vertex.");
                // PMD base offsets are absolute positions, applied before additive expression offsets.
                positions[vertex.Index] = vertex.Position;
            }
        var morphs = source.Morphs.Skip(1).Select(m => new PmxMorph
        {
            Name = m.Name, EnglishName = m.EnglishName, Type = PmxMorphType.Position, ControlPanel = m.Type,
            PositionOffsets = m.Vertices.Select(v =>
            {
                if (baseMorph is null || v.Index >= baseMorph.Vertices.Length) throw new InvalidDataException("Invalid PMD morph base index.");
                uint index = baseMorph.Vertices[v.Index].Index;
                if (index >= source.Vertices.Length) throw new InvalidDataException("Invalid PMD base vertex.");
                return new PmxMorph.PositionOffset((int)index, v.Position);
            }).ToArray()
        }).ToArray();
        var frames = new List<PmxDisplayFrame>();
        frames.Add(new()
        {
            Name = "表情", EnglishName = "Expression", Type = PmxDisplayFrameType.Special,
            Targets = source.MorphDisplay.Select(index => index == 0 || index > morphs.Length
                ? throw new InvalidDataException("Invalid PMD morph display.")
                : new PmxDisplayTarget(PmxDisplayTargetType.Morph, index - 1)).ToArray()
        });
        frames.AddRange(source.BoneDisplayFrames.Select((f, i) => new PmxDisplayFrame
        {
            Name = f.Name, EnglishName = f.EnglishName, Type = i == 0 ? PmxDisplayFrameType.Special : PmxDisplayFrameType.Default,
            Targets = f.Bones.Select(index => new PmxDisplayTarget(PmxDisplayTargetType.Bone, index)).ToArray()
        }));
        var bodies = source.RigidBodies.Select(b =>
        {
            int bone = b.BoneIndex < 0 ? 0 : b.BoneIndex;
            if ((uint)bone >= (uint)source.Bones.Length) throw new InvalidDataException("PMD rigid body has no valid reference bone.");
            return new PmxRigidBody
            {
                Name = b.Name, EnglishName = "", BoneIndex = b.BoneIndex, Group = b.Group, CollisionGroup = b.CollisionMask,
                Shape = (PmxRigidBodyShape)b.Shape, ShapeSize = b.Size, Translation = b.Position + source.Bones[bone].Position,
                Rotation = b.Rotation, Mass = b.Mass, TranslationDamping = b.TranslationDamping,
                RotationDamping = b.RotationDamping, Repulsion = b.Repulsion, Friction = b.Friction,
                Operation = (PmxRigidBodyOperation)b.Operation
            };
        }).ToArray();
        var joints = source.Joints.Select(j => new PmxJoint
        {
            Name = j.Name, EnglishName = "", Type = PmxJointType.SpringDof6, RigidBodyAIndex = j.RigidBodyA,
            RigidBodyBIndex = j.RigidBodyB, Translation = j.Position, Rotation = j.Rotation,
            TranslationLowerLimit = j.TranslationLower, TranslationUpperLimit = j.TranslationUpper,
            RotationLowerLimit = j.RotationLower, RotationUpperLimit = j.RotationUpper,
            SpringTranslationFactor = j.TranslationSpring, SpringRotationFactor = j.RotationSpring
        }).ToArray();
        if (source.Indices.Length % 3 != 0) throw new InvalidDataException("Invalid PMD triangle count.");
        return new PmxModel
        {
            Header = new() { Version = 2f, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 4, TextureIndexSize = 4, MaterialIndexSize = 4, BoneIndexSize = 4, MorphIndexSize = 4, RigidBodyIndexSize = 4 },
            Name = source.Name, EnglishName = source.EnglishName, Comment = source.Comment, EnglishComment = source.EnglishComment,
            Vertices = source.Vertices.Select((v, index) => new PmxVertex
            {
                Position = positions[index], Normal = v.Normal, Uv = v.Uv, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef2,
                BoneIndices = [v.Bone0, v.Bone1, -1, -1], BoneWeights = [v.Weight0, 1 - v.Weight0, 0, 0], EdgeScale = v.EdgeFlag == 0 ? 1 : 0
            }).ToArray(),
            Faces = Enumerable.Range(0, source.Indices.Length / 3).Select(i => new PmxTriangle(source.Indices[i * 3], source.Indices[i * 3 + 1], source.Indices[i * 3 + 2])).ToArray(),
            Textures = textures.AsReadOnly(), Materials = materials, Bones = bones, Morphs = morphs,
            DisplayFrames = frames.AsReadOnly(), RigidBodies = bodies, Joints = joints, SoftBodies = []
        };
    }
}
