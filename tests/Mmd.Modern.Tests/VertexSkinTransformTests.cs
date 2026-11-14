using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class VertexSkinTransformTests
{
    [Theory]
    [InlineData(SkinningKind.Bdef1)]
    [InlineData(SkinningKind.Bdef2)]
    [InlineData(SkinningKind.Bdef4)]
    [InlineData(SkinningKind.Sdef)]
    [InlineData(SkinningKind.Qdef)]
    public void EffectiveAffineMapMatchesOrdinarySkinWithInverseBindAndNonzeroMorph(SkinningKind kind)
    {
        RigBone[] bones = [new("a", new(1, -2, 3), -1), new("b", new(-3, 2, 1), -1),
            new("c", new(2, 1, -2), -1), new("d", new(-1, -3, 2), -1)];
        var binding = new SkinBinding(kind, 0, kind == SkinningKind.Bdef1 ? -1 : 1,
            kind is SkinningKind.Bdef4 or SkinningKind.Qdef ? 2 : -1,
            kind is SkinningKind.Bdef4 or SkinningKind.Qdef ? 3 : -1,
            kind == SkinningKind.Bdef1 ? new(1, 0, 0, 0)
                : kind is SkinningKind.Bdef4 or SkinningKind.Qdef ? new(.1f, .2f, .3f, .4f) : new(.35f, .65f, 0, 0),
            new(.2f, .5f, -.3f), new(1, 3, 2), new(-2, 1, 3));
        SceneVertex vertex = new(new(2, 3, -1), Vector3.Normalize(new(1, 2, 3)), new(.2f, .7f));
        var rig = new CharacterRig(bones, [vertex], [binding]);
        var animator = new CharacterAnimator(rig, new([]));
        var worlds = bones.Select((bone, i) => Matrix4x4.CreateFromQuaternion(
            Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(i + 1, 2, -1)), .4f + .3f * i))
            * Matrix4x4.CreateTranslation(bone.RestPosition + new Vector3(2 * i, -i, .5f * i))).ToArray();
        Vector3 delta = new(.7f, -1.3f, .5f);
        var morph = new MorphPose([delta], new Vector3[4], Enumerable.Repeat(Quaternion.Identity, 4));
        var sample = animator.SkinWorldPose(worlds, morph);
        var map = Assert.Single(animator.CalculateVertexSkinTransforms(worlds));
        Near(sample.Vertices[0].Position, Vector3.Transform(vertex.Position + delta, map));
        Near(sample.Vertices[0].Normal, Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, map)));
        Assert.Equal(vertex.Uv, sample.Vertices[0].Uv);
    }

    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 3e-5f);
}
