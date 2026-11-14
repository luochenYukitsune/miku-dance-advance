namespace Mmd.Modern.Effects;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One load/parse/validate observation. Codes mirror MME's own error vocabulary where known.</summary>
public sealed record MmeDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? File = null,
    int Line = 0,
    int Column = 0)
{
    public override string ToString()
    {
        var where = File is null ? string.Empty : $"{File}({Line},{Column}): ";
        return $"{where}{Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
    }
}

public static class MmeDiagnosticCodes
{
    public const string EncodingFallback = "MME0001";
    public const string IncludeNotFound = "MME0002";
    public const string IncludeCycle = "MME0003";
    public const string UnknownDirective = "MME0004";
    public const string UnexpectedToken = "MME0005";
    public const string UnterminatedAnnotation = "MME0006";
    public const string UnknownPassName = "MME0010";
    public const string UnknownTextureName = "MME0011";
    public const string UnknownParameterName = "MME0012";
    public const string UnsupportedScriptCommand = "MME0013";
    public const string InvalidRenderColorTarget = "MME0014";
    public const string InvalidClearType = "MME0015";
    public const string InvalidClearColorType = "MME0016";
    public const string InvalidClearDepthType = "MME0017";
    public const string UnknownScriptExternal = "MME0018";
    public const string UnknownSemantic = "MME0019";
    public const string SyntaxError = "MME0020";
    public const string InvalidConditionalExpression = "MME0021";
    public const string UnsupportedInitializer = "MME0022";
    public const string InvalidLoopType = "MME0023";
    public const string UnsupportedNestedLoop = "MME0024";
}
