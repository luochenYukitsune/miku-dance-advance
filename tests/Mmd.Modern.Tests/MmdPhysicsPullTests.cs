using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class MmdPhysicsPullTests
{
    [Fact]
    public void OriginalOverextensionFixturePullsUnboundBodyAndClearsBothVelocities()
    {
        var definition = Definition();
        using var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null);
        using var session = new CharacterPhysicsSession(definition, backend);
        var displaced = Matrix4x4.CreateRotationY(.4f) * Matrix4x4.CreateTranslation(10, 0, 0);
        backend.ResetBody(1, displaced);
        backend.ApplyImpulse(1, new(2, 3, 4), new(.1f, .2f, .3f));

        Assert.Empty(session.ReflectPose([]));
        var corrected = session.GetBodyWorldTransform(1);
        Assert.Equal(new Vector3(3, 0, 0), corrected.Translation);
        displaced.Translation = corrected.Translation;
        Near(displaced, corrected);
        // Remove the solver constraint so it cannot hide surviving linear/angular momentum.
        backend.RemoveJoint(0);
        backend.Step(1f / 120);
        Near(corrected, session.GetBodyWorldTransform(1));
        Assert.Equal(Matrix4x4.Identity, session.GetBodyWorldTransform(0));
    }

    [Theory]
    [InlineData(4.999f, 4.999f)]
    [InlineData(5f, 3f)]
    public void OriginalOverextensionThresholdIncludesExactlyTwoUnits(float distance, float expected)
    {
        using var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null);
        using var session = new CharacterPhysicsSession(Definition(), backend);
        backend.ResetBody(1, Matrix4x4.CreateTranslation(distance, 0, 0));
        session.ReflectPose([]);
        Assert.Equal(expected, session.GetBodyWorldTransform(1).Translation.X);
    }

    [Fact]
    public void LoaderMaximumIncludesJointOffsetsAndLinearLimitVectorLength()
    {
        // |joint - A|=1, |joint - B|=2, |max(abs(lower),abs(upper))|=5 => max=8.
        using var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null);
        using var session = new CharacterPhysicsSession(Definition(new(1, 0, 0), new(-3, 0, 0), new(1, 4, 0)), backend);
        backend.ResetBody(1, Matrix4x4.CreateTranslation(10, 0, 0));
        session.ReflectPose([]);
        Assert.Equal(new Vector3(8, 0, 0), session.GetBodyWorldTransform(1).Translation);
    }

    [Fact]
    public void ExplicitSabaReflectionKeepsUnboundBodyAndMomentum()
    {
        using var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null, mmdJointPullCorrection: false);
        using var session = new CharacterPhysicsSession(Definition(), backend);
        backend.ResetBody(1, Matrix4x4.CreateTranslation(10, 0, 0));
        backend.ApplyImpulse(1, new(2, 0, 0), Vector3.Zero);
        session.ReflectPose([]);
        Assert.Equal(10, session.GetBodyWorldTransform(1).Translation.X);
        backend.RemoveJoint(0); backend.Step(1f / 120);
        Assert.True(session.GetBodyWorldTransform(1).Translation.X > 10);
    }

    [Fact]
    public void LoaderMaximumRejectsOverflowDuringDefinitionPreparation()
    {
        Assert.Throws<ArgumentException>(() => Definition(lower: new(float.MaxValue, 0, 0)));
    }

    [Fact]
    public void NativeCapabilityIsPreflightedAndExplicitSabaWorksWithEitherAbi()
    {
        // Root also runs this single test in an isolated output directory containing the retained ABI4/bit1 DLL.
        IntPtr module = NativeLibrary.Load("mmd_bullet", typeof(BulletCharacterPhysicsBackend).Assembly, null);
        bool supported;
        try
        {
            supported = NativeLibrary.TryGetExport(module, "mmd_bullet_features", out var entry)
                && (Marshal.GetDelegateForFunctionPointer<Features>(entry)() & 2) != 0
                && NativeLibrary.TryGetExport(module, "mmd_bullet_correct_joint_overextension", out _);
        }
        finally { NativeLibrary.Free(module); }
        using var mmd = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null);
        var definition = Definition();
        mmd.Initialize(new([], [definition.Bodies[0]], []), [Matrix4x4.CreateTranslation(7, 0, 0)]);
        if (supported) mmd.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
        else
        {
            Assert.Throws<NotSupportedException>(() => mmd.Initialize(definition, definition.BodyRestWorldTransforms.ToArray()));
            Assert.Equal(7, mmd.GetBodyWorldTransform(0).Translation.X);
        }
        using var incremental = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null);
        incremental.Initialize(new([], definition.Bodies, []), definition.BodyRestWorldTransforms.ToArray());
        int AddIncrementalJoint() => incremental.AddJoint(definition.Joints[0], definition.JointLocalFramesA[0], definition.JointLocalFramesB[0]);
        if (supported) Assert.Equal(0, AddIncrementalJoint());
        else Assert.Throws<NotSupportedException>(() => AddIncrementalJoint());
        Assert.Equal(new Vector3(3, 0, 0), incremental.GetBodyWorldTransform(1).Translation);
        using var saba = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null, mmdJointPullCorrection: false);
        using var session = new CharacterPhysicsSession(definition, saba);
        saba.ResetBody(1, Matrix4x4.CreateTranslation(10, 0, 0));
        session.ReflectPose([]);
        Assert.Equal(10, session.GetBodyWorldTransform(1).Translation.X);

        using var shared = new SharedCharacterPhysicsSession(new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null));
        var retainedId = Guid.NewGuid(); var candidateId = Guid.NewGuid();
        var existing = new CharacterPhysicsDefinition([], [definition.Bodies[0] with { Position = new(7, 0, 0) }], []);
        shared.AddActor(retainedId, existing, Matrix4x4.Identity, []);
        if (supported)
        {
            shared.AddActor(candidateId, definition, Matrix4x4.Identity, []);
            shared.ReplaceActor(retainedId, definition, Matrix4x4.Identity, []);
            Assert.Equal(2, shared.ActorIds.Count);
        }
        else
        {
            Assert.Throws<NotSupportedException>(() => shared.AddActor(candidateId, definition, Matrix4x4.Identity, []));
            Assert.Throws<NotSupportedException>(() => shared.ReplaceActor(retainedId, definition, Matrix4x4.Identity, []));
            Assert.False(shared.IsFaulted);
            Assert.Equal(retainedId, Assert.Single(shared.ActorIds));
            Assert.Equal(7, shared.GetBodyWorldTransform(retainedId, 0).Translation.X);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Features();

    private static CharacterPhysicsDefinition Definition(Vector3 jointPosition = default, Vector3 lower = default, Vector3 upper = default)
    {
        PhysicsBodyDefinition Body(Vector3 p) => new("body", -1, PhysicsShapeKind.Sphere, Vector3.One,
            p, Vector3.Zero, 1, 0, 0, 0, 0, 0, 0, PhysicsBodyMode.Dynamic);
        return new([], [Body(Vector3.Zero), Body(new(3, 0, 0))],
            [new("joint", 0, 1, 0, jointPosition, Vector3.Zero, lower, upper, Vector3.Zero,
                Vector3.Zero, Vector3.Zero, Vector3.Zero)]);
    }

    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.InRange(Vector3.Distance(expected.Translation, actual.Translation), 0, 1e-6f);
        Assert.InRange(MathF.Abs(Quaternion.Dot(Quaternion.CreateFromRotationMatrix(expected),
            Quaternion.CreateFromRotationMatrix(actual))) - 1, -1e-6f, 1e-6f);
    }
}
