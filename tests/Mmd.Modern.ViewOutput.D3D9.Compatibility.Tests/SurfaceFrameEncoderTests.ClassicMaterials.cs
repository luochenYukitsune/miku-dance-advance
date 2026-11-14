using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed partial class SurfaceFrameEncoderTests
{
    [Theory]
    [InlineData(ModelRole.Character)]
    [InlineData(ModelRole.Environment)]
    public void ClassicProjectsDerivedPurposeFromTheAcceptedSampleWithoutChangingItsSource(ModelRole role)
    {
        using var project = ClassicMaterialProject(role);
        var encoder = new D3D9SurfaceFrameEncoder(MaterialBindings(project));
        OfflineFrame? resolved = null;
        encoder.UseClassicPassInputs(frame => { resolved = frame; return ClassicSelections(frame); });
        var frame = MaterialCapture(project, encoder);
        var ownerFrame = project.Frame; double ownerClock = project.CurrentFrame;
        var composed = Assert.Single(frame.Scene.Materials);
        var authored = Assert.Single(Assert.Single(frame.ObjectMaterialInputs).Value.MaterialFlags);
        Assert.NotEqual(authored, composed);
        Assert.Equal(role == ModelRole.Character, composed.PreserveDirectLightPalette);
        Assert.Equal(role == ModelRole.Environment, composed.UseSceneLighting);

        var packet = encoder.Serialize(frame, Request, Vector4.One);

        Assert.NotNull(resolved);
        Assert.Equal(authored, Assert.Single(resolved.Scene.Materials));
        Assert.Same(frame.Sample, resolved.Sample);
        Assert.Same(frame.Scene.Surface, resolved.Scene.Surface);
        Assert.Same(frame.Scene.Outline, resolved.Scene.Outline);
        Assert.Same(frame.SceneInputs, resolved.SceneInputs);
        Assert.Same(frame.ObjectMaterialInputs, resolved.ObjectMaterialInputs);
        Assert.Same(frame.RenderingStyles, resolved.RenderingStyles);
        Assert.Equal(composed, Assert.Single(frame.Scene.Materials));
        Assert.Same(ownerFrame, project.Frame); Assert.Equal(ownerClock, project.CurrentFrame);
        Assert.Equal(7.5, BinaryPrimitives.ReadDoubleLittleEndian(packet.Frame.AsSpan(40)));
        Assert.Equal(5U, U32(packet.Frame, 8)); Assert.Equal(6U, U32(packet.Visual, 8));
        Assert.Equal(SHA256.HashData(packet.Frame), packet.Visual.AsSpan(20, 32).ToArray());
    }

    [Fact]
    public void ClassicKeepsTheRealPmmModelShadowGateAfterRestoringAuthoredPurposeChannels()
    {
        using var project = ClassicMaterialProject(ModelRole.Character, pmmShadowDisabled: true);
        var encoder = new D3D9SurfaceFrameEncoder(MaterialBindings(project));
        OfflineFrame? resolved = null;
        encoder.UseClassicPassInputs(frame => { resolved = frame; return ClassicSelections(frame); });
        var frame = MaterialCapture(project, encoder);
        var authored = Assert.Single(Assert.Single(frame.ObjectMaterialInputs).Value.MaterialFlags);
        Assert.True(authored.CastSelfShadow); Assert.True(authored.ReceiveSelfShadow);
        Assert.False(Assert.Single(frame.Scene.Materials).CastSelfShadow);
        Assert.False(Assert.Single(frame.Scene.Materials).ReceiveSelfShadow);

        _ = encoder.Serialize(frame, Request, Vector4.One);

        Assert.NotNull(resolved);
        Assert.Equal(authored with { CastSelfShadow = false, ReceiveSelfShadow = false }, Assert.Single(resolved.Scene.Materials));
        Assert.True(authored.CastSelfShadow); Assert.True(authored.ReceiveSelfShadow);
    }

    [Fact]
    public void ClassicRejectsRawModernOverridesAndPassSelectionsFromAnotherFrame()
    {
        foreach (var flags in new OriginalFrameMaterialFlags[]
        {
            new(true, true, true) { UseSceneLighting = true },
            new(true, true, true) { PreserveDirectLightPalette = true },
            new(true, true, true, CullTransparentSurface: true)
        })
        {
            using var project = ClassicMaterialProject(ModelRole.Character, sourceFlags: flags);
            var encoder = new D3D9SurfaceFrameEncoder(MaterialBindings(project));
            encoder.UseClassicPassInputs(ClassicSelections);
            var error = Assert.Throws<NotSupportedException>(() => encoder.Serialize(MaterialCapture(project, encoder), Request, Vector4.One));
            Assert.Contains("modern source overrides", error.Message, StringComparison.Ordinal);
        }

        using var valid = ClassicMaterialProject(ModelRole.Character);
        var acceptedEncoder = new D3D9SurfaceFrameEncoder(MaterialBindings(valid));
        acceptedEncoder.UseClassicPassInputs(frame => ClassicSelections(frame with { }));
        var identityError = Assert.Throws<InvalidOperationException>(() => acceptedEncoder.Serialize(MaterialCapture(valid, acceptedEncoder), Request, Vector4.One));
        Assert.Contains("exact accepted frame", identityError.Message, StringComparison.Ordinal);
    }

    private static D3D9ClassicPassInputs ClassicSelections(OfflineFrame frame) => new(frame, true, null,
        new(false, -Vector3.UnitY, new(Vector4.Zero, Vector4.One, Vector4.Zero, Vector4.Zero, 0), Vector3.One, 1, false, false),
        frame.RenderingStyles.Select(value => new D3D9ClassicModelPassInputs(value.ObjectId, frame.SceneInputs[value.ObjectId].SourceOutlineOptions)));

    private static SceneProjectSession ClassicMaterialProject(ModelRole role, bool pmmShadowDisabled = false,
        OriginalFrameMaterialFlags? sourceFlags = null)
    {
        var basis = Model(); var material = basis.Materials[0];
        var model = new PmxModel
        {
            Header = basis.Header, Name = basis.Name, EnglishName = basis.EnglishName, Comment = basis.Comment, EnglishComment = basis.EnglishComment,
            Vertices = basis.Vertices, Faces = basis.Faces, Textures = basis.Textures, Bones = basis.Bones, Morphs = basis.Morphs,
            DisplayFrames = basis.DisplayFrames, RigidBodies = basis.RigidBodies, Joints = basis.Joints, SoftBodies = basis.SoftBodies,
            Materials = [new() { Name = material.Name, EnglishName = "", Diffuse = material.Diffuse, Ambient = material.Ambient,
                Specular = material.Specular, SpecularPower = material.SpecularPower, EdgeColor = Vector4.One, EdgeSize = material.EdgeSize,
                DrawMode = PmxDrawModeFlags.CastSelfShadow | PmxDrawModeFlags.ReceiveSelfShadow,
                TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }]
        };
        var flags = sourceFlags ?? new(true, true, false);
        if (pmmShadowDisabled) flags = flags with { CastSelfShadow = false, ReceiveSelfShadow = false };
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, new(0, 0, -5), 30, 1, Eligible: false), materials: [flags]);
        var layout = new SceneCompositionEditor(); var actor = layout.Add("classic", "classic.pmx", role, rest);
        var lighting = new SceneLightingEditor();
        if (role == ModelRole.Environment) lighting.SetModelRole(actor.Key, role, environmentIndoor: true);
        ScenePmmControlState? controls = null;
        if (pmmShadowDisabled)
        {
            var document = new PmmDocument
            {
                AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 64, OutputHeight = 32, CameraFov = 30,
                Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
                Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
                Models = [new() { Index = 0, Name = new(0, "classic", []), Path = new(0, "classic.pmx", []), BoneNames = [new(0, "root", [])],
                    SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, SelfShadowEnabled = false,
                    InitialBoneKeyframes = [new() { Rotation = Quaternion.Identity, Interpolation = new byte[16] }],
                    InitialModelKeyframe = new() { Visible = true }, BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)] }]
            };
            controls = ScenePmmControlState.Create(document, [new(actor.Id, 0, model)]);
        }
        SceneActorMotion[] motions = sourceFlags is null ? [new(actor.Id, model, new VmdMotion(), PhysicsEnabled: false)] : [];
        return new(layout, lighting, motions, Provider(), initialFrame: 7.5, pmmControls: controls);
    }
}
