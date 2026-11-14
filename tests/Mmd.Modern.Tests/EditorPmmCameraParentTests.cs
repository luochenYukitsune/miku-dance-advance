using System.Numerics;
using System.Reflection;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;
using Avalonia;
using Avalonia.Platform;

namespace Mmd.Modern.Tests;

public sealed class EditorPmmCameraParentTests : IDisposable
{
    private readonly IDisposable locatorScope;
    public EditorPmmCameraParentTests()
    {
        // These are model tests. Register only packaged text loading, without a headless UI event loop.
        locatorScope = (IDisposable)typeof(AvaloniaLocator).GetMethod("EnterScope", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var locator = typeof(AvaloniaLocator).GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var binding = locator.GetType().GetMethod("Bind")!.MakeGenericMethod(typeof(IAssetLoader)).Invoke(locator, null)!;
        binding.GetType().GetMethod("ToConstant")!.MakeGenericMethod(typeof(StandardAssetLoader)).Invoke(binding, [new StandardAssetLoader(typeof(EditorWindow).Assembly)]);
    }
    public void Dispose() => locatorScope.Dispose();
    [Fact]
    public async Task ImportedFollowSummaryAndLocalCameraRegistrationRetainTheParent()
    {
        using var playback = ResolvedOutputCameraFixture.Playback();
        var plan = playback.Plan.ConfirmRoles(new Dictionary<int, ModelPurpose> { [0] = new(ModelRole.Character) });
        using var candidate = EditorProjectInterchange.CreatePmmCandidate(plan,
            (model, token) => EditorProjectWorkspace.CreateModelAsset(model.Model, model.Path, token), new NoPhysics(), token: TestContext.Current.CancellationToken);
        var inspector = new EditorSceneInspectorViewModel { SceneSelectionIndex = 1 };
        inspector.SetPmmCameraContext(candidate.Session);
        inspector.SetSceneMotion(candidate.Session.CaptureAuthoredContent().SceneMotion);
        Assert.True(inspector.HasCameraParent);
        Assert.Equal(Assert.Single(candidate.Session.Objects).Name, inspector.CameraParentObject);
        Assert.Equal("root", inspector.CameraParentBone);
        var originalParent = inspector.CameraPose.ParentModelIndex;
        inspector.CameraCenterX = 3.25m;
        inspector.CameraRotationZ = 12;
        Assert.Equal(originalParent, inspector.CameraPose.ParentModelIndex);
        Assert.Equal(0, inspector.CameraPose.ParentBoneIndex);
        VmdMotion? committed = null;
        inspector.CommitSceneMotion = motion => committed = motion;
        inspector.ConfirmSceneOverwriteAsync = _ => Task.FromResult(true);
        await inspector.RegisterCameraKeyframeAsync();
        var key = Assert.Single(committed!.CameraKeyframes);
        Assert.Equal(3.25f, key.Interest.X);
        Assert.InRange(key.Rotation.Z, .20943f, .20945f);
        Assert.Equal(originalParent, inspector.CameraPose.ParentModelIndex);
        Assert.False(inspector.HasCameraDraft);
    }

    [Fact]
    public void SavedLocalCameraDraftUsesItsOriginalParentFields()
    {
        var inspector = new EditorSceneInspectorViewModel();
        var saved = new PmmCameraPose(new(1, 2, 3), new(.1f, .2f, .3f), -12, 40, true, 42, 7, true);
        inspector.SetCameraDraft(saved);
        inspector.CameraDistance = -18;
        Assert.Equal(saved with { LookAt = saved.LookAt with { Z = -18 } }, inspector.CameraPose);
        Assert.Equal(-12, inspector.CameraPose.Distance);
        Assert.Equal(-18m, inspector.CameraCenterZ);
        var parent = Matrix4x4.CreateRotationY(.6f) * Matrix4x4.CreateTranslation(4, 5, 6);
        Assert.True(Vector3.Distance(saved.CreateCamera(parent).Eye, inspector.CameraPose.CreateCamera(parent).Eye) > 20);
        inspector.CameraCenterZ = -9;
        Assert.Equal(-9m, inspector.CameraDistance);
        Assert.Equal(-12, inspector.CameraPose.Distance);
        Assert.True(inspector.HasCameraDraft);
    }

    [Fact]
    public async Task RegisteringSavedCameraPanRetainsItsWorldEye()
    {
        var inspector = new EditorSceneInspectorViewModel { SceneSelectionIndex = 1 };
        var saved = new PmmCameraPose(new(3, 12, -4), new(.31f, -.47f, .22f), -37, 40, true,
            ViewOffset: new(2, -3));
        inspector.SetCameraDraft(saved);
        var expected = inspector.CameraPose.CreateCamera();
        VmdMotion? committed = null;
        inspector.CommitSceneMotion = motion => committed = motion;
        await inspector.RegisterCameraKeyframeAsync();
        var key = Assert.Single(committed!.CameraKeyframes);
        var registered = new VmdSceneTracks(committed!).CameraAt(key.Frame)!.Value;
        var actual = registered.CreateCamera();
        Assert.Equal(Vector2.Zero, registered.ViewOffset);
        Assert.NotEqual(saved.LookAt, key.Interest);
        Assert.InRange(Vector3.Distance(expected.Eye, actual.Eye), 0, .0001f);
        Assert.InRange(Vector3.Distance(expected.Up, actual.Up), 0, .0001f);
        Assert.Equal(registered, inspector.CameraPose);
        Assert.False(inspector.HasCameraDraft);
    }

    [Fact]
    public void RenamedFollowObjectRefreshesItsLabelWithoutDiscardingALocalCameraDraft()
    {
        using var playback = ResolvedOutputCameraFixture.Playback();
        var plan = playback.Plan.ConfirmRoles(new Dictionary<int, ModelPurpose> { [0] = new(ModelRole.Character) });
        using var candidate = EditorProjectInterchange.CreatePmmCandidate(plan,
            (model, token) => EditorProjectWorkspace.CreateModelAsset(model.Model, model.Path, token), new NoPhysics(), token: TestContext.Current.CancellationToken);
        var session = candidate.Session;
        var inspector = new EditorSceneInspectorViewModel();
        inspector.SetPmmCameraContext(session);
        inspector.SetSceneMotion(session.CaptureAuthoredContent().SceneMotion);
        inspector.CameraCenterX = 3.25m;
        var draft = inspector.CameraPose;
        var controls = session.PmmControls;
        var item = Assert.Single(session.Objects);
        session.Edit(edit => edit.Composition.SetObjectMetadata(item.Id, "Renamed camera target", item.Role), TestContext.Current.CancellationToken);
        Assert.Same(controls, session.PmmControls);
        var changed = new List<string?>();
        inspector.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        inspector.SetPmmCameraContext(session);
        Assert.Equal("Renamed camera target", inspector.CameraParentObject);
        Assert.Contains(nameof(EditorSceneInspectorViewModel.CameraParentObject), changed);
        Assert.True(inspector.HasCameraDraft);
        Assert.Equal(draft, inspector.CameraPose);
    }

    [Theory]
    [InlineData(1.5f, false)]
    [InlineData(.5f, false)]
    [InlineData(1.5f, true)]
    [InlineData(.5f, true)]
    public void NewLightWorldCameraReconstructsTheEyeUnderUniformParentScale(float scale, bool ignoreParentRotation)
    {
        var local = new PmmCameraPose(new(1, 2, -3), new(.2f, -.4f, .1f), -12, 40, true, 7, 0, ignoreParentRotation, new(2, -3));
        var parent = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(.6f)
            * Matrix4x4.CreateRotationX(-.3f) * Matrix4x4.CreateTranslation(4, 5, 6);
        var method = typeof(EditorViewportHost).GetMethod("WorldCameraPose", BindingFlags.NonPublic | BindingFlags.Static)!;
        var worldPose = (PmmCameraPose)method.Invoke(null, [local, parent])!;
        var expected = local.CreateCamera(parent);
        var actual = worldPose.CreateCamera();
        var forward = Vector3.Normalize(expected.Target - expected.Eye);
        Assert.Equal(-1, worldPose.ParentModelIndex);
        Assert.False(worldPose.IgnoreParentRotation);
        Assert.Equal(Vector2.Zero, worldPose.ViewOffset);
        Assert.InRange(Vector3.Distance(actual.Eye, expected.Eye), 0, .0001f);
        Assert.InRange(Vector3.Distance(worldPose.LookAt, expected.Eye + forward * MathF.Abs(local.CameraDepth)), 0, .0001f);
        Assert.Equal(-MathF.Abs(local.CameraDepth), worldPose.Distance);
        Assert.InRange(Vector3.Distance(Vector3.Normalize(actual.Target - actual.Eye), forward), 0, .0001f);
        Assert.InRange(Vector3.Distance(Vector3.Normalize(actual.Up), Vector3.Normalize(expected.Up)), 0, .0001f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void NewLightWorldCameraRetainsRollAtThePitchGimbal(int pitchSign)
    {
        var local = new PmmCameraPose(new(1, 2, -8), new(pitchSign * MathF.PI / 2, -.4f, .7f), -12, 40, true, 7);
        var method = typeof(EditorViewportHost).GetMethod("WorldCameraPose", BindingFlags.NonPublic | BindingFlags.Static)!;
        var worldPose = (PmmCameraPose)method.Invoke(null, [local, Matrix4x4.Identity])!;
        var expected = local.CreateCamera(Matrix4x4.Identity);
        var actual = worldPose.CreateCamera();
        Assert.InRange(Vector3.Distance(actual.Eye, expected.Eye), 0, .0001f);
        Assert.InRange(Vector3.Distance(Vector3.Normalize(actual.Target - actual.Eye), Vector3.Normalize(expected.Target - expected.Eye)), 0, .0001f);
        Assert.InRange(Vector3.Distance(Vector3.Normalize(actual.Up), Vector3.Normalize(expected.Up)), 0, .0001f);
    }

    [Fact]
    public void ParentPanAndZoomEditCameraSpaceEyeWithoutChangingRawDistance()
    {
        var pose = new PmmCameraPose(new(1, 2, -20), new(.31f, -.47f, .22f), -99, 40, true, 7, 0, true);
        var pan = typeof(EditorViewportHost).GetMethod("PanCamera", BindingFlags.NonPublic | BindingFlags.Static)!;
        var zoom = typeof(EditorViewportHost).GetMethod("ZoomCamera", BindingFlags.NonPublic | BindingFlags.Static)!;
        var moved = (PmmCameraPose)pan.Invoke(null, [pose, 10f, -5f, 800])!;
        float units = 40 * MathF.Tan(40 * MathF.PI / 360) / 800;
        Assert.InRange(Vector3.Distance(moved.LookAt, pose.LookAt + new Vector3(-10, -5, 0) * units), 0, .00001f);
        Assert.Equal(pose.Distance, moved.Distance);
        Assert.Equal(pose.IgnoreParentRotation, moved.IgnoreParentRotation);
        var closer = (PmmCameraPose)zoom.Invoke(null, [moved, 120])!;
        Assert.Equal(-18, closer.LookAt.Z);
        Assert.Equal(moved.LookAt.X, closer.LookAt.X);
        Assert.Equal(moved.LookAt.Y, closer.LookAt.Y);
        Assert.Equal(pose.Distance, closer.Distance);

        var ordinary = pose with { ParentModelIndex = -1, IgnoreParentRotation = false };
        var ordinaryZoom = (PmmCameraPose)zoom.Invoke(null, [ordinary, 120])!;
        Assert.Equal(ordinary.LookAt, ordinaryZoom.LookAt);
        Assert.InRange(MathF.Abs(ordinaryZoom.Distance - ordinary.Distance * .9f), 0, .00001f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedDraftWithADifferentParentCommitsPoseAndParentInOneUndoStep(bool allDrafts)
    {
        using var playback = ResolvedOutputCameraFixture.Playback();
        var plan = playback.Plan.ConfirmRoles(new Dictionary<int, ModelPurpose> { [0] = new(ModelRole.Character) });
        using var candidate = EditorProjectInterchange.CreatePmmCandidate(plan,
            (model, token) => EditorProjectWorkspace.CreateModelAsset(model.Model, model.Path, token), new NoPhysics(), token: TestContext.Current.CancellationToken);
        using var workspace = new EditorProjectWorkspace();
        workspace.Adopt(candidate);
        var original = workspace.Session!.CameraPose!.Value;
        var local = original with { LookAt = new(3, 4, 5), Fov = 44.5f, ParentModelIndex = -1, ParentBoneIndex = -1 };
        var editor = new SceneTrackEditor(workspace.Session.CaptureAuthoredContent().SceneMotion!.Motion);
        var edit = editor.PrepareRegister(new VmdMotion { CameraKeyframes = [new()
        { Interest = local.LookAt, Rotation = local.Angle, Distance = local.Distance, ViewAngle = 45, Perspective = true }] });
        editor.Apply(edit, confirmOverwrite: true);
        if (allDrafts) workspace.CommitDraftMotions(new Dictionary<Guid, VmdMotion>(), editor.ExportMotion(), 0, local);
        else workspace.CommitSceneMotion(editor.ExportMotion(), 0, local);
        Assert.Equal(-1, workspace.Session.CameraPose!.Value.ParentModelIndex);
        Assert.Equal(local.LookAt, workspace.Session.CameraPose!.Value.LookAt);
        Assert.Equal(45, workspace.Session.CameraPose!.Value.Fov);
        Assert.True(workspace.Undo());
        Assert.Equal(original, workspace.Session.CameraPose!.Value);
        Assert.True(workspace.Redo());
        Assert.Equal(-1, workspace.Session.CameraPose!.Value.ParentModelIndex);
        Assert.Equal(local.LookAt, workspace.Session.CameraPose!.Value.LookAt);
    }

    [Theory]
    [InlineData("ExcellentShadow2/ExcellentShadow.x")]
    [InlineData("SvSSAO/SvSSAO.x")]
    [InlineData("Diffusion7/Diffusion.x")]
    [InlineData("LightBloom/LightBloom.x")]
    [InlineData("AutoLuminous4/AutoLuminous.x")]
    public void KnownMmeHelperIsANoticeAndItsRawMetadataIsRetained(string helper)
    {
        var accessory = new PmmAccessory { Index = 5, Path = new(0, "C:/effects/" + helper, []),
            InitialKeyframe = new() { Translation = new(1, 2, 3) } };
        var plan = MetadataPlan(accessory);
        Assert.Empty(EditorProjectInterchange.InspectPmmSupport(plan));
        Assert.Single(EditorProjectInterchange.InspectPmmNotices(plan));
        Assert.Same(accessory, Assert.Single(plan.Document.Accessories));
        Assert.Equal(new Vector3(1, 2, 3), accessory.InitialKeyframe.Translation);
    }

    [Theory]
    [InlineData("C:/props/tree.x")]
    [InlineData("C:/props/LightBloom.x")]
    [InlineData("C:/props/SvSSAO/visible-prop.x")]
    public void OrdinaryGeometryStillBlocksRatherThanBeingClassifiedByExtensionOrFilenameAlone(string path)
    {
        var plan = MetadataPlan(new PmmAccessory { Path = new(0, path, []) });
        Assert.Single(EditorProjectInterchange.InspectPmmSupport(plan));
        Assert.Empty(EditorProjectInterchange.InspectPmmNotices(plan));
    }

    private static PmmProjectImportPlan MetadataPlan(PmmAccessory accessory)
    {
        var document = new PmmDocument { Version = 2, Models = [new() { Index = 0 }], Accessories = [accessory] };
        return (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [document, "pmm-support.pmm", new List<PreparedPmmModel>(), new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
    }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
            => throw new InvalidOperationException("The camera fixture has no physics bodies.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
            => throw new InvalidOperationException("The camera fixture has no physics bodies.");
    }
}
