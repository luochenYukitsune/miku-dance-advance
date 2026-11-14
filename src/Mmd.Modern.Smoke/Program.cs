using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Mmd.Modern.Core;
using Mmd.Modern.Physics.Bepu;
using Mmd.Modern.Rendering.Veldrid;

if (args.Any(a => a.StartsWith("--mme-", StringComparison.OrdinalIgnoreCase) || a.Equals("--effect", StringComparison.OrdinalIgnoreCase) || a.Equals("--d3d12-postfx", StringComparison.OrdinalIgnoreCase) || a.Equals("--d3d12-smoke", StringComparison.OrdinalIgnoreCase)))
    throw new NotSupportedException("MME experiments are archived for the legacy compatibility renderer; modern rendering does not load MME.");

var modernIndex = Array.FindIndex(args, a => a.Equals("--d3d12-modern", StringComparison.OrdinalIgnoreCase));
if (modernIndex >= 0)
{
    var selectedModel = Array.FindIndex(args, a => a.Equals("--model", StringComparison.OrdinalIgnoreCase));
    if (modernIndex + 1 >= args.Length || args[modernIndex + 1].StartsWith("--", StringComparison.Ordinal)
        || selectedModel < 0 || selectedModel + 1 >= args.Length || args[selectedModel + 1].StartsWith("--", StringComparison.Ordinal))
        throw new InvalidOperationException("--d3d12-modern <output> --model <file.pmx> requires an explicit model");
    var styleIndex = Array.FindIndex(args, a => a.Equals("--style", StringComparison.OrdinalIgnoreCase));
    if (styleIndex >= 0 && (styleIndex + 1 >= args.Length || args[styleIndex + 1].StartsWith("--", StringComparison.Ordinal)))
        throw new InvalidOperationException("--style needs original or soft");
    var style = styleIndex < 0 ? "original" : args[styleIndex + 1].ToLowerInvariant();
    Environment.Exit(style switch
    {
        "original" => Mmd.Modern.Smoke.OriginalStyleSmokeCli.Run(args[modernIndex + 1], args[selectedModel + 1], Mmd.Modern.Smoke.OriginalStyleSmokeCli.ParseOptions(args)),
        "soft" => Mmd.Modern.Smoke.ModernToonSmokeCli.Run(args[modernIndex + 1], args[selectedModel + 1]),
        _ => throw new InvalidOperationException("--style must be original or soft")
    });
}

var meshIndex = Array.FindIndex(args, a => a.Equals("--d3d12-mesh", StringComparison.OrdinalIgnoreCase));
if (meshIndex >= 0)
{
    if (meshIndex + 1 >= args.Length) throw new InvalidOperationException("--d3d12-mesh requires an output directory; optional --model <file.pmx>");
    var modelIndex = Array.FindIndex(args, a => a.Equals("--model", StringComparison.OrdinalIgnoreCase));
    if (modelIndex >= 0 && modelIndex + 1 >= args.Length) throw new InvalidOperationException("--model requires a PMX path");
    Environment.Exit(Mmd.Modern.Smoke.MeshSmokeCli.Run(args[meshIndex + 1], modelIndex < 0 ? null : args[modelIndex + 1]));
}

var toonIndex = Array.FindIndex(args, a => a.Equals("--toon-ramps", StringComparison.OrdinalIgnoreCase));
if (toonIndex >= 0)
{
    if (toonIndex + 1 >= args.Length) throw new InvalidOperationException("--toon-ramps requires an output path");
    Environment.Exit(Mmd.Modern.Smoke.ToonSmokeCli.Run(args[toonIndex + 1]));
}

