namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Material acceptance, random sequence and radiance are identical for both native intersection backends.</summary>
internal static class D3D12RayGiShaders
{
    internal const string Shared="""
        struct GiTriangle { float4 P0; float4 P1; float4 P2; float4 N0U0; float4 N1U1; float4 N2U2; float4 V012; float4 SphereUv01; float4 SphereUv2; uint4 Meta; };
        struct GiMaterial { float4 DiffuseFactor; float4 Emission; uint4 DiffuseTexture; uint4 SphereTexture; float4 Parameters; uint4 Flags; };
        struct GiBvhNode { float4 BoundsMin; float4 BoundsMax; uint4 Data; };
        struct GiRay { float3 Origin; float TMin; float3 Direction; float TMax; };
        struct GiHit { uint TriangleIndex; float T; float2 Barycentrics; uint FrontFace; };
        cbuffer GiConstants:register(b0) {
            uint4 GiDispatch; uint4 GiCounts; float4 GiLightDirectionDistance;
            float4 GiIrradianceStrength; float4 GiTuning; float4 GiReserved;
            row_major float4x4 GiCamera;
        };
        StructuredBuffer<GiTriangle> GiTriangles:register(t0);
        StructuredBuffer<GiMaterial> GiMaterials:register(t1);
        StructuredBuffer<uint> GiTexels:register(t2);
        StructuredBuffer<GiBvhNode> GiBvhNodes:register(t3);
        StructuredBuffer<uint> GiTriangleOrder:register(t4);
        Texture2D<float4> GiGBufferPosition:register(t5);
        Texture2D<float4> GiGBufferNormal:register(t6);
        #ifdef GI_HARDWARE
        RaytracingAccelerationStructure GiHardwareScene:register(t7);
        #endif
        RWStructuredBuffer<float4> GiIndirect:register(u0);
        RWStructuredBuffer<uint> GiDiagnostics:register(u1);
        void ReportTraversalOverflow() { InterlockedAdd(GiDiagnostics[0],1); }
        void ReportSecondaryHit() {
            #ifdef GI_DIAGNOSTICS
            InterlockedAdd(GiDiagnostics[1],1);
            #endif
        }
        void ReportShadowRay() {
            #ifdef GI_DIAGNOSTICS
            InterlockedAdd(GiDiagnostics[2],1);
            #endif
        }
        float4 UnpackGiTexel(uint value) { return float4(value&255,(value>>8)&255,(value>>16)&255,value>>24)/255.0; }
        int GiCoordinate(int value,int size,bool wrap) { return wrap?((value%size)+size)%size:clamp(value,0,size-1); }
        float4 GiReadTexel(uint4 texture,int2 p,bool wrap) {
            p=int2(GiCoordinate(p.x,(int)texture.y,wrap),GiCoordinate(p.y,(int)texture.z,wrap));
            return UnpackGiTexel(GiTexels[texture.x+p.y*texture.y+p.x]);
        }
        float4 SampleTexture(uint4 texture,float2 uv,uint sampling) {
            bool wrap=sampling==2||sampling==3;
            if(wrap) uv=frac(uv); else uv=saturate(uv);
            if(sampling==0||sampling==2) return GiReadTexel(texture,(int2)floor(uv*texture.yz),wrap);
            float2 coordinate=uv*texture.yz-.5; int2 p=(int2)floor(coordinate); float2 f=frac(coordinate);
            return lerp(lerp(GiReadTexel(texture,p,wrap),GiReadTexel(texture,p+int2(1,0),wrap),f.x),
                lerp(GiReadTexel(texture,p+int2(0,1),wrap),GiReadTexel(texture,p+int2(1,1),wrap),f.x),f.y);
        }
        float3 GiWeights(float2 bary) { return float3(1-bary.x-bary.y,bary.x,bary.y); }
        float2 GiDiffuseUv(GiTriangle tri,float2 bary) {
            float3 w=GiWeights(bary); return float2(dot(w,float3(tri.N0U0.w,tri.N1U1.w,tri.N2U2.w)),dot(w,tri.V012.xyz));
        }
        float2 GiSphereUv(GiTriangle tri,float2 bary) {
            float3 w=GiWeights(bary);
            if(tri.Meta.z==1) {
                float3 n0=tri.SphereUv01.xyz,n1=float3(tri.SphereUv01.w,tri.SphereUv2.xy),n2=float3(tri.SphereUv2.zw,tri.V012.w);
                float2 a=mul(float4(n0,0),GiCamera).xy,b=mul(float4(n1,0),GiCamera).xy,c=mul(float4(n2,0),GiCamera).xy;
                float2 projection=a*w.x+b*w.y+c*w.z;
                return float2(.5*projection.x+.5,-.5*projection.y+.5);
            }
            return tri.SphereUv01.xy*w.x+tri.SphereUv01.zw*w.y+tri.SphereUv2.xy*w.z;
        }
        float3 GetAlbedo(uint triangleIndex,float2 bary) {
            GiTriangle tri=GiTriangles[triangleIndex]; GiMaterial material=GiMaterials[tri.Meta.x];
            return saturate(material.DiffuseFactor.rgb*SampleTexture(material.DiffuseTexture,GiDiffuseUv(tri,bary),material.DiffuseTexture.w).rgb);
        }
        bool AcceptCandidate(uint triangleIndex,float2 bary,float3 rayDirection) {
            GiTriangle tri=GiTriangles[triangleIndex]; GiMaterial material=GiMaterials[tri.Meta.x];
            if(material.Parameters.z==0) return false;
            bool front=dot(cross(tri.P1.xyz-tri.P0.xyz,tri.P2.xyz-tri.P0.xyz),rayDirection)<0;
            if(material.Parameters.y==0&&!front) return false;
            float2 uv=GiDiffuseUv(tri,bary);
            uint cutoffBits=asuint(material.Parameters.x);
            float alpha;
            if((material.Flags.x==1u||material.Flags.x==2u) && cutoffBits>=0x00800000u && cutoffBits<0x7f800000u && all(isfinite(uv))) {
                // Equal decoded texel alphas survive the nested lerps unchanged. Keep the original decoder and multiply.
                float constantAlpha=UnpackGiTexel(GiTexels[material.DiffuseTexture.x]).a;
                alpha=min(material.DiffuseFactor.a,1)*constantAlpha;
            } else {
                alpha=min(material.DiffuseFactor.a,1)*SampleTexture(material.DiffuseTexture,uv,material.DiffuseTexture.w).a;
            }
            if(material.SphereTexture.w!=0) alpha*=SampleTexture(material.SphereTexture,GiSphereUv(tri,bary),material.DiffuseTexture.w).a;
            return alpha>=material.Parameters.x;
        }
        float3 GiGeometricNormal(GiTriangle tri) { return normalize(cross(tri.P1.xyz-tri.P0.xyz,tri.P2.xyz-tri.P0.xyz)); }
        float3 GiShadingNormal(GiTriangle tri,float2 bary,float3 incoming) {
            float3 w=GiWeights(bary); float3 n=normalize(tri.N0U0.xyz*w.x+tri.N1U1.xyz*w.y+tri.N2U2.xyz*w.z);
            if(dot(n,incoming)>0) n=-n; return n;
        }
        uint GiHash(uint value) { value^=value>>16; value*=0x7feb352d; value^=value>>15; value*=0x846ca68b; return value^(value>>16); }
        float GiRandom(uint value) { return (GiHash(value)>>8)*(1.0/16777216.0); }
        float3 GiCosineDirection(float3 n,uint seed) {
            float u=GiRandom(seed),phi=6.28318530718*GiRandom(seed^0x9e3779b9);
            float3 tangent=normalize(cross(abs(n.z)<.999?float3(0,0,1):float3(0,1,0),n));
            return tangent*(sqrt(u)*cos(phi))+cross(n,tangent)*(sqrt(u)*sin(phi))+n*sqrt(1-u);
        }
        float2 GiBarycentricPosition(GiTriangle tri,float3 p) {
            float3 a=tri.P1.xyz-tri.P0.xyz,b=tri.P2.xyz-tri.P0.xyz,c=p-tri.P0.xyz;
            float aa=dot(a,a),ab=dot(a,b),bb=dot(b,b),ca=dot(c,a),cb=dot(c,b),d=aa*bb-ab*ab;
            return float2((bb*ca-ab*cb)/d,(aa*cb-ab*ca)/d);
        }
        float3 GiOffset(float3 p,float3 geometric,float3 direction) { return p+geometric*(dot(geometric,direction)>=0?GiTuning.x:-GiTuning.x); }
        """;

