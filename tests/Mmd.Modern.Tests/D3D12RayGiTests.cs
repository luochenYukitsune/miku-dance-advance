using System.Text.Json;
using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12RayGiTests
{
    [Fact]
    public void SoftwareAndHardwareTraverseRealSceneGeometryAndChangeIndirectRadiance()
    {
        var frame = Bounce();
        var cpuScene = RayGiScene.FromOriginal(frame);
        Assert.True(cpuScene.Bvh.TraceAny(new(new(0, 0, -.001f), -Vector3.UnitZ, .001f, 50)),
            "CPU BVH must see the colored wall behind the receiver");
        using var session = new D3D12FrameSession();
        var capabilities = session.QueryRayGiCapabilities();
        Assert.True(capabilities.SoftwareAvailable,
            $"tier={capabilities.RawRaytracingTier}, sm={capabilities.ShaderModel}, device5={capabilities.Device5}, list4={capabilities.CommandList4}, compiler={capabilities.CompilerAvailable}, reason={capabilities.UnavailableReason}");
        var off = session.Capture(frame, 257, 191);
        var softwareOptions = new RayGiOptions(LightingMode.SoftwareRayTracing, Strength: 8,
            SamplesPerPixel: 8, Seed: 17, MaxRayDistance: 50, RayEpsilon: .001f,
            DirectionalIrradiance: new(3), DirectScale: 1);
        var software = session.Capture(frame, 257, 191, rayGiOptions: softwareOptions);
        int softwareChanged = Changed(off, software);
        SaveDebug("software-red", capabilities, off, software, softwareChanged);
        Assert.True(softwareChanged > 100, $"software GI changed only {softwareChanged} pixels");
        Assert.NotNull(session.RayGiStatistics);
        Assert.Equal(LightingMode.SoftwareRayTracing, session.RayGiStatistics!.Effective);
        Assert.Equal("GPU-BVH", session.RayGiBackendIdentity);
        var softwareStatistics=session.RayGiStatistics;

        Assert.True(capabilities.HardwareAvailable,
            capabilities.UnavailableReason ?? $"tier={capabilities.RawRaytracingTier}, sm={capabilities.ShaderModel}");
        var hardwareOptions = softwareOptions with { Mode = LightingMode.HardwareRayTracing };
        var hardware = session.Capture(frame, 257, 191, rayGiOptions: hardwareOptions);
        int hardwareChanged = Changed(off, hardware);
        Assert.True(hardwareChanged > 100, $"hardware GI changed only {hardwareChanged} pixels");
        Assert.StartsWith("DXR-raw-", session.RayGiBackendIdentity);
        Assert.Equal(LightingMode.HardwareRayTracing, session.RayGiStatistics!.Effective);

        string? path = Environment.GetEnvironmentVariable("MMD_RAYGI_ARTIFACTS");
        if (path is not null)
        {
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "off.rgba"), off.Rgba);
            File.WriteAllBytes(Path.Combine(path, "software.rgba"), software.Rgba);
            File.WriteAllBytes(Path.Combine(path, "hardware.rgba"), hardware.Rgba);
            File.WriteAllText(Path.Combine(path, "result.json"), JsonSerializer.Serialize(new
            {
                capabilities,
                softwareChanged,
                hardwareChanged,
                softwareStatistics,
                hardwareStatistics=session.RayGiStatistics,
                backend = session.RayGiBackendIdentity,
                adapter = session.AdapterName,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static int Changed(RenderedFrame left, RenderedFrame right)
    {
        Assert.Equal(left.Rgba.Length, right.Rgba.Length);
        int changed = 0;
        for (int i = 0; i < left.Rgba.Length; i += 4)
            if (!left.Rgba.AsSpan(i, 3).SequenceEqual(right.Rgba.AsSpan(i, 3))) changed++;
        return changed;
    }

    internal static OriginalFrameScene Bounce()
    {
        static SceneVertex[] Quad(float z, float half)
        {
            var normal = z < 0 ? Vector3.UnitZ : -Vector3.UnitZ;
            return
            [
                new(new(-half, -half, z), normal, new(0, 1)),
                new(new(half, -half, z), normal, new(1, 1)),
                new(new(half, half, z), normal, new(1, 0)),
                new(new(-half, half, z), normal, new(0, 0)),
            ];
        }
        var vertices = Quad(0, 2).Concat(Quad(-8, 15)).ToArray();
        var materials = new[]
        {
            new SceneMaterial("receiver", 0, 6, new(.9f, .9f, .9f, 1), Vector3.Zero, Vector3.Zero, 1),
            new SceneMaterial("red wall", 6, 6, new(1, .05f, .04f, 1), Vector3.Zero, Vector3.Zero, 1),
        };
        var snapshot = new SceneSnapshot(vertices, [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6], materials,
            camera: new(new(0, 0, -6), Vector3.Zero, Vector3.UnitY, 4.5f, .1f, 30),
            lightDirection: Vector3.Normalize(new(0.5f, 1, 0.5f)));
        var surface = new OriginalStyleScene(snapshot,
        [
            new(Vector3.One, DoubleSided: false),
            new(Vector3.One, DoubleSided: true),
        ], OriginalStyleProfile.SelectedReference with { TextureSampling = OriginalTextureSampling.PointClamp });
        return new(surface, options: D3D12OriginalFrameTests.Options(true),
            materials: [new(false, false, false), new(false, false, false)]);
    }

    private static void SaveDebug(string name, D3D12RayGiCapabilities capabilities,
        RenderedFrame off, RenderedFrame gi, int changed)
    {
        string? path = Environment.GetEnvironmentVariable("MMD_RAYGI_ARTIFACTS");
        if (path is null) return;
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, name + "-off.rgba"), off.Rgba);
        File.WriteAllBytes(Path.Combine(path, name + "-gi.rgba"), gi.Rgba);
        File.WriteAllText(Path.Combine(path, name + ".json"), JsonSerializer.Serialize(new
        {
            changed,
            capabilities,
            offSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(off.Rgba)).ToLowerInvariant(),
            giSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(gi.Rgba)).ToLowerInvariant(),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
