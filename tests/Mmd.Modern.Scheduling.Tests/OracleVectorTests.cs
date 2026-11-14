using System.Text.Json;
using Mmd.Modern.Scheduling;
using Xunit;

namespace Mmd.Modern.Scheduling.Tests;

public sealed class OracleVectorTests
{
    public static IEnumerable<object[]> Vectors()
    {
        foreach (var name in new[] { "O0", "O2" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"oracle_vectors_semantic_frame_{name}.json")));
            var index = 0;
            foreach (var vector in document.RootElement.GetProperty("vectors").EnumerateArray())
                yield return [name, index++, vector.GetProperty("label").GetString()!];
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Frozen_native_region_has_exact_order_and_float_bits(string optimization, int index, string label)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"oracle_vectors_semantic_frame_{optimization}.json")));
        var vector = document.RootElement.GetProperty("vectors")[index];
        var source = vector.GetProperty("input");
        var state = new SemanticFrameState<TraceModel>
        {
            CursorSeconds = Float(source, "cursor_bits"), PhysicsMode = source.GetProperty("mode").GetInt32(),
            Phase = source.GetProperty("phase").GetUInt32(), Playing = source.GetProperty("playing").GetByte(),
            CoreGate = source.GetProperty("core_gate").GetByte(), WorldGate = source.GetProperty("world_gate").GetByte(),
            Pending = source.GetProperty("pending").GetByte(), Reset = source.GetProperty("reset").GetByte()
        };
        foreach (var model in vector.GetProperty("models").EnumerateArray())
        {
            var slot = model.GetProperty("slot").GetInt32();
            state.SetModel(slot, new TraceModel(slot, model.GetProperty("rank").GetByte()));
        }
        var input = new SemanticFrameInput
        {
            DeltaSeconds = Float(source, "delta_bits"), TargetSeconds = Float(source, "target_bits"),
            WarmupSkipSlot = source.GetProperty("warmup_skip_slot").GetInt32()
        };
        var trace = new FrameTrace();
        var scheduler = new SemanticFrameScheduler<TraceModel>();
        // These are actual native REGION vectors. No synthetic CPU/draw/time events are added.
        var result = label == "main" ? scheduler.ReplayMainRegion(state, input, trace.Callbacks)
            : scheduler.ReplayTimeRegion(state, input, trace.Callbacks);
        var expected = vector.GetProperty("expected");
        Assert.Equal(FrameStatus.Ok, result.Status);
        Assert.Equal(expected.GetProperty("events").EnumerateArray().Select(EventKey), trace.Events);
        Assert.Equal(expected.GetProperty("remaining").GetUInt32(), Bits(result.RemainingDelta));
        Assert.Equal(expected.GetProperty("cursor").GetUInt32(), Bits(state.CursorSeconds));
        Assert.Equal(expected.GetProperty("pending").GetByte(), state.Pending);
        Assert.Equal(expected.GetProperty("reset").GetByte(), state.Reset);
        Assert.Equal((uint)trace.Events.Count(e => e.StartsWith("w|", StringComparison.Ordinal)), result.WorldSteps);
        Assert.Equal((uint)trace.Events.Count(e => e.StartsWith("s|", StringComparison.Ordinal) && e.Split('|')[2] == "1"), result.WarmupSteps);
    }

    private static string EventKey(JsonElement item) => string.Join('|', item.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText()));
    private static float Float(JsonElement source, string key) => BitConverter.UInt32BitsToSingle(source.GetProperty(key).GetUInt32());
    internal static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}

internal sealed record TraceModel(int Slot, byte Rank);

internal sealed class FrameTrace
{
    public List<string> Events { get; } = [];
    public SemanticFrameCallbacks<TraceModel> Callbacks => new()
    {
        Rank = m => m.Rank,
        Scene = (seconds, skip, _, mode) => Events.Add($"s|{OracleVectorTests.Bits(seconds)}|{skip}|{mode}"),
        Morph = m => Events.Add($"m|{m.Slot}"),
        Bone = (m, after, _, mode) => Events.Add($"b|{m.Slot}|{after}|{mode}"),
        Push = m => Events.Add($"p|{m.Slot}"), ResetModel = m => Events.Add($"r|{m.Slot}"),
        Pull = m => Events.Add($"l|{m.Slot}"), Vertex = (m, _) => Events.Add($"v|{m.Slot}"),
        Step = (delta, maxSteps, step) => { Events.Add($"w|{OracleVectorTests.Bits(delta)}|{maxSteps}|{OracleVectorTests.Bits(step)}"); return 937; },
        Draw = () => { Events.Add("draw"); return 0; }
    };
}
