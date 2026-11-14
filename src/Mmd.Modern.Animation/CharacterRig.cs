// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// SDEF anchor preparation adapted from Saba PMXModel.cpp,
// commit 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332.
// Copyright(c) 2016-2017 benikabocha. Distributed under the MIT License.
// Full license: third_party/saba/LICENCE.txt. No OpenGL handedness conversion.
using System.Collections;
using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Animation;

public sealed record RigBone(string Name, Vector3 RestPosition, int ParentIndex,
    int DeformLayer = 0, bool AfterPhysics = false, AppendTransform? Append = null, IkDefinition? Ik = null,
    Vector3? FixedAxis = null);
public enum SkinningKind { Bdef1, Bdef2, Bdef4, Sdef, Qdef }
[Flags]
public enum UnsupportedRigFeatures { None = 0, InverseKinematics = 1, AppendTransform = 2, Physics = 4, ExternalParent = 8 }

/// <summary>SDEF coordinates are the original raw C/R values, with no handedness conversion.</summary>
public readonly record struct SkinBinding(SkinningKind Kind, int Bone0, int Bone1, int Bone2, int Bone3, Vector4 Weights,
    Vector3 SdefC = default, Vector3 SdefR0 = default, Vector3 SdefR1 = default)
{
    internal int Bone(int slot) => slot switch { 0 => Bone0, 1 => Bone1, 2 => Bone2, _ => Bone3 };
    internal float Weight(int slot) => slot switch { 0 => Weights.X, 1 => Weights.Y, 2 => Weights.Z, _ => Weights.W };
}

// The only binding payload retained by a BDEF-only rig whose unused SDEF fields
// are all positive zero. Preserve every hot field, including unused bone slots.
internal readonly record struct BdefBinding(SkinningKind Kind, int Bone0, int Bone1, int Bone2, int Bone3, Vector4 Weights)
{
    internal int Bone(int slot) => slot switch { 0 => Bone0, 1 => Bone1, 2 => Bone2, _ => Bone3 };
    internal float Weight(int slot) => slot switch { 0 => Weights.X, 1 => Weights.Y, 2 => Weights.Z, _ => Weights.W };
    internal SkinBinding Expand() => new(Kind, Bone0, Bone1, Bone2, Bone3, Weights);
}

public sealed class CharacterRig
{
    private readonly SkinBinding[]? fullBindings;
    private readonly BdefBinding[]? compactBdefBindings;
    public IReadOnlyList<RigBone> Bones { get; }
    public IReadOnlyList<SceneVertex> Vertices { get; }
    public IReadOnlyList<SkinBinding> Bindings { get; }
    public UnsupportedRigFeatures UnsupportedFeatures { get; }
    public BoneEvaluationSchedule EvaluationSchedule { get; }
    internal int[] EvaluationOrder { get; }
    internal int[] DeformOrder { get; }
    internal int[] FirstChild { get; }
    internal int[] NextSibling { get; }
    internal bool[] IsIkLink { get; }
    internal bool HasConstraints { get; }
    internal MmdBoneSchedule? MmdSchedule { get; }
    internal Vector3[] LocalRestPositions { get; }
    internal bool HasCompactBdefBindings => compactBdefBindings is not null;
    internal ReadOnlySpan<BdefBinding> CompactBdefBindings => compactBdefBindings;
    internal ReadOnlySpan<SkinBinding> FullBindings => fullBindings;
    internal SceneVertex[] VertexArray { get; }
    internal Matrix4x4[] InverseBindMatrices { get; }
    internal (Vector3 Anchor0, Vector3 Anchor1)[] SdefAnchors { get; }
    internal bool HasQdef { get; }

