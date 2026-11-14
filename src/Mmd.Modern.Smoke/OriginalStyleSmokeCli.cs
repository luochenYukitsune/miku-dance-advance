using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D12;
using SkiaSharp;

namespace Mmd.Modern.Smoke;

public sealed record OriginalStyleSmokeOptions(string? ToonDirectory=null,Vector3? IncomingLight=null,
    Vector3? LightAmbient=null,Vector3? LightSpecular=null,bool UseViewSphereRotation=true,
    OriginalTextureSampling Sampling=OriginalTextureSampling.LinearWrap);

/// <summary>Original character surface slice. This CLI is not full original visual compatibility acceptance.</summary>
public static class OriginalStyleSmokeCli
{
    public static OriginalStyleSmokeOptions ParseOptions(string[] args)
    {
        string? Value(string flag) {
            int i=Array.FindIndex(args,a=>a.Equals(flag,StringComparison.OrdinalIgnoreCase));
            if(i<0) return null;
            if(i+1>=args.Length||args[i+1].StartsWith("--",StringComparison.Ordinal)) throw new ArgumentException($"{flag} needs a value.");
            return args[i+1];
        }
        Vector3? Vector(string flag) {
            if(Value(flag) is not {} text) return null;
            var fields=text.Split(',');
            if(fields.Length!=3) throw new ArgumentException($"{flag} needs x,y,z in invariant decimal notation.");
            var parsed=fields.Select(f=>float.Parse(f,CultureInfo.InvariantCulture)).ToArray();
            if(parsed.Any(p=>!float.IsFinite(p))) throw new ArgumentException($"{flag} must be finite.");
            return new(parsed[0],parsed[1],parsed[2]);
        }
        bool view=Value("--sphere-rotation")?.ToLowerInvariant() switch { null or "view"=>true,"identity"=>false,var bad=>throw new ArgumentException($"Unknown --sphere-rotation {bad}; use view or identity.") };
        var sampling=Value("--sampler")?.ToLowerInvariant() switch {
            null or "linear-wrap"=>OriginalTextureSampling.LinearWrap,"linear-clamp"=>OriginalTextureSampling.LinearClamp,
            "point-wrap"=>OriginalTextureSampling.PointWrap,"point-clamp"=>OriginalTextureSampling.PointClamp,
            var bad=>throw new ArgumentException($"Unknown --sampler {bad}.") };
        return new(Value("--toon-dir"),Vector("--light"),Vector("--light-ambient"),Vector("--light-specular"),view,sampling);
    }

