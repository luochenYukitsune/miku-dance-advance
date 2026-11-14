using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterToonResponseTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(.4f, 0)]
    [InlineData(.45f, 0)]
    [InlineData(.5f, .5f)]
    [InlineData(.55f, 1)]
    [InlineData(.6f, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void DefaultResponseRetainsEndpointsAndOnlySoftensTheNarrowBoundary(float input, float expected)
        => Assert.Equal(expected, CharacterToonMath.Evaluate(input, new()), 5);

    [Fact]
    public void TransitionIsSmoothMonotoneAndPreservesPaletteEndpoints()
    {
        var response = new CharacterToonResponse(.6f, .2f);
        float previous = 0;
        for (int i = 0; i <= 1000; i++)
        {
            float value = CharacterToonMath.Evaluate(i / 1000f, response);
            Assert.InRange(value, previous, 1);
            previous = value;
        }
        Assert.Equal(.15625f, CharacterToonMath.Evaluate(.55f, response), 5);
        Assert.Equal(.84375f, CharacterToonMath.Evaluate(.65f, response), 5);
        Assert.Equal(0, CharacterToonMath.Evaluate(0, response));
        Assert.Equal(1, CharacterToonMath.Evaluate(1, response));
    }

    [Fact]
    public void InvalidParametersAndNonFiniteInputsAreRejectedAtTheNeutralBoundary()
    {
        CharacterToonResponse[] invalid =
        [new(float.NaN, .1f), new(float.PositiveInfinity, .1f), new(-.1f, .1f), new(1.1f, .1f),
         new(.5f, 0), new(.5f, -.1f), new(.5f, float.NaN), new(.5f, float.PositiveInfinity),
         new(0, .1f), new(1, .1f), new(.1f, .3f)];
        foreach (var response in invalid)
            Assert.Throws<ArgumentException>(() => CharacterToonMath.Validate(response));
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => CharacterToonMath.Evaluate(value, new()));
        Assert.Throws<ArgumentNullException>(() => CharacterToonMath.Validate(null!));
        CharacterToonMath.Validate(new(.5f, 1));
    }

    [Fact]
    public void ShadowVisibilityControlsTheSameScalarBeforeTheResponse()
    {
        // The renderer supplies min(saturate(3 * NdotL), shadowVisibility).
        float litNormal = Math.Clamp(3 * .8f, 0, 1);
        Assert.Equal(0, CharacterToonMath.Evaluate(MathF.Min(litNormal, .4f), new()));
        Assert.Equal(.5f, CharacterToonMath.Evaluate(MathF.Min(litNormal, .5f), new()), 5);
        Assert.Equal(1, CharacterToonMath.Evaluate(MathF.Min(litNormal, 1), new()));
    }

    [Fact]
    public void MaterialSnapshotEqualityIncludesResponseParametersForTemporalConsumers()
    {
        var original = new OriginalFrameMaterialFlags(true, true, false);
        var modern = original with { CharacterToonResponse = new() };
        Assert.NotEqual(original, modern);
        Assert.Equal(modern, modern with { CharacterToonResponse = new(.5f, .1f) });
        Assert.NotEqual(modern, modern with { CharacterToonResponse = new(.6f, .1f) });
        Assert.NotEqual(modern, modern with { CharacterToonResponse = new(.5f, .2f) });
        Assert.Null(original.CharacterToonResponse);
    }
}
