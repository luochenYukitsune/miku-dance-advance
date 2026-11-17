using System.Numerics;
using Mmd.Modern.Demo;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class DemoUsabilityTests
{
    private static FrameViewportViewModel Demo()
    {
        var model=new FrameViewportViewModel(new(Enabled:true,EngineDemo:true));
        model.LoadEngineDemo();return model;
    }
    private static string Fixture=>Path.Combine(AppContext.BaseDirectory,"fixtures","rich-w1-uv4.pmx");

    [Fact] public void FailedPmxPreparationPreservesDemoFrameLightsAndPhysics()
    {
        using var vm=Demo();var frame=vm.GetFrame(800,600);var lights=vm.LightEditor;
        Assert.Throws<FileNotFoundException>(()=>vm.LoadModel(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".pmx")));
        Assert.True(vm.HasScene);Assert.True(vm.HasEngineDemo);Assert.Same(frame,vm.GetFrame(800,600));Assert.Same(lights,vm.LightEditor);
    }

    [Fact] public void AppearanceChangesDoNotAccumulateAndResetRestoresOriginalInputs()
    {
        using var vm=Demo();var original=vm.GetFrame(800,600);
        vm.AmbientStrength=.5;vm.SpecularStrength=0;vm.OutlineWidthScale=2;
        var edited=vm.GetFrame(800,600);
        Assert.Equal(original.Surface.Profile.LightAmbient*.5f,edited.Surface.Profile.LightAmbient);
        Assert.Equal(Vector3.Zero,edited.Surface.Profile.LightSpecular);Assert.Equal(2f,edited.OutlineOptions!.ModelWidth);
        vm.Yaw+=10;
        Assert.Equal(edited.Surface.Profile.LightAmbient,vm.GetFrame(800,600).Surface.Profile.LightAmbient);
        vm.ResetAppearanceSettings();var reset=vm.GetFrame(800,600);
        Assert.Same(original.Surface.Snapshot,reset.Surface.Snapshot);
        Assert.Equal(original.Surface.Profile,reset.Surface.Profile);Assert.Equal(original.OutlineOptions!.ModelWidth,reset.OutlineOptions!.ModelWidth);
    }

    [Fact] public void OpacityAffectsActualSurfaceAndOutlineAndDisablesUnsupportedGi()
    {
        using var vm=Demo();var original=vm.GetFrame(800,600);
        vm.UpdateLightingCapabilities(new(RenderApi.Direct3D12,true,true,true,0),new(Compute:true,ShaderModel:65,
            RawRaytracingTier:12,Device5:true,CommandList4:true,CompilerAvailable:true,SoftwareImplementationReady:true,HardwareImplementationReady:true));
        Assert.True(vm.TrySelectGiMode(LightingMode.HardwareRayTracing));
        vm.MaterialOpacity=.4;var translucent=vm.GetFrame(800,600);
        Assert.Equal(LightingMode.Legacy,vm.GiMode);Assert.False(vm.HardwareGiAvailable);
        Assert.Equal(original.Surface.Snapshot.Materials[0].Diffuse.W*.4f,translucent.Surface.Snapshot.Materials[0].Diffuse.W);
        Assert.Equal(original.Outline!.Materials[0].Color.W*.4f,translucent.Outline!.Materials[0].Color.W);
        vm.MaterialOpacity=1;Assert.True(vm.HardwareGiAvailable);Assert.Equal(LightingMode.Legacy,vm.GiMode);
        Assert.Same(original.Surface.Snapshot,vm.GetFrame(800,600).Surface.Snapshot);
    }

    [Fact] public void PanMovesEyeAndTargetTogetherAndResetRestoresFraming()
    {
        using var vm=Demo();var before=vm.GetFrame(800,600).Options!.CameraOverride!;
        var point=before.Target;
        vm.PanCameraPixels(80,40,800,600);var after=vm.GetFrame(800,600).Options!.CameraOverride!;
        Assert.InRange(Vector3.Distance(before.Eye-before.Target,after.Eye-after.Target),0,1e-4f);
        var a=Vector4.Transform(new Vector4(point,1),before.ViewProjection(800,600));
        var b=Vector4.Transform(new Vector4(point,1),after.ViewProjection(800,600));
        Assert.InRange((b.X/b.W-a.X/a.W)*400,79.99f,80.01f);
        Assert.InRange(-(b.Y/b.W-a.Y/a.W)*300,39.99f,40.01f);
        vm.ResetCamera();Assert.Equal(0,vm.PanX);Assert.Equal(0,vm.PanY);
    }

    [Fact] public void PmxCommitLeavesDemoModeAndReportsSceneStateToControls()
    {
        using var vm=Demo();var changed=new HashSet<string?>();vm.PropertyChanged+=(_,e)=>changed.Add(e.PropertyName);
        using var prepared=vm.PrepareModel(Fixture);
        Assert.True(vm.HasEngineDemo);
        vm.CommitModel(prepared);
        Assert.True(vm.HasScene);Assert.False(vm.HasEngineDemo);Assert.Equal(Path.GetFullPath(Fixture),vm.ModelPath);
        Assert.Contains(nameof(vm.HasEngineDemo),changed);Assert.Contains(nameof(vm.ModelPath),changed);
        Assert.Equal(LightingMode.Legacy,vm.GiMode);
        vm.LoadEngineDemo();Assert.True(vm.HasEngineDemo);Assert.Null(vm.ModelPath);
    }

    [Fact] public void NonfiniteCameraAndAppearanceInputsCannotPoisonAnExistingFrame()
    {
        using var vm=Demo();var original=vm.GetFrame(800,600);
        Assert.Throws<ArgumentOutOfRangeException>(()=>vm.PanX=double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(()=>vm.AmbientStrength=double.PositiveInfinity);
        Assert.Throws<ArgumentOutOfRangeException>(()=>vm.MaterialOpacity=-.1);
        Assert.Throws<ArgumentOutOfRangeException>(()=>vm.PanCameraPixels(double.NaN,0,800,600));
        Assert.Same(original,vm.GetFrame(800,600));
    }
}
