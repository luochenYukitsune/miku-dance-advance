using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class CharacterOutlinePlaybackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceCustomStyleSurvivesAnimationAndSeekWhileEnvironmentClearsIt(bool shared)
    {
        var token = TestContext.Current.CancellationToken;
        var model = EditedMotionProjectTests.Model();
        var source = EditedMotionProjectTests.Rest(model);
        var style = new CharacterOutlineStyle(.23f, new(.1f, .2f, .3f, .8f), false);
        var rest = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions,
            materials: source.Materials.Select(flags => flags with { CharacterOutlineStyle = style }));
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, rest);
        var room = layout.Add("room", "room.pmx", ModelRole.Environment, rest);
        var motion = new VmdMotion
        {
            BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = Vector3.UnitX }],
        };
        using var session = new SceneAnimationSession(layout.Objects,
            [new(actor.Id, model, motion, PhysicsEnabled: false), new(room.Id, model, motion, PhysicsEnabled: false)],
            _ => throw new InvalidOperationException("No independent physics expected."), cancellationToken: token,
            sharedPhysicsFactory: shared ? () => new TrackingSharedBackend() : null);
        void Check()
        {
            Assert.Equal(style, session.Frame.Materials[0].CharacterOutlineStyle);
            Assert.Null(session.Frame.Materials[1].CharacterOutlineStyle);
        }
        Check(); session.Advance(.25, token); Check();
        Assert.True(session.Frame.Surface.Snapshot.HasDynamicVertices);
        session.Seek(25, token); Check(); session.Seek(0, token); Check();
        session.ClearMotion(actor.Id); Check();
        session.Seek(15, token); Check();
        Assert.Equal(style, Assert.Single(rest.Materials).CharacterOutlineStyle);
        Assert.Null(Assert.Single(source.Materials).CharacterOutlineStyle);
    }
}
