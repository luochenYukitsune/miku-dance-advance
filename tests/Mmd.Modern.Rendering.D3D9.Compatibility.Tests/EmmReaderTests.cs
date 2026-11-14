using System.Collections;
using System.Reflection;
using System.Text;
using Xunit;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

/// <summary>CPU-only contracts from the pinned MIT EmmFile reader; reflection keeps RED buildable.</summary>
public sealed class EmmReaderTests
{
    private const string Minimum = "[Info]\nVersion=3\n[Object]\nPmd0=models\\actor.pmx\nAcs1=stage.x\n[Effect]\nDefault=none\n";

    [Fact]
    public void FullGenericGrammarKeepsNamedOwnerScopesAndPresence()
    {
        var document = Read(Minimum + "Pmd0.show=false\nPmd0[0]=none\nPmd0[1].show=true\n"
            + "[Effect@Depth(2)]\nOwner=Pmd0[7]@Parent(1)\nPmd0=effects\\depth.fx\nPmd0[2]=special.fx\nAcs1.show=true\n"
            + "[Effect@Parent(1)]\nOwner=Acs1\nPmd0=none\n");
        Assert.Equal(3, Get<int>(document, "Version"));
        Assert.True(Get<bool>(document, "IsSupportedVersion"));
        Assert.Equal(936, Get<int>(document, "AnsiCodePage"));
        var objects = Items(document, "Objects");
        Assert.Equal(2, objects.Length);
        Assert.Equal("Pmd0", Get<string>(objects[0], "Key"));
        Assert.True(Get<bool>(objects[0], "IsModel"));
        Assert.Equal(0, Get<int>(objects[0], "Slot"));
        Assert.Equal("models\\actor.pmx", Get<string>(objects[0], "Path"));
        Assert.False(Get<bool>(objects[1], "IsModel"));
        Assert.Equal(1, Get<int>(objects[1], "Slot"));
        var sections = Items(document, "EffectSections");
        Assert.Equal(3, sections.Length);
        Assert.True(Get<bool>(sections[0], "IsDefault"));
        Assert.Equal("none", Get<string>(sections[0], "DefaultPath"));
        var baseEntry = Assert.Single(Items(sections[0], "Entries"));
        Assert.Null(Value(baseEntry, "EffectPath"));
        Assert.Equal(false, Value(baseEntry, "Shown"));
        Assert.Equal("none", DictionaryValue(baseEntry, "SubsetPaths", 0));
        Assert.Equal(true, DictionaryValue(baseEntry, "SubsetShown", 1));
        Assert.Equal("Effect@Depth(2)", Get<string>(sections[1], "Name"));
        Assert.Equal("Depth", Get<string>(sections[1], "BaseName"));
        Assert.False(Get<bool>(sections[1], "IsDefault"));
        Assert.Null(Value(sections[1], "DefaultPath"));
        var owner = Value(sections[1], "Owner")!;
        Assert.Equal("Pmd0", Get<string>(owner, "ObjectKey"));
        Assert.Equal(7, Get<int>(owner, "Subset"));
        Assert.Equal("Parent(1)", Get<string>(owner, "ParentTargetLabel"));
        Assert.Equal("effects\\depth.fx", Get<string>(Items(sections[1], "Entries")[0], "EffectPath"));
        Assert.Equal(-1, Get<int>(Value(sections[2], "Owner")!, "Subset"));
        Assert.Empty(Items(document, "Diagnostics"));
    }

    [Fact]
    public void SourceBytesAndEveryRowRemainImmutableAndInOriginalOrder()
    {
        var source = LegacyAnsiEncoding.Create(936).GetBytes(Minimum.Replace("actor", "角色", StringComparison.Ordinal)
            + "; preserved comment\r\n[Unknown]\r\nFuture=preserved\r\nPmd0=kept.fx # discarded by scanner\r\n");
        var expected = source.ToArray();
        var document = Read(source);
        source[0] = 0;
        var copy = CopyBytes(document);
        Assert.Equal(expected, copy);
        copy[1] = 0;
        Assert.Equal(expected, CopyBytes(document));
        var rows = Items(document, "Rows");
        Assert.Equal(11, rows.Length);
        Assert.Equal("; preserved comment\r\n", Get<string>(rows[7], "RawText"));
        Assert.False(Get<bool>(rows[8], "IsAccepted"));
        Assert.Equal("Future", Get<string>(rows[9], "Key"));
        Assert.Equal("Effect", Get<string>(rows[9], "SectionName"));
        Assert.False(Get<bool>(rows[9], "IsAccepted"));
        Assert.Equal("kept.fx", Get<string>(Assert.Single(Items(Items(document, "EffectSections")[0], "Entries")), "EffectPath"));
        AssertImmutable(Value(document, "Objects")!);
        AssertImmutable(Value(document, "Rows")!);
        AssertImmutable(Value(document, "Diagnostics")!);
    }

