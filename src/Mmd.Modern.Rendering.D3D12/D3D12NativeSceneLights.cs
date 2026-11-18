using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Mmd.Modern.Rendering.D3D12;

/// <summary>Executed direct-light work, with the recorded secondary-GI participation reported separately.
/// ShadowDrawCalls are recorded draws. Every eligible caster draw of every face (ShadowCasterDraws) is either
/// recorded, rejected by the face frustum, or covered by a face whose identical previous result was reused.</summary>
public sealed record D3D12NativeLightStatistics(int EnabledLights, int ShadowFaces, int ShadowDrawCalls,
    int ShadowTileSize, bool NativeLightsContributeToGi = false)
{
    public int ShadowCasterDraws { get; init; }
    public int ShadowCulledDraws { get; init; }
    public int ShadowReusedFaces { get; init; }
    public int ShadowReusedDraws { get; init; }
}

internal static class NativeLightShadowProjection
{
    internal static Vector3 Direction(NativeSceneLight light) => Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, light.Rotation));

    /// <summary>World-position to light clip; model World must be applied exactly once by the vertex shader.</summary>
    internal static Matrix4x4[] Create(SceneSnapshot scene, NativeSceneLight light)
    {
        if (light.Kind == SceneLightKind.Area)
            return Enumerable.Range(0, NativeAreaLight.Samples).SelectMany(sample => Create(scene,
                light with { Kind = SceneLightKind.Point, Position = NativeAreaLight.SamplePosition(light, sample) })).ToArray();
        if (light.Kind == SceneLightKind.Directional)
        {
            Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
            if (!scene.HasDeferredDeformation || scene.Indices.IsEmpty)
            {
                foreach (uint index in scene.Indices)
                {
                    var p = Vector3.Transform(scene.GetVertex((int)index).Position, scene.World);
                    minimum = Vector3.Min(minimum, p); maximum = Vector3.Max(maximum, p);
                }
            }
            else
            {
                ModernDirectionalShadowProjection.ComputeDeferredWorldBounds(scene, ref minimum, ref maximum);
            }
            Vector3 center = minimum * .5f + maximum * .5f;
            float radius = MathF.Max((maximum - minimum).Length() * .53f, .1f);
            if (!float.IsFinite(radius)) throw new ArgumentException("Scene bounds exceed the native shadow projection range.", nameof(scene));
            var direction = Direction(light);
            var view = Matrix4x4.CreateLookAt(center - direction * (radius + 1), center, Up(direction));
            return RequireFinite([view * Matrix4x4.CreateOrthographic(2 * radius, 2 * radius, .01f, 2 * radius + 2)]);
        }
        float near = MathF.Max(light.Range * .001f, float.Epsilon);
        if (light.Kind == SceneLightKind.Spot)
        {
            var direction = Direction(light);
            float angle = MathF.Min(2 * light.OuterConeRadians, MathF.PI - .001f);
            return RequireFinite([Matrix4x4.CreateLookAt(light.Position, light.Position + direction, Up(direction)) *
                Matrix4x4.CreatePerspectiveFieldOfView(angle, 1, near, light.Range)]);
        }
        Vector3[] directions = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, near, light.Range);
        return RequireFinite(directions.Select(direction => Matrix4x4.CreateLookAt(light.Position, light.Position + direction, Up(direction)) * projection).ToArray());
    }

    private static Matrix4x4[] RequireFinite(Matrix4x4[] matrices)
    {
        // Finite positions can still lose a unit look-direction at large magnitudes;
        // LookAt then normalizes zero and produces NaNs. Never upload those matrices.
        foreach (var matrix in matrices)
            if (!float.IsFinite(matrix.M11) || !float.IsFinite(matrix.M12) || !float.IsFinite(matrix.M13) || !float.IsFinite(matrix.M14)
                || !float.IsFinite(matrix.M21) || !float.IsFinite(matrix.M22) || !float.IsFinite(matrix.M23) || !float.IsFinite(matrix.M24)
                || !float.IsFinite(matrix.M31) || !float.IsFinite(matrix.M32) || !float.IsFinite(matrix.M33) || !float.IsFinite(matrix.M34)
                || !float.IsFinite(matrix.M41) || !float.IsFinite(matrix.M42) || !float.IsFinite(matrix.M43) || !float.IsFinite(matrix.M44))
                throw new ArgumentException("Native light shadow projection exceeds the supported finite coordinate range.");
        return matrices;
    }

    private static Vector3 Up(Vector3 direction) => MathF.Abs(direction.Y) < .99f ? Vector3.UnitY : Vector3.UnitZ;
}

