using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class OfflineFrameFailureContextTests
{
    [Fact]
    public async Task AFailedPhysicsStepIsAttemptedAndDoesNotBecomeACompletedTickOrFrame()
    {
        var backend = new SceneAnimationSessionTests.FallingBackend();
        var basic = EditedMotionProjectTests.Model();
        var model = new PmxModel
        {
            Header = basic.Header, Name = basic.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = basic.Vertices, Faces = basic.Faces, Materials = basic.Materials, Bones = basic.Bones,
            Textures = [], Morphs = [], DisplayFrames = [], Joints = [], SoftBodies = [], RigidBodies = [new()
            {
                Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
                Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic
            }]
        };
        var scene = new SceneCompositionEditor();
        var actor = scene.Add("actor", "actor.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(model));
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 1 }] };
        using var session = new SceneAnimationSession(scene.Objects, [new(actor.Id, model, motion)], _ => backend,
            cancellationToken: TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<SceneActorAnimationException>(() => OfflineFrameRunner.RunAsync(session,
            new(4, 3, new(60), 0, 1, includeEndFrame: false), (_, _) =>
            { backend.FailStep = true; return ValueTask.CompletedTask; }, cancellationToken: TestContext.Current.CancellationToken));
        var cause = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal("fixture step failed", cause.Message);
        var context = Assert.IsType<OfflineFrameFailureContext>(error.Data[OfflineFrameFailureContext.DataKey]);
        Assert.Equal("simulation", context.Phase); Assert.Equal(1, context.CompletedFrames);
        Assert.Equal(1, context.Sample!.Index); Assert.Equal(new OfflineFrameTime(1, 2), context.Sample.RequestedSourceFrame);
        Assert.Equal(0, context.SimulationTicks); Assert.Equal(1, context.AttemptingSimulationTick);
        Assert.Equal(0, backend.Steps); Assert.Equal(0, backend.DisposeCount);
    }
}
