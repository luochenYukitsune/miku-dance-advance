using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Formats.Tests;

public sealed class PmxInputBudgetTests
{
    [Fact]
    public void DefaultAllowsTenGiBAndHostCanRaiseItWithoutAllocatingInputBuffer()
    {
        const long tenGiB = 10L * 1024 * 1024 * 1024;
        using var exact = new DeclaredLengthStream(tenGiB);
        Assert.Equal("header.magic", Assert.Throws<PmxFormatException>(() => PmxReader.Read(exact)).Field);
        using var over = new DeclaredLengthStream(tenGiB + 1);
        Assert.Equal("input.length", Assert.Throws<PmxFormatException>(() => PmxReader.Read(over)).Field);
        Assert.Equal(0, over.Position);
        using var larger = new DeclaredLengthStream(30L * 1024 * 1024 * 1024);
        Assert.Equal("header.magic", Assert.Throws<PmxFormatException>(() => PmxReader.Read(larger,
            new() { MaxInputBytes = larger.Length })).Field);
    }

    [Fact]
    public void SeekableBudgetMeasuresRemainingInputAndLeavesCallerStreamOpen()
    {
        using var stream = new MemoryStream(new byte[33]);
        stream.Position = 16;
        var error = Assert.Throws<PmxFormatException>(() => PmxReader.Read(stream, new() { MaxInputBytes = 16 }));
        Assert.Equal("input.length", error.Field);
        Assert.Equal(16, stream.Position); Assert.True(stream.CanRead);
        stream.Position = 17;
        error = Assert.Throws<PmxFormatException>(() => PmxReader.Read(stream, new() { MaxInputBytes = 16 }));
        Assert.Equal("header.magic", error.Field);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void InfiniteNonSeekableInputReadsOnlyOneBytePastBudget()
    {
        using var stream = new ProbeStream();
        var error = Assert.Throws<PmxFormatException>(() => PmxReader.Read(stream, new() { MaxInputBytes = 17 }));
        Assert.Equal("input.length", error.Field);
        Assert.Equal(18, stream.BytesRead); Assert.False(stream.Disposed);
    }

    [Fact]
    public void ValidNonSeekableInputParsesAtExactBudgetAndLeavesSourceOpen()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "rich-w1-uv4.pmx"));
        using var expectedInput = new MemoryStream(bytes, writable: false);
        var expected = PmxReader.Read(expectedInput);
        using var stream = new ProbeStream(contents: bytes);
        var actual = PmxReader.Read(stream, new() { MaxInputBytes = bytes.Length });
        Assert.Equal(PmxWriter.ToBytes(expected), PmxWriter.ToBytes(actual));
        Assert.Equal(bytes.Length, stream.BytesRead); Assert.False(stream.Disposed);
    }

    [Theory]
    [InlineData(32L)]
    [InlineData(long.MaxValue)]
    public void SourceReadFailureDoesNotDisposeCallerStream(long limit)
    {
        using var stream = new ProbeStream(failAfter: 5);
        Assert.Throws<IOException>(() => PmxReader.Read(stream, new() { MaxInputBytes = limit }));
        Assert.Equal(5, stream.BytesRead); Assert.False(stream.Disposed);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void InvalidBudgetRejectsWithoutReadingSource(long limit)
    {
        using var stream = new ProbeStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => PmxReader.Read(stream, new() { MaxInputBytes = limit }));
        Assert.Equal(0, stream.BytesRead); Assert.False(stream.Disposed);
    }

    [Fact]
    public void FileOverloadUsesBudgetAndOldOverloadsRemainAvailable()
    {
        string path = Path.Combine(Path.GetTempPath(), "pmx-budget-" + Guid.NewGuid().ToString("N") + ".pmx");
        try
        {
            File.WriteAllBytes(path, new byte[17]);
            var error = Assert.Throws<PmxFormatException>(() => PmxReader.Read(path, new() { MaxInputBytes = 16 }));
            Assert.Equal("input.length", error.Field);
            Assert.NotNull(typeof(PmxReader).GetMethod(nameof(PmxReader.Read), [typeof(Stream)]));
            Assert.NotNull(typeof(PmxReader).GetMethod(nameof(PmxReader.Read), [typeof(string)]));
        }
        finally { File.Delete(path); }
    }

    private sealed class DeclaredLengthStream(long declaredLength) : MemoryStream(new byte[4], writable: false)
    {
        public override long Length => declaredLength;
    }

    private sealed class ProbeStream(int? failAfter = null, byte[]? contents = null) : Stream
    {
        public long BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failAfter is { } failure && BytesRead >= failure) throw new IOException("Source read failed.");
            int read = failAfter is { } maximum ? Math.Min(count, (int)(maximum - BytesRead)) : count;
            if (contents is not null)
            {
                read = Math.Min(read, contents.Length - (int)BytesRead);
                contents.AsSpan((int)BytesRead, read).CopyTo(buffer.AsSpan(offset, read));
            }
            else buffer.AsSpan(offset, read).Clear();
            BytesRead += read; return read;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
