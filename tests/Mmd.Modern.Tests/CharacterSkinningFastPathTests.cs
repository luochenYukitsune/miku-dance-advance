using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterSkinningFastPathTests
{
    private static readonly float NegativeZero = BitConverter.Int32BitsToSingle(int.MinValue);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryBdefKindMatchesOldFourSlotArithmeticBitForBit(int morphMode)
    {
        var rig = BdefRig();
        var worlds = Worlds(rigid: false);
        var morphs = Pose(rig, morphMode);
        var actual = new CharacterAnimator(rig, new([])).SkinWorldPose(worlds, morphs);
        Exact(OldSkin(rig, worlds, morphs), actual.Vertices);
        Exact(worlds, actual.BoneWorldMatrices);
        Exact(Palette(rig, worlds), actual.SkinMatrices);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MixedSdefAndQdefKeepTheirOldArithmeticIncludingMorphs(int morphMode)
    {
        var rig = MixedRig();
        var worlds = Worlds(rigid: true);
        var morphs = Pose(rig, morphMode);
        var animator = new CharacterAnimator(rig, new([]));
        Exact(OldSkin(rig, worlds, morphs), animator.SkinWorldPose(worlds, morphs).Vertices);

        // Exercise the shared skinning entry from animation and physics completion too.
        var options = new CharacterEvaluationOptions(Morphs: morphs);
        var sampled = animator.Sample(0, options);
        Exact(OldSkin(rig, sampled.BoneWorldMatrices, morphs), sampled.Vertices);
        using var evaluation = animator.BeginPhysicsSample(0, options);
        var completed = evaluation.Complete(evaluation.BeforePhysicsBoneWorldMatrices, []);
        Exact(OldSkin(rig, completed.BoneWorldMatrices, morphs), completed.Vertices);
    }

    [Fact]
    public void OpposingNormalsRetainNormalizedSourceOrZeroFallbackExactly()
    {
        Vector3[] normals = [new(3, 4, 0), Vector3.Zero, new(NegativeZero, NegativeZero, 0),
            new(1e-7f, 0, 0), new(float.Epsilon, 0, 0)];
        var rig = new CharacterRig([new("a", Vector3.Zero, -1), new("b", Vector3.Zero, -1)],
            normals.Select(n => new SceneVertex(new(NegativeZero, 0, NegativeZero), n, new(NegativeZero, 0))),
            normals.Select(_ => new SkinBinding(SkinningKind.Bdef2, 0, 1, -1, -1, new(.5f, .5f, NegativeZero, 0))));
        Matrix4x4[] worlds = [Matrix4x4.Identity, Matrix4x4.CreateScale(-1, -1, 1)];
        var actual = new CharacterAnimator(rig, new([])).SkinWorldPose(worlds);
        Exact(OldSkin(rig, worlds, null), actual.Vertices);
        Assert.Equal(Vector3.Normalize(normals[0]), actual.Vertices[0].Normal);
        foreach (var vertex in actual.Vertices.Skip(1)) Assert.Equal(Vector3.Zero, vertex.Normal);
    }

    [Fact]
    public void RawCompletionPaletteKeepsRoundoffAndPublicAffineGuardIsUnchanged()
    {
        var rig = BdefRig();
        var worlds = Worlds(rigid: false);
        worlds[0].M44 = float.BitIncrement(1f);
        var skin = Palette(rig, worlds);
        var morphs = Pose(rig, 4);
        var animator = new CharacterAnimator(rig, new([]));
        Assert.Throws<ArgumentException>(() => animator.SkinWorldPose(worlds, morphs));

        // Physics completion passes its evaluated raw palettes directly to Skin. This
        // invocation checks that optimization does not repair or revalidate their fields.
        var method = typeof(CharacterAnimator).GetMethod("Skin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var actual = (CharacterSample)method.Invoke(animator, [worlds, skin, morphs])!;
        Exact(OldSkin(rig, worlds, morphs), actual.Vertices);
        Exact(worlds, actual.BoneWorldMatrices);
        Exact(skin, actual.SkinMatrices);
    }

    [Fact]
    public void MorphRemovalAndRestoreNeverMutateAnyPreviouslyOwnedOutput()
    {
        var rig = MixedRig();
        var animator = new CharacterAnimator(rig, new([]));
        var worlds = Worlds(rigid: true);
        var held = new List<(CharacterSample Sample, byte[] Vertices, byte[] Worlds, byte[] Skin)>();
        foreach (int mode in new[] { 0, 2, 3, 4, 1, 0, 4 })
        {
            var morphs = Pose(rig, mode);
            var next = animator.SkinWorldPose(worlds, morphs);
            Exact(OldSkin(rig, worlds, morphs), next.Vertices);
            foreach (var previous in held)
            {
                Assert.NotSame(previous.Sample.Vertices, next.Vertices);
                Assert.NotSame(previous.Sample.BoneWorldMatrices, next.BoneWorldMatrices);
                Assert.NotSame(previous.Sample.SkinMatrices, next.SkinMatrices);
                Assert.Equal(previous.Vertices, Bytes(previous.Sample.Vertices));
                Assert.Equal(previous.Worlds, Bytes(previous.Sample.BoneWorldMatrices));
                Assert.Equal(previous.Skin, Bytes(previous.Sample.SkinMatrices));
            }
            held.Add((next, Bytes(next.Vertices), Bytes(next.BoneWorldMatrices), Bytes(next.SkinMatrices)));
        }
        var expected = OldSkin(rig, worlds, Pose(rig, 4));
        held[^1].Sample.Vertices[0] = default;
        held[^1].Sample.BoneWorldMatrices[0] = default;
        held[^1].Sample.SkinMatrices[0] = default;
        Exact(expected, animator.SkinWorldPose(worlds, Pose(rig, 4)).Vertices);
    }

    [Fact]
    public void BorrowedMorphStorageStillCopiesInputsAndHasReadOnlyPublicViews()
    {
        var rig = MixedRig();
        var source = Pose(rig, 4)!;
        var deltas = source.VertexDeltas.ToArray();
        var uvs = source.VertexUvDeltas.ToArray();
        var translations = source.BoneTranslations.ToArray();
        var rotations = source.BoneRotations.ToArray();
        var morphs = new MorphPose(deltas, translations, rotations, uvs);
        var worlds = Worlds(rigid: true);
        var expected = OldSkin(rig, worlds, morphs);
        Array.Fill(deltas, new Vector3(999)); Array.Fill(uvs, new Vector2(888));
        Array.Fill(translations, new Vector3(777)); Array.Fill(rotations, Quaternion.Identity);
        Assert.False(morphs.VertexDeltas is Vector3[]);
        Assert.False(morphs.VertexUvDeltas is Vector2[]);
        Assert.Throws<NotSupportedException>(() => ((IList<Vector3>)morphs.VertexDeltas)[0] = Vector3.Zero);
        Assert.Throws<NotSupportedException>(() => ((IList<Vector2>)morphs.VertexUvDeltas)[0] = Vector2.Zero);
        Exact(expected, new CharacterAnimator(rig, new([])).SkinWorldPose(worlds, morphs).Vertices);
    }

    [Fact]
    public void QdefStillRejectsNonRigidTransformsAndZeroWeightBonesAreNotRead()
    {
        var rig = new CharacterRig([new("a", Vector3.Zero, -1)],
            [new SceneVertex(new(1, 2, 3), Vector3.UnitY, Vector2.Zero)],
            [new(SkinningKind.Qdef, -1, 0, -1, -1, new(NegativeZero, 1, 0, NegativeZero))]);
        var animator = new CharacterAnimator(rig, new([]));
        Matrix4x4[] rigid = [Matrix4x4.CreateRotationX(.7f) * Matrix4x4.CreateTranslation(2, 3, 4)];
        Exact(OldSkin(rig, rigid, null), animator.SkinWorldPose(rigid).Vertices);
        Assert.Throws<NotSupportedException>(() => animator.SkinWorldPose([Matrix4x4.CreateScale(2, 1, 1)]));
    }

    private static CharacterRig BdefRig()
    {
        SkinBinding[] bindings = [
            new(SkinningKind.Bdef1, 0, -1, -1, -1, new(1, NegativeZero, 0, NegativeZero)),
            new(SkinningKind.Bdef1, 3, -1, -1, -1, new(1, 0, 0, 0)),
            new(SkinningKind.Bdef2, 0, 1, -1, -1, new(.375f, .625f, 0, 0)),
            new(SkinningKind.Bdef2, -1, 2, -1, -1, new(NegativeZero, 1, 0, 0)),
            new(SkinningKind.Bdef2, 1, -1, -1, -1, new(1, NegativeZero, 0, 0)),
            new(SkinningKind.Bdef4, 0, 1, 2, 3, new(.1f, .2f, .3f, .4f)),
            new(SkinningKind.Bdef4, -1, 1, -1, 3, new(NegativeZero, .25f, NegativeZero, .75f)),
            new(SkinningKind.Bdef4, 3, 2, 1, -1, new(.99995f, .000025f, .000025f, NegativeZero))];
        Vector3[] positions = [new(.137f, -42.7f, 8.93f), new(NegativeZero, 0, NegativeZero),
            new(1e15f, -3e14f, 2e15f), new(float.Epsilon, -float.Epsilon, 1e-35f)];
        Vector3[] normals = [new(.37f, -.81f, .233f), new(NegativeZero, 0, NegativeZero),
            new(1e-7f, -2e-7f, 0), new(1e17f, -3e16f, 2e17f)];
        var vertices = new List<SceneVertex>(); var repeatedBindings = new List<SkinBinding>();
        foreach (var binding in bindings)
        for (int i = 0; i < positions.Length; i++)
        {
            vertices.Add(new(positions[i], normals[i], i == 1 ? new(NegativeZero, 0) : new(.173f, -.829f)));
            repeatedBindings.Add(binding);
        }
        return new(Bones(), vertices, repeatedBindings);
    }

    private static CharacterRig MixedRig()
    {
        var bdef = BdefRig();
        var vertices = bdef.Vertices.ToList(); var bindings = bdef.Bindings.ToList();
        foreach (var kind in new[] { SkinningKind.Sdef, SkinningKind.Qdef })
        foreach (bool endpoint in new[] { false, true })
        {
            vertices.Add(new(new(3.17f, -5.29f, 8.73f), new(.37f, -.81f, .233f), new(.173f, -.829f)));
            bindings.Add(new(kind, endpoint ? -1 : 0, 1, -1, kind == SkinningKind.Qdef && !endpoint ? 3 : -1,
                endpoint ? new(NegativeZero, 1, 0, NegativeZero) : kind == SkinningKind.Sdef
                    ? new(.37f, .63f, 0, 0) : new(.2f, .3f, 0, .5f),
                new(1.3f, -2.9f, 4.7f), new(-3.1f, 5.3f, 2.7f), new(4.1f, -1.7f, 6.3f)));
        }
        return new(Bones(), vertices, bindings);
    }

    private static RigBone[] Bones() => [new("a", new(1.1f, -2.3f, .7f), -1),
        new("b", new(-3.7f, 2.9f, 1.3f), -1), new("c", new(7.3f, -5.1f, 4.9f), -1),
        new("d", new(-2.1f, 9.7f, -8.3f), -1)];

    private static Matrix4x4[] Worlds(bool rigid)
    {
        var result = new Matrix4x4[4];
        for (int i = 0; i < result.Length; i++)
        {
            var rotation = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(.13f + i * .19f, -.47f, .23f, .61f - i * .11f)));
            var scale = rigid ? Matrix4x4.Identity : Matrix4x4.CreateScale(.7f + i * .3f, 1.9f - i * .2f, 1.1f + i * .1f);
            if (!rigid) scale.M12 = .073f * (i + 1);
            result[i] = scale * rotation * Matrix4x4.CreateTranslation(3.7f - i * 5.3f, -8.1f + i * .7f, 2.9f + i * 1.1f);
        }
        return result;
    }

    // 0 absent, 1 signed-zero no-op, 2 positions only, 3 UVs only, 4 both.
    private static MorphPose? Pose(CharacterRig rig, int mode)
    {
        if (mode == 0) return null;
        var deltas = Enumerable.Range(0, rig.Vertices.Count).Select(i => mode is 2 or 4 && i % 3 != 0
            ? new Vector3(.19f * (i + 1), -.37f, .83f) : new Vector3(NegativeZero, 0, NegativeZero));
        var uvs = Enumerable.Range(0, rig.Vertices.Count).Select(i => mode is 3 or 4 && i % 2 != 0
            ? new Vector2(-.137f, .251f * (i + 1)) : new Vector2(NegativeZero, 0));
        return new(deltas, new Vector3[rig.Bones.Count], Enumerable.Repeat(Quaternion.Identity, rig.Bones.Count), uvs);
    }

    private static Matrix4x4[] Palette(CharacterRig rig, Matrix4x4[] worlds)
        => rig.Bones.Select((bone, i) => Matrix4x4.CreateTranslation(-bone.RestPosition) * worlds[i]).ToArray();

    // Frozen pre-optimization arithmetic: four slot switches, zero skip before bone
    // lookup, individual Vector3 transforms, weighted addition in slot order, then
    // the exact original normal fallback. Never calls the new BDEF helper.
    private static SceneVertex[] OldSkin(CharacterRig rig, Matrix4x4[] worlds, MorphPose? morphs)
    {
        var skin = Palette(rig, worlds);
        var rotations = rig.Bindings.Any(b => b.Kind == SkinningKind.Sdef)
            ? worlds.Select(m => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m))).ToArray() : [];
        var result = new SceneVertex[rig.Vertices.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var vertex = rig.Vertices[i]; var binding = rig.Bindings[i];
            if (morphs is not null && morphs.VertexDeltas[i] != Vector3.Zero)
                vertex = vertex with { Position = vertex.Position + morphs.VertexDeltas[i] };
            if (morphs is not null && morphs.VertexUvDeltas[i] != Vector2.Zero)
                vertex = vertex with { Uv = vertex.Uv + morphs.VertexUvDeltas[i] };
            Vector3 position = Vector3.Zero, normal = Vector3.Zero;
            if (binding.Kind == SkinningKind.Sdef)
            {
                int a = binding.Weights.X > 0 ? binding.Bone0 : binding.Bone1;
                int b = binding.Weights.Y > 0 ? binding.Bone1 : binding.Bone0;
                var rotation = Quaternion.Normalize(Quaternion.Slerp(rotations[a], rotations[b], binding.Weights.Y));
                var weightedR = binding.SdefR0 * binding.Weights.X + binding.SdefR1 * binding.Weights.Y;
                var anchor0 = binding.SdefC + (binding.SdefR0 - weightedR) * .5f;
                var anchor1 = binding.SdefC + (binding.SdefR1 - weightedR) * .5f;
                var offset = Vector3.Transform(anchor0, skin[a]) * binding.Weights.X
                    + Vector3.Transform(anchor1, skin[b]) * binding.Weights.Y;
                position = Vector3.Transform(vertex.Position - binding.SdefC, rotation) + offset;
                normal = Vector3.Transform(vertex.Normal, rotation);
            }
            else if (binding.Kind == SkinningKind.Qdef)
            {
                var (rotation, translation) = OldQdef(binding, skin);
                position = Vector3.Transform(vertex.Position, rotation) + translation;
                normal = Vector3.Transform(vertex.Normal, rotation);
            }
            else for (int slot = 0; slot < 4; slot++)
            {
                float weight = Weight(binding, slot);
                if (weight == 0) continue;
                var matrix = skin[Bone(binding, slot)];
                position += Vector3.Transform(vertex.Position, matrix) * weight;
                normal += Vector3.TransformNormal(vertex.Normal, matrix) * weight;
            }
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal)
                : vertex.Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(vertex.Normal) : Vector3.Zero;
            result[i] = vertex with { Position = position, Normal = normal };
        }
        return result;
    }

    private static (Quaternion Rotation, Vector3 Translation) OldQdef(SkinBinding binding, Matrix4x4[] skin)
    {
        Quaternion reference = default, real = default, dual = default;
        bool hasReference = false;
        for (int slot = 0; slot < 4; slot++)
        {
            float weight = Weight(binding, slot);
            if (weight == 0) continue;
            var matrix = skin[Bone(binding, slot)];
            var partReal = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));
            var partDual = (new Quaternion(matrix.Translation, 0) * partReal) * .5f;
            if (!hasReference) { reference = partReal; hasReference = true; }
            if (Quaternion.Dot(reference, partReal) < 0) weight = -weight;
            real += partReal * weight; dual += partDual * weight;
        }
        float inverseNorm = 1 / MathF.Sqrt(real.LengthSquared());
        real *= inverseNorm; dual *= inverseNorm;
        dual -= real * Quaternion.Dot(real, dual);
        var translation = (dual * Quaternion.Conjugate(real)) * 2;
        return (real, new(translation.X, translation.Y, translation.Z));
    }

    private static int Bone(SkinBinding b, int slot) => slot switch { 0 => b.Bone0, 1 => b.Bone1, 2 => b.Bone2, _ => b.Bone3 };
    private static float Weight(SkinBinding b, int slot) => slot switch { 0 => b.Weights.X, 1 => b.Weights.Y, 2 => b.Weights.Z, _ => b.Weights.W };
    private static byte[] Bytes<T>(T[] values) where T : struct => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static void Exact<T>(T[] expected, T[] actual) where T : struct => Assert.Equal(Bytes(expected), Bytes(actual));
}
