using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class BoundedHistoryTests
{
    [Fact]
    public void PushingPastCapacityDropsTheOldestEntryAndKeepsStackOrder()
    {
        var history = new BoundedHistory<int>(3);
        for (int i = 1; i <= 3; i++) Assert.False(history.Push(i, out _));
        Assert.True(history.Push(4, out int dropped));
        Assert.Equal(1, dropped);
        Assert.Equal(new[] { 4, 3, 2 }, history);
        Assert.True(history.TryPeek(out int top)); Assert.Equal(4, top);
        Assert.Equal(4, history.Pop()); Assert.Equal(3, history.Pop()); Assert.Equal(2, history.Pop());
        Assert.False(history.TryPeek(out _));
        Assert.Throws<InvalidOperationException>(() => history.Pop());
    }

    [Fact]
    public void ProjectAndWorkspaceShareOneDefaultCapacity()
        => Assert.Equal(100, BoundedHistory<object>.DefaultCapacity);
}
