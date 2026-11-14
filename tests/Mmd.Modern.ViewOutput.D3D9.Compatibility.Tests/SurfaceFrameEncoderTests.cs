using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed partial class SurfaceFrameEncoderTests
{
    private static readonly ViewRenderRequest Request = new(64, 32);
    private const ulong FirstId = 0x1234567887654321, SecondId = 0xfedcba9876543210;

    [Fact]
    public void BindingsFreezeBoneNamesAndRejectDuplicateOrInvalidIdentities()
    {
        string[] names = ["root", "目"];
        var binding = new D3D9SurfaceObjectBinding(Guid.NewGuid(), FirstId, "模型/model.pmx", "actor", names);
        names[0] = "changed";
        Assert.Equal("root", binding.BoneNames[0]);
        Assert.Throws<ArgumentException>(() => new D3D9SurfaceFrameEncoder([binding, binding]));
        Assert.Throws<ArgumentException>(() => new D3D9SurfaceFrameEncoder([binding,
            new(Guid.NewGuid(), FirstId, "other.pmx", "other")]));
        Assert.Throws<ArgumentException>(() => new D3D9SurfaceObjectBinding(Guid.Empty, 1, "a.pmx", "a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new D3D9SurfaceObjectBinding(Guid.NewGuid(), 0, "a.pmx", "a"));
        Assert.Throws<ArgumentException>(() => new D3D9SurfaceObjectBinding(Guid.NewGuid(), 1, "a\0.pmx", "a"));
    }

    [Fact]
    public void FractionalSampleUsesSampledSourceTimeAndLeavesOwnerClockAndFrameUntouched()
    {
        using var project = Project(out var encoder);
        var before = project.Frame; double clock = project.CurrentFrame;
        var frame = Capture(project, encoder) with { Sample = new(17, new(17, 60), new(11, 3), new(7, 2)) };
        var packets = encoder.Serialize(frame, Request, Vector4.One);
        Assert.Equal(3.5, BinaryPrimitives.ReadDoubleLittleEndian(packets.Frame.AsSpan(40)));
        Assert.Equal((float)(3.5 / 30), BinaryPrimitives.ReadSingleLittleEndian(packets.Frame.AsSpan(48)));
        Assert.Equal(SHA256.HashData(packets.Frame), packets.Visual.AsSpan(20, 32).ToArray());
        Assert.Equal(3.5, encoder.Encode(frame, Request, Vector4.One).SourceFrame);
        Assert.Same(before, project.Frame); Assert.Equal(clock, project.CurrentFrame);
    }

    [Fact]
    public void MultipleObjectsKeepFixed64BitIdsMaterialTextureOwnershipAndRawNonuniformNormals()
    {
        using var project = Project(out var encoder);
        var frame = Capture(project, encoder); var packets = encoder.Serialize(frame, Request, Vector4.One);
        var parsed = ParseFrame(packets.Frame);
        Assert.Equal(new[] { FirstId, SecondId }, parsed.ObjectIds);
        Assert.Equal(new[] { FirstId, SecondId }, parsed.MaterialIds);
        Assert.Equal(new[] { 0, 1 }, parsed.DiffuseTextures);
        int normals = 128 + 132 * 2;
        var source = frame.SceneInputs[project.Objects[0].Id].SourceGeometry!;
        Matrix4x4.Invert(source.World, out var inverse);
        var expected = Vector3.TransformNormal(source.GetVertex(0).Normal, Matrix4x4.Transpose(inverse));
        Assert.Equal(expected, ReadVector(packets.Visual, normals));
        Assert.Equal(new Vector3(2, 3, 4), ReadVector(packets.Visual, normals + 12));
        Assert.NotEqual(Vector3.Normalize(expected), ReadVector(packets.Visual, normals));
        Assert.Equal(new Vector3(-2, 1, 0), ReadVector(packets.Visual, normals + 3 * 24 + 12));
        // Binding enumeration never renumbers logical objects or changes packet object ordering.
        var reordered = new D3D9SurfaceFrameEncoder(project.Objects.Select((item, i) =>
            new D3D9SurfaceObjectBinding(item.Id, i == 0 ? FirstId : SecondId, item.SourcePath, item.Name)).Reverse());
        Assert.Equal(packets.Frame, reordered.Serialize(frame, Request, Vector4.One).Frame);
    }

    [Theory]
    [InlineData(OriginalSphereMode.Multiply)]
    [InlineData(OriginalSphereMode.Add)]
    public void ActiveSphereOnPlacedGeometryIsRejectedWhileIdentityAndDisabledSurfacesRemainSupported(OriginalSphereMode mode)
    {
        using var project = Project(out var encoder);
        var frame = Capture(project, encoder);
        Assert.Throws<NotSupportedException>(() => encoder.Encode(WithSphere(frame, 0, mode), Request, Vector4.One));
        // The second instance has identity SourceGeometry.World, independently of the first instance's placement.
        Assert.Equal(0, encoder.Encode(WithSphere(frame, 1, mode), Request, Vector4.One).SourceFrame);
        var disabled = WithSphere(frame, 0, mode);
        var flags = disabled.Scene.Materials.ToArray(); flags[0] = flags[0] with { SurfaceEnabled = false };
        var scene = new OriginalFrameScene(disabled.Scene.Surface, disabled.Scene.Outline, disabled.Scene.OutlineOptions,
            disabled.Scene.Options, flags, disabled.Scene.Lighting, disabled.Scene.EditorOverlay);
        Assert.Equal(0, encoder.Encode(disabled with { Scene = scene }, Request, Vector4.One).SourceFrame);
    }

    [Fact]
    public void ZeroTriangleSphereMaterialDoesNotRejectPlacedGeometry()
    {
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("empty-sphere", "empty-sphere.pmx", ModelRole.Unspecified,
            Triangle("empty-sphere", new(2, 3, 4), Matrix4x4.CreateScale(2, 3, 4), includeEmptySphere: true));
        using var project = new SceneProjectSession(layout, new(), [], Provider());
        var encoder = new D3D9SurfaceFrameEncoder([new(actor.Id, FirstId, actor.SourcePath, actor.Name)]);
        Assert.Equal(0, encoder.Encode(Capture(project, encoder), Request, Vector4.One).SourceFrame);
    }

    [Fact]
    public void MissingSourceInputsWrongObjectCoverageAndInvalidMaterialRangesAreRejected()
    {
        using var project = Project(out var encoder);
        var frame = Capture(project, encoder);
        Assert.Throws<ArgumentException>(() => encoder.Encode(frame with { SceneInputs = SceneObjectRenderInputs.Empty }, Request, Vector4.One));
        var noGeometry = project.CaptureRenderSnapshot(project.Objects.Select(item => new SceneObjectRenderRequest(item.Id)));
        Assert.Throws<NotSupportedException>(() => encoder.Encode(frame with { SceneInputs = noGeometry.SceneInputs }, Request, Vector4.One));
        var badRows = frame.RenderingStyles.ToArray(); badRows[1] = badRows[1] with { MaterialStart = 0 };
        Assert.Throws<ArgumentException>(() => encoder.Encode(frame with { RenderingStyles = badRows }, Request, Vector4.One));
        var missing = new D3D9SurfaceFrameEncoder([new(project.Objects[0].Id, FirstId, "a.pmx", "a")]);
        Assert.Throws<ArgumentException>(() => missing.Encode(frame, Request, Vector4.One));
        var extra = new D3D9SurfaceFrameEncoder(project.Objects.Select((item, i) => new D3D9SurfaceObjectBinding(item.Id,
            i == 0 ? FirstId : SecondId, item.SourcePath, item.Name)).Append(new(Guid.NewGuid(), 3, "extra.pmx", "extra")));
        Assert.Throws<ArgumentException>(() => extra.Encode(frame, Request, Vector4.One));
    }

    [Fact]
    public void ParentCameraUsesFrozenParentResolvedCameraWinsAndMissingParentIsRejected()
    {
        using var project = Project(out var encoder);
        var pose = new PmmCameraPose(new(1, 2, -8), new(.1f, .2f, .3f), -40, 43, true, ParentModelIndex: 0);
        var parent = Matrix4x4.CreateRotationY(.4f) * Matrix4x4.CreateTranslation(6, 7, 8);
        var frame = Capture(project, encoder) with { Camera = pose, CameraParentTransform = parent };
        var packets = encoder.Serialize(frame, Request, Vector4.One); var camera = pose.CreateCamera(parent);
        Assert.Equal(camera.Eye, ReadVector(packets.Visual, 52));
        var expectedView = Matrix4x4.CreateLookAtLeftHanded(camera.Eye, camera.Target, camera.Up);
        Assert.Equal(expectedView, ReadMatrix(packets.Frame, 52));
        Assert.Throws<NotSupportedException>(() => encoder.Encode(frame with { CameraParentTransform = null }, Request, Vector4.One));
        var resolved = SceneCamera.Default with { Eye = new(11, 12, -20) };
        var preferred = encoder.Serialize(frame with { ResolvedCamera = resolved, CameraParentTransform = null }, Request, Vector4.One);
        Assert.Equal(resolved.Eye, ReadVector(preferred.Visual, 52));
    }

    [Fact]
    public void SceneCameraOverrideAndNonfiniteCameraAreHandled()
    {
        using var project = Project(out var encoder);
        var frame = Capture(project, encoder); var selected = SceneCamera.Default with { Eye = new(7, 9, -25) };
        var scene = new OriginalFrameScene(frame.Scene.Surface, options: new(OriginalAlphaBlendMode.Normal, true, false, CameraOverride: selected),
            materials: frame.Scene.Materials);
        Assert.Equal(selected.Eye, ReadVector(encoder.Serialize(frame with { Scene = scene }, Request, Vector4.One).Visual, 52));
        Assert.Throws<ArgumentException>(() => encoder.Encode(frame with { ResolvedCamera = selected with { Eye = new(float.NaN, 0, 0) } }, Request, Vector4.One));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenActorRetainsItsNativeIdAndReappearsWithoutReassigningTheOtherActor(bool placedSphere)
    {
        var model = Model(); var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 1));
        if (placedSphere)
        {
            var placed = Triangle("animated", Vector3.UnitZ, Matrix4x4.CreateScale(2));
            rest = new(new(placed.Surface.Snapshot, [new(Vector3.One, 0, OriginalSphereMode.Multiply)]), placed.Outline, placed.OutlineOptions);
        }
        var layout = new SceneCompositionEditor(); var first = layout.Add("animated", "animated.pmx", ModelRole.Unspecified, rest);
        var second = layout.Add("static", "static.pmx", ModelRole.Unspecified, Triangle("static", new(-2, 1, 0), Matrix4x4.Identity));
        var motion = new VmdMotion { IkKeyframes = [new() { Frame = 0, Visible = true }, new() { Frame = 1, Visible = false }, new() { Frame = 2, Visible = true }] };
        using var project = new SceneProjectSession(layout, new(), [new(first.Id, model, motion, PhysicsEnabled: false)], Provider());
        var encoder = new D3D9SurfaceFrameEncoder([new(first.Id, FirstId, first.SourcePath, first.Name, ["root"]),
            new(second.Id, SecondId, second.SourcePath, second.Name)]);
        project.Seek(1, TestContext.Current.CancellationToken);
        var hidden = ParseFrame(encoder.Serialize(Capture(project, encoder), Request, Vector4.One).Frame);
        Assert.Equal(new[] { FirstId, SecondId }, hidden.ObjectIds); Assert.Equal(new[] { false, true }, hidden.Visible);
        Assert.Equal(new[] { SecondId }, hidden.MaterialIds);
        project.Seek(2, TestContext.Current.CancellationToken);
        if (placedSphere) Assert.Throws<NotSupportedException>(() => encoder.Encode(Capture(project, encoder), Request, Vector4.One));
        else Assert.Equal(new[] { FirstId, SecondId }, ParseFrame(encoder.Serialize(Capture(project, encoder), Request, Vector4.One).Frame).MaterialIds);
    }

    private static SceneProjectSession Project(out D3D9SurfaceFrameEncoder encoder)
    {
        var layout = new SceneCompositionEditor();
        var first = layout.Add("one", "one.pmx", ModelRole.Unspecified,
            Triangle("one", new(2, 3, 4), Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(2, 0, 0)));
        var second = layout.Add("two", "two.pmx", ModelRole.Unspecified, Triangle("two", new(-2, 1, 0), Matrix4x4.Identity));
        encoder = new([new(first.Id, FirstId, first.SourcePath, first.Name), new(second.Id, SecondId, second.SourcePath, second.Name)]);
        return new(layout, new(), [], Provider());
    }
    private static ProjectSettingsBackendProvider Provider() => new((_, _) => throw new InvalidOperationException("No physics in encoder tests."),
        _ => throw new InvalidOperationException("No shared physics in encoder tests."));
    private static OfflineFrame Capture(SceneProjectSession project, D3D9SurfaceFrameEncoder encoder)
    {
        var snapshot = project.CaptureRenderSnapshot(encoder.SceneInputRequests);
        return new(new(0, new(0, 1), new((long)project.CurrentFrame, 1), new((long)project.CurrentFrame, 1)), Request.Width, Request.Height,
            snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
        { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ResolvedCamera = snapshot.ResolvedCamera,
            CameraParentTransform = snapshot.CameraParentTransform, MorphChannels = snapshot.MorphChannels };
    }
    private static OfflineFrame WithSphere(OfflineFrame frame, int globalMaterial, OriginalSphereMode mode)
    {
        var bindings = frame.Scene.Surface.Materials.ToArray();
        bindings[globalMaterial] = bindings[globalMaterial] with
        { SphereMode = mode, SphereTexture = frame.Scene.Surface.Snapshot.Materials[globalMaterial].DiffuseTexture };
        var scene = new OriginalFrameScene(new(frame.Scene.Surface.Snapshot, bindings, frame.Scene.Surface.Profile),
            frame.Scene.Outline, frame.Scene.OutlineOptions, frame.Scene.Options, frame.Scene.Materials, frame.Scene.Lighting, frame.Scene.EditorOverlay);
        return frame with { Scene = scene };
    }
    private static OriginalFrameScene Triangle(string name, Vector3 normal, Matrix4x4 world, bool includeEmptySphere = false)
    {
        SceneVertex[] vertices = [new(Vector3.Zero, normal, Vector2.Zero), new(Vector3.UnitX, normal, Vector2.UnitX), new(Vector3.UnitY, normal, Vector2.UnitY)];
        var texture = new SceneTexture(name, 1, 1, [255, 128, 64, 255]);
        var material = new SceneMaterial(name, 0, 3, Vector4.One, Vector3.One, new(.1f), 5, 0);
        SceneMaterial[] materials = includeEmptySphere ? [material with { Name = "empty-sphere", IndexCount = 0 }, material] : [material];
        OriginalStyleMaterialBinding[] bindings = includeEmptySphere
            ? [new(new(.5f), 0, OriginalSphereMode.Multiply), new(new(.5f))] : [new(new(.5f))];
        var snapshot = new SceneSnapshot(vertices, [0, 1, 2], materials, [texture], world: world);
        var outline = new OriginalOutlineData(vertices.Select(vertex => new OriginalOutlineVertex(vertex.Position, vertex.Normal,
            OriginalOutlineVertexMode.Expand, 1, includeEmptySphere ? 1 : 0)),
            includeEmptySphere ? [new(.1f, Vector4.One, false), new(.1f, Vector4.One, false)] : [new(.1f, Vector4.One, false)]);
        return new(new(snapshot, bindings), outline, new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 1, false));
    }
    private static PmxModel Model() => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "actor", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex { Position = position, Normal = Vector3.UnitZ,
            Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
        Faces = [new(0, 1, 2)], Textures = [], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One,
            Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero,
            EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
            ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
        Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
    };
    private sealed record Parsed(ulong[] ObjectIds, bool[] Visible, ulong[] MaterialIds, int[] DiffuseTextures);
    private static Parsed ParseFrame(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        reader.BaseStream.Position = 20; int objects = reader.ReadInt32(), vertices = reader.ReadInt32(), indices = reader.ReadInt32(), materials = reader.ReadInt32();
        var objectIds = new ulong[objects]; var visible = new bool[objects]; reader.BaseStream.Position = 208;
        for (int i = 0; i < objects; i++)
        {
            objectIds[i] = reader.ReadUInt64(); reader.ReadInt32(); reader.ReadInt32(); visible[i] = reader.ReadInt32() != 0;
            SkipString(reader); SkipString(reader); int bones = reader.ReadInt32();
            for (int b = 0; b < bones; b++) { SkipString(reader); reader.BaseStream.Position += 64; }
            int morphs = reader.ReadInt32(); for (int m = 0; m < morphs; m++) { SkipString(reader); reader.ReadSingle(); }
        }
        reader.BaseStream.Position += vertices * 32 + indices * 4;
        var materialIds = new ulong[materials]; var textures = new int[materials];
        for (int i = 0; i < materials; i++)
        { materialIds[i] = reader.ReadUInt64(); reader.BaseStream.Position += 60; textures[i] = reader.ReadInt32(); reader.BaseStream.Position += 12; }
        return new(objectIds, visible, materialIds, textures);
    }
    private static void SkipString(BinaryReader reader) { int count = reader.ReadInt32(); reader.BaseStream.Position += count; }
    private static Vector3 ReadVector(byte[] bytes, int offset) => new(BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset)),
        BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 4)), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 8)));
    private static Matrix4x4 ReadMatrix(byte[] bytes, int offset)
    {
        var values = Enumerable.Range(0, 16).Select(i => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + i * 4))).ToArray();
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
    }
}
