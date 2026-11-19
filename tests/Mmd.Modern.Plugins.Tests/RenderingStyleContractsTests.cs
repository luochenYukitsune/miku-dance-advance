using System.Buffers.Binary;
using System.Numerics;
using Mmd.Modern.Plugins.Contracts;
using Xunit;

namespace Mmd.Modern.Plugins.Tests;

public sealed class RenderingStyleContractsTests
{
    [Fact]
    public void LocalizedText_resolve_matches_exact_case_insensitive_primary_subtag_and_fallback()
    {
        var text = LocalizedText.From(
            "Default Style",
            zhCn: "默认风格",
            en: "Default Style EN",
            ja: "デフォルトスタイル");

        Assert.Equal("默认风格", text.Resolve("zh-CN"));
        Assert.Equal("默认风格", text.Resolve("zh"));
        Assert.Equal("默认风格", text.Resolve("zh-Hans"));
        Assert.Equal("默认风格", text.Resolve("ZH_cn"));
        Assert.Equal("Default Style EN", text.Resolve("en-US"));
        Assert.Equal("Default Style EN", text.Resolve("en"));
        Assert.Equal("デフォルトスタイル", text.Resolve("ja-JP"));
        Assert.Equal("デフォルトスタイル", text.Resolve("ja"));
        Assert.Equal("Default Style", text.Resolve("fr-FR"));
        Assert.Equal("Default Style", text.Resolve(null));
        Assert.Equal("Default Style", text.Resolve("   "));
    }

    [Fact]
    public void LocalizedText_resolve_handles_case_sensitive_user_dictionary()
    {
        var custom = new LocalizedText(
            "Fallback",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["zh-CN"] = "简体中文",
                ["en"] = "English",
            });

