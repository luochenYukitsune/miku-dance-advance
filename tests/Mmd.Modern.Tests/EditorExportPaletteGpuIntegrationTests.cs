using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
[Trait("Category", "Gpu")]
public sealed class EditorExportPaletteGpuIntegrationTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(QualityLoadingAppBuilder));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CharacterWithSavedAdditiveHardwareGiExportsWithoutChangingTheDraft() => await session.Dispatch(async () =>
    {
        using (var gpu = new D3D12FrameSession())
        {
            var capabilities = gpu.QueryRayGiCapabilities();
            Assert.True(capabilities.HardwareAvailable, capabilities.UnavailableReason);
            Assert.False(gpu.IsWarp);
        }

        string directory = Path.Combine(Path.GetTempPath(), "mda-export-palette-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string modelPath = Path.Combine(directory, "fixture.pmx");
            string outputPath = Path.Combine(directory, "frame.png");
            using var workspace = new EditorProjectWorkspace();
            var model = Model();
            var asset = EditorProjectWorkspace.CreateModelAsset(model, modelPath, Token);
            workspace.AddModel(modelPath, asset, ModelRole.Character);
            var frame = workspace.Session!.Frame;
            Assert.Contains(frame.Materials, flags => flags.SurfaceEnabled && flags.GiToonResponseOverride == RayGiToonResponse.PreservePaletteBands);
            Assert.Contains(frame.Surface.Snapshot.Materials, material => material.IndexCount > 0);

            var preview = new ViewQualityOptions(Gi: new RayGiOptions(LightingMode.Legacy,
                Composition: RayGiComposition.ToonPalette, Denoiser: RayGiDenoiser.TemporalSpatial));
            var quality = new ViewQualityOptions(Gi: new RayGiOptions(LightingMode.HardwareRayTracing,
                    Strength: 4, SamplesPerPixel: 16, Composition: RayGiComposition.Additive),
                Antialiasing: FrameAntialiasing.Smaa1X, SmaaQuality: SmaaQuality.Ultra, SpatialSamples: 32);
            var plan = new OfflineFramePlan(32, 32, new(60), 0, 0);
            var request = new ExportRequest(plan, quality);
            var preset = new ViewOutputPresetDefinition("test.export.palette", "Export palette",
                new(1280, 720, preview), new(32, 32, new(60), quality));
            var draft = new OutputSettingsDraft(preset, request, OutputMediaKind.PngImage, outputPath, false, Vector4.One, null);
            draft.Validate();

            var result = await EditorExportService.ExportAsync(workspace, draft, null, null, token: Token);

            Assert.Equal(1, result.CompletedFrames);
            Assert.True(File.Exists(outputPath));
            var pixels = ReadPng(File.ReadAllBytes(outputPath), 32, 32);
            Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i =>
                pixels[i * 4] != 255 || pixels[i * 4 + 1] != 255 || pixels[i * 4 + 2] != 255);
            Assert.Same(request, draft.Request);
            Assert.Equal(RayGiComposition.Additive, draft.Request.Quality!.EffectiveGi.Composition);
            Assert.Equal(4, draft.Request.Quality.EffectiveGi.Strength);
            Assert.Equal(16, draft.Request.Quality.EffectiveGi.SamplesPerPixel);
            Assert.Equal(32, draft.Request.Quality.SpatialSamples);
            Assert.Equal(SmaaQuality.Ultra, draft.Request.Quality.SmaaQuality);
            Assert.Equal(preview, draft.Preset.Preview.EffectiveQuality);
            Assert.Equal(RayGiComposition.Additive, draft.Preset.Export.Quality!.EffectiveGi.Composition);
        }
        finally
        {
            string cleanup = Path.GetFullPath(directory);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!cleanup.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(cleanup).StartsWith("mda-export-palette-", StringComparison.Ordinal)
                || (File.GetAttributes(cleanup) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unexpected export fixture cleanup path.");
            Directory.Delete(cleanup, recursive: true);
        }
        return true;
    }, Token).WaitAsync(TimeSpan.FromMinutes(5), Token);

    private static byte[] ReadPng(byte[] png, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        int width = 0, height = 0; using var compressed = new MemoryStream();
        for (int p = 8; p < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            string type = Encoding.ASCII.GetString(png, p + 4, 4);
            if (type == "IHDR") { width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p + 8)); height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p + 12)); }
            if (type == "IDAT") compressed.Write(png.AsSpan(p + 8, length));
            p += length + 12;
        }
        Assert.Equal(expectedWidth, width); Assert.Equal(expectedHeight, height);
        compressed.Position = 0; using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream(); zlib.CopyTo(raw);
        byte[] rows = raw.ToArray(), pixels = new byte[width * height * 4];
        Assert.Equal(height * (width * 4 + 1), rows.Length);
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, rows[y * (width * 4 + 1)]);
            rows.AsSpan(y * (width * 4 + 1) + 1, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
        }
        return pixels;
    }

    private static PmxModel Model() => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "fixture", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = new[] { new Vector3(-3, 7, 0), new Vector3(3, 7, 0), new Vector3(0, 13, 0) }
            .Select(p => new PmxVertex { Position = p, Normal = -Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [],
                WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
        Faces = [new(0, 1, 2)], Textures = [],
        Materials = [new() { Name = "color", EnglishName = "", Diffuse = new(.2f, .4f, .8f, 1), Specular = Vector3.Zero,
            SpecularPower = 4, Ambient = new(.3f), DrawMode = PmxDrawModeFlags.BothFaces, EdgeSize = 0,
            SphereMode = PmxSphereMode.None, EdgeColor = Vector4.Zero, TextureIndex = -1, SphereTextureIndex = -1,
            ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
        Morphs = [], DisplayFrames = [], Joints = [], SoftBodies = [], RigidBodies = []
    };

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
