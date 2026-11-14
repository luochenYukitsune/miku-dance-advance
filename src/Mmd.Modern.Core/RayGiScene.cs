using System.Numerics;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Core;

[StructLayout(LayoutKind.Sequential)]
public readonly struct RayGiUInt4 : IEquatable<RayGiUInt4>
{
    public readonly uint X, Y, Z, W;
    public RayGiUInt4(uint x, uint y, uint z, uint w) { X = x; Y = y; Z = z; W = w; }
    public bool Equals(RayGiUInt4 other) => X == other.X && Y == other.Y && Z == other.Z && W == other.W;
    public override bool Equals(object? obj) => obj is RayGiUInt4 other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);
    public static bool operator ==(RayGiUInt4 left, RayGiUInt4 right) => left.Equals(right);
    public static bool operator !=(RayGiUInt4 left, RayGiUInt4 right) => !left.Equals(right);
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct RayGiTriangleGpu
{
    public readonly Vector4 P0, P1, P2, N0U0, N1U1, N2U2, V012, SphereUv01, SphereUv2;
    public readonly RayGiUInt4 Meta;
    public RayGiTriangleGpu(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 n0u0, Vector4 n1u1,
        Vector4 n2u2, Vector4 v012, Vector4 sphereUv01, Vector4 sphereUv2, RayGiUInt4 meta)
    {
        P0 = p0; P1 = p1; P2 = p2; N0U0 = n0u0; N1U1 = n1u1; N2U2 = n2u2;
        V012 = v012; SphereUv01 = sphereUv01; SphereUv2 = sphereUv2; Meta = meta;
    }
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct RayGiMaterialGpu
{
    public readonly Vector4 DiffuseFactor, Emission;
    public readonly RayGiUInt4 DiffuseTexture, SphereTexture;
    public readonly Vector4 Parameters;
    /// <summary>X: 1 proves diffuse alpha255 without a sphere map; 2 proves diffuse alpha255 with a sphere map.
    /// Zero is unproved. Other values and Y/Z/W are reserved. Sphere alpha is always sampled separately.</summary>
    public readonly RayGiUInt4 Flags;
    public RayGiMaterialGpu(Vector4 diffuseFactor, Vector4 emission, RayGiUInt4 diffuseTexture,
        RayGiUInt4 sphereTexture, Vector4 parameters, RayGiUInt4 flags)
    {
        DiffuseFactor = diffuseFactor; Emission = emission; DiffuseTexture = diffuseTexture;
        SphereTexture = sphereTexture; Parameters = parameters; Flags = flags;
    }
}

/// <summary>Optional colour-only side stream. The accepted 96-byte material/visibility ABI is unchanged.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RayGiDiffuseTextureFactorGpu
{
    public readonly Vector4 Multiply, Add;
    public RayGiDiffuseTextureFactorGpu(Vector4 multiply, Vector4 add) { Multiply = multiply; Add = add; }
    public static RayGiDiffuseTextureFactorGpu Identity { get; } = new(Vector4.One, Vector4.Zero);
}

/// <summary>Optional triangle-order UV1.xy side stream. Uv01 packs vertices 0/1; Uv2.xy is vertex 2.
/// The final two floats are padding, never authored UV1.zw. The 160-byte triangle ABI is unchanged.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RayGiSubTextureUvGpu
{
    public readonly Vector4 Uv01, Uv2;
    public RayGiSubTextureUvGpu(Vector4 uv01, Vector4 uv2) { Uv01 = uv01; Uv2 = uv2; }
}

/// <summary>Immutable world-space transport geometry. RGBA8 texels retain the raster path's linear UNorm convention.
/// Secondary visibility uses binary alpha cutoff, including material/diffuse/sphere alpha; it does not model transmission.
/// Receiver translucency and unsupported frame Transp are handled by the common renderer.
/// This static transport snapshot is independent of camera visibility.</summary>
public sealed class RayGiScene
{
    private readonly RayGiTriangleGpu[]? triangles;
    private readonly GeometryData? originalGeometry;
    private readonly RayGiMaterialGpu[] materials;
    private readonly RayGiDiffuseTextureFactorGpu[] diffuseTextureFactors;
    private readonly RayGiSubTextureUvGpu[] subTextureUvs;
    private readonly uint[] texels;
    private readonly Lazy<RayGiBvh> bvh;
    private static readonly ConditionalWeakTable<OriginalStyleScene, TransportCache> SceneCache = new();
    private sealed record TextureData(uint[] Texels, RayGiUInt4[] Textures, bool[] AllAlpha255)
    { internal Guid Identity { get; } = Guid.NewGuid(); }
    // Metadata wrappers, deformations and incremental project composition retain this immutable
    // ordered texture owner. Keep its packed pixels independently of per-pose triangles; the weak
    // key retires the table with the texture list and never retains a history of scene snapshots.
    private static readonly ConditionalWeakTable<ReadOnlyCollection<SceneTexture>, TextureData> TextureCache = new();
    private sealed class GeometryData
    {
        private SceneSnapshot? source;
        internal SceneSnapshot? Source => Volatile.Read(ref source);
        internal int TriangleCount { get; }
        internal TextureData Pixels { get; }
        internal Guid Identity { get; } = Guid.NewGuid();
        internal Lazy<RayGiTriangleGpu[]> Triangles { get; }

        internal GeometryData(SceneSnapshot source, TextureData pixels)
        {
            this.source = source;
            TriangleCount = source.Indices.Length / 3;
            Pixels = pixels;
            Triangles = new(Materialize, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        private RayGiTriangleGpu[] Materialize()
        {
            var rows = BuildTriangleRows(source!, Environment.ProcessorCount);
            // Eager callers retain the packed geometry without extending the original mesh lifetime.
            Volatile.Write(ref source, null);
            return rows;
        }
    }
    private sealed record MaterialData(bool[] Enabled, bool[] DoubleSided, RayGiMaterialGpu[] Materials,
        RayGiDiffuseTextureFactorGpu[] DiffuseTextureFactors)
    { internal Guid Identity { get; } = Guid.NewGuid(); }
    private static readonly ConditionalWeakTable<SceneSnapshot, GeometryData> GeometryCache = new();
    private sealed record SubTextureUvData(RayGiSubTextureUvGpu[] Rows)
    { internal Guid Identity { get; } = Guid.NewGuid(); }
    // At most one table per live immutable snapshot; neither frame rotations nor material variants accumulate rows.
    private static readonly ConditionalWeakTable<SceneSnapshot, SubTextureUvData> SubTextureUvCache = new();
    private sealed class TransportCache
    {
        private readonly List<(Matrix4x4 Rotation, bool[] Enabled, bool[] DoubleSided, RayGiScene Scene)> entries = [];
        private readonly List<MaterialData> materialEntries = [];
        internal RayGiScene Get(OriginalStyleScene style, OriginalFrameScene? frame, bool requireTriangles)
        {
            var rotation = frame?.Options?.SphereRotationOverride ?? style.Profile.SphereRotation;
            lock (entries)
            {
                foreach (var entry in entries)
                {
                    if (entry.Rotation != rotation) continue;
                    bool match = true;
                    for (int i = 0; i < entry.Enabled.Length; i++)
                        if (entry.Enabled[i] != (frame?.Materials[i].SurfaceEnabled ?? true) || entry.DoubleSided[i] != DoubleSided(style, frame, i)) { match = false; break; }
                    if (match)
                    {
                        if (requireTriangles) _ = entry.Scene.Triangles;
                        return entry.Scene;
                    }
                }
                var geometry = GeometryCache.GetValue(style.Snapshot, BuildGeometry);
                if (requireTriangles) _ = geometry.Triangles.Value;
                var enabled = Enumerable.Range(0, style.Materials.Count).Select(i => frame?.Materials[i].SurfaceEnabled ?? true).ToArray();
                var doubleSided = Enumerable.Range(0, style.Materials.Count).Select(i => DoubleSided(style, frame, i)).ToArray();
                var material = materialEntries.Find(e => e.Enabled.AsSpan().SequenceEqual(enabled) && e.DoubleSided.AsSpan().SequenceEqual(doubleSided));
                if (material is null)
                {
                    material = new(enabled, doubleSided, BuildMaterials(style, geometry, enabled, doubleSided), BuildDiffuseTextureFactors(style));
                    if (materialEntries.Count == 4) materialEntries.RemoveAt(0);
                    materialEntries.Add(material);
                }
                var uvData = style.Materials.Any(m => m.SphereMode == OriginalSphereMode.SubTexture)
                    ? SubTextureUvCache.GetValue(style.Snapshot, BuildSubTextureUvs) : null;
                var result = new RayGiScene(geometry, material, uvData, rotation);
                if (entries.Count == 4) entries.RemoveAt(0);
                entries.Add((rotation, enabled, doubleSided, result));
                return result;
            }
        }
    }
    public ReadOnlySpan<RayGiTriangleGpu> Triangles => originalGeometry?.Triangles.Value ?? triangles;
    internal int TriangleCount => originalGeometry?.TriangleCount ?? triangles!.Length;
    public ReadOnlySpan<RayGiMaterialGpu> Materials => materials;
    /// <summary>Empty selects the accepted identity compute shader; otherwise one row per material.
    /// Only mapped diffuse colour is transformed. Raw alpha and sphere visibility remain in Materials.</summary>
    public ReadOnlySpan<RayGiDiffuseTextureFactorGpu> DiffuseTextureFactors => diffuseTextureFactors;
    /// <summary>Empty for ordinary materials; otherwise one immutable 32-byte UV1.xy row per triangle.</summary>
    public ReadOnlySpan<RayGiSubTextureUvGpu> SubTextureUvs => subTextureUvs;
    /// <summary>Independent upload owner, stable across material eligibility and sphere rotation. Empty uses Guid.Empty.</summary>
    public Guid SubTextureUvIdentity { get; private set; }
    public ReadOnlySpan<uint> Texels => texels;
    public Guid Identity { get; } = Guid.NewGuid();
    /// <summary>Stable across camera/sphere-UV/frame eligibility changes sharing the immutable mesh and World.</summary>
    public Guid GeometryIdentity { get; private set; } = Guid.NewGuid();
    /// <summary>Stable packed texel storage independent of camera, sampler and material eligibility.</summary>
    public Guid TexelIdentity { get; private set; } = Guid.NewGuid();
    /// <summary>Stable material table independent of sphere rotation.</summary>
    public Guid MaterialIdentity { get; private set; } = Guid.NewGuid();
    public Matrix4x4 SphereRotation { get; private set; } = Matrix4x4.Identity;
    internal object GeometryCacheKey { get; private set; } = new();
    public RayGiBvh Bvh => bvh.Value;

    public RayGiScene(ReadOnlySpan<RayGiTriangleGpu> triangles, ReadOnlySpan<RayGiMaterialGpu> materials, ReadOnlySpan<uint> texels)
        : this(triangles, materials, texels, []) { }

    public RayGiScene(ReadOnlySpan<RayGiTriangleGpu> triangles, ReadOnlySpan<RayGiMaterialGpu> materials,
        ReadOnlySpan<uint> texels, ReadOnlySpan<RayGiDiffuseTextureFactorGpu> diffuseTextureFactors)
        : this(triangles, materials, texels, diffuseTextureFactors, []) { }

    public RayGiScene(ReadOnlySpan<RayGiTriangleGpu> triangles, ReadOnlySpan<RayGiMaterialGpu> materials,
        ReadOnlySpan<uint> texels, ReadOnlySpan<RayGiDiffuseTextureFactorGpu> diffuseTextureFactors,
        ReadOnlySpan<RayGiSubTextureUvGpu> subTextureUvs)
    {
        if (triangles.Length >= 1 << 24) throw new ArgumentException("RGBA32F Gbuffer IDs require fewer than 2^24 triangles.");
        if (materials.Length == 0 || texels.Length == 0) throw new ArgumentException("A material table and white fallback texel are required.");
        this.triangles = triangles.ToArray(); this.materials = materials.ToArray(); this.texels = texels.ToArray();
        if (!diffuseTextureFactors.IsEmpty && diffuseTextureFactors.Length != materials.Length)
            throw new ArgumentException("A nonempty diffuse texture-factor table requires one row per GI material.", nameof(diffuseTextureFactors));
        this.diffuseTextureFactors = diffuseTextureFactors.ToArray();
        foreach (var factor in this.diffuseTextureFactors)
            if (!Finite(factor.Multiply) || !Finite(factor.Add))
                throw new ArgumentException("GI diffuse texture factors must be finite.", nameof(diffuseTextureFactors));
        if (!subTextureUvs.IsEmpty && subTextureUvs.Length != triangles.Length)
            throw new ArgumentException("A nonempty SubTexture UV table requires one row per GI triangle.", nameof(subTextureUvs));
        this.subTextureUvs = subTextureUvs.ToArray();
        SubTextureUvIdentity = subTextureUvs.IsEmpty ? Guid.Empty : Guid.NewGuid();
        foreach (var row in this.subTextureUvs)
            if (!Finite(row.Uv01) || !Finite(row.Uv2))
                throw new ArgumentException("GI SubTexture UV rows must be finite.", nameof(subTextureUvs));
        foreach (var t in this.triangles)
            if (!Finite(t.P0) || !Finite(t.P1) || !Finite(t.P2) || !Finite(t.N0U0) || !Finite(t.N1U1) || !Finite(t.N2U2)
                || !Finite(t.V012) || !Finite(t.SphereUv01) || !Finite(t.SphereUv2) || t.Meta.X >= materials.Length)
                throw new ArgumentException("Triangles require finite world geometry, normal/UV rows and valid materials.");
        HashSet<(uint Offset, uint Width, uint Height)>? verifiedAlphaViews = null;
        foreach (var m in this.materials)
        {
            ValidateTexture(m.DiffuseTexture); ValidateTexture(m.SphereTexture);
            if (!Finite(m.DiffuseFactor) || !Finite(m.Emission) || Vector3.Min(Xyz(m.Emission), Vector3.Zero) != Vector3.Zero
                || !Finite(m.Parameters) || m.Parameters.X is < 0 or > 1
                || m.DiffuseTexture.W > (uint)OriginalTextureSampling.LinearWrap || m.SphereTexture.W > (uint)OriginalSphereMode.SubTexture
                || m.Flags.X > 2 || m.Flags.Y != 0 || m.Flags.Z != 0 || m.Flags.W != 0) throw new ArgumentException("Invalid GI material contract.");
            if (m.SphereTexture.W == (uint)OriginalSphereMode.SubTexture && subTextureUvs.IsEmpty)
                throw new ArgumentException("SubTexture GI materials require a complete UV1 triangle table.", nameof(subTextureUvs));
            if (m.Flags.X != 0)
            {
                // Public callers may copy a proof, but cannot assert it over different pixels or sphere modes.
                if ((m.Flags.X == 1 && m.SphereTexture.W != 0) || (m.Flags.X == 2 && m.SphereTexture.W == 0))
                    throw new ArgumentException("The diffuse alpha proof must match the sphere sampling mode.", nameof(materials));
                var view = m.DiffuseTexture;
                if ((verifiedAlphaViews ??= []).Add((view.X, view.Y, view.Z)))
                {
                    int start = (int)view.X, end = checked(start + (int)(view.Y * view.Z));
                    for (int pixel = start; pixel < end; pixel++)
                        if ((this.texels[pixel] >> 24) != 255)
                            throw new ArgumentException("A diffuse alpha proof requires every referenced alpha byte to be 255.", nameof(materials));
                }
            }
        }
        bvh = new(() => RayGiBvh.Build(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private RayGiScene(GeometryData geometry, MaterialData material, SubTextureUvData? uvData, Matrix4x4 rotation)
    {
        originalGeometry = geometry; materials = material.Materials; texels = geometry.Pixels.Texels;
        diffuseTextureFactors = material.DiffuseTextureFactors;
        subTextureUvs = uvData?.Rows ?? [];
        SubTextureUvIdentity = uvData?.Identity ?? Guid.Empty;
        GeometryIdentity = geometry.Identity; TexelIdentity = geometry.Pixels.Identity; GeometryCacheKey = geometry;
        MaterialIdentity = material.Identity; SphereRotation = rotation;
        bvh = new(() => RayGiBvh.Build(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static RayGiScene FromOriginal(OriginalStyleScene style)
    { ArgumentNullException.ThrowIfNull(style); return SceneCache.GetValue(style, static _ => new()).Get(style, null, requireTriangles: true); }
    public static RayGiScene FromOriginal(OriginalFrameScene frame)
    { ArgumentNullException.ThrowIfNull(frame); return SceneCache.GetValue(frame.Surface, static _ => new()).Get(frame.Surface, frame, requireTriangles: true); }

    internal static RayGiScene FromOriginalForGpu(OriginalFrameScene frame)
    { ArgumentNullException.ThrowIfNull(frame); return SceneCache.GetValue(frame.Surface, static _ => new()).Get(frame.Surface, frame, requireTriangles: false); }

    // A recorder can build the payload into a caller-owned buffer without storing it in the snapshot.
    internal void WriteTriangleRows(RayGiTriangleGpu[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length != TriangleCount)
            throw new ArgumentException("Triangle output must match the exact triangle count.", nameof(destination));
        if (originalGeometry?.Source is { } source)
            FillTriangleRows(source, Environment.ProcessorCount, destination);
        else
            Triangles.CopyTo(destination);
    }

    private static SubTextureUvData BuildSubTextureUvs(SceneSnapshot source)
    {
        var stream = source.AdditionalUvs ?? throw new ArgumentException("SubTexture GI transport requires explicit UV1.");
        if (stream.ChannelCount < 1 || stream.Vertices.Length != source.VertexCount)
            throw new ArgumentException("SubTexture UV1 must preserve vertex order and count.");
        var rows = new RayGiSubTextureUvGpu[source.Indices.Length / 3];
        for (int i = 0; i < source.Indices.Length; i += 3)
        {
            var a = stream.Vertices[(int)source.Indices[i]].Uv1;
            var b = stream.Vertices[(int)source.Indices[i + 1]].Uv1;
            var c = stream.Vertices[(int)source.Indices[i + 2]].Uv1;
            rows[i / 3] = new(new(a.X, a.Y, b.X, b.Y), new(c.X, c.Y, 0, 0));
        }
        return new(rows);
    }
    private static bool DoubleSided(OriginalStyleScene style, OriginalFrameScene? frame, int i)
        => style.Materials[i].DoubleSided || (frame?.Options is not null && frame.Materials[i].CullTransparentSurface && MathF.Min(style.Snapshot.Materials[i].Diffuse.W, 1) < 1);

    private static GeometryData BuildGeometry(SceneSnapshot source)
    {
        if (source.Indices.Length / 3 >= 1 << 24) throw new ArgumentException("RGBA32F Gbuffer IDs require fewer than 2^24 triangles.");
        var pixels = TextureCache.GetValue(source.Textures, BuildTextures);
        return new(source, pixels);
    }

    private static int TriangleConversionParallelism(int processorCount) => Math.Clamp(processorCount, 1, 16);

    private static RayGiTriangleGpu[] BuildTriangleRows(SceneSnapshot source, int maxDegreeOfParallelism)
    {
        if (source.Indices.Length / 3 >= 1 << 24) throw new ArgumentException("RGBA32F Gbuffer IDs require fewer than 2^24 triangles.");
        // Validated material ranges partition all triangle indices. Conversion assigns
        // every row before publishing the array, so a large zero-fill is unnecessary.
        var worldTriangles = GC.AllocateUninitializedArray<RayGiTriangleGpu>(source.Indices.Length / 3);
        FillTriangleRows(source, maxDegreeOfParallelism, worldTriangles);
        return worldTriangles;
    }

    private static void FillTriangleRows(SceneSnapshot source, int maxDegreeOfParallelism, RayGiTriangleGpu[] worldTriangles)
    {
        Matrix4x4.Invert(source.World, out var inverse);
        var normalWorld = Matrix4x4.Transpose(inverse);
        // Each worker reads immutable vertex chunks without materializing the compatibility view,
        // and writes a disjoint triangle range.
        int invalid = 0;
        void ConvertRange(int m, int start, int end)
        {
            var indices = source.Indices;
            bool finite = true;
            for (int i = start; i < end; i += 3)
            {
                var a = source.GetVertex((int)indices[i]); var b = source.GetVertex((int)indices[i + 1]); var c = source.GetVertex((int)indices[i + 2]);
                var triangle = new RayGiTriangleGpu(new(Vector3.Transform(a.Position, source.World), 0), new(Vector3.Transform(b.Position, source.World), 0), new(Vector3.Transform(c.Position, source.World), 0),
                    new(Vector3.TransformNormal(a.Normal, normalWorld), a.Uv.X), new(Vector3.TransformNormal(b.Normal, normalWorld), b.Uv.X), new(Vector3.TransformNormal(c.Normal, normalWorld), c.Uv.X),
                    new(a.Uv.Y, b.Uv.Y, c.Uv.Y, c.Normal.Z), new(a.Normal, b.Normal.X), new(b.Normal.Y, b.Normal.Z, c.Normal.X, c.Normal.Y), new((uint)m, (uint)(i / 3), 1, 0));
                worldTriangles[i / 3] = triangle;
                finite &= Finite(triangle.P0) && Finite(triangle.P1) && Finite(triangle.P2)
                    && Finite(triangle.N0U0) && Finite(triangle.N1U1) && Finite(triangle.N2U2);
            }
            if (!finite) Interlocked.Exchange(ref invalid, 1);
        }
        int workers = TriangleConversionParallelism(maxDegreeOfParallelism);
        if (workers == 1 || worldTriangles.Length < 32768)
        {
            for (int m = 0; m < source.Materials.Count; m++)
                ConvertRange(m, source.Materials[m].StartIndex, source.Materials[m].StartIndex + source.Materials[m].IndexCount);
        }
        else
        {
            const int blockIndices = 4096 * 3;
            var blocks = new List<(int Material, int Start, int End)>();
            for (int m = 0; m < source.Materials.Count; m++)
            {
                var material = source.Materials[m]; int end = material.StartIndex + material.IndexCount;
                for (int start = material.StartIndex; start < end; start += blockIndices)
                    blocks.Add((m, start, Math.Min(start + blockIndices, end)));
            }
            Parallel.For(0, blocks.Count, new ParallelOptions { MaxDegreeOfParallelism = workers, TaskScheduler = TaskScheduler.Default }, i =>
            {
                var block = blocks[i]; ConvertRange(block.Material, block.Start, block.End);
            });
        }
        // Preserve the public failure type instead of exposing Parallel.For's AggregateException.
        if (invalid != 0) throw new ArgumentException("Transformed GI positions and normals must remain finite.");
    }

    private static TextureData BuildTextures(ReadOnlyCollection<SceneTexture> images)
    {
        int count = 1;
        foreach (var image in images) count = checked(count + image.Rgba.Length / 4);
        var packed = new uint[count]; packed[0] = 0xffffffff;
        var textures = new RayGiUInt4[images.Count];
        var allAlpha255 = new bool[images.Count];
        int offset = 1;
        for (int i = 0; i < textures.Length; i++)
        {
            var image = images[i];
            textures[i] = new((uint)offset, (uint)image.Width, (uint)image.Height, 0);
            var bytes = image.Rgba;
            bool opaque = true;
            for (int j = 0; j < bytes.Length; j += 4)
            {
                byte alpha = bytes[j + 3];
                opaque &= alpha == 255;
                packed[offset++] = (uint)(bytes[j] | bytes[j + 1] << 8 | bytes[j + 2] << 16 | alpha << 24);
            }
            allAlpha255[i] = opaque;
        }
        return new(packed, textures, allAlpha255);
    }

    private static RayGiMaterialGpu[] BuildMaterials(OriginalStyleScene style, GeometryData geometry, bool[] enabled, bool[] doubleSided)
    {
        RayGiUInt4 Texture(int index)
        {
            var texture = index < 0 ? new RayGiUInt4(0, 1, 1, 0) : geometry.Pixels.Textures[index];
            return new(texture.X, texture.Y, texture.Z, (uint)style.Profile.TextureSampling);
        }
        var transport = new RayGiMaterialGpu[style.Materials.Count];
        for (int m = 0; m < transport.Length; m++)
        {
            var material = style.Snapshot.Materials[m]; var binding = style.Materials[m];
            var sphere = Texture(binding.SphereTexture);
            var diffuse = material.Diffuse; diffuse.W = MathF.Min(diffuse.W, 1);
            bool alphaOne = material.DiffuseTexture < 0 ? (geometry.Pixels.Texels[0] >> 24) == 255
                : geometry.Pixels.AllAlpha255[material.DiffuseTexture];
            var flags = alphaOne ? new RayGiUInt4(binding.SphereMode == OriginalSphereMode.None ? 1u : 2u, 0, 0, 0) : default;
            transport[m] = new(diffuse, new(material.Emission, 0), Texture(material.DiffuseTexture),
                new(sphere.X, sphere.Y, sphere.Z, (uint)binding.SphereMode),
                new(material.AlphaCutoff, doubleSided[m] ? 1 : 0, enabled[m] ? 1 : 0, 0), flags);
        }
        return transport;
    }

    private static RayGiDiffuseTextureFactorGpu[] BuildDiffuseTextureFactors(OriginalStyleScene style)
    {
        // No-map techniques ignore diffuse factors instead of transforming the white fallback.
        bool needed = Enumerable.Range(0, style.Materials.Count).Any(i =>
            style.Snapshot.Materials[i].DiffuseTexture >= 0 && !style.Materials[i].TextureFactors.Diffuse.IsIdentity);
        if (!needed) return [];
        return Enumerable.Range(0, style.Materials.Count).Select(i =>
        {
            var factor = style.Materials[i].TextureFactors.Diffuse;
            return style.Snapshot.Materials[i].DiffuseTexture < 0 ? RayGiDiffuseTextureFactorGpu.Identity
                : new RayGiDiffuseTextureFactorGpu(factor.Multiply, factor.Add);
        }).ToArray();
    }

    /// <summary>CPU reference for shared mip0 point/bilinear sampling. The production integrator samples on GPU.</summary>
    public Vector4 SampleTexture(RayGiUInt4 texture, Vector2 uv, OriginalTextureSampling? samplingOverride = null)
    {
        ValidateTexture(texture);
        var sampling = samplingOverride ?? (OriginalTextureSampling)texture.W;
        if (!Enum.IsDefined(sampling) || !float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) throw new ArgumentException("Invalid texture sample.");
        bool wrap = sampling is OriginalTextureSampling.PointWrap or OriginalTextureSampling.LinearWrap;
        bool linear = sampling is OriginalTextureSampling.LinearClamp or OriginalTextureSampling.LinearWrap;
        // Reduce before conversion to avoid integer overflow for large, finite authored UVs.
        uv = wrap ? new(uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y)) : Vector2.Clamp(uv, Vector2.Zero, Vector2.One);
        int width = (int)texture.Y, height = (int)texture.Z;
        int Coordinate(int value, int size) => wrap ? ((value % size) + size) % size : Math.Clamp(value, 0, size - 1);
        Vector4 Fetch(int x, int y)
        {
            uint p = texels[checked((int)texture.X + Coordinate(y, height) * width + Coordinate(x, width))];
            return new((p & 255) / 255f, ((p >> 8) & 255) / 255f, ((p >> 16) & 255) / 255f, (p >> 24) / 255f);
        }
        if (!linear) return Fetch((int)MathF.Floor(uv.X * width), (int)MathF.Floor(uv.Y * height));
        float x = uv.X * width - .5f, y = uv.Y * height - .5f;
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        return Vector4.Lerp(Vector4.Lerp(Fetch(ix, iy), Fetch(ix + 1, iy), x - ix), Vector4.Lerp(Fetch(ix, iy + 1), Fetch(ix + 1, iy + 1), x - ix), y - iy);
    }

    public bool AcceptCandidate(uint triangleIndex, Vector2 barycentrics, Vector3 direction)
    {
        var t = Triangles[checked((int)triangleIndex)]; var m = materials[t.Meta.X];
        if (m.Parameters.Z == 0) return false;
        var a = Xyz(t.P0); var b = Xyz(t.P1); var c = Xyz(t.P2);
        if (m.Parameters.Y == 0 && Vector3.Dot(Vector3.Cross(b - a, c - a), direction) >= 0) return false;
        float w = 1 - barycentrics.X - barycentrics.Y;
        var uv = new Vector2(t.N0U0.W * w + t.N1U1.W * barycentrics.X + t.N2U2.W * barycentrics.Y, t.V012.X * w + t.V012.Y * barycentrics.X + t.V012.Z * barycentrics.Y);
        float alpha = MathF.Min(m.DiffuseFactor.W, 1) * SampleTexture(m.DiffuseTexture, uv).W;
        if (m.SphereTexture.W != 0)
        {
            var sphereUv = m.SphereTexture.W == (uint)OriginalSphereMode.SubTexture
                ? SubTextureUv(triangleIndex, barycentrics) : SphereUv(triangleIndex, barycentrics);
            alpha *= SampleTexture(m.SphereTexture, sphereUv, (OriginalTextureSampling)m.DiffuseTexture.W).W;
        }
        return alpha >= m.Parameters.X;
    }

    /// <summary>Barycentric UV1.xy only: independent of UV0, sphere rotation and preserved extra UV channels.</summary>
    public Vector2 SubTextureUv(uint triangleIndex, Vector2 barycentrics)
    {
        var row = subTextureUvs[checked((int)triangleIndex)];
        float w = 1 - barycentrics.X - barycentrics.Y;
        return new(row.Uv01.X * w + row.Uv01.Z * barycentrics.X + row.Uv2.X * barycentrics.Y,
            row.Uv01.Y * w + row.Uv01.W * barycentrics.X + row.Uv2.Y * barycentrics.Y);
    }

    /// <summary>Raster-equivalent sphere coordinates. Explicit fixtures use baked UVs (Meta.Z=0);
    /// adapted triangles use raw original normals (Meta.Z=1), rotated by this lightweight scene wrapper.</summary>
    public Vector2 SphereUv(uint triangleIndex, Vector2 barycentrics)
    {
        var t = Triangles[checked((int)triangleIndex)];
        float w = 1 - barycentrics.X - barycentrics.Y;
        if (t.Meta.Z == 1)
        {
            var a = OriginalStyleMath.SphereUv(new(t.SphereUv01.X, t.SphereUv01.Y, t.SphereUv01.Z), SphereRotation);
            var b = OriginalStyleMath.SphereUv(new(t.SphereUv01.W, t.SphereUv2.X, t.SphereUv2.Y), SphereRotation);
            var c = OriginalStyleMath.SphereUv(new(t.SphereUv2.Z, t.SphereUv2.W, t.V012.W), SphereRotation);
            return a * w + b * barycentrics.X + c * barycentrics.Y;
        }
        return new(t.SphereUv01.X * w + t.SphereUv01.Z * barycentrics.X + t.SphereUv2.X * barycentrics.Y,
            t.SphereUv01.Y * w + t.SphereUv01.W * barycentrics.X + t.SphereUv2.Y * barycentrics.Y);
    }

    private void ValidateTexture(RayGiUInt4 t)
    {
        if (t.Y == 0 || t.Z == 0 || t.Y > 4096 || t.Z > 4096 || (ulong)t.X + (ulong)t.Y * t.Z > (ulong)texels.Length)
            throw new ArgumentException("Texture view is outside the packed texel table.");
    }
    internal static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);
    private static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
}
