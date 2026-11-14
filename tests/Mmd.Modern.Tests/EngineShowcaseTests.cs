using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics.Bepu;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EngineShowcaseTests
{
    private static EngineShowcaseController Demo() => new(_ => new BepuCharacterPhysicsBackend(new(0, -9.81f, 0), groundY: null));

    [Fact]
    public void OriginalSceneHasOneFloorCompleteBindingsAndStaticRayTracingInput()
    {
        var scene = EngineShowcaseScene.Create();
        Assert.Single(scene.Parts, p => p.Name == "Floor");
        Assert.Equal(4, scene.Parts.Count(p => p.IsDynamic));
        Assert.False(scene.Frame.Surface.Snapshot.HasDynamicVertices);
        Assert.Empty(scene.Frame.Surface.Snapshot.Textures);
        Assert.Equal(FrameAntialiasing.Smaa1X, scene.Frame.Options!.Antialiasing);
        Assert.Null(scene.Frame.Options.GroundShadow);
        Assert.Equal(scene.Frame.Surface.Snapshot.Vertices.Length, scene.Frame.Outline!.Vertices.Count);
        Assert.Equal(scene.Parts.Count, scene.Frame.Surface.Snapshot.Materials.Count);
        Assert.Equal(scene.Parts.Count, scene.Frame.Materials.Count);
        var vertices = scene.Frame.Surface.Snapshot.Vertices.ToArray();
        for (int i = 0; i < vertices.Length; i++)
        {
            Assert.Equal(vertices[i].Position, scene.Frame.Outline.Vertices[i].Position);
            Assert.Equal(vertices[i].Normal, scene.Frame.Outline.Vertices[i].Normal);
        }
    }

    [Fact]
    public void ActualBepuGravityMovesBoxesAndSurfaceOutlineTogetherWhileRoomStaysFixed()
    {
        using var demo = Demo();
        var before = demo.Frame.Surface.Snapshot.Vertices.ToArray();
        demo.IsPlaying = true;
        for (int i = 0; i < 30; i++) demo.Advance(1.0 / 120);
        Assert.Contains("Bepu", demo.BackendName);
        Assert.True(demo.Frame.Surface.Snapshot.HasDynamicVertices);
        Assert.False(demo.IsAtResetPose);
        Assert.Equal(30, demo.TotalStepCount);
        var after = demo.Frame.Surface.Snapshot.Vertices.ToArray();
        foreach (var part in demo.Scene.Parts)
        {
            var first = after[part.StartVertex];
            if (part.IsDynamic) Assert.True(Vector3.Distance(before[part.StartVertex].Position, first.Position) > .01f, part.Name);
            else Assert.Equal(before[part.StartVertex], first);
        }
        for (int i = 0; i < after.Length; i++)
        {
            Assert.Equal(after[i].Position, demo.Frame.Outline!.Vertices[i].Position);
            Assert.Equal(after[i].Normal, demo.Frame.Outline.Vertices[i].Normal);
        }
    }

    [Fact]
    public void PausingReusesFrameAndResetReplayIsDeterministic()
    {
        using var demo = Demo();
        OriginalFrameScene reset = demo.Frame;
        demo.IsPlaying = true;
        for (int i = 0; i < 120; i++) demo.Advance(1.0 / 120);
        var expected = demo.BodyWorldTransforms.ToArray();
        demo.IsPlaying = false;
        var paused = demo.Frame;
        double time = demo.ElapsedSeconds;
        Assert.Same(paused, demo.Advance(1));
        Assert.Equal(time, demo.ElapsedSeconds);
        Assert.True(paused.Surface.Snapshot.HasDynamicVertices);
        demo.Reset();
        Assert.Same(reset, demo.Frame);
        Assert.False(demo.IsPlaying);
        Assert.True(demo.IsAtResetPose);
        Assert.False(demo.Frame.Surface.Snapshot.HasDynamicVertices);
        demo.IsPlaying = true;
        for (int i = 0; i < 120; i++) demo.Advance(1.0 / 120);
        Assert.Equal(expected, demo.BodyWorldTransforms);
    }

    [Fact]
    public void BoxesCollideWithTheOnlyFloorAndSuspendedSpringStaysAttached()
    {
        using var demo = Demo();
        demo.IsPlaying = true;
        for (int i = 0; i < 600; i++) demo.Advance(1.0 / 120);
        var vertices = demo.Frame.Surface.Snapshot.Vertices.ToArray();
        foreach (var part in demo.Scene.Parts.Where(p => p.IsDynamic))
            Assert.True(vertices.Skip(part.StartVertex).Take(part.VertexCount).Min(v => v.Position.Y) > -.04f, part.Name);
        var joint = Assert.Single(demo.PhysicsDefinition.Joints);
        Vector3 pivotA = Vector3.Transform(demo.PhysicsDefinition.JointLocalFramesA[0].Translation, demo.BodyWorldTransforms[joint.BodyA]);
        Vector3 pivotB = Vector3.Transform(demo.PhysicsDefinition.JointLocalFramesB[0].Translation, demo.BodyWorldTransforms[joint.BodyB]);
        Assert.True(Vector3.Distance(pivotA, pivotB) < .025f, $"Spring anchor separated by {Vector3.Distance(pivotA, pivotB)}");
        Assert.True(joint.AngularSpring.LengthSquared() > 0);
        Assert.Equal(4, demo.PhysicsDefinition.Bodies.Count(b => b.Mass > 0));
    }

    [Fact]
    public void FractionalStepAndInvalidTimeDoNotReplaceAValidFrame()
    {
        using var demo = Demo();
        var initial = demo.Frame;
        demo.IsPlaying = true;
        Assert.Same(initial, demo.Advance(1.0 / 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => demo.Advance(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => demo.Advance(-1));
        Assert.Same(initial, demo.Frame);
    }

    [Fact]
    public void DefaultCameraContainsEveryFeaturedDynamicObject()
    {
        var scene = EngineShowcaseScene.Create();
        var snapshot = scene.Frame.Surface.Snapshot;
        Matrix4x4 camera = snapshot.Camera.ViewProjection(1920, 1080);
        var vertices = snapshot.Vertices.ToArray();
        foreach (var part in scene.Parts.Where(p => p.IsDynamic))
            foreach (var vertex in vertices.Skip(part.StartVertex).Take(part.VertexCount))
            {
                Vector4 clip = Vector4.Transform(new Vector4(vertex.Position, 1), camera);
                Assert.True(clip.W > 0 && clip.Z / clip.W is >= 0 and <= 1, part.Name);
                Assert.InRange(clip.X / clip.W, -.95f, .95f);
                Assert.InRange(clip.Y / clip.W, -.95f, .95f);
            }
    }
}