public sealed partial class D3D12OriginalStyleRenderer
{
    internal const int NativeShadowTileSize = NativeLightShadowAtlasLayout.TileSize;
    public D3D12NativeLightStatistics? LastNativeLightStatistics { get; private set; }
    internal void ResetNativeLightStatistics() => LastNativeLightStatistics = null;
    internal void SetNativeGiParticipation(bool active)
    {
        if(LastNativeLightStatistics is { } statistics)
            LastNativeLightStatistics=statistics with {NativeLightsContributeToGi=active};
    }
    internal static bool HasNativeLights(OriginalFrameScene frame) => frame.Lighting is { } lighting &&
        (lighting.Settings.EnvironmentStrength > 0 || lighting.EffectiveLights.Any(l => l.Enabled && l.Intensity > 0 && l.Color != Vector3.Zero));

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeLightConstants(Vector4 PositionKind, Vector4 DirectionRange, Vector4 ColorIntensity, Vector4 ConeShadow, Vector4 AreaRightTile);
    [InlineArray(SceneLightingSnapshot.MaxActiveLights)]
    internal struct NativeLightArray { private NativeLightConstants first; }
    [InlineArray(SceneLightingSnapshot.MaxActiveLights * 6 * NativeAreaLight.Samples)]
    internal struct NativeMatrixArray { private Matrix4x4 first; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFrameConstants
    {
        internal Vector4 Info;
        internal NativeLightArray Lights;
        internal NativeMatrixArray Matrices;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeMaterialInfo(uint Count, uint ReceiverMask, uint SceneLighting, uint ReceiveShadows);
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeMaterialConstants(NativeMaterialInfo Info, Vector4 Diffuse);
    internal sealed record PreparedNativeLights(ID3D12Resource Atlas, int Count, ulong FrameAddress, uint[] ReceiverMasks);

