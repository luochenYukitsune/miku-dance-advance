using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bepu;
using Mmd.Modern.Physics.Bullet;
using Mmd.Modern.Physics.Runtime;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ProjectPhysicsRuntimeTests
{
    [Fact]
    public void DefaultProjectSettingsKeepBulletWithoutCoupledSoftBodies()
    {
        using var backend = ProjectPhysicsRuntime.CreateRigid(new ProjectPhysicsSettings());
        Assert.False(Assert.IsType<BulletCharacterPhysicsBackend>(backend).SupportsCoupledSoftBodies);
    }

    [Theory]
    [InlineData(ProjectPhysicsBackend.HostDefault, false, typeof(BulletCharacterPhysicsBackend))]
    [InlineData(ProjectPhysicsBackend.HostDefault, true, typeof(BulletCharacterPhysicsBackend))]
    [InlineData(ProjectPhysicsBackend.Bullet, false, typeof(BulletCharacterPhysicsBackend))]
    [InlineData(ProjectPhysicsBackend.Bullet, true, typeof(BulletCharacterPhysicsBackend))]
    [InlineData(ProjectPhysicsBackend.Bepu, false, typeof(BepuCharacterPhysicsBackend))]
    public void RigidFactoryPreservesBackendSelectionAndCoupledCapability(
        ProjectPhysicsBackend selected, bool coupled, Type expectedType)
    {
        using var explicitBackend = ProjectPhysicsRuntime.CreateRigid(selected, coupledSoftBodies: coupled);
        using var settingsBackend = ProjectPhysicsRuntime.CreateRigid(new ProjectPhysicsSettings
        { Backend = selected, CoupledSoftBodies = coupled });

        foreach (var backend in new[] { explicitBackend, settingsBackend })
        {
            Assert.Equal(expectedType, backend.GetType());
            if (backend is BulletCharacterPhysicsBackend bullet)
                Assert.Equal(coupled, bullet.SupportsCoupledSoftBodies);
            else
                Assert.False(backend is ICharacterSoftBodyProvider);
        }
    }

    [Theory]
    [InlineData(ProjectPhysicsBackend.HostDefault, typeof(BulletCharacterPhysicsBackend))]
    [InlineData(ProjectPhysicsBackend.Bullet, typeof(BulletCharacterPhysicsBackend))]
    [InlineData(ProjectPhysicsBackend.Bepu, typeof(BepuCharacterPhysicsBackend))]
    public void ProjectProviderCreatesFreshIndependentAndSharedOwners(ProjectPhysicsBackend selected, Type expectedType)
    {
        var provider = ProjectPhysicsRuntime.CreateProjectProvider();
        var settings = new ProjectPhysicsSettings { Backend = selected };
        var item = SceneObject();
        using var independent = provider.CreateIndependent(settings, item);
        using var nextIndependent = provider.CreateIndependent(settings, item);
        using var shared = provider.CreateShared(settings with { SharedWorld = true });
        using var nextShared = provider.CreateShared(settings with { SharedWorld = true });

        ICharacterPhysicsBackend[] owners = [independent, nextIndependent, shared, nextShared];
        for (int i = 0; i < owners.Length; i++)
        {
            Assert.Equal(expectedType, owners[i].GetType());
            for (int j = 0; j < i; j++) Assert.NotSame(owners[j], owners[i]);
        }
    }

    [Theory]
    [InlineData(ProjectPhysicsBackend.HostDefault)]
    [InlineData(ProjectPhysicsBackend.Bullet)]
    [InlineData(ProjectPhysicsBackend.Bepu)]
    public void IndependentSoftBodiesKeepFreshBulletOwnersForEveryRigidSelection(ProjectPhysicsBackend selected)
    {
        var provider = ProjectPhysicsRuntime.CreateProjectProvider();
        var settings = new ProjectPhysicsSettings { Backend = selected, GroundEnabled = false };
        var item = SceneObject();
        using var first = provider.CreateSoftBody(settings, item);
        using var second = provider.CreateSoftBody(settings, item);

        Assert.IsType<SoftBodyBulletBackend>(first);
        Assert.IsType<SoftBodyBulletBackend>(second);
        Assert.NotSame(first, second);
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Initialize([], []));
    }

    [Fact]
    public void PoseInitializationPolicyKeepsDefaultAndValidatesCustomSubsteps()
    {
        Assert.Equal(120, ProjectPhysicsRuntime.CreateProjectProvider().PoseInitializationSubsteps);
        foreach (int substeps in new[] { 0, 45, 240 })
            Assert.Equal(substeps, ProjectPhysicsRuntime.CreateProjectProvider(substeps).PoseInitializationSubsteps);
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectPhysicsRuntime.CreateProjectProvider(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectPhysicsRuntime.CreateProjectProvider(241));
    }

    [Fact]
    public void BepuCoupledSoftBodiesAreExplicitlyRejectedByFactoryAndProvider()
    {
        var settings = new ProjectPhysicsSettings { Backend = ProjectPhysicsBackend.Bepu, CoupledSoftBodies = true };
        var provider = ProjectPhysicsRuntime.CreateProjectProvider();
        var error = Assert.Throws<ProjectPhysicsCapabilityException>(() => ProjectPhysicsRuntime.CreateRigid(ProjectPhysicsBackend.Bepu, coupledSoftBodies: true));
        Assert.Equal(ProjectPhysicsBackend.Bepu, error.Backend);
        Assert.Equal("coupledSoftBodies", error.Capability);
        Assert.Throws<ProjectPhysicsCapabilityException>(() => ProjectPhysicsRuntime.CreateRigid(settings));
        Assert.Throws<ProjectPhysicsCapabilityException>(() => provider.CreateIndependent(settings, SceneObject()));
        Assert.Throws<ProjectPhysicsCapabilityException>(() => provider.CreateShared(settings with { SharedWorld = true }));
    }

    [Fact]
    public void UnknownBackendIsRejectedWithoutFallingBackToBullet()
    {
        var unknown = (ProjectPhysicsBackend)int.MaxValue;
        var settings = new ProjectPhysicsSettings { Backend = unknown };
        var provider = ProjectPhysicsRuntime.CreateProjectProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectPhysicsRuntime.CreateRigid(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectPhysicsRuntime.CreateRigid(settings));
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.CreateIndependent(settings, SceneObject()));
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.CreateShared(settings));
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.CreateSoftBody(settings, SceneObject()));
    }

    [Theory]
    [InlineData(ProjectPhysicsBackend.HostDefault)]
    [InlineData(ProjectPhysicsBackend.Bullet)]
    [InlineData(ProjectPhysicsBackend.Bepu)]
    public void UninitializedRigidOwnersDisposeBeforeAnySolverInitialization(ProjectPhysicsBackend selected)
    {
        var backend = ProjectPhysicsRuntime.CreateRigid(selected);
        backend.Dispose();
        backend.Dispose();
        // The disposed guard precedes native ABI calls and Bepu Simulation allocation.
        Assert.Throws<ObjectDisposedException>(() => backend.Initialize(new([], [], []), []));
    }

    private static EditableSceneObject SceneObject()
    {
        var frame = new OriginalFrameScene(new(SceneSnapshot.CreateEmpty(), []), OriginalOutlineData.Empty,
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1));
        return new SceneCompositionEditor().Add("factory fixture", "factory-fixture.pmx", ModelRole.Character, frame);
    }
}
