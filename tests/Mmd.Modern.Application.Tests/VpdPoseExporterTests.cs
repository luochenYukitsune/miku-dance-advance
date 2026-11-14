using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class VpdPoseExporterTests
{
    [Fact]
    public void CurrentSolvedAppendPoseBakesAndReplaysOnlyWithConstraintsRemoved()
    {
        var model = Model();
        RigBone[] constrained = [new("child", model.Bones[0].Position, 1, DeformLayer: 1,
            Append: new(1, .5f, false, true, true)), new("parent", model.Bones[1].Position, -1)];
        var motion = new MotionClip([new BoneTrack("parent",
            [new(0, Vector3.Zero, Quaternion.Identity, BoneCurves.Linear),
             new(10, new(4, 6, 8), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .8f), BoneCurves.Linear)])]);
        var animator = new CharacterAnimator(new(constrained, [], []), motion);
        var solved = animator.Sample(5);
        var pose = VpdPoseExporter.ConvertBakedPose(model, solved.BoneWorldMatrices);
        Assert.True(pose.Bones[0].Translation.Length() > 1);
        var bakedMotion = new MotionClip(pose.Bones.Select(b => new BoneTrack(b.BoneName,
            [new(0, b.Translation, b.Rotation, BoneCurves.Linear)])));
        var plainRig = new CharacterRig(model.Bones.Select(b => new RigBone(b.Name, b.Position, b.ParentIndex)), [], []);
        var replay = new CharacterAnimator(plainRig, bakedMotion).Sample(0);
        for (int i = 0; i < model.Bones.Count; i++) Near(solved.BoneWorldMatrices[i], replay.BoneWorldMatrices[i]);
        var doubled = new CharacterAnimator(new(constrained, [], []), bakedMotion).Sample(0);
        Assert.True(Vector3.Distance(solved.BoneWorldMatrices[0].Translation, doubled.BoneWorldMatrices[0].Translation) > .1f);
    }

    [Fact]
    public void BakedWorldsRecoverLocalDeltasWithChildBeforeParentAndNoHandednessFlip()
    {
        var model = Model();
        var parent = Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(3, 4, 5);
        var childLocal = Matrix4x4.CreateRotationZ(-.4f) * Matrix4x4.CreateTranslation(2, 5, -7);
        Matrix4x4[] input = [childLocal * parent, parent];
        var copy = input.ToArray();
        var pose = VpdPoseExporter.ConvertBakedPose(model, input);
        Assert.Equal(copy, input);
        Assert.Equal("child", pose.Bones[0].BoneName);
        Near(new(0, 2, -7), pose.Bones[0].Translation);
        Near(new(2, 2, 2), pose.Bones[1].Translation);
        for (int i = 0; i < input.Length; i++)
        {
            var bone = model.Bones[i]; var p = pose.Bones[i];
            var rest = bone.Position - (bone.ParentIndex < 0 ? Vector3.Zero : model.Bones[bone.ParentIndex].Position);
            var reconstructed = Matrix4x4.CreateFromQuaternion(p.Rotation) * Matrix4x4.CreateTranslation(rest + p.Translation);
            if (bone.ParentIndex >= 0) reconstructed *= input[bone.ParentIndex];
            Near(input[i], reconstructed);
            Assert.InRange(MathF.Abs(p.Rotation.Length() - 1), 0, 1e-6f);
        }
    }

    [Fact]
    public void RawLocalAnimationExportPreservesEditableDeltasAndMorphWeights()
    {
        var model = Model();
        var pose = VpdPoseExporter.ConvertLocalPose(model,
            [new(new(1, 2, 3), new(0, 0, 0, 2)), new(Vector3.Zero, Quaternion.Identity)],
            new Dictionary<int, float> { [0] = -.3f });
        Assert.Equal(new Vector3(1, 2, 3), pose.Bones[0].Translation);
        Assert.Equal(Quaternion.Identity, pose.Bones[0].Rotation);
        Assert.Equal(-.3f, Assert.Single(pose.Morphs).Weight);
        Assert.Equal(new[] { 0, 1 }, pose.Bones.Select(b => b.Index));
        using var buffer = new MemoryStream();
        VpdWriter.Write(buffer, pose, VpdTextEncoding.Utf8); buffer.Position = 0;
        var loaded = VpdReader.Read(buffer, new() { Encoding = VpdTextEncoding.Utf8 });
        var motion = VpdMotionAdapter.Convert(loaded, model, 123);
        Assert.Equal(new Vector3(1, 2, 3), motion.BoneKeyframes[0].Translation);
        Assert.Equal(123u, motion.MorphKeyframes[0].Frame);
        Assert.Equal(-.3f, motion.MorphKeyframes[0].Weight);
    }

    [Theory]
    [InlineData("scale")]
    [InlineData("shear")]
    [InlineData("reflection")]
    [InlineData("singular")]
    [InlineData("perspective")]
    [InlineData("nan")]
    public void BakedNonRigidWorldsAreRejected(string kind)
    {
        var matrix = kind switch
        {
            "scale" => Matrix4x4.CreateScale(2), "reflection" => Matrix4x4.CreateScale(-1, 1, 1),
            "singular" => default, _ => Matrix4x4.Identity
        };
        if (kind == "shear") matrix.M12 = .2f;
        if (kind == "perspective") matrix.M14 = .1f;
        if (kind == "nan") matrix.M43 = float.NaN;
        Assert.Throws<ArgumentException>(() => VpdPoseExporter.ConvertBakedPose(Model(), [matrix, Matrix4x4.Identity]));
    }

    [Fact]
    public void BakeInspectsOnlyTheSelectedFlipBranchForBoneEffects()
    {
        var model = Copy(Model(), morphs: [Morph("vertex", PmxMorphType.Position), Morph("bone", PmxMorphType.Bone),
            new() { Name = "flip", EnglishName = "", Type = PmxMorphType.Flip, ControlPanel = 0, FlipOffsets = [new(0, 1), new(1, 1)] }]);
        var worlds = model.Bones.Select(b => Matrix4x4.CreateTranslation(b.Position)).ToArray();
        Assert.Equal(.1f, VpdPoseExporter.ConvertBakedPose(model, worlds, new Dictionary<int, float> { [2] = .1f }).Morphs[2].Weight);
        Assert.Throws<ArgumentException>(() => VpdPoseExporter.ConvertBakedPose(model, worlds, new Dictionary<int, float> { [2] = 1 }));
    }

    [Fact]
    public void BakeRejectsBoneMorphDoubleApplicationButRawLocalAllowsIt()
    {
        var source = Model();
        var model = Copy(source, morphs: [Morph("bone", PmxMorphType.Bone),
            Morph("group", PmxMorphType.Group, [new(0, .5f)]),
            Morph("vertex", PmxMorphType.Position), Morph("safeGroup", PmxMorphType.Group, [new(2, .5f)])]);
        var worlds = model.Bones.Select(b => Matrix4x4.CreateTranslation(b.Position)).ToArray();
        Assert.Throws<ArgumentException>(() => VpdPoseExporter.ConvertBakedPose(model, worlds, new Dictionary<int, float> { [0] = 1 }));
        Assert.Throws<ArgumentException>(() => VpdPoseExporter.ConvertBakedPose(model, worlds, new Dictionary<int, float> { [1] = 1 }));
        Assert.Equal(.8f, VpdPoseExporter.ConvertBakedPose(model, worlds, new Dictionary<int, float> { [3] = .8f }).Morphs[3].Weight);
        Assert.Equal(1, VpdPoseExporter.ConvertLocalPose(model,
            [new(Vector3.Zero, Quaternion.Identity), new(Vector3.Zero, Quaternion.Identity)],
            new Dictionary<int, float> { [0] = 1 }).Morphs[0].Weight);
    }

    [Fact]
    public void InvalidHierarchyNamesCountsAndNonfiniteLocalValuesFail()
    {
        var model = Model();
        Assert.Throws<ArgumentException>(() => VpdPoseExporter.ConvertBakedPose(model, [Matrix4x4.Identity]));
        Assert.Throws<ArgumentException>(() => VpdPoseExporter.ConvertLocalPose(model,
            [new(Vector3.Zero, default), new(Vector3.Zero, Quaternion.Identity)]));
        Assert.Throws<ArgumentException>(() => VpdPoseExporter.ConvertLocalPose(model,
            [new(new(float.NaN, 0, 0), Quaternion.Identity), new(Vector3.Zero, Quaternion.Identity)]));
        Assert.Throws<ArgumentException>(() => VpdPoseExporter.ConvertBakedPose(model, [Matrix4x4.Identity, Matrix4x4.Identity],
            new Dictionary<int, float> { [1] = 1 }));
        Assert.Throws<ArgumentException>(() => VpdPoseExporter.ConvertBakedPose(model, [Matrix4x4.Identity, Matrix4x4.Identity],
            new Dictionary<int, float> { [0] = float.NaN }));
        Assert.Throws<ArgumentException>(() => VpdPoseExporter.ConvertBakedPose(Copy(model,
            [Bone("child", 1, Vector3.Zero), Bone("parent", 0, Vector3.Zero)]), [Matrix4x4.Identity, Matrix4x4.Identity]));
        Assert.Throws<ArgumentException>(() => VpdPoseExporter.ConvertBakedPose(Copy(model,
            [Bone("same", 1, Vector3.Zero), Bone("same", -1, Vector3.Zero)]), [Matrix4x4.Identity, Matrix4x4.Identity]));
    }

    private static PmxModel Model() => Copy(PmmMotionAdapterTests.Pmx(["child", "parent"], ["smile"]),
        [Bone("child", 1, new(3, 5, 3)), Bone("parent", -1, new(1, 2, 3))]);
    private static PmxBone Bone(string name, int parent, Vector3 rest) => new()
    { Name = name, EnglishName = "", Position = rest, ParentIndex = parent, DeformDepth = 0, Flags = PmxBoneFlags.None };
    private static PmxMorph Morph(string name, PmxMorphType type, PmxMorph.GroupOffset[]? groups = null) => new()
    { Name = name, EnglishName = "", Type = type, ControlPanel = 0, GroupOffsets = groups ?? [] };
    private static PmxModel Copy(PmxModel p, PmxBone[]? bones = null, PmxMorph[]? morphs = null) => new()
    {
        Header = p.Header, Name = p.Name, EnglishName = p.EnglishName, Comment = p.Comment, EnglishComment = p.EnglishComment,
        Vertices = p.Vertices, Faces = p.Faces, Textures = p.Textures, Materials = p.Materials,
        Bones = bones ?? p.Bones, Morphs = morphs ?? p.Morphs, DisplayFrames = p.DisplayFrames,
        RigidBodies = p.RigidBodies, Joints = p.Joints, SoftBodies = p.SoftBodies
    };
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 3e-5f);
    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        Near(new Vector3(expected.M11, expected.M12, expected.M13), new Vector3(actual.M11, actual.M12, actual.M13));
        Near(new Vector3(expected.M21, expected.M22, expected.M23), new Vector3(actual.M21, actual.M22, actual.M23));
        Near(new Vector3(expected.M31, expected.M32, expected.M33), new Vector3(actual.M31, actual.M32, actual.M33)); Near(expected.Translation, actual.Translation);
    }
}
