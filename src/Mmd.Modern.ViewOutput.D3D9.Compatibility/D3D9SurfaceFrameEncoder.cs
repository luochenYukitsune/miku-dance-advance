using System.Numerics;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D9.Compatibility;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

/// <summary>Serializes an already evaluated project sample. Never samples animation, physics or files.</summary>
internal sealed partial class D3D9SurfaceFrameEncoder
{
    private readonly IReadOnlyDictionary<Guid, D3D9SurfaceObjectBinding> objects;
    private readonly IReadOnlyDictionary<ulong, int[]> configuredSubsets = new Dictionary<ulong, int[]>();
    internal IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests { get; private set; }
    internal IReadOnlyDictionary<Guid, ulong> NativeObjectIds { get; }
    internal D3D9SurfaceScope Scope { get; }
    private Func<OfflineFrame, D3D9ClassicPassInputs>? classicPassInputs;
    private readonly D3D9ClassicScreenCaptureMode? screenCaptureMode;
    internal bool HasClassicPassInputs => classicPassInputs is not null;

    internal void UseClassicPassInputs(Func<OfflineFrame, D3D9ClassicPassInputs>? resolver)
    {
        if (screenCaptureMode is not null && resolver is null)
            throw new NotSupportedException("Classic screen metadata requires the configured EMM original-pass path.");
        classicPassInputs = resolver;
        if (resolver is not null)
            SceneInputRequests = Array.AsReadOnly(objects.Values.Where(value => value.Kind == PmmDrawableKind.Model)
                .Select(value => new SceneObjectRenderRequest(value.ObjectId, Enumerable.Range(0, value.BoneNames.Count),
                    includeSourceGeometry: true, includeMaterialInputs: true)).ToArray());
    }

    internal D3D9SurfaceFrameEncoder(IEnumerable<D3D9SurfaceObjectBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var frozen = bindings.ToArray();
        if (frozen.Any(value => value is null) || frozen.Select(value => value.ObjectId).Distinct().Count() != frozen.Length
            || frozen.Select(value => value.NativeId).Distinct().Count() != frozen.Length)
            throw new ArgumentException("Bindings require nonnull entries and unique scene and native identities.", nameof(bindings));
        objects = frozen.ToDictionary(value => value.ObjectId);
        NativeObjectIds = new ReadOnlyDictionary<Guid, ulong>(frozen.ToDictionary(value => value.ObjectId, value => value.NativeId));
        Scope = D3D9SurfaceScope.IsolatedSurface;
        SceneInputRequests = Array.AsReadOnly(frozen.Where(value => value.Kind == PmmDrawableKind.Model).Select(value => new SceneObjectRenderRequest(value.ObjectId,
            Enumerable.Range(0, value.BoneNames.Count), includeSourceGeometry: true)).ToArray());
    }

