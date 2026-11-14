using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Application;
using Mmd.Modern.Core;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

internal sealed partial class D3D9SurfaceFrameEncoder
{
    private sealed record MaterialPacketObject(D3D9SurfaceObjectBinding Binding, byte[] Record,
        int MaterialStart, int MaterialCount, int VertexCount, bool Typed);

    // This splice consumes only packets already validated by the ordinary model/accessory
    // serializers. Visible geometry/material payloads retain their existing bytes. The only
    // newly resolved records are detached hidden-model materials, never dummy geometry.
    private (byte[] Frame, byte[] Visual) SerializeMaterialInputs(OfflineFrame frame, ViewRenderRequest request, Vector4 background)
    {
        var models = frame.RenderingStyles;
        double sourceFrame = frame.Sample.SampledSourceFrame.ToDouble();
        if (frame.ObjectMaterialInputs.Count != models.Count
            || frame.ObjectMaterialInputs.Keys.Any(id => !objects.TryGetValue(id, out var binding)
                || binding.Kind != PmmDrawableKind.Model))
            throw new InvalidDataException("EMM requires an exact source-local material table for every configured model.");
        foreach (var model in models)
        {
            if (!frame.ObjectMaterialInputs.TryGetValue(model.ObjectId, out var input)
                || input.ObjectId != model.ObjectId || input.SourceFrame != sourceFrame || input.Visible != model.Visible
                || !double.IsFinite(input.LocalFrame) || input.LocalFrame < 0 || input.LocalFrame > input.SourceFrame
                || input.Bindings.Count != input.MaterialCount || input.MaterialFlags.Count != input.MaterialCount
                || input.OutlineMaterials is not null && input.OutlineMaterials.Count != input.MaterialCount)
                throw new InvalidDataException("EMM material metadata must match the exact object, source/local clock and visibility of this sample.");
            int topologyCursor = 0;
            for (int local = 0; local < input.MaterialCount; local++)
            {
                var material = input.Materials[local];
                if (material.StartIndex != topologyCursor || material.IndexCount < 0 || material.IndexCount % 3 != 0
                    || !Enum.IsDefined(input.Bindings[local].SphereMode))
                    throw new InvalidDataException("Source-local material metadata has invalid authored topology or sphere mode.");
                topologyCursor = checked(topologyCursor + material.IndexCount);
            }
        }

        bool accessories = frame.Accessories.Count != 0 || objects.Values.Any(value => value.Kind == PmmDrawableKind.Accessory);
        var original = accessories ? SerializeTyped(frame, request, background) : SerializeModels(frame, request, background);
        bool typed = BinaryPrimitives.ReadUInt32LittleEndian(original.Frame.AsSpan(8)) == 3;
        var scene = frame.Scene.Surface.Snapshot;
        var byNativeId = objects.Values.ToDictionary(value => value.NativeId);
        var packetObjects = new List<MaterialPacketObject>(objects.Count);
        int vertexOffset;
        using (var reader = new BinaryReader(new MemoryStream(original.Frame), Encoding.UTF8))
        {
            reader.BaseStream.Position = typed ? 216 : 208;
            for (int i = 0; i < objects.Count; i++)
            {
                int start = checked((int)reader.BaseStream.Position);
                var binding = byNativeId[reader.ReadUInt64()];
                if (typed) reader.BaseStream.Position += 16; // kind, order and vertex range
                int materialStart = checked((int)reader.ReadUInt32()), count = checked((int)reader.ReadUInt32());
                reader.ReadUInt32(); SkipMaterialPacketString(reader); SkipMaterialPacketString(reader);
                int bones = checked((int)reader.ReadUInt32());
                for (int b = 0; b < bones; b++) { SkipMaterialPacketString(reader); reader.BaseStream.Position += 64; }
                int morphs = checked((int)reader.ReadUInt32());
                for (int m = 0; m < morphs; m++) { SkipMaterialPacketString(reader); reader.BaseStream.Position += 4; }
                if (binding.Kind == PmmDrawableKind.Accessory) reader.BaseStream.Position += 120;
                int length = checked((int)reader.BaseStream.Position - start);
                int vertices = typed ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.Frame.AsSpan(start + 20)))
                    : frame.SceneInputs[binding.ObjectId].SourceGeometry?.VertexCount ?? 0;
                packetObjects.Add(new(binding, original.Frame.AsSpan(start, length).ToArray(), materialStart, count, vertices, typed));
            }
            vertexOffset = checked((int)reader.BaseStream.Position);
        }
        int verticesCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.Frame.AsSpan(24)));
        int indicesCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.Frame.AsSpan(28)));
        int baseMaterialCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.Frame.AsSpan(32)));
        int baseTextureCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.Frame.AsSpan(36)));
        int materialOffset = checked(vertexOffset + verticesCount * 32 + indicesCount * 4);
        int textureOffset = materialOffset;
        foreach (var item in packetObjects) textureOffset = checked(textureOffset + item.MaterialCount * (item.Binding.Kind == PmmDrawableKind.Model ? 84 : 80));
        int uvOffset;
        using (var reader = new BinaryReader(new MemoryStream(original.Frame)))
        {
            reader.BaseStream.Position = textureOffset;
            for (int i = 0; i < baseTextureCount; i++)
            { reader.ReadUInt32(); reader.ReadUInt32(); int length = checked((int)reader.ReadUInt32()); reader.BaseStream.Position += length; }
            uvOffset = checked((int)reader.BaseStream.Position);
        }
        // Rebuild the same reference table used by the accessory serializer. Deduplication
        // is by immutable reference identity, never filename or equal pixels.
        var textures = new List<SceneTexture>(scene.Textures);
        var textureIndices = new Dictionary<SceneTexture, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < textures.Count; i++) textureIndices.TryAdd(textures[i], i);
        if (accessories)
            foreach (var accessory in frame.Accessories)
                foreach (var material in accessory.Inputs.Document!.Materials)
                    _ = ResolveAccessoryTexture(material.Material.Texture, objects[accessory.ObjectId], textures, textureIndices);
        if (textures.Count != baseTextureCount)
            throw new InvalidDataException("Validated accessory texture ownership changed during metadata promotion.");

        int MapTexture(SceneObjectMaterialInputs input, int local)
        {
            if (local == -1) return -1;
            if ((uint)local >= (uint)input.Textures.Count)
                throw new InvalidDataException("Material metadata refers outside its source-local texture table.");
            var texture = input.Textures[local];
            if (!textureIndices.TryGetValue(texture, out int index))
            { index = textures.Count; textures.Add(texture); textureIndices.Add(texture, index); }
            return index;
        }
        foreach (var model in models)
        {
            var input = frame.ObjectMaterialInputs[model.ObjectId];
            for (int local = 0; local < input.MaterialCount; local++)
            {
                var material = input.Materials[local]; var binding = input.Bindings[local];
                int diffuse = MapTexture(input, material.DiffuseTexture), toon = MapTexture(input, material.ToonTexture), sphere = MapTexture(input, binding.SphereTexture);
                if (!model.Visible) continue;
                var source = frame.SceneInputs[model.ObjectId].SourceGeometry!;
                int global = model.MaterialStart + local;
                if (input.MaterialCount != model.MaterialCount || local >= source.Materials.Count || material != source.Materials[local]
                    || diffuse != scene.Materials[global].DiffuseTexture || toon != scene.Materials[global].ToonTexture
                    || (binding with { SphereTexture = sphere }) != frame.Scene.Surface.Materials[global]
                    || input.MaterialFlags[local].SurfaceEnabled != frame.Scene.Materials[global].SurfaceEnabled)
                    throw new InvalidDataException("Visible material metadata differs from the same-sample authored source and composed ownership.");
            }
            if (model.Visible && input.MaterialCount != model.MaterialCount)
                throw new InvalidDataException("Visible source-local material count differs from the composed sample.");
        }
        int hiddenCount = models.Where(value => !value.Visible).Sum(value => frame.ObjectMaterialInputs[value.ObjectId].MaterialCount);
        int materialCount = checked(baseMaterialCount + hiddenCount);
        long frameEstimate = original.Frame.Length + (typed ? 0L : 8L + 16L * packetObjects.Count) + 84L * hiddenCount;
        foreach (var texture in textures.Skip(baseTextureCount)) frameEstimate += 12L + texture.Rgba.Length;
        if (materialCount > 1_000_000 || textures.Count > 4096 || frameEstimate > 1_073_741_824
            || 164L + 148L * materialCount + 24L * verticesCount > 16_777_216)
            throw new NotSupportedException("Source-local material metadata exceeds native packet limits.");

        IReadOnlyDictionary<Guid, int>? orders = !typed && frame.MixedDrawOrder.Count != 0 ? ResolveHostOrders(frame) : null;
        using var frameStream = new MemoryStream();
        using (var writer = new BinaryWriter(frameStream, Encoding.UTF8, leaveOpen: true))
        {
            var header = original.Frame.AsSpan(0, 208).ToArray();
            WriteCount(header, 8, 4); WriteCount(header, 32, materialCount); WriteCount(header, 36, textures.Count);
            writer.Write(header);
            uint flags = typed ? BinaryPrimitives.ReadUInt32LittleEndian(original.Frame.AsSpan(208))
                : scene.AdditionalUvs is null ? 0U : 1U;
            writer.Write(flags | 4U); writer.Write(frame.AccessoryIndexAfterModels ?? 0);
            int nativeMaterialCursor = 0, vertexCursor = 0, objectIndex = 0;
            foreach (var item in packetObjects)
            {
                bool hiddenModel = item.Binding.Kind == PmmDrawableKind.Model && !frame.SceneInputs[item.Binding.ObjectId].Visible;
                int count = hiddenModel ? frame.ObjectMaterialInputs[item.Binding.ObjectId].MaterialCount : item.MaterialCount;
                if (item.Typed)
                {
                    var record = item.Record.ToArray(); WriteCount(record, 24, nativeMaterialCursor); WriteCount(record, 28, count); writer.Write(record);
                }
                else
                {
                    writer.Write(item.Binding.NativeId); U(writer, 1); writer.Write(orders?[item.Binding.ObjectId] ?? objectIndex + 1);
                    U(writer, vertexCursor); U(writer, item.VertexCount); U(writer, nativeMaterialCursor); U(writer, count);
                    writer.Write(item.Record.AsSpan(16));
                }
                nativeMaterialCursor = checked(nativeMaterialCursor + count); vertexCursor = checked(vertexCursor + item.VertexCount);
                objectIndex++;
            }
            writer.Write(original.Frame.AsSpan(vertexOffset, materialOffset - vertexOffset));
            int originalMaterialOffset = materialOffset, drawIndexCursor = 0;
            foreach (var item in packetObjects)
            {
                if (item.Binding.Kind == PmmDrawableKind.Model && !frame.SceneInputs[item.Binding.ObjectId].Visible)
                {
                    var input = frame.ObjectMaterialInputs[item.Binding.ObjectId];
                    for (int local = 0; local < input.MaterialCount; local++)
                        WriteModelFrameMaterial(writer, item.Binding.NativeId, local, drawIndexCursor, 0,
                            input.Materials[local], input.Bindings[local], input.MaterialFlags[local], index => MapTexture(input, index));
                }
                int recordSize = item.Binding.Kind == PmmDrawableKind.Model ? 84 : 80;
                for (int local = 0; local < item.MaterialCount; local++)
                {
                    drawIndexCursor = checked(drawIndexCursor + (int)BinaryPrimitives.ReadUInt32LittleEndian(original.Frame.AsSpan(originalMaterialOffset + 16)));
                    writer.Write(original.Frame.AsSpan(originalMaterialOffset, recordSize)); originalMaterialOffset += recordSize;
                }
            }
            writer.Write(original.Frame.AsSpan(textureOffset, uvOffset - textureOffset));
            foreach (var texture in textures.Skip(baseTextureCount))
            { U(writer, texture.Width); U(writer, texture.Height); U(writer, texture.Rgba.Length); writer.Write(texture.Rgba); }
            writer.Write(original.Frame.AsSpan(uvOffset));
        }
        var frameBytes = frameStream.ToArray();
        using var visualStream = new MemoryStream();
        using (var writer = new BinaryWriter(visualStream, Encoding.UTF8, leaveOpen: true))
        {
            var header = original.Visual.AsSpan(0, 128).ToArray(); WriteCount(header, 8, 5); WriteCount(header, 12, materialCount);
            SHA256.HashData(frameBytes).CopyTo(header.AsSpan(20)); writer.Write(header);
            if (typed) writer.Write(original.Visual.AsSpan(128, 36));
            else
            {
                Vec(writer, (frame.Light?.Color ?? frame.Scene.Surface.Profile.LightAmbient) - new Vector3(.300000011920929f));
                Vec(writer, new Vector3(10)); Vec(writer, frame.Light?.Color ?? frame.Scene.Surface.Profile.LightSpecular);
            }
            int originalVisualOffset = typed ? 164 : 128;
            foreach (var item in packetObjects)
            {
                if (item.Binding.Kind == PmmDrawableKind.Model && !frame.SceneInputs[item.Binding.ObjectId].Visible)
                {
                    var input = frame.ObjectMaterialInputs[item.Binding.ObjectId];
                    var profile = ResolveModelProfile(frame, request, input.Profile);
                    for (int local = 0; local < input.MaterialCount; local++)
                    {
                        WriteModelVisualMaterial(writer, item.Binding.NativeId, local, input.Materials[local], input.Bindings[local], profile, input.Textures);
                        Vec(writer, input.Materials[local].Diffuse);
                    }
                }
                for (int local = 0; local < item.MaterialCount; local++)
                {
                    int size = typed ? 148 : 132; writer.Write(original.Visual.AsSpan(originalVisualOffset, size)); originalVisualOffset += size;
                    if (!typed) Vec(writer, scene.Materials[item.MaterialStart + local].Diffuse);
                }
            }
            writer.Write(original.Visual.AsSpan(originalVisualOffset));
        }
        return (frameBytes, visualStream.ToArray());
    }

    private static void SkipMaterialPacketString(BinaryReader reader)
    { int length = checked((int)reader.ReadUInt32()); reader.BaseStream.Position += length; }
}
