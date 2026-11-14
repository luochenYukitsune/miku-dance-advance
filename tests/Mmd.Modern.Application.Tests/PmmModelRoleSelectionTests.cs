using System.Numerics;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmModelRoleSelectionTests
{
    [Fact]
    public void DuplicatePathsHaveIndependentStoredIdentitiesAndExplicitUnspecifiedIsConfirmed()
    {
        var original = Plan();
        Assert.True(original.CanCommit);
        Assert.True(original.HasUnconfirmedRoles);
        Assert.All(original.ModelRoleSelections, row => Assert.Null(row.Purpose));
        var selections = new Dictionary<int, ModelPurpose> { [7] = new(ModelRole.Character), [19] = new(ModelRole.Unspecified) };
        var confirmed = original.ConfirmRoles(selections);
        selections[7] = new(ModelRole.Environment);
        Assert.False(confirmed.HasUnconfirmedRoles);
        Assert.Same(confirmed, confirmed.RequireConfirmedRoles());
        Assert.Equal(ModelRole.Character, confirmed.Models[0].Purpose.Role);
        Assert.Equal(ModelRole.Unspecified, confirmed.Models[1].Purpose.Role);
        Assert.True(confirmed.ModelRoleSelections[1].IsConfirmed);
        Assert.Equal("pmm-model:19", confirmed.ModelRoleSelections[1].StableKey);
        Assert.Equal(confirmed.Models[0].Path, confirmed.Models[1].Path);
        Assert.True(original.HasUnconfirmedRoles);
        Assert.Equal(ModelRole.Unspecified, original.Models[0].Purpose.Role);
    }

    [Fact]
    public void PartialAndInvalidBatchNeverMutateOriginalCandidate()
    {
        var original = Plan();
        var partial = original.WithModelPurposes(new Dictionary<int, ModelPurpose> { [7] = new(ModelRole.Prop) });
        Assert.True(partial.HasUnconfirmedRoles);
        Assert.Throws<InvalidOperationException>(() => partial.RequireConfirmedRoles());
        Assert.Throws<InvalidOperationException>(() => original.ConfirmRoles(new Dictionary<int, ModelPurpose> { [7] = new(ModelRole.Character) }));
        Assert.Throws<ArgumentException>(() => original.WithModelPurposes(new Dictionary<int, ModelPurpose>
            { [7] = new(ModelRole.Character), [123] = new(ModelRole.Environment) }));
        Assert.Throws<ArgumentException>(() => original.WithModelPurposes(new Dictionary<int, ModelPurpose> { [7] = new(ModelRole.Character, true) }));
        Assert.Throws<ArgumentException>(() => original.WithModelPurposes(new Dictionary<int, ModelPurpose> { [7] = new((ModelRole)99) }));
        Assert.Empty(original.ModelPurposes);
        Assert.False(partial.ConfirmRoles(new Dictionary<int, ModelPurpose> { [19] = new(ModelRole.Environment, true) }).HasUnconfirmedRoles);
    }

    [Fact]
    public void MissingModelsStillAppearInManualConfirmationPreflight()
    {
        var document = new PmmDocument { Models = [new() { Index = 42, Name = new(0, "scene.pmx", []), Path = new(0, "missing.pmd", []) }] };
        var plan = PmmProjectImport.Prepare(document, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "project.pmm"),
            new() { ModelPurposes = new Dictionary<int, ModelPurpose> { [42] = new(ModelRole.Prop) } }, TestContext.Current.CancellationToken);
        Assert.False(plan.CanCommit);
        var row = Assert.Single(plan.ModelRoleSelections);
        Assert.Equal(42, row.ModelIndex);
        Assert.Null(row.ResolvedPath);
        Assert.Equal(ModelRole.Prop, row.Role);
        Assert.True(row.IsConfirmed);
        Assert.False(plan.HasUnconfirmedRoles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedTimelineAndRepeatedPublicationCarryMixedModelRoles(bool shared)
    {
        var plan = Plan().ConfirmRoles(new Dictionary<int, ModelPurpose>
            { [7] = new(ModelRole.Character), [19] = new(ModelRole.Environment, true) });
        var rest = EditedMotionProjectTests.Rest(plan.Models[0].Model);
        using var playback = new PmmProjectPlayback(plan, new Dictionary<int, OriginalFrameScene> { [7] = rest, [19] = rest },
            (_, _) => new TrackingSharedBackend(), shared);
        void Check()
        {
            int count = rest.Materials.Count;
            Assert.True(count > 0);
            Assert.All(playback.Frame.Materials.Take(count), flags => Assert.Equal(RayGiToonResponse.PreservePaletteBands, flags.GiToonResponseOverride));
            Assert.All(playback.Frame.Materials.Skip(count), flags => Assert.Equal(RayGiToonResponse.Continuous, flags.GiToonResponseOverride));
            Assert.All(playback.Frame.Materials.Take(count), flags => Assert.Null(flags.CharacterToonResponse));
            Assert.All(playback.Frame.Materials.Skip(count), flags => Assert.Null(flags.CharacterToonResponse));
            Assert.All(playback.Frame.Materials.Take(count), flags => Assert.Null(flags.CharacterOutlineStyle));
            Assert.All(playback.Frame.Materials.Skip(count), flags => Assert.Null(flags.CharacterOutlineStyle));
            Assert.All(playback.Frame.Materials.Take(count), flags => Assert.True(flags.PreserveDirectLightPalette));
            Assert.All(playback.Frame.Materials.Skip(count), flags => Assert.False(flags.PreserveDirectLightPalette));
            var draw = playback.SampleDrawPlan();
            Assert.Equal(2, draw.Items.Count);
        }
        Check(); playback.Seek(0); Check(); playback.Advance(1, 1d / 30); Check(); playback.Reset(); Check();
        Assert.All(rest.Materials, flags => Assert.Null(flags.GiToonResponseOverride));
        Assert.All(rest.Materials, flags => Assert.Null(flags.CharacterToonResponse));
        Assert.All(rest.Materials, flags => Assert.Null(flags.CharacterOutlineStyle));
        Assert.All(rest.Materials, flags => Assert.False(flags.PreserveDirectLightPalette));
    }

    [Fact]
    public void UnconfirmedLegacyPlaybackRetainsExplicitMaterialResponse()
    {
        var plan = Plan(); var source = EditedMotionProjectTests.Rest(plan.Models[0].Model);
        var rest = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions,
            materials: source.Materials.Select(flags => flags with { GiToonResponseOverride = RayGiToonResponse.Continuous,
                CharacterToonResponse = new(.6f, .2f), CharacterOutlineStyle = new(.25f, Vector4.Zero, false) }));
        using var playback = new PmmProjectPlayback(plan, new Dictionary<int, OriginalFrameScene> { [7] = rest, [19] = rest },
            (_, _) => throw new InvalidOperationException("No rigid-body assets in this fixture."));
        Assert.All(playback.Frame.Materials, flags => Assert.Equal(RayGiToonResponse.Continuous, flags.GiToonResponseOverride));
        Assert.All(playback.Frame.Materials, flags => Assert.Equal(new CharacterToonResponse(.6f, .2f), flags.CharacterToonResponse));
        Assert.All(playback.Frame.Materials, flags => Assert.Equal(new CharacterOutlineStyle(.25f, Vector4.Zero, false), flags.CharacterOutlineStyle));
        playback.Seek(0);
        Assert.All(playback.Frame.Materials, flags => Assert.Equal(RayGiToonResponse.Continuous, flags.GiToonResponseOverride));
        Assert.All(playback.Frame.Materials, flags => Assert.Equal(new CharacterToonResponse(.6f, .2f), flags.CharacterToonResponse));
        Assert.All(playback.Frame.Materials, flags => Assert.Equal(new CharacterOutlineStyle(.25f, Vector4.Zero, false), flags.CharacterOutlineStyle));
        Assert.True(plan.HasUnconfirmedRoles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitToonAndDisabledCharacterOutlineSurviveDynamicPmmPublication(bool shared)
    {
        var plan = Plan().ConfirmRoles(new Dictionary<int, ModelPurpose>
            { [7] = new(ModelRole.Character), [19] = new(ModelRole.Environment) });
        var source = EditedMotionProjectTests.Rest(plan.Models[0].Model);
        var style = new CharacterOutlineStyle(.3f, new(.2f, .3f, .4f, 1), false);
        var response = new CharacterToonResponse(.6f, .2f);
        var rest = new OriginalFrameScene(source.Surface, source.Outline, source.OutlineOptions,
            materials: source.Materials.Select(flags => flags with { CharacterOutlineStyle = style, CharacterToonResponse = response }));
        using var playback = new PmmProjectPlayback(plan, new Dictionary<int, OriginalFrameScene> { [7] = rest, [19] = rest },
            (_, _) => new TrackingSharedBackend(), shared);
        void Check()
        {
            Assert.All(playback.Frame.Materials.Take(rest.Materials.Count), flags => Assert.Equal(style, flags.CharacterOutlineStyle));
            Assert.All(playback.Frame.Materials.Take(rest.Materials.Count), flags => Assert.Equal(response, flags.CharacterToonResponse));
            Assert.All(playback.Frame.Materials.Skip(rest.Materials.Count), flags => Assert.Null(flags.CharacterOutlineStyle));
            Assert.All(playback.Frame.Materials.Skip(rest.Materials.Count), flags => Assert.Null(flags.CharacterToonResponse));
        }
        Check(); playback.Seek(0); Check(); playback.Advance(1, 1d / 30); Check(); playback.Reset(); Check();
        Assert.All(rest.Materials, flags => Assert.Equal(style, flags.CharacterOutlineStyle));
        Assert.All(rest.Materials, flags => Assert.Equal(response, flags.CharacterToonResponse));
    }

    private static PmmProjectImportPlan Plan()
    {
        var model = EditedMotionProjectTests.Model();
        PmmModel Actor(byte index, byte order) => new()
        {
            Index = index, DrawOrderIndex = order, TransformOrderIndex = order, Visible = true, LastFrame = 2,
            Name = new(0, "same name", []), Path = new(0, "same.pmx", []), InitialModelKeyframe = new() { Visible = true },
        };
        var document = new PmmDocument
        {
            Models = [Actor(7, 0), Actor(19, 1)], OutputWidth = 1280, OutputHeight = 720, CameraFov = 30,
            Settings = new() { PhysicsSimulationMode = 1, CameraLookAtModelIndex = -1 },
            Camera = new() { Position = new(0, 0, -10), Perspective = true,
                InitialKeyframe = new() { Distance = -10, Fov = 30, Perspective = true, ParentModelIndex = -1,
                    Interpolation = Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray() } },
            Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY,
                InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } },
        };
        var models = document.Models.Select(source => new PreparedPmmModel(source, "same.pmx", model,
            PmmMotionAdapter.Convert(source, model), PmmMotionAdapter.ConvertSavedPose(source, model))).ToList();
        return (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [document, "synthetic.pmm", models, new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
    }
}
