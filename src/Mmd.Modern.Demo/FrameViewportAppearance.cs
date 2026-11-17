using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Demo;

/// <summary>Preview overrides. Source material/profile data stays immutable and default multipliers retain the old path.</summary>
public sealed partial class FrameViewportViewModel
{
    private double ambientStrength=1,specularStrength=1,outlineWidthScale=1,materialOpacity=1;
    public double AmbientStrength {get=>ambientStrength;set{ValidateAppearance(value,2);if(value==ambientStrength)return;ambientStrength=value;Invalidate();}}
    public double SpecularStrength {get=>specularStrength;set{ValidateAppearance(value,2);if(value==specularStrength)return;specularStrength=value;Invalidate();}}
    public double OutlineWidthScale {get=>outlineWidthScale;set{ValidateAppearance(value,3);if(value==outlineWidthScale)return;outlineWidthScale=value;Invalidate();}}
    public double MaterialOpacity
    {
        get=>materialOpacity;
        set
        {
            ValidateAppearance(value,1);if(value==materialOpacity)return;materialOpacity=value;
            if(value<1&&GiEnabled)TrySelectGiMode(LightingMode.Legacy);
            Invalidate();NotifyGiMenu();
            if(value<1)Status="整体透明度已改变；当前光追不计算部分透光，GI 保持关闭。贴图自身透明通道仍保留。";
        }
    }
    private static void ValidateAppearance(double value,double maximum)
    {if(!double.IsFinite(value)||value<0||value>maximum)throw new ArgumentOutOfRangeException(nameof(value));}
    public void ResetAppearanceSettings()
    {
        ambientStrength=specularStrength=outlineWidthScale=materialOpacity=1;cachedFrame=null;
        Changed(nameof(AmbientStrength));Changed(nameof(SpecularStrength));Changed(nameof(OutlineWidthScale));Changed(nameof(MaterialOpacity));NotifyGiMenu();
    }
    private OriginalStyleScene ApplyAppearance(OriginalStyleScene input)
    {
        if(ambientStrength==1&&specularStrength==1&&materialOpacity==1)return input;
        var snapshot=materialOpacity==1?input.Snapshot:input.Snapshot.WithMaterials(input.Snapshot.Materials.Select(m=>m with
        {Diffuse=new(m.Diffuse.X,m.Diffuse.Y,m.Diffuse.Z,m.Diffuse.W*(float)materialOpacity)}));
        return new(snapshot,input.Materials,input.Profile with
        {LightAmbient=input.Profile.LightAmbient*(float)ambientStrength,LightSpecular=input.Profile.LightSpecular*(float)specularStrength});
    }
    private OriginalOutlineData ApplyOutlineAppearance(OriginalOutlineData input)=>materialOpacity==1?input:new(input.Vertices,
        input.Materials.Select(m=>m with{Color=new(m.Color.X,m.Color.Y,m.Color.Z,m.Color.W*(float)materialOpacity)}));
}
