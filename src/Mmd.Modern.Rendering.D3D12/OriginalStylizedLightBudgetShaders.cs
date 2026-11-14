namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently authored endpoint budgeting. Only newly added native/ToonPalette
    // diffuse light is scaled; the original surface, AO, additive sphere, authored
    // highlight and raw alpha never enter this shared budget.
    private static string ApplyStylizedLightBudget(string source,bool gi)
    {
        static string ReplaceRequired(string text,string anchor,string replacement)
        {
            if(!text.Contains(anchor,StringComparison.Ordinal))
                throw new InvalidOperationException("Stylized light budget shader anchor missing: "+anchor);
            return text.Replace(anchor,replacement,StringComparison.Ordinal);
        }

        // Capture the exact generated endpoint, after MorphDiffuse/MorphSphere and
        // UV1/SubTexture transformations. There must not be a second hand-maintained
        // texture expression which can accidentally include additive sphere or use
        // NativeDiffuse (material diffuse) instead of the visible Eg lit endpoint.
        const string endpointStart="precise float3 diffuseLit=";
        const string endpointEnd="// ReferenceRadiance is fixed at 1";
        int start=source.IndexOf(endpointStart,StringComparison.Ordinal);
        int end=start<0?-1:source.IndexOf(endpointEnd,start,StringComparison.Ordinal);
        if(start<0||end<0)
            throw new InvalidOperationException("Stylized light budget diffuse endpoint is missing.");
        string endpoint=source[start..end].TrimEnd();
        string helper="""
            float3 StylizedRemainingLightBudget(SurfaceOutput v) {
                ENDPOINT
                return diffuseLit*max(1-GenshinPaletteFactor(GenshinPrimaryBlend(v)),0);
            }
            float StylizedAddedLightScale(float3 budget,float3 directIncrement,float3 giIncrement) {
                precise float3 total=directIncrement+giIncrement;
                // Keep the accepted low-light add sequence, including exact-zero
                // channels. A single scale preserves the added direct light's hue.
                if(all(total<=budget)) return 1;
                float scale=1;
                if(total.x>0) scale=min(scale,budget.x/total.x);
                if(total.y>0) scale=min(scale,budget.y/total.y);
                if(total.z>0) scale=min(scale,budget.z/total.z);
                return scale;
            }
            """.Replace("ENDPOINT",endpoint,StringComparison.Ordinal);

        if(gi)
        {
            // Retain the accepted GI function verbatim (including its standalone
            // entry). The native+palette entry uses this copied body to receive AO
            // and the GI increment separately, without subtracting specular from a
            // finished color or evaluating PSSurface twice.
            const string signature="float4 PSSurfaceGi(SurfaceOutput v,uint primitive:SV_PrimitiveID):SV_Target";
            int functionStart=source.IndexOf(signature,StringComparison.Ordinal);
            int brace=functionStart<0?-1:source.IndexOf('{',functionStart);
            if(functionStart<0||brace<0)
                throw new InvalidOperationException("Stylized light budget GI function is missing.");
            int depth=1,position=brace+1;
            for(;position<source.Length&&depth>0;position++)
            {
                if(source[position]=='{')depth++;
                else if(source[position]=='}')depth--;
            }
            if(depth!=0)throw new InvalidOperationException("Stylized light budget GI function is incomplete.");
            string giBase=source[functionStart..position];
            giBase=ReplaceRequired(giBase,signature,
                "float4 StylizedGiSurfaceBase(SurfaceOutput v,uint primitive,out float3 giIncrement,out bool hasGiIncrement)");
            giBase=ReplaceRequired(giBase,"float4 color=PSSurface(v);",
                "giIncrement=0; hasGiIncrement=false; float4 color=PSSurface(v);");
            giBase=ReplaceRequired(giBase,"color.rgb+=increment;return color;",
                "giIncrement=increment; hasGiIncrement=true; return color;");
            giBase=ReplaceRequired(giBase,"color.rgb+=transport.rgb*GiSurfaceStrength;",
                "giIncrement=transport.rgb*GiSurfaceStrength; hasGiIncrement=true;");
            helper+="\n"+giBase;
            source=ReplaceRequired(source,
                "float4 color=PSSurfaceGi(v,primitive); color.rgb+=NativeDirect(v); return color;",
                """
                // Additive GI keeps its accepted independent composition. Only the
                // explicitly selected ToonPalette contract spends a shared budget.
                if(GiAmbientShare.w<1) {
                    float4 color=PSSurfaceGi(v,primitive); color.rgb+=NativeDirect(v); return color;
                }
                float3 giIncrement; bool hasGiIncrement;
                float4 color=StylizedGiSurfaceBase(v,primitive,giIncrement,hasGiIncrement);
                float3 directIncrement=NativeDirect(v);
                float scale=StylizedAddedLightScale(StylizedRemainingLightBudget(v),directIncrement,giIncrement);
                if(scale==1) {
                    if(hasGiIncrement) color.rgb+=giIncrement;
                    color.rgb+=directIncrement;
                } else color.rgb+=scale*(directIncrement+giIncrement);
                return color;
                """);
        }

        source=ReplaceRequired(source,
            "float4 color=PSSurface(v); color.rgb+=NativeDirect(v); return color;",
            """
            float4 color=PSSurface(v);
            float3 directIncrement=NativeDirect(v);
            float scale=StylizedAddedLightScale(StylizedRemainingLightBudget(v),directIncrement,0);
            if(scale==1) color.rgb+=directIncrement;
            else color.rgb+=scale*directIncrement;
            return color;
            """);
        const string insertion="float4 PSSurfaceNative(SurfaceOutput v):SV_Target";
        return ReplaceRequired(source,insertion,helper+"\n"+insertion);
    }
}
