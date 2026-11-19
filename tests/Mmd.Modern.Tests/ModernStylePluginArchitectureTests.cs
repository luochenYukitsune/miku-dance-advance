using System.Collections.ObjectModel;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ModernStylePluginArchitectureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ArchitectureAndPackagingBoundariesKeepRenderingNeutralAndPluginsIsolated()
    {
        // 1. Mmd.Modern.Rendering.D3D12 and its transitive Mmd.Modern.* graph must never reference Plugins or StylePlugin assemblies.
        var pending = new Queue<Assembly>([typeof(D3D12FrameSession).Assembly]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();
            string name = assembly.GetName().Name!;
            if (!visited.Add(name))
                continue;

            Assert.False(IsForbiddenPluginOrHostAssembly(name), $"D3D12 graph loaded forbidden assembly '{name}'.");
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                string refName = reference.Name ?? "";
                Assert.False(IsForbiddenPluginOrHostAssembly(refName),
                    $"Rendering assembly '{name}' must not reference '{refName}'.");
                if (refName.StartsWith("Mmd.Modern.", StringComparison.Ordinal))
                    pending.Enqueue(Assembly.Load(reference));
            }
        }
        Assert.Contains("Mmd.Modern.Rendering.D3D12", visited);

        foreach (var type in typeof(D3D12FrameSession).Assembly.GetExportedTypes())
        {
            AssertNoPluginContractTypes(type);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertNoPluginContractTypes(method.ReturnType);
                foreach (var param in method.GetParameters())
                    AssertNoPluginContractTypes(param.ParameterType);
            }
            foreach (var ctor in type.GetConstructors())
            {
                foreach (var param in ctor.GetParameters())
                    AssertNoPluginContractTypes(param.ParameterType);
            }
            foreach (var prop in type.GetProperties())
                AssertNoPluginContractTypes(prop.PropertyType);
            foreach (var field in type.GetFields())
                AssertNoPluginContractTypes(field.FieldType);
        }

        // 2. Mmd.Modern.ViewOutput.D3D12 depends on Contracts (interfaces/DTOs only), never on PluginManager or concrete StylePlugins.
        var viewRefs = typeof(D3D12ViewRenderer).Assembly
            .GetReferencedAssemblies()
            .Select(static a => a.Name!)
            .ToArray();
        Assert.Contains("Mmd.Modern.Plugins.Contracts", viewRefs);
        Assert.DoesNotContain("Mmd.Modern.Plugins", viewRefs);
        Assert.DoesNotContain(viewRefs, static name => IsConcreteStylePlugin(name));

        // 3. Mmd.Modern.Core remains neutral with zero plugin/D3D12/Vortice/Avalonia coupling.
        var coreRefs = typeof(SceneSnapshot).Assembly
            .GetReferencedAssemblies()
            .Select(static a => a.Name!)
            .ToArray();
        Assert.DoesNotContain(coreRefs, static name =>
            name.StartsWith("Mmd.Modern.Plugins", StringComparison.Ordinal)
            || IsConcreteStylePlugin(name)
            || name.StartsWith("Mmd.Modern.Rendering", StringComparison.Ordinal)
            || name.StartsWith("Vortice", StringComparison.Ordinal)
            || name.StartsWith("Avalonia", StringComparison.Ordinal));

        // 4. Mmd.Modern.App.deps.json contains zero static references to concrete style plugin assemblies.
        string appDepsPath = Path.Combine(AppContext.BaseDirectory, "Mmd.Modern.App.deps.json");
        Assert.True(File.Exists(appDepsPath), $"Expected Mmd.Modern.App.deps.json at '{appDepsPath}'.");
        using (var stream = File.OpenRead(appDepsPath))
        using (var deps = JsonDocument.Parse(stream))
        {
            if (deps.RootElement.TryGetProperty("libraries", out var libraries))
            {
                foreach (var lib in libraries.EnumerateObject())
                    Assert.False(IsConcreteStylePlugin(lib.Name), $"App deps.json must not reference plugin library '{lib.Name}'.");
            }
            if (deps.RootElement.TryGetProperty("targets", out var targets))
            {
                foreach (var target in targets.EnumerateObject())
                {
                    foreach (var entry in target.Value.EnumerateObject())
                        Assert.False(IsConcreteStylePlugin(entry.Name), $"App deps.json target '{target.Name}' must not reference '{entry.Name}'.");
                }
            }
        }

        // 5. Staged plugin folders exist and must contain zero stray Contracts DLL or PDB files.
        Assert.True(Directory.Exists(TestStylePlugins.BundledPluginsRoot));
        foreach (string pluginId in TestStylePlugins.OfficialPluginIds)
        {
            string dir = Path.Combine(TestStylePlugins.BundledPluginsRoot, pluginId);
            Assert.True(Directory.Exists(dir), $"Missing bundled plugin staging folder '{dir}'.");
            Assert.True(File.Exists(Path.Combine(dir, "mda-plugin.json")));
            Assert.False(File.Exists(Path.Combine(dir, "Mmd.Modern.Plugins.Contracts.dll")),
                $"Plugin directory '{dir}' must not carry a duplicate Mmd.Modern.Plugins.Contracts.dll.");
            Assert.Empty(Directory.GetFiles(dir, "*.pdb", SearchOption.AllDirectories));
        }

        string exampleDir = Path.Combine(TestStylePlugins.ExamplePluginsRoot, "mda.style.custom-ink-cel");
        Assert.True(Directory.Exists(exampleDir), $"Missing example plugin staging folder '{exampleDir}'.");
        Assert.True(File.Exists(Path.Combine(exampleDir, "mda-plugin.json")));
        Assert.False(File.Exists(Path.Combine(exampleDir, "Mmd.Modern.Plugins.Contracts.dll")));
        Assert.Empty(Directory.GetFiles(exampleDir, "*.pdb", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CollectiblePluginManagerDiscoversOfficialAndExamplePluginsPrewarmsCustomInkCelAndTogglesCleanly()
    {
        var capabilities = new PluginCapability[]
        {
            new(PluginCapabilities.EditorCommands, typeof(IPluginCommand)),
            new(PluginCapabilities.RenderingStyles, typeof(IRenderingStyleProvider))
        };
        await using var manager = new PluginManager(new PluginHostInfo(new Version(0, 1, 22), 1, "en"), capabilities);

        var refresh = await manager.RefreshAsync([TestStylePlugins.BundledPluginsRoot, TestStylePlugins.ExamplePluginsRoot], Token);
        Assert.True(refresh.Succeeded, refresh.Diagnostic);

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mda.style.genshin"] = "genshin",
            ["mda.style.star-rail"] = "star-rail",
            ["mda.style.wuthering-waves"] = "wuthering-waves",
            ["mda.style.endfield"] = "endfield",
            ["mda.style.custom-ink-cel"] = "custom-ink-cel",
        };
        Assert.Equal(expected.Count, manager.Plugins.Count);

        foreach (var (pluginId, expectedStyle) in expected)
        {
            var info = manager.Plugins.Single(p => p.Id == pluginId);
            Assert.Equal(PluginState.Disabled, info.State);
            Assert.Contains(PluginCapabilities.RenderingStyles, info.Capabilities!);
            Assert.Contains(expectedStyle, info.ProvidedStyles!);
            Assert.NotNull(info.LocalizedNames);
            Assert.NotNull(info.LocalizedDescriptions);
            foreach (string lang in new[] { "zh-CN", "en", "ja" })
            {
                Assert.True(info.LocalizedNames!.ContainsKey(lang), $"{pluginId} missing localizedName '{lang}'.");
                Assert.True(info.LocalizedDescriptions!.ContainsKey(lang), $"{pluginId} missing localizedDescription '{lang}'.");
                Assert.False(string.IsNullOrWhiteSpace(info.GetDisplayName(lang)));
                Assert.False(string.IsNullOrWhiteSpace(info.GetDescription(lang)));
            }
        }

        foreach (string pluginId in expected.Keys)
        {
            var enabled = await manager.EnableAsync(pluginId, Token);
            Assert.True(enabled.Succeeded, enabled.Diagnostic);
        }

        var providers = manager.Extensions.GetExtensions<IRenderingStyleProvider>(PluginCapabilities.RenderingStyles);
        Assert.Equal(expected.Count, providers.Count);
        var definitions = providers.SelectMany(ext => ext.Value.GetStyles()).ToArray();
        Assert.Equal(expected.Count, definitions.Length);
        foreach (string styleId in expected.Values)
            Assert.Contains(definitions, def => def.StyleId == styleId);

        var viewCatalog = new D3D12ViewStyleCatalog(definitions, displayLanguage: "en");
        var presetCatalog = viewCatalog.ToD3D12PresetCatalog();
        Assert.Equal(6, presetCatalog.List().Count);
        Assert.NotNull(viewCatalog.FindStyle("custom-ink-cel"));
        Assert.NotNull(presetCatalog.Find("custom-ink-cel").PluginRegistration);

        var warmup = D3D12ViewShaderPrewarm.BuildFromViewRequest(
            new ViewQualityOptions(),
            styleCatalog: viewCatalog,
            stylePresets: presetCatalog);

        var customVariants = warmup.StylizedVariants.Where(v => v.StyleId == "custom-ink-cel").ToArray();
        Assert.Contains(customVariants, v => v.Module.ModuleId == "default");
        var accentVariant = Assert.Single(customVariants, v => v.Module.ModuleId == "ink-rim-accent");
        Assert.Equal("1", accentVariant.Module.Defines!["INK_RIM_ACCENT"]);
        Assert.True(accentVariant.Module.GeometryRequirements.HasFlag(
            D3D12StyleGeometryRequirements.FrontFace | D3D12StyleGeometryRequirements.PrimitiveId));

        foreach (var variant in customVariants)
        {
            var hooks = variant.EffectiveHooksOverride ?? variant.Module.EffectiveHooks;
            var geom = variant.EffectiveGeometryOverride ?? variant.Module.GeometryRequirements;
            byte[] vs = D3D12StyleShaderAssembler.CompileVertex(variant.Module, geom, nativeLights: false, gi: false, subTexture: false);
            byte[] psSurface = D3D12StyleShaderAssembler.CompilePixel(variant.StyleId, "PSSurface", variant.Module, hooks, geom,
                nativeLights: false, gi: false, textureMorph: false, subTexture: false, variant.CharacterToon, variant.PreserveDirectLightPalette);
            byte[] psGiNative = D3D12StyleShaderAssembler.CompilePixel(variant.StyleId, "PSSurfaceGiNative", variant.Module, hooks, geom,
                nativeLights: true, gi: true, textureMorph: false, subTexture: false, variant.CharacterToon, variant.PreserveDirectLightPalette);
            Assert.NotEmpty(vs);
            Assert.NotEmpty(psSurface);
            Assert.NotEmpty(psGiNative);
        }

        // Disable example plugin cleanly without interrupting the remaining 4 official plugins.
        var disabled = await manager.DisableAsync("mda.style.custom-ink-cel", Token);
        Assert.True(disabled.Succeeded, disabled.Diagnostic);
        Assert.Equal(PluginState.Disabled, manager.Plugins.Single(p => p.Id == "mda.style.custom-ink-cel").State);
        Assert.DoesNotContain(manager.Extensions.GetExtensions<IRenderingStyleProvider>(PluginCapabilities.RenderingStyles)
            .SelectMany(ext => ext.Value.GetStyles()), def => def.StyleId == "custom-ink-cel");
        Assert.Equal(4, manager.Extensions.GetExtensions<IRenderingStyleProvider>(PluginCapabilities.RenderingStyles).Count);

        // Re-enable cleanly.
        var reEnabled = await manager.EnableAsync("mda.style.custom-ink-cel", Token);
        Assert.True(reEnabled.Succeeded, reEnabled.Diagnostic);
        Assert.Equal(PluginState.Active, manager.Plugins.Single(p => p.Id == "mda.style.custom-ink-cel").State);
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task CustomInkCelPluginRendersDistinctLutAndRimAccentOnGpuAndRestoresMmdBaseline()
    {
        const int width = 128, height = 96;
        var source = SphereFixture();
        var objectId = Guid.NewGuid();
        var viewCatalog = TestStylePlugins.ViewCatalogWithExample;
        var presetCatalog = TestStylePlugins.PresetCatalogWithExample;
        var request = new ViewRenderRequest(width, height);
        var plan = new OfflineFramePlan(width, height, new(30), 0, 3);

        OfflineFrame FrameWithStyle(int sampleIndex, string styleId) =>
            new(plan.GetSample(sampleIndex), width, height, source, null, null, null)
            {
                RenderingStyles =
                [
                    new SceneRenderingStyleBinding(objectId, new(styleId), ModelRole.Character, 0, source.Materials.Count, true)
                ]
            };

        byte[] mmdBaseline;
        byte[] customDefaultPixels;
        byte[] mmdRestored;

        await using (var renderer = new D3D12ViewRenderer(request, stylePresets: presetCatalog, styleCatalog: viewCatalog))
        {
            Assert.False(renderer.IsWarp);
            using var baseline = await renderer.CaptureAsync(FrameWithStyle(0, "mmd"), Token);
            mmdBaseline = baseline.Pixels.ToArray();

            using var customDefault = await renderer.CaptureAsync(FrameWithStyle(1, "custom-ink-cel"), Token);
            customDefaultPixels = customDefault.Pixels.ToArray();

            using var restored = await renderer.CaptureAsync(FrameWithStyle(2, "mmd"), Token);
            mmdRestored = restored.Pixels.ToArray();
        }

        Assert.True(MeanRgbDifference(mmdBaseline, customDefaultPixels) > 4.0,
            "custom-ink-cel default LUT/ink contour rendering must produce pixels distinctly different from MMD baseline.");
        Assert.Equal(mmdBaseline, mmdRestored);

        // Render with explicit material mode "ink-rim-accent" and tuned StyleGlobalParameters.
        var accentBinding = new D3D12ViewPluginMaterialBinding(
            objectId,
            0,
            new StyleMaterialBindingData(
                "custom-ink-cel",
                "ink-rim-accent",
                NumberValues: new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["AccentBoost"] = 2.8
                })));

        var globalParams = new Dictionary<string, StyleMaterialBindingData>(StringComparer.Ordinal)
        {
            ["custom-ink-cel"] = new StyleMaterialBindingData(
                "custom-ink-cel",
                "default",
                NumberValues: new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["InkEdgeStrength"] = 0.9,
                    ["RimHighlightIntensity"] = 1.35
                }),
                VectorValues: new ReadOnlyDictionary<string, float[]>(new Dictionary<string, float[]>(StringComparer.Ordinal)
                {
                    ["InkWarmTint"] = [1.0f, 0.80f, 0.65f]
                }))
        };

        byte[] customAccentPixels;
        await using (var accentRenderer = new D3D12ViewRenderer(
            request,
            stylePresets: presetCatalog,
            styleCatalog: viewCatalog,
            pluginBindings: [accentBinding],
            styleGlobalParameters: globalParams))
        {
            Assert.False(accentRenderer.IsWarp);
            using var accent = await accentRenderer.CaptureAsync(FrameWithStyle(0, "custom-ink-cel"), Token);
            customAccentPixels = accent.Pixels.ToArray();
        }

        Assert.True(MeanRgbDifference(mmdBaseline, customAccentPixels) > 4.0);
        Assert.True(MeanRgbDifference(customDefaultPixels, customAccentPixels) > 0.5,
            "ink-rim-accent mode + tuned StyleGlobalParameters must measurably change rendered pixels vs custom-ink-cel default.");
    }

    private static bool IsConcreteStylePlugin(string name)
        => name.StartsWith("Mda.StylePlugin.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Mda.CustomStylePlugin", StringComparison.OrdinalIgnoreCase);

    private static bool IsForbiddenPluginOrHostAssembly(string name)
        => name.StartsWith("Mmd.Modern.Plugins", StringComparison.Ordinal)
            || name.Equals("Mmd.Modern.App", StringComparison.Ordinal)
            || IsConcreteStylePlugin(name);

    private static void AssertNoPluginContractTypes(Type type)
    {
        string asmName = type.Assembly.GetName().Name ?? "";
        Assert.False(IsForbiddenPluginOrHostAssembly(asmName),
            $"Public D3D12 API type '{type.FullName}' leaks forbidden assembly '{asmName}'.");
        if (type.HasElementType && type.GetElementType() is { } elem)
            AssertNoPluginContractTypes(elem);
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
                AssertNoPluginContractTypes(arg);
        }
    }

    private static OriginalFrameScene SphereFixture()
    {
        const int rings = 24, segments = 32;
        var vertices = new List<SceneVertex>();
        var indices = new List<uint>();
        for (int r = 0; r <= rings; r++)
        {
            for (int s = 0; s <= segments; s++)
            {
                float theta = MathF.PI * r / rings, phi = 2 * MathF.PI * s / segments;
                var normal = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
                vertices.Add(new(normal * .95f, normal, new((float)s / segments, (float)r / rings)));
            }
        }
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                uint a = (uint)(r * (segments + 1) + s), b = a + segments + 1;
                indices.AddRange([a, a + 1, b, a + 1, b + 1, b]);
            }
        }
        var snapshot = new SceneSnapshot(vertices.ToArray(), indices.ToArray(),
            [new SceneMaterial("body", 0, indices.Count, new(.82f, .52f, .58f, 1), new(.3f, .3f, .3f), Vector3.Zero, 20)],
            camera: new(new(0, 0, -4.5f), Vector3.Zero, Vector3.UnitY, 2.2f, .1f, 20),
            lightDirection: Vector3.Normalize(new(-.65f, .35f, -.6f)));
        return new(
            new(snapshot, [new OriginalStyleMaterialBinding(new(.7f))],
                new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: D3D12OriginalFrameTests.Options());
    }

    private static double MeanRgbDifference(byte[] a, byte[] b)
    {
        long sum = 0;
        int count = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i + 3] == 0 && b[i + 3] == 0)
                continue;
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            count++;
        }
        return count == 0 ? 0 : sum / (3.0 * count);
    }
}
