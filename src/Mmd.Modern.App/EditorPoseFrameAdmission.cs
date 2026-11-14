using System.Runtime.CompilerServices;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

/// <summary>Reference identity inside small controller presentation keys, including record-valued models.</summary>
internal readonly struct EditorPresentationReference(object? value) : IEquatable<EditorPresentationReference>
{
    private readonly object? identity = value;
    public bool Equals(EditorPresentationReference other) => ReferenceEquals(identity, other.identity);
    public override bool Equals(object? other) => other is EditorPresentationReference reference && Equals(reference);
    public override int GetHashCode() => identity is null ? 0 : RuntimeHelpers.GetHashCode(identity);
}

internal sealed record EditorPoseFrameCandidate(nint Window, int Width, int Height, long Revision,
    ViewQualityOptions Quality, object BindingIdentity, PreviewFrame? Frame, long PreparationToken,
    object? SnapshotIdentity, object? BonePickingIdentity, object? LightPickingIdentity,
    bool IsBoneDragging, bool IsLightDragging, bool PoseInputPending);

/// <summary>One last admitted target, never a history of pose payloads. Admission does not pretend
/// a dropped frame was presented or advance either picking metadata or native temporal history.</summary>
internal sealed class EditorPoseFrameAdmission
{
    private EditorPoseFrameCandidate? last;
    internal long SuppressedFrames { get; private set; }

    internal bool TryAdmit(EditorPoseFrameCandidate candidate)
    {
        if (candidate.IsBoneDragging && !candidate.IsLightDragging && candidate.PoseInputPending
            && candidate.SnapshotIdentity is not null && candidate.Frame is { } frame
            && candidate.Quality.EffectiveTemporalUpscaling.Mode == ViewTemporalUpscalingMode.Off
            && candidate.Quality.EffectiveGi.Denoiser != RayGiDenoiser.TemporalSpatial
            && frame.Scene.Options?.RayGi?.Denoiser != RayGiDenoiser.TemporalSpatial
            && last is { } previous && previous.Frame is { } previousFrame
            && ReferenceEquals(candidate.SnapshotIdentity, previous.SnapshotIdentity)
            && candidate.Window == previous.Window && candidate.Width == previous.Width && candidate.Height == previous.Height
            && candidate.Revision == previous.Revision && candidate.Quality == previous.Quality
            && ReferenceEquals(candidate.BindingIdentity, previous.BindingIdentity)
            && candidate.PreparationToken == previous.PreparationToken
            && candidate.IsBoneDragging == previous.IsBoneDragging && candidate.IsLightDragging == previous.IsLightDragging
            && Equals(candidate.BonePickingIdentity, previous.BonePickingIdentity)
            && Equals(candidate.LightPickingIdentity, previous.LightPickingIdentity)
            && SameFrame(frame, previousFrame))
        { SuppressedFrames++; return false; }
        last = candidate;
        return true;
    }

    internal void Clear() => last = null;

    private static bool SameFrame(PreviewFrame a, PreviewFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.SourceFrame != b.SourceFrame
            || a.Camera != b.Camera || a.ResolvedCamera != b.ResolvedCamera || a.CameraParentTransform != b.CameraParentTransform
            || a.Light != b.Light || a.SelfShadow != b.SelfShadow || a.TemporalState != b.TemporalState
            || !ReferenceEquals(a.MorphChannels, b.MorphChannels) || !ReferenceEquals(a.RenderingStyles, b.RenderingStyles)
            || !ReferenceEquals(a.SceneInputs, b.SceneInputs)) return false;
        var x = a.Scene; var y = b.Scene;
        if (!ReferenceEquals(x.Surface, y.Surface) || !ReferenceEquals(x.Outline, y.Outline)
            || x.OutlineOptions != y.OutlineOptions || x.Options != y.Options
            || !ReferenceEquals(x.Lighting, y.Lighting) || !x.Materials.SequenceEqual(y.Materials)) return false;
        if (ReferenceEquals(x.EditorOverlay, y.EditorOverlay)) return true;
        return x.EditorOverlay is { } first && y.EditorOverlay is { } second
            && first.Lines.SequenceEqual(second.Lines);
    }
}
