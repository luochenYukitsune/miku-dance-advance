using System.Numerics;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace Mmd.Modern.Tests;

[Trait("Category", "Gpu")]
public sealed class D3D12CharacterTemporalTests
{
    [Theory]
    [InlineData(0, .25f, false, 0)]
    [InlineData(128, .25f, false, 128)]
    [InlineData(128, .75f, false, 0)]
    [InlineData(255, .25f, true, 0)]
    public void DiffuseCutoutAndReactiveUseTheActualSampledAlpha(byte alpha, float cutoff, bool depth, int reactive)
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var frame = Frame(textureAlpha: alpha, cutoff: cutoff);
        var output = Capture(runtime, pass, frame, frame);
        Assert.Equal(depth, output.Depth[32 * 64 + 32] < 1);
        Assert.InRange(output.Reactive[32 * 64 + 32], Math.Max(0, reactive - 1), reactive + 1);
        Assert.Equal(0, runtime.Readbacks);
    }

    [Fact]
    public void SphereAlphaParticipatesWhileToonTextureAlphaDoesNot()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var frame = Frame(textureAlpha: 255, sphereAlpha: 128);
        var output = Capture(runtime, pass, frame, frame);
        Assert.Equal(1, output.Depth[32 * 64 + 32]);
        Assert.InRange(output.Reactive[32 * 64 + 32], 127, 129);
        frame = Frame(textureAlpha: 255, toonAlpha: 0);
        output = Capture(runtime, pass, frame, frame);
        Assert.InRange(output.Depth[32 * 64 + 32], .39999f, .40001f);
        Assert.Equal(0, output.Reactive[32 * 64 + 32]);
    }

    [Theory]
    [InlineData(OriginalTextureSampling.PointClamp, true)]
    [InlineData(OriginalTextureSampling.LinearClamp, false)]
    public void SpatialTextureCoverageUsesUvsAndSelectedSampler(OriginalTextureSampling sampling, bool centerOpaque)
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var source = Frame(cutoff: .25f); var s = source.Surface.Snapshot; var vertices = s.Vertices.ToArray();
        vertices[0] = vertices[0] with { Uv = new(0, 0) }; vertices[1] = vertices[1] with { Uv = new(1, 0) };
        vertices[2] = vertices[2] with { Uv = new(.5f, 0) };
        var texture = new SceneTexture("cutout ramp", 2, 1, [255, 255, 255, 0, 255, 255, 255, 255]);
        var snapshot = new SceneSnapshot(vertices, s.Indices, [s.Materials[0] with { ToonTexture = -1 }], [texture], camera: s.Camera);
        var frame = new OriginalFrameScene(new(snapshot, source.Surface.Materials,
            source.Surface.Profile with { TextureSampling = sampling }), options: source.Options);
        var output = Capture(runtime, pass, frame, frame);
        Assert.Equal(centerOpaque, output.Depth[32 * 64 + 32] < 1);
        Assert.Equal(1, output.Depth[50 * 64 + 16]); Assert.Equal(0, output.Reactive[50 * 64 + 16]);
        Assert.InRange(output.Depth[50 * 64 + 48], .39999f, .40001f); Assert.Equal(0, output.Reactive[50 * 64 + 48]);
        if (!centerOpaque) Assert.InRange(output.Reactive[32 * 64 + 32], 130, 136);
    }

    [Fact]
    public void HalfTransparentMovingGeometryHasReactiveCoverageAndNoInventedMotion()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var previous = Frame(alpha: .5f);
        var current = Frame(alpha: .5f, translation: new(.25f, .125f, 0));
        var output = Capture(runtime, pass, current, previous);
        Assert.Equal(1, output.Depth[32 * 64 + 32]);
        Assert.Equal(Vector2.Zero, output.Motion[32 * 64 + 32]);
        Assert.InRange(output.Reactive[32 * 64 + 32], 127, 129);
    }

    [Theory]
    [InlineData(true, .5f, false)]
    [InlineData(true, 0f, false)]
    [InlineData(false, .5f, true)]
    public void TransparentDepthWritesRetainOpaqueInputsAndBlockLaterGeometry(bool writeDepth, float alpha, bool opaqueVisible)
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var baseFrame = Frame(alpha: alpha, cutoff: 0); var s = baseFrame.Surface.Snapshot;
        var vertices = s.Vertices.ToArray().Concat(s.Vertices.ToArray().Select(v => v with { Position = v.Position + Vector3.UnitZ })).ToArray();
        var snapshot = new SceneSnapshot(vertices, new uint[] { 0, 1, 2, 3, 4, 5 },
            [s.Materials[0], s.Materials[0] with { Name = "opaque behind", StartIndex = 3, Diffuse = Vector4.One }], s.Textures, camera: s.Camera);
        var frame = new OriginalFrameScene(new(snapshot, [baseFrame.Surface.Materials[0], baseFrame.Surface.Materials[0]]),
            options: baseFrame.Options! with { WriteDepth = writeDepth });
        var output = Capture(runtime, pass, frame, frame);
        Assert.Equal(opaqueVisible, output.Depth[32 * 64 + 32] < 1);
        Assert.Equal(Vector2.Zero, output.Motion[32 * 64 + 32]);
        Assert.InRange(output.Reactive[32 * 64 + 32], alpha == 0 ? 0 : 127, alpha == 0 ? 0 : 129);
    }

    [Fact]
    public void FullyOccludedTransparentGeometryDoesNotMarkReactive()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var baseFrame = Frame(); var s = baseFrame.Surface.Snapshot;
        var vertices = s.Vertices.ToArray().Concat(s.Vertices.ToArray().Select(v => v with { Position = v.Position + Vector3.UnitZ })).ToArray();
        var snapshot = new SceneSnapshot(vertices, new uint[] { 0, 1, 2, 3, 4, 5 },
            [s.Materials[0], s.Materials[0] with { Name = "transparent behind", StartIndex = 3, Diffuse = new(1, 1, 1, .5f) }], s.Textures, camera: s.Camera);
        var frame = new OriginalFrameScene(new(snapshot, [baseFrame.Surface.Materials[0], baseFrame.Surface.Materials[0]]), options: baseFrame.Options);
        var output = Capture(runtime, pass, frame, frame);
        Assert.InRange(output.Depth[32 * 64 + 32], .39999f, .40001f);
        Assert.Equal(0, output.Reactive[32 * 64 + 32]);
    }

    [Fact]
    public void TransparencyOverrideMatchesTheOriginalOutsideShadowEarlyReturn()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var original = Frame();
        var frame = new OriginalFrameScene(original.Surface, options: original.Options! with { Transp = true });
        var output = Capture(runtime, pass, frame, frame);
        Assert.Equal(1, output.Depth[32 * 64 + 32]); Assert.InRange(output.Reactive[32 * 64 + 32], 127, 129);
        frame = new(original.Surface, options: frame.Options! with { SelfShadow = new(Matrix4x4.CreateTranslation(10, 0, 0),
            32, 32, false, false, OriginalTextureSampling.PointClamp, OriginalDepthComparison.LessEqual) });
        output = Capture(runtime, pass, frame, frame);
        Assert.InRange(output.Depth[32 * 64 + 32], .39999f, .40001f); Assert.Equal(0, output.Reactive[32 * 64 + 32]);
    }

    [Fact]
    public void EditorOverlayMetadataDoesNotEnterCharacterTemporalCoverage()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var baseline = Frame(); var overlay = new ViewportOverlaySnapshot([new(Vector2.Zero, Vector2.One, Vector4.One)]);
        var frame = new OriginalFrameScene(baseline.Surface, options: baseline.Options, editorOverlay: overlay);
        var output = Capture(runtime, pass, frame, frame); var expected = Capture(runtime, pass, baseline, baseline);
        Assert.Equal(expected.Depth, output.Depth); Assert.Equal(expected.Motion, output.Motion); Assert.Equal(expected.Reactive, output.Reactive);
    }

    [Fact]
    public void EffectiveCameraWorldAndPreviousDeformationDetermineMotionWithoutJitter()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var previous = Frame();
        var current = Frame(translation: new(.125f, .125f, 0), world: Matrix4x4.CreateTranslation(.125f, 0, 0));
        var camera = current.Surface.Snapshot.Camera with { Eye = new(.125f, 0, -5), Target = new(.125f, 0, 0), ProjectionJitterPixels = new(.4f, -.3f) };
        current = new(current.Surface, options: current.Options! with { CameraOverride = camera }, materials: current.Materials);
        var output = Capture(runtime, pass, current, previous);
        Assert.InRange(output.Motion[32 * 64 + 32].X, -4.0001f, -3.9999f);
        Assert.InRange(output.Motion[32 * 64 + 32].Y, 3.9999f, 4.0001f);
    }

    [Fact]
    public void SingleSidedCoverageCullsExactlyOneWindingAndDisabledSurfaceIsAbsent()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var forward = Frame(doubleSided: false); var reverse = Frame(doubleSided: false, reversed: true);
        var a = Capture(runtime, pass, forward, forward); var b = Capture(runtime, pass, reverse, reverse);
        Assert.NotEqual(a.Depth[32 * 64 + 32] < 1, b.Depth[32 * 64 + 32] < 1);
        var disabled = new OriginalFrameScene(forward.Surface, options: forward.Options,
            materials: [new(false, false, false, SurfaceEnabled: false)]);
        var output = Capture(runtime, pass, disabled, disabled);
        Assert.All(output.Depth, v => Assert.Equal(1, v));
    }

    [Fact]
    public void CullTransparentSurfaceFlagUsesResolvedHostAlphaForBothWindings()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        foreach (bool reverse in new[] { false, true })
        {
            var source = Frame(alpha: .5f, doubleSided: false, reversed: reverse);
            var frame = new OriginalFrameScene(source.Surface, options: source.Options,
                materials: [new(false, false, false, CullTransparentSurface: true)]);
            var output = Capture(runtime, pass, frame, frame);
            Assert.InRange(output.Reactive[32 * 64 + 32], 127, 129);
            Assert.Equal(1, output.Depth[32 * 64 + 32]);
        }
    }

    [Fact]
    public void OutlineUsesActualExpandedStreamAndItsPreviousPositionForMotion()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var previous = OutlineFrame(0); var current = OutlineFrame(.125f);
        var output = Capture(runtime, pass, current, previous);
        int covered = 0;
        for (int i = 0; i < output.Depth.Length; i++) if (output.Depth[i] < 1)
        {
            covered++; Assert.InRange(output.Motion[i].X, -4.0001f, -3.9999f);
        }
        Assert.True(covered > 300);
    }

    [Fact]
    public void AnimatedOutlineWidthChangesCoverageAndProducesExpandedPositionMotion()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var expanded = OutlineFrame(0);
        var collapsed = new OriginalFrameScene(expanded.Surface,
            new OriginalOutlineData(expanded.Outline!.Vertices, [expanded.Outline.Materials[0] with { Width = 0 }]),
            expanded.OutlineOptions, expanded.Options, expanded.Materials);
        var a = Capture(runtime, pass, expanded, collapsed); var b = Capture(runtime, pass, collapsed, collapsed);
        Assert.True(a.Depth.Count(v => v < 1) > b.Depth.Count(v => v < 1) + 40);
        Assert.True(a.Motion.Count(v => v.LengthSquared() > .001f) > 300);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectedGroundUsesExpandedGeometryAndRealBlendAndStencilCoverage(bool blended)
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        OriginalFrameScene Ground(float x)
        {
            var frame = Frame(translation: new(x, 0, 0)); var s = frame.Surface.Snapshot;
            s = s.WithCamera(s.Camera with { Eye = new(0, 5, -5), OrthographicHeight = 4 });
            var outline = new OriginalOutlineData(s.Vertices.ToArray().Select(v => new OriginalOutlineVertex(v.Position,
                Vector3.Zero, OriginalOutlineVertexMode.Expand, 1, 0)), [new(1, Vector4.One)]);
            return new(new(s, frame.Surface.Materials), outline, new(Vector4.Zero, Matrix4x4.Identity, new(0, 5, -5), 0, 0, false),
                frame.Options! with { GroundShadow = new(new(1, -1, 1), new(0, 0, 0, .5f), blended, true) },
                [new(false, false, true, SurfaceEnabled: false)]);
        }
        var current = Ground(.125f); var previous = Ground(0);
        var output = Capture(runtime, pass, current, previous);
        if (blended)
        {
            Assert.All(output.Depth, v => Assert.Equal(1, v)); Assert.All(output.Motion, v => Assert.Equal(Vector2.Zero, v));
            Assert.True(output.Reactive.Count(v => v > 0) > 30);
        }
        else
        {
            int covered = 0;
            for (int i = 0; i < output.Depth.Length; i++) if (output.Depth[i] < 1)
            { covered++; Assert.InRange(output.Motion[i].X, -2.0001f, -1.9999f); }
            Assert.True(covered > 30); Assert.All(output.Reactive, v => Assert.Equal(0, v));
        }
    }

    [Fact]
    public void CharacterTargetsRetireAcrossResizeAbortAndPipelineReuse()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var frame = Frame(textureAlpha: 128);
        foreach (var size in new[] { 65, 97, 1, 65, 49 })
        {
            var context = runtime.Begin(); var inputs = pass.Record(context, frame, frame, size, size);
            Assert.NotNull(inputs.ReactiveMask); Assert.Equal(Format.R8_UNorm, inputs.ReactiveMask!.Description.Format);
            runtime.Submit(); Assert.InRange(runtime.LiveFrameResources, 1, 140);
        }
        long pipelines = runtime.PipelineCreations;
        pass.Record(runtime.Begin(), frame, frame, 64, 64); runtime.Abort();
        pass.Record(runtime.Begin(), frame, frame, 64, 64); runtime.Wait(runtime.Submit());
        Assert.Equal(pipelines, runtime.PipelineCreations); Assert.Equal(0, runtime.Readbacks);
        runtime.WaitForIdle(); Assert.InRange(runtime.LiveFrameResources, 1, 3);
    }

    [Fact]
    public void UnusableHistoryAndTransparentBackgroundRejectBeforeFrameAllocationsThenRecover()
    {
        using var runtime = new D3D12GpuRuntime(); using var pass = new D3D12TemporalInputPass(runtime);
        var frame = Frame(); var context = runtime.Begin(); var reversed = Frame(reversed: true);
        Assert.Throws<NotSupportedException>(() => pass.Record(context, frame, reversed, 64, 64));
        var transparent = new OriginalFrameScene(frame.Surface, options: frame.Options! with { BackgroundColor = new(0, 0, 0, .5f) });
        Assert.Throws<NotSupportedException>(() => pass.Record(context, transparent, transparent, 64, 64));
        Assert.Equal(0, runtime.FrameOwnedAllocations);
        pass.Record(context, frame, frame, 64, 64); runtime.Wait(runtime.Submit());
    }

    private static OriginalFrameScene Frame(float alpha = 1, byte textureAlpha = 255, byte? sphereAlpha = null,
        byte toonAlpha = 255, float cutoff = .01f, Vector3 translation = default, Matrix4x4? world = null,
        bool doubleSided = true, bool reversed = false)
    {
        SceneVertex[] vertices = [new(new Vector3(-.8f, -.8f, 0) + translation, -Vector3.UnitZ, Vector2.Zero),
            new(new Vector3(.8f, -.8f, 0) + translation, -Vector3.UnitZ, Vector2.Zero),
            new(new Vector3(0, .8f, 0) + translation, -Vector3.UnitZ, Vector2.Zero)];
        SceneTexture[] textures = [new("diffuse", 1, 1, [255, 255, 255, textureAlpha]),
            new("toon", 1, 1, [255, 255, 255, toonAlpha]), new("sphere", 1, 1, [255, 255, 255, sphereAlpha ?? 255])];
        var snapshot = new SceneSnapshot(vertices, reversed ? new uint[] { 0, 2, 1 } : [0, 1, 2],
            [new("surface", 0, 3, new(1, 1, 1, alpha), Vector3.Zero, Vector3.One, 1, DiffuseTexture: 0, ToonTexture: 1, AlphaCutoff: cutoff)],
            textures, world: world, camera: new(new(0, 0, -5), Vector3.Zero, Vector3.UnitY, 2, 1, 11, LeftHanded: true));
        return new(new(snapshot, [new(Vector3.One, sphereAlpha is null ? -1 : 2,
            sphereAlpha is null ? OriginalSphereMode.None : OriginalSphereMode.Multiply, doubleSided)]),
            options: new(OriginalAlphaBlendMode.Normal, true, false));
    }

    private static OriginalFrameScene OutlineFrame(float x)
    {
        var frame = Frame(translation: new(x, 0, 0));
        var vertices = frame.Surface.Snapshot.Vertices.ToArray().Select(v => new OriginalOutlineVertex(v.Position,
            Vector3.Normalize(new(v.Position.X - x, v.Position.Y, 0)), OriginalOutlineVertexMode.Expand, 1, 0));
        var data = new OriginalOutlineData(vertices, [new(200, Vector4.One)]);
        var options = new OriginalOutlineOptions(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -5), 0, 1);
        // Draw both windings so front-face culling leaves precisely the visible shell.
        var s = frame.Surface.Snapshot;
        var snapshot = new SceneSnapshot(s.Vertices, new uint[] { 0, 1, 2, 0, 2, 1 },
            [s.Materials[0] with { IndexCount = 6 }], s.Textures, camera: s.Camera);
        return new(new(snapshot, frame.Surface.Materials), data, options, frame.Options,
            [new(false, false, false, SurfaceEnabled: false)]);
    }

    private static (float[] Depth, Vector2[] Motion, byte[] Reactive) Capture(D3D12GpuRuntime runtime,
        D3D12TemporalInputPass pass, OriginalFrameScene current, OriginalFrameScene previous)
    {
        const int width = 64, height = 64;
        var context = runtime.Begin();
        try
        {
            var inputs = pass.Record(context, current, previous, width, height);
            var depth = Copy(inputs.Depth); var motion = Copy(inputs.MotionVectors); var reactive = Copy(inputs.ReactiveMask!);
            runtime.Wait(runtime.Submit());
            return (Read<float>(depth, 4), Read<Vector2>(motion, 8), Read<byte>(reactive, 1));
            (ID3D12Resource Resource, PlacedSubresourceFootPrint Footprint, int Bytes) Copy(ID3D12Resource source)
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
                var output = new T[width * height]; var data = source.Resource.Map<byte>(0, source.Bytes);
                try { for (int y = 0; y < height; y++) MemoryMarshal.Cast<byte, T>(data.Slice(y * source.Footprint.Footprint.RowPitch,
                    width * stride)).CopyTo(output.AsSpan(y * width, width)); }
                finally { source.Resource.Unmap(0); } return output;
            }
        }
        finally { runtime.Abort(); }
    }
}
