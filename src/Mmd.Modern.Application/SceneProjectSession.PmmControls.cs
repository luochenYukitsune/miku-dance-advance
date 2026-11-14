using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed partial class SceneProjectEdit
{
    /// <summary>Imports control provenance and its authored character tracks in one detached project transaction.
    /// The caller explicitly maps modern identities to stored PMM IDs; list order is never guessed.</summary>
    public void ImportPmmControls(PmmDocument document, IReadOnlyDictionary<Guid, byte> modelIndices)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(modelIndices);
        if (modelIndices.Count != Motions.Count || Motions.Any(m => !modelIndices.ContainsKey(m.ObjectId)))
            throw new InvalidDataException("PMM control import must map every motion-bound actor exactly once.");
        var state = ScenePmmControlState.Create(document, Motions.Select(m => new ScenePmmActorSource(m.ObjectId, modelIndices[m.ObjectId], m.Model)));
        PublishPmmEdit(state);
    }

    /// <summary>Reuses existing conflict checking, synchronizes full edited pose/curve records and commits through the owner's Undo boundary.</summary>
    public void EditPmmControls(Func<PmmTrackEditor, PmmTrackEditPlan> prepare, bool confirmOverwrite = false)
    {
        var state = PmmControls ?? throw new InvalidOperationException("Import PMM control provenance before editing its tracks.");
        PublishPmmEdit(state.SynchronizeMotions(Motions).Edit(prepare, confirmOverwrite));
    }

    private void PublishPmmEdit(ScenePmmControlState state)
    {
        var document = state.ExportDocument();
        var next = Motions.Select(binding =>
        {
            var model = document.Models.Single(m => m.Index == state.ModelIndices[binding.ObjectId]);
            var motion = PmmMotionAdapter.Convert(model, binding.Model);
            var asset = SceneMotionAsset.FromMotion(motion, binding.Asset?.Name ?? "pmm-motion.vmd", new() { AnsiCodePage = document.AnsiCodePage });
            return binding with { Motion = asset.Motion, Asset = asset };
        }).ToArray();
        Motions.Clear(); Motions.AddRange(next); PmmControls = state;
    }
}

public sealed partial class SceneProjectSession
{
    /// <summary>Exports current project dimensions/FPS and preserves retained PMM control provenance.
    /// Output fields on low-level exporter options are superseded by this owner's typed settings.</summary>
    public PmmDocument ExportPmm(PmmSessionExportOptions? options = null)
    { RequireIdle(); RequireAccessoryPersistenceSupport(); RequirePmmCompatibility(options); return PmaExportRaw(options); }
    public void SavePmm(string path, PmmSessionExportOptions? options = null, CancellationToken cancellationToken = default)
    { RequireIdle(); cancellationToken.ThrowIfCancellationRequested(); RequireAccessoryPersistenceSupport(); RequirePmmCompatibility(options); PmmSessionExporter.Save(path, animation, ProjectPmmOutput(options), cancellationToken); }
}
