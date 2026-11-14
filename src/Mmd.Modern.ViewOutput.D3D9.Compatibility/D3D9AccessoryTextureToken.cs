namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>The existing traditional material-token grammar, shared by persistence and frame encoding.</summary>
internal readonly record struct D3D9AccessoryTextureToken(uint Type, string? Primary, string? Sphere)
{
    internal static D3D9AccessoryTextureToken Parse(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return new(0, null, null);
        var pieces = token.Split('*');
        if (pieces.Any(value => string.IsNullOrWhiteSpace(value)) || pieces.Length > 2)
            throw new InvalidDataException("Accessory texture token has unsupported combined syntax.");
        // Original single-token strstr is case-sensitive and is not a basename test.
        // Combined plain*sphere tokens keep their ordinary file semantics.
        if (pieces.Length == 1 && token.Contains("screen.bmp", StringComparison.Ordinal))
            return new(3, null, null);
        if (pieces.Any(value => Path.GetExtension(value).Equals(".avi", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("Direct accessory AVI tokens are not an original independent video channel.");
        static uint SphereType(string value) => Path.GetExtension(value).ToLowerInvariant() switch { ".sph" => 1U, ".spa" => 2U, _ => 0U };
        if (pieces.Length == 2)
        {
            uint sphere = SphereType(pieces[1]);
            if (SphereType(pieces[0]) != 0 || sphere == 0)
                throw new InvalidDataException("Combined accessory tokens require a plain texture followed by sph or spa.");
            return new(sphere + 3, pieces[0], pieces[1]);
        }
        return new(SphereType(token), token, null);
    }

    internal IEnumerable<string> References
    {
        get
        {
            if (Primary is not null) yield return Primary;
            if (Sphere is not null) yield return Sphere;
        }
    }
}
