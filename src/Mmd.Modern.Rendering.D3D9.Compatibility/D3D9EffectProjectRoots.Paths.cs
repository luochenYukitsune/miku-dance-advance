using System.Text;

namespace Mmd.Modern.Rendering.D3D9.Compatibility;

/// <summary>Shared lexical EMM policy. This neither probes files nor discovers an implicit effect.</summary>
internal static class D3D9EffectProjectRootPaths
{
    internal static D3D9EmmRuntimeEffectKind EffectKind(string value, bool named)
    {
        // Exact case is the pinned EMM runtime's policy. hide/main_default are owner-target symbols,
        // whereas an identically spelled base-section value remains a required filename.
        if (value == "none") return D3D9EmmRuntimeEffectKind.None;
        if (named && value == "hide") return D3D9EmmRuntimeEffectKind.Hide;
        if (named && value == "main_default") return D3D9EmmRuntimeEffectKind.MainDefault;
        return D3D9EmmRuntimeEffectKind.File;
    }

    internal static string OriginalPath(string path, string workingDirectory)
    {
        TextValidity(path);
        if (string.IsNullOrWhiteSpace(path)) throw Invalid("EMM file identities must not be empty.");
        // The EMM loader stores effect rows verbatim and resolves them from its working directory.
        // EMM-directory joining belongs to object matching, not to FX rows or implicit fallback.
        // A different drive's relative directory is ambient state and is intentionally unavailable.
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
            (path.Length == 2 || path[2] is not ('\\' or '/')))
        {
            if (!string.Equals(path[..2], workingDirectory[..2], StringComparison.OrdinalIgnoreCase))
                throw Invalid($"Drive-relative EMM path '{path}' has no frozen working directory for that drive.");
            path = Path.Combine(workingDirectory, path[2..]);
        }
        return D3D9EffectResourcePaths.Canonicalize(Path.GetFullPath(path, workingDirectory), nameof(path));
    }

    internal static int TextValidity(string value)
    {
        if (value is null || value.Contains('\0')) throw Invalid("EMM runtime strings cannot be null or contain NUL.");
        int bytes;
        try { bytes = D3D9EffectResourcePaths.Utf8.GetByteCount(value); }
        catch (EncoderFallbackException error) { throw new ArgumentException("EMM runtime strings require valid Unicode for strict UTF-8.", "plan", error); }
        if (bytes > 32767) throw Invalid("An EMM runtime string exceeds 32767 UTF-8 bytes.");
        return bytes;
    }

    private static ArgumentException Invalid(string message) => new(message, "plan");
}
