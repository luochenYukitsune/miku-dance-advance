using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Scheduling;
using Xunit;
using static Mmd.Modern.Scheduling.Tests.OracleVectorTests;

namespace Mmd.Modern.Scheduling.Tests;

public sealed class FrameBoundaryTests
{
    [Fact]
    public void Full_frame_orders_cpu_draw_time_host_pre_world_post_and_resets_three_rounds()
    {
        var state = State(mode: 3, playing: 2, pending: 1, reset: 2);
        var trace = new FrameTrace();
        var hooks = trace.Callbacks with { AfterTime = (_, _) => trace.Events.Add("after_time") };
        var result = new SemanticFrameScheduler<TraceModel>().Run(state, new() { DeltaSeconds = 0.07f }, hooks);
        string[] expected = ["v|0", "v|3", "v|8", "v|254", "draw", "s|0|0|3", "after_time",
            "m|3", "b|3|0|0", "m|0", "b|0|0|0", "p|0", "p|3", "p|8", "p|254",
            "w|1015580809|10|1015580809", "w|1015580809|10|1015580809",
            "r|0", "r|3", "r|8", "r|254", "w|1015580809|10|1015580809",
            "r|0", "r|3", "r|8", "r|254", "w|1015580809|10|1015580809",
            "r|0", "r|3", "r|8", "r|254", "w|1015580809|10|1015580809",
            "l|0", "l|3", "l|8", "l|254", "m|3", "b|3|1|3", "m|0", "b|0|1|3"];
        Assert.Equal(expected, trace.Events);
        Assert.Equal(FrameStatus.Ok, result.Status);
        Assert.Equal(5u, result.WorldSteps);
        Assert.Equal(0, state.Pending);
        Assert.Equal(0, state.Reset);
        WriteTrace("mode3-nonzero-playing-reset-lowbit-pending", state, result, trace);
    }

    [Fact]
    public void Draw_failure_returns_after_vertices_without_advancing_time_or_flags()
    {
        var state = State(mode: 3, playing: 1, pending: 1, reset: 1);
        var trace = new FrameTrace();
        var result = new SemanticFrameScheduler<TraceModel>().Run(state, new() { DeltaSeconds = 0.1f, TargetSeconds = 1f },
            trace.Callbacks with { Draw = () => { trace.Events.Add("draw_failed"); return -9; } });
        Assert.Equal(["v|0", "v|3", "v|8", "v|254", "draw_failed"], trace.Events);
        Assert.Equal(FrameStatus.DrawFailed, result.Status);
        Assert.Equal(-9, result.DrawStatus);
        Assert.Equal(0f, state.CursorSeconds);
        Assert.Equal(1, state.Pending);
        Assert.Equal(1, state.Reset);
        Assert.Equal(0u, result.WorldSteps);
    }

    [Theory]
    [InlineData(0, 2, 0, 0u, 2, 0)]
    [InlineData(0, 0, 2, 0u, 0, 2)]
    [InlineData(2, 0, 0, 0u, 0, 0)]
    [InlineData(0, 3, 2, 5u, 0, 0)]
    [InlineData(3, 2, 2, 4u, 0, 0)]
    public void Mode3_tests_low_bit_but_reset_rounds_and_playing_test_nonzero(byte playing, byte pending, byte reset, uint steps, byte expectedPending, byte expectedReset)
    {
        var state = State(3, playing, pending, reset);
        var trace = new FrameTrace();
        var result = new SemanticFrameScheduler<TraceModel>().Run(state, new() { DeltaSeconds = 0.07f }, trace.Callbacks);
        Assert.Equal(FrameStatus.Ok, result.Status);
        Assert.Equal(steps, result.WorldSteps);
        Assert.Equal(expectedPending, state.Pending);
        Assert.Equal(expectedReset, state.Reset);
        Assert.Equal(playing != 0, trace.Events.Any(e => e.StartsWith("s|", StringComparison.Ordinal)));
        Assert.Contains($"b|3|0|{(reset != 0 ? 0 : 3)}", trace.Events);
    }

