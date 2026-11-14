using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Application;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class SurfaceAccessoryEncoderTests
{
    private static readonly ViewRenderRequest Request = new(64, 32, Quality: new() { SurfaceOrder = SurfaceDrawOrder.Original });
    private const ulong CarrierNativeId = 43;

    [Fact]
    public void TypedBindingUsesIndependentIdentityAndDoesNotRequestModelBones()
    {
        var accessory = D3D9SurfaceObjectBinding.CreateAccessory(Guid.NewGuid(), 42, "part.x", "part");
        var encoder = new D3D9SurfaceFrameEncoder([accessory]);
        Assert.Equal(PmmDrawableKind.Accessory, accessory.Kind);
        Assert.Empty(encoder.SceneInputRequests);
        Assert.Throws<ArgumentException>(() => new D3D9SurfaceFrameEncoder([accessory,
            new(Guid.NewGuid(), 42, "actor.pmx", "actor")]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AccessoryPacketKeepsLocalGeometryRawMaterialPoseOpacityAndSplit(bool visible)
    {
        using var project = Project(visible, "", out var binding);
        var encoder = Encoder(project, binding);
        var frame = Capture(project, encoder);
        var packets = encoder.Serialize(frame, Request, Vector4.One);
        Assert.Equal(3u, U(packets.Frame, 8)); Assert.Equal(4u, U(packets.Visual, 8));
        Assert.Equal(2u, U(packets.Frame, 208)); Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(packets.Frame.AsSpan(212)));
        Assert.Equal(SHA256.HashData(packets.Frame), packets.Visual.AsSpan(20, 32).ToArray());
        using var reader = new BinaryReader(new MemoryStream(packets.Frame)); reader.BaseStream.Position = 216;
        Assert.Equal(2u, U(packets.Frame, 20)); SkipHiddenCarrier(reader);
        Assert.Equal(42ul, reader.ReadUInt64()); Assert.Equal(2u, reader.ReadUInt32());
        Assert.NotEqual(0, reader.ReadInt32()); Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(visible ? 3u : 0u, reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32()); Assert.Equal(visible ? 1u : 0u, reader.ReadUInt32());
        SkipString(reader); SkipString(reader); Assert.Equal(0u, reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32());
        var sample = Assert.Single(frame.Accessories);
        Assert.Equal(sample.Inputs.World.M41, ReadFloat(reader, 12));
        reader.BaseStream.Position += 3 * 4; // remaining world row
        Assert.Equal(2f, reader.ReadSingle()); Assert.Equal(-3f, reader.ReadSingle()); Assert.Equal(4f, reader.ReadSingle());
        Assert.Equal(.31f, reader.ReadSingle()); Assert.Equal(-.47f, reader.ReadSingle()); Assert.Equal(.63f, reader.ReadSingle());
        Assert.Equal(1.7f, reader.ReadSingle()); Assert.Equal(.5f, reader.ReadSingle());
        reader.BaseStream.Position += 24; // flags + parent identity + bone
        if (visible) { Assert.Equal(0f, reader.ReadSingle()); Assert.Equal(0f, reader.ReadSingle()); Assert.Equal(0f, reader.ReadSingle()); reader.BaseStream.Position += 3 * 32 - 12 + 3 * 4; }
        Assert.Equal(42ul, reader.ReadUInt64()); Assert.Equal(0u, reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(visible ? 3u : 0u, reader.ReadUInt32()); reader.ReadUInt32();
        Assert.Equal(1f, reader.ReadSingle()); Assert.Equal(.5f, reader.ReadSingle()); Assert.Equal(.25f, reader.ReadSingle());
        Assert.Equal(.8f, reader.ReadSingle()); // raw alpha, opacity is applied once by the native adapter
        Assert.Equal(7.5, encoder.Encode(frame, Request, Vector4.One).SourceFrame);
    }

    [Fact]
    public void AccessoryRequiresOriginalOrderAndExplicitTextureResources()
    {
        using var project = Project(true, "", out var binding);
        var encoder = Encoder(project, binding); var frame = Capture(project, encoder);
        Assert.Throws<NotSupportedException>(() => encoder.Encode(frame, Request with { Quality = new() { SurfaceOrder = SurfaceDrawOrder.OpaqueFirst } }, Vector4.One));
        using var textured = Project(true, "missing.png", out var missing);
        var missingEncoder = Encoder(textured, missing);
        Assert.Throws<InvalidDataException>(() => missingEncoder.Encode(Capture(textured, missingEncoder), Request, Vector4.One));
    }

    [Fact]
    public void PreviewConversionRetainsSameFrozenAccessoryAndOrderSidecars()
    {
        using var project = Project(true, "", out var binding); var encoder = Encoder(project, binding);
        var snapshot = project.CaptureRenderSnapshot(encoder.SceneInputRequests);
        var preview = new PreviewFrame(64, 32, 7.5, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
        { ResolvedCamera = snapshot.ResolvedCamera, CameraParentTransform = snapshot.CameraParentTransform,
            MorphChannels = snapshot.MorphChannels, RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs,
            Accessories = snapshot.Accessories, MixedDrawOrder = snapshot.MixedDrawOrder, AccessoryIndexAfterModels = snapshot.AccessoryIndexAfterModels,
            UnpreparedAccessoryIndices = snapshot.UnpreparedAccessoryIndices, AccessoryParentBoneWorldMatrices = snapshot.AccessoryParentBoneWorldMatrices };
        var offline = D3D9SurfaceFrameInput.FromPreview(preview);
        Assert.Same(preview.Accessories, offline.Accessories); Assert.Same(preview.MixedDrawOrder, offline.MixedDrawOrder);
        Assert.Same(preview.SceneInputs, offline.SceneInputs); Assert.Same(preview.RenderingStyles, offline.RenderingStyles);
        Assert.Same(preview.UnpreparedAccessoryIndices, offline.UnpreparedAccessoryIndices);
        Assert.Same(preview.AccessoryParentBoneWorldMatrices, offline.AccessoryParentBoneWorldMatrices);
        Assert.Equal(-1, offline.AccessoryIndexAfterModels); Assert.Equal(7.5, offline.Sample.SampledSourceFrame.ToDouble());
        Assert.Equal(7.5, encoder.Encode(offline, Request, Vector4.One).SourceFrame);
    }

    [Fact]
    public void MetadataOnlyAccessoryDeficitIsRejectedBeforePacketSubmission()
    {
        using var project = Project(true, "", out var binding);
        var encoder = Encoder(project, binding);
        var frame = Capture(project, encoder) with { UnpreparedAccessoryIndices = new byte[] { 99 } };
        Assert.Throws<NotSupportedException>(() => encoder.Encode(frame, Request, Vector4.One));
    }

    [Theory]
    [InlineData("image.png", 0, 0, -1)]
    [InlineData("effect.sph", 1, 0, -1)]
    [InlineData("effect.spa", 2, 0, -1)]
    [InlineData("image.png*effect.sph", 4, 0, 1)]
    [InlineData("image.png*effect.spa", 5, 0, 1)]
    public void TraditionalTextureTokensRetainTheirIndependentRoles(string token, uint type, int primary, int sphere)
    {
        using var project = Project(true, token, out var metadata);
        var images = token.Split('*').ToDictionary(name => name, name => new SceneTexture(name, 1, 1, new byte[] { 1, 2, 3, 255 }));
        var binding = D3D9SurfaceObjectBinding.CreateAccessory(metadata.ObjectId, metadata.NativeId, metadata.SourcePath, metadata.Name, images);
        var encoder = Encoder(project, binding);
        var packets = encoder.Serialize(Capture(project, encoder), Request, Vector4.One);
        // The hidden carrier contributes metadata only; the accessory keeps its own local geometry.
        using var reader = new BinaryReader(new MemoryStream(packets.Frame)); reader.BaseStream.Position = 216;
        SkipHiddenCarrier(reader); reader.BaseStream.Position += 36;
        SkipString(reader); SkipString(reader); reader.BaseStream.Position += 8 + 120 + 3 * 32 + 3 * 4;
        reader.BaseStream.Position += 24 + 4 * 4 + 3 * 4 + 4 + 3 * 4;
        Assert.Equal(type, reader.ReadUInt32()); Assert.Equal(primary, reader.ReadInt32()); Assert.Equal(sphere, reader.ReadInt32());
    }

    [Fact]
    public void ScreenMediaIsAnExplicitCpuPreflightFailure()
    {
        using var project = Project(true, "screen.bmp", out var binding);
        var encoder = Encoder(project, binding);
        Assert.Throws<NotSupportedException>(() => encoder.Encode(Capture(project, encoder), Request, Vector4.One));
    }

    [Fact]
    public void FrozenScreenModeLetsSynchronousPreflightValidateWithoutDecodedMediaAndRejectsModeDrift()
    {
        using var project = Project(true, "screen.bmp", out var accessory);
        D3D9SurfaceObjectBinding[] bindings =
            [new(Assert.Single(project.Objects).Id, CarrierNativeId, "carrier.pmx", "carrier", ["root"]), accessory];
        var configuration = SurfaceEmmViewTests.Configuration(bindings);
        const D3D9ClassicScreenCaptureMode mode = D3D9ClassicScreenCaptureMode.CropFourByThree;
        static D3D9ClassicPassInputs Passes(OfflineFrame accepted) => new(accepted, true, null,
            new(false, -Vector3.UnitY, new(Vector4.Zero, Vector4.One, Vector4.Zero, Vector4.Zero, 0), Vector3.One, 1, false, false),
            accepted.RenderingStyles.Select(value => new D3D9ClassicModelPassInputs(value.ObjectId,
                accepted.SceneInputs[value.ObjectId].SourceOutlineOptions)));
        var encoder = new D3D9SurfaceFrameEncoder(bindings, configuration, mode);
        encoder.UseClassicPassInputs(Passes);
        var frame = Capture(project, encoder) with
            { ObjectMaterialInputs = project.CaptureRenderSnapshot(encoder.SceneInputRequests).ObjectMaterialInputs };
        var before = project.Frame; double clock = project.CurrentFrame;
        var preflight = new D3D9SurfacePreflightInputs(bindings, configuration, Passes, mode);

        preflight.ValidateEmmFrame(frame, Request, Vector4.One);
        var packet = encoder.Serialize(frame, Request, Vector4.One);
        int start = packet.Frame.AsSpan().LastIndexOf("MDA9MED1"u8);
        Assert.True(start > 0);
        Assert.Equal(7.5, BinaryPrimitives.ReadDoubleLittleEndian(packet.Frame.AsSpan(start + 12)));
        Assert.Equal((uint)mode, U(packet.Frame, start + 24));
        Assert.Equal(0U, U(packet.Frame, start + 28));
        Assert.Throws<InvalidOperationException>(() => encoder.Serialize(frame, Request, Vector4.One, new(frame, [])));
        Assert.Throws<InvalidOperationException>(() => encoder.Serialize(frame, Request, Vector4.One,
            new(frame, [], D3D9ClassicScreenCaptureMode.FullFrame)));
        Assert.Throws<InvalidOperationException>(() => encoder.Serialize(frame, Request, Vector4.One, new(frame with { }, [], mode)));
        Assert.Same(before, project.Frame); Assert.Equal(clock, project.CurrentFrame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AccessoryLightMatchesOriginalPassAmbientWithoutClampingOrChangingSpecular(bool authoredLight)
    {
        using var project = Project(true, "", out var binding);
        var encoder = Encoder(project, binding);
        var frame = Capture(project, encoder) with
        { Light = authoredLight ? new PmmLightPose(new(.1f, .6f, .9f), new(0, -1, 0)) : null };
        var rawAmbient = frame.Light?.Color ?? frame.Scene.Surface.Profile.LightAmbient;
        var rawSpecular = frame.Light?.Color ?? frame.Scene.Surface.Profile.LightSpecular;
        var packets = encoder.Serialize(frame, Request, Vector4.One);
        using var reader = new BinaryReader(new MemoryStream(packets.Visual));
        reader.BaseStream.Position = 128;
        // Pinned original accessory pass subtracts 0.300000011920929f from
        // SceneLight.Ambient. Its diffuse is 10; specular keeps the raw light.
        var expectedAmbient = rawAmbient - new Vector3(.300000011920929f);
        Assert.Equal(expectedAmbient.X, reader.ReadSingle());
        Assert.Equal(expectedAmbient.Y, reader.ReadSingle());
        Assert.Equal(expectedAmbient.Z, reader.ReadSingle());
        for (int i = 0; i < 3; i++) Assert.Equal(10f, reader.ReadSingle());
        Assert.Equal(rawSpecular.X, reader.ReadSingle());
        Assert.Equal(rawSpecular.Y, reader.ReadSingle());
        Assert.Equal(rawSpecular.Z, reader.ReadSingle());
        reader.BaseStream.Position = 164 + 16;
        Assert.Equal(expectedAmbient.X + .02f, reader.ReadSingle());
        Assert.Equal(.5f * expectedAmbient.Y + .03f, reader.ReadSingle());
        Assert.Equal(.25f * expectedAmbient.Z + .04f, reader.ReadSingle());
        if (authoredLight) Assert.True(expectedAmbient.X < 0);
    }

    private static SceneProjectSession Project(bool visible, string texture, out D3D9SurfaceObjectBinding binding)
    {
        string x = $$$"""
            xof 0303txt 0032
            Mesh mesh { 3; 0;0;0;,1;0;0;,0;1;0;; 1; 3;0,1,2;;
              MeshNormals { 1;0;0;1;;1;3;0,0,0;; }
              MeshTextureCoords {3;0;0;,1;0;,0;1;;}
              MeshMaterialList {1;1;0;; Material mat {1;0.5;0.25;0.8;;8;0.1;0.2;0.3;;0.02;0.03;0.04;; TextureFilename {"{{{texture}}}";} }} }
            """;
        var raw = XTextReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(x)), new() { AnsiCodePage = 65001 });
        var source = XAccessoryAdapter.FromDocument(raw).Frame;
        var asset = new PreparedPmmAccessory(7, "part.x", new string('a', 64), source, []) { Document = raw };
        var key = new PmmAccessoryKeyframe { Scale = 1.7f, Translation = new(2, -3, 4), Rotation = new(.31f, -.47f, .63f),
            OpacityAndVisibility = PmmTrackSampling.PackOpacity(.5f, visible), ParentModelIndex = -1, ShadowEnabled = true };
        var model = SurfaceFrameEncoderTests.AccessoryCarrierModel();
        var layout = new SceneCompositionEditor();
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 0, false));
        var carrier = layout.Add("carrier", "carrier.pmx", ModelRole.Unspecified, rest);
        PmmString S(string text) => new(0, text, []);
        var pmm = new PmmDocument { AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 64, OutputHeight = 32, CameraFov = 30,
            Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
            Models = [new() { Index = 42, Name = S("carrier"), Path = S("carrier.pmx"), DrawOrderIndex = 1,
                BoneNames = [S("root")], SelectedMorphIndices = [-1, -1, -1, -1],
                InitialBoneKeyframes = [new() { Rotation = Quaternion.Identity, Interpolation = new byte[16] }],
                BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)],
                InitialModelKeyframe = new() { Visible = false }, Visible = false }],
            Settings = new() { AccessoryIndexAfterModels = -1, PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0],
                UnknownMatrix = new byte[64], SelectionTrailerFlag = 0, CameraLookAtModelIndex = -1, CameraLookAtBoneIndex = -1 },
            AccessoryNames = [new(0, "part", [])], Accessories = [new() { Index = 7,
            Name = new(0, "part", []), Path = new(0, "part.x", []), InitialKeyframe = key, ParentModelIndex = -1, Scale = 1 }] };
        var id = Guid.NewGuid(); binding = D3D9SurfaceObjectBinding.CreateAccessory(id, 42, "part.x", "part");
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30 }],
            IkKeyframes = [new() { Visible = false }] };
        return new(layout, new(), [new(carrier.Id, model, motion, PhysicsEnabled: false)], provider, initialFrame: 7.5,
            pmmControls: ScenePmmControlState.Create(pmm, [new(carrier.Id, 42, model)]), accessories: [new(id, asset)]);
    }

    private static OfflineFrame Capture(SceneProjectSession project, D3D9SurfaceFrameEncoder encoder)
    {
        var s = project.CaptureRenderSnapshot(encoder.SceneInputRequests);
        return new(new(0, new(0, 1), new(15, 2), new(15, 2)), 64, 32, s.Scene, s.Camera, s.Light, s.SelfShadow)
        { ResolvedCamera = s.ResolvedCamera, CameraParentTransform = s.CameraParentTransform, MorphChannels = s.MorphChannels,
            RenderingStyles = s.RenderingStyles, SceneInputs = s.SceneInputs, Accessories = s.Accessories,
            MixedDrawOrder = s.MixedDrawOrder, AccessoryIndexAfterModels = s.AccessoryIndexAfterModels,
            UnpreparedAccessoryIndices = s.UnpreparedAccessoryIndices, AccessoryParentBoneWorldMatrices = s.AccessoryParentBoneWorldMatrices };
    }

    private static D3D9SurfaceFrameEncoder Encoder(SceneProjectSession project, D3D9SurfaceObjectBinding accessory)
        => new([new(Assert.Single(project.Objects).Id, CarrierNativeId, "carrier.pmx", "carrier", ["root"]), accessory]);

    private static void SkipHiddenCarrier(BinaryReader reader)
    {
        Assert.Equal(CarrierNativeId, reader.ReadUInt64()); Assert.Equal(1u, reader.ReadUInt32()); Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(0u, reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32());
        SkipString(reader); SkipString(reader); Assert.Equal(0u, reader.ReadUInt32()); Assert.Equal(0u, reader.ReadUInt32());
    }
    private static uint U(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void SkipString(BinaryReader reader) { uint count = reader.ReadUInt32(); reader.BaseStream.Position += count; }
    private static float ReadFloat(BinaryReader reader, int skipFloats) { reader.BaseStream.Position += skipFloats * 4; return reader.ReadSingle(); }
}
