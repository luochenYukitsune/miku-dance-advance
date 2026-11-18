using System.Runtime.ExceptionServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

/// <summary>Neutral render input, with exact encoder time and explicit 120Hz sampled pose time.
/// Dimensions are instructions to the eventual renderer, not a claim that this runner allocates an image.</summary>
public sealed record OfflineFrame(OfflineFrameSample Sample, int Width, int Height, OriginalFrameScene Scene,
    PmmCameraPose? Camera, PmmLightPose? Light, VmdSelfShadowPose? SelfShadow)
{
    /// <summary>Optional world-space camera already resolved by the scene owner; raw Camera remains editor metadata.</summary>
    public SceneCamera? ResolvedCamera { get; init; }
    /// <summary>The solved camera rig transform, retained independently of its local authored pose.</summary>
    public System.Numerics.Matrix4x4? CameraParentTransform { get; init; }
    /// <summary>Per-actor immutable diagnostic channels accompanying the geometry sample.
    /// Evaluated texture factors used for rendering must already be in Scene.Surface.Materials;
    /// this actor-local metadata does not map actor IDs to combined material ranges.</summary>
    public IReadOnlyDictionary<Guid, CharacterMorphChannels> MorphChannels { get; init; }
        = new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, CharacterMorphChannels>(new Dictionary<Guid, CharacterMorphChannels>());
    /// <summary>Effective per-instance preset references and exact combined material ranges supplied by a project owner.
    /// Other neutral animation inputs have no project style selection and leave this list empty.</summary>
    public IReadOnlyList<SceneRenderingStyleBinding> RenderingStyles { get; init; } = Array.Empty<SceneRenderingStyleBinding>();
    /// <summary>Selected inputs from this geometry sample; never a callback into another live clock.</summary>
    public IReadOnlyDictionary<Guid, SceneObjectRenderInputs> SceneInputs { get; init; } = SceneObjectRenderInputs.Empty;
    /// <summary>Optional detached source-local material tables from this exact sample, including hidden models.</summary>
    public IReadOnlyDictionary<Guid, SceneObjectMaterialInputs> ObjectMaterialInputs { get; init; } = SceneObjectMaterialInputs.Empty;
    public IReadOnlyList<SceneProjectAccessoryFrame> Accessories { get; init; } = Array.Empty<SceneProjectAccessoryFrame>();
    public IReadOnlyList<SceneProjectDrawItem> MixedDrawOrder { get; init; } = Array.Empty<SceneProjectDrawItem>();
    public int? AccessoryIndexAfterModels { get; init; }
    public IReadOnlyList<byte> UnpreparedAccessoryIndices { get; init; } = Array.Empty<byte>();
    /// <summary>Same-sample solved requested bones for models parenting accessories, including hidden parents.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, System.Numerics.Matrix4x4>> AccessoryParentBoneWorldMatrices { get; init; }
        = new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, IReadOnlyDictionary<int, System.Numerics.Matrix4x4>>(
            new Dictionary<Guid, IReadOnlyDictionary<int, System.Numerics.Matrix4x4>>());
}
public sealed record OfflineFrameProgress(long CompletedFrames, long TotalFrames, long SimulationTicks,
    OfflineFrameTime PresentationTime);
public sealed record OfflineFrameRunResult(long CompletedFrames, long SimulationTicks);

