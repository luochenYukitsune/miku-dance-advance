using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Core;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>User-selected surface treatment; never inferred from diffuse brightness, names or colors.</summary>
public enum D3D12GeneratedPmxSurfaceKind { Cloth, Coated, Metal, Hair }

/// <summary>
/// Editable MDA auxiliary-map settings, not a reconstruction of original game material data.
/// Luminance height is an explicit artistic approximation and defaults to zero (a flat normal).
/// </summary>
public sealed record D3D12GeneratedPmxMapOptions(
    D3D12GeneratedPmxSurfaceKind Kind = D3D12GeneratedPmxSurfaceKind.Cloth,
    int MapSize = 256,
    float HeightFromLuminanceStrength = 0,
    float? HighlightMask = null,
    float? MatcapWidth = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        if (MapSize is < 16 or > 1024 || (MapSize & (MapSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(MapSize), "Map size must be a power of two from 16 through 1024.");
        ValidateRange(HeightFromLuminanceStrength, 0, 2, nameof(HeightFromLuminanceStrength));
        if (HighlightMask is { } mask) ValidateRange(mask, 0, 1, nameof(HighlightMask));
        if (MatcapWidth is { } width) ValidateRange(width, .03f, 1, nameof(MatcapWidth));
    }

    private static void ValidateRange(float value, float minimum, float maximum, string name)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name);
    }
}

/// <summary>Immutable generated images plus reproducible source/settings provenance. Source pixels remain untouched.</summary>
public sealed class D3D12GeneratedPmxMaterialMapResult
{
    public D3D12GeneratedPmxMapOptions Options { get; }
    /// <summary>Uppercase SHA-256 of the source RGBA bytes, before resampling or any material treatment.</summary>
    public string SourceRgbaSha256 { get; }
    public string GeneratorRevision => D3D12GeneratedPmxMaterialMaps.GeneratorRevision;
    public bool IsGenerated => true;
    public SceneTexture Normal { get; }
    public SceneTexture P { get; }
    public SceneTexture Matcap { get; }

    internal D3D12GeneratedPmxMaterialMapResult(D3D12GeneratedPmxMapOptions options, string sourceRgbaSha256,
        SceneTexture normal, SceneTexture p, SceneTexture matcap)
    {
        Options = options; SourceRgbaSha256 = sourceRgbaSha256;
        Normal = normal; P = p; Matcap = matcap;
    }
}

/// <summary>
/// Bounded, deterministic CPU generation of editable MDA material aids. These original numerical images
/// contain no plugin assets and do not claim recovered geometry or original game texture semantics.
/// </summary>
public static class D3D12GeneratedPmxMaterialMaps
{
    public const string GeneratorRevision = "mda-pmx-aux-v1";

    public static D3D12GeneratedPmxMaterialMapResult Generate(SceneTexture source,
        D3D12GeneratedPmxMapOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (source.Kind != SceneTextureKind.Image)
            throw new ArgumentException("Auxiliary maps require a source image texture.", nameof(source));
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        // Hash row-by-row so even maximum-size immutable source images remain cancellable.
        string sourceHash;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            for (int y = 0; y < source.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(source.Rgba.Slice(y * source.Width * 4, source.Width * 4));
            }
            sourceHash = Convert.ToHexString(hash.GetHashAndReset());
        }

