// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// Interpolation and BDEF/SDEF adapted from Saba VMDAnimation.cpp / PMXModel.cpp,
// commit 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332.
// Copyright(c) 2016-2017 benikabocha. Distributed under the MIT License.
// Full license: third_party/saba/LICENCE.txt. Row-vector matrices, no OpenGL Z flip.
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Animation;

/// <summary>Caller-owned sample arrays; no array is reused by subsequent samples.</summary>
public sealed record CharacterSample
{
    private readonly SceneVertex[]? vertices;
    private readonly Lazy<SceneVertex[]>? deferredVertices;
    public SceneVertex[] Vertices => vertices ?? deferredVertices!.Value;
    public Matrix4x4[] BoneWorldMatrices { get; }
    public Matrix4x4[] SkinMatrices { get; }
    /// <summary>Deferred skinning input. Vertices then lazily copies the deformation's exact CPU reference.</summary>
    public SceneDeformation? Deformation { get; }
    public CharacterSample(SceneVertex[] Vertices, Matrix4x4[] BoneWorldMatrices, Matrix4x4[] SkinMatrices)
    { vertices = Vertices; this.BoneWorldMatrices = BoneWorldMatrices; this.SkinMatrices = SkinMatrices; }
    internal CharacterSample(SceneDeformation deformation, Matrix4x4[] boneWorldMatrices, Matrix4x4[] skinMatrices)
    {
        Deformation = deformation; BoneWorldMatrices = boneWorldMatrices; SkinMatrices = skinMatrices;
        deferredVertices = new(deformation.CopyReferenceVertices, LazyThreadSafetyMode.ExecutionAndPublication);
    }
}

public sealed class CharacterAnimator
{
    private const int SkinningBlockSize = 4096;
    private const int ParallelSkinningThreshold = 32768;
    private const long MaximumParallelQdefWorkspaceBytes = 4L * 1024 * 1024;
    private const string ParallelSkinningDegree = "Mmd.Modern.Animation.ParallelSkinning.Degree";
    private readonly CharacterRig rig;
    private readonly BoneTrack?[] tracksByBone;
    private readonly SegmentCurveOwnership ownership;
    private readonly int? skinningDegreeOverride;
    // Unset by the public constructor; observations never affect eligibility or
    // retain a sample's arrays. Read-only per instance so concurrent samples do
    // not introduce mutable animator scratch or last-sample diagnostics.
    private readonly Action<int, int, int>? skinningRangeObserverForTesting;
    private static readonly ConditionalWeakTable<MorphPose, SceneMorphDeltas> morphDeltas = new();
    private MmdConstraintEvaluator? pooledEvaluator;
    private volatile bool deferVertexSkinning;
    public IReadOnlyList<string> Diagnostics { get; }
    /// <summary>Selected by the owner only between samples. Samples then carry a deformation input instead of
    /// skinned vertices; bones, IK, physics and morph weights are still evaluated here.</summary>
    internal bool DeferVertexSkinning { get => deferVertexSkinning; set => deferVertexSkinning = value; }

    internal CharacterAnimator(CharacterRig rig, MotionClip clip, int skinningDegree,
        Action<int, int, int>? skinningRangeObserverForTesting = null) : this(rig, clip)
    {
        skinningDegreeOverride = skinningDegree;
        this.skinningRangeObserverForTesting = skinningRangeObserverForTesting;
    }

