using System.Reflection;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12ResourceOwnershipTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Resource : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }
    private static Type Type(string name) => typeof(D3D12FrameSession).Assembly.GetType("Mmd.Modern.Rendering.D3D12." + name)!;

    [Fact]
    public void ReplacedCacheOwnerKeepsResourceUntilEveryFrameLeaseRetires()
    {
        var resource = new Resource();
        var type = Type("D3D12RetainedResource");
        var owner = (IDisposable)Activator.CreateInstance(type, All, null, [Guid.NewGuid(), resource], null)!;
        var a = (IDisposable)type.GetMethod("Lease", All)!.Invoke(owner, null)!;
        var b = (IDisposable)type.GetMethod("Lease", All)!.Invoke(owner, null)!;
        owner.Dispose(); owner.Dispose();
        Assert.Equal(0, resource.Disposals);
        a.Dispose(); a.Dispose();
        Assert.Equal(0, resource.Disposals);
        b.Dispose(); b.Dispose();
        Assert.Equal(1, resource.Disposals);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ConstructionFailureDisposesEveryCompletedAllocationExactlyOnce(int failAfter)
    {
        var type = Type("D3D12ConstructionScope");
        Assert.NotNull(type);
        Resource[] resources = [new(), new()];
        Assert.Throws<OutOfMemoryException>((Action)(() =>
        {
            using var scope = (IDisposable)Activator.CreateInstance(type, All, null, [], null)!;
            var own = type.GetMethod("Own", All)!.MakeGenericMethod(typeof(Resource));
            for (int i = 0; i < failAfter; i++) own.Invoke(scope, [resources[i]]);
            throw new OutOfMemoryException("Deterministic allocation boundary failure.");
        }));
        for (int i = 0; i < resources.Length; i++) Assert.Equal(i < failAfter ? 1 : 0, resources[i].Disposals);
    }

    [Fact]
    public void SuccessfulConstructionTransfersOwnershipWithoutPrematureDisposal()
    {
        var type = Type("D3D12ConstructionScope");
        Assert.NotNull(type);
        var resource = new Resource();
        using (var scope = (IDisposable)Activator.CreateInstance(type, All, null, [], null)!)
        {
            type.GetMethod("Own", All)!.MakeGenericMethod(typeof(Resource)).Invoke(scope, [resource]);
            type.GetMethod("Complete", All)!.Invoke(scope, null);
        }
        Assert.Equal(0, resource.Disposals);
        resource.Dispose();
        Assert.Equal(1, resource.Disposals);
    }
}