    [Fact]
    public void CommentMarkersAreRemovedBeforeTrimmingAndHaveNoQuoteEscape()
    {
        var document = Read(Minimum + "Pmd0=  \"folder;cut.fx\"  \nAcs1=part#cut.fx\n");
        var entries = Items(Items(document, "EffectSections")[0], "Entries");
        Assert.Equal("\"folder", Get<string>(entries[0], "EffectPath"));
        Assert.Equal("part", Get<string>(entries[1], "EffectPath"));
    }

    [Fact]
    public void RejectedAndDuplicateHeadersKeepPreviousActiveSection()
    {
        var document = Read(Minimum + "[Unknown]\nPmd0=first.fx\n[Info]\nAcs1=second.fx\n[Unknown]\nPmd0.show=false\n");
        var entries = Items(Items(document, "EffectSections")[0], "Entries");
        Assert.Equal("first.fx", Get<string>(entries[0], "EffectPath"));
        Assert.Equal("second.fx", Get<string>(entries[1], "EffectPath"));
        Assert.Equal(false, Value(entries[0], "Shown"));
        Assert.Equal(3, Items(document, "Diagnostics").Length);
    }

    [Fact]
    public void EmptyAndMalformedValuesStillOccupyTheirKeyAndFirstWins()
    {
        var document = Read(Minimum + "Pmd0=\nPmd0=second.fx\nPmd0.show=TRUE\nPmd0.show=false\nAcs1=first.fx\nAcs1=second.fx\n");
        var entries = Items(Items(document, "EffectSections")[0], "Entries");
        Assert.Equal(2, entries.Length); // native creates the entry before validating a .show value
        Assert.Null(Value(entries[0], "EffectPath"));
        Assert.Null(Value(entries[0], "Shown"));
        Assert.Equal("first.fx", Get<string>(entries[1], "EffectPath"));
        Assert.Equal(5, Items(document, "Diagnostics").Length);
    }

    [Fact]
    public void DistinctDecimalSpellingsShareObjectSlotButAssignmentsRequireCanonicalKey()
    {
        var document = Read("[Info]\nVersion=3\n[Object]\nPmd00=first.pmx\nPmd0=second.pmx\n[Effect]\nDefault=none\nPmd0=none\n");
        var item = Assert.Single(Items(document, "Objects"));
        Assert.Equal("Pmd0", Get<string>(item, "Key"));
        Assert.Equal("second.pmx", Get<string>(item, "Path"));
        Assert.Empty(Items(document, "Diagnostics"));
        AssertFormatError("[Info]\nVersion=3\n[Object]\nPmd00=first.pmx\n[Effect]\nDefault=none\nPmd00=none\n", "UnknownObject");
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("4", true)]
    [InlineData("-1", true)]
    [InlineData("+3", true)]
    [InlineData("2147483648", false)]
    [InlineData("-2147483648", false)]
    [InlineData("3.0", false)]
    public void PinnedVersionGateAndFinalNonzeroValidationAreSeparate(string version, bool accepted)
    {
        var text = Minimum.Replace("Version=3", "Version=" + version, StringComparison.Ordinal);
        if (!accepted) { AssertFormatError(text, "MissingVersion"); return; }
        var document = Read(text);
        Assert.Equal(int.Parse(version, System.Globalization.CultureInfo.InvariantCulture), Get<int>(document, "Version"));
        Assert.Equal(version == "+3", Get<bool>(document, "IsSupportedVersion"));
        Assert.Equal(version == "+3" ? 0 : 1, Items(document, "Diagnostics").Length);
    }

