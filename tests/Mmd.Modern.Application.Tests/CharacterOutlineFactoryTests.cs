using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class CharacterOutlineFactoryTests
{
    private static readonly MethodInfo GeneratedMethod = typeof(OriginalOutlineData)
        .GetMethod("CreateGenerated", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Fact]
    public void GeneratedOutlineMatchesEveryPublicConstructorFieldAndPublishesStableStorage()
    {
        var source = Vertices(3); var materials = Materials();
        var expected = new OriginalOutlineData(source, materials);
        var writer = new WriterState(source);
        var actual = Generated(source.Length, materials, Writer(writer));
        Assert.Equal(1, writer.Calls); Assert.Equal(source.Length, writer.Length);
        Assert.Equal(Bytes(expected), Bytes(actual)); Assert.Equal(expected.Materials, actual.Materials);

        source[0] = source[0] with { Position = new(999), Mode = OriginalOutlineVertexMode.Inward };
        materials[0] = new(999, Vector4.One);
        _ = Generated(source.Length, materials, Writer(writer));
        Assert.Equal(Bytes(expected), Bytes(actual)); Assert.Equal(expected.Materials, actual.Materials);
    }

    [Fact]
    public void PublicOutlineAndSurfaceDeformationStillCopyCallerOwnedArrays()
    {
        var vertices = Vertices(3); var materials = Materials();
        var outline = new OriginalOutlineData(vertices, materials); var before = Bytes(outline);
        vertices[0] = vertices[0] with { VertexScale = 99 }; materials[0] = new(99, Vector4.One);
        Assert.Equal(before, Bytes(outline)); Assert.Equal(.4f, outline.Materials[0].Width);

        var rest = EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model()).Surface.Snapshot;
        var callerOwned = rest.Vertices.ToArray();
        var deformed = rest.WithDeformedVertices(callerOwned); var saved = deformed.Vertices.ToArray();
        callerOwned[0] = callerOwned[0] with { Position = new(999), Normal = new(float.NaN, 0, 0) };
        Assert.Equal(saved, deformed.Vertices.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void GeneratedOutlineRetainsAllVertexValidation(int invalid)
    {
        var source = Vertices(1);
        source[0] = invalid switch
        {
            0 => source[0] with { Position = new(float.NaN, 0, 0) },
            1 => source[0] with { Normal = new(0, float.PositiveInfinity, 0) },
            2 => source[0] with { Mode = (OriginalOutlineVertexMode)99 },
            3 => source[0] with { VertexScale = float.NaN },
            4 => source[0] with { VertexScale = -.1f },
            5 => source[0] with { SelectedMaterialIndex = -1 },
            _ => source[0] with { SelectedMaterialIndex = 1 }
        };
        var materials = Materials();
        Assert.ThrowsAny<ArgumentException>(() => new OriginalOutlineData(source, materials));
        Assert.ThrowsAny<ArgumentException>(() => Generated(source.Length, materials, Writer(new(source))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void GeneratedOutlineRetainsAllMaterialValidation(int invalid)
    {
        OriginalOutlineMaterial[] materials = invalid switch
        {
            0 => [],
            1 => [null!],
            2 => [new(float.NaN, Vector4.One)],
            3 => [new(-1, Vector4.One)],
            _ => [new(1, new(0, float.PositiveInfinity, 0, 1))]
        };
        var source = Vertices(1);
        Assert.ThrowsAny<ArgumentException>(() => new OriginalOutlineData(source, materials));
        Assert.ThrowsAny<ArgumentException>(() => Generated(1, materials, Writer(new(source))));
    }

    [Fact]
    public void GeneratedOutlineRejectsInvalidSizeAndMissingInputs()
    {
        var writer = Writer(new(Vertices(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Generated(0, Materials(), writer));
        Assert.Throws<ArgumentOutOfRangeException>(() => Generated(-1, Materials(), writer));
        Assert.Throws<ArgumentNullException>(() => Generated(1, null!, writer));
        Assert.Throws<ArgumentNullException>(() => Generated(1, Materials(), null));
    }

    [Fact]
    public void GeneratedOutlineRemovesOneFullTemporaryVertexArray()
    {
        const int count = 20000;
        var source = Vertices(count); var materials = Materials(); var writer = Writer(new(source));
        OriginalOutlineData OldConstruction()
        {
            var temporary = new OriginalOutlineVertex[count]; source.AsSpan().CopyTo(temporary);
            return new(temporary, materials);
        }
        _ = OldConstruction(); _ = Generated(count, materials, writer);
        _ = OldConstruction(); _ = Generated(count, materials, writer);
        long before = GC.GetAllocatedBytesForCurrentThread(); var old = OldConstruction();
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread(); var generated = Generated(count, materials, writer);
        long generatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        long oneArrayPayload = (long)count * Unsafe.SizeOf<OriginalOutlineVertex>();
        // Reflection argument boxing is small; the saved array must remain proportional to vertices.
        Assert.InRange(oldBytes - generatedBytes, oneArrayPayload - 4096, oneArrayPayload + 4096);
        Assert.Equal(Bytes(old), Bytes(generated));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10923)]
    public void ControllerOutlineMatchesIndependentFullConstructionAndKeepsEarlierFrames(int triangles)
    {
        // 32769 vertices exercises multiple owned chunks and a one-vertex final chunk.
        var model = ControllerModel(triangles); var rest = EditedMotionProjectTests.Rest(model);
        var outline = ControllerOutline(rest.Outline!); var motion = ControllerMotion();
        using var controller = new CharacterAnimationController(model, motion, rest.Surface, outline);
        var animator = new CharacterAnimator(PmxCharacterRigAdapter.Convert(model, rest.Surface.Snapshot), MmdBoneMotionAdapter.Convert(motion));
        var actual = controller.SampleAt(4.25); var saved = Bytes(actual.Outline);
        var savedSurface = Bytes(actual.Surface.Snapshot);
        Assert.True(CurrentPose(controller).Vertices.AsSpan().Overlaps(actual.Surface.Snapshot.Vertices));
        AssertControllerOracle(animator.Sample(4.25), outline, outline.Materials, actual);
        var later = controller.Advance(8.5, .1);
        AssertControllerOracle(animator.Sample(8.5), outline, outline.Materials, later);
        Assert.False(saved.AsSpan(0, 12).SequenceEqual(Bytes(later.Outline).AsSpan(0, 12)));
        Assert.Equal(saved, Bytes(actual.Outline));
        Assert.Equal(savedSurface, Bytes(actual.Surface.Snapshot));
        var earlier = controller.SampleAt(1.5);
        AssertControllerOracle(animator.Sample(1.5), outline, outline.Materials, earlier);
        Assert.Equal(saved, Bytes(actual.Outline));
        var reset = controller.ResetPhysics(6);
        AssertControllerOracle(animator.Sample(6), outline, outline.Materials, reset);
        var parents = new Dictionary<int, Matrix4x4> { [-1] = Matrix4x4.CreateTranslation(2, -3, 4) };
        controller.SetOutsideParents(parents);
        var final = controller.SampleAt(10);
        AssertControllerOracle(animator.Sample(10, new() { OutsideParents = parents }), outline, outline.Materials, final);
        var savedFinal = Bytes(final.Surface.Snapshot);
        var nextParents = new Dictionary<int, Matrix4x4> { [-1] = Matrix4x4.CreateTranslation(-4, 5, -6) };
        var refreshed = controller.RefreshFrozenOutsideParents(nextParents);
        AssertControllerOracle(animator.Sample(10, new() { OutsideParents = nextParents }), outline, outline.Materials, refreshed);
        Assert.Equal(savedFinal, Bytes(final.Surface.Snapshot));
        Assert.Equal(savedSurface, Bytes(actual.Surface.Snapshot)); Assert.Equal(saved, Bytes(actual.Outline));
    }

    [Fact]
    public void ControllerKeepsCopyBoundaryForDisabledSoftBodyModels()
    {
        var model = PmxSoftBodyTests.MaterialFixture(); var rest = EditedMotionProjectTests.Rest(model);
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = model.Bones[0].Name }] };
        using var controller = new CharacterAnimationController(model, motion, rest.Surface, rest.Outline!);
        var actual = controller.SampleAt(0); var pose = CurrentPose(controller);
        Assert.NotEmpty(model.SoftBodies); Assert.False(controller.SoftBodiesEnabled);
        Assert.False(pose.Vertices.AsSpan().Overlaps(actual.Surface.Snapshot.Vertices));
        Assert.Equal(MemoryMarshal.AsBytes(pose.Vertices.AsSpan()).ToArray(), Bytes(actual.Surface.Snapshot));
    }

    [Fact]
    public void ControllerDeformedOutlineKeepsPreparedMaterialMorphsAndEarlierMaterialSnapshots()
    {
        var model = ControllerModel(2, materialMorph: true); var rest = EditedMotionProjectTests.Rest(model);
        var outline = ControllerOutline(rest.Outline!); var motion = ControllerMotion(materialMorph: true);
        using var controller = new CharacterAnimationController(model, motion, rest.Surface, outline);
        var animator = new CharacterAnimator(PmxCharacterRigAdapter.Convert(model, rest.Surface.Snapshot), MmdBoneMotionAdapter.Convert(motion));
        var colorDelta = new Vector4(.2f, -.1f, .3f, -.4f);
        OriginalOutlineMaterial[] ExpectedMaterials(float weight) => outline.Materials.Select((material, index) => index == 1
            ? material with { Width = material.Width + .6f * weight, Color = material.Color + colorDelta * weight }
            : material).ToArray();

        var actual = controller.SampleAt(5); var saved = Bytes(actual.Outline); var savedMaterials = actual.Outline.Materials.ToArray();
        AssertControllerOracle(animator.Sample(5), outline, ExpectedMaterials(.5f), actual);
        Assert.NotEqual(outline.Materials[1], actual.Outline.Materials[1]);
        var later = controller.SampleAt(10);
        AssertControllerOracle(animator.Sample(10), outline, ExpectedMaterials(1), later);
        Assert.Equal(saved, Bytes(actual.Outline)); Assert.Equal(savedMaterials, actual.Outline.Materials);
        var disabled = controller.SampleAt(10, enableMorphs: false);
        AssertControllerOracle(animator.Sample(10), outline, outline.Materials, disabled);
        Assert.Equal(saved, Bytes(actual.Outline)); Assert.Equal(savedMaterials, actual.Outline.Materials);
    }

    private static void AssertControllerOracle(CharacterSample pose, OriginalOutlineData metadata,
        IReadOnlyList<OriginalOutlineMaterial> materials, (OriginalStyleScene Surface, OriginalOutlineData Outline) actual)
    {
        var expected = new OriginalOutlineData(metadata.Vertices.Select((vertex, index) => vertex with
        { Position = pose.Vertices[index].Position, Normal = pose.Vertices[index].Normal }), materials);
        var publicSurface = actual.Surface.Snapshot.WithDeformedVertices(pose.Vertices);
        Assert.Equal(Bytes(publicSurface), Bytes(actual.Surface.Snapshot));
        Assert.Equal(Bytes(expected), Bytes(actual.Outline)); Assert.Equal(materials, actual.Outline.Materials);
        // Compare the raw float bytes independently of Vector3's numeric equality.
        var surfaceVectors = actual.Surface.Snapshot.Vertices.ToArray().SelectMany(vertex => new[] { vertex.Position, vertex.Normal }).ToArray();
        var outlineVectors = actual.Outline.Vertices.SelectMany(vertex => new[] { vertex.Position, vertex.Normal }).ToArray();
        Assert.Equal(MemoryMarshal.AsBytes(surfaceVectors.AsSpan()).ToArray(), MemoryMarshal.AsBytes(outlineVectors.AsSpan()).ToArray());
    }

    private static OriginalOutlineData ControllerOutline(OriginalOutlineData source) => new(source.Vertices.Select((vertex, index) => vertex with
    {
        Mode = index % 2 == 0 ? OriginalOutlineVertexMode.Inward : OriginalOutlineVertexMode.Expand,
        VertexScale = .25f + index % 7, SelectedMaterialIndex = index % 2
    }), [new(.4f, new(.1f, .2f, .3f, .8f)), new(.8f, new(.3f, .4f, .5f, .9f), false)]);

    private static VmdMotion ControllerMotion(bool materialMorph = false) => new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 10, Translation = new(3, 2, 1),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f) }],
        MorphKeyframes = materialMorph ? [new() { MorphName = "edge" }, new() { MorphName = "edge", Frame = 10, Weight = 1 }] : []
    };

    private static PmxModel ControllerModel(int triangles, bool materialMorph = false)
    {
        var source = EditedMotionProjectTests.Model();
        PmxMaterial Material(string name, int count) => new()
        {
            Name = name, EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
            Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.DrawEdge, EdgeColor = Vector4.One, EdgeSize = 1,
            TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
            ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = count
        };
        return new()
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 4, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "outline refresh", EnglishName = "", Comment = "", EnglishComment = "", Textures = [],
            Vertices = Enumerable.Range(0, triangles * 3).Select(index => new PmxVertex
            {
                Position = source.Vertices[index % 3].Position + new Vector3(index / 3 * .125f, 0, 0),
                Normal = index % 3 == 0 ? Vector3.Zero : new(.25f, 3, .5f), Uv = new(index % 3 * .25f), AdditionalUvs = [],
                WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1
            }).ToArray(),
            Faces = Enumerable.Range(0, triangles).Select(index => new PmxTriangle(index * 3, index * 3 + 1, index * 3 + 2)).ToArray(),
            Materials = [Material("first", 3), Material("second", (triangles - 1) * 3)], Bones = source.Bones,
            Morphs = materialMorph ? [new() { Name = "edge", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Material,
                MaterialOffsets = [new(1, PmxMaterialMorphOperation.Add, Vector4.Zero, Vector3.Zero, 0, Vector3.Zero,
                    new(.2f, -.1f, .3f, -.4f), .6f, Vector4.Zero, Vector4.Zero, Vector4.Zero)] }] : [],
            DisplayFrames = [], Joints = [], RigidBodies = [], SoftBodies = []
        };
    }

    private sealed class WriterState(OriginalOutlineVertex[] source)
    {
        internal int Calls { get; private set; }
        internal int Length { get; private set; }
        public void Write(Span<OriginalOutlineVertex> destination)
        { Calls++; Length = destination.Length; source.AsSpan().CopyTo(destination); }
    }
    private static Delegate Writer(WriterState state) => Delegate.CreateDelegate(
        GeneratedMethod.GetParameters()[2].ParameterType, state, nameof(WriterState.Write));
    private static OriginalOutlineData Generated(int count, IEnumerable<OriginalOutlineMaterial> materials, Delegate? writer)
    {
        try { return (OriginalOutlineData)GeneratedMethod.Invoke(null, [count, materials, writer])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static OriginalOutlineMaterial[] Materials() => [new(.4f, new(.1f, .2f, .3f, .8f))];
    private static OriginalOutlineVertex[] Vertices(int count) => Enumerable.Range(0, count)
        .Select(i => new OriginalOutlineVertex(new(i, -.5f, 2), i % 2 == 0 ? Vector3.Zero : new(0, 3, 0),
            i % 2 == 0 ? OriginalOutlineVertexMode.Inward : OriginalOutlineVertexMode.Expand, .2f + i, 0)).ToArray();
    private static byte[] Bytes(OriginalOutlineData value) => MemoryMarshal.AsBytes(value.Vertices.ToArray().AsSpan()).ToArray();
    private static byte[] Bytes(SceneSnapshot value) => MemoryMarshal.AsBytes(value.Vertices).ToArray();
    private static CharacterSample CurrentPose(CharacterAnimationController controller) => (CharacterSample)typeof(CharacterAnimationController)
        .GetField("sampledPose", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
}
