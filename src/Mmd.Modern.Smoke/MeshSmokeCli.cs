using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D12;
using SkiaSharp;

namespace Mmd.Modern.Smoke;

/// <summary>Windowless indexed mesh acceptance, separate from --d3d12-smoke effect colour strips.</summary>
public static class MeshSmokeCli
{
    public static int Run(string directory, string? modelPath = null)
    {
        string output = Path.GetFullPath(directory); Directory.CreateDirectory(output);
        var frames = new List<object>(); var sources = new List<object>(); var checks = new Dictionary<string, object>();
        using var renderer = new D3D12MeshRenderer();
        RenderedFrame Capture(string name, SceneSnapshot scene, int width = 769, int height = 577)
        {
            var frame = renderer.Render(scene, width, height);
            byte[] bmp = FrameImage.ToBmp(frame);
            File.WriteAllBytes(Path.Combine(output, name + ".bmp"), bmp);
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            Marshal.Copy(frame.Rgba, 0, bitmap.GetPixels(), frame.Rgba.Length);
            using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(output, name + ".png"), png.ToArray());
            frames.Add(new { name, frame.Width, frame.Height, frame.Backend, frame.Device, frame.DrawCalls,
                vertices = renderer.LastVertexCount, indices = renderer.LastIndexCount, uploadedTextures = renderer.LastUploadedTextures,
                nonBackground = NonBackground(frame), rawSha256 = Hash(frame.Rgba), bmpSha256 = Hash(bmp), debugMessages = renderer.LastDebugMessages });
            return frame;
        }
        void Source(string path, string role) => sources.Add(new { role, path = Path.GetFullPath(path), bytes = new FileInfo(path).Length, sha256 = Hash(File.ReadAllBytes(path)), readOnlySource = true });

