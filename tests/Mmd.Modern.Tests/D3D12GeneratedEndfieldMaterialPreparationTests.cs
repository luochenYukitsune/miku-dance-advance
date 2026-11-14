using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12GeneratedEndfieldMaterialPreparationTests
{
    private static D3D12GeneratedEndfieldMaterialSelection Selection(int index = 0,
        D3D12GeneratedPmxSurfaceKind kind = D3D12GeneratedPmxSurfaceKind.Cloth)
        => new(index, new(Kind: kind));

    [Fact]
    public void PreparationAppendsOnlySelectedMapsAndPreservesOriginalMetadata()
    {
        var source = Frame();
        var originalLayout = D3D12MaterialBindingLayout.Capture(source);
        byte[] originalPixels = source.Surface.Snapshot.Textures[0].Rgba.ToArray();
        var result = Prepare(source, [Selection(1)]);
        var before = source.Surface.Snapshot;
        var after = result.SourceRest.Surface.Snapshot;
        Assert.Single(before.Textures);
        Assert.Equal(originalPixels, before.Textures[0].Rgba.ToArray());
        Assert.Equal(4, after.Textures.Count);
        Assert.Same(before.Textures[0], after.Textures[0]);
        Assert.Equal(before.Vertices.ToArray(), after.Vertices.ToArray());
        Assert.Equal(before.Indices.ToArray(), after.Indices.ToArray());
        Assert.Equal(before.Materials, after.Materials);
        Assert.Same(before.AdditionalUvs, after.AdditionalUvs);
        Assert.Same(before.RawRadialSeeds, after.RawRadialSeeds);
        Assert.Equal(before.World, after.World);
        Assert.Same(before.Camera, after.Camera);
        Assert.Equal(before.LightDirection, after.LightDirection);
        Assert.Equal(source.Surface.Materials, result.SourceRest.Surface.Materials);
        Assert.Same(source.Surface.Profile, result.SourceRest.Surface.Profile);
        Assert.Same(source.Outline, result.SourceRest.Outline);
        Assert.Same(source.OutlineOptions, result.SourceRest.OutlineOptions);
        Assert.Same(source.Options, result.SourceRest.Options);
        Assert.Same(source.Lighting, result.SourceRest.Lighting);
        Assert.Same(source.EditorOverlay, result.SourceRest.EditorOverlay);
        Assert.Equal(source.Materials, result.SourceRest.Materials);
        Assert.Equal(originalLayout.Fingerprint, D3D12MaterialBindingLayout.Capture(source).Fingerprint);
        var row = Assert.Single(result.Profile.Materials);
        Assert.Equal(1, row.LocalMaterialIndex);
        Assert.Equal(D3D12CharacterMaterialRole.Body, row.Role);
        Assert.Null(row.PmxMaterialTreatment);
        Assert.NotNull(row.EndfieldMatcap);
        Assert.Equal(1, row.EndfieldMatcap.Normal.SourceSceneTextureOrdinal);
        Assert.Equal(2, row.EndfieldMatcap.P.SourceSceneTextureOrdinal);
        Assert.Equal(3, row.EndfieldMatcap.Matcap.SourceSceneTextureOrdinal);
        var eg = OriginalStyleMath.ResolveHostColors(before.Materials[1], source.Surface.Profile).EgColor;
        Assert.Equal(new Vector3(eg.X, eg.Y, eg.Z), row.EndfieldMatcap.BaseColor);
        Assert.Equal(Vector3.Normalize(before.LightDirection), row.EndfieldMatcap.LightControlDirection);
        var layout = D3D12MaterialBindingLayout.Capture(result.SourceRest);
        layout.ValidateTexture(row.EndfieldMatcap.Normal);
        layout.ValidateTexture(row.EndfieldMatcap.P);
        layout.ValidateTexture(row.EndfieldMatcap.Matcap);
        Assert.Single(D3D12MaterialBindingProfileResolver.Resolve(result.Profile, result.SourceRest, Guid.NewGuid(), "endfield").EndfieldMatcapBindings);
        Assert.Empty(D3D12MaterialBindingProfileResolver.Resolve(result.Profile, result.SourceRest, Guid.NewGuid(), "mmd").EndfieldMatcapBindings);
    }

    [Fact]
    public void ExplicitHairRoleAndWhiteFallbackHaveDeterministicGeneratedProvenance()
    {
        var first = Prepare(Frame(), [Selection(1, D3D12GeneratedPmxSurfaceKind.Hair), Selection(0)]);
        var second = Prepare(Frame(), [Selection(0), Selection(1, D3D12GeneratedPmxSurfaceKind.Hair)]);
        Assert.Equal(first.ManifestBytes, second.ManifestBytes);
        Assert.Equal(D3D12MaterialBindingProfileJson.Encode(first.Profile), D3D12MaterialBindingProfileJson.Encode(second.Profile));
        Assert.Equal(D3D12CharacterMaterialRole.Hair, first.Profile.Materials[1].Role);
        using var doc = JsonDocument.Parse(first.ManifestBytes);
        Assert.True(doc.RootElement.GetProperty("isGenerated").GetBoolean());
        var materials = doc.RootElement.GetProperty("materials");
        Assert.Equal(0, materials[0].GetProperty("sourceSceneTextureOrdinal").GetInt32());
        Assert.Equal(-1, materials[1].GetProperty("sourceSceneTextureOrdinal").GetInt32());
        Assert.True(materials[0].GetProperty("outputsAreGenerated").GetBoolean());
        Assert.True(materials[1].GetProperty("outputsAreGenerated").GetBoolean());
        Assert.False(materials[0].TryGetProperty("sourceIsGenerated", out _));
        Assert.False(materials[1].TryGetProperty("sourceIsGenerated", out _));
        Assert.False(materials[0].GetProperty("sourceUsesWhiteFallback").GetBoolean());
        Assert.True(materials[1].GetProperty("sourceUsesWhiteFallback").GetBoolean());
        Assert.False(materials[1].GetProperty("outputsUserEdited").GetBoolean());
        Assert.Equal("Hair", materials[1].GetProperty("options").GetProperty("kind").GetString());
        Assert.Equal(64, materials[1].GetProperty("sourceRgbaSha256").GetString()!.Length);
        Assert.False(string.IsNullOrWhiteSpace(materials[1].GetProperty("generatorRevision").GetString()));
        Assert.Equal(3, materials[1].GetProperty("outputs").GetArrayLength());
    }

    [Fact]
    public void EditingDerivedMapsCreatesNewGuardsAndRetainsOriginalAndPreviousResults()
    {
        var original = Frame();
        var prepared = Prepare(original, [Selection()]);
        var oldGuard = prepared.Profile.Materials[0].EndfieldMatcap!.Normal;
        var texture = prepared.SourceRest.Surface.Snapshot.Textures[oldGuard.SourceSceneTextureOrdinal];
        byte[] pixels = texture.Rgba.ToArray(); pixels[0] ^= 127;
        var edited = prepared.WithEditedMaps(0, normal: new("user-edited-normal", texture.Width, texture.Height, pixels));
        var guard = edited.Profile.Materials[0].EndfieldMatcap!.Normal;
        Assert.NotEqual(oldGuard.RgbaSha256, guard.RgbaSha256);
        Assert.Equal(oldGuard.SourceSceneTextureOrdinal, guard.SourceSceneTextureOrdinal);
        Assert.Equal(oldGuard, prepared.Profile.Materials[0].EndfieldMatcap!.Normal);
        Assert.Single(original.Surface.Snapshot.Textures);
        D3D12MaterialBindingLayout.Capture(edited.SourceRest).ValidateTexture(guard);
        using var doc = JsonDocument.Parse(edited.ManifestBytes);
        Assert.True(doc.RootElement.GetProperty("materials")[0].GetProperty("outputsUserEdited").GetBoolean());
        Assert.Throws<ArgumentOutOfRangeException>(() => prepared.WithEditedMaps(1, normal: texture));
        Assert.Throws<ArgumentException>(() => prepared.WithEditedMaps(0, normal: new("wrong", 1, 1, [0, 0, 0, 255])));
        Assert.Throws<ArgumentException>(() => prepared.WithEditedMaps(0, normal: SceneTexture.ProceduralToon()));
    }

    [Fact]
    public void InvalidOrAmbiguousSelectionAndCancellationAreRejected()
    {
        var frame = Frame();
        Assert.Throws<ArgumentException>(() => Prepare(frame, []));
        Assert.Throws<ArgumentException>(() => Prepare(frame, [Selection(), Selection()]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Prepare(frame, [Selection(-1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Prepare(frame, [Selection(2)]));
        Assert.Throws<ArgumentNullException>(() => Prepare(frame, [null!]));
        Assert.Throws<ArgumentNullException>(() => Prepare(frame, [new(0, null!)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Prepare(frame, [new(0, new(MapSize: 15))]));
        Assert.Throws<ArgumentException>(() => Prepare(Frame(proceduralDiffuse: true), [Selection()]));
        Assert.Throws<ArgumentException>(() => Prepare(Frame(dynamic: true), [Selection()]));
        Assert.Throws<OperationCanceledException>(() => D3D12GeneratedEndfieldMaterialPreparation.Prepare(frame,
            [Selection()], "generated-test", "Generated test", new CancellationToken(true)));
    }

    [Fact]
    public void AggregateTextureAndPixelBudgetsRejectBeforeGeneratingAnyMaps()
    {
        var frame = RepeatedFrame(22, 1);
        var tooManyPixels = Enumerable.Range(0, 22).Select(index =>
            new D3D12GeneratedEndfieldMaterialSelection(index, new(MapSize: 1024)));
        Assert.Throws<ArgumentException>(() => Prepare(frame, tooManyPixels));
        Assert.Throws<ArgumentException>(() => Prepare(RepeatedFrame(1, 4094), [Selection()]));
        Assert.Equal(256L * 1024 * 1024, D3D12GeneratedEndfieldMaterialPreparation.MaxGeneratedBytes);
        Assert.Single(frame.Surface.Snapshot.Textures);
    }

    private static OriginalFrameScene RepeatedFrame(int materialCount, int textureCount)
    {
        var original = Frame();
        var source = original.Surface.Snapshot;
        var materials = Enumerable.Range(0, materialCount).Select(index => source.Materials[0] with { StartIndex = index * 3 });
        var indices = Enumerable.Range(0, materialCount).SelectMany(_ => new uint[] { 0, 1, 2 }).ToArray();
        var snapshot = new SceneSnapshot(source.Vertices, indices, materials,
            Enumerable.Repeat(source.Textures[0], textureCount));
        return new(new(snapshot, Enumerable.Repeat(original.Surface.Materials[0], materialCount)));
    }

    private static D3D12GeneratedEndfieldMaterialPreparationResult Prepare(OriginalFrameScene frame,
        IEnumerable<D3D12GeneratedEndfieldMaterialSelection> selection)
        => D3D12GeneratedEndfieldMaterialPreparation.Prepare(frame, selection, "generated-test", "Generated test");

    private static OriginalFrameScene Frame(bool proceduralDiffuse = false, bool dynamic = false)
    {
        SceneVertex[] vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX), new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY)];
        SceneMaterial[] materials = [new("explicit A", 0, 3, new(.8f, .7f, .6f, 1), Vector3.One, new(.2f), 16, 0),
            new("explicit B", 3, 3, Vector4.One, Vector3.Zero, new(.3f), 2)];
        var snapshot = new SceneSnapshot(vertices, [0, 1, 2, 0, 2, 1], materials,
            [proceduralDiffuse ? SceneTexture.ProceduralToon() : new("source", 1, 1, [96, 128, 160, 255])],
            Matrix4x4.CreateTranslation(1, 2, 3), additionalUvs: new(1, new SceneAdditionalUvVertex[3]),
            rawRadialSeeds: new([Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ]));
        if (dynamic) snapshot = snapshot.WithDeformedVertices(vertices);
        var outline = new OriginalOutlineData(vertices.Select(vertex => new OriginalOutlineVertex(vertex.Position,
            vertex.Normal, OriginalOutlineVertexMode.Expand, 1, 0)), [new(.18f, Vector4.One), new(.18f, Vector4.One)]);
        return new(new(snapshot, [new(new(.6f)), new(new(.5f))]), outline,
            new(Vector4.One, Matrix4x4.Identity, Vector3.Zero, 30, 1),
            new(OriginalAlphaBlendMode.Normal, true, true),
            [new(true, true, false), new(false, false, true)], SceneLightingSnapshot.Empty,
            new([new(Vector2.Zero, Vector2.One, Vector4.One)]));
    }
}
