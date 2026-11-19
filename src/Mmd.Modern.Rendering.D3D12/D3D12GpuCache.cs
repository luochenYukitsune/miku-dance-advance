using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Numerics;
using Mmd.Modern.Core;
using Vortice.Direct3D12;
using Vortice.DXGI;
namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Device-local cached streams and compiled state. Mutable vertices and staging use fence slots.</summary>
internal sealed class D3D12GpuCache:IDisposable
{
    private readonly D3D12GpuRuntime runtime;
    private readonly Dictionary<string,IDisposable> objects=[];
    private readonly Dictionary<string,(string Hash,ID3D12Resource Resource,object? Identity)> dynamicBuffers=[];
    // Fixed stream names bound the active cache. Replaced entries survive only through frame-fence leases.
    private readonly Dictionary<string,D3D12RetainedResource> replaceableObjects=[];
    private readonly D3D12BufferUploadStaging bufferUploadStaging;
    private readonly D3D12VertexBufferPool sceneVertexBuffers;
    private readonly D3D12VertexBufferPool expandedPositionBuffers;
    private long largeGiRetentionFrameId=-1;
    private bool retainLargeGiUploads;
    internal D3D12GpuCache(D3D12GpuRuntime runtime)
    {this.runtime=runtime;bufferUploadStaging=new(runtime);sceneVertexBuffers=new(runtime,bufferUploadStaging);expandedPositionBuffers=new(runtime,bufferUploadStaging);}
    internal D3D12BufferUploadStatistics BufferUploadStatistics=>bufferUploadStaging.Statistics;
    internal void BeginFrame(bool retainLargeGiUploads)
    {
        bufferUploadStaging.BeginFrame();
        largeGiRetentionFrameId=runtime.FrameId;this.retainLargeGiUploads=retainLargeGiUploads;
        if(!retainLargeGiUploads)bufferUploadStaging.ClearLargeRetainedPages();
    }
    internal D3D12SceneVertexBufferStatistics SceneVertexBufferStatistics=>sceneVertexBuffers.Statistics;
    internal D3D12SceneVertexBufferStatistics ExpandedPositionBufferStatistics=>expandedPositionBuffers.Statistics;
    private sealed record TextureIdentity(string Key,ID3D12Resource Resource);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SceneTexture,TextureIdentity> textureIdentities=new();
    // The same source bytes may be numerical UNorm and authored sRGB in one frame.
    // Separate identity and content namespaces prevent either role from changing the other.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SceneTexture,TextureIdentity> srgbColorTextureIdentities=new();
    internal int LiveResourceCount=>objects.Count+dynamicBuffers.Count+replaceableObjects.Count+sceneVertexBuffers.RetainedBufferCount+expandedPositionBuffers.RetainedBufferCount;
    internal ID3D12Resource ExpandedPositionBuffer(string stream,Vector4[] positions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);ArgumentNullException.ThrowIfNull(positions);
        if(!runtime.IsRecording)throw new InvalidOperationException("Expanded position uploads require an open GPU frame.");
        if(!D3D12ExpandedPositionRevision.TryGet(positions,out var revision)||!revision!.SupportsIncrementalUpdates)
        {
            expandedPositionBuffers.Remove(stream);
            return BufferByIdentity(stream,MemoryMarshal.AsBytes(positions.AsSpan()),ResourceStates.VertexAndConstantBuffer,positions);
        }
        RemoveReplaceable(stream);return expandedPositionBuffers.GetExpanded(stream,positions);
    }
    internal ID3D12Resource ExpandedPositionBuffer(string stream,D3D12ExpandedPositions positions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);ArgumentNullException.ThrowIfNull(positions);
        if(!runtime.IsRecording)throw new InvalidOperationException("Expanded position uploads require an open GPU frame.");
        if(positions.Length==0)throw new ArgumentException("An expanded buffer requires nonempty geometry.",nameof(positions));
        if(!positions.Revision.SupportsIncrementalUpdates)
        {
            expandedPositionBuffers.Remove(stream);
            return GetReplaceable(stream,positions,()=>CreateExpandedBuffer(positions));
        }
        RemoveReplaceable(stream);return expandedPositionBuffers.GetExpanded(stream,positions);
    }
    private ID3D12Resource CreateExpandedBuffer(D3D12ExpandedPositions positions)
    {
        var gpu=runtime.Device.CreateCommittedResource(HeapType.Default,ResourceDescription.Buffer(checked((ulong)positions.Length*16)),ResourceStates.CopyDest);
        try
        {
            ulong offset=0;
            for(int index=0;index<positions.ChunkCount;index++)
            {
                var bytes=MemoryMarshal.AsBytes(positions.GetChunk(index));var upload=bufferUploadStaging.Write(bytes);
                runtime.Commands.CopyBufferRegion(gpu,offset,upload.Resource,upload.Offset,(ulong)bytes.Length);offset+=(ulong)bytes.Length;
            }
            runtime.Commands.ResourceBarrierTransition(gpu,ResourceStates.CopyDest,ResourceStates.VertexAndConstantBuffer);
            runtime.BufferUploads++;return gpu;
        }
        catch{gpu.Dispose();throw;}
    }
    internal ID3D12Resource SceneVertexBuffer(string stream,SceneSnapshot scene)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);ArgumentNullException.ThrowIfNull(scene);
        if(!runtime.IsRecording)throw new InvalidOperationException("Scene vertex uploads require an open GPU frame.");
        if(!scene.VertexRevision.SupportsIncrementalUpdates)
        {
            // Public arbitrary deformation has no proven unchanged chunks. Preserve its
            // existing single immutable owner and global no-change reuse across slots.
            sceneVertexBuffers.Remove(stream);
            return BufferByIdentity(stream,MemoryMarshal.AsBytes(scene.Vertices),ResourceStates.VertexAndConstantBuffer,new D3D12VertexStreamIdentity(scene));
        }
        RemoveReplaceable(stream);return sceneVertexBuffers.Get(stream,scene);
    }
    internal T GetReplaceable<T>(string stream,object identity,Func<T> create) where T:class,IDisposable
    {
        if(TryGetReplaceable<T>(stream,identity,out var found))return found!;
        return Replace(stream,identity,create());
    }
    // Lend the previous immutable value only after leasing it through this recording fence.
    // The replacement factory may record GPU reads of that value before replacing its owner.
    internal T GetReplaceable<T>(string stream,object identity,Func<T?,T> create) where T:class,IDisposable
    {
        if(TryGetReplaceable<T>(stream,identity,out var found))return found!;
        T? previous=null;
        if(replaceableObjects.TryGetValue(stream,out var retained))
        {KeepForFrame(retained);previous=(T)retained.Value;}
        return Replace(stream,identity,create(previous));
    }
    private bool TryGetReplaceable<T>(string stream,object identity,out T? value) where T:class,IDisposable
    {
        if(!runtime.IsRecording)throw new InvalidOperationException("Replaceable GPU resources require an open frame.");
        if(replaceableObjects.TryGetValue(stream,out var found)&&Equals(found.Identity,identity))
        {KeepForFrame(found);value=(T)found.Value;return true;}
        value=null;return false;
    }
    private T Replace<T>(string stream,object identity,T value) where T:class,IDisposable
    {
        var retained=new D3D12RetainedResource(identity,value);
        if(replaceableObjects.Remove(stream,out var previous))previous.Dispose();
        replaceableObjects.Add(stream,retained);
        runtime.OnAbort(()=>
        {
            if(replaceableObjects.TryGetValue(stream,out var current)&&ReferenceEquals(current,retained))
            {replaceableObjects.Remove(stream);retained.Dispose();}
        });
        KeepForFrame(retained);return value;
    }
    private void KeepForFrame(D3D12RetainedResource retained)
    {
        if(retained.LastFrameId==runtime.FrameId)return;
        runtime.Own(retained.Lease());retained.LastFrameId=runtime.FrameId;
    }
    // Removing the cache owner never invalidates already recorded readers: every use
    // has its own fence lease. Optional identity retains compatible pool slots on resize.
    internal void RemoveReplaceable(string stream,object? keepIdentity=null)
    {
        if(!replaceableObjects.TryGetValue(stream,out var retained)
            ||keepIdentity is not null&&Equals(retained.Identity,keepIdentity))return;
        replaceableObjects.Remove(stream);retained.Dispose();
    }
    internal ID3D12Resource BufferByIdentity(string stream,ReadOnlySpan<byte> bytes,ResourceStates finalState,object identity)
    {
        if(TryGetReplaceable<ID3D12Resource>(stream,identity,out var found))return found!;
        return CreateIdentityBuffer(stream,bytes,finalState,identity);
    }
    private ID3D12Resource CreateIdentityBuffer(string stream,ReadOnlySpan<byte> bytes,ResourceStates finalState,object identity)
    {
        // Honor the renderer's policy only for its current frame. Direct cache callers
        // that do not call BeginFrame retain the established GI upload behavior.
        bool retainLargeUpload=stream=="gi:triangles"&&(largeGiRetentionFrameId!=runtime.FrameId||retainLargeGiUploads);
        return Replace(stream,identity,CreateBuffer(bytes,finalState,retainLargeUpload));
    }
    /// <summary>Defers CPU stream assembly until an immutable payload actually changes.</summary>
    internal ID3D12Resource BufferByIdentity<T>(string stream,ResourceStates finalState,object identity,Func<T[]> create) where T:unmanaged
    {
        if(TryGetReplaceable<ID3D12Resource>(stream,identity,out var found))return found!;
        var values=create();
        return CreateIdentityBuffer(stream,MemoryMarshal.AsBytes(values.AsSpan()),finalState,identity);
    }
    internal delegate ReadOnlySpan<byte> BufferDataFactory();
    // The upload consumes the borrowed bytes before returning and keeps only the GPU resource.
    internal ID3D12Resource DeferredBufferByIdentity(string stream,ResourceStates finalState,object identity,BufferDataFactory create)
    {
        if(TryGetReplaceable<ID3D12Resource>(stream,identity,out var found))return found!;
        return CreateIdentityBuffer(stream,create(),finalState,identity);
    }
    /// <summary>One immutable topology owner; old resources remain fence-leased while in flight.</summary>
    internal ID3D12Resource IndexBuffer(SceneSnapshot scene)
        =>BufferByIdentity("immutable-triangle-indices",MemoryMarshal.AsBytes(scene.Indices),ResourceStates.IndexBuffer,new D3D12IndexStreamIdentity(scene));
    /// <summary>Build input used only by the current frame. It is not retained in a content-addressed cache.</summary>
    internal ID3D12Resource FrameBuffer(ReadOnlySpan<byte> bytes,ResourceStates finalState)
        =>runtime.Own(CreateBuffer(bytes,finalState));
    /// <summary>The synchronous writer fills a frame-owned upload range before any GPU copy is recorded.</summary>
    internal ID3D12Resource FrameBuffer(int byteCount,ResourceStates finalState,D3D12BufferUploadWriter writer)
    {
        if(byteCount<=0)throw new ArgumentOutOfRangeException(nameof(byteCount));
        ArgumentNullException.ThrowIfNull(writer);
        if(!runtime.IsRecording)throw new InvalidOperationException("GPU uploads require an open frame.");
        var gpu=runtime.Device.CreateCommittedResource(HeapType.Default,ResourceDescription.Buffer((ulong)byteCount),ResourceStates.CopyDest);
        try
        {
            var upload=bufferUploadStaging.Write(byteCount,writer);
            runtime.Commands.CopyBufferRegion(gpu,0,upload.Resource,upload.Offset,(ulong)byteCount);
            runtime.Commands.ResourceBarrierTransition(gpu,ResourceStates.CopyDest,finalState);
            runtime.Own(gpu);runtime.BufferUploads++;return gpu;
        }
        catch{gpu.Dispose();throw;}
    }
    private ID3D12Resource CreateBuffer(ReadOnlySpan<byte> bytes,ResourceStates finalState,bool retainLargeUpload=false)
    {
        if(!runtime.IsRecording)throw new InvalidOperationException("GPU uploads require an open frame.");
        var gpu=runtime.Device.CreateCommittedResource(HeapType.Default,ResourceDescription.Buffer((ulong)bytes.Length),ResourceStates.CopyDest);
        try
        {
            var upload=retainLargeUpload?bufferUploadStaging.WriteLargeRetained(bytes):bufferUploadStaging.Write(bytes);
            runtime.Commands.CopyBufferRegion(gpu,0,upload.Resource,upload.Offset,(ulong)bytes.Length);runtime.Commands.ResourceBarrierTransition(gpu,ResourceStates.CopyDest,finalState);
            runtime.BufferUploads++;return gpu;
        }
        catch{gpu.Dispose();throw;}
    }
    internal T Get<T>(string key,Func<T> create) where T:class,IDisposable
    {
        if(objects.TryGetValue(key,out var value))return (T)value;
        var created=create();objects.Add(key,created);
        runtime.OnAbort(()=>{if(objects.Remove(key,out var aborted))aborted.Dispose();});
        return created;
    }
    internal ID3D12Resource Buffer(ReadOnlySpan<byte> bytes,ResourceStates finalState)
    {
        var key=$"buffer:{finalState}:{Convert.ToHexString(SHA256.HashData(bytes))}";
        if(objects.TryGetValue(key,out var found))return (ID3D12Resource)found;
        var gpu=CreateBuffer(bytes,finalState);
        objects.Add(key,gpu);
        runtime.OnAbort(()=>{if(objects.Remove(key,out var aborted))aborted.Dispose();});return gpu;
    }
    internal ID3D12Resource Texture(SceneTexture texture)
    {
        if(textureIdentities.TryGetValue(texture,out var identity))
        {
            if(objects.TryGetValue(identity.Key,out var live)&&ReferenceEquals(live,identity.Resource))return identity.Resource;
            textureIdentities.Remove(texture);
        }
        string key=$"texture:{texture.Width}:{texture.Height}:{texture.RgbaSha256}";
        if(objects.TryGetValue(key,out var found)){textureIdentities.Add(texture,new(key,(ID3D12Resource)found));return (ID3D12Resource)found;}
        var desc=ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm,(uint)texture.Width,(uint)texture.Height,mipLevels:1);
        var gpu=runtime.Device.CreateCommittedResource(HeapType.Default,desc,ResourceStates.CopyDest);
        var layouts=new PlacedSubresourceFootPrint[1];var rows=new int[1];var sizes=new ulong[1];
        runtime.Device.GetCopyableFootprints(desc,0,1,0,layouts,rows,sizes,out ulong size);
        var upload=runtime.Own(runtime.Device.CreateCommittedResource(HeapType.Upload,ResourceDescription.Buffer(size),ResourceStates.GenericRead));
        var mapped=upload.Map<byte>(0,checked((int)size));
        for(int y=0;y<texture.Height;y++)texture.Rgba.Slice(y*texture.Width*4,texture.Width*4).CopyTo(mapped.Slice(y*layouts[0].Footprint.RowPitch,texture.Width*4));
        upload.Unmap(0);runtime.Commands.CopyTextureRegion(new TextureCopyLocation(gpu,0),0,0,0,new TextureCopyLocation(upload,layouts[0]));
        runtime.Commands.ResourceBarrierTransition(gpu,ResourceStates.CopyDest,ResourceStates.PixelShaderResource);
        objects.Add(key,gpu);textureIdentities.Add(texture,new(key,gpu));runtime.TextureUploads++;
        runtime.OnAbort(()=>{textureIdentities.Remove(texture);if(objects.Remove(key,out var aborted))aborted.Dispose();});return gpu;
    }
    /// <summary>MatCap authored color role. Hardware sRGB decode precedes bilinear filtering;
    /// alpha remains linear. Linear color reuses the unchanged numerical UNorm resource.</summary>
    internal ID3D12Resource TextureColor(SceneTexture texture,D3D12MatcapColorEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if(!Enum.IsDefined(encoding))throw new ArgumentOutOfRangeException(nameof(encoding));
        if(encoding==D3D12MatcapColorEncoding.LinearRgb)return Texture(texture);
        if(srgbColorTextureIdentities.TryGetValue(texture,out var identity))
        {
            if(objects.TryGetValue(identity.Key,out var live)&&ReferenceEquals(live,identity.Resource))return identity.Resource;
            srgbColorTextureIdentities.Remove(texture);
        }
        string key=$"texture-color-srgb:{texture.Width}:{texture.Height}:{texture.RgbaSha256}";
        if(objects.TryGetValue(key,out var found))
        {
            srgbColorTextureIdentities.Add(texture,new(key,(ID3D12Resource)found));return (ID3D12Resource)found;
        }
        var desc=ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm_SRgb,(uint)texture.Width,(uint)texture.Height,mipLevels:1);
        var gpu=runtime.Device.CreateCommittedResource(HeapType.Default,desc,ResourceStates.CopyDest);
        try
        {
            var layouts=new PlacedSubresourceFootPrint[1];var rows=new int[1];var sizes=new ulong[1];
            runtime.Device.GetCopyableFootprints(desc,0,1,0,layouts,rows,sizes,out ulong size);
            var upload=runtime.Own(runtime.Device.CreateCommittedResource(HeapType.Upload,ResourceDescription.Buffer(size),ResourceStates.GenericRead));
            var mapped=upload.Map<byte>(0,checked((int)size));
            for(int y=0;y<texture.Height;y++)texture.Rgba.Slice(y*texture.Width*4,texture.Width*4).CopyTo(mapped.Slice(y*layouts[0].Footprint.RowPitch,texture.Width*4));
            upload.Unmap(0);runtime.Commands.CopyTextureRegion(new TextureCopyLocation(gpu,0),0,0,0,new TextureCopyLocation(upload,layouts[0]));
            runtime.Commands.ResourceBarrierTransition(gpu,ResourceStates.CopyDest,ResourceStates.PixelShaderResource);
            objects.Add(key,gpu);srgbColorTextureIdentities.Add(texture,new(key,gpu));runtime.TextureUploads++;
            runtime.OnAbort(()=>{srgbColorTextureIdentities.Remove(texture);if(objects.Remove(key,out var aborted))aborted.Dispose();});return gpu;
        }
        catch{gpu.Dispose();throw;}
    }
    // One mutable stream per slot. Camera-dependent edge geometry cannot grow an unbounded content cache.
    internal ID3D12Resource DynamicBuffer(string stream,ReadOnlySpan<byte> bytes,ResourceStates finalState,object? identity=null)
    {
        string key=$"{stream}:{runtime.SlotIndex}";
        if(identity is not null&&dynamicBuffers.TryGetValue(key,out var same)&&ReferenceEquals(same.Identity,identity))return same.Resource;
        string hash=Convert.ToHexString(SHA256.HashData(bytes));
        if(dynamicBuffers.TryGetValue(key,out var previous)&&previous.Hash==hash){dynamicBuffers[key]=(hash,previous.Resource,identity);return previous.Resource;}
        var gpu=CreateBuffer(bytes,finalState);
        // The same stream can be replaced more than once in one command list. Its earlier
        // draws/copies still reference the old buffer until this submission's fence retires.
        if(previous.Resource is not null)runtime.Own(previous.Resource);
        dynamicBuffers[key]=(hash,gpu,identity);
        runtime.OnAbort(()=>{if(dynamicBuffers.Remove(key,out var aborted))aborted.Resource.Dispose();});return gpu;
    }
    public void Dispose(){foreach(var value in replaceableObjects.Values)value.Dispose();replaceableObjects.Clear();foreach(var value in dynamicBuffers.Values)value.Resource.Dispose();dynamicBuffers.Clear();foreach(var value in objects.Values)value.Dispose();objects.Clear();sceneVertexBuffers.Dispose();expandedPositionBuffers.Dispose();bufferUploadStaging.Dispose();}
}