/// <summary>Runs an exclusively owned session without wall-clock timing, GUI, GPU or encoder dependencies.
/// Seek restarts at Start-PreRoll; each subsequent animation/physics sample advances exactly one 120Hz tick.
/// The caller must not edit, dispose or advance the session from callbacks or another thread. Cancellation or
/// capture failure leaves it at the last evaluated tick; it neither disposes the session nor rolls back its solver.
/// With overlapCapture, frame N's capture runs on the thread pool while this owner simulates and snapshots frame N+1.
/// Captures remain one at a time and in output order, and progress, ticks and failure context are those of the
/// sequential run. The callback may then only read its detached OfflineFrame; it must never touch the session.</summary>
public static class OfflineFrameRunner
{
    /// <summary>Uses the project's existing animation/physics owner and its current rendering metadata.
    /// The fixed-step scheduling, pre-roll, cancellation and exclusive-clock contract are shared with RunAsync.</summary>
    public static async Task<OfflineFrameRunResult> RunProjectAsync(SceneProjectSession session, OfflineFramePlan plan,
        Func<OfflineFrame, CancellationToken, ValueTask> capture, IProgress<OfflineFrameProgress>? progress = null,
        CancellationToken cancellationToken = default, IEnumerable<SceneObjectRenderRequest>? sceneInputRequests = null,
        bool overlapCapture = false)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(capture);
        cancellationToken.ThrowIfCancellationRequested();
        if (session.IsFaulted) throw new InvalidOperationException("Recover or reload a faulted project session before offline output.");
        _ = session.RenderingStyles; // Reject opaque future selection metadata before changing the project clock.
        var requests = SceneObjectRenderRequest.Freeze(sceneInputRequests);
        if (requests.Count != 0) _ = session.CaptureRenderSnapshotWithCancellation(requests, cancellationToken); // Invalid requests cannot seek the owner.
        return await RunCoreAsync(plan, session.DurationFrames, (frame, token) => session.Seek(frame, token),
            token => session.Advance(1.0 / OfflineFramePlan.SimulationTicksPerSecond, token),
            () => session.CurrentFrame, () => session.IsFaulted, (sample, width, height, token) =>
            {
                var snapshot = session.CaptureRenderSnapshotWithCancellation(requests, token);
                return new(sample, width, height, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
                {
                    ResolvedCamera = snapshot.ResolvedCamera, CameraParentTransform = snapshot.CameraParentTransform, MorphChannels = snapshot.MorphChannels,
                    RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs,
                    ObjectMaterialInputs = snapshot.ObjectMaterialInputs,
                    Accessories = snapshot.Accessories, MixedDrawOrder = snapshot.MixedDrawOrder,
                    AccessoryIndexAfterModels = snapshot.AccessoryIndexAfterModels,
                    UnpreparedAccessoryIndices = snapshot.UnpreparedAccessoryIndices,
                    AccessoryParentBoneWorldMatrices = snapshot.AccessoryParentBoneWorldMatrices
                };
            }, capture, progress, cancellationToken, overlapCapture).ConfigureAwait(false);
    }

