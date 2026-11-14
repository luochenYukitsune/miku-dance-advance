using System.Text;
using System.Collections.ObjectModel;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>Frozen metadata for one logical source object. Native IDs must never be recycled for another object in a session.</summary>
public sealed class D3D9SurfaceObjectBinding
{
    public Guid ObjectId { get; }
    public ulong NativeId { get; }
    public string SourcePath { get; }
    public string Name { get; }
    public IReadOnlyList<string> BoneNames { get; }
    public PmmDrawableKind Kind { get; private init; } = PmmDrawableKind.Model;
    /// <summary>Already decoded traditional texture inputs keyed by their authored image filename;
    /// a combined .x token supplies its plain and sphere filenames as separate entries.
    /// The encoder never opens files or silently substitutes a missing image.</summary>
    public IReadOnlyDictionary<string, SceneTexture> AccessoryTextures { get; private init; }
        = new ReadOnlyDictionary<string, SceneTexture>(new Dictionary<string, SceneTexture>());

    public D3D9SurfaceObjectBinding(Guid objectId, ulong nativeId, string sourcePath, string name,
        IEnumerable<string>? boneNames = null)
    {
        if (objectId == Guid.Empty) throw new ArgumentException("A scene object identity is required.", nameof(objectId));
        if (nativeId == 0) throw new ArgumentOutOfRangeException(nameof(nativeId));
        ValidateText(sourcePath, nameof(sourcePath)); ValidateText(name, nameof(name));
        var names = boneNames?.ToArray() ?? [];
        foreach (var value in names) ValidateText(value, nameof(boneNames), allowEmpty: true);
        ObjectId = objectId; NativeId = nativeId; SourcePath = sourcePath; Name = name;
        BoneNames = Array.AsReadOnly(names);
    }

    public static D3D9SurfaceObjectBinding CreateAccessory(Guid objectId, ulong nativeId, string sourcePath, string name,
        IReadOnlyDictionary<string, SceneTexture>? textures = null)
    {
        var frozen = new Dictionary<string, SceneTexture>(StringComparer.OrdinalIgnoreCase);
        if (textures is not null)
            foreach (var pair in textures)
            {
                ValidateText(pair.Key, nameof(textures)); ArgumentNullException.ThrowIfNull(pair.Value);
                if (!frozen.TryAdd(pair.Key, pair.Value)) throw new ArgumentException("Accessory texture tokens must be unique.", nameof(textures));
            }
        return new(objectId, nativeId, sourcePath, name)
        { Kind = PmmDrawableKind.Accessory, AccessoryTextures = new ReadOnlyDictionary<string, SceneTexture>(frozen) };
    }

    internal static void ValidateText(string value, string parameter, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Contains('\0'))
            throw new ArgumentException("Packet metadata requires text without embedded NUL.", parameter);
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(value) > 1_048_576)
                throw new ArgumentException("Packet metadata exceeds the native UTF-8 limit.", parameter);
        }
        catch (EncoderFallbackException error) { throw new ArgumentException("Packet metadata must be valid Unicode.", parameter, error); }
    }
}
