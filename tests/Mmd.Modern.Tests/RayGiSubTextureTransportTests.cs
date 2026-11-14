using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class RayGiSubTextureTransportTests
{
    [Fact]
    public void SideTablePreservesAbiCopiesInputAndRequiresCompleteFiniteRows()
    {
        Assert.Equal(160, Marshal.SizeOf<RayGiTriangleGpu>());
        Assert.Equal(96, Marshal.SizeOf<RayGiMaterialGpu>());
        Assert.Equal(32, Marshal.SizeOf<RayGiSubTextureUvGpu>());
        Assert.Equal(16, Marshal.OffsetOf<RayGiSubTextureUvGpu>(nameof(RayGiSubTextureUvGpu.Uv2)).ToInt32());
        var source = RayGiScene.FromOriginal(Style());
        RayGiSubTextureUvGpu[] rows = source.SubTextureUvs.ToArray();
        var saved = rows[0];
        var copy = new RayGiScene(source.Triangles, source.Materials, source.Texels, [], rows);
        rows[0] = default;
        Assert.Equal(saved, copy.SubTextureUvs[0]);
        Assert.Throws<ArgumentException>(() => new RayGiScene(source.Triangles, source.Materials, source.Texels));
        Assert.Throws<ArgumentException>(() => new RayGiScene(source.Triangles, source.Materials, source.Texels, [], new RayGiSubTextureUvGpu[2]));
        Assert.Throws<ArgumentException>(() => new RayGiScene(source.Triangles, source.Materials, source.Texels, [], [new(new(float.NaN), Vector4.Zero)]));
        Assert.Throws<ArgumentException>(() => new RayGiScene(source.Triangles, source.Materials, source.Texels, [], [new(Vector4.Zero, new(0, 0, float.PositiveInfinity, 0))]));
        var m = source.Materials[0];
        var invalid = new RayGiMaterialGpu(m.DiffuseFactor, m.Emission, m.DiffuseTexture,
            new(m.SphereTexture.X,m.SphereTexture.Y,m.SphereTexture.Z,4),m.Parameters,m.Flags);
        Assert.Throws<ArgumentException>(() => new RayGiScene(source.Triangles, [invalid], source.Texels, [], source.SubTextureUvs));
    }

    [Fact]
    public void AdapterIndexesUv1AndInterpolatesWithoutUv0NormalProjectionOrYFlip()
    {
        // Triangle indices are 2,0,1, deliberately different from vertex order.
        var style = Style(uvs: [new(.1f,.2f,8,9), new(.8f,.9f,10,11), new(.3f,.4f,12,13)]);
        var scene = RayGiScene.FromOriginal(style);
        Assert.Equal(new Vector4(.3f,.4f,.1f,.2f), scene.SubTextureUvs[0].Uv01);
        Assert.Equal(new Vector4(.8f,.9f,0,0), scene.SubTextureUvs[0].Uv2);
        var bary = new Vector2(.2f,.3f);
        var expected = new Vector2(.3f*.5f+.1f*.2f+.8f*.3f, .4f*.5f+.2f*.2f+.9f*.3f);
        Assert.Equal(expected, scene.SubTextureUv(0, bary));
        Assert.NotEqual(new Vector2(.99f), expected); // Every UV0 is .99.
        Assert.NotEqual(scene.SphereUv(0, bary), expected);
        var turned = RayGiScene.FromOriginal(new OriginalFrameScene(style, options: new(OriginalAlphaBlendMode.Normal, true, false,
            SphereRotationOverride: Matrix4x4.CreateRotationY(1))));
        Assert.Equal(expected, turned.SubTextureUv(0, bary));
    }

    [Theory]
    [InlineData(OriginalTextureSampling.PointClamp, false)]
    [InlineData(OriginalTextureSampling.PointWrap, true)]
    [InlineData(OriginalTextureSampling.LinearClamp, false)]
    [InlineData(OriginalTextureSampling.LinearWrap, true)]
    public void CandidatesSelectUv1AndRespectSelectedAddressing(OriginalTextureSampling sampling, bool accepted)
    {
        // 1.25 clamps to transparent right texel; wraps to opaque left texel.
        var scene = RayGiScene.FromOriginal(Style(uvs: Enumerable.Repeat(new Vector4(1.25f,.5f,0,0),3).ToArray(), sampling: sampling));
        Assert.Equal(accepted, scene.AcceptCandidate(0, new(.2f,.3f), -Vector3.UnitZ));
        var ray = new RayGiRay(new(.2f,.3f,1), -Vector3.UnitZ, 0, 2);
        Assert.Equal(accepted, scene.Bvh.TraceAny(ray));
        Assert.Equal(accepted, scene.Bvh.TraceBruteForce(ray, out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RawCutoffIgnoresColourWeightsInheritedAddAndUnusedUvChannels(bool diffuse, bool additive)
    {
        var style = Style(uvs: Enumerable.Repeat(new Vector4(.25f,.5f,0,0),3).ToArray(), diffuse: diffuse, additive: additive);
        var snapshot = style.Snapshot;
        SceneTexture[] textures = [new("fractional sphere alpha",2,1,[255,255,255,64,255,255,255,0]),snapshot.Textures[1]];
        style = new(new(snapshot.Vertices,snapshot.Indices,snapshot.Materials,textures,additionalUvs:snapshot.AdditionalUvs), style.Materials, style.Profile);
        var binding = style.Materials[0] with { TextureFactors = new(
            new(new(4,5,6,0), new(7,8,9,0)), new(new(9,8,7,0), new(6,5,4,0)), new(Vector4.Zero, Vector4.Zero)) };
        style = new(style.Snapshot, [binding], style.Profile);
        var first = RayGiScene.FromOriginal(style);
        float expectedAlpha = .5f * (diffuse ? 128/255f : 1) * (64/255f);
        foreach (float cutoff in new[] { expectedAlpha-.001f, expectedAlpha+.001f })
        {
            var changed = new OriginalStyleScene(style.Snapshot.WithMaterials([style.Snapshot.Materials[0] with { AlphaCutoff = cutoff }]), [binding], style.Profile);
            var scene = RayGiScene.FromOriginal(changed);
            Assert.Equal(cutoff < expectedAlpha, scene.AcceptCandidate(0, new(.2f,.3f), -Vector3.UnitZ));
        }
        var uvEdit = new SceneAdditionalUvs(4, Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f,.5f,999,-999), new(123),new(456),new(789)),3).ToArray());
        var edited = RayGiScene.FromOriginal(new OriginalStyleScene(style.Snapshot.WithAdditionalUvs(uvEdit), [binding], style.Profile));
        Assert.Equal(first.SubTextureUvs.ToArray(), edited.SubTextureUvs.ToArray());
        Assert.Equal(first.AcceptCandidate(0, new(.2f,.3f), -Vector3.UnitZ), edited.AcceptCandidate(0, new(.2f,.3f), -Vector3.UnitZ));
        Assert.Equal(uvEdit.Vertices[0], style.Snapshot.WithAdditionalUvs(uvEdit).AdditionalUvs!.Vertices[0]);
    }

    [Fact]
    public void OrdinaryPathIsEmptyMissingUvRejectsAndMaterialWrappersShareGeometryAndUvOwner()
    {
        var style = Style();
        var first = RayGiScene.FromOriginal(style);
        var ordinary = RayGiScene.FromOriginal(new OriginalStyleScene(style.Snapshot, [style.Materials[0] with { SphereMode = OriginalSphereMode.Multiply }], style.Profile));
        Assert.True(ordinary.SubTextureUvs.IsEmpty);
        Assert.Equal(Guid.Empty, ordinary.SubTextureUvIdentity);
        Assert.Equal(first.GeometryIdentity, ordinary.GeometryIdentity);
        var addWrapper = RayGiScene.FromOriginal(new OriginalStyleScene(style.Snapshot, [style.Materials[0] with { SubTextureAdditive = true }], style.Profile));
        Assert.Equal(first.SubTextureUvIdentity, addWrapper.SubTextureUvIdentity);
        Assert.Equal(first.GeometryIdentity, addWrapper.GeometryIdentity);
        Assert.Throws<ArgumentException>(() => new OriginalStyleScene(style.Snapshot.WithAdditionalUvs(null), style.Materials, style.Profile));
        var field = typeof(RayGiBvh).GetField("nodes", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var firstNodes = field.GetValue(first.Bvh);
        for (int i=0;i<20;i++)
        {
            var frame = new OriginalFrameScene(style, options: new(OriginalAlphaBlendMode.Normal,true,false,
                SphereRotationOverride: Matrix4x4.CreateRotationY(i*.1f)), materials: [new(false,false,false,SurfaceEnabled:i%2==0)]);
            var scene = RayGiScene.FromOriginal(frame);
            Assert.Equal(first.SubTextureUvIdentity, scene.SubTextureUvIdentity);
            Assert.Equal(first.GeometryIdentity, scene.GeometryIdentity);
            Assert.Same(firstNodes, field.GetValue(scene.Bvh));
        }
        var edited = RayGiScene.FromOriginal(new OriginalStyleScene(style.Snapshot.WithAdditionalUvs(new(1,
            Enumerable.Repeat(new SceneAdditionalUvVertex(new(.75f,.5f,0,0),default,default,default),3).ToArray())), style.Materials, style.Profile));
        Assert.NotEqual(first.SubTextureUvIdentity, edited.SubTextureUvIdentity);
        var cache = typeof(RayGiScene).GetField("SceneCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object?[] arguments = [style,null];
        Assert.True((bool)cache.GetType().GetMethod("TryGetValue")!.Invoke(cache,arguments)!);
        var entries = (System.Collections.ICollection)arguments[1]!.GetType().GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(arguments[1])!;
        Assert.InRange(entries.Count,1,4);
    }

    private static OriginalStyleScene Style(Vector4[]? uvs=null, OriginalTextureSampling sampling=OriginalTextureSampling.PointClamp, bool diffuse=false, bool additive=false)
    {
        uvs ??= Enumerable.Repeat(new Vector4(.25f,.5f,0,0),3).ToArray();
        SceneVertex[] vertices = [new(new(1,0,0),Vector3.UnitZ,new(.99f)),new(new(0,1,0),Vector3.UnitZ,new(.99f)),new(Vector3.Zero,Vector3.UnitZ,new(.99f))];
        SceneTexture[] textures = [new("sphere alpha",2,1,[255,255,255,255,255,255,255,0]),new("diffuse alpha",1,1,[255,255,255,128])];
        var snapshot = new SceneSnapshot(vertices,[2,0,1],[new("surface",0,3,new(1,1,1,.5f),Vector3.Zero,Vector3.Zero,1,DiffuseTexture:diffuse?1:-1,AlphaCutoff:.2f)],textures,
            additionalUvs:new(1,uvs.Select(v=>new SceneAdditionalUvVertex(v,default,default,default)).ToArray()));
        return new(snapshot,[new(Vector3.One,0,OriginalSphereMode.SubTexture) { SubTextureAdditive=additive }],
            OriginalStyleProfile.SelectedReference with { TextureSampling=sampling });
    }
}