    private PreparedNativeLights RecordNativeLights(D3D12RecordingContext context, D3D12GpuCache cache, FramePipelines pipeline,
        OriginalFrameScene frame, ID3D12Resource vertexBuffer, ID3D12Resource indexBuffer,
        ID3D12DescriptorHeap srvHeap, int srvStride, int slotBase, ID3D12Resource white, FrameConstants common)
    {
        var scene = frame.Surface.Snapshot;
        var lights = frame.Lighting!.EffectiveLights.Where(NativeLightShadowAtlasLayout.Contributes).ToArray();
        var layout = NativeLightShadowAtlasLayout.Create(frame.Lighting);
        var constants = new NativeFrameConstants { Info = new(layout.Columns, layout.Rows, frame.Lighting.Settings.EnvironmentStrength, 0) };
        var projections = new Matrix4x4[lights.Length][];
        int faceCount = 0, atlasTile = 0;
        for (int i = 0; i < lights.Length; i++)
        {
            var light = lights[i];
            NativeAreaLight.Validate(light);
            projections[i] = light.CastShadows ? NativeLightShadowProjection.Create(scene, light) : [];
            float worldBias = 0;
            if (light.Kind == SceneLightKind.Directional && projections[i].Length > 0)
                worldBias = .00015f / MathF.Sqrt(projections[i][0].M11 * projections[i][0].M11 + projections[i][0].M21 * projections[i][0].M21 + projections[i][0].M31 * projections[i][0].M31);
            constants.Lights[i] = new(new(light.Position, (float)light.Kind), new(NativeLightShadowProjection.Direction(light), light.Range),
                new(light.Color, SceneLightFalloff.RenderIntensity(light)), NativeAreaLight.Cone(light, projections[i].Length > 0 ? faceCount : -1, worldBias),
                new(light.Kind == SceneLightKind.Area ? NativeAreaLight.Right(light) : Vector3.Zero, atlasTile));
            for (int face = 0; face < projections[i].Length; face++) constants.Matrices[faceCount++] = projections[i][face];
            atlasTile += light.Kind == SceneLightKind.Area ? projections[i].Length / NativeAreaLight.Samples : projections[i].Length;
        }
        int draws = 0, casters = 0, culled = 0, reusedFaces = 0, reusedDraws = 0;
        ID3D12Resource atlas = white;
        nativeShadowDiagnostics = null;
        if (faceCount > 0)
        {
            int atlasWidth = layout.Width, atlasHeight = layout.Height;
            var targets=D3D12ShadowTargets.Get(context,cache,"original-native-shadow",atlasWidth,atlasHeight,Format.D32_Float);
            atlas=targets.Color;
            // A face is reused only from this exact resource, written by a submitted recording of identical inputs.
            var faceCache=D3D12NativeShadowFaceCache.For(targets);
            bool full=!NativeShadowReuse||!faceCache.Valid;
            faceCache.Valid=false;
            context.Runtime.OnAbort(()=>{faceCache.Valid=false;faceCache.Faces=[];nativeShadowDiagnostics=null;});
            var versions=nativeShadowCasterVersions.Update(scene);
            bool subTexture=HasSubTexture(frame);
            object? additionalUvs=subTexture?scene.AdditionalUvs:null;
            nativeShadowCasters.Clear();
            for (int m = 0; m < scene.Materials.Count; m++)
            {
                var material = scene.Materials[m]; var flags = frame.Materials[m]; var binding = frame.Surface.Materials[m];
                if (!flags.CastSelfShadow || material.IndexCount == 0) continue;
                var host = OriginalStyleMath.ResolveHostColors(material, frame.Surface.Profile);
                if (host.EgColor.W < .5f) continue;
                bool shadowBoth=flags.ShadowDoubleSided||(flags.UseSceneLighting&&binding.DoubleSided);
                float mode = SurfaceMode(binding);
                // Sphere rotation is read only by normal-projected sphere modes (Multiply/Add); SubTexture reads UV1.
                Matrix4x4 sphereRotation = mode is > 0 and < 3 ? common.SphereRotation : default;
                nativeShadowCasters.Add(new(
                    new(m, material.StartIndex, material.IndexCount, shadowBoth ? 1 : 0, versions[m],
                        sphereRotation, host.EgColor, new(material.AlphaCutoff, mode, material.DiffuseTexture >= 0 ? 1 : 0, 0)),
                    shadowBoth ? pipeline.NativeShadowBoth! : pipeline.NativeShadowBack!,
                    material.DiffuseTexture < 0 ? null : scene.Textures[material.DiffuseTexture],
                    mode > 0 && binding.SphereTexture >= 0 ? scene.Textures[binding.SphereTexture] : null));
            }
            // The vertex shader applies World then the face matrix, as the two-multiply visibility test assumes.
            var bounds=nativeShadowCasters.Count>0&&NativeShadowCulling&&frame.Options?.EnableFrustumCulling==true?(activeSurfaceBounds??=GetSurfaceVisibilityBounds(scene)):null;
            var faces=new Dictionary<D3D12NativeShadowFaceCache.Rect,D3D12NativeShadowFaceCache.Face>();
            var misses=new List<(D3D12NativeShadowFaceCache.Rect Rect,D3D12NativeShadowFaceCache.Face Face)>();
            bool anyMissDraws = false;
            int tile = 0;
            for (int i = 0; i < lights.Length; i++)
            {
                for (int face = 0; face < projections[i].Length; face++)
                {
                    bool area = lights[i].Kind == SceneLightKind.Area;
                    int cell = area ? tile + face % 6 : tile + face;
                    var (x, y) = layout.TileOrigin(cell);
                    int size = area ? NativeShadowTileSize / 2 : NativeShadowTileSize;
                    if (area) { int sample = face / 6; x += (sample & 1) * size; y += (sample >> 1) * size; }
                    var projection = projections[i][face];
                    nativeShadowDraws.Clear(); nativeShadowReferences.Clear();
                    foreach (var caster in nativeShadowCasters)
                    {
                        casters++;
                        // A box outside one clip plane rasterizes nothing inside this face's viewport and scissor.
                        if (bounds is not null && !OriginalFrustumVisibility.IsPotentiallyVisible(bounds[caster.Draw.Material], scene.World, projection)) { culled++; continue; }
                        nativeShadowDraws.Add(caster.Draw);
                        nativeShadowReferences.Add(caster.Pipeline);
                        nativeShadowReferences.Add(caster.DiffuseTexture);
                        nativeShadowReferences.Add(caster.SphereTexture);
                    }
                    var rect = new D3D12NativeShadowFaceCache.Rect(x, y, size);
                    var faceDraws = CollectionsMarshal.AsSpan(nativeShadowDraws); var faceReferences = CollectionsMarshal.AsSpan(nativeShadowReferences);
                    if (!full && faceCache.Faces.TryGetValue(rect, out var previous) && previous.Matches(projection, scene.World, additionalUvs, faceDraws, faceReferences))
                    { faces.Add(rect, previous); reusedFaces++; reusedDraws += faceDraws.Length; continue; }
                    var recorded = new D3D12NativeShadowFaceCache.Face(projection, scene.World, additionalUvs, faceDraws.ToArray(), faceReferences.ToArray());
                    faces.Add(rect, recorded); misses.Add((rect, recorded));
                    if (recorded.Draws.Length > 0) anyMissDraws = true;
                }
                tile += lights[i].Kind == SceneLightKind.Area ? projections[i].Length / NativeAreaLight.Samples : projections[i].Length;
            }
            // Drawn rectangles that are not reused return to the clear value; the rest of the atlas already holds it.
            var clears = new List<RawRect>();
            if (!full)
                foreach (var (rect, previous) in faceCache.Faces)
                    if (previous.Draws.Length > 0 && !(faces.TryGetValue(rect, out var kept) && ReferenceEquals(kept, previous)))
                        clears.Add(new RawRect(rect.X, rect.Y, rect.X + rect.Size, rect.Y + rect.Size));
            var timing = NativeShadowDiagnostics ? new D3D12CaptureTiming(context, "native-shadow") : null;
            timing?.Begin();
            if (full || clears.Count > 0 || anyMissDraws)
            {
                targets.Begin(context, full ? null : clears.Count == 0 ? [] : clears.ToArray());
                if (anyMissDraws)
                {
                    var commands = context.Commands;
                    commands.SetGraphicsRootSignature(pipeline.Root); commands.SetDescriptorHeaps(srvHeap);
                    commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    commands.IASetVertexBuffers(0, new VertexBufferView(vertexBuffer.GPUVirtualAddress, checked(scene.VertexCount * 32), 32));
                    // Native cutout depth must consume the same explicit UV1 stream as colour.
                    // Its cache entry is retained by the recording context through the submit fence.
                    if (subTexture) BindAdditionalUvs(context,cache,scene);
                    commands.IASetIndexBuffer(new IndexBufferView(indexBuffer.GPUVirtualAddress, checked(scene.Indices.Length * 4), Format.R32_UInt));
                    foreach (var (rect, recorded) in misses)
                    {
                        if (recorded.Draws.Length == 0) continue;
                        commands.RSSetViewport(rect.X, rect.Y, rect.Size, rect.Size, 0, 1);
                        commands.RSSetScissorRect(new RawRect(rect.X, rect.Y, rect.X + rect.Size, rect.Y + rect.Size));
                        for (int k = 0; k < recorded.Draws.Length; k++)
                        {
                            var draw = recorded.Draws[k]; var material = scene.Materials[draw.Material]; var binding = frame.Surface.Materials[draw.Material];
                            commands.SetPipelineState((ID3D12PipelineState)recorded.References[3 * k]!);
                            commands.SetGraphicsRootConstantBufferView(0, context.UploadConstants(common with {
                                LightViewProjection = recorded.Projection, EgColor = draw.EgColor, Options = draw.Options }));
                            commands.SetGraphicsRootDescriptorTable(1, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase + (material.DiffuseTexture < 0 ? 0 : material.DiffuseTexture + 2), srvStride));
                            commands.SetGraphicsRootDescriptorTable(2, srvHeap.GetGPUDescriptorHandleForHeapStart().Offset(slotBase + (binding.SphereTexture < 0 ? 0 : binding.SphereTexture + 2), srvStride));
                            commands.DrawIndexedInstanced(draw.IndexCount, 1, draw.StartIndex, 0, 0); draws++;
                        }
                    }
                }
                targets.End(context);
            }
            timing?.EndAndResolve();
            faceCache.Faces = faces; faceCache.Valid = true;
            if (timing is not null) nativeShadowDiagnostics = new(timing, CopyNativeShadowAtlas(context, targets.Color), atlasWidth, atlasHeight);
        }
        else nativeShadowCasterVersions.Reset();
        LastNativeLightStatistics = new(lights.Length, faceCount, draws, NativeShadowTileSize)
            { ShadowCasterDraws = casters, ShadowCulledDraws = culled, ShadowReusedFaces = reusedFaces, ShadowReusedDraws = reusedDraws };
        var masks = new Dictionary<Guid, uint>();
        var materialMasks = frame.Materials.Select(flags =>
        {
            var id = flags.LightReceiverObjectId ?? Guid.Empty;
            if (!masks.TryGetValue(id, out var mask)) masks.Add(id, mask = frame.Lighting.ReceiverMask(flags.LightReceiverObjectId));
            return mask;
        }).ToArray();
        return new(atlas, lights.Length, context.UploadConstants(constants), materialMasks);
    }

