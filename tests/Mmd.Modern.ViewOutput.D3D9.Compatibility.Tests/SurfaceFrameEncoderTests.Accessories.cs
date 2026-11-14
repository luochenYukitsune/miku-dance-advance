using System.Numerics;
using System.Text;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed partial class SurfaceFrameEncoderTests
{
    internal static PmxModel AccessoryCarrierModel() => Model();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedParentUsesTheExactNativeBoneAndRetainsItWhileTheModelIsHidden(bool hidden)
    {
        var model = Model();
        var layout = new SceneCompositionEditor();
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 0, false));
        var actor = layout.Add("parent", "parent.pmx", ModelRole.Unspecified, rest);
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 10, Translation = new(10, 0, 0) }],
            IkKeyframes = [new() { Visible = !hidden }] };
        var raw = XTextReader.Read(new MemoryStream(Encoding.ASCII.GetBytes("""
            xof 0303txt 0032
            Mesh mesh {3;0;0;0;,1;0;0;,0;1;0;;1;3;0,1,2;;
              MeshNormals {1;0;0;1;;1;3;0,0,0;;}
              MeshMaterialList {1;1;0;;Material mat {1;1;1;0.8;;8;0.1;0.2;0.3;;0.02;0.03;0.04;;}}}
            """)), new() { AnsiCodePage = 65001 });
        var asset = new PreparedPmmAccessory(7, "part.x", new string('b', 64), XAccessoryAdapter.FromDocument(raw).Frame, []) { Document = raw };
        PmmString S(string value) => new(0, value, []);
        var key = new PmmAccessoryKeyframe { Scale = 1, ParentModelIndex = 42, ParentBoneIndex = 0,
            OpacityAndVisibility = PmmTrackSampling.PackOpacity(.5f, true) };
        var pmm = new PmmDocument { AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 64, OutputHeight = 32, CameraFov = 30,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Models = [new() { Index = 42, Name = S("parent"), Path = S("parent.pmx"), DrawOrderIndex = 1,
                BoneNames = [S("root")], SelectedMorphIndices = [-1, -1, -1, -1],
                InitialBoneKeyframes = [new() { Rotation = Quaternion.Identity, Interpolation = new byte[16] }],
                BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)],
                InitialModelKeyframe = new() { Visible = !hidden }, Visible = !hidden }],
            Settings = new() { AccessoryIndexAfterModels = 0, PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0],
                UnknownMatrix = new byte[64], SelectionTrailerFlag = 0, CameraLookAtModelIndex = -1, CameraLookAtBoneIndex = -1 },
            AccessoryNames = [S("part")], Accessories = [new() { Index = 7, Name = S("part"), Path = S("part.x"),
                DrawOrderIndex = 0, Scale = 1, ParentModelIndex = 42, ParentBoneIndex = 0, InitialKeyframe = key }] };
        var accessoryId = Guid.NewGuid();
        var controls = ScenePmmControlState.Create(pmm, [new(actor.Id, 42, model)]);
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, motion, PhysicsEnabled: false)], Provider(),
            initialFrame: 7.5, pmmControls: controls, accessories: [new(accessoryId, asset)]);
        var encoder = new D3D9SurfaceFrameEncoder([new(actor.Id, FirstId, "parent.pmx", "parent", ["root"]),
            D3D9SurfaceObjectBinding.CreateAccessory(accessoryId, SecondId, "part.x", "part")]);
        var s = project.CaptureRenderSnapshot(encoder.SceneInputRequests);
        Assert.Equal(hidden ? 0 : 1, s.SceneInputs[actor.Id].BoneWorldMatrices.Count);
        var expectedParent = s.AccessoryParentBoneWorldMatrices[actor.Id][0];
        Assert.Equal(expectedParent, Assert.Single(s.Accessories).Inputs.ParentWorld);
        var frame = new OfflineFrame(new(0, new(0, 1), new(15, 2), new(15, 2)), 64, 32, s.Scene, s.Camera, s.Light, s.SelfShadow)
        { ResolvedCamera = s.ResolvedCamera, CameraParentTransform = s.CameraParentTransform, MorphChannels = s.MorphChannels,
            RenderingStyles = s.RenderingStyles, SceneInputs = s.SceneInputs, Accessories = s.Accessories,
            MixedDrawOrder = s.MixedDrawOrder, AccessoryIndexAfterModels = s.AccessoryIndexAfterModels,
            UnpreparedAccessoryIndices = s.UnpreparedAccessoryIndices,
            AccessoryParentBoneWorldMatrices = s.AccessoryParentBoneWorldMatrices };
        var request = Request with { Quality = new() { SurfaceOrder = SurfaceDrawOrder.Original } };
        var packets = encoder.Serialize(frame, request, Vector4.One);
        using var reader = new BinaryReader(new MemoryStream(packets.Frame)); reader.BaseStream.Position = 216;
        Assert.Equal(FirstId, reader.ReadUInt64()); Assert.Equal(1u, reader.ReadUInt32()); Assert.Equal(1, reader.ReadInt32());
        reader.BaseStream.Position += 20; SkipString(reader); SkipString(reader); Assert.Equal(1u, reader.ReadUInt32());
        SkipString(reader); reader.BaseStream.Position += 12 * 4; Assert.Equal(expectedParent.M41, reader.ReadSingle());
        reader.BaseStream.Position += 3 * 4; Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(SecondId, reader.ReadUInt64()); Assert.Equal(2u, reader.ReadUInt32()); Assert.Equal(2, reader.ReadInt32());
        reader.BaseStream.Position += 20; SkipString(reader); SkipString(reader); reader.BaseStream.Position += 8 + 64 + 12 + 12 + 4 + 4 + 8;
        Assert.Equal(1u, reader.ReadUInt32()); Assert.Equal(FirstId, reader.ReadUInt64()); Assert.Equal(0, reader.ReadInt32());
        if (hidden)
            Assert.Throws<InvalidDataException>(() => encoder.Serialize(frame with
            { AccessoryParentBoneWorldMatrices = new Dictionary<Guid, IReadOnlyDictionary<int, Matrix4x4>>() }, request, Vector4.One));
    }
}