    internal const string Integrator="""
        [numthreads(8,8,1)] void CSMain(uint3 thread:SV_DispatchThreadID) {
            if(any(thread.xy>=GiDispatch.xy)) return;
            float4 receiver=GiGBufferNormal.Load(int3(thread.xy,0));
            uint pixelIndex=thread.y*GiDispatch.x+thread.x;
            if(receiver.w<1) { GiIndirect[pixelIndex]=0; return; }
            uint receiverIndex=(uint)receiver.w-1;
            GiTriangle tri=GiTriangles[receiverIndex]; float3 p=GiGBufferPosition.Load(int3(thread.xy,0)).xyz;
            float3 n=normalize(receiver.xyz),geometric=GiGeometricNormal(tri);
            #ifdef GI_TOON
            float3 sum=0;
            #else
            float3 albedo=GetAlbedo(receiverIndex,GiBarycentricPosition(tri,p)),sum=0;
            #endif
            uint nearHits=0;
            for(uint sampleIndex=0;sampleIndex<GiDispatch.z;sampleIndex++) {
                uint seed=GiHash(thread.x+GiDispatch.x*thread.y)^GiHash(GiDispatch.w)^GiHash(sampleIndex+0x12345);
                float3 direction=GiCosineDirection(n,seed);
                GiRay ray; ray.Origin=GiOffset(p,geometric,direction);ray.TMin=GiTuning.x;ray.Direction=direction;ray.TMax=GiLightDirectionDistance.w;
                GiHit hit;
                if(TraceClosest(ray,hit)) {
                    if(hit.T<=GiTuning.z) nearHits++;
                    ReportSecondaryHit();
                    GiTriangle secondary=GiTriangles[hit.TriangleIndex]; GiMaterial material=GiMaterials[secondary.Meta.x];
                    float3 q=ray.Origin+ray.Direction*hit.T,normal=GiShadingNormal(secondary,hit.Barycentrics,ray.Direction);
                    float3 light=normalize(GiLightDirectionDistance.xyz),outgoing=material.Emission.rgb;
                    float cosine=max(dot(normal,light),0);
                    if(cosine>0) {
                        GiRay shadow;shadow.Origin=GiOffset(q,GiGeometricNormal(secondary),light);shadow.TMin=GiTuning.x;shadow.Direction=light;shadow.TMax=GiLightDirectionDistance.w;
                        ReportShadowRay();
                        if(!TraceAny(shadow)) outgoing+=GetAlbedo(hit.TriangleIndex,hit.Barycentrics)*(GiIrradianceStrength.xyz*(cosine/3.14159265359))*GiTuning.y;
                    }
                    #ifdef GI_TOON
                    sum+=outgoing;
                    #else
                    sum+=albedo*outgoing;
                    #endif
                }
            }
            GiIndirect[pixelIndex]=float4(sum/GiDispatch.z,1-(float)nearHits/GiDispatch.z);
        }
        """;

