using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

// Uses the ordinary encoder fixtures. No FX file, native library, GPU or additional clock is used.
public sealed partial class SurfaceFrameEncoderTests
{
    [Fact]
    public void ObjectFxEncoderUsesExplicitScopeAndKeepsTheSameSamplePacketsAndStableIds()
    {
        using var project = Project(out var legacy);
        var bindings = FxBindings(project);
        var rows = new[] { new D3D9ObjectFxAssignment(FirstId, -1, D3D9ObjectFxAssignmentMode.File, @"D:\absent\whole.fx"),
            new D3D9ObjectFxAssignment(FirstId, 0, D3D9ObjectFxAssignmentMode.Standard),
            new D3D9ObjectFxAssignment(SecondId, 0, D3D9ObjectFxAssignmentMode.Inherit) };
        var configuration = new D3D9ObjectFxConfiguration(rows);
        var encoder = new D3D9SurfaceFrameEncoder(bindings.Reverse(), configuration);
        rows[0] = new(999, -1, D3D9ObjectFxAssignmentMode.Standard);
        var frame = Capture(project, encoder) with { Sample = new(17, new(17, 60), new(11, 3), new(7, 2)) };
        var before = project.Frame; var clock = project.CurrentFrame;
        var expected = legacy.Serialize(frame, Request, Vector4.One);
        var actual = encoder.Serialize(frame, Request, Vector4.One);
        Assert.Equal(expected.Frame, actual.Frame); Assert.Equal(expected.Visual, actual.Visual);
        Assert.Equal(D3D9SurfaceScope.ConfiguredObjectFx, encoder.Scope);
        Assert.Equal(D3D9SurfaceScope.ConfiguredObjectFx, encoder.Encode(frame, Request, Vector4.One).Scope);
        Assert.Equal(D3D9SurfaceScope.IsolatedSurface, legacy.Encode(frame, Request, Vector4.One).Scope);
        Assert.Equal(3.5, encoder.Encode(frame, Request, Vector4.One).SourceFrame);
        Assert.Equal(new[] { FirstId, SecondId }, ParseFrame(actual.Frame).ObjectIds);
        Assert.Same(before, project.Frame); Assert.Equal(clock, project.CurrentFrame);
    }

