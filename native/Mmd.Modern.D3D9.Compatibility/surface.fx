// Independently authored MDA SM3 port of the in-repository mathematical surface contract.
// Execution evidence is scoped by the native build/validation manifest. No extracted MMD shader/resource content.
// Sources: OriginalStyleShaders.cs, OriginalFrameShaders.cs and OriginalFrameTextureMorphShaders.cs.
// Accessory parameter composition follows the pinned MIT C++ renderer; no extracted FX resources.
// Row-vector convention. Positions/world-lighting normals come from the evaluated snapshot;
// a separate stream preserves raw local normals for the sphere-map projection.
row_major float4x4 matWorld;
row_major float4x4 matNormalWorld;
row_major float4x4 matWorldViewProj;
row_major float4x4 matRotate;
row_major float4x4 matLightViewProj; // Frozen Core warp; accessory-local inputs include accessory world here.
float4 EgColor;
float4 SpcColor;    // rgb = host lightSpecular * material specular; w = resolved shininess.
float4 LightDir;    // xyz = incoming normalized L, NOT light propagation direction.
float4 Place;       // xyz = actual world-space eye position.
float4 ToonColor;   // FINAL binding toon colour, after host ResolveTextureMorphToon.
float4 ShaderOptions; // x=cutoff, y=0/1/2 or UV1 3(multiply)/4(inherited add), z=has diffuse, w=half-alpha override.
float4 TexCMul;
float4 TexCAdd;
float4 SphCMul;
float4 SphCAdd;
// Retained host ABI names. They do not supersede the resolved contract above.
float4 DifColor;
float parthf;
float4 ShadowOptions; // x=receive map, y=partial-far; z=solid ground alpha from edge stream.
float4 GroundDirection;
float4 AccessoryOptions; // x=single sphere, y=has diffuse, z=has sphere, w=ambient colour factor.
row_major float4x4 matWRotate;
row_major float4x4 matObjectWorld;
sampler2D ShadowSampler : register(s0);
bool spadd;
bool transp;

// Device-bound samplers. No sampler_state Texture assignment: host owns the bindings/state.
sampler2D DiffuseSampler : register(s1);
sampler2D SphereSampler : register(s2);

