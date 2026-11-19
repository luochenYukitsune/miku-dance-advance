namespace Mmd.Modern.Rendering.D3D12;

internal sealed partial class D3D12RayGiTemporalPass
{
    // Independently authored for the world-triangle GI ABI. Algorithm references: Falcor 759aad0
    // SVGFReproject (valid-tap renormalization, capped history) and Microsoft e5975f9 RTAO
    // temporal blend (local signal clamp). No upstream shader or framework code is copied.
    internal const string Source="""
        struct Triangle { float4 P0;float4 P1;float4 P2;float4 N0U0;float4 N1U1;float4 N2U2;float4 V012;float4 SphereUv01;float4 SphereUv2;uint4 Meta; };
        cbuffer TemporalConstants:register(b0) {
            uint4 Dimensions;uint4 Control;row_major float4x4 PreviousViewProjection;
            float4 PreviousEye;float4 PreviousForwardPixel;
        };
        StructuredBuffer<Triangle> Triangles:register(t0);
        StructuredBuffer<Triangle> PreviousTriangles:register(t1);
        StructuredBuffer<float4> Input:register(t2);
        Texture2D<float4> Position:register(t3);
        Texture2D<float4> Normal:register(t4);
        StructuredBuffer<float4> PreviousSignal:register(t5);
        StructuredBuffer<float4> PreviousMoments:register(t6);
        StructuredBuffer<float4> PreviousPosition:register(t7);
        StructuredBuffer<float4> PreviousNormal:register(t8);
        RWStructuredBuffer<float4> Output:register(u0);
        RWStructuredBuffer<float4> Moments:register(u1);
        RWStructuredBuffer<float4> SavedPosition:register(u2);
        RWStructuredBuffer<float4> SavedNormal:register(u3);
        bool Unit(float3 value,out float3 result) {
            result=0;if(!all(isfinite(value)))return false;
            float scale=max(abs(value.x),max(abs(value.y),abs(value.z)));
            if(!(scale>0))return false;
            value/=scale;float magnitude=length(value);
            if(!(magnitude>0)||!isfinite(magnitude))return false;
            result=value/magnitude;return all(isfinite(result));
        }
        bool Connected(Triangle a,Triangle b) {
            return all(a.P0.xyz==b.P0.xyz)||all(a.P0.xyz==b.P1.xyz)||all(a.P0.xyz==b.P2.xyz)
                ||all(a.P1.xyz==b.P0.xyz)||all(a.P1.xyz==b.P1.xyz)||all(a.P1.xyz==b.P2.xyz)
                ||all(a.P2.xyz==b.P0.xyz)||all(a.P2.xyz==b.P1.xyz)||all(a.P2.xyz==b.P2.xyz);
        }
        bool ValidId(float id,out uint index) {
            index=0;if(!isfinite(id)||id<1||id>Dimensions.z||floor(id)!=id)return false;
            index=(uint)id-1;return true;
        }
        bool Guide(int2 pixel,out float3 p,out float3 n,out uint id,out Triangle tri) {
            p=0;n=0;id=0;tri=(Triangle)0;
            float4 position=Position.Load(int3(pixel,0)),normal=Normal.Load(int3(pixel,0));
            if(!all(isfinite(position))||position.w<0||position.w>1||!ValidId(normal.w,id)||!Unit(normal.xyz,n))return false;
            tri=Triangles[id];p=position.xyz;
            return tri.Meta.x<Dimensions.w&&tri.Meta.z==1&&all(isfinite(tri.P0.xyz))&&all(isfinite(tri.P1.xyz))&&all(isfinite(tri.P2.xyz));
        }
        bool Barycentric(float3 p,Triangle tri,out float3 bary,out float3 face) {
            bary=0;face=0;float3 a=tri.P1.xyz-tri.P0.xyz,b=tri.P2.xyz-tri.P0.xyz,c=p-tri.P0.xyz;
            float scale=max(max(abs(a.x),max(abs(a.y),abs(a.z))),max(abs(b.x),max(abs(b.y),abs(b.z))));
            if(!(scale>0)||!isfinite(scale))return false;
            a/=scale;b/=scale;c/=scale;
            if(!all(isfinite(c))||!Unit(cross(a,b),face))return false;
            float aa=dot(a,a),bb=dot(b,b),ab=dot(a,b),ac=dot(a,c),bc=dot(b,c),denominator=aa*bb-ab*ab;
            if(!isfinite(denominator)||denominator<=max(aa*bb*1e-7,1e-20))return false;
            float y=(bb*ac-ab*bc)/denominator,z=(aa*bc-ab*ac)/denominator;
            bary=float3(1-y-z,y,z);
            return all(isfinite(bary))&&all(bary>=-.002)&&all(bary<=1.002)&&abs(dot(c,face))<.002;
        }
        float Blend(float a,float b,float t) { return clamp(a+(b-a)*t,min(a,b),max(a,b)); }
        float3 Blend3(float3 a,float3 b,float t) {return float3(Blend(a.x,b.x,t),Blend(a.y,b.y,t),Blend(a.z,b.z,t));}
        float BoundedLuminance(float3 c) {
            float scale=max(c.x,max(c.y,c.z));if(!(scale>0))return 0;
            float luminance=scale*dot(c/scale,float3(.2126,.7152,.0722));
            return luminance/(1+luminance);
        }
        [numthreads(8,8,1)]
        void CSMain(uint3 thread:SV_DispatchThreadID) {
            if(any(thread.xy>=Dimensions.xy))return;
            uint index=thread.y*Dimensions.x+thread.x;int2 pixel=int2(thread.xy);
            float4 current=Input[index];Output[index]=current;
            SavedPosition[index]=Position.Load(int3(pixel,0));SavedNormal[index]=Normal.Load(int3(pixel,0));Moments[index]=0;
            if(!all(isfinite(current))||any(current.rgb<0))return;
            float3 p,n,bary,face;uint id;Triangle tri;
            if(!Guide(pixel,p,n,id,tri)||!Barycentric(p,tri,bary,face))return;
            float lum=BoundedLuminance(current.rgb);Moments[index]=float4(lum,lum*lum,1,0);
            if(Control.x==0)return;
            Triangle previousTri=PreviousTriangles[id];
            if(previousTri.Meta.x!=tri.Meta.x||previousTri.Meta.z!=1)return;
            float3 expected=previousTri.P0.xyz*bary.x+previousTri.P1.xyz*bary.y+previousTri.P2.xyz*bary.z;
            float3 expectedNormal,previousFace,unused;
            if(!Unit(previousTri.N0U0.xyz*bary.x+previousTri.N1U1.xyz*bary.y+previousTri.N2U2.xyz*bary.z,expectedNormal)
                ||!Barycentric(expected,previousTri,unused,previousFace))return;
            // Incident radiance depends on the current receiver hemisphere even when position is unchanged.
            // A sharp normal-only deformation must not inherit the previous hemisphere's lighting.
            if(dot(n,expectedNormal)<.95)return;
            float4 clip=mul(float4(expected,1),PreviousViewProjection);
            if(!all(isfinite(clip))||clip.w<=0)return;
            float3 ndc=clip.xyz/clip.w;if(any(abs(ndc.xy)>1)||ndc.z<0||ndc.z>1)return;
            float2 coordinate=float2(.5+.5*ndc.x,.5-.5*ndc.y)*Dimensions.xy-.5;
            int2 origin=int2(floor(coordinate));float2 fraction=frac(coordinate);
            float expectedDepth=dot(expected-PreviousEye.xyz,PreviousForwardPixel.xyz);
            float footprint=PreviousForwardPixel.w*(Control.y!=0?expectedDepth:1);
            if(!isfinite(footprint)||!(footprint>0))return;
            float tolerance=max(.0001,footprint*1.5/max(abs(dot(previousFace,PreviousForwardPixel.xyz)),.1));
            float3 historical=0;float4 moments=0;float total=0;
            [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++) {
                int2 q=origin+int2(x,y);if(any(q<0)||any(q>=int2(Dimensions.xy)))continue;
                uint j=q.y*Dimensions.x+q.x;float4 oldP=PreviousPosition[j],oldN=PreviousNormal[j];uint oldId;float3 oldNormal;
                if(!all(isfinite(oldP))||oldP.w<0||oldP.w>1||!ValidId(oldN.w,oldId)||!Unit(oldN.xyz,oldNormal))continue;
                Triangle tapTri=PreviousTriangles[oldId];
                if(tapTri.Meta.x!=tri.Meta.x||tapTri.Meta.z!=1||(oldId!=id&&!Connected(previousTri,tapTri))||dot(oldNormal,expectedNormal)<.95)continue;
                // Compare stored previous visibility with the reconstructed PREVIOUS surface depth.
                // Comparing against current depth incorrectly rejects camera/object motion and accepts disocclusion.
                float oldDepth=dot(oldP.xyz-PreviousEye.xyz,PreviousForwardPixel.xyz);
                if(!isfinite(oldDepth)||abs(oldDepth-expectedDepth)>tolerance||distance(oldP.xyz,expected)>tolerance*2)continue;
                float4 signal=PreviousSignal[j],m=PreviousMoments[j];
                if(!all(isfinite(signal))||any(signal.rgb<0)||!all(isfinite(m))||m.z<1||m.z>32)continue;
                float weight=(x!=0?fraction.x:1-fraction.x)*(y!=0?fraction.y:1-fraction.y);
                if(!(weight>0))continue;
                float next=total+weight;historical=Blend3(historical,signal.rgb,weight/next);moments=lerp(moments,m,weight/next);total=next;
            }
            if(total<.01)return;
            float3 minimum=current.rgb,maximum=current.rgb;
            [unroll]for(int y=-2;y<=2;y++)[unroll]for(int x=-2;x<=2;x++) {
                int2 q=pixel+int2(x,y);if(any(q<0)||any(q>=int2(Dimensions.xy)))continue;
                float3 qp,qn;uint qid;Triangle qt;
                if(!Guide(q,qp,qn,qid,qt)||qt.Meta.x!=tri.Meta.x||(qid!=id&&!Connected(tri,qt))||dot(n,qn)<.95)continue;
                float3 delta=qp-p;if(abs(dot(delta,face))>tolerance*.1||length(delta)>tolerance*3)continue;
                float3 sample=Input[q.y*Dimensions.x+q.x].rgb;
                if(!all(isfinite(sample))||any(sample<0))continue;
                minimum=min(minimum,sample);maximum=max(maximum,sample);
            }
            historical=clamp(historical,minimum,maximum);
            float maxHistory=Control.z>0?min((float)Control.z,32.0):32.0;
            float count=min(maxHistory,moments.z+1),alpha=1/count;
            Output[index]=float4(Blend3(historical,current.rgb,alpha),current.a);
            // Compress luminance only for bounded diagnostic moments. Signal RGB remains linear incident radiance.
            float mean=Blend(moments.x,lum,alpha),second=Blend(moments.y,lum*lum,alpha);
            Moments[index]=float4(mean,second,count,max(0,second-mean*mean));
        }
        """;
}
