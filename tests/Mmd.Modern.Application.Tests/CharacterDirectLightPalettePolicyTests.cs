using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class CharacterDirectLightPalettePolicyTests
{
    [Fact]
    public void CharacterProtectsAdditionalDirectLightWithGiOffWithoutSelectingANewPrimaryResponse()
    {
        var source = Rest();
        Assert.False(Assert.Single(source.Materials).PreserveDirectLightPalette);
        Assert.False(source.Options?.RayGi?.Enabled == true);

        var resolved = SceneModelShadingPolicy.Apply(source, ModelRole.Character);

        Assert.True(Assert.Single(resolved.Materials).PreserveDirectLightPalette);
        Assert.Null(Assert.Single(resolved.Materials).CharacterToonResponse);
        Assert.Null(Assert.Single(resolved.Materials).CharacterOutlineStyle);
        Assert.Same(source.Surface, resolved.Surface);
        Assert.Same(source.Options, resolved.Options);
        Assert.Equal(Assert.Single(source.Materials), Assert.Single(resolved.Materials) with
        { PreserveDirectLightPalette = false, GiToonResponseOverride = null });
        Assert.False(Assert.Single(source.Materials).PreserveDirectLightPalette);
    }

    [Theory]
    [InlineData(ModelRole.Prop)]
    [InlineData(ModelRole.Environment)]
    [InlineData(ModelRole.Unspecified)]
    public void NonCharacterPurposeClearsAFormerCharactersDirectProtection(ModelRole role)
    {
        var source = Rest();
        var flagged = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions, source.Options,
            source.Materials.Select(flags => flags with { PreserveDirectLightPalette = true }));

        var resolved = SceneModelShadingPolicy.Apply(flagged, role);

        Assert.False(Assert.Single(resolved.Materials).PreserveDirectLightPalette);
        Assert.Same(source.Surface, resolved.Surface);
        Assert.Same(source.Options, resolved.Options);
        Assert.True(Assert.Single(flagged.Materials).PreserveDirectLightPalette);
    }

    [Fact]
    public void MixedCompositionKeepsProtectionPerRoleAndReapplicationRetainsExplicitPrimaryStyle()
    {
        var raw = Rest();
        var response = new CharacterToonResponse(.7f, .2f);
        var source = new OriginalFrameScene(raw.Surface, raw.Outline, raw.OutlineOptions, raw.Options,
            raw.Materials.Select(flags => flags with { CharacterToonResponse = response }));
        var layout = new SceneCompositionEditor();
        layout.Add("actor", "actor.pmx", ModelRole.Character, source);
        layout.Add("prop", "prop.pmx", ModelRole.Prop, raw);
        layout.Add("room", "room.pmx", ModelRole.Environment, raw);

        Assert.Equal(new[] { true, false, false }, layout.Frame.Materials.Select(flags => flags.PreserveDirectLightPalette));
        Assert.Same(response, layout.Frame.Materials[0].CharacterToonResponse);
        var character = SceneModelShadingPolicy.Apply(source, ModelRole.Character);
        Assert.Same(character, SceneModelShadingPolicy.Apply(character, ModelRole.Character));
        Assert.Same(response, Assert.Single(character.Materials).CharacterToonResponse);
    }

    private static OriginalFrameScene Rest() => EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model());
}
