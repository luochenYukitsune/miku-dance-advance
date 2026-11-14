using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

/// <summary>CPU transport only: seeds are explicit rest-derived directions, never posed UVs or normals.</summary>
public sealed class RawRadialSeedTransportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly Vector3[] Seeds = [new(.3f, -.7f, .2f), new(-.8f, .4f, -.1f), Vector3.Zero];

    [Fact]
    public void CombineUsesVertexOffsetsAndWorldDirectionOnceAcrossDuplicateAndAbsentSources()
    {
        var local = Frame(new(Seeds));
        var firstWorld = Matrix4x4.CreateScale(-2, 3, .5f) * Matrix4x4.CreateRotationZ(.43f)
            * Matrix4x4.CreateTranslation(70, -30, 90);
        var lastWorld = Matrix4x4.CreateScale(4, .25f, 2) * Matrix4x4.CreateRotationY(-.61f)
            * Matrix4x4.CreateTranslation(-21, 41, 18);
        var first = Place(local, firstWorld); var absent = Frame(); var last = Place(local, lastWorld);
        var snapshot = Combine(first, absent, last).Surface.Snapshot;
        Assert.Equal(Matrix4x4.Identity, snapshot.World);
        Assert.Equal(9, snapshot.RawRadialSeeds!.Vertices.Length);
        for (int i = 0; i < Seeds.Length; i++)
        {
            Near(Direction(Seeds[i], firstWorld), snapshot.RawRadialSeeds.Vertices[i]);
            Assert.Equal(Vector3.Zero, snapshot.RawRadialSeeds.Vertices[3 + i]);
            Near(Direction(Seeds[i], lastWorld), snapshot.RawRadialSeeds.Vertices[6 + i]);
        }
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, snapshot.Indices.ToArray());
        Assert.Equal(new[] { 0, 3, 6 }, snapshot.Materials.Select(material => material.StartIndex));
        Assert.Equal(Seeds, local.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
        Assert.True(snapshot.RawRadialSeeds.Vertices[0].Length() > 1); // Neither normalize nor inverse transpose.
    }

    [Fact]
    public void FastUpdateUsesCurrentLocalSeedsAndWorldWithoutAccumulatingBaselineTransform()
    {
        var original = Place(Frame(new(Seeds)), Matrix4x4.CreateRotationZ(.4f));
        var baseline = Combine(original);
        Vector3[] changedSeeds = [new(.9f, .6f, -.2f), new(.5f, -.3f, .8f), Vector3.Zero];
        var current = Place(Frame(new(changedSeeds)), Matrix4x4.CreateScale(-3, .75f, 2)
            * Matrix4x4.CreateRotationY(.37f) * Matrix4x4.CreateTranslation(400, 500, 600));
        var expected = Combine(current).Surface.Snapshot;
        var first = ProjectSceneAssembler.WithVertices(baseline, [current]);
        var second = ProjectSceneAssembler.WithVertices(first, [current]);
        Assert.Equal(expected.RawRadialSeeds!.Vertices.ToArray(), first.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
        Assert.Equal(expected.RawRadialSeeds.Vertices.ToArray(), second.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
        for (int i = 0; i < changedSeeds.Length; i++) Near(Direction(changedSeeds[i], current.Surface.Snapshot.World),
            second.Surface.Snapshot.RawRadialSeeds!.Vertices[i]);
        Assert.NotEqual(baseline.Surface.Snapshot.RawRadialSeeds!.Vertices[0], second.Surface.Snapshot.RawRadialSeeds!.Vertices[0]);
        Assert.Equal(Seeds, original.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
    }

    [Fact]
    public void AbsentPresentAbsentUpdatesRemoveThePreviouslyComposedStream()
    {
        var ordinary = Frame(); var bound = Frame(new(Seeds));
        var baseline = Combine(ordinary, ordinary);
        Assert.Null(baseline.Surface.Snapshot.RawRadialSeeds);
        Assert.Null(ProjectSceneAssembler.WithVertices(baseline, [ordinary, ordinary]).Surface.Snapshot.RawRadialSeeds);
        var present = ProjectSceneAssembler.WithVertices(baseline, [ordinary, bound]);
        Assert.Equal(new[] { Vector3.Zero, Vector3.Zero, Vector3.Zero }.Concat(Seeds),
            present.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
        var absent = ProjectSceneAssembler.WithVertices(present, [ordinary, ordinary]);
        Assert.Null(absent.Surface.Snapshot.RawRadialSeeds);
        Assert.NotNull(present.Surface.Snapshot.RawRadialSeeds); // Prior published frame stays immutable.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameCountChangedIndicesOrMaterialRangesRequireBaselineRebuild(bool changeRanges)
    {
        var original = Frame(new(Seeds), twoMaterials: true);
        var changed = Frame(new(Seeds), twoMaterials: true, reverseIndices: !changeRanges, moveMaterialRange: changeRanges);
        var baseline = Combine(original);
        Assert.Throws<ArgumentException>(() => ProjectSceneAssembler.WithVertices(baseline, [changed]));
        Assert.Equal(Seeds, baseline.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
    }

    [Fact]
    public void CompositionPreservesOutstandingSourceWorldAndBakesPlacementOnlyAtCombine()
    {
        var local = Frame(new(Seeds));
        var sourceWorld = Matrix4x4.CreateScale(-2, 3, .5f) * Matrix4x4.CreateRotationX(.24f)
            * Matrix4x4.CreateTranslation(4, 5, 6);
        var source = Place(local, sourceWorld); var editor = new SceneCompositionEditor();
        var item = editor.Add("bound", "bound.pmx", ModelRole.Character, source);
        Assert.Same(source.Surface.Snapshot.RawRadialSeeds, editor.Frame.Surface.Snapshot.RawRadialSeeds);
        Assert.Equal(sourceWorld, editor.Frame.Surface.Snapshot.World); // Single identity placement shortcut.
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .51f);
        editor.SetTransform(item.Id, new(100, 200, 300), rotation, 1.5f);
        var placed = editor.Frame.Surface.Snapshot;
        var expectedWorld = sourceWorld * Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(100, 200, 300);
        for (int i = 0; i < Seeds.Length; i++) Near(Direction(Seeds[i], expectedWorld), placed.RawRadialSeeds!.Vertices[i]);
        Assert.Equal(Matrix4x4.Identity, placed.World);
        Assert.Equal(Seeds, source.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
        editor.Undo();
        Assert.Same(source.Surface.Snapshot.RawRadialSeeds, editor.Frame.Surface.Snapshot.RawRadialSeeds);
    }

    [Fact]
    public void AnimatedPlacementAndEqualSizedVisibilityReplacementKeepCurrentSourceSeeds()
    {
        var model = EditedMotionProjectTests.Model();
        var first = Frame(new(Seeds));
        Vector3[] otherSeeds = [new(-.2f, .1f, .6f), new(.7f, .9f, -.3f), new(.1f, -.5f, .2f)];
        var second = Frame(new(otherSeeds)); var layout = new SceneCompositionEditor();
        var a = layout.Add("a", "a.pmx", ModelRole.Character, first);
        var b = layout.Add("b", "b.pmx", ModelRole.Character, second);
        var placementRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f);
        layout.SetTransform(b.Id, new(100, 20, 30), placementRotation, 2);
        Matrix4x4 secondWorld = layout.Objects[1].World;
        using var session = new SceneAnimationSession(layout.Objects,
            [new(a.Id, model, Motion(true), PhysicsEnabled: false), new(b.Id, model, Motion(false), PhysicsEnabled: false)],
            _ => throw new InvalidOperationException("This fixture must not allocate a physics world."), cancellationToken: Token);
        session.Seek(2, Token); var old = session.Frame.Surface.Snapshot;
        Assert.Equal(Seeds, old.RawRadialSeeds!.Vertices.ToArray());
        Assert.NotEqual(first.Surface.Snapshot.Vertices[0].Position, old.Vertices[0].Position);
        session.Seek(3, Token); // Same identity: WithVertices path, despite changed posed geometry.
        Assert.Equal(Seeds, session.Frame.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
        session.Seek(5, Token); // Equal vertex/material counts, a hidden and b visible: Combine path.
        Assert.False(session.Actors[0].Visible); Assert.True(session.Actors[1].Visible);
        Assert.Equal(3, session.Frame.Surface.Snapshot.Vertices.Length);
        for (int i = 0; i < otherSeeds.Length; i++) Near(Direction(otherSeeds[i], secondWorld),
            session.Frame.Surface.Snapshot.RawRadialSeeds!.Vertices[i]);
        session.Advance(1.0 / 30, Token); // Posed placement reconstruction plus fast update.
        for (int i = 0; i < otherSeeds.Length; i++) Near(Direction(otherSeeds[i], secondWorld),
            session.Frame.Surface.Snapshot.RawRadialSeeds!.Vertices[i]);
        session.Seek(10, Token);
        Assert.True(session.Actors[0].Visible); Assert.False(session.Actors[1].Visible);
        Assert.Equal(Seeds, session.Frame.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
        Assert.Equal(Seeds, old.RawRadialSeeds.Vertices.ToArray());
    }

    [Fact]
    public void AccessoryKeepsLocalSeedsAcrossOpacityAndWorldAndRestoresAfterHiddenFrame()
    {
        var source = Place(Frame(new(Seeds)), Matrix4x4.CreateRotationZ(.23f));
        PmmDocument Document(bool visible) => new() { Accessories = [new() { Index = 7,
            Scale = .2f, Translation = new(3, 4, 5), Rotation = new(.11f, -.27f, .43f),
            OpacityAndVisibility = visible ? (byte)101 : (byte)0, ParentModelIndex = -1, ParentBoneIndex = -1 }] };
        var visible = PmmAccessoryScene.Sample(Document(true), 7, source, 0, saved: true);
        Assert.True(visible.Visible); Assert.Equal(.5f, visible.Opacity);
        Assert.Same(source.Surface.Snapshot.RawRadialSeeds, visible.Frame.Surface.Snapshot.RawRadialSeeds);
        Assert.Equal(.5f, visible.Frame.Surface.Snapshot.Materials[0].Diffuse.W);
        var baked = Combine(visible.Frame).Surface.Snapshot;
        for (int i = 0; i < Seeds.Length; i++) Near(Direction(Seeds[i], visible.Frame.Surface.Snapshot.World), baked.RawRadialSeeds!.Vertices[i]);
        var hidden = PmmAccessoryScene.Sample(Document(false), 7, source, 0, saved: true);
        Assert.False(hidden.Visible); Assert.True(hidden.Frame.Surface.Snapshot.IsEmpty);
        Assert.Null(hidden.Frame.Surface.Snapshot.RawRadialSeeds);
        var restored = PmmAccessoryScene.Sample(Document(true), 7, source, 0, saved: true);
        Assert.Same(source.Surface.Snapshot.RawRadialSeeds, restored.Frame.Surface.Snapshot.RawRadialSeeds);
        Assert.Equal(Seeds, source.Surface.Snapshot.RawRadialSeeds!.Vertices.ToArray());
        Assert.Equal(1, source.Surface.Snapshot.Materials[0].Diffuse.W);
    }

    private static VmdMotion Motion(bool first) => new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 20, Translation = new(4, 2, 1) }],
        IkKeyframes = [new() { Frame = 0, Visible = first }, new() { Frame = 5, Visible = !first }, new() { Frame = 10, Visible = first }]
    };

    private static OriginalFrameScene Frame(SceneRawRadialSeeds? seeds = null, bool twoMaterials = false,
        bool reverseIndices = false, bool moveMaterialRange = false)
    {
        var rest = EditedMotionProjectTests.Rest(EditedMotionProjectTests.Model());
        var source = rest.Surface.Snapshot;
        uint[] indices = twoMaterials ? [0, 1, 2, 2, 1, 0] : [0, 1, 2];
        if (reverseIndices) (indices[1], indices[2]) = (indices[2], indices[1]);
        SceneMaterial[] materials = twoMaterials
            ? [source.Materials[0] with { IndexCount = moveMaterialRange ? 0 : 3 },
               source.Materials[0] with { Name = "second", StartIndex = moveMaterialRange ? 0 : 3, IndexCount = moveMaterialRange ? 6 : 3 }]
            : source.Materials.ToArray();
        var snapshot = new SceneSnapshot(source.Vertices, indices, materials, source.Textures,
            source.World, source.Camera, source.LightDirection, source.AdditionalUvs, seeds);
        var outline = new OriginalOutlineData(rest.Outline!.Vertices,
            Enumerable.Repeat(rest.Outline.Materials[0], materials.Length));
        return new(new(snapshot, Enumerable.Repeat(rest.Surface.Materials[0], materials.Length), rest.Surface.Profile),
            outline, rest.OutlineOptions);
    }

    private static OriginalFrameScene Place(OriginalFrameScene input, Matrix4x4 world)
    {
        var source = input.Surface.Snapshot;
        var snapshot = new SceneSnapshot(source.Vertices, source.Indices, source.Materials, source.Textures,
            world, source.Camera, source.LightDirection, source.AdditionalUvs, source.RawRadialSeeds);
        return new(new(snapshot, input.Surface.Materials, input.Surface.Profile), input.Outline, input.OutlineOptions,
            input.Options, input.Materials);
    }

    private static OriginalFrameScene Combine(params OriginalFrameScene[] frames) =>
        ProjectSceneAssembler.Combine(frames, Vector3.One, Vector3.UnitY);

    // Independent row-vector direction oracle: no TransformNormal helper, translation, normalization or inverse.
    private static Vector3 Direction(Vector3 value, Matrix4x4 matrix) => new(
        value.X * matrix.M11 + value.Y * matrix.M21 + value.Z * matrix.M31,
        value.X * matrix.M12 + value.Y * matrix.M22 + value.Z * matrix.M32,
        value.X * matrix.M13 + value.Y * matrix.M23 + value.Z * matrix.M33);

    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 2e-6f);
}
