using System.Reflection;
using System.Runtime.ExceptionServices;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12BufferUploadWriterTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public void InvalidWriterArgumentsAreRejectedBeforeAllocationOrInvocation()
    {
        var method = GetStagingWrite();
        using var runtime = new D3D12GpuRuntime();
        using var staging = new D3D12BufferUploadStaging(runtime);
        runtime.Begin();
        var writer = new Writer();
        Assert.ThrowsAny<ArgumentException>(() => Write(method, staging, 0, writer));
        Assert.ThrowsAny<ArgumentException>(() => Write(method, staging, -1, writer));
        Assert.ThrowsAny<ArgumentException>(() => Invoke(method, staging, [4, null!]));
        Assert.Equal(0, writer.Calls);
        Assert.Equal(0, staging.Statistics.PageCreations);
        Assert.Equal(0, staging.Statistics.Copies);
        runtime.Abort();
    }

    [Fact]
    public void MixedWritesUseDistinctAlignedTailsAndRemainExactAfterPoolDisposal()
    {
        var method = GetStagingWrite();
        using var runtime = new D3D12GpuRuntime();
        using var staging = new D3D12BufferUploadStaging(runtime);
        runtime.Begin();
        var first = Payload(513, 1);
        var second = Payload(997, 2);
        var third = Payload(71, 3);
        var a = staging.Write(first);
        var b = Write(method, staging, second.Length, new Writer { Bytes = second });
        var c = staging.Write(third);
        Assert.Same(a.Resource, b.Resource);
        Assert.Same(a.Resource, c.Resource);
        Assert.Equal(0UL, a.Offset);
        Assert.Equal(516UL, b.Offset);
        Assert.Equal(1516UL, c.Offset);
        Assert.Equal(3, staging.Statistics.Copies);
        Assert.Equal(first.Length + second.Length + third.Length, staging.Statistics.CopiedBytes);
        using var readA = QueueRead(runtime, a, first.Length);
        using var readB = QueueRead(runtime, b, second.Length);
        using var readC = QueueRead(runtime, c, third.Length);
        staging.Dispose();
        Assert.Equal(0, staging.Statistics.RetainedPages);
        Assert.Equal(1, staging.Statistics.LivePages);
        runtime.Submit();
        runtime.WaitForIdle();
        Assert.Equal(first, Read(readA, first.Length));
        Assert.Equal(second, Read(readB, second.Length));
        Assert.Equal(third, Read(readC, third.Length));
        Assert.Equal(0, staging.Statistics.LivePages);
    }

    [Fact]
    public void PartialFailureConsumesItsRangeWithoutCountingSuccessOrCorruptingCopies()
    {
        var method = GetStagingWrite();
        using var runtime = new D3D12GpuRuntime();
        using var staging = new D3D12BufferUploadStaging(runtime);
        runtime.Begin();
        var beforeBytes = Payload(13, 5);
        var afterBytes = Payload(11, 9);
        var before = staging.Write(beforeBytes);
        var failed = new Writer { ThrowAfterPartial = true };
        var initial = staging.Statistics;
        Assert.Throws<InvalidOperationException>(() => Write(method, staging, 17, failed));
        Assert.Equal(1, failed.Calls);
        Assert.Equal(initial.Copies, staging.Statistics.Copies);
        Assert.Equal(initial.CopiedBytes, staging.Statistics.CopiedBytes);
        var after = Write(method, staging, afterBytes.Length, new Writer { Bytes = afterBytes });
        Assert.Same(before.Resource, after.Resource);
        Assert.Equal(0UL, before.Offset);
        // 13 bytes consume 16 aligned bytes; the failed 17-byte reservation consumes 20.
        Assert.Equal(36UL, after.Offset);
        Assert.Equal(2, staging.Statistics.Copies);
        Assert.Equal(beforeBytes.Length + afterBytes.Length, staging.Statistics.CopiedBytes);
        using var readBefore = QueueRead(runtime, before, beforeBytes.Length);
        using var readAfter = QueueRead(runtime, after, afterBytes.Length);
        runtime.Submit(); runtime.WaitForIdle();
        Assert.Equal(beforeBytes, Read(readBefore, beforeBytes.Length));
        Assert.Equal(afterBytes, Read(readAfter, afterBytes.Length));
    }

    [Fact]
    public void WriterRejectsPoolReentryAndRestoresGuardOnBothSuccessAndFailure()
    {
        var method = GetStagingWrite();
        using var runtime = new D3D12GpuRuntime();
        using var staging = new D3D12BufferUploadStaging(runtime);
        runtime.Begin();
        var nested = new Writer { Bytes = Payload(4, 2) };
        var outerBytes = Payload(9, 3);
        var outer = new Writer
        {
            Bytes = outerBytes,
            BeforeWrite = () =>
            {
                Assert.Throws<InvalidOperationException>(() => staging.Write(new byte[4]));
                Assert.Throws<InvalidOperationException>(() => Write(method, staging, 4, nested));
                Assert.Throws<InvalidOperationException>(() => staging.WriteLargeRetained(new byte[4]));
                Assert.Throws<InvalidOperationException>(() => staging.BeginFrame());
                Assert.Throws<InvalidOperationException>(() => staging.ClearLargeRetainedPages());
                Assert.Throws<InvalidOperationException>(() => staging.Dispose());
            },
        };
        var first = Write(method, staging, outerBytes.Length, outer);
        Assert.Equal(1, outer.Calls);
        Assert.Equal(0, nested.Calls);
        var failure = new Writer { ThrowAfterPartial = true };
        Assert.Throws<InvalidOperationException>(() => Write(method, staging, 7, failure));
        var finalBytes = Payload(5, 7);
        var final = Write(method, staging, finalBytes.Length, new Writer { Bytes = finalBytes });
        using var readFirst = QueueRead(runtime, first, outerBytes.Length);
        using var readFinal = QueueRead(runtime, final, finalBytes.Length);
        runtime.Submit(); runtime.WaitForIdle();
        Assert.Equal(outerBytes, Read(readFirst, outerBytes.Length));
        Assert.Equal(finalBytes, Read(readFinal, finalBytes.Length));
    }

    [Fact]
    public void OversizedDirectWriteIsTransientAndAbortReleasesItWhileSlotPageCanBeReused()
    {
        var method = GetStagingWrite();
        using var runtime = new D3D12GpuRuntime();
        using var staging = new D3D12BufferUploadStaging(runtime);
        runtime.Begin();
        var small = Write(method, staging, 513, new Writer { Fill = 0x31 });
        int oversized = D3D12BufferUploadStaging.MaximumRetainedPageBytes + 1;
        Write(method, staging, oversized, new Writer { Fill = 0x79 });
        Assert.Equal(1, staging.Statistics.RetainedPages);
        Assert.Equal(1, staging.Statistics.LiveTransientPages);
        Assert.Equal((long)MiB, staging.Statistics.RetainedBytes);
        runtime.Abort();
        Assert.Equal(0, staging.Statistics.LiveTransientPages);
        Assert.Equal(1, staging.Statistics.LivePages);
        for (int i = 0; i < 2; i++) { runtime.Begin(); runtime.Submit(); }
        runtime.Begin();
        var expected = Payload(513, 11);
        var recovered = Write(method, staging, expected.Length, new Writer { Bytes = expected });
        Assert.Same(small.Resource, recovered.Resource);
        Assert.Equal(0UL, recovered.Offset);
        Assert.Equal(2, staging.Statistics.PageCreations);
        using var read = QueueRead(runtime, recovered, expected.Length);
        runtime.Submit(); runtime.WaitForIdle();
        Assert.Equal(expected, Read(read, expected.Length));
    }

    [Fact]
    public void CacheWriterFailureDoesNotCountUploadAndNextSuccessfulCopyIsExact()
    {
        var method = GetCacheWrite();
        using var runtime = new D3D12GpuRuntime();
        using var cache = new D3D12GpuCache(runtime);
        var context = runtime.Begin();
        long uploads = runtime.BufferUploads;
        var stats = cache.BufferUploadStatistics;
        var failed = new Writer { ThrowAfterPartial = true };
        Assert.Throws<InvalidOperationException>(() => WriteCache(method, cache, 17, ResourceStates.CopySource, failed));
        Assert.Equal(1, failed.Calls);
        Assert.Equal(uploads, runtime.BufferUploads);
        Assert.Equal(stats.Copies, cache.BufferUploadStatistics.Copies);
        Assert.Equal(stats.CopiedBytes, cache.BufferUploadStatistics.CopiedBytes);
        var bytes = Payload(257, 19);
        var writer = new Writer { Bytes = bytes };
        var source = WriteCache(method, cache, bytes.Length, ResourceStates.CopySource, writer);
        Assert.Equal(1, writer.Calls);
        Assert.Equal(uploads + 1, runtime.BufferUploads);
        using var read = context.Device.CreateCommittedResource(HeapType.Readback,
            ResourceDescription.Buffer((ulong)bytes.Length), ResourceStates.CopyDest);
        context.Commands.CopyBufferRegion(read, 0, source, 0, (ulong)bytes.Length);
        runtime.Submit(); runtime.WaitForIdle();
        Assert.Equal(bytes, Read(read, bytes.Length));
        // The existing cache exposes no reliable count for failed temporary DEFAULT resources or
        // command-list entries. This asserts observable counters and output, not disposal proof.
    }

    private sealed class Writer
    {
        internal byte[]? Bytes;
        internal byte Fill;
        internal bool ThrowAfterPartial;
        internal Action? BeforeWrite;
        internal int Calls;

        public void Write(Span<byte> destination)
        {
            Calls++;
            BeforeWrite?.Invoke();
            if (ThrowAfterPartial)
            {
                destination.Slice(0, destination.Length / 2).Fill(0xe7);
                throw new InvalidOperationException("Deliberate partial writer failure.");
            }
            if (Bytes is null) destination.Fill(Fill);
            else
            {
                Assert.Equal(Bytes.Length, destination.Length);
                Bytes.AsSpan().CopyTo(destination);
            }
        }
    }

    private static MethodInfo GetStagingWrite()
    {
        var method = typeof(D3D12BufferUploadStaging).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == "Write" && candidate.GetParameters().Length == 2
                && candidate.GetParameters()[0].ParameterType == typeof(int)
                && typeof(Delegate).IsAssignableFrom(candidate.GetParameters()[1].ParameterType));
        Assert.NotNull(method);
        AssertWriterSignature(method.GetParameters()[1].ParameterType);
        return method;
    }

    private static MethodInfo GetCacheWrite()
    {
        var method = typeof(D3D12GpuCache).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == "FrameBuffer" && candidate.GetParameters().Length == 3
                && candidate.GetParameters()[0].ParameterType == typeof(int)
                && candidate.GetParameters()[1].ParameterType == typeof(ResourceStates)
                && typeof(Delegate).IsAssignableFrom(candidate.GetParameters()[2].ParameterType));
        Assert.NotNull(method);
        AssertWriterSignature(method.GetParameters()[2].ParameterType);
        return method;
    }

    private static void AssertWriterSignature(Type delegateType)
    {
        var invoke = delegateType.GetMethod("Invoke");
        Assert.NotNull(invoke);
        Assert.Equal(typeof(void), invoke.ReturnType);
        var parameters = invoke.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(Span<byte>), parameters[0].ParameterType);
    }

    private static Delegate BindWriter(Type delegateType, Writer writer)
        => Delegate.CreateDelegate(delegateType, writer, typeof(Writer).GetMethod(nameof(Writer.Write))!);

    private static D3D12BufferUploadStaging.Allocation Write(MethodInfo method,
        D3D12BufferUploadStaging staging, int length, Writer writer)
        => (D3D12BufferUploadStaging.Allocation)Invoke(method, staging,
            [length, BindWriter(method.GetParameters()[1].ParameterType, writer)]);

    private static ID3D12Resource WriteCache(MethodInfo method, D3D12GpuCache cache,
        int length, ResourceStates state, Writer writer)
        => (ID3D12Resource)Invoke(method, cache,
            [length, state, BindWriter(method.GetParameters()[2].ParameterType, writer)]);

    private static object Invoke(MethodInfo method, object target, object[] arguments)
    {
        try { return method.Invoke(target, arguments)!; }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static byte[] Payload(int length, int seed)
    {
        var bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = unchecked((byte)(i * 17 + seed * 31));
        return bytes;
    }

    private static ID3D12Resource QueueRead(D3D12GpuRuntime runtime,
        D3D12BufferUploadStaging.Allocation source, int length)
    {
        var read = runtime.Device.CreateCommittedResource(HeapType.Readback,
            ResourceDescription.Buffer((ulong)length), ResourceStates.CopyDest);
        runtime.Commands.CopyBufferRegion(read, 0, source.Resource, source.Offset, (ulong)length);
        return read;
    }

    private static byte[] Read(ID3D12Resource resource, int length)
    {
        var data = resource.Map<byte>(0, length);
        try { return data.ToArray(); }
        finally { resource.Unmap(0); }
    }
}
