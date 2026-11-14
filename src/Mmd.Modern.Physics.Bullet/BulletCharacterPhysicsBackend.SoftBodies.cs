using System.Numerics;
using System.Runtime.InteropServices;

namespace Mmd.Modern.Physics.Bullet;

public sealed partial class BulletCharacterPhysicsBackend
{
    private readonly bool coupledSoftBodies;
    private readonly HashSet<SoftBinding> softBindings = [];
    public bool SupportsCoupledSoftBodies => coupledSoftBodies;

    public ICharacterSoftBodyBinding CreateSoftBodyBinding(IReadOnlyList<int> rigidBodySlots)
    {
        _ = GetWorld(); ArgumentNullException.ThrowIfNull(rigidBodySlots);
        if (!coupledSoftBodies) throw new NotSupportedException("Select coupledSoftBodies when creating the Bullet owner to attach soft bodies.");
        var slots = rigidBodySlots.ToArray();
        if (slots.Distinct().Count() != slots.Length || slots.Any(slot => !mutableBodies.ContainsKey(slot)))
            throw new ArgumentException("Soft binding slots must be distinct live rigid bodies.", nameof(rigidBodySlots));
        var binding = new SoftBinding(this, slots); softBindings.Add(binding); return binding;
    }

    private static void RequireCoupledCapability()
    {
        try
        {
            if ((Native.Features() & 1) == 0 || (AttachedNative.Features() & 15) != 15)
                throw new NotSupportedException("This Bullet native library lacks the coupled soft-rigid world capabilities; rebuild the bridge.");
        }
        catch (EntryPointNotFoundException error)
        {
            throw new NotSupportedException("This older Bullet native library has no coupled soft-rigid world exports; rebuild the bridge.", error);
        }
    }

    private void InvalidateSoftBindings()
    {
        foreach (var binding in softBindings.ToArray()) binding.Dispose();
    }

    private sealed class SoftBinding(BulletCharacterPhysicsBackend owner, int[] slots) : ICharacterSoftBodyBinding
    {
        public bool ReferencesSlot(int slot) => slots.Contains(slot);
        private IntPtr handle;
        private bool disposed, frozen;
        private SoftBodyBulletBackend.Component[] components = [];
        public IReadOnlyList<string> Diagnostics { get; private set; } = Array.Empty<string>();

        private WorldHandle OwnerWorld()
        {
            ObjectDisposedException.ThrowIf(disposed, this); return owner.GetWorld();
        }
        private IntPtr Content()
        {
            _ = OwnerWorld(); return handle != IntPtr.Zero ? handle : throw new InvalidOperationException("Initialize the attached soft content before sampling or updating it.");
        }

