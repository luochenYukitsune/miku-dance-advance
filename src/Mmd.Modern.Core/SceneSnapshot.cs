using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Core;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct SceneVertex(Vector3 Position, Vector3 Normal, Vector2 Uv);
internal delegate void SceneVertexRangeWriter(int rangeIndex, Span<SceneVertex> destination);

/// <summary>Texture provenance, independent of its display name or file extension.</summary>
public enum SceneTextureKind { Image, IndexedProceduralToon, ModernSoftToon }

/// <summary>RGBA8 linear UNorm pixels. Both constructor input and public access are read-only snapshots.</summary>
public sealed class SceneTexture
{
    private readonly byte[] pixels;
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public SceneTextureKind Kind { get; }
    public ReadOnlySpan<byte> Rgba => pixels;
    public SceneTexture(string name, int width, int height, ReadOnlySpan<byte> rgba, SceneTextureKind kind = SceneTextureKind.Image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || rgba.Length != (long)width * height * 4)
            throw new ArgumentException("Texture must contain width * height * 4 RGBA8 bytes (dimensions 1..4096).");
        Name = name; Width = width; Height = height; Kind = kind; pixels = rgba.ToArray();
    }
    public static SceneTexture ProceduralToon(int preset = 1) => new($"procedural-toon-{preset}", ToonRamp.Entries, 1, ToonRamps.Get(preset).Rgba.Span, SceneTextureKind.IndexedProceduralToon);
}

public sealed record SceneMaterial(string Name, int StartIndex, int IndexCount, Vector4 Diffuse,
    Vector3 Specular, Vector3 Ambient, float Shininess, int DiffuseTexture = -1, int ToonTexture = -1,
    Vector4? ToonUvTransform = null, float AlphaCutoff = 1f / 255)
{
    /// <summary>Nonnegative linear secondary radiance; the original raster surface has no general emission term.</summary>
    public Vector3 Emission { get; init; } = Vector3.Zero;
}

/// <summary>System.Numerics row-vector camera; renderer passes View * Projection without transposing.</summary>
public sealed record SceneCamera(Vector3 Eye, Vector3 Target, Vector3 Up, float OrthographicHeight, float Near, float Far,
    float? VerticalFovDegrees=null,bool LeftHanded=false)
{
    public static SceneCamera Default { get; } = new(new(0, 9, -30), new(0, 8.5f, 0), Vector3.UnitY, 23, .1f, 100);
    /// <summary>Projection-only displacement in output pixels: positive X is right, positive Y is down.</summary>
    public Vector2 ProjectionJitterPixels { get; init; } = Vector2.Zero;

    public Matrix4x4 ViewProjection(int width, int height)
    {
        if(width<=0||height<=0)throw new ArgumentOutOfRangeException(nameof(width));
        Validate();
        Matrix4x4 viewProjection;
        if(LeftHanded)viewProjection=Matrix4x4.CreateLookAtLeftHanded(Eye,Target,Up)*(VerticalFovDegrees is {} lhDegrees
            ?Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(lhDegrees*MathF.PI/180,(float)width/height,Near,Far)
            :Matrix4x4.CreateOrthographicLeftHanded(OrthographicHeight*width/height,OrthographicHeight,Near,Far));
        else viewProjection=Matrix4x4.CreateLookAt(Eye,Target,Up)*(VerticalFovDegrees is {} degrees
            ?Matrix4x4.CreatePerspectiveFieldOfView(degrees*MathF.PI/180,(float)width/height,Near,Far)
            :Matrix4x4.CreateOrthographic(OrthographicHeight*width/height,OrthographicHeight,Near,Far));
        // Skip the multiply entirely so the default path retains historical floating-point bits.
        return ProjectionJitterPixels==Vector2.Zero?viewProjection
            :viewProjection*Matrix4x4.CreateTranslation(2*ProjectionJitterPixels.X/width,-2*ProjectionJitterPixels.Y/height,0);
    }

    internal void Validate()
    {
        if(VerticalFovDegrees is {} fov&&(!float.IsFinite(fov)||fov<=0||fov>=180))
            throw new ArgumentOutOfRangeException(nameof(VerticalFovDegrees));
        Vector3 direction=Target-Eye;
        Vector3 cross=Vector3.Cross(direction,Up);
        float directionLength=direction.LengthSquared(),upLength=Up.LengthSquared(),crossLength=cross.LengthSquared();
        if(!Finite(Eye)||!Finite(Target)||!Finite(Up)||!Finite(direction)||!Finite(cross)
            ||!float.IsFinite(directionLength)||directionLength<=0||!float.IsFinite(upLength)||upLength<=0
            ||!float.IsFinite(crossLength)||crossLength<1e-12f
            ||!float.IsFinite(OrthographicHeight)||OrthographicHeight<=0||!float.IsFinite(Near)||Near<=0
            ||!float.IsFinite(Far)||Far<=Near
            ||!float.IsFinite(ProjectionJitterPixels.X)||!float.IsFinite(ProjectionJitterPixels.Y))
            throw new ArgumentException("Camera must have finite nondegenerate pose, projection and jitter.");
    }

    private static bool Finite(Vector3 value)=>float.IsFinite(value.X)&&float.IsFinite(value.Y)&&float.IsFinite(value.Z);
}