struct Input { float3 Position:POSITION0; float3 Normal:NORMAL0; float2 Uv:TEXCOORD0; float3 SphereNormal:TEXCOORD5; };
struct UvInput {
    float3 Position:POSITION0; float3 Normal:NORMAL0; float2 Uv:TEXCOORD0;
    float4 Uv1:TEXCOORD1; float4 Uv2:TEXCOORD2; float4 Uv3:TEXCOORD3; float4 Uv4:TEXCOORD4;
    float3 SphereNormal:TEXCOORD5;
};
struct SurfaceOutput {
    float4 Position:POSITION0;
    float3 Normal:TEXCOORD0;
    float2 Uv:TEXCOORD1;
    float3 WorldPosition:TEXCOORD2;
    float2 SphereProjection:TEXCOORD3;
    float2 AdditionalUv:TEXCOORD4;
    float4 LightClip:TEXCOORD6;
};
SurfaceOutput SurfaceVertex(float3 position,float3 normal,float2 uv,float3 sphereNormal,float2 additionalUv) {
    SurfaceOutput o;
    float4 worldPosition=mul(float4(position,1),matWorld);
    o.Position=mul(float4(position,1),matWorldViewProj);
    o.WorldPosition=worldPosition.xyz;
    o.LightClip=mul(float4(position,1),matLightViewProj);
    o.Normal=mul(float4(normal,0),matNormalWorld).xyz;
    o.Uv=uv;
    o.AdditionalUv=additionalUv;
    // Preserve raw input-normal projection in VS, before interpolation; do NOT normalize here.
    o.SphereProjection=mul(float4(sphereNormal,0),matRotate).xy;
    return o;
}
SurfaceOutput VSMain(Input v) { return SurfaceVertex(v.Position,v.Normal,v.Uv,v.SphereNormal,float2(0,0)); }
SurfaceOutput VSSubTexture(UvInput v) { return SurfaceVertex(v.Position,v.Normal,v.Uv,v.SphereNormal,v.Uv1.xy); }
float3 MorphDiffuse(float3 sampleColor) {
    if(ShaderOptions.z==0) return sampleColor;
    float alpha=TexCMul.w+TexCAdd.w;
    float3 scaled=sampleColor*TexCMul.rgb;
    float3 biased=scaled+TexCAdd.rgb;
    float3 weighted=biased*alpha;
    return weighted+(1-alpha);
}
float3 MorphSphere(float3 sampleColor,bool additive) {
    float alpha=SphCMul.w+SphCAdd.w;
    float3 scaled=sampleColor*SphCMul.rgb;
    float3 biased=scaled+SphCAdd.rgb;
    float3 weighted=biased*alpha;
    if(additive) return weighted;
    return weighted+(1-alpha);
}
float OriginalVisibility(float4 lightClip,out bool inside) {
    inside=true;
    if(ShadowOptions.x==0) return 1;
    float3 q=lightClip.xyz/lightClip.w;
    float2 uv=float2(.5+.5*q.x,.5-.5*q.y);
    inside=all(uv>=0)&&all(uv<=1);
    if(!inside) return 1;
    float delta=max(q.z-tex2D(ShadowSampler,uv).r,0);
    float weighted=delta*(ShadowOptions.y>0?8000:1500);
    if(ShadowOptions.y>0) weighted=weighted*uv.y;
    return 1-saturate(weighted-.3);
}
float4 PSMain(SurfaceOutput v):COLOR0 {
    float4 texel=ShaderOptions.z>0?tex2D(DiffuseSampler,v.Uv):float4(1,1,1,1);
    float2 sphereUv=ShaderOptions.y>=3?v.AdditionalUv:float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5);
    float4 sphere=ShaderOptions.y>0?tex2D(SphereSampler,sphereUv):float4(1,1,1,1);
    // Alpha comes from RAW sampled alpha, independently of texture-colour factor W.
    float alpha=EgColor.a*texel.a*sphere.a;
    float3 baseColor=EgColor.rgb*MorphDiffuse(texel.rgb);
    if(ShaderOptions.y==1) baseColor=baseColor*MorphSphere(sphere.rgb,false);
    if(ShaderOptions.y==2) baseColor=baseColor+MorphSphere(sphere.rgb,true);
    if(ShaderOptions.y>=3) {
        if(ShaderOptions.y==4 && ShaderOptions.z>0) baseColor+=MorphSphere(sphere.rgb,true);
        else baseColor*=MorphSphere(sphere.rgb,false);
    }
    float3 n=normalize(v.Normal);
    float3 l=LightDir.xyz;
    float3 h=normalize(l+normalize(Place.xyz-v.WorldPosition));
    float3 highlight=saturate(pow(max(0,dot(n,h)),SpcColor.w)*SpcColor.rgb);
    bool inside;
    float visibility=OriginalVisibility(v.LightClip,inside);
    if(!inside) { clip(alpha-ShaderOptions.x); return float4(baseColor+highlight,alpha); }
    float c=min(saturate(dot(n,l)*3),visibility);
    // Only the two-map UV1 technique uses Toon W in alpha; the no-diffuse
    // sibling always multiplies colour and preserves the raw sampled alpha.
    if(ShaderOptions.y>=3 && ShaderOptions.z>0 && c<1) alpha*=c+ToonColor.w*(1-c);
    if(ShaderOptions.w>0) alpha=.5;
    clip(alpha-ShaderOptions.x);
    float3 result;
    if(ShaderOptions.z>0) result=baseColor*(c+ToonColor.rgb*(1-c))+highlight*c;
    else result=baseColor*c+baseColor*ToonColor.rgb*(1-c)+highlight*c;
    return float4(result,alpha);
}
// Standard technique handles remain available to the host's original flag cache.
struct DepthOutput { float4 Position:POSITION0; float4 LightClip:TEXCOORD0; };
DepthOutput VSDepth(float3 position:POSITION0) {
    DepthOutput o;o.Position=mul(float4(position,1),matLightViewProj);o.LightClip=o.Position;return o;
}
float4 PSDepth(DepthOutput v):COLOR0 { return float4(v.LightClip.z/v.LightClip.w,0,0,1); }
struct SolidOutput { float4 Position:POSITION0; float4 Color:COLOR0; };
SolidOutput VSSolid(float3 position:POSITION0,float4 color:COLOR0) {
    SolidOutput o;o.Position=mul(float4(position,1),matWorldViewProj);o.Color=color;return o;
}
SolidOutput VSSolidPlain(float3 position:POSITION0) {
    SolidOutput o;o.Position=mul(float4(position,1),matWorldViewProj);o.Color=1;return o;
}
float4 PSSolid(SolidOutput v):COLOR0 { return float4(EgColor.rgb,ShadowOptions.z>0?v.Color.a*.64999998:EgColor.a); }
SurfaceOutput VSAccessory(Input v) {
    SurfaceOutput o=SurfaceVertex(v.Position,v.Normal,v.Uv,v.Normal,float2(0,0));
    o.WorldPosition=mul(float4(v.Position,1),matObjectWorld).xyz;
    o.SphereProjection=mul(float4(v.Normal,0),matWRotate).xy;
    return o;
}
float4 PSAccessory(SurfaceOutput v):COLOR0 {
    float4 texel=AccessoryOptions.y>0?tex2D(DiffuseSampler,v.Uv):float4(1,1,1,1);
    float2 sphereUv=float2(.5*v.SphereProjection.x+.5,-.5*v.SphereProjection.y+.5);
    float4 sphere=AccessoryOptions.z>0?(AccessoryOptions.x>0?tex2D(DiffuseSampler,sphereUv):tex2D(SphereSampler,sphereUv)):float4(1,1,1,1);
    float alpha=EgColor.a*texel.a*sphere.a;clip(alpha-ShaderOptions.x);
    float3 n=normalize(v.Normal),l=LightDir.xyz;
    float3 h=normalize(l+normalize(Place.xyz-v.WorldPosition));
    float3 highlight=saturate(pow(max(0,dot(n,h)),SpcColor.w)*SpcColor.rgb);
    bool inside;float visibility=OriginalVisibility(v.LightClip,inside);
    float lit=max(0,dot(n,l))*visibility;
    float3 base=(EgColor.rgb+DifColor.rgb*lit)*texel.rgb;
    if(AccessoryOptions.z>0) base=spadd?base+sphere.rgb:base*sphere.rgb;
    return float4(base+highlight*visibility,alpha);
}
// The helpers have standard ColorRender flags (0) in MMHack's existing
// unknown-handle fallback and use only the original XYZ+DIFFUSE or XYZ input.
technique MdaSolidTec { pass Solid { VertexShader=compile vs_3_0 VSSolid(); PixelShader=compile ps_3_0 PSSolid(); } }
technique MdaSolidPlainTec { pass Solid { VertexShader=compile vs_3_0 VSSolidPlain(); PixelShader=compile ps_3_0 PSSolid(); } }

