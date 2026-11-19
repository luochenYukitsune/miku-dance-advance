using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>64-byte GPU triangle-corner record. Tangent.w is Mikk sign; UvParity.z is separate World sign.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct D3D12TangentCorner(Vector4 WorldPosition, Vector4 WorldNormal, Vector4 Tangent, Vector4 UvParity);

internal sealed class D3D12TangentCornerGeometryData
{
    private readonly D3D12TangentCorner[] corners;
    internal ReadOnlySpan<D3D12TangentCorner> Corners => corners;
    internal string Fingerprint { get; }
    internal string AppearanceFingerprint { get; }

    internal D3D12TangentCornerGeometryData(D3D12TangentCorner[] corners)
    {
        this.corners = corners;
        Fingerprint = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(corners.AsSpan())));
        using var appearance = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<Vector4> values = stackalloc Vector4[3];
        foreach (var corner in corners)
        {
            values[0] = corner.WorldNormal; values[1] = corner.Tangent; values[2] = corner.UvParity;
            appearance.AppendData(MemoryMarshal.AsBytes(values));
        }
        AppearanceFingerprint = Convert.ToHexString(appearance.GetHashAndReset());
    }
}

/// <summary>
/// Generic per-draw tangent-corner and raw-radial-seed geometry builder computed purely from evaluated
/// mesh vertices, normals, UVs, and indices using <see cref="D3D12Mikktspace"/>.
/// </summary>
public sealed class D3D12TangentCornerGeometry
{
    public SceneSnapshot Source { get; }
    public int LocalMaterialIndex { get; }

