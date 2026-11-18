using System.Collections;
using System.Runtime.ExceptionServices;

namespace Mmd.Modern.Core;

internal delegate void OwnedVertexRangeWriter<T>(int rangeIndex, int rangeOffset, Span<T> destination) where T : unmanaged;

/// <summary>Private immutable vertex storage. Each publication owns a small chunk table and shares
/// unchanged immutable payloads, never a prior storage object or snapshot. A root may slice one
/// privately owned array; no owned array or mutable memory is exposed to callers.</summary>
internal sealed class OwnedVertexChunks<T> : IList<T> where T : unmanaged
{
    internal const int ChunkSize = 4096;
    private readonly ReadOnlyMemory<T>[] chunks;
    public int Count { get; }
    internal int ChunkCount => chunks.Length;
    public bool IsReadOnly => true;

    /// <summary>Takes sole ownership of an already-private array. Callers must never retain or
    /// mutate it. Public input paths must make their defensive copy before calling this.</summary>
    internal OwnedVertexChunks(T[] owned)
    {
        ArgumentNullException.ThrowIfNull(owned);
        Count = owned.Length;
        chunks = new ReadOnlyMemory<T>[Count / ChunkSize + (Count % ChunkSize == 0 ? 0 : 1)];
        for (int chunk = 0; chunk < chunks.Length; chunk++)
        {
            int start = chunk * ChunkSize;
            chunks[chunk] = owned.AsMemory(start, Math.Min(ChunkSize, Count - start));
        }
    }

