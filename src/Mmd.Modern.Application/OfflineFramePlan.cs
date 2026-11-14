using System.Numerics;

namespace Mmd.Modern.Application;

/// <summary>Reduced nonnegative rational value. No floating-point time accumulates in an output plan.</summary>
public readonly record struct OfflineFrameTime
{
    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }
    public OfflineFrameTime(BigInteger numerator, BigInteger denominator)
    {
        if (numerator < 0 || denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / gcd; Denominator = denominator / gcd;
    }
    public double ToDouble()
    {
        if (Denominator <= 0) throw new InvalidOperationException("An uninitialized rational time is invalid.");
        double numerator = (double)Numerator, denominator = (double)Denominator;
        // Keep the established division for ordinary output-plan times.
        if (double.IsFinite(numerator) && double.IsFinite(denominator)) return numerator / denominator;

        // Converting the terms separately can erase subnormals or produce Infinity / Infinity.
        // Round the rational directly to the binary64 grid, including its fixed subnormal spacing.
        long exponent = Numerator.GetBitLength() - Denominator.GetBitLength();
        if (exponent > 1024) return double.PositiveInfinity;
        if (exponent < -1075) return 0;
        int binaryExponent = (int)exponent;
        if (binaryExponent >= 0 ? Numerator < (Denominator << binaryExponent)
            : (Numerator << -binaryExponent) < Denominator) binaryExponent--;
        if (binaryExponent > 1023) return double.PositiveInfinity;
        int scale = Math.Max(binaryExponent - 52, -1074);
        BigInteger dividend = Numerator, divisor = Denominator;
        if (scale < 0) dividend <<= -scale;
        else divisor <<= scale;
        var significand = BigInteger.DivRem(dividend, divisor, out var remainder);
        int midpoint = (remainder << 1).CompareTo(divisor);
        if (midpoint > 0 || (midpoint == 0 && !significand.IsEven)) significand++;
        return Math.ScaleB((double)significand, scale);
    }
}

public readonly record struct OfflineFrameRate
{
    public int Numerator { get; }
    public int Denominator { get; }
    public OfflineFrameRate(int numerator, int denominator = 1)
    {
        if (numerator <= 0 || denominator <= 0 || numerator > 1_000_000 || denominator > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(numerator), "FPS requires positive numerator and denominator at most 1,000,000.");
        int gcd = (int)BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / gcd; Denominator = denominator / gcd;
    }
}

public sealed record OfflineFrameSample(long Index, OfflineFrameTime PresentationTime,
    OfflineFrameTime RequestedSourceFrame, OfflineFrameTime SampledSourceFrame);

/// <summary>Integer MMD source-frame interval, optionally including its end sample for legacy callers.
/// Duration-based output excludes that end boundary; a zero-length interval still captures one image.
/// Output timestamps start at zero; MMD always remains 30fps.
/// The simulation grid is 120Hz independently of output FPS. Off-grid output holds the preceding complete tick,
/// explicitly reported as SampledSourceFrame; it does not vary the physics timestep to fit an encoder.</summary>
public sealed class OfflineFramePlan
{
    public const int SourceFramesPerSecond = 30;
    public const int SimulationTicksPerSecond = 120;
    public const long MaximumFrames = 10_000_000;
    public const long MaximumSimulationTicks = 100_000_000;
    public int Width { get; }
    public int Height { get; }
    public OfflineFrameRate FrameRate { get; }
    public uint StartFrame { get; }
    public uint EndFrame { get; }
    public uint PhysicsPreRollFrames { get; }
    public bool IncludeEndFrame { get; }
    public long FrameCount { get; }
    public uint SimulationStartFrame => StartFrame - PhysicsPreRollFrames;

    public OfflineFramePlan(int width, int height, OfflineFrameRate frameRate, uint startFrame, uint endFrame,
        uint physicsPreRollFrames = 0, bool includeEndFrame = true)
    {
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > 268_435_456)
            throw new ArgumentOutOfRangeException(nameof(width), "Output dimensions exceed the bounded frame allocation contract.");
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
            throw new ArgumentException("An initialized positive frame rate is required.", nameof(frameRate));
        if (endFrame < startFrame || physicsPreRollFrames > startFrame)
            throw new ArgumentOutOfRangeException(nameof(endFrame), "The interval must ascend and pre-roll cannot precede frame zero.");
        var durationNumerator = (BigInteger)(endFrame - startFrame) * frameRate.Numerator;
        var durationDenominator = SourceFramesPerSecond * (BigInteger)frameRate.Denominator;
        var count = includeEndFrame ? durationNumerator / durationDenominator + 1
            : BigInteger.Max(BigInteger.One, (durationNumerator + durationDenominator - 1) / durationDenominator);
        if (count > MaximumFrames || (long)(endFrame - startFrame + (ulong)physicsPreRollFrames) * 4 > MaximumSimulationTicks)
            throw new ArgumentOutOfRangeException(nameof(endFrame), "Offline frame or simulation tick budget exceeded.");
        Width = width; Height = height; FrameRate = frameRate; StartFrame = startFrame; EndFrame = endFrame;
        PhysicsPreRollFrames = physicsPreRollFrames; IncludeEndFrame = includeEndFrame; FrameCount = (long)count;
    }

    public OfflineFrameSample GetSample(long index)
    {
        if (index < 0 || index >= FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        BigInteger numerator = (BigInteger)StartFrame * FrameRate.Numerator
            + (BigInteger)index * SourceFramesPerSecond * FrameRate.Denominator;
        return new(index, new((BigInteger)index * FrameRate.Denominator, FrameRate.Numerator),
            new(numerator, FrameRate.Numerator), new(numerator * 4 / FrameRate.Numerator, 4));
    }
}
