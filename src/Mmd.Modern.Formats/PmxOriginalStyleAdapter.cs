using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Formats;

public sealed record PmxOriginalStyleAdaptationResult(OriginalStyleScene Scene,IReadOnlyList<string> Diagnostics);

/// <summary>Static PMX character rest pose to a format-neutral surface contract. Assets are caller owned.</summary>
public static class PmxOriginalStyleAdapter
{
    /// <summary>Distinct authored texture tokens that <see cref="FromRestPose"/> may request, in request order.
    /// Out-of-range identities are omitted; the adapter still reports them when it reaches them.</summary>
    public static IReadOnlyList<string> RequestedTextures(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var result=new List<string>();var seen=new HashSet<int>();
        void Add(int index) {if(index>=0&&index<model.Textures.Count&&seen.Add(index))result.Add(model.Textures[index]);}
        foreach(var m in model.Materials) {
            Add(m.TextureIndex);
            if(m.SphereMode!=PmxSphereMode.None)Add(m.SphereTextureIndex);
            if(m.ToonMode==PmxToonMode.Separate)Add(m.ToonTextureIndex);
        }
        return result.AsReadOnly();
    }

    public static PmxOriginalStyleAdaptationResult FromRestPose(PmxModel model,Func<string,SceneTexture?>? loadTexture=null,
        Func<int,SceneTexture?>? loadCommonToon=null,OriginalStyleProfile? profile=null,bool initialSubTextureAdditive=false,
        bool retainToonTextures=false,Func<SceneTexture?>? loadDefaultToon=null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var vertices=new SceneVertex[model.Vertices.Count];
        for(int i=0;i<vertices.Length;i++) vertices[i]=new(model.Vertices[i].Position,model.Vertices[i].Normal,model.Vertices[i].Uv);
        SceneAdditionalUvs? additionalUvs=null;
        int channelCount=model.Header.AdditionalUvCount;
        if(channelCount is <0 or >4)throw new ArgumentException("PMX declares zero to four additional UV channels.");
        if(channelCount>0) {
            var values=new SceneAdditionalUvVertex[vertices.Length];
            for(int i=0;i<values.Length;i++) {
                var uv=model.Vertices[i].AdditionalUvs;
                if(uv.Count!=channelCount)throw new ArgumentException($"Vertex {i}: additional UV count differs from the PMX header.");
                values[i]=new(uv[0],channelCount>1?uv[1]:default,channelCount>2?uv[2]:default,channelCount>3?uv[3]:default);
            }
            additionalUvs=new(channelCount,values);
        }
        var indices=new uint[checked(model.Faces.Count*3)];
        for(int i=0;i<model.Faces.Count;i++) {
            var t=model.Faces[i];
            if(t.A<0||t.B<0||t.C<0) throw new ArgumentException("Negative PMX vertex index.");
            indices[i*3]=(uint)t.A;indices[i*3+1]=(uint)t.B;indices[i*3+2]=(uint)t.C;
        }
        var diagnostics=new List<string> {
            "PMX rest pose only: no bone, SDEF/QDEF animation, morph or physics evaluation.",
            "Original surface slice: default morph factors; in-bounds self-shadow visibility=1; no edge/ground/self shadow, transparency sorting or attachment techniques.",
            "Camera, sphere rotation and sampler are explicit selected inputs; original host camera and UI lighting defaults are unverified." };
        var textures=new List<SceneTexture>();
        var loaded=new Dictionary<int,SceneTexture?>(); var bound=new Dictionary<int,int>(); var common=new Dictionary<int,SceneTexture?>();
        var boundCommon=new Dictionary<int,int>(); SceneTexture? defaultToon=null; bool loadedDefault=false; int boundDefault=-1;
        SceneTexture? Load(int index,string role) {
            if(index==-1) return null;
            if(index<0||index>=model.Textures.Count) throw new ArgumentException($"Invalid PMX {role} texture identity {index}.");
            if(loaded.TryGetValue(index,out var cached)) return cached;
            var image=loadTexture?.Invoke(model.Textures[index]); loaded[index]=image;
            if(image is null) diagnostics.Add($"PMX {role} '{model.Textures[index]}' not loaded; missing-image fallback.");
            return image;
        }
        int Bind(int index,string role) {
            if(bound.TryGetValue(index,out int cached)) return cached;
            var image=Load(index,role);
            if(image is null) return -1;
            int result=textures.Count;textures.Add(image);bound[index]=result;return result;
        }
        var materials=new SceneMaterial[model.Materials.Count]; var bindings=new OriginalStyleMaterialBinding[materials.Length]; int next=0;
        bool subTextureAdditive=initialSubTextureAdditive;
        for(int i=0;i<materials.Length;i++) {
            var m=model.Materials[i];
            if(m.SphereMode==PmxSphereMode.SubTexture&&additionalUvs is null) throw new NotSupportedException($"Material {i}: PMX sphere mode 3 requires declared UV1 coordinates.");
            if(!Enum.IsDefined(m.SphereMode)) throw new ArgumentException("Invalid PMX sphere mode.");
            if((m.DrawMode&(PmxDrawModeFlags.DrawPoint|PmxDrawModeFlags.DrawLine))!=0) throw new NotSupportedException($"Material {i}: PMX point/line drawing is unsupported by this triangle surface slice.");
            int diffuse=Bind(m.TextureIndex,"diffuse");
            int sphere=m.SphereMode==PmxSphereMode.None?-1:Bind(m.SphereTextureIndex,"sphere");
            OriginalSphereMode mode=sphere<0?OriginalSphereMode.None:(OriginalSphereMode)m.SphereMode;
            if(sphere>=0&&m.FaceVertexCount>0) {
                if(mode==OriginalSphereMode.Multiply)subTextureAdditive=false;
                if(mode==OriginalSphereMode.Add)subTextureAdditive=true;
            }
            if(mode==OriginalSphereMode.SubTexture)diagnostics.Add($"Material {i}: SubTexture uses UV1.xy and explicit inherited additive={subTextureAdditive}; cross-model/other draw-entry state must be supplied by the host.");
            SceneTexture? toon; int toonTexture=-1;
            if(m.ToonMode==PmxToonMode.Common) {
                if(m.ToonTextureIndex is <0 or >9) throw new ArgumentException("PMX common toon index outside 0..9.");
                if(!common.TryGetValue(m.ToonTextureIndex,out toon)) {toon=loadCommonToon?.Invoke(m.ToonTextureIndex);common[m.ToonTextureIndex]=toon;}
                diagnostics.Add($"Material {i}: common toon {m.ToonTextureIndex} {(toon is null?"missing; white fallback":"explicit user reference bitmap, bottom-left RGB/256; original common-cache equivalence unverified")}.");
                if(retainToonTextures&&toon is not null) {
                    if(!boundCommon.TryGetValue(m.ToonTextureIndex,out toonTexture)) {
                        toonTexture=textures.Count;textures.Add(toon);boundCommon[m.ToonTextureIndex]=toonTexture;
                    }
                }
            } else if(m.ToonMode==PmxToonMode.Separate) {
                toon=Load(m.ToonTextureIndex,"toon");
                if(retainToonTextures&&m.ToonTextureIndex==-1) {
                    if(!loadedDefault) {defaultToon=loadDefaultToon?.Invoke();loadedDefault=true;}
                    toon=defaultToon;
                    if(toon is not null) {
                        if(boundDefault<0) {boundDefault=textures.Count;textures.Add(toon);}
                        toonTexture=boundDefault;
                    }
                } else if(retainToonTextures&&toon is not null) toonTexture=Bind(m.ToonTextureIndex,"toon");
            }
            else throw new ArgumentException("Invalid PMX toon mode.");
            if(retainToonTextures&&m.FaceVertexCount>0&&(toon is null||toon.Kind!=SceneTextureKind.Image)) {
                string identity=m.ToonMode==PmxToonMode.Common?$"common toon {m.ToonTextureIndex+1:00}"
                    :m.ToonTextureIndex==-1?"original default toon (slot 0)":$"authored toon '{model.Textures[m.ToonTextureIndex]}'";
                throw new NotSupportedException($"Material {i}: Classic rendering requires the actual {identity} image. Supply that resource explicitly; no replacement ramp is generated.");
            }
            if(toon is null) diagnostics.Add($"Material {i}: no toon image; white shadow-colour fallback (1,1,1).");
            if(m.FaceVertexCount<0||m.FaceVertexCount>indices.Length-next) throw new ArgumentException("PMX material range outside indices.");
            materials[i]=new(m.Name,next,m.FaceVertexCount,m.Diffuse,m.Specular,m.Ambient,m.SpecularPower,diffuse,toonTexture);
            // The original effect's ToonColor remains white for reference -1, independently
            // of the slot-0 image consumed by the fixed-function pass.
            bindings[i]=new(toon is null||m.ToonMode==PmxToonMode.Separate&&m.ToonTextureIndex==-1
                ?Vector3.One:OriginalStyleMath.ReadToonShadowColor(toon),sphere,mode,(m.DrawMode&PmxDrawModeFlags.BothFaces)!=0) {SubTextureAdditive=subTextureAdditive};
            next+=m.FaceVertexCount;
        }
        return new(new(new(vertices,indices,materials,textures,additionalUvs:additionalUvs),bindings,profile),diagnostics.AsReadOnly());
    }
}
