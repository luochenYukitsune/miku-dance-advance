using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class D3D12FaceViewTests
{
    private static readonly D3D12StylePresetCatalog Catalog = D3D12StylePresetCatalog.BuiltIn;

    [Fact]
    public void ExplicitLocalIndicesKeepSameSourceInstancesIndependentAndDetachHostInput()
    {
        var scene = Frame();
        var first = Binding(0, 1);
        var second = Binding(1, 1);
        var source = new[] { new D3D12ViewCharacterMaterialBinding(second.ObjectId, 0, new(D3D12CharacterMaterialRole.Face, .7f)),
            new D3D12ViewCharacterMaterialBinding(first.ObjectId, 0, new(D3D12CharacterMaterialRole.Face, .25f)) };
        int enumerations = 0;
        IEnumerable<D3D12ViewCharacterMaterialBinding> Once()
        {
            enumerations++;
            foreach (var item in source) yield return item;
        }

        var resolved = D3D12ViewRenderingStyles.Resolve(scene, Catalog, [second, first], Once());

        Assert.Equal(1, enumerations);
        Assert.Equal(.25f, resolved.SurfaceStyles.EffectiveNormalShadeStrength(0));
        Assert.Equal(.7f, resolved.SurfaceStyles.EffectiveNormalShadeStrength(1));
        source[0] = source[0] with { Style = new() };
        Assert.Equal(D3D12CharacterMaterialRole.Face, resolved.SurfaceStyles.CharacterMaterials[1].Role);
        Assert.Equal(.7f, resolved.SurfaceStyles.EffectiveNormalShadeStrength(1));
        Assert.All(scene.Surface.Snapshot.Materials, material => Assert.Equal("same-source.pmx", material.Name));
        Assert.Equal(resolved.Scene.Materials,
            D3D12ViewRenderingStyles.Apply(scene, Catalog, [second, first],
                [new(first.ObjectId, 0, new(D3D12CharacterMaterialRole.Face, .25f)),
                    new(second.ObjectId, 0, new(D3D12CharacterMaterialRole.Face, .7f))]).Materials);
    }

    [Fact]
    public void HiddenAssignmentsRemainExplicitWithoutConsumingMaterialsAndValidateWhenVisible()
    {
        var scene = Frame();
        var visible = Binding(0, 2);
        var hidden = Binding(2, 0) with { Visible = false };
        D3D12ViewCharacterMaterialBinding[] selected = [new(hidden.ObjectId, 200, new(D3D12CharacterMaterialRole.Face))];

        Assert.True(D3D12ViewRenderingStyles.Preflight(scene, Catalog, [visible, hidden], selected).IsAvailable);
        var resolved = D3D12ViewRenderingStyles.Resolve(scene, Catalog, [visible, hidden], selected);
        Assert.All(resolved.SurfaceStyles.CharacterMaterials, material => Assert.Equal(D3D12CharacterMaterialRole.Body, material.Role));
        Assert.Contains(resolved.Diagnostics, diagnostic => diagnostic.Contains(hidden.ObjectId.ToString("D"))
            && diagnostic.Contains("Hidden") && diagnostic.Contains("200"));

        SceneRenderingStyleBinding[] nowVisible = [visible with { MaterialCount = 1 }, hidden with
            { MaterialStart = 1, MaterialCount = 1, Visible = true }];
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, Catalog, nowVisible, selected));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(scene, Catalog, nowVisible, selected));
    }

    [Fact]
    public void MmdRetainsFaceConfigurationWithoutChangingItsEffectiveResponseOrLegacyOutput()
    {
        var scene = Frame();
        var binding = Binding(0, 2, "mmd");
        var plain = D3D12ViewRenderingStyles.Resolve(scene, Catalog, [binding]);
        var face = D3D12ViewRenderingStyles.Resolve(scene, Catalog, [binding],
            [new(binding.ObjectId, 1, new(D3D12CharacterMaterialRole.Face, .2f))]);

        Assert.Equal(plain.Scene.Materials, face.Scene.Materials);
        Assert.Equal(D3D12CharacterMaterialRole.Face, face.SurfaceStyles.CharacterMaterials[1].Role);
        Assert.Equal(1f, face.SurfaceStyles.EffectiveNormalShadeStrength(1));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(plain.SurfaceStyles, face.SurfaceStyles));
        Assert.Contains(face.Diagnostics, diagnostic => diagnostic.Contains("whole draw") && diagnostic.Contains("MMD"));
        var genshin = binding with { Preset = new("genshin") };
        var genshinBody = D3D12ViewRenderingStyles.Resolve(scene, Catalog, [genshin]);
        // Explicit Body classification now opts into the renderer's ordinary PMX
        // profile. Identity isolates this test's unused Face-only scalar contract.
        var unusedBodySetting = D3D12ViewRenderingStyles.Resolve(scene, Catalog, [genshin],
            [new(binding.ObjectId, 1, new(D3D12CharacterMaterialRole.Body, .1f, PmxMaterialTreatment: new()))]);
        Assert.Equal(1f, unusedBodySetting.SurfaceStyles.EffectiveNormalShadeStrength(1));
        Assert.True(D3D12SurfaceStyleMap.Equivalent(genshinBody.SurfaceStyles, unusedBodySetting.SurfaceStyles));
        var legacy = D3D12ViewRenderingStyles.Resolve(scene, Catalog, []);
        Assert.Same(scene, legacy.Scene);
        Assert.Empty(legacy.Diagnostics);
    }

    [Theory]
    [InlineData(ModelRole.Environment)]
    [InlineData(ModelRole.Prop)]
    [InlineData(ModelRole.Unspecified)]
    public void ExplicitMaterialRolesRejectNonCharactersIncludingHiddenObjects(ModelRole role)
    {
        var scene = Frame();
        var visible = Binding(0, 2);
        var other = Binding(2, 0) with { Role = role, Visible = false };
        D3D12ViewCharacterMaterialBinding[] selected = [new(other.ObjectId, 0, new(D3D12CharacterMaterialRole.Face))];
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, Catalog, [visible, other], selected));
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(scene, Catalog, [visible, other], selected));
    }

    [Fact]
    public void InvalidIdentityIndexStyleAndDuplicateBindingsRejectBeforeDeviceConstruction()
    {
        var id = Guid.NewGuid();
        D3D12ViewCharacterMaterialBinding[][] invalid =
        [
            [new(Guid.Empty, 0, new())], [new(id, -1, new())], [new(id, 0, null!)], [null!],
            [new(id, 0, new((D3D12CharacterMaterialRole)200))],
            [new(id, 0, new(D3D12CharacterMaterialRole.Face, float.NaN))],
            [new(id, 0, new(D3D12CharacterMaterialRole.Face, float.PositiveInfinity))],
            [new(id, 0, new(D3D12CharacterMaterialRole.Face, -.01f))],
            [new(id, 0, new(D3D12CharacterMaterialRole.Body, 1.01f))],
            [new(id, 0, new()), new(id, 0, new(D3D12CharacterMaterialRole.Face))],
        ];
        foreach (var bindings in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(() => new D3D12ViewRenderer(new(97, 73), characterMaterials: bindings));
            // A zero HWND would fail native construction if static input were not checked first.
            Assert.ThrowsAny<ArgumentException>(() => new D3D12RealtimeViewRenderer(0, new(97, 73), characterMaterials: bindings));
        }
        var scene = Frame();
        var known = Binding(0, 2);
        foreach (var selected in new[]
        {
            new[] { new D3D12ViewCharacterMaterialBinding(Guid.NewGuid(), 0, new()) },
            new[] { new D3D12ViewCharacterMaterialBinding(known.ObjectId, 2, new()) },
        })
        {
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Preflight(scene, Catalog, [known], selected));
            Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(scene, Catalog, [known], selected));
        }
        Assert.Throws<ArgumentException>(() => D3D12ViewRenderingStyles.Resolve(scene, Catalog, [], [new(id, 0, new())]));
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task ActualCaptureUsesFaceLocallyMatchesDirectMapAndRejectsBeforeSubmission()
    {
        const int width = 129, height = 97;
        var scene = Frame();
        var binding = Binding(0, 2);
        var selected = new[] { new D3D12ViewCharacterMaterialBinding(binding.ObjectId, 0, new(D3D12CharacterMaterialRole.Face, .35f)) };
        var request = new ViewRenderRequest(width, height);
        var plan = new OfflineFramePlan(width, height, new(30), 0, 3);
        OfflineFrame FrameAt(int index, SceneRenderingStyleBinding instance) => new(plan.GetSample(index), width, height, scene, null, null, null)
        { RenderingStyles = Array.AsReadOnly(new[] { instance }) };
        var token = TestContext.Current.CancellationToken;
        await using var renderer = new D3D12ViewRenderer(request, characterMaterials: selected);
        selected[0] = selected[0] with { Style = new() };
        Assert.False(renderer.IsWarp);
        using var face = await renderer.CaptureAsync(FrameAt(0, binding), token);
        var viewScene = D3D12ViewScene.Resolve(scene, null, null, null,
            new Dictionary<Guid, CharacterMorphChannels>(), request.EffectiveQuality);
        var expected = D3D12ViewRenderingStyles.Resolve(viewScene, Catalog, [binding],
            [new(binding.ObjectId, 0, new(D3D12CharacterMaterialRole.Face, .35f))]);
        using var direct = new D3D12FrameSession();
        Assert.Equal(direct.Capture(expected.Scene, width, height, surfaceStyles: expected.SurfaceStyles).Rgba, face.Pixels.ToArray());
        var body = D3D12ViewRenderingStyles.Resolve(viewScene, Catalog, [binding]);
        var baseline = direct.Capture(body.Scene, width, height, surfaceStyles: body.SurfaceStyles);
        int changed = 0;
        for (int offset = 0; offset < baseline.Rgba.Length; offset += 4)
        {
            Assert.Equal(baseline.Rgba[offset + 3], face.Pixels.Span[offset + 3]);
            if (baseline.Rgba[offset] == face.Pixels.Span[offset]) continue;
            Assert.True(face.Pixels.Span[offset] > baseline.Rgba[offset]);
            changed++;
        }
        Assert.True(changed > 100);
        // The unselected lower material's complete half-frame must remain byte-identical.
        int bodyRow = Project(scene, new(0, -.6f, 0), width, height).Y;
        int begin = bodyRow < height / 2 ? 0 : height / 2;
        int end = bodyRow < height / 2 ? height / 2 : height;
        for (int y = begin; y < end; y++)
            Assert.Equal(baseline.Rgba.AsSpan(y * width * 4, width * 4).ToArray(), face.Pixels.Span.Slice(y * width * 4, width * 4).ToArray());

        var before = renderer.Statistics!;
        var invalid = binding with { Role = ModelRole.Environment };
        Assert.Throws<ArgumentException>(() => renderer.ValidateRenderingStyles(scene, [invalid]));
        await Assert.ThrowsAsync<ArgumentException>(() => renderer.CaptureAsync(FrameAt(1, invalid), token).AsTask());
        Assert.Same(before, renderer.Statistics);
        using var mmd = await renderer.CaptureAsync(FrameAt(1, binding with { Preset = new("mmd") }), token);
        var mmdScene = D3D12ViewRenderingStyles.Resolve(viewScene, Catalog, [binding with { Preset = new("mmd") }]);
        Assert.Equal(direct.Capture(mmdScene.Scene, width, height, surfaceStyles: mmdScene.SurfaceStyles).Rgba, mmd.Pixels.ToArray());
        Assert.Equal(before.FrameId + 1, renderer.Statistics!.FrameId);
        Assert.Equal(before.TotalSubmissions + 1, renderer.Statistics.TotalSubmissions);
    }

    [Fact, Trait("Category", "Gpu")]
    public async Task ActualHwndPresentConsumesFaceConfigurationWithoutOrdinaryReadbackAndPreflights()
    {
        nint window = CreateWindowExW(0, "STATIC", "MMD Face View test", 0x80000000, 0, 0, 128, 96, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            var scene = Frame();
            var binding = Binding(0, 2);
            var frame = new PreviewFrame(96, 72, 0, scene, null, null, null)
            { RenderingStyles = Array.AsReadOnly(new[] { binding }) };
            var selected = new[] { new D3D12ViewCharacterMaterialBinding(binding.ObjectId, 0, new(D3D12CharacterMaterialRole.Face)) };
            await using var renderer = new D3D12RealtimeViewRenderer(window, new(96, 72), characterMaterials: selected);
            selected[0] = selected[0] with { MaterialIndex = 999 };
            var token = TestContext.Current.CancellationToken;
            renderer.ValidateRenderingStyles(scene, frame.RenderingStyles);
            await renderer.PresentAsync(frame, token);
            var before = renderer.Statistics;
            var invalid = frame with { RenderingStyles = Array.AsReadOnly(new[] { binding with { ObjectId = Guid.NewGuid() } }) };
            Assert.Throws<ArgumentException>(() => renderer.ValidateRenderingStyles(scene, invalid.RenderingStyles));
            await Assert.ThrowsAsync<ArgumentException>(() => renderer.PresentAsync(invalid, token).AsTask());
            Assert.Equal(before.PresentCount, renderer.Statistics.PresentCount);
            Assert.Equal(before.Frame!.FrameId, renderer.Statistics.Frame!.FrameId);
            Assert.Equal(before.Frame.SubmittedFence, renderer.Statistics.Frame.SubmittedFence);
            await renderer.PresentAsync(frame with
            { RenderingStyles = Array.AsReadOnly(new[] { binding with { Preset = new("mmd") } }) }, token);
            Assert.Equal(2, renderer.Statistics.PresentCount);
            Assert.Equal(0, renderer.Statistics.ExplicitCaptureCount);
            Assert.Equal(0, renderer.Statistics.Frame!.TotalReadbacks);
        }
        finally { DestroyWindow(window); }
    }

    private static SceneRenderingStyleBinding Binding(int start, int count, string id = "genshin")
        => new(Guid.NewGuid(), new(id), ModelRole.Character, start, count, true);

    private static OriginalFrameScene Frame()
    {
        const float d = -.4f;
        var normal = new Vector3(d, 0, -MathF.Sqrt(1 - d * d));
        SceneVertex[] Quad(float y) => [new(new(-1, y - .45f, 0), normal, Vector2.Zero),
            new(new(1, y - .45f, 0), normal, Vector2.UnitX), new(new(1, y + .45f, 0), normal, Vector2.One),
            new(new(-1, y + .45f, 0), normal, Vector2.UnitY)];
        var snapshot = new SceneSnapshot([.. Quad(.6f), .. Quad(-.6f)], [0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6],
            [new("same-source.pmx", 0, 6, new(.6f, .4f, .2f, 1), Vector3.Zero, Vector3.Zero, 1),
                new("same-source.pmx", 6, 6, new(.6f, .4f, .2f, 1), Vector3.Zero, Vector3.Zero, 1)],
            camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 3, .1f, 20), lightDirection: Vector3.UnitX);
        return new(new(snapshot, [new(new(.2f)), new(new(.2f))],
            new(Vector3.One, Vector3.Zero, Matrix4x4.Identity, OriginalTextureSampling.PointClamp)),
            options: D3D12OriginalFrameTests.Options(shadow: new(Matrix4x4.CreateScale(.2f), 32, 32,
                false, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual)));
    }

    private static (int X, int Y) Project(OriginalFrameScene frame, Vector3 position, int width, int height)
    {
        var clip = Vector4.Transform(new Vector4(position, 1), frame.Surface.Snapshot.Camera.ViewProjection(width, height));
        return ((int)((clip.X / clip.W * .5f + .5f) * width), (int)((.5f - clip.Y / clip.W * .5f) * height));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extended, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
