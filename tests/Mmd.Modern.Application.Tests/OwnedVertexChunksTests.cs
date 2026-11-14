using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class OwnedVertexChunksTests
{
    private const int Chunk = 4096;
    private delegate void CopyRange(int sourceIndex, Span<int> destination);
    private delegate ReadOnlySpan<int> ReadChunk(int chunkIndex);
    private static readonly Type StorageType = typeof(SceneSnapshot).Assembly.GetType("Mmd.Modern.Core.OwnedVertexChunks`1")!.MakeGenericType(typeof(int));

    [Fact]
    public void ChunkedWritesPreserveRangeOffsetsAndUnwrittenBytesAcrossSharedBoundaryChunks()
    {
        var source = Create(Enumerable.Range(0, Chunk * 3 + 5).ToArray());
        var next = Compose(source, [(Chunk - 2, 5), (Chunk + 9, 2), (Chunk * 3 + 4, 1)]);
        var expected = source.ToArray();
        Apply(expected, Chunk - 2, 5, 0); Apply(expected, Chunk + 9, 2, 1); Apply(expected, Chunk * 3 + 4, 1, 2);
        Assert.Equal(expected, next);
        Assert.Equal(Enumerable.Range(0, source.Count), source);
        Assert.Equal(5, Memories(next)[3].Length);
        Assert.True(Memories(source)[2].Equals(Memories(next)[2]));
        Assert.False(Memories(source)[0].Equals(Memories(next)[0]));
        Assert.False(Memories(source)[1].Equals(Memories(next)[1]));
        Assert.False(Memories(source)[3].Equals(Memories(next)[3]));
    }

    [Fact]
    public void CopyToEnumerationAndCollectionInterfacesNeedNoFlatStorage()
    {
        var source = Create(Enumerable.Range(0, Chunk * 2 + 7).ToArray());
        var changed = Compose(source, [(Chunk - 1, 3)]);
        var copied = Enumerable.Repeat(-1, source.Count + 4).ToArray(); changed.CopyTo(copied, 2);
        Assert.Equal(-1, copied[0]); Assert.Equal(-1, copied[1]); Assert.Equal(-1, copied[^1]);
        Assert.Equal(changed, copied.Skip(2).Take(changed.Count));
        Assert.Equal(changed.ToArray(), ((IEnumerable)changed).Cast<int>());
        Assert.True(changed.IsReadOnly); Assert.True(changed.Contains(100)); Assert.Equal(100, changed.IndexOf(100));
        Assert.Equal(-1, changed.IndexOf(-100));
        Assert.Throws<NotSupportedException>(() => changed[0] = 1);
        Assert.Throws<NotSupportedException>(() => changed.Add(1));
        Assert.Throws<NotSupportedException>(() => changed.Insert(0, 1));
        Assert.Throws<NotSupportedException>(() => changed.Remove(1));
        Assert.Throws<NotSupportedException>(() => changed.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => changed.Clear());
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = changed[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = changed[changed.Count]);
        Assert.Throws<ArgumentNullException>(() => changed.CopyTo(null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => changed.CopyTo(new int[changed.Count], -1));
        Assert.Throws<ArgumentException>(() => changed.CopyTo(new int[changed.Count], 1));
        Assert.Same(changed, Compose(changed, []));
    }

    [Fact]
    public void BoundedSpanCopyCrossesChunksAndClipsTheFinalChunk()
    {
        var source = Create(Enumerable.Range(0, Chunk * 2 + 7).ToArray());
        var changed = Compose(source, [(Chunk - 1, 3)]);
        var copy = (CopyRange)Delegate.CreateDelegate(typeof(CopyRange), changed,
            StorageType.GetMethod("CopyTo", BindingFlags.Instance | BindingFlags.NonPublic)!);
        var read = (ReadChunk)Delegate.CreateDelegate(typeof(ReadChunk), changed,
            StorageType.GetMethod("GetChunk", BindingFlags.Instance | BindingFlags.NonPublic)!);
        var values = new int[Chunk + 9]; copy(Chunk - 2, values);
        Assert.Equal(changed.Skip(Chunk - 2), values);
        Assert.Equal(7, read(2).Length);
        Assert.Equal(changed.Skip(Chunk * 2), read(2).ToArray());
        copy(changed.Count, Span<int>.Empty);
        Assert.Throws<ArgumentException>(() => copy(changed.Count, new int[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => copy(-1, new int[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => read(-1).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => read(3).ToArray());
    }

    [Fact]
    public void AllRangesValidateBeforeAnyWriterRunsAndWriterFailureKeepsOldOutput()
    {
        var source = Create(Enumerable.Range(0, Chunk + 3).ToArray());
        foreach (var invalid in new (int Start, int Count)[][]
        {
            [(-1, 1)], [(0, 0)], [(source.Count, 1)], [(1, int.MaxValue)],
            [(0, 2), (1, 1)], [(1, 1), (0, 1)], [(0, 1), (source.Count, 1)]
        })
            Assert.Throws<ArgumentException>(() => Compose(source, invalid, nameof(UnexpectedWriter)));
        Assert.Throws<InvalidOperationException>(() => Compose(source, [(Chunk - 1, 3)], nameof(ThrowingWriter)));
        Assert.Equal(Enumerable.Range(0, source.Count), source);
    }

    [Fact]
    public void CopiedRangesShareCompleteImmutableSegmentsAndCopyMixedBoundaryChunks()
    {
        var baseline = Create(Enumerable.Range(0, Chunk * 9 + 19).ToArray());
        var aligned = Create(Enumerable.Range(100_000, Chunk * 2).ToArray());
        var unaligned = Create(Enumerable.Range(200_000, Chunk * 2 + 19).ToArray());
        var partial = Create(Enumerable.Range(300_000, 10).ToArray());
        var tail = Create(Enumerable.Range(400_000, Chunk + 26).ToArray());
        (int Start, IList<int> Source)[] ranges =
        [
            (0, aligned), (Chunk * 2 + 3, unaligned), (Chunk * 5 + 4, partial), (Chunk * 8 - 7, tail)
        ];
        var expected = baseline.ToArray();
        foreach (var range in ranges) range.Source.CopyTo(expected, range.Start);

        var next = CopyRanges(baseline, ranges);

        Assert.Equal(expected, next);
        Assert.Equal(Enumerable.Range(0, baseline.Count), baseline);
        var memories = Memories(next);
        Assert.Equal(Memories(aligned)[0], memories[0]);
        Assert.Equal(Memories(aligned)[1], memories[1]);
        Assert.Equal(Memories(baseline)[6], memories[6]);
        // The short target tail fits inside one source segment even though its source offset
        // is unaligned. The preceding full target chunk crosses two source segments.
        Assert.Equal(Memories(tail)[1].Slice(7, 19), memories[9]);
        foreach (int chunk in new[] { 2, 3, 4, 5, 7, 8 })
            Assert.All(new[] { baseline, aligned, unaligned, partial, tail }, source =>
                Assert.All(Memories(source), memory => Assert.False(memories[chunk].Span.Overlaps(memory.Span))));
    }

    [Fact]
    public void SharedCopiedChunksKeepSignedZeroAndOldGenerationsExactDuringLaterWrites()
    {
        int[] bits = [0, unchecked((int)0x80000000), 1, unchecked((int)0xbf800000)];
        var values = Enumerable.Range(0, Chunk * 2 + 19).Select(i => BitConverter.Int32BitsToSingle(bits[i % bits.Length])).ToArray();
        var source = Create(values.ToArray());
        var baseline = Create(new float[values.Length]);
        var first = CopyRanges(baseline, [(0, source)]);
        var before = first.Select(BitConverter.SingleToInt32Bits).ToArray();
        var patch = Create(new[] { 2f, -3f, 4f });

        var second = CopyRanges(first, [(Chunk - 1, patch)]);

        Assert.Equal(values.Select(BitConverter.SingleToInt32Bits), first.Select(BitConverter.SingleToInt32Bits));
        Assert.Equal(before, source.Select(BitConverter.SingleToInt32Bits));
        Assert.Equal(before, first.Select(BitConverter.SingleToInt32Bits));
        var expected = values.ToArray(); patch.CopyTo(expected, Chunk - 1);
        Assert.Equal(expected.Select(BitConverter.SingleToInt32Bits), second.Select(BitConverter.SingleToInt32Bits));
        for (int chunk = 0; chunk < Memories(first).Length; chunk++)
            Assert.Equal(Memories(source)[chunk], Memories(first)[chunk]);
        Assert.NotEqual(Memories(first)[0], Memories(second)[0]);
        Assert.NotEqual(Memories(first)[1], Memories(second)[1]);
        Assert.Equal(Memories(first)[2], Memories(second)[2]);
    }

    [Fact]
    public void SharedCopiedStorageDoesNotRetainSourceOrParentStorageObjects()
    {
        var retained = RetainOnlyCopiedResult();
        for (int attempt = 0; attempt < 3 && retained.Old.Any(reference => reference.IsAlive); attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(retained.Old, reference => Assert.False(reference.IsAlive));
        Assert.Equal(Chunk * 2 + 13, retained.Latest.Count);
        Assert.Equal(200_000, retained.Latest[0]);
        Assert.Equal(1_000_000, retained.Latest[Chunk + 1]);
        Assert.Equal(100_000 + Chunk * 2 + 12, retained.Latest[^1]);
        GC.KeepAlive(retained);
    }

    [Fact]
    public void NewStorageDoesNotRetainParentStorageObjects()
    {
        var retained = RetainOnlyLatest();
        for (int attempt = 0; attempt < 3 && retained.Old.Any(reference => reference.IsAlive); attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(retained.Old, reference => Assert.False(reference.IsAlive));
        Assert.Equal(Chunk * 2 + 1, retained.Latest.Count);
        Assert.Equal(1_000_000, retained.Latest[0]);
        GC.KeepAlive(retained);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IList<int> Latest, WeakReference[] Old) RetainOnlyLatest()
    {
        var first = Create(Enumerable.Range(0, Chunk * 2 + 1).ToArray());
        var second = Compose(first, [(0, 1)]);
        var third = Compose(second, [(Chunk + 1, 1)]);
        return (third, [new(first), new(second)]);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IList<int> Latest, WeakReference[] Old) RetainOnlyCopiedResult()
    {
        var baseline = Create(Enumerable.Range(0, Chunk * 2 + 13).ToArray());
        var source = Create(Enumerable.Range(100_000, baseline.Count).ToArray());
        var first = CopyRanges(baseline, [(0, source)]);
        var second = Compose(first, [(Chunk + 1, 1)]);
        var replacement = Create(Enumerable.Range(200_000, Chunk).ToArray());
        var latest = CopyRanges(second, [(0, replacement)]);
        return (latest, [new(baseline), new(source), new(first), new(second), new(replacement)]);
    }
    private static IList<T> Create<T>(T[] owned) where T : unmanaged
        => (IList<T>)Activator.CreateInstance(StorageType.GetGenericTypeDefinition().MakeGenericType(typeof(T)),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [owned], null)!;
    private static ReadOnlyMemory<T>[] Memories<T>(IList<T> value) where T : unmanaged
        => (ReadOnlyMemory<T>[])value.GetType().GetField("chunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static IList<T> CopyRanges<T>(IList<T> baseline, (int Start, IList<T> Source)[] ranges) where T : unmanaged
    {
        var storageType = baseline.GetType();
        var tuple = typeof(ValueTuple<,>).MakeGenericType(typeof(int), storageType);
        var values = Array.CreateInstance(tuple, ranges.Length);
        for (int i = 0; i < ranges.Length; i++) values.SetValue(Activator.CreateInstance(tuple, ranges[i].Start, ranges[i].Source), i);
        try { return (IList<T>)storageType.GetMethod("WithCopiedRanges", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(baseline, [values])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static IList<int> Compose(IList<int> source, (int Start, int Count)[] ranges, string writer = nameof(PatchWriter))
    {
        var method = StorageType.GetMethod("WithRanges", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var callback = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType,
            typeof(OwnedVertexChunksTests).GetMethod(writer, BindingFlags.Static | BindingFlags.NonPublic)!);
        try { return (IList<int>)method.Invoke(source, [ranges, callback])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Apply(int[] destination, int start, int count, int range)
    { for (int i = 0; i < count; i++) destination[start + i] = 1_000_000 + range * 100 + i; }
    private static void PatchWriter(int range, int offset, Span<int> destination)
    { for (int i = 0; i < destination.Length; i++) destination[i] = 1_000_000 + range * 100 + offset + i; }
    private static void UnexpectedWriter(int range, int offset, Span<int> destination)
        => throw new Xunit.Sdk.XunitException("An invalid later range reached the writer.");
    private static void ThrowingWriter(int range, int offset, Span<int> destination)
    { destination[0] = -1; throw new InvalidOperationException("writer failed"); }
}
