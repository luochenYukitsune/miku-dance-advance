using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics.Bepu;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SceneAnimationBepuTests
{
    [Fact]
    public void TwoActualBepuWorldsFreezeAtDifferentMotionEndsAndReplayAfterSeek()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx");
        byte[] bytes = File.ReadAllBytes(path); using var input = new MemoryStream(bytes); var source = PmxReader.Read(input);
        var model = new PmxModel { Header = source.Header, Name = "original physics fixture", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = source.Vertices.Select(v => new PmxVertex { Position = v.Position, Normal = v.Normal, Uv = v.Uv, AdditionalUvs = v.AdditionalUvs,
                WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = v.EdgeScale }).ToArray(),
            Faces = source.Faces, Textures = source.Textures, Materials = source.Materials, Bones = source.Bones,
            Morphs = [], DisplayFrames = [], SoftBodies = [], Joints = [], RigidBodies = [new() { Name = "sphere", EnglishName = "", BoneIndex = 0,
                Group = 0, CollisionGroup = 0, Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = source.Bones[0].Position,
                Rotation = Vector3.Zero, Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }] };
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
        var layout = new SceneCompositionEditor(); var a = layout.Add("short", path, ModelRole.Character, rest);
        var b = layout.Add("long", path, ModelRole.Character, rest); layout.SetTransform(b.Id, new(20, 0, 0), Quaternion.Identity, 1);
        VmdMotion Motion(uint end) => new() { BoneKeyframes = [new() { BoneName = model.Bones[0].Name, Frame = 0 }, new() { BoneName = model.Bones[0].Name, Frame = end }] };
        int allocations = 0;
        using var session = new SceneAnimationSession(layout.Objects, [new(a.Id, model, Motion(6)), new(b.Id, model, Motion(30))],
            _ => { allocations++; return new BepuCharacterPhysicsBackend(new(0, -9.8f, 0), null); }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, allocations); Assert.All(session.Actors, actor => Assert.Contains("Bepu", actor.PhysicsBackend));
        for (int i = 0; i < 6; i++) session.Advance(1.0 / 30, TestContext.Current.CancellationToken);
        var stopped = session.Frame.Surface.Snapshot.Vertices[0]; int count = rest.Surface.Snapshot.Vertices.Length;
        for (int i = 0; i < 9; i++) session.Advance(1.0 / 30, TestContext.Current.CancellationToken);
        Assert.Equal(stopped, session.Frame.Surface.Snapshot.Vertices[0]);
        Assert.True(session.Frame.Surface.Snapshot.Vertices[count].Position.Y < stopped.Position.Y - .1f);
        var target = session.Frame; Assert.Same(target, session.Advance(0, TestContext.Current.CancellationToken));
        session.Seek(0, TestContext.Current.CancellationToken);
        for (int i = 0; i < 15; i++) session.Advance(1.0 / 30, TestContext.Current.CancellationToken);
        Assert.Equal(target.Surface.Snapshot.Vertices, session.Frame.Surface.Snapshot.Vertices);
        var other = session.Frame.Surface.Snapshot.Vertices[count];
        session.SetActorOptions(a.Id, true, true, false, TestContext.Current.CancellationToken);
        Assert.False(session.Actors[0].PhysicsEnabled); Assert.Equal(other, session.Frame.Surface.Snapshot.Vertices[count]);
        Assert.Equal(rest.Surface.Snapshot.Vertices[0], session.Frame.Surface.Snapshot.Vertices[0]);
        Assert.True(session.Frame.Surface.Snapshot.HasDynamicVertices); Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