    public static async Task<OfflineFrameRunResult> RunAsync(SceneAnimationSession session, OfflineFramePlan plan,
        Func<OfflineFrame, CancellationToken, ValueTask> capture, IProgress<OfflineFrameProgress>? progress = null,
        CancellationToken cancellationToken = default, bool overlapCapture = false)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(capture);
        cancellationToken.ThrowIfCancellationRequested();
        if (session.IsFaulted) throw new InvalidOperationException("Recover or reload a faulted animation session before offline output.");
        return await RunCoreAsync(plan, session.DurationFrames, (frame, token) => session.Seek(frame, token),
            token => session.Advance(1.0 / OfflineFramePlan.SimulationTicksPerSecond, token),
            () => session.CurrentFrame, () => session.IsFaulted, (sample, width, height, token) => new(sample, width, height, session.GetFrame(token),
                session.CameraPose, session.LightPose, session.SelfShadowPose)
            {
                ResolvedCamera = session.Camera ?? session.Frame.Options?.CameraOverride,
                CameraParentTransform = session.CameraParentTransform,
                MorphChannels = new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, CharacterMorphChannels>(
                    session.Actors.Where(actor => actor.HasMotion).ToDictionary(actor => actor.ObjectId,
                        actor => session.GetMorphChannels(actor.ObjectId)))
            }, capture, progress, cancellationToken, overlapCapture).ConfigureAwait(false);
    }

    /// <summary>Uses the existing PMM owner and its resolved parent camera. No second animation/physics clock is created.</summary>
    public static Task<OfflineFrameRunResult> RunPmmAsync(PmmProjectPlayback playback, OfflineFramePlan plan,
        Func<OfflineFrame, CancellationToken, ValueTask> capture, IProgress<OfflineFrameProgress>? progress = null,
        CancellationToken cancellationToken = default, bool overlapCapture = false)
    {
        ArgumentNullException.ThrowIfNull(playback);
        return RunCoreAsync(plan, playback.DurationFrames, (frame, token) => { token.ThrowIfCancellationRequested(); playback.Seek(frame); },
            token => { token.ThrowIfCancellationRequested(); playback.Advance(playback.CurrentFrame + .25, 1.0 / OfflineFramePlan.SimulationTicksPerSecond); },
            () => playback.CurrentFrame, () => false, (sample, width, height, token) =>
            {
                token.ThrowIfCancellationRequested();
                var light = playback.IsSavedPose ? playback.Tracks.SavedLight : playback.Tracks.LightAt(playback.CurrentFrame);
                var shadow = playback.SelfShadowPose;
                return new OfflineFrame(sample, width, height, playback.Frame, playback.CameraPose, light,
                    shadow is { } value ? new VmdSelfShadowPose(value.Mode, value.Distance) : null)
                    { ResolvedCamera = playback.Camera, CameraParentTransform = playback.CameraParentTransform };
            }, capture, progress, cancellationToken, overlapCapture);
    }

    private const string ClockChanged = "Offline capture changed the exclusively owned animation clock.";

    private static async Task<OfflineFrameRunResult> RunCoreAsync(OfflineFramePlan plan, uint duration,
        Action<uint, CancellationToken> seek, Action<CancellationToken> advance, Func<double> currentFrame, Func<bool> faulted,
        Func<OfflineFrameSample, int, int, CancellationToken, OfflineFrame> snapshot, Func<OfflineFrame, CancellationToken, ValueTask> capture,
        IProgress<OfflineFrameProgress>? progress, CancellationToken cancellationToken, bool overlapCapture)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(capture);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.EndFrame > duration)
            throw new ArgumentOutOfRangeException(nameof(plan), "The output interval exceeds the session's imported motion duration.");
        long ticks = 0, completed = 0;
        long? attemptingTick = null;
        OfflineFrameSample? sample = null;
        string phase = "seek";
        using var overlap = overlapCapture ? new CaptureOverlap(plan, cancellationToken) : null;
        var token = overlap?.Token ?? cancellationToken;
        try
        {
            // Capture failure is not session failure; a new run always resets physics explicitly here.
            seek(plan.SimulationStartFrame, token);
            phase = "progress";
            progress?.Report(new(0, plan.FrameCount, 0, new(0, 1)));
            for (long index = 0; index < plan.FrameCount; index++)
            {
                sample = plan.GetSample(index);
                long target = (long)(sample.SampledSourceFrame.Numerator * 4 / sample.SampledSourceFrame.Denominator)
                    - (long)plan.SimulationStartFrame * 4;
                phase = "simulation";
                while (ticks < target)
                {
                    token.ThrowIfCancellationRequested();
                    attemptingTick = ticks + 1;
                    advance(token);
                    ticks++;
                    attemptingTick = null;
                }
                phase = "snapshot";
                token.ThrowIfCancellationRequested();
                double frameBeforeCapture = currentFrame();
                var output = snapshot(sample, plan.Width, plan.Height, token);
                if (overlap is not null)
                {
                    // The previous frame was capturing while this one was simulated; finish it before the next capture.
                    await overlap.CompleteAsync(currentFrame, faulted, frameBeforeCapture, progress).ConfigureAwait(false);
                    completed = index;
                    phase = "between-frames";
                    token.ThrowIfCancellationRequested();
                    overlap.Start(capture, output, index, ticks, frameBeforeCapture);
                }
                else
                {
                    phase = "capture";
                    await capture(output, token).ConfigureAwait(false);
                    phase = "validate-clock";
                    if (currentFrame() != frameBeforeCapture || faulted()) throw new InvalidOperationException(ClockChanged);
                    completed = index + 1;
                    phase = "progress";
                    progress?.Report(new(completed, plan.FrameCount, ticks, sample.PresentationTime));
                }
                phase = "between-frames";
                sample = null;
                token.ThrowIfCancellationRequested();
            }
            if (overlap is not null)
            {
                await overlap.CompleteAsync(currentFrame, faulted, currentFrame(), progress).ConfigureAwait(false);
                completed = plan.FrameCount;
            }
            return new(plan.FrameCount, ticks);
        }
        catch (Exception error)
        {
            // No capture may outlive the run. The earliest failure of either side is the one propagated.
            var failure = error;
            if (overlap is not null) { failure = await overlap.DrainAsync(error, progress).ConfigureAwait(false); completed = overlap.Completed; }
            if (!ReferenceEquals(failure, overlap?.Attributed))
                Describe(failure, phase, completed, sample, ticks, attemptingTick, plan.SimulationStartFrame, Observe(currentFrame));
            if (ReferenceEquals(failure, error)) throw;
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private static double? Observe(Func<double> currentFrame)
    {
        try { double value = currentFrame(); return double.IsFinite(value) ? value : null; } catch { return null; }
    }

    // Diagnostic observation must never replace the solver/capture exception, even for a custom Data dictionary.
    private static void Describe(Exception error, string phase, long completed, OfflineFrameSample? sample, long ticks,
        long? attemptingTick, uint startFrame, double? observed)
    {
        try { error.Data[OfflineFrameFailureContext.DataKey] = new OfflineFrameFailureContext(phase, completed, sample, ticks, attemptingTick, startFrame, observed); }
        catch { }
    }

    /// <summary>At most one capture in flight beside the owner's clock. The first failure on either side is recorded
    /// and cancels the other through the shared linked token; a capture failure keeps its own frame's context.</summary>
    private sealed class CaptureOverlap(OfflineFramePlan plan, CancellationToken cancellationToken) : IDisposable
    {
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private Task<Exception?>? pending;
        private Exception? first;
        private long index, ticks;
        private OfflineFrameSample? sample;
        private double clock;
        public CancellationToken Token => stop.Token;
        public long Completed { get; private set; }
        public Exception? Attributed { get; private set; }

        public void Start(Func<OfflineFrame, CancellationToken, ValueTask> capture, OfflineFrame frame, long frameIndex,
            long frameTicks, double frameClock)
        {
            index = frameIndex; sample = frame.Sample; ticks = frameTicks; clock = frameClock;
            var token = stop.Token;
            pending = Task.Run(async () =>
            {
                try { await capture(frame, token).ConfigureAwait(false); return null; }
                catch (Exception error) { Fail(error); return (Exception?)error; }
            });
        }

        /// <summary>Waits for the in-flight capture, then validates the exclusive clock and reports that frame's progress.</summary>
        public async Task CompleteAsync(Func<double> currentFrame, Func<bool> faulted, double expectedClock,
            IProgress<OfflineFrameProgress>? progress)
        {
            if (pending is null) return;
            var error = await pending.ConfigureAwait(false); pending = null;
            if (error is not null)
            {
                Attributed = error; Describe(error, "capture", index, sample, ticks, null, plan.SimulationStartFrame, clock);
                ExceptionDispatchInfo.Capture(error).Throw();
            }
            string phase = "validate-clock";
            try
            {
                if (currentFrame() != expectedClock || faulted()) throw new InvalidOperationException(ClockChanged);
                Completed = index + 1;
                phase = "progress";
                progress?.Report(new(Completed, plan.FrameCount, ticks, sample!.PresentationTime));
            }
            catch (Exception failure)
            {
                Fail(failure); Attributed = failure;
                Describe(failure, phase, Completed, sample, ticks, null, plan.SimulationStartFrame, Observe(currentFrame));
                throw;
            }
        }

        /// <summary>Stops and awaits an in-flight capture after an owner-side failure; returns the first failure overall.</summary>
        public async Task<Exception> DrainAsync(Exception error, IProgress<OfflineFrameProgress>? progress)
        {
            Fail(error);
            if (pending is not null)
            {
                var captureError = await pending.ConfigureAwait(false); pending = null;
                if (captureError is null)
                {
                    Completed = index + 1;
                    // The sequential run had reported this frame before failing; a notification error cannot replace the failure.
                    try { progress?.Report(new(Completed, plan.FrameCount, ticks, sample!.PresentationTime)); } catch { }
                }
                else if (ReferenceEquals(captureError, Volatile.Read(ref first)))
                { Attributed = captureError; Describe(captureError, "capture", index, sample, ticks, null, plan.SimulationStartFrame, clock); }
            }
            return Volatile.Read(ref first) ?? error;
        }

        private void Fail(Exception error)
        {
            Interlocked.CompareExchange(ref first, error, null);
            // Cancellation callbacks belong to the work being stopped; the first failure is already recorded.
            try { stop.Cancel(); } catch { }
        }

        public void Dispose() => stop.Dispose();
    }
}
