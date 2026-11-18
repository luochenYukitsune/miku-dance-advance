using Vortice.D3DCompiler;
namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Independent SM5 implementation of the documented SM3 mathematical contract, with selected state.</summary>
internal static partial class OriginalFrameShaders
{
    private const string Source="""
        cbuffer FrameConstants:register(b0) {
            row_major float4x4 World; row_major float4x4 NormalWorld;
            row_major float4x4 ViewProjection; row_major float4x4 SphereRotation;
            row_major float4x4 LightViewProjection;
            float4 EgColor; float4 SpecularPower; float4 IncomingLight; float4 EyePosition;
            float4 ToonShadowColor; float4 Options; float4 ShadowOptions; float4 GroundDirection;
        };
        Texture2D<float4> DiffuseMap:register(t0); Texture2D<float4> SphereMap:register(t1);
        Texture2D<float> ShadowMap:register(t2);
        SamplerState SelectedSampler:register(s0); SamplerState ShadowSampler:register(s1);
        struct Input { float3 Position:POSITION; float3 Normal:NORMAL; float2 Uv:TEXCOORD0; };
        struct SurfaceOutput {
            float4 Position:SV_POSITION; float3 Normal:NORMAL; float2 Uv:TEXCOORD0;
            float3 WorldPosition:TEXCOORD1; float2 SphereProjection:TEXCOORD2; float4 LightClip:TEXCOORD3;
        };
        SurfaceOutput VSSurface(Input v) {
            SurfaceOutput o; float4 p=mul(float4(v.Position,1),World);
            o.Position=mul(p,ViewProjection); o.WorldPosition=p.xyz; o.Normal=mul(float4(v.Normal,0),NormalWorld).xyz;
            o.Uv=v.Uv; o.SphereProjection=mul(float4(v.Normal,0),SphereRotation).xy;
            o.LightClip=ShadowOptions.z>0?mul(float4(v.Position,1),LightViewProjection):float4(0,0,0,1); return o;
        }
        float4 PSSurface(SurfaceOutput v):SV_Target {
            float4 texel=DiffuseMap.Sample(SelectedSampler,v.Uv);
            float4 sphere=Options.y>0?SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)):float4(1,1,1,1);
            float alpha=EgColor.a*texel.a*sphere.a;
            precise float3 baseColor=EgColor.rgb*texel.rgb;
            if(Options.y==1) baseColor=baseColor*sphere.rgb; if(Options.y==2) baseColor=baseColor+sphere.rgb;
            float3 n=normalize(v.Normal); float3 l=IncomingLight.xyz;
            float3 h=normalize(l+normalize(EyePosition.xyz-v.WorldPosition));
            float3 highlight=saturate(pow(max(0,dot(n,h)),SpecularPower.w)*SpecularPower.rgb);
            float3 q=v.LightClip.xyz/v.LightClip.w; float2 uv=float2(.5+.5*q.x,.5-.5*q.y);
            if(uv.x<0||uv.x>1||uv.y<0||uv.y>1) { clip(alpha-Options.x); return float4(baseColor+highlight,alpha); }
            precise float delta=max(q.z-ShadowMap.Sample(ShadowSampler,uv),0);
            precise float weighted=delta*(ShadowOptions.y>0?8000:1500);
            if(ShadowOptions.y>0) weighted=weighted*uv.y;
            precise float visibility=1-saturate(weighted-.3);
            float c=min(saturate(dot(n,l)*3),visibility);
            precise float3 color;
            if(Options.z>0) color=baseColor*(c+ToonShadowColor.rgb*(1-c))+highlight*c;
            else color=baseColor*c+baseColor*ToonShadowColor.rgb*(1-c)+highlight*c;
            if(Options.w>0) alpha=.5; clip(alpha-Options.x); return float4(color,alpha);
        }
        struct DepthOutput { float4 Position:SV_POSITION; float4 LightClip:TEXCOORD0; };
        DepthOutput VSDepth(float3 position:POSITION) { DepthOutput o; o.Position=mul(float4(position,1),LightViewProjection); o.LightClip=o.Position; return o; }
        float4 PSDepth(DepthOutput v):SV_Target { return float4(v.LightClip.z/v.LightClip.w,0,0,1); }
        float4 VSGround(float3 position:POSITION):SV_POSITION {
            float4 p=float4(position.x-position.y*GroundDirection.x/GroundDirection.y,.1,position.z-position.y*GroundDirection.z/GroundDirection.y,1);
            return mul(mul(p,World),ViewProjection);
        }
        float4 PSColor():SV_Target { return EgColor; }
        """;
    // Kept separate so Off compiles exactly the accepted source and entry points.
    private const string GiSource="""
        cbuffer GiSurfaceConstants:register(b1) {
            uint GiTriangleBase; uint GiSurfaceWidth; float GiSurfaceStrength; float GiOcclusionStrength;
            // xyz is the experimental AO allocation; w: additive 0, continuous palette 1, palette bands 2.
            float4 GiAmbientShare;
        };
        StructuredBuffer<float4> SurfaceIndirect:register(t3);
        Texture2D<float4> SurfaceGiPosition:register(t4);
        Texture2D<float4> SurfaceGiNormal:register(t5);
        struct GiGBufferOutput { float4 WorldDepth:SV_Target0; float4 NormalId:SV_Target1; };
        GiGBufferOutput PSGBuffer(SurfaceOutput v,uint primitive:SV_PrimitiveID) {
            float4 texel=DiffuseMap.Sample(SelectedSampler,v.Uv);
            float4 sphere=Options.y>0?SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)):float4(1,1,1,1);
            float alpha=EgColor.a*texel.a*sphere.a;
            clip(alpha-Options.x);clip(alpha-.999);
            GiGBufferOutput o;o.WorldDepth=float4(v.WorldPosition,v.Position.z);
            o.NormalId=float4(normalize(v.Normal),(float)(GiTriangleBase+primitive+1));return o;
        }
        float3 GiOccludableFill(SurfaceOutput v) {
            float3 texel=DiffuseMap.Sample(SelectedSampler,v.Uv).rgb;
            precise float3 fill=EgColor.rgb*GiAmbientShare.rgb*texel;
            if(Options.y==1) fill=fill*SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)).rgb;
            float3 q=v.LightClip.xyz/v.LightClip.w; float2 uv=float2(.5+.5*q.x,.5-.5*q.y);
            if(uv.x<0||uv.x>1||uv.y<0||uv.y>1) return fill;
            precise float delta=max(q.z-ShadowMap.Sample(ShadowSampler,uv),0);
            precise float weighted=delta*(ShadowOptions.y>0?8000:1500);
            if(ShadowOptions.y>0) weighted=weighted*uv.y;
            precise float visibility=1-saturate(weighted-.3);
            float c=min(saturate(dot(normalize(v.Normal),IncomingLight.xyz)*3),visibility);
            if(Options.z>0) return fill*(c+ToonShadowColor.rgb*(1-c));
            return fill*c+fill*ToonShadowColor.rgb*(1-c);
        }
        bool GiToonPaletteDelta(SurfaceOutput v,float3 incident,out float3 increment) {
            increment=0;
            if(GiSurfaceStrength==0 || all(incident==0)) return false;
            float3 q=v.LightClip.xyz/v.LightClip.w; float2 uv=float2(.5+.5*q.x,.5-.5*q.y);
            if(uv.x<0||uv.x>1||uv.y<0||uv.y>1) return false;
            precise float delta=max(q.z-ShadowMap.Sample(ShadowSampler,uv),0);
            precise float weighted=delta*(ShadowOptions.y>0?8000:1500);
            if(ShadowOptions.y>0) weighted=weighted*uv.y;
            precise float visibility=1-saturate(weighted-.3);
            float c=min(saturate(dot(normalize(v.Normal),IncomingLight.xyz)*3),visibility);
            if(c==1) return false;
            precise float3 toonFactor=c+ToonShadowColor.rgb*(1-c);
            precise float3 diffuseLit=EgColor.rgb*DiffuseMap.Sample(SelectedSampler,v.Uv).rgb;
            if(Options.y==1) diffuseLit=diffuseLit*SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)).rgb;
            // ReferenceRadiance is fixed at 1 in the current linear-UNorm numerical convention.
            // Add sphere and specular retain their original contributions outside this budget.
            if(GiAmbientShare.w==2) {
                // A shared neutral scalar follows the original palette direction. Five fixed
                // fill levels consume at most 25% of its dark-to-lit contrast; texture RGB is untouched.
                float luminance=dot(max(incident,0),float3(.2126,.7152,.0722));
                float mapped=saturate(GiSurfaceStrength*luminance/1.0);
                float fill=floor(mapped*4+.5)*.0625;
                increment=diffuseLit*max(1-toonFactor,0)*fill;
                return any(increment!=0);
            }
            increment=diffuseLit*max(1-toonFactor,0)*saturate(GiSurfaceStrength*max(incident,0)/1.0);
            return any(increment!=0);
        }
        float4 PSSurfaceGi(SurfaceOutput v,uint primitive:SV_PrimitiveID):SV_Target {
            float4 color=PSSurface(v);
            int3 pixel=int3((int2)v.Position.xy,0);
            float4 receiver=SurfaceGiNormal.Load(pixel);
            float depth=SurfaceGiPosition.Load(pixel).w;
            if(color.a>=.999 && receiver.w==(float)(GiTriangleBase+primitive+1) && depth==v.Position.z) {
                float4 transport=SurfaceIndirect[pixel.y*GiSurfaceWidth+pixel.x];
                if(GiOcclusionStrength>0) color.rgb-=GiOccludableFill(v)*((1-transport.a)*GiOcclusionStrength);
                if(GiAmbientShare.w>=1) {
                    float3 increment;
                    if(!GiToonPaletteDelta(v,transport.rgb,increment)) return color;
                    color.rgb+=increment;return color;
                }
                color.rgb+=transport.rgb*GiSurfaceStrength;
            }
            return color;
        }
        """;
    public static byte[] Compile(string entry,bool vertex)=>Compiler.Compile(Source,entry,"OriginalFrame.hlsl",vertex?"vs_5_1":"ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray();
    internal static byte[] CompileGi(string entry,bool vertex)=>Compiler.Compile(Source+GiSource,entry,"OriginalFrameGi.hlsl",vertex?"vs_5_1":"ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray();
    // Native direct lights are a separate variant. Their shadow atlas never changes the
    // primary MMD shadow map or the independently optional GI transport/composition.
    private static readonly string NativeSource=NativeSourceTemplate.Replace("NATIVE_LIGHT_MODEL",NativeLightModel.Hlsl,StringComparison.Ordinal);
    private const string NativeSourceTemplate="""
        struct NativeLight {
            float4 PositionKind; float4 DirectionRange; float4 ColorIntensity; float4 ConeShadow; float4 AreaRightTile;
        };
        cbuffer NativeLightConstants:register(b2) {
            uint4 NativeInfo; float4 NativeDiffuse;
        };
        cbuffer NativeSharedLightConstants:register(b0,space1) {
            float4 NativeAtlasLayout;
            NativeLight NativeLights[32];
            row_major float4x4 NativeShadowMatrices[768];
        };
        Texture2D<float> NativeShadowAtlas:register(t6);
        NATIVE_LIGHT_MODEL
        #ifndef NATIVE_LIGHT_RESPONSE
        #define NATIVE_LIGHT_RESPONSE(v,n,l) saturate(dot(n,l)*3)
        #endif
        // Additional lights are summed into an 8-bit target. A hard clip makes a light's
        // shadow vanish once the other lights already saturate the receiver: lit and shadowed
        // pixels both become the ceiling. Below the knee the accepted additive arithmetic is
        // unchanged; above it a C1-continuous rational shoulder approaches the room
        // asymptotically, so a pixel receiving less light always stays strictly darker.
        static const float NativeHighlightKnee=.6;
        float NativeHighlightShoulder(float t) {
            if(t<=NativeHighlightKnee) return t;
            float u=(t-NativeHighlightKnee)/(1-NativeHighlightKnee);
            return NativeHighlightKnee+(1-NativeHighlightKnee)*u/(1+u);
        }
        // One scale for all channels keeps the added light's hue. Channels already at or
        // above their ceiling clip regardless and do not drive the scale.
        float NativeSoftAddScale(float3 room,float3 addition) {
            if(!all(isfinite(room)) || !all(isfinite(addition)) || any(addition<0)) return 1;
            float t=0;
            if(room.x>0) t=max(t,addition.x/room.x);
            if(room.y>0) t=max(t,addition.y/room.y);
            if(room.z>0) t=max(t,addition.z/room.z);
            if(t<=NativeHighlightKnee || !isfinite(t)) return 1;
            return NativeHighlightShoulder(t)/t;
        }
        float3 NativeAddSoft(float3 color,float3 addition) {
            float scale=NativeSoftAddScale(max(1-color,0),addition);
            return scale==1?color+addition:color+scale*addition;
        }
        SurfaceOutput VSNativeDepth(Input v) {
            SurfaceOutput o=VSSurface(v);
            o.Position=mul(mul(float4(v.Position,1),World),LightViewProjection);
            return o;
        }
        float PSNativeDepth(SurfaceOutput v):SV_Target {
            float alpha=EgColor.a*DiffuseMap.Sample(SelectedSampler,v.Uv).a;
            if(Options.y>0) alpha*=SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)).a;
            // Native shadows cover opaque/cutout geometry. Coloured/translucent transmission
            // is not implemented; material alpha and animated texture alpha both participate.
            clip(alpha-max(Options.x,.5));
            return v.Position.z;
        }
        uint NativePointFace(float3 fromLight) {
            float3 a=abs(fromLight);
            if(a.x>=a.y && a.x>=a.z) return fromLight.x>=0?0:1;
            if(a.y>=a.z) return fromLight.y>=0?2:3;
            return fromLight.z>=0?4:5;
        }
        NativeLight NativeSample(NativeLight light,uint sample) {
            if(light.PositionKind.w==3) {
                float2 sign=float2((sample&1)?1:-1,(sample&2)?1:-1);
                float3 up=cross(light.AreaRightTile.xyz,light.DirectionRange.xyz);
                light.PositionKind.xyz+=(light.AreaRightTile.xyz*(sign.x*light.ConeShadow.x)+up*(sign.y*light.ConeShadow.y))*.5773502691896258;
                light.ColorIntensity.w*=.25;
                if(light.ConeShadow.z>=0) light.ConeShadow.z+=sample*6;
            }
            return light;
        }
        float NativeVisibility(uint index,NativeLight light,float3 position,float3 normal,float3 towardLight,float3 worldDx,float3 worldDy,uint sample) {
            if(light.ConeShadow.z<0 || NativeInfo.w==0) return 1;
            uint face=(light.PositionKind.w==1||light.PositionKind.w==3)?NativePointFace(position-light.PositionKind.xyz):0;
            uint tile=(uint)light.ConeShadow.z+face;
            // Bias the receiver in world units before projection, then compare real geometry
            // depth. This is independent of the original PMM projective shadow convention.
            float offset=light.PositionKind.w==0?light.ConeShadow.w:max(light.DirectionRange.w*.00015,.0001);
            float4 clipPosition=mul(float4(position+normal*offset,1),NativeShadowMatrices[tile]);
            if(clipPosition.w<=0) return 1;
            float3 q=clipPosition.xyz/clipPosition.w;
            float2 uv=float2(.5+.5*q.x,.5-.5*q.y);
            if(any(uv<0)||any(uv>1)||q.z<0||q.z>1) return 1;
            bool area=light.PositionKind.w==3;
            int tileSize=area?256:512;
            uint atlasTile=(uint)light.AreaRightTile.w+face;
            uint columns=(uint)NativeAtlasLayout.x;
            int2 origin=int2(atlasTile%columns,atlasTile/columns)*512;
            if(area) origin+=int2(sample&1,sample>>1)*tileSize;
            int2 pixel=(int2)(uv*tileSize);
            float2 receiverDepthGradient=0;
            if(NativeInfo.z>0) {
                // Each PCF tap lies on a different point of a sloping receiver. Transform the
                // pre-loop world derivatives through THIS selected cube face, rather than ddx(q),
                // whose neighbouring pixel may have selected another face. The common /clip.w
                // cancels when solving this 2x2 projected derivative Jacobian.
                float4 tx=mul(float4(worldDx,0),NativeShadowMatrices[tile]);
                float4 ty=mul(float4(worldDy,0),NativeShadowMatrices[tile]);
                float3 dx=tx.xyz-q*tx.w,dy=ty.xyz-q*ty.w;
                float determinant=dx.x*dy.y-dx.y*dy.x;
                float scale=max(abs(dx.x*dy.y)+abs(dx.y*dy.x),1e-20);
                if(abs(determinant)>scale*1e-5) {
                    float2 gradient=float2(dx.z*dy.y-dx.y*dy.z,dx.x*dy.z-dx.z*dy.x)/determinant;
                    gradient*=float2(2,-2); // NDC to texture UV, including the flipped Y.
                    if(all(isfinite(gradient))) receiverDepthGradient=gradient;
                }
            }
            if(area) {
                // Contact-hardening filtering integrates each quadrature stratum instead of
                // leaving four disconnected hard silhouettes. The blocker/receiver distance
                // ratio is reconstructed in range-normalized cube-view Z to avoid far^2 overflow.
                float receiverZ=.001/max(1-q.z*.999,1e-6);
                float span=min(max(light.ConeShadow.x,light.ConeShadow.y)/light.DirectionRange.w,4);
                float searchRadius=clamp(span*tileSize*.25/max(receiverZ,1e-4),1,24);
                float blockers=0,blockerZ=0;
                [unroll] for(int by=-1;by<=1;by++) [unroll] for(int bx=-1;bx<=1;bx++) {
                    int2 local=clamp((int2)(uv*tileSize+float2(bx,by)*searchRadius),int2(0,0),int2(tileSize-1,tileSize-1));
                    float expected=q.z+dot(receiverDepthGradient,((float2)local+.5)/tileSize-uv);
                    float depth=NativeShadowAtlas.Load(int3(origin+local,0));
                    if(depth<expected-.00002) { blockers++;blockerZ+=.001/max(1-depth*.999,1e-6); }
                }
                if(blockers==0) return 1;
                blockerZ/=blockers;
                float radius=clamp(span*tileSize*.25*max(receiverZ-blockerZ,0)/max(receiverZ*blockerZ,1e-6),1,32);
                float soft=0;
                [unroll] for(int tap=0;tap<16;tap++) {
                    float angle=tap*2.399963229728653;
                    float2 offset=float2(cos(angle),sin(angle))*sqrt((tap+.5)/16)*radius;
                    int2 local=clamp((int2)(uv*tileSize+offset),int2(0,0),int2(tileSize-1,tileSize-1));
                    float expected=q.z+dot(receiverDepthGradient,((float2)local+.5)/tileSize-uv);
                    soft+=(expected-.00002<=NativeShadowAtlas.Load(int3(origin+local,0)))?1.0:0.0;
                }
                return soft/16;
            }
            // A 3x3 depth comparison filter softens only the geometric shadow boundary.
            // Clamp within each face so adjacent lights cannot leak across atlas tiles.
            float visibility=0;
            [unroll] for(int y=-1;y<=1;y++) [unroll] for(int x=-1;x<=1;x++) {
                int2 local=clamp(pixel+int2(x,y),int2(0,0),int2(tileSize-1,tileSize-1));
                int2 p=origin+local;
                // Include the receiver's fractional texel position and the clamped tap centre.
                // Merely adding x/y over tileSize leaves a changing half-texel self-shadow error.
                float expected=q.z+dot(receiverDepthGradient,((float2)local+.5)/tileSize-uv);
                visibility+=(expected-.00002<=NativeShadowAtlas.Load(int3(p,0)))?1.0:0.0;
            }
            return visibility/9;
        }
        // Existing specialised character sources retain their original comparison, as do all
        // materials outside the explicit scene response (NativeInfo.z=0).
        float NativeVisibility(uint index,NativeLight light,float3 position,float3 normal,float3 towardLight) {
            return NativeVisibility(index,light,position,normal,towardLight,0,0,0);
        }
        float3 NativeDirect(SurfaceOutput v) {
            // Derivatives must be evaluated before per-light face selection or divergent continue.
            float3 worldDx=ddx(v.WorldPosition),worldDy=ddy(v.WorldPosition);
            float3 n=normalize(v.Normal);
            float3 texel=DiffuseMap.Sample(SelectedSampler,v.Uv).rgb;
            float3 diffuse=NativeDiffuse.rgb*texel;
            if(Options.y==1) diffuse*=SphereMap.Sample(SelectedSampler,float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5)).rgb;
            float3 result=0;
            if(NativeAtlasLayout.z>0) result=diffuse*NativeAtlasLayout.z;
            [loop] for(uint i=0;i<(uint)NativeInfo.x;i++) {
                if((NativeInfo.y & (1u<<i))==0) continue;
                NativeLight source=NativeLights[i];
                [loop] for(uint sample=0;sample<(source.PositionKind.w==3?4u:1u);sample++) {
                NativeLight light=NativeSample(source,sample);
                float3 delta=light.PositionKind.xyz-v.WorldPosition;
                float distanceSquared=max(dot(delta,delta),.0001);
                float3 l=light.PositionKind.w==0?-light.DirectionRange.xyz:delta*rsqrt(distanceSquared);
                float attenuation=NativeLightAttenuation(light.PositionKind,light.DirectionRange,light.ConeShadow,distanceSquared,l);
                // Each light passes through the same light/shadow response as the main light: the
                // original three-times-clamped toon, or the selected style's band (see NATIVE_LIGHT_RESPONSE).
                float toon=NATIVE_LIGHT_RESPONSE(v,n,l);
                if(toon<=0 || attenuation<=0) continue;
                float visibility=NativeVisibility(i,light,v.WorldPosition,n,l,worldDx,worldDy,sample);
                result+=diffuse*light.ColorIntensity.rgb*(light.ColorIntensity.w*attenuation*toon*visibility);
                }
            }
            return result;
        }
        float4 PSSurfaceNative(SurfaceOutput v):SV_Target {
            float4 color=PSSurface(v); color.rgb=NativeAddSoft(color.rgb,NativeDirect(v)); return color;
        }
        float4 PSSurfaceGiNative(SurfaceOutput v,uint primitive:SV_PrimitiveID):SV_Target {
            float4 color=PSSurfaceGi(v,primitive); color.rgb=NativeAddSoft(color.rgb,NativeDirect(v)); return color;
        }
        """;
    internal static byte[] CompileNative(string entry,bool vertex)=>Compiler.Compile(Source+GiSource+NativeSource,entry,"OriginalFrameNative.hlsl",vertex?"vs_5_1":"ps_5_1",ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness).ToArray();
}
