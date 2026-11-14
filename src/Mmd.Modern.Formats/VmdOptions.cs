using System.Text;

namespace Mmd.Modern.Formats;

/// <summary>Selects the ANSI encoding for VMD names; the file does not declare its encoding.</summary>
public sealed class VmdReadOptions
{
    private IReadOnlyList<VmdNameOverride> nameOverrides = Array.Empty<VmdNameOverride>();
    private IReadOnlyList<string>? boneNameCandidates;
    private IReadOnlyList<string>? morphNameCandidates;
    public int AnsiCodePage { get; init; } = 932;
    public IReadOnlyList<VmdNameOverride> NameOverrides
    {
        get => nameOverrides;
        init { ArgumentNullException.ThrowIfNull(value); nameOverrides = Array.AsReadOnly(value.ToArray()); }
    }
    public IReadOnlyList<string>? BoneNameCandidates
    {
        get => boneNameCandidates;
        init => boneNameCandidates = value is null ? null : Array.AsReadOnly(value.ToArray());
    }
    public IReadOnlyList<string>? MorphNameCandidates
    {
        get => morphNameCandidates;
        init => morphNameCandidates = value is null ? null : Array.AsReadOnly(value.ToArray());
    }
}

/// <summary>Selects the ANSI encoding and optional exported model header without changing the motion.</summary>
public sealed class VmdWriteOptions
{
    /// <summary>1 writes the legacy 'Vocaloid Motion Data file' ten-byte model header; 2 writes VMD0002.</summary>
    public int Version { get; init; } = 2;
    public int AnsiCodePage { get; init; } = 932;
    public string? ModelNameOverride { get; init; }
    public bool PreserveOriginalNames { get; init; } = true;
}

internal static class VmdEncoding
{
    public static Encoding Create(int codePage)
    {
        if (codePage is not (932 or 936))
            throw new ArgumentOutOfRangeException(nameof(codePage), codePage, "VMD ANSI code page must be 932 or 936.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return LegacyAnsiEncoding.Create(codePage);
    }
}
