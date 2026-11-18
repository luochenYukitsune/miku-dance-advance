using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class AddedModelHostOrderTests
{
    private static SceneProjectDrawItem Imported(byte raw, int order) => new(Guid.NewGuid(), PmmDrawableKind.Model, raw, raw, order, true, false);
    private static SceneProjectDrawItem Accessory(byte raw, int index) => new(Guid.NewGuid(), PmmDrawableKind.Accessory, index, raw, raw, true, false);
    private static SceneProjectDrawItem Added(int order) => new(Guid.NewGuid(), PmmDrawableKind.Model, -1, 0, order, true, false);

    private static Dictionary<Guid, int> Orders(IReadOnlyList<SceneProjectDrawItem> items, int split = 0) =>
        D3D9SurfaceFrameEncoder.ComputeHostOrders(items, items.Count(value => value.Kind == PmmDrawableKind.Accessory), split,
            items.Count(value => value.Kind == PmmDrawableKind.Model));

    [Fact]
    public void AddedModelsFollowEveryImportedModelEvenAfterASparseByte()
    {
        // Imported 0 and 255: the added models must sort after 255, not into the gap after 0.
        var low = Imported(0, 0); var high = Imported(255, 1); var first = Added(10); var second = Added(11);
        var orders = Orders([low, high, second, first]);
        Assert.Equal(0, orders[low.ObjectId]); Assert.Equal(255, orders[high.ObjectId]);
        Assert.Equal(256, orders[first.ObjectId]); Assert.Equal(257, orders[second.ObjectId]);
    }

    [Fact]
    public void AnAddedModelNeverGetsTheNoObjectSentinel()
    {
        // With no imported model (or only accessories), raw 0 would become host order 0, which MME reads as "no object".
        var only = Added(0);
        Assert.Equal(1, Orders([only])[only.ObjectId]);
        var before = Accessory(0, 0); var added = Added(1);
        var mixed = Orders([before, added], split: 1);
        Assert.Equal(-1, mixed[before.ObjectId]); Assert.Equal(1, mixed[added.ObjectId]);
    }

    [Fact]
    public void ManyAddedModelsAreNotLimitedByTheByteRange()
    {
        var added = Enumerable.Range(0, 300).Select(Added).ToArray();
        var orders = Orders([Imported(0, -1), Imported(255, -1), .. added]);
        var addedOrders = added.Select(value => orders[value.ObjectId]).ToArray();
        Assert.Equal(300, addedOrders.Distinct().Count());
        Assert.All(addedOrders, value => Assert.True(value > 255));
    }

    [Fact]
    public void AnAddedModelNeverSharesAHostOrderWithAnAccessoryAfterTheModels()
    {
        // MME maps host order to object (models first, then accessories overwrite), so a shared order would make the
        // added model's effects resolve to the accessory. Imported model raw 2 -> 2; accessory raw 0 after the models
        // -> 0 + 2 models + 1 = 3. Numbering the added model from the model bytes alone would also give it 3.
        var model = Imported(2, 0); var accessory = Accessory(0, 0); var added = Added(1);
        var orders = Orders([model, accessory, added]);
        Assert.Equal(2, orders[model.ObjectId]); Assert.Equal(3, orders[accessory.ObjectId]);
        Assert.Equal(4, orders[added.ObjectId]);
        Assert.Equal(orders.Count, orders.Values.Distinct().Count());
    }

    [Fact]
    public void AnImportedModelNeverSharesAHostOrderWithAnAccessoryShiftedByAnAddedModel()
    {
        // Imported models raw 0 and 4, accessory raw 0 with split 0. Adding a model raises the model count to 3, so the
        // raw formula gives the accessory 0 + 3 + 1 = 4, the imported model's order. Every object must keep a distinct
        // order, with the draw order (models by raw byte, then the post-split accessory) unchanged.
        var low = Imported(0, 0); var high = Imported(4, 1); var accessory = Accessory(0, 0); var added = Added(3);
        var orders = Orders([low, high, accessory, added]);
        Assert.Equal(4, orders.Values.Distinct().Count());
        Assert.DoesNotContain(0, orders.Values);
        Assert.True(orders[low.ObjectId] < orders[high.ObjectId]);
        Assert.True(orders[high.ObjectId] < orders[added.ObjectId]);
        Assert.True(orders[added.ObjectId] < orders[accessory.ObjectId]);
    }

    [Fact]
    public void RenumberingKeepsPreSplitAccessoriesNegativeAndInTheirOriginalOrder()
    {
        var first = Accessory(0, 0); var second = Accessory(1, 1); var after = Accessory(2, 2);
        var low = Imported(0, 3); var high = Imported(5, 4); var added = Added(5);
        // split 2: pre-split -1, -2; models 0 + 2, 5 + 2; post-split 2 + 3 + 1 = 6; added -> 8. No collision yet.
        var plain = Orders([first, second, after, low, high, added], split: 2);
        Assert.Equal([-1, -2, 2, 7, 6, 8], new[] { first, second, low, high, after, added }.Select(value => plain[value.ObjectId]));
        // Model raw 4 collides with the post-split accessory (4 + 2 = 6): renumber, keeping groups and order.
        var shifted = Imported(4, 4);
        var dense = Orders([first, second, after, low, shifted, added], split: 2);
        Assert.Equal([-1, -2, 3, 4, 5, 6], new[] { first, second, low, shifted, added, after }.Select(value => dense[value.ObjectId]));
    }

    [Fact]
    public void AddedModelsStayDistinctFromEveryAccessoryOrderInALargeMixedTable()
    {
        var accessories = Enumerable.Range(0, 40).Select(index => Accessory((byte)(index * 3), index)).ToArray();
        var models = Enumerable.Range(0, 20).Select(index => Imported((byte)(index * 5), index)).ToArray();
        var added = Enumerable.Range(0, 30).Select(index => Added(100 + index)).ToArray();
        var orders = Orders([.. accessories, .. models, .. added], split: 30);
        var accessoryOrders = accessories.Select(value => orders[value.ObjectId]).ToHashSet();
        var modelOrders = models.Select(value => orders[value.ObjectId]).ToHashSet();
        foreach (var value in added)
        {
            int order = orders[value.ObjectId];
            Assert.DoesNotContain(order, accessoryOrders); Assert.DoesNotContain(order, modelOrders);
            Assert.True(order > modelOrders.Max());
        }
        Assert.Equal(added.Length, added.Select(value => orders[value.ObjectId]).Distinct().Count());
    }
}
