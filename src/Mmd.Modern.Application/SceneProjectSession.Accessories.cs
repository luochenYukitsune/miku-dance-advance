using System.Collections.ObjectModel;
using System.Numerics;

namespace Mmd.Modern.Application;

/// <summary>Independent immutable accessory binding. The stored PMM identity is Asset.Index, never its list position.</summary>
public sealed record SceneProjectAccessory(Guid ObjectId, PreparedPmmAccessory Asset);

public sealed record SceneProjectAccessoryFrame(Guid ObjectId, PreparedPmmAccessory Asset,
    PmmAccessoryFrame Sample, ScenePmmAccessoryParent? Parent)
{
    public PmmAccessorySampleInputs Inputs => Sample.Inputs
        ?? throw new InvalidOperationException("A project accessory frame requires its retained sample inputs.");
}

/// <summary>Same-frame mixed draw metadata. SourceDrawOrderIndex and StoredIndex retain original PMM values.</summary>
public sealed record SceneProjectDrawItem(Guid ObjectId, PmmDrawableKind Kind, int StoredIndex,
    byte SourceDrawOrderIndex, int DrawOrderIndex, bool Visible, bool AddBlendEnabled);

public sealed partial class SceneProjectSession
{
    public IReadOnlyList<SceneProjectAccessory> Accessories
    { get { RequireAlive(); return Array.AsReadOnly(content.Accessories); } }

    /// <summary>Legacy metadata-only owners remain inspectable; renderers must reject any nonempty deficit.</summary>
    public IReadOnlyList<byte> UnpreparedAccessoryIndices
    {
        get
        {
            RequireAlive();
            var prepared = content.Accessories.Select(a => a.Asset.Index).ToHashSet();
            return Array.AsReadOnly(content.PmmControls?.AccessoryIndices.Where(id => !prepared.Contains(id)).ToArray() ?? []);
        }
    }

    internal static SceneProjectAccessory[] FreezeAccessories(IEnumerable<SceneProjectAccessory> source,
        HashSet<Guid> modelIds, ScenePmmControlState? controls)
    {
        var items = source.ToArray();
        if (items.Any(item => item is null || item.ObjectId == Guid.Empty || modelIds.Contains(item.ObjectId)
                || item.Asset is null || item.Asset.Source is null || item.Asset.Document is null || item.Asset.Diagnostics is null)
            || items.Select(item => item.ObjectId).Distinct().Count() != items.Length
            || items.Select(item => item.Asset.Index).Distinct().Count() != items.Length)
            throw new InvalidDataException("Project accessories require unique independent identities and complete immutable prepared .x inputs.");
        // A metadata-only legacy owner is valid, but a partial published prepared set is not.
        if (items.Length == 0) return [];
        if (controls is null || !items.Select(item => item.Asset.Index).ToHashSet().SetEquals(controls.AccessoryIndices))
            throw new InvalidDataException("Prepared accessories must match every declared stored PMM accessory ID exactly.");
        var document = controls.AccessoryDocument;
        _ = PmmDrawOrderPolicy.Build(document);
        foreach (var item in items)
        {
            var accessory = document.Accessories.Single(a => a.Index == item.Asset.Index);
            var frames = new[] { accessory.InitialKeyframe }.Concat(accessory.Keyframes).Select(key => key.Frame).Distinct();
            // Validate every authored hold target, including future and saved poses, before a candidate
            // can allocate solvers or replace the live owner. Identity is used only for transform validation.
            foreach (int frame in frames)
            {
                var pose = controls.AccessoryAt(item.Asset.Index, frame, saved: false);
                _ = controls.ResolveAccessoryParent(pose);
                _ = PmmAccessoryScene.Sample(document, item.Asset.Index, item.Asset.Source, frame, (_, _) => Matrix4x4.Identity);
            }
            _ = controls.ResolveAccessoryParent(controls.AccessoryAt(item.Asset.Index, 0, saved: true));
            _ = PmmAccessoryScene.Sample(document, item.Asset.Index, item.Asset.Source, 0, (_, _) => Matrix4x4.Identity, saved: true);
        }
        return items.Select(item => item with
            { Asset = item.Asset with { Diagnostics = Array.AsReadOnly(item.Asset.Diagnostics.ToArray()) } }).ToArray();
    }

