using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Mmd.Modern.Formats;

namespace Mmd.Modern.PmxScan;

/// <summary>
/// Reads every PMX under a corpus root with the production reader and writes a
/// machine-readable inventory (counts, additional UV count, text encoding).
/// </summary>
internal static class Program
{
    private const string DefaultCorpusRoot = @"D:\desktop\mmd渲染";
    private const string DefaultOutputPath = "artifacts/formats/pmx-scan.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] args)
    {
        string corpusRoot = args.Length > 0 ? args[0] : DefaultCorpusRoot;
        string outputPath = args.Length > 1 ? args[1] : DefaultOutputPath;

        if (!Directory.Exists(corpusRoot))
        {
            Console.Error.WriteLine($"corpus root not found: {corpusRoot}");
            return 2;
        }

        string[] files = Directory
            .GetFiles(corpusRoot, "*.pmx", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            Console.Error.WriteLine($"no *.pmx files under: {corpusRoot}");
            return 2;
        }

        var scans = new List<ScanFile>(files.Length);
        var errors = new List<ScanError>();

        foreach (string file in files)
        {
            try
            {
                using FileStream stream = File.OpenRead(file);
                PmxModel model = PmxReader.Read(stream);
                scans.Add(Scan(file, corpusRoot, model));
                Console.WriteLine(
                    $"{Path.GetRelativePath(corpusRoot, file)}: " +
                    $"v={model.Vertices.Count} f={model.Faces.Count} " +
                    $"m={model.Materials.Count} b={model.Bones.Count} " +
                    $"morph={model.Morphs.Count} uv={model.Header.AdditionalUvCount} " +
                    $"enc={EncodingName(model.Header.TextEncoding)}");
            }
            catch (Exception exception)
            {
                errors.Add(new ScanError(
                    Path.GetFullPath(file),
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message));
                Console.Error.WriteLine(
                    $"{Path.GetRelativePath(corpusRoot, file)}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        var summary = new ScanSummary(
            Files: files.Length,
            Parsed: scans.Count,
            Failed: errors.Count,
            TotalVertices: scans.Sum(scan => (long)scan.Vertices),
            TotalFaces: scans.Sum(scan => (long)scan.Faces),
            TotalMaterials: scans.Sum(scan => (long)scan.Materials),
            TotalBones: scans.Sum(scan => (long)scan.Bones),
            TotalMorphs: scans.Sum(scan => (long)scan.Morphs));

        var report = new ScanReport(
            Tool: "tools/Mmd.Modern.PmxScan",
            CorpusRoot: Path.GetFullPath(corpusRoot),
            Files: scans,
            Summary: summary,
            Errors: errors);

        string fullOutputPath = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string json = JsonSerializer.Serialize(report, JsonOptions);
        File.WriteAllText(fullOutputPath, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.WriteLine(
            $"wrote {fullOutputPath}: parsed {summary.Parsed}/{summary.Files}, " +
            $"failed {summary.Failed}");
        return errors.Count == 0 ? 0 : 1;
    }

    private static ScanFile Scan(string path, string corpusRoot, PmxModel model)
    {
        var weightTypes = model.Vertices
            .GroupBy(vertex => vertex.WeightType)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key.ToString(), group => group.Count());

        var morphTypes = model.Morphs
            .GroupBy(morph => morph.Type)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key.ToString(), group => group.Count());

        return new ScanFile(
            Path: Path.GetFullPath(path),
            RelativePath: Path.GetRelativePath(corpusRoot, path),
            Bytes: new FileInfo(path).Length,
            Sha256: ComputeSha256(path),
            Version: model.Header.Version,
            Encoding: EncodingName(model.Header.TextEncoding),
            AdditionalUvCount: model.Header.AdditionalUvCount,
            IndexWidths: new IndexWidths(
                Vertex: model.Header.VertexIndexSize,
                Texture: model.Header.TextureIndexSize,
                Material: model.Header.MaterialIndexSize,
                Bone: model.Header.BoneIndexSize,
                Morph: model.Header.MorphIndexSize,
                RigidBody: model.Header.RigidBodyIndexSize),
            Vertices: model.Vertices.Count,
            Faces: model.Faces.Count,
            Materials: model.Materials.Count,
            Bones: model.Bones.Count,
            Morphs: model.Morphs.Count,
            Textures: model.Textures.Count,
            DisplayFrames: model.DisplayFrames.Count,
            RigidBodies: model.RigidBodies.Count,
            Joints: model.Joints.Count,
            SoftBodies: model.SoftBodies.Count,
            WeightTypes: weightTypes,
            MorphTypes: morphTypes);
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string EncodingName(PmxTextEncoding encoding) =>
        encoding == PmxTextEncoding.Utf16LittleEndian ? "UTF-16LE" : "UTF-8";

    private sealed record ScanReport(
        string Tool,
        string CorpusRoot,
        IReadOnlyList<ScanFile> Files,
        ScanSummary Summary,
        IReadOnlyList<ScanError> Errors);

    private sealed record ScanFile(
        string Path,
        string RelativePath,
        long Bytes,
        string Sha256,
        float Version,
        string Encoding,
        int AdditionalUvCount,
        IndexWidths IndexWidths,
        int Vertices,
        int Faces,
        int Materials,
        int Bones,
        int Morphs,
        int Textures,
        int DisplayFrames,
        int RigidBodies,
        int Joints,
        int SoftBodies,
        IReadOnlyDictionary<string, int> WeightTypes,
        IReadOnlyDictionary<string, int> MorphTypes);

    private sealed record IndexWidths(
        int Vertex,
        int Texture,
        int Material,
        int Bone,
        int Morph,
        int RigidBody);

    private sealed record ScanSummary(
        int Files,
        int Parsed,
        int Failed,
        long TotalVertices,
        long TotalFaces,
        long TotalMaterials,
        long TotalBones,
        long TotalMorphs);

    private sealed record ScanError(string Path, string Type, string Message);
}
