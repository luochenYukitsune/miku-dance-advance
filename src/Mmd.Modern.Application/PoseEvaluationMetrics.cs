using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Mmd.Modern.Application;

// Opt-in EventPipe diagnostics. Disabled sessions take only IsEnabled branches and allocate
// nothing. Warmup boundary samples aggregate on their owning thread and emit after all steps.
[EventSource(Name = "MDA-PoseEvaluation")]
internal sealed class PoseEvaluationEventSource : EventSource
{
    internal static readonly PoseEvaluationEventSource Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Phase(string stage, string actor, long elapsedTicks, long allocatedBytes, int count)
        => WriteEvent(1, stage, actor, elapsedTicks, allocatedBytes, count);
}

internal static class PoseEvaluationMetrics
{
    [ThreadStatic] private static Aggregate? current;

    internal static Scope Measure(string stage, Guid actor = default)
        => PoseEvaluationEventSource.Log.IsEnabled() ? new(stage, actor, current) : default;

    internal static AggregateScope AggregateBoundaries()
    {
        if (!PoseEvaluationEventSource.Log.IsEnabled()) return default;
        var previous = current;
        current = new();
        return new(current, previous);
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly string? stage;
        private readonly Guid actor;
        private readonly Aggregate? aggregate;
        private readonly long startTicks, startBytes;

        internal Scope(string stage, Guid actor, Aggregate? aggregate)
        {
            this.stage = stage; this.actor = actor; this.aggregate = aggregate;
            startTicks = Stopwatch.GetTimestamp(); startBytes = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            if (stage is null) return;
            long ticks = Stopwatch.GetTimestamp() - startTicks;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
            if (aggregate is { } target) target.Add(stage, actor, ticks, bytes);
            else PoseEvaluationEventSource.Log.Phase(stage, actor == Guid.Empty ? "" : actor.ToString("D"), ticks, bytes, 1);
        }
    }

    internal sealed class Aggregate
    {
        private readonly Dictionary<(string Stage, Guid Actor), (long Ticks, long Bytes, int Count)> values = [];

        internal void Add(string stage, Guid actor, long ticks, long bytes)
        {
            var key = (stage, actor);
            values.TryGetValue(key, out var previous);
            values[key] = (previous.Ticks + ticks, previous.Bytes + bytes, previous.Count + 1);
        }

        internal void Emit()
        {
            foreach (var (key, value) in values)
                PoseEvaluationEventSource.Log.Phase(key.Stage, key.Actor == Guid.Empty ? "" : key.Actor.ToString("D"),
                    value.Ticks, value.Bytes, value.Count);
        }
    }

    internal readonly struct AggregateScope(Aggregate? aggregate, Aggregate? previous) : IDisposable
    {
        public void Dispose()
        {
            if (aggregate is null) return;
            current = previous;
            aggregate.Emit();
        }
    }
}