    public D3D12TangentCornerGeometry(SceneSnapshot source, int localMaterialIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)localMaterialIndex >= (uint)source.Materials.Count) throw new ArgumentOutOfRangeException(nameof(localMaterialIndex));
        Source = source; LocalMaterialIndex = localMaterialIndex;
    }

    private const int CacheCapacity = 32;
    private static readonly ConditionalWeakTable<SceneSnapshot, SnapshotCache> SnapshotCaches = new();
    private static readonly object ContentLock = new();
    private static readonly Dictionary<string, WeakReference<D3D12TangentCornerGeometryData>> ContentCache = new();
    private static readonly Queue<string> ContentOrder = new();
    private static readonly byte[] RevisionBytes = Encoding.UTF8.GetBytes(D3D12Mikktspace.GeneratorRevision);

    internal static int ResolveUvChannel(D3D12PluginBufferBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.DataKey)) return 0;
        string key = binding.DataKey.Trim();
        if (key.Equals("default", StringComparison.OrdinalIgnoreCase)) return 0;
        if (key.Length == 1 && key[0] is >= '0' and <= '4') return key[0] - '0';
        int uvIndex = key.LastIndexOf("uv", StringComparison.OrdinalIgnoreCase);
        if (uvIndex >= 0 && uvIndex + 3 == key.Length && key[uvIndex + 2] is >= '0' and <= '4')
            return key[uvIndex + 2] - '0';
        throw new ArgumentException($"Unsupported MikkTangentCorners DataKey '{binding.DataKey}'. Expected empty or uv0..uv4.", nameof(binding));
    }

    internal static ReadOnlySpan<Vector3> GetOrComputeRawRadialSeeds(SceneSnapshot source, out object cacheIdentity)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.RawRadialSeeds is { } explicitSeeds)
        {
            if (explicitSeeds.Vertices.Length != source.VertexCount)
                throw new ArgumentException("Raw radial seed stream length must match scene vertex count.", nameof(source));
            ValidateWorldSeeds(explicitSeeds.Vertices, source.World);
            cacheIdentity = explicitSeeds;
            return explicitSeeds.Vertices;
        }

        var snapshotCache = SnapshotCaches.GetValue(source, _ => new());
        lock (snapshotCache)
        {
            if (snapshotCache.ComputedRawRadialSeeds is { } existing)
            {
                ValidateWorldSeeds(existing, source.World);
                cacheIdentity = existing;
                return existing;
            }
            var computed = ComputeRawRadialSeeds(source);
            ValidateWorldSeeds(computed, source.World);
            snapshotCache.ComputedRawRadialSeeds = computed;
            cacheIdentity = computed;
            return computed;
        }
    }

    private static Vector3[] ComputeRawRadialSeeds(SceneSnapshot source)
    {
        int count = source.VertexCount;
        var seeds = new Vector3[count];
        if (count == 0) return seeds;

        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        bool anyFinite = false;
        for (int i = 0; i < count; i++)
        {
            var p = source.GetVertex(i).Position;
            if (!Finite(p)) continue;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            anyFinite = true;
        }
        Vector3 center = anyFinite ? (min + max) * 0.5f : Vector3.Zero;

        for (int i = 0; i < count; i++)
        {
            var v = source.GetVertex(i);
            Vector3 radial = Finite(v.Position) ? v.Position - center : Vector3.Zero;
            float len2 = radial.LengthSquared();
            if (float.IsFinite(len2) && len2 > 1e-12f)
            {
                seeds[i] = radial * (1f / MathF.Sqrt(len2));
                continue;
            }
            Vector3 n = v.Normal;
            float nLen2 = n.LengthSquared();
            if (Finite(n) && float.IsFinite(nLen2) && nLen2 > 1e-12f)
            {
                n *= 1f / MathF.Sqrt(nLen2);
                Vector3 axis = MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
                Vector3 fallback = Vector3.Cross(n, axis);
                float fLen2 = fallback.LengthSquared();
                seeds[i] = fLen2 > 1e-12f ? fallback * (1f / MathF.Sqrt(fLen2)) : Vector3.UnitY;
            }
            else
            {
                seeds[i] = Vector3.UnitY;
            }
        }
        return seeds;
    }

    private static void ValidateWorldSeeds(ReadOnlySpan<Vector3> seeds, Matrix4x4 world)
    {
        if (world == Matrix4x4.Identity) return;
        foreach (var seed in seeds)
        {
            var worldSeed = Vector3.TransformNormal(seed, world);
            if (!Finite(worldSeed))
                throw new ArgumentException("Raw radial seed direction overflows its scene World transform.");
        }
    }

    internal static D3D12TangentCornerGeometryData Build(SceneSnapshot source, int materialIndex, int uvChannel)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)materialIndex >= (uint)source.Materials.Count) throw new ArgumentOutOfRangeException(nameof(materialIndex));
        if (uvChannel is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(uvChannel));
        if (uvChannel > 0 && (source.AdditionalUvs is null || source.AdditionalUvs.ChannelCount < uvChannel))
            throw new ArgumentException($"MikkTSpace tangent corners require selected effective UV{uvChannel}, which is absent from the evaluated capture.", nameof(source));
        if (source.Indices.Length > D3D12Mikktspace.MaximumCorners)
            throw new ArgumentException($"MikkTSpace source mesh exceeds the {D3D12Mikktspace.MaximumCorners:N0} corner generation budget.", nameof(source));
        var snapshotCache = SnapshotCaches.GetValue(source, _ => new());
        lock (snapshotCache)
        {
            var key = (materialIndex, uvChannel);
            if (snapshotCache.Data.TryGetValue(key, out var cached)) return cached;
            if (!snapshotCache.FullData.TryGetValue(uvChannel, out var full))
            {
                full = BuildUncached(source, uvChannel);
                snapshotCache.FullData.Add(uvChannel, full);
            }
            var material = source.Materials[materialIndex];
            var built = material.StartIndex == 0 && material.IndexCount == full.Corners.Length ? full
                : new D3D12TangentCornerGeometryData(full.Corners.Slice(material.StartIndex, material.IndexCount).ToArray());
            if (snapshotCache.Data.Count >= CacheCapacity) snapshotCache.Data.Clear();
            snapshotCache.Data.Add(key, built);
            return built;
        }
    }

    private static D3D12TangentCornerGeometryData BuildUncached(SceneSnapshot source, int uvChannel)
    {
        int cornerCount = source.Indices.Length;
        var linearWorld = source.World; linearWorld.M41 = 0; linearWorld.M42 = 0; linearWorld.M43 = 0;
        if (!Matrix4x4.Invert(linearWorld, out var inverse)) throw new ArgumentException("Tangent-corner World transform must be invertible.", nameof(source));
        var normalWorld = Matrix4x4.Transpose(inverse);
        float parity = DeterminantSign(source.World);
        var localToCompact = new Dictionary<uint, uint>();
        var positions = new List<Vector3>(); var normals = new List<Vector3>();
        var shadingNormals = new List<Vector3>(); var uvs = new List<Vector2>();
        var indices = new uint[cornerCount];
        var sourceIndices = source.Indices;
        for (int i = 0; i < cornerCount; ++i)
        {
            uint sourceIndex = sourceIndices[i];
            if (!localToCompact.TryGetValue(sourceIndex, out uint compact))
            {
                var vertex = source.GetVertex(checked((int)sourceIndex));
                Vector2 uv = EffectiveUv(source, checked((int)sourceIndex), uvChannel);
                Vector3 unitNormal = NormalizeRequired(vertex.Normal, "current local shading normal");
                Vector3 worldPosition = Vector3.Transform(vertex.Position, source.World);
                Vector3 worldNormal = Vector3.TransformNormal(vertex.Normal, normalWorld);
                if (!Finite(worldPosition) || !Finite(worldNormal) || worldNormal.LengthSquared() <= 0 || !float.IsFinite(worldNormal.LengthSquared()))
                    throw new ArgumentException("Transformed positions and original shading normals must remain finite and nonzero.", nameof(source));
                compact = checked((uint)positions.Count); localToCompact.Add(sourceIndex, compact);
                positions.Add(vertex.Position); normals.Add(unitNormal); shadingNormals.Add(vertex.Normal); uvs.Add(uv);
            }
            indices[i] = compact;
        }
        var localPositions = positions.ToArray(); var localNormals = normals.ToArray();
        var localShadingNormals = shadingNormals.ToArray(); var selectedUvs = uvs.ToArray();
        string consumedKey = InputFingerprint(localPositions, localShadingNormals, selectedUvs, indices, source.World);
        lock (ContentLock)
            if (ContentCache.TryGetValue(consumedKey, out var weak) && weak.TryGetTarget(out var cached)) return cached;
        Vector4[] tangents = cornerCount == 0 ? [] : D3D12Mikktspace.Generate(localPositions, localNormals, selectedUvs, indices);
        var corners = new D3D12TangentCorner[cornerCount];
        for (int i = 0; i < corners.Length; ++i)
        {
            int vertex = checked((int)indices[i]);
            Vector4 tangent = tangents[i];
            Vector3 worldTangent = NormalizeRequired(Vector3.TransformNormal(new(tangent.X, tangent.Y, tangent.Z), normalWorld), "normal-matrix transformed tangent");
            if (tangent.W is not (1 or -1)) throw new ArgumentException("MikkTSpace corner handedness must be +1 or -1.");
            corners[i] = new(new(Vector3.Transform(localPositions[vertex], source.World), 1),
                new(Vector3.TransformNormal(localShadingNormals[vertex], normalWorld), 0),
                new(worldTangent, tangent.W), new(selectedUvs[vertex], parity, 0));
        }
        var result = new D3D12TangentCornerGeometryData(corners);
        lock (ContentLock)
        {
            if (!ContentCache.ContainsKey(consumedKey)) ContentOrder.Enqueue(consumedKey);
            ContentCache[consumedKey] = new(result);
            while (ContentOrder.Count > CacheCapacity) ContentCache.Remove(ContentOrder.Dequeue());
        }
        return result;
    }

    private static Vector2 EffectiveUv(SceneSnapshot source, int vertex, int channel)
    {
        if (channel == 0) return source.GetVertex(vertex).Uv;
        var value = source.AdditionalUvs!.Vertices[vertex];
        Vector4 selected = channel switch { 1 => value.Uv1, 2 => value.Uv2, 3 => value.Uv3, 4 => value.Uv4, _ => throw new ArgumentOutOfRangeException(nameof(channel)) };
        return new(selected.X, selected.Y);
    }

    private static Vector3 NormalizeRequired(Vector3 value, string description)
    {
        float length = value.LengthSquared();
        if (!Finite(value) || !float.IsFinite(length) || length <= 0)
            throw new ArgumentException($"Tangent-corner {description} must be finite, nonzero and representable.");
        var result = Vector3.Normalize(value);
        if (!Finite(result)) throw new ArgumentException($"Tangent-corner {description} cannot be normalized.");
        return result;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static float DeterminantSign(Matrix4x4 value)
    {
        double determinant = (double)value.M11 * ((double)value.M22 * value.M33 - (double)value.M23 * value.M32)
            - (double)value.M12 * ((double)value.M21 * value.M33 - (double)value.M23 * value.M31)
            + (double)value.M13 * ((double)value.M21 * value.M32 - (double)value.M22 * value.M31);
        if (!double.IsFinite(determinant) || determinant == 0) throw new ArgumentException("Scene World transform has invalid determinant.");
        return determinant < 0 ? -1 : 1;
    }

    private static string InputFingerprint(Vector3[] positions, Vector3[] normals, Vector2[] uvs, uint[] indices, Matrix4x4 world)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(RevisionBytes);
        ReadOnlySpan<int> lengths = [positions.Length, indices.Length];
        hash.AppendData(MemoryMarshal.AsBytes(lengths));
        hash.AppendData(MemoryMarshal.AsBytes(positions.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(normals.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(uvs.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(indices.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref world, 1)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class SnapshotCache
    {
        internal Dictionary<int, D3D12TangentCornerGeometryData> FullData { get; } = new();
        internal Dictionary<(int Material, int Uv), D3D12TangentCornerGeometryData> Data { get; } = new();
        internal Vector3[]? ComputedRawRadialSeeds { get; set; }
    }
}
