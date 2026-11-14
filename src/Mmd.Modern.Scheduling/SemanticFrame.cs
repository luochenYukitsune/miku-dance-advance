using System.Numerics;

namespace Mmd.Modern.Scheduling;

/// <summary>Host-owned references and resolved playback state. Keep stable during a call.</summary>
public sealed class SemanticFrameState<TModel> where TModel : class
{
    public const int ModelSlots = 255;
    private readonly TModel?[] models = new TModel?[ModelSlots];
    public SemanticFrameState() => Models = Array.AsReadOnly(models);
    public IReadOnlyList<TModel?> Models { get; }
    public void SetModel(int slot, TModel? model) => models[slot] = model;
    public float CursorSeconds { get; set; }
    public int PhysicsMode { get; set; }
    public uint Phase { get; set; }
    public byte Playing { get; set; }
    public byte CoreGate { get; set; }
    public byte WorldGate { get; set; }
    public byte Pending { get; set; }
    public byte Reset { get; set; }
}

/// <summary>Value transport only; System.Numerics does not imply original matrix compatibility.</summary>
public readonly record struct VertexInput(Matrix4x4 Transform, Vector3 Camera, float Angle, byte EdgeEnabled);

public sealed record SemanticFrameInput
{
    public float DeltaSeconds { get; init; }
    public float TargetSeconds { get; init; }
    public uint WarmupLimit { get; init; } = 10000;
    public int WarmupSkipSlot { get; init; } = -1;
    public VertexInput Vertex { get; init; }
}

public delegate void SceneCallback<TModel>(float seconds, byte skip, IReadOnlyList<TModel?> models, int mode) where TModel : class;
public delegate void BoneCallback<TModel>(TModel model, byte after, IReadOnlyList<TModel?> models, int mode) where TModel : class;

/// <summary>Borrowed synchronous capabilities. Supply real implementations for the executed path.</summary>
public sealed record SemanticFrameCallbacks<TModel> where TModel : class
{
    public Func<TModel, byte>? Rank { get; init; }
    public SceneCallback<TModel>? Scene { get; init; }
    public Action<TModel>? Morph { get; init; }
    public BoneCallback<TModel>? Bone { get; init; }
    public Action<TModel>? Push { get; init; }
    public Action<TModel>? ResetModel { get; init; }
    public Action<TModel>? Pull { get; init; }
    public Action<TModel, VertexInput>? Vertex { get; init; }
    public Func<float, int, float, int>? Step { get; init; }
    public Func<int>? Draw { get; init; }
    public Action<float, int>? AfterTime { get; init; }
}

public enum FrameStatus { Ok, BadArgument, BadTime, WarmupLimit, CursorStalled, DrawFailed, MissingCallback, ReentrantCall }
public readonly record struct SemanticFrameResult(FrameStatus Status, float RemainingDelta, uint WarmupSteps, uint WorldSteps, int DrawStatus);

public sealed class SemanticFrameScheduler<TModel> where TModel : class
{
    private int running;

    /// <summary>
    /// Runs one resolved frame. Callbacks must not change state, slots, ranks or runtime bindings.
    /// Host callback exceptions propagate; already completed work is not rolled back.
    /// </summary>
    public SemanticFrameResult Run(SemanticFrameState<TModel>? state, SemanticFrameInput? input, SemanticFrameCallbacks<TModel>? callbacks)
        => Execute(state, input, callbacks, Region.FullFrame);

    // Friend-test boundaries only. Native vectors cover these regions separately,
    // not CPU/draw/after-time or a whole original application frame.
    internal SemanticFrameResult ReplayMainRegion(SemanticFrameState<TModel> state, SemanticFrameInput input, SemanticFrameCallbacks<TModel> callbacks)
        => Execute(state, input, callbacks, Region.Main);
    internal SemanticFrameResult ReplayTimeRegion(SemanticFrameState<TModel> state, SemanticFrameInput input, SemanticFrameCallbacks<TModel> callbacks)
        => Execute(state, input, callbacks, Region.Time);

