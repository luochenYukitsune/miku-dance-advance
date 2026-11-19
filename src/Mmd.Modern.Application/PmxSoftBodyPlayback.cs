using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

/// <summary>Owns the soft solver; callers feed post-skinning model-space targets and actual rigid worlds.
/// Fixed 120Hz advancement holds input targets within this call. Seek resets to the supplied pose.
/// No rigid reverse impulse feedback or original MMD soft-body equivalence is claimed.</summary>
public sealed class PmxSoftBodyPlayback : IDisposable
{
    public const double FixedStepSeconds = 1d / 120;
    private readonly ISoftBodyBackend backend;
    private readonly int vertexCount;
    private readonly Matrix4x4[] restRigids;
    private double remainder;
    private bool disposed, initialized;
    /// <summary>True after a solver step or sample failed. A failure can leave the native world half-stepped
    /// (predicted but unsolved), so further steps are refused until <see cref="Reset(ReadOnlySpan{SceneVertex}, ReadOnlySpan{Matrix4x4})"/>
    /// rebuilds the state from an animated pose.</summary>
    public bool IsFaulted { get; private set; }
    public IReadOnlyList<string> Diagnostics => backend.Diagnostics;
    public bool SupportsRigidProxyUpdates => backend is ISoftBodyRigidProxyUpdater;
    public PmxSoftBodyPlayback(PmxModel model, ISoftBodyBackend backend)
    {
        ArgumentNullException.ThrowIfNull(model); this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        vertexCount = model.Vertices.Count;
        try
        {
            var adaptation = PmxSoftBodyAdapter.Convert(model);
            restRigids = adaptation.RigidRestWorlds.ToArray();
            backend.Initialize(adaptation.Definitions, adaptation.Colliders);
        }
        catch { backend.Dispose(); throw; }
    }
    public void Reset(ReadOnlySpan<SceneVertex> skinned, ReadOnlySpan<Matrix4x4> rigidWorlds)
    {
        RequireAlive(); var positions = Targets(skinned);
        try { backend.Reset(positions, rigidWorlds); remainder = 0; initialized = true; IsFaulted = false; }
        catch { IsFaulted = true; throw; }
    }
    public void Reset(ReadOnlySpan<SceneVertex> skinned) => Reset(skinned, restRigids);
    public SceneVertex[] Advance(double seconds, ReadOnlySpan<SceneVertex> skinned, ReadOnlySpan<Matrix4x4> rigidWorlds)
    {
        RequireAlive();
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 10) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (IsFaulted) throw new InvalidOperationException("Soft-body playback failed; reset it before advancing again.");
        var positions = Targets(skinned);
        if (!initialized) { Reset(skinned, rigidWorlds); }
        double remaining = remainder + seconds;
        int steps = (int)Math.Floor(remaining / FixedStepSeconds + 1e-9);
        try
        {
            for (int i = 0; i < steps; i++) backend.Step((float)FixedStepSeconds, positions, rigidWorlds);
            remainder = Math.Max(0, remaining - steps * FixedStepSeconds);
            return Apply(skinned);
        }
        catch (Exception error) when (error is not ArgumentException)
        {
            IsFaulted = true;
            throw;
        }
    }
    public SceneVertex[] Advance(double seconds, ReadOnlySpan<SceneVertex> skinned) => Advance(seconds, skinned, restRigids);
    public SceneVertex[] Apply(ReadOnlySpan<SceneVertex> skinned)
    {
        RequireAlive(); if (!initialized) throw new InvalidOperationException("Reset the soft solver before sampling.");
        // Sampling is read-only: it is also the non-mutating probe used to validate a candidate rigid replacement,
        // whose failure must leave the soft history untouched. Faults are recorded where the solver is stepped (Advance).
        _ = Targets(skinned); var output = skinned.ToArray(); var claimed = new HashSet<int>();
        foreach (var sample in backend.Sample())
        {
            if (sample.Positions.Length != sample.VertexIndices.Length || sample.Normals.Length != sample.VertexIndices.Length)
                throw new InvalidOperationException("Soft backend returned mismatched node arrays.");
            for (int i = 0; i < sample.VertexIndices.Length; i++)
            {
                int id = sample.VertexIndices[i];
                if ((uint)id >= (uint)output.Length) throw new InvalidOperationException("Soft backend returned an unknown global vertex.");
                if (!claimed.Add(id)) throw new InvalidOperationException("Soft backend returned duplicate output ownership; shared vertices require one common solver node.");
                Vector3 normal = sample.Normals[i];
                if (!Finite(sample.Positions[i]) || !Finite(normal)) throw new InvalidOperationException("Soft backend returned nonfinite nodes.");
                // Ropes have no faces. Keep the skinned normal if Bullet has no surface normal for a node.
                output[id] = output[id] with { Position = sample.Positions[i], Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : output[id].Normal };
            }
        }
        return output;
    }
    public void SetGravity(Vector3 gravity) { RequireAlive(); backend.SetGravity(gravity); }
    /// <summary>Atomically refreshes the existing collider poses, preserving nodes, velocities and remainder.</summary>
    public void UpdateRigidProxies(ReadOnlySpan<Matrix4x4> rigidWorlds)
    {
        RequireAlive();
        if (!initialized) throw new InvalidOperationException("Reset the soft solver before updating rigid proxies.");
        if (rigidWorlds.Length != restRigids.Length) throw new ArgumentException("Rigid proxy pose count mismatch.", nameof(rigidWorlds));
        foreach (var world in rigidWorlds) PhysicsMath.ValidateRigid(world, nameof(rigidWorlds));
        if (backend is not ISoftBodyRigidProxyUpdater updater)
            throw new NotSupportedException("The soft backend cannot atomically update rigid proxies while preserving soft history.");
        updater.UpdateRigidProxies(rigidWorlds);
    }
    private Vector3[] Targets(ReadOnlySpan<SceneVertex> skinned)
    {
        if (skinned.Length != vertexCount) throw new ArgumentException("Skinned vertex count differs from PMX.", nameof(skinned));
        var result = new Vector3[skinned.Length];
        for (int i = 0; i < result.Length; i++)
        { if (!Finite(skinned[i].Position)) throw new ArgumentException("Nonfinite skinned vertex.", nameof(skinned)); result[i] = skinned[i].Position; }
        return result;
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private void RequireAlive() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose() { if (disposed) return; disposed = true; backend.Dispose(); }
}
