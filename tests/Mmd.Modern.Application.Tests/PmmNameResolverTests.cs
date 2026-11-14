using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmNameResolverTests
{
    private static byte[] Bytes(string name, int page)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(name);
    }

    private static PmmString Raw(string name, int page)
    {
        byte[] bytes = Bytes(name, page);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new(17, Encoding.GetEncoding(page == 932 ? 936 : 932).GetString(bytes), bytes);
    }

    [Fact]
    public void SelectedPmxNamesDetermineMixedEncodingBindingsWithoutChangingSource()
    {
        var chinese = Raw("带", 936); var japanese = Raw("センター", 932);
        string oldValue = chinese.Value; byte[] oldRaw = chinese.RawBytes.ToArray();
        var results = PmmNameResolver.ResolveTable([chinese, japanese], ["センター", "带"], "bone");
        Assert.Equal(["带", "センター"], results.Select(r => r.BoundName));
        Assert.Equal(["带", "センター"], results.Select(r => r.DisplayName));
        Assert.Equal(oldValue, chinese.Value); Assert.Equal(oldRaw, chinese.RawBytes);
        Assert.Contains(results[0].Candidates, c => c.AnsiCodePage == 936 && c.Value == "带");
        Assert.Contains(results[1].Candidates, c => c.AnsiCodePage == 932 && c.Value == "センター");
    }

    [Fact]
    public void UnmatchedTwoValidDecodingsShowBothCandidatesWithoutGuessing()
    {
        var source = Raw("中", 936); var diagnostics = new List<string>();
        var result = Assert.Single(PmmNameResolver.ResolveTable([source], ["unrelated"], "bone", diagnostics));
        Assert.Null(result.BoundName);
        Assert.Contains("CP932:", result.DisplayName); Assert.Contains("CP936: 中", result.DisplayName);
        Assert.Contains(result.DisplayName, Assert.Single(diagnostics));
    }

    [Fact]
    public void ByteBackedNameNeverUsesUnverifiedDecodedValueAsThirdBindingCandidate()
    {
        var source = new PmmString(0, "injected", Bytes("中", 936));
        var result = Assert.Single(PmmNameResolver.ResolveTable([source], ["injected"], "bone"));
        Assert.Null(result.BoundName); Assert.DoesNotContain(result.Candidates, c => c.Value == "injected");
    }

    [Fact]
    public void BothDecodingsMatchingDifferentPmxNamesAreRejected()
    {
        var source = Raw("中", 936);
        string[] meanings = PmmNameResolver.DecodeCandidates(source).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, meanings.Length);
        Assert.Throws<InvalidDataException>(() => PmmNameResolver.ResolveTable([source], meanings, "bone"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateTargetNamesAndDuplicateSourceBindingsAreRejected(bool duplicateTarget)
    {
        var source = new PmmString(0, "A", [65]);
        Assert.Throws<InvalidDataException>(() => PmmNameResolver.ResolveTable(
            duplicateTarget ? [source] : [source, source], duplicateTarget ? ["A", "A"] : ["A"], "bone"));
    }

    [Fact]
    public void V1UnicodeMetadataWithoutBytesRemainsAvailable()
    {
        var source = new PmmString(-1, "Unicode😀", []);
        var result = Assert.Single(PmmNameResolver.ResolveTable([source], ["Unicode😀"], "bone"));
        Assert.Equal("Unicode😀", result.BoundName); Assert.Equal("Unicode😀", result.DisplayName);
        Assert.Equal(0, Assert.Single(result.Candidates).AnsiCodePage);
    }

    [Fact]
    public void InvalidBytesDoNotMatchLossyReplacementText()
    {
        var source = new PmmString(0, "?", [0x81]);
        var result = Assert.Single(PmmNameResolver.ResolveTable([source], ["?"], "morph"));
        Assert.Null(result.BoundName); Assert.Empty(result.Candidates); Assert.Equal("0x81", result.DisplayName);
    }

    [Fact]
    public void NullPaddingIsIgnoredForBindingAndRetainedInSource()
    {
        byte[] bytes = [65, 0, 0x81]; var source = new PmmString(0, "A", bytes);
        var result = Assert.Single(PmmNameResolver.ResolveTable([source], ["A"], "bone"));
        Assert.Equal("A", result.BoundName); Assert.Equal("A", result.DisplayName); Assert.Equal(bytes, source.RawBytes);
    }

    [Fact]
    public void DocumentedJapaneseDuplicateBindsByOfficialMappingWithoutChangingBytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        // Microsoft defines ED40 as U+7E8A, although the runtime's default strict codec rejects it.
        byte[] bytes = [0xed, 0x40];
        Assert.Throws<DecoderFallbackException>(() => encoding.GetString(bytes));
        var source = new PmmString(0, "wrong display", bytes);
        var result = Assert.Single(PmmNameResolver.ResolveTable([source], ["\u7e8a"], "bone"));
        Assert.Equal("\u7e8a", result.BoundName);
        Assert.Contains(result.Candidates, c => c.AnsiCodePage == 932 && c.Value == "\u7e8a");
        Assert.Equal(bytes, source.RawBytes); Assert.Equal("wrong display", source.Value);
    }

    [Fact]
    public void InstanceDisplayUsesUniqueModelHeaderMatchAndPreservesAsciiAlias()
    {
        var name = Raw("Fネバネバ", 932);
        var result = PmmNameResolver.ResolveDisplay(name, ["Fネバネバ", "Fibrin Sticky part"]);
        Assert.Equal("Fネバネバ", result.DisplayName); Assert.Equal("Fネバネバ", result.BoundName);
        var alias = new PmmString(0, "Stage", [83, 116, 97, 103, 101]);
        var aliasResult = PmmNameResolver.ResolveDisplay(alias, ["Original model name"]);
        Assert.Null(aliasResult.BoundName); Assert.Equal("Stage", aliasResult.DisplayName);
    }

    [Fact]
    public void UnverifiedNonAsciiAliasShowsAlternativesWithoutBecomingModelName()
    {
        var source = Raw("中", 936);
        var result = PmmNameResolver.ResolveDisplay(source, ["Different header"]);
        Assert.Null(result.BoundName); Assert.Contains("CP936: 中", result.DisplayName);
        Assert.DoesNotContain("Different header", result.DisplayName);
        string[] both = PmmNameResolver.DecodeCandidates(source).Select(c => c.Value).ToArray();
        var ambiguous = PmmNameResolver.ResolveDisplay(source, both);
        Assert.Null(ambiguous.BoundName); Assert.Equal(result.DisplayName, ambiguous.DisplayName);
    }
}
