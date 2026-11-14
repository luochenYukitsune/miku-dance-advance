// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// CCD, planar/Euler limits, rollback and grant scheduling adapted from MMDIkSolver.cpp / PMXModel.cpp.
// Distributed under the MIT License; full notice: LICENSE.Saba.txt.
// System.Numerics uses row-vector matrices; quaternion Hamilton products retain Saba's order.
using System.Numerics;

namespace Mmd.Modern.Animation;

/// <summary>One sample owns all solver state. No seek or enable toggle can accumulate an earlier pose.</summary>
internal sealed class MmdConstraintEvaluator
{
    private readonly CharacterRig rig;
    private readonly Vector3[] translations;
    private readonly Quaternion[] animationRotations;
    private readonly MorphPose? morphs;
    private readonly Quaternion[] ikRotations;
    private readonly Quaternion[] appendRotations;
    private readonly Vector3[] appendTranslations;
    private readonly Matrix4x4[] locals;
    private readonly Matrix4x4[] world;
    private readonly int[] stack;
    private readonly Vector3[]? workingTranslations;
    private readonly bool[]? lateOrdinaryParents;
    private readonly bool[]? retainedIkWorlds;
    private Dictionary<int, (Vector3 Translation, Quaternion Rotation)>? physicsLocalPoses;
    private IReadOnlyDictionary<int, Matrix4x4>? outsideParents;
    private HashSet<int>? physicsDrivenBones;

    internal MmdConstraintEvaluator(CharacterRig rig, Vector3[] translations, Quaternion[] rotations, Matrix4x4[] world, MorphPose? morphs = null)
    {
        this.rig = rig; this.translations = translations; animationRotations = rotations; this.world = world;
        this.morphs = morphs?.HasBoneEffects == true ? morphs : null;
        int count = rotations.Length;
        ikRotations = new Quaternion[count]; appendRotations = new Quaternion[count]; appendTranslations = new Vector3[count];
        Array.Fill(ikRotations, Quaternion.Identity); Array.Fill(appendRotations, Quaternion.Identity);
        locals = new Matrix4x4[count]; stack = new int[count];
        for (int i = 0; i < count; i++) UpdateLocal(i);
        if (rig.MmdSchedule is not null)
        {
            workingTranslations = new Vector3[count];
            lateOrdinaryParents = new bool[count]; retainedIkWorlds = new bool[count];
            for (int i = 0; i < count; i++) workingTranslations[i] = AnimatedTranslation(i);
        }
    }

    internal void Evaluate(CharacterEvaluationOptions? options)
    {
        ValidateOptions(options);
        EvaluatePhase(false, options); EvaluatePhase(true, options);
    }

    internal void EvaluateBeforePhysics(CharacterEvaluationOptions? options)
    {
        ValidateOptions(options);
        EvaluatePhase(false, options);
    }

    internal void EvaluateAfterPhysics(CharacterEvaluationOptions? options) => EvaluatePhase(true, options);

