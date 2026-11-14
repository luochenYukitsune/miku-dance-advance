using System.Numerics;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneProjectAccessoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void FractionalSnapshotUsesHiddenParentsSolvedNativeNameMappedBoneAndRetainsRawInputs()
    {
        var f = Fixture(); using var project = Create(f);
        Assert.Equal(40u, project.DurationFrames);
        project.AdvancePreview(.25, Token);
        var snapshot = project.CaptureRenderSnapshot(); var item = Assert.Single(snapshot.Accessories);
        Assert.Equal(7.5, snapshot.SourceFrame); Assert.Equal(snapshot.SourceFrame, item.Inputs.SourceFrame);
        Assert.False(item.Inputs.IsSavedPose); Assert.False(Assert.Single(snapshot.RenderingStyles).Visible);
        Assert.Equal(f.Accessory.ObjectId, item.ObjectId); Assert.Equal(7, item.Asset.Index);
        var parent = Assert.IsType<ScenePmmAccessoryParent>(item.Parent);
        Assert.Equal(f.ActorId, parent.ObjectId); Assert.Equal(1, parent.BoneIndex); Assert.Equal("root", parent.BoneName);
        Assert.Equal(42, parent.PmmModelIndex); Assert.Equal(0, parent.PmmBoneIndex);
        using var oracle = new CharacterAnimationController(f.Model, f.Motion.Motion,
            f.Layout.Objects[0].Source.Surface, f.Layout.Objects[0].Source.Outline!, allowEmptyMotion: true);
        oracle.SampleAt(snapshot.SourceFrame);
        Assert.Equal(oracle.BoneWorldMatrices[parent.BoneIndex] * f.Layout.Objects[0].World, item.Inputs.ParentWorld);
        Assert.InRange(item.Inputs.ParentWorld.Translation.X, 17.4999f, 17.5001f);
        Assert.Equal(item.Inputs.Source.Surface.Snapshot.World * PmmAccessoryScene.WorldTransform(item.Inputs.Pose, item.Inputs.ParentWorld), item.Inputs.World);
        Assert.Equal(item.Inputs.World, item.Inputs.SourceGeometry.World);
        Assert.Same(f.Accessory.Asset.Document, item.Inputs.Document);
        Assert.Equal("image.png*effect.spa", Assert.Single(item.Inputs.Document!.Materials).Material.Texture);
        Assert.Equal(.8f, Assert.Single(item.Inputs.SourceGeometry.Materials).Diffuse.W);
        Assert.Equal(.4f, Assert.Single(item.Sample.Frame.Surface.Snapshot.Materials).Diffuse.W);
        Assert.Equal(0, snapshot.AccessoryIndexAfterModels);
        Assert.Equal(new[] { f.ActorId, f.Accessory.ObjectId }, snapshot.MixedDrawOrder.Select(i => i.ObjectId));
        Assert.False(snapshot.MixedDrawOrder[0].Visible); Assert.True(snapshot.MixedDrawOrder[1].AddBlendEnabled);
        project.AdvancePreview(.25, Token);
        Assert.Equal(7.5, item.Inputs.SourceFrame); Assert.InRange(item.Inputs.ParentWorld.Translation.X, 17.4999f, 17.5001f);
    }

    [Fact]
    public void TransactionsUndoPreviewAndAuthoredCaptureRetainIndependentStableAccessoryBindings()
    {
        var f = Fixture(); using var project = Create(f); SceneProjectEdit? retained = null;
        var authored = project.CaptureAuthoredContent(); Assert.Equal(f.Accessory.ObjectId, Assert.Single(authored.Accessories).ObjectId);
        project.Edit(edit => { retained = edit; edit.Settings = edit.Settings with { Width = 800 }; }, Token);
        retained!.Accessories.Clear(); Assert.Single(project.Accessories);
        Assert.True(project.Undo(Token)); Assert.Equal(f.Accessory.ObjectId, Assert.Single(project.Accessories).ObjectId);
        Assert.True(project.Redo(Token)); Assert.Equal(f.Accessory.ObjectId, Assert.Single(project.Accessories).ObjectId);
        using var preview = project.CreatePosePreview(f.ActorId, f.Motion.Motion, Token);
        Assert.Equal(f.Accessory.ObjectId, Assert.Single(preview.CaptureRenderSnapshot().Accessories).ObjectId);
        Assert.Equal(f.Accessory.ObjectId, Assert.Single(authored.Accessories).ObjectId);
        Assert.Throws<NotSupportedException>(() => ((IList<SceneProjectAccessory>)project.Accessories).Clear());
        Assert.Throws<NotSupportedException>(() => PmaProjectStore.CaptureSnapshot(project, cancellationToken: Token));
        Assert.Throws<NotSupportedException>(() => PmcProjectStore.CaptureSnapshot(project, cancellationToken: Token));
        Assert.Throws<NotSupportedException>(() => project.Save("unused.mmdscene", cancellationToken: Token));
        Assert.Throws<NotSupportedException>(() => project.ExportPmm());
        Assert.Throws<NotSupportedException>(() => project.SavePmm("unused.pmm", cancellationToken: Token));
    }

    [Fact]
    public void FailedAssetCandidateRetainsFrameBindingsAndUndoHistory()
    {
        var f = Fixture(); using var project = Create(f); project.Advance(.1, Token);
        var before = project.CaptureRenderSnapshot();
        Assert.Throws<InvalidDataException>(() => project.Edit(edit => edit.Accessories[0] = f.Accessory with
            { Asset = f.Accessory.Asset with { Document = null } }, Token));
        Assert.Throws<InvalidDataException>(() => project.Edit(edit => edit.Accessories.Add(f.Accessory with { ObjectId = Guid.NewGuid() }), Token));
        Assert.Throws<InvalidDataException>(() => project.Edit(edit => edit.Accessories[0] = f.Accessory with
            { Asset = f.Accessory.Asset with { Index = 99 } }, Token));
        Assert.Same(before.Scene, project.Frame); Assert.Equal(before.SourceFrame, project.CurrentFrame);
        Assert.Equal(f.Accessory.ObjectId, Assert.Single(project.Accessories).ObjectId); Assert.False(project.CanUndo);
    }

    [Fact]
    public void MetadataOnlyOwnersExposeUnpreparedIdsAndFutureOrSavedInvalidParentsFailBeforePublication()
    {
        var f = Fixture(); using var project = Create(f, prepared: false);
        Assert.Equal(new byte[] { 7 }, project.UnpreparedAccessoryIndices);
        Assert.Equal(new byte[] { 7 }, project.CaptureRenderSnapshot().UnpreparedAccessoryIndices);
        Assert.Empty(project.CaptureRenderSnapshot().Accessories);
        foreach (bool saved in new[] { false, true })
        {
            var doc = Document(parentModel: saved ? 42 : 99, savedParentModel: saved ? 99 : 42);
            Assert.Throws<PmmFormatException>(() => ScenePmmControlState.Create(doc, [new(f.ActorId, 42, f.Model)]));
        }
    }

    [Fact]
    public void AnAccessoryParentWithoutANativeNameBindingFailsBeforeAnyOwnerCanPublish()
    {
        var source = Document();
        // Both stored ordinals are valid in PMM, but the requested original native name is
        // absent from the replacement skeleton. A passive unmatched track is not detachment.
        var replacement = PmmMotionAdapterTests.Pmx(["unused"]);
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(source, [new(Guid.NewGuid(), 42, replacement)]));
    }

    [Fact]
    public void HiddenAccessoryRetainsGeometryWorldAndMixedOrderIdentity()
    {
        var f = Fixture(visible: false); using var project = Create(f);
        var snapshot = project.CaptureRenderSnapshot(); var item = Assert.Single(snapshot.Accessories);
        Assert.False(item.Sample.Visible); Assert.Empty(item.Sample.Frame.Surface.Snapshot.Vertices.ToArray());
        Assert.NotEmpty(item.Inputs.SourceGeometry.Vertices.ToArray()); Assert.NotNull(item.Parent);
        Assert.False(snapshot.MixedDrawOrder.Single(i => i.Kind == PmmDrawableKind.Accessory).Visible);
        Assert.Throws<NotSupportedException>(() => ((IList<SceneProjectAccessoryFrame>)snapshot.Accessories).Clear());
    }

    [Fact]
    public void HiddenParentSidecarContainsEveryRequestedActualBoneWithoutChangingHiddenSceneInputs()
    {
        var f = Fixture(); using var project = Create(f); project.AdvancePreview(.25, Token);
        var snapshot = project.CaptureRenderSnapshot([new(f.ActorId, [0, 1])]);
        var sidecar = snapshot.AccessoryParentBoneWorldMatrices[f.ActorId];
        Assert.Equal(new[] { 0, 1 }, sidecar.Keys.Order());
        Assert.Empty(snapshot.SceneInputs[f.ActorId].BoneWorldMatrices);
        Assert.Equal(Assert.Single(snapshot.Accessories).Inputs.ParentWorld, sidecar[1]);
        Assert.InRange(sidecar[0].Translation.X, 9.9999f, 10.0001f);
        Assert.InRange(sidecar[1].Translation.X, 17.4999f, 17.5001f);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<int, Matrix4x4>)sidecar).Clear());
        project.AdvancePreview(.25, Token);
        Assert.InRange(sidecar[1].Translation.X, 17.4999f, 17.5001f);
        var minimal = project.CaptureRenderSnapshot().AccessoryParentBoneWorldMatrices[f.ActorId];
        Assert.Equal(1, Assert.Single(minimal).Key);
    }

    [Fact]
    public void NonidentitySourceWorldIsAppliedExactlyOnceAndGuidPoolsCannotOverlap()
    {
        var f = Fixture(); var original = f.Accessory.Asset.Source;
        var sourceWorld = Matrix4x4.CreateRotationX(.3f) * Matrix4x4.CreateTranslation(3, 2, 1);
        var source = new OriginalFrameScene(new(original.Surface.Snapshot.WithWorld(sourceWorld), original.Surface.Materials, original.Surface.Profile),
            original.Outline, original.OutlineOptions, original.Options, original.Materials, original.Lighting, original.EditorOverlay);
        f = f with { Accessory = f.Accessory with { Asset = f.Accessory.Asset with { Source = source } } };
        using var project = Create(f); project.AdvancePreview(.25, Token);
        var item = Assert.Single(project.CaptureRenderSnapshot().Accessories);
        Assert.Equal(sourceWorld * PmmAccessoryScene.WorldTransform(item.Inputs.Pose, item.Inputs.ParentWorld), item.Inputs.World);
        Assert.Equal(original.Surface.Snapshot.Vertices.ToArray(), item.Inputs.SourceGeometry.Vertices.ToArray());
        Assert.Throws<InvalidDataException>(() => project.Edit(edit => edit.Accessories[0] = edit.Accessories[0] with { ObjectId = f.ActorId }, Token));
        Assert.False(project.CanUndo);
    }

    [Fact]
    public void SnapshotAccessoryTableFollowsOriginalDocumentOrderDespiteReversedBindingsAndSparseIds()
    {
        var f = Fixture(); var original = f.Controls.ExportDocument(); var first = original.Accessories[0];
        var second = new PmmAccessory
        {
            Index = 2, Name = new(0, "second", []), Path = first.Path, DrawOrderIndex = 1,
            Scale = 2, OpacityAndVisibility = 1, ParentModelIndex = 42, ParentBoneIndex = 0,
            InitialKeyframe = new() { Scale = 2, OpacityAndVisibility = 1, ParentModelIndex = 42, ParentBoneIndex = 0 }
        };
        var document = new PmmDocument
        {
            AnsiCodePage = original.AnsiCodePage, PanelStates = original.PanelStates, OutputWidth = original.OutputWidth,
            OutputHeight = original.OutputHeight, CameraFov = original.CameraFov, Camera = original.Camera,
            Settings = original.Settings, Models = original.Models, Light = original.Light, Gravity = original.Gravity,
            SelfShadow = original.SelfShadow, AccessoryNames = [first.Name, second.Name], Accessories = [first, second]
        };
        var controls = ScenePmmControlState.Create(document, [new(f.ActorId, 42, f.Model)]);
        var secondBinding = f.Accessory with { ObjectId = Guid.NewGuid(), Asset = f.Accessory.Asset with { Index = 2 } };
        using var project = new SceneProjectSession(f.Layout, new(), [f.Motion],
            new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException()),
            pmmControls: controls, accessories: [secondBinding, f.Accessory], cancellationToken: Token);
        Assert.Equal(new byte[] { 2, 7 }, project.Accessories.Select(item => item.Asset.Index));
        var snapshot = project.CaptureRenderSnapshot();
        Assert.Equal(new byte[] { 7, 2 }, snapshot.Accessories.Select(item => item.Asset.Index));
        Assert.Equal(new[] { f.Accessory.ObjectId, secondBinding.ObjectId }, snapshot.Accessories.Select(item => item.ObjectId));
        Assert.Equal(new[] { 42, 7, 2 }, snapshot.MixedDrawOrder.Select(item => item.StoredIndex));
    }

    private sealed record Inputs(SceneCompositionEditor Layout, Guid ActorId, PmxModel Model, SceneActorMotion Motion,
        ScenePmmControlState Controls, SceneProjectAccessory Accessory);

    private static SceneProjectSession Create(Inputs f, bool prepared = true) => new(f.Layout, new(), [f.Motion],
        new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("No physics expected."), _ => throw new InvalidOperationException()),
        pmmControls: f.Controls, accessories: prepared ? [f.Accessory] : null, cancellationToken: Token);

    private static Inputs Fixture(bool visible = true)
    {
        var basis = EditedMotionProjectTests.Model(); var skeleton = PmmMotionAdapterTests.Pmx(["unused", "root"]);
        var model = new PmxModel { Header = basis.Header, Name = "parent", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = basis.Vertices, Faces = basis.Faces, Textures = basis.Textures, Materials = basis.Materials,
            Bones = skeleton.Bones, Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [] };
        var layout = new SceneCompositionEditor(); var actor = layout.Add("parent", "parent.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(model));
        layout.SetTransform(actor.Id, new(10, 0, 0), Quaternion.Identity, 1);
        var motion = new SceneActorMotion(actor.Id, model, new()
        {
            BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 10, Translation = new(10, 0, 0) }],
            IkKeyframes = [new() { Visible = false }]
        }, PhysicsEnabled: false);
        var document = Document(visible: visible); var controls = ScenePmmControlState.Create(document, [new(actor.Id, 42, model)]);
        var raw = XAccessoryTests.Read(XAccessoryTests.Triangle.Replace("image.png", "image.png*effect.spa"));
        var asset = new PreparedPmmAccessory(7, "part.x", new string('a', 64), XAccessoryAdapter.FromDocument(raw).Frame, []) { Document = raw };
        return new(layout, actor.Id, model, motion, controls, new(Guid.NewGuid(), asset));
    }

    private static PmmDocument Document(int parentModel = 42, int savedParentModel = 42, bool visible = true)
    {
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(v => v).ToArray();
        PmmString S(string text) => new(0, text, []);
        return new()
        {
            AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 30,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0,
                CameraLookAtModelIndex = -1, CameraLookAtBoneIndex = -1, AccessoryIndexAfterModels = 0 },
            Models = [new() { Index = 42, Name = S("parent"), Path = S("parent.pmx"), BoneNames = [S("root"), S("unused")],
                SelectedMorphIndices = [-1, -1, -1, -1], Visible = false,
                InitialBoneKeyframes = Enumerable.Range(0, 2).Select(i => new PmmBoneKeyframe
                    { ObjectIndex = i, Rotation = Quaternion.Identity, Interpolation = linear }).ToArray(),
                InitialModelKeyframe = new() { Visible = false },
                BoneStates = Enumerable.Range(0, 2).Select(_ => new PmmBoneState(0, Vector3.Zero, Quaternion.Identity, false, false, 0)).ToArray() }],
            AccessoryNames = [S("part")], Accessories = [new() { Index = 7, Name = S("part"), Path = S("part.x"), DrawOrderIndex = 0,
                Scale = 2, OpacityAndVisibility = PmmTrackSampling.PackOpacity(.5f, visible), ParentModelIndex = savedParentModel, ParentBoneIndex = 0,
                AddBlendEnabled = true, InitialKeyframe = new() { NextIndex = 1, Scale = 2, OpacityAndVisibility = PmmTrackSampling.PackOpacity(.5f, visible),
                    ParentModelIndex = parentModel, ParentBoneIndex = 0, Rotation = new(4 * MathF.PI + .2f, -.3f, .4f) },
                Keyframes = [new() { ObjectIndex = 1, Frame = 40, Scale = 2, OpacityAndVisibility = PmmTrackSampling.PackOpacity(.5f, visible),
                    ParentModelIndex = parentModel, ParentBoneIndex = 0, Rotation = new(4 * MathF.PI + .2f, -.3f, .4f) }] }]
        };
    }
}
