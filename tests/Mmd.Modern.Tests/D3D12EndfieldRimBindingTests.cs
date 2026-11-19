using System.Numerics;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12EndfieldRimBindingTests
{
    internal static readonly EndfieldRimSpec Binding = new(new(.12f,.07f,.03f),new(.3f,.2f,.1f),1.8f,.5f);
    internal static D3D12SurfaceStyleMap Map(EndfieldRimSpec? spec,
        string family="endfield",
        D3D12CharacterMaterialRole role=D3D12CharacterMaterialRole.Body,
        Core.OriginalFrameScene? frame=null)
        =>T8dTestHelpers.CreateDirectSurfaceStyleMap(
            frame??D3D12EndfieldHairBindingTests.Frame(seeds:false,textures:[]),
            [family],[new(role)],[spec is null?null:[spec.ToBinding()]]);

    [Fact]
    public void ExplicitRimNeedsNeitherRadialSeedsNorTexturesAndZeroStrengthIsExactOff()
    {
        var frame=D3D12EndfieldHairBindingTests.Frame(seeds:false,textures:[]);
        var map=Map(Binding,frame:frame);
        map.Validate(frame);
        Assert.Equal("rim",map.CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        Assert.Null(new D3D12CharacterMaterialStyle().PluginDraw);
        Assert.Equal("default",Map(Binding with {EdgeStrength=0},frame:frame).CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(null,frame:frame),Map(Binding with {EdgeStrength=0},frame:frame)));
    }

    [Fact]
    public void FamilyRoleAndDedicatedDataAreExplicit()
    {
        var frame=D3D12EndfieldHairBindingTests.Frame(seeds:false,textures:[]);
        Assert.Throws<NotSupportedException>(()=>new D3D12SurfaceStyleMap(["mmd"],[Map(Binding,frame:frame).CharacterMaterials[0]],TestStylePlugins.AllPresetCatalog).Validate(frame));
        foreach(var family in new[]{"genshin","star-rail","wuthering-waves"})
            Assert.NotEqual("rim",Map(Binding,family,frame:frame).CharacterMaterials[0].PluginDraw?.ShaderModuleId);
        foreach(var role in new[]{StyleMaterialRole.Face,StyleMaterialRole.Eye})
            Assert.False(T8dTestHelpers.ValidateEndfield(Binding.ToBinding(),role).IsValid);
        Assert.True(T8dTestHelpers.ValidateEndfield([Binding.ToBinding(),T8dTestHelpers.EndfieldHair(0,new(.1f))],StyleMaterialRole.Hair).IsValid);
        Assert.False(T8dTestHelpers.ValidateEndfield([Binding.ToBinding(),T8dTestHelpers.EndfieldFaceSdf()],StyleMaterialRole.Hair).IsValid);
        Assert.Throws<ArgumentException>(()=>new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            PmxMaterialTreatment:new(HairHighlightStrength:.2f),
            PluginDraw:D3D12StylePluginAdapter.ToD3D12DrawData(T8dTestHelpers.BuildEndfieldDrawData([Binding.ToBinding()],StyleMaterialRole.Hair)! with {TreatmentOverride=new(HairHighlightStrength:.2f)})).Validate());
    }

    [Fact]
    public void EveryConsumedScalarAndColorChangesHistoryWhileEqualCopiesDoNot()
    {
        var copy=Binding with {};
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(Binding),Map(copy)));
        foreach(var changed in new[]{Binding with{BaseColor=new(.2f)},Binding with{RimColor=new(.8f)},
            Binding with{EdgeSize=.4f},Binding with{EdgeStrength=-1},Binding with{DirectionRadians=1},
            Binding with{RangeStrength=-2},Binding with{RadialStrength=2}})
            Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Binding),Map(changed)));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(Map(Binding),Map(null)));
    }

    public static IEnumerable<object[]> Invalid()
    {
        foreach(float value in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity})
        {
            yield return [Binding with{BaseColor=new(value,0,0)}];
            yield return [Binding with{RimColor=new(0,value,0)}];
            yield return [Binding with{EdgeSize=value}];yield return [Binding with{EdgeStrength=value}];
            yield return [Binding with{DirectionRadians=value}];yield return [Binding with{RangeStrength=value}];
            yield return [Binding with{RadialStrength=value}];
        }
        yield return [Binding with{BaseColor=new(-.01f)}];yield return [Binding with{RimColor=new(16.01f)}];
        yield return [Binding with{EdgeSize=16.01f}];yield return [Binding with{EdgeStrength=32.01f}];
        yield return [Binding with{RangeStrength=-32.01f}];yield return [Binding with{RadialStrength=32.01f}];
        yield return [Binding with{DirectionRadians=65537}];
    }
    [Theory,MemberData(nameof(Invalid))]
    public void NonfiniteAndOutsideExplicitNumericalDomainFailBeforeGpu(EndfieldRimSpec spec)
        =>Assert.False(T8dTestHelpers.ValidateEndfield(spec.ToBinding()).IsValid);

    [Fact]
    public void SignedStrengthsAndHdrColorsAreNotSourceUiClamped()
    {
        var value=Binding with{BaseColor=new(0,1,16),RimColor=new(16,0,3),EdgeSize=-16,
            EdgeStrength=-32,RangeStrength=-32,RadialStrength=32,DirectionRadians=-65536};
        Assert.True(T8dTestHelpers.ValidateEndfield(value.ToBinding()).IsValid);
        var vecs=T8dTestHelpers.ReadCbvVectors(T8dTestHelpers.BuildEndfieldDrawData([value.ToBinding()])!);
        Assert.Equal(-32,vecs[2].Y);Assert.Equal(16,vecs[0].Z);
    }
}
