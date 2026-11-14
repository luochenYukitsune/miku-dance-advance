using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed partial class SurfaceFrameEncoderTests
{
    [Fact]
    public void EmmMetadataInsertsHiddenMaterialsBetweenVisibleObjectsWithoutShiftingDrawGeometry()
    {
        using var project = MaterialProject(out var hiddenId);
        var bindings = MaterialBindings(project);
        var ordinary = new D3D9SurfaceFrameEncoder(bindings);
        var encoder = new D3D9SurfaceFrameEncoder(bindings, MaterialConfiguration(bindings));
        Assert.All(encoder.SceneInputRequests, value => Assert.True(value.IncludeMaterialInputs));
        project.Seek(5, TestContext.Current.CancellationToken); project.Advance(.25 / 30, TestContext.Current.CancellationToken);
        var frame = MaterialCapture(project, encoder);
        var before = project.Frame; double clock = project.CurrentFrame;
        var packets = encoder.Serialize(frame, Request, Vector4.One);
        var old = ordinary.Serialize(frame, Request, Vector4.One);
        var parsed = ReadMetadataMaterials(packets.Frame);
        Assert.Equal(4U, U32(packets.Frame, 8)); Assert.Equal(5U, U32(packets.Visual, 8));
        Assert.Equal(4U, U32(packets.Frame, 208)); Assert.Equal(4U, U32(packets.Frame, 32));
        Assert.Equal(new[] { 0, 1, 3 }, parsed.Objects.Select(value => value.MaterialStart));
        Assert.Equal(new[] { 1, 2, 1 }, parsed.Objects.Select(value => value.MaterialCount));
        Assert.Equal(new[] { 3, 0, 3 }, parsed.Objects.Select(value => value.VertexCount));
        Assert.Equal(new[] { true, false, true }, parsed.Objects.Select(value => value.Visible));
        Assert.Equal(new[] { 0, 3, 3, 3 }, parsed.Materials.Select(value => value.DrawStart));
        Assert.Equal(new[] { 3, 0, 0, 3 }, parsed.Materials.Select(value => value.DrawCount));
        Assert.Equal(new[] { 0, 0, 1, 0 }, parsed.Materials.Select(value => value.LocalIndex));
        Assert.Equal(new[] { 0, 2, 2, 1 }, parsed.Materials.Select(value => value.Texture));
        Assert.Equal(3U, U32(packets.Frame, 36));
        Assert.Equal(6U, U32(packets.Frame, 24)); Assert.Equal(6U, U32(packets.Frame, 28));
        int oldVertexOffset = LegacyVertices(old.Frame);
        Assert.Equal(old.Frame.AsSpan(oldVertexOffset, 6 * 32 + 6 * 4).ToArray(),
            packets.Frame.AsSpan(parsed.VertexOffset, 6 * 32 + 6 * 4).ToArray());
        var hidden = frame.ObjectMaterialInputs[hiddenId];
        Assert.Equal(hidden.Materials.Select(value => value.Diffuse), parsed.Materials.Skip(1).Take(2).Select(value => value.Diffuse));
        Assert.Equal(SHA256.HashData(packets.Frame), packets.Visual.AsSpan(20, 32).ToArray());
        Assert.Equal(164 + 4 * 148 + 6 * 24, packets.Visual.Length);
        Assert.Equal(old.Visual.AsSpan(128, 132).ToArray(), packets.Visual.AsSpan(164, 132).ToArray());
        Assert.Equal(old.Visual.AsSpan(128 + 132, 132).ToArray(), packets.Visual.AsSpan(164 + 3 * 148, 132).ToArray());
        Assert.Equal(D3D9SurfaceScope.ConfiguredEmm, encoder.Encode(frame, Request, Vector4.One).Scope);
        Assert.Equal(clock, project.CurrentFrame); Assert.Same(before, project.Frame);
    }

    [Fact]
    public void HiddenMaterialVisualValuesUseTheSameResolvedMaterialConversionAsVisibleOracle()
    {
        using var hidden = MaterialProject(out var hiddenId);
        using var visible = MaterialProject(out var visibleId, hide: false);
        var hiddenBindings = MaterialBindings(hidden); var visibleBindings = MaterialBindings(visible);
        var hiddenEncoder = new D3D9SurfaceFrameEncoder(hiddenBindings, MaterialConfiguration(hiddenBindings));
        var visibleEncoder = new D3D9SurfaceFrameEncoder(visibleBindings, MaterialConfiguration(visibleBindings));
        hidden.Seek(5, TestContext.Current.CancellationToken); hidden.Advance(.25 / 30, TestContext.Current.CancellationToken);
        visible.Seek(5, TestContext.Current.CancellationToken); visible.Advance(.25 / 30, TestContext.Current.CancellationToken);
        var hiddenFrame = MaterialCapture(hidden, hiddenEncoder); var visibleFrame = MaterialCapture(visible, visibleEncoder);
        Assert.Equal(hiddenFrame.ObjectMaterialInputs[hiddenId].Materials, visibleFrame.ObjectMaterialInputs[visibleId].Materials);
        var hp = hiddenEncoder.Serialize(hiddenFrame, Request, Vector4.One);
        var vp = visibleEncoder.Serialize(visibleFrame, Request, Vector4.One);
        // Independent scene Guids share only the explicit native identity. Compare complete
        // VIS material records, including original colors, factors, alpha and raw diffuse.
        Assert.Equal(vp.Visual.AsSpan(164 + 148, 2 * 148).ToArray(), hp.Visual.AsSpan(164 + 148, 2 * 148).ToArray());
        var retained = hp.Frame.ToArray(); hidden.Seek(10, TestContext.Current.CancellationToken);
        Assert.Equal(retained, hp.Frame);
        Assert.Equal(5.25, hiddenFrame.ObjectMaterialInputs[hiddenId].LocalFrame);
        Assert.False(hiddenFrame.ObjectMaterialInputs[hiddenId].Visible);
    }

    [Fact]
    public void EmmRequiresExactClockIdentityAndCompleteSidecarsWhileOrdinaryScopeKeepsLegacyPackets()
    {
        using var project = MaterialProject(out var hiddenId);
        var bindings = MaterialBindings(project); var ordinary = new D3D9SurfaceFrameEncoder(bindings);
        var emm = new D3D9SurfaceFrameEncoder(bindings, MaterialConfiguration(bindings));
        project.Seek(5, TestContext.Current.CancellationToken); var frame = MaterialCapture(project, emm);
        Assert.Throws<InvalidDataException>(() => emm.Serialize(frame with { ObjectMaterialInputs = SceneObjectMaterialInputs.Empty }, Request, Vector4.One));
        Assert.Throws<InvalidDataException>(() => emm.Serialize(frame with { Sample = frame.Sample with { SampledSourceFrame = new(6, 1) } }, Request, Vector4.One));
        var stale = frame.ObjectMaterialInputs;
        project.Seek(10, TestContext.Current.CancellationToken); var shown = MaterialCapture(project, emm);
        Assert.Throws<InvalidDataException>(() => emm.Serialize(shown with { ObjectMaterialInputs = stale }, Request, Vector4.One));
        var swapped = frame.ObjectMaterialInputs.ToDictionary(pair => pair.Key, pair => pair.Value);
        swapped[hiddenId] = swapped.First(pair => pair.Key != hiddenId).Value;
        Assert.Throws<InvalidDataException>(() => emm.Serialize(frame with { ObjectMaterialInputs = swapped }, Request, Vector4.One));
        var oldWith = ordinary.Serialize(frame, Request, Vector4.One);
        var oldWithout = ordinary.Serialize(frame with { ObjectMaterialInputs = SceneObjectMaterialInputs.Empty }, Request, Vector4.One);
        Assert.Equal(1U, U32(oldWith.Frame, 8)); Assert.Equal(oldWithout.Frame, oldWith.Frame); Assert.Equal(oldWithout.Visual, oldWith.Visual);
    }

    [Fact]
    public async Task PreviewAndOfflineProjectionRetainTheExactDetachedMaterialTable()
    {
        using var project = MaterialProject(out _);
        var bindings = MaterialBindings(project); var encoder = new D3D9SurfaceFrameEncoder(bindings, MaterialConfiguration(bindings));
        project.Seek(5, TestContext.Current.CancellationToken); project.Advance(.25 / 30, TestContext.Current.CancellationToken);
        var snapshot = project.CaptureRenderSnapshot(encoder.SceneInputRequests);
        var preview = new PreviewFrame(64, 32, snapshot.SourceFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
        { SceneInputs = snapshot.SceneInputs, RenderingStyles = snapshot.RenderingStyles, ObjectMaterialInputs = snapshot.ObjectMaterialInputs };
        var offline = D3D9SurfaceFrameInput.FromPreview(preview);
        Assert.Same(snapshot.ObjectMaterialInputs, offline.ObjectMaterialInputs);
        Assert.Equal(5.25, offline.Sample.SampledSourceFrame.ToDouble());
        Assert.Equal(5.25, encoder.Encode(offline, Request, Vector4.One).SourceFrame);
        int captures = 0;
        await OfflineFrameRunner.RunProjectAsync(project, new(64, 32, new(30), 5, 6, includeEndFrame: false), (frame, token) =>
        {
            Assert.Equal(3, frame.ObjectMaterialInputs.Count);
            Assert.All(frame.ObjectMaterialInputs.Values, value => Assert.Equal(frame.Sample.SampledSourceFrame.ToDouble(), value.SourceFrame));
            _ = encoder.Encode(frame, Request, Vector4.One); captures++;
            return ValueTask.CompletedTask;
        }, sceneInputRequests: encoder.SceneInputRequests, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, captures);
    }

    private static SceneProjectSession MaterialProject(out Guid hiddenId, bool hide = true)
    {
        var layout = new SceneCompositionEditor();
        layout.Add("first", "first.pmx", ModelRole.Unspecified, Triangle("same-name", Vector3.UnitZ, Matrix4x4.Identity));
        var model = MaterialModel();
        var texture = new SceneTexture("same-name", 1, 1, [255, 128, 64, 255]);
        SceneVertex[] vertices = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero), new(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX), new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY)];
        var scene = new SceneSnapshot(vertices, [0, 1, 2],
            [new("hidden-a", 0, 3, new(.4f, .6f, .8f, 1), new(.2f), new(.1f), 5, 0),
             new("hidden-b", 3, 0, new(.7f, .3f, .2f, .6f), new(.4f), new(.2f), 9, 0)], [texture]);
        var rest = new OriginalFrameScene(new(scene, [new(new(.3f)), new(new(.6f))]),
            PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 1, false));
        var actor = layout.Add("hidden", "hidden.pmx", ModelRole.Unspecified, rest); hiddenId = actor.Id;
        layout.Add("last", "last.pmx", ModelRole.Unspecified, Triangle("same-name", Vector3.UnitZ, Matrix4x4.Identity));
        var motion = new VmdMotion
        { IkKeyframes = [new() { Visible = !hide }, new() { Frame = 10, Visible = true }],
          MorphKeyframes = [new() { MorphName = "tint", Weight = 0 }, new() { MorphName = "tint", Frame = 10, Weight = 1 }] };
        return new(layout, new(), [new(actor.Id, model, motion, PhysicsEnabled: false)], Provider());
    }

    private static PmxModel MaterialModel()
    {
        var model = Model();
        PmxMaterial Material(int count) => new()
        { Name = "material", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.One, SpecularPower = 5, Ambient = Vector3.Zero,
          DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1,
          SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = count };
        return new()
        { Header = model.Header, Name = model.Name, EnglishName = "", Comment = "", EnglishComment = "", Vertices = model.Vertices,
          Faces = model.Faces, Textures = [], Materials = [Material(3), Material(0)], Bones = model.Bones,
          Morphs = [new() { Name = "tint", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
              MaterialOffsets = [new(-1, PmxMaterialMorphOperation.Add, new(.2f, -.1f, .3f, 0), new(.1f), 2, new(.05f), Vector4.Zero, 0,
                  new(.1f), new(.2f), new(.3f))] }], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [] };
    }

    private static D3D9SurfaceObjectBinding[] MaterialBindings(SceneProjectSession project) => project.Objects.Select((item, i) =>
        new D3D9SurfaceObjectBinding(item.Id, (ulong)(i + 10), item.SourcePath, item.Name)).ToArray();
    private static D3D9EmmRuntimeConfiguration MaterialConfiguration(IReadOnlyList<D3D9SurfaceObjectBinding> bindings)
    {
        var document = EmmReader.Read(Encoding.ASCII.GetBytes("[Info]\nVersion=3\n[Object]\n[Effect]\nDefault=none\n"));
        var plan = EmmImportPlan.Create(document, @"D:\source\scene.emm",
            bindings.Select((value, i) => new EmmObjectBinding(value.ObjectId, true, value.SourcePath, i)), @"D:\source");
        return D3D9EmmRuntimeConfiguration.Create(plan, bindings.Select(value => new D3D9EmmRuntimeRootSeed(value.ObjectId, true)), new D3D9EffectResourceMap([]));
    }
    private static OfflineFrame MaterialCapture(SceneProjectSession project, D3D9SurfaceFrameEncoder encoder)
    {
        var snapshot = project.CaptureRenderSnapshot(encoder.SceneInputRequests);
        return D3D9SurfaceFrameInput.FromPreview(new(64, 32, snapshot.SourceFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
        { RenderingStyles = snapshot.RenderingStyles, SceneInputs = snapshot.SceneInputs, ObjectMaterialInputs = snapshot.ObjectMaterialInputs,
          ResolvedCamera = snapshot.ResolvedCamera, MixedDrawOrder = snapshot.MixedDrawOrder, AccessoryIndexAfterModels = snapshot.AccessoryIndexAfterModels });
    }
    private sealed record MetadataObject(int MaterialStart, int MaterialCount, int VertexCount, bool Visible);
    private sealed record MetadataMaterial(int LocalIndex, int DrawStart, int DrawCount, Vector4 Diffuse, int Texture);
    private sealed record MetadataPacket(MetadataObject[] Objects, MetadataMaterial[] Materials, int VertexOffset);
    private static MetadataPacket ReadMetadataMaterials(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8); reader.BaseStream.Position = 216;
        var rows = new MetadataObject[U32(bytes, 20)];
        for (int i = 0; i < rows.Length; i++)
        {
            reader.BaseStream.Position += 20; int vertices = reader.ReadInt32();
            rows[i] = new(reader.ReadInt32(), reader.ReadInt32(), vertices, reader.ReadUInt32() != 0);
            SkipString(reader); SkipString(reader); int bones = reader.ReadInt32();
            for (int b = 0; b < bones; b++) { SkipString(reader); reader.BaseStream.Position += 64; }
            int morphs = reader.ReadInt32(); for (int m = 0; m < morphs; m++) { SkipString(reader); reader.BaseStream.Position += 4; }
        }
        int vertexOffset = checked((int)reader.BaseStream.Position);
        reader.BaseStream.Position += U32(bytes, 24) * 32 + U32(bytes, 28) * 4;
        var materials = new MetadataMaterial[U32(bytes, 32)];
        for (int i = 0; i < materials.Length; i++)
        {
            reader.ReadUInt64(); int local = reader.ReadInt32(), start = reader.ReadInt32(), count = reader.ReadInt32(); reader.ReadUInt32();
            var diffuse = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            reader.BaseStream.Position += 28; int texture = reader.ReadInt32(); reader.BaseStream.Position += 12;
            materials[i] = new(local, start, count, diffuse, texture);
        }
        return new(rows, materials, vertexOffset);
    }
    private static int LegacyVertices(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8); reader.BaseStream.Position = 208;
        for (int i = 0; i < U32(bytes, 20); i++)
        {
            reader.BaseStream.Position += 20; SkipString(reader); SkipString(reader); int bones = reader.ReadInt32();
            for (int b = 0; b < bones; b++) { SkipString(reader); reader.BaseStream.Position += 64; }
            int morphs = reader.ReadInt32(); for (int m = 0; m < morphs; m++) { SkipString(reader); reader.BaseStream.Position += 4; }
        }
        return checked((int)reader.BaseStream.Position);
    }
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
}
