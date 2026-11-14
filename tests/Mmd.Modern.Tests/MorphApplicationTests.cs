using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class MorphApplicationTests
{
    [Fact]
    public void MorphOnlyMotionIsPlayableAndDeformsSurfaceAndOutline()
    {
        var model = Model([Position("smile", new(0, 2, 0))]);
        using var controller = Controller(model, new() { MorphKeyframes = [Key("smile", 0, .5f), Key("smile", 30, 1)] });
        var sample = controller.Sample(0);
        Assert.Equal(30u, controller.DurationFrames);
        Assert.Equal(new Vector3(1, 1, 0), sample.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(sample.Surface.Snapshot.Vertices[0].Position, sample.Outline.Vertices[0].Position);
        Assert.Same(sample.Surface, controller.Sample(0).Surface);
    }

    [Fact]
    public void MorphDurationExtendsBoneMotionAndFirstLateKeyUsesItsWeightWithoutASeed()
    {
        var model = Model([Position("smile", new(0, 2, 0))]);
        using var controller = Controller(model, new()
        {
            BoneKeyframes = [new() { BoneName = "root", Frame = 10 }],
            MorphKeyframes = [Key("smile", 20, .75f), Key("smile", 40, 1)]
        });
        Assert.Equal(40u, controller.DurationFrames);
        Assert.Equal(new Vector3(1, 1.5f, 0), controller.Sample(0).Surface.Snapshot.Vertices[0].Position);
    }

    [Fact]
    public void MorphToggleChangesTheCacheKeyAndOffRestoresExactVertices()
    {
        var model = Model([Position("smile", new(0, 2, 0))]);
        using var controller = Controller(model, new() { MorphKeyframes = [Key("smile", 0, 1)] });
        var off = controller.Sample(0, enableMorphs: false);
        var on = controller.Sample(0);
        Assert.NotEqual(off.Surface.Snapshot.Vertices.ToArray(), on.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(off.Surface.Snapshot.Vertices.ToArray(), controller.Sample(0, enableMorphs: false).Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(on.Surface.Snapshot.Vertices.ToArray(), controller.Sample(0).Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(1, controller.SupportedMorphCount); Assert.Equal(1, controller.MatchedMorphTrackCount); Assert.True(controller.SupportsMorphs);
    }

    [Fact]
    public void ContinuousMorphFramesDoNotReuseAnIntegerFrameAndIntegerSeekRemainsAbsolute()
    {
        var model = Model([Position("smile", new(0, 2, 0))]);
        using var controller = Controller(model, new() { MorphKeyframes = [Key("smile", 0, 0), Key("smile", 2, 1)] });
        Assert.Equal(new Vector3(1, .5f, 0), controller.Advance(.5, 0).Surface.Snapshot.Vertices[0].Position);
        var sample = controller.Advance(.75, 0);
        Assert.Equal(new Vector3(1, .75f, 0), sample.Surface.Snapshot.Vertices[0].Position);
        Assert.Same(sample.Surface, controller.Advance(.75, 0).Surface);
        Assert.Equal(Vector3.UnitX, controller.Sample(0).Surface.Snapshot.Vertices[0].Position);
    }

    [Fact]
    public void PhysicsEnableResetAndAdvanceReceiveBoneMorphsAndKeepVertexMorphsInTheCompletedPose()
    {
        PmxRigidBody body = new() { Name = "kinematic", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
            Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.1f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
            Mass = 0, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Static };
        var model = Model([Position("vertex", new(0, 2, 0)), Bone("bone", new(4, 0, 0), Quaternion.Identity)], [body]);
        using var controller = Controller(model, new() { MorphKeyframes = [Key("vertex", 0, 0), Key("vertex", 2, 1), Key("bone", 0, 0), Key("bone", 2, 1)] });
        var backend = new RecordingBackend();
        controller.EnablePhysics(backend, 1);
        Assert.Equal(new Vector3(2, 0, 0), backend.World.Translation);
        Assert.Equal(new Vector3(3, 1, 0), controller.Sample(1).Surface.Snapshot.Vertices[0].Position);
        var advanced = controller.Advance(1.5, 1d / 120);
        Assert.Equal(new Vector3(3, 0, 0), backend.World.Translation);
        Assert.Equal(new Vector3(4, 1.5f, 0), advanced.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(advanced.Surface.Snapshot.Vertices[0].Position, advanced.Outline.Vertices[0].Position);
        Assert.Equal(Vector3.UnitX, controller.ResetPhysics(1, enableMorphs: false).Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(Vector3.Zero, backend.World.Translation);
        Assert.Equal(new Vector3(3, 1, 0), controller.ResetPhysics(1).Surface.Snapshot.Vertices[0].Position);
    }

    [Fact]
    public void EmptyCompositeMorphsDoNotBindOrRejectSupportedTracks()
    {
        var morphs = new[] { PmxMorphType.Group, PmxMorphType.Flip, PmxMorphType.Impulse }
            .Select(t => new PmxMorph { Name = t.ToString(), EnglishName = "", ControlPanel = 1, Type = t }).ToList();
        morphs.Add(Position("supported", Vector3.UnitY));
        using var controller = Controller(Model(morphs), new() { MorphKeyframes = [Key("supported", 0, 1), Key("Group", 0, 1)] });
        Assert.Equal(new Vector3(1, 1, 0), controller.Sample(0).Surface.Snapshot.Vertices[0].Position);
        Assert.DoesNotContain(controller.Diagnostics, item => item.Contains("type 'Group'", StringComparison.Ordinal));
        // This fixture's Group has no descendants, so that particular track still cannot bind.
        Assert.Contains(controller.Diagnostics, item => item.Contains("track 'Group'", StringComparison.Ordinal));
        Assert.Throws<NotSupportedException>(() => Controller(Model(morphs), new() { MorphKeyframes = [Key("Group", 0, 1)] }));
    }

    internal static VmdMorphKeyframe Key(string name, uint frame, float weight) => new() { MorphName = name, Frame = frame, Weight = weight };
    internal static PmxMorph Position(string name, Vector3 delta) => new()
    {
        Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position,
        PositionOffsets = [new(0, delta)]
    };
    internal static PmxMorph Bone(string name, Vector3 delta, Quaternion rotation) => new()
    {
        Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Bone,
        BoneOffsets = [new(0, delta, rotation)]
    };
    internal static CharacterAnimationController Controller(PmxModel model, VmdMotion motion)
        => new(model, motion, PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model));

    internal static PmxModel Model(IReadOnlyList<PmxMorph> morphs, IReadOnlyList<PmxRigidBody>? bodies = null) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "fixture", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = [Vertex(Vector3.UnitX), Vertex(Vector3.UnitY), Vertex(Vector3.Zero)], Faces = [new(0, 1, 2)], Textures = [],
        Materials = [new() { Name = "surface", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero,
            SpecularPower = 1, Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.DrawEdge, EdgeColor = Vector4.One,
            EdgeSize = 1, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
            ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1,
            DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate }],
        Morphs = morphs, DisplayFrames = [], RigidBodies = bodies ?? [], Joints = [], SoftBodies = []
    };
    private static PmxVertex Vertex(Vector3 position) => new()
    {
        Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
        BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1
    };

    private sealed class RecordingBackend : ICharacterPhysicsBackend
    {
        public Matrix4x4 World;
        public string Name => "morph phase recording";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> worlds) => World = worlds[0];
        public void SetKinematicTarget(int index, Matrix4x4 world, float seconds) => World = world;
        public Matrix4x4 GetBodyWorldTransform(int index) => World;
        public void Step(float seconds) { }
        public void Dispose() { }
    }
}
