using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmDrawPlanTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "pmm-draw-plan-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public PmmDrawPlanTests() => Directory.CreateDirectory(folder);

    [Theory]
    [InlineData(0, "M7,M42,A2,A33,A90")]
    [InlineData(1, "A2,M7,M42,A33,A90")]
    [InlineData(2, "A2,A33,M7,M42,A90")]
    public void NonnegativeCutInsertsWholeModelBlockBeforeZeroBasedAccessoryOrder(int cut, string expected)
    {
        using var project = Project(cut);
        var accessories = PmmAccessoryPlayback.Prepare(project, cancellationToken: Token);
        var plan = accessories.SampleDrawPlan();
        Assert.Equal(expected, Ids(plan));
        Assert.Equal(Enumerable.Range(0, 5), plan.Items.Select(item => item.DrawOrderIndex));
        Assert.Equal(new[] { 0, 1 }, plan.Items.Where(item => item.Kind == PmmDrawableKind.Model).Select(item => (int)item.SourceDrawOrderIndex));
        Assert.True(Assert.Single(plan.Items, item => item.StoredIndex == 33).AddBlendEnabled);
        Assert.All(plan.Items, item => Assert.True(item.Visible));
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public void MinusOnePreservesRawGlobalOrdersWithoutGuessingAllAccessoriesBeforeOrAfter()
    {
        using var project = Project(-1, globalOrders: true);
        var plan = PmmAccessoryPlayback.Prepare(project, cancellationToken: Token).SampleDrawPlan();
        Assert.Equal("A2,M7,A33,M42,A90", Ids(plan));
        Assert.Equal(Enumerable.Range(0, 5), plan.Items.Select(item => item.DrawOrderIndex));
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public void DuplicateEffectiveOrderHasDocumentStableFallbackAndExplicitDiagnostic()
    {
        using var project = Project(-1);
        var plan = PmmAccessoryPlayback.Prepare(project, cancellationToken: Token).SampleDrawPlan();
        Assert.Equal("A2,M7,A33,M42,A90", Ids(plan));
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Contains("same draw order", StringComparison.OrdinalIgnoreCase)
            && diagnostic.Contains("original", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HiddenEntriesRemainOrderedAndSavedVisibilityChangesAtTheSameTimelineClock()
    {
        using var project = Project(1, hideSaved: true);
        var playback = PmmAccessoryPlayback.Prepare(project, cancellationToken: Token);
        var saved = playback.SampleDrawPlan();
        Assert.Equal("A2,M7,M42,A33,A90", Ids(saved));
        Assert.False(Assert.Single(saved.Items, item => item.Kind == PmmDrawableKind.Model && item.StoredIndex == 42).Visible);
        Assert.False(Assert.Single(saved.Items, item => item.Kind == PmmDrawableKind.Accessory && item.StoredIndex == 2).Visible);
        project.Seek(0);
        var zero = playback.SampleDrawPlan();
        Assert.All(zero.Items, item => Assert.True(item.Visible));
        var modelAtZero = Assert.Single(zero.Items, item => item.Kind == PmmDrawableKind.Model && item.StoredIndex == 7);
        project.Seek(10);
        var animated = playback.SampleDrawPlan();
        Assert.False(Assert.Single(animated.Items, item => item.Kind == PmmDrawableKind.Model && item.StoredIndex == 7).Visible);
        var additive = Assert.Single(animated.Items, item => item.Kind == PmmDrawableKind.Accessory && item.StoredIndex == 33);
        Assert.False(additive.Visible);
        Assert.True(additive.AddBlendEnabled);
        Assert.Equal(5, animated.Items.Count);
        Assert.All(zero.Items, item => Assert.True(item.Visible));
        Assert.NotSame(modelAtZero.Frame, Assert.Single(animated.Items, item => item.Kind == PmmDrawableKind.Model && item.StoredIndex == 7).Frame);
        Assert.Equal(0, modelAtZero.Frame.Surface.Snapshot.Vertices[0].Position.X);
    }

    [Fact]
    public void PlanRequiresCompleteUniqueKnownAccessoryIdsAndPreservesCurrentFrameOnFailure()
    {
        using var project = Project(1);
        var playback = PmmAccessoryPlayback.Prepare(project, cancellationToken: Token);
        var frames = playback.Sample();
        var before = project.Frame;
        Assert.Throws<ArgumentException>(() => project.SampleDrawPlan());
        Assert.Throws<ArgumentException>(() => project.SampleDrawPlan(frames.Take(2).ToArray()));
        Assert.Throws<ArgumentException>(() => project.SampleDrawPlan([frames[0], frames[0], frames[2]]));
        Assert.Throws<ArgumentException>(() => project.SampleDrawPlan([frames[0] with { Index = 255 }, frames[1], frames[2]]));
        Assert.Throws<ArgumentException>(() => project.SampleDrawPlan(frames.Select(f => f.Index == 33 ? f with { AddBlendEnabled = false } : f).ToArray()));
        Assert.Same(before, project.Frame);
        Assert.Equal(5, playback.SampleDrawPlan().Items.Count);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void UnsupportedRawCutRejectsWithoutReinterpretingIt(int cut)
    {
        using var project = Project(cut);
        var accessories = PmmAccessoryPlayback.Prepare(project, cancellationToken: Token);
        Assert.Throws<NotSupportedException>(() => accessories.SampleDrawPlan());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void ModelsOnlyPlanCanOmitAccessorySamplesAndDisposedOwnerRejects(int cut)
    {
        var project = Project(cut, noAccessories: true);
        Assert.Equal("M7,M42", Ids(project.SampleDrawPlan()));
        project.Dispose();
        Assert.Throws<ObjectDisposedException>(() => project.SampleDrawPlan());
    }

    [Fact]
    public void NeutralPlanPreservesAdditiveWhileManualNormalBlendOverrideStillRejectsIt()
    {
        using var project = Project(1);
        var accessories = PmmAccessoryPlayback.Prepare(project, cancellationToken: Token);
        var additive = Assert.Single(accessories.SampleDrawPlan().Items, item => item.AddBlendEnabled);
        Assert.Equal(PmmDrawableKind.Accessory, additive.Kind);
        Assert.Throws<NotSupportedException>(() => accessories.CombineWithProject(accessoriesAfterModels: true));
        Assert.Throws<NotSupportedException>(() => accessories.CombineWithProject(accessoriesAfterModels: false));
    }

    private static string Ids(PmmDrawPlan plan) => string.Join(',', plan.Items.Select(item => (item.Kind == PmmDrawableKind.Model ? "M" : "A") + item.StoredIndex));

    private PmmProjectPlayback Project(int cut, bool globalOrders = false, bool hideSaved = false, bool noAccessories = false)
    {
        var pmd = new PmdModel("model", [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero, 0, 0, 1),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero, 0, 0, 1), new(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero, 0, 0, 1)],
            [0, 1, 2], [new(Vector4.One, Vector3.Zero, Vector3.Zero, 1, "", 0, 3)], [new("bone", -1, Vector3.Zero)], 0);
        string modelPath = Path.Combine(folder, "actor.pmd"); File.WriteAllBytes(modelPath, PmdWriter.ToBytes(pmd));
        string accessoryPath = Path.Combine(folder, "part.x"); File.WriteAllText(accessoryPath, XAccessoryTests.Triangle);
        byte[] curves = Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray();
        PmmModel Actor(byte id, byte order) => new()
        {
            Index = id, DrawOrderIndex = order, TransformOrderIndex = id, Path = new(0, modelPath, []),
            Visible = !hideSaved || id != 42, LastFrame = 20, BoneNames = [new(0, "bone", [])],
            BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)],
            InitialBoneKeyframes = [new() { NextIndex = 1, Rotation = Quaternion.Identity, Interpolation = curves }],
            BoneKeyframes = [new() { ObjectIndex = 1, RecordIndex = 1, Frame = 20, Translation = Vector3.UnitX, Rotation = Quaternion.Identity, Interpolation = curves }],
            InitialModelKeyframe = new() { Visible = true, NextIndex = id == 7 ? 1 : 0 },
            ModelKeyframes = id == 7 ? [new() { ObjectIndex = 1, RecordIndex = 1, Frame = 10, Visible = false }] : []
        };
        PmmAccessory Accessory(byte id, byte order) => new()
        {
            Index = id, DrawOrderIndex = order, Name = new(0, "part" + id, []), Path = new(0, accessoryPath, []),
            Scale = 1, ParentModelIndex = -1, ParentBoneIndex = -1, OpacityAndVisibility = (byte)(hideSaved && id == 2 ? 0 : 1),
            AddBlendEnabled = id == 33,
            InitialKeyframe = new() { Scale = 1, OpacityAndVisibility = 1, ParentModelIndex = -1, ParentBoneIndex = -1 },
            Keyframes = id == 33 ? [new() { ObjectIndex = 1, RecordIndex = 1, Frame = 10, Scale = 1, OpacityAndVisibility = 0, ParentModelIndex = -1, ParentBoneIndex = -1 }] : []
        };
        var document = new PmmDocument
        {
            Models = [Actor(42, (byte)(globalOrders ? 3 : 1)), Actor(7, (byte)(globalOrders ? 1 : 0))],
            Accessories = noAccessories ? [] : [Accessory(90, (byte)(globalOrders ? 4 : 2)), Accessory(2, 0), Accessory(33, (byte)(globalOrders ? 2 : 1))],
            OutputWidth = 1280, OutputHeight = 720, CameraFov = 30,
            Settings = new() { PhysicsSimulationMode = 0, CameraLookAtModelIndex = -1, AccessoryIndexAfterModels = cut },
            Camera = new() { Position = new(0, 0, -10), Perspective = true, InitialKeyframe = new() { Distance = -10, Fov = 30, Perspective = true, ParentModelIndex = -1,
                Interpolation = Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray() } },
            Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY, InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } }
        };
        var plan = PmmProjectImport.Prepare(document, Path.Combine(folder, "scene.pmm"), cancellationToken: Token);
        Assert.True(plan.CanCommit, string.Join("; ", plan.UnresolvedAssets.Select(a => a.Error)));
        var model = PmdPmxAdapter.Convert(pmd);
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
        return new(plan, new Dictionary<int, OriginalFrameScene> { [42] = rest, [7] = rest }, (_, _) => throw new InvalidOperationException("No physics in draw-plan fixture."));
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
