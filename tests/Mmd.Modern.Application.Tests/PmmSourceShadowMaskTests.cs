using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmSourceShadowMaskTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _)
        => throw new InvalidOperationException("Fixture has no physics bodies.");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PmmSourceMasksSurviveInitialSeekAndAnimatedMaterialFlags(bool sourceCast, bool sourceReceive)
    {
        var (layout, actor, model, motion) = Fixture(sourceCast, sourceReceive);
        var controls = ScenePmmControlState.Create(Document(), [new(actor.Id, 0, model)]);
        using var session = new SceneAnimationSession(layout.Objects,
            [new(actor.Id, model, motion, PhysicsEnabled: false)], NoPhysics,
            cancellationToken: Token, pmmControls: controls);

        void AssertFlags(bool evaluatedCast)
        {
            var flags = Assert.Single(session.Frame.Materials);
            Assert.Equal(sourceCast && evaluatedCast, flags.CastSelfShadow);
            Assert.Equal(sourceReceive, flags.ReceiveSelfShadow);
            // Published animation never rewrites the explicit source-local mask.
            Assert.Equal(sourceCast, Assert.Single(actor.Source.Materials).CastSelfShadow);
            Assert.Equal(sourceReceive, Assert.Single(actor.Source.Materials).ReceiveSelfShadow);
        }

        AssertFlags(true);
        session.Seek(1, Token); AssertFlags(false);
        Assert.Equal(0, Assert.Single(session.Frame.Outline!.Materials).Color.W);
        session.Seek(2, Token); AssertFlags(true);
        session.Seek(0, Token); AssertFlags(true);
        session.Advance(1.0 / 30, Token); AssertFlags(false);
        session.Advance(1.0 / 30, Token); AssertFlags(true);
    }

    [Fact]
    public void OrdinaryAnimationContinuesToUseEvaluatedMaterialFlags()
    {
        var (layout, actor, model, motion) = Fixture(false, false);
        using var session = new SceneAnimationSession(layout.Objects,
            [new(actor.Id, model, motion, PhysicsEnabled: false)], NoPhysics, cancellationToken: Token);
        var flags = Assert.Single(session.Frame.Materials);
        Assert.True(flags.CastSelfShadow); Assert.True(flags.ReceiveSelfShadow);
        session.Seek(1, Token);
        flags = Assert.Single(session.Frame.Materials);
        Assert.False(flags.CastSelfShadow); Assert.True(flags.ReceiveSelfShadow);
        session.Seek(2, Token);
        flags = Assert.Single(session.Frame.Materials);
        Assert.True(flags.CastSelfShadow); Assert.True(flags.ReceiveSelfShadow);
    }

    private static (SceneCompositionEditor Layout, EditableSceneObject Actor, PmxModel Model, VmdMotion Motion)
        Fixture(bool sourceCast, bool sourceReceive)
    {
        var basis = EditedMotionProjectTests.Model();
        var model = new PmxModel
        {
            Header = basis.Header, Name = basis.Name, EnglishName = basis.EnglishName,
            Comment = basis.Comment, EnglishComment = basis.EnglishComment,
            Vertices = basis.Vertices, Faces = basis.Faces, Textures = basis.Textures, Bones = basis.Bones,
            DisplayFrames = basis.DisplayFrames, RigidBodies = basis.RigidBodies, Joints = basis.Joints,
            SoftBodies = basis.SoftBodies,
            Materials = [new()
            {
                Name = "shadow", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero,
                SpecularPower = 1, Ambient = Vector3.Zero, EdgeColor = Vector4.One, EdgeSize = 1,
                DrawMode = PmxDrawModeFlags.CastSelfShadow | PmxDrawModeFlags.ReceiveSelfShadow | PmxDrawModeFlags.DrawEdge,
                TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3
            }],
            Morphs = [new()
            {
                Name = "hide-edge", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Material,
                MaterialOffsets = [new(0, PmxMaterialMorphOperation.Multiply,
                    Vector4.One, Vector3.One, 1, Vector3.One, new(1, 1, 1, 0), 1,
                    Vector4.One, Vector4.One, Vector4.One)]
            }]
        };
        var rest = EditedMotionProjectTests.Rest(model);
        var source = new OriginalFrameScene(rest.Surface, rest.Outline, rest.OutlineOptions, rest.Options,
            rest.Materials.Select(flags => flags with { CastSelfShadow = sourceCast, ReceiveSelfShadow = sourceReceive }));
        var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "synthetic.pmx", ModelRole.Character, source);
        var motion = new VmdMotion
        {
            MorphKeyframes = [new() { MorphName = "hide-edge", Frame = 0, Weight = 0 },
                new() { MorphName = "hide-edge", Frame = 1, Weight = 1 },
                new() { MorphName = "hide-edge", Frame = 2, Weight = 0 }]
        };
        return (layout, actor, model, motion);
    }

    private static PmmDocument Document() => new()
    {
        AnsiCodePage = 932, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 30,
        Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30,
            ParentModelIndex = -1, ParentBoneIndex = -1 } },
        Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 },
        Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } },
        Models = [new()
        {
            Index = 0, Name = new(0, "model", []), Path = new(0, "synthetic.pmx", []),
            BoneNames = [new(0, "root", [])], SelectedMorphIndices = [-1, -1, -1, -1],
            Visible = true, SelfShadowEnabled = true, LastFrame = 2,
            InitialBoneKeyframes = [new() { Rotation = Quaternion.Identity, Interpolation = new byte[16] }],
            InitialModelKeyframe = new() { Visible = true },
            BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)]
        }]
    };
}