    /// <summary>Exact reuse of unchanged shadow faces; disabled only to reproduce the full redraw for comparisons.</summary>
    internal bool NativeShadowReuse { get; set; } = true;
    /// <summary>Per-face caster rejection, also gated by the frame's EnableFrustumCulling like the surface pass.</summary>
    internal bool NativeShadowCulling { get; set; } = true;
    /// <summary>Opt-in diagnostics: GPU timestamps around the shadow work and a copy of the finished atlas.</summary>
    internal bool NativeShadowDiagnostics { get; set; }
    internal sealed record NativeShadowDiagnosticCapture(D3D12CaptureTiming Timing,
        (ID3D12Resource Resource, PlacedSubresourceFootPrint Layout) Atlas, int Width, int Height)
    {
        /// <summary>Valid after the frame's fence completes and before its slot is reused.</summary>
        internal float[] ReadAtlas()
        {
            int pitch = checked((int)Atlas.Layout.Footprint.RowPitch);
            var result = new float[checked(Width * Height)];
            var mapped = Atlas.Resource.Map<byte>(0, checked(pitch * Height));
            try { for (int y = 0; y < Height; y++) MemoryMarshal.Cast<byte, float>(mapped.Slice(y * pitch, Width * 4)).CopyTo(result.AsSpan(y * Width, Width)); }
            finally { Atlas.Resource.Unmap(0); }
            return result;
        }
    }
    private NativeShadowDiagnosticCapture? nativeShadowDiagnostics;
    internal NativeShadowDiagnosticCapture? LastNativeShadowDiagnostics => nativeShadowDiagnostics;
    private readonly record struct NativeShadowCaster(D3D12NativeShadowFaceCache.Draw Draw, ID3D12PipelineState Pipeline,
        SceneTexture? DiffuseTexture, SceneTexture? SphereTexture);
    private readonly D3D12NativeShadowCasterVersions nativeShadowCasterVersions = new();
    private readonly List<NativeShadowCaster> nativeShadowCasters = [];
    private readonly List<D3D12NativeShadowFaceCache.Draw> nativeShadowDraws = [];
    private readonly List<object?> nativeShadowReferences = [];

