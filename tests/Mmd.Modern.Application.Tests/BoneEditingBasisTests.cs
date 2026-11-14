using System.Numerics;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Animation;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class BoneEditingBasisTests
{
    [Fact]
    public void GlobalDragRemovesParentRotationAndNeverParentTranslation()
    {
        var parent = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(30, -40, 50);
        var basis = BoneEditingBasis.Create(Bone(), parent, parent, BoneEditingSpace.Global);
        var pose = basis.TranslateInBasis(new(new(2, 3, 4), Quaternion.Identity), new(0, 2, 0));
        Near(new Vector3(4, 3, 4), pose.Translation);
        Near(new Vector3(0, 2, 0), Vector3.TransformNormal(pose.Translation - new Vector3(2, 3, 4), parent));
    }

    [Fact]
    public void LocalAxesUseCurrentBoneRotationAndOrthogonalizePmxXZ()
    {
        var bone = Bone(PmxBoneFlags.LocalAxis, new(0, 2, 0), new(0, 1, 4));
        var world = Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(9, 8, 7);
        var basis = BoneEditingBasis.Create(bone, Matrix4x4.Identity, world, BoneEditingSpace.Local);
        Near(Vector3.TransformNormal(Vector3.UnitY, world), basis.XAxis);
        Near(Vector3.TransformNormal(-Vector3.UnitX, world), basis.YAxis);
        Near(Vector3.TransformNormal(Vector3.UnitZ, world), basis.ZAxis);
        Near(world.Translation, basis.Origin);
        Near(basis.XAxis * 2 + basis.ZAxis * 3, basis.TranslationToWorld(new(2, 0, 3)));
    }

    [Fact]
    public void LocalWithoutDefinitionUsesAnimatedBoneBasis()
    {
        var world = Matrix4x4.CreateRotationZ(.9f) * Matrix4x4.CreateTranslation(7, 8, 9);
        var basis = BoneEditingBasis.Create(Bone(), Matrix4x4.Identity, world, BoneEditingSpace.Local);
        Near(Vector3.TransformNormal(Vector3.UnitX, world), basis.XAxis);
    }

    [Fact]
    public void ViewBasisIsCameraToWorldNotWorldToCameraOrProjection()
    {
        var camera = Matrix4x4.CreateRotationX(.4f) * Matrix4x4.CreateRotationY(.8f) * Matrix4x4.CreateTranslation(100, 200, 300);
        var basis = BoneEditingBasis.Create(Bone(), Matrix4x4.Identity, Matrix4x4.Identity, BoneEditingSpace.View, camera);
        Near(Vector3.TransformNormal(new(1, 2, -3), camera), basis.TranslationToWorld(new(1, 2, -3)));
        Near(Vector3.Zero, basis.Origin);
        Assert.Throws<ArgumentException>(() => BoneEditingBasis.Create(Bone(), Matrix4x4.Identity, Matrix4x4.Identity, BoneEditingSpace.View));
    }

    [Fact]
    public void WorldRotationUsesNonCommutingParentAndPreservesLocalTranslation()
    {
        var parent = Matrix4x4.CreateRotationY(.8f) * Matrix4x4.CreateTranslation(10, 20, 30);
        var initial = new VpdLocalBoneTransform(new(4, -2, 7), Quaternion.CreateFromAxisAngle(Vector3.UnitX, .5f));
        var world = Matrix4x4.CreateFromQuaternion(initial.Rotation) * Matrix4x4.CreateTranslation(initial.Translation) * parent;
        var basis = BoneEditingBasis.Create(Bone(), parent, world, BoneEditingSpace.Global);
        var delta = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .6f);
        var result = basis.RotateInWorld(initial, delta);
        var expected = Rotation(world) * Matrix4x4.CreateFromQuaternion(delta);
        Near(expected, Matrix4x4.CreateFromQuaternion(result.Rotation) * Rotation(parent));
        Assert.Equal(initial.Translation, result.Translation);
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitY, result.Rotation),
            Vector3.Transform(Vector3.UnitY, Quaternion.Concatenate(delta, initial.Rotation))) > .05f);
    }

    [Fact]
    public void BasisRotationUsesConjugationAndDoesNotRotateBoneOrigin()
    {
        var world = Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(8, 9, 10);
        var basis = BoneEditingBasis.Create(Bone(PmxBoneFlags.LocalAxis, Vector3.UnitZ, Vector3.UnitX),
            Matrix4x4.Identity, world, BoneEditingSpace.Local);
        var pose = new VpdLocalBoneTransform(new(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f));
        var delta = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f);
        var result = basis.RotateInBasis(pose, delta);
        var expected = Matrix4x4.CreateFromQuaternion(pose.Rotation)
            * Matrix4x4.CreateFromAxisAngle(basis.XAxis, .4f);
        Near(expected, Matrix4x4.CreateFromQuaternion(result.Rotation));
        Assert.Equal(pose.Translation, result.Translation);
    }

    [Fact]
    public void SolvedWorldIsOnlyGizmoOrientationAndDoesNotBakeConstraintIntoRawKey()
    {
        var solved = Matrix4x4.CreateRotationZ(1.1f) * Matrix4x4.CreateTranslation(100, 200, 300);
        var basis = BoneEditingBasis.Create(Bone(), Matrix4x4.Identity, solved, BoneEditingSpace.Local);
        var raw = new VpdLocalBoneTransform(new(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .2f));
        var moved = basis.TranslateInBasis(raw, Vector3.UnitX);
        Near(raw.Translation + basis.XAxis, moved.Translation);
        Assert.Equal(raw.Rotation, moved.Rotation);
        Near(Matrix4x4.CreateFromQuaternion(raw.Rotation), Matrix4x4.CreateFromQuaternion(basis.RotateInWorld(raw, Quaternion.Identity).Rotation));
    }

    [Fact]
    public void RegisterUsesExistingConflictConfirmationAndUndoWithTemplateBytes()
    {
        byte[] interpolation = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var old = new VmdBoneKeyframe { BoneName = "bone", Frame = 4, Translation = new(8, 9, 10), Interpolation = interpolation };
        var editor = new MotionEditor(new() { BoneKeyframes = [old] });
        var basis = BoneEditingBasis.Create(Bone(), Matrix4x4.Identity, Matrix4x4.Identity, BoneEditingSpace.Global);
        var pose = basis.TranslateInWorld(new(old.Translation, old.Rotation), Vector3.UnitX);
        var plan = basis.PrepareRegister(editor, 4, pose, old);
        Assert.Single(plan.Conflicts);
        Assert.Throws<InvalidOperationException>(() => editor.Apply(plan));
        editor.Apply(plan, confirmOverwrite: true);
        var key = Assert.Single(editor.ExportMotion().BoneKeyframes);
        Near(new Vector3(9, 9, 10), key.Translation); Assert.Equal(interpolation, key.Interpolation);
        Assert.True(editor.Undo()); Near(old.Translation, Assert.Single(editor.ExportMotion().BoneKeyframes).Translation);
        Assert.True(editor.Redo()); Near(pose.Translation, Assert.Single(editor.ExportMotion().BoneKeyframes).Translation);
    }

    [Fact]
    public void RegisteredDeltaReplaysThroughAnimatorWithNonzeroRestAndParent()
    {
        var parent = Matrix4x4.CreateRotationY(.8f) * Matrix4x4.CreateTranslation(5, 9, -2);
        var raw = new VpdLocalBoneTransform(new(1, -3, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f));
        var rest = new Vector3(2, 4, 6);
        var initial = Matrix4x4.CreateFromQuaternion(raw.Rotation) * Matrix4x4.CreateTranslation(rest + raw.Translation) * parent;
        var basis = BoneEditingBasis.Create(Bone(), parent, initial, BoneEditingSpace.Global);
        var translated = basis.TranslateInWorld(raw, new(3, -2, 4));
        var edited = basis.RotateInWorld(translated, Quaternion.CreateFromAxisAngle(Vector3.UnitX, .3f));
        var key = basis.CreateKey(17, edited);
        var rig = new CharacterRig([new("parent", Vector3.Zero, -1), new("bone", rest, 0)], [], []);
        var animator = new CharacterAnimator(rig, new MotionClip([
            new BoneTrack("parent", [new(17, parent.Translation, Quaternion.CreateFromRotationMatrix(parent), BoneCurves.Linear)]),
            new BoneTrack("bone", [new(17, key.Translation, key.Rotation, BoneCurves.Linear)])]));
        var actual = animator.Sample(17).BoneWorldMatrices[1];
        Near(initial.Translation + new Vector3(3, -2, 4), actual.Translation);
        Near(Rotation(initial) * Matrix4x4.CreateRotationX(.3f), Rotation(actual));
    }

    [Fact]
    public void NewKeyHasLinearCurvesAndFixedAxisDoesNotInventNewAnimationLimits()
    {
        var basis = BoneEditingBasis.Create(Bone(PmxBoneFlags.FixedAxis), Matrix4x4.Identity, Matrix4x4.Identity, BoneEditingSpace.Global);
        var edited = basis.RotateInWorld(new(Vector3.Zero, Quaternion.Identity), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .6f));
        var key = basis.CreateKey(12, edited);
        Assert.Equal("bone", key.BoneName); Assert.Equal(12u, key.Frame);
        Near(Matrix4x4.CreateRotationY(.6f), Matrix4x4.CreateFromQuaternion(key.Rotation));
        Assert.Equal(64, key.Interpolation.Count);
        Assert.Equal(PmmMotionAdapter.ConvertBoneInterpolation([20,20,107,107,20,20,107,107,20,20,107,107,20,20,107,107]), key.Interpolation);
        Assert.Throws<ArgumentException>(() => basis.CreateKey(1, edited, new() { BoneName = "other" }));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 1)]
    [InlineData(1, 0, 0, 2, 0, 0)]
    [InlineData(float.NaN, 0, 0, 0, 0, 1)]
    public void InvalidPmxLocalAxesAreRejected(float x, float y, float z, float zx, float zy, float zz)
    {
        Assert.Throws<ArgumentException>(() => BoneEditingBasis.Create(Bone(PmxBoneFlags.LocalAxis, new(x,y,z), new(zx,zy,zz)),
            Matrix4x4.Identity, Matrix4x4.Identity, BoneEditingSpace.Local));
    }

    [Theory]
    [InlineData("parent-scale")]
    [InlineData("bone-reflection")]
    [InlineData("view-projection")]
    public void NonRigidMatricesCannotSilentlyDistortEditAmounts(string invalid)
    {
        var parent = invalid == "parent-scale" ? Matrix4x4.CreateScale(2) : Matrix4x4.Identity;
        var bone = invalid == "bone-reflection" ? Matrix4x4.CreateScale(-1,1,1) : Matrix4x4.Identity;
        var view = invalid == "view-projection" ? Matrix4x4.CreatePerspectiveFieldOfView(1,1,.1f,100) : Matrix4x4.Identity;
        Assert.Throws<ArgumentException>(() => BoneEditingBasis.Create(Bone(), parent, bone, BoneEditingSpace.View, view));
    }

    [Fact]
    public void NonFiniteInputsAndOverflowFailBeforeEditorChanges()
    {
        var basis = BoneEditingBasis.Create(Bone(), Matrix4x4.Identity, Matrix4x4.Identity, BoneEditingSpace.Global);
        Assert.Throws<ArgumentException>(() => basis.TranslateInWorld(new(Vector3.Zero, Quaternion.Identity), new(float.NaN,0,0)));
        Assert.Throws<ArgumentException>(() => basis.TranslateInWorld(new(new(float.MaxValue,0,0), Quaternion.Identity), new(float.MaxValue,0,0)));
        Assert.Throws<ArgumentException>(() => basis.RotateInWorld(new(Vector3.Zero, Quaternion.Identity), default));
        Assert.Throws<ArgumentOutOfRangeException>(() => BoneEditingBasis.Create(Bone(), Matrix4x4.Identity, Matrix4x4.Identity, (BoneEditingSpace)99));
    }

    private static PmxBone Bone(PmxBoneFlags flags = 0, Vector3 x = default, Vector3 z = default) => new()
    { Name="bone", EnglishName="bone", Position=new(2,4,6), ParentIndex=-1, DeformDepth=0, Flags=flags, LocalXAxis=x, LocalZAxis=z, FixedAxis=Vector3.UnitX };
    private static Matrix4x4 Rotation(Matrix4x4 m) { m.Translation=Vector3.Zero; return m; }
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual),0,2e-5f);
    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        Near(Vector3.TransformNormal(Vector3.UnitX, expected), Vector3.TransformNormal(Vector3.UnitX, actual));
        Near(Vector3.TransformNormal(Vector3.UnitY, expected), Vector3.TransformNormal(Vector3.UnitY, actual));
        Near(Vector3.TransformNormal(Vector3.UnitZ, expected), Vector3.TransformNormal(Vector3.UnitZ, actual));
        Near(expected.Translation, actual.Translation);
    }
}

