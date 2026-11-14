using System.Numerics;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class SurfaceDrawOrderTests
{
    [Fact]
    public void OriginalAndNullOptionsKeepEligibleMaterialOrder()
    {
        var specs = new[] { (0.5f, -1, OriginalSphereMode.None), (1f, -1, OriginalSphereMode.None), (1f, -1, OriginalSphereMode.None) };
        Assert.Equal(new[] { 0, 1, 2 }, SurfaceDrawOrderPlanner.Plan(Frame(specs, null)));
        Assert.Equal(new[] { 0, 1, 2 }, SurfaceDrawOrderPlanner.Plan(Frame(specs, Options(SurfaceDrawOrder.Original))));
    }

    [Fact]
    public void OpaqueFirstIsStableAndOmitsDisabledAndEmptyMaterials()
    {
        var specs = new[] { (.5f, -1, OriginalSphereMode.None), (1f, -1, OriginalSphereMode.None), (.4f, -1, OriginalSphereMode.None), (2f, -1, OriginalSphereMode.None), (1f, -1, OriginalSphereMode.None), (1f, -1, OriginalSphereMode.None) };
        var frame = Frame(specs, Options(SurfaceDrawOrder.OpaqueFirst), disabled: 4, empty: 5);
        Assert.Equal(new[] { 1, 3, 0, 2 }, SurfaceDrawOrderPlanner.Plan(frame));
    }

    [Fact]
    public void DiffuseAndSphereAlphaAndSubTextureAreConservativelyTransparent()
    {
        var specs = new[] { (1f, 0, OriginalSphereMode.None), (1f, 0, OriginalSphereMode.Multiply), (1f, 0, OriginalSphereMode.Add), (1f, 1, OriginalSphereMode.SubTexture), (1f, 1, OriginalSphereMode.None) };
        Assert.Equal(new[] { 4, 0, 1, 2, 3 }, SurfaceDrawOrderPlanner.Plan(Frame(specs, Options(SurfaceDrawOrder.OpaqueFirst), subToonAlpha: .5f)));
        Assert.Equal(new[] { 3, 4, 0, 1, 2 }, SurfaceDrawOrderPlanner.Plan(Frame(specs, Options(SurfaceDrawOrder.OpaqueFirst))));
    }

    [Fact]
    public void MaterialAlphaIsReevaluatedAndTextureMorphWDoesNotAffectOpacity()
    {
        var opaque = Frame(new[] { (1f, 1, OriginalSphereMode.None), (1f, -1, OriginalSphereMode.None) }, Options(SurfaceDrawOrder.OpaqueFirst), morphW: true);
        var changed = Frame(new[] { (.25f, 1, OriginalSphereMode.None), (1f, -1, OriginalSphereMode.None) }, Options(SurfaceDrawOrder.OpaqueFirst), morphW: true);
        Assert.Equal(new[] { 0, 1 }, SurfaceDrawOrderPlanner.Plan(opaque));
        Assert.Equal(new[] { 1, 0 }, SurfaceDrawOrderPlanner.Plan(changed));
    }

    [Fact]
    public void TranspDiagnosticDoesNotReorderAndInvalidOrderIsRejected()
    {
        var specs = new[] { (.5f, -1, OriginalSphereMode.None), (1f, -1, OriginalSphereMode.None) };
        Assert.Equal(new[] { 0, 1 }, SurfaceDrawOrderPlanner.Plan(Frame(specs, Options(SurfaceDrawOrder.OpaqueFirst) with { Transp = true })));
        Assert.Equal(new[] { 0, 1 }, SurfaceDrawOrderPlanner.Plan(Frame(specs, Options(SurfaceDrawOrder.OpaqueFirst) with { BlendMode = OriginalAlphaBlendMode.Additive })));
        Assert.Throws<ArgumentException>(() => OriginalFrameMath.Validate(Options((SurfaceDrawOrder)99)));
    }

    private static OriginalFrameOptions Options(SurfaceDrawOrder order) => new(OriginalAlphaBlendMode.Normal, true, false, SurfaceOrder: order);

    private static OriginalFrameScene Frame((float alpha, int texture, OriginalSphereMode sphere)[] specs, OriginalFrameOptions? options,
        int disabled = -1, int empty = -1, bool morphW = false, float subToonAlpha = 1)
    {
        var vertices = new[] { new SceneVertex(Vector3.Zero, Vector3.UnitZ, Vector2.Zero), new SceneVertex(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero), new SceneVertex(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero) };
        var materials = new List<SceneMaterial>(); var bindings = new List<OriginalStyleMaterialBinding>(); var flags = new List<OriginalFrameMaterialFlags>(); var indices = new List<uint>();
        for (int i = 0; i < specs.Length; i++)
        {
            var (alpha, texture, sphere) = specs[i]; int count = i == empty ? 0 : 3;
            materials.Add(new($"m{i}", indices.Count, count, new(1, 1, 1, alpha), Vector3.Zero, Vector3.Zero, 1, sphere == OriginalSphereMode.None ? texture : sphere == OriginalSphereMode.SubTexture ? 1 : -1));
            if (count != 0) indices.AddRange(new uint[] { 0, 1, 2 });
            var binding = new OriginalStyleMaterialBinding(Vector3.Zero, sphere == OriginalSphereMode.None ? -1 : texture, sphere);
            if (morphW) binding = binding with { TextureFactors = new(new(Vector4.Zero, Vector4.One), new(Vector4.Zero, Vector4.One), OriginalTextureFactor.Identity) };
            if (sphere == OriginalSphereMode.SubTexture) binding = binding with { TextureFactors = binding.TextureFactors with { Toon = new(new(1, 1, 1, subToonAlpha), Vector4.Zero) } };
            bindings.Add(binding); flags.Add(new(false, false, false, SurfaceEnabled: i != disabled));
        }
        var textures = new[] { new SceneTexture("alpha", 1, 1, new byte[] { 255, 255, 255, 127 }), new SceneTexture("opaque", 1, 1, new byte[] { 255, 255, 255, 255 }) };
        var uv = new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(Vector4.Zero, Vector4.Zero, Vector4.Zero, Vector4.Zero), 3).ToArray());
        return new(new(new(vertices, indices.ToArray(), materials, textures, additionalUvs: uv), bindings), options: options, materials: flags);
    }
}
