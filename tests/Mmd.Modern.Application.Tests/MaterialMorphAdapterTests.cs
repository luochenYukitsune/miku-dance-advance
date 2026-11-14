using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class MaterialMorphAdapterTests
{
    [Fact]
    public void AnimatedMaterialAlonePreservesPublishedVertexAndOutlineStorage()
    {
        var model=Model([Morph("alpha",Offset(0,PmxMaterialMorphOperation.Multiply,new(1,1,1,0)))],rig:true);
        var baseline=Baseline(model);
        var motion=new VmdMotion {BoneKeyframes=[new(){BoneName="root"},new(){BoneName="root",Frame=20}],
            MorphKeyframes=[new(){MorphName="alpha",Weight=.25f},new(){MorphName="alpha",Frame=20,Weight=.75f}]};
        using var controller=new CharacterAnimationController(model,motion,baseline.Surface,baseline.Outline);
        var first=controller.SampleAt(0); var originalVertices=first.Surface.Snapshot.Vertices.ToArray();
        var next=controller.SampleAt(.5);
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(first.Surface.Snapshot.Vertices),
            ref MemoryMarshal.GetReference(next.Surface.Snapshot.Vertices)),"Unchanged final geometry received a new vertex storage identity.");
        Assert.Same(first.Outline.Vertices,next.Outline.Vertices);
        Assert.NotEqual(first.Surface.Snapshot.Materials[0].Diffuse,next.Surface.Snapshot.Materials[0].Diffuse);
        Assert.Equal(originalVertices,first.Surface.Snapshot.Vertices.ToArray());
        using var fresh=new CharacterAnimationController(model,motion,baseline.Surface,baseline.Outline);
        var oracle=fresh.SampleAt(.5);
        Assert.Equal(oracle.Surface.Snapshot.Vertices.ToArray(),next.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(oracle.Surface.Snapshot.Materials,next.Surface.Snapshot.Materials);
    }

    [Fact]
    public void ChangingMaterialWeightsShareTheUnmodifiedOutlineGeometry()
    {
        var model=Model([Morph("alpha",Offset(0,PmxMaterialMorphOperation.Multiply,new(1,1,1,0)))]);
        var adapter=new PmxMaterialMorphAdapter(model);
        var baseline=Baseline(model);
        const int count=32768;
        var vertices=Enumerable.Range(0,count).Select(i=>baseline.Surface.Snapshot.Vertices[i%baseline.Surface.Snapshot.VertexCount]).ToArray();
        var snapshot=new SceneSnapshot(vertices,baseline.Surface.Snapshot.Indices,baseline.Surface.Snapshot.Materials,
            baseline.Surface.Snapshot.Textures);
        var surface=new OriginalStyleScene(snapshot,baseline.Surface.Materials,baseline.Surface.Profile);
        var outline=new OriginalOutlineData(Enumerable.Range(0,count).Select(i=>baseline.Outline.Vertices[i%baseline.Outline.VertexCount]),baseline.Outline.Materials);
        var original=outline.Vertices.ToArray();
        adapter.Apply(surface,outline,new Dictionary<int,float>{[0]=.25f});
        long before=GC.GetAllocatedBytesForCurrentThread();
        var next=adapter.Apply(surface,outline,new Dictionary<int,float>{[0]=.5f});
        long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.True(bytes<128*1024,$"A material-only morph copied {count} outline vertices: {bytes} bytes.");
        Assert.Same(outline.Vertices,next.Outline.Vertices);
        Assert.Equal(original,next.Outline.Vertices);
        Assert.Equal(.4f,next.Surface.Snapshot.Materials[0].Diffuse.W);
        Assert.Equal(.8f,surface.Snapshot.Materials[0].Diffuse.W);
        Assert.Equal(outline.Materials,next.Outline.Materials);
    }

    [Fact]
    public void IndexedTransparencyPreservesIndependentEdgeAlphaAndUnaffectedMaterials()
    {
        var model = Model([Morph("透明度", Offset(0, PmxMaterialMorphOperation.Multiply, new(1, 1, 1, 0)))]);
        var adapter = new PmxMaterialMorphAdapter(model);
        var (surface, outline) = Baseline(model);
        var result = adapter.Apply(surface, outline, new Dictionary<int, float> { [0] = 1 });
        Assert.Equal(0, result.Surface.Snapshot.Materials[0].Diffuse.W);
        Assert.Equal(surface.Snapshot.Materials[1], result.Surface.Snapshot.Materials[1]);
        Assert.Equal(outline.Materials[0], result.Outline.Materials[0]);
        Assert.Equal(1, adapter.SupportedMorphCount);
        Assert.Equal(0, adapter.SupportedMorphIndices["透明度"]);
    }

    [Fact]
    public void EdgeAlphaIsEvaluatedSeparatelyWithoutChangingSurfaceAlphaOrDrawFlag()
    {
        var model = Model([Morph("edge", Offset(0, PmxMaterialMorphOperation.Multiply, Vector4.One, new(1, 1, 1, 0)))]);
        var (surface, outline) = Baseline(model);
        var result = new PmxMaterialMorphAdapter(model).Apply(surface, outline, new Dictionary<int, float> { [0] = 1 });
        Assert.Equal(surface.Snapshot.Materials[0].Diffuse, result.Surface.Snapshot.Materials[0].Diffuse);
        Assert.Equal(0, result.Outline.Materials[0].Color.W);
        Assert.Equal(outline.Materials[0].Width, result.Outline.Materials[0].Width);
        Assert.True(result.Outline.Materials[0].DrawEnabled);
    }

    [Fact]
    public void AllMaterialsUseSeparateMultiplyAndAddAccumulatorsAndCallerScaledEdgeBaseline()
    {
        // Add precedes multiply deliberately: it must not become part of the multiplied base.
        var add = Offset(-1, PmxMaterialMorphOperation.Add, new(.4f), new(.4f), .4f);
        var multiply = Offset(-1, PmxMaterialMorphOperation.Multiply, new(.5f), new(.5f), .5f);
        var model = Model([Morph("add", add), Morph("mul", multiply)]);
        var adapter = new PmxMaterialMorphAdapter(model);
        var (surface, originalOutline) = Baseline(model);
        var outline = new OriginalOutlineData(originalOutline.Vertices, originalOutline.Materials.Select(m => m with { Width = m.Width * 3 }));
        var result = adapter.Apply(surface, outline, new Dictionary<int, float> { [1] = .5f, [0] = .5f });
        for (int i = 0; i < model.Materials.Count; i++)
        {
            var before = surface.Snapshot.Materials[i]; var after = result.Surface.Snapshot.Materials[i];
            Assert.Equal(before.Diffuse * .75f + new Vector4(.2f), after.Diffuse);
            Assert.Equal(before.Ambient * .75f + new Vector3(.2f), after.Ambient);
            Assert.Equal(before.Specular * .75f + new Vector3(.2f), after.Specular);
            Assert.Equal(before.Shininess * .75f + .2f, after.Shininess);
            Assert.Equal(outline.Materials[i].Color * .75f + new Vector4(.2f), result.Outline.Materials[i].Color);
            Assert.Equal(outline.Materials[i].Width * .75f + .2f, result.Outline.Materials[i].Width);
        }
    }

    [Theory]
    [InlineData(-2f)]
    [InlineData(3f)]
    public void FiniteWeightsAndResultingColorsAreNotClamped(float weight)
    {
        var model = Model([Morph("alpha", Offset(0, PmxMaterialMorphOperation.Add, new(0, 0, 0, .5f)))]);
        var (surface, outline) = Baseline(model);
        var result = new PmxMaterialMorphAdapter(model).Apply(surface, outline, new Dictionary<int, float> { [0] = weight });
        Assert.Equal(surface.Snapshot.Materials[0].Diffuse.W + .5f * weight, result.Surface.Snapshot.Materials[0].Diffuse.W);
    }

    [Fact]
    public void SamplingRestBaselinesNeverAccumulatesAndPreservesDynamicGeometryAndStaticResources()
    {
        var model = Model([Morph("alpha", Offset(0, PmxMaterialMorphOperation.Multiply, new(1, 1, 1, 0)))]);
        var adapter = new PmxMaterialMorphAdapter(model);
        var (restSurface, outline) = Baseline(model);
        var vertices = restSurface.Snapshot.Vertices.ToArray();
        vertices[0] = vertices[0] with { Position = new(4, 5, 6), Uv = new(.25f, .75f) };
        var surface = new OriginalStyleScene(restSurface.Snapshot.WithDeformedVertices(vertices), restSurface.Materials, restSurface.Profile);
        var weights = new Dictionary<int, float> { [0] = .5f };
        var first = adapter.Apply(surface, outline, weights);
        var again = adapter.Apply(surface, outline, weights);
        Assert.Equal(.4f, first.Surface.Snapshot.Materials[0].Diffuse.W);
        Assert.Equal(first.Surface.Snapshot.Materials[0], again.Surface.Snapshot.Materials[0]);
        Assert.Equal(.8f, surface.Snapshot.Materials[0].Diffuse.W);
        Assert.True(first.Surface.Snapshot.HasDynamicVertices);
        Assert.Same(surface.Snapshot.Textures, first.Surface.Snapshot.Textures);
        Assert.Same(surface.Profile, first.Surface.Profile);
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(surface.Snapshot.Vertices), ref MemoryMarshal.GetReference(first.Surface.Snapshot.Vertices)));
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(surface.Snapshot.Indices), ref MemoryMarshal.GetReference(first.Surface.Snapshot.Indices)));
        Assert.Equal(surface.Snapshot.Materials[0].DiffuseTexture, first.Surface.Snapshot.Materials[0].DiffuseTexture);
        Assert.Equal(surface.Snapshot.Materials[1].StartIndex, first.Surface.Snapshot.Materials[1].StartIndex);
        var zero = adapter.Apply(surface, outline, new Dictionary<int, float>());
        Assert.Same(surface, zero.Surface);
        Assert.Same(outline, zero.Outline);
    }

    [Fact]
    public void OffsetsAreCopiedDuplicateNamesKeepFirstPmxAndNonidentityTextureFactorsAreDiagnosed()
    {
        PmxMorph.MaterialOffset[] offsets = [Offset(0, PmxMaterialMorphOperation.Multiply, new(1, 1, 1, 0))];
        var materialMorph = Morph("same", offsets);
        var blocked = new PmxMorph { Name = "same", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position };
        var texture = Offset(0, PmxMaterialMorphOperation.Add, Vector4.Zero) with { TextureFactor = Vector4.One, SphereTextureFactor = Vector4.One, ToonTextureFactor = Vector4.One };
        var model = Model([blocked, materialMorph, Morph("factors", texture)]);
        var adapter = new PmxMaterialMorphAdapter(model);
        offsets[0] = offsets[0] with { Diffuse = Vector4.One };
        Assert.False(adapter.SupportedMorphIndices.ContainsKey("same"));
        Assert.Equal(2, adapter.SupportedMorphIndices["factors"]);
        Assert.Contains(adapter.Diagnostics, d => d.Contains("Duplicate", StringComparison.Ordinal));
        Assert.Contains(adapter.Diagnostics, d => d.Contains("texture", StringComparison.OrdinalIgnoreCase) && d.Contains("factors", StringComparison.Ordinal));
        var (surface, outline) = Baseline(model);
        Assert.Equal(0, adapter.Apply(surface, outline, new Dictionary<int, float> { [1] = 1 }).Surface.Snapshot.Materials[0].Diffuse.W);
    }

    [Fact]
    public void InvalidOffsetsAndNonfiniteWeightsAreRejectedAndNegativeWidthHasExplicitBoundary()
    {
        Assert.Throws<ArgumentException>(() => new PmxMaterialMorphAdapter(Model([Morph("invalid", Offset(-2, PmxMaterialMorphOperation.Add, Vector4.Zero))])));
        Assert.Throws<ArgumentException>(() => new PmxMaterialMorphAdapter(Model([Morph("invalid", Offset(2, PmxMaterialMorphOperation.Add, Vector4.Zero))])));
        Assert.Throws<ArgumentException>(() => new PmxMaterialMorphAdapter(Model([Morph("invalid", Offset(0, PmxMaterialMorphOperation.Add, new(float.NaN)))])));
        var model = Model([Morph("edge", Offset(0, PmxMaterialMorphOperation.Add, Vector4.Zero, Vector4.Zero, -4))]);
        var adapter = new PmxMaterialMorphAdapter(model); var (surface, outline) = Baseline(model);
        Assert.Throws<ArgumentException>(() => adapter.Apply(surface, outline, new Dictionary<int, float> { [999] = float.PositiveInfinity }));
        Assert.Contains("width", Assert.Throws<NotSupportedException>(() => adapter.Apply(surface, outline, new Dictionary<int, float> { [0] = 1 })).Message, StringComparison.OrdinalIgnoreCase);
        var power = new PmxMaterialMorphAdapter(Model([Morph("power", Offset(0, PmxMaterialMorphOperation.Add, Vector4.Zero) with { SpecularPower = -8 })]));
        Assert.Contains("power", Assert.Throws<NotSupportedException>(() => power.Apply(surface, outline, new Dictionary<int, float> { [0] = 1 })).Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static PmxMorph Morph(string name, params PmxMorph.MaterialOffset[] offsets) => new()
    {
        Name = name, EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material, MaterialOffsets = offsets
    };

    internal static PmxMorph.MaterialOffset Offset(int index, PmxMaterialMorphOperation operation, Vector4 diffuse, Vector4? edge = null, float? width = null)
    {
        bool multiply = operation == PmxMaterialMorphOperation.Multiply;
        return new(index, operation, diffuse, new(diffuse.X), diffuse.X, new(diffuse.X),
            edge ?? (multiply ? Vector4.One : Vector4.Zero), width ?? (multiply ? 1 : 0),
            multiply ? Vector4.One : Vector4.Zero, multiply ? Vector4.One : Vector4.Zero, multiply ? Vector4.One : Vector4.Zero);
    }

    internal static (OriginalStyleScene Surface, OriginalOutlineData Outline) Baseline(PmxModel model)
        => (PmxOriginalStyleAdapter.FromRestPose(model, _ => new("diffuse", 1, 1, [255, 255, 255, 255])).Scene, PmxOriginalOutlineAdapter.FromRestPose(model));

    internal static PmxModel Model(IReadOnlyList<PmxMorph> morphs,bool rig=false) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "material-test", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = Enumerable.Range(0, 3).Select(i => new PmxVertex { Position = new(i, 0, 0), Normal = Vector3.UnitZ, Uv = Vector2.Zero,
            AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [rig?0:-1, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
        Faces = [new(0, 1, 2), new(0, 2, 1)], Textures = ["diffuse"],
        Materials = Enumerable.Range(0, 2).Select(i => new PmxMaterial { Name = $"material-{i}", EnglishName = "", Diffuse = new(.8f / (i + 1)),
            Specular = new(2), SpecularPower = 4, Ambient = new(.2f), DrawMode = PmxDrawModeFlags.DrawEdge, EdgeColor = new(.1f, .2f, .3f, .8f), EdgeSize = 2,
            TextureIndex = 0, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }).ToArray(),
        Bones = rig?SharedPhysicsFixture.Model(physics:false).Bones:[], Morphs = morphs, DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
    };
}
