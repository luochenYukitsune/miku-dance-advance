using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

internal static class StageScene
{
    public static ValueTask<SceneAnimationSession> CreateSession(CancellationToken token)
    {
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "self-authored moving triangle", EnglishName = "", Comment = "ViewOutput stage fixture", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "amber triangle", EnglishName = "", Diffuse = new(.9f,.35f,.1f,1), Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = new(.1f), DrawMode = PmxDrawModeFlags.BothFaces, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.AllowTranslate }],
            Morphs = [], DisplayFrames = [], Joints = [], RigidBodies = [], SoftBodies = [],
            Vertices = new[] { new Vector3(-6,2,0), new Vector3(6,2,0), new Vector3(0,15,0) }.Select(position => new PmxVertex
            { Position = position, Normal = -Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0,-1,-1,-1], BoneWeights = [1,0,0,0], EdgeScale = 1 }).ToArray()
        };
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(new(0,0,0,1), Matrix4x4.Identity, new(0,0,-5), 30, 1, false), materials: PmxOriginalFrameAdapter.FromMaterials(model));
        var layout = new SceneCompositionEditor(); var actor = layout.Add("triangle", "self-authored.pmx", ModelRole.Character, rest);
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = new(8,0,0) }] };
        return ValueTask.FromResult(new SceneAnimationSession(layout.Objects, [new(actor.Id, model, motion, PhysicsEnabled: false)],
            _ => throw new InvalidOperationException("Stage triangle has no physics"), cancellationToken: token));
    }
}
