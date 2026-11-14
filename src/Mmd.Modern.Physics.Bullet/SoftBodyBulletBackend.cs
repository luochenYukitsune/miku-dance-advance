using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mmd.Modern.Physics.Bullet;

/// <summary>Independent Bullet 2.75 soft world. Reset replaces the world atomically at the skinned pose.</summary>
public sealed class SoftBodyBulletBackend : ISoftBodyBackend, ISoftBodyRigidProxyUpdater
{
    private Vector3 gravity;
    private readonly float? groundY;
    private SoftBodyDefinition[] definitions = [];
    private Component[] components = [];
    private PhysicsBodyDefinition[] colliders = [];
    private Handle? world;
    private bool disposed;
    public IReadOnlyList<string> Diagnostics { get; private set; } = Array.Empty<string>();

    public SoftBodyBulletBackend(Vector3? gravity = null, float? groundY = 0)
    {
        this.gravity = gravity ?? new(0, -98, 0); this.groundY = groundY;
        if (!Finite(this.gravity) || groundY is { } y && !float.IsFinite(y))
            throw new ArgumentException("Soft-world gravity/ground must be finite.");
    }

    public void Initialize(IReadOnlyList<SoftBodyDefinition> definitions, IReadOnlyList<PhysicsBodyDefinition> colliders)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(definitions); ArgumentNullException.ThrowIfNull(colliders);
        var candidate = definitions.ToArray(); var rigid = colliders.ToArray();
        if (candidate.Any(d => d is null) || rigid.Any(d => d is null)) throw new ArgumentException("Null soft definition/collider.");
        foreach (var d in candidate)
            if (d.Anchors.Any(a => a.BodyIndex >= rigid.Length)) throw new ArgumentException("Anchor rigid index exceeds collider count.");
        // Reuse rigid validation, without fabricating bone bindings in this independent world.
        _ = new CharacterPhysicsDefinition([], rigid.Select(d => d with { BoneIndex = -1 }), []);
        if (candidate.Any(d => d.Anchors.Any(a => a.NearMode && rigid[a.BodyIndex].Mode != PhysicsBodyMode.Kinematic)))
            throw new NotSupportedException("Physical Near anchors require reverse rigid-position feedback; only authored Kinematic Near anchors are supported by the independent soft world.");
        var groups = BuildComponents(candidate);
        var previous = world; world = null; this.definitions = candidate; components = groups; this.colliders = rigid; previous?.Dispose();
        Diagnostics = rigid.Length == 0 ? Array.Empty<string>() : Array.AsReadOnly(new[]
        { "Soft-body collision and anchors use externally driven rigid proxies; reverse impulses are not fed into the independent rigid solver." });
    }

    public void Reset(ReadOnlySpan<Vector3> globalSkinnedPositions, ReadOnlySpan<Matrix4x4> rigidWorlds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var points = ValidateTargets(globalSkinnedPositions); var poses = ValidateRigids(rigidWorlds);
        if (Native.Abi() != 1) throw new InvalidOperationException("Expected independent float32 Bullet 2.75 soft ABI1.");
        RequireSharedClusterCapability(components, Native.Features);
        if (components.Any(c => c.Parts.Length > 1))
        {
            try
            {
                if ((Native.Features() & 4) == 0) throw new NotSupportedException("This native soft library does not support common solver nodes; rebuild the soft bridge.");
            }
            catch (EntryPointNotFoundException error)
            {
                throw new NotSupportedException("This older native soft library has no common-node capability export; rebuild the soft bridge.", error);
            }
        }
        if (groundY.HasValue && definitions.Any(d => (d.Settings.Flags & 2) != 0))
        {
            try
            {
                if ((Native.Features() & 2) == 0) throw new NotSupportedException("This native soft library does not support cluster contact with analytic plane ground; rebuild the soft bridge.");
            }
            catch (EntryPointNotFoundException error)
            {
                throw new NotSupportedException("This older native soft library has no cluster-plane capability export; rebuild the soft bridge.", error);
            }
        }
        if (definitions.Any(d => d.Anchors.Any(a => a.NearMode)))
        {
            try
            {
                if ((Native.Features() & 1) == 0) throw new NotSupportedException("This native soft library does not support Kinematic Near anchors; rebuild the soft bridge.");
            }
            catch (EntryPointNotFoundException error)
            {
                throw new NotSupportedException("This older native soft library has no Kinematic Near capability export; rebuild the soft bridge.", error);
            }
        }
        Check(Native.Create(Flatten([gravity]), groundY.HasValue ? 1 : 0, groundY ?? 0, out var pointer));
        Handle? candidate = new(pointer);
        try
        {
            if (candidate.IsInvalid) throw new InvalidOperationException("Native soft world is null.");
            for (int i = 0; i < colliders.Length; i++)
            {
                var c = colliders[i];
                Check(Native.AddCollider(candidate, (int)c.Shape, c.CollisionGroup, c.CollisionMask,
                    Flatten([c.Size]), poses[i]));
            }
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i]; var d = component.Parts[0]; var map = component.Map;
                var pins = component.Parts.SelectMany(p => p.PinVertexIndices).Distinct().Select(id => map[id]).ToArray();
                var allAnchors = component.Parts.SelectMany(p => p.Anchors).Distinct().ToArray();
                var anchors = allAnchors.SelectMany(a => new[] { map[a.VertexIndex], a.BodyIndex }).ToArray();
                if (component.Parts.Length == 1)
                    Check(Native.Add(candidate, points[i], component.Ids.Length, d.TriangleIndices.ToArray(),
                        d.TriangleIndices.Count, Options(d), Values(d), pins, pins.Length, anchors, allAnchors.Length));
                else
                {
                    var nodeOffsets = new List<int> { 0 }; var triangleOffsets = new List<int> { 0 };
                    var nodes = new List<int>(); var triangles = new List<int>();
                    foreach (var part in component.Parts)
                    {
                        nodes.AddRange(part.VertexIndices.Select(id => map[id])); nodeOffsets.Add(nodes.Count);
                        triangles.AddRange(part.TriangleIndices); triangleOffsets.Add(triangles.Count);
                    }
                    try
                    {
                        Check(Native.AddShared(candidate, points[i], component.Ids.Length, component.Parts.Length,
                            nodeOffsets.ToArray(), nodes.ToArray(), nodes.Count, triangleOffsets.ToArray(), triangles.ToArray(), triangles.Count,
                            component.Parts.SelectMany(Options).ToArray(), component.Parts.SelectMany(Values).ToArray(),
                            pins, pins.Length, anchors, allAnchors.Length));
                    }
                    catch (EntryPointNotFoundException error)
                    {
                        throw new NotSupportedException("Native soft library advertises common nodes but has no shared topology export; rebuild the soft bridge.", error);
                    }
                }
                var near = allAnchors.Where(a => a.NearMode).ToArray();
                if (near.Length != 0)
                    Check(Native.NearAnchors(candidate, i, near.SelectMany(a => new[] { map[a.VertexIndex], a.BodyIndex }).ToArray(), near.Length));
            }
            var previous = world; world = candidate; candidate = null; previous?.Dispose();
        }
        finally { candidate?.Dispose(); }
    }

    public void Step(float seconds, ReadOnlySpan<Vector3> globalSkinnedPositions, ReadOnlySpan<Matrix4x4> rigidWorlds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > .1f) throw new ArgumentOutOfRangeException(nameof(seconds));
        var points = ValidateTargets(globalSkinnedPositions); var poses = ValidateRigids(rigidWorlds);
        var w = GetWorld();
        for (int i = 0; i < poses.Length; i++) Check(Native.ColliderPose(w, i, poses[i]));
        for (int i = 0; i < points.Length; i++) Check(Native.Targets(w, i, points[i], components[i].Ids.Length));
        Check(Native.Step(w, seconds));
    }

    public IReadOnlyList<SoftBodySample> Sample()
    {
        ObjectDisposedException.ThrowIf(disposed, this); var w = GetWorld();
        var result = new SoftBodySample[components.Length];
        for (int i = 0; i < result.Length; i++)
        {
            var d = components[i]; int count = d.Ids.Length;
            var positions = new float[count * 3]; var normals = new float[count * 3];
            Check(Native.Sample(w, i, positions, normals, count));
            var p = Unflatten(positions); var n = Unflatten(normals);
            if (p.Concat(n).Any(v => !Finite(v))) throw new InvalidOperationException("Nonfinite soft output.");
            result[i] = new(d.Ids.ToArray(), p, n);
        }
        return Array.AsReadOnly(result);
    }

    public void UpdateRigidProxies(ReadOnlySpan<Matrix4x4> rigidWorlds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var poses = ValidateRigids(rigidWorlds); var w = GetWorld();
        var batch = poses.SelectMany(pose => pose).ToArray();
        try
        {
            if ((Native.Features() & 8) == 0)
                throw new NotSupportedException("This native soft library cannot atomically update rigid proxies; rebuild the soft bridge.");
            Check(Native.ColliderPoses(w, batch, poses.Length));
        }
        catch (EntryPointNotFoundException error)
        {
            throw new NotSupportedException("This older native soft library has no atomic rigid-proxy batch export; rebuild the soft bridge.", error);
        }
    }

    public void SetGravity(Vector3 gravity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Finite(gravity)) throw new ArgumentException("Gravity must be finite.", nameof(gravity));
        if (world is { } w) Check(Native.Gravity(w, Flatten([gravity])));
        this.gravity = gravity;
    }

    private float[][] ValidateTargets(ReadOnlySpan<Vector3> values)
    {
        var result = new float[components.Length][];
        for (int i = 0; i < result.Length; i++)
        {
            var d = components[i]; var points = new Vector3[d.Ids.Length];
            for (int j = 0; j < points.Length; j++)
            {
                int id = d.Ids[j];
                if (id >= values.Length || !Finite(values[id])) throw new ArgumentException("Invalid skinned target.", nameof(values));
                points[j] = values[id];
            }
            result[i] = Flatten(points);
        }
        return result;
    }

    private float[][] ValidateRigids(ReadOnlySpan<Matrix4x4> values)
    {
        if (values.Length != colliders.Length) throw new ArgumentException("Rigid proxy pose count mismatch.", nameof(values));
        var result = new float[values.Length][];
        for (int i = 0; i < result.Length; i++)
        {
            PhysicsMath.ValidateRigid(values[i], nameof(values));
            Matrix4x4.Decompose(values[i], out _, out var q, out var p); q = Quaternion.Normalize(q);
            result[i] = [p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W];
        }
        return result;
    }

    private Handle GetWorld() => world ?? throw new InvalidOperationException("Reset the soft world before stepping/sampling.");
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static float[] Flatten(IEnumerable<Vector3> values) => values.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray();
    private static Vector3[] Unflatten(float[] values) => Enumerable.Range(0, values.Length / 3)
        .Select(i => new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2])).ToArray();
    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"Bullet soft ABI failure: {Marshal.PtrToStringUTF8(Native.Error())}");
    }

    internal sealed record Component(SoftBodyDefinition[] Parts, int[] Ids, Dictionary<int, int> Map);

    internal static void RequireSharedClusterCapability(Component[] groups, Func<int> features)
    {
        if (!groups.Any(c => c.Parts.Length > 1 && (c.Parts[0].Settings.Flags & 2) != 0)) return;
        try
        {
            if ((features() & 16) == 0)
                throw new NotSupportedException("This native soft library does not support shared-node source clusters; rebuild the soft bridge.");
        }
        catch (EntryPointNotFoundException error)
        {
            throw new NotSupportedException("This older native soft library has no shared-node cluster capability export; rebuild the soft bridge.", error);
        }
    }

    internal static Component[] BuildComponents(SoftBodyDefinition[] source)
    {
        var parent = Enumerable.Range(0, source.Length).ToArray();
        int Root(int i) { while (i != parent[i]) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        var owner = new Dictionary<int, int>();
        var rest = new Dictionary<int, Vector3>();
        for (int i = 0; i < source.Length; i++)
            for (int j = 0; j < source[i].VertexIndices.Count; j++)
            {
                int id = source[i].VertexIndices[j]; var point = source[i].Positions[j];
                if (rest.TryGetValue(id, out var previous) && previous != point)
                    throw new ArgumentException($"Shared vertex {id} has inconsistent rest positions in '{source[i].Name}'.");
                rest[id] = point;
                if (owner.TryGetValue(id, out int other)) parent[Root(i)] = Root(other);
                else owner.Add(id, i);
            }
        var result = new List<Component>();
        foreach (var group in Enumerable.Range(0, source.Length).GroupBy(Root))
        {
            var parts = group.Select(i => source[i]).ToArray();
            if (parts.Length > 1)
            {
                var first = parts[0]; var a = first.Settings;
                foreach (var part in parts)
                {
                    var b = part.Settings;
                    void Require(bool equal, string field)
                    {
                        if (!equal) throw new NotSupportedException($"Common-node parts '{first.Name}' and '{part.Name}' conflict in body-wide {field}.");
                    }
                    Require(a.Margin == b.Margin, nameof(a.Margin));
                    Require(a.Group == b.Group, nameof(a.Group));
                    Require(a.CollisionMask == b.CollisionMask, nameof(a.CollisionMask));
                    Require(a.AeroModel == b.AeroModel, nameof(a.AeroModel));
                    Require(a.Config.SequenceEqual(b.Config), nameof(a.Config));
                    Require(a.ClusterConfig.SequenceEqual(b.ClusterConfig), nameof(a.ClusterConfig));
                    Require(a.Iterations.SequenceEqual(b.Iterations), nameof(a.Iterations));
                    Require((a.Flags & 4) == (b.Flags & 4), "Flags.Randomization");
                    Require((a.Flags & 2) == (b.Flags & 2), "Flags.Cluster");
                }
                foreach (var anchors in parts.SelectMany(p => p.Anchors).GroupBy(a => (a.VertexIndex, a.BodyIndex)))
                    if (anchors.Select(a => a.NearMode).Distinct().Count() > 1)
                        throw new NotSupportedException($"Common-node anchor {anchors.Key} has conflicting Near modes.");
                double total = parts.Sum(p => (double)p.Settings.Mass);
                if (total > float.MaxValue) throw new ArgumentException("Shared total mass overflows float32.");
                foreach (var mass in parts.SelectMany(p => p.VertexIndices.Select(id =>
                    (Id: id, Mass: (double)p.Settings.Mass / p.VertexIndices.Count))).GroupBy(v => v.Id))
                {
                    float sum = (float)mass.Sum(v => v.Mass);
                    if (!float.IsFinite(sum) || sum <= 0 || !float.IsFinite(1 / sum))
                        throw new ArgumentException($"Shared vertex {mass.Key} mass is outside finite float32 solver range.");
                }
            }
            // A singleton keeps its authored local order, preserving the existing native path exactly.
            var ids = parts.Length == 1 ? parts[0].VertexIndices.ToArray()
                : parts.SelectMany(p => p.VertexIndices).Distinct().Order().ToArray();
            result.Add(new(parts, ids, ids.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index)));
        }
        return result.ToArray();
    }

    internal static int[] Options(SoftBodyDefinition d)
    {
        var s = d.Settings;
        return [(int)d.Shape, s.Flags, s.BendingDistance, s.ClusterCount, s.AeroModel, s.Group, s.CollisionMask,
            s.Iterations[0], s.Iterations[1], s.Iterations[2], s.Iterations[3]];
    }
    internal static float[] Values(SoftBodyDefinition d)
        => [d.Settings.Mass, d.Settings.Margin, .. d.Settings.Config, .. d.Settings.ClusterConfig, .. d.Settings.Stiffness];
    public void Dispose() { if (disposed) return; disposed = true; world?.Dispose(); world = null; }
    private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Handle(IntPtr pointer) : base(true) => SetHandle(pointer);
        protected override bool ReleaseHandle() { Native.Destroy(handle); return true; }
    }

    private static class Native
    {
        private const string Library = "mmd_bullet_soft";
        [DllImport(Library, EntryPoint = "mmd_soft_abi", CallingConvention = CallingConvention.Cdecl)] public static extern int Abi();
        [DllImport(Library, EntryPoint = "mmd_soft_features", CallingConvention = CallingConvention.Cdecl)] public static extern int Features();
        [DllImport(Library, EntryPoint = "mmd_soft_error", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr Error();
        [DllImport(Library, EntryPoint = "mmd_soft_create", CallingConvention = CallingConvention.Cdecl)] public static extern int Create(float[] gravity, int ground, float y, out IntPtr result);
        [DllImport(Library, EntryPoint = "mmd_soft_destroy", CallingConvention = CallingConvention.Cdecl)] public static extern void Destroy(IntPtr pointer);
        [DllImport(Library, EntryPoint = "mmd_soft_add_collider", CallingConvention = CallingConvention.Cdecl)] public static extern int AddCollider(Handle w, int shape, int group, int mask, float[] size, float[] pose);
        [DllImport(Library, EntryPoint = "mmd_soft_add", CallingConvention = CallingConvention.Cdecl)] public static extern int Add(Handle w, float[] points, int count, int[] triangles, int numIndices, int[] options, float[] values, int[] pins, int numPins, int[] anchors, int numAnchors);
        [DllImport(Library, EntryPoint = "mmd_soft_add_shared", CallingConvention = CallingConvention.Cdecl)] public static extern int AddShared(Handle w, float[] points, int count, int partCount, int[] nodeOffsets, int[] nodes, int nodeCount, int[] triangleOffsets, int[] triangles, int triangleCount, int[] options, float[] values, int[] pins, int pinCount, int[] anchors, int anchorCount);
        [DllImport(Library, EntryPoint = "mmd_soft_near_anchors", CallingConvention = CallingConvention.Cdecl)] public static extern int NearAnchors(Handle w, int index, int[] nodeRigidPairs, int count);
        [DllImport(Library, EntryPoint = "mmd_soft_targets", CallingConvention = CallingConvention.Cdecl)] public static extern int Targets(Handle w, int index, float[] points, int count);
        [DllImport(Library, EntryPoint = "mmd_soft_collider_pose", CallingConvention = CallingConvention.Cdecl)] public static extern int ColliderPose(Handle w, int index, float[] pose);
        [DllImport(Library, EntryPoint = "mmd_soft_collider_poses", CallingConvention = CallingConvention.Cdecl)] public static extern int ColliderPoses(Handle w, float[] poses, int count);
        [DllImport(Library, EntryPoint = "mmd_soft_step", CallingConvention = CallingConvention.Cdecl)] public static extern int Step(Handle w, float seconds);
        [DllImport(Library, EntryPoint = "mmd_soft_sample", CallingConvention = CallingConvention.Cdecl)] public static extern int Sample(Handle w, int index, [Out] float[] points, [Out] float[] normals, int count);
        [DllImport(Library, EntryPoint = "mmd_soft_gravity", CallingConvention = CallingConvention.Cdecl)] public static extern int Gravity(Handle w, float[] gravity);
    }
}
