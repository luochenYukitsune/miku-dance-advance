using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class AddedModelHostOrderTests
{
    private static SceneProjectDrawItem Imported(byte raw, int order) => new(Guid.NewGuid(), PmmDrawableKind.Model, raw, raw, order, true, false);
    private static SceneProjectDrawItem Added(int order) => new(Guid.NewGuid(), PmmDrawableKind.Model, -1, 0, order, true, false);

    [Fact]
    public void AddedModelsFollowEveryImportedModelEvenAfterASparseByte()
    {
        // Imported 0 and 255: the added models must sort after 255, not into the gap after 0.
        var first = Added(10); var second = Added(11);
        var orders = D3D9SurfaceFrameEncoder.AddedModelRawOrders([Imported(0, 0), Imported(255, 1), second, first]);
        Assert.Equal(256, orders[first.ObjectId]); Assert.Equal(257, orders[second.ObjectId]);
    }

    [Fact]
    public void AnAddedModelNeverGetsTheNoObjectSentinel()
    {
        // With no imported model (or only accessories), raw 0 would become host order 0, which MME reads as "no object".
        var only = Added(0);
        Assert.Equal(1, D3D9SurfaceFrameEncoder.AddedModelRawOrders([only])[only.ObjectId]);
    }

    [Fact]
    public void ManyAddedModelsAreNotLimitedByTheByteRange()
    {
        var added = Enumerable.Range(0, 300).Select(Added).ToArray();
        var orders = D3D9SurfaceFrameEncoder.AddedModelRawOrders([Imported(0, -1), Imported(255, -1), .. added]);
        Assert.Equal(300, orders.Values.Distinct().Count());
        Assert.All(orders.Values, value => Assert.True(value > 255));
    }
}
