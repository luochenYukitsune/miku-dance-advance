using System.Collections.ObjectModel;
using System.Numerics;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

/// <summary>Explicit renderer-neutral inputs needed from one instance at the geometry sample's clock.
/// Only selected solved bones and source-local texture indices are captured; this never requests animation evaluation.</summary>
public sealed class SceneObjectRenderRequest
{
    public Guid ObjectId { get; }
    public IReadOnlyList<int> BoneIndices { get; }
    public IReadOnlyList<int> LocalTextureIndices { get; }
    /// <summary>Best-effort local image capture for dormant configurations; the active renderer validates missing mappings.</summary>
    public IReadOnlyList<int> OptionalLocalTextureIndices { get; }
    /// <summary>Retains the exact evaluated source-local mesh and its current instance World for corner-frame generation.</summary>
    public bool IncludeSourceGeometry { get; }
    /// <summary>Retains the complete same-frame source-local material table even when this instance is hidden.
    /// This is independent of composed geometry and does not populate hidden bone or texture selections.</summary>
    public bool IncludeMaterialInputs { get; }

    public SceneObjectRenderRequest(Guid objectId, IEnumerable<int>? boneIndices = null, IEnumerable<int>? localTextureIndices = null,
        IEnumerable<int>? optionalLocalTextureIndices = null, bool includeSourceGeometry = false, bool includeMaterialInputs = false)
    {
        if (objectId == Guid.Empty) throw new ArgumentException("A render input request requires a scene object identity.", nameof(objectId));
        ObjectId = objectId;
        BoneIndices = FreezeIndices(boneIndices, nameof(boneIndices));
        LocalTextureIndices = FreezeIndices(localTextureIndices, nameof(localTextureIndices));
        OptionalLocalTextureIndices = FreezeIndices(optionalLocalTextureIndices, nameof(optionalLocalTextureIndices));
        IncludeSourceGeometry = includeSourceGeometry;
        IncludeMaterialInputs = includeMaterialInputs;
    }

    private static IReadOnlyList<int> FreezeIndices(IEnumerable<int>? input, string parameter)
    {
        var indices = input?.Distinct().ToArray() ?? [];
        if (indices.Any(index => index < 0)) throw new ArgumentOutOfRangeException(parameter, "Requested local indices must be nonnegative.");
        return Array.AsReadOnly(indices);
    }

    internal static IReadOnlyList<SceneObjectRenderRequest> Freeze(IEnumerable<SceneObjectRenderRequest>? input)
    {
        var requests = input?.ToArray() ?? [];
        if (requests.Any(request => request is null) || requests.Select(request => request.ObjectId).Distinct().Count() != requests.Length)
            throw new ArgumentException("Render input requests require nonnull entries and unique object identities.", nameof(input));
        return Array.AsReadOnly(requests);
    }
}

/// <summary>Detached inputs from the same already evaluated frame as its geometry. Bone transforms are in
/// composed world space (including placement exactly once); texture values index that frame's combined table.
/// Hidden instances have no consumed entries and keep their requests in the renderer for later reappearance.</summary>
public sealed class SceneObjectRenderInputs
{
    public static IReadOnlyDictionary<Guid, SceneObjectRenderInputs> Empty { get; }
        = new ReadOnlyDictionary<Guid, SceneObjectRenderInputs>(new Dictionary<Guid, SceneObjectRenderInputs>());
    public Guid ObjectId { get; }
    public bool Visible { get; }
    /// <summary>Presence on this exact instance source, never inferred from a composed stream with padding.</summary>
    public bool HasRawRadialSeeds { get; }
    public IReadOnlyDictionary<int, Matrix4x4> BoneWorldMatrices { get; }
    public IReadOnlyDictionary<int, int> TextureIndices { get; }
    public SceneSnapshot? SourceGeometry { get; }
    /// <summary>Matching already skinned local outline from the same accepted LocalFrame as SourceGeometry.
    /// Material-only morph preparation is deliberately not used for this geometry.</summary>
    public OriginalOutlineData? SourceOutline { get; }
    public OriginalOutlineOptions? SourceOutlineOptions { get; }

    internal SceneObjectRenderInputs(Guid objectId, bool visible, Dictionary<int, Matrix4x4> bones, Dictionary<int, int> textures,
        bool hasRawRadialSeeds = false, SceneSnapshot? sourceGeometry = null,
        OriginalOutlineData? sourceOutline = null, OriginalOutlineOptions? sourceOutlineOptions = null)
    {
        ObjectId = objectId; Visible = visible; HasRawRadialSeeds = hasRawRadialSeeds;
        BoneWorldMatrices = new ReadOnlyDictionary<int, Matrix4x4>(bones);
        TextureIndices = new ReadOnlyDictionary<int, int>(textures);
        SourceGeometry = sourceGeometry;
        SourceOutline = sourceOutline; SourceOutlineOptions = sourceOutlineOptions;
    }
}
