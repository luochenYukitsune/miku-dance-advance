namespace Mmd.Modern.Rendering.D3D12;

/// <summary>The requested GI reservation exceeds its memory policy before any GPU frame begins.
/// Callers may explicitly retry a different preview request; export quality is never changed here.</summary>
public sealed class D3D12GiMemoryBudgetExceededException : NotSupportedException
{
    public ulong RequiredBytes { get; }
    public ulong AvailableBytes { get; }
    public int SpatialSamples { get; }
    public bool IsPreSubmit => true;

    // Only the renderer's measured preflight gates construct this exception. Allocation,
    // command recording, submission and device failures retain their original exception types.
    internal D3D12GiMemoryBudgetExceededException(string message, ulong requiredBytes, ulong availableBytes,
        int spatialSamples) : base(message)
    {
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
        SpatialSamples = spatialSamples;
    }
}