/// <summary>Format-neutral render-ready data. No model, bone, GPU or mutable array objects escape this boundary.</summary>
public sealed class SceneSnapshot
{
    // The geometry value is immutable; only the optional contiguous compatibility view is
    // memoized. All metadata wrappers share this owner and its lazy result.
    private readonly VertexStorage vertexStorage;
    private readonly uint[] indices;
    private sealed class VertexStorage
    {
        private readonly OwnedVertexChunks<SceneVertex>? eager;
        private readonly Lazy<OwnedVertexChunks<SceneVertex>>? deferred;
        private readonly Lazy<SceneVertex[]> contiguous;
        internal readonly int Count;
        // Deferred storage only: the eager owner of every other range (placeholders inside Ranges)
        // and the ranges whose values are produced on demand by their deformation's CPU reference.
        internal readonly VertexStorage? Base;
        internal readonly ReadOnlyCollection<SceneDeformationRange> Ranges = ReadOnlyCollection<SceneDeformationRange>.Empty;
        internal bool IsMaterialized => deferred?.IsValueCreated ?? contiguous.IsValueCreated;
        internal OwnedVertexChunks<SceneVertex> Chunks => eager ?? deferred!.Value;
        internal int ChunkCount => Count / OwnedVertexChunks<SceneVertex>.ChunkSize + (Count % OwnedVertexChunks<SceneVertex>.ChunkSize == 0 ? 0 : 1);
        internal VertexStorage(SceneVertex[] owned)
        { eager = new(owned); Count = owned.Length; contiguous = new(() => owned, LazyThreadSafetyMode.ExecutionAndPublication); }
        internal VertexStorage(OwnedVertexChunks<SceneVertex> chunks)
        {
            eager = chunks; Count = chunks.Count;
            contiguous = new(() => { var result = new SceneVertex[chunks.Count]; chunks.CopyTo(0, result); return result; },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }
        internal VertexStorage(VertexStorage baseline, SceneDeformationRange[] ranges)
        {
            Base = baseline; Count = baseline.Count; Ranges = Array.AsReadOnly(ranges);
            var bounds = ranges.Select(range => (range.Start, range.Count)).ToArray();
            deferred = new(() => baseline.Chunks.WithRanges(bounds, (index, offset, destination) => WriteDeformed(ranges[index], offset, destination)),
                LazyThreadSafetyMode.ExecutionAndPublication);
            contiguous = new(() => { var result = new SceneVertex[Count]; Chunks.CopyTo(0, result); return result; },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }
        internal SceneVertex[] Contiguous => contiguous.Value;
        // Same placement arithmetic as project composition of CPU-deformed actor vertices.
        private static void WriteDeformed(SceneDeformationRange range, int offset, Span<SceneVertex> destination)
        {
            var source = range.Deformation.Materialize().AsSpan(offset, destination.Length);
            var world = range.World;
            if (world == Matrix4x4.Identity) { source.CopyTo(destination); return; }
            if (!Matrix4x4.Invert(world, out var inverse)) throw new ArgumentException("Deformation World must be invertible.");
            var normalTransform = Matrix4x4.Transpose(inverse);
            for (int index = 0; index < destination.Length; index++)
            {
                var vertex = source[index];
                var normal = Vector3.TransformNormal(vertex.Normal, normalTransform);
                var placed = vertex with { Position = Vector3.Transform(vertex.Position, world), Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.Zero };
                if (!Finite(placed.Position) || !Finite(placed.Normal) || !Finite(placed.Normal.LengthSquared()) || !Finite(placed.Uv.X) || !Finite(placed.Uv.Y))
                    throw new ArgumentException("Composed vertices must be finite.");
                destination[index] = placed;
            }
        }
    }
    /// <summary>Contiguous compatibility view. Composed frames materialize it once on demand;
    /// renderers can read immutable chunks without allocating this whole-scene copy.</summary>
    public ReadOnlySpan<SceneVertex> Vertices => vertexStorage.Contiguous;
    public int VertexCount => vertexStorage.Count;
    public int VertexChunkCount => vertexStorage.ChunkCount;
    public SceneVertex GetVertex(int index) => vertexStorage.Chunks[index];
    public ReadOnlySpan<SceneVertex> GetVertexChunk(int chunkIndex) => vertexStorage.Chunks.GetChunk(chunkIndex);
    public void CopyVertices(int sourceIndex, Span<SceneVertex> destination) => vertexStorage.Chunks.CopyTo(sourceIndex, destination);
    /// <summary>Conservative immutable vertex lineage; metadata-only wrappers share this identity.</summary>
    public SceneVertexRevision VertexRevision => SceneVertexRevision.For(vertexStorage, VertexCount);
    public ReadOnlySpan<uint> Indices => indices;
    public ReadOnlyCollection<SceneMaterial> Materials { get; }
    public ReadOnlyCollection<SceneTexture> Textures { get; }
    public Matrix4x4 World { get; }
    public SceneCamera Camera { get; }
    public Vector3 LightDirection { get; }
    /// <summary>Optional immutable UV1..UV4 values in exactly the same vertex order as Vertices.</summary>
    public SceneAdditionalUvs? AdditionalUvs { get; }
    /// <summary>Optional immutable rest-derived raw radial seeds in exactly the same local vertex order.</summary>
    public SceneRawRadialSeeds? RawRadialSeeds { get; }
    /// <summary>Deformed frames use a replaceable GPU vertex stream instead of a permanent content cache.</summary>
    public bool HasDynamicVertices { get; }
    /// <summary>Ranges whose vertices are described by deformation inputs. Their values are still observable through
    /// every vertex accessor (computed once by the CPU reference on first read); a GPU engine may instead deform
    /// them itself and read the remaining ranges from StaticVertexView without evaluating the reference.</summary>
    public IReadOnlyList<SceneDeformationRange> DeformationRanges => vertexStorage.Ranges;
    public bool HasDeferredDeformation => vertexStorage.Base is not null;
    /// <summary>True once a deferred deformation was evaluated on the CPU for this snapshot's vertices.</summary>
    public bool IsDeferredDeformationMaterialized => vertexStorage.Base is not null && vertexStorage.IsMaterialized;
    private SceneSnapshot? staticVertexView;
    /// <summary>Same metadata over the eager owner: exact outside DeformationRanges, placeholders inside them.</summary>
    public SceneSnapshot StaticVertexView => vertexStorage.Base is not { } baseline ? this
        : Volatile.Read(ref staticVertexView) ?? Interlocked.CompareExchange(ref staticVertexView, new(this, baseline, HasDynamicVertices), null) ?? staticVertexView!;
    /// <summary>The single actor-level deformation covering every vertex, if this snapshot is exactly that.</summary>
    public SceneDeformation? WholeDeformation => vertexStorage.Ranges.Count == 1 && vertexStorage.Ranges[0] is { Start: 0 } range
        && range.Count == VertexCount && range.World == Matrix4x4.Identity ? range.Deformation : null;

    /// <summary>Publishes a deformation of these rest-topology vertices without evaluating it. Vertex reads produce the
    /// deformation's CPU reference; the current vertices remain as placeholders for engines that deform on the GPU.</summary>
    public SceneSnapshot WithDeferredDeformation(SceneDeformation deformation)
    {
        ArgumentNullException.ThrowIfNull(deformation);
        if (deformation.Model.VertexCount != VertexCount) throw new ArgumentException("Deformation must preserve topology.", nameof(deformation));
        return WithDeferredRanges([new(0, deformation, Matrix4x4.Identity)]);
    }
    /// <summary>Wraps the eager owner with ordered, disjoint deferred ranges. All deferred ranges are new lineage.</summary>
    internal SceneSnapshot WithDeferredRanges(IReadOnlyList<SceneDeformationRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var baseline = vertexStorage.Base ?? vertexStorage;
        var owned = ranges.ToArray();
        if (owned.Length == 0) return ReferenceEquals(baseline, vertexStorage) ? this : new(this, baseline, dynamicVertices: true);
        int end = 0;
        foreach (var range in owned)
        {
            if (range is null || range.Start < end || range.Count <= 0 || range.Count > VertexCount - range.Start)
                throw new ArgumentException("Deformation ranges must be ordered, disjoint and within the baseline.", nameof(ranges));
            ValidateWorld(range.World); end = range.Start + range.Count;
        }
        var storage = new VertexStorage(baseline, owned);
        SceneVertexRevision.RegisterComposed(baseline, storage, VertexCount, owned.Select(range => (range.Start, range.Count)).ToArray());
        return new(this, storage, dynamicVertices: true);
    }
    /// <summary>A composition source range: the actor's whole deformation placed at Start by World.</summary>
    internal static SceneDeformationRange PlacedDeformation(int start, SceneDeformation deformation, Matrix4x4 world) => new(start, deformation, world);
    /// <summary>Explicitly empty render input: no geometry, materials or texture references to draw or trace.</summary>
    public bool IsEmpty => VertexCount == 0;

    /// <summary>Creates a clear-only scene. Ordinary mesh construction still rejects missing geometry.</summary>
    public static SceneSnapshot CreateEmpty(SceneCamera? camera = null, Vector3? lightDirection = null)
        => new(camera ?? SceneCamera.Default, lightDirection ?? Vector3.Normalize(new Vector3(-.3f, .7f, -1)));
    private SceneSnapshot(SceneCamera camera, Vector3 lightDirection)
    {
        camera.Validate();
        if (!Finite(lightDirection) || !Finite(lightDirection.LengthSquared()) || lightDirection.LengthSquared() < 1e-12f)
            throw new ArgumentException("Invalid light.", nameof(lightDirection));
        vertexStorage = new(Array.Empty<SceneVertex>()); indices = []; Materials = Array.AsReadOnly<SceneMaterial>([]); Textures = Array.AsReadOnly<SceneTexture>([]);
        World = Matrix4x4.Identity; Camera = camera; LightDirection = lightDirection;
    }

    public SceneSnapshot WithDeformedVertices(SceneVertex[] deformed)
    {
        ArgumentNullException.ThrowIfNull(deformed);
        if(deformed.Length!=VertexCount)throw new ArgumentException("Deformation must preserve topology.",nameof(deformed));
        // Validate the exact private bytes that will be published. A caller may still mutate
        // its own array while it is being copied, but cannot change this owned payload afterward.
        var owned = deformed.ToArray();
        ValidateDeformedVertices(owned);
        return new(this,owned);
    }
    /// <summary>Transfers a privately owned deformation without copying. Every retained alias must
    /// remain private and read-only after this call; mutable caller inputs must use the public method.</summary>
    internal SceneSnapshot WithOwnedDeformedVertices(SceneVertex[] deformed)
    {
        ArgumentNullException.ThrowIfNull(deformed);
        if(deformed.Length!=VertexCount)throw new ArgumentException("Deformation must preserve topology.",nameof(deformed));
        ValidateDeformedVertices(deformed);
        return new(this,deformed);
    }
    private static void ValidateDeformedVertices(ReadOnlySpan<SceneVertex> deformed)
    {
        foreach(var v in deformed)
            if(!Finite(v.Position)||!Finite(v.Normal)||!Finite(v.Normal.LengthSquared())||!Finite(v.Uv.X)||!Finite(v.Uv.Y))
                throw new ArgumentException("Deformed vertices must be finite.",nameof(deformed));
    }
    private SceneSnapshot(SceneSnapshot source,SceneVertex[] ownedDeformed)
    {
        vertexStorage=new(ownedDeformed);indices=source.indices;Materials=source.Materials;Textures=source.Textures;
        World=source.World;Camera=source.Camera;LightDirection=source.LightDirection;HasDynamicVertices=true;
        AdditionalUvs=source.AdditionalUvs;RawRadialSeeds=source.RawRadialSeeds;
    }
    internal bool SharesVertexStorage(SceneSnapshot other) => ReferenceEquals(vertexStorage, other.vertexStorage);
    /// <summary>Exact byte equality proves an already validated immutable deformation can be
    /// retained. Metadata belongs to this snapshot; no old scene, revision history or mutable input
    /// is retained. Signed zero and every normal/UV component participate in the proof.</summary>
    internal SceneSnapshot? ReuseUnchangedDeformation(SceneSnapshot previous,ReadOnlySpan<SceneVertex> deformed)
    {
        if(deformed.Length!=VertexCount)throw new ArgumentException("Deformation must preserve topology.",nameof(deformed));
        if(previous.VertexCount!=VertexCount)return null;
        int offset=0;
        for(int chunk=0;chunk<previous.VertexChunkCount;chunk++)
        {
            var prior=previous.GetVertexChunk(chunk);
            if(!MemoryMarshal.AsBytes(prior).SequenceEqual(MemoryMarshal.AsBytes(deformed.Slice(offset,prior.Length))))return null;
            offset+=prior.Length;
        }
        return new(this,previous.vertexStorage,dynamicVertices:true);
    }
    internal bool SharesIndexStorage(SceneSnapshot other) => ReferenceEquals(indices, other.indices);
    /// <summary>Deferred counterpart of ReuseUnchangedDeformation: equal deformation inputs keep the published storage.</summary>
    internal SceneSnapshot? ReuseUnchangedDeferredDeformation(SceneSnapshot previous, SceneDeformation deformation)
        => previous.VertexCount == VertexCount && previous.WholeDeformation is { } prior && prior.HasSameInputs(deformation)
            && ReferenceEquals(previous.vertexStorage.Base, vertexStorage.Base ?? vertexStorage)
            ? new(this, previous.vertexStorage, dynamicVertices: true) : null;

    /// <summary>Compatibility writer for callers that require a single span per changed range.
    /// The product composition path uses WithComposedVertexChunks and has no range temporary.</summary>
    internal SceneSnapshot WithComposedVertices(IReadOnlyList<(int Start, int Count)> ranges, SceneVertexRangeWriter write)
    {
        ArgumentNullException.ThrowIfNull(ranges); ArgumentNullException.ThrowIfNull(write);
        var ownedRanges = ranges.ToArray();
        // Validate before invoking any writer, including callers whose range list is mutable.
        int end = 0;
        foreach (var range in ownedRanges)
        {
            if (range.Start < end || range.Start > VertexCount || range.Count <= 0 || range.Count > VertexCount - range.Start)
                throw new ArgumentException("Composition ranges must be ordered, disjoint and within the baseline.", nameof(ranges));
            end = range.Start + range.Count;
        }
        var buffers = new SceneVertex[ownedRanges.Length][];
        for (int i = 0; i < ownedRanges.Length; i++)
        {
            var range = ownedRanges[i]; var buffer = new SceneVertex[range.Count];
            CopyVertices(range.Start, buffer); write(i, buffer); buffers[i] = buffer;
            foreach (var v in buffer)
                if (!Finite(v.Position) || !Finite(v.Normal) || !Finite(v.Normal.LengthSquared()) || !Finite(v.Uv.X) || !Finite(v.Uv.Y))
                    throw new ArgumentException("Composed vertices must be finite.", nameof(write));
        }
        return WithComposedVertexChunks(ownedRanges, (range, offset, destination) =>
            buffers[range].AsSpan(offset, destination.Length).CopyTo(destination));
    }

    /// <summary>Clone only touched immutable chunks. Bounded synchronous writers never receive
    /// an array; unchanged chunks are already validated, and no prior owner is retained.</summary>
    internal SceneSnapshot WithComposedVertexChunks(IReadOnlyList<(int Start, int Count)> ranges,
        OwnedVertexRangeWriter<SceneVertex> write)
    {
        ArgumentNullException.ThrowIfNull(ranges); ArgumentNullException.ThrowIfNull(write);
        var ownedRanges = ranges.ToArray();
        // A deferred baseline composes its eager owner; deferred ranges are re-attached by the caller.
        var baseline = vertexStorage.Base ?? vertexStorage;
        var chunks = baseline.Chunks.WithRanges(ownedRanges, (range, offset, destination) =>
        {
            write(range, offset, destination);
            foreach (var v in destination)
                if (!Finite(v.Position) || !Finite(v.Normal) || !Finite(v.Normal.LengthSquared()) || !Finite(v.Uv.X) || !Finite(v.Uv.Y))
                    throw new ArgumentException("Composed vertices must be finite.", nameof(write));
        });
        var storage = ReferenceEquals(chunks, baseline.Chunks) ? baseline : new VertexStorage(chunks);
        SceneVertexRevision.RegisterComposed(baseline, storage, VertexCount, ownedRanges);
        return new(this, storage, dynamicVertices: true);
    }

    /// <summary>Copy complete identity-World source ranges without new floating-point arithmetic.
    /// All source bytes were validated in their final private storage before publication; no
    /// caller-owned array or arbitrary writer can enter this path. Copies stay chunked and owned.</summary>
    internal SceneSnapshot WithIdentityVertexChunks(IReadOnlyList<(int Start, SceneSnapshot Source)> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        var updates = replacements.ToArray();
        if (World != Matrix4x4.Identity)
            throw new ArgumentException("Identity composition requires an identity baseline World.", nameof(replacements));
        var sources = new (int Start, OwnedVertexChunks<SceneVertex> Source)[updates.Length];
        var ranges = new (int Start, int Count)[updates.Length];
        for (int index = 0; index < updates.Length; index++)
        {
            var range = updates[index];
            if (range.Source is null || range.Source.World != Matrix4x4.Identity)
                throw new ArgumentException("Identity composition requires nonnull identity-World sources.", nameof(replacements));
            sources[index] = (range.Start, range.Source.vertexStorage.Chunks);
            ranges[index] = (range.Start, range.Source.VertexCount);
        }
        var baseline = vertexStorage.Base ?? vertexStorage;
        var chunks = baseline.Chunks.WithCopiedRanges(sources);
        var storage = ReferenceEquals(chunks, baseline.Chunks) ? baseline : new VertexStorage(chunks);
        SceneVertexRevision.RegisterComposed(baseline, storage, VertexCount, ranges);
        return new(this, storage, dynamicVertices: true);
    }
    private SceneSnapshot(SceneSnapshot source, VertexStorage storage, bool dynamicVertices)
    {
        vertexStorage = storage; indices = source.indices; Materials = source.Materials; Textures = source.Textures;
        World = source.World; Camera = source.Camera; LightDirection = source.LightDirection; HasDynamicVertices = dynamicVertices;
        AdditionalUvs = source.AdditionalUvs; RawRadialSeeds = source.RawRadialSeeds;
    }
    public SceneSnapshot WithLighting(Vector3 incomingDirection)
    {
        if(!Finite(incomingDirection)||!Finite(incomingDirection.LengthSquared())||incomingDirection.LengthSquared()<1e-12f)throw new ArgumentException("Invalid light direction.",nameof(incomingDirection));
        return new(this,incomingDirection);
    }
    private SceneSnapshot(SceneSnapshot source,Vector3 lightDirection)
    {
        vertexStorage=source.vertexStorage;indices=source.indices;Materials=source.Materials;Textures=source.Textures;
        World=source.World;Camera=source.Camera;HasDynamicVertices=source.HasDynamicVertices;LightDirection=lightDirection;
        AdditionalUvs=source.AdditionalUvs;RawRadialSeeds=source.RawRadialSeeds;
    }
    /// <summary>Replaces only the camera, sharing the immutable geometry and preserving deformation state.</summary>
    public SceneSnapshot WithCamera(SceneCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        camera.Validate();
        return new(this,camera);
    }
    private SceneSnapshot(SceneSnapshot source,SceneCamera camera)
    {
        vertexStorage=source.vertexStorage;indices=source.indices;Materials=source.Materials;Textures=source.Textures;
        World=source.World;Camera=camera;HasDynamicVertices=source.HasDynamicVertices;LightDirection=source.LightDirection;
        AdditionalUvs=source.AdditionalUvs;RawRadialSeeds=source.RawRadialSeeds;
    }
    /// <summary>Replaces only object placement, sharing immutable streams and preserving deformation state.</summary>
    public SceneSnapshot WithWorld(Matrix4x4 world)
    {
        ValidateWorld(world);
        return world == World ? this : new(this,world);
    }
    private SceneSnapshot(SceneSnapshot source,Matrix4x4 world)
    {
        vertexStorage=source.vertexStorage;indices=source.indices;Materials=source.Materials;Textures=source.Textures;
        World=world;Camera=source.Camera;HasDynamicVertices=source.HasDynamicVertices;LightDirection=source.LightDirection;
        AdditionalUvs=source.AdditionalUvs;RawRadialSeeds=source.RawRadialSeeds;
    }
    public SceneSnapshot WithMaterials(IEnumerable<SceneMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);return new(this,Array.AsReadOnly(materials.ToArray()));
    }
    private SceneSnapshot(SceneSnapshot source,ReadOnlyCollection<SceneMaterial> materials)
    {
        vertexStorage=source.vertexStorage;indices=source.indices;Materials=materials;Textures=source.Textures;
        World=source.World;Camera=source.Camera;HasDynamicVertices=source.HasDynamicVertices;LightDirection=source.LightDirection;
        AdditionalUvs=source.AdditionalUvs;RawRadialSeeds=source.RawRadialSeeds;
        if(Materials.Count!=source.Materials.Count)throw new ArgumentException("Material updates must preserve topology.",nameof(materials));
        ValidateMaterials();
    }

