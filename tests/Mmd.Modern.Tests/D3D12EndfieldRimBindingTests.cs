using System.Numerics;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12EndfieldRimBindingTests
{
    internal static readonly D3D12EndfieldRimBinding Binding = new(new(.12f,.07f,.03f),new(.3f,.2f,.1f),1.8f,.5f);
    internal static D3D12SurfaceStyleMap Map(D3D12EndfieldRimBinding? binding,
        D3D12SurfaceStyle family=D3D12SurfaceStyle.EndfieldPmx,
        D3D12CharacterMaterialRole role=D3D12CharacterMaterialRole.Body)
        =>new([family],[new(role,EndfieldRim:binding)]);

    [Fact]
    public void ExplicitRimNeedsNeitherRadialSeedsNorTexturesAndZeroStrengthIsExactOff()
    {
        var frame=D3D12EndfieldHairBindingTests.Frame(seeds:false,textures:[]);
        Map(Binding).Validate(frame);
        Assert.True(Map(Binding).HasConsumedEndfieldRim);
        Assert.False(Map(Binding).HasConsumedEndfieldHair);
        Assert.Same(Binding,Map(Binding).EffectiveEndfieldRim(0));
        Assert.Null(new D3D12CharacterMaterialStyle().EndfieldRim);
        Assert.Null(Map(Binding with {EdgeStrength=0}).EffectiveEndfieldRim(0));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(Map(null),Map(Binding with {EdgeStrength=0})));
    }

    [Fact]
    public void FamilyRoleAndDedicatedDataAreExplicit()
    {
        var frame=D3D12EndfieldHairBindingTests.Frame(seeds:false,textures:[]);
        foreach(var family in new[]{D3D12SurfaceStyle.Mmd,D3D12SurfaceStyle.GenshinPmx,
            D3D12SurfaceStyle.StarRailPmx,D3D12SurfaceStyle.WutheringPmx})
            Assert.Throws<NotSupportedException>(()=>Map(Binding,family).Validate(frame));
        foreach(var role in new[]{D3D12CharacterMaterialRole.Face,D3D12CharacterMaterialRole.Eye})
            Assert.Throws<ArgumentException>(()=>new D3D12CharacterMaterialStyle(role,EndfieldRim:Binding).Validate());
        new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,EndfieldRim:Binding,
            EndfieldHair:new(0,new(.1f))).Validate();
        Assert.Throws<ArgumentException>(()=>new D3D12CharacterMaterialStyle(D3D12CharacterMaterialRole.Hair,
            EndfieldRim:Binding,StarMcHair:new(0,0)).Validate());
        Assert.Throws<ArgumentException>(()=>new D3D12CharacterMaterialStyle(EndfieldRim:Binding,
            PmxMaterialTreatment:new()).Validate());
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
    public void NonfiniteAndOutsideExplicitNumericalDomainFailBeforeGpu(D3D12EndfieldRimBinding binding)
        =>Assert.Throws<ArgumentOutOfRangeException>(binding.Validate);

    [Fact]
    public void SignedStrengthsAndHdrColorsAreNotSourceUiClamped()
    {
        var value=Binding with{BaseColor=new(0,1,16),RimColor=new(16,0,3),EdgeSize=-16,
            EdgeStrength=-32,RangeStrength=-32,RadialStrength=32,DirectionRadians=-65536};
        value.Validate();Assert.Equal(-32,value.EdgeStrength);Assert.Equal(16,value.BaseColor.Z);
    }
}