/// <summary>Snapshot topology arrays are private and immutable. Exact span origin permits constant-time reuse.</summary>
internal static class D3D12ImmutableGeometry
{
    internal static bool SameStorage<T>(ReadOnlySpan<T> first,ReadOnlySpan<T> second)
        =>first.Length==second.Length&&(first.IsEmpty||first.Overlaps(second,out int offset)&&offset==0);
}
internal readonly struct D3D12VertexStreamIdentity(SceneSnapshot scene):IEquatable<D3D12VertexStreamIdentity>
{
    private readonly SceneSnapshot scene=scene;
    public bool Equals(D3D12VertexStreamIdentity other)=>scene is null||other.scene is null?ReferenceEquals(scene,other.scene):ReferenceEquals(scene.VertexRevision,other.scene.VertexRevision);
    public override bool Equals(object? value)=>value is D3D12VertexStreamIdentity other&&Equals(other);
    public override int GetHashCode()=>scene is null?0:scene.VertexCount;
}
internal readonly struct D3D12IndexStreamIdentity(SceneSnapshot scene):IEquatable<D3D12IndexStreamIdentity>
{
    private readonly SceneSnapshot scene=scene;
    public bool Equals(D3D12IndexStreamIdentity other)=>scene is null||other.scene is null?ReferenceEquals(scene,other.scene)
        :D3D12ImmutableGeometry.SameStorage(scene.Indices,other.scene.Indices)||scene.Indices.SequenceEqual(other.scene.Indices);
    public override bool Equals(object? value)=>value is D3D12IndexStreamIdentity other&&Equals(other);
    public override int GetHashCode()=>scene is null?0:scene.Indices.Length;
}
internal readonly record struct D3D12TemporalVertexIdentity(D3D12VertexStreamIdentity Current,D3D12VertexStreamIdentity Previous)
{
    internal D3D12TemporalVertexIdentity(SceneSnapshot current,SceneSnapshot previous):this(new D3D12VertexStreamIdentity(current),new D3D12VertexStreamIdentity(previous)){}
}
