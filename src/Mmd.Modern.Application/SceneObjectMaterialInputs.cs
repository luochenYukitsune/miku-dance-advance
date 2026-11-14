using System.Collections.ObjectModel;
using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

/// <summary>Detached source-local material values at one accepted animation boundary. Material order and
/// texture indices belong to this object's source, independently of its range in the composed scene.
/// Source StartIndex/IndexCount describe authored topology; they do not authorize hidden geometry draws.</summary>
public sealed class SceneObjectMaterialInputs
{
    public static IReadOnlyDictionary<Guid, SceneObjectMaterialInputs> Empty { get; }
        = new ReadOnlyDictionary<Guid, SceneObjectMaterialInputs>(new Dictionary<Guid, SceneObjectMaterialInputs>());
    public Guid ObjectId { get; }
    public double SourceFrame { get; }
    public double LocalFrame { get; }
    public bool Visible { get; }
    public int MaterialCount => Materials.Count;
    public IReadOnlyList<SceneMaterial> Materials { get; }
    public IReadOnlyList<OriginalStyleMaterialBinding> Bindings { get; }
    /// <summary>Absent only when the source has no outline. Otherwise one evaluated entry per material.</summary>
    public IReadOnlyList<OriginalOutlineMaterial>? OutlineMaterials { get; }
    public IReadOnlyList<OriginalFrameMaterialFlags> MaterialFlags { get; }
    /// <summary>Immutable source-local texture references. Capturing this table never reads files or copies image bytes.</summary>
    public IReadOnlyList<SceneTexture> Textures { get; }
    public OriginalStyleProfile Profile { get; }

    internal SceneObjectMaterialInputs(Guid objectId, double sourceFrame, double localFrame, bool visible,
        OriginalStyleScene surface, OriginalOutlineData? outline, IReadOnlyList<OriginalFrameMaterialFlags> flags)
    {
        if (objectId == Guid.Empty || !double.IsFinite(sourceFrame) || !double.IsFinite(localFrame)
            || localFrame < 0 || sourceFrame < localFrame)
            throw new ArgumentException("Material inputs require a valid object and accepted source/local clock.");
        int count = surface.Snapshot.Materials.Count;
        if (surface.Materials.Count != count || flags.Count != count || outline is not null && outline.Materials.Count != count)
            throw new ArgumentException("Material inputs require exactly matching source-local tables.");
        ObjectId = objectId; SourceFrame = sourceFrame; LocalFrame = localFrame; Visible = visible;
        // Only small material/reference tables are copied. The detached values never retain a
        // controller, scene geometry, solver or callback; texture pixel storage is already immutable.
        Materials = Array.AsReadOnly(surface.Snapshot.Materials.ToArray());
        Bindings = Array.AsReadOnly(surface.Materials.ToArray());
        OutlineMaterials = outline is null ? null : Array.AsReadOnly(outline.Materials.ToArray());
        MaterialFlags = Array.AsReadOnly(flags.ToArray());
        Textures = Array.AsReadOnly(surface.Snapshot.Textures.ToArray());
        Profile = surface.Profile;
    }
}
