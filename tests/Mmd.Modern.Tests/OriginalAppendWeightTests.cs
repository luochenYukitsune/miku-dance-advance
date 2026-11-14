using System.Numerics;
using Mmd.Modern.Animation;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OriginalAppendWeightTests
{
    // Frozen from original 9.32 BA200, SHA 882c97dc...4387156. These noncommuting
    // poses enter D3DX's negative-W matrix extraction and its acos(W)-3.14 branch.
    // See artifacts/stage-20261021-core-completion/complex-bones-original.
    public static IEnumerable<object[]> OriginalCases()
    {
        yield return [false, false, -1f, new float[] { -0.602904679f, 0.0921317887f, -0.648722303f, 0.455167062f }, new float[] { 0.324270916f, 0.295384704f, -0.0446317055f, 0.897554601f }, new float[] { 0.681584001f, -0.132505015f, 0.71964258f, 0f, -0.515239775f, -0.785237432f, 0.343408108f, 0f, 0.519587338f, -0.604850054f, -0.603477001f, 0f, -0.600000024f, 0.900000036f, -0.300000012f, 1f }];
        yield return [true, true, 0.5f, new float[] { -0.709920505f, 0.184944856f, 0.465259655f, 0.495319826f }, new float[] { 0.528526901f, 0.273997892f, -0.587074056f, 0.548569525f }, new float[] { 0.615572095f, -0.0235789418f, -0.787727714f, 0f, 0.604980767f, 0.654704571f, 0.453166783f, 0f, 0.505043745f, -0.755516946f, 0.417282701f, 0f, -0.300000012f, 0.450000018f, 0.300000012f, 1f }];
        yield return [false, true, 0.5f, new float[] { 0.246210108f, -0.29133495f, -0.781176327f, 0.494234839f }, new float[] { 0.207947135f, 0.0950047464f, 0.0255793637f, 0.973179214f }, new float[] { 0.566026092f, -0.772810698f, -0.287015826f, 0f, 0.790543199f, 0.410097092f, 0.454820573f, 0f, -0.233785838f, -0.48433876f, 0.843065917f, 0f, -0.300000012f, 0.450000018f, 0.300000012f, 1f }];
        yield return [false, true, 1.5f, new float[] { -0.799701459f, 0.274246364f, -0.226123204f, 0.483874783f }, new float[] { 0.112067867f, 0.411468826f, 0.441300113f, 0.789549497f }, new float[] { 0.885599256f, 0.292363256f, -0.360884637f, 0f, 0.239554197f, -0.953215957f, -0.184370339f, 0f, -0.397904247f, 0.076826781f, -0.914204121f, 0f, -0.099999994f, 0.150000006f, 0.700000048f, 1f }];
        yield return [false, true, 1.5f, new float[] { -0.437294377f, 0.502584338f, -0.575173197f, 0.47471929f }, new float[] { -0.62058977f, 0.173882701f, -0.131326585f, 0.753250604f }, new float[] { -0.15130049f, 0.726369202f, 0.670444369f, 0f, -0.854276001f, -0.437316775f, 0.281009197f, 0f, 0.497313142f, -0.530227721f, 0.686686337f, 0f, -0.099999994f, 0.150000006f, 0.700000048f, 1f }];
    }

    [Theory]
    [MemberData(nameof(OriginalCases))]
    public void MmdAppendMatchesOriginalNegativeWBranch(bool sourceAfter, bool recipientAfter,
        float weight, float[] source, float[] recipient, float[] expected)
    {
        foreach (float sign in new[] { 1f, -1f })
        {
            Quaternion Q(float[] q) => new(sign*q[0], sign*q[1], sign*q[2], sign*q[3]);
            BoneTrack Track(string name, Vector3 t, float[] q) => new(name, [new(0,t,Q(q),BoneCurves.Linear)]);
            RigBone[] bones = [new("source",Vector3.Zero,-1,AfterPhysics:sourceAfter),
                new("recipient",Vector3.Zero,-1,DeformLayer:1,AfterPhysics:recipientAfter,Append:new(0,weight,false,true,true)),
                new("child",Vector3.UnitX,1,DeformLayer:2,AfterPhysics:recipientAfter)];
            var animator = new CharacterAnimator(new(bones,[],[],evaluationSchedule:BoneEvaluationSchedule.Mmd),
                new([Track("source",new(.2f,-.3f,.4f),source),Track("recipient",new(-.4f,.6f,.1f),recipient)]));
            var sample = animator.Sample(0);
            var m = sample.BoneWorldMatrices[1];
            float[] actual = [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
            for (int i=0;i<16;i++) Assert.InRange(MathF.Abs(actual[i]-expected[i]),0,3e-6f);
            var expectedChild = new Vector3(expected[12]+expected[0],expected[13]+expected[1],expected[14]+expected[2]);
            Assert.InRange(Vector3.Distance(expectedChild,sample.BoneWorldMatrices[2].Translation),0,3e-6f);
        }
    }
}
