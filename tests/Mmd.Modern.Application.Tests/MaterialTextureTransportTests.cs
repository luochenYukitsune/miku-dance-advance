using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class MaterialTextureTransportTests
{
    [Fact]
    public void IdentityKeepsOriginalBindingsAndRawFactorsDoNotChangeOpacity()
    {
        var model=Model(1); var rest=EditedMotionProjectTests.Rest(model);
        using var controller=new CharacterAnimationController(model,Motion(),rest.Surface,rest.Outline!);
        var zero=controller.Sample(0);
        Assert.Same(rest.Surface.Materials[0],zero.Surface.Materials[0]);
        Assert.True(zero.Surface.Materials[0].TextureFactors.IsIdentity);
        var half=controller.Sample(5);
        var factors=half.Surface.Materials[0].TextureFactors;
        Assert.Equal(new Vector4(.5f,1,1.5f,2),factors.Diffuse.Add);
        Assert.Equal(new Vector4(2.5f,3,3.5f,4),factors.Sphere.Add);
        Assert.Equal(new Vector4(4.5f,5,5.5f,6),factors.Toon.Add);
        Assert.Equal(Vector4.One,factors.Diffuse.Multiply);
        Assert.Equal(1,half.Surface.Snapshot.Materials[0].Diffuse.W);
        Assert.True(zero.Surface.Materials[0].TextureFactors.IsIdentity);
        Assert.True(rest.Surface.Materials[0].TextureFactors.IsIdentity);
        var disabled=controller.Sample(5,enableMorphs:false);
        Assert.Same(rest.Surface.Materials[0],disabled.Surface.Materials[0]);
        Assert.Equal(factors,controller.Sample(5).Surface.Materials[0].TextureFactors);
    }

    [Fact]
    public void EvaluatedMultipliersKeepAllFourComponentsWithoutApplyingThemAsAlpha()
    {
        var model=Model(1,multiply:true); var rest=EditedMotionProjectTests.Rest(model);
        using var controller=new CharacterAnimationController(model,Motion(),rest.Surface,rest.Outline!);
        var half=controller.Sample(5);
        var factors=half.Surface.Materials[0].TextureFactors;
        Assert.Equal(new Vector4(1,1.5f,2,2.5f),factors.Diffuse.Multiply);
        Assert.Equal(new Vector4(3,3.5f,4,4.5f),factors.Sphere.Multiply);
        Assert.Equal(new Vector4(5,5.5f,6,6.5f),factors.Toon.Multiply);
        Assert.Equal(Vector4.Zero,factors.Diffuse.Add);
        Assert.Equal(Vector4.Zero,factors.Sphere.Add);
        Assert.Equal(Vector4.Zero,factors.Toon.Add);
        Assert.Equal(1,half.Surface.Snapshot.Materials[0].Diffuse.W);
    }

    [Fact]
    public void LocalPacketsSurviveUnequalActorsFastRecompositionAndVisibilityChanges()
    {
        var firstModel=Model(1); var secondModel=Model(2);
        var layout=new SceneCompositionEditor();
        var first=layout.Add("first","first.pmx",ModelRole.Character,EditedMotionProjectTests.Rest(firstModel));
        var second=layout.Add("second","second.pmx",ModelRole.Character,EditedMotionProjectTests.Rest(secondModel));
        var firstMotion=Motion(visibleTrack:true); var secondMotion=Motion(scale:2);
        using var session=new SceneAnimationSession(layout.Objects,
            [new(first.Id,firstModel,firstMotion,PhysicsEnabled:false),new(second.Id,secondModel,secondMotion,PhysicsEnabled:false)],
            _=>throw new InvalidOperationException("No physics in texture transport fixture."),cancellationToken:TestContext.Current.CancellationToken);
        session.Seek(2,TestContext.Current.CancellationToken);
        var old=session.Frame;
        Assert.Equal(9,old.Surface.Snapshot.Vertices.Length);
        Assert.Equal(3,old.Surface.Materials.Count);
        Assert.Equal(.2f,old.Surface.Materials[0].TextureFactors.Diffuse.Add.X);
        Assert.Equal(.4f,old.Surface.Materials[1].TextureFactors.Diffuse.Add.X);
        Assert.Equal(.8f,old.Surface.Materials[2].TextureFactors.Diffuse.Add.X);
        session.Seek(3,TestContext.Current.CancellationToken); // Same visible topology: WithVertices path.
        var fast=session.Frame;
        Assert.Equal(.3f,fast.Surface.Materials[0].TextureFactors.Diffuse.Add.X);
        Assert.Equal(.6f,fast.Surface.Materials[1].TextureFactors.Diffuse.Add.X);
        Assert.Equal(1.2f,fast.Surface.Materials[2].TextureFactors.Diffuse.Add.X);
        session.Seek(5,TestContext.Current.CancellationToken); // First actor hidden: source ranges rebuild.
        Assert.Equal(6,session.Frame.Surface.Snapshot.Vertices.Length);
        Assert.Equal(2,session.Frame.Surface.Materials.Count);
        Assert.Equal(1,session.Frame.Surface.Materials[0].TextureFactors.Diffuse.Add.X);
        Assert.Equal(2,session.Frame.Surface.Materials[1].TextureFactors.Diffuse.Add.X);
        session.Seek(10,TestContext.Current.CancellationToken);
        Assert.Equal(3,session.Frame.Surface.Materials.Count);
        Assert.Equal(1,session.Frame.Surface.Materials[0].TextureFactors.Diffuse.Add.X);
        Assert.Equal(2,session.Frame.Surface.Materials[1].TextureFactors.Diffuse.Add.X);
        Assert.Equal(4,session.Frame.Surface.Materials[2].TextureFactors.Diffuse.Add.X);
        Assert.Equal(.2f,old.Surface.Materials[0].TextureFactors.Diffuse.Add.X);
        Assert.Equal(.6f,fast.Surface.Materials[1].TextureFactors.Diffuse.Add.X);
    }

    [Fact]
    public void CombineAndWithVerticesRetainAllSixIndependentVectors()
    {
        var model=Model(1); var rest=EditedMotionProjectTests.Rest(model);
        var factors=new OriginalMaterialTextureFactors(new(new(.5f),new(1)),new(new(2),new(-3)),new(new(4),new(5)));
        var binding=rest.Surface.Materials[0] with {TextureFactors=factors};
        var local=new OriginalFrameScene(new(rest.Surface.Snapshot,[binding],rest.Surface.Profile),rest.Outline,rest.OutlineOptions,
            rest.Options,rest.Materials);
        var combined=ProjectSceneAssembler.Combine([rest,local],Vector3.One,Vector3.UnitY);
        Assert.True(combined.Surface.Materials[0].TextureFactors.IsIdentity);
        Assert.Equal(factors,combined.Surface.Materials[1].TextureFactors);
        var changed=factors with {Toon=factors.Toon with {Add=new(10,11,12,13)}};
        var next=new OriginalFrameScene(new(local.Surface.Snapshot,[binding with {TextureFactors=changed}],local.Surface.Profile),
            local.Outline,local.OutlineOptions,local.Options,local.Materials);
        var fast=ProjectSceneAssembler.WithVertices(combined,[rest,next]);
        Assert.True(fast.Surface.Materials[0].TextureFactors.IsIdentity);
        Assert.Equal(changed,fast.Surface.Materials[1].TextureFactors);
        Assert.Equal(factors,combined.Surface.Materials[1].TextureFactors);
        Assert.Equal(factors,binding.TextureFactors);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void NonfiniteRawVectorsRejectBeforePublishingScene(int vector)
    {
        var rest=EditedMotionProjectTests.Rest(Model(1));
        var bad=new Vector4(1,2,3,float.NaN); var value=OriginalMaterialTextureFactors.Identity;
        value=vector switch
        {
            0=>value with {Diffuse=value.Diffuse with {Multiply=bad}},
            1=>value with {Diffuse=value.Diffuse with {Add=bad}},
            2=>value with {Sphere=value.Sphere with {Multiply=bad}},
            3=>value with {Sphere=value.Sphere with {Add=bad}},
            4=>value with {Toon=value.Toon with {Multiply=bad}},
            _=>value with {Toon=value.Toon with {Add=bad}}
        };
        Assert.False(value.IsFinite);
        Assert.Throws<ArgumentException>(()=>new OriginalStyleScene(rest.Surface.Snapshot,
            [rest.Surface.Materials[0] with {TextureFactors=value}],rest.Surface.Profile));
        Assert.True(rest.Surface.Materials[0].TextureFactors.IsIdentity);
    }

    [Fact]
    public void OverflowingEvaluatedFactorsLeavePriorPublishedPoseAndChannelsIntact()
    {
        var model=Model(1,overflow:true); var rest=EditedMotionProjectTests.Rest(model);
        using var controller=new CharacterAnimationController(model,Motion(scale:2),rest.Surface,rest.Outline!);
        var previous=controller.Sample(0); var channels=controller.MorphChannels; var bones=controller.BoneWorldMatrices;
        Assert.Throws<ArgumentException>(()=>controller.Sample(10));
        Assert.Same(channels,controller.MorphChannels); Assert.Same(bones,controller.BoneWorldMatrices);
        Assert.True(previous.Surface.Materials[0].TextureFactors.IsIdentity);
        Assert.Same(previous.Surface,controller.Sample(0).Surface);
    }

    private static VmdMotion Motion(float scale=1,bool visibleTrack=false)=>new()
    {
        MorphKeyframes=[new(){MorphName="texture",Weight=0},new(){MorphName="texture",Frame=10,Weight=scale}],
        IkKeyframes=visibleTrack ? [new(){Frame=0,Visible=true},new(){Frame=5,Visible=false},new(){Frame=10,Visible=true}] : []
    };

    private static PmxModel Model(int materialCount,bool overflow=false,bool multiply=false)
    {
        var baseline=EditedMotionProjectTests.Model();
        return new()
        {
            Header=baseline.Header,Name="texture-transport",EnglishName="",Comment="",EnglishComment="",
            Vertices=Enumerable.Range(0,materialCount).SelectMany(material=>baseline.Vertices.Select(vertex=>new PmxVertex
            {
                Position=vertex.Position+new Vector3(material*2,0,0),Normal=vertex.Normal,Uv=vertex.Uv,AdditionalUvs=[],
                WeightType=vertex.WeightType,BoneIndices=vertex.BoneIndices,BoneWeights=vertex.BoneWeights,EdgeScale=vertex.EdgeScale
            })).ToArray(),
            Faces=Enumerable.Range(0,materialCount).Select(index=>new PmxTriangle(index*3,index*3+1,index*3+2)).ToArray(),
            Materials=Enumerable.Range(0,materialCount).Select(index=>new PmxMaterial
            {
                Name=$"material-{index}",EnglishName="",Diffuse=Vector4.One,Specular=Vector3.Zero,SpecularPower=1,Ambient=Vector3.Zero,
                DrawMode=PmxDrawModeFlags.None,EdgeColor=Vector4.Zero,EdgeSize=0,TextureIndex=-1,SphereTextureIndex=-1,
                SphereMode=PmxSphereMode.None,ToonMode=PmxToonMode.Separate,ToonTextureIndex=-1,Memo="",FaceVertexCount=3
            }).ToArray(),
            Bones=baseline.Bones,Textures=[],DisplayFrames=[],RigidBodies=[],Joints=[],SoftBodies=[],
            Morphs=[new(){Name="texture",EnglishName="",ControlPanel=1,Type=PmxMorphType.Material,
                MaterialOffsets=Enumerable.Range(0,materialCount).Select(index=>new PmxMorph.MaterialOffset(index,
                    multiply?PmxMaterialMorphOperation.Multiply:PmxMaterialMorphOperation.Add,
                    multiply?Vector4.One:Vector4.Zero,multiply?Vector3.One:Vector3.Zero,multiply?1:0,
                    multiply?Vector3.One:Vector3.Zero,multiply?Vector4.One:Vector4.Zero,multiply?1:0,
                    overflow?new Vector4(float.MaxValue):new Vector4(1,2,3,4)*(index+1),
                    new Vector4(5,6,7,8)*(index+1),new Vector4(9,10,11,12)*(index+1))).ToArray()}]
        };
    }
}
