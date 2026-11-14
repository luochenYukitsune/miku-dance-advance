using Mmd.Modern.Animation;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public sealed partial class CharacterAnimationController
{
    /// <summary>Updates a fixed editing pose while preserving the active solver's body history.</summary>
    internal (OriginalStyleScene Surface, OriginalOutlineData Outline) PreviewInteractiveBones(double frame, double elapsed,
        IReadOnlyDictionary<int, BoneLocalTransform> overrides, bool ikEnabled, bool morphsEnabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(overrides);
        ValidateFrame(frame);
        if (!double.IsFinite(elapsed) || elapsed < 0 || elapsed > (softBodies is null ? 86400 : 10))
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (frozenBoneWorlds is not null)
            throw new InvalidOperationException("Seek or reset the actor before editing after a frozen attachment refresh.");
        frame = Math.Min(frame, DurationFrames);
        var prepared = PrepareMorphs(frame, morphsEnabled);
        var evaluation = Evaluation(frame, ikEnabled, morphsEnabled) with { LocalBoneOverrides = overrides };
        var pose = physics is null ? animator.Sample(frame, evaluation) : physics.Advance(frame, elapsed, evaluation, []);
        return Store(pose, frame, ikEnabled, morphsEnabled, prepared, elapsed);
    }
}
