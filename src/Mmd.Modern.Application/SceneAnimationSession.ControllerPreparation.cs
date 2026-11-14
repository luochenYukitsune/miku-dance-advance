using System.Globalization;
using System.Runtime.ExceptionServices;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed partial class SceneAnimationSession
{
    private const string ParallelControllerPreparationDegree = "Mmd.Modern.Application.ParallelControllerPreparation.Degree";
    // Observations are unset in production and never participate in eligibility decisions.
    private static Action<Guid>? ControllerPreparationObserverForTesting = null;
    private static Action<Guid, CharacterAnimationController>? ControllerPreparedObserverForTesting = null;

    private sealed class PreparedController(Guid objectId)
    {
        internal Guid ObjectId { get; } = objectId;
        internal CharacterAnimationController? Controller;
        internal ExceptionDispatchInfo? ValidationFailure;
        internal ExceptionDispatchInfo? ConstructionFailure;

        internal CharacterAnimationController TakeController()
        {
            ConstructionFailure?.Throw();
            var result = Controller ?? throw new InvalidOperationException("Controller preparation did not complete.");
            Controller = null;
            return result;
        }
    }

    private static int ControllerPreparationDegree()
    {
        int maximum = Math.Min(16, Math.Max(1, Environment.ProcessorCount));
        object? value = AppContext.GetData(ParallelControllerPreparationDegree);
        int requested = value is int number ? number : maximum;
        if (value is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            requested = parsed;
        return Math.Clamp(requested, 1, maximum);
    }

    private static bool StandardControllerModel(PmxModel model, CancellationToken token)
    {
        if (!StandardPreviewModel(model) || !StandardList(model.Vertices)
            || !StandardList(model.Faces) || !StandardList(model.RigidBodies)) return false;
        // Public PMX models can contain changed array elements or caller-provided nested lists.
        // Recheck current elements on every preparation; a prior import is not a read-safety proof.
        int index = 0;
        foreach (var vertex in model.Vertices)
        {
            if ((index++ & 1023) == 0) token.ThrowIfCancellationRequested();
            if (vertex is null || !StandardList(vertex.BoneIndices) || !StandardList(vertex.BoneWeights)
                || !StandardList(vertex.AdditionalUvs)) return false;
        }
        return true;
    }

    private static bool StandardControllerMotion(SceneActorMotion binding)
    {
        var motion = binding.Motion;
        // SceneMotionAsset privately parses its own bytes. Its read-only wrappers cannot hide
        // caller callbacks. Arbitrary ReadOnlyCollection wrappers receive no such exemption.
        if (binding.Asset is { } asset) return ReferenceEquals(motion, asset.Motion);
        // Keyframe interpolation and IK Switches copy their inputs into private arrays at
        // assignment. Their sealed keyframe types cannot override subsequent reads.
        return StandardList(motion.BoneKeyframes) && StandardList(motion.MorphKeyframes)
            && StandardList(motion.IkKeyframes) && StandardList(motion.CameraKeyframes)
            && StandardList(motion.LightKeyframes) && StandardList(motion.SelfShadowKeyframes)
            && StandardList(motion.Diagnostics);
    }

    private bool HasPerEntryPhysicsCallbacks(SceneActorMotion binding)
    {
        if (!binding.PhysicsEnabled) return false;
        bool soft = binding.Model.SoftBodies.Count != 0;
        // Match PrepareEntry's independent backend routes. A factory or backend method can
        // change a later caller-owned PMX/VMD, so that later controller must remain unbuilt.
        if (soft && !coupledSoftBodies) return true;
        return sharedPhysicsFactory is null && pmmControls is null
            && (binding.Model.RigidBodies.Count != 0 || coupledSoftBodies && soft);
    }

    private PreparedController?[]? PrepareInitialControllers(Entry[] source, int[] order, CancellationToken token)
    {
        int degree = ControllerPreparationDegree();
        if (degree < 2) return null;
        // Single-actor sessions cannot overlap work; avoid a second walk over their vertices.
        if (source.Count(entry => entry.Motion is not null) < 2) return null;
        var models = new HashSet<PmxModel>();
        using (PoseEvaluationMetrics.Measure("session.constructor.controller-read-safety"))
        {
            foreach (int index in order)
            {
                token.ThrowIfCancellationRequested();
                if (source[index].Motion is not { } binding) continue;
                // A shared source is inspected once for read behavior in this call only. Actual
                // topology validation still runs independently for every actor and every seek.
                if (binding.Model is null || binding.Motion is null
                    || (models.Add(binding.Model) && !StandardControllerModel(binding.Model, token))
                    || !StandardControllerMotion(binding) || HasPerEntryPhysicsCallbacks(binding)) return null;
            }
        }
        token.ThrowIfCancellationRequested();
        var prepared = new PreparedController?[source.Length];
        foreach (int index in order)
            if (source[index].Motion is not null) prepared[index] = new(source[index].Object.Id);
        try
        {
            using var measure = PoseEvaluationMetrics.Measure("session.constructor.parallel-prepare");
            Parallel.For(0, order.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = degree,
                TaskScheduler = TaskScheduler.Default,
            }, workIndex =>
            {
                int index = order[workIndex];
                if (prepared[index] is not { } slot) return;
                var entry = source[index]; var binding = entry.Motion!;
                try
                {
                    token.ThrowIfCancellationRequested();
                    ValidateControllerModel(entry.Object, binding);
                }
                catch (Exception error) { slot.ValidationFailure = ExceptionDispatchInfo.Capture(error); return; }
                try
                {
                    token.ThrowIfCancellationRequested();
                    slot.Controller = ConstructController(entry.Object, binding);
                    ControllerPreparedObserverForTesting?.Invoke(entry.Object.Id, slot.Controller);
                    token.ThrowIfCancellationRequested();
                }
                catch (Exception error) { slot.ConstructionFailure = ExceptionDispatchInfo.Capture(error); }
            });
            // Every worker has joined. Failures are consumed at the actor's original serial
            // turn, after earlier samples/factories, rather than at worker completion time.
            return prepared;
        }
        catch { ReleasePreparedControllers(prepared, order); throw; }
    }

    private void ReleasePreparedControllers(PreparedController?[]? prepared, int[] order)
    {
        if (prepared is null) return;
        foreach (int index in order)
            if (prepared[index] is { Controller: { } controller } slot)
            {
                slot.Controller = null;
                Release(slot.ObjectId, controller);
            }
    }

    private static void ValidateControllerModel(EditableSceneObject item, SceneActorMotion binding)
    {
        ControllerPreparationObserverForTesting?.Invoke(item.Id);
        ArgumentNullException.ThrowIfNull(binding.Model); ArgumentNullException.ThrowIfNull(binding.Motion);
        if (binding.Asset is { } asset && !ReferenceEquals(binding.Motion, asset.Motion))
            throw new ArgumentException("A persistent motion must be the motion parsed from its captured asset.");
        var snapshot = item.Source.Surface.Snapshot; var model = binding.Model;
        if (model.Vertices.Count != snapshot.Vertices.Length || model.Faces.Count * 3L != snapshot.Indices.Length || model.Materials.Count != snapshot.Materials.Count)
            throw new ArgumentException("The bound PMX must match the static source topology.");
        using (PoseEvaluationMetrics.Measure("entry.topology-validation", binding.ObjectId))
        {
            for (int i = 0; i < model.Vertices.Count; i++)
                if (model.Vertices[i].Position != snapshot.Vertices[i].Position)
                    throw new ArgumentException("The bound PMX rest vertices must match the source asset.");
            for (int i = 0; i < model.Faces.Count; i++)
                if (snapshot.Indices[3 * i] != model.Faces[i].A || snapshot.Indices[3 * i + 1] != model.Faces[i].B || snapshot.Indices[3 * i + 2] != model.Faces[i].C)
                    throw new ArgumentException("The bound PMX indices must match the source asset.");
        }
    }

    private static CharacterAnimationController ConstructController(EditableSceneObject item, SceneActorMotion binding)
    {
        using var measure = PoseEvaluationMetrics.Measure("entry.controller.construct", binding.ObjectId);
        return new(binding.Model, binding.RuntimeMotion, item.Source.Surface, item.Source.Outline!, allowEmptyMotion: true);
    }
}
