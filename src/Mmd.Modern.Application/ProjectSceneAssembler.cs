using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
namespace Mmd.Modern.Application;

/// <summary>Combines models in explicit source draw order, preserving each material's triangle range.</summary>
public static class ProjectSceneAssembler
{
    private const string ParallelOutlineCompositionDegree = "Mmd.Modern.Application.ParallelOutlineComposition.Degree";

    private static int OutlineCompositionDegree()
    {
        int maximum = Math.Min(16, Math.Max(1, Environment.ProcessorCount));
        object? value = AppContext.GetData(ParallelOutlineCompositionDegree);
        int requested = value is int number ? number : Math.Min(4, maximum);
        if (value is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            requested = parsed;
        return Math.Clamp(requested, 1, maximum);
    }

    /// <summary>One owner's latest composition only. The immutable baseline/storage check makes an
    /// abandoned candidate harmless: it cannot be used to patch the owner's still-published frame.
    /// Published immutable chunks share unchanged payloads; only changed actors are transformed.</summary>
    internal sealed class CompositionCache(SceneSnapshot? initialTextureSource)
    {
        private OriginalFrameScene? frame;
        private Guid[] ids = [];
        private OriginalFrameScene[] sources = [], models = [];
        private Vector3 lightColor, incomingDirection;

        internal void Clear() { initialTextureSource = null; frame = null; ids = []; sources = []; models = []; }

        internal OriginalFrameScene Compose(OriginalFrameScene? baseline, IReadOnlyList<Guid> actorIds,
            IReadOnlyList<OriginalFrameScene> actorSources, IReadOnlyList<OriginalFrameScene> inputs,
            Vector3 ambient, Vector3 incoming)
        {
            if (inputs.Count == 0 || actorIds.Count != inputs.Count || actorSources.Count != inputs.Count)
                throw new ArgumentException("Composition needs one source and identity per visible actor.", nameof(inputs));
            bool compatible = baseline is not null && frame is not null
                && ReferenceEquals(baseline.Surface.Snapshot, frame.Surface.Snapshot) && ReferenceEquals(baseline.Outline, frame.Outline)
                && ids.Length == inputs.Count && SameBits(lightColor, ambient) && SameBits(incomingDirection, incoming);
            if (compatible)
                for (int i = 0; i < inputs.Count; i++)
                    if (ids[i] != actorIds[i] || !ReferenceEquals(sources[i], actorSources[i]) || !SameTopology(models[i], inputs[i]))
                    { compatible = false; break; }
            if (!compatible)
            {
                var textureSource = initialTextureSource;
                initialTextureSource = null;
                return Remember(Combine(inputs, ambient, incoming, textureSource), actorIds, actorSources, inputs, ambient, incoming);
            }

            var ranges = new List<(int Start, int Count)>(); var dirtyModels = new List<int>();
            bool changed = false, seedsChanged = false, identityGeometry = true; int offset = 0;
            for (int i = 0; i < inputs.Count; i++)
            {
                var prior = models[i]; var next = inputs[i];
                var oldSnapshot = prior.Surface.Snapshot; var snapshot = next.Surface.Snapshot;
                bool sameWorld = SameBits(oldSnapshot.World, snapshot.World);
                bool geometryChanged = !oldSnapshot.SharesVertexStorage(snapshot) || !sameWorld;
                if (geometryChanged)
                {
                    ranges.Add((offset, snapshot.VertexCount)); dirtyModels.Add(i);
                    identityGeometry &= snapshot.World == Matrix4x4.Identity;
                }
                bool seedChanged = !ReferenceEquals(oldSnapshot.RawRadialSeeds, snapshot.RawRadialSeeds) || !sameWorld;
                seedsChanged |= seedChanged;
                changed |= geometryChanged || seedChanged || !ReferenceEquals(oldSnapshot.AdditionalUvs, snapshot.AdditionalUvs)
                    || !oldSnapshot.Materials.SequenceEqual(snapshot.Materials) || !prior.Surface.Materials.SequenceEqual(next.Surface.Materials)
                    || !prior.Outline!.Materials.SequenceEqual(next.Outline!.Materials) || !SameFlagIdentities(prior, next)
                    || (i == 0 && !ReferenceEquals(prior.Surface.Profile, next.Surface.Profile));
                offset = checked(offset + snapshot.VertexCount);
            }
            if (!changed) return Remember(baseline!, actorIds, actorSources, inputs, ambient, incoming);

            var baselineFrame = baseline!;
            SceneSnapshot updated;
            using (PoseEvaluationMetrics.Measure("composition.vertices"))
            {
                if (ranges.Count > 0 && identityGeometry)
                {
                    var replacements = new (int Start, SceneSnapshot Source)[ranges.Count];
                    for (int range = 0; range < replacements.Length; range++)
                        replacements[range] = (ranges[range].Start, inputs[dirtyModels[range]].Surface.Snapshot);
                    updated = baselineFrame.Surface.Snapshot.WithIdentityVertexChunks(replacements);
                }
                else
                    updated = baselineFrame.Surface.Snapshot.WithComposedVertexChunks(ranges, (range, rangeOffset, destination) =>
                        CopyTransformedVertices(inputs[dirtyModels[range]].Surface.Snapshot, destination, rangeOffset));
            }
            using (PoseEvaluationMetrics.Measure("composition.additional-uvs"))
                updated = updated.WithAdditionalUvs(CombineAdditionalUvs(inputs, offset, baselineFrame.Surface.Snapshot.AdditionalUvs));
            if (seedsChanged)
                using (PoseEvaluationMetrics.Measure("composition.radial-seeds"))
                    updated = updated.WithRawRadialSeeds(CombineRawRadialSeeds(inputs, offset));
            List<OriginalStyleMaterialBinding> bindings;
            List<OriginalOutlineMaterial> edgeMaterials;
            List<OriginalFrameMaterialFlags> flags;
            using (PoseEvaluationMetrics.Measure("composition.materials"))
            {
                int materialCount = baselineFrame.Surface.Materials.Count;
                var materials = new List<SceneMaterial>(materialCount); bindings = new(materialCount);
                edgeMaterials = new(materialCount); flags = new(materialCount);
                var textures = new Dictionary<SceneTexture, int>(baselineFrame.Surface.Snapshot.Textures.Count, ReferenceEqualityComparer.Instance);
                for (int index = 0; index < baselineFrame.Surface.Snapshot.Textures.Count; index++)
                    textures.TryAdd(baselineFrame.Surface.Snapshot.Textures[index], index);
                int indexBase = 0;
                foreach (var input in inputs)
                {
                    var snapshot = input.Surface.Snapshot;
                    int Texture(int index) => index < 0 ? -1 : textures[snapshot.Textures[index]];
                    foreach (var material in snapshot.Materials)
                        materials.Add(material with { StartIndex = checked(material.StartIndex + indexBase), DiffuseTexture = Texture(material.DiffuseTexture), ToonTexture = Texture(material.ToonTexture) });
                    foreach (var binding in input.Surface.Materials) bindings.Add(binding with { SphereTexture = Texture(binding.SphereTexture) });
                    edgeMaterials.AddRange(input.Outline!.Materials); flags.AddRange(input.Materials);
                    indexBase = checked(indexBase + snapshot.Indices.Length);
                }
                if (!materials.SequenceEqual(updated.Materials)) updated = updated.WithMaterials(materials);
            }
            OriginalOutlineData outline;
            using (PoseEvaluationMetrics.Measure("composition.outline"))
            {
                outline = baselineFrame.Outline!.WithSurfaceVertexChunks(updated, ranges, edgeMaterials, OutlineCompositionDegree());
            }
            var result = new OriginalFrameScene(new(updated, bindings.SequenceEqual(baselineFrame.Surface.Materials) ? baselineFrame.Surface.Materials : bindings,
                baselineFrame.Surface.Profile), outline, baselineFrame.OutlineOptions, baselineFrame.Options, flags);
            return Remember(result, actorIds, actorSources, inputs, ambient, incoming);
        }

        private OriginalFrameScene Remember(OriginalFrameScene result, IReadOnlyList<Guid> actorIds,
            IReadOnlyList<OriginalFrameScene> actorSources, IReadOnlyList<OriginalFrameScene> inputs, Vector3 ambient, Vector3 incoming)
        {
            // Copy the small owner tables; caller list mutation cannot alter provenance.
            ids = actorIds.ToArray(); sources = actorSources.ToArray(); models = inputs.ToArray();
            lightColor = ambient; incomingDirection = incoming; frame = result; return result;
        }
        private static bool SameTopology(OriginalFrameScene first, OriginalFrameScene second)
        {
            var a = first.Surface.Snapshot; var b = second.Surface.Snapshot;
            if (second.Outline is null || a.VertexCount != b.VertexCount || !a.SharesIndexStorage(b)
                || a.Materials.Count != b.Materials.Count || a.Textures.Count != b.Textures.Count) return false;
            for (int i = 0; i < a.Materials.Count; i++)
                if (a.Materials[i].StartIndex != b.Materials[i].StartIndex || a.Materials[i].IndexCount != b.Materials[i].IndexCount) return false;
            for (int i = 0; i < a.Textures.Count; i++)
                if (!ReferenceEquals(a.Textures[i], b.Textures[i])) return false;
            return true;
        }
        private static bool SameFlagIdentities(OriginalFrameScene first, OriginalFrameScene second)
        {
            if (first.Materials.Count != second.Materials.Count) return false;
            for (int index = 0; index < first.Materials.Count; index++)
                if (!ReferenceEquals(first.Materials[index], second.Materials[index])) return false;
            return true;
        }
        private static bool SameBits<T>(T first, T second) where T : unmanaged
            => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref first, 1))
                .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref second, 1)));
    }

    // One description belongs to each live combined UV stream, with no strongly rooted history.
    // Keep only source UV identities and segment lengths: retaining source snapshots here would
    // also retain their superseded deformed vertex arrays. UV topology means this ordered,
    // vertex-length partition; triangle indices and World do not participate in UV concatenation.
    private static readonly ConditionalWeakTable<SceneAdditionalUvs,AdditionalUvComposition> additionalUvCompositions=new();
    private readonly record struct AdditionalUvSegment(SceneAdditionalUvs? Stream,int VertexCount);
    private sealed class AdditionalUvComposition
    {
        private readonly AdditionalUvSegment[] segments;
        private readonly int vertexCount;
        internal AdditionalUvComposition(IReadOnlyList<OriginalFrameScene> models,int vertexCount)
        {
            this.vertexCount=vertexCount;segments=new AdditionalUvSegment[models.Count];
            for(int index=0;index<segments.Length;index++)
            {
                var snapshot=models[index].Surface.Snapshot;
                segments[index]=new(snapshot.AdditionalUvs,snapshot.VertexCount);
            }
        }
        internal bool Matches(IReadOnlyList<OriginalFrameScene> models,int count)
        {
            if(count!=vertexCount||models.Count!=segments.Length)return false;
            for(int index=0;index<segments.Length;index++)
            {
                var snapshot=models[index].Surface.Snapshot;
                if(snapshot.VertexCount!=segments[index].VertexCount
                    ||!ReferenceEquals(snapshot.AdditionalUvs,segments[index].Stream))return false;
            }
            return true;
        }
    }

    public static OriginalFrameScene Combine(IReadOnlyList<OriginalFrameScene> models,Vector3 lightColor,Vector3 incomingDirection)
        => Combine(models, lightColor, incomingDirection, null);

    private static OriginalFrameScene Combine(IReadOnlyList<OriginalFrameScene> models, Vector3 lightColor, Vector3 incomingDirection,
        SceneSnapshot? textureSource)
    {
        ArgumentNullException.ThrowIfNull(models);if(models.Count==0)throw new ArgumentException("A project needs a visible mesh.",nameof(models));
        int vertexCount=0,indexCount=0,materialCount=0,textureCount=0;
        foreach(var model in models)
        {
            if(model.Outline is null)throw new ArgumentException("Project models require outline geometry even when outline drawing is disabled.",nameof(models));
            vertexCount=checked(vertexCount+model.Surface.Snapshot.VertexCount);
            indexCount=checked(indexCount+model.Surface.Snapshot.Indices.Length);
            materialCount=checked(materialCount+model.Surface.Snapshot.Materials.Count);
            textureCount=checked(textureCount+model.Surface.Snapshot.Textures.Count);
        }
        var vertices=new SceneVertex[vertexCount];var indices=new uint[indexCount];var materials=new List<SceneMaterial>(materialCount);
        var textures=new List<SceneTexture>(textureCount);var textureIdentities=new Dictionary<SceneTexture,int>(textureCount,ReferenceEqualityComparer.Instance);
        var bindings=new List<OriginalStyleMaterialBinding>(materialCount);
        var edges=new OriginalOutlineVertex[vertexCount];var edgeMaterials=new List<OriginalOutlineMaterial>(materialCount);var flags=new List<OriginalFrameMaterialFlags>(materialCount);
        int vertexOffset=0,indexOffset=0;
        foreach(var model in models)
        {
            var snapshot=model.Surface.Snapshot;
            if(model.Outline is null)throw new ArgumentException("Project models require outline geometry even when outline drawing is disabled.",nameof(models));
            int vertexBase=vertexOffset,indexBase=indexOffset,materialBase=materials.Count;
            var textureMap=new int[snapshot.Textures.Count];
            for(int i=0;i<textureMap.Length;i++)
            {
                var texture=snapshot.Textures[i];
                if(!textureIdentities.TryGetValue(texture,out int existing))
                {existing=textures.Count;textureIdentities.Add(texture,existing);textures.Add(texture);}
                textureMap[i]=existing;
            }
            int Texture(int value)=>value<0?-1:textureMap[value];
            CopyTransformedVertices(snapshot,vertices.AsSpan(vertexOffset,snapshot.VertexCount));
            vertexOffset=checked(vertexOffset+snapshot.VertexCount);
            foreach(uint index in snapshot.Indices)indices[indexOffset++]=checked(index+(uint)vertexBase);
            foreach(var material in snapshot.Materials)materials.Add(material with{StartIndex=checked(material.StartIndex+indexBase),DiffuseTexture=Texture(material.DiffuseTexture),ToonTexture=Texture(material.ToonTexture)});
            foreach(var material in model.Surface.Materials)bindings.Add(material with{SphereTexture=Texture(material.SphereTexture)});
            for(int i=0;i<model.Outline.Vertices.Count;i++)
            {var edge=model.Outline.Vertices[i];var vertex=vertices[vertexBase+i];edges[vertexBase+i]=edge with{Position=vertex.Position,Normal=vertex.Normal,SelectedMaterialIndex=checked(edge.SelectedMaterialIndex+materialBase)};}
            edgeMaterials.AddRange(model.Outline.Materials);flags.AddRange(model.Materials);
        }
        var scene=new SceneSnapshot(vertices,indices,materials,textures,world:null,camera:null,lightDirection:incomingDirection,
            additionalUvs:CombineAdditionalUvs(models,vertexCount),rawRadialSeeds:CombineRawRadialSeeds(models,vertexCount),
            textureSource:textureSource,dynamicVertices:models.Any(m=>m.Surface.Snapshot.HasDynamicVertices));
        var profile=models[0].Surface.Profile with{LightAmbient=lightColor,LightSpecular=lightColor};
        return new(new(scene,bindings,profile),new(edges,edgeMaterials),models[0].OutlineOptions,materials:flags);
    }

    /// <summary>Only pose changes; the source model order and topology must match the baseline.</summary>
    public static OriginalFrameScene WithVertices(OriginalFrameScene baseline,IReadOnlyList<OriginalFrameScene> models)
    {
        ArgumentNullException.ThrowIfNull(baseline);ArgumentNullException.ThrowIfNull(models);
        int count=models.Sum(m=>m.Surface.Snapshot.VertexCount);
        if(count!=baseline.Surface.Snapshot.VertexCount||models.Sum(m=>m.Surface.Materials.Count)!=baseline.Surface.Materials.Count)
            throw new ArgumentException("Project topology changed; rebuild its baseline.",nameof(models));
        ValidateSeedTopology(baseline.Surface.Snapshot,models);
        var vertices=new SceneVertex[count];var edges=new OriginalOutlineVertex[count];int offset=0,indexBase=0;
        int materialCount=baseline.Surface.Materials.Count;
        var materials=new List<SceneMaterial>(materialCount);var bindings=new List<OriginalStyleMaterialBinding>(materialCount);var edgeMaterials=new List<OriginalOutlineMaterial>(materialCount);
        var flags=new List<OriginalFrameMaterialFlags>(materialCount);
        var textureIdentities=new Dictionary<SceneTexture,int>(baseline.Surface.Snapshot.Textures.Count,ReferenceEqualityComparer.Instance);
        for(int index=0;index<baseline.Surface.Snapshot.Textures.Count;index++)textureIdentities.TryAdd(baseline.Surface.Snapshot.Textures[index],index);
        foreach(var model in models)
        {
            var snapshot=model.Surface.Snapshot;
            CopyTransformedVertices(snapshot,vertices.AsSpan(offset,snapshot.VertexCount));
            for(int i=0;i<snapshot.VertexCount;i++)
            {var vertex=vertices[offset];edges[offset]=baseline.Outline!.Vertices[offset] with{Position=vertex.Position,Normal=vertex.Normal};offset++;}
            int Texture(int value)
            {
                if(value<0)return -1;
                if(textureIdentities.TryGetValue(snapshot.Textures[value],out int index))return index;
                throw new ArgumentException("Project textures changed; rebuild its baseline.",nameof(models));
            }
            foreach(var material in snapshot.Materials)materials.Add(material with{StartIndex=checked(material.StartIndex+indexBase),DiffuseTexture=Texture(material.DiffuseTexture),ToonTexture=Texture(material.ToonTexture)});
            foreach(var binding in model.Surface.Materials)bindings.Add(binding with{SphereTexture=Texture(binding.SphereTexture)});
            edgeMaterials.AddRange(model.Outline!.Materials);flags.AddRange(model.Materials);indexBase+=snapshot.Indices.Length;
        }
        var updated=baseline.Surface.Snapshot.WithDeformedVertices(vertices)
            .WithAdditionalUvs(CombineAdditionalUvs(models,count,baseline.Surface.Snapshot.AdditionalUvs))
            .WithRawRadialSeeds(CombineRawRadialSeeds(models,count));
        if(!materials.SequenceEqual(updated.Materials))updated=updated.WithMaterials(materials);
        return new(new(updated,bindings.SequenceEqual(baseline.Surface.Materials)?baseline.Surface.Materials:bindings,baseline.Surface.Profile),
            new(edges,edgeMaterials.SequenceEqual(baseline.Outline!.Materials)?baseline.Outline.Materials:edgeMaterials),baseline.OutlineOptions,baseline.Options,flags);
    }
    /// <summary>Bakes source-local rest seeds through each remaining World exactly once.
    /// Zero segments represent inactive sources, not proof of authored Endfield inputs.</summary>
    private static SceneRawRadialSeeds? CombineRawRadialSeeds(IReadOnlyList<OriginalFrameScene> models,int vertexCount)
    {
        bool present=false;
        foreach(var model in models)
            if(model.Surface.Snapshot.RawRadialSeeds is not null){present=true;break;}
        if(!present)return null;
        var values=new Vector3[vertexCount];int offset=0;
        foreach(var model in models)
        {
            var snapshot=model.Surface.Snapshot;
            int count=snapshot.VertexCount;
            if(count>vertexCount-offset)throw new ArgumentException("Project vertex layout changed; rebuild its baseline.",nameof(models));
            if(snapshot.RawRadialSeeds is {} stream)
            {
                if(stream.Vertices.Length!=count)throw new ArgumentException("Raw radial seeds must preserve source vertex order and count.",nameof(models));
                if(snapshot.World==Matrix4x4.Identity)stream.Vertices.CopyTo(values.AsSpan(offset,count));
                else for(int i=0;i<count;i++)values[offset+i]=Vector3.TransformNormal(stream.Vertices[i],snapshot.World);
            }
            offset=checked(offset+count);
        }
        if(offset!=vertexCount)throw new ArgumentException("Project vertex layout changed; rebuild its baseline.",nameof(models));
        return new(values);
    }
    /// <summary>Checks constructive topology before reusing global vertex identities. Equal counts alone
    /// cannot prove source correspondence; the caller must also preserve source order and vertex identity.</summary>
    private static void ValidateSeedTopology(SceneSnapshot baseline,IReadOnlyList<OriginalFrameScene> models)
    {
        bool requiresCheck=baseline.RawRadialSeeds is not null;
        if(!requiresCheck)
            foreach(var model in models)
                if(model.Surface.Snapshot.RawRadialSeeds is not null){requiresCheck=true;break;}
        if(!requiresCheck)return;
        int vertexBase=0,indexBase=0,materialBase=0;
        foreach(var model in models)
        {
            var snapshot=model.Surface.Snapshot;
            if(snapshot.Indices.Length>baseline.Indices.Length-indexBase
                ||snapshot.Materials.Count>baseline.Materials.Count-materialBase)
                throw new ArgumentException("Project topology changed; rebuild its baseline.",nameof(models));
            for(int i=0;i<snapshot.Indices.Length;i++)
                if(baseline.Indices[indexBase+i]!=checked(snapshot.Indices[i]+(uint)vertexBase))
                    throw new ArgumentException("Project triangle identities changed; rebuild its baseline.",nameof(models));
            foreach(var material in snapshot.Materials)
            {
                var expected=baseline.Materials[materialBase++];
                if(expected.StartIndex!=checked(material.StartIndex+indexBase)||expected.IndexCount!=material.IndexCount)
                    throw new ArgumentException("Project material ranges changed; rebuild its baseline.",nameof(models));
            }
            vertexBase=checked(vertexBase+snapshot.VertexCount);
            indexBase=checked(indexBase+snapshot.Indices.Length);
        }
        if(vertexBase!=baseline.VertexCount||indexBase!=baseline.Indices.Length||materialBase!=baseline.Materials.Count)
            throw new ArgumentException("Project topology changed; rebuild its baseline.",nameof(models));
    }
    private static SceneAdditionalUvs? CombineAdditionalUvs(IReadOnlyList<OriginalFrameScene> models,int vertexCount,
        SceneAdditionalUvs? previous=null)
    {
        if(previous is not null&&additionalUvCompositions.TryGetValue(previous,out var composition)
            &&composition.Matches(models,vertexCount))return previous;
        int channelCount=0;
        foreach(var model in models)channelCount=Math.Max(channelCount,model.Surface.Snapshot.AdditionalUvs?.ChannelCount??0);
        if(channelCount==0)return null;
        var values=new SceneAdditionalUvVertex[vertexCount];int offset=0;
        foreach(var model in models)
        {
            var snapshot=model.Surface.Snapshot;
            if(snapshot.AdditionalUvs is {} stream)stream.Vertices.CopyTo(values.AsSpan(offset));
            offset+=snapshot.VertexCount;
        }
        var combined=new SceneAdditionalUvs(channelCount,values);
        additionalUvCompositions.Add(combined,new(models,vertexCount));
        return combined;
    }
    private static void CopyTransformedVertices(SceneSnapshot snapshot,Span<SceneVertex> destination,int sourceStart=0)
    {
        var world=snapshot.World;
        if(world==Matrix4x4.Identity){snapshot.CopyVertices(sourceStart,destination);return;}
        if(!Matrix4x4.Invert(world,out var inverse))throw new ArgumentException("Project model world transform must be invertible.",nameof(world));
        var normalTransform=Matrix4x4.Transpose(inverse);
        for(int index=0;index<destination.Length;index++)
        {
            var vertex=snapshot.GetVertex(sourceStart+index);
            var normal=Vector3.TransformNormal(vertex.Normal,normalTransform);
            destination[index]=vertex with{Position=Vector3.Transform(vertex.Position,world),Normal=normal.LengthSquared()>1e-12f?Vector3.Normalize(normal):Vector3.Zero};
        }
    }
}
