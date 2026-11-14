using System.Numerics;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class PhysicsAffineReflectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepRotatedTranslatedChainRemainsRigidAndExactlyAffineAfterReflection(bool drivenRoot)
    {
        const int count = 64;
        var pose = Enumerable.Range(0, count).Select(i => Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(
            Quaternion.CreateFromYawPitchRoll(.17f + i * .13f, -.23f + i * .07f, .31f + i * .09f)))
            * Matrix4x4.CreateTranslation(2 + i * .2f, 10 + i * .3f, -100 + i * .1f)).ToArray();
        var bones = pose.Select((world, i) => new PhysicsBoneDefinition(i - 1, world)).ToArray();
        PhysicsBodyDefinition[] bodies = drivenRoot ? [Body()] : [];
        var definition = new CharacterPhysicsDefinition(bones, bodies, []);
        using var session = new CharacterPhysicsSession(definition, new RetainedBackend());
        session.Reset(pose);
        var result = session.Advance(0, pose);
        for (int i = 0; i < count; i++)
        {
            PhysicsMath.ValidateRigid(result[i], $"bone {i}");
            Assert.Equal(0, result[i].M14); Assert.Equal(0, result[i].M24); Assert.Equal(0, result[i].M34);
            Assert.Equal(1, result[i].M44);
            Assert.InRange(Vector3.Distance(pose[i].Translation, result[i].Translation), 0, .002f);
            var expected = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(pose[i]));
            var actual = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(result[i]));
            Assert.InRange(MathF.Abs(Quaternion.Dot(expected, actual)), .99999f, 1.00001f);
        }
    }

    [Fact] public void AffineCorrectionDoesNotAcceptScaledShearedOrNonfiniteInput()
    {
        var definition = new CharacterPhysicsDefinition([new(-1, Matrix4x4.Identity)], [], []);
        using var session = new CharacterPhysicsSession(definition, new RetainedBackend());
        foreach (var invalid in InvalidMatrices())
        {
            Assert.Throws<ArgumentException>(() => session.Reset([invalid]));
            Assert.Throws<ArgumentException>(() => session.Advance(0, [invalid]));
        }
    }

    [Fact] public void AffineCorrectionDoesNotHideAnInvalidBackendBodyTransform()
    {
        var definition = new CharacterPhysicsDefinition([new(-1, Matrix4x4.Identity)], [Body()], []);
        var backend = new RetainedBackend();
        using var session = new CharacterPhysicsSession(definition, backend);
        foreach (var invalid in InvalidMatrices())
        {
            backend.InvalidOutput = invalid;
            Assert.Throws<ArgumentException>(() => session.Advance(0, [Matrix4x4.Identity]));
        }
    }

    private static IEnumerable<Matrix4x4> InvalidMatrices()
    {
        yield return Matrix4x4.CreateScale(1.01f, 1, 1);
        var shear = Matrix4x4.Identity; shear.M12 = .001f; yield return shear;
        var nonfinite = Matrix4x4.Identity; nonfinite.M44 = float.NaN; yield return nonfinite;
        var perspective = Matrix4x4.Identity; perspective.M14 = .01f; yield return perspective;
    }
    private static PhysicsBodyDefinition Body() => new("driven", 0, PhysicsShapeKind.Sphere, new(.1f),
        new(2.3f, 10.4f, -99.8f), new(.2f, .3f, .4f), 1, 0, 0, 0, .5f, 0, 0, PhysicsBodyMode.Dynamic);
    private sealed class RetainedBackend : ICharacterPhysicsBackend
    {
        private Matrix4x4[] transforms = [];
        public Matrix4x4? InvalidOutput;
        public string Name => "retained body fixture";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> bodyWorldTransforms) => transforms = bodyWorldTransforms.ToArray();
        public Matrix4x4 GetBodyWorldTransform(int index) => InvalidOutput ?? transforms[index];
        public void SetKinematicTarget(int index, Matrix4x4 world, float seconds) => transforms[index] = world;
        public void Step(float seconds) { }
        public void Dispose() { }
    }
}
