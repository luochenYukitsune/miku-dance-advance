using System.Numerics;

namespace Mmd.Modern.Animation;

/// <summary>Owned evaluation spanning one physics step. Completion consumes it; disposal may discard it.</summary>
public sealed class CharacterPhysicsSample : IDisposable
{
    private readonly object sync = new();
    private readonly Matrix4x4[] beforePhysicsWorlds;
    private readonly Matrix4x4[] world;
    private readonly CharacterEvaluationOptions? options;
    private CharacterAnimator? animator;
    private MmdConstraintEvaluator? evaluator;
    private bool completed;
    private bool disposed;

    internal CharacterPhysicsSample(CharacterAnimator animator, MmdConstraintEvaluator evaluator, Matrix4x4[] world,
        CharacterEvaluationOptions? options)
    {
        this.animator = animator; this.evaluator = evaluator; this.world = world; this.options = options;
        beforePhysicsWorlds = (Matrix4x4[])world.Clone();
    }

    /// <summary>A stable, read-only snapshot independent from the state completed by physics.</summary>
    public ReadOnlySpan<Matrix4x4> BeforePhysicsBoneWorldMatrices => beforePhysicsWorlds;

    /// <summary>Import a complete bone-world pose, reflecting only the supplied driven bones, then evaluate the after-physics phase and skin.</summary>
    public CharacterSample Complete(ReadOnlySpan<Matrix4x4> physicsWorlds, IReadOnlyCollection<int> reflectedBoneIndices)
    {
        lock (sync)
        {
            var indices = ValidateCompletion(physicsWorlds, reflectedBoneIndices);
            completed = true;
            try { return animator!.CompletePhysicsSample(evaluator!, world, physicsWorlds, indices, options); }
            finally { animator = null; evaluator = null; }
        }
    }

    /// <summary>Completes reflection and after-physics animation without skinning vertices. The returned
    /// bone worlds are caller-owned; use SkinWorldPose when this physical boundary needs a rendered pose.</summary>
    public Matrix4x4[] CompleteBoneWorldMatrices(ReadOnlySpan<Matrix4x4> physicsWorlds, IReadOnlyCollection<int> reflectedBoneIndices)
    {
        lock (sync)
        {
            var indices = ValidateCompletion(physicsWorlds, reflectedBoneIndices);
            completed = true;
            try { return animator!.CompletePhysicsBoneWorlds(evaluator!, world, physicsWorlds, indices, options); }
            finally { animator = null; evaluator = null; }
        }
    }

    private HashSet<int> ValidateCompletion(ReadOnlySpan<Matrix4x4> physicsWorlds, IReadOnlyCollection<int> reflectedBoneIndices)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("A physics sample can only be completed once.");
        ArgumentNullException.ThrowIfNull(reflectedBoneIndices);
        if (physicsWorlds.Length != world.Length) throw new ArgumentException("Physics pose bone count differs from the rig.", nameof(physicsWorlds));
        var indices = new HashSet<int>();
        foreach (int index in reflectedBoneIndices)
            if (index < 0 || index >= world.Length || !indices.Add(index))
                throw new ArgumentException("Reflected bone indices must be valid and unique.", nameof(reflectedBoneIndices));
        foreach (var transform in physicsWorlds) ValidateWorld(transform);
        return indices;
    }

    private static void ValidateWorld(Matrix4x4 value)
    {
        var x = new Vector3(value.M11, value.M12, value.M13);
        var y = new Vector3(value.M21, value.M22, value.M23);
        var z = new Vector3(value.M31, value.M32, value.M33);
        if (!AnimationValidation.Finite(x) || !AnimationValidation.Finite(y) || !AnimationValidation.Finite(z)
            || !AnimationValidation.Finite(value.Translation) || !float.IsFinite(value.M14) || !float.IsFinite(value.M24)
            || !float.IsFinite(value.M34) || !float.IsFinite(value.M44)
            || MathF.Abs(value.M14) + MathF.Abs(value.M24) + MathF.Abs(value.M34) > 1e-6f || MathF.Abs(value.M44 - 1) > 1e-6f
            || MathF.Abs(x.LengthSquared() - 1) + MathF.Abs(y.LengthSquared() - 1) + MathF.Abs(z.LengthSquared() - 1) > 1e-3f
            || MathF.Abs(Vector3.Dot(x, y)) + MathF.Abs(Vector3.Dot(x, z)) + MathF.Abs(Vector3.Dot(y, z)) > 1e-3f
            || MathF.Abs(Vector3.Dot(Vector3.Cross(x, y), z) - 1) > 1e-3f)
            throw new ArgumentException("Physics bone worlds must be finite rigid affine transforms.", "physicsWorlds");
    }

    public void Dispose()
    {
        lock (sync) { disposed = true; animator = null; evaluator = null; }
    }
}
