using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
        var encoder = ObjectFxEncoder(bindings.Reverse(), configuration);
        rows[0] = new(999, -1, D3D9ObjectFxAssignmentMode.Standard);
        var frame = Capture(project, encoder) with { Sample = new(17, new(17, 60), new(11, 3), new(7, 2)) };
        var before = project.Frame; var clock = project.CurrentFrame;
        var expected = legacy.Serialize(frame, Request, Vector4.One);
        var actual = encoder.Serialize(frame, Request, Vector4.One);
        Assert.Equal(expected.Frame, actual.Frame); Assert.Equal(expected.Visual, actual.Visual);
        Assert.Equal(D3D9SurfaceScope.ConfiguredObjectFx, EncoderScope(encoder));
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
        Assert.Throws<ArgumentException>(() => ObjectFxEncoder(FxBindings(project), configuration));
        Assert.Throws<ArgumentException>(() => ObjectFxInputs(FxBindings(project), configuration));
        Assert.Throws<ArgumentNullException>(() => ObjectFxEncoder(FxBindings(project), null!));
        Assert.Throws<ArgumentNullException>(() => ObjectFxInputs(FxBindings(project), null!));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(int.MaxValue, false)]
    public void ObjectFxVisibleSubsetRangeUsesTheAuthoredSourceMaterialCount(int subset, bool accepted)
    {
        using var project = Project(out _);
        var encoder = ObjectFxEncoder(FxBindings(project), new([new(FirstId, subset, D3D9ObjectFxAssignmentMode.Standard)]));
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
        var encoder = ObjectFxEncoder([new(actor.Id, FirstId, actor.SourcePath, actor.Name, ["root"])],
            new([new(FirstId, subset, D3D9ObjectFxAssignmentMode.Standard)]));
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
        var configured = ObjectFxInputs(bindings, new([]));
        var legacy = new D3D9SurfacePreflightInputs(bindings);
        Assert.Equal(D3D9SurfaceScope.ConfiguredObjectFx, FacadeScope(configured));
        Assert.Equal(D3D9SurfaceScope.IsolatedSurface, FacadeScope(legacy));
        Assert.Equal(Request.Width, ObjectFxRequest(configured, Request, Vector4.One).Width);
        Assert.Throws<InvalidOperationException>(() => configured.ValidateRequest(Request, Vector4.One, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm));
        Assert.Throws<InvalidOperationException>(() => ObjectFxRequest(legacy, Request, Vector4.One));
        var frame = Capture(project, encoder);
        var preview = FxPreview(frame, .1);
        ObjectFxFrame(configured, frame, Request, Vector4.One);
        ObjectFxFrame(configured, preview, Request, Vector4.One);
        Assert.Throws<InvalidOperationException>(() => configured.ValidateFrame(frame, Request, Vector4.One, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm));
        Assert.Throws<InvalidOperationException>(() => configured.ValidateFrame(preview, Request, Vector4.One, D3D9SurfaceIsolation.NoConfiguredMmeOrEmm));
        Assert.Throws<InvalidOperationException>(() => ObjectFxFrame(legacy, frame, Request, Vector4.One));
        Assert.Throws<InvalidOperationException>(() => ObjectFxFrame(legacy, preview, Request, Vector4.One));
    }

    [Theory]
    [InlineData("samples")]
    [InlineData("aa")]
    [InlineData("extent")]
    [InlineData("alpha")]
    [InlineData("nan")]
    public void ObjectFxRequestDoesNotBypassTheOrdinarySurfaceQualityAndBackgroundRestrictions(string invalid)
    {
        var inputs = ObjectFxInputs([], new([]));
        var request = invalid switch
        {
            "samples" => Request with { Quality = new(SpatialSamples: 2) },
            "aa" => Request with { Quality = new(Antialiasing: FrameAntialiasing.Smaa1X) },
            "extent" => Request with { Width = 4097 },
            _ => Request
        };
        var background = invalid == "alpha" ? Vector4.Zero : invalid == "nan" ? new(float.NaN, 1, 1, 1) : Vector4.One;
        Assert.Throws<NotSupportedException>(() => ObjectFxRequest(inputs, request, background));
    }

    [Theory]
    [InlineData("shadow")]
    [InlineData("style")]
    [InlineData("placed-sphere")]
    public void ObjectFxFrameKeepsTheOrdinarySurfaceSceneFeatureLimits(string invalid)
    {
        using var project = Project(out var encoder);
        var inputs = ObjectFxInputs(FxBindings(project), new([]));
        var frame = Capture(project, encoder);
        frame = invalid switch
        {
            "shadow" => frame with { SelfShadow = new(1, 10) },
            "style" => frame with { RenderingStyles = frame.RenderingStyles.Select(row => row with
                { Preset = RenderingPresetReference.BuiltIn(RenderingStyleId.Genshin) }).ToArray() },
            _ => WithSphere(frame, 0, OriginalSphereMode.Multiply)
        };
        Assert.Throws<NotSupportedException>(() => ObjectFxFrame(inputs, frame, Request, Vector4.One));
    }

    private static D3D9SurfaceObjectBinding[] FxBindings(SceneProjectSession project) => project.Objects.Select((item, index) =>
        new D3D9SurfaceObjectBinding(item.Id, index == 0 ? FirstId : SecondId, item.SourcePath, item.Name)).ToArray();

    private static PreviewFrame FxPreview(OfflineFrame frame, double sourceFrame) => new(frame.Width, frame.Height, sourceFrame,
        frame.Scene, frame.Camera, frame.Light, frame.SelfShadow)
    { RenderingStyles = frame.RenderingStyles, SceneInputs = frame.SceneInputs, ResolvedCamera = frame.ResolvedCamera,
        CameraParentTransform = frame.CameraParentTransform, MorphChannels = frame.MorphChannels };

    // Reflection keeps the RED suite runnable before the explicit scope API exists.
    private static D3D9SurfaceFrameEncoder ObjectFxEncoder(IEnumerable<D3D9SurfaceObjectBinding> bindings, D3D9ObjectFxConfiguration configuration) =>
        FxConstruct<D3D9SurfaceFrameEncoder>(bindings, configuration);

    private static D3D9SurfacePreflightInputs ObjectFxInputs(IEnumerable<D3D9SurfaceObjectBinding> bindings, D3D9ObjectFxConfiguration configuration) =>
        FxConstruct<D3D9SurfacePreflightInputs>(bindings, configuration);

    private static T FxConstruct<T>(IEnumerable<D3D9SurfaceObjectBinding> bindings, D3D9ObjectFxConfiguration configuration)
    {
        // Match the (bindings, configuration) constructor even when later parameters are optional, as on
        // D3D9SurfacePreflightInputs(bindings, configuration, classicPassInputs = null).
        var constructor = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SingleOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length >= 2 && parameters[0].ParameterType == typeof(IEnumerable<D3D9SurfaceObjectBinding>)
                    && parameters[1].ParameterType == typeof(D3D9ObjectFxConfiguration) && parameters.Skip(2).All(p => p.IsOptional);
            });
        Assert.NotNull(constructor);
        object?[] arguments = [bindings, configuration, .. constructor.GetParameters().Skip(2).Select(p => p.DefaultValue)];
        try { return (T)constructor.Invoke(arguments); }
        catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }

    private static D3D9SurfaceScope EncoderScope(D3D9SurfaceFrameEncoder encoder) => FxScope(encoder);
    private static D3D9SurfaceScope FacadeScope(D3D9SurfacePreflightInputs inputs) => FxScope(inputs);
    private static D3D9SurfaceScope FxScope(object value)
    {
        var property = value.GetType().GetProperty("Scope", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(property);
        return Assert.IsType<D3D9SurfaceScope>(property.GetValue(value));
    }

    private static ViewRenderRequest ObjectFxRequest(D3D9SurfacePreflightInputs inputs, ViewRenderRequest request, Vector4 background) =>
        Assert.IsType<ViewRenderRequest>(FxCall(inputs, "ValidateObjectFxRequest", [typeof(ViewRenderRequest), typeof(Vector4)], [request, background]));

    private static void ObjectFxFrame(D3D9SurfacePreflightInputs inputs, object frame, ViewRenderRequest request, Vector4 background) =>
        FxCall(inputs, "ValidateObjectFxFrame", [frame.GetType(), typeof(ViewRenderRequest), typeof(Vector4)], [frame, request, background]);

    private static object? FxCall(object target, string name, Type[] signature, object[] arguments)
    {
        var method = target.GetType().GetMethod(name, signature);
        Assert.NotNull(method);
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }
}
