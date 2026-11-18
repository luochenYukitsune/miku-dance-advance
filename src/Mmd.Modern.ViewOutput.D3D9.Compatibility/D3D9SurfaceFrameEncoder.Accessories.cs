using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility;

internal sealed partial class D3D9SurfaceFrameEncoder
{
    private sealed record AccessoryRow(SceneProjectAccessoryFrame Sample, D3D9SurfaceObjectBinding Binding,
        int VertexStart, int MaterialStart, int IndexStart, IReadOnlyList<AccessoryMaterial> Materials);
    private sealed record AccessoryMaterial(XTextMaterialRange Source, uint Type, int Primary, int Sphere);

    private (byte[] Frame, byte[] Visual) SerializeTyped(OfflineFrame frame, ViewRenderRequest request, Vector4 background)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate();
        if (request.EffectiveQuality.SurfaceOrder != SurfaceDrawOrder.Original)
            throw new NotSupportedException("Traditional accessory scenes require original object draw order.");
        var accessories = frame.Accessories;
        var accessoryBindings = objects.Values.Where(value => value.Kind == PmmDrawableKind.Accessory).ToArray();
        if (accessories.Count != accessoryBindings.Length || accessories.Any(value => value is null)
            || accessories.Select(value => value.ObjectId).Distinct().Count() != accessories.Count
            || accessories.Any(value => !objects.TryGetValue(value.ObjectId, out var binding) || binding.Kind != PmmDrawableKind.Accessory))
            throw new ArgumentException("Accessory samples must cover the configured accessory identities exactly.", nameof(frame));
        if (frame.AccessoryIndexAfterModels is not { } rawSplit)
            throw new InvalidDataException("A traditional accessory scene requires its original raw accessory split.");
        // The legacy model serializer remains the single validator for model geometry, textures,
        // placement, UV streams and material state. Its bytes are promoted without resampling.
        var models = SerializeModels(frame, request, background);
        var scene = frame.Scene.Surface.Snapshot;
        var hostOrders = ResolveHostOrders(frame);
        int modelVertexOffset = LegacyObjectEnd(models.Frame, frame.RenderingStyles.Count);
        int modelIndexOffset = checked(modelVertexOffset + scene.VertexCount * 32);
        int modelMaterialOffset = checked(modelIndexOffset + scene.Indices.Length * 4);
        int modelTextureOffset = checked(modelMaterialOffset + scene.Materials.Count * 84);
        int modelUvOffset = modelTextureOffset;
        using (var reader = new BinaryReader(new MemoryStream(models.Frame)))
        {
            reader.BaseStream.Position = modelTextureOffset;
            foreach (var _ in scene.Textures)
            { reader.ReadUInt32(); reader.ReadUInt32(); int length = checked((int)reader.ReadUInt32()); reader.BaseStream.Position += length; }
            modelUvOffset = checked((int)reader.BaseStream.Position);
        }
        var textures = new List<SceneTexture>();
        var textureIndices = new Dictionary<SceneTexture, int>(ReferenceEqualityComparer.Instance);
        foreach (var texture in scene.Textures) { textureIndices.TryAdd(texture, textures.Count); textures.Add(texture); }
        int totalVertices = scene.VertexCount, totalIndices = scene.Indices.Length, totalMaterials = scene.Materials.Count;
        var rows = new List<AccessoryRow>(accessories.Count);
        double sourceFrame = frame.Sample.SampledSourceFrame.ToDouble();
        foreach (var item in accessories)
        {
            var binding = objects[item.ObjectId]; var inputs = item.Inputs;
            var source = inputs.SourceGeometry;
            var document = inputs.Document ?? throw new InvalidDataException("Accessory rendering requires the original parsed X document.");
            D3D9SurfacePreflight.ValidateScene(inputs.Source, request.EffectiveQuality, HasClassicPassInputs);
            if (!ReferenceEquals(document, item.Asset.Document) || item.Asset.Index != item.Sample.Index
                || sourceFrame != inputs.SourceFrame || item.Sample.Visible != inputs.Pose.Visible
                || item.Sample.Opacity != inputs.Pose.Opacity || source.AdditionalUvs is not null
                || document.Vertices.Count != source.VertexCount || document.Materials.Count != source.Materials.Count
                || source.Indices.Length != document.Indices.Count)
                throw new InvalidDataException("Accessory source, raw document and sample metadata do not describe the same frozen asset.");
            for (int i = 0; i < source.Indices.Length; i++)
                if (source.Indices[i] != document.Indices[i])
                    throw new InvalidDataException("Accessory source indices differ from the original X document.");
            for (int i = 0; i < source.VertexCount; i++)
                if (source.GetVertex(i) != document.Vertices[i])
                    throw new InvalidDataException("Traditional accessory geometry must preserve the original X local vertex stream.");
            _ = NormalMatrix(inputs.World);
            if (source.World != inputs.World || !float.IsFinite(inputs.Pose.Scale) || inputs.Pose.Scale <= 0
                || inputs.Pose.Opacity is < 0 or > 1 || !float.IsFinite(inputs.Pose.Opacity))
                throw new InvalidDataException("Accessory world, scale or opacity is invalid.");
            ValidateAccessoryParent(frame, item);
            var materials = new List<AccessoryMaterial>(document.Materials.Count);
            int indexCursor = 0;
            for (int local = 0; local < document.Materials.Count; local++)
            {
                var raw = document.Materials[local]; var neutral = source.Materials[local];
                if (raw.StartIndex != indexCursor || raw.IndexCount < 0 || raw.IndexCount % 3 != 0
                    || raw.IndexCount > source.Indices.Length - indexCursor || neutral.StartIndex != raw.StartIndex || neutral.IndexCount != raw.IndexCount)
                    throw new InvalidDataException("Accessory raw material ranges must cover its original local index stream.");
                indexCursor = checked(indexCursor + raw.IndexCount);
                var resolved = ResolveAccessoryTexture(raw.Material.Texture, binding, textures, textureIndices);
                materials.Add(new(raw, resolved.Type, resolved.Primary, resolved.Sphere));
            }
            if (indexCursor != source.Indices.Length)
                throw new InvalidDataException("Accessory materials do not cover the local index stream.");
            if (configuredSubsets.TryGetValue(binding.NativeId, out var subsets) && subsets.Any(value => value >= materials.Count))
                throw new ArgumentException("Accessory FX subset is outside its authored material range.", nameof(frame));
            rows.Add(new(item, binding, totalVertices, totalMaterials, totalIndices, materials));
            totalMaterials = checked(totalMaterials + materials.Count);
            if (item.Sample.Visible)
            { totalVertices = checked(totalVertices + source.VertexCount); totalIndices = checked(totalIndices + source.Indices.Length); }
        }
        if (objects.Count > 1_000_000 || totalVertices > 10_000_000 || totalIndices > 60_000_000
            || totalMaterials > 1_000_000 || textures.Count > 4096
            || 164L + 148L * totalMaterials + 24L * totalVertices > 16_777_216)
            throw new NotSupportedException("Accessory scene exceeds native packet limits.");
        long frameBytesEstimate = models.Frame.Length + 8L + 16L * frame.RenderingStyles.Count
            + 32L * (totalVertices - scene.VertexCount) + 4L * (totalIndices - scene.Indices.Length)
            + 80L * (totalMaterials - scene.Materials.Count)
            + (scene.AdditionalUvs is null ? 0 : 64L * (totalVertices - scene.VertexCount));
        foreach (var row in rows)
            frameBytesEstimate += 172L + Encoding.UTF8.GetByteCount(row.Binding.SourcePath) + Encoding.UTF8.GetByteCount(row.Binding.Name);
        foreach (var texture in textures.Skip(scene.Textures.Count)) frameBytesEstimate += 12L + texture.Rgba.Length;
        foreach (var pair in frame.AccessoryParentBoneWorldMatrices)
            if (frame.RenderingStyles.Any(value => value.ObjectId == pair.Key && !value.Visible))
                foreach (var bone in pair.Value) frameBytesEstimate += 68L + Encoding.UTF8.GetByteCount(objects[pair.Key].BoneNames[bone.Key]);
        if (frameBytesEstimate > 1_073_741_824) throw new NotSupportedException("Accessory scene exceeds the native frame byte limit.");

