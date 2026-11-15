using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorTextureFallbackTests
{
    [Fact]
    public void OneUnusableTextureDegradesToMissingWhileOtherTexturesStillLoad()
    {
        var good = new SceneTexture("ok.png", 1, 1, new byte[4]);
        var failures = new List<string>();
        var load = EditorTextureLoader.WithModelBudget(relative => relative switch
        {
            "ok.png" => good,
            "..\\outside.png" => throw new InvalidDataException("outside"),
            "broken.png" => throw new NotSupportedException("codec"),
            "locked.png" => throw new IOException("locked"),
            _ => null,
        }, failures: failures.Add);

        Assert.Null(load("..\\outside.png"));
        Assert.Null(load("broken.png"));
        Assert.Null(load("locked.png"));
        Assert.Same(good, load("ok.png"));
        Assert.Equal(3, failures.Count);
        Assert.Contains(failures, f => f.Contains("broken.png"));
    }

    [Fact]
    public void BudgetOverflowSkipsOnlyTheTextureThatWouldExceedIt()
    {
        var small = new SceneTexture("a.png", 1, 1, new byte[4]);
        var large = new SceneTexture("b.png", 2, 2, new byte[16]);
        var failures = new List<string>();
        var load = EditorTextureLoader.WithModelBudget(relative => relative == "a.png" ? small : large, maximumBytes: 8, failures: failures.Add);
        Assert.Same(small, load("a.png"));
        Assert.Null(load("b.png"));
        Assert.Same(small, load("a.png"));
        Assert.Single(failures);
    }

    [Fact]
    public void CancellationStillPropagates()
    {
        var load = EditorTextureLoader.WithModelBudget(_ => throw new OperationCanceledException());
        Assert.Throws<OperationCanceledException>(() => load("x.png"));
    }
}