    internal void ImportPhysicsWorlds(ReadOnlySpan<Matrix4x4> physicsWorlds, IReadOnlyCollection<int> reflectedBoneIndices)
    {
        // Saba PMXModel::UpdatePhysicsAnimation reflects all body worlds before CalcLocalTransform.
        // Preserve that complete reflected parent pose, independently of reflected-index order.
        // CalcLocalTransform writes only the local matrix: animation/IK/append components are unchanged.
        foreach (int index in reflectedBoneIndices)
        {
            int parent = rig.Bones[index].ParentIndex;
            // A late-pass world was composed with the ordinary parent. Reflect
            // through the same parent space, rather than undoing an unused attachment.
            bool ordinaryParent = lateOrdinaryParents?[index] == true;
            if (!ordinaryParent && outsideParents?.TryGetValue(index, out var external) == true)
            {
                Matrix4x4.Invert(external, out var inverseExternal);
                locals[index] = physicsWorlds[index] * inverseExternal;
                // Locals retain their regular rest offset; UpdateWorld removes it for an individual attachment.
                locals[index].Translation += rig.LocalRestPositions[index];
            }
            else if (parent < 0)
            {
                locals[index] = physicsWorlds[index];
                if (!ordinaryParent && outsideParents?.TryGetValue(-1, out var modelParent) == true)
                {
                    Matrix4x4.Invert(modelParent, out var inverseExternal);
                    locals[index] *= inverseExternal;
                }
            }
            else
            {
                if (!Matrix4x4.Invert(physicsWorlds[parent], out var inverseParent))
                    throw new ArgumentException("Physics parent world cannot be inverted.", nameof(physicsWorlds));
                locals[index] = physicsWorlds[index] * inverseParent;
            }
        }
        // Resolve every effective reflected local before reading grant sources.
        // Keep those matrices intact while extracting the separate simulated
        // bases, so neither input order nor a source's own append changes a reader.
        foreach (int index in reflectedBoneIndices)
        {
            if (rig.MmdSchedule is not null && physicsDrivenBones?.Contains(index) == true)
            {
                int parent = rig.Bones[index].ParentIndex;
                // B9F60 selects the simulated pose for active dynamic bones. Complete
                // supplies effective local poses (including morphs), so retain that
                // base separately from IK/grant changes and do not apply morphs twice.
                physicsLocalPoses ??= new();
                var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(locals[index]));
                if (rig.Bones[index].Append is { Rotate: true } append)
                {
                    // Original C3460 removes the inherited rotation before writing
                    // simulated 0x19c. Its root/parent branches use opposite sides;
                    // retain that order even when the rotations do not commute.
                    var sourceRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(locals[append.SourceBoneIndex]));
                    var inverseGrant = Quaternion.Conjugate(WeightMmdAppendRotation(sourceRotation, append.Weight));
                    rotation = Quaternion.Normalize(parent < 0 ? inverseGrant * rotation : rotation * inverseGrant);
                }
                physicsLocalPoses[index] = (locals[index].Translation - rig.LocalRestPositions[index],
                    rotation);
            }
        }
        UpdateAllWorld();
    }

    private void ValidateOptions(CharacterEvaluationOptions? options)
    {
        physicsDrivenBones = options?.PhysicsDrivenBoneIndices is { } driven ? new(driven) : null;
        if (physicsDrivenBones is not null && physicsDrivenBones.Any(index => index < 0 || index >= rig.Bones.Count))
            throw new ArgumentException("Physics-driven indices must reference rig bones.", nameof(options));
        outsideParents = options?.OutsideParents;
        if (outsideParents is not null)
            foreach (var (index, value) in outsideParents)
            {
                if (index < -1 || index >= rig.Bones.Count)
                    throw new ArgumentException("Outside parents must reference a rig bone or the whole-model slot -1.", nameof(options));
                var x = new Vector3(value.M11, value.M12, value.M13);
                var y = new Vector3(value.M21, value.M22, value.M23);
                var z = new Vector3(value.M31, value.M32, value.M33);
                if (!AnimationValidation.Finite(x) || !AnimationValidation.Finite(y) || !AnimationValidation.Finite(z)
                    || !AnimationValidation.Finite(value.Translation) || value.M14 != 0 || value.M24 != 0 || value.M34 != 0 || value.M44 != 1
                    || !Matrix4x4.Invert(value, out _))
                    throw new ArgumentException("Outside parents must be finite invertible affine transforms.", nameof(options));
            }
        if (options?.IkEnabledOverrides is { } overrides)
            foreach (int index in overrides.Keys)
                if (index < 0 || index >= rig.Bones.Count || rig.Bones[index].Ik is null)
                    throw new ArgumentException("IK overrides must reference controller bones.", nameof(options));
    }

    private void EvaluatePhase(bool afterPhysics, CharacterEvaluationOptions? options)
    {
        if (rig.MmdSchedule is { } schedule)
        {
            EvaluateMmdPhase(afterPhysics ? schedule.AfterPhysics : schedule.BeforePhysics, schedule, options);
            return;
        }
        foreach (int index in rig.DeformOrder)
            if (rig.Bones[index].AfterPhysics == afterPhysics) UpdateLocal(index);
        UpdateAllWorld();
        foreach (int index in rig.DeformOrder)
        {
            var bone = rig.Bones[index];
            if (bone.AfterPhysics != afterPhysics) continue;
            if (bone.Append is { } append) { UpdateAppend(index, append); UpdateSubtree(index); }
            if (bone.Ik is { } ik && ShouldSolveIk(index, ik, options))
            {
                Solve(index, ik); UpdateSubtree(index);
            }
        }
        UpdateAllWorld();
    }

    private void EvaluateMmdPhase(int[][] layers, MmdBoneSchedule schedule, CharacterEvaluationOptions? options)
    {
        // B9F60 prepares working translation again for each phase, even for bones
        // outside that phase. Earlier local matrices remain available to rotation
        // grants, but their committed translation grants must not leak into it.
        for (int i = 0; i < rig.Bones.Count; i++) workingTranslations![i] = AnimatedTranslation(i);
        foreach (var indices in layers)
            foreach (int index in indices) lateOrdinaryParents![index] = false;
        UpdateAllWorld();
        foreach (var indices in layers)
        {
            // BA200: prepare affected locals, commit ordinary locals, refresh worlds, IK, rebuild.
            // A prepare translation is temporary; later readers still see its uncommitted working delta.
            foreach (int index in indices)
                if (schedule.RebuildAfterIk[index]) UpdateMmdLocal(index, false);
            foreach (int index in indices)
                if (!schedule.RebuildAfterIk[index]) UpdateMmdLocal(index, true);
            UpdateAllWorld();
            foreach (int index in indices)
                if (rig.Bones[index].Ik is { } ik && ShouldSolveIk(index, ik, options))
                {
                    Solve(index, ik); UpdateSubtree(index);
                }
            foreach (int index in indices)
                if (schedule.RebuildAfterIk[index] && !schedule.IsIkLink[index])
                {
                    UpdateMmdLocal(index, true);
                    // BA200's late pass uses the ordinary parent, bypassing both
                    // explicit and whole-model outside-parent snapshot composition.
                    lateOrdinaryParents![index] = true; UpdateWorld(index);
                }
        }
        // Preserve the existing physics/external-parent propagation contract for untouched descendants.
        UpdateAllWorld();
    }

    private void UpdateMmdLocal(int index, bool commitTranslation)
    {
        retainedIkWorlds![index] = false;
        if (rig.Bones[index].Append is { } append)
        {
            int source = append.SourceBoneIndex;
            if (append.Rotate)
            {
                // Original9.32 BA200 reads the evaluated local matrix for both raw
                // LocalAppend flag values, including prior append, IK and physics.
                // The explicitly selected Saba schedule keeps its separate Local policy.
                var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(locals[source]));
                appendRotations[index] = WeightMmdAppendRotation(rotation, append.Weight);
            }
            if (append.Translate)
                appendTranslations[index] = workingTranslations![source] * append.Weight;
        }
        UpdateLocal(index);
        if (commitTranslation) workingTranslations![index] = AnimatedTranslation(index) + appendTranslations[index];
    }

    private static Quaternion WeightMmdAppendRotation(Quaternion rotation, float weight)
    {
        // Original BA200 preserves D3DX matrix extraction's sign. Its negative-W branch
        // subtracts the literal 3.14f, so hemisphere-canonicalizing Slerp changes the result.
        float w = Math.Clamp(rotation.W, -1f, 1f);
        float halfAngle = MathF.Acos(w);
        if (w < 0f) halfAngle -= 3.14f;
        float length = MathF.Sqrt(rotation.X * rotation.X + rotation.Y * rotation.Y + rotation.Z * rotation.Z);
        if (halfAngle == 0f || length < 1.1920929e-7f) return Quaternion.Identity;
        float weightedAngle = halfAngle * weight;
        float scale = MathF.Sin(weightedAngle) / length;
        return new(rotation.X * scale, rotation.Y * scale, rotation.Z * scale, MathF.Cos(weightedAngle));
    }

    private bool ShouldSolveIk(int controller, IkDefinition ik, CharacterEvaluationOptions? options)
        => (options?.EnableIk ?? true)
            && !(options?.IkEnabledOverrides?.TryGetValue(controller, out bool enabled) == true && !enabled)
            && !(physicsDrivenBones?.Contains(rig.Bones[ik.TargetBoneIndex].ParentIndex) ?? false);

    private void UpdateLocal(int index)
    {
        var rotation = AnimatedRotation(index);
        if (rig.IsIkLink[index]) rotation = ikRotations[index] * rotation;
        if (rig.Bones[index].Append?.Rotate == true) rotation *= appendRotations[index];
        var translation = rig.LocalRestPositions[index] + AnimatedTranslation(index);
        if (rig.Bones[index].Append?.Translate == true) translation += appendTranslations[index];
        locals[index] = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(translation);
    }

    // Keep VMD and morph base components separate. IK removes the complete animated rotation,
    // while append reads the effective source transform, including the morph base.
    private Quaternion AnimatedRotation(int index) => physicsLocalPoses?.TryGetValue(index, out var pose) == true
        ? pose.Rotation : morphs?.ApplyBoneRotation(index, animationRotations[index]) ?? animationRotations[index];
    private Vector3 AnimatedTranslation(int index) => physicsLocalPoses?.TryGetValue(index, out var pose) == true
        ? pose.Translation : morphs?.ApplyBoneTranslation(index, translations[index]) ?? translations[index];

    // CCD's inverse world includes the evaluated append. Apply and remove the same complete
    // base as UpdateLocal; omitting a noncommuting append changes the correction's local axis.
    private Quaternion EffectiveBaseRotation(int index)
    {
        var rotation = AnimatedRotation(index);
        return rig.Bones[index].Append?.Rotate == true ? rotation * appendRotations[index] : rotation;
    }

    private void UpdateAllWorld()
    {
        foreach (int i in rig.EvaluationOrder) UpdateWorld(i);
    }

    private void UpdateWorld(int index)
    {
        if (retainedIkWorlds?[index] == true) return;
        int parent = rig.Bones[index].ParentIndex;
        bool ordinaryParent = lateOrdinaryParents?[index] == true;
        if (!ordinaryParent && outsideParents?.TryGetValue(index, out var external) == true)
        {
            // Original MMD BA200: W_s=T(b_s)*L_s*T(-b_s)*W_target. The bound bone
            // replaces its ordinary parent; our locals encode R*T(restOffset+delta).
            var relative = locals[index]; relative.Translation -= rig.LocalRestPositions[index];
            world[index] = relative * external;
        }
        else
        {
            world[index] = parent < 0 ? locals[index] : locals[index] * world[parent];
            if (!ordinaryParent && parent < 0 && outsideParents?.TryGetValue(-1, out var modelParent) == true) world[index] *= modelParent;
        }
    }

    private void UpdateSubtree(int index)
    {
        int count = 0; stack[count++] = index;
        while (count > 0)
        {
            int current = stack[--count]; UpdateWorld(current);
            for (int child = rig.FirstChild[current]; child >= 0; child = rig.NextSibling[child]) stack[count++] = child;
        }
    }

    private void UpdateAppend(int index, AppendTransform append)
    {
        int source = append.SourceBoneIndex;
        if (append.Rotate)
        {
            var rotation = AnimatedRotation(source);
            if (rig.IsIkLink[source]) rotation = ikRotations[source] * rotation;
            // Original BA200 reads the source's evaluated local rotation, not just its grant component.
            // Keep the explicit local-grant branch separate until its original semantics are verified.
            if (!append.Local && rig.Bones[source].Append?.Rotate == true) rotation *= appendRotations[source];
            appendRotations[index] = Quaternion.Slerp(Quaternion.Identity, rotation, append.Weight);
        }
        if (append.Translate)
        {
            // Original BA200 uses the source working animation delta, including an already evaluated grant.
            // Saba's GetTranslate()-GetInitialTranslate() accidentally discards VMD's separate animation delta.
            var delta = AnimatedTranslation(source);
            if (!append.Local && rig.Bones[source].Append?.Translate == true) delta += appendTranslations[source];
            appendTranslations[index] = delta * append.Weight;
        }
        UpdateLocal(index);
    }

    private float StepLimit(IkDefinition ik, int ordinal) => ik.AnglePolicy == IkAnglePolicy.Saba
        ? ik.AngleLimit : 4f * (ordinal + 1) * ik.AngleLimit;

    private void Solve(int controller, IkDefinition ik)
    {
        int count = ik.Links.Count;
        if (count == 0 || ik.Iterations == 0) return;
        Vector3 mmdGoal = world[controller].Translation;
        var previousAngles = new Vector3[count]; var planeAngles = new float[count]; var saved = new Quaternion[count];
        for (int i = 0; i < count; i++)
        {
            int node = ik.Links[i].BoneIndex;
            ikRotations[node] = saved[i] = Quaternion.Identity; UpdateLocal(node);
            // BA200 retains worlds until an angle correction refreshes this chain. Refreshing
            // all descendants here changes the first input of a subsequent foot/toe IK.
            if (ik.AnglePolicy == IkAnglePolicy.Saba) UpdateSubtree(node);
            planeAngles[i] = InitialPlaneAngle(ik.Links[i], EffectiveBaseRotation(node));
        }
        if (ik.AnglePolicy == IkAnglePolicy.Mmd) UpdateLocal(ik.TargetBoneIndex);
        // BA200 mode3 skips dynamic chain-link world recomposition in the
        // inner loop. Their local IK rotation may change, while descendants
        // continue solving against the retained simulated world transform.
        if (retainedIkWorlds is not null && physicsLocalPoses is not null)
            foreach (var link in ik.Links)
                if (physicsLocalPoses.ContainsKey(link.BoneIndex)) retainedIkWorlds[link.BoneIndex] = true;
        float bestDistance = float.MaxValue;
        for (int iteration = 0; iteration < ik.Iterations; iteration++)
        {
            Vector3 goal = ik.AnglePolicy == IkAnglePolicy.Mmd ? mmdGoal : world[controller].Translation;
            for (int ordinal = 0; ordinal < count; ordinal++)
            {
                var link = ik.Links[ordinal]; int node = link.BoneIndex;
                if (node == ik.TargetBoneIndex) continue;
                if (!Matrix4x4.Invert(world[node], out var inverse)) continue;
                int plane = PlaneAxis(link);
                bool projectPlane = ik.AnglePolicy == IkAnglePolicy.Mmd && plane >= 0
                    && iteration < (ik.Iterations >> 1);
                bool sabaPlane = ik.AnglePolicy == IkAnglePolicy.Saba && plane >= 0;
                bool fixedAxis = ik.AnglePolicy == IkAnglePolicy.Mmd && rig.Bones[node].FixedAxis.HasValue
                    && !projectPlane;
                var targetVector = Vector3.Transform(world[ik.TargetBoneIndex].Translation, inverse);
                // Saba's planar helper samples the controller again for each link; its general
                // helper holds the iteration's goal fixed while the chain is updated.
                var goalVector = Vector3.Transform(sabaPlane ? world[controller].Translation : goal, inverse);
                if (!TryNormalize(targetVector, out targetVector) || !TryNormalize(goalVector, out goalVector)) continue;
                if (ik.AnglePolicy == IkAnglePolicy.Mmd && Vector3.DistanceSquared(targetVector, goalVector) < 1e-7f)
                    return; // BA200 BB87E: finish the entire controller once the directions coincide.
                float cosine = Math.Clamp(Vector3.Dot(targetVector, goalVector), -1, 1);
                var correctionAxis = Vector3.Cross(targetVector, goalVector);
                float angle = MathF.Acos(cosine);
                float unconstrainedAngle = angle;
                float limit = StepLimit(ik, ordinal); angle = MathF.Min(angle, limit);
                if (sabaPlane)
                {
                    SolvePlane(node, link, iteration, plane, targetVector, goalVector, angle, ref planeAngles[ordinal]);
                }
                else
                {
                    if (angle < MathF.PI / 180 * .001f) continue;
                    Vector3 axis = correctionAxis;
                    if (!TryNormalize(axis, out axis))
                    {
                        // The original cross product is undefined at exactly 180 degrees; choose a stable orthogonal axis.
                        var least = Vector3.Abs(targetVector);
                        var basis = least.X <= least.Y && least.X <= least.Z ? Vector3.UnitX
                            : least.Y <= least.Z ? Vector3.UnitY : Vector3.UnitZ;
                        if (!TryNormalize(Vector3.Cross(targetVector, basis), out axis)) continue;
                    }
                    var baseRotation = EffectiveBaseRotation(node);
                    var totalRotation = Quaternion.Normalize(ikRotations[node] * baseRotation);
                    Quaternion rotation;
                    if (projectPlane)
                    {
                        // Original PMX single-axis projection is limited to the first floor(N/2)
                        // iterations. The later half uses the general correction and Euler limits.
                        var direction = plane == 0 ? Vector3.UnitX : plane == 1 ? Vector3.UnitY : Vector3.UnitZ;
                        var axisInParent = Vector3.Transform(axis, totalRotation);
                        float signedAngle = Vector3.Dot(axisInParent, direction) < 0 ? -angle : angle;
                        rotation = Quaternion.CreateFromAxisAngle(direction, signedAngle) * totalRotation;
                    }
                    else if (fixedAxis)
                    {
                        // BA200 projects the normalized correction onto the fixed axis in parent
                        // coordinates, then caps the signed angle (not cap-then-project).
                        var axisInParent = Vector3.Transform(axis, totalRotation);
                        var fixedDirection = rig.Bones[node].FixedAxis!.Value;
                        float projectedAngle = Math.Clamp(unconstrainedAngle * Vector3.Dot(axisInParent, fixedDirection), -limit, limit);
                        rotation = Quaternion.CreateFromAxisAngle(fixedDirection, projectedAngle) * totalRotation;
                    }
                    else rotation = ikRotations[node] * baseRotation * Quaternion.CreateFromAxisAngle(axis, angle);
                    if (link.MinimumAngles is { } min && link.MaximumAngles is { } max)
                    {
                        if (ik.AnglePolicy == IkAnglePolicy.Mmd)
                            rotation = MmdIkRotationLimits.Constrain(rotation, min, max, iteration, ik.Iterations);
                        else
                        {
                            // Preserve the pinned Saba oracle's continuous XYZ and per-axis step cap.
                            var xyz = Decompose(Matrix4x4.CreateFromQuaternion(rotation), previousAngles[ordinal]);
                            xyz = Vector3.Clamp(xyz, min, max);
                            xyz = Vector3.Clamp(xyz - previousAngles[ordinal], new(-limit), new(limit)) + previousAngles[ordinal];
                            previousAngles[ordinal] = xyz;
                            rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, xyz.X)
                                * Quaternion.CreateFromAxisAngle(Vector3.UnitY, xyz.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, xyz.Z);
                        }
                    }
                    ikRotations[node] = Quaternion.Normalize(rotation * Quaternion.Inverse(baseRotation));
                    UpdateLocal(node);
                    if (ik.AnglePolicy == IkAnglePolicy.Mmd)
                    {
                        // BA200 BCC60-BCE93 refreshes only the affected chain prefix and its
                        // effector. Ordinary children retain their world until their scheduled pass.
                        for (int prior = ordinal; prior >= 0; prior--) UpdateWorld(ik.Links[prior].BoneIndex);
                        UpdateWorld(ik.TargetBoneIndex);
                    }
                    else UpdateSubtree(node);
                }
            }
            // Original MMD must reach the later unrestricted half even if a projected
            // iteration temporarily increases distance. Keep Saba's best-pose rollback only there.
            if (ik.AnglePolicy == IkAnglePolicy.Mmd) continue;
            float distance = Vector3.Distance(world[ik.TargetBoneIndex].Translation, world[controller].Translation);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                for (int i = 0; i < count; i++) saved[i] = ikRotations[ik.Links[i].BoneIndex];
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    int node = ik.Links[i].BoneIndex; ikRotations[node] = saved[i]; UpdateLocal(node); UpdateSubtree(node);
                }
                break;
            }
        }
    }

    private void SolvePlane(int node, IkLinkDefinition link, int iteration, int component,
        Vector3 target, Vector3 goal, float angle, ref float accumulatedAngle)
    {
        Vector3 axis = component == 0 ? Vector3.UnitX : component == 1 ? Vector3.UnitY : Vector3.UnitZ;
        float positive = Vector3.Dot(Vector3.Transform(target, Quaternion.CreateFromAxisAngle(axis, angle)), goal);
        float negative = Vector3.Dot(Vector3.Transform(target, Quaternion.CreateFromAxisAngle(axis, -angle)), goal);
        float next = accumulatedAngle + (positive > negative ? angle : -angle);
        float min = Component(link.MinimumAngles!.Value, component), max = Component(link.MaximumAngles!.Value, component);
        if (iteration == 0 && (next < min || next > max))
        {
            if (-next > min && -next < max) next *= -1;
            else
            {
                float middle = (min + max) * .5f;
                if (MathF.Abs(middle - next) > MathF.Abs(middle + next)) next *= -1;
            }
        }
        accumulatedAngle = Math.Clamp(next, min, max);
        ikRotations[node] = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, accumulatedAngle) * Quaternion.Inverse(EffectiveBaseRotation(node)));
        UpdateLocal(node); UpdateSubtree(node);
    }

    private static float InitialPlaneAngle(IkLinkDefinition link, Quaternion rotation)
    {
        int component = PlaneAxis(link);
        if (component < 0) return 0;
        rotation = Quaternion.Normalize(rotation);
        var vector = new Vector3(rotation.X, rotation.Y, rotation.Z);
        var offAxis = vector;
        if (component == 0) offAxis.X = 0;
        else if (component == 1) offAxis.Y = 0;
        else offAxis.Z = 0;
        // A valid animated hinge bend is the starting total angle, not an IK delta from zero.
        // Non-hinge or out-of-range bases retain the existing projection/limit path.
        if (offAxis.LengthSquared() > 1e-12f) return 0;
        // Reduce in double precision: float +/-pi followed by +/-tau can round an equivalent
        // q/-q representation outside a hinge endpoint. Snap only seed conversion roundoff;
        // every accepted angle is still clamped to the original, unchanged limits.
        double angle = Math.IEEERemainder(2 * Math.Atan2(Component(vector, component), rotation.W), Math.Tau);
        float minimum = Component(link.MinimumAngles!.Value, component), maximum = Component(link.MaximumAngles!.Value, component);
        for (int offset = 0; offset < 3; offset++)
        {
            double candidate = angle + (offset == 1 ? Math.Tau : offset == 2 ? -Math.Tau : 0);
            if (candidate >= minimum - 1e-6 && candidate <= maximum + 1e-6)
                return Math.Clamp((float)candidate, minimum, maximum);
        }
        return 0;
    }

    private static int PlaneAxis(IkLinkDefinition link)
    {
        if (link.MinimumAngles is not { } min || link.MaximumAngles is not { } max) return -1;
        // Require both bounds of each other axis to be zero. Saba's OR misclassifies half-zero multi-axis ranges.
        if ((min.X != 0 || max.X != 0) && min.Y == 0 && max.Y == 0 && min.Z == 0 && max.Z == 0) return 0;
        if ((min.Y != 0 || max.Y != 0) && min.X == 0 && max.X == 0 && min.Z == 0 && max.Z == 0) return 1;
        if ((min.Z != 0 || max.Z != 0) && min.X == 0 && max.X == 0 && min.Y == 0 && max.Y == 0) return 2;
        return -1;
    }

    private static float Component(Vector3 vector, int component) => component == 0 ? vector.X : component == 1 ? vector.Y : vector.Z;
    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        float length = value.LengthSquared();
        if (!float.IsFinite(length) || length < 1e-20f) { normalized = default; return false; }
        normalized = value / MathF.Sqrt(length); return true;
    }

    private static Vector3 Decompose(Matrix4x4 matrix, Vector3 before)
    {
        Vector3 result; float sy = Math.Clamp(-matrix.M13, -1, 1);
        if (1 - MathF.Abs(sy) < 1e-6f)
        {
            result.Y = MathF.Asin(sy);
            if (MathF.Abs(MathF.Sin(before.X)) < MathF.Abs(MathF.Sin(before.Z)))
            {
                bool positive = MathF.Cos(before.X) > 0;
                result.X = positive ? 0 : MathF.PI; result.Z = MathF.Asin(Math.Clamp(positive ? -matrix.M21 : matrix.M21, -1, 1));
            }
            else
            {
                bool positive = MathF.Cos(before.Z) > 0;
                result.Z = positive ? 0 : MathF.PI; result.X = MathF.Asin(Math.Clamp(positive ? -matrix.M32 : matrix.M32, -1, 1));
            }
        }
        else result = new(MathF.Atan2(matrix.M23, matrix.M33), MathF.Asin(sy), MathF.Atan2(matrix.M12, matrix.M11));
        float bestError = Difference(result, before);
        var original = result;
        for (int xSign = 1; xSign >= -1; xSign -= 2)
            for (int ySign = 1; ySign >= -1; ySign -= 2)
                for (int zSign = 1; zSign >= -1; zSign -= 2)
                {
                    var alternative = new Vector3(original.X + xSign * MathF.PI, ySign * MathF.PI - original.Y, original.Z + zSign * MathF.PI);
                    float error = Difference(alternative, before);
                    if (error < bestError) { bestError = error; result = alternative; }
                }
        return result;
    }

    private static float Difference(Vector3 a, Vector3 b) => MathF.Abs(MathF.IEEERemainder(a.X - b.X, MathF.Tau))
        + MathF.Abs(MathF.IEEERemainder(a.Y - b.Y, MathF.Tau)) + MathF.Abs(MathF.IEEERemainder(a.Z - b.Z, MathF.Tau));
}