    /// <summary>Replaces only the optional UV stream, preserving immutable geometry and deformation state.</summary>
    public SceneSnapshot WithAdditionalUvs(SceneAdditionalUvs? additionalUvs)
    {
        ValidateAdditionalUvs(additionalUvs, VertexCount);
        return ReferenceEquals(AdditionalUvs, additionalUvs) ? this : new(this, additionalUvs);
    }
    private SceneSnapshot(SceneSnapshot source, SceneAdditionalUvs? additionalUvs)
    {
        vertexStorage=source.vertexStorage;indices=source.indices;Materials=source.Materials;Textures=source.Textures;
        World=source.World;Camera=source.Camera;HasDynamicVertices=source.HasDynamicVertices;LightDirection=source.LightDirection;
        AdditionalUvs=additionalUvs;RawRadialSeeds=source.RawRadialSeeds;
    }

    /// <summary>Replaces only the raw radial stream, preserving vertex identity, geometry and deformation state.</summary>
    public SceneSnapshot WithRawRadialSeeds(SceneRawRadialSeeds? rawRadialSeeds)
    {
        ValidateRawRadialSeeds(rawRadialSeeds, VertexCount);
        return ReferenceEquals(RawRadialSeeds, rawRadialSeeds) ? this : new(this, rawRadialSeeds);
    }
    private SceneSnapshot(SceneSnapshot source, SceneRawRadialSeeds? rawRadialSeeds)
    {
        vertexStorage=source.vertexStorage;indices=source.indices;Materials=source.Materials;Textures=source.Textures;
        World=source.World;Camera=source.Camera;HasDynamicVertices=source.HasDynamicVertices;LightDirection=source.LightDirection;
        AdditionalUvs=source.AdditionalUvs;RawRadialSeeds=rawRadialSeeds;
    }

