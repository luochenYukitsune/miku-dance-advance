using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

internal sealed partial class D3D12TemporalInputPass
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CharacterTemporalVertex(Vector3 Current, Vector3 Previous, Vector3 Normal, Vector2 Uv,
        Vector3 SourcePosition);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CharacterTemporalConstants(Matrix4x4 CurrentWorld, Matrix4x4 PreviousWorld,
        Matrix4x4 RasterViewProjection, Matrix4x4 CurrentViewProjection, Matrix4x4 PreviousViewProjection,
        Matrix4x4 SphereRotation, Matrix4x4 LightViewProjection, Vector4 Extent, Vector4 Coverage, Vector4 Controls,
        Vector4 GroundDirection, Vector4 PreviousGroundDirection, Vector4 OutlineScales);

    private D3D12GpuCache? characterCache;
    private D3D12GpuSkinning? characterGpuSkinning;
    private readonly D3D12VisibilityBoundsCache characterBoundsCache = new(2);
    private sealed record OutlinePositions(OriginalOutlineData Data,OriginalOutlineOptions Options,
        IReadOnlyList<OriginalFrameMaterialFlags>? Materials,D3D12ExpandedPositions Positions);
    // At most two current/previous outline payloads per technique, never a history of poses/cameras.
    private OutlinePositions? originalPositionsFirst,originalPositionsSecond,modernPositionsFirst,modernPositionsSecond;
    private readonly D3D12OutlinePositionCache originalTemporalPositionCache=new(),modernTemporalPositionCache=new();
    private readonly record struct EdgeStreamIdentity(D3D12ExpandedPositions Positions,D3D12ExpandedPositions PreviousPositions,D3D12VertexStreamIdentity Source);

    private D3D12ExpandedPositions GetCharacterOutlinePositions(OriginalFrameScene frame,bool modern)
    {
        var first=modern?modernPositionsFirst:originalPositionsFirst;
        var second=modern?modernPositionsSecond:originalPositionsSecond;
        bool Matches(OutlinePositions? entry)=>entry is not null&&ReferenceEquals(entry.Data,frame.Outline)
            &&entry.Options==frame.OutlineOptions&&(!modern||entry.Materials!.SequenceEqual(frame.Materials));
        if(Matches(first))return first!.Positions;
        var found=Matches(second)?second!:new OutlinePositions(frame.Outline!,frame.OutlineOptions!,modern?frame.Materials:null,
            (modern?modernTemporalPositionCache:originalTemporalPositionCache).GetChunks(frame.Outline!,frame.OutlineOptions!,modern?frame.Materials:null));
        if(modern){modernPositionsSecond=first;modernPositionsFirst=found;}
        else{originalPositionsSecond=first;originalPositionsFirst=found;}
        return found.Positions;
    }

    /// <summary>Original toon geometry coverage. The opaque/cutout layer supplies device depth and geometric
    /// backward motion. Blended layers retain that opaque motion and supply their sampled alpha in R8 reactive
    /// instead of inventing one motion/depth for a composite pixel. A separate visibility DSV follows the real
    /// colour pass (including transparent depth writes, source order and ground stencil). The depth/motion pass
    /// also writes that visibility for blended fragments, while its 0/1 MRT blend retains the last opaque inputs.
    /// This prevents a transparent first submission from exposing a later opaque draw which the colour pass culls.
    /// Reactive is a conservative
    /// maximum over visible submissions, so an earlier layer can remain marked after later opaque coverage.
    /// SubTexture uses the current primary technique's shadow-dependent alpha. Ground and expanded/inward outlines are actual geometry here.
    /// All transient resources and replaced texture sets remain leased through the recording frame's fence.</summary>
    internal D3D12TemporalInputs Record(D3D12RecordingContext context, OriginalFrameScene current,
        OriginalFrameScene previous, int width, int height, D3D12OriginalStyleRenderer.PreparedFrame? preparedFrame = null,
        D3D12GpuSkinning? gpuSkinning = null, ID3D12Resource? currentSurfaceVb = null)
        => RecordCharacter(context, current, previous, width, height, preparedFrame, false, null, gpuSkinning, currentSurfaceVb).Inputs!;

    /// <summary>Output-resolution unjittered visibility for world guides. Reuses the character's
    /// alpha, texture, culling and outline coverage. Fully transparent texels do not hide guides;
    /// other visible fragments follow the frame's depth-write policy, including blended materials.</summary>
    internal D3D12GpuTarget RecordEditorVisibility(D3D12RecordingContext context, OriginalFrameScene frame,
        int width, int height, D3D12OriginalStyleRenderer.PreparedFrame? preparedFrame = null,D3D12EditorGeometry? sharedGeometry = null)
        => RecordCharacter(context, frame, frame, width, height, preparedFrame, true,sharedGeometry).Visibility;

    private (D3D12TemporalInputs? Inputs, D3D12GpuTarget Visibility) RecordCharacter(D3D12RecordingContext context,
        OriginalFrameScene current, OriginalFrameScene previous, int width, int height,
        D3D12OriginalStyleRenderer.PreparedFrame? preparedFrame, bool editorVisibility,D3D12EditorGeometry? sharedGeometry = null,
        D3D12GpuSkinning? gpuSkinning = null, ID3D12Resource? currentSurfaceVb = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(previous);
        if (!ReferenceEquals(context.Runtime, runtime) || !runtime.IsRecording)
            throw new InvalidOperationException("Temporal inputs require an open frame on the owning D3D12 runtime.");
        if (width is < 1 or > 4096 || height is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(width), "Temporal input extents must be 1..4096.");
        ValidateCharacterPair(current, previous);
        bool subTexture = D3D12OriginalStyleRenderer.HasSubTexture(current);
        if (subTexture && current.Surface.Snapshot.AdditionalUvs is null)
            throw new ArgumentException("SubTexture temporal coverage requires an explicit UV1 stream.");
        if (subTexture && !current.Surface.Snapshot.IsEmpty && current.Options?.SelfShadow?.Generate == true && preparedFrame is null)
            throw new InvalidOperationException("SubTexture temporal coverage requires the current colour pass's prepared primary shadow.");
        var scene = current.Surface.Snapshot; var oldScene = previous.Surface.Snapshot;
        if(sharedGeometry is not null&&(!editorVisibility||!ReferenceEquals(sharedGeometry.Scene,scene)))
            throw new ArgumentException("Borrowed editor geometry must belong to this exact current scene.",nameof(sharedGeometry));
        var options = current.Options; var oldOptions = previous.Options;
        var camera = options?.CameraOverride ?? scene.Camera; var oldCamera = oldOptions?.CameraOverride ?? oldScene.Camera;
        if (editorVisibility) camera = oldCamera = camera with { ProjectionJitterPixels = Vector2.Zero };
        bool stencil = options?.GroundShadow?.UseStencil == true;
        var depthFormat = stencil ? Format.D24_UNorm_S8_UInt : Format.D32_Float;
        var device = context.Device; var commands = context.Commands;
        characterCache ??= new(runtime);
        var rtvHeap = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.RenderTargetView, editorVisibility ? 1 : 3)));
        var dsvHeap = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.DepthStencilView, editorVisibility ? 1 : 2)));
        int rtvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        int dsvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        var depthRtv = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        var motionRtv = depthRtv; motionRtv.Offset(1, rtvStride);
        var reactiveRtv = depthRtv; if (!editorVisibility) reactiveRtv.Offset(2, rtvStride);
        var opaqueDsv = dsvHeap.GetCPUDescriptorHandleForHeapStart();
        var colorDsv = opaqueDsv; if (!editorVisibility) colorDsv.Offset(1, dsvStride);
        ID3D12Resource Target(Format format, ResourceFlags flags, ResourceStates state) => context.Own(device.CreateCommittedResource(
            HeapType.Default, ResourceDescription.Texture2D(format, (uint)width, (uint)height, mipLevels: 1, flags: flags), state));
        var depth = editorVisibility ? null : Target(Format.R32_Float, ResourceFlags.AllowRenderTarget, ResourceStates.RenderTarget);
        var motion = editorVisibility ? null : Target(Format.R32G32_Float, ResourceFlags.AllowRenderTarget, ResourceStates.RenderTarget);
        var reactive = Target(Format.R8_UNorm, ResourceFlags.AllowRenderTarget, ResourceStates.RenderTarget);
        var opaqueVisibility = editorVisibility ? null : Target(depthFormat, ResourceFlags.AllowDepthStencil, ResourceStates.DepthWrite);
        var colorVisibility = Target(depthFormat, ResourceFlags.AllowDepthStencil, ResourceStates.DepthWrite);
        if (!editorVisibility) { device.CreateRenderTargetView(depth!, null, depthRtv); device.CreateRenderTargetView(motion!, null, motionRtv); }
        device.CreateRenderTargetView(reactive, null, reactiveRtv);
        if (!editorVisibility) device.CreateDepthStencilView(opaqueVisibility!, null, opaqueDsv);
        device.CreateDepthStencilView(colorVisibility, null, colorDsv);
        commands.UnsetRenderTargets();
        if (!editorVisibility)
        {
            commands.ClearRenderTargetView(depthRtv, new Color4(1f, 1f, 1f, 1f));
            commands.ClearRenderTargetView(motionRtv, new Color4(0f, 0f, 0f, 0f));
        }
        commands.ClearRenderTargetView(reactiveRtv, new Color4(0f, 0f, 0f, 0f));
        var clear = ClearFlags.Depth | (stencil ? ClearFlags.Stencil : 0);
        if (!editorVisibility) commands.ClearDepthStencilView(opaqueDsv, clear, 1, 0);
        commands.ClearDepthStencilView(colorDsv, clear, 1, 0);
        commands.RSSetViewport(0, 0, width, height, 0, 1); commands.RSSetScissorRect(new RawRect(0, 0, width, height));

        if (!scene.IsEmpty)
        {
            // Real temporal history retains its existing replaceable texture owner. The
            // editor replay borrows the primary recorder's identical numerical UNorm views.
            var textures = sharedGeometry is null?characterCache.GetReplaceable("character-temporal-textures", new CharacterTextureIdentity(scene),
                () => CreateCharacterTextures(context, scene)):null;
            int shadowSlot = scene.Textures.Count + 1;
            int srvCount = shadowSlot + (subTexture ? 2 : 0);
            var srvHeap = context.Own(device.CreateDescriptorHeap(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                srvCount, DescriptorHeapFlags.ShaderVisible)));
            int srvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            ID3D12Resource textureWhite;
            if(sharedGeometry is not null)
            {
                // The primary cache owns these resources until its renderer waits for
                // the recording fences. This pass owns only the new SRV descriptors.
                textureWhite=sharedGeometry.Cache.Texture(new("editor-white",1,1,[255,255,255,255]));
                device.CreateShaderResourceView(textureWhite,null,srvHeap.GetCPUDescriptorHandleForHeapStart());
                for(int i=0;i<scene.Textures.Count;i++)
                    device.CreateShaderResourceView(sharedGeometry.Cache.Texture(scene.Textures[i]),null,
                        srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(i+1,srvStride));
            }
            else
            {
                textureWhite=textures!.Resources[-1];
                foreach(var pair in textures.Resources)
                    device.CreateShaderResourceView(pair.Value,null,srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(pair.Key+1,srvStride));
            }
            if (subTexture)
            {
                // Prepared resources are owned by the same recording fence. A white RGBA
                // fallback also samples to scalar one when there is no generated map.
                var white = preparedFrame?.White ?? textureWhite;
                device.CreateShaderResourceView(white, null, srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(shadowSlot, srvStride));
                device.CreateShaderResourceView(preparedFrame?.Shadow ?? white, null,
                    srvHeap.GetCPUDescriptorHandleForHeapStart().Offset(shadowSlot + 1, srvStride));
                var uv = scene.AdditionalUvs!;
                var stream = sharedGeometry?.AdditionalUvs??characterCache.BufferByIdentity("character-temporal-uv1", MemoryMarshal.AsBytes(uv.Vertices),
                    ResourceStates.VertexAndConstantBuffer, uv);
                var uvView=new VertexBufferView(stream.GPUVirtualAddress,checked(scene.VertexCount*64),64);
                if(sharedGeometry is null)commands.IASetVertexBuffers(1,uvView);
                else commands.IASetVertexBuffers(2,uvView);
            }
            runtime.DescriptorHighWater = Math.Max(runtime.DescriptorHighWater, srvCount + 5);
            string geometryStream=editorVisibility?"character-visibility":"character-temporal";
            bool deferredPair=scene.HasDeferredDeformation||oldScene.HasDeferredDeformation;
            bool sharedDeferred=sharedGeometry is not null&&scene.HasDeferredDeformation;
            var ib = sharedGeometry?.Indices??characterCache.IndexBuffer(scene);
            ID3D12Resource vb;
            ID3D12Resource? groundVb = null;
            ID3D12Resource? modernEdgeVb = null;
            ID3D12Resource widthSource;
            D3D12ExpandedPositions? positions = null;
            D3D12ExpandedPositions? modernPositions = null;
            bool scaledOutline = current.Outline is not null && (current.Materials.Any(flags => flags.OutlineWidthScale != 1)
                || previous.Materials.Any(flags => flags.OutlineWidthScale != 1));
            bool hasModernOutline = current.Outline is not null && (current.Materials.Any(flags => flags.CharacterOutlineStyle is not null)
                || previous.Materials.Any(flags => flags.CharacterOutlineStyle is not null));
            D3D12GpuSkinning? activeSkinning = null;
            ID3D12Resource? currSurfGpu = null, currOrigExpGpu = null, currModExpGpu = null, currWidthSrcGpu = null;

            if(sharedGeometry is not null)
            {
                vb=sharedGeometry.Surface;
                commands.IASetVertexBuffers(1,new VertexBufferView(vb.GPUVirtualAddress,checked(scene.VertexCount*32),32));
                if(sharedDeferred)
                {
                    groundVb=sharedGeometry.OriginalOutline;
                    modernEdgeVb=sharedGeometry.ModernOutline;
                    widthSource=scaledOutline&&sharedGeometry.WidthSource is not null
                        ? sharedGeometry.Cache.GpuSkinning.PackTemporalBuffer("editor-vis-width-pair",scene.VertexCount,32,4u,
                            sharedGeometry.WidthSource,ResourceStates.VertexAndConstantBuffer,
                            sharedGeometry.WidthSource,ResourceStates.VertexAndConstantBuffer)
                        : vb;
                }
                else
                {
                    positions=sharedGeometry.OriginalPositions;
                    groundVb=sharedGeometry.OriginalOutline;
                    modernPositions=sharedGeometry.ModernPositions;
                    modernEdgeVb=sharedGeometry.ModernOutline;
                    widthSource=scaledOutline?D3D12OutlineDrawScale.SourcePairBuffer(current.Outline!,previous.Outline!,characterCache):vb;
                }
            }
            else if(deferredPair)
            {
                activeSkinning=gpuSkinning??(characterGpuSkinning??=new(runtime,characterCache));
                currSurfGpu=currentSurfaceVb
                    ??activeSkinning.TryResolveSnapshotVerticesOnGpu(scene,"char-temp-curr-vb")
                    ??characterCache.SceneVertexBuffer("char-temp-curr-static",scene);
                var prevSurfGpu=ReferenceEquals(scene,oldScene) ? currSurfGpu
                    : activeSkinning.TryResolveSnapshotVerticesOnGpu(oldScene,"char-temp-prev-vb")
                    ??characterCache.SceneVertexBuffer("char-temp-prev-static",oldScene);
                vb=activeSkinning.PackTemporalBuffer(geometryStream+"-vertices",scene.VertexCount,56,1u,
                    currSurfGpu,ResourceStates.VertexAndConstantBuffer,prevSurfGpu,ResourceStates.VertexAndConstantBuffer,
                    currSurfGpu,ResourceStates.VertexAndConstantBuffer);
                if(current.Outline is not null)
                {
                    bool sameOutlineInput=ReferenceEquals(current,previous);
                    ID3D12Resource? prevOrigExp=null,prevModExp=null,prevWidthSrc=null;
                    if(scene.HasDeferredDeformation)
                    {
                        var currRes=activeSkinning.ResolveTemporalOutlineBuffers(current,currSurfGpu,true,hasModernOutline,scaledOutline,"temp-curr");
                        currOrigExpGpu=currRes.OriginalExpanded;
                        currModExpGpu=currRes.ModernExpanded;
                        currWidthSrcGpu=currRes.WidthSource;
                    }
                    else
                    {
                        var cpOrig=GetCharacterOutlinePositions(current,false);
                        currOrigExpGpu=characterCache.ExpandedPositionBuffer("char-temp-curr-orig-stat",cpOrig);
                        if(hasModernOutline)
                        {
                            var cpMod=GetCharacterOutlinePositions(current,true);
                            currModExpGpu=characterCache.ExpandedPositionBuffer("char-temp-curr-mod-stat",cpMod);
                        }
                        if(scaledOutline)currWidthSrcGpu=D3D12OutlineDrawScale.SourceBuffer(current.Outline,characterCache);
                    }
                    if(sameOutlineInput)
                    {
                        prevOrigExp=currOrigExpGpu;prevModExp=currModExpGpu;prevWidthSrc=currWidthSrcGpu;
                    }
                    else if(oldScene.HasDeferredDeformation)
                    {
                        var prevRes=activeSkinning.ResolveTemporalOutlineBuffers(previous,prevSurfGpu,true,hasModernOutline,scaledOutline,"temp-prev");
                        prevOrigExp=prevRes.OriginalExpanded;
                        prevModExp=prevRes.ModernExpanded;
                        prevWidthSrc=prevRes.WidthSource;
                    }
                    else
                    {
                        var ppOrig=GetCharacterOutlinePositions(previous,false);
                        prevOrigExp=characterCache.ExpandedPositionBuffer("char-temp-prev-orig-stat",ppOrig);
                        if(hasModernOutline)
                        {
                            var ppMod=GetCharacterOutlinePositions(previous,true);
                            prevModExp=characterCache.ExpandedPositionBuffer("char-temp-prev-mod-stat",ppMod);
                        }
                        if(scaledOutline)prevWidthSrc=D3D12OutlineDrawScale.SourceBuffer(previous.Outline!,characterCache);
                    }
                    groundVb=activeSkinning.PackTemporalBuffer(geometryStream+"-original-edges",scene.VertexCount,56,2u,
                        currOrigExpGpu!,ResourceStates.VertexAndConstantBuffer,prevOrigExp!,ResourceStates.VertexAndConstantBuffer,
                        currSurfGpu,ResourceStates.VertexAndConstantBuffer);
                    if(hasModernOutline)
                        modernEdgeVb=activeSkinning.PackTemporalBuffer(geometryStream+"-modern-edges",scene.VertexCount,56,3u,
                            currModExpGpu!,ResourceStates.VertexAndConstantBuffer,prevModExp!,ResourceStates.VertexAndConstantBuffer,
                            currSurfGpu,ResourceStates.VertexAndConstantBuffer);
                    widthSource=scaledOutline
                        ? activeSkinning.PackTemporalBuffer(geometryStream+"-width-source",scene.VertexCount,32,4u,
                            currWidthSrcGpu!,ResourceStates.VertexAndConstantBuffer,prevWidthSrc!,ResourceStates.VertexAndConstantBuffer)
                        : vb;
                }
                else
                {
                    widthSource=vb;
                }
            }
            else
            {
                vb = characterCache.BufferByIdentity(geometryStream+"-vertices",ResourceStates.VertexAndConstantBuffer,
                    new D3D12TemporalVertexIdentity(scene,oldScene),()=>
                    {
                        var vertices=new CharacterTemporalVertex[scene.VertexCount];
                        for(int i=0;i<vertices.Length;i++)
                        {
                            var vertex=scene.GetVertex(i);
                            vertices[i]=new(vertex.Position,oldScene.GetVertex(i).Position,vertex.Normal,vertex.Uv,vertex.Position);
                        }
                        return vertices;
                    });
                ID3D12Resource EdgeStream(D3D12ExpandedPositions expPos,D3D12ExpandedPositions prevExpPos,bool textured=false)
                {
                    return characterCache.BufferByIdentity(geometryStream+(textured?"-modern-edges":"-original-edges"),ResourceStates.VertexAndConstantBuffer,
                        new EdgeStreamIdentity(expPos,prevExpPos,new D3D12VertexStreamIdentity(scene)),()=>
                        {
                            var edges=new CharacterTemporalVertex[expPos.Length];
                            for(int i=0;i<edges.Length;i++)
                            {
                                var vertex=scene.GetVertex(i);
                                edges[i]=new(new(expPos[i].X,expPos[i].Y,expPos[i].Z),
                                    new(prevExpPos[i].X,prevExpPos[i].Y,prevExpPos[i].Z),
                                    textured?vertex.Normal:Vector3.Zero,textured?vertex.Uv:Vector2.Zero,vertex.Position);
                            }
                            return edges;
                        });
                }
                positions = current.Outline is null ? null : GetCharacterOutlinePositions(current,false);
                D3D12ExpandedPositions? previousPositions = previous.Outline is null ? null : GetCharacterOutlinePositions(previous,false);
                groundVb = positions is null ? null : EdgeStream(positions, previousPositions!);
                if (hasModernOutline)
                {
                    modernPositions = GetCharacterOutlinePositions(current,true);
                    var previousModernPositions = GetCharacterOutlinePositions(previous,true);
                    modernEdgeVb = EdgeStream(modernPositions, previousModernPositions, true);
                }
                widthSource = scaledOutline ? D3D12OutlineDrawScale.SourcePairBuffer(current.Outline!, previous.Outline!, characterCache) : vb;
            }
            // Slot three is independent of the surface, shared editor and UV1 streams.
            commands.IASetVertexBuffers(3, new VertexBufferView(widthSource.GPUVirtualAddress, checked(scene.VertexCount * 32), 32));
            var raster = camera.ViewProjection(width, height);
            var unjittered = (camera with { ProjectionJitterPixels = Vector2.Zero }).ViewProjection(width, height);
            var oldUnjittered = (oldCamera with { ProjectionJitterPixels = Vector2.Zero }).ViewProjection(width, height);
            var common = new CharacterTemporalConstants(scene.World, oldScene.World, raster, unjittered, oldUnjittered,
                options?.SphereRotationOverride ?? current.Surface.Profile.SphereRotation,
                options?.SelfShadow?.LightViewProjection ?? Matrix4x4.Identity,
                new(width, height, 0, 0), Vector4.Zero, new(options is null ? 1 : 0,
                    options?.BlendMode == OriginalAlphaBlendMode.Additive ? 1 : 0, options?.SelfShadow is null ? 0 : 1, 0),
                new(options?.GroundShadow?.ProjectionDirection ?? Vector3.UnitY, 0),
                new(oldOptions?.GroundShadow?.ProjectionDirection ?? Vector3.UnitY, 0), Vector4.One);
            var surfaceBounds = options?.EnableFrustumCulling == true
                ? sharedGeometry?.SurfaceBounds ?? (scene.HasDeferredDeformation ? characterBoundsCache.GetSurface(scene) : OriginalFrustumVisibility.SurfaceMaterialBounds(scene))
                : null;
            var edgeBounds = surfaceBounds is not null && !scene.HasDeferredDeformation && positions is not null
                ? sharedGeometry?.OriginalBounds ?? D3D12VisibilityBoundsCache.PositionMaterialBounds(scene, positions)
                : null;
            var modernEdgeBounds = surfaceBounds is not null && !scene.HasDeferredDeformation && modernPositions is not null
                ? sharedGeometry?.ModernBounds ?? D3D12VisibilityBoundsCache.PositionMaterialBounds(scene, modernPositions)
                : null;
            commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            commands.IASetIndexBuffer(new IndexBufferView(ib.GPUVirtualAddress, checked(scene.Indices.Length * 4), Format.R32_UInt));
            // Replay the same geometry order for both layers, with independent visibility.
            for (int layer = editorVisibility ? 1 : 0; layer < 2; layer++)
            {
                bool mask = layer == 1;
                if (mask) commands.OMSetRenderTargets(reactiveRtv, colorDsv);
                else commands.OMSetRenderTargets([depthRtv, motionRtv], opaqueDsv);
                commands.SetDescriptorHeaps(srvHeap);
                void Draw(ID3D12Resource stream, int index, int mode, float alpha, float cutoff, OriginalSphereMode sphere,
                    int diffuse, int sphereTexture, CullMode cull, bool opaque, bool additive, bool writeDepth, bool simple = false)
                {
                    var state = GetCharacterPipeline(new(current.Surface.Profile.TextureSampling, depthFormat, cull,
                        writeDepth, mask, mode == 2, stencil, simple, mode == 3, subTexture,
                        subTexture ? options?.SelfShadow?.Sampling ?? OriginalTextureSampling.LinearClamp : OriginalTextureSampling.LinearClamp,
                        D3D12RasterWinding.FrontCounterClockwise(current), editorVisibility,sharedGeometry is not null));
                    commands.SetGraphicsRootSignature(state.Root); commands.SetPipelineState(state.State);
                    int stride=sharedGeometry is null?56:mode==0?32:mode==3?52:16;
                    commands.IASetVertexBuffers(0, new VertexBufferView(stream.GPUVirtualAddress, checked(scene.VertexCount * stride), stride));
                    commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(diffuse + 1, srvStride));
                    commands.SetGraphicsRootDescriptorTable(2, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(sphereTexture + 1, srvStride));
                    if (subTexture)
                    {
                        commands.SetGraphicsRootDescriptorTable(3, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(
                            shadowSlot + (options is not null && current.Materials[index].ReceiveSelfShadow ? 1 : 0), srvStride));
                        commands.SetGraphicsRootConstantBufferView(4,
                            D3D12OriginalStyleRenderer.UploadTemporalCoverageConstants(context, current, width, height, index));
                        commands.SetGraphicsRootConstantBufferView(5,
                            D3D12OriginalStyleRenderer.UploadTemporalTextureFactors(context, current.Surface.Materials[index]));
                    }
                    var constants = common with { Extent = new(width, height, mode, 0),
                        Coverage = new(alpha, cutoff, (float)sphere, (mode == 0 || mode == 3) && options?.Transp == true ? 1 : 0),
                        Controls = new(opaque ? 1 : 0, additive ? 1 : 0, common.Controls.Z,
                            mode == 3 ? current.Materials[index].CharacterOutlineStyle?.Color?.W ?? 1 : 0) };
                    if (mode == 1 || mode == 3) constants = constants with { RasterViewProjection = scene.World * raster,
                        CurrentViewProjection = scene.World * unjittered, PreviousViewProjection = oldScene.World * oldUnjittered,
                        OutlineScales = new(current.Materials[index].OutlineWidthScale, previous.Materials[index].OutlineWidthScale, 0, 0) };
                    commands.SetGraphicsRootConstantBufferView(0, context.UploadConstants(constants));
                    var range = scene.Materials[index];
                    commands.DrawIndexedInstanced(range.IndexCount, 1, range.StartIndex, 0, 0);
                }
                bool frameWriteDepth = options?.WriteDepth ?? true;
                if (options?.GroundShadow is { ProjectionDirection.Y: < 0 } ground)
                {
                    commands.OMSetStencilRef(2);
                    for (int i = 0; i < scene.Materials.Count; i++)
                        if (current.Materials[i].CastGroundShadow && scene.Materials[i].IndexCount > 0)
                            Draw(groundVb!, i, 2, ground.Color.W, 0, OriginalSphereMode.None, -1, -1, CullMode.None,
                                !ground.AlphaBlend, false, frameWriteDepth);
                }
                commands.OMSetStencilRef(1);
                foreach (int i in SurfaceDrawOrderPlanner.Plan(current))
                {
                    var range = scene.Materials[i]; var binding = current.Surface.Materials[i]; var flags = current.Materials[i];
                    if (range.IndexCount == 0 || (options is not null && !flags.SurfaceEnabled)) continue;
                    if (surfaceBounds is not null && !OriginalFrustumVisibility.IsPotentiallyVisible(surfaceBounds[i], scene.World, raster)) continue;
                    float alpha = OriginalStyleMath.ResolveHostColors(range, current.Surface.Profile).EgColor.W;
                    bool both = binding.DoubleSided || (options is not null && flags.CullTransparentSurface && alpha < 1);
                    Draw(vb, i, 0, alpha, range.AlphaCutoff, binding.SphereMode, range.DiffuseTexture, binding.SphereTexture,
                        both ? CullMode.None : CullMode.Back, options is null,
                        options?.BlendMode == OriginalAlphaBlendMode.Additive, frameWriteDepth, options is null);
                }
                if (current.OutlineOptions is { Eligible: true, ModelWidth: > 0 } && current.Outline is { } outline)
                    for (int i = 0; i < outline.Materials.Count; i++)
                    {
                        var edge = CharacterOutlineMath.ResolveMaterial(outline.Materials[i], current.Materials[i].CharacterOutlineStyle);
                        if (!edge.DrawEnabled || current.Materials[i].OutlineWidthScale == 0 || scene.Materials[i].IndexCount == 0) continue;
                        var bounds = current.Materials[i].CharacterOutlineStyle is null ? edgeBounds : modernEdgeBounds;
                        if (bounds is not null)
                        {
                            var actualBounds = current.Materials[i].OutlineWidthScale == 1 ? bounds[i]
                                : D3D12OutlineDrawScale.MaterialBounds(current, i, current.Materials[i].CharacterOutlineStyle is null ? positions! : modernPositions!);
                            if (!OriginalFrustumVisibility.IsPotentiallyVisible(actualBounds, scene.World * raster)) continue;
                        }
                        if (current.Materials[i].CharacterOutlineStyle is not null)
                        {
                            var material = scene.Materials[i]; var binding = current.Surface.Materials[i];
                            float alpha = OriginalStyleMath.ResolveHostColors(material, current.Surface.Profile).EgColor.W;
                            Draw(modernEdgeVb!, i, 3, alpha, material.AlphaCutoff, binding.SphereMode,
                                material.DiffuseTexture, binding.SphereTexture, CullMode.Front,
                                options is null, options?.BlendMode == OriginalAlphaBlendMode.Additive, frameWriteDepth,
                                simple: options is null);
                        }
                        else Draw(groundVb!, i, 1, edge.Color.W, 0, OriginalSphereMode.None, -1, -1, CullMode.Front,
                            options is null, options?.BlendMode == OriginalAlphaBlendMode.Additive, frameWriteDepth);
                    }
            }
            if (!editorVisibility && scene.HasDeferredDeformation && activeSkinning is not null && currSurfGpu is not null)
                activeSkinning.CaptureTemporalSnapshot(current, currSurfGpu, currOrigExpGpu, currModExpGpu, currWidthSrcGpu);
        }
        commands.UnsetRenderTargets();
        var visibility = new D3D12GpuTarget("$editor-guide-depth", colorVisibility, colorDsv, width, height, depthFormat, true)
            { Initialized = true, StencilInitialized = stencil };
        if (editorVisibility) return (null, visibility);
        commands.ResourceBarrierTransition(depth!, ResourceStates.RenderTarget, D3D12TemporalInputs.ShaderReadState);
        commands.ResourceBarrierTransition(motion!, ResourceStates.RenderTarget, D3D12TemporalInputs.ShaderReadState);
        commands.ResourceBarrierTransition(reactive, ResourceStates.RenderTarget, D3D12TemporalInputs.ShaderReadState);
        return (new(depth!, motion!, width, height) { ReactiveMask = reactive }, visibility);
    }

    private static void ValidateCharacterPair(OriginalFrameScene current, OriginalFrameScene previous)
    {
        var a = current.Surface.Snapshot; var b = previous.Surface.Snapshot;
        if (a.VertexCount != b.VertexCount || !new D3D12IndexStreamIdentity(a).Equals(new D3D12IndexStreamIdentity(b)) || a.Materials.Count != b.Materials.Count)
            throw new NotSupportedException("Character temporal history requires stable vertex correspondence and triangle topology; reset after changes.");
        for (int i = 0; i < a.Materials.Count; i++)
            if (a.Materials[i].StartIndex != b.Materials[i].StartIndex || a.Materials[i].IndexCount != b.Materials[i].IndexCount)
                throw new NotSupportedException("Character temporal history requires stable material ranges; reset after changes.");
        if ((current.Outline is null) != (previous.Outline is null))
            throw new NotSupportedException("Character temporal history requires matching outline correspondence; reset after changes.");
        if (current.Options?.BackgroundColor is { W: < 1 } || previous.Options?.BackgroundColor is { W: < 1 })
            throw new NotSupportedException("Character temporal reconstruction requires an opaque background.");
        // EditorOverlay is metadata for the output-resolution overlay pass, not scene geometry.
    }

    private void DisposeCharacterInputs()
    {
        foreach (var item in characterPipelines.Values) { item.State.Dispose(); item.Root.Dispose(); }
        characterPipelines.Clear(); characterGpuSkinning?.Dispose(); characterGpuSkinning = null; characterCache?.Dispose(); characterCache = null;
        originalPositionsFirst=originalPositionsSecond=modernPositionsFirst=modernPositionsSecond=null;
        originalTemporalPositionCache.Clear();modernTemporalPositionCache.Clear();
    }
}