    public CharacterRig(IEnumerable<RigBone> bones, IEnumerable<SceneVertex> vertices, IEnumerable<SkinBinding> bindings,
        UnsupportedRigFeatures unsupportedFeatures = UnsupportedRigFeatures.None,
        BoneEvaluationSchedule evaluationSchedule = BoneEvaluationSchedule.Saba)
    {
        ArgumentNullException.ThrowIfNull(bones); ArgumentNullException.ThrowIfNull(vertices); ArgumentNullException.ThrowIfNull(bindings);
        if (!Enum.IsDefined(evaluationSchedule)) throw new ArgumentOutOfRangeException(nameof(evaluationSchedule));
        EvaluationSchedule = evaluationSchedule;
        const UnsupportedRigFeatures allFeatures = UnsupportedRigFeatures.InverseKinematics | UnsupportedRigFeatures.AppendTransform
            | UnsupportedRigFeatures.Physics | UnsupportedRigFeatures.ExternalParent;
        if ((unsupportedFeatures & ~allFeatures) != 0) throw new ArgumentOutOfRangeException(nameof(unsupportedFeatures));
        var boneCopy = bones.ToArray(); VertexArray = vertices.ToArray(); var bindingCopy = bindings.ToArray();
        if (VertexArray.Length != bindingCopy.Length) throw new ArgumentException("One skin binding per vertex is required.", nameof(bindings));
        var names = new HashSet<string>(StringComparer.Ordinal);
        LocalRestPositions = new Vector3[boneCopy.Length]; InverseBindMatrices = new Matrix4x4[boneCopy.Length];
        for (int i = 0; i < boneCopy.Length; i++)
        {
            var bone = boneCopy[i];
            if (bone is null || string.IsNullOrWhiteSpace(bone.Name) || !names.Add(bone.Name) || !AnimationValidation.Finite(bone.RestPosition))
                throw new ArgumentException("Bone names must be unique and nonempty; rest positions must be finite.", nameof(bones));
            if (bone.ParentIndex < -1 || bone.ParentIndex >= boneCopy.Length || bone.ParentIndex == i)
                throw new ArgumentException("Bone parent is outside the hierarchy or points to itself.", nameof(bones));
            if (bone.FixedAxis is { } fixedAxis)
            {
                float lengthSquared = fixedAxis.LengthSquared();
                if (!AnimationValidation.Finite(fixedAxis) || !float.IsFinite(lengthSquared) || lengthSquared < 1e-12f)
                    throw new ArgumentException($"Invalid fixed axis at bone {i}.", nameof(bones));
                boneCopy[i] = bone with { FixedAxis = Vector3.Normalize(fixedAxis) };
            }
        }
        for (int i = 0; i < boneCopy.Length; i++)
        {
            var bone = boneCopy[i];
            LocalRestPositions[i] = bone.ParentIndex < 0 ? bone.RestPosition : bone.RestPosition - boneCopy[bone.ParentIndex].RestPosition;
            if (!AnimationValidation.Finite(LocalRestPositions[i])) throw new ArgumentException("Local rest position overflow.", nameof(bones));
            InverseBindMatrices[i] = Matrix4x4.CreateTranslation(-bone.RestPosition);
        }
        EvaluationOrder = BuildEvaluationOrder(boneCopy);
        DeformOrder = Enumerable.Range(0, boneCopy.Length).OrderBy(i => boneCopy[i].DeformLayer).ToArray();
        FirstChild = Enumerable.Repeat(-1, boneCopy.Length).ToArray();
        NextSibling = Enumerable.Repeat(-1, boneCopy.Length).ToArray();
        IsIkLink = new bool[boneCopy.Length];
        long work = 0;
        for (int i = boneCopy.Length - 1; i >= 0; i--)
        {
            var bone = boneCopy[i]; int parent = bone.ParentIndex;
            if (parent >= 0) { NextSibling[i] = FirstChild[parent]; FirstChild[parent] = i; }
            if (bone.Append is { } append)
            {
                if (append.SourceBoneIndex < 0 || append.SourceBoneIndex >= boneCopy.Length || append.SourceBoneIndex == i
                    || !float.IsFinite(append.Weight) || MathF.Abs(append.Weight) > 1000)
                    throw new ArgumentException($"Invalid append reference or weight at bone {i}.", nameof(bones));
                HasConstraints = true;
            }
            if (bone.Ik is not { } ik) continue;
            HasConstraints = true;
            if (ik.TargetBoneIndex < 0 || ik.TargetBoneIndex >= boneCopy.Length)
                throw new ArgumentException($"Invalid IK target at bone {i}.", nameof(bones));
            work += (long)ik.Iterations * ik.Links.Count;
            if (work > 1_048_576) throw new ArgumentException("Rig exceeds the bounded IK link-iteration budget.", nameof(bones));
            var uniqueLinks = new HashSet<int>();
            foreach (var link in ik.Links)
            {
                if (link.BoneIndex < 0 || link.BoneIndex >= boneCopy.Length || !uniqueLinks.Add(link.BoneIndex)
                    || link.MinimumAngles.HasValue != link.MaximumAngles.HasValue)
                    throw new ArgumentException($"Invalid or duplicate IK link at bone {i}.", nameof(bones));
                if (link.MinimumAngles is { } min && link.MaximumAngles is { } max
                    && (!AnimationValidation.Finite(min) || !AnimationValidation.Finite(max)
                        || min.X > max.X || min.Y > max.Y || min.Z > max.Z
                        || Vector3.Abs(min).X > MathF.Tau || Vector3.Abs(min).Y > MathF.Tau || Vector3.Abs(min).Z > MathF.Tau
                        || Vector3.Abs(max).X > MathF.Tau || Vector3.Abs(max).Y > MathF.Tau || Vector3.Abs(max).Z > MathF.Tau))
                    throw new ArgumentException($"Invalid IK XYZ limits at bone {i}.", nameof(bones));
                IsIkLink[link.BoneIndex] = true;
            }
        }
        ValidateAppendCycles(boneCopy);
        if (evaluationSchedule == BoneEvaluationSchedule.Mmd) MmdSchedule = new(boneCopy);
        SdefAnchors = bindingCopy.Any(binding => binding.Kind == SkinningKind.Sdef) ? new (Vector3, Vector3)[VertexArray.Length] : [];
        HasQdef = bindingCopy.Any(binding => binding.Kind == SkinningKind.Qdef);
        bool canCompactBdef = true;
        for (int i = 0; i < VertexArray.Length; i++)
        {
            var vertex = VertexArray[i];
            float normalLength = vertex.Normal.LengthSquared();
            if (!AnimationValidation.Finite(vertex.Position) || !AnimationValidation.Finite(vertex.Normal) || !AnimationValidation.Finite(vertex.Uv)
                || !float.IsFinite(normalLength))
                throw new ArgumentException($"Vertex {i} must have finite coordinates and normals.", nameof(vertices));
            var binding = bindingCopy[i];
            ValidateBinding(binding, boneCopy.Length, i);
            // Normalize only the small accepted roundoff in the weight sum.
            float sum = binding.Weights.X + binding.Weights.Y + binding.Weights.Z + binding.Weights.W;
            bindingCopy[i] = binding = binding with { Weights = binding.Weights / sum };
            canCompactBdef &= (binding.Kind is SkinningKind.Bdef1 or SkinningKind.Bdef2 or SkinningKind.Bdef4)
                && PositiveZero(binding.SdefC) && PositiveZero(binding.SdefR0) && PositiveZero(binding.SdefR1);
            if (binding.Kind == SkinningKind.Sdef)
            {
                var weightedR = binding.SdefR0 * binding.Weights.X + binding.SdefR1 * binding.Weights.Y;
                SdefAnchors[i] = (binding.SdefC + (binding.SdefR0 - weightedR) * .5f,
                    binding.SdefC + (binding.SdefR1 - weightedR) * .5f);
                if (!AnimationValidation.Finite(SdefAnchors[i].Anchor0) || !AnimationValidation.Finite(SdefAnchors[i].Anchor1))
                    throw new ArgumentException($"SDEF anchor overflow at vertex {i}.", nameof(bindings));
            }
        }
        if (canCompactBdef)
        {
            compactBdefBindings = new BdefBinding[bindingCopy.Length];
            for (int i = 0; i < compactBdefBindings.Length; i++)
            {
                var binding = bindingCopy[i];
                compactBdefBindings[i] = new(binding.Kind, binding.Bone0, binding.Bone1, binding.Bone2, binding.Bone3, binding.Weights);
            }
            // The adapter owns only the compact payload. Do not retain bindingCopy,
            // the source enumerable or this rig through a compatibility closure.
            Bindings = new ReadOnlyCollection<SkinBinding>(new CompactBindingList(compactBdefBindings));
        }
        else
        {
            fullBindings = bindingCopy;
            Bindings = Array.AsReadOnly(fullBindings);
        }
        Bones = Array.AsReadOnly(boneCopy); Vertices = Array.AsReadOnly(VertexArray);
        UnsupportedFeatures = unsupportedFeatures;
    }

