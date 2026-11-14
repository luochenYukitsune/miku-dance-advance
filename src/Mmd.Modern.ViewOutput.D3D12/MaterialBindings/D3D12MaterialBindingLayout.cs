using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput.D3D12;

/// <summary>
/// Immutable identity of an explicitly supplied source-local, unplaced rest frame. This is not a model-file hash,
/// a skeleton identity or a posed/composed-frame identity. The host remains responsible for supplying authored rest inputs.
/// </summary>
public sealed class D3D12MaterialBindingLayout
{
    private readonly SceneSnapshot source;
    public const int CurrentVersion = 1;
    public int Version => CurrentVersion;
    public string Fingerprint { get; }
    public int MaterialCount => source.Materials.Count;
    public int TextureCount => source.Textures.Count;

    private D3D12MaterialBindingLayout(SceneSnapshot source, string fingerprint)
    { this.source = source; Fingerprint = fingerprint; }

    /// <summary>
    /// Captures version 1: little-endian Int32/UInt32 and finite IEEE binary32, signed zero canonicalized to positive zero.
    /// Names, paths, texture pixels, world, camera, lighting, global profile/options and modern per-frame overrides are excluded.
    /// Texture ordinals in authored material bindings are included; selected image pixels have separate explicit guards.
    /// </summary>
    public static D3D12MaterialBindingLayout Capture(OriginalFrameScene sourceRest)
    {
        ArgumentNullException.ThrowIfNull(sourceRest);
        var scene = sourceRest.Surface.Snapshot;
        if (scene.HasDynamicVertices)
            throw new ArgumentException("Material binding identity requires source rest geometry, not a deformed frame.", nameof(sourceRest));
        using var hash = new CanonicalHash();
        hash.Bytes("MDA-material-binding-layout\0"u8);
        hash.Int(CurrentVersion);
        hash.Int(scene.Vertices.Length);
        foreach (var vertex in scene.Vertices)
        { hash.Vector(vertex.Position); hash.Vector(vertex.Normal); hash.Vector(vertex.Uv); }
        hash.Int(scene.Indices.Length);
        foreach (uint index in scene.Indices) hash.UInt(index);
        hash.Int(scene.Materials.Count);
        for (int i = 0; i < scene.Materials.Count; i++)
        {
            var material = scene.Materials[i];
            hash.Int(material.StartIndex); hash.Int(material.IndexCount);
            hash.Vector(material.Diffuse); hash.Vector(material.Specular); hash.Vector(material.Ambient);
            hash.Float(material.Shininess); hash.Int(material.DiffuseTexture); hash.Int(material.ToonTexture);
            hash.Bool(material.ToonUvTransform.HasValue);
            if (material.ToonUvTransform is { } transform) hash.Vector(transform);
            hash.Float(material.AlphaCutoff); hash.Vector(material.Emission);
            var binding = sourceRest.Surface.Materials[i];
            hash.Vector(binding.ToonShadowColor); hash.Int(binding.SphereTexture); hash.Int((int)binding.SphereMode);
            hash.Bool(binding.DoubleSided); hash.Bool(binding.SubTextureAdditive);
            hash.Factor(binding.TextureFactors.Diffuse); hash.Factor(binding.TextureFactors.Sphere); hash.Factor(binding.TextureFactors.Toon);
            var flags = sourceRest.Materials[i];
            hash.Bool(flags.CastSelfShadow); hash.Bool(flags.ReceiveSelfShadow); hash.Bool(flags.CastGroundShadow);
            hash.Bool(flags.SurfaceEnabled); hash.Bool(flags.ShadowDoubleSided); hash.Bool(flags.CullTransparentSurface);
        }
        hash.Bool(scene.AdditionalUvs is not null);
        if (scene.AdditionalUvs is { } additional)
        {
            hash.Int(additional.ChannelCount); hash.Int(additional.Vertices.Length);
            foreach (var vertex in additional.Vertices)
            { hash.Vector(vertex.Uv1); hash.Vector(vertex.Uv2); hash.Vector(vertex.Uv3); hash.Vector(vertex.Uv4); }
        }
        hash.Bool(scene.RawRadialSeeds is not null);
        if (scene.RawRadialSeeds is { } seeds)
        {
            hash.Int(seeds.Vertices.Length);
            foreach (var seed in seeds.Vertices) hash.Vector(seed);
        }
        hash.Bool(sourceRest.Outline is not null);
        if (sourceRest.Outline is { } outline)
        {
            hash.Int(outline.Vertices.Count);
            foreach (var vertex in outline.Vertices)
            {
                hash.Vector(vertex.Position); hash.Vector(vertex.Normal); hash.Int((int)vertex.Mode);
                hash.Float(vertex.VertexScale); hash.Int(vertex.SelectedMaterialIndex);
            }
            hash.Int(outline.Materials.Count);
            foreach (var material in outline.Materials)
            { hash.Float(material.Width); hash.Vector(material.Color); hash.Bool(material.DrawEnabled); }
        }
        return new(scene, hash.Complete());
    }

