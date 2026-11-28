using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using SkiaSharp;
using Role = Mmd.Modern.Rendering.D3D12.D3D12CharacterMaterialRole;

namespace Mmd.Modern.Demo;

/// <summary>Headless dev tool: renders one model with every available style (host MMD plus loaded
/// rendering-style plugins), per material-role scope, into labelled horizontal strips. Usage:
/// --style-compare --model a.pmx --output dir [--scope none|body|auto]... [--size 640x900] [--tag name]</summary>
internal static class StyleCompareCli
{
    private static readonly string[] AllScopes = ["none", "body", "auto", "noguess"];
    private static readonly (string Name, float YawDegrees)[] Views = [("front", 0), ("three-quarter", 40)];
    private const float FovDegrees = 30;
    private const int LabelHeight = 40;

    public static int Run(string[] args)
    {
        string? model = null, output = null, tag = null;
        var scopes = new List<string>();
        var styles = new List<string>();
        bool polishCompare = false;
        int width = 640, height = 900;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
            switch (args[i])
            {
                case "--style-compare": break;
                case "--polish-compare": polishCompare = true; break;
                case "--model": model = Next(); break;
                case "--output": output = Next(); break;
                case "--tag": tag = Next(); break;
                case "--style":
                    foreach (string raw in Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        styles.Add(raw);
                    break;
                case "--scope":
                    string scope = Next();
                    if (!AllScopes.Contains(scope)) throw new ArgumentException($"Unknown scope '{scope}'.");
                    scopes.Add(scope); break;
                case "--size":
                    var parts = Next().Split('x', 'X');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height)
                        || width < 16 || height < 16) throw new ArgumentException("--size expects WxH, for example 640x900.");
                    break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        if (model is null || output is null) throw new ArgumentException("--model and --output are required.");
        if (polishCompare) return RunPolishCompare(model, output, tag, styles, width, height);
        tag ??= "untagged";
        if (scopes.Count == 0) scopes.AddRange(AllScopes);

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var pluginManager = LoadStylePlugins(out var styleCatalog, out var presetCatalog);
        try
        {
            string[] presets = new[] { D3D12SurfaceStyleMap.DefaultStyleId }
                .Concat(styleCatalog.Styles.Select(static s => s.StyleId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Console.WriteLine($"Loaded {styleCatalog.Styles.Count} style plugin definition(s): {string.Join(", ", presets)}");

            var source = LoadModel(Path.GetFullPath(model));
            var materials = source.Surface.Snapshot.Materials;
            var autoRoles = materials.Select(material => D3D12CharacterMaterialRoleGuess.Guess(material.Name)).ToArray();
            Console.WriteLine($"{materials.Count} materials, {source.Surface.Snapshot.Textures.Count} textures; auto classification:");
            for (int i = 0; i < materials.Count; i++)
                Console.WriteLine($"  {i,3}  {autoRoles[i],-4}  tex {materials[i].DiffuseTexture,3}  {materials[i].Name}");

            string directory = Path.Combine(output, tag), tileDirectory = Path.Combine(directory, "tiles");
            Directory.CreateDirectory(tileDirectory);
            var objectId = Guid.NewGuid();
            using var session = new D3D12FrameSession();
            Console.WriteLine($"D3D12 session ready (WARP: {session.IsWarp}).");
            int failures = 0;
            foreach (var (viewName, yaw) in Views)
            {
                var frame = BuildFrame(source, yaw);
                foreach (string scope in scopes)
                {
                    var characterMaterials = scope switch
                    {
                        "none" or "noguess" => null,
                        "body" => Enumerable.Range(0, materials.Count).Select(i => Binding(objectId, i, Role.Body)).ToArray(),
                        _ => Enumerable.Range(0, materials.Count).Select(i => Binding(objectId, i, autoRoles[i])).ToArray(),
                    };
                    var tiles = new List<(string Label, SKBitmap Tile)>();
                    foreach (string preset in presets)
                    {
                        SKBitmap tile;
                        try
                        {
                            var result = D3D12ViewRenderingStyles.Resolve(
                                frame,
                                presetCatalog,
                                [new SceneRenderingStyleBinding(objectId, new(preset), ModelRole.Character, 0, materials.Count, true)],
                                characterMaterials,
                                styleCatalog: styleCatalog,
                                guessMaterialRoles: scope != "noguess");
                            tile = ToBitmap(session.Capture(result.Scene, width, height, surfaceStyles: result.SurfaceStyles));
                        }
                        catch (Exception error)
                        {
                            failures++;
                            Console.WriteLine($"  [{viewName}/{scope}/{preset}] FAILED: {error.GetType().Name}: {error.Message}");
                            tile = ErrorTile($"{error.GetType().Name}: {error.Message}", width, height);
                        }
                        SavePng(tile, Path.Combine(tileDirectory, $"{viewName}-{scope}-{preset}.png"));
                        tiles.Add((preset, tile));
                    }
                    string stripName = viewName == "front" ? $"{scope}.png" : $"{scope}-{viewName}.png";
                    using (var strip = Strip(tiles, $"{viewName} / {scope}", width, height))
                        SavePng(strip, Path.Combine(directory, stripName));
                    foreach (var (_, tile) in tiles) tile.Dispose();
                    Console.WriteLine($"Wrote {Path.GetFullPath(Path.Combine(directory, stripName))}");
                }
            }
            Console.WriteLine(failures == 0 ? "All tiles rendered." : $"{failures} tile(s) failed; see red tiles.");
            return 0;
        }
        finally
        {
            pluginManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string ResolveRequestedStyleId(string styleId, HashSet<string> availableStyles)
    {
        if (availableStyles.Contains(styleId))
            return styleId;
        if (string.Equals(styleId, "starrail", StringComparison.OrdinalIgnoreCase)
            && availableStyles.Contains("star-rail"))
            return "star-rail";
        foreach (string candidate in availableStyles)
        {
            if (string.Equals(candidate, styleId, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        throw new ArgumentException($"Style '{styleId}' is not loaded. Available plugin styles: {string.Join(", ", availableStyles)}");
    }

    private static int RunPolishCompare(
        string model,
        string output,
        string? tag,
        List<string> requestedStyles,
        int width,
        int height)
    {
        if (requestedStyles.Count == 0)
            requestedStyles.AddRange(["star-rail", "genshin"]);

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var pluginManager = LoadStylePlugins(out var styleCatalog, out var presetCatalog);
        try
        {
            var availableStyles = new HashSet<string>(styleCatalog.Styles.Select(static s => s.StyleId), StringComparer.Ordinal);
            for (int i = 0; i < requestedStyles.Count; i++)
                requestedStyles[i] = ResolveRequestedStyleId(requestedStyles[i], availableStyles);

            var source = LoadModel(Path.GetFullPath(model));
            var materials = source.Surface.Snapshot.Materials;
            var autoRoles = materials.Select(material => D3D12CharacterMaterialRoleGuess.Guess(material.Name)).ToArray();
            string modelSlug = SanitizeFileName(Path.GetFileNameWithoutExtension(model));
            string directory = string.IsNullOrWhiteSpace(tag) ? output : Path.Combine(output, tag);
            string tileDirectory = Path.Combine(directory, "tiles");
            Directory.CreateDirectory(tileDirectory);

            var objectId = Guid.NewGuid();
            using var session = new D3D12FrameSession();
            Console.WriteLine($"D3D12 polish-compare ready (WARP: {session.IsWarp}, model: {modelSlug}, styles: {string.Join(", ", requestedStyles)}).");

            // Mandatory MMD baseline 0-pixel-diff regression check.
            int mmdDiffPixels = 0;
            foreach (var (viewName, yaw) in Views)
            {
                var frame = BuildFrame(source, yaw);
                var mmdResolved = D3D12ViewRenderingStyles.Resolve(
                    frame,
                    presetCatalog,
                    [new SceneRenderingStyleBinding(objectId, new(D3D12SurfaceStyleMap.DefaultStyleId), ModelRole.Character, 0, materials.Count, true)],
                    characterMaterials: null,
                    styleCatalog: styleCatalog,
                    guessMaterialRoles: false);
                var baseline = session.Capture(mmdResolved.Scene, width, height, surfaceStyles: mmdResolved.SurfaceStyles);
                var off = session.Capture(
                    mmdResolved.Scene,
                    width,
                    height,
                    surfaceStyles: mmdResolved.SurfaceStyles,
                    depthRim: D3D12DepthRimOptions.Off,
                    bloom: D3D12BloomOptions.Off,
                    toneCurve: D3D12ToneCurveOptions.Off);
                int viewDiff = CountDifferentPixels(baseline.Rgba, off.Rgba);
                mmdDiffPixels += viewDiff;
                Console.WriteLine($"  [mmd/{viewName}] 0-diff check: {viewDiff} different pixel(s).");
            }
            if (mmdDiffPixels != 0)
            {
                Console.Error.WriteLine($"ERROR: MMD baseline regression check failed with {mmdDiffPixels} different pixel(s).");
                return 1;
            }

            var faceForward = Vector3.TransformNormal(-Vector3.UnitZ, source.Surface.Snapshot.World);
            var depthRimOn = D3D12StylizedPolishDefaults.DepthRim;
            var bloomOn = D3D12StylizedPolishDefaults.Bloom;
            var toneCurveOn = D3D12StylizedPolishDefaults.ToneCurve;

            int failures = 0;
            foreach (string style in requestedStyles)
            {
                var baseShading = presetCatalog.Find(style).DefaultStylizedShading;
                var baselineMaterials = Enumerable.Range(0, materials.Count)
                    .Select(i => new D3D12ViewCharacterMaterial(
                        objectId,
                        i,
                        autoRoles[i],
                        StylizedShading: baseShading is null
                            ? null
                            : D3D12StylizedShadingCatalog.ResolveForRole(
                                baseShading with { CastShadowStrength = 0f, FaceLightStrength = 0f, FaceLightDirection = null },
                                autoRoles[i])))
                    .ToArray();
                var enhancedMaterials = Enumerable.Range(0, materials.Count)
                    .Select(i =>
                    {
                        var shading = baseShading is null
                            ? null
                            : D3D12StylizedShadingCatalog.ResolveForRole(
                                baseShading with
                                {
                                    CastShadowStrength = D3D12StylizedPolishDefaults.DefaultCastShadowStrength,
                                    FaceLightStrength = D3D12StylizedPolishDefaults.DefaultFaceLightStrength,
                                    FaceLightDirection = faceForward,
                                },
                                autoRoles[i]);
                        return new D3D12ViewCharacterMaterial(objectId, i, autoRoles[i], StylizedShading: shading);
                    })
                    .ToArray();

                var stages = new (
                    string Key,
                    string Label,
                    D3D12ViewCharacterMaterial[] Materials,
                    D3D12DepthRimOptions? DepthRim,
                    D3D12BloomOptions? Bloom,
                    D3D12ToneCurveOptions? ToneCurve)[]
                {
                    ("0-baseline", "Baseline", baselineMaterials, null, null, null),
                    ("1-face-shadow", "+Face+HardShadow", enhancedMaterials, null, null, null),
                    ("2-depth-rim", "+DepthRim", enhancedMaterials, depthRimOn, null, null),
                    ("3-bloom-tone", "+Bloom+ToneCurve", enhancedMaterials, depthRimOn, bloomOn, toneCurveOn),
                };

                var rowStrips = new List<SKBitmap>(Views.Length);
                try
                {
                    foreach (var (viewName, yaw) in Views)
                    {
                        var frame = BuildFrame(source, yaw);
                        var tiles = new List<(string Label, SKBitmap Tile)>(stages.Length);
                        try
                        {
                            foreach (var stage in stages)
                            {
                                SKBitmap tile;
                                try
                                {
                                    var resolved = D3D12ViewRenderingStyles.Resolve(
                                        frame,
                                        presetCatalog,
                                        [new SceneRenderingStyleBinding(objectId, new(style), ModelRole.Character, 0, materials.Count, true)],
                                        stage.Materials,
                                        styleCatalog: styleCatalog,
                                        guessMaterialRoles: true);
                                    tile = ToBitmap(session.Capture(
                                        resolved.Scene,
                                        width,
                                        height,
                                        surfaceStyles: resolved.SurfaceStyles,
                                        depthRim: stage.DepthRim,
                                        bloom: stage.Bloom,
                                        toneCurve: stage.ToneCurve));
                                }
                                catch (Exception error)
                                {
                                    failures++;
                                    Console.WriteLine($"  [{style}/{viewName}/{stage.Key}] FAILED: {error.GetType().Name}: {error.Message}");
                                    tile = ErrorTile($"{error.GetType().Name}: {error.Message}", width, height);
                                }
                                SavePng(tile, Path.Combine(tileDirectory, $"{modelSlug}-{style}-{viewName}-{stage.Key}.png"));
                                tiles.Add((stage.Label, tile));
                            }
                            rowStrips.Add(Strip(tiles, $"{modelSlug} | {style} | {viewName}", width, height));
                        }
                        finally
                        {
                            foreach (var (_, tile) in tiles) tile.Dispose();
                        }
                    }

                    string sheetPath = Path.Combine(directory, $"{modelSlug}-{style}-polish-compare.png");
                    using var sheet = StackVertically(rowStrips);
                    SavePng(sheet, sheetPath);
                    Console.WriteLine($"Wrote {Path.GetFullPath(sheetPath)}");
                }
                finally
                {
                    foreach (var row in rowStrips) row.Dispose();
                }
            }

            Console.WriteLine(failures == 0 ? "Polish compare completed." : $"{failures} tile(s) failed; see red tiles.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            pluginManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int CountDifferentPixels(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return Math.Max(a.Length, b.Length) / 4;
        int diff = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3])
                diff++;
        }
        return diff;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "model";
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }

    private static SKBitmap StackVertically(IReadOnlyList<SKBitmap> rows)
    {
        int totalWidth = rows.Max(static r => r.Width);
        int totalHeight = rows.Sum(static r => r.Height);
        var stacked = new SKBitmap(totalWidth, totalHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(stacked);
        canvas.Clear(new SKColor(28, 28, 32));
        int y = 0;
        foreach (var row in rows)
        {
            canvas.DrawBitmap(row, 0, y);
            y += row.Height;
        }
        return stacked;
    }

    private static PluginManager LoadStylePlugins(out D3D12ViewStyleCatalog styleCatalog, out D3D12StylePresetCatalog presetCatalog)
    {
        string pluginRoot = Path.Combine(AppContext.BaseDirectory, "plugins");
        var manager = new PluginManager(
            new PluginHostInfo(new Version(0, 1, 0), 1, "en"),
            [
                new(PluginCapabilities.EditorCommands, typeof(IPluginCommand)),
                new(PluginCapabilities.RenderingStyles, typeof(IRenderingStyleProvider))
            ]);
        try
        {
            var discovery = manager.RefreshAsync([pluginRoot]).GetAwaiter().GetResult();
            if (!discovery.Succeeded)
                Console.WriteLine($"WARNING: plugin discovery in '{pluginRoot}' reported: {discovery.Diagnostic}");

            foreach (var plugin in manager.Plugins)
            {
                if (plugin.State != PluginState.Disabled
                    || plugin.Capabilities?.Contains(PluginCapabilities.RenderingStyles, StringComparer.Ordinal) != true)
                    continue;
                var enabled = manager.EnableAsync(plugin.Id).GetAwaiter().GetResult();
                if (!enabled.Succeeded)
                    Console.WriteLine($"WARNING: failed to enable plugin '{plugin.Id}': {enabled.Diagnostic}");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var definitions = new List<IRenderingStyleDefinition>();
            foreach (var provider in manager.Extensions.GetExtensions<IRenderingStyleProvider>(PluginCapabilities.RenderingStyles))
            {
                var styles = provider.Value.GetStyles();
                if (styles is null) continue;
                foreach (var style in styles)
                {
                    if (style is not null && seen.Add(style.StyleId))
                        definitions.Add(style);
                }
            }

            styleCatalog = new D3D12ViewStyleCatalog(definitions, "en");
            presetCatalog = styleCatalog.ToD3D12PresetCatalog();
            return manager;
        }
        catch
        {
            manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>Some distributed models keep their textures in a subfolder the PMX does not name. When the
    /// model's own folder resolves none of them, retry texture resolution from the first direct subfolder that does.</summary>
    private static OriginalFrameScene LoadModel(string path)
    {
        var asset = EditorProjectWorkspace.LoadModelAsset(path);
        var textures = asset.Model.Textures.Select(texture => texture.Replace('\\', '/')).ToArray();
        bool Resolves(string folder) => textures.Any(texture => File.Exists(Path.Combine(folder, texture)));
        string modelFolder = Path.GetDirectoryName(path)!;
        if (textures.Length == 0 || Resolves(modelFolder)) return asset.Source;
        string? fallback = Directory.EnumerateDirectories(modelFolder).FirstOrDefault(Resolves);
        if (fallback is null)
        {
            Console.WriteLine("WARNING: none of the model's textures were found; rendering untextured.");
            return asset.Source;
        }
        Console.WriteLine($"WARNING: textures not beside the model; resolving them from subfolder '{Path.GetFileName(fallback)}'.");
        return EditorProjectWorkspace.CreateModelAsset(asset.Model, Path.Combine(fallback, Path.GetFileName(path))).Source;
    }

    private static D3D12ViewCharacterMaterial Binding(Guid objectId, int material, Role role)
        => new(objectId, material, role);

    /// <summary>Frames head to knees with a perspective camera yawed about the model, and a fixed key light
    /// from the viewer's upper front-left so a terminator crosses face and body.</summary>
    private static OriginalFrameScene BuildFrame(OriginalFrameScene source, float yawDegrees)
    {
        var snapshot = source.Surface.Snapshot;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var vertex in snapshot.Vertices)
        {
            var position = Vector3.Transform(vertex.Position, snapshot.World);
            min = Vector3.Min(min, position); max = Vector3.Max(max, position);
        }
        // Tails, wings and capes stretch the bounding box; the median vertex depth stays on the body.
        var depths = new float[snapshot.VertexCount];
        for (int i = 0; i < depths.Length; i++) depths[i] = Vector3.Transform(snapshot.Vertices[i].Position, snapshot.World).Z;
        Array.Sort(depths);
        // Percentiles ignore stray helper geometry far from the body. The upper body
        // (head to mid-thigh) is framed so face, hair and clothing shading read clearly.
        var heights = new float[snapshot.VertexCount];
        for (int i = 0; i < heights.Length; i++) heights[i] = Vector3.Transform(snapshot.Vertices[i].Position, snapshot.World).Y;
        Array.Sort(heights);
        float low = heights[heights.Length / 100], high = heights[heights.Length - 1 - heights.Length / 200], span = high - low;
        float top = high + span * .03f, bottom = low + span * .45f, halfVisible = (top - bottom) / 2;
        var target = new Vector3((min.X + max.X) / 2, (top + bottom) / 2, depths[depths.Length / 2]);
        float distance = halfVisible / MathF.Tan(FovDegrees * MathF.PI / 360), yaw = yawDegrees * MathF.PI / 180;
        // PMX data is left-handed with the character facing -Z, as in the PMM camera path.
        var eye = target + new Vector3(MathF.Sin(yaw), 0, -MathF.Cos(yaw)) * distance;
        var camera = new SceneCamera(eye, target, Vector3.UnitY, halfVisible * 2, distance * .05f, distance * 4,
            VerticalFovDegrees: FovDegrees, LeftHanded: true);
        // LightDirection points toward the light; screen-left is -X for this left-handed camera.
        var lit = snapshot.WithCamera(camera).WithLighting(Vector3.Normalize(new(-.55f, .7f, -.5f)));
        var surface = new OriginalStyleScene(lit, source.Surface.Materials, source.Surface.Profile);
        var shadow = new OriginalSelfShadowOptions(ModernDirectionalShadowProjection.Fit(lit), 2048, 2048, true, false,
            OriginalTextureSampling.LinearClamp, OriginalDepthComparison.LessEqual);
        var outlineOptions = source.OutlineOptions is { } outline
            ? outline with { ReferencePoint = new(target, 1), CameraPosition = eye, FovDegrees = FovDegrees } : null;
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, SelfShadow: shadow,
            BackgroundColor: new(.78f, .80f, .84f, 1));
        return new(surface, source.Outline, outlineOptions, options, source.Materials);
    }

    private static SKBitmap ToBitmap(RenderedFrame frame)
    {
        var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int row = 0; row < frame.Height; row++) Marshal.Copy(frame.Rgba, row * frame.Width * 4,
            IntPtr.Add(bitmap.GetPixels(), row * bitmap.RowBytes), frame.Width * 4);
        return bitmap;
    }

    private static SKBitmap ErrorTile(string message, int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(150, 24, 24));
        using var paint = new SKPaint { Color = SKColors.White, TextSize = 18, IsAntialias = true };
        float y = 40;
        var line = "";
        foreach (string word in ("ERROR " + message).Split(' '))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (paint.MeasureText(candidate) > width - 24 && line.Length > 0)
            {
                canvas.DrawText(line, 12, y, paint); y += 24; line = word;
            }
            else line = candidate;
        }
        canvas.DrawText(line, 12, y, paint);
        return bitmap;
    }

    private static SKBitmap Strip(IReadOnlyList<(string Label, SKBitmap Tile)> tiles, string caption, int width, int height)
    {
        var strip = new SKBitmap(width * tiles.Count, height + LabelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(strip);
        canvas.Clear(new SKColor(28, 28, 32));
        using var label = new SKPaint { Color = SKColors.White, TextSize = 26, IsAntialias = true };
        SKTypeface? matchedTypeface = null;
        foreach (char ch in caption)
        {
            if (ch > 127)
            {
                matchedTypeface = SKFontManager.Default.MatchCharacter(ch);
                if (matchedTypeface is not null) break;
            }
        }
        using var captionTypeface = matchedTypeface;
        using var note = new SKPaint { Color = new SKColor(150, 150, 160), TextSize = 18, IsAntialias = true, Typeface = captionTypeface };
        for (int i = 0; i < tiles.Count; i++)
        {
            canvas.DrawText(tiles[i].Label, i * width + 12, 30, label);
            canvas.DrawBitmap(tiles[i].Tile, i * width, LabelHeight);
        }
        canvas.DrawText(caption, strip.Width - note.MeasureText(caption) - 12, 28, note);
        return strip;
    }

    private static void SavePng(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        png.SaveTo(stream);
    }
}
