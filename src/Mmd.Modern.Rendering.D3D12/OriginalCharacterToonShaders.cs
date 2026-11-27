using Vortice.D3DCompiler;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Independent modern-character palette response. Accepted shader literals and ordinary entry points stay intact.</summary>
internal static partial class OriginalFrameShaders
{
    private const string CharacterToonHelper = """
        // Only the palette blend is remapped. Raw texture alpha, highlight, geometry and
        // the original dark/light colour endpoints keep their established contracts.
        float CharacterMainToon(float combinedLight) {
            if(IncomingLight.w<=0) return combinedLight;
            float lower=ShadowOptions.w-IncomingLight.w*.5;
            float t=saturate((combinedLight-lower)/IncomingLight.w);
            return t*t*(3-2*t);
        }
        """;

    internal static string CharacterToonSource(bool native,bool gi,bool textureMorph,bool subTexture)
    {
        string source=Source+(native||gi?GiSource:"")+(native?NativeSource:"");
        if(subTexture)source=OriginalSubTextureShader.Apply(source,true,native,gi);
        else if(textureMorph)source=OriginalTextureMorphShader.Apply(source);

        static string ReplaceRequired(string text,string anchor,string replacement)
        {
            if(!text.Contains(anchor,StringComparison.Ordinal))
                throw new InvalidOperationException("Character toon shader anchor missing: "+anchor);
            return text.Replace(anchor,replacement,StringComparison.Ordinal);
        }
        const string main="float c=min(saturate(dot(n,l)*3),visibility);";
        source=ReplaceRequired(source,main,main+" float originalC=c; c=CharacterMainToon(c);");
        if(native||gi)
        {
            // Both AO's occludable fill and GI's remaining dark-to-lit budget must use
            // the same palette response as the visible surface.
            const string fill="float c=min(saturate(dot(normalize(v.Normal),IncomingLight.xyz)*3),visibility);";
            source=ReplaceRequired(source,fill,fill+" c=CharacterMainToon(c);");
        }
        source=ReplaceRequired(source,"+highlight*c;","+highlight*originalC;");
        if(subTexture)
            source=ReplaceRequired(source,
                "if(Options.y>=3 && Options.z>0 && c<1) alpha*=c+ToonShadowColor.w*(1-c);",
                "if(Options.y>=3 && Options.z>0 && originalC<1) alpha*=originalC+ToonShadowColor.w*(1-originalC);");
        int insertion=source.IndexOf("struct Input",StringComparison.Ordinal);
        if(insertion<0)throw new InvalidOperationException("Character toon shader input declaration is missing.");
        return source.Insert(insertion,CharacterToonHelper+"\n");
    }

    internal static byte[] CompileCharacterToon(string entry,bool native,bool gi,bool textureMorph,bool subTexture,bool clustered=false)
    {
        string source=CharacterToonSource(native,gi,textureMorph,subTexture);
        if(clustered&&native) source=OriginalClusteredLightShader.Apply(source);
        return D3D12FxcShaderCache.Compile(source,entry,
            clustered?"OriginalFrameCharacterToonClustered.hlsl":"OriginalFrameCharacterToon.hlsl","ps_5_1",
            ShaderFlags.EnableStrictness|ShaderFlags.IeeeStrictness);
    }
}

public sealed partial class D3D12OriginalStyleRenderer
{
    private readonly Dictionary<(string Entry,bool Native,bool Gi,bool Morph,bool SubTexture,bool Clustered),byte[]> characterToonFrameShaders=[];

    private byte[] GetCharacterToonFrameShader(string entry,bool native,bool gi,bool textureMorph,bool subTexture,bool clustered=false)
    {
        var key=(entry,native,gi,textureMorph,subTexture,clustered);
        if(characterToonFrameShaders.TryGetValue(key,out var found))return found;
        var compiled=OriginalFrameShaders.CompileCharacterToon(entry,native,gi,textureMorph,subTexture,clustered);
        runtime.ShaderCompilations++;
        characterToonFrameShaders.Add(key,compiled);
        return compiled;
    }
}
