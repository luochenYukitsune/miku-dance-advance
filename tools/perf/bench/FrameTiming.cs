using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace PerfBench;

[EventSource(Name = "MDA-BenchFrames")]
sealed class FrameMarkers : EventSource
{
    internal static readonly FrameMarkers Log = new();
    [Event(1)] public void Boundary(int measuredIndex) => WriteEvent(1, measuredIndex);
}

// Native step breakdown is populated when the loaded mmd_bullet DLL exports
// mmd_study_take_frame_v1; standard runtime DLLs fall back to managed timings only.
sealed class FrameTiming : EventListener
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Native
    {
        public long Steps, Simulation, Collision, Solver, PairJoin, IslandJoin, PairCaller, PairWriteback;
    }

    [DllImport("mmd_bullet", EntryPoint = "mmd_study_take_frame_v1", ExactSpelling = true)]
    private static extern int ReadNative(out Native sample, int size);
    private static readonly int NativeSize = Marshal.SizeOf<Native>();
    private static bool? nativeStudyAvailable;

    private static void Take(out Native sample)
    {
        if (nativeStudyAvailable == false) { sample = default; return; }
        try
        {
            if (ReadNative(out sample, NativeSize) != 1) throw new InvalidOperationException("Mismatched frame timing ABI.");
            nativeStudyAvailable = true;
        }
        catch (EntryPointNotFoundException)
        {
            nativeStudyAvailable = false;
            sample = default;
        }
    }

    internal readonly record struct Row(long Steps, double BulletMs, double CollisionMs, double SolverMs,
        double PairJoinMs, double IslandJoinMs, double PrepareViewMs, double GcPauseMs,
        double ActorPrepareMs, double WorldStepMs, double ActorCompleteMs, double PairCallerMs, double PairWritebackMs,
        double MorphsMs, double BonesBeforePhysicsMs);

    public readonly Row[] Rows;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private long actorPrepare, worldStep, actorComplete, morphs, bones;
    private TimeSpan pause;

    public FrameTiming(int frames)
    {
        Rows = new Row[frames];
        Take(out _); // Discard initialization and warmup.
        pause = GC.GetTotalPauseDuration();
        if (FrameMarkers.Log.IsEnabled()) FrameMarkers.Log.Boundary(-1);
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "MDA-PoseEvaluation") EnableEvents(source, EventLevel.Informational);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (Environment.CurrentManagedThreadId != owner || e.EventId != 1 ||
            e.Payload is not { Count: 5 } data || data[2] is not long ticks) return;
        // The two actor.prepare scopes nest inside boundary.physical.prepare.
        switch (data[0])
        {
            case "boundary.physical.prepare": actorPrepare += ticks; break;
            case "actor.prepare.morphs": morphs += ticks; break;
            case "actor.prepare.bones": bones += ticks; break;
            case "boundary.world.step": worldStep += ticks; break;
            case "boundary.physical.complete-bones":
            case "boundary.physical.complete-full":
            case "boundary.physical.complete-deferred-bones": actorComplete += ticks; break;
        }
    }

    public void Record(int index, double viewMs)
    {
        if (FrameMarkers.Log.IsEnabled()) FrameMarkers.Log.Boundary(index);
        Take(out var s);
        var now = GC.GetTotalPauseDuration();
        const double nsPerMs = 1e6;
        double msPerTick = 1000d / Stopwatch.Frequency;
        Rows[index] = new(s.Steps, s.Simulation / nsPerMs, s.Collision / nsPerMs, s.Solver / nsPerMs,
            s.PairJoin / nsPerMs, s.IslandJoin / nsPerMs, viewMs, (now - pause).TotalMilliseconds,
            actorPrepare * msPerTick, worldStep * msPerTick, actorComplete * msPerTick,
            s.PairCaller / nsPerMs, s.PairWriteback / nsPerMs, morphs * msPerTick, bones * msPerTick);
        pause = now;
        actorPrepare = worldStep = actorComplete = morphs = bones = 0;
    }
}