    private const string DiffuseTextureMorphAlbedo="""
        struct GiDiffuseTextureFactor { float4 Multiply; float4 Add; };
        StructuredBuffer<GiDiffuseTextureFactor> GiDiffuseTextureFactors:register(t8);
        float3 GetAlbedo(uint triangleIndex,float2 bary) {
            GiTriangle tri=GiTriangles[triangleIndex]; GiMaterial material=GiMaterials[tri.Meta.x];
            GiDiffuseTextureFactor factor=GiDiffuseTextureFactors[tri.Meta.x];
            float3 raw=SampleTexture(material.DiffuseTexture,GiDiffuseUv(tri,bary),material.DiffuseTexture.w).rgb;
            precise float weight=factor.Multiply.w+factor.Add.w;
            precise float3 scaled=raw*factor.Multiply.rgb;
            precise float3 biased=scaled+factor.Add.rgb;
            precise float3 weighted=biased*weight;
            precise float3 effective=weighted+(1-weight);
            return saturate(material.DiffuseFactor.rgb*effective);
        }
        """;

    // Position/direction and artistic distance/cone falloff match NativeDirect. Secondary
    // diffuse radiance uses the existing integrator's Lambert cosine/pi, not primary toon.
    // Reference: Microsoft's MIT D3D12RaytracingSimpleLighting hit-position diffuse example;
    // no sample source copied. Visibility is the shared alpha-aware TraceAny implementation.
    private const string NativeLightSource="""
        struct GiNativeLight { float4 PositionKind; float4 DirectionRange; float4 ColorIntensity; float4 ConeShadow; float4 AreaRightTile; };
        StructuredBuffer<GiNativeLight> GiNativeLights:register(t9);
        NATIVE_LIGHT_MODEL
        float3 GiNativeOutgoing(uint triangleIndex,float2 bary,float3 q,float3 normal) {
            float3 incident=0;
            if(GiReserved.z>0) incident=GiReserved.z.xxx;
            uint receiverMask=0xffffffffu;
            if(GiReserved.y>0) receiverMask=asuint(GiNativeLights[(uint)GiReserved.x+GiTriangles[triangleIndex].Meta.x].PositionKind.x);
            [loop] for(uint i=0;i<(uint)GiReserved.x;i++) {
                if((receiverMask & (1u<<i))==0) continue;
                GiNativeLight source=GiNativeLights[i];
                [loop] for(uint sample=0;sample<(source.PositionKind.w==3?4u:1u);sample++) {
                GiNativeLight light=source;
                if(light.PositionKind.w==3) {
                    float2 sign=float2((sample&1)?1:-1,(sample&2)?1:-1);
                    float3 up=cross(light.AreaRightTile.xyz,light.DirectionRange.xyz);
                    light.PositionKind.xyz+=(light.AreaRightTile.xyz*(sign.x*light.ConeShadow.x)+up*(sign.y*light.ConeShadow.y))*.5773502691896258;
                    light.ColorIntensity.w*=.25;
                }
                float3 delta=light.PositionKind.xyz-q;
                float rawDistanceSquared=dot(delta,delta);
                if(light.PositionKind.w!=0 && rawDistanceSquared<1e-12) continue;
                float distanceSquared=max(rawDistanceSquared,.0001);
                float distance=sqrt(rawDistanceSquared);
                float3 toward=light.PositionKind.w==0?-light.DirectionRange.xyz:delta/distance;
                float attenuation=NativeLightAttenuation(light.PositionKind,light.DirectionRange,light.ConeShadow,distanceSquared,toward);
                float cosine=max(dot(normal,toward),0);
                if(cosine<=0 || attenuation<=0) continue;
                if(light.ConeShadow.z>0) {
                    GiRay shadow;shadow.Origin=GiOffset(q,GiGeometricNormal(GiTriangles[triangleIndex]),toward);
                    shadow.Direction=toward;shadow.TMin=GiTuning.x;
                    shadow.TMax=light.PositionKind.w==0?GiLightDirectionDistance.w:max(distance-GiTuning.x,GiTuning.x);
                    ReportShadowRay();
                    if(TraceAny(shadow)) continue;
                }
                // Keep the bounded colour*intensity product before inverse-square amplification.
                // A tiny colour with huge intensity must not overflow an intensity*attenuation intermediate.
                precise float3 irradiance=light.ColorIntensity.rgb*light.ColorIntensity.w;
                precise float angularRadiance=attenuation*(cosine/3.14159265359);
                incident+=irradiance*angularRadiance;
                }
            }
            return GetAlbedo(triangleIndex,bary)*incident*GiTuning.y;
        }
        """;