    public CharacterAnimator(CharacterRig rig, MotionClip clip)
    {
        ArgumentNullException.ThrowIfNull(rig); ArgumentNullException.ThrowIfNull(clip);
        this.rig = rig; ownership = clip.CurveOwnership; tracksByBone = new BoneTrack?[rig.Bones.Count];
        var bones = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < rig.Bones.Count; i++) bones.Add(rig.Bones[i].Name, i);
        var diagnostics = new List<string>();
        foreach (var track in clip.Tracks)
        {
            if (bones.TryGetValue(track.BoneName, out int index)) tracksByBone[index] = track;
            else diagnostics.Add($"Motion track '{track.BoneName}' has no matching rig bone.");
        }
        foreach (var feature in Enum.GetValues<UnsupportedRigFeatures>())
            if (feature != UnsupportedRigFeatures.None && rig.UnsupportedFeatures.HasFlag(feature))
                diagnostics.Add($"{feature} is not evaluated; this is a bone-only motion preview.");
        if(rig.HasConstraints)
            diagnostics.Add(rig.EvaluationSchedule == BoneEvaluationSchedule.Saba
                ? "Constraint schedule uses Saba stable deformation-layer order with per-node append/IK; original MMD's separate layer passes are not selected."
                : "Constraint schedule uses PMX layer preparation and post-IK rebuild passes; direct-parent dynamic IK suppression, simulated working poses, mode-3 inner-link refresh and selected late external-parent branches are verified within bounded original-function cases; broader original combinations remain unverified.");
        if(rig.Bones.Any(b=>b.Append?.Local==true))
            diagnostics.Add(rig.EvaluationSchedule == BoneEvaluationSchedule.Saba
                ? "Local append uses the explicitly selected Saba local/nonlocal policy."
                : "Local append uses original 9.32's evaluated local source for both flag values; bounded append-chain original-function comparisons are verified, not full original application playback.");
        foreach (var bone in rig.Bones)
            if (bone.Ik is { } ik && ik.Links.Any(link => link.MinimumAngles is { } min && link.MaximumAngles is { } max
                && (min.X != 0 || max.X != 0 ? 1 : 0) + (min.Y != 0 || max.Y != 0 ? 1 : 0) + (min.Z != 0 || max.Z != 0 ? 1 : 0) > 1))
                diagnostics.Add(ik.AnglePolicy == IkAnglePolicy.Saba
                    ? $"IK '{bone.Name}' uses Saba continuous XYZ Euler limits; original MMD's alternative Euler orders and iteration-half reflection are not selected."
                    : $"IK '{bone.Name}' uses original-calibrated PMX multi-axis Euler orders and iteration-half reflection; full original IK scheduling and single-axis behavior remain unverified.");
        Diagnostics = diagnostics.AsReadOnly();
    }

    /// <summary>Absolute, stateless evaluation. Finite negative/late frames clamp to track endpoints.</summary>
    public CharacterSample Sample(double frame, CharacterEvaluationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SampleCore(frame, options);
    }

    public CharacterSample Sample(double frame) => SampleCore(frame, null);

    /// <summary>Stateless animation/IK/outside-parent evaluation with exactly the ordinary sample's
    /// arithmetic, omitting only vertex skinning. The returned bone worlds are caller-owned.</summary>
    public Matrix4x4[] SampleBoneWorldMatrices(double frame, CharacterEvaluationOptions? options = null)
        => SampleWorldPalette(frame, options).World;

    /// <summary>Skins an owned final pose without reevaluating animation, IK or a physics solver.</summary>
    public CharacterSample SkinWorldPose(ReadOnlySpan<Matrix4x4> boneWorldMatrices, MorphPose? morphs = null)
    {
        morphs?.ValidateRig(rig);
        var (world, skin) = SkinPalette(boneWorldMatrices);
        return Skin(world, skin, morphs);
    }

    /// <summary>Exact per-vertex affine position maps for the supplied bone worlds. Vertex morph deltas
    /// precede these maps; SDEF anchors and QDEF hemisphere blending match ordinary skinning.</summary>
    public Matrix4x4[] CalculateVertexSkinTransforms(ReadOnlySpan<Matrix4x4> boneWorldMatrices)
    {
        var (world, skin) = SkinPalette(boneWorldMatrices);
        var rotations = SdefWorldRotations(world);
        var qdef = rig.HasQdef ? new QdefSkinning(skin) : null;
        var result = new Matrix4x4[rig.VertexArray.Length];
        if (rig.HasCompactBdefBindings)
        {
            var compact = rig.CompactBdefBindings;
            for (int i = 0; i < result.Length; i++)
            {
                ref readonly var binding = ref compact[i]; Matrix4x4 matrix = default;
                for (int slot = 0; slot < 4; slot++)
                {
                    float weight = binding.Weight(slot); if (weight == 0) continue;
                    matrix += skin[binding.Bone(slot)] * weight;
                }
                matrix.M14 = matrix.M24 = matrix.M34 = 0; matrix.M44 = 1; result[i] = matrix;
            }
            return result;
        }
        var bindings = rig.FullBindings;
        for (int i = 0; i < result.Length; i++)
        {
            var binding = bindings[i]; Matrix4x4 matrix;
            if (binding.Kind == SkinningKind.Qdef)
            {
                var (rotation, translation) = qdef!.Blend(binding);
                matrix = Matrix4x4.CreateFromQuaternion(rotation); matrix.Translation = translation;
            }
            else if (binding.Kind == SkinningKind.Sdef)
            {
                var (rotation, offset) = SdefTransform(i, binding, skin, rotations);
                matrix = Matrix4x4.CreateFromQuaternion(rotation);
                matrix.Translation = offset - Vector3.Transform(binding.SdefC, rotation);
            }
            else
            {
                matrix = default;
                for (int slot = 0; slot < 4; slot++)
                {
                    float weight = binding.Weight(slot); if (weight == 0) continue;
                    matrix += skin[binding.Bone(slot)] * weight;
                }
            }
            // Transform(Vector3, Matrix4x4) is affine; its ignored fourth column must not
            // turn tiny weight-sum roundoff into a projective inverse during rebasing.
            matrix.M14 = matrix.M24 = matrix.M34 = 0; matrix.M44 = 1; result[i] = matrix;
        }
        return result;
    }

    private (Matrix4x4[] World, Matrix4x4[] Skin) SkinPalette(ReadOnlySpan<Matrix4x4> boneWorldMatrices)
    {
        if (boneWorldMatrices.Length != rig.Bones.Count)
            throw new ArgumentException("Final pose must contain every rig bone.", nameof(boneWorldMatrices));
        var world = boneWorldMatrices.ToArray(); var skin = new Matrix4x4[world.Length];
        for (int i = 0; i < world.Length; i++)
        {
            var value = world[i];
            if (!AnimationValidation.Finite(new Vector3(value.M11,value.M12,value.M13))
                || !AnimationValidation.Finite(new Vector3(value.M21,value.M22,value.M23))
                || !AnimationValidation.Finite(new Vector3(value.M31,value.M32,value.M33))
                || !AnimationValidation.Finite(value.Translation) || value.M14 != 0 || value.M24 != 0 || value.M34 != 0 || value.M44 != 1
                || !Matrix4x4.Invert(value, out _))
                throw new ArgumentException("Final bone worlds must be finite invertible affine matrices.", nameof(boneWorldMatrices));
            skin[i] = rig.InverseBindMatrices[i] * value;
        }
        return (world, skin);
    }

    private MmdConstraintEvaluator RentEvaluator()
        => Interlocked.Exchange(ref pooledEvaluator, null) ?? new MmdConstraintEvaluator(rig);

    internal void ReturnEvaluator(MmdConstraintEvaluator? evaluator)
    {
        if (evaluator is null) return;
        evaluator.Release();
        Interlocked.CompareExchange(ref pooledEvaluator, evaluator, null);
    }

    /// <summary>Evaluate animation through the before-physics phase. Complete the owned sample after the physics step.</summary>
    public CharacterPhysicsSample BeginPhysicsSample(double frame, CharacterEvaluationOptions? options = null)
    {
        if (!double.IsFinite(frame)) throw new ArgumentOutOfRangeException(nameof(frame));
        options?.Morphs?.ValidateRig(rig);
        var localBoneOverrides = CopyNormalizedLocalBoneOverrides(options?.LocalBoneOverrides);
        if (options is not null)
        {
            IReadOnlyDictionary<int, bool>? ikOverrides = options.IkEnabledOverrides is { Count: > 0 } overrides
                ? new Dictionary<int, bool>(overrides) : null;
            IReadOnlyDictionary<int, Matrix4x4>? outsideParents = options.OutsideParents is { Count: > 0 } op
                ? new Dictionary<int, Matrix4x4>(op) : null;
            IReadOnlyDictionary<int, OutsideParentTrackWindow>? trackWindows = options.OutsideParentTrackWindows is { Count: > 0 } tw
                ? new Dictionary<int, OutsideParentTrackWindow>(tw) : null;
            // ValidateOptions copies PhysicsDrivenBoneIndices into the evaluator's own HashSet before returning.
            if (!ReferenceEquals(ikOverrides, options.IkEnabledOverrides)
                || !ReferenceEquals(outsideParents, options.OutsideParents)
                || !ReferenceEquals(localBoneOverrides, options.LocalBoneOverrides)
                || !ReferenceEquals(trackWindows, options.OutsideParentTrackWindows))
                options = options with
                {
                    IkEnabledOverrides = ikOverrides,
                    OutsideParents = outsideParents,
                    LocalBoneOverrides = localBoneOverrides,
                    OutsideParentTrackWindows = trackWindows,
                };
        }
        var world = new Matrix4x4[rig.Bones.Count];
        var evaluator = RentEvaluator();
        try
        {
            var translations = evaluator.Translations; var rotations = evaluator.AnimationRotations;
            var trackWindows = options?.OutsideParentTrackWindows;
            for (int i = 0; i < rotations.Length; i++)
            {
                OutsideParentTrackWindow? window = trackWindows is not null && trackWindows.TryGetValue(i, out var found) ? found : null;
                (translations[i], rotations[i]) = EvaluateTrack(tracksByBone[i], frame, window);
            }
            ApplyLocalBoneOverrides(localBoneOverrides, translations, rotations);
            evaluator.EvaluateBeforePhysics(world, options?.Morphs, options);
            return new(this, evaluator, world, options);
        }
        catch
        {
            ReturnEvaluator(evaluator);
            throw;
        }
    }

    private CharacterSample SampleCore(double frame, CharacterEvaluationOptions? options)
    {
        var (world, skin) = SampleWorldPalette(frame, options);
        return Skin(world, skin, options?.Morphs);
    }

    private (Matrix4x4[] World, Matrix4x4[] Skin) SampleWorldPalette(double frame, CharacterEvaluationOptions? options)
    {
        if (!double.IsFinite(frame)) throw new ArgumentOutOfRangeException(nameof(frame));
        var morphs = options?.Morphs;
        morphs?.ValidateRig(rig);
        var localBoneOverrides = CopyNormalizedLocalBoneOverrides(options?.LocalBoneOverrides);
        var world = new Matrix4x4[rig.Bones.Count]; var skin = new Matrix4x4[rig.Bones.Count];
        var trackWindows = options?.OutsideParentTrackWindows;
        if (rig.HasConstraints || options?.OutsideParents?.Count > 0 || options?.PhysicsDrivenBoneIndices?.Count > 0
            || options?.IkEnabledOverrides?.Count > 0 || trackWindows?.Count > 0)
        {
            var evaluator = RentEvaluator();
            try
            {
                var translations = evaluator.Translations; var rotations = evaluator.AnimationRotations;
                for (int i = 0; i < rotations.Length; i++)
                {
                    OutsideParentTrackWindow? window = trackWindows is not null && trackWindows.TryGetValue(i, out var found) ? found : null;
                    (translations[i], rotations[i]) = EvaluateTrack(tracksByBone[i], frame, window);
                }
                ApplyLocalBoneOverrides(localBoneOverrides, translations, rotations);
                evaluator.Evaluate(world, morphs, options);
                for (int i = 0; i < skin.Length; i++) skin[i] = rig.InverseBindMatrices[i] * world[i];
            }
            finally
            {
                ReturnEvaluator(evaluator);
            }
        }
        else foreach (int index in rig.EvaluationOrder)
        {
            var (translation, rotation) = EvaluateTrack(tracksByBone[index], frame);
            if (localBoneOverrides is not null && localBoneOverrides.TryGetValue(index, out var localOverride))
                (translation, rotation) = (localOverride.Translation, localOverride.Rotation);
            if (morphs?.HasBoneEffects == true)
            {
                translation = morphs.ApplyBoneTranslation(index, translation);
                var combined = morphs.ApplyBoneRotation(index, rotation);
                if (combined != rotation) rotation = Quaternion.Normalize(combined);
            }
            var local = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(rig.LocalRestPositions[index] + translation);
            int parent = rig.Bones[index].ParentIndex;
            world[index] = parent < 0 ? local : local * world[parent];
            skin[index] = rig.InverseBindMatrices[index] * world[index];
        }
        return (world, skin);
    }

    private Dictionary<int, BoneLocalTransform>? CopyNormalizedLocalBoneOverrides(IReadOnlyDictionary<int, BoneLocalTransform>? overrides)
    {
        if (overrides is null or { Count: 0 }) return null;
        var copy = new Dictionary<int, BoneLocalTransform>(overrides.Count);
        foreach (var (index, value) in overrides)
        {
            if (index < 0 || index >= rig.Bones.Count)
                throw new ArgumentException("Local bone overrides must reference rig bones.", "options");
            if (!AnimationValidation.Finite(value.Translation))
                throw new ArgumentException("Local bone override translations must be finite.", "options");
            var rotation = value.Rotation;
            // Match BoneKeyframe's double-precision normalization, including finite
            // very large and subnormal quaternions whose float LengthSquared is unsafe.
            double lengthSquared = (double)rotation.X * rotation.X + (double)rotation.Y * rotation.Y
                + (double)rotation.Z * rotation.Z + (double)rotation.W * rotation.W;
            if (!double.IsFinite(lengthSquared) || lengthSquared == 0)
                throw new ArgumentException("Local bone override rotations must be finite and nonzero.", "options");
            double length = Math.Sqrt(lengthSquared);
            copy.Add(index, new(value.Translation, new((float)(rotation.X / length), (float)(rotation.Y / length),
                (float)(rotation.Z / length), (float)(rotation.W / length))));
        }
        return copy;
    }

    private static void ApplyLocalBoneOverrides(IReadOnlyDictionary<int, BoneLocalTransform>? overrides,
        Vector3[] translations, Quaternion[] rotations)
    {
        if (overrides is null) return;
        foreach (var (index, value) in overrides)
            (translations[index], rotations[index]) = (value.Translation, value.Rotation);
    }

    internal CharacterSample CompletePhysicsSample(MmdConstraintEvaluator evaluator, Matrix4x4[] world,
        ReadOnlySpan<Matrix4x4> physicsWorlds, IReadOnlyCollection<int> reflectedBoneIndices, CharacterEvaluationOptions? options,
        IReadOnlyDictionary<int, Matrix4x4>? afterPhysicsOutsideParents = null)
    {
        CompletePhysicsBoneWorlds(evaluator, world, physicsWorlds, reflectedBoneIndices, options, afterPhysicsOutsideParents);
        return SkinCompletedPhysicsWorlds(world, options?.Morphs);
    }

    // The palette is the caller-owned result of CompletePhysicsBoneWorlds, whose options
    // already validated the morph rig. Match the ordinary physics completion's arithmetic
    // without SkinWorldPose's extra invertibility/affine checks on the final palette.
    internal CharacterSample SkinCompletedPhysicsWorlds(Matrix4x4[] world, MorphPose? morphs)
    {
        var skin = new Matrix4x4[world.Length];
        for (int i = 0; i < skin.Length; i++) skin[i] = rig.InverseBindMatrices[i] * world[i];
        return Skin(world, skin, morphs);
    }

    internal Matrix4x4[] CompletePhysicsBoneWorlds(MmdConstraintEvaluator evaluator, Matrix4x4[] world,
        ReadOnlySpan<Matrix4x4> physicsWorlds, IReadOnlyCollection<int> reflectedBoneIndices, CharacterEvaluationOptions? options,
        IReadOnlyDictionary<int, Matrix4x4>? afterPhysicsOutsideParents = null)
    {
        evaluator.ImportPhysicsWorlds(physicsWorlds, reflectedBoneIndices);
        if (afterPhysicsOutsideParents is not null) evaluator.UpdateOutsideParents(afterPhysicsOutsideParents);
        evaluator.EvaluateAfterPhysics(options);
        return world;
    }

    private CharacterSample Skin(Matrix4x4[] world, Matrix4x4[] skin, MorphPose? morphs)
        => deferVertexSkinning ? Defer(world, skin, morphs) : SkinVertices(world, skin, morphs);

    // The palettes are the reference skinning's own derived inputs. QDEF bones are validated now, in
    // the reference order, so a non-rigid QDEF palette still fails at the sample instead of at render.
    private CharacterSample Defer(Matrix4x4[] world, Matrix4x4[] skin, MorphPose? morphs)
    {
        var model = rig.SkinningModel;
        Quaternion[] real = [], dual = [];
        if (model.HasQdef)
        {
            real = new Quaternion[skin.Length]; dual = new Quaternion[skin.Length];
            new QdefSkinning(skin).CopyPalette(rig.QdefBones, real, dual);
        }
        var deltas = morphs is { HasVertexEffects: true } or { HasUvEffects: true } ? morphDeltas.GetValue(morphs, BuildMorphDeltas) : null;
        var ownedWorld = world.ToArray(); var ownedSkin = skin.ToArray();
        var deformation = new SceneDeformation(model, skin, model.HasSdef ? SdefWorldRotations(world) : [], real, dual, deltas,
            () => SkinVertices(ownedWorld, ownedSkin, morphs).Vertices);
        return new(deformation, world, skin);
    }

    private static SceneMorphDeltas BuildMorphDeltas(MorphPose morphs)
    {
        var positions = morphs.HasVertexEffects ? morphs.PositionRange : default;
        int firstDelta = morphs.FirstVertex;
        var uvs = morphs.HasUvEffects ? morphs.VertexUvDeltaSpan : default;
        int start, end;
        if (!positions.IsEmpty && !uvs.IsEmpty)
        {
            start = Math.Min(morphs.ActiveVertexStart, morphs.ActiveUvStart);
            end = Math.Max(morphs.ActiveVertexEnd, morphs.ActiveUvEnd);
        }
        else if (!positions.IsEmpty)
        {
            start = morphs.ActiveVertexStart;
            end = morphs.ActiveVertexEnd;
        }
        else if (!uvs.IsEmpty)
        {
            start = morphs.ActiveUvStart;
            end = morphs.ActiveUvEnd;
        }
        else
        {
            start = 0;
            end = 0;
        }
        int capacity = Math.Min(Math.Max(0, end - start), 256);
        var indices = new List<int>(capacity); var p = new List<Vector3>(capacity); var t = new List<Vector2>(capacity);
        for (int i = start; i < end; i++)
        {
            int deltaIndex = i - firstDelta;
            var position = (uint)deltaIndex < (uint)positions.Length ? positions[deltaIndex] : Vector3.Zero;
            var uv = uvs.IsEmpty ? Vector2.Zero : uvs[i];
            if (position == Vector3.Zero && uv == Vector2.Zero) continue;
            indices.Add(i); p.Add(position); t.Add(uv);
        }
        return new(morphs.VertexCount, CollectionsMarshal.AsSpan(indices), CollectionsMarshal.AsSpan(p), CollectionsMarshal.AsSpan(t));
    }

    private CharacterSample SkinVertices(Matrix4x4[] world, Matrix4x4[] skin, MorphPose? morphs)
    {
        if (rig.HasCompactBdefBindings) return SkinBdef(world, skin, morphs);
        var worldRotations = SdefWorldRotations(world);
        var vertices = new SceneVertex[rig.VertexArray.Length];
        SkinRanges(skin, morphs, worldRotations, vertices, compactBdef: false);
        return new(vertices, world, skin);
    }

    private int SkinningDegree(int vertexCount)
    {
        if (vertexCount < ParallelSkinningThreshold) return 1;
        int maximum = Math.Min(16, Math.Max(1, Environment.ProcessorCount));
        if (skinningDegreeOverride is { } degree) return Math.Clamp(degree, 1, maximum);
        object? value = AppContext.GetData(ParallelSkinningDegree);
        int requested = value is int number ? number : Math.Min(4, maximum);
        if (value is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            requested = parsed;
        return Math.Clamp(requested, 1, maximum);
    }

    private void SkinRanges(Matrix4x4[] skin, MorphPose? morphs, Quaternion[] worldRotations,
        SceneVertex[] vertices, bool compactBdef)
    {
        int degree = SkinningDegree(vertices.Length);
        if (degree > 1 && rig.HasQdef)
        {
            // Each worker owns two quaternion palettes and a ready array. A sparse
            // QDEF vertex on a rig with many bones must not multiply unbounded
            // workspace; one worker retains the original serial allocation policy.
            long workerBytes = 33L * skin.Length + 128;
            degree = (int)Math.Max(1, Math.Min(degree, MaximumParallelQdefWorkspaceBytes / workerBytes));
        }
        if (degree == 1)
        {
            skinningRangeObserverForTesting?.Invoke(0, 0, vertices.Length);
            if (compactBdef) SkinBdefRange(skin, morphs, vertices, 0, vertices.Length);
            else SkinRange(skin, morphs, worldRotations, vertices, rig.HasQdef ? new QdefSkinning(skin) : null, 0, vertices.Length);
            return;
        }

        int blockCount = (vertices.Length - 1) / SkinningBlockSize + 1;
        degree = Math.Min(degree, blockCount);
        var failures = new ExceptionDispatchInfo?[blockCount];
        int claimed = -1;
        Parallel.For(0, degree, new ParallelOptions { MaxDegreeOfParallelism = degree, TaskScheduler = TaskScheduler.Default }, worker =>
        {
            // QDEF's palette is lazy and mutable. Give each worker its own cache,
            // reused across that worker's blocks; sharing one cache would race its
            // ready/real/dual publication. All other inputs are immutable or
            // sample-owned and completed before workers start. Workers claim the
            // next unclaimed block, so a slower core (or a late-starting worker)
            // takes fewer blocks instead of holding back a fixed share. Each block
            // writes only its own vertex range, so the output is claim-order independent.
            var qdef = rig.HasQdef ? new QdefSkinning(skin) : null;
            for (int block; (block = Interlocked.Increment(ref claimed)) < blockCount;)
            {
                int start = block * SkinningBlockSize;
                int end = start + Math.Min(SkinningBlockSize, vertices.Length - start);
                try
                {
                    skinningRangeObserverForTesting?.Invoke(worker, start, end);
                    if (compactBdef) SkinBdefRange(skin, morphs, vertices, start, end);
                    else SkinRange(skin, morphs, worldRotations, vertices, qdef, start, end);
                }
                catch (Exception error)
                {
                    // Each block still visits vertices in ascending order. Join
                    // every worker, then rethrow the earliest block's original
                    // exception rather than Parallel.For's aggregate/race winner.
                    failures[block] = ExceptionDispatchInfo.Capture(error);
                }
            }
        });
        foreach (var failure in failures) failure?.Throw();
    }

    private void SkinRange(Matrix4x4[] skin, MorphPose? morphs, Quaternion[] worldRotations,
        SceneVertex[] vertices, QdefSkinning? qdef, int start, int end)
    {
        var source = rig.VertexArray; var bindings = rig.FullBindings;
        // Acquire spans inside each range; no ref-struct crosses the worker lambda.
        ReadOnlySpan<Vector3> vertexDeltas = morphs is { HasVertexEffects: true } && start < morphs.ActiveVertexEnd && end > morphs.ActiveVertexStart
            ? morphs.PositionRange : default;
        int firstDelta = morphs?.FirstVertex ?? 0;
        ReadOnlySpan<Vector2> uvDeltas = morphs is { HasUvEffects: true } && start < morphs.ActiveUvEnd && end > morphs.ActiveUvStart
            ? morphs.VertexUvDeltaSpan : default;
        for (int i = start; i < end; i++)
        {
            var vertex = source[i]; ref readonly var binding = ref bindings[i];
            // Saba UpdatePositions applies the model-space delta before both BDEF and SDEF.
            // SDEF C/R anchors stay at their bind values; only the position-minus-C term changes.
            if (!vertexDeltas.IsEmpty)
            {
                int deltaIndex = i - firstDelta;
                if ((uint)deltaIndex < (uint)vertexDeltas.Length)
                {
                    var delta = vertexDeltas[deltaIndex];
                    if (delta != Vector3.Zero) vertex = vertex with { Position = vertex.Position + delta };
                }
            }
            // Saba UpdatePositions adds only the base UV offset's X/Y; UVs do not undergo skinning.
            if (!uvDeltas.IsEmpty)
            {
                var delta = uvDeltas[i];
                if (delta != Vector2.Zero) vertex = vertex with { Uv = vertex.Uv + delta };
            }
            Vector3 position, normal;
            if (binding.Kind == SkinningKind.Qdef)
            {
                (position, normal) = qdef!.Transform(vertex, binding);
            }
            else if (binding.Kind == SkinningKind.Sdef)
            {
                var (rotation, offset) = SdefTransform(i, binding, skin, worldRotations);
                position = Vector3.Transform(vertex.Position - binding.SdefC, rotation) + offset;
                normal = Vector3.Transform(vertex.Normal, rotation);
            }
            else
            {
                position = Vector3.Zero; normal = Vector3.Zero;
                // Rig validation guarantees unused BDEF slots have zero weights.
                // Keep the original +zero accumulators, per-influence transforms,
                // multiplication and slot order, including the BDEF1 weight of one.
                AddBdefInfluence(binding.Bone0, binding.Weights.X, vertex.Position, vertex.Normal, skin, ref position, ref normal);
                if (binding.Kind != SkinningKind.Bdef1)
                    AddBdefInfluence(binding.Bone1, binding.Weights.Y, vertex.Position, vertex.Normal, skin, ref position, ref normal);
                if (binding.Kind == SkinningKind.Bdef4)
                {
                    AddBdefInfluence(binding.Bone2, binding.Weights.Z, vertex.Position, vertex.Normal, skin, ref position, ref normal);
                    AddBdefInfluence(binding.Bone3, binding.Weights.W, vertex.Position, vertex.Normal, skin, ref position, ref normal);
                }
            }
            // Equal opposing rotations can cancel the blended normal. Keep a finite fallback.
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal)
                : vertex.Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(vertex.Normal) : Vector3.Zero;
            vertices[i] = vertex with { Position = position, Normal = normal };
        }
    }

    private CharacterSample SkinBdef(Matrix4x4[] world, Matrix4x4[] skin, MorphPose? morphs)
    {
        var vertices = new SceneVertex[rig.VertexArray.Length];
        SkinRanges(skin, morphs, [], vertices, compactBdef: true);
        return new(vertices, world, skin);
    }

    private void SkinBdefRange(Matrix4x4[] skin, MorphPose? morphs, SceneVertex[] vertices, int start, int end)
    {
        var source = rig.VertexArray; var bindings = rig.CompactBdefBindings;
        ReadOnlySpan<Vector3> vertexDeltas = morphs is { HasVertexEffects: true } && start < morphs.ActiveVertexEnd && end > morphs.ActiveVertexStart
            ? morphs.PositionRange : default;
        int firstDelta = morphs?.FirstVertex ?? 0;
        ReadOnlySpan<Vector2> uvDeltas = morphs is { HasUvEffects: true } && start < morphs.ActiveUvEnd && end > morphs.ActiveUvStart
            ? morphs.VertexUvDeltaSpan : default;
        for (int i = start; i < end; i++)
        {
            var vertex = source[i]; ref readonly var binding = ref bindings[i];
            if (!vertexDeltas.IsEmpty)
            {
                int deltaIndex = i - firstDelta;
                if ((uint)deltaIndex < (uint)vertexDeltas.Length)
                {
                    var delta = vertexDeltas[deltaIndex];
                    if (delta != Vector3.Zero) vertex = vertex with { Position = vertex.Position + delta };
                }
            }
            if (!uvDeltas.IsEmpty)
            {
                var delta = uvDeltas[i];
                if (delta != Vector2.Zero) vertex = vertex with { Uv = vertex.Uv + delta };
            }
            // Match the full representation's BDEF arithmetic exactly: positive
            // zero accumulators, zero-weight skip and original influence order.
            Vector3 position = Vector3.Zero, normal = Vector3.Zero;
            AddBdefInfluence(binding.Bone0, binding.Weights.X, vertex.Position, vertex.Normal, skin, ref position, ref normal);
            if (binding.Kind != SkinningKind.Bdef1)
                AddBdefInfluence(binding.Bone1, binding.Weights.Y, vertex.Position, vertex.Normal, skin, ref position, ref normal);
            if (binding.Kind == SkinningKind.Bdef4)
            {
                AddBdefInfluence(binding.Bone2, binding.Weights.Z, vertex.Position, vertex.Normal, skin, ref position, ref normal);
                AddBdefInfluence(binding.Bone3, binding.Weights.W, vertex.Position, vertex.Normal, skin, ref position, ref normal);
            }
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal)
                : vertex.Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(vertex.Normal) : Vector3.Zero;
            vertices[i] = vertex with { Position = position, Normal = normal };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddBdefInfluence(int bone, float weight, Vector3 vertexPosition, Vector3 vertexNormal,
        Matrix4x4[] skin, ref Vector3 position, ref Vector3 normal)
    {
        // Zero weights may reference the missing-bone placeholder -1. Do not fetch
        // the palette entry or add signed zero for either +0 or -0 influences.
        if (weight == 0) return;
        var matrix = skin[bone];
        position += Vector3.Transform(vertexPosition, matrix) * weight;
        normal += Vector3.TransformNormal(vertexNormal, matrix) * weight;
    }

    private Quaternion[] SdefWorldRotations(Matrix4x4[] world)
    {
        Quaternion[] rotations = rig.SdefAnchors.Length == 0 ? [] : new Quaternion[world.Length];
        for (int i = 0; i < rotations.Length; i++) rotations[i] = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(world[i]));
        return rotations;
    }

    private (Quaternion Rotation, Vector3 Offset) SdefTransform(int vertex, SkinBinding binding, Matrix4x4[] skin, Quaternion[] rotations)
    {
        // A missing bone with zero influence is a valid PMX placeholder.
        int bone0 = binding.Weights.X > 0 ? binding.Bone0 : binding.Bone1;
        int bone1 = binding.Weights.Y > 0 ? binding.Bone1 : binding.Bone0;
        var rotation = Quaternion.Normalize(Quaternion.Slerp(rotations[bone0], rotations[bone1], binding.Weights.Y));
        var anchors = rig.SdefAnchors[vertex];
        var offset = Vector3.Transform(anchors.Anchor0, skin[bone0]) * binding.Weights.X
            + Vector3.Transform(anchors.Anchor1, skin[bone1]) * binding.Weights.Y;
        return (rotation, offset);
    }

    private (Vector3 Translation, Quaternion Rotation) EvaluateTrack(BoneTrack? track, double frame,
        OutsideParentTrackWindow? window = null)
    {
        if (track is null || track.KeyArray.Length == 0) return (Vector3.Zero, Quaternion.Identity);
        var keys = track.KeyArray;
        if (frame <= keys[0].Frame) return (keys[0].Translation, keys[0].Rotation);
        if (frame >= keys[^1].Frame)
        {
            // MMD 9.32 CC9CE/D13BA: when the active outside-parent run started after the bone's
            // last authored keyframe, the bone's local translation/rotation resets to identity.
            if (window is { BeginFrame: > 0 } resetWindow && keys[^1].Frame < resetWindow.BeginFrame && resetWindow.BeginFrame <= frame)
                return (Vector3.Zero, Quaternion.Identity);
            return (keys[^1].Translation, keys[^1].Rotation);
        }
        int lower = 0, upper = keys.Length - 1;
        while (upper - lower > 1)
        {
            int middle = lower + (upper - lower) / 2;
            if (keys[middle].Frame <= frame) lower = middle;
            else upper = middle;
        }
        var a = keys[lower]; var b = keys[upper];
        if (frame == a.Frame) return (a.Translation, a.Rotation);
        // MMD 9.32 CD0CE/D1B44: never interpolate bone curves across an outside-parent switch.
        if (window is { BeginFrame: > 0 } beginWindow && a.Frame < beginWindow.BeginFrame && beginWindow.BeginFrame < b.Frame)
            return frame < beginWindow.BeginFrame ? (a.Translation, a.Rotation) : (b.Translation, b.Rotation);
        if (window is { EndFrame: > 0 } endWindow && a.Frame < endWindow.EndFrame && endWindow.EndFrame <= b.Frame)
            return frame < endWindow.EndFrame ? (a.Translation, a.Rotation) : (b.Translation, b.Rotation);
        double progress = (frame - a.Frame) / ((double)b.Frame - a.Frame);
        var curves = ownership == SegmentCurveOwnership.Previous ? a.Curves : b.Curves;
        var translation = new Vector3(
            Blend(a.Translation.X, b.Translation.X, curves.X.Evaluate(progress)),
            Blend(a.Translation.Y, b.Translation.Y, curves.Y.Evaluate(progress)),
            Blend(a.Translation.Z, b.Translation.Z, curves.Z.Evaluate(progress)));
        var rotation = Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, (float)curves.Rotation.Evaluate(progress)));
        return (translation, rotation);
    }

    private static float Blend(float a, float b, double weight) => (float)((1 - weight) * a + weight * b);
}
