using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Core;

public enum OriginalOutlineVertexMode { Expand, Inward }

/// <summary>Already deformed model-space inputs. Normal is raw, including non-unit and zero normals.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct OriginalOutlineVertex(Vector3 Position,Vector3 Normal,OriginalOutlineVertexMode Mode,float VertexScale,int SelectedMaterialIndex);
internal delegate void OriginalOutlineRangeWriter(int rangeIndex, Span<OriginalOutlineVertex> destination);
internal delegate void OriginalOutlineVertexWriter(Span<OriginalOutlineVertex> destination);
public sealed record OriginalOutlineMaterial(float Width,Vector4 Color,bool DrawEnabled=true);

/// <summary>Explicit selected inputs, not original UI defaults. DistanceTransform uses homogeneous row vectors without divide.</summary>
public sealed record OriginalOutlineOptions(Vector4 ReferencePoint,Matrix4x4 DistanceTransform,Vector3 CameraPosition,float FovDegrees,float ModelWidth,bool Eligible=true);

/// <summary>Immutable outline snapshot. SelectedMaterialIndex is established at load and survives material-width morphs.</summary>
public sealed class OriginalOutlineData
{
    private readonly OwnedVertexChunks<OriginalOutlineVertex>? eager;
    private readonly Lazy<OwnedVertexChunks<OriginalOutlineVertex>>? deferred;
    private readonly int count;
    private OwnedVertexChunks<OriginalOutlineVertex> vertices => eager ?? deferred!.Value;
    /// <summary>No edge geometry, paired only with an explicitly empty scene snapshot.</summary>
    public static OriginalOutlineData Empty { get; } = new();
    public ReadOnlyCollection<OriginalOutlineVertex> Vertices { get; }
    /// <summary>Reads immutable owned geometry without creating a compatibility flat array.</summary>
    public int VertexCount => count;
    public int VertexChunkCount => count / OwnedVertexChunks<OriginalOutlineVertex>.ChunkSize + (count % OwnedVertexChunks<OriginalOutlineVertex>.ChunkSize == 0 ? 0 : 1);
    public ReadOnlySpan<OriginalOutlineVertex> GetVertexChunk(int chunkIndex) => vertices.GetChunk(chunkIndex);
    /// <summary>Conservative immutable outline lineage; material-only updates share this identity.</summary>
    public SceneVertexRevision VertexRevision => SceneVertexRevision.For(Vertices, Vertices.Count);
    public ReadOnlyCollection<OriginalOutlineMaterial> Materials { get; }
    /// <summary>Deferred surface whose DeformationRanges supply this outline's positions and raw normals at the same
    /// vertex indices. Reading those vertices evaluates the surface's CPU reference. Null for ordinary outlines.</summary>
    public SceneSnapshot? DeferredSurface { get; }
    // Eager owner of the mode, scale and selected material of every vertex and of all non-deferred positions.
    private readonly OriginalOutlineData? deferredBase;
    private OriginalOutlineData? staticView;
    /// <summary>These materials over the eager owner: exact outside the deferred ranges, placeholders inside them.</summary>
    public OriginalOutlineData StaticView => deferredBase is not { } owner ? this
        : Volatile.Read(ref staticView) ?? Interlocked.CompareExchange(ref staticView, new(owner.vertices, owner.Vertices, Materials), null) ?? staticView!;
    private OriginalOutlineData()
    { eager=new([]);Vertices=new(eager);Materials=Array.AsReadOnly<OriginalOutlineMaterial>([]); }
    public OriginalOutlineData(IEnumerable<OriginalOutlineVertex> vertices,IEnumerable<OriginalOutlineMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(vertices); ArgumentNullException.ThrowIfNull(materials);
        var ownedVertices=vertices.ToArray(); var ownedMaterials=materials.ToArray();
        ValidateContents(ownedVertices,ownedMaterials);
        eager=new(ownedVertices);count=eager.Count;Vertices=new(eager); Materials=Array.AsReadOnly(ownedMaterials);
    }
    /// <summary>Core allocates the only output vertex array. The synchronous writer receives its
    /// full bounded span, never an owned array; publication retains every ordinary outline check.</summary>
    internal static OriginalOutlineData CreateGenerated(int vertexCount,IEnumerable<OriginalOutlineMaterial> materials,
        OriginalOutlineVertexWriter write)
    {
        ArgumentNullException.ThrowIfNull(materials); ArgumentNullException.ThrowIfNull(write);
        if(vertexCount<=0)throw new ArgumentOutOfRangeException(nameof(vertexCount));
        var ownedMaterials=materials.ToArray();
        var ownedVertices=new OriginalOutlineVertex[vertexCount];
        write(ownedVertices);
        ValidateContents(ownedVertices,ownedMaterials);
        var storage=new OwnedVertexChunks<OriginalOutlineVertex>(ownedVertices);
        return new(storage,new(storage),Array.AsReadOnly(ownedMaterials));
    }
    private static void ValidateContents(ReadOnlySpan<OriginalOutlineVertex> vertices,IReadOnlyList<OriginalOutlineMaterial> materials)
    {
        if(vertices.Length==0 || materials.Count==0) throw new ArgumentException("Outline requires vertices and materials.");
        foreach(var m in materials)
            if(m is null || !float.IsFinite(m.Width) || m.Width<0 || !OriginalOutlineMath.Finite(m.Color)) throw new ArgumentException("Invalid outline material.");
        foreach(var v in vertices)
            if(!OriginalOutlineMath.Finite(new Vector4(v.Position,0)) || !OriginalOutlineMath.Finite(new Vector4(v.Normal,0))
                || !Enum.IsDefined(v.Mode) || !float.IsFinite(v.VertexScale) || v.VertexScale<0 || v.SelectedMaterialIndex<0 || v.SelectedMaterialIndex>=materials.Count)
                throw new ArgumentException("Invalid outline vertex.");
    }
    /// <summary>Updates only material values while retaining already validated immutable geometry.</summary>
    internal OriginalOutlineData WithMaterials(IReadOnlyList<OriginalOutlineMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        if(materials.Count!=Materials.Count)throw new ArgumentException("Material updates must preserve outline material count.",nameof(materials));
        static bool Bits(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
        bool same=true;
        for(int i=0;i<materials.Count;i++)
        {
            var next=materials[i];var prior=Materials[i];
            if(next is null||!float.IsFinite(next.Width)||next.Width<0||!OriginalOutlineMath.Finite(next.Color))
                throw new ArgumentException("Invalid outline material.",nameof(materials));
            same&=next.DrawEnabled==prior.DrawEnabled&&Bits(next.Width,prior.Width)
                &&Bits(next.Color.X,prior.Color.X)&&Bits(next.Color.Y,prior.Color.Y)
                &&Bits(next.Color.Z,prior.Color.Z)&&Bits(next.Color.W,prior.Color.W);
        }
        if(same)return this;
        var replaced=Array.AsReadOnly(materials.ToArray());
        return deferredBase is null?new(vertices,Vertices,replaced):new(this,replaced);
    }
    /// <summary>Takes positions and raw normals of the surface's deferred ranges from that surface, without
    /// evaluating it. Mode, scale, selected material and every other range keep this outline's validated values.</summary>
    internal OriginalOutlineData WithDeferredSurface(SceneSnapshot surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var owner=deferredBase??this;
        if(surface.VertexCount!=VertexCount)throw new ArgumentException("Surface updates must preserve outline vertex count.",nameof(surface));
        if(!surface.HasDeferredDeformation)return ReferenceEquals(owner,this)?this:new(owner.vertices,owner.Vertices,Materials);
        return new(owner,surface,Materials);
    }
    private OriginalOutlineData(OriginalOutlineData owner,SceneSnapshot surface,ReadOnlyCollection<OriginalOutlineMaterial> materials)
    {
        deferredBase=owner;DeferredSurface=surface;count=owner.count;Materials=materials;
        var bounds=surface.DeformationRanges.Select(range=>(range.Start,range.Count)).ToArray();
        deferred=new(()=>owner.vertices.WithRanges(bounds,(index,offset,destination)=>
        {
            int start=bounds[index].Start+offset;
            var source=surface.GetVertexChunk(start/OwnedVertexChunks<SceneVertex>.ChunkSize)
                .Slice(start%OwnedVertexChunks<SceneVertex>.ChunkSize,destination.Length);
            for(int i=0;i<destination.Length;i++)destination[i]=destination[i] with{Position=source[i].Position,Normal=source[i].Normal};
        }),LazyThreadSafetyMode.ExecutionAndPublication);
        Vertices=new(new DeferredVertexList<OriginalOutlineVertex>(count,()=>deferred.Value));
        SceneVertexRevision.RegisterComposed(owner.Vertices,Vertices,count,bounds);
    }
    private OriginalOutlineData(OriginalOutlineData source,ReadOnlyCollection<OriginalOutlineMaterial> materials)
    {
        eager=source.eager;deferred=source.deferred;count=source.count;Vertices=source.Vertices;
        deferredBase=source.deferredBase;DeferredSurface=source.DeferredSurface;Materials=materials;
    }
    /// <summary>Compatibility writer for complete actor ranges. Product composition uses the
    /// chunk writer below; this adapter allocates only each explicitly requested range.</summary>
    internal OriginalOutlineData WithComposedVertices(IReadOnlyList<(int Start, int Count)> ranges,
        OriginalOutlineRangeWriter write, IReadOnlyList<OriginalOutlineMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(ranges); ArgumentNullException.ThrowIfNull(write);
        var updates=ranges.ToArray();var replacements=new OriginalOutlineVertex[updates.Length][];
        var owner=deferredBase??this;
        return WithComposedVertexChunks(updates,(range,offset,destination)=>
        {
            var replacement=replacements[range];
            if(replacement is null)
            {
                replacement=new OriginalOutlineVertex[updates[range].Count];
                owner.vertices.CopyTo(updates[range].Start,replacement);write(range,replacement);replacements[range]=replacement;
            }
            replacement.AsSpan(offset,destination.Length).CopyTo(destination);
        },materials);
    }
    /// <summary>Clones each touched immutable chunk once and writes bounded portions of actor
    /// ranges. Unchanged bytes and all prior published collections remain immutable.</summary>
    internal OriginalOutlineData WithComposedVertexChunks(IReadOnlyList<(int Start, int Count)> ranges,
        OwnedVertexRangeWriter<OriginalOutlineVertex> write, IReadOnlyList<OriginalOutlineMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(ranges); ArgumentNullException.ThrowIfNull(write); ArgumentNullException.ThrowIfNull(materials);
        if (materials.Count != Materials.Count) throw new ArgumentException("Composition must preserve outline material count.", nameof(materials));
        var nextMaterials = materials.SequenceEqual(Materials) ? Materials : Array.AsReadOnly(materials.ToArray());
        foreach (var m in nextMaterials)
            if (m is null || !float.IsFinite(m.Width) || m.Width < 0 || !OriginalOutlineMath.Finite(m.Color))
                throw new ArgumentException("Invalid outline material.", nameof(materials));
        var updates=ranges.ToArray();
        // A deferred outline composes its eager owner; the caller re-attaches the deferred surface.
        var owner=deferredBase??this;
        var composed=owner.vertices.WithRanges(updates,(range,offset,destination)=>
        {
            write(range,offset,destination);
            foreach (var v in destination)
                if (!OriginalOutlineMath.Finite(new Vector4(v.Position, 0)) || !OriginalOutlineMath.Finite(new Vector4(v.Normal, 0))
                    || !Enum.IsDefined(v.Mode) || !float.IsFinite(v.VertexScale) || v.VertexScale < 0
                    || v.SelectedMaterialIndex < 0 || v.SelectedMaterialIndex >= nextMaterials.Count)
                    throw new ArgumentException("Invalid composed outline vertex.", nameof(write));
        });
        if(ReferenceEquals(composed,owner.vertices))return ReferenceEquals(nextMaterials,owner.Materials)?owner:new(owner.vertices,owner.Vertices,nextMaterials);
        var nextVertices = new ReadOnlyCollection<OriginalOutlineVertex>(composed);
        SceneVertexRevision.RegisterComposed(owner.Vertices, nextVertices, composed.Count, updates);
        return new(composed,nextVertices,nextMaterials);
    }
    /// <summary>Refresh position and raw normal from an already validated immutable surface.
    /// The owned outline mode, scale and selected material remain unchanged and validated;
    /// no external writer or caller-owned vertex storage enters the parallel copy path.</summary>
    internal OriginalOutlineData WithSurfaceVertexChunks(SceneSnapshot surface,
        IReadOnlyList<(int Start, int Count)> ranges, IReadOnlyList<OriginalOutlineMaterial> materials, int maxDegree)
    {
        ArgumentNullException.ThrowIfNull(surface); ArgumentNullException.ThrowIfNull(ranges); ArgumentNullException.ThrowIfNull(materials);
        if (surface.VertexCount != VertexCount)
            throw new ArgumentException("Surface updates must preserve outline vertex count.", nameof(surface));
        var ownedMaterials = materials.ToArray();
        if (ownedMaterials.Length != Materials.Count)
            throw new ArgumentException("Composition must preserve outline material count.", nameof(materials));
        foreach (var material in ownedMaterials)
            if (material is null || !float.IsFinite(material.Width) || material.Width < 0 || !OriginalOutlineMath.Finite(material.Color))
                throw new ArgumentException("Invalid outline material.", nameof(materials));
        var nextMaterials = ownedMaterials.SequenceEqual(Materials) ? Materials : Array.AsReadOnly(ownedMaterials);
        var updates = ranges.ToArray();
        var owner = deferredBase ?? this;
        var composed = owner.vertices.WithTrustedParallelRanges(updates, (range, offset, destination) =>
        {
            int start = updates[range].Start + offset;
            var source = surface.GetVertexChunk(start / OwnedVertexChunks<SceneVertex>.ChunkSize)
                .Slice(start % OwnedVertexChunks<SceneVertex>.ChunkSize, destination.Length);
            for (int index = 0; index < destination.Length; index++)
            {
                var vertex = source[index];
                destination[index] = destination[index] with { Position = vertex.Position, Normal = vertex.Normal };
            }
        }, maxDegree);
        if (ReferenceEquals(composed, owner.vertices))
            return ReferenceEquals(nextMaterials, owner.Materials) ? owner : new(owner.vertices, owner.Vertices, nextMaterials);
        var nextVertices = new ReadOnlyCollection<OriginalOutlineVertex>(composed);
        SceneVertexRevision.RegisterComposed(owner.Vertices, nextVertices, composed.Count, updates);
        return new(composed, nextVertices, nextMaterials);
    }
    private OriginalOutlineData(OwnedVertexChunks<OriginalOutlineVertex> storage,ReadOnlyCollection<OriginalOutlineVertex> vertices, ReadOnlyCollection<OriginalOutlineMaterial> materials)
    { eager=storage;count=storage.Count;Vertices = vertices; Materials = materials; }
}

/// <summary>Independent implementation of the documented original outline input/output contract.</summary>
public static class OriginalOutlineMath
{
    public static float ComputeModelBase(OriginalOutlineOptions options)
    {
        ValidateOptions(options);
        var p=options.ReferencePoint; var m=options.DistanceTransform;
        float x=Dot4(p,m.M11,m.M21,m.M31,m.M41),y=Dot4(p,m.M12,m.M22,m.M32,m.M42),z=Dot4(p,m.M13,m.M23,m.M33,m.M43);
        float dx=Subtract(x,options.CameraPosition.X),dy=Subtract(y,options.CameraPosition.Y),dz=Subtract(z,options.CameraPosition.Z);
        float squared=Add(Add(Multiply(dx,dx),Multiply(dy,dy)),Multiply(dz,dz));
        float distance=MathF.Sqrt(squared);
        float width=Multiply(distance,4.5e-5f);
        float fov=Add(Multiply(options.FovDegrees,.6f),1);
        width=Multiply(width,fov);
        return Multiply(width,options.ModelWidth);
    }

