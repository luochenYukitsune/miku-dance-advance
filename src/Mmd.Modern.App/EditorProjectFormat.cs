using Mmd.Modern.Application;

namespace Mmd.Modern.App;

/// <summary>The selected engine's fixed native package identity. Changing a suffix is not conversion.</summary>
public sealed record EditorProjectFormat
{
    private EditorProjectFormat(string packageFormat, string extension)
    { PackageFormat = packageFormat; Extension = extension; }

    public string PackageFormat { get; }
    public string Extension { get; }
    public static EditorProjectFormat Modern { get; } = new(PmaProjectStore.Format, ".pma");
    public static EditorProjectFormat Classic { get; } = new(PmcProjectStore.Format, ".pmc");

    internal static EditorProjectFormat FromPackageFormat(string packageFormat) => packageFormat switch
    {
        PmaProjectStore.Format => Modern,
        PmcProjectStore.Format => Classic,
        _ => throw new InvalidDataException("Unknown native project package identity.")
    };

    internal string RequirePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(full), Extension))
            throw new ArgumentException("The selected engine requires a " + Extension + " project.", nameof(path));
        return full;
    }
}
