using System.Numerics;
using System.Reflection;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorRigSettingsGroundOutsideParentTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PmmGroundOutsideParentDisplaysLocalizedGroundTargetWithoutTargetBones() => await session.Dispatch(() =>
    {
        var parentPmx = BuildPmx("parent", ["root"]);
        var childPmx = BuildPmx("child", ["hip", "hand"]);
        byte[] boneCurve = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(b => b).ToArray();
        byte[] cameraCurve = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 6).SelectMany(b => b).ToArray();
        var parentPmm = new PmmModel
        {
            Index = 0, DrawOrderIndex = 0, TransformOrderIndex = 0, Visible = true,
            Name = new(0, "parent", []), Path = new(0, "parent.pmx", []),
            BoneNames = [new(0, "root", [])], SelectedMorphIndices = new int[4],
            InitialBoneKeyframes = [new() { Rotation = Quaternion.Identity, Interpolation = boneCurve }],
            BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)],
            InitialModelKeyframe = new() { Visible = true },
        };
        var childPmm = new PmmModel
        {
            Index = 1, DrawOrderIndex = 1, TransformOrderIndex = 1, Visible = true,
            Name = new(0, "child", []), Path = new(0, "child.pmx", []),
            BoneNames = [new(0, "hip", []), new(0, "hand", [])], SelectedMorphIndices = new int[4],
            InitialBoneKeyframes =
            [
                new() { RecordIndex = 0, ObjectIndex = 0, Rotation = Quaternion.Identity, Interpolation = boneCurve },
                new() { RecordIndex = 1, ObjectIndex = 1, Rotation = Quaternion.Identity, Interpolation = boneCurve },
            ],
            BoneStates =
            [
                new(0, Vector3.Zero, Quaternion.Identity, false, false, 0),
                new(1, Vector3.Zero, Quaternion.Identity, false, false, 0),
            ],
            OutsideParentSubjectBoneIndices = [1],
            InitialModelKeyframe = new() { Visible = true, OutsideParents = [new(0, -2, -1)] },
            OutsideParentStates = [new(0, 0, int.MaxValue, new(0, -2, -1))],
        };
        var document = new PmmDocument
        {
            AnsiCodePage = 932, PanelStates = new byte[7], Models = [parentPmm, childPmm],
            OutputWidth = 96, OutputHeight = 72, CameraFov = 45,
            Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY, InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } },
            Settings = new() { CameraLookAtModelIndex = -1, CameraLookAtBoneIndex = -1, EdgeColorRgb = new int[3], UnknownMatrix = new byte[64], PreferredFps = 30 },
            Camera = new() { Position = new(0, 0, -4), Perspective = true, InitialKeyframe = new() { Distance = -4, Fov = 45, Perspective = true, ParentModelIndex = -1, ParentBoneIndex = -1, Interpolation = cameraCurve } },
        };
        var prepared = new List<PreparedPmmModel>
        {
            new(parentPmm, "parent.pmx", parentPmx, PmmMotionAdapter.Convert(parentPmm, parentPmx), PmmMotionAdapter.ConvertSavedPose(parentPmm, parentPmx)),
            new(childPmm, "child.pmx", childPmx, PmmMotionAdapter.Convert(childPmm, childPmx), PmmMotionAdapter.ConvertSavedPose(childPmm, childPmx)),
        };
        var plan = (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [document, "ground-outside-parent.pmm", prepared, new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
        var confirmed = plan.ConfirmRoles(new Dictionary<int, ModelPurpose>
        {
            [0] = new(ModelRole.Character),
            [1] = new(ModelRole.Character),
        });
        using var candidate = EditorProjectInterchange.CreatePmmCandidate(confirmed,
            (model, token) => EditorProjectWorkspace.CreateModelAsset(model.Model, model.Path, token),
            new NoPhysics(), token: Token);
        using var workspace = new EditorProjectWorkspace();
        workspace.Adopt(candidate);

        Guid parentId = candidate.ModelObjects[0];
        Guid childId = candidate.ModelObjects[1];
        EditorLocalization.SetLanguage("zh-CN");
        try
        {
            var vm = new EditorRigSettingsViewModel(workspace, childId, selectedBoneIndex: 1);
            Assert.False(vm.CanEditOutsideParents);
            Assert.Equal(3, vm.TargetModels.Count);
            Assert.Contains(vm.TargetModels, item => item.IsGround);
            Assert.NotNull(vm.SelectedSubject);
            Assert.Equal(1, vm.SelectedSubject!.Index);
            Assert.NotNull(vm.SelectedTargetModel);
            Assert.True(vm.SelectedTargetModel!.IsGround);
            Assert.Null(vm.SelectedTargetModel.ObjectId);
            Assert.False(vm.HasTargetModel);
            Assert.Empty(vm.TargetBones);
            Assert.Null(vm.SelectedTargetBone);
            Assert.Equal("地面", vm.SelectedTargetModel.Label);
            Assert.Equal("地面外亲无需目标骨骼", vm.TargetBonePlaceholder);

            EditorLocalization.SetLanguage("en");
            Assert.Equal("Ground", vm.SelectedTargetModel.Label);
            Assert.Equal("No target bone for Ground", vm.TargetBonePlaceholder);

            EditorLocalization.SetLanguage("ja");
            Assert.Equal("地面", vm.SelectedTargetModel.Label);
            Assert.Equal("地面には対象ボーン不要", vm.TargetBonePlaceholder);

            // Switch to unbound bone 0 ("hip"), then back to ground-bound bone 1 ("hand").
            vm.SelectedSubject = vm.Subjects.Single(choice => choice.Index == 0);
            Assert.NotNull(vm.SelectedTargetModel);
            Assert.False(vm.SelectedTargetModel!.IsGround);
            Assert.Null(vm.SelectedTargetModel.ObjectId);
            Assert.False(vm.HasTargetModel);
            Assert.Empty(vm.TargetBones);
            Assert.Equal(EditorLocalization.Text("无外亲"), vm.SelectedTargetModel.Label);
            Assert.Equal(EditorLocalization.Text("Mda.Text.Ux.Rig.SelectTargetModelFirst"), vm.TargetBonePlaceholder);

            vm.SelectedSubject = vm.Subjects.Single(choice => choice.Index == 1);
            Assert.NotNull(vm.SelectedTargetModel);
            Assert.True(vm.SelectedTargetModel!.IsGround);
            Assert.False(vm.HasTargetModel);
            Assert.Empty(vm.TargetBones);
            Assert.Equal(EditorLocalization.Text("Mda.Text.Ux.Rig.GroundTarget"), vm.SelectedTargetModel.Label);
            Assert.Equal(EditorLocalization.Text("Mda.Text.Ux.Rig.GroundNoTargetBone"), vm.TargetBonePlaceholder);

            // An actor in the same PMM without ground outside parents omits the Ground entry.
            var parentVm = new EditorRigSettingsViewModel(workspace, parentId);
            Assert.False(parentVm.CanEditOutsideParents);
            Assert.DoesNotContain(parentVm.TargetModels, item => item.IsGround);
            Assert.Equal(2, parentVm.TargetModels.Count);
        }
        finally { EditorLocalization.SetLanguage("zh-CN"); }
        return true;
    }, Token);

    [Fact]
    public async Task NonPmmEditableWorkspaceOmitsGroundChoiceFromTargetModels() => await session.Dispatch(() =>
    {
        var parentPmx = BuildPmx("parent", ["root"]);
        var childPmx = BuildPmx("child", ["hip", "hand"]);
        using var workspace = new EditorProjectWorkspace { PhysicsProviderFactory = () => new NoPhysics() };
        Guid parentId = workspace.AddModel("parent.pmx", EditorProjectWorkspace.CreateModelAsset(parentPmx, "parent.pmx", Token), ModelRole.Character);
        Guid childId = workspace.AddModel("child.pmx", EditorProjectWorkspace.CreateModelAsset(childPmx, "child.pmx", Token), ModelRole.Character);

        var vm = new EditorRigSettingsViewModel(workspace, childId);
        Assert.True(vm.CanEditOutsideParents);
        Assert.DoesNotContain(vm.TargetModels, item => item.IsGround);
        Assert.Equal(2, vm.TargetModels.Count);
        Assert.Null(vm.TargetModels[0].ObjectId);
        Assert.False(vm.TargetModels[0].IsGround);
        Assert.Equal(parentId, vm.TargetModels[1].ObjectId);
        return true;
    }, Token);

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);

    private static PmxModel BuildPmx(string name, string[] boneNames) => new()
    {
        Header = new()
        {
            Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1,
        },
        Name = name, EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 2, 1)],
        Materials = [new()
        {
            Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
            Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.BothFaces, EdgeColor = Vector4.Zero, EdgeSize = 0,
            TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
            ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3,
        }],
        Bones = boneNames.Select(boneName => new PmxBone
        {
            Name = boneName, EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None,
        }).ToArray(),
        Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
        Vertices = new[] { new Vector3(-.7f, -.6f, 0), new Vector3(.7f, -.6f, 0), new Vector3(0, .7f, 0) }.Select(position => new PmxVertex
        {
            Position = position, Normal = -Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [],
            WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1,
        }).ToArray(),
    };

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
            => throw new InvalidOperationException("The fixture has no physics bodies.");
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings)
            => throw new InvalidOperationException("The fixture has no physics bodies.");
    }
}
