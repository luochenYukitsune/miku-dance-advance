using Mmd.Modern.Core;

namespace Mmd.Modern.Application;

public sealed partial class SceneCompositionEditor
{
    /// <summary>Replace immutable surface bindings while preserving object, skeleton and geometry identities.</summary>
    public void ReplaceMaterialSource(Guid objectId, OriginalFrameScene source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int index = Index(objectId);
        var previous = objects[index];
        var before = previous.Source.Surface.Snapshot;
        var after = source.Surface.Snapshot;
        if (!ReferenceEquals(previous.Source.Outline, source.Outline)
            || !before.Vertices.SequenceEqual(after.Vertices) || !before.Indices.SequenceEqual(after.Indices)
            || before.Materials.Count != after.Materials.Count || before.HasDynamicVertices != after.HasDynamicVertices
            || !ReferenceEquals(before.AdditionalUvs, after.AdditionalUvs) || !ReferenceEquals(before.RawRadialSeeds, after.RawRadialSeeds)
            || previous.Source.OutlineOptions != source.OutlineOptions || previous.Source.Options != source.Options
            || !ReferenceEquals(previous.Source.Lighting, source.Lighting) || !ReferenceEquals(previous.Source.EditorOverlay, source.EditorOverlay)
            || !previous.Source.Materials.SequenceEqual(source.Materials) || previous.Source.Surface.Profile != source.Surface.Profile
            || before.World != after.World || before.Camera != after.Camera || before.LightDirection != after.LightDirection)
            throw new ArgumentException("A material source edit must preserve geometry, outline and view state.", nameof(source));
        for (int material = 0; material < before.Materials.Count; material++)
            if (before.Materials[material].IndexCount != after.Materials[material].IndexCount
                || before.Materials[material].StartIndex != after.Materials[material].StartIndex)
                throw new ArgumentException("A material source edit must preserve material geometry ranges.", nameof(source));
        Save(); objects[index] = previous with { Source = source }; cached = null;
    }
}