    public SceneSnapshot(ReadOnlySpan<SceneVertex> vertices, ReadOnlySpan<uint> indices,
        IEnumerable<SceneMaterial> materials, IEnumerable<SceneTexture>? textures = null,
        Matrix4x4? world = null, SceneCamera? camera = null, Vector3? lightDirection = null,
        SceneAdditionalUvs? additionalUvs = null, SceneRawRadialSeeds? rawRadialSeeds = null)
        : this(vertices, indices, materials, textures, world, camera, lightDirection, additionalUvs, rawRadialSeeds, null, false)
    { }

    internal SceneSnapshot(ReadOnlySpan<SceneVertex> vertices, ReadOnlySpan<uint> indices,
        IEnumerable<SceneMaterial> materials, IEnumerable<SceneTexture>? textures,
        Matrix4x4? world, SceneCamera? camera, Vector3? lightDirection,
        SceneAdditionalUvs? additionalUvs, SceneRawRadialSeeds? rawRadialSeeds, SceneSnapshot? textureSource, bool dynamicVertices)
    {
        ArgumentNullException.ThrowIfNull(materials);
        vertexStorage = new(vertices.ToArray()); this.indices = indices.ToArray();
        Materials = Array.AsReadOnly(materials.ToArray());
        var ownedTextures = textures?.ToArray() ?? [];
        // Material slots keep their meaning only when the immutable textures match in order.
        Textures = textureSource is not null && ownedTextures.Length == textureSource.Textures.Count
            && ownedTextures.SequenceEqual<SceneTexture>(textureSource.Textures, ReferenceEqualityComparer.Instance)
            ? textureSource.Textures : Array.AsReadOnly(ownedTextures);
        World = world ?? Matrix4x4.Identity; Camera = camera ?? SceneCamera.Default;
        HasDynamicVertices = dynamicVertices;
        LightDirection = lightDirection ?? Vector3.Normalize(new Vector3(-.3f, .7f, -1));
        ValidateAdditionalUvs(additionalUvs, VertexCount); AdditionalUvs=additionalUvs;
        ValidateRawRadialSeeds(rawRadialSeeds, VertexCount); RawRadialSeeds=rawRadialSeeds;
        if (VertexCount == 0 || this.indices.Length == 0 || this.indices.Length % 3 != 0 || Materials.Count == 0)
            throw new ArgumentException("A nonempty triangle mesh with materials is required.");
        foreach (var v in vertexStorage.Chunks)
            if (!Finite(v.Position) || !Finite(v.Normal) || !Finite(v.Normal.LengthSquared()) || !Finite(v.Uv.X) || !Finite(v.Uv.Y))
                throw new ArgumentException("Vertices must have finite position, UV and normal.");
        foreach (uint i in this.indices) if (i >= VertexCount) throw new ArgumentException("Index outside vertex array.");
        if (Textures.Any(t => t is null)) throw new ArgumentException("Null texture.");
        ValidateMaterials();
        ValidateWorld(World);
        Camera.Validate();
        if (!Finite(LightDirection) || !Finite(LightDirection.LengthSquared()) || LightDirection.LengthSquared() < 1e-12f)
            throw new ArgumentException("Invalid light.");
    }
    private static void ValidateWorld(Matrix4x4 world)
    {
        if (!Finite(world) || world.M14 != 0 || world.M24 != 0 || world.M34 != 0 || world.M44 != 1 || !Matrix4x4.Invert(world, out _))
            throw new ArgumentException("World must be a finite invertible affine matrix.");
    }
    private static void ValidateAdditionalUvs(SceneAdditionalUvs? additionalUvs, int vertexCount)
    {
        if (additionalUvs is not null && additionalUvs.Vertices.Length != vertexCount)
            throw new ArgumentException("Additional UV stream must preserve vertex order and count.", nameof(additionalUvs));
    }
    private static void ValidateRawRadialSeeds(SceneRawRadialSeeds? rawRadialSeeds, int vertexCount)
    {
        if (rawRadialSeeds is not null && rawRadialSeeds.Vertices.Length != vertexCount)
            throw new ArgumentException("Raw radial seed stream must preserve vertex order and count.", nameof(rawRadialSeeds));
    }
    private void ValidateMaterials()
    {
        int next = 0;
        foreach (var m in Materials)
        {
            if (m is null || m.StartIndex != next || m.IndexCount < 0 || m.IndexCount % 3 != 0 || m.IndexCount > this.indices.Length - next
                || !Finite(new Vector3(m.Diffuse.X, m.Diffuse.Y, m.Diffuse.Z)) || !Finite(m.Diffuse.W)
                || !Finite(m.Specular) || !Finite(m.Ambient) || !Finite(m.Shininess) || m.Shininess < 0
                || !Finite(m.Emission) || Vector3.Min(m.Emission, Vector3.Zero) != Vector3.Zero
                || !Finite(m.AlphaCutoff) || m.AlphaCutoff is < 0 or > 1
                || (m.ToonUvTransform is { } uv && (!Finite(new Vector3(uv.X, uv.Y, uv.Z)) || !Finite(uv.W)))
                || m.DiffuseTexture < -1 || m.DiffuseTexture >= Textures.Count || m.ToonTexture < -1 || m.ToonTexture >= Textures.Count)
                throw new ArgumentException("Materials must partition all triangle indices and reference valid texture identities with finite colours.");
            next += m.IndexCount;
        }
        if (next != this.indices.Length) throw new ArgumentException("Material ranges do not cover indices.");
    }
    private static bool Finite(float f) => float.IsFinite(f);
    private static bool Finite(Vector3 v) => Finite(v.X) && Finite(v.Y) && Finite(v.Z);
    private static bool Finite(Matrix4x4 m) => Finite(m.M11) && Finite(m.M12) && Finite(m.M13) && Finite(m.M14)
        && Finite(m.M21) && Finite(m.M22) && Finite(m.M23) && Finite(m.M24) && Finite(m.M31) && Finite(m.M32) && Finite(m.M33) && Finite(m.M34)
        && Finite(m.M41) && Finite(m.M42) && Finite(m.M43) && Finite(m.M44);
}

public sealed record SceneAdaptationResult(SceneSnapshot Snapshot, IReadOnlyList<string> Diagnostics);