    public static int Run(string directory,string modelPath,OriginalStyleSmokeOptions? options=null)
    {
        options??=new();
        string path=Path.GetFullPath(modelPath),modelDirectory=Path.GetDirectoryName(path)!;
        string output=Path.GetFullPath(directory);
        Directory.CreateDirectory(output);
        var sources=new List<SourceIdentity>(); var sourcePaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loads=new List<object>(); var errors=new List<string>();
        void Source(string file,string role) {
            if(!sourcePaths.Add(file)) return;
            var info=new FileInfo(file);sources.Add(new(role,info.FullName,info.Length,Hash(File.ReadAllBytes(file))));
        }
        Source(path,"user PMX read only");
        SceneTexture? LoadAt(string root,string relative,string role) {
            try {
                var image=SceneTextureLoader.Load(root,relative);
                loads.Add(new {role,relative,status=image is null?"missing or unsupported":"loaded",width=image?.Width,height=image?.Height,
                    rgbaSha256=image is null?null:Hash(image.Rgba)});
                if(image is not null) {
                    string normalized=relative.Replace('\\',Path.DirectorySeparatorChar).Replace('/',Path.DirectorySeparatorChar);
                    Source(Path.GetFullPath(Path.Combine(root,normalized)),role+" read only");
                }
                return image;
            } catch(Exception e) when(e is InvalidDataException or IOException or ArgumentException) {
                errors.Add($"{role} {relative}: {e.Message}");return null;
            }
        }
        PmxModel model;
        using(var input=File.OpenRead(path)) model=PmxReader.Read(input);
        string? toonDirectory=options.ToonDirectory is null?null:Path.GetFullPath(options.ToonDirectory);
        var profile=OriginalStyleProfile.SelectedReference with {
            LightAmbient=options.LightAmbient??OriginalStyleProfile.SelectedReference.LightAmbient,
            LightSpecular=options.LightSpecular??OriginalStyleProfile.SelectedReference.LightSpecular,
            TextureSampling=options.Sampling };
        var adapted=PmxOriginalStyleAdapter.FromRestPose(model,p=>LoadAt(modelDirectory,p,"model image"),
            toonDirectory is null?null:i=>LoadAt(toonDirectory,$"toon{i+1:00}.bmp","explicit optional common-toon reference"),profile);
        var mesh=AutoFrame(adapted.Scene.Snapshot,options.IncomingLight);
        var sphereRotation=Matrix4x4.Identity;
        if(options.UseViewSphereRotation) {
            sphereRotation=Matrix4x4.CreateLookAt(mesh.Camera.Eye,mesh.Camera.Target,mesh.Camera.Up);
            sphereRotation.M41=0;sphereRotation.M42=0;sphereRotation.M43=0;
        }
        var scene=new OriginalStyleScene(mesh,adapted.Scene.Materials,profile with {SphereRotation=sphereRotation});
        using var renderer=new D3D12OriginalStyleRenderer();
        var timer=Stopwatch.StartNew();
        var frame=renderer.Render(scene,961,1081);timer.Stop();
        var debug=renderer.LastDebugMessages.ToArray();
        Require(debug.Length==0,"D3D12 debug messages must be empty");
        byte[] bmp=FrameImage.ToBmp(frame);File.WriteAllBytes(Path.Combine(output,"actualOriginalSurface.bmp"),bmp);
        using var bitmap=new SKBitmap(new SKImageInfo(frame.Width,frame.Height,SKColorType.Rgba8888,SKAlphaType.Unpremul));
        Marshal.Copy(frame.Rgba,0,bitmap.GetPixels(),frame.Rgba.Length);
        using var image=SKImage.FromBitmap(bitmap);
        using var png=image.Encode(SKEncodedImageFormat.Png,100);
        byte[] pngBytes=png.ToArray();File.WriteAllBytes(Path.Combine(output,"actualOriginalSurface.png"),pngBytes);
        Require(frame.Rgba.AsSpan().SequenceEqual(renderer.Render(scene,961,1081).Rgba),"same-device repeat must be exact");
        using(var fresh=new D3D12OriginalStyleRenderer())
            Require(frame.Rgba.AsSpan().SequenceEqual(fresh.Render(scene,961,1081).Rgba),"fresh-device repeat must be exact");
        foreach(var asset in sources) Require(new FileInfo(asset.Path).Length==asset.Bytes&&Hash(File.ReadAllBytes(asset.Path))==asset.Sha256,"source asset identity must be unchanged");
        int nonBackground=0,intermediateAlpha=0;
        for(int i=0;i<frame.Rgba.Length;i+=4) {
            if(frame.Rgba[i]!=20||frame.Rgba[i+1]!=24||frame.Rgba[i+2]!=32||frame.Rgba[i+3]!=255) nonBackground++;
            if(frame.Rgba[i+3] is >0 and <255) intermediateAlpha++;
        }
        Require(nonBackground>10000,"selected model must produce visible geometry");
        var camera=mesh.Camera;
        var report=new {
            success=true,defaultAppearance="original-surface-slice",defaultFrame="actualOriginalSurface.png",
            compatibilityLevel="independently implemented surface slice; no full original L2/L3 claim",
            renderer="actual D3D12 indexed character surface, depth, diffuse and sphere GPU sampling; CPU fixed toon colour",
            renderer.AdapterName,renderer.IsWarp,featureLevel=renderer.FeatureLevel.ToString(),
            os=RuntimeInformation.OSDescription,framework=RuntimeInformation.FrameworkDescription,
            sources,textureLoads=loads,textureLoadErrors=errors,diagnostics=adapted.Diagnostics,
            frame=new {frame.Width,frame.Height,frame.Backend,frame.Device,frame.DrawCalls,nonBackgroundPixels=nonBackground,intermediateAlphaPixels=intermediateAlpha,
                uploadedTextures=renderer.LastUploadedTextures,vertices=renderer.LastVertexCount,indices=renderer.LastIndexCount,
                milliseconds=timer.Elapsed.TotalMilliseconds,rawSha256=Hash(frame.Rgba),bmpSha256=Hash(bmp),pngSha256=Hash(pngBytes),debugMessages=debug},
            debugValidation=new {renderer.DebugRequested,renderer.DebugLayerEnabled,renderer.DebugInfoQueueAvailable,
                status=renderer.DebugInfoQueueAvailable?"available; inspected stored messages":"unavailable; empty message list does not prove validation clean"},
            materialCounts=new {total=mesh.Materials.Count,
                pmxDeclaredSphereModes=model.Materials.GroupBy(m=>m.SphereMode).ToDictionary(g=>g.Key.ToString(),g=>g.Count()),
                activeSphereModes=scene.Materials.GroupBy(m=>m.SphereMode).ToDictionary(g=>g.Key.ToString(),g=>g.Count()),
                declaredSphereWithNoTexture=model.Materials.Count(m=>m.SphereMode!=PmxSphereMode.None&&m.SphereTextureIndex<0)},
            profile=new {lightAmbient=V3(scene.Profile.LightAmbient),lightSpecular=V3(scene.Profile.LightSpecular),
                chosenLightParameterSource="explicit CLI values or selected .602 reference scalar; original UI defaults unverified",
                incomingLight=V3(Vector3.Normalize(mesh.LightDirection)),propagationLight=V3(-Vector3.Normalize(mesh.LightDirection)),
                sphereRotationChoice=options.UseViewSphereRotation?"rotation part of selected System.Numerics RH view":"explicit identity",
                sphereRotation=M16(sphereRotation),hostSphereRotationEquivalenceVerified=false,
                sampler=scene.Profile.TextureSampling.ToString(),samplerOriginalHostEquivalenceVerified=false,
                renderTarget="RGBA8_UNorm; no sRGB conversion, exposure, gamma or tone map",sampleCount=1,
                alphaState="opaque/cutout, clip(alpha-cutoff), depth read/write; no alpha blend or translucent sorting",
                cullState="PMX BothFaces=>none; others=>D3D12 back-face cull, counter-clockwise front for PMX coordinates with selected RH SceneCamera; original native camera equivalence unverified",
                selfShadow="in-bounds visibility=1 only, not a claim about original shadow-disabled resources or outside-projection branch",
                morphFactors="default multiply=1/add=0",toonInput=toonDirectory??"no common-toon directory; white fallback",
                commonToonCacheEquivalenceVerified=false},
            materialMapping=mesh.Materials.Select((m,i)=>new {index=i,m.Name,m.StartIndex,m.IndexCount,diffuse=V4(m.Diffuse),ambient=V3(m.Ambient),
                specular=V3(m.Specular),m.Shininess,m.DiffuseTexture,m.AlphaCutoff,
                declaredSphereMode=model.Materials[i].SphereMode.ToString(),declaredSphereTextureIndex=model.Materials[i].SphereTextureIndex,
                egColor=V4(OriginalStyleMath.ResolveHostColors(m,scene.Profile).EgColor),specularPower=V4(OriginalStyleMath.ResolveHostColors(m,scene.Profile).SpecularPower),
                toonShadowColor=V3(scene.Materials[i].ToonShadowColor),scene.Materials[i].SphereTexture,sphereMode=scene.Materials[i].SphereMode.ToString(),scene.Materials[i].DoubleSided}).ToArray(),
            camera=new {eye=V3(camera.Eye),target=V3(camera.Target),up=V3(camera.Up),camera.OrthographicHeight,camera.Near,camera.Far,
                construction="existing selected rest-pose AutoFrame/System.Numerics camera; original host parity unverified"},
            checks=new {sameDeviceExact=true,freshDeviceExact=true,sourceAssetsUnchanged=true,
                vertexSha256=Hash(MemoryMarshal.AsBytes(mesh.Vertices)),indexSha256=Hash(MemoryMarshal.AsBytes(mesh.Indices))},
            giEnabled=false,rayTracingEnabled=false,bloomEnabled=false,mmeEnabled=false,originalExecutableRequired=false,visibleWindowCreated=false,
            limitations=new[] {"Surface fragment slice only; no original EXE screenshot comparison or complete original L2/L3 acceptance",
                "Static PMX rest pose; no motion, bone evaluation, morph or physics","No edge, ground/self shadow, additional-UV sphere mode 3 or attachment technique",
                "Opaque/cutout only; intermediate alpha is written, not blended/sorted",
                "Original camera/matRotate, UI lighting values, sampling and full state parity remain unverified","No swapchain or Avalonia integration in this slice"}
        };
        string json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(Path.Combine(output,"report.json"),json);Console.WriteLine(json);return 0;
    }

    private sealed record SourceIdentity(string Role,string Path,long Bytes,string Sha256);
    private static SceneSnapshot AutoFrame(SceneSnapshot scene,Vector3? light)
    {
        Vector3 min=new(float.MaxValue),max=new(float.MinValue);
        foreach(uint i in scene.Indices) {var p=scene.Vertices[(int)i].Position;min=Vector3.Min(min,p);max=Vector3.Max(max,p);}
        var center=(min+max)*.5f;float span=Math.Max(.1f,Math.Max(max.Y-min.Y,Math.Max(max.X-min.X,max.Z-min.Z)));
        var camera=new SceneCamera(center+new Vector3(0,0,-span*2),center,Vector3.UnitY,span*1.2f,.01f,span*5);
        var framed = scene.WithCamera(camera);
        return light is { } direction ? framed.WithLighting(direction) : framed;
    }
    private static float[] V3(Vector3 v)=>[v.X,v.Y,v.Z];
    private static float[] V4(Vector4 v)=>[v.X,v.Y,v.Z,v.W];
    private static float[] M16(Matrix4x4 m)=>[m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
}
