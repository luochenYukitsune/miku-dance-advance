using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;
namespace Mmd.Modern.Tests;

public sealed class AppearanceMorphIntegrationTests
{
    [Fact] public void MaterialOnlyTimelineInterpolatesAndToggleSeekDoNotAccumulate()
    {
        var morph=new PmxMorph{Name="fade",EnglishName="",ControlPanel=1,Type=PmxMorphType.Material,
            MaterialOffsets=[new(-1,PmxMaterialMorphOperation.Multiply,new(1,1,1,0),Vector3.One,1,Vector3.One,new(1,1,1,0),1,Vector4.One,Vector4.One,Vector4.One)]};
        using var controller=MorphApplicationTests.Controller(MorphApplicationTests.Model([morph]),new(){MorphKeyframes=[MorphApplicationTests.Key("fade",0,0),MorphApplicationTests.Key("fade",10,1)]});
        Assert.True(controller.SupportsMorphs);Assert.Equal(1,controller.MatchedMorphTrackCount);
        var halfway=controller.Sample(5);Assert.Equal(.5f,halfway.Surface.Snapshot.Materials[0].Diffuse.W);Assert.Equal(.5f,halfway.Outline.Materials[0].Color.W);
        Assert.Equal(1,controller.Sample(5,enableMorphs:false).Surface.Snapshot.Materials[0].Diffuse.W);
        Assert.Equal(.5f,controller.Sample(5).Surface.Snapshot.Materials[0].Diffuse.W);
        Assert.Equal(.75f,controller.Advance(2.5,.01).Surface.Snapshot.Materials[0].Diffuse.W);
        Assert.Equal(0,controller.Sample(10).Surface.Snapshot.Materials[0].Diffuse.W);Assert.Equal(1,controller.Sample(0).Surface.Snapshot.Materials[0].Diffuse.W);
    }
    [Fact] public void DynamicMaterialEligibilityUsesCurrentEdgeAlphaAndExactDiffuseSentinel()
    {
        var source=MorphApplicationTests.Model([]);var material=source.Materials[0];
        var model=new PmxModel{Header=source.Header,Name=source.Name,EnglishName="",Comment="",EnglishComment="",Vertices=source.Vertices,Faces=source.Faces,Textures=[],
            Bones=source.Bones,Morphs=[],DisplayFrames=[],RigidBodies=[],Joints=[],SoftBodies=[],Materials=[new(){Name=material.Name,EnglishName="",Diffuse=material.Diffuse,Specular=material.Specular,
                SpecularPower=material.SpecularPower,Ambient=material.Ambient,DrawMode=PmxDrawModeFlags.CastSelfShadow|PmxDrawModeFlags.GroundShadow|PmxDrawModeFlags.ReceiveSelfShadow,
                EdgeColor=Vector4.Zero,EdgeSize=material.EdgeSize,TextureIndex=-1,SphereTextureIndex=-1,SphereMode=PmxSphereMode.None,ToonMode=PmxToonMode.Separate,ToonTextureIndex=-1,Memo="",FaceVertexCount=3}]};
        var surface=PmxOriginalStyleAdapter.FromRestPose(model).Scene;var outline=PmxOriginalOutlineAdapter.FromRestPose(model);
        Assert.False(PmxOriginalFrameAdapter.FromEvaluatedMaterials(model,surface,outline)[0].CastSelfShadow);
        var visible=new OriginalOutlineData(outline.Vertices,[outline.Materials[0] with{Color=Vector4.One}]);
        var flags=PmxOriginalFrameAdapter.FromEvaluatedMaterials(model,surface,visible)[0];Assert.True(flags.CastSelfShadow&&flags.CastGroundShadow);
        var sentinel=new OriginalStyleScene(surface.Snapshot.WithMaterials([surface.Snapshot.Materials[0] with{Diffuse=new(1,1,1,.98f)}]),surface.Materials);
        flags=PmxOriginalFrameAdapter.FromEvaluatedMaterials(model,sentinel,visible)[0];Assert.False(flags.CastSelfShadow);Assert.True(flags.CastGroundShadow);
    }
}
