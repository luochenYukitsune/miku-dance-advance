using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorMainLightDirectionTests
{
    [Fact]
    public async Task DirectDirectionEditingKeepsColorAndIntensityAndRegistersTheExactPose()
    {
        var model = new EditorSceneInspectorViewModel { SceneSelectionIndex = 2, LightColorR = .25m,
            LightColorG = .5m, LightColorB = .75m, LightIntensity = 3m };
        var color = model.LightPose.Color;
        var direction = Vector3.Normalize(new Vector3(.3f, -.6f, .7f));
        VmdMotion? registered = null;
        model.CommitSceneMotion = value => registered = value;
        model.LightDirectionVector = direction;
        Assert.Equal(color, model.LightPose.Color);
        Assert.Equal(3m, model.LightIntensity);
        Assert.Equal(.25m, model.LightColorR);
        Assert.Equal(direction, model.LightPose.Direction);
        Assert.True(model.HasLightDraft);
        Assert.Null(registered);
        await model.RegisterLightKeyframeAsync();
        var key = Assert.Single(registered!.LightKeyframes);
        Assert.Equal(direction, key.Direction);
        Assert.Equal(color, key.Color);
    }

    [Fact]
    public void InvalidDirectDirectionCannotPoisonTheExistingPose()
    {
        var model = new EditorSceneInspectorViewModel();
        var before = model.LightPose;
        foreach (var direction in new[] { Vector3.Zero, new Vector3(float.NaN, 1, 0), new Vector3(1, float.PositiveInfinity, 0) })
        {
            model.LightDirectionVector = direction;
            Assert.Equal(before, model.LightPose);
            Assert.False(model.HasLightDraft);
        }
    }
}
