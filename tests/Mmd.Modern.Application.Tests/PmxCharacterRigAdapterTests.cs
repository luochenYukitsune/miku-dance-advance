using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmxCharacterRigAdapterTests
{
    [Theory]
    // Original 9.32 PMX loader working values. Its single-precision pi makes the toe minimum 39, not 40.
    [InlineData(40, 2.0000007f, 40, .5000001788139343f)]
    [InlineData(3, .31415927f, 39, .07853981852531433f)]
    [InlineData(0, 1f, 12, .25f)]
    [InlineData(3, 0f, 3, 0f)]
    [InlineData(3, .001f, 360, .00025f)]
    [InlineData(2048, .001f, 2048, .00025f)]
    public void PmxParametersMatchOriginalLoaderWithoutChangingRawModel(
        int rawIterations, float rawLimit, int expectedIterations, float expectedLimit)
    {
        var model = Model(rawIterations, rawLimit);
        var ik = PmxCharacterRigAdapter.Convert(model, Rest(model)).Bones[2].Ik!;

        Assert.Equal(expectedIterations, ik.Iterations);
        Assert.Equal(expectedLimit, ik.AngleLimit);
        Assert.Equal(IkAnglePolicy.Mmd, ik.AnglePolicy);
        Assert.Equal(rawIterations, model.Bones[2].IkIterationCount);
        Assert.Equal(rawLimit, model.Bones[2].IkLimit);
    }

    [Theory]
    [InlineData(-1, 1f)]
    [InlineData(2049, 1f)]
    [InlineData(65535, 1f)]
    [InlineData(65536, 1f)]
    [InlineData(3, -1f)]
    [InlineData(3, 8f)]
    [InlineData(3, float.NaN)]
    [InlineData(3, float.PositiveInfinity)]
    public void NormalizationDoesNotHideInvalidRawParameters(int iterations, float limit)
    {
        var model = Model(iterations, limit);
        Assert.Throws<ArgumentOutOfRangeException>(() => PmxCharacterRigAdapter.Convert(model, Rest(model)));
    }

    [Fact]
    public void PmdConversionRestoresItsAngleBeforeApplyingTheSharedLoaderMinimum()
    {
        var vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }
            .Select(position => new ModelVertex(position, Vector3.UnitZ, Vector2.Zero, 0, 0, 1)).ToArray();
        var source = new PmdModel("pmd", vertices, [0, 1, 2],
            [new(Vector4.One, Vector3.Zero, Vector3.Zero, 1, "", 0, 3)],
            [new("link", -1, Vector3.Zero), new("tip", 0, Vector3.UnitX), new("goal", -1, Vector3.UnitY)], 0)
        {
            IkChains = [new(2, 1, 8, .25f, [0])]
        };
        var model = PmdPmxAdapter.Convert(source);
        // PmdPmxAdapter already multiplies the PMD angle by four for the raw PMX representation.
        Assert.Equal(1f, model.Bones[2].IkLimit);
        var ik = PmxCharacterRigAdapter.Convert(model, Rest(model)).Bones[2].Ik!;
        Assert.Equal(.25f, ik.AngleLimit);
        Assert.Equal(12, ik.Iterations);
        Assert.Equal(8, source.IkChains[0].Iterations);
        Assert.Equal(.25f, source.IkChains[0].AngleLimit);
    }

    [Fact]
    public void NonStandardButCommonModelDataIsRepairedAndReportedInsteadOfFailingTheWholeModel()
    {
        var template = Model(3, 1f);
        var model = new PmxModel
        {
            Header = template.Header, Name = template.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = template.Vertices, Faces = template.Faces, Textures = template.Textures, Materials = template.Materials,
            Bones =
            [
                new() { Name = "link", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None },
                // Duplicate name and an append flag pointing at -1.
                new() { Name = "link", EnglishName = "", Position = Vector3.UnitX, ParentIndex = 0, DeformDepth = 0,
                    Flags = PmxBoneFlags.AppendRotate, AppendBoneIndex = -1, AppendWeight = 1 },
                // Repeated link and swapped limits.
                new() { Name = "goal", EnglishName = "", Position = Vector3.UnitY, ParentIndex = -1, DeformDepth = 0,
                    Flags = PmxBoneFlags.Ik, IkTargetBoneIndex = 1, IkIterationCount = 3, IkLimit = 1,
                    IkLinks = [new(0, true, new(1, 0, 0), new(-1, 0, 0)), new(0, false, default, default)] },
                // IK target -1: the IK is ignored, the bone stays.
                new() { Name = "broken", EnglishName = "", Position = Vector3.UnitZ, ParentIndex = -1, DeformDepth = 0,
                    Flags = PmxBoneFlags.Ik, IkTargetBoneIndex = -1, IkIterationCount = 3, IkLimit = 1, IkLinks = [] },
            ],
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        var rig = PmxCharacterRigAdapter.Convert(model, Rest(model), out var repairs);
        Assert.Equal("link", rig.Bones[0].Name);
        Assert.NotEqual("link", rig.Bones[1].Name);
        Assert.Null(rig.Bones[1].Append);
        var ik = rig.Bones[2].Ik!;
        var link = Assert.Single(ik.Links);
        Assert.Equal(new Vector3(-1, 0, 0), link.MinimumAngles);
        Assert.Equal(new Vector3(1, 0, 0), link.MaximumAngles);
        Assert.Null(rig.Bones[3].Ik);
        Assert.Contains(repairs, r => r.Contains("bone 1") && r.Contains("renamed"));
        Assert.Contains(repairs, r => r.Contains("bone 1 append"));
        Assert.Contains(repairs, r => r.Contains("bone 2 IK link 0 skipped"));
        Assert.Contains(repairs, r => r.Contains("bone 3 IK target -1"));
    }

    [Fact]
    public void GeneratedNamesNeverTakeAnotherBonesAuthoredName()
    {
        var template = Model(3, 1f);
        PmxBone Bone(string name) => new() { Name = name, EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None };
        var model = new PmxModel
        {
            Header = template.Header, Name = template.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = template.Vertices, Faces = template.Faces, Textures = template.Textures, Materials = template.Materials,
            Bones = [Bone("A"), Bone("A"), Bone("A#1")],
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        var rig = PmxCharacterRigAdapter.Convert(model, Rest(model), out _);
        Assert.Equal("A", rig.Bones[0].Name);
        Assert.Equal("A#1", rig.Bones[2].Name);
        Assert.NotEqual("A#1", rig.Bones[1].Name);
        Assert.NotEqual("A", rig.Bones[1].Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IkSwitchOnlyControlsTheFirstBoneWithTheAuthoredName(bool firstIsIk)
    {
        var template = Model(3, 1f);
        var goal = template.Bones[2];
        var model = new PmxModel
        {
            Header = template.Header, Name = template.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = template.Vertices, Faces = template.Faces, Textures = template.Textures, Materials = template.Materials,
            Bones =
            [
                template.Bones[0], template.Bones[1],
                firstIsIk ? goal : new() { Name = goal.Name, EnglishName = "", Position = goal.Position,
                    ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None },
                new() { Name = "second link", EnglishName = "", Position = Vector3.Zero,
                    ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None },
                new() { Name = "second tip", EnglishName = "", Position = Vector3.UnitX,
                    ParentIndex = 3, DeformDepth = 0, Flags = PmxBoneFlags.None },
                new() { Name = goal.Name, EnglishName = "", Position = goal.Position,
                    ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.Ik, IkTargetBoneIndex = 4, IkIterationCount = 3, IkLimit = 1,
                    IkLinks = [new(3, false, default, default)] }
            ],
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        var rest = EditedMotionProjectTests.Rest(model);
        var motion = new VmdMotion { IkKeyframes = [new() { Frame = 1, Switches = [new(goal.Name, false)] }] };
        using var controller = new CharacterAnimationController(model, motion, rest.Surface, rest.Outline!);
        Assert.Contains(controller.Diagnostics, d => d.Contains("bone 5") && d.Contains("renamed"));
        Assert.Equal(!firstIsIk, controller.Diagnostics.Contains($"IK switch '{goal.Name}' has no matching IK controller."));

        controller.Sample(0);
        Vector3 firstTip = controller.BoneWorldMatrices[1].Translation;
        Vector3 secondTip = controller.BoneWorldMatrices[4].Translation;
        Assert.Equal(firstIsIk, Vector3.Distance(firstTip, Vector3.UnitX) > 0.1f);
        Assert.True(Vector3.Distance(secondTip, Vector3.UnitX) > 0.1f);

        controller.Sample(1);
        Assert.Equal(Vector3.UnitX, controller.BoneWorldMatrices[1].Translation);
        Assert.Equal(secondTip, controller.BoneWorldMatrices[4].Translation);
    }

    private static SceneSnapshot Rest(PmxModel model) => EditedMotionProjectTests.Rest(model).Surface.Snapshot;

    private static PmxModel Model(int iterations, float limit)
    {
        var template = EditedMotionProjectTests.Model();
        return new()
        {
            Header = template.Header, Name = template.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = template.Vertices, Faces = template.Faces, Textures = template.Textures, Materials = template.Materials,
            Bones =
            [
                new() { Name = "link", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None },
                new() { Name = "tip", EnglishName = "", Position = Vector3.UnitX, ParentIndex = 0, DeformDepth = 0, Flags = PmxBoneFlags.None },
                new() { Name = "goal", EnglishName = "", Position = Vector3.UnitY, ParentIndex = -1, DeformDepth = 0,
                    Flags = PmxBoneFlags.Ik, IkTargetBoneIndex = 1, IkIterationCount = iterations, IkLimit = limit,
                    IkLinks = [new(0, false, default, default)] }
            ],
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
    }
}
