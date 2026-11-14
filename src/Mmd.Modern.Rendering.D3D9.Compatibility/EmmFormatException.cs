using System.Collections.ObjectModel;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>An EMM could not be safely decoded, exceeded a caller budget, or failed final structural validation.</summary>
public sealed class EmmFormatException : FormatException
{
    public EmmFormatException(string message, IEnumerable<EmmDiagnostic> diagnostics, Exception? innerException = null)
        : base(message, innerException) => Diagnostics = Array.AsReadOnly(diagnostics.ToArray());

    public ReadOnlyCollection<EmmDiagnostic> Diagnostics { get; }
}