    [Fact]
    public void ObjectFxConfigurationRequiresEveryNativeObjectIdentityBeforeAnyFrameOrDevice()
    {
        using var project = Project(out _);
        var configuration = new D3D9ObjectFxConfiguration([new(999, -1, D3D9ObjectFxAssignmentMode.Standard)]);
        Assert.Throws<ArgumentException>(() => new D3D9SurfaceFrameEncoder(FxBindings(project), configuration));
        Assert.Throws<ArgumentException>(() => new D3D9SurfacePreflightInputs(FxBindings(project), configuration));
        Assert.Throws<ArgumentNullException>(() => new D3D9SurfaceFrameEncoder(FxBindings(project), (D3D9ObjectFxConfiguration)null!));
        Assert.Throws<ArgumentNullException>(() => new D3D9SurfacePreflightInputs(FxBindings(project), (D3D9ObjectFxConfiguration)null!));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(int.MaxValue, false)]
    public void ObjectFxVisibleSubsetRangeUsesTheAuthoredSourceMaterialCount(int subset, bool accepted)
    {
        using var project = Project(out _);
        var encoder = new D3D9SurfaceFrameEncoder(FxBindings(project), new D3D9ObjectFxConfiguration([new(FirstId, subset, D3D9ObjectFxAssignmentMode.Standard)]));
        var frame = Capture(project, encoder);
        if (accepted) Assert.Equal(D3D9SurfaceScope.ConfiguredObjectFx, encoder.Encode(frame, Request, Vector4.One).Scope);
        else Assert.Throws<ArgumentException>(() => encoder.Encode(frame, Request, Vector4.One));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void ObjectFxHiddenRowsDoNotUseZeroVisibleMaterialsAsAnAuthoredSubsetCount(int subset, bool visibleAccepted)
    {
        var model = Model();
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 1));
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("animated", "animated.pmx", ModelRole.Unspecified, rest);
        var motion = new VmdMotion { IkKeyframes = [new() { Frame = 0, Visible = true },
            new() { Frame = 1, Visible = false }, new() { Frame = 2, Visible = true }] };
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, motion, PhysicsEnabled: false)], Provider());
        var encoder = new D3D9SurfaceFrameEncoder([new(actor.Id, FirstId, actor.SourcePath, actor.Name, ["root"])],
            new D3D9ObjectFxConfiguration([new(FirstId, subset, D3D9ObjectFxAssignmentMode.Standard)]));
        project.Seek(1, TestContext.Current.CancellationToken);
        var hidden = encoder.Encode(Capture(project, encoder), Request, Vector4.One);
        Assert.Equal(D3D9SurfaceScope.ConfiguredObjectFx, hidden.Scope);
        project.Seek(2, TestContext.Current.CancellationToken);
        var visible = Capture(project, encoder);
        if (visibleAccepted) Assert.Equal(2, encoder.Encode(visible, Request, Vector4.One).SourceFrame);
        else Assert.Throws<ArgumentException>(() => encoder.Encode(visible, Request, Vector4.One));
    }

    [Fact]
    public void ObjectFxCpuFacadeHasExplicitRequestsAndRejectsBothDirectionsOfScopeMixing()
    {
        using var project = Project(out var encoder);
        var bindings = FxBindings(project);
        var configured = new D3D9SurfacePreflightInputs(bindings, new D3D9ObjectFxConfiguration([]));
        var legacy = new D3D9SurfacePreflightInputs(bindings);
        Assert.Equal(D3D9SurfaceScope.ConfiguredObjectFx, configured.Scope);
        Assert.Equal(D3D9SurfaceScope.IsolatedSurface, legacy.Scope);
        Assert.Equal(Request.Width, configured.ValidateObjectFxRequest(Request, Vector4.One).Width);
        Assert.Throws<InvalidOperationException>(() => configured.ValidateRequest(Request, Vector4.One, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm));
        Assert.Throws<InvalidOperationException>(() => legacy.ValidateObjectFxRequest(Request, Vector4.One));
        var frame = Capture(project, encoder);
        var preview = FxPreview(frame, .1);
        configured.ValidateObjectFxFrame(frame, Request, Vector4.One);
        configured.ValidateObjectFxFrame(preview, Request, Vector4.One);
        Assert.Throws<InvalidOperationException>(() => configured.ValidateFrame(frame, Request, Vector4.One, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm));
        Assert.Throws<InvalidOperationException>(() => configured.ValidateFrame(preview, Request, Vector4.One, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm));
        Assert.Throws<InvalidOperationException>(() => legacy.ValidateObjectFxFrame(frame, Request, Vector4.One));
        Assert.Throws<InvalidOperationException>(() => legacy.ValidateObjectFxFrame(preview, Request, Vector4.One));
    }

    [Theory]
    [InlineData("samples")]
    [InlineData("aa")]
    [InlineData("extent")]
    [InlineData("alpha")]
    [InlineData("nan")]
    public void ObjectFxRequestDoesNotBypassTheOrdinarySurfaceQualityAndBackgroundRestrictions(string invalid)
    {
        var inputs = new D3D9SurfacePreflightInputs([], new D3D9ObjectFxConfiguration([]));
        var request = invalid switch
        {
            "samples" => Request with { Quality = new(SpatialSamples: 2) },
            "aa" => Request with { Quality = new(Antialiasing: FrameAntialiasing.Smaa1X) },
            "extent" => Request with { Width = 4097 },
            _ => Request
        };
        var background = invalid == "alpha" ? Vector4.Zero : invalid == "nan" ? new(float.NaN, 1, 1, 1) : Vector4.One;
        Assert.Throws<NotSupportedException>(() => inputs.ValidateObjectFxRequest(request, background));
    }

    [Theory]
    [InlineData("shadow")]
    [InlineData("style")]
    [InlineData("placed-sphere")]
    public void ObjectFxFrameKeepsTheOrdinarySurfaceSceneFeatureLimits(string invalid)
    {
        using var project = Project(out var encoder);
        var inputs = new D3D9SurfacePreflightInputs(FxBindings(project), new D3D9ObjectFxConfiguration([]));
        var frame = Capture(project, encoder);
        frame = invalid switch
        {
            "shadow" => frame with { SelfShadow = new(1, 10) },
            "style" => frame with { RenderingStyles = frame.RenderingStyles.Select(row => row with
                { Preset = RenderingPresetReference.BuiltIn(RenderingStyleId.Genshin) }).ToArray() },
            _ => WithSphere(frame, 0, OriginalSphereMode.Multiply)
        };
        Assert.Throws<NotSupportedException>(() => inputs.ValidateObjectFxFrame(frame, Request, Vector4.One));
    }

    private static D3D9SurfaceObjectBinding[] FxBindings(SceneProjectSession project) => project.Objects.Select((item, index) =>
        new D3D9SurfaceObjectBinding(item.Id, index == 0 ? FirstId : SecondId, item.SourcePath, item.Name)).ToArray();

    private static PreviewFrame FxPreview(OfflineFrame frame, double sourceFrame) => new(frame.Width, frame.Height, sourceFrame,
        frame.Scene, frame.Camera, frame.Light, frame.SelfShadow)
    { RenderingStyles = frame.RenderingStyles, SceneInputs = frame.SceneInputs, ResolvedCamera = frame.ResolvedCamera,
        CameraParentTransform = frame.CameraParentTransform, MorphChannels = frame.MorphChannels };
}
