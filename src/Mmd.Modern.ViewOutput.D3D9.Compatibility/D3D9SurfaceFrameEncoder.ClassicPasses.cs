using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

internal sealed partial class D3D9SurfaceFrameEncoder
{
    private (byte[] Frame, byte[] Visual) SerializeClassicPasses(OfflineFrame frame, ViewRenderRequest request,
        D3D9ClassicPassInputs passes, (byte[] Frame, byte[] Visual) original)
    {
        if (passes.Models.Count != frame.RenderingStyles.Count
            || frame.RenderingStyles.Any(value => !passes.Models.ContainsKey(value.ObjectId)))
            throw new InvalidDataException("Classic model selections must cover exactly this sample's model identities, including hidden models.");
        var ground = passes.GroundShadow;
        if (ground.Enabled && ground.ProjectionDirection.Y >= 0)
            throw new ArgumentException("Original ground projection requires an outgoing light direction below the horizon.", nameof(passes));
        OriginalFrameMath.Validate(new(OriginalAlphaBlendMode.Normal, true, false, passes.SelfShadow));
        // These source flags must be supplied separately from modern frame adapter policy. When
        // an inherited pass already exists it must agree, so it cannot be silently discarded.
        if (frame.Scene.Options?.SelfShadow is { } inherited && inherited != passes.SelfShadow)
            throw new InvalidDataException("Inherited self-shadow settings differ from the explicit Classic same-sample selection.");
        if (frame.Scene.Options?.GroundShadow is { } inheritedGround
            && (!ground.Enabled || inheritedGround.ProjectionDirection != ground.ProjectionDirection
                || inheritedGround.AlphaBlend != ground.Translucent || inheritedGround.UseStencil != ground.UseStencil))
            throw new InvalidDataException("Inherited ground settings differ from the explicit Classic same-sample selection.");

        var byNative = objects.Values.ToDictionary(value => value.NativeId);
        var modelStyles = frame.RenderingStyles.ToDictionary(value => value.ObjectId);
        var accessories = frame.Accessories.ToDictionary(value => value.ObjectId);
        var packetRows = new List<(D3D9SurfaceObjectBinding Binding, int VertexStart, int VertexCount, int MaterialCount)>();
        using (var reader = new BinaryReader(new MemoryStream(original.Frame), Encoding.UTF8))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(original.Frame.AsSpan(8)) != 4
                || BinaryPrimitives.ReadUInt32LittleEndian(original.Visual.AsSpan(8)) != 5)
                throw new InvalidDataException("Classic promotion requires the existing complete typed material packet.");
            reader.BaseStream.Position = 216;
            for (int i = 0; i < objects.Count; i++)
            {
                var binding = byNative[reader.ReadUInt64()]; reader.ReadUInt32(); reader.ReadInt32();
                int vertexStart = checked((int)reader.ReadUInt32()), vertices = checked((int)reader.ReadUInt32());
                reader.ReadUInt32(); int materials = checked((int)reader.ReadUInt32()); reader.ReadUInt32();
                SkipMaterialPacketString(reader); SkipMaterialPacketString(reader);
                int bones = checked((int)reader.ReadUInt32());
                for (int b = 0; b < bones; b++) { SkipMaterialPacketString(reader); reader.BaseStream.Position += 64; }
                int morphs = checked((int)reader.ReadUInt32());
                for (int m = 0; m < morphs; m++) { SkipMaterialPacketString(reader); reader.BaseStream.Position += 4; }
                if (binding.Kind == PmmDrawableKind.Accessory) reader.BaseStream.Position += 120;
                packetRows.Add((binding, vertexStart, vertices, materials));
            }
        }
        int vertexCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.Frame.AsSpan(24)));
        int materialCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.Frame.AsSpan(32)));
        long requiredBytes = original.Frame.Length + 244L + 12L * objects.Count + 32L * materialCount + 16L * vertexCount;
        if (requiredBytes > 1_073_741_824) throw new NotSupportedException("Classic pass inputs exceed the native packet limit.");
        var edgePositions = new Vector3[vertexCount]; var edgeColors = new uint[vertexCount];
        var eligible = new Dictionary<Guid, bool>();
        foreach (var row in packetRows.Where(value => value.Binding.Kind == PmmDrawableKind.Model))
        {
            var id = row.Binding.ObjectId; var input = frame.ObjectMaterialInputs[id];
            var selection = passes.Models[id]; var outline = frame.SceneInputs[id].SourceOutline;
            var options = selection.OutlineOptions is { } selectedOptions
                ? D3D9ClassicPassInputs.ResolveOutlineOptions(frame, selectedOptions) : null;
            if (row.MaterialCount != input.MaterialCount)
                throw new InvalidDataException("Classic material ownership differs from the exact source-local table.");
            if (outline is not null && (options is null || outline.Materials.Count != input.MaterialCount))
                throw new InvalidDataException("Original outline geometry requires explicit per-model options and matching materials.");
            if (outline is not null && (input.OutlineMaterials is null || !outline.Materials.SequenceEqual(input.OutlineMaterials)))
                throw new InvalidDataException("Original outline geometry and source-local material preparation differ at this sample.");
            bool enabled = options?.Eligible == true && outline is not null;
            eligible.Add(id, enabled);
            if (row.VertexCount == 0) continue;
            var source = frame.SceneInputs[id].SourceGeometry
                ?? throw new InvalidDataException("Classic model passes require the same-sample raw source geometry.");
            if (source.VertexCount != row.VertexCount || (outline is not null && outline.VertexCount != row.VertexCount))
                throw new InvalidDataException("Original outline topology differs from this model's same-sample geometry.");
            if (outline is null)
            {
                if (ground.Enabled && input.MaterialFlags.Any(value => value.CastGroundShadow))
                    throw new NotSupportedException("Original model ground silhouettes require the actual source edge stream.");
                for (int i = 0; i < source.VertexCount; i++) edgePositions[row.VertexStart + i] = Vector3.Transform(source.GetVertex(i).Position, source.World);
                continue;
            }
            float modelBase = OriginalOutlineMath.ComputeModelBase(options!);
            var widths = outline.Materials.Select(value => value.Width).ToArray();
            int offset = 0;
            for (int chunk = 0; chunk < outline.VertexChunkCount; chunk++)
            {
                var vertices = outline.GetVertexChunk(chunk); var positions = new Vector4[vertices.Length];
                OriginalOutlineBatchMath.BuildPositions(vertices, modelBase, widths, positions);
                for (int i = 0; i < vertices.Length; i++)
                {
                    var raw = source.GetVertex(offset + i); var edge = vertices[i];
                    if (edge.Position != raw.Position || edge.Normal != raw.Normal)
                        throw new InvalidDataException("Classic outlines must preserve exact source-local deformed positions and raw normals.");
                    edgePositions[row.VertexStart + offset + i] = Vector3.Transform(new(positions[i].X, positions[i].Y, positions[i].Z), source.World);
                    uint edgeColor = Argb(outline.Materials[edge.SelectedMaterialIndex].Color);
                    edgeColors[row.VertexStart + offset + i] = passes.GlobalSelfShadowEnabled ? edgeColor | 0xff000000U : edgeColor;
                }
                offset += vertices.Length;
            }
        }
        foreach (var row in packetRows.Where(value => value.Binding.Kind == PmmDrawableKind.Accessory))
        {
            var source = accessories[row.Binding.ObjectId].Inputs.SourceGeometry;
            for (int i = 0; i < row.VertexCount; i++) edgePositions[row.VertexStart + i] = Vector3.Transform(source.GetVertex(i).Position, source.World);
        }
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            var prefix = original.Frame.ToArray(); WriteCount(prefix, 8, 5);
            BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(208), BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(208)) | 8U);
            writer.Write(prefix); writer.Write("MDA9CLS1"u8); U(writer, 2);
            var shadow = passes.SelfShadow;
            U(writer, (passes.EffectFrame ? 64 : 0) | (shadow is null ? 0 : 1) | (shadow?.Generate == true ? 2 : 0) | (shadow?.PartialFar == true ? 4 : 0)
                | (ground.Enabled ? 8 : 0) | (ground.Translucent ? 16 : 0) | (ground.UseStencil ? 32 : 0));
            U(writer, shadow?.Width ?? 0); U(writer, shadow?.Height ?? 0);
            U(writer, shadow?.Sampling == OriginalTextureSampling.LinearClamp ? 2 : 1); U(writer, 2);
            U(writer, shadow?.DepthComparison == OriginalDepthComparison.Less ? 1 : 2);
            Matrix(writer, shadow?.LightViewProjection ?? Matrix4x4.Identity); Vec(writer, ground.ProjectionDirection);
            Vec(writer, ground.Material.Diffuse); Vec(writer, ground.Material.Ambient); Vec(writer, ground.Material.Specular);
            Vec(writer, ground.Material.Emissive); F(writer, ground.Material.Power); Vec(writer, ground.LightAmbient); F(writer, ground.AccessoryAmbientGray);
            // Original model lighting restores the source ambient/specular and zero diffuse;
            // accessory ambient-minus-.3 and diffuse-times-10 are separate captured channels.
            Vec(writer, frame.Light?.Color ?? frame.Scene.Surface.Profile.LightAmbient);
            Vec(writer, Vector3.Zero);
            Vec(writer, frame.Light?.Color ?? frame.Scene.Surface.Profile.LightSpecular);
            U(writer, packetRows.Count);
            foreach (var row in packetRows)
            {
                writer.Write(row.Binding.NativeId);
                U(writer, (eligible.GetValueOrDefault(row.Binding.ObjectId) ? 1 : 0)
                    | (passes.Models.TryGetValue(row.Binding.ObjectId, out var model) && model.AdditiveBlend ? 2 : 0));
            }
            U(writer, materialCount);
            foreach (var row in packetRows)
                for (int local = 0; local < row.MaterialCount; local++)
                {
                    writer.Write(row.Binding.NativeId); U(writer, local);
                    if (row.Binding.Kind == PmmDrawableKind.Model)
                    {
                        var input = frame.ObjectMaterialInputs[row.Binding.ObjectId];
                        var style = modelStyles[row.Binding.ObjectId];
                        var flags = style.Visible ? frame.Scene.Materials[style.MaterialStart + local] : input.MaterialFlags[local];
                        var edge = input.OutlineMaterials?[local];
                        U(writer, (flags.CastSelfShadow ? 1 : 0) | (flags.ReceiveSelfShadow ? 2 : 0) | (flags.CastGroundShadow ? 4 : 0)
                            | (edge?.DrawEnabled == true && eligible[row.Binding.ObjectId] ? 8 : 0) | (flags.ShadowDoubleSided ? 16 : 0));
                        Vec(writer, edge?.Color ?? Vector4.Zero);
                    }
                    else
                    {
                        var accessory = accessories[row.Binding.ObjectId];
                        var flags = accessory.Inputs.Source.Materials[local];
                        U(writer, (flags.CastSelfShadow ? 1 : 0) | (flags.ReceiveSelfShadow ? 2 : 0) | (flags.CastGroundShadow ? 4 : 0)
                            | (flags.ShadowDoubleSided ? 16 : 0)); Vec(writer, Vector4.Zero);
                    }
                }
            U(writer, vertexCount);
            for (int i = 0; i < vertexCount; i++) { Vec(writer, edgePositions[i]); writer.Write(edgeColors[i]); }
        }
        var frameBytes = stream.ToArray(); var visualBytes = original.Visual.ToArray();
        WriteCount(visualBytes, 8, 6); SHA256.HashData(frameBytes).CopyTo(visualBytes.AsSpan(20));
        return (frameBytes, visualBytes);
    }

    private static uint Argb(Vector4 color)
    {
        static uint Channel(float value)
        {
            if (!float.IsFinite(value)) throw new ArgumentException("Original edge color must be finite.");
            // PackPmxEdgeColor truncates to int32 then uint8; morph channels
            // outside [0,1] retain that original byte conversion, not saturation.
            return unchecked((uint)(int)(value * 255f)) & 0xffU;
        }
        return Channel(color.W) << 24 | Channel(color.X) << 16 | Channel(color.Y) << 8 | Channel(color.Z);
    }
}