var output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "artifacts/gpu");
Directory.CreateDirectory(output);
global::Veldrid.GraphicsBackend? requestedBackend = null;
var backendIndex = Array.FindIndex(args, a => a.Equals("--backend", StringComparison.OrdinalIgnoreCase));
if (backendIndex >= 0 && backendIndex + 1 < args.Length)
{
    requestedBackend = args[backendIndex + 1].ToLowerInvariant() switch
    {
        "vulkan" => global::Veldrid.GraphicsBackend.Vulkan,
        "d3d11" or "direct3d11" => global::Veldrid.GraphicsBackend.Direct3D11,
        var value => throw new InvalidOperationException($"Unsupported smoke backend: {value}"),
    };
}
var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures/humanoid.pmd");
using var input = File.OpenRead(fixture);
var model = PmdReader.Read(input);
Require(model.Vertices.Length == 2618 && model.Indices.Length == 13440 && model.Materials.Length == 14 && model.Bones.Length == 6, "Fixture counts");
var restVertices = Skinning.Apply(model, Skinning.CreateSkinMatrices(model));
RenderedFrame rest;
RenderedFrame pose;
using (var renderer = new VeldridRenderBackend(requestedBackend))
{
    rest = renderer.Render(PmdSceneAdapter.FromSkinned(model, restVertices).Snapshot, 960, 720);
    var rotations = Enumerable.Repeat(Quaternion.Identity, model.Bones.Length).ToArray();
    rotations[2] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.55f);
    var posedVertices = Skinning.Apply(model, Skinning.CreateSkinMatrices(model, rotations));
    Require(Vector3.Distance(restVertices[374].Position, posedVertices[374].Position) > 0.1f, "CPU pose must change an arm vertex");
    pose = renderer.Render(PmdSceneAdapter.FromSkinned(model, posedVertices).Snapshot, 960, 720);
}
int nonBackground = 0, changedPixels = 0;
var colors = new HashSet<int>();
for (int i = 0; i < rest.Rgba.Length; i += 4)
{
    if (rest.Rgba[i] != 20 || rest.Rgba[i + 1] != 24 || rest.Rgba[i + 2] != 32) nonBackground++;
    if (!rest.Rgba.AsSpan(i, 4).SequenceEqual(pose.Rgba.AsSpan(i, 4))) changedPixels++;
    colors.Add((rest.Rgba[i] << 16) | (rest.Rgba[i + 1] << 8) | rest.Rgba[i + 2]);
}
Require(rest.DrawCalls == 14 && nonBackground > 20000 && nonBackground < 300000 && colors.Count > 100 && changedPixels > 500, "Actual GPU geometry/color/pose acceptance");
using (var freshDevice = new VeldridRenderBackend(requestedBackend))
{
    var repeat = freshDevice.Render(PmdSceneAdapter.FromSkinned(model, restVertices).Snapshot, 960, 720);
    Require(repeat.Rgba.AsSpan().SequenceEqual(rest.Rgba), "Fresh device must reproduce rest frame");
}
float fallY, finalY;
using (var physics = new BepuPhysicsBackend())
{
    physics.AddGround(0);
    var body = physics.AddDynamicSphere(new Vector3(0, 5, 0), .5f, 1);
    for (int i = 0; i < 30; i++) physics.Step(1f / 120);
    fallY = physics.GetPosition(body).Y;
    for (int i = 0; i < 570; i++) physics.Step(1f / 120);
    finalY = physics.GetPosition(body).Y;
    Require(fallY is > 4.5f and < 4.9f && finalY is > .47f and < .53f, "Bepu fall and contact");
    physics.Reset();
    try { physics.GetPosition(body); throw new InvalidOperationException("Stale handle accepted"); } catch (ArgumentException) { }
}
var bmp = FrameImage.ToBmp(rest);
File.WriteAllBytes(Path.Combine(output, "rest.bmp"), bmp);
File.WriteAllBytes(Path.Combine(output, "posed.bmp"), FrameImage.ToBmp(pose));
var report = new
{
    success = true, backend = rest.Backend, device = rest.Device, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    width = rest.Width, height = rest.Height, vertices = model.Vertices.Length, indices = model.Indices.Length,
    materials = model.Materials.Length, bones = model.Bones.Length, draw_calls = rest.DrawCalls,
    non_background_pixels = nonBackground, unique_colors = colors.Count, changed_pose_pixels = changedPixels,
    fresh_device_repeat_exact = true, devices_created_and_disposed = 2,
    physics_fall_y = fallY, physics_rest_y = finalY, physics_reset_checked = true,
    pmd_unparsed_tail_bytes = model.UnparsedTailBytes,
    fixture_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))).ToLowerInvariant(),
    rest_bmp_sha256 = Convert.ToHexString(SHA256.HashData(bmp)).ToLowerInvariant(),
    original_executable_required = false, native_core_wrapper_used = false, visible_window_created = false,
    scope = "Original generated PMD only; basic material lighting; no original image equivalence or Bullet numerical equivalence"
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(output, "report.json"), json); Console.WriteLine(json);
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
