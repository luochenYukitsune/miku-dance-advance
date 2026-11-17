using System.Security.Cryptography;
using Mmd.Modern.Core;
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
        => Rebind(objectId, model, materialIndices, encoded, authoredToken, overridden: true, token);

    /// <summary>十槽界面用的只读状态：Toon01–10 各槽在该模型里的材质、当前图片和是否已替换。</summary>
    internal IReadOnlyList<CommonSlotState> DescribeCommonSlots(Guid objectId, PmxModel model)
    {
        var bindings = RequireBindings(objectId, model);
        var result = new CommonSlotState[10];
        for (int slot = 1; slot <= 10; slot++)
        {
            var matching = bindings.Where(value => value.Slot == slot).ToArray();
            result[slot - 1] = new(slot, matching.Select(value => value.MaterialIndex).ToArray(),
                matching.Length == 0 ? null : images[matching[0].ImageSha256].Texture, matching.Any(value => value.Override));
        }
        return result;
    }

    /// <summary>把该模型的 Common 槽换回调用方给出的原版图片，并去掉替换标记。
    /// 与 Replace 走同一套绑定逻辑，同模型同槽的材质一起更新，其他模型不变。</summary>
    internal EditorClassicToonPackage RestoreCommon(Guid objectId, PmxModel model, int slot,
        byte[] encoded, string authoredToken, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slot, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 10);
        int[] indices = RequireBindings(objectId, model).Where(value => value.Slot == slot).Select(value => value.MaterialIndex).ToArray();
        if (indices.Length == 0) throw new ArgumentException("The model does not use this common Toon slot.", nameof(slot));
        return Rebind(objectId, model, indices, encoded, authoredToken, overridden: false, token);
    }

    private EditorClassicToonPackage Rebind(Guid objectId, PmxModel model, IReadOnlyList<int> materialIndices,
        byte[] encoded, string authoredToken, bool overridden, CancellationToken token)
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
        {
            // 恢复为原版图片时，不绘制的空材质不属于必需绑定，直接去掉它的替换记录。
            if (!overridden && model.Materials[index].FaceVertexCount <= 0) continue;
            entries.Add(new(index, Slot(model.Materials[index]), sha, overridden));
        }
        nextModels[objectId] = entries.OrderBy(value => value.MaterialIndex).ToArray();
        var used = nextModels.Values.SelectMany(value => value).Select(value => value.ImageSha256).ToHashSet(StringComparer.Ordinal);
        var nextImages = images.Where(pair => used.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        nextImages[sha] = image;
        ValidateBudget(nextImages.Values);
        token.ThrowIfCancellationRequested();
        return new(nextModels, nextImages);
    }
}

internal sealed record CommonSlotState(int Slot, IReadOnlyList<int> MaterialIndices, SceneTexture? Image, bool Replaced)
{
    internal bool Used => MaterialIndices.Count > 0;
}
