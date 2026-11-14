using System.Numerics;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmAccessoryInputRetentionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SampleRetainsRawPanelAndSourceEvenWhenHidden(bool visible)
    {
        var original = XAccessoryAdapter.FromDocument(XAccessoryTests.Read(XAccessoryTests.Triangle)).Frame;
        var sourceWorld = Matrix4x4.CreateRotationY(.27f) * Matrix4x4.CreateTranslation(1, -2, 3);
        var source = new OriginalFrameScene(new(original.Surface.Snapshot.WithWorld(sourceWorld),
            original.Surface.Materials, original.Surface.Profile), original.Outline, original.OutlineOptions,
            original.Options, original.Materials, original.Lighting, original.EditorOverlay);
        var rotation = new Vector3(4 * MathF.PI + .31f, -6 * MathF.PI - .47f, 2 * MathF.PI + .63f);
        var pose = new PmmAccessoryPose(new(2, -3, 4), rotation, 1.7f, .5f, visible, true, 3, 4);
        var document = new PmmDocument { Accessories = [new()
        {
            Index = 7, Translation = pose.Translation, Rotation = pose.Rotation, Scale = pose.Scale,
            OpacityAndVisibility = PmmTrackSampling.PackOpacity(pose.Opacity, visible), ShadowEnabled = true,
            ParentModelIndex = 3, ParentBoneIndex = 4
        }] };
        var parent = Matrix4x4.CreateRotationX(-.38f) * Matrix4x4.CreateRotationZ(.52f)
            * Matrix4x4.CreateTranslation(16, 4, -2);
        int parentCalls = 0;
        var sample = PmmAccessoryScene.Sample(document, 7, source, 7.5, (model, bone) =>
        {
            Assert.Equal(3, model); Assert.Equal(4, bone); parentCalls++;
            return parent;
        }, saved: true);

        var inputs = Assert.IsType<PmmAccessorySampleInputs>(sample.Inputs);
        Assert.Equal(7.5, inputs.SourceFrame); Assert.True(inputs.IsSavedPose);
        Assert.Equal(pose, inputs.Pose); Assert.Equal(1, parentCalls);
        Assert.Same(source, inputs.Source); Assert.Null(inputs.Document);
        Assert.Equal(parent, inputs.ParentWorld);
        Assert.Equal(sourceWorld * PmmAccessoryScene.WorldTransform(pose, parent), inputs.World);
        Assert.Equal(inputs.World, inputs.SourceGeometry.World);
        Assert.Equal(source.Surface.Snapshot.Vertices.ToArray(), inputs.SourceGeometry.Vertices.ToArray());
        Assert.Equal(source.Surface.Snapshot.Indices.ToArray(), inputs.SourceGeometry.Indices.ToArray());
        Assert.Equal(.8f, Assert.Single(inputs.SourceGeometry.Materials).Diffuse.W);
        Assert.Equal(sourceWorld, source.Surface.Snapshot.World);
        Assert.Equal(.8f, Assert.Single(source.Surface.Snapshot.Materials).Diffuse.W);
        if (visible)
        {
            Assert.Equal(inputs.World, sample.Frame.Surface.Snapshot.World);
            Assert.Equal(.4f, Assert.Single(sample.Frame.Surface.Snapshot.Materials).Diffuse.W);
        }
        else
        {
            Assert.Empty(sample.Frame.Surface.Snapshot.Vertices.ToArray());
            Assert.NotEmpty(inputs.SourceGeometry.Vertices.ToArray());
        }
    }

    [Fact]
    public void LegacyManualFrameAndPreparedAssetLeaveOptionalInputsAbsent()
    {
        var source = XAccessoryAdapter.FromDocument(XAccessoryTests.Read(XAccessoryTests.Triangle)).Frame;
        var sample = new PmmAccessoryFrame(7, source, true, 1, false);
        var asset = new PreparedPmmAccessory(7, "part.x", new string('a', 64), source, []);
        Assert.Null(sample.Inputs); Assert.Null(asset.Document);
    }
}
