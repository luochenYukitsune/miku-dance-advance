using System.Numerics;
using System.Reflection;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ResolvedOutputCameraTests
{
    [Fact]
    public void ParentedMetadataStillRequiresAnExplicitResolvedCamera()
    {
        var scene = D3D12OriginalOutlineTests.Box();
        var pose = new PmmCameraPose(Vector3.Zero, Vector3.Zero, -4, 45, true, 7, 0);
        Assert.Throws<NotSupportedException>(() => D3D12ViewScene.Resolve(scene, pose, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), new()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolvedCameraDrivesProjectionOutlineAndSphereWithoutMutatingMetadata(bool perspective)
    {
        var scene = D3D12OriginalOutlineTests.Box();
        var pose = new PmmCameraPose(new(2, 3, 4), new(.1f, .2f, .3f), -4, 45, perspective, 7, 0);
        var camera = new SceneCamera(new(9, 5, -8), new(3, 2, 1), Vector3.UnitY, 8, .1f, 100,
            perspective ? 61 : null, LeftHanded: true);
        var result = D3D12ViewScene.Resolve(scene, pose, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), new(), camera);
        Assert.Same(camera, result.Options!.CameraOverride);
        Assert.Equal(camera.Eye, result.OutlineOptions!.CameraPosition);
        Assert.Equal(perspective ? 61 : 45, result.OutlineOptions.FovDegrees);
        var expectedRotation = Matrix4x4.CreateLookAtLeftHanded(camera.Eye, camera.Target, camera.Up);
        expectedRotation.M41 = expectedRotation.M42 = expectedRotation.M43 = 0;
        Assert.Equal(expectedRotation, result.Options.SphereRotationOverride);
        Assert.NotEqual(camera.Eye, scene.OutlineOptions!.CameraPosition);
        Assert.Equal(7, pose.ParentModelIndex);
        Assert.Equal(new Vector3(2, 3, 4), pose.LookAt);
    }

    [Fact]
    public void LegacyUnparentedAndCameraFreePacketsPreserveTheirCameraRules()
    {
        var scene = D3D12OriginalOutlineTests.Box();
        var pose = new PmmCameraPose(new(1, 2, 3), new(.1f, .2f, .3f), -4, 45, true);
        var result = D3D12ViewScene.Resolve(scene, pose, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), new());
        Assert.Equal(pose.CreateCamera(), result.Options!.CameraOverride);
        var without = D3D12ViewScene.Resolve(scene, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), new());
        Assert.Equal(scene.Options?.CameraOverride, without.Options!.CameraOverride);
        Assert.Equal(scene.OutlineOptions, without.OutlineOptions);
    }

    [Fact]
    public void PmmParentMotionSuppliesResolvedOfflineAndPreviewPackets()
    {
        using var playback = ResolvedOutputCameraFixture.Playback();
        var first = playback.Camera;
        playback.Seek(10);
        var resolved = playback.Camera;
        Assert.NotEqual(first.Eye, resolved.Eye);
        Assert.NotEqual(playback.CameraPose.CreateCamera().Eye, resolved.Eye);
        var plan = new OfflineFramePlan(96, 72, new(30), 10, 10);
        var offline = new OfflineFrame(plan.GetSample(0), 96, 72, playback.Frame,
            playback.CameraPose, null, null) { ResolvedCamera = resolved };
        var preview = new PreviewFrame(96, 72, 10, playback.Frame, playback.CameraPose, null, null)
            { ResolvedCamera = resolved };
        Assert.Same(resolved, offline.ResolvedCamera);
        Assert.Same(resolved, preview.ResolvedCamera);
        Assert.Equal(0, offline.Camera!.Value.ParentModelIndex);
        Assert.Equal(offline.Camera, preview.Camera);
        var result = D3D12ViewScene.Resolve(preview.Scene, preview.Camera, preview.Light, preview.SelfShadow,
            preview.MorphChannels, new(), preview.ResolvedCamera);
        Assert.Same(resolved, result.Options!.CameraOverride);
        Assert.Equal(resolved.Eye, result.OutlineOptions!.CameraPosition);
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task PublicCaptureMatchesIndependentResolvedReferenceAndLegacyUnparentedFrame()
    {
        using var playback = ResolvedOutputCameraFixture.Playback();
        await using var renderer = new D3D12ViewRenderer(new(96, 72));
        using var reference = new D3D12FrameSession();
        byte[]? first = null;
        foreach (int frame in new[] { 0, 10 })
        {
            playback.Seek(frame);
            var scene = D3D12OriginalOutlineTests.Box();
            var camera = playback.Camera;
            var rotation = Matrix4x4.CreateLookAtLeftHanded(camera.Eye, camera.Target, camera.Up);
            rotation.M41 = rotation.M42 = rotation.M43 = 0;
            var expected = new OriginalFrameScene(scene.Surface, scene.Outline,
                scene.OutlineOptions! with { CameraPosition = camera.Eye, FovDegrees = playback.CameraPose.Fov },
                new ViewQualityOptions().ApplyTo(scene.Options) with { CameraOverride = camera, SphereRotationOverride = rotation },
                scene.Materials, scene.Lighting, scene.EditorOverlay);
            var plan = new OfflineFramePlan(96, 72, new(30), (uint)frame, (uint)frame);
            var packet = new OfflineFrame(plan.GetSample(0), 96, 72, scene, playback.CameraPose, null, null)
                { ResolvedCamera = camera };
            using var actual = await renderer.CaptureAsync(packet, TestContext.Current.CancellationToken);
            byte[] pixels = actual.Pixels.ToArray();
            Assert.Equal(reference.Capture(expected, 96, 72).Rgba, pixels);
            Assert.Equal(new byte[] { 20, 24, 32, 255 }, pixels[..4]);
            uint background = BitConverter.ToUInt32(pixels, 0);
            Assert.Contains(Enumerable.Range(1, pixels.Length / 4 - 1), i => BitConverter.ToUInt32(pixels, 4 * i) != background);
            if (first is null) first = pixels; else Assert.NotEqual(first, pixels);
        }
        var unparentedPose = playback.CameraPose with { ParentModelIndex = -1 };
        var unparented = new OfflineFrame(new OfflineFramePlan(96, 72, new(30), 0, 0).GetSample(0),
            96, 72, playback.Frame, unparentedPose, null, null);
        using var legacy = await renderer.CaptureAsync(unparented, TestContext.Current.CancellationToken);
        using var explicitCamera = await renderer.CaptureAsync(unparented with { ResolvedCamera = unparentedPose.CreateCamera() }, TestContext.Current.CancellationToken);
        Assert.Equal(legacy.Pixels.ToArray(), explicitCamera.Pixels.ToArray());
    }
}