        using var frameStream = new MemoryStream();
        using (var writer = new BinaryWriter(frameStream, Encoding.UTF8, leaveOpen: true))
        {
            var header = models.Frame.AsSpan(0, 208).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 3);
            WriteCount(header, 20, objects.Count); WriteCount(header, 24, totalVertices); WriteCount(header, 28, totalIndices);
            WriteCount(header, 32, totalMaterials); WriteCount(header, 36, textures.Count);
            writer.Write(header); U(writer, (scene.AdditionalUvs is null ? 0 : 1) | (rows.Count == 0 ? 0 : 2)); writer.Write(rawSplit);
            int vertexCursor = 0;
            foreach (var row in frame.RenderingStyles)
            {
                var binding = objects[row.ObjectId]; var input = frame.SceneInputs[row.ObjectId];
                int vertices = input.SourceGeometry?.VertexCount ?? 0;
                writer.Write(binding.NativeId); U(writer, 1); writer.Write(hostOrders[row.ObjectId]);
                U(writer, vertexCursor); U(writer, vertices); U(writer, row.MaterialStart); U(writer, row.MaterialCount); U(writer, row.Visible ? 1 : 0);
                Str(writer, binding.SourcePath); Str(writer, binding.Name);
                var bones = ModelBones(frame, row.ObjectId); U(writer, bones.Count);
                foreach (var bone in bones.OrderBy(pair => pair.Key)) { Str(writer, binding.BoneNames[bone.Key]); Matrix(writer, bone.Value); }
                U(writer, 0); vertexCursor = checked(vertexCursor + vertices);
            }
            foreach (var row in rows)
            {
                var item = row.Sample; var input = item.Inputs; var pose = input.Pose;
                writer.Write(row.Binding.NativeId); U(writer, 2); writer.Write(hostOrders[item.ObjectId]);
                U(writer, row.VertexStart); U(writer, item.Sample.Visible ? input.SourceGeometry.VertexCount : 0);
                U(writer, row.MaterialStart); U(writer, row.Materials.Count); U(writer, item.Sample.Visible ? 1 : 0);
                Str(writer, row.Binding.SourcePath); Str(writer, row.Binding.Name); U(writer, 0); U(writer, 0);
                Matrix(writer, input.World); Vec(writer, pose.Translation); Vec(writer, pose.Rotation); F(writer, pose.Scale); F(writer, pose.Opacity);
                U(writer, pose.ShadowEnabled ? 1 : 0); U(writer, item.Sample.AddBlendEnabled ? 1 : 0); U(writer, item.Parent is null ? 0 : 1);
                writer.Write(item.Parent is { } parent ? objects[parent.ObjectId].NativeId : 0UL); writer.Write(item.Parent?.BoneIndex ?? -1);
            }
            writer.Write(models.Frame.AsSpan(modelVertexOffset, scene.VertexCount * 32));
            foreach (var row in rows.Where(value => value.Sample.Sample.Visible))
                for (int i = 0; i < row.Sample.Inputs.SourceGeometry.VertexCount; i++)
                { var vertex = row.Sample.Inputs.SourceGeometry.GetVertex(i); Vec(writer, vertex.Position); Vec(writer, vertex.Normal); Vec(writer, vertex.Uv); }
            writer.Write(models.Frame.AsSpan(modelIndexOffset, scene.Indices.Length * 4));
            foreach (var row in rows.Where(value => value.Sample.Sample.Visible))
                foreach (uint index in row.Sample.Inputs.SourceGeometry.Indices)
                {
                    if (index >= row.Sample.Inputs.SourceGeometry.VertexCount) throw new InvalidDataException("Accessory index leaves its local vertex range.");
                    writer.Write(checked(index + (uint)row.VertexStart));
                }
            writer.Write(models.Frame.AsSpan(modelMaterialOffset, scene.Materials.Count * 84));
            foreach (var row in rows)
                for (int local = 0; local < row.Materials.Count; local++)
                {
                    var raw = row.Materials[local]; var material = raw.Source.Material;
                    writer.Write(row.Binding.NativeId); U(writer, local);
                    U(writer, row.IndexStart + (row.Sample.Sample.Visible ? raw.Source.StartIndex : 0)); U(writer, row.Sample.Sample.Visible ? raw.Source.IndexCount : 0);
                    var input = row.Sample.Inputs.Source;
                    U(writer, (input.Surface.Materials[local].DoubleSided ? 1 : 0) | (input.Materials[local].SurfaceEnabled ? 2 : 0));
                    Vec(writer, material.Diffuse); Vec(writer, material.Specular); F(writer, material.SpecularPower); Vec(writer, material.Emissive);
                    writer.Write(raw.Type); writer.Write(raw.Primary); writer.Write(raw.Sphere);
                }
            foreach (var texture in textures) { U(writer, texture.Width); U(writer, texture.Height); U(writer, texture.Rgba.Length); writer.Write(texture.Rgba); }
            if (scene.AdditionalUvs is not null)
            {
                U(writer, scene.AdditionalUvs.ChannelCount); U(writer, totalVertices);
                writer.Write(models.Frame.AsSpan(modelUvOffset + 8));
                for (int i = scene.VertexCount; i < totalVertices; i++) { Vec(writer, Vector4.Zero); Vec(writer, Vector4.Zero); Vec(writer, Vector4.Zero); Vec(writer, Vector4.Zero); }
            }
        }
        var frameBytes = frameStream.ToArray();
        if (frameBytes.Length > 1_073_741_824) throw new NotSupportedException("FRM1 packet exceeds the native limit.");
        // Original SceneLight.Ambient stores the timeline light color; entering
        // the accessory pass subtracts 0.300000011920929f, without clamping.
        var ambient = (frame.Light?.Color ?? frame.Scene.Surface.Profile.LightAmbient) - new Vector3(.300000011920929f);
        var specular = frame.Light?.Color ?? frame.Scene.Surface.Profile.LightSpecular;
        var diffuse = new Vector3(10);
        using var visualStream = new MemoryStream();
        using (var writer = new BinaryWriter(visualStream, Encoding.UTF8, leaveOpen: true))
        {
            var header = models.Visual.AsSpan(0, 128).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 4); WriteCount(header, 12, totalMaterials); WriteCount(header, 16, totalVertices);
            SHA256.HashData(frameBytes).CopyTo(header.AsSpan(20)); writer.Write(header); Vec(writer, ambient); Vec(writer, diffuse); Vec(writer, specular);
            for (int i = 0; i < scene.Materials.Count; i++) { writer.Write(models.Visual.AsSpan(128 + i * 132, 132)); Vec(writer, scene.Materials[i].Diffuse); }
            foreach (var row in rows)
                for (int local = 0; local < row.Materials.Count; local++)
                {
                    var raw = row.Materials[local]; var material = raw.Source.Material;
                    bool opaque = material.Diffuse.W * row.Sample.Inputs.Pose.Opacity >= 1
                        && TextureOpaque(raw.Primary, textures) && TextureOpaque(raw.Sphere, textures);
                    writer.Write(row.Binding.NativeId); U(writer, local); U(writer, opaque ? 1 : 0);
                    var rgb = new Vector3(material.Diffuse.X, material.Diffuse.Y, material.Diffuse.Z);
                    Vec(writer, new Vector4(rgb * ambient + material.Emissive, material.Diffuse.W * row.Sample.Inputs.Pose.Opacity));
                    Vec(writer, new Vector4(material.Specular * .1f * specular, material.SpecularPower == 0 ? .1f : material.SpecularPower));
                    Vec(writer, Vector4.One); F(writer, 1f / 255);
                    Vec(writer, Vector4.One); Vec(writer, Vector4.Zero); Vec(writer, Vector4.One); Vec(writer, Vector4.Zero);
                    Vec(writer, new Vector4(rgb * .1f * diffuse, 1));
                }
            writer.Write(models.Visual.AsSpan(128 + scene.Materials.Count * 132));
            foreach (var row in rows.Where(value => value.Sample.Sample.Visible))
            {
                var normalWorld = NormalMatrix(row.Sample.Inputs.World);
                for (int i = 0; i < row.Sample.Inputs.SourceGeometry.VertexCount; i++)
                { var normal = row.Sample.Inputs.SourceGeometry.GetVertex(i).Normal; Vec(writer, Vector3.TransformNormal(normal, normalWorld)); Vec(writer, normal); }
            }
        }
        return (frameBytes, visualStream.ToArray());
    }

    private IReadOnlyDictionary<int, Matrix4x4> ModelBones(OfflineFrame frame, Guid id)
    {
        var input = frame.SceneInputs[id];
        var bones = input.Visible || !frame.AccessoryParentBoneWorldMatrices.TryGetValue(id, out var retained) ? input.BoneWorldMatrices : retained;
        var binding = objects[id];
        if (bones.Count != (input.Visible || frame.AccessoryParentBoneWorldMatrices.ContainsKey(id) ? binding.BoneNames.Count : 0)
            || bones.Keys.Any(value => (uint)value >= (uint)binding.BoneNames.Count))
            throw new InvalidDataException("Accessory parents require all requested native bone identities from the same sample.");
        return bones;
    }

    private void ValidateAccessoryParent(OfflineFrame frame, SceneProjectAccessoryFrame item)
    {
        if (item.Parent is not { } parent)
        {
            if (item.Inputs.ParentWorld != Matrix4x4.Identity)
                throw new InvalidDataException("An attached accessory is missing its resolved native parent identity.");
            return;
        }
        if (!objects.TryGetValue(parent.ObjectId, out var binding) || binding.Kind != PmmDrawableKind.Model
            || !frame.SceneInputs.ContainsKey(parent.ObjectId) || parent.BoneIndex < 0 || parent.BoneIndex >= binding.BoneNames.Count
            || binding.BoneNames[parent.BoneIndex] != parent.BoneName)
            throw new InvalidDataException("Accessory parent identity or native bone name is unavailable.");
        var bones = ModelBones(frame, parent.ObjectId);
        if (!bones.TryGetValue(parent.BoneIndex, out var world) || world != item.Inputs.ParentWorld)
            throw new InvalidDataException("Accessory parent matrix differs from the same-sample solved native bone.");
    }

    private IReadOnlyDictionary<Guid, int> ResolveHostOrders(OfflineFrame frame)
    {
        if (frame.MixedDrawOrder.Count != objects.Count || frame.MixedDrawOrder.Any(value => value is null)
            || frame.MixedDrawOrder.Select(value => value.ObjectId).Distinct().Count() != objects.Count
            || frame.MixedDrawOrder.Any(value => !objects.TryGetValue(value.ObjectId, out var binding) || binding.Kind != value.Kind))
            throw new InvalidDataException("Traditional rendering requires a complete same-sample mixed object table.");
        int split = frame.AccessoryIndexAfterModels!.Value;
        // Packet row order (models in rendering-style order, then accessories in table order) is what the native stable
        // draw sort falls back to on equal orders, so renumbering breaks ties the same way.
        var packetOrder = frame.RenderingStyles.Select(row => row.ObjectId).Concat(frame.Accessories.Select(item => item.ObjectId)).ToList();
        var result = ComputeHostOrders(frame.MixedDrawOrder, frame.Accessories.Count, split, frame.RenderingStyles.Count, packetOrder);
        foreach (var value in frame.MixedDrawOrder)
        {
            if (value.Kind == PmmDrawableKind.Accessory)
            {
                var item = frame.Accessories.Single(item => item.ObjectId == value.ObjectId);
                if (value.StoredIndex != item.Sample.Index || value.Visible != item.Sample.Visible || value.AddBlendEnabled != item.Sample.AddBlendEnabled)
                    throw new InvalidDataException("Mixed accessory metadata differs from the same frozen sample.");
            }
            else if (value.Visible != frame.SceneInputs[value.ObjectId].Visible)
                throw new InvalidDataException("Mixed model visibility differs from the same frozen sample.");
        }
        return result;
    }

    /// <summary>Host (MME) orders, one signed value per object that no other object shares. The host keys its
    /// order-to-object map by the signed value (ExpGetCurrentObject returns it), so a shared value would hand one
    /// object's effects to another; the value is the object's identity as far as that map can tell. (Equal absolute
    /// values, such as model raw 0 and the last pre-split accessory, are what the original loader produces and are
    /// kept.)
    /// Imported objects use the original PMM loader's formulas on their raw bytes. Models added after the import have no
    /// byte and get the next values above every assigned one, in DrawOrderIndex order (never 0, MME's "no current
    /// object"; not limited by the byte range). When that still yields a shared value (non-canonical, sparse bytes, or
    /// a post-split accessory shifted onto an imported model by the larger model count), every object is renumbered
    /// densely in the native draw order instead, in the original loader's layout: pre-split accessories -1..-b, then
    /// models b+1.., then post-split accessories. Each object keeps its group and its position within it, so the
    /// drawn order is unchanged: equal orders within a group keep the order of <paramref name="packetOrder"/>, the
    /// object rows of the native packet, which is the tie-break of the native stable sort.</summary>
    internal static Dictionary<Guid, int> ComputeHostOrders(IReadOnlyList<SceneProjectDrawItem> drawOrder, int accessoryCount, int split, int modelCount,
        IReadOnlyList<Guid> packetOrder)
    {
        var result = new Dictionary<Guid, int>();
        foreach (var value in drawOrder.Where(value => !value.IsAddedAfterImport))
        {
            // The original PMM loader copies the raw model/accessory byte without renumbering.
            // Its native getters use different formulas from the core's neutral sorted keys.
            int raw = value.SourceDrawOrderIndex;
            result.Add(value.ObjectId, value.Kind == PmmDrawableKind.Model ? checked(raw + Math.Min(accessoryCount, split))
                : raw < split ? checked(-raw - 1) : checked(raw + modelCount + 1));
        }
        int next = Math.Max(1, result.Values.DefaultIfEmpty(0).Max() + 1);
        foreach (var value in drawOrder.Where(value => value.IsAddedAfterImport).OrderBy(value => value.DrawOrderIndex))
            result.Add(value.ObjectId, next++);
        return result.Values.Distinct().Count() == result.Count ? result : DenseHostOrders(drawOrder, result, packetOrder);
    }

    private static Dictionary<Guid, int> DenseHostOrders(IReadOnlyList<SceneProjectDrawItem> drawOrder, Dictionary<Guid, int> original,
        IReadOnlyList<Guid> packetOrder)
    {
        // Native draw groups (session.cpp originalDrawBefore): accessories with a negative order first, by descending
        // order; then models; then the remaining accessories, both by ascending order. The sort is a std::stable_sort
        // over packet rows, so ties keep packet row order, not the PMM draw table's.
        var row = packetOrder.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index);
        if (row.Count != drawOrder.Count || drawOrder.Any(value => !row.ContainsKey(value.ObjectId)))
            throw new InvalidDataException("Host order renumbering requires the packet row of every drawn object.");
        int Group(SceneProjectDrawItem value) => value.Kind == PmmDrawableKind.Model ? 1 : original[value.ObjectId] < 0 ? 0 : 2;
        var sorted = drawOrder.OrderBy(Group)
            .ThenBy(value => Group(value) == 0 ? -original[value.ObjectId] : original[value.ObjectId])
            .ThenBy(value => row[value.ObjectId]).ToList();
        var result = new Dictionary<Guid, int>();
        int before = 0, after = sorted.Count(value => Group(value) == 0);
        foreach (var value in sorted)
            result.Add(value.ObjectId, Group(value) == 0 ? -++before : ++after);
        return result;
    }

    private static (uint Type, int Primary, int Sphere) ResolveAccessoryTexture(string token, D3D9SurfaceObjectBinding binding,
        List<SceneTexture> textures, Dictionary<SceneTexture, int> indices)
    {
        var parsed = D3D9AccessoryTextureToken.Parse(token);
        int Resolve(string value)
        {
            if (!binding.AccessoryTextures.TryGetValue(value, out var texture))
                throw new InvalidDataException($"Accessory texture resource is missing: {value}");
            if (!indices.TryGetValue(texture, out var index)) { index = textures.Count; textures.Add(texture); indices.Add(texture, index); }
            return index;
        }
        return (parsed.Type, parsed.Primary is null ? -1 : Resolve(parsed.Primary), parsed.Sphere is null ? -1 : Resolve(parsed.Sphere));
    }

    private static int LegacyObjectEnd(byte[] frame, int count)
    {
        using var reader = new BinaryReader(new MemoryStream(frame), Encoding.UTF8); reader.BaseStream.Position = 208;
        void SkipString() { int byteCount = checked((int)reader.ReadUInt32()); reader.BaseStream.Position += byteCount; }
        for (int i = 0; i < count; i++)
        {
            reader.BaseStream.Position += 20; SkipString(); SkipString(); int bones = checked((int)reader.ReadUInt32());
            for (int b = 0; b < bones; b++) { SkipString(); reader.BaseStream.Position += 64; }
            int morphs = checked((int)reader.ReadUInt32()); for (int m = 0; m < morphs; m++) { SkipString(); reader.BaseStream.Position += 4; }
        }
        return checked((int)reader.BaseStream.Position);
    }
    private static bool TextureOpaque(int index, IReadOnlyList<SceneTexture> textures)
    {
        if (index < 0) return true;
        var pixels = textures[index].Rgba;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 255) return false;
        return true;
    }
    private static void WriteCount(byte[] header, int offset, int count) => BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset), checked((uint)count));
}