    private static bool PositiveZero(Vector3 value) => BitConverter.SingleToInt32Bits(value.X) == 0
        && BitConverter.SingleToInt32Bits(value.Y) == 0 && BitConverter.SingleToInt32Bits(value.Z) == 0;

    // ReadOnlyCollection preserves the existing public collection interfaces and
    // mutation guards; this private list reconstructs one binding at a time.
    private sealed class CompactBindingList(BdefBinding[] bindings) : IList<SkinBinding>
    {
        public int Count => bindings.Length;
        public bool IsReadOnly => true;
        public SkinBinding this[int index]
        {
            get
            {
                if ((uint)index >= (uint)bindings.Length) throw new ArgumentOutOfRangeException(nameof(index));
                return bindings[index].Expand();
            }
            set => throw new NotSupportedException();
        }
        public int IndexOf(SkinBinding item)
        {
            for (int i = 0; i < bindings.Length; i++) if (bindings[i].Expand().Equals(item)) return i;
            return -1;
        }
        public bool Contains(SkinBinding item) => IndexOf(item) >= 0;
        public void CopyTo(SkinBinding[] array, int arrayIndex)
        {
            if (array is null || arrayIndex < 0 || array.Length - arrayIndex < bindings.Length)
            {
                // Array-backed ReadOnlyCollection delegates these failures to
                // Array.Copy. Its bounds checks precede element-type checks, so
                // this invalid-only call preserves exception types and arguments
                // without allocating a complete SkinBinding compatibility array.
                Array.Copy(bindings, 0, array!, arrayIndex, bindings.Length);
                return;
            }
            for (int i = 0; i < bindings.Length; i++) array[arrayIndex + i] = bindings[i].Expand();
        }
        public IEnumerator<SkinBinding> GetEnumerator() => new Enumerator(bindings);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Add(SkinBinding item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, SkinBinding item) => throw new NotSupportedException();
        public bool Remove(SkinBinding item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();

        private sealed class Enumerator(BdefBinding[] bindings) : IEnumerator<SkinBinding>
        {
            private int index = -1;
            public SkinBinding Current => (uint)index < (uint)bindings.Length ? bindings[index].Expand()
                : throw new InvalidOperationException("Enumeration has not started or has already finished.");
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (index < bindings.Length) index++;
                return index < bindings.Length;
            }
            public void Reset() => index = -1;
            public void Dispose() { }
        }
    }