    public static Vector3 ExpandPosition(OriginalOutlineVertex vertex,float modelBase,float currentSelectedWidth)
    {
        if(vertex.Mode==OriginalOutlineVertexMode.Inward)
            return new(Subtract(vertex.Position.X,Multiply(vertex.Normal.X,.005f)),Subtract(vertex.Position.Y,Multiply(vertex.Normal.Y,.005f)),Subtract(vertex.Position.Z,Multiply(vertex.Normal.Z,.005f)));
        if(vertex.Mode!=OriginalOutlineVertexMode.Expand) throw new ArgumentOutOfRangeException(nameof(vertex));
        float Offset(float n)=>Multiply(Multiply(Multiply(n,modelBase),vertex.VertexScale),currentSelectedWidth);
        return new(Add(vertex.Position.X,Offset(vertex.Normal.X)),Add(vertex.Position.Y,Offset(vertex.Normal.Y)),Add(vertex.Position.Z,Offset(vertex.Normal.Z)));
    }

    public static int[] SelectVertexMaterialsAtLoad(int vertexCount,ReadOnlySpan<uint> indices,ReadOnlySpan<int> materialIndexCounts,ReadOnlySpan<float> initialWidths)
    {
        if(vertexCount<=0 || materialIndexCounts.Length==0 || materialIndexCounts.Length!=initialWidths.Length) throw new ArgumentException("Invalid outline selection dimensions.");
        int next=0;
        foreach(int count in materialIndexCounts)
        {
            if(count<0 || count%3!=0 || count>indices.Length-next) throw new ArgumentException("Outline material ranges must partition triangle indices.");
            next+=count;
        }
        if(next!=indices.Length) throw new ArgumentException("Outline material ranges do not cover indices.");
        foreach(uint i in indices) if(i>=vertexCount) throw new ArgumentException("Outline index outside vertex array.");
        foreach(float width in initialWidths) if(!float.IsFinite(width)||width<0) throw new ArgumentException("Finite nonnegative outline widths are required.");
        var selected=new int[vertexCount]; var smallest=new float[vertexCount];
        Array.Fill(smallest,BitConverter.Int32BitsToSingle(0x4479f333));
        next=0;
        for(int material=0;material<initialWidths.Length;material++)
        {
            int end=next+materialIndexCounts[material];
            for(int i=next;i<end;i++)
            {
                int vertex=(int)indices[i];
                if(initialWidths[material]<smallest[vertex]) { smallest[vertex]=initialWidths[material]; selected[vertex]=material; }
            }
            next=end;
        }
        return selected;
    }