    private const string SubTextureCoordinates="""
        struct GiSubTextureUv { float4 Uv01; float4 Uv2; };
        StructuredBuffer<GiSubTextureUv> GiSubTextureUvs:register(t10);
        float2 GiSubTextureUvAt(uint triangleIndex,float2 bary) {
            GiSubTextureUv row=GiSubTextureUvs[triangleIndex]; float3 w=GiWeights(bary);
            return row.Uv01.xy*w.x+row.Uv01.zw*w.y+row.Uv2.xy*w.z;
        }
        """;

    internal static string Source(bool hardware,bool textureMorph=false,bool nativeLights=false,bool subTexture=false)
    {
        string integrator=Integrator;
        if(nativeLights)
        {
            const string anchor="float3 light=normalize(GiLightDirectionDistance.xyz),outgoing=material.Emission.rgb;";
            if(!integrator.Contains(anchor,StringComparison.Ordinal))
                throw new InvalidOperationException("The native-light GI variant cannot locate its secondary shading.");
            integrator=integrator.Replace(anchor,anchor+"\n            outgoing+=GiNativeOutgoing(hit.TriangleIndex,hit.Barycentrics,q,normal);",StringComparison.Ordinal);
        }
        string shared=textureMorph?MorphShared():Shared;
        if(subTexture)
        {
            int start=shared.IndexOf("float3 GetAlbedo(",StringComparison.Ordinal);
            const string coordinate="SampleTexture(material.SphereTexture,GiSphereUv(tri,bary),material.DiffuseTexture.w).a";
            if(start<0||!shared.Contains(coordinate,StringComparison.Ordinal))
                throw new InvalidOperationException("The GI SubTexture variant cannot locate its shared visibility function.");
            shared=shared[..start]+SubTextureCoordinates+"\n        "+shared[start..];
            shared=shared.Replace(coordinate,"SampleTexture(material.SphereTexture,material.SphereTexture.w==3?GiSubTextureUvAt(triangleIndex,bary):GiSphereUv(tri,bary),material.DiffuseTexture.w).a",StringComparison.Ordinal);
        }
        return shared+(hardware?D3D12HardwareRayGi.TraceSource:D3D12SoftwareRayGi.TraceSource)
            +(nativeLights?NativeLightSource.Replace("NATIVE_LIGHT_MODEL",NativeLightModel.Hlsl,StringComparison.Ordinal):"")+integrator;
    }

    private static string MorphShared()
    {
        int start=Shared.IndexOf("float3 GetAlbedo(",StringComparison.Ordinal);
        if(start<0)throw new InvalidOperationException("The GI factor variant cannot locate its albedo function.");
        int end=Shared.IndexOf("bool AcceptCandidate(",start,StringComparison.Ordinal);
        if(end<0)throw new InvalidOperationException("The GI factor variant cannot locate its visibility function.");
        // Accepted identity, traversal, opacity and integrator source literals stay unchanged.
        return Shared[..start]+DiffuseTextureMorphAlbedo+"\n        "+Shared[end..];
    }
}