    private static void ValidateAppendCycles(RigBone[] bones)
    {
        var state = new byte[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            int current = i;
            while (current >= 0 && state[current] == 0)
            {
                state[current] = 1; current = bones[current].Append?.SourceBoneIndex ?? -1;
            }
            if (current >= 0 && state[current] == 1) throw new ArgumentException("Append references contain a cycle.", nameof(bones));
            current = i;
            while (current >= 0 && state[current] == 1)
            {
                state[current] = 2; current = bones[current].Append?.SourceBoneIndex ?? -1;
            }
        }
    }

    private static int[] BuildEvaluationOrder(RigBone[] bones)
    {
        var state = new byte[bones.Length]; var chain = new int[bones.Length]; var order = new int[bones.Length]; int written = 0;
        for (int i = 0; i < bones.Length; i++)
        {
            int current = i, depth = 0;
            while (current != -1 && state[current] != 2)
            {
                if (state[current] == 1) throw new ArgumentException("Bone hierarchy contains a cycle.", nameof(bones));
                state[current] = 1; chain[depth++] = current; current = bones[current].ParentIndex;
            }
            while (depth > 0) { current = chain[--depth]; state[current] = 2; order[written++] = current; }
        }
        return order;
    }

    private static void ValidateBinding(SkinBinding binding, int boneCount, int vertex)
    {
        if (!Enum.IsDefined(binding.Kind)) throw new ArgumentException($"Unknown skinning kind at vertex {vertex}.");
        int slots = binding.Kind switch { SkinningKind.Bdef1 => 1, SkinningKind.Bdef4 or SkinningKind.Qdef => 4, _ => 2 };
        double total = 0;
        for (int slot = 0; slot < 4; slot++)
        {
            float weight = binding.Weight(slot); int bone = binding.Bone(slot);
            if (!float.IsFinite(weight) || weight is < 0 or > 1 || (slot >= slots && weight != 0)
                || bone < -1 || (weight > 0 && (bone < 0 || bone >= boneCount)))
                throw new ArgumentException($"Invalid skin weight or bone at vertex {vertex}, slot {slot}.");
            total += weight;
        }
        if (Math.Abs(total - 1) > .0001) throw new ArgumentException($"Skin weights must sum to one at vertex {vertex}.");
        if (binding.Kind == SkinningKind.Sdef && (!AnimationValidation.Finite(binding.SdefC)
            || !AnimationValidation.Finite(binding.SdefR0) || !AnimationValidation.Finite(binding.SdefR1)))
            throw new ArgumentException($"Non-finite SDEF coordinates at vertex {vertex}.");
    }
}
