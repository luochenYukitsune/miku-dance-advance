using System.Collections.ObjectModel;
using Mmd.Modern.Application;

namespace Mmd.Modern.ViewOutput;

/// <summary>Owns one existing scene clock; renderers never advance or evaluate animation themselves.</summary>
internal abstract class ViewPlaybackOwner : IDisposable
{
    internal abstract uint DurationFrames { get; }
    internal abstract void Advance(double elapsedSeconds, CancellationToken token);
    internal abstract PreviewFrame Snapshot(int width, int height, IReadOnlyList<SceneObjectRenderRequest>? sceneInputRequests = null);
    internal abstract Task<OfflineFrameRunResult> RunAsync(OfflineFramePlan plan,
        Func<OfflineFrame, CancellationToken, ValueTask> capture, IProgress<OfflineFrameProgress>? progress, CancellationToken token,
        IReadOnlyList<SceneObjectRenderRequest>? sceneInputRequests = null, bool overlapCapture = false);
    public abstract void Dispose();
    internal static ViewPlaybackOwner From(SceneAnimationSession session) => new SceneOwner(session);
    internal static ViewPlaybackOwner From(PmmProjectPlayback playback) => new PmmOwner(playback);
    internal static ViewPlaybackOwner From(SceneProjectSession project) => new ProjectOwner(project);
    internal static ViewPlaybackOwner From(PmaLoadedProject project) => new PmaOwner(project);

    private sealed class ProjectOwner(SceneProjectSession project) : ViewPlaybackOwner
    {
        internal override uint DurationFrames => project.DurationFrames;
        internal override void Advance(double elapsedSeconds, CancellationToken token) => project.Advance(elapsedSeconds, token);
        internal override PreviewFrame Snapshot(int width, int height, IReadOnlyList<SceneObjectRenderRequest>? sceneInputRequests = null)
        {
            var snapshot = project.CaptureRenderSnapshot(sceneInputRequests);
            return new(width, height, snapshot.SourceFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
            {
                ResolvedCamera = snapshot.ResolvedCamera,
                CameraParentTransform = snapshot.CameraParentTransform,
                MorphChannels = snapshot.MorphChannels,
                RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs,
                ObjectMaterialInputs = snapshot.ObjectMaterialInputs,
                Accessories = snapshot.Accessories, MixedDrawOrder = snapshot.MixedDrawOrder,
                AccessoryIndexAfterModels = snapshot.AccessoryIndexAfterModels,
                UnpreparedAccessoryIndices = snapshot.UnpreparedAccessoryIndices,
                AccessoryParentBoneWorldMatrices = snapshot.AccessoryParentBoneWorldMatrices
            };
        }
        internal override Task<OfflineFrameRunResult> RunAsync(OfflineFramePlan plan,
            Func<OfflineFrame, CancellationToken, ValueTask> capture, IProgress<OfflineFrameProgress>? progress, CancellationToken token,
            IReadOnlyList<SceneObjectRenderRequest>? sceneInputRequests = null, bool overlapCapture = false)
            => OfflineFrameRunner.RunProjectAsync(project, plan, capture, progress, token, sceneInputRequests, overlapCapture);
        public override void Dispose() => project.Dispose();
    }

    // The PMA owner retains extraction along with the session. Disposing only its project owner would
    // leave extracted resources behind; disposing PMA during creation would remove live texture paths.
    private sealed class PmaOwner(PmaLoadedProject project) : ViewPlaybackOwner
    {
        private readonly ProjectOwner owner = new(project.Session);
        internal override uint DurationFrames => owner.DurationFrames;
        internal override void Advance(double elapsedSeconds, CancellationToken token) => owner.Advance(elapsedSeconds, token);
        internal override PreviewFrame Snapshot(int width, int height, IReadOnlyList<SceneObjectRenderRequest>? sceneInputRequests = null)
            => owner.Snapshot(width, height, sceneInputRequests);
        internal override Task<OfflineFrameRunResult> RunAsync(OfflineFramePlan plan,
            Func<OfflineFrame, CancellationToken, ValueTask> capture, IProgress<OfflineFrameProgress>? progress, CancellationToken token,
            IReadOnlyList<SceneObjectRenderRequest>? sceneInputRequests = null, bool overlapCapture = false)
            => owner.RunAsync(plan, capture, progress, token, sceneInputRequests, overlapCapture);
        public override void Dispose() => project.Dispose();
    }