    private SemanticFrameResult Execute(SemanticFrameState<TModel>? state, SemanticFrameInput? input, SemanticFrameCallbacks<TModel>? callbacks, Region region)
    {
        var result = new Progress { Remaining = input?.DeltaSeconds ?? 0f };
        if (state is null || input is null || callbacks is null) return result.Finish(FrameStatus.BadArgument);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return result.Finish(FrameStatus.ReentrantCall);
        try
        {
            var status = Preflight(state, input, callbacks, out var warmups);
            if (status != FrameStatus.Ok) return result.Finish(status);
            if (region == Region.FullFrame)
            {
                if (state.Phase != 0 && state.CoreGate == 0)
                    Every(state, model => callbacks.Vertex!(model, input.Vertex));
                result.DrawStatus = callbacks.Draw!();
                if (result.DrawStatus != 0) return result.Finish(FrameStatus.DrawFailed);
            }
            if (state.Phase == 0) return result.Finish(FrameStatus.Ok);
            if (region is Region.FullFrame or Region.Time) RunTime(state, input, callbacks, warmups, ref result);
            if (region == Region.FullFrame) callbacks.AfterTime?.Invoke(state.CursorSeconds, state.PhysicsMode);
            if (region is Region.FullFrame or Region.Main) RunMain(state, callbacks, ref result);
            return result.Finish(FrameStatus.Ok);
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private static FrameStatus Preflight(SemanticFrameState<TModel> state, SemanticFrameInput input, SemanticFrameCallbacks<TModel> hooks, out uint warmups)
    {
        warmups = 0;
        if (input.WarmupSkipSlot is < -1 or >= SemanticFrameState<TModel>.ModelSlots) return FrameStatus.BadArgument;
        if (hooks.Draw is null) return FrameStatus.MissingCallback;
        if (state.Phase == 0) return FrameStatus.Ok;
        if (!float.IsFinite(input.DeltaSeconds)) return FrameStatus.BadTime;
        if (state.Playing != 0)
        {
            if (!float.IsFinite(state.CursorSeconds) || !float.IsFinite(input.TargetSeconds)) return FrameStatus.BadTime;
            if (hooks.Scene is null) return FrameStatus.MissingCallback;
            if (state.PhysicsMode > 0)
            {
                var cursor = Binary32.Add(state.CursorSeconds, SemanticTime.FixedStep);
                while (cursor < input.TargetSeconds)
                {
                    if (cursor == state.CursorSeconds && warmups == 0) return FrameStatus.CursorStalled;
                    if (warmups == input.WarmupLimit) return FrameStatus.WarmupLimit;
                    ++warmups;
                    var next = Binary32.Add(cursor, SemanticTime.FixedStep);
                    if (next == cursor) return FrameStatus.CursorStalled;
                    cursor = next;
                }
            }
        }
        if ((state.CoreGate == 0 || warmups != 0) && (hooks.Rank is null || hooks.Morph is null || hooks.Bone is null)) return FrameStatus.MissingCallback;
        if (state.CoreGate == 0 && hooks.Vertex is null) return FrameStatus.MissingCallback;
        if ((warmups != 0 || (state.CoreGate == 0 && WorldActive(state))) && (hooks.Push is null || hooks.Step is null)) return FrameStatus.MissingCallback;
        if (state.CoreGate == 0 && WorldActive(state) && (hooks.Pull is null || (state.Reset != 0 && hooks.ResetModel is null))) return FrameStatus.MissingCallback;
        return FrameStatus.Ok;
    }

    private static void RunTime(SemanticFrameState<TModel> state, SemanticFrameInput input, SemanticFrameCallbacks<TModel> hooks, uint warmups, ref Progress result)
    {
        if (state.Playing == 0) return;
        if (state.PhysicsMode > 0) state.CursorSeconds = Binary32.Add(state.CursorSeconds, SemanticTime.FixedStep);
        for (uint index = 0; index < warmups; ++index)
        {
            hooks.Scene!(state.CursorSeconds, 1, state.Models, state.PhysicsMode);
            Ranked(state, hooks, 0, state.PhysicsMode, input.WarmupSkipSlot);
            Every(state, hooks.Push!, input.WarmupSkipSlot);
            Step(hooks, SemanticTime.FixedStep, ref result);
            result.Remaining = Binary32.Subtract(result.Remaining, SemanticTime.FixedStep);
            state.CursorSeconds = Binary32.Add(state.CursorSeconds, SemanticTime.FixedStep);
            ++result.Warmups;
        }
        state.CursorSeconds = input.TargetSeconds;
        hooks.Scene!(state.CursorSeconds, 0, state.Models, state.PhysicsMode);
    }

    private static void RunMain(SemanticFrameState<TModel> state, SemanticFrameCallbacks<TModel> hooks, ref Progress result)
    {
        if (state.CoreGate != 0) return;
        Ranked(state, hooks, 0, state.Reset != 0 ? 0 : state.PhysicsMode);
        if (WorldActive(state))
        {
            Every(state, hooks.Push!);
            if (state.PhysicsMode == 3 && (state.Pending & 1) != 0)
            {
                Step(hooks, SemanticTime.FixedStep, ref result);
                Step(hooks, SemanticTime.FixedStep, ref result);
            }
            else Step(hooks, result.Remaining, ref result);
            if (state.Reset != 0)
            {
                for (var round = 0; round < 3; ++round)
                {
                    Every(state, hooks.ResetModel!);
                    Step(hooks, SemanticTime.FixedStep, ref result);
                }
                state.Reset = 0;
            }
            Every(state, hooks.Pull!);
            state.Pending = 0;
        }
        Ranked(state, hooks, 1, state.PhysicsMode);
    }

    private static bool WorldActive(SemanticFrameState<TModel> state)
        => state.WorldGate == 0 && (state.PhysicsMode is 1 or 2 ||
            (state.PhysicsMode == 3 && ((state.Pending | state.Reset | state.Playing) & 1) != 0));

    private static void Ranked(SemanticFrameState<TModel> state, SemanticFrameCallbacks<TModel> hooks, byte after, int mode, int skip = -1)
    {
        for (var rank = 0; rank < SemanticFrameState<TModel>.ModelSlots; ++rank)
        {
            for (var slot = 0; slot < state.Models.Count; ++slot)
            {
                var model = state.Models[slot];
                if (model is null || hooks.Rank!(model) != rank) continue;
                if (slot != skip)
                {
                    hooks.Morph!(model);
                    hooks.Bone!(model, after, state.Models, mode);
                }
                break; // Exclusion is AFTER the first match: do not fall back to duplicate ranks.
            }
        }
    }

    private static void Every(SemanticFrameState<TModel> state, Action<TModel> operation, int skip = -1)
    {
        for (var slot = 0; slot < state.Models.Count; ++slot)
            if (slot != skip && state.Models[slot] is { } model) operation(model);
    }

    private static void Step(SemanticFrameCallbacks<TModel> hooks, float delta, ref Progress result)
    {
        _ = hooks.Step!(delta, 10, SemanticTime.FixedStep); // Original scheduler ignores backend return value.
        ++result.WorldSteps;
    }

    private enum Region { FullFrame, Main, Time }
    private struct Progress
    {
        public float Remaining;
        public uint Warmups, WorldSteps;
        public int DrawStatus;
        public readonly SemanticFrameResult Finish(FrameStatus status) => new(status, Remaining, Warmups, WorldSteps, DrawStatus);
    }
}
