using System.Numerics;

namespace Mmd.Modern.Application;

/// <summary>Original noise block's scalar arithmetic and strict half-second update gate.
/// A private MSVCRT-compatible stream makes seek replayable; it intentionally does not share the original
/// process-global rand stream with UI/blink operations. The caller supplies its actual simulation delta.
/// This is not a claim that a fixed-120Hz host reproduces every original render-clock schedule.</summary>
public sealed class PmmGravityNoise
{
    private readonly uint seed;
    private uint state;
    private float elapsed;
    private Vector3 current;
    private bool initialized;
    public Vector3 Current => initialized ? current : throw new InvalidOperationException("Reset gravity noise before sampling.");
    public float ElapsedSeconds => elapsed;
    public ulong RandomDrawCount { get; private set; }
    public PmmGravityNoise(uint seed = 1) { this.seed = seed; state = seed; }
    public void Reset(PmmGravityPose pose)
    {
        Validate(pose); state = seed; elapsed = 0; RandomDrawCount = 0;
        current = pose.WorldGravity; initialized = true;
    }
    /// <summary>One original update boundary. Zero delta preserves the current noisy acceleration.
    /// Large deltas cause at most one update and discard excess time, matching the original block.</summary>
    public Vector3 Advance(PmmGravityPose pose, double seconds)
    {
        Validate(pose);
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 10) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!initialized) throw new InvalidOperationException("Reset gravity noise before advancing.");
        if (!pose.NoiseEnabled) { current = pose.WorldGravity; return current; }
        float delta = (float)seconds;
        float nextElapsed = elapsed + delta;
        if (nextElapsed <= .5f) { elapsed = nextElapsed; return current; }
        // Evaluate against a local PRNG snapshot before committing; a numeric failure cannot consume its stream.
        uint nextState = state;
        int Next() { nextState = unchecked(nextState * 214013u + 2531011u); return (int)((nextState >> 16) & 32767); }
        float magnitude = (float)Next() * 10f;
        magnitude /= 32767f; magnitude *= pose.Noise; magnitude += pose.Acceleration;
        float Axis(float direction)
        {
            float jitter = (float)Next() * .2f; jitter /= 32767f; jitter -= .1f;
            float value = jitter + direction; return value * magnitude;
        }
        var candidate = new Vector3(Axis(pose.Direction.X), Axis(pose.Direction.Y), Axis(pose.Direction.Z));
        if (!Finite(candidate)) throw new InvalidDataException("Noisy gravity exceeds the finite physics range.");
        state = nextState; RandomDrawCount += 4; current = candidate; elapsed = 0; return current;
    }
    private static void Validate(PmmGravityPose pose)
    {
        _ = PmmPhysicsSettings.ToGravity(pose.Acceleration, pose.Direction);
        if (!Finite(pose.WorldGravity)) throw new ArgumentException("Gravity pose contains nonfinite world acceleration.", nameof(pose));
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