    [Theory]
    [InlineData("[Object]\n[Effect]\nDefault=none\n", "MissingInfo")]
    [InlineData("[Info]\n[Object]\n[Effect]\nDefault=none\n", "MissingVersion")]
    [InlineData("[Info]\nVersion=3\n[Effect]\nDefault=none\n", "MissingObject")]
    [InlineData("[Info]\nVersion=3\n[Object]\n", "MissingEffect")]
    [InlineData("[Info]\nVersion=3\n[Object]\n[Effect]\n", "MissingDefault")]
    public void StructuralFailureThrowsDiagnosticRichFormatException(string text, string code) => AssertFormatError(text, code);

    [Theory]
    [InlineData("[Effect@Depth]\nPmd0=none\n", "MissingOwner")]
    [InlineData("[Effect@Depth]\nOwner=Acs99\n", "UnknownOwner")]
    [InlineData("Pmd99=none\n", "UnknownObject")]
    [InlineData("Pmd99.show=invalid\n", "UnknownObject")]
    public void FinalValidationChecksOwnersAndEvenEntriesCreatedByRejectedShowRows(string suffix, string code) => AssertFormatError(Minimum + suffix, code);

    [Fact]
    public void NamedSectionAndNumericRegexesAreAsciiExactCase()
    {
        var document = Read(Minimum + "[Effect@深度]\nObj=none\npmd0=none\nPmd０=none\nPmd0[-1]=none\nPmd0[2147483648]=none\n"
            + "[Effect@Depth_1(02)]\nOwner=Pmd0[0]@Other_1(00)\nPmd0=none\nDefault=not-valid-here.fx\n");
        var section = Items(document, "EffectSections")[1];
        Assert.Equal("Depth_1", Get<string>(section, "BaseName"));
        Assert.Equal("Other_1(00)", Get<string>(Value(section, "Owner")!, "ParentTargetLabel"));
        Assert.Null(Value(section, "DefaultPath"));
        Assert.Equal(7, Items(document, "Diagnostics").Length);
    }

    [Fact]
    public void Cp932StrictDecodingPreservesMicrosoftDuplicateSpellingBytes()
    {
        var bytes = Encoding.ASCII.GetBytes("[Info]\nVersion=3\n[Object]\nPmd0=").Concat(new byte[] { 0xed, 0x40 })
            .Concat(Encoding.ASCII.GetBytes(".pmx\n[Effect]\nDefault=none\n")).ToArray();
        var document = Read(bytes, ("AnsiCodePage", 932));
        Assert.Equal("纊.pmx", Get<string>(Assert.Single(Items(document, "Objects")), "Path"));
        Assert.Equal(bytes, CopyBytes(document));
    }

