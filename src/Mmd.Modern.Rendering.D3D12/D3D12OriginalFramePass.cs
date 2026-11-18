using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
namespace Mmd.Modern.Rendering.D3D12;

public sealed partial class D3D12OriginalStyleRenderer
{
    private readonly Dictionary<FramePipelineKey,FramePipelines> framePipelines=new();
    private readonly record struct FramePipelineKey(OriginalTextureSampling SurfaceSampling,OriginalTextureSampling ShadowSampling,Format Depth,
        OriginalAlphaBlendMode Blend,bool WriteDepth,bool GroundBlend,bool Stencil,OriginalDepthComparison ShadowComparison,bool Gi=false,bool NativeLights=false,bool TextureMorph=false,bool SubTexture=false,FrameAlphaOutput AlphaOutput=FrameAlphaOutput.LegacyMax,bool CharacterToon=false,bool FaceSdf=false,bool PackedFaceData=false,bool GenshinHair=false,bool StarMcHair=false,bool EndfieldHair=false,bool EndfieldRim=false,bool GenshinAtlas=false,bool EndfieldMatcap=false,bool GenshinMetallic=false,bool GenshinMetallicSpecular=false,bool EndfieldPalette=false,bool EndfieldBaseImage=false,bool StarMcBody=false,bool FrontCounterClockwise=true,bool CharacterDirectPalette=false);
    private sealed record FramePipelines(ID3D12RootSignature Root,ID3D12PipelineState SurfaceBack,ID3D12PipelineState SurfaceBoth,
        ID3D12PipelineState ShadowBack,ID3D12PipelineState ShadowBoth,ID3D12PipelineState Ground,OutlinePipeline Outline,
        ID3D12PipelineState? NativeShadowBack=null,ID3D12PipelineState? NativeShadowBoth=null,int TextureMorphRoot=-1,int FaceSdfRoot=-1,int GenshinHairRoot=-1,int StarMcHairRoot=-1,int EndfieldHairRoot=-1,int EndfieldRimRoot=-1,int GenshinAtlasRoot=-1,int EndfieldMatcapRoot=-1,int GenshinMetallicRoot=-1,int GenshinMetallicSpecularRoot=-1,int EndfieldPaletteRoot=-1,int EndfieldBaseImageRoot=-1,int StarMcBodyRoot=-1,FramePipelineKey Key=default);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FrameConstants(Matrix4x4 World,Matrix4x4 NormalWorld,Matrix4x4 ViewProjection,Matrix4x4 SphereRotation,Matrix4x4 LightViewProjection,
        Vector4 EgColor,Vector4 SpecularPower,Vector4 IncomingLight,Vector4 EyePosition,Vector4 ToonShadowColor,Vector4 Options,Vector4 ShadowOptions,Vector4 GroundDirection,
        Vector4 PmxMaterialTreatmentParams=default,Vector4 StylizedShadeParams=default,Vector4 StylizedTintParams=default,
        Vector4 StylizedWarmParams=default,Vector4 StylizedGradeParams=default);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FaceSdfConstants(Vector4 Forward,Vector4 Right,Vector4 Parameters)
    {
        internal static FaceSdfConstants From(D3D12FaceSdfBinding binding)
        {
            var basis=binding.OrthonormalBasis();
            return new(new(basis.Forward,0),new(basis.Right,0),new(binding.Transition,binding.Offset,0,0));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GenshinHairConstants(Vector4 Parameters)
    {
        internal static GenshinHairConstants From(D3D12GenshinNonMetallicSpecularBinding binding)
            => new(new(binding.GlobalSpecularScale,binding.Shininess,binding.Multiplier,binding.Intensity));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct StarMcHairConstants(Vector4 Parameters,Vector4 Selection)
    {
        internal static StarMcHairConstants From(D3D12StarMcHairBinding binding)
            => new(new(binding.HighlightCoefficient,binding.HighlightBrightness,binding.ShadowCoefficient,binding.ShadowSoftness),
                new(binding.Intensity,binding.HighlightChannel==D3D12HairHighlightChannel.Alpha?1:0,0,0));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GenshinAtlasConstants(Vector4 Layout0,Vector4 Layout1,Vector4 Layout2)
    {
        internal static GenshinAtlasConstants From(D3D12GenshinHairShadowAtlasBinding binding)
            => new(new(binding.ExplicitRowId,binding.DayNight,binding.ShadowPosition,binding.ShadowRampWidth),
                new(binding.VertexWidth,binding.VertexAo,binding.UseLightmapAo,binding.UseVertexColorAo),
                new((float)binding.AtlasEncoding,0,0,0));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EndfieldHairConstants(Vector4 PaletteBrightness,Vector4 ViewDirection)
    {
        internal static EndfieldHairConstants From(D3D12EndfieldHairBinding binding,SceneCamera camera)
            =>new(new(binding.PaletteColor,binding.Brightness),new(Vector3.Normalize(camera.Eye-camera.Target),camera.VerticalFovDegrees is null?1:0));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EndfieldRimConstants(Vector4 BaseRange,Vector4 ColorRadial,Vector4 Parameters,
        Vector4 CameraRight,Vector4 CameraUp,Vector4 CameraForward,Vector4 Incoming)
    {
        internal static EndfieldRimConstants From(D3D12EndfieldRimBinding binding,SceneCamera camera)
        {
            var view=camera.LeftHanded?Matrix4x4.CreateLookAtLeftHanded(camera.Eye,camera.Target,camera.Up)
                :Matrix4x4.CreateLookAt(camera.Eye,camera.Target,camera.Up);
            return new(new(binding.BaseColor,binding.RangeStrength),new(binding.RimColor,binding.RadialStrength),
                new(binding.EdgeSize,binding.EdgeStrength,binding.DirectionRadians,0),
                new(view.M11,view.M21,view.M31,0),new(view.M12,view.M22,view.M32,0),
                new(view.M13,view.M23,view.M33,0),new(Vector3.Normalize(camera.Eye-camera.Target),camera.VerticalFovDegrees is null?1:0));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EndfieldMatcapConstants(Vector4 BaseBaseline,Vector4 HighlightNormal,
        Vector4 RotationGate,Vector4 LightControl,Vector4 CameraRight,Vector4 CameraUp,Vector4 CameraForward,Vector4 Reserved,Vector4 RangeDirectionWidth)
    {
        internal static EndfieldMatcapConstants From(D3D12EndfieldMatcapBinding b,SceneCamera camera)
        {
            var view=camera.LeftHanded?Matrix4x4.CreateLookAtLeftHanded(camera.Eye,camera.Target,camera.Up)
                :Matrix4x4.CreateLookAt(camera.Eye,camera.Target,camera.Up);
            return new(new(b.BaseColor,b.BaselineStrength),new(b.HighlightColor,b.NormalStrength),
                new(b.RotationRadians,b.HighlightCoefficient,b.ShadowHardness,b.CoupleEndfieldPalette?1:0),
                new(b.EffectiveLightControlDirection,0),new(view.M11,view.M21,view.M31,0),
                new(view.M12,view.M22,view.M32,0),new(view.M13,view.M23,view.M33,0),
                b.HighlightRange is {} range?new(1,range.OverallBrightness,range.HighlightBrightness,range.DirectionalShadow.NormalStrength):default,
                b.HighlightRange is {} selected?new(selected.DirectionalShadow.WorldDirection,selected.DirectionalShadow.Width):default);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct StarMcBodyConstants(Vector4 DetailMetalHighlight,Vector4 ShadowWarm,Vector4 ShadowColor,
        Vector4 RimColorStrength,Vector4 RimShape,Vector4 CameraBackward)
    {
        internal static StarMcBodyConstants From(D3D12StarMcBodyBinding b,SceneCamera camera)
            =>new(new(b.DetailStrength,b.MetallicSaturation,b.HighlightCoefficient,b.HighlightStrength),
                new(b.ShadowCoefficient,b.ShadowSoftness,b.ShadowColorStrength,b.WarmBandStrength),new(b.EffectiveShadowColor,0),
                new(b.EffectiveRimColor,b.RimStrength),new(b.RimSize,b.RimDirectionRadians,camera.VerticalFovDegrees is null?1:0,0),
                new(Vector3.Normalize(camera.Eye-camera.Target),0));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GenshinMetallicConstants(Vector4 Parameters,Vector4 DarkLit,Vector4 Light,Vector4 Shadow,Vector4 CameraUp,Vector4 CameraBackward)
    {
        internal static GenshinMetallicConstants From(D3D12GenshinMetallicBinding b,SceneCamera camera)
        {
            var view=camera.LeftHanded?Matrix4x4.CreateLookAtLeftHanded(camera.Eye,camera.Target,camera.Up)
                :Matrix4x4.CreateLookAt(camera.Eye,camera.Target,camera.Up);
            return new(new(b.EnableMetallics,b.TileScale,b.Brightness,(float)b.LitMode),new(b.DarkColor,b.UniformLitFactor),
                new(b.LightColor,0),new(b.ShadowMultiplyColor,0),new(view.M12,view.M22,view.M32,0),
                new(Vector3.Normalize(camera.Eye-camera.Target),camera.VerticalFovDegrees is null?1:0));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GenshinMetallicSpecularConstants(Vector4 ColorShininess,Vector4 SharpScale,Vector4 Options)
    {
        internal static GenshinMetallicSpecularConstants From(D3D12GenshinMetallicSpecularBinding b)
            =>new(new(b.Color,b.Shininess),new(b.SharpColor,b.Scale),new(b.Attenuation,b.SharpOffset,b.RampMix,b.ConsumesRamp?1:0));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EndfieldPaletteConstants(Vector4 Emission,Vector4 Light,Vector4 Modes)
    {
        internal static EndfieldPaletteConstants From(D3D12EndfieldPaletteBinding b,Vector3? primaryRadiance=null)
            =>new(new(b.EmissionStrength,b.UniformEmissionBlue,b.Density,(float)b.EmissionInputMode),
                new(b.ReceivedLightLinearRgb,b.LightInfluence),ModesFor(b,primaryRadiance));
        private static Vector4 ModesFor(D3D12EndfieldPaletteBinding b,Vector3? primaryRadiance)
        {
            var light=b.ReceivedLightInputMode==D3D12EndfieldPaletteInputMode.RendererDiffuse?primaryRadiance??Vector3.One:Vector3.Zero;
            return new((float)b.ReceivedLightInputMode,light.X,light.Y,light.Z);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EndfieldBaseImageConstants(Vector4 BaseContrast,Vector4 ModelShadow,
        Vector4 ShadowTint,Vector4 MultiplierSaturation,Vector4 Options,Vector4 Modes,
        Vector4 SourceDirectionWidth,Vector4 SourceOptions,Vector4 SourceView)
    {
        internal static EndfieldBaseImageConstants From(D3D12EndfieldBaseImageBinding b,SceneCamera camera)
            =>new(new(b.BaseImageLinearRgb,b.Contrast),new(b.ModelColor,b.ShadowFactor),
                new(b.ShadowColor,b.WarmTint),new(b.ColorMultiplier,b.Saturation),
                new(b.DetailBlue,b.ShadowStrength,(float)b.BaseInputMode,(float)b.DetailInputMode),
                new((float)b.ShadowInputMode,0,0,0),
                new(b.DirectionalShadow?.WorldDirection??Vector3.UnitZ,b.DirectionalShadow?.Width??.3f),
                new(b.FacingBase is null?0:1,b.FacingBase?.Blend??0,b.DirectionalShadow is null?0:1,b.DirectionalShadow?.NormalStrength??2),
                new(camera.VerticalFovDegrees is null?Vector3.Normalize(camera.Eye-camera.Target):camera.Eye,camera.VerticalFovDegrees is null?1:0));
    }

    internal sealed record PreparedFrame(ID3D12Resource White,ID3D12Resource Shadow,D3D12GpuCache Cache)
    {
        internal PreparedNativeLights? Native { get; set; }
    }
    // The caller retains every resource through the recording context's fence. This
    // preparation records the current primary map once, before mode3 GI receivers,
    // and the ordinary colour recorder reuses exactly the same pair (including SSAA).
    private int PreparePrimaryShadow(D3D12RecordingContext context,FramePipelines pipeline,OriginalFrameScene frame,
        ID3D12Resource vertexBuffer,ID3D12Resource indexBuffer,FrameConstants common,D3D12GpuCache cache,
        ref PreparedFrame? prepared)
    {
        using var shadowCpu=MeasureSceneCpu(SceneCpuStage.PrimaryShadow);
        if(prepared is not null)return 0;
        BeginVisibility(frame);
        var commands=context.Commands;var scene=frame.Surface.Snapshot;var options=frame.Options!;int draws=0;
        var white=D3D12ShadowTargets.White(context,cache);
        ID3D12Resource shadow=white;
        if(options.SelfShadow is {Generate:true} s)
        {
            var targets=D3D12ShadowTargets.Get(context,cache,"original-primary-shadow",s.Width,s.Height,Format.D24_UNorm_S8_UInt);
            shadow=targets.Color;targets.Begin(context);
            commands.RSSetViewport(0,0,s.Width,s.Height,0,1); commands.RSSetScissorRect(new RawRect(0,0,s.Width,s.Height));
            commands.SetGraphicsRootSignature(pipeline.Root); commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            commands.IASetVertexBuffers(0,new VertexBufferView(vertexBuffer.GPUVirtualAddress,checked(scene.VertexCount*32),32));
            commands.IASetIndexBuffer(new IndexBufferView(indexBuffer.GPUVirtualAddress,checked(scene.Indices.Length*4),Format.R32_UInt));
            commands.SetGraphicsRootConstantBufferView(0,context.UploadConstants(common));
            for(int i=0;i<scene.Materials.Count;i++) {
                var material=scene.Materials[i]; var flags=frame.Materials[i];
                if(!flags.CastSelfShadow||material.IndexCount==0||material.Diffuse.W==.98f) continue;
                bool shadowBoth=flags.ShadowDoubleSided||(flags.UseSceneLighting&&frame.Surface.Materials[i].DoubleSided);
                commands.SetPipelineState(shadowBoth?pipeline.ShadowBoth:pipeline.ShadowBack);
                commands.DrawIndexedInstanced(material.IndexCount,1,material.StartIndex,0,0); draws++;shadowDrawn++;
            }
            targets.End(context);
        }
        prepared=new(white,shadow,cache);
        return draws;
    }

    internal int PrepareGiFrame(D3D12RecordingContext context,OriginalFrameScene frame,Format depthFormat,
        ID3D12Resource vb,ID3D12Resource ib,D3D12GpuCache cache,ref PreparedFrame? prepared)
    {
        if(prepared is not null)return 0;
        var scene=frame.Surface.Snapshot;var options=frame.Options!;var camera=options.CameraOverride??scene.Camera;
        Matrix4x4.Invert(scene.World,out var inverse);
        var common=new FrameConstants(scene.World,Matrix4x4.Transpose(inverse),Matrix4x4.Identity,
            options.SphereRotationOverride??frame.Surface.Profile.SphereRotation,
            options.SelfShadow?.LightViewProjection??Matrix4x4.Identity,Vector4.One,Vector4.Zero,
            new(Vector3.Normalize(scene.LightDirection),0),new(camera.Eye,1),Vector4.One,Vector4.Zero,
            new(0,options.SelfShadow?.PartialFar==true?1:0,options.SelfShadow is null?0:1,0),Vector4.Zero);
        return PreparePrimaryShadow(context,GetFramePipelines(frame,depthFormat,true),frame,vb,ib,common,cache,ref prepared);
    }

    /// <summary>Borrowed recorder. Does not clear caller targets, reset/close lists, submit, wait, or read back.
    /// Caller owns shared heap, targets and all retained resources until its fence completes.</summary>
    private int RecordFrame(ID3D12Device device,ID3D12GraphicsCommandList commands,FramePipelines pipeline,OriginalFrameScene frame,
        int width,int height,ID3D12Resource vertexBuffer,ID3D12Resource indexBuffer,ID3D12DescriptorHeap srvHeap,int srvStride,
        CpuDescriptorHandle rtv,CpuDescriptorHandle dsv,Format depthFormat,FrameBufferUpload upload,Func<ID3D12Resource,ID3D12Resource> own,Action<IDisposable> retain,ref PreparedFrame? prepared,int slotBase,D3D12RecordingContext context,D3D12GpuCache cache,D3D12RayGiRenderer.Prepared? gi=null,D3D12SurfaceStyleMap? surfaceStyles=null)
    {
        var options=frame.Options!; var style=frame.Surface; var scene=style.Snapshot;
        if(options.GroundShadow is {UseStencil:true} && depthFormat!=Format.D24_UNorm_S8_UInt) throw new ArgumentException("Stencil ground requires D24S8 target.");
        var camera=options.CameraOverride??scene.Camera;
        Matrix4x4.Invert(scene.World,out var inverse);
        var common=new FrameConstants(scene.World,Matrix4x4.Transpose(inverse),camera.ViewProjection(width,height),options.SphereRotationOverride??style.Profile.SphereRotation,
            options.SelfShadow?.LightViewProjection??Matrix4x4.Identity,Vector4.One,Vector4.Zero,new(Vector3.Normalize(scene.LightDirection),0),new(camera.Eye,1),
            Vector4.One,Vector4.Zero,new(0,options.SelfShadow?.PartialFar==true?1:0,options.SelfShadow is null?0:1,0),new(options.GroundShadow?.ProjectionDirection??Vector3.UnitY,0));
        void Constants(FrameConstants value) {
            commands.SetGraphicsRootConstantBufferView(0,context.UploadConstants(value));
        }
        void Geometry(ID3D12Resource vb,int stride,int count) {
            commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            commands.IASetVertexBuffers(0,new VertexBufferView(vb.GPUVirtualAddress,checked(count*stride),stride));
            commands.IASetIndexBuffer(new IndexBufferView(indexBuffer.GPUVirtualAddress,checked(scene.Indices.Length*4),Format.R32_UInt));
        }
        int draws=0,whiteSlot=slotBase+scene.Textures.Count+2,realSlot=whiteSlot+1;
        draws+=PreparePrimaryShadow(context,pipeline,frame,vertexBuffer,indexBuffer,common,cache,ref prepared);
        var ready=prepared!;
        device.CreateShaderResourceView(ready.White,null,srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(whiteSlot,srvStride));
        device.CreateShaderResourceView(ready.Shadow,null,srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(realSlot,srvStride));
        bool nativeLights=HasNativeLights(frame);
        int nativeRoot=gi is null?4:7;
        if(nativeLights) {
            using var nativeCpu=MeasureSceneCpu(SceneCpuStage.NativeLights);
            bool firstPreparation=ready.Native is null;
            ready.Native??=RecordNativeLights(context,cache,pipeline,frame,vertexBuffer,indexBuffer,srvHeap,srvStride,slotBase,ready.White,common);
            SetNativeGiParticipation(gi is not null);
            if(firstPreparation)draws+=LastNativeLightStatistics?.ShadowDrawCalls??0;
            device.CreateShaderResourceView(ready.Native.Atlas,null,srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(realSlot+1,srvStride));
            device.CreateConstantBufferView(new ConstantBufferViewDescription {
                BufferLocation=ready.Native.FrameAddress,
                SizeInBytes=(Marshal.SizeOf<NativeFrameConstants>()+255)&~255
            },srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(realSlot+2,srvStride));
        }
        commands.OMSetRenderTargets(rtv,dsv); commands.RSSetViewport(0,0,width,height,0,1); commands.RSSetScissorRect(new RawRect(0,0,width,height));
        commands.SetGraphicsRootSignature(pipeline.Root); commands.SetDescriptorHeaps(srvHeap);
        if(options.GroundShadow is {} ground && ground.ProjectionDirection.Y<0)
        {
            using var groundCpu=MeasureSceneCpu(SceneCpuStage.Ground);
            if(frame.Outline is null||frame.OutlineOptions is null) throw new ArgumentException("Ground shadow requires the independent edge position stream and explicit outline geometry inputs.");
            var positions=GetEdgePositions(frame);
            var groundVb=upload(positions);
            Geometry(groundVb,16,positions.Length); commands.SetPipelineState(pipeline.Ground); commands.OMSetStencilRef(2); Constants(common with {EgColor=ground.Color});
            for(int i=0;i<scene.Materials.Count;i++) {
                var material=scene.Materials[i]; if(!frame.Materials[i].CastGroundShadow||material.IndexCount==0) continue;
                commands.DrawIndexedInstanced(material.IndexCount,1,material.StartIndex,0,0); draws++;groundDrawn++;
            }
        }
        commands.OMSetStencilRef(1); Geometry(vertexBuffer,32,scene.VertexCount);
        if(gi is not null) {
            commands.SetGraphicsRootShaderResourceView(5,gi.Indirect.GPUVirtualAddress);
            commands.SetGraphicsRootDescriptorTable(6,gi.Inputs);
        }
        if(nativeLights)
        {
            commands.SetGraphicsRootDescriptorTable(nativeRoot+1,srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(realSlot+1,srvStride));
        }
        using(var surfaceCpu=MeasureSceneCpu(SceneCpuStage.SurfaceLoop))
        {
        foreach(int i in SurfaceDrawOrderPlanner.Plan(frame)) {
            var material=scene.Materials[i]; var binding=style.Materials[i]; var flags=frame.Materials[i];
            if(!flags.SurfaceEnabled||material.IndexCount==0) continue;
            if(!ShouldDrawSurface(frame,i,common.ViewProjection))continue;
            var host=OriginalStyleMath.ResolveHostColors(material,style.Profile,flags.UseSceneLighting);
            bool both=binding.DoubleSided||(flags.CullTransparentSurface&&host.EgColor.W<1);
            var surfaceFamily=surfaceStyles?.Materials[i]??D3D12SurfaceStyle.Mmd;
            bool stylized=surfaceFamily is D3D12SurfaceStyle.GenshinPmx or D3D12SurfaceStyle.StarRailPmx
                or D3D12SurfaceStyle.WutheringPmx or D3D12SurfaceStyle.EndfieldPmx;
            var response=flags.CharacterToonResponse;
            if(stylized)
            {
                var selected=GetStylizedSurfacePipelines(pipeline,surfaceFamily,surfaceStyles!.EffectiveFaceSdf(i) is not null,
                    surfaceStyles.EffectiveGenshinNonMetallicSpecular(i) is not null,surfaceStyles.EffectiveStarMcHair(i) is not null,surfaceStyles.EffectiveEndfieldHair(i) is not null,surfaceStyles.EffectiveEndfieldRim(i) is not null,surfaceStyles.EffectiveGenshinHairShadowAtlas(i) is not null,surfaceStyles.EffectiveEndfieldMatcap(i) is not null,surfaceStyles.EffectiveGenshinMetallic(i) is not null,surfaceStyles.EffectiveGenshinMetallic(i)?.Specular is not null,surfaceStyles.EffectiveEndfieldMatcap(i)?.Palette is not null,surfaceStyles.EffectiveEndfieldMatcap(i)?.Palette?.BaseImage is not null,surfaceStyles.EffectiveStarMcBody(i) is not null);
                commands.SetPipelineState(both?selected.Both:selected.Back);
                response??=StylizedDefaultResponse;
                response=surfaceStyles.EffectiveToonResponse(i,response);
            }
            else commands.SetPipelineState(both?pipeline.SurfaceBoth:pipeline.SurfaceBack);
            var materialTreatment=surfaceStyles?.EffectivePmxMaterialTreatment(i);
            var stylizedShading=stylized?surfaceStyles!.EffectiveStylizedShading(i):null;
            var materialToon=SurfaceToon(binding,flags.UseSceneLighting);
            if(materialTreatment is not null)materialToon=materialTreatment.TransformShade(materialToon);
            Constants(common with {EgColor=host.EgColor,SpecularPower=host.SpecularPower,ToonShadowColor=materialToon,
                // The established shaders read EyePosition.xyz only. Stylized families reuse W for
                // its per-material normal response without changing the constant/root ABI.
                EyePosition=new(camera.Eye,stylized?surfaceStyles!.EffectiveNormalShadeStrength(i):1),
                // Ground projection uses xyz only. W is consumed exclusively by the
                // stylized surface families' explicit facial cast-shadow attenuation.
                GroundDirection=stylized?new(common.GroundDirection.X,common.GroundDirection.Y,common.GroundDirection.Z,
                    surfaceStyles!.EffectiveGeometricShadowStrength(i)):common.GroundDirection,
                // Only generated stylized surface shaders declare this optional
                // tail. Identity is explicit; no new root or per-value PSO key.
                PmxMaterialTreatmentParams=new(materialTreatment?.Saturation??1,materialTreatment?.ValueContrast??0,
                    materialTreatment?.HairHighlightStrength??0,materialTreatment?.HairHighlightWidth??.12f),
                // 风格化明暗只由四个画风家族的普通表面声明；全零表示不启用。
                StylizedShadeParams=stylizedShading?.Shade??default,StylizedTintParams=stylizedShading?.Tint??default,
                StylizedWarmParams=stylizedShading?.Warm??default,StylizedGradeParams=stylizedShading?.Grade??default,
                IncomingLight=new(common.IncomingLight.X,common.IncomingLight.Y,common.IncomingLight.Z,response?.TransitionWidth??0),
                ShadowOptions=new(common.ShadowOptions.X,common.ShadowOptions.Y,common.ShadowOptions.Z,response?.Threshold??0),
                Options=new(material.AlphaCutoff,SurfaceMode(binding),material.DiffuseTexture>=0?1:0,options.Transp?1:0)});
            if(pipeline.TextureMorphRoot>=0)commands.SetGraphicsRootConstantBufferView(pipeline.TextureMorphRoot,context.UploadConstants(new TextureMorphConstants(binding.TextureFactors)));
            if(pipeline.FaceSdfRoot>=0)
            {
                var faceSdf=surfaceStyles?.EffectiveFaceSdf(i);
                commands.SetGraphicsRootConstantBufferView(pipeline.FaceSdfRoot,context.UploadConstants(faceSdf is null?default:FaceSdfConstants.From(faceSdf)));
                commands.SetGraphicsRootDescriptorTable(pipeline.FaceSdfRoot+1,srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(faceSdf is null?0:faceSdf.CombinedTextureIndex+2),srvStride));
                if(pipeline.Key.PackedFaceData)
                    commands.SetGraphicsRootDescriptorTable(pipeline.FaceSdfRoot+2,srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(
                        slotBase+(faceSdf is {HasSecondaryImage:true}?faceSdf.SecondaryCombinedTextureIndex+2:0),srvStride));
            }
            if(pipeline.GenshinHairRoot>=0)
            {
                var hair=surfaceStyles?.EffectiveGenshinNonMetallicSpecular(i);
                // Both resources are initialized for ordinary draws in a mixed frame.
                // Only the separate packed-hair PS variant consumes this root state.
                commands.SetGraphicsRootConstantBufferView(pipeline.GenshinHairRoot,
                    context.UploadConstants(hair is null?default:GenshinHairConstants.From(hair)));
                commands.SetGraphicsRootDescriptorTable(pipeline.GenshinHairRoot+1,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(hair is null?0:hair.CombinedTextureIndex+2),srvStride));
            }
            if(pipeline.StarMcHairRoot>=0)
            {
                var hair=surfaceStyles?.EffectiveStarMcHair(i);
                // Initialize the CBV and both independent mask descriptors for
                // ordinary/Face/Genshin draws as well as selected Star/MC hair.
                commands.SetGraphicsRootConstantBufferView(pipeline.StarMcHairRoot,
                    context.UploadConstants(hair is null?default:StarMcHairConstants.From(hair)));
                commands.SetGraphicsRootDescriptorTable(pipeline.StarMcHairRoot+1,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(hair is null?0:hair.HighlightCombinedTextureIndex+2),srvStride));
                commands.SetGraphicsRootDescriptorTable(pipeline.StarMcHairRoot+2,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(hair is null?0:hair.ShadowCombinedTextureIndex+2),srvStride));
            }
            if(pipeline.EndfieldHairRoot>=0)
            {
                var hair=surfaceStyles?.EffectiveEndfieldHair(i);
                // Explicit initialization prevents a selected material leaking into an ordinary draw.
                commands.SetGraphicsRootConstantBufferView(pipeline.EndfieldHairRoot,
                    context.UploadConstants(hair is null?default:EndfieldHairConstants.From(hair,camera)));
                commands.SetGraphicsRootDescriptorTable(pipeline.EndfieldHairRoot+1,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(hair is null?0:hair.CombinedTextureIndex+2),srvStride));
            }
            if(pipeline.EndfieldRimRoot>=0)
            {
                var rim=surfaceStyles?.EffectiveEndfieldRim(i);
                commands.SetGraphicsRootConstantBufferView(pipeline.EndfieldRimRoot,
                    context.UploadConstants(rim is null?default:EndfieldRimConstants.From(rim,camera)));
            }
            if(pipeline.GenshinAtlasRoot>=0)
            {
                var atlas=surfaceStyles?.EffectiveGenshinHairShadowAtlas(i);
                commands.SetGraphicsRootConstantBufferView(pipeline.GenshinAtlasRoot,
                    context.UploadConstants(atlas is null?default:GenshinAtlasConstants.From(atlas)));
                commands.SetGraphicsRootDescriptorTable(pipeline.GenshinAtlasRoot+1,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(atlas is null?0:atlas.LightmapCombinedTextureIndex+2),srvStride));
                commands.SetGraphicsRootDescriptorTable(pipeline.GenshinAtlasRoot+2,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(atlas is null?0:atlas.AtlasCombinedTextureIndex+2),srvStride));
            }
            if(pipeline.StarMcBodyRoot>=0)
            {
                var body=surfaceStyles?.EffectiveStarMcBody(i);
                commands.SetGraphicsRootConstantBufferView(pipeline.StarMcBodyRoot,
                    context.UploadConstants(body is null?default:StarMcBodyConstants.From(body,camera)));
                commands.SetGraphicsRootDescriptorTable(pipeline.StarMcBodyRoot+1,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(body is null?0:body.DetailCombinedTextureIndex+2),srvStride));
            }
            if(pipeline.EndfieldMatcapRoot>=0)
            {
                var matcap=surfaceStyles?.EffectiveSurfaceMatcap(i);
                commands.SetGraphicsRootConstantBufferView(pipeline.EndfieldMatcapRoot,
                    context.UploadConstants(matcap is null?default:EndfieldMatcapConstants.From(matcap,camera)));
                commands.SetGraphicsRootDescriptorTable(pipeline.EndfieldMatcapRoot+1,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(matcap is null?0:matcap.NormalCombinedTextureIndex+2),srvStride));
                commands.SetGraphicsRootDescriptorTable(pipeline.EndfieldMatcapRoot+2,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(matcap is null?0:matcap.PCombinedTextureIndex+2),srvStride));
                int colorSlot=slotBase;
                ID3D12Resource corners;
                if(matcap is not null)
                {
                    colorSlot=slotBase+scene.Textures.Count+4+(nativeLights?2:0)+i;
                    var data=surfaceStyles!.EndfieldMatcapGeometry(i,frame);
                    corners=cache.BufferByIdentity("original-matcap-corners-"+i,MemoryMarshal.AsBytes(data.Corners),
                        ResourceStates.PixelShaderResource,data.Fingerprint);
                }
                else corners=cache.BufferByIdentity("original-matcap-dummy",new byte[192],ResourceStates.PixelShaderResource,"zero");
                commands.SetGraphicsRootDescriptorTable(pipeline.EndfieldMatcapRoot+3,srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(colorSlot,srvStride));
                commands.SetGraphicsRootShaderResourceView(pipeline.EndfieldMatcapRoot+4,corners.GPUVirtualAddress);
            }
            if(pipeline.GenshinMetallicRoot>=0)
            {
                var metal=surfaceStyles?.EffectiveGenshinMetallic(i);
                commands.SetGraphicsRootConstantBufferView(pipeline.GenshinMetallicRoot,
                    context.UploadConstants(metal is null?default:GenshinMetallicConstants.From(metal,camera)));
                commands.SetGraphicsRootDescriptorTable(pipeline.GenshinMetallicRoot+1,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(metal is null?0:metal.LightmapCombinedTextureIndex+2),srvStride));
                int metalColorSlot=metal is null?slotBase:slotBase+scene.Textures.Count+4+(nativeLights?2:0)
                    +(surfaceStyles!.HasConsumedSurfaceMatcap?surfaceStyles.Count:0)+i;
                commands.SetGraphicsRootDescriptorTable(pipeline.GenshinMetallicRoot+2,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(metalColorSlot,srvStride));
            }
            if(pipeline.GenshinMetallicSpecularRoot>=0)
            {
                var spec=surfaceStyles?.EffectiveGenshinMetallic(i)?.Specular;
                commands.SetGraphicsRootConstantBufferView(pipeline.GenshinMetallicSpecularRoot,
                    context.UploadConstants(spec is null?default:GenshinMetallicSpecularConstants.From(spec)));
                int ramp=spec is {ConsumesRamp:true,RampCombinedTextureIndex:{} selectedRamp}?selectedRamp+2:0;
                commands.SetGraphicsRootDescriptorTable(pipeline.GenshinMetallicSpecularRoot+1,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+ramp,srvStride));
            }
            if(pipeline.EndfieldPaletteRoot>=0)
            {
                var palette=surfaceStyles?.EffectiveEndfieldMatcap(i)?.Palette;
                commands.SetGraphicsRootConstantBufferView(pipeline.EndfieldPaletteRoot,
                    context.UploadConstants(palette is null?default:EndfieldPaletteConstants.From(palette,style.Profile.LightAmbient)));
                int emission=palette is {ConsumesEmissionTexture:true,EmissionCombinedTextureIndex:{} e}?e+2:0;
                int light=palette is {ConsumesReceivedLightTexture:true,ReceivedLightCombinedTextureIndex:{} l}?l+2:0;
                commands.SetGraphicsRootDescriptorTable(pipeline.EndfieldPaletteRoot+1,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+emission,srvStride));
                commands.SetGraphicsRootDescriptorTable(pipeline.EndfieldPaletteRoot+2,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+light,srvStride));
            }
            if(pipeline.EndfieldBaseImageRoot>=0)
            {
                var baseImage=surfaceStyles?.EffectiveEndfieldMatcap(i)?.Palette?.BaseImage;
                commands.SetGraphicsRootConstantBufferView(pipeline.EndfieldBaseImageRoot,
                    context.UploadConstants(baseImage is null?default:EndfieldBaseImageConstants.From(baseImage,camera)));
                int colorSlot=slotBase;
                if(baseImage is {ConsumesBaseTexture:true})
                    colorSlot=slotBase+scene.Textures.Count+4+(nativeLights?2:0)
                        +(surfaceStyles!.HasConsumedSurfaceMatcap?surfaceStyles.Count:0)
                        +(surfaceStyles.HasConsumedGenshinMetallic?surfaceStyles.Count:0)+i;
                commands.SetGraphicsRootDescriptorTable(pipeline.EndfieldBaseImageRoot+1,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(colorSlot,srvStride));
                int detail=baseImage is {ConsumesDetailTexture:true,DetailCombinedTextureIndex:{} d}?d+2:0;
                int shadow=baseImage is {ConsumesShadowTexture:true,ShadowCombinedTextureIndex:{} q}?q+2:0;
                commands.SetGraphicsRootDescriptorTable(pipeline.EndfieldBaseImageRoot+2,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+detail,srvStride));
                commands.SetGraphicsRootDescriptorTable(pipeline.EndfieldBaseImageRoot+3,
                    srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+shadow,srvStride));
                var lut=cache.BufferByIdentity("endfield-shadow-lut-v1",D3D12EndfieldShadowLut.Bytes,
                    ResourceStates.PixelShaderResource,typeof(D3D12EndfieldShadowLut));
                commands.SetGraphicsRootShaderResourceView(pipeline.EndfieldBaseImageRoot+4,lut.GPUVirtualAddress);
            }
            if(gi is not null) {
                var giResponse=flags.GiToonResponseOverride??gi.ToonResponse;
                float compositionTag=gi.Composition==RayGiComposition.ToonPalette&&giResponse==RayGiToonResponse.PreservePaletteBands?2:(float)gi.Composition;
                commands.SetGraphicsRootConstantBufferView(4,context.UploadConstants(new GiSurfaceConstants((uint)(material.StartIndex/3),(uint)width,gi.Strength,gi.OcclusionStrength,new(OriginalStyleMath.ResolveAmbientShare(material,style.Profile,flags.UseSceneLighting),compositionTag))));
            }
            if(nativeLights) {
                var info=new NativeMaterialInfo((uint)ready.Native!.Count,ready.Native.ReceiverMasks[i],
                    flags.UseSceneLighting?1u:0u, flags.ReceiveSelfShadow?1u:0u);
                var diffuse=material.Diffuse;
                // Native shaders consume diffuse RGB only. W is an independent
                // per-material policy; coverage still uses EgColor/raw texture alpha.
                diffuse.W=flags.PreserveDirectLightPalette?1:0;
                commands.SetGraphicsRootConstantBufferView(nativeRoot,context.UploadConstants(new NativeMaterialConstants(info,diffuse)));
            }
            commands.SetGraphicsRootDescriptorTable(1,srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(material.DiffuseTexture<0?0:material.DiffuseTexture+2),srvStride));
            commands.SetGraphicsRootDescriptorTable(2,srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase+(binding.SphereTexture<0?0:binding.SphereTexture+2),srvStride));
            commands.SetGraphicsRootDescriptorTable(3,srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(flags.ReceiveSelfShadow?realSlot:whiteSlot,srvStride));
            commands.DrawIndexedInstanced(material.IndexCount,1,material.StartIndex,0,0); draws++;
        }
        }
        if(frame.Outline is not null&&frame.OutlineOptions is {Eligible:true,ModelWidth:>0})
            draws+=RecordCombinedOutline(device,commands,pipeline.Outline,frame,width,height,vertexBuffer,indexBuffer,upload,own,context,
                srvHeap,srvStride,slotBase,depthFormat,cache,surfaceStyles);
        return draws;
    }

    private FramePipelines GetFramePipelines(OriginalFrameScene frame,Format depthFormat,bool gi=false,bool faceSdf=false,bool packedFaceData=false,bool genshinHair=false,bool starMcHair=false,bool endfieldHair=false,bool endfieldRim=false,bool genshinAtlas=false,bool endfieldMatcap=false,bool genshinMetallic=false,bool genshinMetallicSpecular=false,bool endfieldPalette=false,bool endfieldBaseImage=false,bool starMcBody=false)
    {
        using var pipelineCpu=MeasureSceneCpu(SceneCpuStage.Pipelines);
        bool subTexture=HasSubTexture(frame);
        bool textureMorph=subTexture||frame.Surface.Materials.Any(binding=>!binding.TextureFactors.IsIdentity);
        var o=frame.Options!; var key=new FramePipelineKey(frame.Surface.Profile.TextureSampling,o.SelfShadow?.Sampling??OriginalTextureSampling.LinearClamp,depthFormat,o.BlendMode,o.WriteDepth,o.GroundShadow?.AlphaBlend==true,o.GroundShadow?.UseStencil==true,o.SelfShadow?.DepthComparison??OriginalDepthComparison.LessEqual,gi,HasNativeLights(frame),textureMorph,subTexture,o.AlphaOutput,frame.Materials.Any(flags=>flags.CharacterToonResponse is not null),faceSdf,packedFaceData,genshinHair,starMcHair,endfieldHair,endfieldRim,genshinAtlas,endfieldMatcap,genshinMetallic,genshinMetallicSpecular,endfieldPalette,endfieldBaseImage,starMcBody,D3D12RasterWinding.FrontCounterClockwise(frame),HasNativeLights(frame)&&frame.Materials.Any(flags=>flags.PreserveDirectLightPalette));
        if(framePipelines.TryGetValue(key,out var existing))return existing;
        RootParameter[] parameters=[new(RootParameterType.ConstantBufferView,new RootDescriptor(0,0),ShaderVisibility.All),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,0)),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,1)),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,2)),ShaderVisibility.Pixel)];
        if(gi)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(1,0),ShaderVisibility.Pixel),
            new(RootParameterType.ShaderResourceView,new RootDescriptor(3,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,2,4)),ShaderVisibility.Pixel)];
        if(key.NativeLights)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(2,0),ShaderVisibility.Pixel),
            // One table holds the atlas SRV and frame CBV. Its root cost remains one DWORD,
            // even when a mixed-style scene enables every optional material resource family.
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,6),
                new DescriptorRange(DescriptorRangeType.ConstantBufferView,1,0,1)),ShaderVisibility.Pixel)];
        int textureMorphRoot=key.TextureMorph?parameters.Length:-1;
        if(key.TextureMorph)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(3,0),ShaderVisibility.Pixel)];
        int faceSdfRoot=key.FaceSdf?parameters.Length:-1;
        if(key.FaceSdf)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(4,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,7)),ShaderVisibility.Pixel)];
        if(key.PackedFaceData)parameters=[..parameters,
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,8)),ShaderVisibility.Pixel)];
        int genshinHairRoot=key.GenshinHair?parameters.Length:-1;
        if(key.GenshinHair)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(5,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,9)),ShaderVisibility.Pixel)];
        int starMcHairRoot=key.StarMcHair?parameters.Length:-1;
        if(key.StarMcHair)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(6,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,10)),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,11)),ShaderVisibility.Pixel)];
        int endfieldHairRoot=key.EndfieldHair?parameters.Length:-1;
        if(key.EndfieldHair)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(7,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,12)),ShaderVisibility.Pixel)];
        int endfieldRimRoot=key.EndfieldRim?parameters.Length:-1;
        if(key.EndfieldRim)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(8,0),ShaderVisibility.Pixel)];
        int genshinAtlasRoot=key.GenshinAtlas?parameters.Length:-1;
        if(key.GenshinAtlas)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(9,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,13)),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,14)),ShaderVisibility.Pixel)];
        int endfieldMatcapRoot=key.EndfieldMatcap?parameters.Length:-1;
        if(key.EndfieldMatcap)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(10,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,15)),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,16)),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,17)),ShaderVisibility.Pixel),
            new(RootParameterType.ShaderResourceView,new RootDescriptor(18,0),ShaderVisibility.Pixel)];
        int genshinMetallicRoot=key.GenshinMetallic?parameters.Length:-1;
        if(key.GenshinMetallic)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(11,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,19)),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,20)),ShaderVisibility.Pixel)];
        int genshinMetallicSpecularRoot=key.GenshinMetallicSpecular?parameters.Length:-1;
        if(key.GenshinMetallicSpecular)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(12,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,21)),ShaderVisibility.Pixel)];
        int endfieldPaletteRoot=key.EndfieldPalette?parameters.Length:-1;
        if(key.EndfieldPalette)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(13,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,22)),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,23)),ShaderVisibility.Pixel)];
        int endfieldBaseImageRoot=key.EndfieldBaseImage?parameters.Length:-1;
        if(key.EndfieldBaseImage)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(14,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,24)),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,25)),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,26)),ShaderVisibility.Pixel),
            new(RootParameterType.ShaderResourceView,new RootDescriptor(27,0),ShaderVisibility.Pixel)];
        int starMcBodyRoot=key.StarMcBody?parameters.Length:-1;
        if(key.StarMcBody)parameters=[..parameters,new(RootParameterType.ConstantBufferView,new RootDescriptor(15,0),ShaderVisibility.Pixel),
            new(new RootDescriptorTable(new DescriptorRange(DescriptorRangeType.ShaderResourceView,1,28)),ShaderVisibility.Pixel)];
        StaticSamplerDescription Sampler(OriginalTextureSampling sampling,int register) =>new(ShaderVisibility.Pixel,register,0) {
            Filter=sampling is OriginalTextureSampling.LinearClamp or OriginalTextureSampling.LinearWrap?Filter.MinMagMipLinear:Filter.MinMagMipPoint,
            AddressU=sampling is OriginalTextureSampling.PointWrap or OriginalTextureSampling.LinearWrap?TextureAddressMode.Wrap:TextureAddressMode.Clamp,
            AddressV=sampling is OriginalTextureSampling.PointWrap or OriginalTextureSampling.LinearWrap?TextureAddressMode.Wrap:TextureAddressMode.Clamp,AddressW=TextureAddressMode.Clamp };
        StaticSamplerDescription[] samplers=[Sampler(key.SurfaceSampling,0),Sampler(key.ShadowSampling,1)];
        if(key.FaceSdf)samplers=[..samplers,Sampler(OriginalTextureSampling.LinearWrap,2)];
        if(key.GenshinHair)samplers=[..samplers,Sampler(OriginalTextureSampling.LinearWrap,3)];
        if(key.StarMcHair)samplers=[..samplers,Sampler(OriginalTextureSampling.LinearWrap,4)];
        if(key.EndfieldHair)samplers=[..samplers,Sampler(OriginalTextureSampling.LinearWrap,5)];
        if(key.GenshinAtlas)samplers=[..samplers,Sampler(OriginalTextureSampling.LinearWrap,6)];
        if(key.EndfieldMatcap)samplers=[..samplers,Sampler(OriginalTextureSampling.LinearWrap,7)];
        if(key.GenshinMetallic)samplers=[..samplers,Sampler(OriginalTextureSampling.LinearWrap,8)];
        var root=runtime.CreateRootSignature(new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,parameters,samplers),RootSignatureVersion.Version10);
        var surfaceFormat=o.AlphaOutput==FrameAlphaOutput.Straight?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm;
        var created=new List<IDisposable>{root};
        try {
            var layout=new InputLayoutDescription(SurfaceLayout(key.SubTexture));
            var positionLayout=new InputLayoutDescription([new InputElementDescription("POSITION",0,Format.R32G32B32_Float,0,0)]);
            var both=RasterizerDescription.CullNone; var back=both; back.CullMode=CullMode.Back;back.FrontCounterClockwise=key.FrontCounterClockwise;
            var front=back;front.CullMode=CullMode.Front;
            // Shadow projections define their own orientation independently of the view camera.
            // Preserve their established light-space rasterization when the editor uses an LH view.
            var shadowBackRaster=both;shadowBackRaster.CullMode=CullMode.Back;shadowBackRaster.FrontCounterClockwise=true;
            var depth=DepthStencilDescription.Default;depth.DepthFunc=ComparisonFunction.LessEqual;depth.DepthWriteMask=o.WriteDepth?DepthWriteMask.All:DepthWriteMask.Zero;
            if(key.Stencil) { depth.StencilEnable=true; depth.StencilReadMask=255;depth.StencilWriteMask=255;
                depth.FrontFace.StencilFunc=ComparisonFunction.Always;depth.FrontFace.StencilPassOp=StencilOperation.Replace;depth.BackFace=depth.FrontFace; }
            var shadowDepth=DepthStencilDescription.Default;shadowDepth.DepthFunc=key.ShadowComparison==OriginalDepthComparison.Less?ComparisonFunction.Less:ComparisonFunction.LessEqual;shadowDepth.DepthWriteMask=depth.DepthWriteMask;
            var groundDepth=depth;groundDepth.DepthFunc=ComparisonFunction.Less;
            if(key.Stencil) { groundDepth.FrontFace.StencilFunc=ComparisonFunction.Greater;groundDepth.BackFace=groundDepth.FrontFace; }
            ID3D12PipelineState Pso(string vs,string ps,InputLayoutDescription input,RasterizerDescription raster,DepthStencilDescription ds,BlendDescription blend,Format colorFormat,Format dsFormat) {
                runtime.PipelineCreations++;
                bool nativeShader=ps.Contains("Native",StringComparison.Ordinal);
                var state=runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {RootSignature=root,
                    VertexShader=key.SubTexture&&(vs is "VSSurface" or "VSNativeDepth")?GetSubTextureFrameShader(vs,true,key.NativeLights,gi):vs=="VSNativeDepth"?GetNativeFrameShader(vs,true):GetFrameShader(vs,true),
                    PixelShader=key.CharacterDirectPalette&&ps.StartsWith("PSSurface",StringComparison.Ordinal)?GetCharacterDirectPaletteShader(ps,gi,key.TextureMorph,key.SubTexture,key.CharacterToon):key.CharacterToon&&ps.StartsWith("PSSurface",StringComparison.Ordinal)?GetCharacterToonFrameShader(ps,key.NativeLights,gi,key.TextureMorph,key.SubTexture):key.SubTexture&&(ps.StartsWith("PSSurface",StringComparison.Ordinal)||ps=="PSNativeDepth")?GetSubTextureFrameShader(ps,false,key.NativeLights,gi):key.TextureMorph&&ps.StartsWith("PSSurface",StringComparison.Ordinal)?GetTextureMorphFrameShader(ps,key.NativeLights,gi):nativeShader?GetNativeFrameShader(ps,false):gi&&ps=="PSSurfaceGi"?GetGiFrameShader(ps,false):GetFrameShader(ps,false),
                    InputLayout=input,BlendState=blend,SampleMask=uint.MaxValue,RasterizerState=raster,DepthStencilState=ds,PrimitiveTopologyType=PrimitiveTopologyType.Triangle,RenderTargetFormats=[colorFormat],DepthStencilFormat=dsFormat,SampleDescription=new(1,0)});
                created.Add(state);return state;
            }
            string surfacePixel=key.NativeLights?(gi?"PSSurfaceGiNative":"PSSurfaceNative"):(gi?"PSSurfaceGi":"PSSurface");
            var surfaceBack=Pso("VSSurface",surfacePixel,layout,back,depth,FrameBlend(o.BlendMode,o.AlphaOutput),surfaceFormat,depthFormat);
            var surfaceBoth=Pso("VSSurface",surfacePixel,layout,both,depth,FrameBlend(o.BlendMode,o.AlphaOutput),surfaceFormat,depthFormat);
            var shadowBack=Pso("VSDepth","PSDepth",positionLayout,shadowBackRaster,shadowDepth,BlendDescription.Opaque,Format.R32_Float,Format.D24_UNorm_S8_UInt);
            var shadowBoth=Pso("VSDepth","PSDepth",positionLayout,both,shadowDepth,BlendDescription.Opaque,Format.R32_Float,Format.D24_UNorm_S8_UInt);
            // Straight output defines coverage compositing for every drawing pass, including ground.
            var ground=Pso("VSGround","PSColor",positionLayout,both,groundDepth,key.GroundBlend||o.AlphaOutput==FrameAlphaOutput.Straight?FrameBlend(OriginalAlphaBlendMode.Normal,o.AlphaOutput):BlendDescription.Opaque,surfaceFormat,depthFormat);
            ID3D12PipelineState? nativeShadowBack=null,nativeShadowBoth=null;
            if(key.NativeLights) {
                nativeShadowBack=Pso("VSNativeDepth","PSNativeDepth",layout,shadowBackRaster,DepthStencilDescription.Default,BlendDescription.Opaque,Format.R32_Float,Format.D32_Float);
                nativeShadowBoth=Pso("VSNativeDepth","PSNativeDepth",layout,both,DepthStencilDescription.Default,BlendDescription.Opaque,Format.R32_Float,Format.D32_Float);
            }
            var outlineRoot=runtime.CreateRootSignature(new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,[new RootParameter(RootParameterType.ConstantBufferView,new RootDescriptor(0,0),ShaderVisibility.All)]),RootSignatureVersion.Version10);created.Add(outlineRoot);
            var outlineShader=GetOutlineShaders();runtime.PipelineCreations++;
            var outlineLayout=new InputLayoutDescription([new InputElementDescription("POSITION",0,Format.R32G32B32_Float,0,0),
                new("TEXCOORD",2,Format.R32G32B32A32_Float,0,1)]);
            var outline=runtime.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {RootSignature=outlineRoot,VertexShader=outlineShader.Vertex,PixelShader=outlineShader.Pixel,InputLayout=outlineLayout,BlendState=FrameBlend(o.BlendMode,o.AlphaOutput),SampleMask=uint.MaxValue,RasterizerState=front,DepthStencilState=depth,PrimitiveTopologyType=PrimitiveTopologyType.Triangle,RenderTargetFormats=[surfaceFormat],DepthStencilFormat=depthFormat,SampleDescription=new(1,0)});created.Add(outline);
            var result=new FramePipelines(root,surfaceBack,surfaceBoth,shadowBack,shadowBoth,ground,new(outlineRoot,outline),nativeShadowBack,nativeShadowBoth,textureMorphRoot,faceSdfRoot,genshinHairRoot,starMcHairRoot,endfieldHairRoot,endfieldRimRoot,genshinAtlasRoot,endfieldMatcapRoot,genshinMetallicRoot,genshinMetallicSpecularRoot,endfieldPaletteRoot,endfieldBaseImageRoot,starMcBodyRoot,key);framePipelines.Add(key,result);return result;
        } catch { for(int i=created.Count-1;i>=0;i--)created[i].Dispose();throw; }
    }
    private static BlendDescription FrameBlend(OriginalAlphaBlendMode mode,FrameAlphaOutput alpha=FrameAlphaOutput.LegacyMax)
    {
        var blend=BlendDescription.Opaque; blend.RenderTarget=(RenderTargetBlendDescription[])blend.RenderTarget.Clone(); var target=blend.RenderTarget[0];target.BlendEnable=true;
        target.SrcBlend=Blend.SourceAlpha;target.DestBlend=mode==OriginalAlphaBlendMode.Additive?Blend.One:Blend.InverseSourceAlpha;target.BlendOp=BlendOperation.Add;
        target.SrcBlendAlpha=Blend.One;target.DestBlendAlpha=alpha==FrameAlphaOutput.Straight?Blend.InverseSourceAlpha:Blend.One;
        target.BlendOpAlpha=alpha==FrameAlphaOutput.Straight?BlendOperation.Add:BlendOperation.Max;blend.RenderTarget[0]=target;return blend;
    }
    private void DisposeFramePipelines() { DisposeStylizedSurfacePipelines();foreach(var p in framePipelines.Values) {p.NativeShadowBoth?.Dispose();p.NativeShadowBack?.Dispose();p.Outline.State.Dispose();p.Outline.Root.Dispose();p.Ground.Dispose();p.ShadowBoth.Dispose();p.ShadowBack.Dispose();p.SurfaceBoth.Dispose();p.SurfaceBack.Dispose();p.Root.Dispose();}framePipelines.Clear(); }
}
