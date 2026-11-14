namespace Mmd.Modern.Rendering.D3D12;

internal static partial class OriginalFrameShaders
{
    // Independently authored ordinary-PMX adapter. The supplied MBTs materials
    // separate HSV/base-palette and shadow-response branches; no graph, code or
    // assets are incorporated here, and this is not their complete palette path.
    private const string PmxMaterialTreatmentHelper = """
        float3 PmxMaterialSaturation(float3 rgb) {
            float saturation=PmxMaterialTreatmentParams.x;
            float contrast=PmxMaterialTreatmentParams.y;
            // Identity must retain the accepted arithmetic exactly. Signed artist
            // colors have no declared HSV meaning and explicitly pass through.
            if(saturation==1 && contrast==0) return rgb;
            if(any(rgb<0)) return rgb;
            float value=max(rgb.x,max(rgb.y,rgb.z));
            float chroma=value-min(rgb.x,min(rgb.y,rgb.z));
            // Scaling HSV S moves every channel away from V by the same factor.
            // Capping S, rather than RGB/V, preserves positive HDR and hue.
            if(saturation!=1 && chroma!=0) {
                float factor=min(saturation,value/chroma);
                rgb=value+(rgb-value)*factor;
            }
            // This bounded value curve leaves HDR and signed artist colors alone.
            // All channels share one scale, preserving palette hue and saturation.
            if(contrast!=0 && all(rgb>=0) && value>0 && value<=1)
                rgb*=((1-contrast)+contrast*value);
            return rgb;
        }
        // MDA procedural ordinary-PMX fallback. UV derivatives supply a strand
        // direction; no authored game masks, source-game parity or physically
        // accurate energy response are implied.
        float3 PmxGenericHair(SurfaceOutput v,float3 n,float3 h,float3 l,float3 baseColor) {
            float3 dpdx=ddx(v.WorldPosition), dpdy=ddy(v.WorldPosition);
            float2 duvdx=ddx(v.Uv), duvdy=ddy(v.Uv);
            // Common scales preserve direction and make the guards independent
            // of pixel resolution, world scale and authored UV density.
            float uvScale=max(max(abs(duvdx.x),abs(duvdx.y)),max(abs(duvdy.x),abs(duvdy.y)));
            if(uvScale<=0) return 0;
            float2 ux=duvdx/uvScale, uy=duvdy/uvScale;
            float determinant=ux.x*uy.y-ux.y*uy.x;
            if(abs(determinant)<1e-8) return 0;
            float3 tangent=dpdy*ux.x-dpdx*uy.x;
            float tangentScale=max(abs(tangent.x),max(abs(tangent.y),abs(tangent.z)));
            if(tangentScale<=0) return 0;
            tangent/=tangentScale;
            tangent-=n*dot(n,tangent);
            float length2=dot(tangent,tangent);
            if(length2<1e-12) return 0;
            tangent*=rsqrt(length2);
            float tangentHalf=dot(tangent,h);
            float lobe=1-tangentHalf*tangentHalf;
            float band=smoothstep(1-PmxMaterialTreatmentParams.w,1,lobe)*saturate(dot(n,l))*step(0,dot(n,h));
            // Preserve tint while bounding the optional decor even for HDR.
            float3 tint=max(baseColor,0)/max(1,max(baseColor.r,max(baseColor.g,baseColor.b)));
            return tint*PmxMaterialTreatmentParams.z*band;
        }
        """;

    private static string ApplyPmxMaterialTreatment(string source, bool native, bool gi)
    {
        static string ReplaceRequired(string text, string anchor, string replacement)
        {
            if (!text.Contains(anchor, StringComparison.Ordinal))
                throw new InvalidOperationException("Ordinary PMX treatment shader anchor missing: " + anchor);
            return text.Replace(anchor, replacement, StringComparison.Ordinal);
        }

        // Transform the complete generated assignment, after MorphDiffuse and
        // after the light-budget helper has copied its visible lit endpoint.
        // Sphere operations following the assignment remain independent.
        static string TreatAssignments(string text, string assignment)
        {
            int at = text.IndexOf(assignment, StringComparison.Ordinal);
            if (at < 0)
                throw new InvalidOperationException("Ordinary PMX treatment diffuse endpoint missing: " + assignment);
            var result = new System.Text.StringBuilder(text.Length + 128);
            int copied = 0;
            while (at >= 0)
            {
                int start = at + assignment.Length, end = text.IndexOf(';', start);
                if (end < 0)
                    throw new InvalidOperationException("Ordinary PMX treatment endpoint is incomplete: " + assignment);
                result.Append(text, copied, start - copied).Append("PmxMaterialSaturation(")
                    .Append(text, start, end - start).Append(')');
                copied = end;
                at = text.IndexOf(assignment, end + 1, StringComparison.Ordinal);
            }
            return result.Append(text, copied, text.Length - copied).ToString();
        }

        source = ReplaceRequired(source, "float4 GroundDirection;", "float4 GroundDirection; float4 PmxMaterialTreatmentParams;");
        const string helperAnchor = "float4 PSSurface(SurfaceOutput v):SV_Target";
        source = ReplaceRequired(source, helperAnchor, PmxMaterialTreatmentHelper + "\n" + helperAnchor);
        source = TreatAssignments(source, "precise float3 baseColor=");
        const string highlightAnchor = "float3 highlight=saturate(pow(max(0,dot(n,h)),SpecularPower.w)*SpecularPower.rgb);";
        source = ReplaceRequired(source, highlightAnchor,
            highlightAnchor + " if(PmxMaterialTreatmentParams.z>0) highlight+=PmxGenericHair(v,n,h,l,baseColor);");
        if (native || gi)
        {
            source = TreatAssignments(source, "precise float3 diffuseLit=");
            const string fillStart = "precise float3 fill=EgColor.rgb*GiAmbientShare.rgb*";
            int start = source.IndexOf(fillStart, StringComparison.Ordinal);
            int end = start < 0 ? -1 : source.IndexOf(';', start);
            if (start < 0 || end < 0)
                throw new InvalidOperationException("Ordinary PMX treatment GI fill endpoint is missing.");
            string texel = source[(start + fillStart.Length)..end];
            // Keep the legacy identity expression and multiplication order intact.
            // Active palette treatment consumes the post-morph visible endpoint before ambient
            // allocation, so nonuniform allocation cannot change its hue/value.
            source = source.Insert(end + 1,
                " if(PmxMaterialTreatmentParams.x!=1 || PmxMaterialTreatmentParams.y!=0) fill=PmxMaterialSaturation(EgColor.rgb*" + texel + ")*GiAmbientShare.rgb;");
        }
        if (native) source = TreatAssignments(source, "float3 diffuse=");
        return source;
    }
}
