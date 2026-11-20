using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Avalonia.Headless;
using Avalonia.Threading;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

/// <summary>Real UI dispatcher/worker scheduling over two tiny PMM actors. No window, GPU,
/// native solver, wall-clock performance threshold or user asset is needed for these contracts.</summary>
[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPosePreviewPmmBaselineTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task OtherActorsSavedBonesAndMorphsPrewarmTheSelectedActorAndSurviveContinuousInputs() => await session.Dispatch(async () =>
    {
        await using var f = new Fixture();
        f.Restore(selectedBone: null, otherBone: 3, otherMorph: .5f);
        Assert.DoesNotContain(f.Editor.SnapshotDrafts(), draft => draft.ObjectId == f.First);
        var stable = f.Editor.SnapshotDrafts().ToArray();
        var sourceFrame = f.Source.Frame;
        f.Request(interactive: false, prewarm: true);
        await f.WaitPublishedAfter(0);
        await f.WaitIdle();
        var baseline = Field(f.Preview, "interactiveBaseline");
        var owner = Field(f.Preview, "warmOwner");
        Assert.NotNull(baseline); Assert.NotNull(owner);
        f.AssertOtherPose(f.Snapshot(), 3, .5f);

        for (int x = 2; x <= 5; x++)
        {
            int publications = f.Publications;
            f.Editor.PositionX = x;
            f.Request(interactive: true);
            await f.WaitPublishedAfter(publications);
            Assert.Same(baseline, Field(f.Preview, "interactiveBaseline"));
            Assert.Same(owner, Field(f.Preview, "warmOwner"));
            var snapshot = f.Snapshot();
            Assert.Equal(new Vector3(x, 0, 0), snapshot.SceneInputs[f.First].BoneWorldMatrices[0].Translation);
            f.AssertOtherPose(snapshot, 3, .5f);
            Assert.Equal(stable, f.Editor.SnapshotDrafts().Where(d => d.ObjectId == f.Second));
        }
        Assert.Same(sourceFrame, f.Source.Frame);
        Assert.Equal(7, f.Source.CurrentFrame); Assert.False(f.Source.CanUndo); Assert.False(f.Source.CanRedo);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ChangedSavedMorphOrOtherActorDraftRetiresTheOldBaselineBeforePublishing() => await session.Dispatch(async () =>
    {
        await using var f = new Fixture();
        f.Restore(selectedBone: 2, otherBone: 3, otherMorph: .5f);
        f.Request(interactive: true);
        await f.WaitPublishedAfter(0);
        var originalBaseline = Field(f.Preview, "interactiveBaseline");
        var originalOwner = Field(f.Preview, "warmOwner");
        f.AssertOtherPose(f.Snapshot(), 3, .5f);

        f.Restore(selectedBone: 4, otherBone: 7, otherMorph: .75f);
        int publications = f.Publications;
        Assert.Null(f.Request(interactive: true)); // A stale warm result cannot flash while the new fixed inputs evaluate.
        Assert.NotSame(originalBaseline, Field(f.Preview, "interactiveBaseline"));
        await f.WaitPublishedAfter(publications);
        Assert.NotSame(originalOwner, Field(f.Preview, "warmOwner"));
        f.AssertOtherPose(f.Snapshot(), 7, .75f);
        Assert.Equal(new Vector3(4, 0, 0), f.Snapshot().SceneInputs[f.First].BoneWorldMatrices[0].Translation);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RemovingOneSelectedActorBoneDraftRestoresItsAuthoredPoseAndReplacesOnlyTheOldBaseline() => await session.Dispatch(async () =>
    {
        await using var f = new Fixture(secondBone: true);
        var sourceFrame = f.Source.Frame;
        var authoredSecondBone = f.Source.CaptureRenderSnapshot(f.Requests).SceneInputs[f.First].BoneWorldMatrices[1];
        f.Restore(selectedBone: 2, otherBone: 3, otherMorph: .5f, selectedSecondBone: 9);
        f.Request(interactive: false, prewarm: true);
        await f.WaitPublishedAfter(0);
        await f.WaitIdle();
        var originalBaseline = Field(f.Preview, "interactiveBaseline");
        var originalOwner = Field(f.Preview, "warmOwner");
        Assert.NotNull(originalBaseline); Assert.NotNull(originalOwner);
        var draftedSecondBone = f.Snapshot().SceneInputs[f.First].BoneWorldMatrices[1];
        Assert.NotEqual(authoredSecondBone, draftedSecondBone);

        int publications = f.Publications;
        f.Editor.PositionX = 6;
        f.Request(interactive: true);
        await f.WaitPublishedAfter(publications);
        Assert.Same(originalBaseline, Field(f.Preview, "interactiveBaseline"));
        Assert.Same(originalOwner, Field(f.Preview, "warmOwner"));
        Assert.Equal(draftedSecondBone, f.Snapshot().SceneInputs[f.First].BoneWorldMatrices[1]);

        // Keep A and every other actor/morph unchanged; remove only B. Its old draft
        // must not leak back from the warm owner's motion when B has no raw override.
        f.Restore(selectedBone: 6, otherBone: 3, otherMorph: .5f);
        Assert.Contains(f.Editor.SnapshotDrafts(), draft => draft.ObjectId == f.First && draft.BoneIndex == 0);
        Assert.DoesNotContain(f.Editor.SnapshotDrafts(), draft => draft.ObjectId == f.First && draft.BoneIndex == 1);
        publications = f.Publications;
        Assert.Null(f.Request(interactive: true));
        Assert.NotSame(originalBaseline, Field(f.Preview, "interactiveBaseline"));
        await f.WaitPublishedAfter(publications);
        var replacementBaseline = Field(f.Preview, "interactiveBaseline");
        var replacementOwner = Field(f.Preview, "warmOwner");
        Assert.NotSame(originalOwner, replacementOwner);
        Assert.Equal(authoredSecondBone, f.Snapshot().SceneInputs[f.First].BoneWorldMatrices[1]);
        Assert.Equal(new Vector3(6, 0, 0), f.Snapshot().SceneInputs[f.First].BoneWorldMatrices[0].Translation);
        f.AssertOtherPose(f.Snapshot(), 3, .5f);

        publications = f.Publications;
        f.Editor.PositionX = 8;
        f.Request(interactive: true);
        await f.WaitPublishedAfter(publications);
        Assert.Same(replacementBaseline, Field(f.Preview, "interactiveBaseline"));
        Assert.Same(replacementOwner, Field(f.Preview, "warmOwner"));
        Assert.Equal(authoredSecondBone, f.Snapshot().SceneInputs[f.First].BoneWorldMatrices[1]);
        Assert.Equal(new Vector3(8, 0, 0), f.Snapshot().SceneInputs[f.First].BoneWorldMatrices[0].Translation);
        Assert.Same(sourceFrame, f.Source.Frame);
        Assert.False(f.Source.CanUndo); Assert.False(f.Source.CanRedo);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ReleasePublishesLatestInputAndQueuedInteractiveCallbacksCannotReplaceIt() => await session.Dispatch(async () =>
    {
        await using var f = new Fixture();
        f.Restore(selectedBone: 1, otherBone: 3, otherMorph: .5f);
        // Do not yield: old worker completions can queue UI publications while the latest input
        // and release close their epoch. The latest released input must win every such callback.
        for (int x = 2; x <= 16; x++) { f.Editor.PositionX = x; f.Request(interactive: true); }
        int publications = f.Publications;
        f.Request(interactive: false);
        await f.WaitPublishedAfter(publications);
        await f.WaitSettled();
        var complete = f.Snapshot();
        await f.WaitIdle();
        // The worker posts its final snapshot at Background priority before retiring.
        // Its exit alone does not guarantee that the UI has applied that snapshot.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal("ReleasedBones", Field(f.Preview, "asyncKind")!.ToString());
        Assert.Equal(EditorPosePreviewState.SettledBones, f.Preview.State);
        Assert.True(f.Preview.HasLatestInputSnapshot); Assert.Equal(0, f.Preview.RemainingSettleSteps);
        var draftMotions = f.Editor.PrepareDraftMotions(f.Actors).ToDictionary(plan => plan.ObjectId, plan => plan.CreateMotion(confirmOverwrite: true));
        using var reference = f.Source.CreatePosePreview(draftMotions, TestContext.Current.CancellationToken);
        var expected = reference.CaptureRenderSnapshot(f.Requests);
        Assert.Equal(expected.Scene.Surface.Snapshot.Vertices.ToArray(), complete.Scene.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(expected.SceneInputs[f.First].BoneWorldMatrices[0], complete.SceneInputs[f.First].BoneWorldMatrices[0]);
        f.AssertOtherPose(complete, 3, .5f);
        Assert.Same(complete, f.Snapshot());
        Assert.Equal("ReleasedBones", Field(f.Preview, "asyncKind")!.ToString());
        Assert.False(f.Source.CanUndo); Assert.False(f.Source.CanRedo);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ClearingAllDraftsReturnsToAuthoredSourceAndSecondGestureOwnsAFreshBaseline() => await session.Dispatch(async () =>
    {
        await using var f = new Fixture();
        f.Restore(selectedBone: 8, otherBone: 3, otherMorph: .5f);
        f.Request(interactive: true);
        await f.WaitPublishedAfter(0);
        var previous = Field(f.Preview, "interactiveBaseline");
        f.Editor.InvalidateDrafts();
        Assert.Empty(f.Editor.SnapshotDrafts());
        Assert.Null(f.Request(interactive: false));
        await f.WaitIdle();
        Assert.Null(Field(f.Preview, "asyncSnapshot"));
        Assert.Null(Field(f.Preview, "warmOwner"));
        Assert.Null(Field(f.Preview, "interactiveBaseline"));
        Assert.False(f.Preview.IsPreparingPreview);

        int publications = f.Publications;
        f.Editor.PositionX = 2;
        f.Request(interactive: true);
        await f.WaitPublishedAfter(publications);
        Assert.NotSame(previous, Field(f.Preview, "interactiveBaseline"));
        var snapshot = f.Snapshot();
        Assert.Equal(new Vector3(2, 0, 0), snapshot.SceneInputs[f.First].BoneWorldMatrices[0].Translation);
        f.AssertOtherPose(snapshot, 0, 0);
        Assert.Equal(7, snapshot.SourceFrame);
        Assert.Equal(7, f.Source.CurrentFrame); Assert.False(f.Source.CanUndo);
        return true;
    }, TestContext.Current.CancellationToken);

    private static object? Field(object owner, string name) => owner.GetType().GetField(name, Instance)!.GetValue(owner);

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly SceneProjectSession Source;
        internal readonly EditorAnimationViewModel Editor = new();
        internal readonly EditorPosePreview Preview = new();
        internal readonly Guid First, Second;
        internal readonly SceneActorMotion[] Actors;
        internal readonly SceneObjectRenderRequest[] Requests;
        internal int Publications;
        private Exception? failure;

        internal Fixture(bool secondBone = false)
        {
            var model = CreateModel(secondBone);
            var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
                PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1),
                materials: PmxOriginalFrameAdapter.FromMaterials(model));
            var layout = new SceneCompositionEditor();
            First = layout.Add("selected", Path.GetFullPath("pmm-preview-selected.pmx"), ModelRole.Character, rest).Id;
            Second = layout.Add("saved", Path.GetFullPath("pmm-preview-saved.pmx"), ModelRole.Character, rest).Id;
            VmdMotion Motion() => new() { BoneKeyframes = model.Bones.SelectMany((bone, index) => new[]
            {
                new VmdBoneKeyframe { BoneName = bone.Name, Translation = new(index, 0, 0) },
                new VmdBoneKeyframe { BoneName = bone.Name, Frame = 30, Translation = new(index, 0, 0) }
            }).ToArray() };
            var actors = new[] { new SceneActorMotion(First, model, Motion(), PhysicsEnabled: false), new SceneActorMotion(Second, model, Motion(), PhysicsEnabled: false) };
            Source = new(layout, new(), actors, new NoPhysics(), initialFrame: 7,
                pmmControls: Controls(First, Second, model), cancellationToken: TestContext.Current.CancellationToken);
            Actors = Source.CaptureAuthoredContent().Motions.ToArray();
            Requests = [new(First, Enumerable.Range(0, model.Bones.Count)), new(Second, [0])];
            Editor.SetActor(First, Actors[0].Model, Actors[0].Motion);
            Editor.CurrentFrame = 7; Editor.SelectBone(0);
        }
        internal void Restore(float? selectedBone, float otherBone, float otherMorph, float? selectedSecondBone = null)
        {
            var poses = new Dictionary<Guid, VmdMotion> { [Second] = new()
            {
                BoneKeyframes = [new() { BoneName = "root", Translation = new(otherBone, 0, 0) }],
                MorphKeyframes = [new() { MorphName = "smile", Weight = otherMorph }]
            } };
            var selected = new List<VmdBoneKeyframe>();
            if (selectedBone.HasValue) selected.Add(new() { BoneName = "root", Translation = new(selectedBone.Value, 0, 0) });
            if (selectedSecondBone.HasValue) selected.Add(new() { BoneName = "independent", Translation = new(selectedSecondBone.Value, 0, 0) });
            if (selected.Count != 0) poses.Add(First, new() { BoneKeyframes = selected });
            Editor.RestoreSavedPoses(Actors, poses, 7);
        }

        private static PmxModel CreateModel(bool secondBone)
        {
            var source = MorphApplicationTests.Model([MorphApplicationTests.Position("smile", new(0, 2, 0))]);
            if (!secondBone) return source;
            return new()
            {
                Header = source.Header, Name = source.Name, EnglishName = source.EnglishName,
                Comment = source.Comment, EnglishComment = source.EnglishComment,
                Vertices = source.Vertices, Faces = source.Faces, Textures = source.Textures, Materials = source.Materials,
                Bones = [source.Bones[0], new() { Name = "independent", EnglishName = "", Position = new(0, 4, 0),
                    ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate }],
                Morphs = source.Morphs, DisplayFrames = source.DisplayFrames, RigidBodies = source.RigidBodies,
                Joints = source.Joints, SoftBodies = source.SoftBodies
            };
        }
        internal SceneProjectRenderSnapshot? Request(bool interactive, bool prewarm = false)
            => Preview.TryGetOrRequest(Source, Editor, Requests, () => Publications++, error => failure = error,
                interactive: interactive, prewarm: prewarm);
        internal SceneProjectRenderSnapshot Snapshot() => Assert.IsType<SceneProjectRenderSnapshot>(Field(Preview, "asyncSnapshot"));
        internal async Task WaitPublishedAfter(int previous)
            => await Wait(() => Publications > previous && !Preview.IsPreparingPreview);
        internal async Task WaitIdle() => await Wait(() => !(bool)Field(Preview, "workerRunning")!);
        internal Task WaitSettled() => Wait(() => Preview.State == EditorPosePreviewState.SettledBones);
        private async Task Wait(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (failure is not null) throw new InvalidOperationException("Small PMM preview failed", failure);
                if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Small PMM preview did not publish or retire its worker.");
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
            if (failure is not null) throw new InvalidOperationException("Small PMM preview failed", failure);
        }
        internal void AssertOtherPose(SceneProjectRenderSnapshot snapshot, float bone, float morph)
        {
            Assert.Equal(new Vector3(bone, 0, 0), snapshot.SceneInputs[Second].BoneWorldMatrices[0].Translation);
            Assert.Equal(new Vector3(1 + bone, 2 * morph, 0), snapshot.Scene.Surface.Snapshot.Vertices[3].Position);
            Assert.Equal(7, snapshot.SourceFrame);
        }
        public async ValueTask DisposeAsync()
        {
            Preview.Dispose();
            await WaitIdle();
            Source.Dispose();
        }
    }

    private static ScenePmmControlState Controls(Guid first, Guid second, PmxModel model)
    {
        PmmString S(string value) => new(0, value, []);
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(value => value).ToArray();
        var document = new PmmDocument
        {
            AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 30,
            Models = Enumerable.Range(0, 2).Select(i => new PmmModel
            {
                Index = (byte)i, Name = S("actor" + i), Path = S("actor" + i + ".pmx"), BoneNames = model.Bones.Select(bone => S(bone.Name)).ToArray(), MorphNames = [S("smile")],
                DrawOrderIndex = (byte)i, SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 30,
                InitialBoneKeyframes = model.Bones.Select((_, index) => new PmmBoneKeyframe
                    { ObjectIndex = index, Rotation = Quaternion.Identity, Interpolation = linear }).ToArray(),
                InitialMorphKeyframes = [new() { ObjectIndex = 0 }], InitialModelKeyframe = new() { Visible = true },
                BoneStates = model.Bones.Select(_ => new PmmBoneState(0, Vector3.Zero, Quaternion.Identity, false, false, 0)).ToArray(), MorphStates = [new(0, 0)]
            }).ToArray(),
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PhysicsSimulationMode = 0, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
            Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } }
        };
        return ScenePmmControlState.Create(document, [new(first, 0, model), new(second, 1, model)]);
    }
    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
            => throw new InvalidOperationException("The baseline scheduling fixture has no rigid bodies.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
            => throw new InvalidOperationException("The baseline scheduling fixture has no rigid bodies.");
    }
    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