    public void ValidateFingerprint(string fingerprint, int version = CurrentVersion)
    {
        if (version != CurrentVersion || !IsCanonicalSha256(fingerprint)
            || !string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Material binding profile does not match the source rest layout and version.", nameof(fingerprint));
    }

    /// <summary>The argument is the source SceneSnapshot texture ordinal, never the PMX file texture ordinal.</summary>
    public D3D12MaterialBindingProfileTexture CaptureTexture(int sourceSceneTextureOrdinal)
    {
        var texture = GetTexture(sourceSceneTextureOrdinal);
        return new(sourceSceneTextureOrdinal, texture.Width, texture.Height, texture.Kind,
            Convert.ToHexStringLower(SHA256.HashData(texture.Rgba)));
    }

    /// <summary>Validates only the explicitly selected slot. Equal content elsewhere never repairs a mismatching slot.</summary>
    public void ValidateTexture(D3D12MaterialBindingProfileTexture selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var texture = GetTexture(selector.SourceSceneTextureOrdinal);
        if (selector.Width != texture.Width || selector.Height != texture.Height || selector.Kind != texture.Kind
            || !IsCanonicalSha256(selector.RgbaSha256)
            || !string.Equals(selector.RgbaSha256, Convert.ToHexStringLower(SHA256.HashData(texture.Rgba)), StringComparison.Ordinal))
            throw new ArgumentException("Selected source scene texture no longer matches its size, kind or RGBA8 content guard.", nameof(selector));
    }

    private SceneTexture GetTexture(int ordinal)
    {
        if ((uint)ordinal >= (uint)source.Textures.Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal), "Explicit source scene texture ordinal is out of range; select a valid source slot.");
        return source.Textures[ordinal];
    }

    private static bool IsCanonicalSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (char ch in value)
            if (!(ch is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private sealed class CanonicalHash : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public void Bytes(ReadOnlySpan<byte> value) => hash.AppendData(value);
        public void Int(int value)
        { Span<byte> data = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(data, value); Bytes(data); }
        public void UInt(uint value)
        { Span<byte> data = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(data, value); Bytes(data); }
        public void Bool(bool value) => Int(value ? 1 : 0);
        public void Float(float value)
        {
            if (!float.IsFinite(value)) throw new ArgumentException("Rest layout numeric inputs must be finite.");
            Int(value == 0 ? 0 : BitConverter.SingleToInt32Bits(value));
        }
        public void Vector(Vector2 value) { Float(value.X); Float(value.Y); }
        public void Vector(Vector3 value) { Float(value.X); Float(value.Y); Float(value.Z); }
        public void Vector(Vector4 value) { Float(value.X); Float(value.Y); Float(value.Z); Float(value.W); }
        public void Factor(OriginalTextureFactor value) { Vector(value.Multiply); Vector(value.Add); }
        public string Complete() => Convert.ToHexStringLower(hash.GetHashAndReset());
        public void Dispose() => hash.Dispose();
    }
}
