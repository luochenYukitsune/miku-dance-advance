using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed partial class SurfaceFrameEncoderTests
{
    [Fact]
    public void SceneFxUsesTheSameEvaluatedPacketsWithDistinctScopeAndNoClockAdvance()
    {
        using var project = Project(out var ordinary);
        var bindings = FxBindings(project);
        var configuration = SceneConfiguration([new(FirstId, 0, D3D9ObjectFxAssignmentMode.Standard)]);
        var encoder = new D3D9SurfaceFrameEncoder(bindings.Reverse(), configuration);
        var frame = Capture(project, encoder) with { Sample = new(17, new(17, 60), new(11, 3), new(7, 2)) };
        var before = project.Frame; var clock = project.CurrentFrame;
        var expected = ordinary.Serialize(frame, Request, Vector4.One);
        var actual = encoder.Serialize(frame, Request, Vector4.One);
        Assert.Equal(expected.Frame, actual.Frame); Assert.Equal(expected.Visual, actual.Visual);
        var snapshot = encoder.Encode(frame, Request, Vector4.One);
        Assert.Equal(D3D9SurfaceScope.ConfiguredSceneFx, encoder.Scope);
        Assert.Equal(D3D9SurfaceScope.ConfiguredSceneFx, snapshot.Scope);
        Assert.Equal(3.5, snapshot.SourceFrame);
        Assert.Equal(new[] { FirstId, SecondId }, ParseFrame(actual.Frame).ObjectIds);
        Assert.Same(before, project.Frame); Assert.Equal(clock, project.CurrentFrame);
    }

    [Fact]
    public void SceneFxCpuPreflightRejectsScopeMixingAndUnknownIdsBeforeDeviceCreation()
    {
        using var project = Project(out var ordinary);
        var bindings = FxBindings(project);
        var scene = new D3D9SurfacePreflightInputs(bindings, SceneConfiguration([]));
        var isolated = new D3D9SurfacePreflightInputs(bindings);
        var objects = new D3D9SurfacePreflightInputs(bindings, new D3D9ObjectFxConfiguration([]));
        Assert.Equal(D3D9SurfaceScope.ConfiguredSceneFx, scene.Scope);
        Assert.Equal(Request.Width, scene.ValidateSceneFxRequest(Request, Vector4.One).Width);
        var frame = Capture(project, ordinary);
        scene.ValidateSceneFxFrame(frame, Request, Vector4.One);
        scene.ValidateSceneFxFrame(FxPreview(frame, .5), Request, Vector4.One);
        Assert.Throws<InvalidOperationException>(() => scene.ValidateRequest(Request, Vector4.One, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm));
        Assert.Throws<InvalidOperationException>(() => scene.ValidateObjectFxRequest(Request, Vector4.One));
        Assert.Throws<InvalidOperationException>(() => isolated.ValidateSceneFxRequest(Request, Vector4.One));
        Assert.Throws<InvalidOperationException>(() => objects.ValidateSceneFxFrame(frame, Request, Vector4.One));
        Assert.Throws<InvalidOperationException>(() => scene.ValidateObjectFxFrame(FxPreview(frame, .5), Request, Vector4.One));
        var unknown = SceneConfiguration([new(999, -1, D3D9ObjectFxAssignmentMode.Standard)]);
        Assert.Throws<ArgumentException>(() => new D3D9SurfaceFrameEncoder(bindings, unknown));
        Assert.Throws<ArgumentException>(() => new D3D9SurfacePreflightInputs(bindings, unknown));
    }

    [Fact]
    public void SceneFxKeepsAuthoredSubsetAndUnsupportedSceneFeatureValidation()
    {
        using var project = Project(out var ordinary);
        var frame = Capture(project, ordinary);
        var valid = new D3D9SurfacePreflightInputs(FxBindings(project), SceneConfiguration([]));
        Assert.Throws<NotSupportedException>(() => valid.ValidateSceneFxRequest(Request with { Quality = new(SpatialSamples: 2) }, Vector4.One));
        Assert.Throws<NotSupportedException>(() => valid.ValidateSceneFxFrame(frame with { SelfShadow = new(1, 10) }, Request, Vector4.One));
        Assert.Throws<NotSupportedException>(() => valid.ValidateSceneFxFrame(frame with { RenderingStyles = frame.RenderingStyles.Select(row =>
            row with { Preset = RenderingPresetReference.BuiltIn(RenderingStyleId.Genshin) }).ToArray() }, Request, Vector4.One));
        var invalidSubset = new D3D9SurfacePreflightInputs(FxBindings(project),
            SceneConfiguration([new(FirstId, 1, D3D9ObjectFxAssignmentMode.Standard)]));
        Assert.Throws<ArgumentException>(() => invalidSubset.ValidateSceneFxFrame(frame, Request, Vector4.One));
    }

    private static D3D9SceneFxConfiguration SceneConfiguration(IEnumerable<D3D9ObjectFxAssignment> assignments) =>
        new(assignments, new D3D9EffectResourceMap([new(@"D:\origin\scene.fx", @"D:\absent\scene.fx")]));
}