        public void Initialize(IReadOnlyList<SoftBodyDefinition> definitions, ReadOnlySpan<Vector3> worldSkinnedPositions)
        {
            var world = OwnerWorld(); ArgumentNullException.ThrowIfNull(definitions);
            var source = definitions.ToArray();
            if (source.Any(d => d is null)) throw new ArgumentException("Null soft definition.", nameof(definitions));
            foreach (var d in source)
                if (d.Anchors.Any(a => a.BodyIndex >= slots.Length)) throw new ArgumentException("Anchor rigid index exceeds actor-local slot mapping.", nameof(definitions));
            var candidateComponents = SoftBodyBulletBackend.BuildComponents(source);
            var points = Points(candidateComponents, worldSkinnedPositions);
            SoftBodyBulletBackend.RequireSharedClusterCapability(candidateComponents, AttachedNative.Features);
            var messages = source.SelectMany((d, soft) => d.Anchors.Select((a, anchor) => (d, soft, a, anchor)))
                .Where(p => p.a.NearMode && owner.mutableBodies[slots[p.a.BodyIndex]].Mode != PhysicsBodyMode.Kinematic)
                .GroupBy(p => p.a.BodyIndex).Where(g => g.Count() > 1)
                .Select(g => $"Dynamic Near rigid {g.Key} has multiple pull locations: {string.Join(", ", g.Select(p => $"soft {p.soft} '{p.d.Name}' anchor {p.anchor} vertex {p.a.VertexIndex}"))}. Model soft/anchor order applies; the last authored pair determines the origin.").ToArray();
            Check(Native.SoftCreate(world, slots, slots.Length, out var candidate), "create attached soft candidate");
            try
            {
                for (int i = 0; i < candidateComponents.Length; i++)
                {
                    var c = candidateComponents[i]; var d = c.Parts[0];
                    var pins = c.Parts.SelectMany(p => p.PinVertexIndices).Distinct().Select(id => c.Map[id]).ToArray();
                    var anchors = c.Parts.SelectMany(p => p.Anchors).Distinct().SelectMany(a => new[] { c.Map[a.VertexIndex], a.BodyIndex }).ToArray();
                    if (c.Parts.Length == 1)
                        SoftCheck(AttachedNative.Add(candidate, points[i], c.Ids.Length, d.TriangleIndices.ToArray(), d.TriangleIndices.Count,
                            SoftBodyBulletBackend.Options(d), SoftBodyBulletBackend.Values(d), pins, pins.Length, anchors, anchors.Length / 2));
                    else
                    {
                        var nodeOffsets = new List<int> { 0 }; var triangleOffsets = new List<int> { 0 };
                        var nodes = new List<int>(); var triangles = new List<int>();
                        foreach (var part in c.Parts)
                        {
                            nodes.AddRange(part.VertexIndices.Select(id => c.Map[id])); nodeOffsets.Add(nodes.Count);
                            triangles.AddRange(part.TriangleIndices); triangleOffsets.Add(triangles.Count);
                        }
                        SoftCheck(AttachedNative.AddShared(candidate, points[i], c.Ids.Length, c.Parts.Length,
                            nodeOffsets.ToArray(), nodes.ToArray(), nodes.Count, triangleOffsets.ToArray(), triangles.ToArray(), triangles.Count,
                            c.Parts.SelectMany(SoftBodyBulletBackend.Options).ToArray(), c.Parts.SelectMany(SoftBodyBulletBackend.Values).ToArray(),
                            pins, pins.Length, anchors, anchors.Length / 2));
                    }
                }
                // Flatten in original source-record order, before grouping changes solver-body order.
                var commands = new List<int>(); int ordinal = 0;
                foreach (var d in source)
                {
                    int component = Array.FindIndex(candidateComponents, c => c.Parts.Contains(d));
                    foreach (var a in d.Anchors)
                    {
                        if (a.NearMode) commands.AddRange([component, candidateComponents[component].Map[a.VertexIndex], a.BodyIndex, ordinal]);
                        ordinal++;
                    }
                }
                SoftCheck(AttachedNative.Near(candidate, commands.ToArray(), commands.Count / 4));
                if (frozen) SoftCheck(AttachedNative.Frozen(candidate, 1));
                var previous = handle; handle = candidate; candidate = IntPtr.Zero;
                components = candidateComponents; Diagnostics = Array.AsReadOnly(messages);
                if (previous != IntPtr.Zero) Check(Native.SoftDestroy(world, previous), "replace attached soft content");
            }
            finally { if (candidate != IntPtr.Zero) Check(Native.SoftDestroy(world, candidate), "discard attached soft candidate"); }
        }

