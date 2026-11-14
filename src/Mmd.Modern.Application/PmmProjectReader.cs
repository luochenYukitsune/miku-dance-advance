using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Read-only asset-aware PMM boundary. PMM 0001 track lengths come from the exact
/// original PMD/PMX, before PMD base-morph removal or any runtime conversion.</summary>
public static class PmmProjectReader
{
    public static PmmDocument Read(string path, PmmImportOptions? importOptions = null,
        PmmReadOptions? readOptions = null, CancellationToken cancellationToken = default)
        => new Reader(path, importOptions, readOptions, cancellationToken).Read();

    public static PmmProjectImportPlan Prepare(string path, PmmImportOptions? importOptions = null,
        PmmReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        var reader = new Reader(path, importOptions, readOptions, cancellationToken);
        var document = reader.Read();
        return PmmProjectImport.PrepareWithModelLoader(document, reader.Path, importOptions,
            reader.LoadRuntimeModel, cancellationToken);
    }

    private sealed class Reader
    {
        private sealed record Loaded(PmxModel? Pmx, PmdModel? Pmd);
        private readonly Dictionary<string, Loaded> assets = new(StringComparer.OrdinalIgnoreCase);
        private readonly PmmAssetPathResolver resolver;
        private readonly PmmImportOptions importOptions;
        private readonly PmmReadOptions readOptions;
        private readonly CancellationToken cancellation;
        public string Path { get; }
        public Reader(string path, PmmImportOptions? importOptions, PmmReadOptions? readOptions, CancellationToken cancellation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            this.cancellation = cancellation; cancellation.ThrowIfCancellationRequested();
            Path = System.IO.Path.GetFullPath(path); this.importOptions = importOptions ?? new();
            this.readOptions = readOptions ?? new(); resolver = new(Path, this.importOptions);
        }
        public PmmDocument Read()
        {
            cancellation.ThrowIfCancellationRequested();
            using var input = new CancelReadStream(File.OpenRead(Path), cancellation);
            var options = new PmmReadOptions
            {
                AnsiCodePage = readOptions.AnsiCodePage,
                MaxElementCount = readOptions.MaxElementCount,
                MaxInputBytes = readOptions.MaxInputBytes,
                // A caller callback is authoritative, including its explicit null result.
                V1ModelMetadataResolver = readOptions.V1ModelMetadataResolver ?? Metadata
            };
            var document = PmmReader.Read(input, options); cancellation.ThrowIfCancellationRequested(); return document;
        }
        private PmmV1ModelMetadata Metadata(PmmV1ModelReference reference)
        {
            cancellation.ThrowIfCancellationRequested();
            importOptions.ModelPathOverrides.TryGetValue(reference.StoredModelIndex, out string? selected);
            var resolution = resolver.Resolve(reference.Path, selected);
            if (resolution.Status != PmmAssetResolutionStatus.Resolved)
                throw new InvalidDataException($"PMM 0001 model #{reference.StoredModelIndex} (position {reference.ModelPosition}) '{reference.Path.Value}': {resolution.Status}; candidates: {string.Join(" | ", resolution.Candidates)}");
            try
            {
                var source = Load(resolution.Path!);
                if (source.Pmd is { } pmd)
                    return new(pmd.Bones.Select(bone => bone.Name), pmd.Morphs.Select(morph => morph.Name), pmd.IkChains.Select(chain => chain.BoneIndex));
                var pmx = source.Pmx!;
                return new(pmx.Bones.Select(bone => bone.Name), pmx.Morphs.Select(morph => morph.Name),
                    pmx.Bones.Select((bone, index) => (bone, index)).Where(pair => pair.bone.Flags.HasFlag(PmxBoneFlags.Ik)).Select(pair => pair.index));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or NotSupportedException or ArgumentException)
            {
                throw new InvalidDataException($"PMM 0001 model #{reference.StoredModelIndex} (position {reference.ModelPosition}) '{reference.Path.Value}' resolved to '{resolution.Path}': {error.Message}", error);
            }
        }
        private Loaded Load(string path)
        {
            cancellation.ThrowIfCancellationRequested();
            if (assets.TryGetValue(path, out var cached)) return cached;
            using var input = new CancelReadStream(File.OpenRead(path), cancellation);
            if (input.Length > 512L * 1024 * 1024) throw new InvalidDataException("Model asset exceeds 512 MiB import limit.");
            string extension = System.IO.Path.GetExtension(path);
            var loaded = extension.ToLowerInvariant() switch
            {
                ".pmx" => new Loaded(PmxReader.Read(input), null),
                ".pmd" => new Loaded(null, PmdReader.Read(input)),
                _ => throw new NotSupportedException("PMM characters require PMD or PMX model assets.")
            };
            cancellation.ThrowIfCancellationRequested(); assets.Add(path, loaded); return loaded;
        }
        public PmxModel LoadRuntimeModel(string path)
        {
            var loaded = Load(path);
            if (loaded.Pmx is not null) return loaded.Pmx;
            var converted = PmdPmxAdapter.Convert(loaded.Pmd!);
            assets[path] = loaded with { Pmx = converted }; return converted;
        }
    }

    private sealed class CancelReadStream(Stream input, CancellationToken cancellation) : Stream
    {
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => input.CanSeek;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set { cancellation.ThrowIfCancellationRequested(); input.Position = value; } }
        public override int Read(byte[] buffer, int offset, int count) { cancellation.ThrowIfCancellationRequested(); return input.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { cancellation.ThrowIfCancellationRequested(); return input.Read(buffer); }
        public override int ReadByte() { cancellation.ThrowIfCancellationRequested(); return input.ReadByte(); }
        public override long Seek(long offset, SeekOrigin origin) { cancellation.ThrowIfCancellationRequested(); return input.Seek(offset, origin); }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
