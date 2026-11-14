using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

/// <summary>Attached PMX soft content. The rigid owner alone advances time; this adapter only supplies
/// pre-physics pin targets and overlays completed world-space samples after bone reflection.</summary>
internal sealed class PmxCoupledSoftBodyPlayback : IDisposable
{
    private readonly ICharacterSoftBodyBinding binding;
    private readonly int vertexCount;
    private readonly Matrix4x4 placement, inversePlacement;
    private bool disposed;
    private SceneVertex[]? frozenSurface;
    public IReadOnlyList<string> Diagnostics => binding.Diagnostics;
    public bool SupportsFrozenCollisionSurface => binding.SupportsFrozenCollisionSurface;

    public PmxCoupledSoftBodyPlayback(PmxModel model, ICharacterSoftBodyBinding binding,
        ReadOnlySpan<SceneVertex> beforePhysicsVertices, Matrix4x4 placement)
    {
        this.binding = binding; vertexCount = model.Vertices.Count; this.placement = placement;
        try
        {
            PhysicsMath.ValidateRigid(placement, nameof(placement));
            Matrix4x4.Invert(placement, out inversePlacement);
            // Keep authored soft-record/anchor order unchanged; grouping is the numerical provider's concern.
            binding.Initialize(PmxSoftBodyAdapter.Convert(model).Definitions, PrepareTargets(beforePhysicsVertices));
        }
        catch { binding.Dispose(); throw; }
    }

    public Vector3[] PrepareTargets(ReadOnlySpan<SceneVertex> vertices)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (vertices.Length != vertexCount) throw new ArgumentException("Coupled target vertex count differs from PMX.", nameof(vertices));
        var targets = new Vector3[vertexCount];
        for (int i = 0; i < targets.Length; i++)
        {
            targets[i] = Vector3.Transform(vertices[i].Position, placement);
            if (!Finite(targets[i])) throw new ArgumentException("Coupled pin target is not finite.", nameof(vertices));
        }
        return targets;
    }

    public void PublishTargets(Vector3[] targets, bool frozen, bool updateTargets = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // Frozen nodes retain their last physical positions, previous positions and saved velocities.
        binding.SetFrozen(frozen);
        if (!frozen)
        {
            frozenSurface = null;
            if (updateTargets) binding.SetTargets(targets);
        }
    }

    public (SceneVertex[] Vertices, Vector3[] Targets) PrepareFrozenSurface(ReadOnlySpan<SceneVertex> vertices)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!SupportsFrozenCollisionSurface) throw new NotSupportedException("The coupled provider cannot update frozen collision geometry.");
        var targets = PrepareTargets(vertices);
        binding.ValidateFrozenCollisionSurface(targets);
        return (vertices.ToArray(), targets);
    }

    public void PublishFrozenSurface((SceneVertex[] Vertices, Vector3[] Targets) surface)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // All skin maps, coordinates and the complete display scene have already been validated.
        binding.SetFrozen(true);
        binding.SetFrozenCollisionSurface(surface.Targets);
        frozenSurface = surface.Vertices;
    }

    public void Rebase(Matrix4x4 localDelta)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var delta = inversePlacement * localDelta * placement;
        delta.M14 = delta.M24 = delta.M34 = 0; delta.M44 = 1;
        PhysicsMath.ValidateRigid(delta, nameof(localDelta)); binding.Rebase(delta);
    }

    public SceneVertex[] Apply(ReadOnlySpan<SceneVertex> vertices)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (vertices.Length != vertexCount) throw new ArgumentException("Coupled output vertex count differs from PMX.", nameof(vertices));
        var output = vertices.ToArray(); var claimed = new HashSet<int>();
        foreach (var sample in binding.Sample())
        {
            if (sample.VertexIndices.Length != sample.Positions.Length || sample.VertexIndices.Length != sample.Normals.Length)
                throw new InvalidOperationException("Coupled provider returned mismatched node arrays.");
            for (int i = 0; i < sample.VertexIndices.Length; i++)
            {
                int id = sample.VertexIndices[i];
                if ((uint)id >= (uint)output.Length || !claimed.Add(id))
                    throw new InvalidOperationException("Coupled provider returned invalid or duplicate global node ownership.");
                var position = Vector3.Transform(sample.Positions[i], inversePlacement);
                var normal = Vector3.TransformNormal(sample.Normals[i], inversePlacement);
                if (!Finite(position) || !Finite(normal))
                    throw new InvalidOperationException("Coupled provider returned nonfinite nodes.");
                float scale = MathF.Max(MathF.Abs(normal.X), MathF.Max(MathF.Abs(normal.Y), MathF.Abs(normal.Z)));
                output[id] = output[id] with { Position = position,
                    Normal = scale > 1e-12f ? Vector3.Normalize(normal / scale) : output[id].Normal };
                // Display normals follow the exact skin map, while Bullet independently keeps
                // geometric collision normals on the same projected node positions.
                if (frozenSurface is not null) output[id] = frozenSurface[id];
            }
        }
        return output;
    }
    public void Dispose() { if (disposed) return; disposed = true; binding.Dispose(); }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
