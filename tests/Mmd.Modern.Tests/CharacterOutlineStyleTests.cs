using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterOutlineStyleTests
{
    [Fact]
    public void DefaultModernStyleDrawsEvenWhenSourceMaterialDisablesEdges()
    {
        var source = new OriginalOutlineMaterial(7, Vector4.One, false);
        var resolved = CharacterOutlineMath.ResolveMaterial(source, new());
        Assert.Equal(new OriginalOutlineMaterial(.18f, CharacterOutlineMath.DefaultColor, true), resolved);
        Assert.False(source.DrawEnabled);
        Assert.Equal(7, source.Width);
        Assert.Same(source, CharacterOutlineMath.ResolveMaterial(source, null));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(.25f, false)]
    public void DisabledOrZeroWidthStyleDoesNotDraw(float width, bool enabled)
    {
        Vector4 color = new(.1f, .2f, .3f, .4f);
        Assert.Equal(new OriginalOutlineMaterial(width, color, false),
            CharacterOutlineMath.ResolveMaterial(new(4, Vector4.One), new(width, color, enabled)));
    }

    [Fact]
    public void NullStyleRetainsOriginalPositionsExactlyAndExplicitWidthsUseSelectedMaterial()
    {
        var data = Data(); var options = OriginalOutlineTests.Options();
        OriginalFrameMaterialFlags[] original = [new(false, false, false), new(false, false, false)];
        var legacy = OriginalOutlineMath.BuildPositions(data, options);
        var retained = CharacterOutlineMath.BuildPositions(data, options, original);
        Assert.Equal(legacy.SelectMany(Bits), retained.SelectMany(Bits));
        var modern = original.Select(flags => flags with { CharacterOutlineStyle = new(.25f) }).ToArray();
        modern[1] = modern[1] with { CharacterOutlineStyle = new(.75f) };
        var positions = CharacterOutlineMath.BuildPositions(data, options, modern);
        float modelBase = OriginalOutlineMath.ComputeModelBase(options);
        for (int i = 0; i < positions.Length; i++)
        {
            var vertex = data.Vertices[i];
            float width = vertex.SelectedMaterialIndex == 0 ? .25f : .75f;
            Assert.Equal(new Vector4(OriginalOutlineMath.ExpandPosition(vertex, modelBase, width), 1), positions[i]);
        }
        Assert.Equal(new Vector4(data.Vertices[2].Position, 1), positions[2]);
        Assert.Equal(new Vector4(OriginalOutlineMath.ExpandPosition(data.Vertices[3], modelBase, 0), 1), positions[3]);
        Assert.Equal(3, data.Materials[0].Width);
        Assert.False(data.Materials[0].DrawEnabled);
    }

    [Fact]
    public void InvalidStyleAndBindingCountsAreRejected()
    {
        CharacterOutlineStyle[] invalid =
        [new(-1), new(float.NaN), new(float.PositiveInfinity), new(float.NegativeInfinity),
         new(Color: new(-.1f, 0, 0, 1)), new(Color: new(0, 1.1f, 0, 1)),
         new(Color: new(0, 0, float.NaN, 1)), new(Color: new(0, 0, 0, float.PositiveInfinity))];
        foreach (var style in invalid)
        {
            Assert.Throws<ArgumentException>(() => CharacterOutlineMath.Validate(style));
            Assert.Throws<ArgumentException>(() => CharacterOutlineMath.ResolveMaterial(new(1, Vector4.One), style));
        }
        Assert.Throws<ArgumentNullException>(() => CharacterOutlineMath.Validate(null!));
        Assert.Throws<ArgumentException>(() => CharacterOutlineMath.BuildPositions(Data(), OriginalOutlineTests.Options(), []));
        Assert.Throws<ArgumentException>(() => CharacterOutlineMath.BuildPositions(Data(), OriginalOutlineTests.Options(), [null!, new(false, false, false)]));
    }

    [Fact]
    public void FrameSnapshotValidatesStyleAndIncludesItInEquality()
    {
        var surface = D3D12OriginalStyleTests.Fixture(OriginalSphereMode.None, 1);
        var source = new OriginalFrameMaterialFlags(true, false, true);
        var modern = source with { CharacterOutlineStyle = new() };
        Assert.NotEqual(source, modern);
        Assert.Equal(modern, modern with { CharacterOutlineStyle = new(.18f) });
        Assert.NotEqual(modern, modern with { CharacterOutlineStyle = new(.6f) });
        Assert.NotEqual(modern, modern with { CharacterOutlineStyle = new(Color: Vector4.Zero) });
        Assert.NotEqual(modern, modern with { CharacterOutlineStyle = new(Enabled: false) });
        OriginalFrameMaterialFlags[] flags = [modern];
        var frame = new OriginalFrameScene(surface, materials: flags);
        flags[0] = source;
        Assert.Equal(modern, Assert.Single(frame.Materials));
        Assert.Throws<ArgumentException>(() => new OriginalFrameScene(surface,
            materials: [source with { CharacterOutlineStyle = new(-1) }]));
    }

    private static OriginalOutlineData Data() => new(
        [new(new(1, 2, 3), new(0, 2, 0), OriginalOutlineVertexMode.Expand, .5f, 0),
         new(new(4, 5, 6), Vector3.UnitX, OriginalOutlineVertexMode.Expand, 2, 1),
         new(new(7, 8, 9), Vector3.UnitY, OriginalOutlineVertexMode.Expand, 0, 1),
         new(new(2, 3, 4), Vector3.UnitY, OriginalOutlineVertexMode.Inward, 0, 0)],
        [new(3, Vector4.One, false), new(2, Vector4.Zero)]);

    private static IEnumerable<int> Bits(Vector4 value) =>
        [BitConverter.SingleToInt32Bits(value.X), BitConverter.SingleToInt32Bits(value.Y),
         BitConverter.SingleToInt32Bits(value.Z), BitConverter.SingleToInt32Bits(value.W)];
}