internal static class ResolvedOutputCameraFixture
{
    internal static PmmProjectPlayback Playback()
    {
        byte[] boneCurve = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(x => x).ToArray();
        byte[] cameraCurve = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 6).SelectMany(x => x).ToArray();
        var source = new PmmModel
        {
            Index = 0, Visible = true, SelectedMorphIndices = new int[4], BoneNames = [new(0, "root", [])],
            InitialBoneKeyframes = [new() { NextIndex = 1, Rotation = Quaternion.Identity, Interpolation = boneCurve }],
            BoneKeyframes = [new() { RecordIndex = 1, ObjectIndex = 1, Frame = 10, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f),
                Translation = new(.4f, .2f, 0), Interpolation = boneCurve }],
            BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)],
            InitialModelKeyframe = new() { Visible = true }
        };
        var document = new PmmDocument
        {
            AnsiCodePage = 932, PanelStates = new byte[7], Models = [source], OutputWidth = 96, OutputHeight = 72, CameraFov = 45,
            Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY,
                InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } },
            Settings = new() { CameraLookAtModelIndex = 0, CameraLookAtBoneIndex = 0,
                EdgeColorRgb = new int[3], UnknownMatrix = new byte[64], PreferredFps = 30 },
            // Parented PMM cameras store their local eye in LookAt, including the keyframe used by Seek.
            Camera = new() { Position = new(0, 0, -4), LookAt = new(0, 0, -4), Perspective = true,
                InitialKeyframe = new() { LookAt = new(0, 0, -4), Distance = -4, Fov = 45, Perspective = true, ParentModelIndex = 0,
                    ParentBoneIndex = 0, Interpolation = cameraCurve } }
        };
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "camera parent", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 2, 1)],
            Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.BothFaces, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
            Vertices = new[] { new Vector3(-.7f, -.6f, 0), new Vector3(.7f, -.6f, 0), new Vector3(0, .7f, 0) }.Select(position => new PmxVertex
            { Position = position, Normal = -Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray()
        };
        var prepared = new PreparedPmmModel(source, "camera-parent.pmx", model,
            PmmMotionAdapter.Convert(source, model), PmmMotionAdapter.ConvertSavedPose(source, model));
        var plan = (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [document, "camera-parent.pmm", new List<PreparedPmmModel> { prepared }, new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -4), 45, 1),
            materials: PmxOriginalFrameAdapter.FromMaterials(model));
        return new(plan, new Dictionary<int, OriginalFrameScene> { [0] = rest },
            (_, _) => throw new InvalidOperationException("The camera fixture has no physics."));
    }
}
