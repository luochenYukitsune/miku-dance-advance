using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(RayGiTimingEnvironmentCollection.Name)]
public sealed class D3D12PmxMaterialTreatmentViewTests
{
    private const int Width = 97, Height = 73;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void WidthValidationUsesTheAppliedPresetRatherThanTheReplacedSourceInterval()
    {
        var source = Source();
        var flags = source.Materials.Select(value => value with { CharacterToonResponse = new(.05f, .1f) }).ToArray();
        source = new(source.Surface, source.Outline, source.OutlineOptions, source.Options, flags);
        var id = Guid.NewGuid();
        var result = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn,
            [new(id, new("star-rail"), ModelRole.Character, 0, 2, true)],
            [new(id, 0, new(PmxMaterialTreatment: new(1, 1.5f)))]);
        Assert.Equal(new CharacterToonResponse(), result.Scene.Materials[0].CharacterToonResponse);
        Assert.Equal(new CharacterToonResponse(.5f, .15f),
            result.SurfaceStyles.EffectiveToonResponse(0, result.Scene.Materials[0].CharacterToonResponse));
        Assert.Equal(new CharacterToonResponse(.05f, .1f), source.Materials[0].CharacterToonResponse);
        var catalog = new D3D12StylePresetCatalog([D3D12StylePresetResult.Resolve(
            new("narrow-custom", "Narrow custom", "star-rail", null, new(.05f, .1f)))]);
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(Source(), catalog,
            [new(id, new("narrow-custom"), ModelRole.Character, 0, 2, true)],
            [new(id, 0, new(PmxMaterialTreatment: new(1, 1.5f)))]));
    }

    [Fact]
    public void ExplicitInstanceLocalSelectionRebasesAndHiddenRowsDoNotLeakToAnotherObject()
    {
        var source = Source(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        SceneRenderingStyleBinding[] objects = [new(a, new("star-rail"), ModelRole.Character, 0, 1, true),
            new(b, new("endfield"), ModelRole.Character, 1, 1, true)];
        D3D12ViewCharacterMaterialBinding[] selected = [new(b, 0,
            new(PmxMaterialTreatment: new(1.02f, .9f)))];
        var detached = D3D12ViewCharacterMaterials.Detach(selected);
        selected[0] = new(a, 0, new(PmxMaterialTreatment: new(.8f, 1.1f)));
        var result = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn,
            objects, detached);
        Assert.Null(result.SurfaceStyles.EffectivePmxMaterialTreatment(0));
        Assert.Equal(new D3D12PmxMaterialTreatment(1.02f, .9f),
            result.SurfaceStyles.EffectivePmxMaterialTreatment(1));
        var hidden = objects[1] with { Visible = false, MaterialCount = 0, MaterialStart = 2 };
        var visible = objects[0] with { MaterialCount = 2 };
        var hiddenResult = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn,
            [visible, hidden], detached);
        Assert.All(Enumerable.Range(0, 2), i => Assert.Null(hiddenResult.SurfaceStyles.EffectivePmxMaterialTreatment(i)));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(source,
            D3D12StylePresetCatalog.BuiltIn, objects, [new(b, 1, new(PmxMaterialTreatment: new(.8f)))]));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(source,
            D3D12StylePresetCatalog.BuiltIn, [objects[0], objects[1] with { Role = ModelRole.Environment }], detached));
    }

    [Theory, InlineData("genshin"), InlineData("star-rail"), InlineData("wuthering-waves"), InlineData("endfield")]
    [Trait("Category", "Gpu")]
    public async Task CaptureAndPresentConsumeTheDetachedSelectionAndMmdRestoresExactly(string family)
    {
        var source = Source(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        SceneRenderingStyleBinding[] objects = [new(a, new(family), ModelRole.Character, 0, 1, true),
            new(b, new(family), ModelRole.Character, 1, 1, true)];
        D3D12ViewCharacterMaterialBinding[] selected = [new(b, 0, new(PmxMaterialTreatment: new(0, 1.1f)))];
        var request = new ViewRenderRequest(Width, Height);
        await using var treated = new D3D12ViewRenderer(request, characterMaterials: selected);
        selected[0] = new(a, 0, new(PmxMaterialTreatment: new(2, .8f)));
        Assert.Empty(treated.SceneInputRequests); // No textures, bones or second animation sample are requested.
        var plan = new OfflineFramePlan(Width, Height, new(30), 0, 1);
        var frame = new OfflineFrame(plan.GetSample(0), Width, Height, source, null, null, null)
        { RenderingStyles = objects, ResolvedCamera = source.Surface.Snapshot.Camera };
        using var actual = await treated.CaptureAsync(frame, Token);
        var resolvedSource = D3D12ViewScene.Resolve(source, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), request.EffectiveQuality, frame.ResolvedCamera);
        var resolved = D3D12ViewRenderingStyles.Resolve(resolvedSource, D3D12StylePresetCatalog.BuiltIn, objects,
            [new(b, 0, new(PmxMaterialTreatment: new(0, 1.1f)))]);
        using var lowLevel = new D3D12FrameSession();
        var expected = lowLevel.Capture(resolved.Scene, Width, Height, surfaceStyles: resolved.SurfaceStyles);
        Assert.Equal(expected.Rgba, actual.Pixels.ToArray());
        await using var ordinary = new D3D12ViewRenderer(request);
        using var before = await ordinary.CaptureAsync(frame, Token);
        var protectedClip = Vector4.Transform(new Vector4(-.8f, 0, 0, 1),
            source.Surface.Snapshot.Camera.ViewProjection(Width, Height));
        bool protectedOnLeft = protectedClip.X / protectedClip.W < 0;
        int changed = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int offset = (y * Width + x) * 4;
            Assert.Equal(before.Pixels.Span[offset + 3], actual.Pixels.Span[offset + 3]);
            // The camera at negative Z reverses world X on this right-handed view.
            // Derive the unselected half from projection rather than assuming it.
            if ((x < Width / 2) == protectedOnLeft)
                Assert.True(before.Pixels.Span.Slice(offset, 4).SequenceEqual(actual.Pixels.Span.Slice(offset, 4)));
            else if (!before.Pixels.Span.Slice(offset, 3).SequenceEqual(actual.Pixels.Span.Slice(offset, 3))) changed++;
        }
        Assert.True(changed > 100, $"Only {changed} selected pixels consumed treatment.");
        var mmd = frame with { RenderingStyles = objects.Select(value => value with { Preset = new("mmd") }).ToArray() };
        using var restored = await treated.CaptureAsync(mmd, Token);
        using var raw = await ordinary.CaptureAsync(mmd, Token);
        Assert.Equal(raw.Pixels.ToArray(), restored.Pixels.ToArray());
        nint window = CreateWindowExW(0, "STATIC", "MDA material treatment", 0x80000000, 0, 0, 128, 96, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            await using var presenter = new D3D12RealtimeViewRenderer(window, request,
                characterMaterials: [new(b, 0, new(PmxMaterialTreatment: new(0, 1.1f)))]);
            Assert.Empty(presenter.SceneInputRequests);
            var preview = new PreviewFrame(Width, Height, 0, source, null, null, null)
            { RenderingStyles = objects, ResolvedCamera = frame.ResolvedCamera };
            await presenter.PresentAsync(preview, Token);
            await presenter.PresentAsync(preview with { RenderingStyles = mmd.RenderingStyles }, Token);
            Assert.Equal(2, presenter.Statistics.PresentCount);
            Assert.Equal(0, presenter.Statistics.ExplicitCaptureCount);
            Assert.Equal(0, presenter.Statistics.Frame!.TotalReadbacks);
        }
        finally { DestroyWindow(window); }
    }

    [Fact]
    public void RendererProfilesApplyOnlyToExplicitOrdinaryMappingsAndIdentityOptOutRestoresOldLook()
    {
        var source = Source(); var id = Guid.NewGuid();
        SceneRenderingStyleBinding[] objects = [new(id, new("star-rail"), ModelRole.Character, 0, 2, true)];
        var mapped = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn,
            objects, [new(id, 1, new(D3D12CharacterMaterialRole.Hair))]);
        Assert.Null(mapped.SurfaceStyles.EffectivePmxMaterialTreatment(0));
        Assert.Equal(D3D12PmxMaterialTreatmentCatalog.Resolve(D3D12SurfaceStyle.StarRailPmx,
            D3D12CharacterMaterialRole.Hair), mapped.SurfaceStyles.EffectivePmxMaterialTreatment(1));
        var optedOut = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn,
            objects, [new(id, 1, new(D3D12CharacterMaterialRole.Hair, PmxMaterialTreatment: new()))]);
        Assert.Null(optedOut.SurfaceStyles.EffectivePmxMaterialTreatment(1));
        foreach (var role in new[] { D3D12CharacterMaterialRole.Face, D3D12CharacterMaterialRole.Eye })
        {
            var protectedRow = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn,
                objects, [new(id, 1, new(role))]);
            Assert.Null(protectedRow.SurfaceStyles.EffectivePmxMaterialTreatment(1));
        }
        var mmd = D3D12ViewRenderingStyles.Resolve(source, D3D12StylePresetCatalog.BuiltIn,
            [objects[0] with { Preset = new("mmd") }], [new(id, 1, new(D3D12CharacterMaterialRole.Hair))]);
        Assert.Null(mmd.SurfaceStyles.EffectivePmxMaterialTreatment(1));
    }

    private static OriginalFrameScene Source()
    {
        var vertices = new List<SceneVertex>(); var indices = new List<uint>();
        for (int i = 0; i < 2; i++)
        {
            float left = i == 0 ? -1.5f : .1f, right = i == 0 ? -.1f : 1.5f;
            uint first = (uint)vertices.Count;
            vertices.AddRange([new(new(left, -1, 0), -Vector3.UnitZ, Vector2.Zero),
                new(new(right, -1, 0), -Vector3.UnitZ, Vector2.UnitX),
                new(new(right, 1, 0), -Vector3.UnitZ, Vector2.One),
                new(new(left, 1, 0), -Vector3.UnitZ, Vector2.UnitY)]);
            indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
        }
        var camera = new SceneCamera(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20);
        var snapshot = new SceneSnapshot(vertices.ToArray(), indices.ToArray(),
            [new("source left", 0, 6, new(.7f, .2f, .1f, 1), Vector3.Zero, Vector3.Zero, 1),
             new("source right", 6, 6, new(.7f, .2f, .1f, 1), Vector3.Zero, Vector3.Zero, 1)],
            camera: camera, lightDirection: -Vector3.UnitZ);
        var outline = new OriginalOutlineData(vertices.Select((vertex, i) => new OriginalOutlineVertex(vertex.Position,
            vertex.Normal, OriginalOutlineVertexMode.Expand, 0, i / 4)),
            [new OriginalOutlineMaterial(0, Vector4.Zero, false), new OriginalOutlineMaterial(0, Vector4.Zero, false)]);
        return new(new(snapshot, [new(new(.5f)), new(new(.5f))], new(Vector3.One, Vector3.One,
            Matrix4x4.Identity, OriginalTextureSampling.PointClamp)), outline,
            new(Vector4.Zero, Matrix4x4.Identity, camera.Eye, 30, 1),
            new(OriginalAlphaBlendMode.Normal, true, false, BackgroundColor: new Vector4(0, 0, 0, 1)));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
