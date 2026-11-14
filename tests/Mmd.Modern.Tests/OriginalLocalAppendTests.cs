using System.Numerics;
using Mmd.Modern.Animation;
using Xunit;

namespace Mmd.Modern.Tests;

// Original9.32 BA200 and BD770->B9F60->BA200 paired LocalAppend0x80 corpus;
// see artifacts/stage-20261021-append-local-original. These are bounded original
// working-field fixtures, with raw REA loader flag/read and normalization audit.
public sealed class OriginalLocalAppendTests
{
    [Theory]
    [MemberData(nameof(OriginalAppendWeightTests.OriginalCases), MemberType = typeof(OriginalAppendWeightTests))]
    public void MmdLocalFlagMatchesOriginalNegativeWBranch(bool sourceAfter, bool recipientAfter,
        float weight, float[] source, float[] recipient, float[] expected)
    {
        foreach (float sign in new[] { 1f, -1f })
        {
            Quaternion Q(float[] q) => new(sign*q[0], sign*q[1], sign*q[2], sign*q[3]);
            BoneTrack Track(string name, Vector3 t, float[] q) => new(name, [new(0,t,Q(q),BoneCurves.Linear)]);
            RigBone[] bones = [new("source",Vector3.Zero,-1,AfterPhysics:sourceAfter),
                new("recipient",Vector3.Zero,-1,DeformLayer:1,AfterPhysics:recipientAfter,Append:new(0,weight,true,true,true)),
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

    public static IEnumerable<object[]> OriginalChainedCases()
    {
        yield return [false, false, new float[] { 0.0983405568f, 0.491702784f, -0.147510835f, 0.852524522f, 0.522529544f, -0.209011817f, 0.313517726f, 0.764842187f, -0.124248176f, 0.207080293f, 0.414160585f, 0.877582562f, 0f, 0f, 0f, 1f }, new float[] { 0.472937822f, -0.154804558f, -0.867389977f, 0f, 0.348221868f, 0.937139392f, 0.0226124823f, 0f, 0.809364736f, -0.312738448f, 0.497115016f, 0f, 0.400000006f, -0.200000003f, 0.800000012f, 1f, 0.661165714f, 0.72026968f, 0.209932283f, 0f, -0.530164003f, 0.250566661f, 0.810026348f, 0f, 0.53083533f, -0.646860182f, 0.547527015f, 0f, 0.439999998f, -0.420000017f, 0.879999995f, 1f, 0.186263859f, 0.878179193f, -0.440575719f, 0f, -0.700417399f, 0.433154106f, 0.567267835f, 0f, 0.68900001f, 0.202925444f, 0.695773125f, 0f, -0.180000007f, 0.390000015f, 0.540000021f, 1f, 0.186263859f, 0.878179193f, -0.440575719f, 0f, -0.700417399f, 0.433154106f, 0.567267835f, 0f, 0.68900001f, 0.202925444f, 0.695773125f, 0f, 0.00626385212f, 1.26817921f, 0.0994243026f, 1f }];
        yield return [true, true, new float[] { 0.0983405568f, 0.491702784f, -0.147510835f, 0.852524522f, 0.522529544f, -0.209011817f, 0.313517726f, 0.764842187f, -0.124248176f, 0.207080293f, 0.414160585f, 0.877582562f, 0f, 0f, 0f, 1f }, new float[] { 0.472937822f, -0.154804558f, -0.867389977f, 0f, 0.348221868f, 0.937139392f, 0.0226124823f, 0f, 0.809364736f, -0.312738448f, 0.497115016f, 0f, 0.400000006f, -0.200000003f, 0.800000012f, 1f, 0.661165714f, 0.72026968f, 0.209932283f, 0f, -0.530164003f, 0.250566661f, 0.810026348f, 0f, 0.53083533f, -0.646860182f, 0.547527015f, 0f, 0.439999998f, -0.420000017f, 0.879999995f, 1f, 0.186263859f, 0.878179193f, -0.440575719f, 0f, -0.700417399f, 0.433154106f, 0.567267835f, 0f, 0.68900001f, 0.202925444f, 0.695773125f, 0f, -0.180000007f, 0.390000015f, 0.540000021f, 1f, 0.186263859f, 0.878179193f, -0.440575719f, 0f, -0.700417399f, 0.433154106f, 0.567267835f, 0f, 0.68900001f, 0.202925444f, 0.695773125f, 0f, 0.00626385212f, 1.26817921f, 0.0994243026f, 1f }];
        yield return [false, true, new float[] { 0.0983405568f, 0.491702784f, -0.147510835f, 0.852524522f, 0.522529544f, -0.209011817f, 0.313517726f, 0.764842187f, -0.124248176f, 0.207080293f, 0.414160585f, 0.877582562f, 0f, 0f, 0f, 1f }, new float[] { 0.472937822f, -0.154804558f, -0.867389977f, 0f, 0.348221868f, 0.937139392f, 0.0226124823f, 0f, 0.809364736f, -0.312738448f, 0.497115016f, 0f, 0.400000006f, -0.200000003f, 0.800000012f, 1f, 0.661165714f, 0.72026968f, 0.209932283f, 0f, -0.530164003f, 0.250566661f, 0.810026348f, 0f, 0.53083533f, -0.646860182f, 0.547527015f, 0f, 0.439999998f, -0.420000017f, 0.879999995f, 1f, 0.186263859f, 0.878179193f, -0.440575719f, 0f, -0.700417399f, 0.433154106f, 0.567267835f, 0f, 0.68900001f, 0.202925444f, 0.695773125f, 0f, -0.300000012f, 0.450000018f, 0.300000012f, 1f, 0.186263859f, 0.878179193f, -0.440575719f, 0f, -0.700417399f, 0.433154106f, 0.567267835f, 0f, 0.68900001f, 0.202925444f, 0.695773125f, 0f, -0.113736153f, 1.32817921f, -0.140575707f, 1f }];
    }

    [Theory]
    [MemberData(nameof(OriginalChainedCases))]
    public void MmdLocalFlagReadsEvaluatedAppendSource(bool sourceAfter, bool recipientAfter, float[] rotations, float[] expected)
    {
        Quaternion Q(int i) => new(rotations[i*4], rotations[i*4+1], rotations[i*4+2], rotations[i*4+3]);
        BoneTrack Track(string name, Vector3 t, int i) => new(name, [new(0,t,Q(i),BoneCurves.Linear)]);
        RigBone[] bones = [new("donor",Vector3.Zero,-1,AfterPhysics:sourceAfter),
            new("source",Vector3.Zero,-1,DeformLayer:1,AfterPhysics:sourceAfter,Append:new(0,.6f,false,true,true)),
            new("recipient",Vector3.Zero,-1,DeformLayer:2,AfterPhysics:recipientAfter,Append:new(1,.5f,true,true,true)),
            new("child",Vector3.UnitX,2,DeformLayer:3,AfterPhysics:recipientAfter)];
        var sample = new CharacterAnimator(new(bones,[],[],evaluationSchedule:BoneEvaluationSchedule.Mmd),
            new([Track("donor",new(.4f,-.2f,.8f),0),Track("source",new(.2f,-.3f,.4f),1),Track("recipient",new(-.4f,.6f,.1f),2)])).Sample(0);
        for(int b=0;b<4;b++)
        {
            var m=sample.BoneWorldMatrices[b];
            float[] actual=[m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
            for(int k=0;k<16;k++) Assert.InRange(MathF.Abs(actual[k]-expected[b*16+k]),0,3e-6f);
        }
    }
}
