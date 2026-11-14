using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CompactBdefBindingTests
{
    private static readonly float NegativeZero = BitConverter.Int32BitsToSingle(int.MinValue);

    [Fact]
    public void PureBdefOwnsOneCompactPayloadAndReconstructsNormalizedPublicValuesExactly()
    {
        var input = Bindings();
        var expected = input.Select(Normalized).ToArray();
        var rig = Rig(input);
        Assert.Equal(72, Unsafe.SizeOf<SkinBinding>());
        Assert.Equal(36, Unsafe.SizeOf<BdefBinding>());
        Assert.True(rig.HasCompactBdefBindings);
        Assert.Equal(input.Length, rig.CompactBdefBindings.Length);
        Assert.True(rig.FullBindings.IsEmpty);
        var resident = ResidentBindingArrays(rig);
        Assert.Equal(typeof(BdefBinding[]), Assert.Single(resident).GetType());
        Assert.Equal(input.Length, resident[0].Length);
        Exact(expected, rig.Bindings.ToArray());

        // Neither the caller's input nor a reconstructed value can mutate the rig.
        Array.Fill(input, default);
        var value = rig.Bindings[0] with { Weights = Vector4.Zero };
        Assert.Equal(Vector4.Zero, value.Weights);
        Exact(expected, rig.Bindings.ToArray());
        for (int pass = 0; pass < 4; pass++)
        {
            Exact(expected, rig.Bindings.ToArray());
            Assert.Same(resident[0], Assert.Single(ResidentBindingArrays(rig)));
        }
    }

    public static IEnumerable<object[]> ColdValues()
    {
        int[] bits = [int.MinValue, 1, 0x3f800000, 0x7fc12345, 0x7f800000, unchecked((int)0xff800000)];
        for (int component = 0; component < 9; component++)
        foreach (int value in bits) yield return [component, value];
    }

    [Theory]
    [MemberData(nameof(ColdValues))]
    public void EveryNonPositiveZeroColdComponentForcesWholeRigFallbackWithoutLosingBits(int component, int bits)
    {
        var input = Bindings();
        input[2] = WithColdComponent(input[2], component, BitConverter.Int32BitsToSingle(bits));
        var expected = input.Select(Normalized).ToArray();
        var rig = Rig(input);
        Assert.False(rig.HasCompactBdefBindings);
        Assert.True(rig.CompactBdefBindings.IsEmpty);
        Assert.Equal(input.Length, rig.FullBindings.Length);
        Assert.Equal(typeof(SkinBinding[]), Assert.Single(ResidentBindingArrays(rig)).GetType());
        Exact(expected, rig.Bindings.ToArray());
        Array.Fill(input, default);
        Exact(expected, rig.Bindings.ToArray());
    }

    [Theory]
    [InlineData(SkinningKind.Sdef)]
    [InlineData(SkinningKind.Qdef)]
    public void MixedKindsKeepOnlyTheCompleteBindingRepresentation(SkinningKind kind)
    {
        var input = Bindings().Append(new SkinBinding(kind, 0, 1, -1, -1, new(.375f, .625f, 0, 0),
            new(1, -2, 3), new(-4, 5, 6), new(7, 8, -9))).ToArray();
        var rig = Rig(input);
        Assert.False(rig.HasCompactBdefBindings);
        Assert.True(rig.CompactBdefBindings.IsEmpty);
        Assert.Equal(typeof(SkinBinding[]), Assert.Single(ResidentBindingArrays(rig)).GetType());
        Exact(input.Select(Normalized).ToArray(), rig.Bindings.ToArray());
        var animator = new CharacterAnimator(rig, new([]));
        Assert.Equal(input.Length, animator.SkinWorldPose(Worlds(rigid: true)).Vertices.Length);
        Assert.Equal(input.Length, animator.CalculateVertexSkinTransforms(Worlds(rigid: true)).Length);
    }

    [Fact]
    public void CompactFacadePreservesReadOnlyCollectionIndexEnumerationAndCopyContracts()
    {
        var rig = Rig(Bindings());
        var expected = Array.AsReadOnly(Bindings().Select(Normalized).ToArray());
        var actual = rig.Bindings;
        Assert.False(actual is SkinBinding[]);
        Assert.Equal(expected.Count, actual.Count);
        foreach (int index in new[] { -1, actual.Count })
        {
            var baseline = Assert.Throws<ArgumentOutOfRangeException>(() => expected[index]);
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() => actual[index]);
            Assert.Equal(baseline.ParamName, exception.ParamName);
        }
        var list = Assert.IsAssignableFrom<IList<SkinBinding>>(actual);
        var untyped = Assert.IsAssignableFrom<IList>(actual);
        Assert.True(list.IsReadOnly); Assert.True(untyped.IsReadOnly); Assert.True(untyped.IsFixedSize);
        Assert.Throws<NotSupportedException>(() => list[0] = default);
        Assert.Throws<NotSupportedException>(() => list.Add(default));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Throws<NotSupportedException>(() => list.Insert(0, default));
        Assert.Throws<NotSupportedException>(() => list.Remove(default));
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => untyped[0] = default(SkinBinding));
        Assert.Equal(expected.IndexOf(expected[2]), list.IndexOf(expected[2]));
        Assert.True(list.Contains(expected[2])); Assert.False(list.Contains(default));
        Assert.Equal(-1, list.IndexOf(default));
        var destination = new SkinBinding[actual.Count + 2];
        list.CopyTo(destination, 1);
        Exact(expected.ToArray(), destination.AsSpan(1, actual.Count).ToArray());
        Assert.Throws<ArgumentNullException>(() => list.CopyTo(null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.CopyTo(destination, -1));
        Assert.Throws<ArgumentException>(() => list.CopyTo(new SkinBinding[actual.Count - 1], 0));
        using var enumerator = actual.GetEnumerator();
        Assert.Throws<InvalidOperationException>(() => enumerator.Current);
        var enumerated = new List<SkinBinding>();
        while (enumerator.MoveNext()) enumerated.Add(enumerator.Current);
        Assert.Throws<InvalidOperationException>(() => enumerator.Current);
        Assert.False(enumerator.MoveNext());
        ((IEnumerator)enumerator).Reset();
        Assert.True(enumerator.MoveNext());
        Exact<SkinBinding>([expected[0]], [enumerator.Current]);
        Exact(expected.ToArray(), enumerated.ToArray());
        Exact(expected.ToArray(), ((IEnumerable)actual).Cast<SkinBinding>().ToArray());
        Assert.Equal(typeof(BdefBinding[]), Assert.Single(ResidentBindingArrays(rig)).GetType());
    }

    [Fact]
    public void EmptyRigRetainsNoCompleteCompatibilityPayload()
    {
        var rig = new CharacterRig([], [], []);
        Assert.True(rig.HasCompactBdefBindings);
        Assert.Empty(rig.Bindings);
        Assert.True(rig.FullBindings.IsEmpty);
        Assert.Equal(typeof(BdefBinding[]), Assert.Single(ResidentBindingArrays(rig)).GetType());
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Bindings[0]);
        var animator = new CharacterAnimator(rig, new([]));
        Assert.Empty(animator.SkinWorldPose([]).Vertices);
        Assert.Empty(animator.CalculateVertexSkinTransforms([]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyToFailuresMatchArrayBackedReadOnlyCollectionIncludingEmptyAndPastEnd(bool empty)
    {
        var input = empty ? Array.Empty<SkinBinding>() : Bindings();
        var actual = (IList<SkinBinding>)(empty ? new CharacterRig([], [], []) : Rig(input)).Bindings;
        var baseline = Array.AsReadOnly(input.Select(Normalized).ToArray());
        (SkinBinding[]? Array, int Index)[] cases = [
            (null, 0), (null, -1), (new SkinBinding[input.Length], -1),
            ([], input.Length == 0 ? 1 : 0), (new SkinBinding[input.Length], input.Length + 1),
            (new SkinBinding[input.Length], int.MaxValue)];
        foreach (var (array, index) in cases)
        {
            var expected = Record.Exception(() => baseline.CopyTo(array!, index));
            var exception = Record.Exception(() => actual.CopyTo(array!, index));
            Assert.NotNull(expected); Assert.NotNull(exception);
            Assert.Equal(expected.GetType(), exception.GetType());
            Assert.Equal(((ArgumentException)expected).ParamName, ((ArgumentException)exception).ParamName);
            Assert.Equal(expected.Message, exception.Message);
        }
        var destination = new SkinBinding[input.Length + 2];
        actual.CopyTo(destination, 2);
        Exact(baseline.ToArray(), destination.AsSpan(2).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactAndFallbackMatchFrozenSlotArithmeticAndKeepEveryOwnedSample(bool rigid)
    {
        var input = Bindings();
        var compact = Rig(input);
        var fallbackInput = input.ToArray();
        fallbackInput[0] = fallbackInput[0] with { SdefC = new(NegativeZero, 0, 0) };
        var fallback = Rig(fallbackInput);
        var animator = new CharacterAnimator(compact, new([]));
        var reference = new CharacterAnimator(fallback, new([]));
        var worlds = Worlds(rigid);
        var held = new List<(CharacterSample Sample, byte[] Vertices, byte[] Worlds, byte[] Skin)>();
        foreach (int mode in new[] { 0, 1, 2, 3, 4, 0, 4 })
        {
            var morphs = Pose(compact, mode);
            var actual = animator.SkinWorldPose(worlds, morphs);
            var old = reference.SkinWorldPose(worlds, morphs);
            Exact(ReferenceSkin(compact, actual.SkinMatrices, morphs), actual.Vertices);
            Exact(old.Vertices, actual.Vertices);
            Exact(old.BoneWorldMatrices, actual.BoneWorldMatrices);
            Exact(old.SkinMatrices, actual.SkinMatrices);
            Exact(ReferenceTransforms(compact, actual.SkinMatrices), animator.CalculateVertexSkinTransforms(worlds));
            Exact(reference.CalculateVertexSkinTransforms(worlds), animator.CalculateVertexSkinTransforms(worlds));
            foreach (var previous in held)
            {
                Assert.NotSame(previous.Sample.Vertices, actual.Vertices);
                Assert.NotSame(previous.Sample.BoneWorldMatrices, actual.BoneWorldMatrices);
                Assert.NotSame(previous.Sample.SkinMatrices, actual.SkinMatrices);
                Assert.Equal(previous.Vertices, Bytes(previous.Sample.Vertices));
                Assert.Equal(previous.Worlds, Bytes(previous.Sample.BoneWorldMatrices));
                Assert.Equal(previous.Skin, Bytes(previous.Sample.SkinMatrices));
            }
            held.Add((actual, Bytes(actual.Vertices), Bytes(actual.BoneWorldMatrices), Bytes(actual.SkinMatrices)));
        }
        var expected = ReferenceSkin(compact, held[^1].Sample.SkinMatrices, Pose(compact, 4));
        held[^1].Sample.Vertices[0] = default;
        held[^1].Sample.BoneWorldMatrices[0] = default;
        held[^1].Sample.SkinMatrices[0] = default;
        Exact(expected, animator.SkinWorldPose(worlds, Pose(compact, 4)).Vertices);
        var options = new CharacterEvaluationOptions(Morphs: Pose(compact, 4));
        Exact(reference.Sample(0, options).Vertices, animator.Sample(0, options).Vertices);
        using var evaluation = animator.BeginPhysicsSample(0, options);
        using var baseline = reference.BeginPhysicsSample(0, options);
        var completed = evaluation.Complete(evaluation.BeforePhysicsBoneWorldMatrices, []);
        var baselineCompleted = baseline.Complete(baseline.BeforePhysicsBoneWorldMatrices, []);
        Exact(baselineCompleted.Vertices, completed.Vertices);
        Exact(ReferenceSkin(compact, completed.SkinMatrices, options.Morphs), completed.Vertices);
        Assert.Equal(typeof(BdefBinding[]), Assert.Single(ResidentBindingArrays(compact)).GetType());
    }

    private static SkinBinding[] Bindings() => [
        new(SkinningKind.Bdef1, 0, 999, -1, 37, new(1, NegativeZero, 0, NegativeZero)),
        new(SkinningKind.Bdef2, -1, 2, 999, -1, new(NegativeZero, 1, 0, NegativeZero)),
        new(SkinningKind.Bdef2, 3, 1, -1, -1, new(.375f, .625f, 0, 0)),
        new(SkinningKind.Bdef4, 0, 1, 2, 3, new(.1f, .2f, .3f, .4f)),
        new(SkinningKind.Bdef4, -1, 3, -1, 2, new(NegativeZero, .75f, NegativeZero, .25f)),
        new(SkinningKind.Bdef4, 3, 2, 1, -1, new(.99995f, .000025f, .000024f, NegativeZero))];

    private static SkinBinding Normalized(SkinBinding binding)
        => binding with { Weights = binding.Weights / (binding.Weights.X + binding.Weights.Y + binding.Weights.Z + binding.Weights.W) };

    private static CharacterRig Rig(SkinBinding[] bindings)
    {
        RigBone[] bones = [new("a", new(1.1f, -2.3f, .7f), -1), new("b", new(-3.7f, 2.9f, 1.3f), -1),
            new("c", new(7.3f, -5.1f, 4.9f), -1), new("d", new(-2.1f, 9.7f, -8.3f), -1)];
        var vertices = Enumerable.Range(0, bindings.Length).Select(i => new SceneVertex(
            i == 0 ? new(NegativeZero, 0, NegativeZero) : i == 1 ? new(float.Epsilon, -float.Epsilon, 1e-35f) : new(.137f * i, -42.7f, 8.93f),
            i == 0 ? new(NegativeZero, 0, NegativeZero) : i == 1 ? new(1e-7f, -2e-7f, 0) : new(.37f, -.81f, .233f),
            i == 0 ? new(NegativeZero, 0) : new(.173f, -.829f)));
        return new(bones, vertices, bindings);
    }

    private static SkinBinding WithColdComponent(SkinBinding binding, int component, float value)
    {
        var cold = new float[9]; cold[component] = value;
        return binding with { SdefC = new(cold[0], cold[1], cold[2]), SdefR0 = new(cold[3], cold[4], cold[5]),
            SdefR1 = new(cold[6], cold[7], cold[8]) };
    }

    private static Matrix4x4[] Worlds(bool rigid) => Enumerable.Range(0, 4).Select(i =>
        (rigid ? Matrix4x4.Identity : Matrix4x4.CreateScale(.7f + i * .3f, 1.9f - i * .2f, 1.1f + i * .1f))
        * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(.13f + i * .19f, -.47f, .23f, .61f - i * .11f)))
        * Matrix4x4.CreateTranslation(3.7f - i * 5.3f, -8.1f + i * .7f, 2.9f + i * 1.1f)).ToArray();

    // 0 absent, 1 signed-zero no-op, 2 positions, 3 UVs, 4 both.
    private static MorphPose? Pose(CharacterRig rig, int mode) => mode == 0 ? null : new(
        Enumerable.Range(0, rig.Vertices.Count).Select(i => mode is 2 or 4 && i % 2 != 0
            ? new Vector3(.19f * (i + 1), -.37f, .83f) : new(NegativeZero, 0, NegativeZero)),
        new Vector3[rig.Bones.Count], Enumerable.Repeat(Quaternion.Identity, rig.Bones.Count),
        Enumerable.Range(0, rig.Vertices.Count).Select(i => mode is 3 or 4 && i % 2 == 0
            ? new Vector2(-.137f, .251f * (i + 1)) : new(NegativeZero, 0)));

    // Frozen four-slot BDEF arithmetic; does not use compact bindings or the new path.
    private static SceneVertex[] ReferenceSkin(CharacterRig rig, Matrix4x4[] skin, MorphPose? morphs)
    {
        var result = new SceneVertex[rig.Vertices.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var vertex = rig.Vertices[i]; var binding = rig.Bindings[i];
            if (morphs is not null && morphs.VertexDeltas[i] != Vector3.Zero)
                vertex = vertex with { Position = vertex.Position + morphs.VertexDeltas[i] };
            if (morphs is not null && morphs.VertexUvDeltas[i] != Vector2.Zero)
                vertex = vertex with { Uv = vertex.Uv + morphs.VertexUvDeltas[i] };
            Vector3 position = Vector3.Zero, normal = Vector3.Zero;
            for (int slot = 0; slot < 4; slot++)
            {
                float weight = binding.Weight(slot); if (weight == 0) continue;
                var matrix = skin[binding.Bone(slot)];
                position += Vector3.Transform(vertex.Position, matrix) * weight;
                normal += Vector3.TransformNormal(vertex.Normal, matrix) * weight;
            }
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal)
                : vertex.Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(vertex.Normal) : Vector3.Zero;
            result[i] = vertex with { Position = position, Normal = normal };
        }
        return result;
    }

    private static Matrix4x4[] ReferenceTransforms(CharacterRig rig, Matrix4x4[] skin)
    {
        var result = new Matrix4x4[rig.Vertices.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var binding = rig.Bindings[i]; Matrix4x4 matrix = default;
            for (int slot = 0; slot < 4; slot++)
            {
                float weight = binding.Weight(slot); if (weight == 0) continue;
                matrix += skin[binding.Bone(slot)] * weight;
            }
            matrix.M14 = matrix.M24 = matrix.M34 = 0; matrix.M44 = 1; result[i] = matrix;
        }
        return result;
    }

    private static List<Array> ResidentBindingArrays(CharacterRig rig)
    {
        var arrays = new List<Array>(); var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(object? value)
        {
            if (value is null || value is string || value.GetType().IsValueType || !seen.Add(value)) return;
            if (value is Array array)
            {
                var element = array.GetType().GetElementType()!;
                if (element == typeof(SkinBinding) || element == typeof(BdefBinding)) arrays.Add(array);
                if (!element.IsValueType) foreach (var item in array) Visit(item);
                return;
            }
            for (var type = value.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                Visit(field.GetValue(value));
        }
        Visit(rig); return arrays;
    }

    private static byte[] Bytes<T>(T[] values) where T : struct => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static void Exact<T>(T[] expected, T[] actual) where T : struct => Assert.Equal(Bytes(expected), Bytes(actual));
}