technique ColorRenderTec < int MMDstdtechflags=0x00000000; > {
 pass Surface { VertexShader=compile vs_3_0 VSMain(); PixelShader=compile ps_3_0 PSMain(); }
}
technique ZValuePlotTec < int MMDstdtechflags=0x00000000; > {
 pass Depth { VertexShader=compile vs_3_0 VSDepth(); PixelShader=compile ps_3_0 PSDepth(); }
}
technique BufferShadowTec < int MMDstdtechflags=0x00000000; > {
 pass Surface { VertexShader=compile vs_3_0 VSMain(); PixelShader=compile ps_3_0 PSMain(); }
}
technique BShadowTextureTec < int MMDstdtechflags=0x00000001; > {
 pass Surface { VertexShader=compile vs_3_0 VSMain(); PixelShader=compile ps_3_0 PSMain(); }
}
technique BShadowSphiaTec < int MMDstdtechflags=0x00000100; > {
 pass Surface { VertexShader=compile vs_3_0 VSMain(); PixelShader=compile ps_3_0 PSMain(); }
}
technique BShadowSphiaTextureTec < int MMDstdtechflags=0x00000101; > {
 pass Surface { VertexShader=compile vs_3_0 VSMain(); PixelShader=compile ps_3_0 PSMain(); }
}
technique BShadowTexCd2Tec < int MMDstdtechflags=0x00010100; > {
 pass SurfaceUv1 { VertexShader=compile vs_3_0 VSSubTexture(); PixelShader=compile ps_3_0 PSMain(); }
}
technique BShadowTextureTexCd2Tec < int MMDstdtechflags=0x00010101; > {
 pass SurfaceUv1 { VertexShader=compile vs_3_0 VSSubTexture(); PixelShader=compile ps_3_0 PSMain(); }
}
// Old packet accessory draws retain their explicit fixed-function call path;
// FRM5 shadow-enabled accessory draws enter these independent SM3 techniques.
technique DiffuseBufferShadowTec < int MMDstdtechflags=0x01000000; > {
 pass Accessory { VertexShader=compile vs_3_0 VSAccessory(); PixelShader=compile ps_3_0 PSAccessory(); }
}
technique DiffuseBSTextureTec < int MMDstdtechflags=0x01000001; > {
 pass Accessory { VertexShader=compile vs_3_0 VSAccessory(); PixelShader=compile ps_3_0 PSAccessory(); }
}
technique DiffuseBSSphiaTec < int MMDstdtechflags=0x01000100; > {
 pass Accessory { VertexShader=compile vs_3_0 VSAccessory(); PixelShader=compile ps_3_0 PSAccessory(); }
}
technique DiffuseBSSphiaTexTec < int MMDstdtechflags=0x01000101; > {
 pass Accessory { VertexShader=compile vs_3_0 VSAccessory(); PixelShader=compile ps_3_0 PSAccessory(); }
}
