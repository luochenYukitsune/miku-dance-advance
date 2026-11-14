using Mmd.Modern.Scheduling;
using Xunit;
using static Mmd.Modern.Scheduling.Tests.OracleVectorTests;

namespace Mmd.Modern.Scheduling.Tests;

public sealed class SemanticTimeTests
{
    [Fact] public void Fixed_step_is_original_binary32_constant() => Assert.Equal(0x3C888889u, Bits(SemanticTime.FixedStep));

    [Theory]
    [InlineData(0u, 0, 1, 0, 0u)]
    [InlineData(1u, 0, 1, 0, 2u)]
    [InlineData(2u, 0, 1, 0, 3u)]
    [InlineData(3u, 0, 1, 0, 0u)]
    [InlineData(uint.MaxValue, 0, 1, 0, uint.MaxValue)]
    [InlineData(uint.MaxValue, 2, 1, 0, 2u)]
    [InlineData(3u, 0, 0, 0, 2u)]
    [InlineData(2u, 0, 1, 2, 2u)]
    public void Phase_preserves_nonzero_byte_and_unsigned_domain(uint phase, byte playing, byte idle, byte force, uint expected)
        => Assert.Equal(expected, SemanticTime.NextPhase(phase, playing, idle, force));

    [Theory]
    [InlineData(0ul, 1ul, 0xBF800000u)]
    [InlineData(0ul, ulong.MaxValue, 0x3F800000u)]
    [InlineData(0x8000000000000000ul, 0ul, 0xDF000000u)]
    [InlineData(0x7FFFFFFFFFFFFFFFul, 0ul, 0x5F000000u)]
    public void Timestamp_subtract_wraps_then_reinterprets_signed64(ulong now, ulong baseline, uint expected)
        => Assert.Equal(expected, Bits(SemanticTime.TimestampTarget(now, baseline, 1f, 0f)));

    [Fact]
    public void Recording_uses_signed_count_unsigned_start_and_sequential_binary32()
    {
        Assert.Equal(Bits(1f), Bits(SemanticTime.RecordingTarget(0, 30, 60f)));
        Assert.Equal(Bits(-0.5f), Bits(SemanticTime.RecordingTarget(-30, 0, 60f)));
        Assert.Equal(0x3C888889u, Bits(SemanticTime.RecordingDelta(60f)));
        Assert.Equal(Bits(143165580f), Bits(SemanticTime.RecordingTarget(0, uint.MaxValue, 60f)));
    }

    [Fact]
    public void Timestamp_rounds_product_before_adding_start()
        => Assert.Equal(998244352u, Bits(SemanticTime.TimestampTarget(123456789, 0, 0.0003f, -37037.035f)));

    [Theory]
    [InlineData(227888720, 2475739260u, 1106231951u, 1286334482u)]
    [InlineData(359863882, 2524391299u, 1123024896u, 1285961572u)]
    [InlineData(997893185, 2877423254u, 1103089369u, 1292052938u)]
    public void Recording_rounds_each_operation_instead_of_one_double_expression(int completed, uint start, uint fpsBits, uint expectedBits)
        => Assert.Equal(expectedBits, Bits(SemanticTime.RecordingTarget(completed, start, BitConverter.UInt32BitsToSingle(fpsBits))));
}