        string pmdPath = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd"); Source(pmdPath, "original self-authored PMD");
        PmdModel pmd; using (var input = File.OpenRead(pmdPath)) pmd = PmdReader.Read(input);
        var restVertices = Skinning.Apply(pmd, Skinning.CreateSkinMatrices(pmd));
        var restScene = PmdSceneAdapter.FromSkinned(pmd, restVertices);
        var rest = Capture("pmd-rest", restScene.Snapshot);
        var rotations = Enumerable.Repeat(Quaternion.Identity, pmd.Bones.Length).ToArray();
        rotations[2] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.55f);
        var posed = Capture("pmd-posed", PmdSceneAdapter.FromSkinned(pmd, Skinning.Apply(pmd, Skinning.CreateSkinMatrices(pmd, rotations))).Snapshot);
        var yawed = Capture("pmd-yaw", PmdSceneAdapter.FromSkinned(pmd, restVertices, .55f).Snapshot);
        Require(rest.DrawCalls == 14 && NonBackground(rest) > 10000, "PMD full material mesh");
        checks["pmdPoseChangedPixels"] = Changed(rest, posed); checks["pmdYawChangedPixels"] = Changed(rest, yawed);
        Require(Changed(rest, posed) > 500 && Changed(rest, yawed) > 500, "PMD pose and yaw response");
        Require(rest.Rgba.AsSpan().SequenceEqual(renderer.Render(restScene.Snapshot, rest.Width, rest.Height).Rgba), "same device deterministic repeat");
        using (var fresh = new D3D12MeshRenderer()) Require(rest.Rgba.AsSpan().SequenceEqual(fresh.Render(restScene.Snapshot, rest.Width, rest.Height).Rgba), "fresh device deterministic repeat");
        checks["pmdSameAndFreshDeviceExact"] = true; checks["pmdDiagnostics"] = restScene.Diagnostics;

        var checker = Capture("checker", Checker(), 257, 191);
        var uv = Capture("checker-uv-flipped", Checker(flip: true), 257, 191);
        var toon = Capture("checker-alternate-toon", Checker(dark: true), 257, 191);
        var large = Capture("checker-uint32", Checker(large: true), 257, 191);
        Require(checker.Rgba.AsSpan().SequenceEqual(large.Rgba), "uint32 indices above 65535");
        Require(Changed(checker, uv) > 1000 && Changed(checker, toon) > 1000, "UV/toon sample response");
        int left = (70 * checker.Width + 95) * 4, right = (70 * checker.Width + 161) * 4;
        Require(checker.Rgba[left + 1] > 200 && checker.Rgba[left] == 0 && checker.Rgba[right] > 200 && checker.Rgba[right + 1] == 0, "UV checker fixed sample regions");
        Require(toon.Rgba[left + 1] == 64 && toon.Rgba[right] == 64, "toon texture fixed sample response");
        checks["uvChangedPixels"] = Changed(checker, uv); checks["toonChangedPixels"] = Changed(checker, toon);
        checks["uint32ImageExact"] = true; checks["checkerTopLeftRgb"] = checker.Rgba.AsSpan(left, 3).ToArray(); checks["checkerTopRightRgb"] = checker.Rgba.AsSpan(right, 3).ToArray();

        var triangle = PmxSceneAdapter.FromRestPose(TrianglePmx());
        var triangleFrame = Capture("pmx-constructed-triangle", WithCamera(triangle.Snapshot, new(new(0,.5f,-5), new(0,.5f,0), Vector3.UnitY, 3, .1f, 20)), 257, 191);
        Require(NonBackground(triangleFrame) > 1000, "constructed PMX triangle");
        string workspace = FindWorkspace();
        string corpusManifest = Path.Combine(workspace, "artifacts", "formats", "pmx-scan.json");
        if (File.Exists(corpusManifest))
        {
            using var corpus = JsonDocument.Parse(File.ReadAllText(corpusManifest));
            foreach (var file in corpus.RootElement.GetProperty("files").EnumerateArray().Take(4))
            {
                string path = file.GetProperty("path").GetString()!;
                if (!File.Exists(path)) path = Path.Combine(Path.GetDirectoryName(workspace)!, "MMD932_20261015_rea", "pmx_toon_review_20261018", "repro", "corpus", Path.GetFileName(path));
                if (!File.Exists(path)) throw new FileNotFoundException("Retained PMX corpus required for acceptance", path);
                Require(Hash(File.ReadAllBytes(path)) == file.GetProperty("sha256").GetString(), "PMX corpus identity");
                Source(path, "retained corpus PMX");
                using var input = File.OpenRead(path); var adapted = PmxSceneAdapter.FromRestPose(PmxReader.Read(input));
                var frame = Capture("corpus-" + Path.GetFileNameWithoutExtension(path), AutoFrame(adapted.Snapshot), 513, 385);
                Require(NonBackground(frame) > 100, "retained PMX visible geometry");
                checks[Path.GetFileName(path) + "Diagnostics"] = adapted.Diagnostics;
            }
        }
        if (modelPath is not null)
        {
            string path = Path.GetFullPath(modelPath), modelDirectory = Path.GetDirectoryName(path)!;
            Source(path, "user selected PMX, never copied or modified");
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var loadErrors = new List<string>();
            var textureLoads = new List<object>();
            SceneTexture? Load(string relative)
            {
                try
                {
                    var texture = SceneTextureLoader.Load(modelDirectory, relative);
                    textureLoads.Add(new { relative, status = texture is null ? "missing or unsupported" : "loaded", width = texture?.Width, height = texture?.Height,
                        rgbaSha256 = texture is null ? null : Hash(texture.Rgba.ToArray()) });
                    if (texture is not null && loaded.Add(relative)) Source(Path.GetFullPath(Path.Combine(modelDirectory, relative)), "user texture, never copied or modified");
                    return texture;
                }
                catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
                { loadErrors.Add($"{relative}: {e.Message}"); textureLoads.Add(new { relative, status = "error", error = e.Message }); return null; }
            }
            using var input = File.OpenRead(path); var model = PmxReader.Read(input);
            var adapted = PmxSceneAdapter.FromRestPose(model, Load);
            var scene = AutoFrame(adapted.Snapshot);
            var first = Capture("character-rest", scene, 961, 1081);
            var camera = scene.Camera;
            var rotated = new SceneSnapshot(scene.Vertices, scene.Indices, scene.Materials, scene.Textures,
                Matrix4x4.CreateTranslation(-camera.Target) * Matrix4x4.CreateRotationY(.4f) * Matrix4x4.CreateTranslation(camera.Target), camera,
                scene.LightDirection, scene.AdditionalUvs, scene.RawRadialSeeds);
            var second = Capture("character-yaw", rotated, 961, 1081);
            Require(NonBackground(first) > 10000 && Changed(first, second) > 1000, "actual character visible geometry and yaw response");
            Require(first.Rgba.AsSpan().SequenceEqual(renderer.Render(scene, first.Width, first.Height).Rgba), "character deterministic repeat");
            checks["characterChangedYawPixels"] = Changed(first, second); checks["characterSameDeviceExact"] = true;
            checks["characterLoadedDiffuseOrToonTextures"] = loaded.Count; checks["characterTextureLoadErrors"] = loadErrors;
            checks["characterDiagnostics"] = adapted.Diagnostics; checks["characterCamera"] = new { eye = camera.Eye.ToString(), target = camera.Target.ToString(), camera.OrthographicHeight, camera.Near, camera.Far };
            checks["characterTextureLoads"] = textureLoads;
            checks["characterMaterialTextureMapping"] = model.Materials.Select((m,i) => new { index = i, name = m.Name, mode = m.ToonMode.ToString(), m.ToonTextureIndex,
                diffusePath = m.TextureIndex < 0 ? null : model.Textures[m.TextureIndex], toonPath = m.ToonMode == PmxToonMode.Separate && m.ToonTextureIndex >= 0 ? model.Textures[m.ToonTextureIndex] : null,
                mappedDiffuse = adapted.Snapshot.Materials[i].DiffuseTexture, mappedToon = adapted.Snapshot.Materials[i].ToonTexture,
                toonUv = adapted.Snapshot.Materials[i].ToonUvTransform?.ToString() ?? "horizontal procedural/default", m.Diffuse.W }).ToArray();
        }
        var report = new { success = true, renderer = "actual D3D12 indexed mesh, depth and GPU diffuse/toon texture sampling", renderer.AdapterName,
            renderer.IsWarp, featureLevel = renderer.FeatureLevel.ToString(), os = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription,
            sources, frames, checks, originalExecutableRequired = false, nativeCoreWrapperUsed = false, visibleWindowCreated = false,
            limitations = new[] { "Modern first frame, no original pixel parity", "PMX static rest pose; no SDEF/QDEF animation/morph/physics", "PMD only preexisting two-weight CPU skinning", "PNG/BMP/JPEG diffuse/separate toon files and authored procedural toon only; separate toon uses vertical y=1-NdotL, procedural uses horizontal x=NdotL", "Fully transparent cutout only; no alpha blending/sorting, sphere maps, edges or self/ground shadows", "No MME shader execution, RT GI, swapchain or realtime UI" } };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "report.json"), json); Console.WriteLine(json); return 0;
    }

    private static SceneSnapshot WithCamera(SceneSnapshot s, SceneCamera c) => s.WithCamera(c);
    private static SceneSnapshot AutoFrame(SceneSnapshot s)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (uint index in s.Indices) { var p = s.Vertices[(int)index].Position; min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var center = (min + max) * .5f; float span = Math.Max(max.Y - min.Y, Math.Max(max.X - min.X, max.Z - min.Z)); span = Math.Max(.1f, span);
        return WithCamera(s, new(center + new Vector3(0, 0, -span * 2), center, Vector3.UnitY, span * 1.2f, .01f, span * 5));
    }
    private static int NonBackground(RenderedFrame f) { int n = 0; for (int i = 0; i < f.Rgba.Length; i += 4) if (f.Rgba[i] != 20 || f.Rgba[i+1] != 24 || f.Rgba[i+2] != 32) n++; return n; }
    private static int Changed(RenderedFrame a, RenderedFrame b) { int n = 0; for (int i = 0; i < a.Rgba.Length; i += 4) if (!a.Rgba.AsSpan(i,4).SequenceEqual(b.Rgba.AsSpan(i,4))) n++; return n; }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string FindWorkspace()
    {
        string directory = Environment.CurrentDirectory;
        while (!File.Exists(Path.Combine(directory, "Mmd.Modern.slnx"))) directory = Path.GetDirectoryName(directory) ?? throw new DirectoryNotFoundException("Run mesh smoke from MMD932_modern workspace.");
        return directory;
    }

    private static SceneSnapshot Checker(bool flip = false, bool dark = false, bool large = false)
    {
        int o = large ? 65536 : 0; var vertices = new SceneVertex[o + 4]; Array.Fill(vertices, new(Vector3.Zero, -Vector3.UnitZ, Vector2.Zero));
        Vector3[] p = [new(-1,-1,0), new(1,-1,0), new(1,1,0), new(-1,1,0)]; Vector2[] uv = [new(0,1), new(1,1), new(1,0), new(0,0)];
        for (int i = 0; i < 4; i++) vertices[o+i] = new(p[i], -Vector3.UnitZ, flip ? new(1-uv[i].X,uv[i].Y) : uv[i]);
        uint u = (uint)o; byte t = dark ? (byte)64 : (byte)255;
        return new(vertices, [u,u+1,u+2,u,u+2,u+3], [new("checker",0,6,Vector4.One,Vector3.Zero,Vector3.Zero,1,0,1)],
            [new("self-authored UV checker",2,2,[255,0,0,255,0,255,0,255,0,0,255,255,255,255,0,255]),new("self-authored toon",1,1,[t,t,t,255])],
            camera: new(new(0,0,-5),Vector3.Zero,Vector3.UnitY,4,.1f,20));
    }
    private static PmxModel TrianglePmx() => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0, VertexIndexSize = 4, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "authored triangle", EnglishName = "authored triangle", Comment = "constructed legitimate rest-pose PMX data", EnglishComment = "", Textures = [],
        Vertices = new[] { new Vector3(-1,0,0),new Vector3(1,0,0),new Vector3(0,1,0) }.Select(p => new PmxVertex { Position = p, Normal = -Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0,-1,-1,-1], BoneWeights = [1,0,0,0], EdgeScale = 1 }).ToArray(),
        Faces = [new(0,1,2)], Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = new(.8f,.3f,.1f,1), Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.BothFaces, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Common, ToonTextureIndex = 0, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "root", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate }], Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
    };
}
