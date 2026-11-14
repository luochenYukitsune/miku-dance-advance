namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently implemented soft shape only: explicit data, fixed Y-up horizontal
    // projection and cubic B-spline repeat. No reference texture or GPL shader is bundled.
    // The PMX palette and existing geometric visibility are deliberate MDA adapters.
    private static string GenshinFaceSdfSurfaceSource(bool native, bool gi, bool textureMorph, bool subTexture)
    {
        const string helper = """
            cbuffer FaceSdfConstants:register(b4) {
                float4 FaceSdfForward; float4 FaceSdfRight; float4 FaceSdfParameters;
            };
            Texture2D<float4> FaceSdfMap:register(t7);
            Texture2D<float4> FaceCombinedMap:register(t8);
            SamplerState FaceSdfSampler:register(s2);
            float4 FaceCubicWeights(float t) {
                float t2=t*t, t3=t2*t, u=1-t;
                return float4(u*u*u,3*t3-6*t2+4,-3*t3+3*t2+3*t+1,t3)/6;
            }
            float4 FaceCubicRepeat(Texture2D<float4> image,float2 uv) {
                uint width,height; image.GetDimensions(width,height);
                float2 size=float2(width,height);
                float2 position=uv*size-.5;
                float2 cell=floor(position), fraction=position-cell;
                float4 wx=FaceCubicWeights(fraction.x), wy=FaceCubicWeights(fraction.y);
                float2 first=float2(wx.x+wx.y,wy.x+wy.y);
                float2 second=float2(wx.z+wx.w,wy.z+wy.w);
                float2 low=(cell-.5+float2(wx.y,wy.y)/first)/size;
                float2 high=(cell+1.5+float2(wx.w,wy.w)/second)/size;
                float4 a=image.SampleLevel(FaceSdfSampler,low,0);
                float4 b=image.SampleLevel(FaceSdfSampler,float2(high.x,low.y),0);
                float4 c=image.SampleLevel(FaceSdfSampler,float2(low.x,high.y),0);
                float4 d=image.SampleLevel(FaceSdfSampler,high,0);
                return a*first.x*first.y+b*second.x*first.y+c*first.x*second.y+d*second.x*second.y;
            }
            float FaceShapePower(float value,float exponent) {
                if(exponent==0) return 1;
                if(value==0) return 0;
                if(value>0) return pow(value,exponent);
                // Negative input is only accepted with a near-integral power.
                float fractional=frac(abs(exponent));
                if(fractional>=.001 && fractional<=.999) return 0;
                float integerPower=floor(exponent+.5);
                float magnitude=pow(-value,integerPower);
                return fmod(abs(integerPower),2)==0?magnitude:-magnitude;
            }
            float FaceSdfLightResponse(SurfaceOutput v,float3 towardLight,float visibility) {
                // Source head vectors are normalized before fixed-plane projection;
                // projecting them does not normalize them a second time.
                float2 horizontal=towardLight.xz;
                float lengthSquared=dot(horizontal,horizontal);
                horizontal=lengthSquared==0?float2(0,0):horizontal*rsqrt(lengthSquared);
                float forward=dot(FaceSdfForward.xz,horizontal);
                float side=dot(FaceSdfRight.xz,horizontal);
                float2 faceUv=v.Uv;
                if(side<=0) faceUv.x=-faceUv.x;
                float lightmap=dot(FaceCubicRepeat(FaceSdfMap,faceUv).rgb,float3(.2126,.7152,.0722));
                float4 combined=FaceCubicRepeat(FaceCombinedMap,v.Uv);
                float baseValue=min((forward+1)*.5,.999);
                float exponentBase=1.5-FaceSdfParameters.y;
                float exponent=exponentBase*exponentBase*exponentBase;
                // Compatible at zero and at a tiny negative input caused by float
                // projection roundoff; x^0=1 and 0^nonzero=0, even negative powers.
                float threshold=FaceShapePower(baseValue,exponent);
                float lower=threshold-FaceSdfParameters.x, upper=threshold+FaceSdfParameters.x;
                float t=upper==lower?0:saturate((lightmap-lower)/(upper-lower));
                float soft=t*t*(3-2*t);
                float alphaGate=combined.a>.55?1:0;
                float extra=saturate(((1-threshold)-.71)/(.8-.71))*saturate((combined.a-alphaGate)*5);
                float band=saturate(soft*combined.g+extra);
                if(EyePosition.w<1) band=lerp(1,band,EyePosition.w);
                if(GroundDirection.w<1) visibility=lerp(1,visibility,GroundDirection.w);
                return band*visibility;
            }
            """;
        string source = GenshinSurfaceSource(native, gi, textureMorph, subTexture);
        const string anchor = "SurfaceOutput VSSurface(Input v) {";
        if (!source.Contains(anchor, StringComparison.Ordinal))
            throw new InvalidOperationException("Genshin soft face shader anchor missing.");
        source = source.Replace(anchor, helper + "\n" + anchor, StringComparison.Ordinal);
        foreach (string visibility in new[] { "1", "visibility" })
        {
            source = source.Replace($"GenshinLightResponse(dot(n,l),{visibility})", $"FaceSdfLightResponse(v,l,{visibility})", StringComparison.Ordinal);
            source = source.Replace($"GenshinLightResponse(dot(normalize(v.Normal),IncomingLight.xyz),{visibility})", $"FaceSdfLightResponse(v,IncomingLight.xyz,{visibility})", StringComparison.Ordinal);
        }
        return source;
    }
}
