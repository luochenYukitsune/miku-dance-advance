// Physics phase flow references saba (https://github.com/benikabocha/saba)
// @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, PMXModel.cpp / MMDPhysics.cpp.
// MIT License, Copyright (c) 2016-2017 benikabocha; third_party/saba/LICENCE.txt.
using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

/// <summary>
/// Owns the injected physics backend and connects animation's before/after-physics phases.
/// ResetAt is an absolute seek with cleared solver state; Advance continues a forward trajectory.
/// A physical seek requires an explicit reset followed by forward advances.
/// </summary>
public sealed class CharacterPhysicsPlayback : IDisposable
{
    private readonly CharacterAnimator animator;
    private readonly CharacterPhysicsDefinition definition;
    private readonly CharacterPhysicsSession session;
    private readonly int[] reflectedBoneIndices;
    private bool disposed;
    private PmxModel? coupledModel;
    internal PmxCoupledSoftBodyPlayback? CoupledSoftBodies { get; private set; }
    internal void EnableCoupledSoftBodies(PmxModel model) => coupledModel = model;
    internal void DisableCoupledSoftBodies() { CoupledSoftBodies?.Dispose(); CoupledSoftBodies = null; coupledModel = null; }

    /// <summary>Last successfully completed frame; null until the first ResetAt.</summary>
    public double? CurrentFrame { get; private set; }
    /// <summary>A solver/completion failure requires a successful ResetAt before advancing again.</summary>
    public bool IsFaulted { get; private set; }
    public int LastStepCount => session.LastStepCount;
    public double DiscardedSeconds => session.DiscardedSeconds;
    public string BackendName => session.BackendName;
    public IReadOnlyList<string> Diagnostics => session.Diagnostics;

