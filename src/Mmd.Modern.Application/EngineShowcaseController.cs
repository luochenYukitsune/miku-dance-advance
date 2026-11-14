using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;

namespace Mmd.Modern.Application;

/// <summary>Original rigid scene driven by an injected solver. Owns no GUI, rendering handle, PMX or custom physics solver.</summary>
public sealed class EngineShowcaseController : IDisposable
{
    private readonly CharacterPhysicsSession physics;
    private bool disposed;
    public EngineShowcaseScene Scene { get; }
    public CharacterPhysicsDefinition PhysicsDefinition { get; }
    public OriginalFrameScene Frame { get; private set; }
    public ReadOnlyCollection<Matrix4x4> BodyWorldTransforms { get; private set; }
    public bool IsPlaying { get; set; }
    public bool IsAtResetPose { get; private set; } = true;
    public double ElapsedSeconds { get; private set; }
    public long TotalStepCount { get; private set; }
    public int LastStepCount => physics.LastStepCount;
    public double DiscardedSeconds => physics.DiscardedSeconds;
    public string BackendName => physics.BackendName;
    public IReadOnlyList<string> Diagnostics => physics.Diagnostics;

    public EngineShowcaseController(Func<CharacterPhysicsDefinition, ICharacterPhysicsBackend> backendFactory)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        Scene = EngineShowcaseScene.Create();
        var bodies = Scene.Parts.Select((p, i) => new PhysicsBodyDefinition(p.Name, -1, PhysicsShapeKind.Box, p.HalfExtents,
            p.Position, p.Rotation, p.IsDynamic ? 1 : 0, .04f, .04f, 0, .8f, p.HasCollision ? 0 : 1,
            p.HasCollision ? (ushort)1 : (ushort)0, p.IsDynamic ? PhysicsBodyMode.Dynamic : PhysicsBodyMode.Kinematic)).ToArray();
        var spring = new PhysicsJointDefinition("Suspended prism spring", Scene.PendulumAnchorIndex, Scene.PendulumBodyIndex, 0,
            Scene.PendulumPivot, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.One, -Vector3.One,
            Vector3.Zero, new(.5f, .5f, .8f));
        PhysicsDefinition = new([], bodies, [spring]);
        physics = new(PhysicsDefinition, backendFactory(PhysicsDefinition) ?? throw new ArgumentException("Physics backend factory returned null.", nameof(backendFactory)));
        Frame = Scene.Frame;
        BodyWorldTransforms = Array.AsReadOnly(PhysicsDefinition.BodyRestWorldTransforms.ToArray());
    }

    public OriginalFrameScene Advance(double seconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!IsPlaying) return Frame;
        physics.Advance(seconds, []);
        if (physics.LastStepCount == 0) return Frame;
        TotalStepCount += physics.LastStepCount;
        ElapsedSeconds = TotalStepCount * CharacterPhysicsSession.FixedStepSeconds;
        BodyWorldTransforms = Array.AsReadOnly(Enumerable.Range(0, PhysicsDefinition.Bodies.Count).Select(physics.GetBodyWorldTransform).ToArray());
        Frame = Scene.WithPartTransforms(BodyWorldTransforms);
        IsAtResetPose = false;
        return Frame;
    }

    /// <summary>Recreates contacts, velocities and warm starts. The static frame is suitable for a fresh DXR showcase.</summary>
    public OriginalFrameScene Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        physics.Reset([]);
        IsPlaying = false; IsAtResetPose = true; ElapsedSeconds = 0; TotalStepCount = 0;
        BodyWorldTransforms = Array.AsReadOnly(PhysicsDefinition.BodyRestWorldTransforms.ToArray());
        return Frame = Scene.Frame;
    }

    public void Dispose() { if (disposed) return; disposed = true; IsPlaying = false; physics.Dispose(); }
}
