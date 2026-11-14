using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed record PreparedPmmAccessory(byte Index, string Path, string Sha256, OriginalFrameScene Source, IReadOnlyList<string> Diagnostics)
{
    /// <summary>Immutable original .x inputs, including material values and unmodified texture tokens.
    /// Legacy manually prepared assets may omit this provenance.</summary>
    public XTextDocument? Document { get; init; }

    /// <summary>Frozen original bytes, identity, encoding and exact canonical X conversion configuration.
    /// Legacy manually prepared assets remain renderable without portable-source provenance.</summary>
    public PmmAccessorySourceSnapshot? SourceSnapshot { get; init; }

    /// <summary>Requires original X provenance and its unchanged canonical document/source binding.
    /// Texture/FX dependency completeness must be checked separately by the portable asset collector.</summary>
    public PmmAccessorySourceSnapshot RequirePortableSource()
    {
        var snapshot = SourceSnapshot ?? throw new NotSupportedException(
            "Portable accessory capture requires frozen original X bytes and its supported reader/adapter configuration.");
        snapshot.Validate(this);
        return snapshot;
    }

    /// <summary>Prepares exclusively from an owned copy of the supplied original bytes. originalPath is
    /// retained as identity and is never opened. Texture callbacks receive unmodified X texture tokens.</summary>
    public static PreparedPmmAccessory FromOriginalBytes(byte index, string originalPath, ReadOnlyMemory<byte> originalBytes,
        Func<string, SceneTexture?>? loadTexture = null, XTextReadOptions? readOptions = null, float unitScale = 1,
        IEnumerable<string>? diagnostics = null, CancellationToken cancellationToken = default)
        => PmmAccessorySourceSnapshot.Prepare(index, originalPath, originalBytes, loadTexture, readOptions,
            unitScale, diagnostics, cancellationToken);
}

/// <summary>Borrowed character project plus freshly prepared immutable accessory assets. The caller owns the
/// character project and advances it first; sampling uses that same saved/animated frame and current bone matrices.
/// Preparation resolves every accessory before returning, so a missing/ambiguous/corrupt asset cannot partially replace a valid owner.</summary>
public sealed class PmmAccessoryPlayback
{
    private readonly PmmProjectPlayback project;
    public IReadOnlyList<PreparedPmmAccessory> Assets { get; }
    private PmmAccessoryPlayback(PmmProjectPlayback project, IEnumerable<PreparedPmmAccessory> assets)
    { this.project = project; Assets = Array.AsReadOnly(assets.ToArray()); }

    public static PmmAccessoryPlayback Prepare(PmmProjectPlayback project, PmmImportOptions? options = null,
        IReadOnlyDictionary<int, string>? accessoryPathOverrides = null, Func<string, SceneTexture?>? loadTexture = null,
        XTextReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project); cancellationToken.ThrowIfCancellationRequested();
        var resolver = new PmmAssetPathResolver(project.Plan.ProjectPath, options);
        var ids = project.Plan.Document.Accessories.Select(a => (int)a.Index).ToHashSet();
        if (accessoryPathOverrides is not null && accessoryPathOverrides.Keys.Any(id => !ids.Contains(id)))
            throw new ArgumentException("Accessory path overrides contain an unknown stored accessory ID.", nameof(accessoryPathOverrides));
        var assets = new List<PreparedPmmAccessory>();
        readOptions ??= new();
        foreach (var accessory in project.Plan.Document.Accessories.OrderBy(a => a.DrawOrderIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? selected = accessoryPathOverrides?.GetValueOrDefault(accessory.Index);
            var resolution = resolver.Resolve(accessory.Path, selected);
            if (resolution.Status != PmmAssetResolutionStatus.Resolved)
                throw new InvalidDataException($"Accessory #{accessory.Index} '{accessory.Path.Value}': {resolution.Status}; candidates: {string.Join(" | ", resolution.Candidates)}");
            string path = resolution.Path!;
            if (!Path.GetExtension(path).Equals(".x", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("PMM accessory assets require .x files.");
            // File.OpenRead shares only readers, so an active writer cannot race the frozen source.
            using var input = File.OpenRead(path);
            PmmAccessorySourceSnapshot.ValidateInputLength(input.Length, readOptions);
            var bytes = new byte[checked((int)input.Length)];
            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = input.Read(bytes, offset, Math.Min(16 * 1024, bytes.Length - offset));
                if (count == 0) throw new EndOfStreamException("Accessory X source changed while freezing its original bytes.");
                offset += count;
            }
            if (input.ReadByte() != -1) throw new InvalidDataException("Accessory X source changed while freezing its original bytes.");
            cancellationToken.ThrowIfCancellationRequested();
            assets.Add(PreparedPmmAccessory.FromOriginalBytes(accessory.Index, path, bytes, texture =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, texture.Replace('\\', Path.DirectorySeparatorChar)));
                return loadTexture?.Invoke(full);
            }, readOptions, diagnostics: resolution.Diagnostics, cancellationToken: cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new PmmAccessoryPlayback(project, assets);
        // Validate the saved/current parent graph before this prepared set can be published.
        _ = candidate.Sample(); return candidate;
    }
    public IReadOnlyList<PmmAccessoryFrame> Sample()
        => Array.AsReadOnly(Assets.Select(asset =>
        {
            var sample = PmmAccessoryScene.Sample(project.Plan.Document, asset.Index, asset.Source,
                project.CurrentFrame, (model, bone) => project.GetBoneWorldTransform(model, bone), project.IsSavedPose);
            return sample with { Inputs = sample.Inputs!.WithDocument(asset.Document) };
        }).ToArray());

    /// <summary>Preserves the PMM mixed draw order, stored identities, visibility and additive blend requirement.</summary>
    public PmmDrawPlan SampleDrawPlan() => project.SampleDrawPlan(Sample());

    /// <summary>Explicit host composition choice, not a claim of the original global model/accessory interleaving.
    /// Additive surfaces remain exposed by Sample; this normal-blend composer rejects them instead of drawing them incorrectly.</summary>
    public OriginalFrameScene CombineWithProject(bool accessoriesAfterModels)
    {
        var accessories = Sample().Where(frame => frame.Visible).ToArray();
        if (accessories.Any(frame => frame.AddBlendEnabled))
            throw new NotSupportedException("Additive accessories require a host render pass; normal-blend scene composition cannot represent them.");
        if (accessories.Length == 0) return project.Frame;
        var frames = accessories.Select(frame => frame.Frame);
        var ordered = accessoriesAfterModels ? new[] { project.Frame }.Concat(frames).ToArray() : frames.Append(project.Frame).ToArray();
        return ProjectSceneAssembler.Combine(ordered, project.Frame.Surface.Profile.LightAmbient, project.Frame.Surface.Snapshot.LightDirection);
    }
}