    internal Matrix4x4[] GetBodyWorldTransforms()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Enumerable.Range(0, definition.Bodies.Count).Select(session.GetBodyWorldTransform).ToArray();
    }

    /// <summary>Updates acceleration without resetting the timeline, velocity or contact solver.</summary>
    public void SetGravity(Vector3 gravity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        session.SetGravity(gravity);
    }

    public CharacterPhysicsPlayback(CharacterAnimator animator, CharacterPhysicsDefinition definition, ICharacterPhysicsBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        // Ownership transfers at construction, including failures before the session takes ownership.
        try
        {
            this.animator = animator ?? throw new ArgumentNullException(nameof(animator));
            this.definition = definition ?? throw new ArgumentNullException(nameof(definition));
            // PMX mode decides reflection, including zero-mass dynamic bodies. The session chooses
            // the last body for duplicate bone bindings; completion imports each driven bone once.
            reflectedBoneIndices = definition.Bodies.Where(body => body.BoneIndex >= 0
                && body.Mode is PhysicsBodyMode.Dynamic or PhysicsBodyMode.DynamicAligned)
                .Select(body => body.BoneIndex).Distinct().ToArray();
        }
        catch
        {
            backend.Dispose();
            throw;
        }
        // CharacterPhysicsSession disposes the backend itself if its initial Reset fails.
        session = new(definition, backend);
    }

    /// <summary>Samples an absolute frame and clears velocity, contacts and fractional simulation time.</summary>
    public CharacterSample ResetAt(double frame, CharacterEvaluationOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateFrame(frame);
        using var evaluation = animator.BeginPhysicsSample(frame, WithPhysicsState(options));
        ValidatePose(evaluation.BeforePhysicsBoneWorldMatrices);
        // Evaluation/argument validation above cannot destroy an existing valid solver state.
        try
        {
            session.Reset(evaluation.BeforePhysicsBoneWorldMatrices);
            if (coupledModel is not null)
            {
                var before = animator.SkinWorldPose(evaluation.BeforePhysicsBoneWorldMatrices, WithPhysicsState(options).Morphs);
                var candidate = new PmxCoupledSoftBodyPlayback(coupledModel, session.CreateSoftBodyBinding(), before.Vertices, Matrix4x4.Identity);
                CoupledSoftBodies?.Dispose(); CoupledSoftBodies = candidate;
            }
            var worlds = session.Advance(0, evaluation.BeforePhysicsBoneWorldMatrices);
            ValidatePose(worlds);
            var sample = evaluation.Complete(worlds, reflectedBoneIndices);
            CurrentFrame = frame;
            IsFaulted = false;
            return sample;
        }
        catch
        {
            IsFaulted = true;
            throw;
        }
    }

    /// <summary>
    /// Advances the existing trajectory using the session's fixed 120Hz steps, then skins once.
    /// Pause by retaining the last sample without calling this method, or pass zero elapsed time.
    /// A backwards frame is a seek and must use ResetAt.
    /// </summary>
    public CharacterSample Advance(double frame, double elapsedSeconds, CharacterEvaluationOptions? options = null,
        ReadOnlySpan<CharacterBodyImpulse> impulses = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateFrame(frame);
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds > 86400)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (IsFaulted) throw new InvalidOperationException("Physics playback failed; call ResetAt before advancing again.");
        if (CurrentFrame is not { } previousFrame) throw new InvalidOperationException("Call ResetAt before the first physics advance.");
        if (frame < previousFrame) throw new ArgumentOutOfRangeException(nameof(frame), "A backwards seek must use ResetAt.");
        session.ValidateImpulses(impulses);
        using var evaluation = animator.BeginPhysicsSample(frame, WithPhysicsState(options));
        ValidatePose(evaluation.BeforePhysicsBoneWorldMatrices);
        try
        {
            Action? publishSoftTargets = null;
            if (CoupledSoftBodies is { } soft)
            {
                var before = animator.SkinWorldPose(evaluation.BeforePhysicsBoneWorldMatrices, WithPhysicsState(options).Morphs);
                var targets = soft.PrepareTargets(before.Vertices);
                publishSoftTargets = () => soft.PublishTargets(targets, frozen: false);
            }
            var worlds = session.Advance(elapsedSeconds, evaluation.BeforePhysicsBoneWorldMatrices, impulses, publishSoftTargets);
            ValidatePose(worlds);
            var sample = evaluation.Complete(worlds, reflectedBoneIndices);
            CurrentFrame = frame;
            return sample;
        }
        catch
        {
            IsFaulted = true;
            throw;
        }
    }

    /// <summary>Settles an independent rigid solver at its current fixed pose, completing and skinning only once.</summary>
    internal CharacterSample StabilizeAt(double frame, int substeps, CharacterEvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateFrame(frame);
        if (substeps is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(substeps));
        if (IsFaulted) throw new InvalidOperationException("Physics playback failed; call ResetAt before advancing again.");
        if (CurrentFrame != frame) throw new InvalidOperationException("Pose initialization requires the current physics frame.");
        if (coupledModel is not null || CoupledSoftBodies is not null)
            throw new InvalidOperationException("Coupled soft bodies require per-step target preparation.");
        cancellationToken.ThrowIfCancellationRequested();
        using var evaluation = animator.BeginPhysicsSample(frame, WithPhysicsState(options));
        ValidatePose(evaluation.BeforePhysicsBoneWorldMatrices);
        try
        {
            Matrix4x4[] worlds = [];
            for (int step = 0; step < substeps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Keep each normal Advance/Reflect boundary: joint pull correction can change the
                // backend after stepping. A single large delta would also hit MaximumSubsteps.
                worlds = session.Advance(CharacterPhysicsSession.FixedStepSeconds, evaluation.BeforePhysicsBoneWorldMatrices);
                ValidatePose(worlds);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return evaluation.Complete(worlds, reflectedBoneIndices);
        }
        catch
        {
            IsFaulted = true;
            throw;
        }
    }

    private CharacterEvaluationOptions WithPhysicsState(CharacterEvaluationOptions? options)
        => (options ?? new()) with { PhysicsDrivenBoneIndices = reflectedBoneIndices };

    private static void ValidateFrame(double frame)
    {
        if (!double.IsFinite(frame)) throw new ArgumentOutOfRangeException(nameof(frame));
    }

    private void ValidatePose(ReadOnlySpan<Matrix4x4> worlds)
    {
        if (worlds.Length != definition.Bones.Count)
            throw new ArgumentException("Animation pose bone count differs from the physics definition.", nameof(worlds));
        foreach (var world in worlds) PhysicsMath.ValidateRigid(world, nameof(worlds));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { DisableCoupledSoftBodies(); } finally { session.Dispose(); }
    }
}
