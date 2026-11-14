using System.Buffers.Binary;
using System.Numerics;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmxAcceptanceTests
{
    private const string DefaultCorpusRoot = @"D:\desktop\mmd渲染";

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "rich-w1-uv4.pmx");

    private static PmxModel ReadFixture()
    {
        using var stream = File.OpenRead(FixturePath);
        return PmxReader.Read(stream);
    }

    private static byte[] FixtureBytes() => File.ReadAllBytes(FixturePath);

    [Fact]
    public void ReadsHeaderAndJapaneseNames()
    {
        PmxModel model = ReadFixture();

        Assert.Equal(2.1f, model.Header.Version);
        Assert.Equal(8, model.Header.HeaderDataSize);
        Assert.Equal(PmxTextEncoding.Utf8, model.Header.TextEncoding);
        Assert.Equal(4, model.Header.AdditionalUvCount);
        Assert.Equal(4, model.Header.VertexIndexSize);
        Assert.Equal(2, model.Header.TextureIndexSize);
        Assert.Equal(4, model.Header.MaterialIndexSize);
        Assert.Equal(2, model.Header.BoneIndexSize);
        Assert.Equal(2, model.Header.MorphIndexSize);
        Assert.Equal(2, model.Header.RigidBodyIndexSize);
        Assert.True(model.Header.IsVersion21);

        Assert.Equal("テストモデル", model.Name);
        Assert.Equal("Test Model", model.EnglishName);
        Assert.Equal("合成PMX 2.1 テストデータ", model.Comment);
        Assert.Equal("Synthetic PMX 2.1 fixture", model.EnglishComment);
    }

    [Fact]
    public void ReadsVerticesWithFourAdditionalUvsAndAllWeightTypes()
    {
        PmxModel model = ReadFixture();

        Assert.Equal(8, model.Vertices.Count);
        Assert.All(model.Vertices, vertex => Assert.Equal(4, vertex.AdditionalUvs.Count));

        PmxVertex first = model.Vertices[0];
        Assert.Equal(new Vector3(0f, 1f, 0f), first.Position);
        Assert.Equal(new Vector3(0f, 1f, 0f), first.Normal);
        Assert.Equal(new Vector2(0f, 0f), first.Uv);
        Assert.Equal(new Vector4(0f, 0f, 0f, 1f), first.AdditionalUvs[0]);
        Assert.Equal(new Vector4(1f, 0.1f, 0.2f, 1f), first.AdditionalUvs[1]);
        Assert.Equal(1f, first.EdgeScale);
        Assert.Equal(PmxVertexWeightType.Bdef1, first.WeightType);
        Assert.Equal(new[] { 0, -1, -1, -1 }, first.BoneIndices);
        Assert.Equal(new[] { 1f, 0f, 0f, 0f }, first.BoneWeights);

        PmxVertex second = model.Vertices[1];
        Assert.Equal(new Vector3(0.5f, 1.25f, -0.125f), second.Position);
        Assert.Equal(new Vector2(0.01f, 0.02f), second.Uv);
        Assert.Equal(PmxVertexWeightType.Bdef2, second.WeightType);
        Assert.Equal(new[] { 0, 1, -1, -1 }, second.BoneIndices);
        Assert.Equal(new[] { 0.75f, 1f - 0.75f, 0f, 0f }, second.BoneWeights);

        PmxVertex bdef4 = model.Vertices[2];
        Assert.Equal(PmxVertexWeightType.Bdef4, bdef4.WeightType);
        Assert.Equal(new[] { 0, 1, 2, 3 }, bdef4.BoneIndices);
        Assert.Equal(new[] { 0.4f, 0.3f, 0.2f, 0.1f }, bdef4.BoneWeights);

        PmxVertex sdef = model.Vertices[3];
        Assert.Equal(PmxVertexWeightType.Sdef, sdef.WeightType);
        Assert.Equal(new[] { 1, 2, -1, -1 }, sdef.BoneIndices);
        Assert.Equal(new[] { 0.6f, 1f - 0.6f, 0f, 0f }, sdef.BoneWeights);
        Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), sdef.SdefC);
        Assert.Equal(new Vector3(0.4f, 0.5f, 0.6f), sdef.SdefR0);
        Assert.Equal(new Vector3(0.7f, 0.8f, 0.9f), sdef.SdefR1);

        PmxVertex qdef = model.Vertices[4];
        Assert.Equal(PmxVertexWeightType.Qdef, qdef.WeightType);
        Assert.Equal(new[] { 0, 2, 3, 4 }, qdef.BoneIndices);
        Assert.Equal(new[] { 0.25f, 0.25f, 0.25f, 0.25f }, qdef.BoneWeights);

        var distribution = model.Vertices
            .GroupBy(vertex => vertex.WeightType)
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(2, distribution[PmxVertexWeightType.Bdef1]);
        Assert.Equal(2, distribution[PmxVertexWeightType.Bdef2]);
        Assert.Equal(2, distribution[PmxVertexWeightType.Bdef4]);
        Assert.Equal(1, distribution[PmxVertexWeightType.Sdef]);
        Assert.Equal(1, distribution[PmxVertexWeightType.Qdef]);

        foreach (PmxVertex vertex in model.Vertices)
        {
            float sum = vertex.BoneWeights.Sum();
            Assert.InRange(sum, 0.99999f, 1.00001f);
        }
    }

    [Fact]
    public void ReadsFacesAndMaterials()
    {
        PmxModel model = ReadFixture();

        Assert.Equal(6, model.Faces.Count);
        Assert.Equal(new PmxTriangle(0, 1, 2), model.Faces[0]);
        Assert.Equal(new PmxTriangle(5, 6, 7), model.Faces[5]);

        Assert.Equal(3, model.Textures.Count);
        Assert.Equal("textures/rich/base.png", model.Textures[0]);
        Assert.Equal("toon/rich.bmp", model.Textures[2]);

        Assert.Equal(3, model.Materials.Count);
        PmxMaterial red = model.Materials[0];
        Assert.Equal("材質_赤", red.Name);
        Assert.Equal("RedMat", red.EnglishName);
        Assert.Equal(new Vector4(1f, 0.2f, 0.2f, 1f), red.Diffuse);
        Assert.Equal(0x13, (int)red.DrawMode);
        Assert.True(red.DrawMode.HasFlag(PmxDrawModeFlags.BothFaces));
        Assert.True(red.DrawMode.HasFlag(PmxDrawModeFlags.DrawEdge));
        Assert.Equal(1f, red.EdgeSize);
        Assert.Equal(0, red.TextureIndex);
        Assert.Equal(1, red.SphereTextureIndex);
        Assert.Equal(PmxSphereMode.Multiply, red.SphereMode);
        Assert.Equal(PmxToonMode.Separate, red.ToonMode);
        Assert.Equal(2, red.ToonTextureIndex);
        Assert.Equal("red material", red.Memo);
        Assert.Equal(6, red.FaceVertexCount);

        PmxMaterial blue = model.Materials[1];
        Assert.Equal("材質_青", blue.Name);
        Assert.Equal(-1, blue.SphereTextureIndex);
        Assert.Equal(PmxToonMode.Common, blue.ToonMode);
        Assert.Equal(1, blue.ToonTextureIndex);

        PmxMaterial green = model.Materials[2];
        Assert.Equal("材質_緑", green.Name);
        Assert.True(green.DrawMode.HasFlag(PmxDrawModeFlags.VertexColor));

        Assert.Equal(18, model.Materials.Sum(material => material.FaceVertexCount));
    }

    [Fact]
    public void ReadsBonesIncludingIkChain()
    {
        PmxModel model = ReadFixture();

        Assert.Equal(5, model.Bones.Count);

        PmxBone center = model.Bones[0];
        Assert.Equal("センター", center.Name);
        Assert.Equal("Center", center.EnglishName);
        Assert.Equal(-1, center.ParentIndex);
        Assert.True(center.Flags.HasFlag(PmxBoneFlags.TargetShowMode));
        Assert.Equal(1, center.LinkBoneIndex);
        Assert.True(center.Flags.HasFlag(PmxBoneFlags.DeformOuterParent));
        Assert.Equal(7, center.ExternalParentKey);

        PmxBone upperBody = model.Bones[1];
        Assert.Equal("上半身", upperBody.Name);
        Assert.Equal(new Vector3(0f, 0f, 1f), upperBody.FixedAxis);
        Assert.Equal(new Vector3(1f, 0f, 0f), upperBody.LocalXAxis);
        Assert.Equal(new Vector3(0f, 0f, 1f), upperBody.LocalZAxis);

        PmxBone arm = model.Bones[2];
        Assert.Equal(1, arm.AppendBoneIndex);
        Assert.Equal(0.5f, arm.AppendWeight);
        Assert.True(arm.Flags.HasFlag(PmxBoneFlags.AppendRotate));
        Assert.True(arm.Flags.HasFlag(PmxBoneFlags.AppendLocal));

        PmxBone elbow = model.Bones[3];
        Assert.Equal("ひじ", elbow.Name);
        Assert.True(elbow.Flags.HasFlag(PmxBoneFlags.Ik));
        Assert.Equal(4, elbow.IkTargetBoneIndex);
        Assert.Equal(40, elbow.IkIterationCount);
        Assert.Equal(0.034906585f, elbow.IkLimit);
        Assert.Equal(2, elbow.IkLinks.Count);
        Assert.Equal(2, elbow.IkLinks[0].BoneIndex);
        Assert.True(elbow.IkLinks[0].EnableLimit);
        Assert.Equal(new Vector3(-1f, -1f, -1f), elbow.IkLinks[0].LimitMin);
        Assert.Equal(new Vector3(1f, 1f, 1f), elbow.IkLinks[0].LimitMax);
        Assert.Equal(1, elbow.IkLinks[1].BoneIndex);
        Assert.False(elbow.IkLinks[1].EnableLimit);

        Assert.Equal("手先", model.Bones[4].Name);
    }

    [Fact]
    public void ReadsEveryMorphKindIncludingPmx21Extensions()
    {
        PmxModel model = ReadFixture();

        Assert.Equal(8, model.Morphs.Count);
        Assert.Equal("グループ", model.Morphs[0].Name);
        Assert.Equal(PmxMorphType.Group, model.Morphs[0].Type);
        Assert.Equal(2, model.Morphs[0].GroupOffsets.Count);
        Assert.Equal(new PmxMorph.GroupOffset(1, 0.5f), model.Morphs[0].GroupOffsets[0]);

        Assert.Equal("位置", model.Morphs[1].Name);
        Assert.Equal(PmxMorphType.Position, model.Morphs[1].Type);
        Assert.Equal(2, model.Morphs[1].PositionOffsets.Count);
        Assert.Equal(new PmxMorph.PositionOffset(0, new Vector3(0.1f, 0.2f, 0.3f)),
            model.Morphs[1].PositionOffsets[0]);

        Assert.Equal("UV", model.Morphs[2].Name);
        Assert.Equal(PmxMorphType.Uv, model.Morphs[2].Type);
        Assert.Equal(2, model.Morphs[2].UvOffsets.Count);

        Assert.Equal("ボーン", model.Morphs[3].Name);
        Assert.Equal(PmxMorphType.Bone, model.Morphs[3].Type);
        Assert.Single(model.Morphs[3].BoneOffsets);
        Assert.Equal(2, model.Morphs[3].BoneOffsets[0].BoneIndex);
        Assert.Equal(new Vector3(0.5f, 0f, 0f), model.Morphs[3].BoneOffsets[0].Position);
        Assert.Equal(Quaternion.Identity, model.Morphs[3].BoneOffsets[0].Rotation);

        Assert.Equal("材質", model.Morphs[4].Name);
        Assert.Equal(PmxMorphType.Material, model.Morphs[4].Type);
        Assert.Equal(2, model.Morphs[4].MaterialOffsets.Count);
        Assert.Equal(PmxMaterialMorphOperation.Multiply, model.Morphs[4].MaterialOffsets[0].Operation);
        Assert.Equal(PmxMaterialMorphOperation.Add, model.Morphs[4].MaterialOffsets[1].Operation);

        Assert.Equal("フリップ", model.Morphs[5].Name);
        Assert.Equal(PmxMorphType.Flip, model.Morphs[5].Type);
        Assert.Single(model.Morphs[5].FlipOffsets);

        Assert.Equal("インパルス", model.Morphs[6].Name);
        Assert.Equal(PmxMorphType.Impulse, model.Morphs[6].Type);
        Assert.Single(model.Morphs[6].ImpulseOffsets);
        Assert.True(model.Morphs[6].ImpulseOffsets[0].Local);
        Assert.Equal(new Vector3(0f, 1f, 0f), model.Morphs[6].ImpulseOffsets[0].TranslationVelocity);

        Assert.Equal("追加UV1", model.Morphs[7].Name);
        Assert.Equal(PmxMorphType.AdditionalUv1, model.Morphs[7].Type);
        Assert.Single(model.Morphs[7].UvOffsets);
    }

    [Fact]
    public void ReadsDisplayFramesRigidBodiesJointsAndSoftBody()
    {
        PmxModel model = ReadFixture();

        Assert.Equal(2, model.DisplayFrames.Count);
        PmxDisplayFrame root = model.DisplayFrames[0];
        Assert.Equal("Root", root.Name);
        Assert.Equal(PmxDisplayFrameType.Default, root.Type);
        Assert.Equal(4, root.Targets.Count);
        Assert.Equal(new PmxDisplayTarget(PmxDisplayTargetType.Bone, 0), root.Targets[0]);
        Assert.Equal(new PmxDisplayTarget(PmxDisplayTargetType.Morph, 4), root.Targets[3]);

        PmxDisplayFrame expressions = model.DisplayFrames[1];
        Assert.Equal("表情", expressions.Name);
        Assert.Equal(PmxDisplayFrameType.Special, expressions.Type);
        Assert.Equal(3, expressions.Targets.Count);

        Assert.Equal(2, model.RigidBodies.Count);
        Assert.Equal("剛体_球", model.RigidBodies[0].Name);
        Assert.Equal(4, model.RigidBodies[0].BoneIndex);
        Assert.Equal(0xFFFF, model.RigidBodies[0].CollisionGroup);
        Assert.Equal(PmxRigidBodyShape.Sphere, model.RigidBodies[0].Shape);
        Assert.Equal(PmxRigidBodyOperation.Dynamic, model.RigidBodies[0].Operation);
        Assert.Equal(PmxRigidBodyShape.Box, model.RigidBodies[1].Shape);
        Assert.Equal(PmxRigidBodyOperation.Static, model.RigidBodies[1].Operation);

        Assert.Single(model.Joints);
        PmxJoint joint = model.Joints[0];
        Assert.Equal("ジョイント", joint.Name);
        Assert.Equal(PmxJointType.SpringDof6, joint.Type);
        Assert.Equal(0, joint.RigidBodyAIndex);
        Assert.Equal(1, joint.RigidBodyBIndex);
        Assert.Equal(new Vector3(1f, 1f, 1f), joint.SpringTranslationFactor);

        Assert.Single(model.SoftBodies);
        PmxSoftBody softBody = model.SoftBodies[0];
        Assert.Equal("ソフトボディ", softBody.Name);
        Assert.Equal(PmxSoftBodyType.TriMesh, softBody.Type);
        Assert.Equal(0, softBody.MaterialIndex);
        Assert.Equal(0xFFFF, softBody.CollisionGroup);
        Assert.True(softBody.Flags.HasFlag(PmxSoftBodyFlags.BLink));
        Assert.True(softBody.Flags.HasFlag(PmxSoftBodyFlags.Cluster));
        Assert.Equal(5, softBody.BLinkLength);
        Assert.Equal(4, softBody.ClusterCount);
        Assert.Equal(1f, softBody.Config.Vcf);
        Assert.Equal(1.1f, softBody.Config.Ahr);
        Assert.Equal(1, softBody.Iterations.V);
        Assert.Single(softBody.Anchors);
        Assert.Equal(new PmxSoftBodyAnchor(0, 0, true), softBody.Anchors[0]);
        Assert.Equal(new[] { 1, 2 }, softBody.PinVertexIndices);
    }

    [Fact]
    public void RejectsTruncatedInputWithFieldAndOffset()
    {
        byte[] bytes = FixtureBytes();
        int[] cuts = [3, 17, 64, bytes.Length / 2, bytes.Length - 1];

        foreach (int cut in cuts)
        {
            using var stream = new MemoryStream(bytes, 0, cut, writable: false);
            PmxFormatException exception = Assert.Throws<PmxFormatException>(() => PmxReader.Read(stream));

            Assert.False(string.IsNullOrWhiteSpace(exception.Field));
            Assert.InRange(exception.Offset, 0, cut);
            Assert.Contains(exception.Field, exception.Message);
            Assert.Contains("offset 0x", exception.Message);
        }
    }

    [Fact]
    public void RejectsInvalidHeaderValuesWithFieldContext()
    {
        byte[] bytes = FixtureBytes();

        byte[] badMagic = (byte[])bytes.Clone();
        badMagic[0] = (byte)'X';
        PmxFormatException magicError = Assert.Throws<PmxFormatException>(
            () => PmxReader.Read(new MemoryStream(badMagic)));
        Assert.Equal("header.magic", magicError.Field);
        Assert.Equal(0, magicError.Offset);

        byte[] badVersion = (byte[])bytes.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(badVersion.AsSpan(4), 3.0f);
        PmxFormatException versionError = Assert.Throws<PmxFormatException>(
            () => PmxReader.Read(new MemoryStream(badVersion)));
        Assert.Equal("header.version", versionError.Field);

        byte[] badEncoding = (byte[])bytes.Clone();
        badEncoding[9] = 9;
        PmxFormatException encodingError = Assert.Throws<PmxFormatException>(
            () => PmxReader.Read(new MemoryStream(badEncoding)));
        Assert.Equal("header.textEncoding", encodingError.Field);

        byte[] badAdditionalUv = (byte[])bytes.Clone();
        badAdditionalUv[10] = 5;
        PmxFormatException uvError = Assert.Throws<PmxFormatException>(
            () => PmxReader.Read(new MemoryStream(badAdditionalUv)));
        Assert.Equal("header.additionalUvCount", uvError.Field);

        byte[] badVertexWidth = (byte[])bytes.Clone();
        badVertexWidth[11] = 3;
        PmxFormatException widthError = Assert.Throws<PmxFormatException>(
            () => PmxReader.Read(new MemoryStream(badVertexWidth)));
        Assert.Equal("header.vertexIndexSize", widthError.Field);
    }

    [Fact]
    public void ReadsRealCorpusPmxFiles()
    {
        string corpusRoot = Environment.GetEnvironmentVariable("MMD_PMX_CORPUS") ?? DefaultCorpusRoot;
        if (!Directory.Exists(corpusRoot))
        {
            Assert.Skip($"PMX corpus not found at '{corpusRoot}'.");
        }

        string[] files = Directory.GetFiles(corpusRoot, "*.pmx", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            Assert.Skip($"No *.pmx files under '{corpusRoot}'.");
        }

        foreach (string file in files)
        {
            using FileStream stream = File.OpenRead(file);
            PmxModel model = PmxReader.Read(stream);

            Assert.True(model.Vertices.Count > 0, $"{file}: no vertices");
            Assert.True(model.Faces.Count > 0, $"{file}: no faces");
            Assert.True(model.Materials.Count > 0, $"{file}: no materials");
            Assert.True(model.Bones.Count > 0, $"{file}: no bones");
            Assert.True(model.Morphs.Count > 0, $"{file}: no morphs");
            Assert.InRange(model.Header.AdditionalUvCount, 0, 4);
            Assert.DoesNotContain('\uFFFD', model.Name);
            Assert.DoesNotContain('\uFFFD', model.EnglishName);

            if (model.Header.AdditionalUvCount > 0)
            {
                Assert.All(model.Vertices,
                    vertex => Assert.Equal(model.Header.AdditionalUvCount, vertex.AdditionalUvs.Count));
            }
        }
    }
}
