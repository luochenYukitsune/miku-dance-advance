using System.Reflection;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Modern renderers do not host MME. Retained legacy effect experiments are not
/// a modern dependency or a modern public loading contract.</summary>
public sealed class ModernRenderingBoundaryTests
{
    [Fact]
    public void ModernRenderAndOutputDependencyGraphsContainNoMmeAssembly()
    {
        var pending=new Queue<Assembly>(new[] {typeof(D3D12FrameSession).Assembly,typeof(D3D12ViewRenderer).Assembly});
        var visited=new HashSet<string>(StringComparer.Ordinal);
        while(pending.Count>0)
        {
            var assembly=pending.Dequeue();
            string name=assembly.GetName().Name!;
            if(!visited.Add(name))continue;
            Assert.NotEqual("Mmd.Modern.Effects",name);
            foreach(var reference in assembly.GetReferencedAssemblies())
            {
                Assert.NotEqual("Mmd.Modern.Effects",reference.Name);
                if(reference.Name?.StartsWith("Mmd.Modern.",StringComparison.Ordinal)==true)
                    pending.Enqueue(Assembly.Load(reference));
            }
        }
        Assert.Contains("Mmd.Modern.Rendering.D3D12",visited);
        Assert.Contains("Mmd.Modern.ViewOutput.D3D12",visited);
    }

    [Fact]
    public void ModernPublicSignaturesContainNoMmeLanguageOrExecutionTypes()
    {
        foreach(var assembly in new[] {typeof(D3D12FrameSession).Assembly,typeof(D3D12ViewRenderer).Assembly})
        foreach(var type in assembly.GetExportedTypes())
        {
            NoMme(type);
            foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly))
            {
                NoMme(method.ReturnType);
                foreach(var parameter in method.GetParameters())NoMme(parameter.ParameterType);
            }
            foreach(var constructor in type.GetConstructors())
                foreach(var parameter in constructor.GetParameters())NoMme(parameter.ParameterType);
            foreach(var property in type.GetProperties())NoMme(property.PropertyType);
            foreach(var field in type.GetFields())NoMme(field.FieldType);
        }
        var modern=typeof(D3D12FrameSession).Assembly;
        Assert.NotNull(modern.GetType("Mmd.Modern.Rendering.D3D12.D3D12FrameRenderer"));
        Assert.Null(modern.GetType("Mmd.Modern.Rendering.D3D12.D3D12EffectRenderer"));
        Assert.Null(modern.GetType("Mmd.Modern.Rendering.D3D12.D3D12OffscreenRenderer"));
        Assert.All(typeof(D3D12FrameSession).GetMethods().Where(m=>m.Name is "Render" or "Capture"),method=>
        {
            Assert.DoesNotContain(method.GetParameters(),p=>p.Name is "effect" or "overrides");
            Assert.Contains(method.GetParameters(),p=>p.ParameterType==typeof(RayGiOptions));
            Assert.Contains(method.GetParameters(),p=>p.ParameterType==typeof(D3D12TemporalUpscalingOptions));
        });
    }

    [Fact]
    public void ModernViewportHasNoMmeLoadingApiAndRejectsTheLegacyEffectSwitch()
    {
        Assert.DoesNotContain(typeof(FrameViewportViewModel).Assembly.GetReferencedAssemblies(),a=>a.Name=="Mmd.Modern.Effects");
        Assert.Null(typeof(FrameViewportViewModel).GetProperty("Effect"));
        Assert.Null(typeof(FrameViewportViewModel).GetMethod("LoadEffect"));
        Assert.Null(typeof(FrameViewportViewModel).GetMethod("DisableEffect"));
        Assert.Null(typeof(FrameViewportOptions).GetProperty("EffectPath"));
        var error=Assert.Throws<NotSupportedException>(()=>FrameViewportOptions.Parse(["--frame-viewer","--effect","legacy.fx"]));
        Assert.Contains("MME",error.Message,StringComparison.Ordinal);
        Assert.Contains("compatibility",error.Message,StringComparison.OrdinalIgnoreCase);
        var options=FrameViewportOptions.Parse(["--frame-viewer","--antialiasing","smaa","--gi","hardware"]);
        Assert.Equal(FrameAntialiasing.Smaa1X,options.Antialiasing);
        Assert.Equal(LightingMode.HardwareRayTracing,options.RayGi!.Mode);
    }

    private static void NoMme(Type type)
    {
        Assert.NotEqual("Mmd.Modern.Effects",type.Assembly.GetName().Name);
        Assert.False(type.Namespace?.StartsWith("Mmd.Modern.Effects",StringComparison.Ordinal)==true,type.FullName);
        if(type.HasElementType)NoMme(type.GetElementType()!);
        if(type.IsGenericType)foreach(var argument in type.GetGenericArguments())NoMme(argument);
    }
}