    private OwnedVertexChunks(int count, ReadOnlyMemory<T>[] chunks) { Count = count; this.chunks = chunks; }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            if (index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            return chunks[index / ChunkSize].Span[index % ChunkSize];
        }
        set => throw new NotSupportedException("Owned vertex chunks are immutable.");
    }

    internal ReadOnlySpan<T> GetChunk(int chunkIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        if (chunkIndex >= chunks.Length) throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        return chunks[chunkIndex].Span;
    }

    internal void CopyTo(int sourceIndex, Span<T> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceIndex);
        if (sourceIndex > Count || destination.Length > Count - sourceIndex)
            throw new ArgumentException("Vertex range is outside the owned storage.", nameof(destination));
        int copied = 0;
        while (copied < destination.Length)
        {
            int source = sourceIndex + copied, offset = source % ChunkSize;
            var chunk = chunks[source / ChunkSize].Span;
            int count = Math.Min(chunk.Length - offset, destination.Length - copied);
            chunk.Slice(offset, count).CopyTo(destination.Slice(copied, count));
            copied += count;
        }
    }

    internal OwnedVertexChunks<T> WithRanges(IReadOnlyList<(int Start, int Count)> ranges, OwnedVertexRangeWriter<T> write)
    {
        ArgumentNullException.ThrowIfNull(ranges); ArgumentNullException.ThrowIfNull(write);
        // A writer cannot change the already-validated range description while a later chunk
        // is being visited. Only this bounded small range table is copied here.
        var updates = ranges.ToArray(); int end = 0;
        foreach (var range in updates)
        {
            if (range.Start < end || range.Start > Count || range.Count <= 0 || range.Count > Count - range.Start)
                throw new ArgumentException("Composition ranges must be ordered, disjoint and within the baseline.", nameof(ranges));
            end = range.Start + range.Count;
        }
        if (updates.Length == 0) return this;
        var next = chunks.ToArray(); var written = new T[]?[chunks.Length];
        for (int rangeIndex = 0; rangeIndex < updates.Length; rangeIndex++)
        {
            var range = updates[rangeIndex]; int rangeOffset = 0;
            while (rangeOffset < range.Count)
            {
                int position = range.Start + rangeOffset, chunkIndex = position / ChunkSize, chunkOffset = position % ChunkSize;
                var owned = written[chunkIndex];
                if (owned is null)
                {
                    owned = chunks[chunkIndex].ToArray(); written[chunkIndex] = owned; next[chunkIndex] = owned;
                }
                int count = Math.Min(owned.Length - chunkOffset, range.Count - rangeOffset);
                write(rangeIndex, rangeOffset, owned.AsSpan(chunkOffset, count));
                rangeOffset += count;
            }
        }
        return new(Count, next);
    }

    /// <summary>Trusted pure writers may update separate target chunks concurrently. Caller
    /// callbacks and mutable input payloads must use the serial WithRanges path instead.</summary>
    internal OwnedVertexChunks<T> WithTrustedParallelRanges(IReadOnlyList<(int Start, int Count)> ranges,
        OwnedVertexRangeWriter<T> write, int maxDegree)
    {
        ArgumentNullException.ThrowIfNull(ranges); ArgumentNullException.ThrowIfNull(write);
        var updates = ranges.ToArray(); int end = 0, dirtyVertices = 0;
        foreach (var range in updates)
        {
            if (range.Start < end || range.Start > Count || range.Count <= 0 || range.Count > Count - range.Start)
                throw new ArgumentException("Composition ranges must be ordered, disjoint and within the baseline.", nameof(ranges));
            end = range.Start + range.Count;
            dirtyVertices += range.Count;
        }
        if (updates.Length == 0) return this;

        // Freeze one entry per touched target chunk. Several actor ranges in one chunk share
        // a single worker and private clone, so neither gaps nor neighbouring ranges can race.
        var work = new List<(int Chunk, int FirstRange)>();
        for (int rangeIndex = 0; rangeIndex < updates.Length; rangeIndex++)
        {
            var range = updates[rangeIndex];
            int lastChunk = (range.Start + range.Count - 1) / ChunkSize;
            for (int chunkIndex = range.Start / ChunkSize; chunkIndex <= lastChunk; chunkIndex++)
                if (work.Count == 0 || work[^1].Chunk != chunkIndex) work.Add((chunkIndex, rangeIndex));
        }
        var plan = work.ToArray();
        var next = chunks.ToArray();
        void WriteChunk(int workIndex)
        {
            var item = plan[workIndex];
            var owned = chunks[item.Chunk].ToArray();
            int chunkStart = item.Chunk * ChunkSize, chunkEnd = chunkStart + owned.Length;
            for (int rangeIndex = item.FirstRange; rangeIndex < updates.Length && updates[rangeIndex].Start < chunkEnd; rangeIndex++)
            {
                var range = updates[rangeIndex];
                int start = Math.Max(chunkStart, range.Start), stop = Math.Min(chunkEnd, range.Start + range.Count);
                write(rangeIndex, start - range.Start, owned.AsSpan(start - chunkStart, stop - start));
            }
            next[item.Chunk] = owned;
        }

        int maximum = Math.Min(16, Math.Max(1, Environment.ProcessorCount));
        int degree = Math.Clamp(maxDegree, 1, maximum);
        if (degree == 1 || dirtyVertices < 32768 || plan.Length < 2)
            for (int index = 0; index < plan.Length; index++) WriteChunk(index);
        else
        {
            var failures = new ExceptionDispatchInfo?[plan.Length];
            int workerCount = Math.Min(degree, plan.Length), claimed = -1;
            Parallel.For(0, workerCount, new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                TaskScheduler = TaskScheduler.Default,
            }, _ =>
            {
                // Claim one chunk at a time so slower cores cannot stall later chunks.
                for (int index; (index = Interlocked.Increment(ref claimed)) < plan.Length;)
                {
                    try { WriteChunk(index); }
                    catch (Exception error) { failures[index] = ExceptionDispatchInfo.Capture(error); }
                }
            });
            // Every worker has joined before an error or immutable result escapes. Preserve
            // the first failing target chunk's original exception, independent of completion.
            foreach (var failure in failures) failure?.Throw();
        }
        return new(Count, next);
    }

    /// <summary>Replace complete ranges from immutable owned sources. Fully covered chunks may
    /// share one immutable source segment; other touched chunks get one private array, filled
    /// once from either their old gaps or the replacement payloads.
    /// No source owner or writable source memory is retained by the published result.</summary>
    internal OwnedVertexChunks<T> WithCopiedRanges(IReadOnlyList<(int Start, OwnedVertexChunks<T> Source)> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        var updates = replacements.ToArray(); int end = 0;
        foreach (var range in updates)
        {
            if (range.Source is null || range.Start < end || range.Start > Count
                || range.Source.Count <= 0 || range.Source.Count > Count - range.Start)
                throw new ArgumentException("Replacement ranges must be ordered, disjoint and within the baseline.", nameof(replacements));
            end = range.Start + range.Source.Count;
        }
        if (updates.Length == 0) return this;

        var next = chunks.ToArray();
        int rangeIndex = 0, chunkIndex = updates[0].Start / ChunkSize;
        while (rangeIndex < updates.Length)
        {
            var previous = chunks[chunkIndex].Span;
            int chunkStart = chunkIndex * ChunkSize, chunkEnd = chunkStart + previous.Length;
            var replacement = updates[rangeIndex];
            int replacementOffset = chunkStart - replacement.Start;
            if (replacementOffset >= 0 && previous.Length <= replacement.Source.Count - replacementOffset
                && previous.Length <= replacement.Source.chunks[replacementOffset / ChunkSize].Length - replacementOffset % ChunkSize)
            {
                // The preflighted disjoint ranges guarantee no later replacement touches this
                // complete target chunk. Retain just its immutable memory, never its owner.
                next[chunkIndex] = replacement.Source.chunks[replacementOffset / ChunkSize]
                    .Slice(replacementOffset % ChunkSize, previous.Length);
                if (previous.Length == replacement.Source.Count - replacementOffset) rangeIndex++;
            }
            else
            {
                var owned = new T[previous.Length]; int cursor = 0;
                while (rangeIndex < updates.Length && updates[rangeIndex].Start < chunkEnd)
                {
                    var range = updates[rangeIndex];
                    int destinationStart = Math.Max(0, range.Start - chunkStart);
                    // Only the gaps survive from the old chunk. Replaced values are never copied
                    // from the baseline before being overwritten, including complete inner chunks.
                    previous.Slice(cursor, destinationStart - cursor).CopyTo(owned.AsSpan(cursor));
                    int sourceOffset = Math.Max(0, chunkStart - range.Start);
                    int count = Math.Min(range.Source.Count - sourceOffset, owned.Length - destinationStart);
                    range.Source.CopyTo(sourceOffset, owned.AsSpan(destinationStart, count));
                    cursor = destinationStart + count;
                    if (count < range.Source.Count - sourceOffset) break;
                    rangeIndex++;
                }
                previous[cursor..].CopyTo(owned.AsSpan(cursor));
                next[chunkIndex] = owned;
            }
            if (rangeIndex < updates.Length)
                chunkIndex = Math.Max(chunkIndex + 1, updates[rangeIndex].Start / ChunkSize);
        }
        return new(Count, next);
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array); ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        if (arrayIndex > array.Length || Count > array.Length - arrayIndex)
            throw new ArgumentException("Destination is too small for the owned vertices.", nameof(array));
        CopyTo(0, array.AsSpan(arrayIndex, Count));
    }
    public int IndexOf(T item)
    {
        var equality = EqualityComparer<T>.Default;
        for (int index = 0; index < Count; index++) if (equality.Equals(this[index], item)) return index;
        return -1;
    }
    public bool Contains(T item) => IndexOf(item) >= 0;
    public IEnumerator<T> GetEnumerator()
    { for (int index = 0; index < Count; index++) yield return this[index]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(T item) => throw new NotSupportedException("Owned vertex chunks are immutable.");
    public void Clear() => throw new NotSupportedException("Owned vertex chunks are immutable.");
    public void Insert(int index, T item) => throw new NotSupportedException("Owned vertex chunks are immutable.");
    public bool Remove(T item) => throw new NotSupportedException("Owned vertex chunks are immutable.");
    public void RemoveAt(int index) => throw new NotSupportedException("Owned vertex chunks are immutable.");
}
