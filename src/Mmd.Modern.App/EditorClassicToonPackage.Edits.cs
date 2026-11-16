using System.Security.Cryptography;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

internal sealed partial class EditorClassicToonPackage
{
    // Keep the authored identity distinct from common 1..10 and default 0.
    private static int Slot(PmxMaterial material) => material.ToonMode == PmxToonMode.Common
        ? checked(material.ToonTextureIndex + 1)
        : material.ToonTextureIndex == -1 ? 0 : checked(material.ToonTextureIndex + 11);

    internal EditorClassicToonPackage Replace(Guid objectId, PmxModel model,
        IReadOnlyList<int> materialIndices, byte[] encoded, string authoredToken, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (encoded.Length is <= 0 or > MaximumImageBytes) throw Invalid("Toon image exceeds the read budget.");
        var selected = materialIndices.ToHashSet();
        if (selected.Count == 0 || selected.Any(index => index < 0 || index >= model.Materials.Count))
            throw new ArgumentException("Choose at least one existing model material.", nameof(materialIndices));
        var common = selected.Where(index => model.Materials[index].ToonMode == PmxToonMode.Common)
            .Select(index => model.Materials[index].ToonTextureIndex).ToHashSet();
        for (int index = 0; index < model.Materials.Count; index++)
            if (model.Materials[index].ToonMode == PmxToonMode.Common && common.Contains(model.Materials[index].ToonTextureIndex))
                selected.Add(index);
        var before = RequireBindings(objectId, model);
        byte[] bytes = encoded.ToArray();
        var texture = EditorTextureLoader.Decode(bytes, authoredToken, token);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        var image = new Image(sha, authoredToken, bytes, texture);
        var nextModels = models.ToDictionary(pair => pair.Key, pair => pair.Value);
        var entries = before.Where(value => !selected.Contains(value.MaterialIndex)).ToList();
        foreach (int index in selected.Order())
            entries.Add(new(index, Slot(model.Materials[index]), sha, true));
        nextModels[objectId] = entries.OrderBy(value => value.MaterialIndex).ToArray();
        var used = nextModels.Values.SelectMany(value => value).Select(value => value.ImageSha256).ToHashSet(StringComparer.Ordinal);
        var nextImages = images.Where(pair => used.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        nextImages[sha] = image;
        ValidateBudget(nextImages.Values);
        token.ThrowIfCancellationRequested();
        return new(nextModels, nextImages);
    }
}