    private void RequireAccessoryPersistenceSupport()
    {
        if (content.Accessories.Length != 0)
            throw new NotSupportedException("Independent .x accessory persistence is not implemented for this project format. Saving would lose accessory assets.");
    }

    private (IReadOnlyList<SceneProjectAccessoryFrame> Accessories,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, Matrix4x4>> ParentBones,
        IReadOnlyList<SceneProjectDrawItem> DrawOrder, int? Split, IReadOnlyList<string> Diagnostics)
        CaptureAccessories(IReadOnlyList<SceneObjectRenderRequest> requests, IReadOnlyList<SceneActorPlaybackState> actors)
    {
        var controls = content.PmmControls;
        var bones = new Dictionary<Guid, IReadOnlyDictionary<int, Matrix4x4>>();
        var frames = new List<SceneProjectAccessoryFrame>(content.Accessories.Length);
        if (controls is null)
            return (frames.AsReadOnly(), new ReadOnlyDictionary<Guid, IReadOnlyDictionary<int, Matrix4x4>>(bones),
                Array.AsReadOnly(Array.Empty<SceneProjectDrawItem>()), null, Array.AsReadOnly(Array.Empty<string>()));
        var parents = content.Accessories.ToDictionary(item => item.ObjectId,
            item => controls.ResolveAccessoryParent(controls.AccessoryAt(item.Asset.Index, animation.CurrentFrame, saved: false)));
        foreach (var group in parents.Values.OfType<ScenePmmAccessoryParent>().GroupBy(parent => parent.ObjectId))
        {
            // Capture the actual referenced bones plus every explicitly requested bone for this parent.
            // Hidden SceneInputs keep their historical empty-table contract; this sidecar is independent.
            var indices = group.Select(parent => parent.BoneIndex)
                .Concat(requests.SingleOrDefault(request => request.ObjectId == group.Key)?.BoneIndices ?? []).Distinct().ToArray();
            bones.Add(group.Key, new ReadOnlyDictionary<int, Matrix4x4>(animation.CaptureSelectedBoneWorldMatrices(group.Key, indices)));
        }
        var document = controls.AccessoryDocument;
        var assets = content.Accessories.ToDictionary(item => item.Asset.Index);
        // Original accessory table order is independent of sparse stored IDs, caller binding
        // order and mixed draw order. Keep it for legacy accessory index-based getters.
        foreach (var source in document.Accessories)
        {
            if (!assets.TryGetValue(source.Index, out var item)) continue;
            var parent = parents[item.ObjectId];
            var sample = PmmAccessoryScene.Sample(document, item.Asset.Index, item.Asset.Source, animation.CurrentFrame,
                (_, _) => parent is null ? Matrix4x4.Identity : bones[parent.ObjectId][parent.BoneIndex], saved: false);
            sample = sample with { Inputs = sample.Inputs!.WithDocument(item.Asset.Document) };
            frames.Add(new(item.ObjectId, item.Asset, sample, parent));
        }
        var order = PmmDrawOrderPolicy.Build(document);
        var byAccessory = frames.ToDictionary(item => (int)item.Asset.Index);
        var byModel = controls.ModelIndices.ToDictionary(pair => (int)pair.Value, pair => pair.Key);
        var byActor = actors.ToDictionary(actor => actor.ObjectId);
        var draw = new List<SceneProjectDrawItem>();
        foreach (var item in order.Items)
        {
            if (item.Kind == PmmDrawableKind.Accessory)
            {
                // Unprepared legacy entries have no invented scene Guid; the explicit deficit is
                // carried alongside this capture and rejected by traditional renderer preflight.
                if (byAccessory.TryGetValue(item.StoredIndex, out var accessory))
                    draw.Add(new(accessory.ObjectId, item.Kind, item.StoredIndex, item.SourceDrawOrderIndex,
                        item.DrawOrderIndex, accessory.Sample.Visible, accessory.Sample.AddBlendEnabled));
            }
            else
            {
                Guid id = byModel[item.StoredIndex];
                draw.Add(new(id, item.Kind, item.StoredIndex, item.SourceDrawOrderIndex, item.DrawOrderIndex, byActor[id].Visible, false));
            }
        }
        return (frames.AsReadOnly(), new ReadOnlyDictionary<Guid, IReadOnlyDictionary<int, Matrix4x4>>(bones),
            draw.AsReadOnly(), document.Settings.AccessoryIndexAfterModels, order.Diagnostics);
    }
}
