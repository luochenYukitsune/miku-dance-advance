using System.Numerics;

namespace Mmd.Modern.Physics;

public enum SoftBodyShape { TriMesh, Rope }
public readonly record struct SoftBodyAnchor(int BodyIndex, int VertexIndex, bool NearMode = false);
public sealed record SoftBodySettings
{
    public float Mass { get; init; } = 1;
    public float Margin { get; init; } = .02f;
    public int Group { get; init; }
    public ushort CollisionMask { get; init; } = ushort.MaxValue;
    public int Flags { get; init; }
    public int BendingDistance { get; init; } = 2;
    public int ClusterCount { get; init; }
    public int AeroModel { get; init; }
    // PMX order: VCF,DP,DG,LF,PR,VC,DF,MT,CHR,KHR,SHR,AHR.
    public IReadOnlyList<float> Config { get; init; } = Array.AsReadOnly(new float[] { 1, .01f, 0, 0, 0, 0, .2f, 0, 1, .1f, 1, .7f });
    public IReadOnlyList<float> ClusterConfig { get; init; } = Array.AsReadOnly(new float[] { 1, .1f, .5f, .5f, .5f, .5f });
    public IReadOnlyList<float> Stiffness { get; init; } = Array.AsReadOnly(new float[] { 1, 1, 1 });
    public IReadOnlyList<int> Iterations { get; init; } = Array.AsReadOnly(new[] { 0, 4, 0, 4 });
}

/// <summary>Immutable source-material topology with explicit original model vertex IDs.
/// Definitions may share IDs: a supporting backend must simulate them with common nodes.</summary>
public sealed class SoftBodyDefinition
{
    /// <summary>Largest accepted absolute vertex coordinate; the native solver enforces the same bound.</summary>
    public const float MaxCoordinate = 1e6f;
    public string Name { get; }
    public SoftBodyShape Shape { get; }
    public IReadOnlyList<int> VertexIndices { get; }
    public IReadOnlyList<Vector3> Positions { get; }
    public IReadOnlyList<int> TriangleIndices { get; }
    public IReadOnlyList<int> PinVertexIndices { get; }
    public IReadOnlyList<SoftBodyAnchor> Anchors { get; }
    public SoftBodySettings Settings { get; }

