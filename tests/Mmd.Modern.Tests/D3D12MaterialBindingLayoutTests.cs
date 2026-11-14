using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12MaterialBindingLayoutTests
{
    [Fact]
    public void CaptureIsDeterministicAndIgnoresDisplayNamesPlacementLightingCameraAndProfile()
    {
        var original = D3D12MaterialBindingLayout.Capture(Frame());
        var moved = D3D12MaterialBindingLayout.Capture(Frame(rename: true, world: Matrix4x4.CreateTranslation(2, 3, 4),
            camera: SceneCamera.Default with { Eye = new(0, 10, -35) }, light: Vector3.UnitY,
            profile: new(new(.1f), new(.9f), Matrix4x4.CreateRotationY(.2f))));
        Assert.Equal(original.Fingerprint, moved.Fingerprint);
        Assert.Equal(1, original.Version);
        Assert.Equal(2, original.MaterialCount);
        Assert.Equal(2, original.TextureCount);
        Assert.Matches("^[0-9a-f]{64}$", original.Fingerprint);
        original.ValidateFingerprint(moved.Fingerprint);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("normal")]
    [InlineData("uv")]
    [InlineData("indices")]
    [InlineData("material-order")]
    [InlineData("material-range")]
    [InlineData("material-value")]
    [InlineData("material-emission")]
    [InlineData("material-texture")]
    [InlineData("material-flags")]
    [InlineData("surface-binding")]
    [InlineData("texture-factor")]
    [InlineData("additional-uv")]
    [InlineData("additional-count")]
    [InlineData("additional-absent")]
    [InlineData("raw-seed")]
    [InlineData("seed-absent")]
    [InlineData("outline")]
    [InlineData("outline-absent")]
    public void RestLayoutChangesAreDetectedEvenWhenAllCountsMatch(string mutation)
    {
        var original = D3D12MaterialBindingLayout.Capture(Frame());
        var changed = D3D12MaterialBindingLayout.Capture(Frame(mutation: mutation));
        Assert.NotEqual(original.Fingerprint, changed.Fingerprint);
        Assert.Throws<ArgumentException>(() => changed.ValidateFingerprint(original.Fingerprint));
    }

    [Fact]
    public void SignedZeroHasOneCanonicalRepresentation()
    {
        Assert.Equal(D3D12MaterialBindingLayout.Capture(Frame()).Fingerprint,
            D3D12MaterialBindingLayout.Capture(Frame(mutation: "negative-zero")).Fingerprint);
    }

    [Fact]
    public void CallerArrayChangesCannotChangeCapturedIdentityOrSelectedPixels()
    {
        var frame = Frame();
        var layout = D3D12MaterialBindingLayout.Capture(frame);
        string before = layout.Fingerprint;
        byte[] bytes = [20, 40, 60, 255];
        var texture = new SceneTexture("explicit slot", 1, 1, bytes);
        bytes[0] = 255;
        Assert.Equal((byte)20, texture.Rgba[0]);
        SceneVertex[] copy = frame.Surface.Snapshot.Vertices.ToArray();
        copy[0] = copy[0] with { Position = Vector3.One };
        Assert.Equal(before, layout.Fingerprint);
        layout.ValidateTexture(layout.CaptureTexture(0));
    }

    [Fact]
    public void ModernPerFrameOverridesCannotChangeAuthoredLayoutIdentity()
    {
        var frame = Frame();
        var styled = new OriginalFrameScene(frame.Surface, frame.Outline, frame.OutlineOptions,
            materials: frame.Materials.Select(flag => flag with { GiToonResponseOverride = RayGiToonResponse.PreservePaletteBands }));
        Assert.Equal(D3D12MaterialBindingLayout.Capture(frame).Fingerprint, D3D12MaterialBindingLayout.Capture(styled).Fingerprint);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteRestNumbersCannotEnterTheSourceContract(float invalid)
    {
        var frame = Frame();
        var snapshot = frame.Surface.Snapshot;
        var vertices = snapshot.Vertices.ToArray();
        vertices[0] = vertices[0] with { Position = new(invalid, 0, 0) };
        Assert.Throws<ArgumentException>(() => new SceneSnapshot(vertices, snapshot.Indices.ToArray(), snapshot.Materials, snapshot.Textures));
        Assert.Throws<ArgumentException>(() => new OriginalStyleScene(snapshot,
            frame.Surface.Materials.Select(binding => binding with { ToonShadowColor = new(invalid, 0, 0) })));
    }

    [Fact]
    public void DynamicFramesAreRejectedInsteadOfBecomingAnAccidentalRestIdentity()
    {
        var frame = Frame();
        var deformed = new OriginalFrameScene(new OriginalStyleScene(
            frame.Surface.Snapshot.WithDeformedVertices(frame.Surface.Snapshot.Vertices.ToArray()), frame.Surface.Materials));
        Assert.Throws<ArgumentException>(() => D3D12MaterialBindingLayout.Capture(deformed));
    }

    [Fact]
    public void FingerprintVersionAndCanonicalSpellingAreStrict()
    {
        var layout = D3D12MaterialBindingLayout.Capture(Frame());
        Assert.Throws<ArgumentException>(() => layout.ValidateFingerprint(layout.Fingerprint, 2));
        Assert.Throws<ArgumentException>(() => layout.ValidateFingerprint(layout.Fingerprint.ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() => layout.ValidateFingerprint(new string('g', 64)));
        Assert.Throws<ArgumentException>(() => layout.ValidateFingerprint(""));
        Assert.Throws<ArgumentNullException>(() => D3D12MaterialBindingLayout.Capture(null!));
    }

    [Fact]
    public void TexturePixelsAndNamesAreNotPartOfLayoutButExplicitSelectedTextureHasAGuard()
    {
        var original = D3D12MaterialBindingLayout.Capture(Frame());
        var changed = D3D12MaterialBindingLayout.Capture(Frame(mutation: "texture-pixels"));
        Assert.Equal(original.Fingerprint, changed.Fingerprint);
        var selected = original.CaptureTexture(0);
        original.ValidateTexture(selected);
        changed.ValidateTexture(original.CaptureTexture(1)); // Unselected image may change.
        Assert.Throws<ArgumentException>(() => changed.ValidateTexture(selected));
        D3D12MaterialBindingLayout.Capture(Frame(rename: true)).ValidateTexture(selected);
        Assert.Throws<ArgumentOutOfRangeException>(() => original.CaptureTexture(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => original.CaptureTexture(2));
    }

    [Fact]
    public void ExplicitSceneOrdinalIsNeverReplacedByAHashSearch()
    {
        var layout = D3D12MaterialBindingLayout.Capture(Frame());
        var first = layout.CaptureTexture(0);
        var second = layout.CaptureTexture(1);
        Assert.Equal(first.RgbaSha256, second.RgbaSha256);
        Assert.NotEqual(first.SourceSceneTextureOrdinal, second.SourceSceneTextureOrdinal);
        layout.ValidateTexture(first); layout.ValidateTexture(second);
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.ValidateTexture(first with { SourceSceneTextureOrdinal = 2 }));
        Assert.Throws<ArgumentException>(() => layout.ValidateTexture(first with { Width = 2 }));
        Assert.Throws<ArgumentException>(() => layout.ValidateTexture(first with { Height = 2 }));
        Assert.Throws<ArgumentException>(() => layout.ValidateTexture(first with { Kind = SceneTextureKind.ModernSoftToon }));
        Assert.Throws<ArgumentException>(() => layout.ValidateTexture(first with { RgbaSha256 = new string('0', 64) }));
        Assert.Throws<ArgumentException>(() => layout.ValidateTexture(first with { RgbaSha256 = first.RgbaSha256.ToUpperInvariant() }));
    }

    private static OriginalFrameScene Frame(string? mutation = null, bool rename = false, Matrix4x4? world = null,
        SceneCamera? camera = null, Vector3? light = null, OriginalStyleProfile? profile = null)
    {
        SceneVertex[] vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX), new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY)];
        if (mutation == "position") vertices[0] = vertices[0] with { Position = new(.01f, 0, 0) };
        if (mutation == "normal") vertices[0] = vertices[0] with { Normal = Vector3.UnitY };
        if (mutation == "uv") vertices[0] = vertices[0] with { Uv = new(.01f, 0) };
        if (mutation == "negative-zero") vertices[0] = vertices[0] with { Position = new(-0f, 0, -0f), Uv = new(-0f, 0) };
        uint[] indices = mutation == "indices" ? [0, 2, 1, 0, 1, 2] : [0, 1, 2, 0, 1, 2];
        SceneMaterial[] materials = [new(rename ? "new name A" : "A", 0, 3, Vector4.One, Vector3.Zero, new(.2f), 2, 0),
            new(rename ? "new name B" : "B", 3, 3, new(.7f), Vector3.One, new(.3f), 3, 1)];
        if (mutation == "material-order") materials = [materials[1] with { StartIndex = 0 }, materials[0] with { StartIndex = 3 }];
        if (mutation == "material-range") materials = [materials[0] with { IndexCount = 0 }, materials[1] with { StartIndex = 0, IndexCount = 6 }];
        if (mutation == "material-value") materials[0] = materials[0] with { Shininess = 2.1f };
        if (mutation == "material-emission") materials[0] = materials[0] with { Emission = new(.1f) };
        if (mutation == "material-texture") materials[0] = materials[0] with { DiffuseTexture = 1 };
        SceneTexture[] textures = [new(rename ? "moved/A.png" : "A.png", 1, 1,
            mutation == "texture-pixels" ? [21, 40, 60, 255] : [20, 40, 60, 255]),
            new(rename ? "moved/B.png" : "B.png", 1, 1, [20, 40, 60, 255])];
        SceneAdditionalUvVertex[] uv = Enumerable.Repeat(new SceneAdditionalUvVertex(new(.2f), Vector4.Zero, Vector4.Zero, Vector4.Zero), 3).ToArray();
        if (mutation == "additional-uv") uv[0] = uv[0] with { Uv1 = new(.3f) };
        Vector3[] seeds = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        if (mutation == "raw-seed") seeds[0] = new(.2f, 1, 0);
        var snapshot = new SceneSnapshot(vertices, indices, materials, textures, world, camera, light,
            mutation == "additional-absent" ? null : new(mutation == "additional-count" ? 2 : 1, uv),
            mutation == "seed-absent" ? null : new(seeds));
        OriginalStyleMaterialBinding[] bindings = [new(new(.6f)), new(new(.5f))];
        if (mutation == "surface-binding") bindings[0] = bindings[0] with { DoubleSided = true };
        if (mutation == "texture-factor") bindings[0] = bindings[0] with
        { TextureFactors = OriginalMaterialTextureFactors.Identity with { Diffuse = new(new(.8f), Vector4.Zero) } };
        OriginalFrameMaterialFlags[] flags = [new(false, true, false), new(false, true, false)];
        if (mutation == "material-flags") flags[0] = flags[0] with { CastSelfShadow = true };
        var outline = new OriginalOutlineData(vertices.Select(v => new OriginalOutlineVertex(v.Position, v.Normal,
            OriginalOutlineVertexMode.Expand, 1, 0)), [new(mutation == "outline" ? .3f : .2f, Vector4.One), new(.2f, Vector4.One)]);
        return new(new(snapshot, bindings, profile), mutation == "outline-absent" ? null : outline,
            mutation == "outline-absent" ? null : new(Vector4.One, Matrix4x4.Identity, Vector3.Zero, 30, 1), materials: flags);
    }
}