    internal D3D9SurfaceFrameEncoder(IEnumerable<D3D9SurfaceObjectBinding> bindings, D3D9ObjectFxConfiguration configuration)
        : this(bindings)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuredSubsets = ValidateAssignments(configuration.Assignments);
        Scope = D3D9SurfaceScope.ConfiguredObjectFx;
    }

    internal D3D9SurfaceFrameEncoder(IEnumerable<D3D9SurfaceObjectBinding> bindings, D3D9SceneFxConfiguration configuration)
        : this(bindings)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuredSubsets = ValidateAssignments(configuration.Assignments);
        Scope = D3D9SurfaceScope.ConfiguredSceneFx;
    }

    internal D3D9SurfaceFrameEncoder(IEnumerable<D3D9SurfaceObjectBinding> bindings, D3D9EmmRuntimeConfiguration configuration,
        D3D9ClassicScreenCaptureMode? screenCaptureMode = null)
        : this(bindings)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (screenCaptureMode is { } mode && !Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(screenCaptureMode));
        this.screenCaptureMode = screenCaptureMode;
        bool Matches(Guid id, bool isModel) => objects.TryGetValue(id, out var binding)
            && binding.Kind == (isModel ? PmmDrawableKind.Model : PmmDrawableKind.Accessory);
        if (configuration.Objects.Count != objects.Count || configuration.ImportPlan.LiveObjects.Count != objects.Count
            || configuration.Objects.Any(value => !Matches(value.ObjectId, value.IsModel))
            || configuration.ImportPlan.LiveObjects.Any(value => !Matches(value.ObjectId, value.IsModel)))
            throw new ArgumentException("EMM configuration and import plan must cover exactly the configured stable scene identities and object kinds.", nameof(configuration));
        var subsets = new Dictionary<ulong, HashSet<int>>();
        void Include(Guid id, bool isModel, IEnumerable<int> values)
        {
            if (!Matches(id, isModel))
                throw new ArgumentException("EMM entries and owners must identify a configured instance of the declared kind.", nameof(configuration));
            ulong nativeId = objects[id].NativeId;
            foreach (int subset in values)
            {
                if (subset < 0) throw new ArgumentException("EMM subset identities must be nonnegative.", nameof(configuration));
                if (!subsets.TryGetValue(nativeId, out var selected)) subsets.Add(nativeId, selected = []);
                selected.Add(subset);
            }
        }
        foreach (var section in configuration.Sections)
        {
            if (section.Owner is { } owner)
                Include(owner.ObjectId, owner.IsModel, owner.Subset < 0 ? [] : [owner.Subset]);
            foreach (var entry in section.Entries)
                Include(entry.ObjectId, entry.IsModel, entry.SubsetEffects.Keys.Concat(entry.SubsetShown.Keys));
        }
        configuredSubsets = subsets.ToDictionary(value => value.Key, value => value.Value.Order().ToArray());
        Scope = D3D9SurfaceScope.ConfiguredEmm;
        SceneInputRequests = Array.AsReadOnly(objects.Values.Where(value => value.Kind == PmmDrawableKind.Model)
            .Select(value => new SceneObjectRenderRequest(value.ObjectId, Enumerable.Range(0, value.BoneNames.Count),
                includeSourceGeometry: true, includeMaterialInputs: true)).ToArray());
    }

    private IReadOnlyDictionary<ulong, int[]> ValidateAssignments(IReadOnlyList<D3D9ObjectFxAssignment> assignments)
    {
        var nativeIds = objects.Values.Select(binding => binding.NativeId).ToHashSet();
        if (assignments.Any(row => !nativeIds.Contains(row.ObjectId)))
            throw new ArgumentException("Object FX assignments must refer to configured stable native object identities.", "configuration");
        return assignments.Where(row => row.Subset >= 0).GroupBy(row => row.ObjectId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Subset).ToArray());
    }

    /// <summary>The identity part of the encoder's frame checks, without serializing geometry or textures. Preview
    /// preflight uses this; the full encode in PresentAsync still enforces every rule before native admission.</summary>
    internal void ValidateIdentities(OfflineFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var rows = frame.RenderingStyles;
        int modelCount = objects.Values.Count(value => value.Kind == PmmDrawableKind.Model);
        if (rows.Count != modelCount || rows.Any(row => row is null)
            || rows.Select(row => row.ObjectId).Distinct().Count() != rows.Count
            || rows.Any(row => !objects.TryGetValue(row.ObjectId, out var binding) || binding.Kind != PmmDrawableKind.Model)
            || frame.SceneInputs.Keys.Any(id => !objects.TryGetValue(id, out var binding) || binding.Kind != PmmDrawableKind.Model))
            throw new ArgumentException("Frame metadata must cover exactly the configured object identities.", nameof(frame));
    }

    internal D3D9SurfaceSnapshot Encode(OfflineFrame frame, ViewRenderRequest request, Vector4 background,
        D3D9ClassicMediaInputs? media = null)
    {
        var packets = Serialize(frame, request, background, media);
        return Scope switch
        {
            D3D9SurfaceScope.ConfiguredObjectFx => D3D9SurfaceSnapshot.CopyConfiguredObjectFx(packets.Frame, packets.Visual),
            D3D9SurfaceScope.ConfiguredSceneFx => D3D9SurfaceSnapshot.CopyConfiguredSceneFx(packets.Frame, packets.Visual),
            D3D9SurfaceScope.ConfiguredEmm => D3D9SurfaceSnapshot.CopyConfiguredEmm(packets.Frame, packets.Visual),
            D3D9SurfaceScope.IsolatedSurface => D3D9SurfaceSnapshot.CopyIsolatedSurface(packets.Frame, packets.Visual,
                D3D9SurfaceIsolation.NoConfiguredMmeOrEmm),
            _ => throw new InvalidOperationException("Unsupported surface scope.")
        };
    }

    // Internal packet seam shares the production serializer; no mutable packet is exposed publicly.
    internal (byte[] Frame, byte[] Visual) Serialize(OfflineFrame frame, ViewRenderRequest request, Vector4 background,
        D3D9ClassicMediaInputs? media = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (screenCaptureMode is { } mode)
        {
            if (media is not null && media.ScreenCaptureMode != mode)
                throw new InvalidOperationException("Decoded Classic screen mode must match the frozen original screen metadata.");
            // Synchronous preflight has no decoder. Preserve the accepted frame identity and
            // validate/encode the same explicit control metadata with no invented media layers.
            media ??= new(frame, [], mode);
        }
        if (media is not null)
        {
            if (Scope != D3D9SurfaceScope.ConfiguredEmm || classicPassInputs is null)
                throw new NotSupportedException("Classic backgrounds require the configured EMM original-pass path.");
            // Check before the material-policy projection creates its neutral wrapper.
            // The media decoder must consume this accepted frame, never another clock.
            if (!ReferenceEquals(media.Frame, frame))
                throw new InvalidOperationException("Classic media inputs must belong to this exact accepted frame.");
        }
        if (frame.UnpreparedAccessoryIndices.Count != 0)
            throw new NotSupportedException("Traditional rendering requires prepared assets for every PMM accessory.");
        if (media?.ScreenCaptureMode is null && frame.Accessories.Any(value =>
            value.Inputs.Document?.Materials.Any(material => D3D9AccessoryTextureToken.Parse(material.Material.Texture).Type == 3) == true))
            throw new NotSupportedException("Classic accessory screen requires explicit original screen metadata.");
        if (classicPassInputs is { } resolve)
        {
            frame = ProjectClassicMaterials(frame);
            var passes = resolve(frame) ?? throw new InvalidOperationException("The Classic pass resolver returned null.");
            if (!ReferenceEquals(passes.Frame, frame))
                throw new InvalidOperationException("Classic pass inputs must be frozen from this exact accepted frame.");
            if (Scope == D3D9SurfaceScope.ConfiguredEmm) ValidateEmmMaterialBounds(frame);
            return SerializeClassicMedia(SerializeClassicPasses(frame, request, passes,
                SerializeMaterialInputs(frame, request, background)), media);
        }
        if (Scope == D3D9SurfaceScope.ConfiguredEmm)
        {
            ValidateEmmMaterialBounds(frame);
            return SerializeMaterialInputs(frame, request, background);
        }
        if (frame.Accessories.Count != 0 || objects.Values.Any(value => value.Kind == PmmDrawableKind.Accessory))
            return SerializeTyped(frame, request, background);
        return SerializeModels(frame, request, background);
    }

    private void ValidateEmmMaterialBounds(OfflineFrame frame)
    {
        foreach (var binding in objects.Values)
        {
            if (!configuredSubsets.TryGetValue(binding.NativeId, out var subsets)) continue;
            int materialCount;
            if (binding.Kind == PmmDrawableKind.Model)
            {
                if (!frame.ObjectMaterialInputs.TryGetValue(binding.ObjectId, out var input))
                    throw new NotSupportedException("EMM subset and owner validation requires this object's real same-sample material inputs, including hidden objects.");
                if (input.ObjectId != binding.ObjectId)
                    throw new ArgumentException("EMM material input identity does not match its configured object.", nameof(frame));
                materialCount = input.MaterialCount;
            }
            else
            {
                var accessory = frame.Accessories.SingleOrDefault(value => value.ObjectId == binding.ObjectId)
                    ?? throw new ArgumentException("EMM accessory inputs must cover every configured accessory.", nameof(frame));
                materialCount = accessory.Inputs.Document?.Materials.Count
                    ?? throw new NotSupportedException("EMM accessory bounds require its original material table.");
            }
            if (subsets.Any(subset => subset >= materialCount))
                throw new ArgumentException("EMM effect, visibility or owner subset is outside this object's authored material range.", nameof(frame));
        }
    }

    private (byte[] Frame, byte[] Visual) SerializeModels(OfflineFrame frame, ViewRenderRequest request, Vector4 background)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(request); request.Validate();
        if (frame.Width != request.Width || frame.Height != request.Height)
            throw new ArgumentException("Frame dimensions must match the renderer request.", nameof(frame));
        if (request.Width > 4096 || request.Height > 4096) throw new NotSupportedException("D3D9 surface extents are limited to 4096.");
        if (background.W != 1) throw new NotSupportedException("The isolated surface requires an opaque background.");
        var scene = frame.Scene.Surface.Snapshot;
        var rows = frame.RenderingStyles;
        int modelCount = objects.Values.Count(value => value.Kind == PmmDrawableKind.Model);
        if (rows.Count != modelCount || frame.SceneInputs.Count != modelCount || rows.Any(row => row is null)
            || rows.Select(row => row.ObjectId).Distinct().Count() != rows.Count
            || rows.Any(row => !objects.TryGetValue(row.ObjectId, out var binding) || binding.Kind != PmmDrawableKind.Model)
            || frame.SceneInputs.Keys.Any(id => !objects.TryGetValue(id, out var binding) || binding.Kind != PmmDrawableKind.Model))
            throw new ArgumentException("Frame metadata must cover exactly the configured object identities.", nameof(frame));
        var additionalUvs = scene.AdditionalUvs;
        bool hasSubTexture = frame.Scene.Surface.Materials.Any(material => material.SphereMode == OriginalSphereMode.SubTexture);
        if (rows.Count > 1_000_000 || scene.VertexCount > 10_000_000 || scene.Indices.Length > 60_000_000
            || scene.Materials.Count > 1_000_000 || scene.Textures.Count > 4096)
            throw new NotSupportedException("Scene exceeds native packet limits.");
        long visualByteCount = 128L + 132L * scene.Materials.Count + 24L * scene.VertexCount;
        long frameByteCount = 208L + 32L * scene.VertexCount + 4L * scene.Indices.Length + 84L * scene.Materials.Count;
        if (additionalUvs is not null) frameByteCount += 8L + 64L * scene.VertexCount;
        foreach (var row in rows)
        {
            var item = objects[row.ObjectId];
            frameByteCount += 36L + Encoding.UTF8.GetByteCount(item.SourcePath) + Encoding.UTF8.GetByteCount(item.Name);
            if (row.Visible)
                foreach (var name in item.BoneNames) frameByteCount += 68L + Encoding.UTF8.GetByteCount(name);
        }
        foreach (var texture in scene.Textures) frameByteCount += 12L + texture.Rgba.Length;
        if (visualByteCount > 16_777_216 || frameByteCount > 1_073_741_824)
            throw new NotSupportedException("Scene exceeds the native packet byte limits.");
        var normalWorld = NormalMatrix(scene.World);
        var rawWorld = new Vector3[scene.VertexCount]; var rawLocal = new Vector3[scene.VertexCount];
        int materialCursor = 0, vertexCursor = 0, indexCursor = 0, sourceUvChannelCount = 0;
        foreach (var row in rows)
        {
            if (row.MaterialStart != materialCursor || row.MaterialCount < 0 || row.MaterialCount > scene.Materials.Count - materialCursor
                || (!row.Visible && row.MaterialCount != 0))
                throw new ArgumentException("Object material ranges must cover the frame in composition order.", nameof(frame));
            if (!frame.SceneInputs.TryGetValue(row.ObjectId, out var input) || input.ObjectId != row.ObjectId || input.Visible != row.Visible)
                throw new ArgumentException("Object inputs and material visibility disagree.", nameof(frame));
            var binding = objects[row.ObjectId];
            if (input.BoneWorldMatrices.Count != (row.Visible ? binding.BoneNames.Count : 0)
                || input.BoneWorldMatrices.Keys.Any(index => (uint)index >= (uint)binding.BoneNames.Count))
                throw new ArgumentException("Selected bone matrices must cover the configured bone names exactly.", nameof(frame));
            if (row.Visible)
            {
                var source = input.SourceGeometry ?? throw new NotSupportedException("Visible objects require same-sample source-local geometry.");
                if (source.Materials.Count != row.MaterialCount || source.VertexCount > scene.VertexCount - vertexCursor
                    || source.Indices.Length > scene.Indices.Length - indexCursor)
                    throw new ArgumentException("Source geometry does not fit its combined object range.", nameof(frame));
                // Hidden rows have no consumed source geometry and a zero visible range. Validate
                // subset addresses only against an object's authored source in a visible sample.
                if (configuredSubsets.TryGetValue(binding.NativeId, out var subsets)
                    && subsets.Any(subset => subset >= source.Materials.Count))
                    throw new ArgumentException("Object FX subset is outside this visible object's authored material range.", nameof(frame));
                var sourceUvs = source.AdditionalUvs;
                sourceUvChannelCount = Math.Max(sourceUvChannelCount, sourceUvs?.ChannelCount ?? 0);
                if (additionalUvs is not null)
                {
                    var combinedUvBytes = MemoryMarshal.AsBytes(additionalUvs.Vertices.Slice(vertexCursor, source.VertexCount));
                    // The core composer copies every xyzw vector, including undeclared high channels,
                    // and uses zero rows only for sources with no stream. Validate that exact contract.
                    if (sourceUvs is null ? combinedUvBytes.IndexOfAnyExcept((byte)0) >= 0
                        : !MemoryMarshal.AsBytes(sourceUvs.Vertices).SequenceEqual(combinedUvBytes))
                        throw new ArgumentException("Source additional UV order does not match the combined object range.", nameof(frame));
                }
                else if (sourceUvs is not null)
                    throw new ArgumentException("Combined additional UV stream is missing an authored source stream.", nameof(frame));
                var sourceNormalWorld = ValidateSourceSurface(source, frame.Scene, row.MaterialStart, row.MaterialCount);
                for (int i = 0; i < source.Indices.Length; i++)
                    if (scene.Indices[indexCursor + i] != checked(source.Indices[i] + (uint)vertexCursor))
                        throw new ArgumentException("Source triangle order does not match the combined object range.", nameof(frame));
                for (int i = 0; i < source.Materials.Count; i++)
                {
                    var local = source.Materials[i]; var combined = scene.Materials[materialCursor + i];
                    if (combined.StartIndex != checked(local.StartIndex + indexCursor) || combined.IndexCount != local.IndexCount
                        || !SameTexture(source, local.DiffuseTexture, scene, combined.DiffuseTexture)
                        || !SameTexture(source, local.ToonTexture, scene, combined.ToonTexture))
                        throw new ArgumentException("Source material order or texture ownership does not match its combined range.", nameof(frame));
                }
                for (int i = 0; i < source.VertexCount; i++)
                {
                    int global = vertexCursor + i; var local = source.GetVertex(i); var combined = scene.GetVertex(global);
                    var worldPosition = Vector3.Transform(local.Position, source.World);
                    var worldNormal = Vector3.TransformNormal(local.Normal, sourceNormalWorld);
                    if (worldPosition != Vector3.Transform(combined.Position, scene.World) || local.Uv != combined.Uv
                        || !SameDirection(worldNormal, Vector3.TransformNormal(combined.Normal, normalWorld)))
                        throw new ArgumentException("Source vertex order does not match the combined object range.", nameof(frame));
                    rawWorld[global] = worldNormal; rawLocal[global] = local.Normal;
                }
                vertexCursor = checked(vertexCursor + source.VertexCount); indexCursor = checked(indexCursor + source.Indices.Length);
            }
            else if (input.SourceGeometry is not null)
                throw new ArgumentException("Hidden object inputs must not carry consumed source geometry.", nameof(frame));
            materialCursor = checked(materialCursor + row.MaterialCount);
        }
        if (materialCursor != scene.Materials.Count || vertexCursor != scene.VertexCount || indexCursor != scene.Indices.Length)
            throw new ArgumentException("Object source ranges do not cover the combined frame exactly.", nameof(frame));
        if (sourceUvChannelCount != (additionalUvs?.ChannelCount ?? 0))
            throw new ArgumentException("Combined additional UV channel count does not match the visible source declarations.", nameof(frame));

        var camera = ResolveCamera(frame);
        _ = camera.ViewProjection(request.Width, request.Height); // Reuse core camera validation.
        var view = camera.LeftHanded ? Matrix4x4.CreateLookAtLeftHanded(camera.Eye, camera.Target, camera.Up)
            : Matrix4x4.CreateLookAt(camera.Eye, camera.Target, camera.Up);
        float aspect = (float)request.Width / request.Height;
        // Original MMD normal viewport/camera-track perspective setup uses
        // near=1, far=100000 (0x140021690 / 0x140069a10). MME effects can use
        // clip z/w as a depth encoding, so the neutral camera's .1/10000
        // changes their calculations even when the visible framing agrees.
        // Keep the shared camera and ordinary renderer requests untouched.
        float near = HasClassicPassInputs && camera.VerticalFovDegrees is not null ? 1 : camera.Near;
        float far = HasClassicPassInputs && camera.VerticalFovDegrees is not null ? 100000 : camera.Far;
        var projection = camera.LeftHanded
            ? camera.VerticalFovDegrees is { } lf ? Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(lf * MathF.PI / 180, aspect, near, far)
                : Matrix4x4.CreateOrthographicLeftHanded(camera.OrthographicHeight * aspect, camera.OrthographicHeight, camera.Near, camera.Far)
            : camera.VerticalFovDegrees is { } rf ? Matrix4x4.CreatePerspectiveFieldOfView(rf * MathF.PI / 180, aspect, near, far)
                : Matrix4x4.CreateOrthographic(camera.OrthographicHeight * aspect, camera.OrthographicHeight, camera.Near, camera.Far);
        if (camera.ProjectionJitterPixels != Vector2.Zero)
            projection *= Matrix4x4.CreateTranslation(2 * camera.ProjectionJitterPixels.X / request.Width, -2 * camera.ProjectionJitterPixels.Y / request.Height, 0);
        var profile = ResolveModelProfile(frame, request);
        var sphereRotation = profile.SphereRotation;
        var incoming = scene.LightDirection;
        if (frame.Light is { } light)
            incoming = -light.Direction;
        if (!float.IsFinite(incoming.LengthSquared()) || incoming.LengthSquared() <= 0)
            throw new ArgumentException("Incoming light direction must be finite and nonzero.", nameof(frame));
        double sampledFrame = frame.Sample.SampledSourceFrame.ToDouble();
        if (!double.IsFinite(sampledFrame)) throw new ArgumentException("Sampled source frame must be finite.", nameof(frame));

        using var frameStream = new MemoryStream();
        using (var writer = new BinaryWriter(frameStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MDA9FRM1"u8); U(writer, additionalUvs is null ? 1 : 2); U(writer, request.Width); U(writer, request.Height); U(writer, rows.Count);
            U(writer, scene.VertexCount); U(writer, scene.Indices.Length); U(writer, scene.Materials.Count); U(writer, scene.Textures.Count);
            writer.Write(sampledFrame); F(writer, (float)(sampledFrame / OfflineFramePlan.SourceFramesPerSecond));
            Matrix(writer, view); Matrix(writer, projection); Vec(writer, incoming); Vec(writer, background);
            foreach (var row in rows)
            {
                var item = objects[row.ObjectId]; var input = frame.SceneInputs[row.ObjectId];
                writer.Write(item.NativeId); U(writer, row.MaterialStart); U(writer, row.MaterialCount); U(writer, row.Visible ? 1 : 0);
                Str(writer, item.SourcePath); Str(writer, item.Name); U(writer, input.BoneWorldMatrices.Count);
                foreach (var bone in input.BoneWorldMatrices.OrderBy(pair => pair.Key))
                { Str(writer, item.BoneNames[bone.Key]); Matrix(writer, bone.Value); }
                U(writer, 0); // Named evaluated morph weights are not exposed; never fabricate them.
            }
            for (int i = 0; i < scene.VertexCount; i++)
            {
                var vertex = scene.GetVertex(i); Vec(writer, Vector3.Transform(vertex.Position, scene.World));
                var normal = Vector3.TransformNormal(vertex.Normal, normalWorld);
                if (normal.LengthSquared() > 0) normal = Vector3.Normalize(normal);
                Vec(writer, normal); Vec(writer, vertex.Uv);
            }
            foreach (uint index in scene.Indices)
            { if (index >= scene.VertexCount) throw new ArgumentException("Vertex index outside the combined frame.", nameof(frame)); writer.Write(index); }
            int cursor = 0;
            foreach (var row in rows)
                for (int local = 0; local < row.MaterialCount; local++)
                {
                    int global = row.MaterialStart + local; var material = scene.Materials[global]; var binding = frame.Scene.Surface.Materials[global];
                    if (material.StartIndex != cursor || material.IndexCount < 0 || material.IndexCount % 3 != 0 || material.IndexCount > scene.Indices.Length - cursor)
                        throw new ArgumentException("Material index ranges must cover the frame in order.", nameof(frame));
                    cursor += material.IndexCount;
                    Texture(material.DiffuseTexture, scene); Texture(material.ToonTexture, scene); Texture(binding.SphereTexture, scene);
                    WriteModelFrameMaterial(writer, objects[row.ObjectId].NativeId, local, material.StartIndex,
                        material.IndexCount, material, binding, frame.Scene.Materials[global], static index => index);
                }
            if (cursor != scene.Indices.Length) throw new ArgumentException("Material indices do not cover the frame.", nameof(frame));
            foreach (var texture in scene.Textures)
            { U(writer, texture.Width); U(writer, texture.Height); U(writer, texture.Rgba.Length); writer.Write(texture.Rgba); }
            if (additionalUvs is not null)
            {
                U(writer, additionalUvs.ChannelCount); U(writer, scene.VertexCount);
                foreach (var vertex in additionalUvs.Vertices)
                { Vec(writer, vertex.Uv1); Vec(writer, vertex.Uv2); Vec(writer, vertex.Uv3); Vec(writer, vertex.Uv4); }
            }
        }
        var frameBytes = frameStream.ToArray();
        if (frameBytes.Length > 1_073_741_824) throw new NotSupportedException("FRM1 packet exceeds the native limit.");
        using var visualStream = new MemoryStream();
        using (var writer = new BinaryWriter(visualStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MDA9VIS1"u8); U(writer, hasSubTexture ? 3 : 2); U(writer, scene.Materials.Count); U(writer, scene.VertexCount);
            writer.Write(SHA256.HashData(frameBytes)); Vec(writer, camera.Eye); Matrix(writer, sphereRotation);
            foreach (var row in rows)
                for (int local = 0; local < row.MaterialCount; local++)
                {
                    int global = row.MaterialStart + local; var material = scene.Materials[global]; var binding = frame.Scene.Surface.Materials[global];
                    WriteModelVisualMaterial(writer, objects[row.ObjectId].NativeId, local, material, binding, profile, scene.Textures);
                }
            for (int i = 0; i < scene.VertexCount; i++) { Vec(writer, rawWorld[i]); Vec(writer, rawLocal[i]); }
        }
        if (visualStream.Length > 16_777_216) throw new NotSupportedException("VIS1 packet exceeds the native limit.");
        return (frameBytes, visualStream.ToArray());
    }

    private static void WriteModelFrameMaterial(BinaryWriter writer, ulong objectId, int localIndex,
        int drawStart, int drawCount, SceneMaterial material, OriginalStyleMaterialBinding binding,
        OriginalFrameMaterialFlags flags, Func<int, int> remapTexture)
    {
        writer.Write(objectId); U(writer, localIndex); U(writer, drawStart); U(writer, drawCount);
        U(writer, (binding.DoubleSided ? 1 : 0) | (flags.SurfaceEnabled ? 2 : 0));
        Vec(writer, material.Diffuse); Vec(writer, material.Specular); Vec(writer, material.Ambient); F(writer, material.Shininess);
        writer.Write(remapTexture(material.DiffuseTexture)); writer.Write(remapTexture(material.ToonTexture));
        writer.Write(remapTexture(binding.SphereTexture)); writer.Write((int)binding.SphereMode);
    }

    private static void WriteModelVisualMaterial(BinaryWriter writer, ulong objectId, int localIndex,
        SceneMaterial material, OriginalStyleMaterialBinding binding, OriginalStyleProfile profile, IReadOnlyList<SceneTexture> textures)
    {
        var colors = OriginalStyleMath.ResolveHostColors(material, profile); var factors = binding.TextureFactors;
        var toon = new Vector4(Add(Multiply(binding.ToonShadowColor.X, factors.Toon.Multiply.X), factors.Toon.Add.X),
            Add(Multiply(binding.ToonShadowColor.Y, factors.Toon.Multiply.Y), factors.Toon.Add.Y),
            Add(Multiply(binding.ToonShadowColor.Z, factors.Toon.Multiply.Z), factors.Toon.Add.Z), Add(factors.Toon.Multiply.W, factors.Toon.Add.W));
        bool opaque = material.Diffuse.W >= 1 && TextureOpaque(material.DiffuseTexture, textures)
            && (binding.SphereMode == OriginalSphereMode.None || TextureOpaque(binding.SphereTexture, textures))
            && (binding.SphereMode != OriginalSphereMode.SubTexture || material.DiffuseTexture < 0 || (float.IsFinite(toon.W) && toon.W >= 1));
        int flags = (opaque ? 1 : 0) | (binding.SphereMode == OriginalSphereMode.SubTexture && binding.SubTextureAdditive ? 2 : 0);
        writer.Write(objectId); U(writer, localIndex); U(writer, flags);
        Vec(writer, colors.EgColor); Vec(writer, colors.SpecularPower); Vec(writer, toon); F(writer, material.AlphaCutoff);
        Vec(writer, factors.Diffuse.Multiply); Vec(writer, factors.Diffuse.Add); Vec(writer, factors.Sphere.Multiply); Vec(writer, factors.Sphere.Add);
    }

    private static OriginalStyleProfile ResolveModelProfile(OfflineFrame frame, ViewRenderRequest request,
        OriginalStyleProfile? sourceProfile = null)
    {
        var profile = sourceProfile ?? frame.Scene.Surface.Profile;
        var sphereRotation = profile.SphereRotation;
        if (frame.ResolvedCamera is not null || frame.Camera is not null)
        {
            var camera = ResolveCamera(frame);
            _ = camera.ViewProjection(request.Width, request.Height);
            sphereRotation = camera.LeftHanded ? Matrix4x4.CreateLookAtLeftHanded(camera.Eye, camera.Target, camera.Up)
                : Matrix4x4.CreateLookAt(camera.Eye, camera.Target, camera.Up);
            sphereRotation.Translation = Vector3.Zero;
        }
        if (frame.Scene.Options?.SphereRotationOverride is { } explicitRotation) sphereRotation = explicitRotation;
        profile = profile with { SphereRotation = sphereRotation };
        return frame.Light is { } light ? profile with { LightAmbient = light.Color, LightSpecular = light.Color } : profile;
    }

    internal static SceneCamera ResolveCamera(OfflineFrame frame)
    {
        if (frame.ResolvedCamera is { } resolved) return resolved;
        if (frame.Camera is { } pose)
        {
            if (pose.ParentModelIndex >= 0 && frame.CameraParentTransform is null)
                throw new NotSupportedException("A parented camera requires its same-sample resolved parent transform.");
            if (frame.CameraParentTransform is { } parent) _ = NormalMatrix(parent);
            return pose.CreateCamera(frame.CameraParentTransform);
        }
        return frame.Scene.Options?.CameraOverride ?? frame.Scene.Surface.Snapshot.Camera;
    }
    // Shared by same-sample encoding and all-potential-object CPU preflight. The latter may run
    // while this object is hidden, so its own World must already include instance placement.
    internal static Matrix4x4 ValidateSourceSurface(SceneSnapshot source, OriginalFrameScene combined,
        int materialStart, int materialCount)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(combined);
        if (materialStart < 0 || materialCount < 0 || materialStart > combined.Materials.Count - materialCount
            || source.Materials.Count != materialCount)
            throw new ArgumentException("Source geometry does not fit its combined object range.");
        for (int local = 0; local < materialCount; local++)
        {
            int global = materialStart + local;
            var binding = combined.Surface.Materials[global];
            if (source.World != Matrix4x4.Identity && combined.Materials[global].SurfaceEnabled
                && combined.Surface.Snapshot.Materials[global].IndexCount > 0
                && binding.SphereMode is OriginalSphereMode.Multiply or OriginalSphereMode.Add)
                throw new NotSupportedException("Active sphere materials on placed source geometry require a validated placement-aware sphere normal contract.");
            if (binding.SphereMode == OriginalSphereMode.SubTexture && source.AdditionalUvs is null)
                throw new NotSupportedException("SubTexture requires this object's own evaluated UV1 stream.");
        }
        return NormalMatrix(source.World);
    }

    private static Matrix4x4 NormalMatrix(Matrix4x4 world)
    {
        if (!Matrix4x4.Invert(world, out var inverse)) throw new NotSupportedException("Source World must be invertible.");
        // Validate all entries, including unused homogeneous components.
        using var stream = new MemoryStream(64); using var writer = new BinaryWriter(stream); Matrix(writer, world); Matrix(writer, inverse);
        return Matrix4x4.Transpose(inverse);
    }
    private static bool SameDirection(Vector3 a, Vector3 b)
    {
        if (!float.IsFinite(a.LengthSquared()) || !float.IsFinite(b.LengthSquared())) return false;
        if (a.LengthSquared() <= 1e-12f || b.LengthSquared() <= 1e-12f) return a.LengthSquared() <= 1e-12f && b.LengthSquared() <= 1e-12f;
        return Vector3.DistanceSquared(Vector3.Normalize(a), Vector3.Normalize(b)) <= 1e-10f;
    }
    private static bool SameTexture(SceneSnapshot a, int ai, SceneSnapshot b, int bi)
        => ai == -1 ? bi == -1 : ai >= 0 && ai < a.Textures.Count && bi >= 0 && bi < b.Textures.Count && ReferenceEquals(a.Textures[ai], b.Textures[bi]);
    private static void Texture(int index, SceneSnapshot scene)
    { if (index < -1 || index >= scene.Textures.Count) throw new ArgumentException("Texture index outside the combined frame."); }
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Multiply(float a, float b) => a * b;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float Add(float a, float b) => a + b;
    private static void U(BinaryWriter writer, int value) => writer.Write(checked((uint)value));
    private static void F(BinaryWriter writer, float value)
    { if (!float.IsFinite(value)) throw new ArgumentException("Packet values must be finite."); writer.Write(value); }
    private static void Str(BinaryWriter writer, string value)
    { var bytes = Encoding.UTF8.GetBytes(value); U(writer, bytes.Length); writer.Write(bytes); }
    private static void Vec(BinaryWriter writer, Vector2 value) { F(writer, value.X); F(writer, value.Y); }
    private static void Vec(BinaryWriter writer, Vector3 value) { F(writer, value.X); F(writer, value.Y); F(writer, value.Z); }
    private static void Vec(BinaryWriter writer, Vector4 value) { F(writer, value.X); F(writer, value.Y); F(writer, value.Z); F(writer, value.W); }
    private static void Matrix(BinaryWriter writer, Matrix4x4 value)
    {
        F(writer, value.M11); F(writer, value.M12); F(writer, value.M13); F(writer, value.M14);
        F(writer, value.M21); F(writer, value.M22); F(writer, value.M23); F(writer, value.M24);
        F(writer, value.M31); F(writer, value.M32); F(writer, value.M33); F(writer, value.M34);
        F(writer, value.M41); F(writer, value.M42); F(writer, value.M43); F(writer, value.M44);
    }
}
