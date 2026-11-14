using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class VmdEncodingTests
{
    [Fact]
    public void ExplicitCp936RoundTripsFullWidthChineseNamesInEveryNameField()
    {
        const string full15 = "1234567890123带";
        const string full20 = "123456789012345678带";
        var motion = new VmdMotion
        {
            ModelName = full20,
            BoneKeyframes = [new() { BoneName = full15 }],
            MorphKeyframes = [new() { MorphName = full15 }],
            IkKeyframes = [new() { Switches = [new(full20, true)] }],
        };
        var writeOptions = new VmdWriteOptions { AnsiCodePage = 936 };
        byte[] bytes = VmdWriter.ToBytes(motion, writeOptions);
        Assert.Equal(new byte[] { 0xb4, 0xf8 }, bytes[48..50]);
        Assert.Equal(new byte[] { 0xb4, 0xf8 }, bytes[67..69]);
        using var input = new MemoryStream(bytes);
        VmdMotion read = VmdReader.Read(input, new VmdReadOptions { AnsiCodePage = 936 });
        Assert.Equal(full20, read.ModelName);
        Assert.Equal(full15, Assert.Single(read.BoneKeyframes).BoneName);
        Assert.Equal(full15, Assert.Single(read.MorphKeyframes).MorphName);
        Assert.Equal(full20, Assert.Single(Assert.Single(read.IkKeyframes).Switches).BoneName);
        Assert.Equal(bytes, VmdWriter.ToBytes(read, writeOptions));
        using var destination = new MemoryStream();
        VmdWriter.Write(destination, motion, writeOptions);
        Assert.Equal(bytes, destination.ToArray());
    }

    [Fact]
    public void ModelNameOverrideChangesOnlyHeaderAndLeavesMotionUntouched()
    {
        const string originalName = "a model name longer than twenty bytes 带";
        var motion = new VmdMotion
        {
            ModelName = originalName,
            BoneKeyframes = [new() { BoneName = "センター" }],
        };
        byte[] bytes = VmdWriter.ToBytes(motion, new VmdWriteOptions { ModelNameOverride = "export" });
        Assert.Equal(originalName, motion.ModelName);
        Assert.Equal("センター", Assert.Single(motion.BoneKeyframes).BoneName);
        using var input = new MemoryStream(bytes);
        VmdMotion read = VmdReader.Read(input);
        Assert.Equal("export", read.ModelName);
        Assert.Equal("センター", Assert.Single(read.BoneKeyframes).BoneName);
        byte[] empty = VmdWriter.ToBytes(motion, new VmdWriteOptions { ModelNameOverride = "" });
        Assert.All(empty[30..50], value => Assert.Equal(0, value));
    }

    [Fact]
    public void HeaderOverrideDoesNotRelaxBoneNameEncodingOrWidth()
    {
        var options = new VmdWriteOptions { ModelNameOverride = "export" };
        ArgumentException encoding = Assert.Throws<ArgumentException>(() => VmdWriter.ToBytes(
            new VmdMotion { BoneKeyframes = [new() { BoneName = "带" }] }, options));
        Assert.Contains("932", encoding.Message);
        Assert.Equal("bones[0].name", encoding.ParamName);
        AssertNoWrite(new() { BoneKeyframes = [new() { BoneName = "12345678901234带" }] },
            new() { AnsiCodePage = 936, ModelNameOverride = "export" });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1252)]
    [InlineData(65001)]
    public void UnsupportedCodePagesAreRejectedBeforeReadingOrWriting(int codePage)
    {
        using var input = new MemoryStream([42, 43, 44]);
        input.Position = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => VmdReader.Read(input,
            new VmdReadOptions { AnsiCodePage = codePage }));
        Assert.Equal(1, input.Position);
        using var output = new MemoryStream();
        output.Write([42, 43, 44]);
        Assert.Throws<ArgumentOutOfRangeException>(() => VmdWriter.Write(output, new VmdMotion(),
            new VmdWriteOptions { AnsiCodePage = codePage }));
        Assert.Equal(3, output.Position);
        Assert.Equal(new byte[] { 42, 43, 44 }, output.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => VmdWriter.ToBytes(new VmdMotion(),
            new VmdWriteOptions { AnsiCodePage = codePage }));
    }

    [Fact]
    public void DefaultAndExplicitCp932RemainStrictAfterUsingCp936()
    {
        var chinese = new VmdMotion { ModelName = "带" };
        byte[] chineseBytes = VmdWriter.ToBytes(chinese, new VmdWriteOptions { AnsiCodePage = 936 });
        using var chineseInput = new MemoryStream(chineseBytes);
        Assert.Equal("带", VmdReader.Read(chineseInput, new VmdReadOptions { AnsiCodePage = 936 }).ModelName);
        Assert.Contains("932", Assert.Throws<ArgumentException>(() => VmdWriter.ToBytes(chinese)).Message);
        Assert.Contains("932", Assert.Throws<ArgumentException>(() => VmdWriter.ToBytes(chinese,
            new VmdWriteOptions { AnsiCodePage = 932 })).Message);

        var japanese = new VmdMotion
        {
            ModelName = "初音ミク",
            BoneKeyframes = [new() { BoneName = "センター" }],
            MorphKeyframes = [new() { MorphName = "まばたき" }],
            IkKeyframes = [new() { Switches = [new("左足ＩＫ", true)] }],
        };
        byte[] defaultBytes = VmdWriter.ToBytes(japanese);
        Assert.Equal(new byte[] { 0x8f, 0x89, 0x89, 0xb9, 0x83, 0x7e, 0x83, 0x4e }, defaultBytes[30..38]);
        Assert.Equal(defaultBytes, VmdWriter.ToBytes(japanese, new VmdWriteOptions { AnsiCodePage = 932 }));
        using var defaultInput = new MemoryStream(defaultBytes);
        using var explicitInput = new MemoryStream(defaultBytes);
        Assert.Equal(japanese.ModelName, VmdReader.Read(defaultInput).ModelName);
        Assert.Equal(japanese.ModelName, VmdReader.Read(explicitInput, new VmdReadOptions { AnsiCodePage = 932 }).ModelName);
    }

    [Fact]
    public void InvalidCp936BytesAreRejectedWithFieldAndSelectedCodePage()
    {
        byte[] bytes = VmdWriter.ToBytes(new VmdMotion());
        bytes[30] = 0x81; // A GBK lead byte without a trailing byte before the NUL terminator.
        using var input = new MemoryStream(bytes);
        VmdFormatException error = Assert.Throws<VmdFormatException>(() => VmdReader.Read(input,
            new VmdReadOptions { AnsiCodePage = 936 }));
        Assert.Contains("header.modelName", error.Message);
        Assert.Contains("936", error.Message);
    }

    [Theory]
    [InlineData("1234567890123456789带")]
    [InlineData("a\0b")]
    [InlineData("😀")]
    public void Cp936NeverTruncatesOrReplacesInvalidModelNames(string name)
        => AssertNoWrite(new() { ModelName = name }, new() { AnsiCodePage = 936 });

    private static void AssertNoWrite(VmdMotion motion, VmdWriteOptions options)
    {
        using var output = new MemoryStream();
        output.Write([42, 43, 44]);
        Assert.ThrowsAny<ArgumentException>(() => VmdWriter.Write(output, motion, options));
        Assert.Equal(3, output.Position);
        Assert.Equal(new byte[] { 42, 43, 44 }, output.ToArray());
    }
}
