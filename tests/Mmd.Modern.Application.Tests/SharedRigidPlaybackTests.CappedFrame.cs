using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed partial class SharedRigidPlaybackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CappedPreviewReflectsTheDisplayedAnimationClockWithoutAddingPhysicalSteps(bool shared)
    {
        var model = Model(); var rest = SharedPhysicsFixture.Rest(model); var layout = new SceneCompositionEditor();
        var actors = Enumerable.Range(0, 2).Select(i => layout.Add("actor" + i, "capped-frame-" + i + ".pmx", ModelRole.Character, rest)).ToArray();
        var motions = actors.Select((actor, i) => new SceneActorMotion(actor.Id, model, Motion(16), PhysicsEnabled: i == 1)).ToArray();
        var controls = Controls(actors, model);
        var expectedFactory = new Factory(); var actualFactory = new Factory();
        using var expected = Create(expectedFactory); using var actual = Create(actualFactory);
        foreach (var backend in expectedFactory.Worlds.Concat(actualFactory.Worlds)) backend.Clear();

        // Ten physical ticks provide the independent held-solver oracle. The requested preview
        // reaches frame 7.5 but retains this same bounded history ending at physical frame 2.5.
        for (int i = 0; i < CharacterPhysicsSession.MaximumSubsteps; i++) expected.Advance(CharacterPhysicsSession.FixedStepSeconds, Token);
        actual.AdvancePreview(.25, Token);
        Assert.All(actualFactory.Worlds, backend => Assert.Equal(CharacterPhysicsSession.MaximumSubsteps, backend.Boundaries.Count));
        var snapshot = actual.CaptureRenderSnapshot([new(actors[0].Id, [0])]);
        Assert.Equal(7.5, snapshot.SourceFrame);
        using var animationOracle = new CharacterAnimationController(model, motions[0].Motion, rest.Surface, rest.Outline!);
        animationOracle.SampleAt(snapshot.SourceFrame);
        Assert.Equal(animationOracle.BoneWorldMatrices[0], snapshot.SceneInputs[actors[0].Id].BoneWorldMatrices[0]);
        Assert.Equal(expectedFactory.Worlds.Count, actualFactory.Worlds.Count);
        for (int i = 0; i < actualFactory.Worlds.Count; i++)
        {
            Assert.Equal(CharacterPhysicsSession.MaximumSubsteps, actualFactory.Worlds[i].Boundaries.Count);
            Assert.Equal(expectedFactory.Worlds[i].Boundaries, actualFactory.Worlds[i].Boundaries);
            AssertBitsEqual(expectedFactory.Worlds[i].Inner.Bodies.Select(body => body.Transform),
                actualFactory.Worlds[i].Inner.Bodies.Select(body => body.Transform));
            AssertBitsEqual(expectedFactory.Worlds[i].Inner.Bodies.Select(body => body.Velocity),
                actualFactory.Worlds[i].Inner.Bodies.Select(body => body.Velocity));
        }
        Assert.False(actual.IsFaulted);
        _ = actual.CaptureRenderSnapshot([new(actors[0].Id, [0])]);
        Assert.All(actualFactory.Worlds, backend => Assert.Equal(CharacterPhysicsSession.MaximumSubsteps, backend.Boundaries.Count));
        SceneProjectSession Create(Factory factory) => new(layout, new(), motions, factory.Provider(),
            new() { Physics = new() { SharedWorld = shared } }, pmmControls: controls, cancellationToken: Token);
    }
}
