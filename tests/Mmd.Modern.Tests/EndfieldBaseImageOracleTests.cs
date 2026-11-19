using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EndfieldBaseImageOracleTests
{
    private static EndfieldBaseImageSpec Neutral => new(new(.2f,.4f,.7f), new(.5f), Vector3.One, Vector3.One, ShadowStrength:0);

    [Theory, InlineData(0), InlineData(1), InlineData(2)]
    public void FixedTablesAreHalfQuantized257TexelsAndSampleNormalizedCoordinates(int row)
    {
        var (lutBinding, values) = T8dTestHelpers.GetEndfieldShadowLut();
        Assert.Equal(771, values.Length);
        Assert.Equal(771 * 16, lutBinding.Data.Length);
        foreach (float q in new[] {-float.MaxValue,-1,0,.125f,.25f,.499f,.5f,.75f,1,3,float.MaxValue})
        {
            float coordinate = Math.Clamp(q,0,1)*257-.5f;
            int j=(int)MathF.Floor(coordinate); float f=coordinate-j;
            Vector4 a=values[row*257+Math.Clamp(j,0,256)];
            Vector4 b=values[row*257+Math.Clamp(j+1,0,256)];
            Near(EndfieldBaseImageOracle.SampleLut(row,q),new Vector3(a.X,a.Y,a.Z)*(1-f)+new Vector3(b.X,b.Y,b.Z)*f);
        }
        foreach (var v in values)
        { Assert.Equal((float)(Half)v.X,v.X); Assert.Equal((float)(Half)v.Y,v.Y); Assert.Equal((float)(Half)v.Z,v.Z); Assert.Equal(1,v.W); }
        Assert.Equal(.7998046875f,EndfieldBaseImageOracle.SampleLut(0,.5f).X);
        Assert.Equal(.6492919921875f,EndfieldBaseImageOracle.SampleLut(0,.25f).X);
        Near(new(1,.85302734375f,.681640625f),EndfieldBaseImageOracle.SampleLut(1,.5f));
        Near(new(1,.89990234375f,.75f),EndfieldBaseImageOracle.SampleLut(2,.5f));
        Assert.NotEqual(.8f,EndfieldBaseImageOracle.SampleLut(0,.5f).X);
    }

    [Fact]
    public void ShadowUsesStrictLumaBoundaryAndIndependentModelColor()
    {
        Vector3 m = new(.2f,.4f,.7f);
        Near(Vector3.Lerp(m,Vector3.One,.3f),EndfieldBaseImageOracle.Shadow(m,Vector3.One,.3f,0));
        Vector3 colored = new(2, .8f, 0); // luma < 1, despite a channel above white.
        Assert.True(Vector3.Distance(EndfieldBaseImageOracle.Shadow(m,colored,.3f,0),Vector3.Lerp(m,Vector3.One,.3f))>.1f);
        Vector3 whiteHigh = new(0,2,0); // luma > 1, despite two channels below white.
        Near(Vector3.Lerp(m,Vector3.One,.3f),EndfieldBaseImageOracle.Shadow(m,whiteHigh,.3f,0));
        Assert.NotEqual(EndfieldBaseImageOracle.Shadow(m,Vector3.One,.5f,0),EndfieldBaseImageOracle.Shadow(m,Vector3.One,.5f,1));
        Assert.Equal(EndfieldBaseImageOracle.Shadow(m,Vector3.One,.5f,1),EndfieldBaseImageOracle.Shadow(m,Vector3.One,.5f,9));
    }

    [Fact]
    public void BackfaceSquaresBeforeUnclampedContrastAndHsvRetainsSignedHdr()
    {
        var b=Neutral with {BaseImageLinearRgb=new(.2f, .7f, 1.4f),Contrast=-.6f,Saturation=1};
        Vector3 squared=b.BaseImageLinearRgb*b.BaseImageLinearRgb;
        Near(b.BaseImageLinearRgb*1.6f-b.BaseImageLinearRgb*b.BaseImageLinearRgb*.6f,EndfieldBaseImageOracle.Evaluate(b,true));
        Near(squared*1.6f-squared*squared*.6f,EndfieldBaseImageOracle.Evaluate(b,false));
        Near(new(0,.5f,.21428572f),EndfieldBaseImageOracle.Hsv(new(-.2f,.5f,.1f),1));
        Near(new(-1),EndfieldBaseImageOracle.Hsv(new(-2,-1,-3),1));
        Near(new(2),EndfieldBaseImageOracle.Hsv(new(.3f,2,.7f),0));
    }

    [Fact]
    public void BaseDetailAndExplicitShadowAreSeparateInputs()
    {
        var b=Neutral with {ModelColor=new(.1f,.3f,.6f),ShadowStrength=1,DetailBlue=.4f,ShadowFactor=.3f};
        Vector3 expected=b.BaseImageLinearRgb*.7f*b.ModelColor;
        Near(expected,EndfieldBaseImageOracle.Evaluate(b,true,sampledShadowFactor:0));
        Near(new Vector3(.3f,.4f,.8f)*.6f,EndfieldBaseImageOracle.Evaluate(Neutral,true,new(.3f,.4f,.8f),.2f,0));
        Assert.NotEqual(EndfieldBaseImageOracle.Evaluate(b,true),EndfieldBaseImageOracle.Evaluate(b with {ModelColor=new(.9f)},true));
    }

    [Fact]
    public void ValidationRejectsAmbiguousNonfiniteAndUnsupportedInputs()
    {
        var matcap = T8dTestHelpers.EndfieldMatcap(coupleEndfieldPalette: true);
        var palette = new EndfieldPaletteSpec(Vector3.Zero).ToBinding();
        Assert.True(T8dTestHelpers.ValidateEndfield([matcap, palette, Neutral.ToBinding()]).IsValid);
        foreach (var bad in new[]
        {
            Neutral with { Contrast = 5 },
            Neutral with { Saturation = float.NaN },
            Neutral with { BaseImageLinearRgb = new(float.PositiveInfinity) },
            Neutral with { DetailInputMode = T8dTestHelpers.ModeTexture },
            Neutral with { BaseSlot = 1 },
            Neutral with { BaseEncoding = T8dTestHelpers.EncodingSrgbRgb },
        })
            Assert.False(T8dTestHelpers.ValidateEndfield([matcap, palette, bad.ToBinding()]).IsValid);
    }

    [Fact]
    public void ConsumedEqualityTracksEffectiveInputsAndIgnoresDormantShadow()
    {
        var matcap = T8dTestHelpers.EndfieldMatcap(coupleEndfieldPalette: true);
        var palette = new EndfieldPaletteSpec(Vector3.Zero).ToBinding();
        var dormant = Neutral with { ShadowInputMode = T8dTestHelpers.ModeTexture, ShadowSlot = 1 };
        var dormantDraw = T8dTestHelpers.BuildEndfieldDrawData([matcap, palette, dormant.ToBinding()])!;
        Assert.DoesNotContain(dormantDraw.TextureBindings!, static t => t.SlotIndex == 8);
        var dormantOther = Neutral with { ShadowInputMode = T8dTestHelpers.ModeTexture, ShadowSlot = 2 };
        var tex = new SceneTexture("tex", 1, 1, [120, 140, 160, 255]);
        SceneVertex[] verts = [new(new(-1,-1,0), Vector3.UnitZ, Vector2.Zero), new(new(1,-1,0), Vector3.UnitZ, Vector2.UnitX), new(new(0,1,0), Vector3.UnitZ, Vector2.UnitY)];
        SceneSnapshot Snap(params SceneTexture[] ts) => new(verts, [0, 1, 2], [new("m", 0, 3, Vector4.One, Vector3.Zero, Vector3.Zero, 1)], ts);
        OriginalFrameScene MakeFrame(params SceneTexture[] ts) => new(new(Snap(ts), [new(Vector3.Zero)], new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), options: new(OriginalAlphaBlendMode.Normal, false, false));

        var frameDormant = MakeFrame(tex, tex, tex, tex, tex);
        var mapDormantA = T8dTestHelpers.CreateDirectSurfaceStyleMap(frameDormant, ["endfield"], bindingsByMaterial: [[matcap, palette, dormant.ToBinding()]]);
        var mapDormantB = T8dTestHelpers.CreateDirectSurfaceStyleMap(frameDormant, ["endfield"], bindingsByMaterial: [[matcap, palette, dormantOther.ToBinding()]]);
        Assert.True(D3D12SurfaceStyleMap.Equivalent(mapDormantA, mapDormantB));

        var t = Neutral with { BaseInputMode = T8dTestHelpers.ModeTexture, BaseSlot = 3 };
        var drawT = T8dTestHelpers.BuildEndfieldDrawData([matcap, palette, t.ToBinding()])!;
        Assert.Contains(drawT.TextureBindings!, static tb => tb.SlotIndex == 6 && tb.SceneTextureIndex == 3);
        var tRebased = t with { BaseSlot = 4 };
        var mapT = T8dTestHelpers.CreateDirectSurfaceStyleMap(frameDormant, ["endfield"], bindingsByMaterial: [[matcap, palette, t.ToBinding()]]);
        var mapTContrast = T8dTestHelpers.CreateDirectSurfaceStyleMap(frameDormant, ["endfield"], bindingsByMaterial: [[matcap, palette, (Neutral with { Contrast = .2f }).ToBinding()]]);
        var mapTRebased = T8dTestHelpers.CreateDirectSurfaceStyleMap(frameDormant, ["endfield"], bindingsByMaterial: [[matcap, palette, tRebased.ToBinding()]]);
        Assert.False(D3D12SurfaceStyleMap.Equivalent(
            T8dTestHelpers.CreateDirectSurfaceStyleMap(frameDormant, ["endfield"], bindingsByMaterial: [[matcap, palette, Neutral.ToBinding()]]),
            mapTContrast));
        Assert.False(D3D12SurfaceStyleMap.Equivalent(mapT, mapTRebased));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(
            mapT.CaptureResolvedInputs(frameDormant),
            mapTRebased.CaptureResolvedInputs(MakeFrame(tex, tex, tex, tex, tex))));
    }

    private static void Near(Vector3 expected,Vector3 actual)=>Assert.InRange(Vector3.Distance(expected,actual),0,4e-6f);
}
