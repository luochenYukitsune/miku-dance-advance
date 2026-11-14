using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class CharacterMorphPreflightTests
{
    [Theory]
    [InlineData("uv", "advance")]
    [InlineData("uv", "sample")]
    [InlineData("uv", "reset")]
    [InlineData("texture", "advance")]
    [InlineData("texture", "sample")]
    [InlineData("texture", "reset")]
    [InlineData("material", "advance")]
    [InlineData("material", "sample")]
    [InlineData("material", "reset")]
    public void NumericMorphFailureDoesNotMutatePhysicsOrPublishedSample(string channel, string operation)
    {
        using var controller = Controller(channel);
        var backend = new RecordingBackend();
        controller.EnablePhysics(backend, 0);
        var previous = controller.SampleAt(0);
        var previousChannels = controller.MorphChannels;
        var previousWorlds = controller.BoneWorldMatrices;
        int resets = backend.Initializations;
        Assert.Throws<ArgumentException>(() =>
        {
            if (operation == "advance") controller.Advance(10, 1d / 30);
            else if (operation == "sample") controller.SampleAt(10);
            else controller.ResetPhysics(10);
        });
        Assert.Equal(resets, backend.Initializations);
        Assert.Equal(0, backend.Steps);
        Assert.False(controller.PhysicsFaulted);
        Assert.Same(previous.Surface, controller.SampleAt(0).Surface);
        Assert.Same(previous.Outline, controller.SampleAt(0).Outline);
        Assert.Same(previousChannels, controller.MorphChannels);
        Assert.Same(previousWorlds, controller.BoneWorldMatrices);
        controller.Advance(1, 1d / 30, enableMorphs: false);
        Assert.Equal(4, backend.Steps); // also proves failed frame 10 did not become the solver's CurrentFrame
        Assert.Equal(resets, backend.Initializations);
    }

    private static CharacterAnimationController Controller(string channel)
    {
        using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        var m = PmxReader.Read(input);
        var offset = new PmxMorph.MaterialOffset(0, PmxMaterialMorphOperation.Add,
            channel == "material" ? new(float.MaxValue) : Vector4.Zero, Vector3.Zero, 0,
            Vector3.Zero, Vector4.Zero, 0, channel == "texture" ? new(float.MaxValue) : Vector4.Zero,
            Vector4.Zero, Vector4.Zero);
        var morph = channel == "uv"
            ? new PmxMorph { Name = "large", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.AdditionalUv1,
                UvOffsets = [new(0, new(float.MaxValue))] }
            : new PmxMorph { Name = "large", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Material,
                MaterialOffsets = [offset] };
        var model = new PmxModel
        {
            Header = m.Header, Name = "preflight", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = m.Vertices.Select(v => new PmxVertex { Position = v.Position, Normal = v.Normal, Uv = v.Uv,
                AdditionalUvs = v.AdditionalUvs, WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0,-1,-1,-1],
                BoneWeights = [1,0,0,0], EdgeScale = v.EdgeScale }).ToArray(), Faces = m.Faces, Textures = m.Textures,
            Materials = m.Materials, Bones = m.Bones, DisplayFrames = [], SoftBodies = [], Joints = [], Morphs = [morph],
            RigidBodies = [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = m.Bones[0].Position,
                Rotation = Vector3.Zero, Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0,
                Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }]
        };
        var motion = new VmdMotion { MorphKeyframes = [new() { MorphName = "large", Frame = 0, Weight = 0 },
            new() { MorphName = "large", Frame = 10, Weight = 2 }, new() { MorphName = "large", Frame = 30, Weight = 0 }] };
        return new(model, motion, PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model));
    }

    private sealed class RecordingBackend : ICharacterPhysicsBackend
    {
        private Matrix4x4[] worlds = [];
        public int Initializations, Steps;
        public string Name => "preflight fixture";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        { Initializations++; worlds = transforms.ToArray(); }
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => worlds[index] = transform;
        public Matrix4x4 GetBodyWorldTransform(int index) => worlds[index];
        public void Step(float seconds) => Steps++;
        public void Dispose() { }
    }
}
