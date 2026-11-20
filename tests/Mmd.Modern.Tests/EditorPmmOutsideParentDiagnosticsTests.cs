using System.Numerics;
using System.Reflection;
using Avalonia;
using Avalonia.Platform;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorPmmOutsideParentDiagnosticsTests : IDisposable
{
    private readonly IDisposable locatorScope;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly byte[] BoneCurve = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(b => b).ToArray();
    private static readonly byte[] CameraCurve = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 6).SelectMany(b => b).ToArray();

    public EditorPmmOutsideParentDiagnosticsTests()
    {
        locatorScope = (IDisposable)typeof(AvaloniaLocator).GetMethod("EnterScope", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var locator = typeof(AvaloniaLocator).GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var binding = locator.GetType().GetMethod("Bind")!.MakeGenericMethod(typeof(IAssetLoader)).Invoke(locator, null)!;
        binding.GetType().GetMethod("ToConstant")!.MakeGenericMethod(typeof(StandardAssetLoader)).Invoke(binding, [new StandardAssetLoader(typeof(EditorWindow).Assembly)]);
    }

    public void Dispose() => locatorScope.Dispose();

    [Fact]
    public void UnregisteredOutsideParentDraftsEmitLocalizedNoticesAndDiagnostics()
    {
        Assert.Throws<ArgumentNullException>(() => EditorProjectInterchange.InspectPmmUnregisteredOutsideParentNotices(null!));

        var pmx0 = BuildPmx("actor0", ["root", "hand", "extra"]);
        var pmx1 = BuildPmx("actor1", ["root", "aux"]);

        var pmm0 = BuildPmmModel(0, "actor0", ["root", "hand", "extra"],
            [1, 2],
            [new(0, -1, -1), new(0, -1, -1)],
            [
                new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = 5, Visible = true, OutsideParents = [new(0, 1, 0), new(0, -1, -1)] },
                new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, NextIndex = 0, Frame = 20, Visible = true, OutsideParents = [new(0, -1, -1), new(0, -1, -1)] },
            ],
            [new(0, 1, 1), new(0, -2, -1)]);

        var pmm1 = BuildPmmModel(1, "actor1", ["root", "aux"],
            [1],
            [new(0, -1, -1)],
            [],
            [new(0, -2, -1)]);

        var document = BuildDocument(currentFrame: 10, [pmm0, pmm1]);
        var plan = BuildPlan(document, [(pmm0, "actor0.pmx", pmx0), (pmm1, "actor1.pmx", pmx1)]);

        string previousLanguage = EditorLocalization.CurrentLanguage;
        try
        {
            foreach (string language in new[] { "zh-CN", "en", "ja" })
            {
                EditorLocalization.SetLanguage(language);
                string expected = EditorLocalization.Format("Mda.Text.PmmUnregisteredOutsideParentNotice", 2, 3);
                Assert.Equal([expected], EditorProjectInterchange.InspectPmmUnregisteredOutsideParentNotices(plan));
                Assert.Equal([expected], EditorProjectInterchange.InspectPmmNotices(plan));
            }

            EditorLocalization.SetLanguage("zh-CN");
            string zhExpected = EditorLocalization.Format("Mda.Text.PmmUnregisteredOutsideParentNotice", 2, 3);
            Assert.Contains("2", zhExpected);
            Assert.Contains("3", zhExpected);

            var confirmed = plan.ConfirmRoles(new Dictionary<int, ModelPurpose>
            {
                [0] = new(ModelRole.Character),
                [1] = new(ModelRole.Character),
            });
            using var candidate = EditorProjectInterchange.CreatePmmCandidate(confirmed,
                (model, token) => EditorProjectWorkspace.CreateModelAsset(model.Model, model.Path, token),
                new NoPhysics(), token: Token);
            Assert.Contains(zhExpected, candidate.Diagnostics);

            var withHelperDoc = BuildDocument(currentFrame: 10, [pmm0, pmm1],
                [new PmmAccessory { Index = 0, Path = new(0, "C:/effects/Diffusion7/Diffusion.x", []) }]);
            var withHelperPlan = BuildPlan(withHelperDoc, [(pmm0, "actor0.pmx", pmx0), (pmm1, "actor1.pmx", pmx1)]);
            var combinedNotices = EditorProjectInterchange.InspectPmmNotices(withHelperPlan);
            Assert.Equal(2, combinedNotices.Count);
            Assert.Equal(EditorLocalization.Format("Mda.Text.PmmMmeNotice", 1), combinedNotices[0]);
            Assert.Equal(zhExpected, combinedNotices[1]);

            var v1Doc = new PmmDocument { Version = 1, CurrentFrame = 10, Models = [pmm0, pmm1] };
            var v1Plan = BuildPlan(v1Doc, [(pmm0, "actor0.pmx", pmx0), (pmm1, "actor1.pmx", pmx1)]);
            Assert.Empty(EditorProjectInterchange.InspectPmmUnregisteredOutsideParentNotices(v1Plan));
        }
        finally
        {
            EditorLocalization.SetLanguage(previousLanguage);
        }
    }

    [Fact]
    public void MatchingAndStaleBoneIndexOutsideParentsEmitNoUnregisteredNotice()
    {
        var pmx0 = BuildPmx("actor0", ["root", "hand", "extra"]);
        var pmx1 = BuildPmx("actor1", ["root"]);

        var pmm0 = BuildPmmModel(0, "actor0", ["root", "hand", "extra"],
            [-1, 1, 2],
            [new(0, -1, -1), new(0, -2, -1), new(0, 1, 0)],
            [
                new() { RecordIndex = 1, ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = 10, Visible = true, OutsideParents = [new(0, -1, -1), new(0, -2, -1), new(0, 1, 0)] },
                new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, NextIndex = 0, Frame = 20, Visible = true, OutsideParents = [new(0, 1, 0), new(0, -1, -1), new(0, -1, -1)] },
            ],
            [new(0, -1, 0), new(0, -2, 0), new(0, 1, 0)]);

        var pmm1 = BuildPmmModel(1, "actor1", ["root"], [], [], [], []);
        var document = BuildDocument(currentFrame: 10, [pmm0, pmm1]);
        var plan = BuildPlan(document, [(pmm0, "actor0.pmx", pmx0), (pmm1, "actor1.pmx", pmx1)]);

        string previousLanguage = EditorLocalization.CurrentLanguage;
        try
        {
            EditorLocalization.SetLanguage("zh-CN");
            Assert.Empty(EditorProjectInterchange.InspectPmmUnregisteredOutsideParentNotices(plan));
            Assert.Empty(EditorProjectInterchange.InspectPmmNotices(plan));

            var withHelperDoc = BuildDocument(currentFrame: 10, [pmm0, pmm1],
                [new PmmAccessory { Index = 0, Path = new(0, "C:/effects/LightBloom/LightBloom.x", []) }]);
            var withHelperPlan = BuildPlan(withHelperDoc, [(pmm0, "actor0.pmx", pmx0), (pmm1, "actor1.pmx", pmx1)]);
            Assert.Equal([EditorLocalization.Format("Mda.Text.PmmMmeNotice", 1)],
                EditorProjectInterchange.InspectPmmNotices(withHelperPlan));

            var confirmed = plan.ConfirmRoles(new Dictionary<int, ModelPurpose>
            {
                [0] = new(ModelRole.Character),
                [1] = new(ModelRole.Character),
            });
            using var candidate = EditorProjectInterchange.CreatePmmCandidate(confirmed,
                (model, token) => EditorProjectWorkspace.CreateModelAsset(model.Model, model.Path, token),
                new NoPhysics(), token: Token);
            Assert.DoesNotContain(candidate.Diagnostics, d => d.Contains("OutsideParentStates", StringComparison.Ordinal));
        }
        finally
        {
            EditorLocalization.SetLanguage(previousLanguage);
        }
    }

    private static PmmModel BuildPmmModel(byte index, string name, string[] boneNames,
        int[] subjectBones, PmmOutsideParent[] initialParents, PmmModelKeyframe[] modelKeyframes,
        PmmOutsideParent[] savedParents)
    {
        return new()
        {
            Index = index, DrawOrderIndex = index, TransformOrderIndex = index, Visible = true,
            Name = new(0, name, []), Path = new(0, name + ".pmx", []),
            BoneNames = boneNames.Select(b => new PmmString(0, b, [])).ToArray(),
            SelectedMorphIndices = [-1, -1, -1, -1],
            InitialBoneKeyframes = boneNames.Select((_, i) => new PmmBoneKeyframe
            {
                RecordIndex = i, ObjectIndex = i, Rotation = Quaternion.Identity, Interpolation = BoneCurve,
            }).ToArray(),
            BoneStates = boneNames.Select((_, i) => new PmmBoneState(i, Vector3.Zero, Quaternion.Identity, false, false, 0)).ToArray(),
            OutsideParentSubjectBoneIndices = subjectBones,
            InitialModelKeyframe = new()
            {
                Visible = true,
                OutsideParents = initialParents,
                NextIndex = modelKeyframes.Length > 0 ? 1 : 0,
            },
            ModelKeyframes = modelKeyframes,
            OutsideParentStates = savedParents.Select((p, slot) => new PmmOutsideParentState(slot, 0, int.MaxValue, p)).ToArray(),
        };
    }

    private static PmmDocument BuildDocument(int currentFrame, PmmModel[] models, PmmAccessory[]? accessories = null) => new()
    {
        Version = 2, AnsiCodePage = 932, PanelStates = new byte[7], Models = models, Accessories = accessories ?? [],
        OutputWidth = 96, OutputHeight = 72, CameraFov = 45, CurrentFrame = currentFrame,
        Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY, InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } },
        Settings = new() { CameraLookAtModelIndex = -1, CameraLookAtBoneIndex = -1, EdgeColorRgb = new int[3], UnknownMatrix = new byte[64], PreferredFps = 30 },
        Camera = new()
        {
            Position = new(0, 0, -4), Perspective = true,
            InitialKeyframe = new() { Distance = -4, Fov = 45, Perspective = true, ParentModelIndex = -1, ParentBoneIndex = -1, Interpolation = CameraCurve },
        },
    };

    private static PmmProjectImportPlan BuildPlan(PmmDocument document, (PmmModel Pmm, string Path, PmxModel Model)[] models)
    {
        var prepared = models.Select(m => new PreparedPmmModel(m.Pmm, m.Path, m.Model,
            PmmMotionAdapter.Convert(m.Pmm, m.Model), PmmMotionAdapter.ConvertSavedPose(m.Pmm, m.Model))).ToList();
        return (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [document, "outside-parent-diagnostics.pmm", prepared, new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
    }

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