        cancellationToken.ThrowIfCancellationRequested();
        int size = options.MapSize, byteCount = checked(size * size * 4);
        byte[] normal = new byte[byteCount];
        cancellationToken.ThrowIfCancellationRequested();
        byte[] p = new byte[byteCount];
        cancellationToken.ThrowIfCancellationRequested();
        byte[] matcap = new byte[byteCount];
        float mask = options.HighlightMask ?? options.Kind switch
        {
            D3D12GeneratedPmxSurfaceKind.Cloth => .08f,
            D3D12GeneratedPmxSurfaceKind.Coated => .3f,
            D3D12GeneratedPmxSurfaceKind.Metal => .8f,
            D3D12GeneratedPmxSurfaceKind.Hair => .2f,
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        float width = options.MatcapWidth ?? options.Kind switch
        {
            D3D12GeneratedPmxSurfaceKind.Cloth => .6f,
            D3D12GeneratedPmxSurfaceKind.Coated => .28f,
            D3D12GeneratedPmxSurfaceKind.Metal => .12f,
            D3D12GeneratedPmxSurfaceKind.Hair => .35f,
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        byte encodedMask = EncodeUnorm(mask);
        for (int y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sourceY = Math.Min((int)((y + .5f) * source.Height / size), source.Height - 1);
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                Vector3 n = Vector3.UnitZ;
                if (options.HeightFromLuminanceStrength > 0)
                {
                    int sourceX = Math.Min((int)((x + .5f) * source.Width / size), source.Width - 1);
                    n = HeightNormal(source, sourceX, sourceY, options.HeightFromLuminanceStrength);
                }
                normal[i] = EncodeUnorm(n.X * .5f + .5f);
                normal[i + 1] = EncodeUnorm(n.Y * .5f + .5f);
                normal[i + 2] = 255; // The consumer reconstructs Z from RG; B is unused.
                normal[i + 3] = 255;

                // Existing P consumer: R = highlight mask, B = detail identity, A = additive highlight extra mask off.
                p[i] = encodedMask; p[i + 1] = 0; p[i + 2] = 255; p[i + 3] = 0;

                float u = (x + .5f) / size - .32f, v = (y + .5f) / size - .32f;
                byte lobe = EncodeUnorm(MathF.Exp(-.5f * (u * u + v * v) / (width * width)));
                matcap[i] = matcap[i + 1] = matcap[i + 2] = lobe;
                matcap[i + 3] = 255;
            }
        }

        string name = FormattableString.Invariant(
            $"GENERATED/{GeneratorRevision}/{sourceHash}/{options.Kind}-{size}-h{options.HeightFromLuminanceStrength:R}-p{mask:R}-w{width:R}");
        cancellationToken.ThrowIfCancellationRequested();
        var normalTexture = new SceneTexture(name + "/Normal", size, size, normal);
        cancellationToken.ThrowIfCancellationRequested();
        var pTexture = new SceneTexture(name + "/P", size, size, p);
        cancellationToken.ThrowIfCancellationRequested();
        var matcapTexture = new SceneTexture(name + "/Matcap", size, size, matcap);
        cancellationToken.ThrowIfCancellationRequested();
        return new(options, sourceHash, normalTexture, pTexture, matcapTexture);
    }

    private static Vector3 HeightNormal(SceneTexture source, int x, int y, float strength)
    {
        int left = (x + source.Width - 1) % source.Width, right = (x + 1) % source.Width;
        int up = (y + source.Height - 1) % source.Height, down = (y + 1) % source.Height;
        ReadOnlySpan<byte> pixels = source.Rgba;
        int centerIndex = (y * source.Width + x) * 4;
        int leftIndex = (y * source.Width + left) * 4, rightIndex = (y * source.Width + right) * 4;
        int upIndex = (up * source.Width + x) * 4, downIndex = (down * source.Width + x) * 4;
        // Reject the whole stencil at cutout edges; hidden RGB must never become relief.
        if (pixels[centerIndex + 3] <= 1 || pixels[leftIndex + 3] <= 1 || pixels[rightIndex + 3] <= 1
            || pixels[upIndex + 3] <= 1 || pixels[downIndex + 3] <= 1) return Vector3.UnitZ;
        float dx = (Luminance(pixels, rightIndex) - Luminance(pixels, leftIndex)) * .5f;
        float dy = (Luminance(pixels, downIndex) - Luminance(pixels, upIndex)) * .5f;
        return Vector3.Normalize(new Vector3(-dx * strength, -dy * strength, 1));
    }

    private static float Luminance(ReadOnlySpan<byte> pixels, int offset)
        => (.2126f * pixels[offset] + .7152f * pixels[offset + 1] + .0722f * pixels[offset + 2]) / 255;

    private static byte EncodeUnorm(float value)
        => (byte)MathF.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
}
