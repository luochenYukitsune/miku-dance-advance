using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class BulletModeCacheTests
{
    private const float Dt = 1f / 120;

    [Theory]
    [InlineData(PhysicsBodyMode.Kinematic, 1f)]
    [InlineData(PhysicsBodyMode.Dynamic, 1f)]
    [InlineData(PhysicsBodyMode.DynamicAligned, 1f)]
    [InlineData(PhysicsBodyMode.Dynamic, 0f)]
    [InlineData(PhysicsBodyMode.DynamicAligned, 0f)]
    public void KnownTemporaryStateSkipsNativeCallsButActualTransitionsReachNative(PhysicsBodyMode mode, float mass)
    {
        using var backend = Initialized(mode, mass);
        using var native = new NativeProbe();

        // Every guarded native mode call clears its thread-local error, including its
        // native early return. Retaining this marker proves that all 4096 calls skipped
        // the ABI without adding a diagnostic counter to the playback path.
        string marker = native.MarkLastError();
        for (int i = 0; i < 4096; i++) backend.SetBodyKinematic(0, false);
        Assert.Equal(marker, native.LastError);

        if (mode == PhysicsBodyMode.Kinematic)
        {
            for (int i = 0; i < 4096; i++) backend.SetBodyKinematic(0, true);
            Assert.Equal(marker, native.LastError);
        }
        else
        {
            backend.SetBodyKinematic(0, true);
            Assert.Equal(string.Empty, native.LastError);
            backend.SetKinematicTarget(0, Matrix4x4.Identity, Dt);
            marker = native.MarkLastError();
            for (int i = 0; i < 4096; i++) backend.SetBodyKinematic(0, true);
            Assert.Equal(marker, native.LastError);
            backend.SetBodyKinematic(0, false);
            Assert.Equal(string.Empty, native.LastError);
            Assert.Throws<ArgumentException>(() => backend.SetKinematicTarget(0, Matrix4x4.Identity, Dt));
        }
    }

    [Theory]
    [InlineData(PhysicsBodyMode.Dynamic)]
    [InlineData(PhysicsBodyMode.DynamicAligned)]
    public void RepeatedModesPreserveNativeMotionFreezeTargetAndResume(PhysicsBodyMode mode)
    {
        using var backend = Initialized(mode);
        backend.ApplyImpulse(0, new(2, 0, 0), Vector3.Zero);
        for (int i = 0; i < 120; i++)
        {
            backend.SetBodyKinematic(0, false);
            backend.Step(Dt);
        }
        Assert.InRange(backend.GetBodyWorldTransform(0).Translation.X, 1.99f, 2.01f);

        backend.SetBodyKinematic(0, true);
        var frozen = Matrix4x4.CreateRotationZ(.4f) * Matrix4x4.CreateTranslation(5, 3, 0);
        backend.SetKinematicTarget(0, frozen, Dt);
        backend.ApplyImpulse(0, new(100, 0, 0), Vector3.Zero);
        for (int i = 0; i < 30; i++)
        {
            backend.SetBodyKinematic(0, true);
            backend.Step(Dt);
        }
        Near(frozen, backend.GetBodyWorldTransform(0));

        backend.SetBodyKinematic(0, false);
        Assert.Throws<ArgumentException>(() => backend.SetKinematicTarget(0, frozen, Dt));
        backend.ApplyImpulse(0, new(3, 0, 0), Vector3.Zero);
        for (int i = 0; i < 120; i++)
        {
            backend.SetBodyKinematic(0, false);
            backend.Step(Dt);
        }
        Assert.InRange(backend.GetBodyWorldTransform(0).Translation.X, 7.99f, 8.01f);
    }

    [Fact]
    public void AuthoredKinematicBodiesKeepAcceptingTargetsForBothBooleanRequests()
    {
        using var backend = Initialized(PhysicsBodyMode.Kinematic);
        for (int i = 0; i < 8; i++)
        {
            backend.SetBodyKinematic(0, i % 2 == 0);
            var target = Matrix4x4.CreateRotationZ(i * .1f) * Matrix4x4.CreateTranslation(i, 3, 0);
            backend.SetKinematicTarget(0, target, Dt);
            backend.Step(Dt);
            Near(target, backend.GetBodyWorldTransform(0));
        }
    }

    [Fact]
    public void AddResetRemoveAndReinitializeKeepTemporaryStateWithTheNativeSlot()
    {
        using var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null);
        backend.Initialize(new([], [], []), []);
        using var native = new NativeProbe();
        int first = backend.AddBody(Body(PhysicsBodyMode.Dynamic), Matrix4x4.Identity);
        string marker = native.MarkLastError();
        backend.SetBodyKinematic(first, false);
        Assert.Equal(marker, native.LastError);

        backend.SetBodyKinematic(first, true);
        var reset = Matrix4x4.CreateRotationY(.3f) * Matrix4x4.CreateTranslation(4, 2, 1);
        backend.ResetBody(first, reset);
        marker = native.MarkLastError();
        backend.SetBodyKinematic(first, true);
        Assert.Equal(marker, native.LastError);
        backend.Step(Dt);
        Near(reset, backend.GetBodyWorldTransform(first));
        var target = Matrix4x4.CreateTranslation(6, 2, 1);
        backend.SetKinematicTarget(first, target, Dt);
        backend.Step(Dt);
        Near(target, backend.GetBodyWorldTransform(first));

        backend.RemoveBody(first);
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.SetBodyKinematic(first, true));
        int second = backend.AddBody(Body(PhysicsBodyMode.DynamicAligned), Matrix4x4.Identity);
        Assert.True(second > first); // The native world appends slots; removed slots stay absent.
        marker = native.MarkLastError();
        backend.SetBodyKinematic(second, false);
        Assert.Equal(marker, native.LastError);
        Assert.Throws<ArgumentException>(() => backend.SetKinematicTarget(second, target, Dt));
        backend.ResetBody(second, Matrix4x4.CreateTranslation(2, 0, 0));
        backend.ApplyImpulse(second, new(1, 0, 0), Vector3.Zero);
        Step(backend, 120);
        Assert.InRange(backend.GetBodyWorldTransform(second).Translation.X, 2.99f, 3.01f);

        backend.SetBodyKinematic(second, true);
        var definition = Definition(PhysicsBodyMode.Dynamic);
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
        marker = native.MarkLastError();
        backend.SetBodyKinematic(0, false); // New world reuses slot zero with initial false state.
        Assert.Equal(marker, native.LastError);
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.SetBodyKinematic(second, true));
        backend.ApplyImpulse(0, new(1, 0, 0), Vector3.Zero);
        Step(backend, 120);
        Assert.InRange(backend.GetBodyWorldTransform(0).Translation.X, .99f, 1.01f);
    }

    [Fact]
    public void FailedInitializeRetainsFrozenStateAndSuccessfulReinitializeClearsIt()
    {
        using var backend = Initialized(PhysicsBodyMode.Dynamic);
        backend.SetBodyKinematic(0, true);
        var frozen = Matrix4x4.CreateTranslation(7, 4, 2);
        backend.SetKinematicTarget(0, frozen, Dt);
        backend.Step(Dt);
        var definition = Definition(PhysicsBodyMode.Dynamic);
        Assert.Throws<ArgumentException>(() => backend.Initialize(definition, [Matrix4x4.CreateScale(2)]));

        using var native = new NativeProbe();
        string marker = native.MarkLastError();
        backend.SetBodyKinematic(0, true);
        Assert.Equal(marker, native.LastError);
        backend.SetKinematicTarget(0, frozen, Dt);
        backend.Step(Dt);
        Near(frozen, backend.GetBodyWorldTransform(0));

        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
        marker = native.MarkLastError();
        backend.SetBodyKinematic(0, false); // Same slot previously frozen in the old world.
        Assert.Equal(marker, native.LastError);
        Assert.Throws<ArgumentException>(() => backend.SetKinematicTarget(0, frozen, Dt));
        backend.ApplyImpulse(0, new(1, 0, 0), Vector3.Zero);
        Step(backend, 120);
        Assert.InRange(backend.GetBodyWorldTransform(0).Translation.X, .99f, 1.01f);
    }

    [Fact]
    public void RedundantRequestsStillValidateInitializationBodyAndOwnerLifetime()
    {
        using var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null);
        Assert.Throws<InvalidOperationException>(() => backend.SetBodyKinematic(0, false));
        backend.Initialize(new([], [], []), []);
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.SetBodyKinematic(0, false));
        int slot = backend.AddBody(Body(PhysicsBodyMode.Dynamic), Matrix4x4.Identity);
        backend.SetBodyKinematic(slot, true);
        backend.RemoveBody(slot);
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.SetBodyKinematic(slot, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.SetBodyKinematic(slot, false));
        backend.Dispose();
        Assert.Throws<ObjectDisposedException>(() => backend.SetBodyKinematic(slot, false));
        Assert.Throws<ObjectDisposedException>(() => backend.SetBodyKinematic(slot, true));
    }

    [Fact]
    public void NativeModeFailureDisablesSkippingUntilANewWorldIsPublished()
    {
        using var backend = Initialized(PhysicsBodyMode.Dynamic);
        using var native = new NativeProbe();
        // Deliberately invalidate only the native slot to provoke a real ABI failure
        // while keeping a valid owned world. Disposal still owns all remaining state.
        native.RemoveBody(backend, 0);
        var failure = Assert.Throws<InvalidOperationException>(() => backend.SetBodyKinematic(0, true));
        Assert.Contains("change body mode (status 1)", failure.Message);
        Assert.Contains("Body index out of range or removed.", failure.Message);
        // false matches the last managed state, but that state is no longer trusted.
        var repeated = Assert.Throws<InvalidOperationException>(() => backend.SetBodyKinematic(0, false));
        Assert.Equal(failure.Message, repeated.Message);

        var definition = Definition(PhysicsBodyMode.Dynamic);
        backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
        string marker = native.MarkLastError();
        backend.SetBodyKinematic(0, false);
        Assert.Equal(marker, native.LastError);
        backend.SetBodyKinematic(0, true);
        Assert.Equal(string.Empty, native.LastError);
        var target = Matrix4x4.CreateTranslation(3, 4, 5);
        backend.SetKinematicTarget(0, target, Dt);
        backend.Step(Dt);
        Near(target, backend.GetBodyWorldTransform(0));
    }

    private static BulletCharacterPhysicsBackend Initialized(PhysicsBodyMode mode, float mass = 1)
    {
        var backend = new BulletCharacterPhysicsBackend(Vector3.Zero, groundY: null);
        var definition = Definition(mode, mass);
        try
        {
            backend.Initialize(definition, definition.BodyRestWorldTransforms.ToArray());
            return backend;
        }
        catch { backend.Dispose(); throw; }
    }

    private static PhysicsBodyDefinition Body(PhysicsBodyMode mode) => new("body", -1,
        PhysicsShapeKind.Sphere, new(.5f, 1, .5f), Vector3.Zero, Vector3.Zero, 1, 0, 0, 0, .5f, 0, 0, mode);
    private static CharacterPhysicsDefinition Definition(PhysicsBodyMode mode, float mass = 1) => new([], [Body(mode) with { Mass = mass }], []);
    private static void Step(BulletCharacterPhysicsBackend backend, int count)
    {
        for (int i = 0; i < count; i++) backend.Step(Dt);
    }

    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.InRange(Vector3.Distance(expected.Translation, actual.Translation), 0, 1e-4f);
        var a = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(expected));
        var b = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(actual));
        Assert.InRange(MathF.Min((a - b).LengthSquared(), (a + b).LengthSquared()), 0, 1e-8f);
    }

    private sealed class NativeProbe : IDisposable
    {
        private readonly IntPtr library = NativeLibrary.Load("mmd_bullet", typeof(BulletCharacterPhysicsBackend).Assembly, null);
        private readonly SetMode setMode;
        private readonly RemoveSlot removeSlot;
        private readonly ErrorPointer errorPointer;

        public NativeProbe()
        {
            setMode = Marshal.GetDelegateForFunctionPointer<SetMode>(NativeLibrary.GetExport(library, "mmd_bullet_set_body_kinematic"));
            removeSlot = Marshal.GetDelegateForFunctionPointer<RemoveSlot>(NativeLibrary.GetExport(library, "mmd_bullet_remove_body"));
            errorPointer = Marshal.GetDelegateForFunctionPointer<ErrorPointer>(NativeLibrary.GetExport(library, "mmd_bullet_last_error"));
        }

        public string LastError => Marshal.PtrToStringUTF8(errorPointer()) ?? string.Empty;
        public string MarkLastError()
        {
            Assert.Equal(1, setMode(IntPtr.Zero, 0, 0));
            string marker = LastError;
            Assert.Equal("Null Bullet context.", marker);
            return marker;
        }

        public void RemoveBody(BulletCharacterPhysicsBackend backend, int slot)
        {
            var handle = (SafeHandle)typeof(BulletCharacterPhysicsBackend)
                .GetField("world", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend)!;
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                Assert.Equal(0, removeSlot(handle.DangerousGetHandle(), slot));
            }
            finally { if (added) handle.DangerousRelease(); }
        }

        public void Dispose() => NativeLibrary.Free(library);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetMode(IntPtr world, int index, int enabled);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RemoveSlot(IntPtr world, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorPointer();
    }
}