    [Fact]
    public void Warmup_remains_active_when_core_and_world_gated_and_mode_is_above_three()
    {
        var state = State(mode: 4, playing: 2);
        state.CoreGate = 2; state.WorldGate = 2;
        var trace = new FrameTrace();
        var result = new SemanticFrameScheduler<TraceModel>().Run(state,
            new() { DeltaSeconds = 0.07f, TargetSeconds = BitConverter.UInt32BitsToSingle(1029338126) },
            trace.Callbacks with { AfterTime = (_, _) => trace.Events.Add("after_time") });
        Assert.Equal(FrameStatus.Ok, result.Status);
        Assert.Equal(3u, result.WarmupSteps);
        Assert.Equal(3u, result.WorldSteps);
        Assert.Equal(1017370379u, Bits(result.RemainingDelta));
        Assert.Equal("draw", trace.Events[0]);
        Assert.Equal("after_time", trace.Events[^1]);
        Assert.DoesNotContain(trace.Events, e => e.StartsWith("l|", StringComparison.Ordinal));
    }

    [Fact]
    public void Full_frame_passes_repeatedly_subtracted_residual_to_main_world_after_host_boundary()
    {
        var state = State(1, 1);
        var trace = new FrameTrace();
        var result = new SemanticFrameScheduler<TraceModel>().Run(state,
            new() { DeltaSeconds = 0.07f, TargetSeconds = BitConverter.UInt32BitsToSingle(1029338126) },
            trace.Callbacks with { AfterTime = (_, _) => trace.Events.Add("after_time") });
        Assert.Equal(FrameStatus.Ok, result.Status);
        Assert.Equal(3u, result.WarmupSteps);
        Assert.Equal(4u, result.WorldSteps);
        Assert.Equal(1017370379u, Bits(result.RemainingDelta));
        Assert.Equal("w|1017370379|10|1015580809", trace.Events.Last(e => e.StartsWith("w|", StringComparison.Ordinal)));
        var finalScene = trace.Events.IndexOf("s|1029338126|0|1");
        Assert.Equal("after_time", trace.Events[finalScene + 1]);
        Assert.Equal("m|3", trace.Events[finalScene + 2]);
        WriteTrace("warmup-residual-main-world", state, result, trace);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1)]
    public void Backward_target_publishes_final_scene_without_warmup(int mode)
    {
        var state = State(mode, 1); state.CursorSeconds = 10f;
        var trace = new FrameTrace();
        var result = new SemanticFrameScheduler<TraceModel>().Run(state, new() { TargetSeconds = -1f, DeltaSeconds = -11f }, trace.Callbacks);
        Assert.Equal(FrameStatus.Ok, result.Status);
        Assert.Equal(0u, result.WarmupSteps);
        Assert.Equal(-1f, state.CursorSeconds);
        Assert.Contains($"s|{Bits(-1f)}|0|{mode}", trace.Events);
    }

    [Fact]
    public void Exact_warmup_limit_is_accepted_and_no_unused_capabilities_are_required()
    {
        var state = State(0, 1); state.CoreGate = 2;
        var sceneCount = 0;
        var result = new SemanticFrameScheduler<TraceModel>().Run(state, new() { TargetSeconds = 2f, WarmupLimit = 0 },
            new() { Scene = (_, _, _, _) => ++sceneCount, Draw = () => 0 });
        Assert.Equal(FrameStatus.Ok, result.Status);
        Assert.Equal(1, sceneCount);
        state.PhysicsMode = 1;
        state.CursorSeconds = 0f;
        var trace = new FrameTrace();
        Assert.Equal(FrameStatus.Ok, new SemanticFrameScheduler<TraceModel>().Run(state,
            new() { TargetSeconds = SemanticTime.FixedStep * 2f, WarmupLimit = 1 }, trace.Callbacks).Status);
    }

    [Fact]
    public void First_rank_match_is_excluded_without_falling_back_and_scene_sees_complete_table()
    {
        var state = State(1, 1);
        state.CoreGate = 1;
        var trace = new FrameTrace();
        var hooks = trace.Callbacks;
        hooks = hooks with { Scene = (time, skip, models, mode) => { Assert.Same(state.Models, models); Assert.Equal(4, models.Count(m => m is not null)); trace.Callbacks.Scene!(time, skip, models, mode); } };
        var result = new SemanticFrameScheduler<TraceModel>().Run(state,
            new() { TargetSeconds = SemanticTime.FixedStep * 2f, DeltaSeconds = 0.1f, WarmupSkipSlot = 0 }, hooks);
        Assert.Equal(1u, result.WarmupSteps);
        Assert.DoesNotContain("m|0", trace.Events);
        Assert.DoesNotContain("m|8", trace.Events);
        Assert.DoesNotContain("m|254", trace.Events);
        Assert.Contains("p|8", trace.Events);
        Assert.Contains("p|254", trace.Events);
    }

    [Fact]
    public void Rank254_is_included_and_rank255_is_world_only()
    {
        var state = new SemanticFrameState<TraceModel> { Phase = 1, PhysicsMode = 1 };
        state.SetModel(254, new(254, 254)); state.SetModel(0, new(0, 255));
        var trace = new FrameTrace();
        var result = new SemanticFrameScheduler<TraceModel>().Run(state, new(), trace.Callbacks);
        Assert.Equal(FrameStatus.Ok, result.Status);
        Assert.Contains("m|254", trace.Events);
        Assert.DoesNotContain("m|0", trace.Events);
        Assert.True(trace.Events.IndexOf("p|0") < trace.Events.IndexOf("p|254"));
    }

    [Fact]
    public void Vertex_receives_all_value_inputs_and_raw_edge_byte()
    {
        var state = State(0);
        var trace = new FrameTrace();
        var vertex = new VertexInput(Matrix4x4.CreateTranslation(1, 2, 3), new(4, 5, 6), 0.7f, 2);
        var models = new List<int>();
        var result = new SemanticFrameScheduler<TraceModel>().Run(state, new() { Vertex = vertex },
            trace.Callbacks with { Vertex = (model, actual) => { Assert.Equal(vertex, actual); models.Add(model.Slot); } });
        Assert.Equal(FrameStatus.Ok, result.Status);
        Assert.Equal([0, 3, 8, 254], models);
    }

    [Fact]
    public void Phase_zero_requires_only_draw_and_ignores_time_even_if_nonfinite()
    {
        var state = State(3, 1, 1, 1); state.Phase = 0;
        var count = 0;
        var result = new SemanticFrameScheduler<TraceModel>().Run(state,
            new() { DeltaSeconds = float.NaN, TargetSeconds = float.PositiveInfinity }, new() { Draw = () => { ++count; return 0; } });
        Assert.Equal(FrameStatus.Ok, result.Status);
        Assert.Equal(1, count);
        Assert.True(float.IsNaN(result.RemainingDelta));
        Assert.Equal(1, state.Reset);
    }

    [Theory]
    [InlineData(-2)] [InlineData(255)]
    public void Invalid_skip_is_rejected_before_draw(int skip)
    {
        var trace = new FrameTrace();
        var result = new SemanticFrameScheduler<TraceModel>().Run(State(), new() { WarmupSkipSlot = skip }, trace.Callbacks);
        Assert.Equal(FrameStatus.BadArgument, result.Status);
        Assert.Empty(trace.Events);
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)]
    public void Nonfinite_active_delta_is_rejected_before_any_callback(float delta)
    {
        var trace = new FrameTrace();
        var result = new SemanticFrameScheduler<TraceModel>().Run(State(), new() { DeltaSeconds = delta }, trace.Callbacks);
        Assert.Equal(FrameStatus.BadTime, result.Status);
        Assert.Empty(trace.Events);
    }

    [Fact]
    public void Warmup_limit_and_cursor_stall_reject_without_partial_callbacks_or_state_changes()
    {
        var trace = new FrameTrace(); var state = State(1, 1);
        var scheduler = new SemanticFrameScheduler<TraceModel>();
        Assert.Equal(FrameStatus.WarmupLimit, scheduler.Run(state, new() { TargetSeconds = 1f, WarmupLimit = 1 }, trace.Callbacks).Status);
        Assert.Equal(0f, state.CursorSeconds);
        state.CursorSeconds = 1048576f;
        Assert.Equal(FrameStatus.CursorStalled, scheduler.Run(state, new() { TargetSeconds = 1048577f }, trace.Callbacks).Status);
        Assert.Equal(1048576f, state.CursorSeconds);
        Assert.Empty(trace.Events);
    }

    [Fact]
    public void Missing_path_capabilities_are_rejected_before_cpu_and_step_result_is_ignored()
    {
        var trace = new FrameTrace(); var scheduler = new SemanticFrameScheduler<TraceModel>();
        foreach (var missing in new[] { trace.Callbacks with { Draw = null }, trace.Callbacks with { Rank = null },
            trace.Callbacks with { Morph = null }, trace.Callbacks with { Bone = null }, trace.Callbacks with { Vertex = null },
            trace.Callbacks with { Push = null }, trace.Callbacks with { Step = null }, trace.Callbacks with { Pull = null }, trace.Callbacks with { ResetModel = null } })
            Assert.Equal(FrameStatus.MissingCallback, scheduler.Run(State(1, 0, 0, 2), new(), missing).Status);
        Assert.Equal(FrameStatus.MissingCallback, scheduler.Run(State(0, 2), new(), trace.Callbacks with { Scene = null }).Status);
        Assert.Empty(trace.Events);
        Assert.Equal(FrameStatus.Ok, scheduler.Run(State(1), new(), trace.Callbacks with { Step = (_, _, _) => int.MinValue }).Status);
    }

    [Fact]
    public void Reentry_is_safely_rejected_and_callback_exception_releases_guard()
    {
        var scheduler = new SemanticFrameScheduler<TraceModel>(); var state = State(); var trace = new FrameTrace();
        var callbacks = trace.Callbacks with { Draw = () => { Assert.Equal(FrameStatus.ReentrantCall, scheduler.Run(state, new(), trace.Callbacks).Status); throw new InvalidOperationException("host failed"); } };
        Assert.Throws<InvalidOperationException>(() => scheduler.Run(state, new(), callbacks));
        Assert.Equal(FrameStatus.Ok, scheduler.Run(state, new(), trace.Callbacks).Status);
    }

    [Fact]
    public void Null_arguments_are_explicit_errors()
    {
        var scheduler = new SemanticFrameScheduler<TraceModel>(); var trace = new FrameTrace();
        Assert.Equal(FrameStatus.BadArgument, scheduler.Run(null, new(), trace.Callbacks).Status);
        Assert.Equal(FrameStatus.BadArgument, scheduler.Run(State(), null, trace.Callbacks).Status);
        Assert.Equal(FrameStatus.BadArgument, scheduler.Run(State(), new(), null).Status);
    }

    internal static SemanticFrameState<TraceModel> State(int mode = 0, byte playing = 0, byte pending = 0, byte reset = 0)
    {
        var state = new SemanticFrameState<TraceModel> { Phase = 1, PhysicsMode = mode, Playing = playing, Pending = pending, Reset = reset };
        state.SetModel(0, new(0, 2)); state.SetModel(3, new(3, 0)); state.SetModel(8, new(8, 2)); state.SetModel(254, new(254, 255));
        return state;
    }

    private static void WriteTrace(string scenario, SemanticFrameState<TraceModel> state, SemanticFrameResult result, FrameTrace trace)
        => TestContext.Current.TestOutputHelper?.WriteLine("SCHEDULING_TRACE_JSON:" + JsonSerializer.Serialize(new
        {
            Scenario = scenario, Evidence = "Executed C# test callbacks; not original full-frame/GPU/bone execution",
            result.Status, RemainingDeltaBits = Bits(result.RemainingDelta), result.WarmupSteps, result.WorldSteps,
            result.DrawStatus, CursorBits = Bits(state.CursorSeconds), state.Pending, state.Reset, trace.Events
        }));
}