        Assert.Equal("简体中文", custom.Resolve("zh-cn"));
        Assert.Equal("简体中文", custom.Resolve("zh"));
        Assert.Equal("English", custom.Resolve("EN-GB"));
        Assert.Equal("Fallback", custom.Resolve("ko-KR"));
    }

    [Fact]
    public void ConstantBufferBuilder_packs_scalars_and_vectors_without_crossing_16_byte_boundaries()
    {
        var packedRow = new StyleConstantBufferBuilder()
            .Write(1.5f)
            .Write(-42)
            .Write(new Vector2(2.5f, 3.5f));
        Assert.Equal(16, packedRow.ByteCount);
        var packedBytes = packedRow.ToArray();
        Assert.Equal(16, packedBytes.Length);
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(packedBytes.AsSpan(0, 4)));
        Assert.Equal(-42, BinaryPrimitives.ReadInt32LittleEndian(packedBytes.AsSpan(4, 4)));
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(packedBytes.AsSpan(8, 4)));
        Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(packedBytes.AsSpan(12, 4)));

        var floatThenVec3 = new StyleConstantBufferBuilder()
            .Write(7u)
            .Write(new Vector3(1f, 2f, 3f));
        Assert.Equal(16, floatThenVec3.ByteCount);
        var vec3Bytes = floatThenVec3.ToArray();
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(vec3Bytes.AsSpan(0, 4)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(vec3Bytes.AsSpan(4, 4)));
        Assert.Equal(2f, BinaryPrimitives.ReadSingleLittleEndian(vec3Bytes.AsSpan(8, 4)));
        Assert.Equal(3f, BinaryPrimitives.ReadSingleLittleEndian(vec3Bytes.AsSpan(12, 4)));

        // float x3 (12 bytes) + Vector2 (8 bytes) straddles 16-byte register -> 4 zero-pad bytes -> offset 16..23.
        var straddlingVec2 = new StyleConstantBufferBuilder()
            .Write(1f)
            .Write(2f)
            .Write(3f)
            .Write(new Vector2(4f, 5f));
        Assert.Equal(24, straddlingVec2.ByteCount);
        var straddlingVec2Bytes = straddlingVec2.ToArray();
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(straddlingVec2Bytes.AsSpan(12, 4)));
        Assert.Equal(4f, BinaryPrimitives.ReadSingleLittleEndian(straddlingVec2Bytes.AsSpan(16, 4)));
        Assert.Equal(5f, BinaryPrimitives.ReadSingleLittleEndian(straddlingVec2Bytes.AsSpan(20, 4)));

        // float x2 (8 bytes) + Vector3 (12 bytes) straddles -> 8 zero-pad bytes -> offset 16..27.
        var straddlingVec3 = new StyleConstantBufferBuilder()
            .Write(1f)
            .Write(2f)
            .Write(new Vector3(3f, 4f, 5f));
        Assert.Equal(28, straddlingVec3.ByteCount);
        var straddlingVec3Bytes = straddlingVec3.ToArray();
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(straddlingVec3Bytes.AsSpan(8, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(straddlingVec3Bytes.AsSpan(12, 4)));
        Assert.Equal(3f, BinaryPrimitives.ReadSingleLittleEndian(straddlingVec3Bytes.AsSpan(16, 4)));

        // float (4 bytes) + Vector4 (16 bytes) -> 12 zero-pad bytes -> offset 16..31.
        var straddlingVec4 = new StyleConstantBufferBuilder()
            .Write(9f)
            .Write(new Vector4(10f, 11f, 12f, 13f));
        Assert.Equal(32, straddlingVec4.ByteCount);
        Assert.Equal(10f, BinaryPrimitives.ReadSingleLittleEndian(straddlingVec4.ToArray().AsSpan(16, 4)));
    }

    [Fact]
    public void ConstantBufferBuilder_aligns_matrix4x4_padTo16_and_enforces_4096_byte_budget()
    {
        var matrix = new Matrix4x4(
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
            13, 14, 15, 16);

        var matrixBuilder = new StyleConstantBufferBuilder()
            .Write(42f)
            .Write(matrix);
        Assert.Equal(80, matrixBuilder.ByteCount);
        var matrixBytes = matrixBuilder.ToArray();
        for (var i = 0; i < 16; i++)
            Assert.Equal(i + 1f, BinaryPrimitives.ReadSingleLittleEndian(matrixBytes.AsSpan(16 + i * 4, 4)));

        matrixBuilder.PadTo16();
        Assert.Equal(80, matrixBuilder.ByteCount);
        matrixBuilder.Write(1f).PadTo16();
        Assert.Equal(96, matrixBuilder.ByteCount);

        var budgetBuilder = new StyleConstantBufferBuilder();
        for (var i = 0; i < 255; i++)
            budgetBuilder.Write(Vector4.One);
        budgetBuilder.Write(1f);
        budgetBuilder.Write(2f);
        Assert.Equal(4088, budgetBuilder.ByteCount);

        // Vector3 at 4088 would pad to 4096 and write 12 bytes (4108 > 4096); must throw without advancing ByteCount.
        Assert.Throws<InvalidOperationException>(() => budgetBuilder.Write(Vector3.One));
        Assert.Equal(4088, budgetBuilder.ByteCount);

        // Vector2 at 4088 fits in the remaining 8 bytes up to exactly 4096.
        budgetBuilder.Write(new Vector2(3f, 4f));
        Assert.Equal(StyleResourceLimits.MaxConstantBufferBytes, budgetBuilder.ByteCount);
        Assert.Equal(StyleResourceLimits.MaxConstantBufferBytes, budgetBuilder.ToArray().Length);

        Assert.Throws<InvalidOperationException>(() => budgetBuilder.Write(1f));
        Assert.Equal(StyleResourceLimits.MaxConstantBufferBytes, budgetBuilder.ByteCount);
    }

    [Theory]
    [InlineData("eyebrow", StyleMaterialRole.Face)]
    [InlineData("眉", StyleMaterialRole.Face)]
    [InlineData("まゆ", StyleMaterialRole.Face)]
    [InlineData("eye", StyleMaterialRole.Eye)]
    [InlineData("LeftEye", StyleMaterialRole.Eye)]
    [InlineData("左目", StyleMaterialRole.Eye)]
    [InlineData("瞳", StyleMaterialRole.Eye)]
    [InlineData("眼睛", StyleMaterialRole.Eye)]
    [InlineData("hair_front", StyleMaterialRole.Hair)]
    [InlineData("前髪", StyleMaterialRole.Hair)]
    [InlineData("头发", StyleMaterialRole.Hair)]
    [InlineData("刘海", StyleMaterialRole.Hair)]
    [InlineData("ヘア", StyleMaterialRole.Hair)]
    [InlineData("face_skin", StyleMaterialRole.Face)]
    [InlineData("顔", StyleMaterialRole.Face)]
    [InlineData("口内", StyleMaterialRole.Face)]
    [InlineData("cheek", StyleMaterialRole.Face)]
    [InlineData("頬", StyleMaterialRole.Face)]
    [InlineData("腮红", StyleMaterialRole.Face)]
    [InlineData("dress", StyleMaterialRole.Body)]
    [InlineData("发光", StyleMaterialRole.Body)]
    [InlineData(null, StyleMaterialRole.Body)]
    public void RenderingStyleDefinitionBase_guesses_roles_across_jp_cn_en_keywords(string? materialName, StyleMaterialRole expected)
    {
        var style = new MinimalStyle();
        var classification = style.ClassifyMaterial(new StyleMaterialInspectionContext(
            Guid.NewGuid(), 0, materialName, ExplicitRole: null, GuessMaterialRoles: true));

        Assert.Equal(expected, classification.ResolvedRole);
    }

    [Fact]
    public void RenderingStyleDefinitionBase_respects_explicit_role_and_pass_through_defaults()
    {
        var style = new MinimalStyle();
        var explicitResult = style.ClassifyMaterial(new StyleMaterialInspectionContext(
            Guid.NewGuid(), 0, "hair", ExplicitRole: StyleMaterialRole.Body, GuessMaterialRoles: true));
        Assert.Equal(StyleMaterialRole.Body, explicitResult.ResolvedRole);

        var disabledGuessResult = style.ClassifyMaterial(new StyleMaterialInspectionContext(
            Guid.NewGuid(), 0, "hair", ExplicitRole: null, GuessMaterialRoles: false));
        Assert.Equal(StyleMaterialRole.Body, disabledGuessResult.ResolvedRole);

        var validation = style.ValidateMaterial(new StyleMaterialValidationContext(
            Guid.NewGuid(), 0, "hair", StyleMaterialRole.Hair, [], 1));
        Assert.True(validation.IsValid);
        Assert.Null(validation.ErrorMessage);

        var error = StyleValidationResult.Error(LocalizedText.From("Invalid", zhCn: "无效"));
        Assert.False(error.IsValid);
        Assert.Equal("Invalid", error.ErrorMessage);
        Assert.Equal("无效", error.LocalizedError!.Resolve("zh-CN"));

        var bindingContext = new StyleMaterialBindingContext(Guid.NewGuid(), 0, "hair", StyleMaterialRole.Hair, []);
        Assert.Empty(style.GetSceneTextureRequests(bindingContext));
        Assert.Null(style.GetBoneTransformRequest(bindingContext));
        Assert.Empty(style.GetShaderModules());
        Assert.Equal(4, style.DefaultRoleTreatments.Count);
    }

    private sealed class MinimalStyle : RenderingStyleDefinitionBase
    {
        public override string StyleId => "minimal-test";
        public override LocalizedText DisplayName { get; } = LocalizedText.From("Minimal");
    }
}