    /// <summary>16-byte model-space position stream, shared across material draws. Alpha/lighting are not baked into geometry.</summary>
    public static Vector4[] BuildPositions(OriginalOutlineData data,OriginalOutlineOptions options)
    {
        ArgumentNullException.ThrowIfNull(data);
        float modelBase=ComputeModelBase(options);
        var result=new Vector4[data.Vertices.Count];
        for(int i=0;i<result.Length;i++)
        {
            var v=data.Vertices[i];
            result[i]=new(ExpandPosition(v,modelBase,data.Materials[v.SelectedMaterialIndex].Width),1);
        }
        return result;
    }

    // Explicit scalar boundaries prevent SIMD Transform/FMA from changing the binary32 operation sequence.
    private static float Dot4(Vector4 p,float x,float y,float z,float w)=>Add(Add(Add(Multiply(p.X,x),Multiply(p.Y,y)),Multiply(p.Z,z)),Multiply(p.W,w));
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Multiply(float a,float b)=>a*b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Add(float a,float b)=>a+b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Subtract(float a,float b)=>a-b;
    internal static bool Finite(Vector4 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z)&&float.IsFinite(v.W);
    internal static void ValidateOptions(OriginalOutlineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var m=options.DistanceTransform;
        if(!Finite(options.ReferencePoint)||!Finite(new(options.CameraPosition,0))||!float.IsFinite(options.FovDegrees)||options.FovDegrees<0
            ||!float.IsFinite(options.ModelWidth)||options.ModelWidth<0
            ||!Finite(new(m.M11,m.M12,m.M13,m.M14))||!Finite(new(m.M21,m.M22,m.M23,m.M24))
            ||!Finite(new(m.M31,m.M32,m.M33,m.M34))||!Finite(new(m.M41,m.M42,m.M43,m.M44)))
            throw new ArgumentException("Outline reference, transform, camera, degrees and width must be explicit finite inputs with nonnegative degrees/width.");
    }
}
