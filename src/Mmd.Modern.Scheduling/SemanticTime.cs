using System.Runtime.CompilerServices;

namespace Mmd.Modern.Scheduling;

public static class SemanticTime
{
    public static float FixedStep => BitConverter.UInt32BitsToSingle(0x3C888889);

    public static uint NextPhase(uint phase, byte playing, byte idleGate, byte forceNonzero)
    {
        if (playing != 0 || idleGate == 0 || forceNonzero != 0) phase = 1;
        return phase switch { 1 => 2, 2 => 3, 3 => 0, _ => phase };
    }

    /// <summary>Unsigned modular subtraction, signed reinterpretation, then sequential float arithmetic.</summary>
    public static float TimestampTarget(ulong now, ulong baseline, float secondsPerTick, float startSeconds)
    {
        var ticks = (float)unchecked((long)(now - baseline));
        return Binary32.Add(Binary32.Multiply(ticks, secondsPerTick), startSeconds);
    }

    /// <summary>Raw arithmetic helper; the host must select a valid recording rate.</summary>
    public static float RecordingTarget(int completedFrames, uint startFrame, float fps)
        => Binary32.Divide(Binary32.Add(Binary32.Divide(Binary32.Multiply(completedFrames, 30f), fps), startFrame), 30f);

    public static float RecordingDelta(float fps) => Binary32.Divide(1f, fps);
}

// Separate float-returning operations make each binary32 rounding point explicit and
// prevent cross-operation multiply/add contraction or accidental double accumulation.
internal static class Binary32
{
    [MethodImpl(MethodImplOptions.NoInlining)] internal static float Add(float left, float right) => left + right;
    [MethodImpl(MethodImplOptions.NoInlining)] internal static float Subtract(float left, float right) => left - right;
    [MethodImpl(MethodImplOptions.NoInlining)] internal static float Multiply(float left, float right) => left * right;
    [MethodImpl(MethodImplOptions.NoInlining)] internal static float Divide(float left, float right) => left / right;
}