    private static (ID3D12Resource, PlacedSubresourceFootPrint) CopyNativeShadowAtlas(D3D12RecordingContext context, ID3D12Resource atlas)
    {
        var footprints = new PlacedSubresourceFootPrint[1];
        context.Device.GetCopyableFootprints(atlas.Description, 0, 1, 0, footprints, new int[1], new ulong[1], out ulong size);
        var readback = context.Own(context.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(size), ResourceStates.CopyDest));
        context.Commands.ResourceBarrierTransition(atlas, ResourceStates.PixelShaderResource, ResourceStates.CopySource);
        context.Commands.CopyTextureRegion(new TextureCopyLocation(readback, footprints[0]), 0, 0, 0, new TextureCopyLocation(atlas, 0));
        context.Commands.ResourceBarrierTransition(atlas, ResourceStates.CopySource, ResourceStates.PixelShaderResource);
        return (readback, footprints[0]);
    }

    private readonly Dictionary<(string, bool), byte[]> nativeFrameShaders = [];
    private byte[] GetNativeFrameShader(string entry, bool vertex)
    {
        if (nativeFrameShaders.TryGetValue((entry, vertex), out var value)) return value;
        value = OriginalFrameShaders.CompileNative(entry, vertex); nativeFrameShaders.Add((entry, vertex), value); runtime.ShaderCompilations++; return value;
    }
}