    [Fact]
    public void InvalidAnsiAndBomNeverUseReplacementOrAutomaticEncodingDetection()
    {
        AssertFormatErrorBytes(new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.ASCII.GetBytes(Minimum)).ToArray(), "UnsupportedBom");
        AssertFormatErrorBytes(Encoding.ASCII.GetBytes(Minimum).Concat(new byte[] { 0x81 }).ToArray(), "InvalidEncoding");
        AssertFormatErrorBytes(new byte[] { 0xff, 0xfe }.Concat(Encoding.Unicode.GetBytes(Minimum)).ToArray(), "UnsupportedBom");
    }

    [Fact]
    public void BoundedInputAndRowsFailButPinnedScannerSplitsLongLines()
    {
        AssertFormatErrorBytes(Encoding.ASCII.GetBytes(Minimum), "InputTooLarge", ("MaxInputBytes", 10L));
        AssertFormatErrorBytes(Encoding.ASCII.GetBytes(Minimum), "TooManyRows", ("MaxRows", 2));
        var split = Read(Minimum + "Pmd0=" + new string('a', 1024) + "\n");
        Assert.Equal(1018, Get<string>(Assert.Single(Items(Items(split, "EffectSections")[0], "Entries")), "EffectPath").Length);
        Assert.Equal(9, Items(split, "Rows").Length);
        Assert.Contains(Items(split, "Diagnostics"), d => Get<string>(d, "Code") == "MalformedLine");
        var edge = Read(Minimum + "Pmd0=" + new string('a', 1018) + "\r\n");
        Assert.Equal(1018, Get<string>(Assert.Single(Items(Items(edge, "EffectSections")[0], "Entries")), "EffectPath").Length);
        Assert.Equal(9, Items(edge, "Rows").Length); // CRLF translates before the 1023-byte fgets limit
        Assert.Equal("\r\n", Get<string>(Items(edge, "Rows")[8], "RawText"));
        Assert.Empty(Items(edge, "Diagnostics"));
        var multibyte = Encoding.ASCII.GetBytes(Minimum + "Pmd0=" + new string('a', 1017))
            .Concat(new byte[] { 0xb4, 0xf8, 10 }).ToArray();
        AssertFormatErrorBytes(multibyte, "InvalidEncoding"); // a DBCS character split by fgets is never replaced
    }

    [Fact]
    public void StreamReadStartsAtCurrentPositionLeavesStreamOpenAndDoesNotSeek()
    {
        var bytes = Encoding.ASCII.GetBytes("skip" + Minimum);
        using var stream = new ChunkedReadStream(bytes, 4);
        var document = ReadStream(stream);
        Assert.Equal(Encoding.ASCII.GetBytes(Minimum), CopyBytes(document));
        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.SeekCalls);
    }

    [Fact]
    public void PublicSpanOverloadIsAvailableWithoutAFilePathEntryPoint()
    {
        var reader = ReaderType();
        Assert.Contains(reader.GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.Name == "Read"
            && m.GetParameters()[0].ParameterType == typeof(ReadOnlySpan<byte>));
        Assert.DoesNotContain(reader.GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.Name == "Read"
            && m.GetParameters()[0].ParameterType == typeof(string));
    }

    private static object Read(string text) => Read(LegacyAnsiEncoding.Create(936).GetBytes(text));
    private static object Read(byte[] bytes, params (string Name, object Value)[] options)
    {
        using var stream = new MemoryStream(bytes);
        return ReadStream(stream, options);
    }
    private static object ReadStream(Stream stream, params (string Name, object Value)[] settings)
    {
        var reader = ReaderType();
        var optionType = reader.Assembly.GetType("Mmd.Modern.Rendering.D3D9.Compatibility.EmmReadOptions");
        Assert.NotNull(optionType);
        var options = Activator.CreateInstance(optionType);
        foreach (var (name, value) in settings) optionType.GetProperty(name)!.SetValue(options, value);
        var method = reader.GetMethod("Read", [typeof(Stream), optionType]);
        Assert.NotNull(method);
        try { return method.Invoke(null, [stream, options])!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static Type ReaderType()
    {
        var type = typeof(ExperimentalD3D9SurfaceSession).Assembly.GetType("Mmd.Modern.Rendering.D3D9.Compatibility.EmmReader");
        Assert.NotNull(type);
        return type;
    }
    private static object? Value(object source, string property)
    {
        var info = source.GetType().GetProperty(property);
        Assert.NotNull(info);
        return info.GetValue(source);
    }
    private static T Get<T>(object source, string property) => Assert.IsType<T>(Value(source, property));
    private static object[] Items(object source, string property) => Assert.IsAssignableFrom<IEnumerable>(Value(source, property)).Cast<object>().ToArray();
    private static object? DictionaryValue(object source, string property, int key)
    {
        var dictionary = Value(source, property)!;
        return dictionary.GetType().GetProperty("Item")!.GetValue(dictionary, [key]);
    }
    private static byte[] CopyBytes(object document) => Assert.IsType<byte[]>(document.GetType().GetMethod("CopySourceBytes")!.Invoke(document, null));
    private static void AssertImmutable(object collection)
    {
        if (collection is IList list) Assert.Throws<NotSupportedException>(() => list.Clear());
        else Assert.DoesNotContain(collection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance), m => m.Name is "Add" or "Remove" or "Clear");
    }
    private static void AssertFormatError(string text, string code) => AssertFormatErrorBytes(LegacyAnsiEncoding.Create(936).GetBytes(text), code);
    private static void AssertFormatErrorBytes(byte[] bytes, string code, params (string Name, object Value)[] options)
    {
        var error = Record.Exception(() => Read(bytes, options));
        Assert.NotNull(error);
        Assert.Equal("Mmd.Modern.Rendering.D3D9.Compatibility.EmmFormatException", error.GetType().FullName);
        Assert.Contains(Items(error, "Diagnostics"), d => Get<string>(d, "Code") == code);
    }

    private sealed class ChunkedReadStream(byte[] source, int start) : Stream
    {
        private int position = start;
        public int SeekCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int size = Math.Min(Math.Min(count, 3), source.Length - position);
            source.AsSpan(position, size).CopyTo(buffer.AsSpan(offset, size));
            position += size;
            return size;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) { SeekCalls++; throw new NotSupportedException(); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