    private sealed class SceneOwner(SceneAnimationSession session) : ViewPlaybackOwner
    {
        internal override uint DurationFrames => session.DurationFrames;
        internal override void Advance(double elapsedSeconds, CancellationToken token) => session.Advance(elapsedSeconds, token);
        internal override PreviewFrame Snapshot(int width, int height, IReadOnlyList<SceneObjectRenderRequest>? sceneInputRequests = null)
        {
            ViewSceneInputRequests.RejectNonProject(sceneInputRequests);
            return new(width, height, session.CurrentFrame, session.Frame, session.CameraPose, session.LightPose, session.SelfShadowPose)
            {
                ResolvedCamera = session.Camera ?? session.Frame.Options?.CameraOverride,
                CameraParentTransform = session.CameraParentTransform,
                MorphChannels = new ReadOnlyDictionary<Guid, CharacterMorphChannels>(session.Actors.Where(actor => actor.HasMotion)
                    .ToDictionary(actor => actor.ObjectId, actor => session.GetMorphChannels(actor.ObjectId)))
            };
        }
        internal override Task<OfflineFrameRunResult> RunAsync(OfflineFramePlan plan,
            Func<OfflineFrame, CancellationToken, ValueTask> capture, IProgress<OfflineFrameProgress>? progress, CancellationToken token,
            IReadOnlyList<SceneObjectRenderRequest>? sceneInputRequests = null, bool overlapCapture = false)
        {
            ViewSceneInputRequests.RejectNonProject(sceneInputRequests);
            return OfflineFrameRunner.RunAsync(session, plan, capture, progress, token, overlapCapture);
        }
        public override void Dispose() => session.Dispose();
    }

    private sealed class PmmOwner(PmmProjectPlayback playback) : ViewPlaybackOwner
    {
        internal override uint DurationFrames => playback.DurationFrames;
        internal override void Advance(double elapsedSeconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds > 86400)
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            double next = Math.Min(playback.DurationFrames, playback.CurrentFrame + elapsedSeconds * OfflineFramePlan.SourceFramesPerSecond);
            playback.Advance(next, (next - playback.CurrentFrame) / OfflineFramePlan.SourceFramesPerSecond);
        }
        internal override PreviewFrame Snapshot(int width, int height, IReadOnlyList<SceneObjectRenderRequest>? sceneInputRequests = null)
        {
            ViewSceneInputRequests.RejectNonProject(sceneInputRequests);
            var light = playback.IsSavedPose ? playback.Tracks.SavedLight : playback.Tracks.LightAt(playback.CurrentFrame);
            var shadow = playback.SelfShadowPose;
            return new(width, height, playback.CurrentFrame, playback.Frame, playback.CameraPose, light,
                shadow is { } value ? new VmdSelfShadowPose(value.Mode, value.Distance) : null)
                { ResolvedCamera = playback.Camera, CameraParentTransform = playback.CameraParentTransform };
        }
        internal override Task<OfflineFrameRunResult> RunAsync(OfflineFramePlan plan,
            Func<OfflineFrame, CancellationToken, ValueTask> capture, IProgress<OfflineFrameProgress>? progress, CancellationToken token,
            IReadOnlyList<SceneObjectRenderRequest>? sceneInputRequests = null, bool overlapCapture = false)
        {
            ViewSceneInputRequests.RejectNonProject(sceneInputRequests);
            return OfflineFrameRunner.RunPmmAsync(playback, plan, capture, progress, token, overlapCapture);
        }
        public override void Dispose() => playback.Dispose();
    }
}
