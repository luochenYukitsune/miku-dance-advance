using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using SkiaSharp;
using Role = Mmd.Modern.Rendering.D3D12.D3D12CharacterMaterialRole;

namespace Mmd.Modern.Demo;

/// <summary>Headless dev tool: renders one model with every built-in style preset, per material-role scope,
/// into labelled horizontal strips. Usage:
/// --style-compare --model a.pmx --output dir [--scope none|body|auto]... [--size 640x900] [--tag name]</summary>
internal static class StyleCompareCli
{
    private static readonly string[] Presets = ["mmd", "genshin", "star-rail", "wuthering-waves", "endfield"];
    private static readonly string[] AllScopes = ["none", "body", "auto", "noguess"];
    private static readonly (string Name, float YawDegrees)[] Views = [("front", 0), ("three-quarter", 40)];
    private const float FovDegrees = 30;
    private const int LabelHeight = 40;

    public static int Run(string[] args)
    {
        string? model = null, output = null, tag = "untagged";
        var scopes = new List<string>();
        int width = 640, height = 900;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
            switch (args[i])
            {
                case "--style-compare": break;
                case "--model": model = Next(); break;
                case "--output": output = Next(); break;
                case "--tag": tag = Next(); break;
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
        if (scopes.Count == 0) scopes.AddRange(AllScopes);

        Console.OutputEncoding = System.Text.Encoding.UTF8;
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
                foreach (string preset in Presets)
                {
                    SKBitmap tile;
                    try
                    {
                        var result = D3D12ViewRenderingStyles.Resolve(frame, D3D12StylePresetCatalog.BuiltIn,
                            [new SceneRenderingStyleBinding(objectId, new(preset), ModelRole.Character, 0, materials.Count, true)],
                            characterMaterials, guessMaterialRoles: scope != "noguess");
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

    private static D3D12ViewCharacterMaterialBinding Binding(Guid objectId, int material, Role role)
        => new(objectId, material, new D3D12CharacterMaterialStyle(role));

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
        using var note = new SKPaint { Color = new SKColor(150, 150, 160), TextSize = 18, IsAntialias = true };
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