        public void SetTargets(ReadOnlySpan<Vector3> worldSkinnedPositions)
        {
            var content = Content(); var points = Points(components, worldSkinnedPositions);
            for (int i = 0; i < points.Length; i++) SoftCheck(AttachedNative.Targets(content, i, points[i], components[i].Ids.Length));
        }
        public IReadOnlyList<SoftBodySample> Sample()
        {
            var content = Content(); var result = new SoftBodySample[components.Length];
            for (int i = 0; i < result.Length; i++)
            {
                var ids = components[i].Ids; var points = new float[ids.Length * 3]; var normals = new float[points.Length];
                SoftCheck(AttachedNative.Sample(content, i, points, normals, ids.Length));
                result[i] = new(ids.ToArray(), Vectors(points), Vectors(normals));
            }
            return Array.AsReadOnly(result);
        }
        public void SetFrozen(bool value) { var content = Content(); SoftCheck(AttachedNative.Frozen(content, value ? 1 : 0)); frozen = value; }
        public bool SupportsFrozenCollisionSurface => (AttachedNative.Features() & 32) != 0;
        public void ValidateFrozenCollisionSurface(ReadOnlySpan<Vector3> worldPositions)
        {
            var content = Content();
            if (!SupportsFrozenCollisionSurface) throw new NotSupportedException("This Bullet library cannot preflight frozen collision geometry; rebuild the bridge.");
            var flattened = Points(components, worldPositions).SelectMany(values => values).ToArray();
            SoftCheck(AttachedNative.ValidateFrozenSurface(content, flattened, flattened.Length / 3));
        }
        public void SetFrozenCollisionSurface(ReadOnlySpan<Vector3> worldPositions)
        {
            var content = Content();
            if (!SupportsFrozenCollisionSurface) throw new NotSupportedException("This Bullet library cannot update frozen collision geometry; rebuild the bridge.");
            if (!frozen) throw new InvalidOperationException("Freeze the attached content before updating collision geometry.");
            // Validate every component before the single native batch can mutate a node.
            var points = Points(components, worldPositions);
            var flattened = points.SelectMany(values => values).ToArray();
            SoftCheck(AttachedNative.FrozenSurface(content, flattened, flattened.Length / 3));
        }
        public void Rebase(Matrix4x4 worldDelta)
        {
            var content = Content(); var p = Pose(worldDelta, nameof(worldDelta));
            SoftCheck(AttachedNative.Rebase(content, [p.Position.X, p.Position.Y, p.Position.Z, p.Qx, p.Qy, p.Qz, p.Qw]));
        }
        private static float[][] Points(SoftBodyBulletBackend.Component[] groups, ReadOnlySpan<Vector3> values)
        {
            var result = new float[groups.Length][];
            for (int i = 0; i < groups.Length; i++)
            {
                var ids = groups[i].Ids; var points = new float[ids.Length * 3];
                for (int j = 0; j < ids.Length; j++)
                {
                    int id = ids[j]; if (id >= values.Length || !Finite(values[id])) throw new ArgumentException("Invalid skinned soft target.", nameof(values));
                    var p = values[id]; points[j * 3] = p.X; points[j * 3 + 1] = p.Y; points[j * 3 + 2] = p.Z;
                }
                result[i] = points;
            }
            return result;
        }
        private static Vector3[] Vectors(float[] values) => Enumerable.Range(0, values.Length / 3)
            .Select(i => new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2])).ToArray();
        private static void SoftCheck(int status)
        {
            if (status != 0) throw new InvalidOperationException($"Attached Bullet soft failure: {Marshal.PtrToStringUTF8(AttachedNative.Error())}");
        }
        public void Dispose()
        {
            if (disposed) return;
            if (handle != IntPtr.Zero) Check(Native.SoftDestroy(owner.GetWorld(), handle), "dispose attached soft content");
            handle = IntPtr.Zero; disposed = true; owner.softBindings.Remove(this);
        }
    }

    private static partial class Native
    {
        [DllImport(Library, EntryPoint = "mmd_bullet_features", CallingConvention = CallingConvention.Cdecl)] public static extern int Features();
        [DllImport(Library, EntryPoint = "mmd_bullet_create_coupled", CallingConvention = CallingConvention.Cdecl)] public static extern int CreateCoupled(NativeVector gravity, int ground, float y, out IntPtr result);
        [DllImport(Library, EntryPoint = "mmd_bullet_soft_create", CallingConvention = CallingConvention.Cdecl)] public static extern int SoftCreate(WorldHandle world, int[] slots, int count, out IntPtr result);
        [DllImport(Library, EntryPoint = "mmd_bullet_soft_destroy", CallingConvention = CallingConvention.Cdecl)] public static extern int SoftDestroy(WorldHandle world, IntPtr content);
    }
    private static class AttachedNative
    {
        private const string Library = "mmd_bullet";
        [DllImport(Library, EntryPoint = "mmd_soft_features", CallingConvention = CallingConvention.Cdecl)] public static extern int Features();
        [DllImport(Library, EntryPoint = "mmd_soft_error", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr Error();
        [DllImport(Library, EntryPoint = "mmd_soft_add", CallingConvention = CallingConvention.Cdecl)] public static extern int Add(IntPtr w, float[] points, int count, int[] triangles, int indices, int[] options, float[] values, int[] pins, int pinCount, int[] anchors, int anchorCount);
        [DllImport(Library, EntryPoint = "mmd_soft_add_shared", CallingConvention = CallingConvention.Cdecl)] public static extern int AddShared(IntPtr w, float[] points, int count, int parts, int[] nodeOffsets, int[] nodes, int nodeCount, int[] triangleOffsets, int[] triangles, int triangleCount, int[] options, float[] values, int[] pins, int pinCount, int[] anchors, int anchorCount);
        [DllImport(Library, EntryPoint = "mmd_soft_attached_near", CallingConvention = CallingConvention.Cdecl)] public static extern int Near(IntPtr w, int[] commands, int count);
        [DllImport(Library, EntryPoint = "mmd_soft_targets", CallingConvention = CallingConvention.Cdecl)] public static extern int Targets(IntPtr w, int index, float[] points, int count);
        [DllImport(Library, EntryPoint = "mmd_soft_sample", CallingConvention = CallingConvention.Cdecl)] public static extern int Sample(IntPtr w, int index, [Out] float[] points, [Out] float[] normals, int count);
        [DllImport(Library, EntryPoint = "mmd_soft_attached_frozen", CallingConvention = CallingConvention.Cdecl)] public static extern int Frozen(IntPtr w, int value);
        [DllImport(Library, EntryPoint = "mmd_soft_attached_frozen_surface", CallingConvention = CallingConvention.Cdecl)] public static extern int FrozenSurface(IntPtr w, float[] points, int count);
        [DllImport(Library, EntryPoint = "mmd_soft_attached_validate_frozen_surface", CallingConvention = CallingConvention.Cdecl)] public static extern int ValidateFrozenSurface(IntPtr w, float[] points, int count);
        [DllImport(Library, EntryPoint = "mmd_soft_attached_rebase", CallingConvention = CallingConvention.Cdecl)] public static extern int Rebase(IntPtr w, float[] transform);
    }
}
