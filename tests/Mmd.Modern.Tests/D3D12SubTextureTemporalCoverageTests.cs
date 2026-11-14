using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12SubTextureTemporalCoverageTests
{
    private const int Size = 64, Center = 32 * Size + 32;

    [Theory]
    [InlineData(0f, false, false, false, false)]
    [InlineData(.5f, false, false, false, false)]
    [InlineData(1f, false, false, false, false)]
    [InlineData(0f, true, false, false, false)]
    [InlineData(.5f, true, true, false, false)]
    [InlineData(0f, false, true, false, false)]
    [InlineData(0f, false, true, true, false)]
    [InlineData(0f, false, false, true, true)]
    public void ActualTwoMapAlphaUsesOriginalLightToonWeightProjectionAndTransp(float c, bool noDiffuse,
        bool inheritedAdd, bool transp, bool outside)
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var source = D3D12SubTextureTests.Fixture(new(.25f), c, 128 / 255f, .2f, noDiffuse, transp, inheritedAdd);
        var options = source.Options! with { BackgroundColor = Vector4.One };
        if (outside) options = options with { SelfShadow = new(Matrix4x4.CreateTranslation(10, 0, 0), 8, 8,
            false, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual) };
        var frame = Copy(source, options: options);
        var actual = Capture(runtime, pass, frame, frame);
        float raw = (noDiffuse ? 1 : 128 / 255f) * (128 / 255f);
        float alpha = outside ? raw : transp ? .5f : raw * (noDiffuse || c >= 1 ? 1 : c + .2f * (1 - c));
        Assert.Equal(1, actual.Depth[Center]); Assert.Equal(Vector2.Zero, actual.Motion[Center]);
        Assert.InRange(actual.Reactive[Center], (int)MathF.Round(alpha * 255) - 1, (int)MathF.Round(alpha * 255) + 1);
        if (!outside && !transp)
        {
            // The null-options surface is a different colour pipeline with opaque blending.
            // Alpha still clips, but surviving fragments contribute opaque depth and zero reactive.
            var simple = new OriginalFrameScene(frame.Surface);
            var slice = Capture(runtime, pass, simple, simple);
            Assert.True(slice.Depth[Center] < 1); Assert.Equal(0, slice.Reactive[Center]);
        }
        Assert.Equal(0, runtime.Readbacks);
    }

    [Theory]
    [InlineData(.25f, 0)]
    [InlineData(.75f, 255)]
    public void Uv1SelectsRawSphereAlphaAndRgbTextureFactorsNeverChangeCoverage(float u, int expected)
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var frame = Fixture(new(u, .5f)); var s = frame.Surface.Snapshot;
        var textures = s.Textures.ToArray(); textures[1] = new("UV1 alpha", 2, 1, [255, 255, 255, 0, 255, 255, 255, 255]);
        var snapshot = new SceneSnapshot(s.Vertices, s.Indices, s.Materials, textures, s.World, s.Camera, s.LightDirection, additionalUvs: s.AdditionalUvs);
        frame = Copy(frame, new(snapshot, frame.Surface.Materials, frame.Surface.Profile));
        var a = Capture(runtime, pass, frame, frame);
        var binding = frame.Surface.Materials[0] with { TextureFactors = new(new(new(.2f), new(.6f)),
            new(new(.3f), new(.1f)), OriginalTextureFactor.Identity) };
        var changed = Copy(frame, new(snapshot, [binding], frame.Surface.Profile));
        var b = Capture(runtime, pass, changed, changed);
        Assert.Equal(expected == 255, a.Depth[Center] < 1); Assert.Equal(0, a.Reactive[Center]);
        Assert.Equal(a.Depth, b.Depth); Assert.Equal(a.Motion, b.Motion); Assert.Equal(a.Reactive, b.Reactive);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CurrentGeneratedPrimaryShadowDeterminesDepthMotionAndReactive(bool noDiffuse, bool inheritedAdd)
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var source = D3D12SubTextureGiReceiverTests.Fixture(noDiffuse, inheritedAdd, true);
        var shadowed = Copy(source, options: source.Options! with { BackgroundColor = Vector4.One });
        var lit = Copy(shadowed, materials: shadowed.Materials.Select((f, i) => i == 0 ? f with { ReceiveSelfShadow = false } : f));
        var a = Capture(runtime, pass, shadowed, shadowed, prepareShadow: true);
        var b = Capture(runtime, pass, lit, lit, prepareShadow: true);
        Assert.Equal(noDiffuse, a.Depth[Center] < 1); Assert.Equal(Vector2.Zero, a.Motion[Center]);
        Assert.InRange(a.Reactive[Center], noDiffuse ? 0 : 50, noDiffuse ? 0 : 52);
        Assert.True(b.Depth[Center] < 1); Assert.Equal(0, b.Reactive[Center]);
        Assert.Equal(0, runtime.Readbacks);
    }

    [Fact]
    public void MissingPreparedGeneratedShadowRejectsBeforeAllocations()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var source = D3D12SubTextureGiReceiverTests.Fixture(false, false, true);
        var frame = Copy(source, options: source.Options! with { BackgroundColor = Vector4.One });
        var context = runtime.Begin();
        Assert.Throws<InvalidOperationException>(() => pass.Record(context, frame, frame, Size, Size));
        Assert.Equal(0, runtime.FrameOwnedAllocations); runtime.Abort();
    }

    [Fact]
    public void ModernOutlineKeepsOriginalSurfaceAlphaAttributesAndExpandedMotion()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var source = Fixture(new(.25f), c: 0); var s = source.Surface.Snapshot;
        var snapshot = new SceneSnapshot(s.Vertices, s.Indices.ToArray().Concat(s.Indices.ToArray().Reverse()).ToArray(),
            [s.Materials[0] with { IndexCount = 12 }], s.Textures, s.World, s.Camera, s.LightDirection, additionalUvs: s.AdditionalUvs);
        var outline = new OriginalOutlineData(s.Vertices.ToArray().Select(v => new OriginalOutlineVertex(v.Position,
            Vector3.Normalize(new(v.Position.X, v.Position.Y, 0)), OriginalOutlineVertexMode.Expand, 1, 0)), [new(200, Vector4.One)]);
        var wide = new OriginalFrameScene(new(snapshot, source.Surface.Materials, source.Surface.Profile), outline,
            new(Vector4.Zero, Matrix4x4.Identity, s.Camera.Eye, 0, 1000), source.Options! with {
                SelfShadow = new(Matrix4x4.Identity, 8, 8, false, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual) },
            [new(false, false, false, SurfaceEnabled: false) { CharacterOutlineStyle = new(2, new(1, 0, 0, .5f)) }]);
        var thin = Copy(wide, materials: [wide.Materials[0] with { CharacterOutlineStyle = new(.5f, new(1, 0, 0, .5f)) }]);
        var actual = Capture(runtime, pass, wide, thin);
        // Raw S.a=1, c=0 and Toon W=.2; the final modern outline alpha multiplies by .5.
        Assert.True(actual.Reactive.Count(v => v >= 25 && v <= 27) > 100);
        Assert.All(actual.Depth, v => Assert.Equal(1, v));
        Assert.All(actual.Motion, v => Assert.Equal(Vector2.Zero, v));
        var opaque = Copy(wide, materials: [wide.Materials[0] with { CharacterOutlineStyle = new(2, new(1, 0, 0, 1)) }]);
        var bindings = opaque.Surface.Materials.Select(b => b with { TextureFactors = OriginalMaterialTextureFactors.Identity });
        opaque = Copy(opaque, new(snapshot, bindings, source.Surface.Profile));
        var moving = Capture(runtime, pass, opaque, Copy(thin, opaque.Surface));
        Assert.True(moving.Motion.Count(v => v.LengthSquared() > .001f) > 100);
    }

    [Fact]
    public void OpaqueUv1SurfaceUsesUnjitteredBackwardMotionAndBlendedSourceOrderRetainsVisibilityDepth()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var source = Fixture(new(.25f)); var s = source.Surface.Snapshot;
        var camera = s.Camera with { LeftHanded = true, ProjectionJitterPixels = new(.4f, -.3f) };
        var old = Copy(source, new(s.WithCamera(camera with { ProjectionJitterPixels = new(-.2f, .1f) }), source.Surface.Materials, source.Surface.Profile));
        var translated = new SceneSnapshot(s.Vertices, s.Indices, s.Materials, s.Textures, Matrix4x4.CreateTranslation(.25f, .125f, 0),
            camera, s.LightDirection, additionalUvs: s.AdditionalUvs);
        var current = Copy(source, new(translated, source.Surface.Materials, source.Surface.Profile));
        var moving = Capture(runtime, pass, current, old);
        Assert.True(moving.Depth[Center] < 1);
        Assert.InRange(moving.Motion[Center].X, -4.0001f, -3.9999f);
        Assert.InRange(moving.Motion[Center].Y, 1.9999f, 2.0001f);
        Assert.Equal(0, moving.Reactive[Center]);
        // A translucent first draw writes real visibility depth and blocks a later opaque
        // layer, while its MRT selector retains the clear opaque inputs.
        var vertices = s.Vertices.ToArray().Concat(s.Vertices.ToArray().Select(v => v with { Position = v.Position + Vector3.UnitZ })).ToArray();
        var uv = new SceneAdditionalUvs(1, Enumerable.Repeat(new SceneAdditionalUvVertex(new(.25f), default, default, default), vertices.Length).ToArray());
        var snapshot = new SceneSnapshot(vertices, new uint[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 },
            [s.Materials[0] with { Diffuse = new(1, 1, 1, .5f) }, s.Materials[0] with { StartIndex = 6 }], s.Textures,
            camera: s.Camera with { LeftHanded = true }, lightDirection: s.LightDirection, additionalUvs: uv);
        var layered = new OriginalFrameScene(new(snapshot, [source.Surface.Materials[0], source.Surface.Materials[0]], source.Surface.Profile), options: source.Options);
        var covered = Capture(runtime, pass, layered, layered);
        Assert.Equal(1, covered.Depth[Center]); Assert.Equal(Vector2.Zero, covered.Motion[Center]);
        Assert.InRange(covered.Reactive[Center], 127, 129);
        var depthOff = Copy(layered, options: layered.Options! with { WriteDepth = false });
        Assert.True(Capture(runtime, pass, depthOff, depthOff).Depth[Center] < 1);
    }

    internal static OriginalFrameScene Fixture(Vector2 uv, float c = 1)
    {
        var source = D3D12SubTextureTests.Fixture(uv, c, 1, .2f);
        return Copy(source, options: source.Options! with { BackgroundColor = Vector4.One });
    }

    internal static OriginalFrameScene Copy(OriginalFrameScene frame, OriginalStyleScene? surface = null,
        OriginalFrameOptions? options = null, IEnumerable<OriginalFrameMaterialFlags>? materials = null)
        => new(surface ?? frame.Surface, frame.Outline, frame.OutlineOptions, options ?? frame.Options,
            materials ?? frame.Materials, frame.Lighting, frame.EditorOverlay);

    private static (float[] Depth, Vector2[] Motion, byte[] Reactive) Capture(D3D12GpuRuntime runtime,
        D3D12TemporalInputPass pass, OriginalFrameScene current, OriginalFrameScene previous, bool prepareShadow = false)
    {
        using var cache = new D3D12GpuCache(runtime);
        using var renderer = prepareShadow ? new D3D12OriginalStyleRenderer(runtime) : null;
        var context = runtime.Begin();
        try
        {
            D3D12OriginalStyleRenderer.PreparedFrame? prepared = null;
            if (prepareShadow)
            {
                var s = current.Surface.Snapshot;
                var vb = cache.FrameBuffer(MemoryMarshal.AsBytes(s.Vertices), ResourceStates.VertexAndConstantBuffer);
                var ib = cache.FrameBuffer(MemoryMarshal.AsBytes(s.Indices), ResourceStates.IndexBuffer);
                Assert.Equal(1, renderer!.PrepareGiFrame(context, current, Format.D32_Float, vb, ib, cache, ref prepared));
            }
            var inputs = pass.Record(context, current, previous, Size, Size, prepared);
            var depth = CopyTarget(inputs.Depth); var motion = CopyTarget(inputs.MotionVectors); var reactive = CopyTarget(inputs.ReactiveMask!);
            runtime.Wait(runtime.Submit());
            return (Read<float>(depth, 4), Read<Vector2>(motion, 8), Read<byte>(reactive, 1));
            (ID3D12Resource Resource, PlacedSubresourceFootPrint Footprint, int Bytes) CopyTarget(ID3D12Resource source)
            {
                var layout = new PlacedSubresourceFootPrint[1];
                runtime.Device.GetCopyableFootprints(source.Description, 0, 1, 0, layout, new int[1], new ulong[1], out ulong size);
                var buffer = context.Own(runtime.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size), ResourceStates.CopyDest));
                context.Commands.ResourceBarrierTransition(source, D3D12TemporalInputs.ShaderReadState, ResourceStates.CopySource);
                context.Commands.CopyTextureRegion(new TextureCopyLocation(buffer, layout[0]), 0, 0, 0, new TextureCopyLocation(source, 0));
                return (buffer, layout[0], checked((int)size));
            }
            T[] Read<T>((ID3D12Resource Resource, PlacedSubresourceFootPrint Footprint, int Bytes) source, int stride) where T : unmanaged
            {
                var output = new T[Size * Size]; var data = source.Resource.Map<byte>(0, source.Bytes);
                try { for (int y = 0; y < Size; y++) MemoryMarshal.Cast<byte, T>(data.Slice(y * source.Footprint.Footprint.RowPitch,
                    Size * stride)).CopyTo(output.AsSpan(y * Size, Size)); }
                finally { source.Resource.Unmap(0); } return output;
            }
        }
        finally { runtime.Abort(); }
    }
}