    public SoftBodyDefinition(string name, SoftBodyShape shape, IEnumerable<int> vertexIndices,
        IEnumerable<Vector3> positions, IEnumerable<int> triangleIndices, IEnumerable<int> pinVertexIndices,
        IEnumerable<SoftBodyAnchor> anchors, SoftBodySettings settings)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(settings);
        var ids = vertexIndices.ToArray(); var points = positions.ToArray(); var triangles = triangleIndices.ToArray();
        var pins = pinVertexIndices.ToArray(); var anchorArray = anchors.ToArray();
        if (!Enum.IsDefined(shape) || ids.Length < 2 || ids.Length != points.Length || ids.Any(i => i < 0)
            || ids.Distinct().Count() != ids.Length || points.Any(p => !PhysicsMath.Finite(p)))
            throw new ArgumentException("Invalid soft-body vertex mapping.");
        // The native solver quantizes positions into sparse-SDF cells with an int conversion; huge finite
        // coordinates from a damaged or crafted model overflowed it and crashed the process.
        if (points.Any(p => MathF.Abs(p.X) > MaxCoordinate || MathF.Abs(p.Y) > MaxCoordinate || MathF.Abs(p.Z) > MaxCoordinate))
            throw new ArgumentException($"Soft-body vertex positions must be within ±{MaxCoordinate:0} units.");
        var map = ids.ToHashSet();
        if (pins.Any(i => !map.Contains(i)) || pins.Distinct().Count() != pins.Length
            || anchorArray.Any(a => !map.Contains(a.VertexIndex) || a.BodyIndex < 0))
            throw new ArgumentException("Pins and anchors must reference material vertices and valid rigid indices.");
        if (shape == SoftBodyShape.TriMesh && (triangles.Length == 0 || triangles.Length % 3 != 0
            || triangles.Any(i => i < 0 || i >= ids.Length))) throw new ArgumentException("Invalid soft-body triangle topology.");
        for (int i = 0; i < triangles.Length; i += 3)
            if (shape == SoftBodyShape.TriMesh && Vector3.Cross(points[triangles[i + 1]] - points[triangles[i]],
                points[triangles[i + 2]] - points[triangles[i]]).LengthSquared() <= 1e-16f)
                throw new ArgumentException("Degenerate soft-body triangle.");
        if (settings.Config is null || settings.ClusterConfig is null || settings.Stiffness is null || settings.Iterations is null
            || settings.Config.Count != 12 || settings.ClusterConfig.Count != 6 || settings.Stiffness.Count != 3 || settings.Iterations.Count != 4
            || settings.Config.Concat(settings.ClusterConfig).Concat(settings.Stiffness).Any(v => !float.IsFinite(v))
            || settings.Iterations.Any(v => v < 0 || v > 1024) || settings.Group is < 0 or > 15 || settings.Flags is < 0 or > 7
            || settings.BendingDistance < 0 || settings.ClusterCount < 0 || settings.AeroModel is < 0 or > 4
            || !float.IsFinite(settings.Mass) || settings.Mass <= 0 || !float.IsFinite(settings.Margin) || settings.Margin < 0)
            throw new ArgumentException("Invalid soft-body configuration.", nameof(settings));
        // Bullet stores each node's inverse mass as count / total; keep both the mass and its inverse finite.
        float nodeMass = settings.Mass / ids.Length;
        if (!float.IsFinite(nodeMass) || nodeMass <= 0 || !float.IsFinite(1 / nodeMass) || !NativeInverseMassFinite(settings.Mass, ids.Length))
            throw new ArgumentException("Soft-body mass per vertex is outside the float solver range.", nameof(settings));
        Name = name; Shape = shape; VertexIndices = Array.AsReadOnly(ids); Positions = Array.AsReadOnly(points);
        TriangleIndices = Array.AsReadOnly(triangles); PinVertexIndices = Array.AsReadOnly(pins); Anchors = Array.AsReadOnly(anchorArray);
        Settings = settings with { Config = Array.AsReadOnly(settings.Config.ToArray()),
            ClusterConfig = Array.AsReadOnly(settings.ClusterConfig.ToArray()), Stiffness = Array.AsReadOnly(settings.Stiffness.ToArray()),
            Iterations = Array.AsReadOnly(settings.Iterations.ToArray()) };
    }

    /// <summary>Replays btSoftBody::setTotalMass in float32 for nodes created with mass 1 (inverse mass 1):
    /// tm = sum of node masses, itm = 1 / tm, then each m_im /= itm * mass. The managed per-node check above
    /// rounds differently, so a mass can pass it while the native inverse mass becomes infinite or zero.</summary>
    internal static bool NativeInverseMassFinite(float mass, int nodes)
    {
        float total = 0;
        for (int i = 0; i < nodes; i++) total += 1f;
        float inverseTotal = 1f / total;
        float scale = inverseTotal * mass;
        if (!float.IsFinite(scale) || scale <= 0) return false;
        float inverseMass = 1f / scale;
        return float.IsFinite(inverseMass) && inverseMass > 0;
    }
}

public sealed record SoftBodySample(int[] VertexIndices, Vector3[] Positions, Vector3[] Normals);

/// <summary>Optional complete rigid-proxy pose replacement without resetting soft nodes, velocities,
/// constraints or simulation time. Validate the entire batch before mutation: any failure must leave
/// every proxy and all soft history unchanged. Collider topology and order stay fixed.</summary>
public interface ISoftBodyRigidProxyUpdater
{
    void UpdateRigidProxies(ReadOnlySpan<Matrix4x4> rigidWorlds);

}

/// <summary>Rigid transforms are externally simulated collision/anchor proxies; no reverse impulse feedback.</summary>
public interface ISoftBodyBackend : IDisposable
{
    IReadOnlyList<string> Diagnostics { get; }
    void Initialize(IReadOnlyList<SoftBodyDefinition> definitions, IReadOnlyList<PhysicsBodyDefinition> colliders);
    void Reset(ReadOnlySpan<Vector3> globalSkinnedPositions, ReadOnlySpan<Matrix4x4> rigidWorlds);
    void Step(float seconds, ReadOnlySpan<Vector3> globalSkinnedPositions, ReadOnlySpan<Matrix4x4> rigidWorlds);
    /// <summary>Each global vertex occurs at most once across all samples; shared source definitions
    /// must reference a common simulated node, never independent samples averaged after solving.</summary>
    IReadOnlyList<SoftBodySample> Sample();
    void SetGravity(Vector3 gravity);
}
