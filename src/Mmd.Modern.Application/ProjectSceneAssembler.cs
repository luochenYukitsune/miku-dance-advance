using System.Collections.ObjectModel;
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
        private const byte DirtySnapshotMaterials = 1 << 0;
        private const byte DirtySurfaceBindings = 1 << 1;
        private const byte DirtyOutlineMaterials = 1 << 2;
        private const byte DirtyFrameFlags = 1 << 3;
        private const byte DirtyRadialSeeds = 1 << 4;

        private OriginalFrameScene? frame;
        private Guid[] ids = [];
        private OriginalFrameScene[] sources = [], models = [];
        private Vector3 lightColor, incomingDirection;

        // Per-topology material/index offsets and local-to-global texture index maps, aligned with `frame`.
        private int totalMaterialCount;
        private int[] actorMaterialOffsets = [];
        private int[] actorIndexOffsets = [];
        private int[][]? actorTextureMaps;

        // Transient scratch buffers used during Compose and cleared before leaving Compose.
        private readonly List<(int Start, int Count)> scratchRanges = new();
        private readonly List<int> scratchDirtyModels = new();
        private readonly List<SceneDeformationRange> scratchDeferred = new();
        private byte[] scratchActorDirtyMask = [];

        internal void Clear()
        {
            initialTextureSource = null;
            frame = null;
            ids = [];
            sources = [];
            models = [];
            InvalidateTopologyCaches();
            scratchRanges.Clear();
            scratchDirtyModels.Clear();
            scratchDeferred.Clear();
            scratchActorDirtyMask = [];
        }

        private void InvalidateTopologyCaches()
        {
            totalMaterialCount = 0;
            actorMaterialOffsets = [];
            actorIndexOffsets = [];
            actorTextureMaps = null;
        }

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
                var rebuilt = Combine(inputs, ambient, incoming, textureSource);
                InvalidateTopologyCaches();
                return Remember(rebuilt, actorIds, actorSources, inputs, ambient, incoming, compatible: false);
            }

            if (scratchActorDirtyMask.Length < inputs.Count)
                scratchActorDirtyMask = new byte[inputs.Count];

            scratchRanges.Clear();
            scratchDirtyModels.Clear();
            scratchDeferred.Clear();
            try
            {
                // Actors deformed by the renderer are attached by reference; their vertices are not copied.
                bool anyDeferred = false, deferredChanged = false;
                bool changed = false, seedsChanged = false, uvsChanged = false, identityGeometry = true;
                bool anySnapshotMaterialsChanged = false, anySurfaceBindingsChanged = false;
                bool anyOutlineMaterialsChanged = false, anyFlagsChanged = false;
                int offset = 0;

                for (int i = 0; i < inputs.Count; i++)
                {
                    var prior = models[i];
                    var next = inputs[i];
                    var oldSnapshot = prior.Surface.Snapshot;
                    var snapshot = next.Surface.Snapshot;
                    byte mask = 0;

                    if (ReferenceEquals(prior, next))
                    {
                        if (snapshot.WholeDeformation is not null) anyDeferred = true;
                    }
                    else
                    {
                        bool sameWorld = SameBits(oldSnapshot.World, snapshot.World);
                        bool geometryChanged = !oldSnapshot.SharesVertexStorage(snapshot) || !sameWorld;
                        if (snapshot.WholeDeformation is not null)
                        {
                            anyDeferred = true;
                            deferredChanged |= geometryChanged;
                        }
                        else if (geometryChanged)
                        {
                            scratchRanges.Add((offset, snapshot.VertexCount));
                            scratchDirtyModels.Add(i);
                            identityGeometry &= snapshot.World == Matrix4x4.Identity;
                        }

                        bool seedChanged = !ReferenceEquals(oldSnapshot.RawRadialSeeds, snapshot.RawRadialSeeds)
                            || (snapshot.RawRadialSeeds is not null && !sameWorld);
                        if (seedChanged)
                        {
                            seedsChanged = true;
                            mask |= DirtyRadialSeeds;
                        }

                        bool uvChanged = !ReferenceEquals(oldSnapshot.AdditionalUvs, snapshot.AdditionalUvs);
                        uvsChanged |= uvChanged;

                        bool snapMatChanged = !SameSceneMaterials(oldSnapshot.Materials, snapshot.Materials);
                        if (snapMatChanged)
                        {
                            anySnapshotMaterialsChanged = true;
                            mask |= DirtySnapshotMaterials;
                        }

                        bool surfMatChanged = !SameStyleBindings(prior.Surface.Materials, next.Surface.Materials);
                        if (surfMatChanged)
                        {
                            anySurfaceBindingsChanged = true;
                            mask |= DirtySurfaceBindings;
                        }

                        bool edgeMatChanged = !SameOutlineMaterials(prior.Outline!.Materials, next.Outline!.Materials);
                        if (edgeMatChanged)
                        {
                            anyOutlineMaterialsChanged = true;
                            mask |= DirtyOutlineMaterials;
                        }

                        bool flagsChanged = !SameFrameFlags(prior.Materials, next.Materials);
                        if (flagsChanged)
                        {
                            anyFlagsChanged = true;
                            mask |= DirtyFrameFlags;
                        }

                        bool profileChanged = i == 0 && !ReferenceEquals(prior.Surface.Profile, next.Surface.Profile);
                        changed |= geometryChanged || seedChanged || uvChanged || snapMatChanged || surfMatChanged
                            || edgeMatChanged || flagsChanged || profileChanged;
                    }

                    scratchActorDirtyMask[i] = mask;
                    offset = checked(offset + snapshot.VertexCount);
                }

                if (!changed)
                    return Remember(baseline!, actorIds, actorSources, inputs, ambient, incoming, compatible: true);

                var baselineFrame = baseline!;
                SceneSnapshot updated = baselineFrame.Surface.Snapshot;
                if (scratchRanges.Count > 0)
                {
                    using (PoseEvaluationMetrics.Measure("composition.vertices"))
                    {
                        if (identityGeometry)
                        {
                            var replacements = new (int Start, SceneSnapshot Source)[scratchRanges.Count];
                            for (int range = 0; range < replacements.Length; range++)
                                replacements[range] = (scratchRanges[range].Start, inputs[scratchDirtyModels[range]].Surface.Snapshot);
                            updated = updated.WithIdentityVertexChunks(replacements);
                        }
                        else
                            updated = updated.WithComposedVertexChunks(scratchRanges, (range, rangeOffset, destination) =>
                                CopyTransformedVertices(inputs[scratchDirtyModels[range]].Surface.Snapshot, destination, rangeOffset));
                    }
                }

                bool baselineDeferred = baselineFrame.Surface.Snapshot.HasDeferredDeformation;
                bool rewrap = scratchRanges.Count > 0 || deferredChanged || anyDeferred != baselineDeferred;
                if (anyDeferred || baselineDeferred)
                {
                    using (PoseEvaluationMetrics.Measure("composition.deferred"))
                    {
                        if (rewrap)
                        {
                            if (anyDeferred)
                            {
                                int defOffset = 0;
                                for (int i = 0; i < inputs.Count; i++)
                                {
                                    var snapshot = inputs[i].Surface.Snapshot;
                                    if (snapshot.WholeDeformation is { } deformation)
                                        scratchDeferred.Add(SceneSnapshot.PlacedDeformation(defOffset, deformation, snapshot.World));
                                    defOffset += snapshot.VertexCount;
                                }
                            }
                            updated = updated.WithDeferredRanges(scratchDeferred);
                        }
                        else
                        {
                            updated = baselineFrame.Surface.Snapshot;
                        }
                    }
                }

                if (uvsChanged)
                {
                    using (PoseEvaluationMetrics.Measure("composition.additional-uvs"))
                        updated = updated.WithAdditionalUvs(CombineAdditionalUvs(inputs, offset, baselineFrame.Surface.Snapshot.AdditionalUvs));
                }

                if (seedsChanged)
                {
                    using (PoseEvaluationMetrics.Measure("composition.radial-seeds"))
                        updated = updated.WithRawRadialSeeds(CombineIncrementalRawRadialSeeds(
                            inputs, offset, baselineFrame.Surface.Snapshot.RawRadialSeeds, scratchActorDirtyMask));
                }

                IReadOnlyList<OriginalStyleMaterialBinding> nextBindings = baselineFrame.Surface.Materials;
                IReadOnlyList<OriginalOutlineMaterial> nextEdgeMaterials = baselineFrame.Outline!.Materials;
                IReadOnlyList<OriginalFrameMaterialFlags> nextFlags = baselineFrame.Materials;

                if (anySnapshotMaterialsChanged || anySurfaceBindingsChanged || anyOutlineMaterialsChanged || anyFlagsChanged)
                {
                    using (PoseEvaluationMetrics.Measure("composition.materials"))
                    {
                        EnsureTopologyMaterialCache(baselineFrame, inputs, needTextureMaps: anySnapshotMaterialsChanged || anySurfaceBindingsChanged);

                        if (anySnapshotMaterialsChanged)
                        {
                            var nextMaterials = new SceneMaterial[totalMaterialCount];
                            var baselineMaterials = baselineFrame.Surface.Snapshot.Materials;
                            for (int i = 0; i < inputs.Count; i++)
                            {
                                int matOffset = actorMaterialOffsets[i];
                                var srcMaterials = inputs[i].Surface.Snapshot.Materials;
                                int matCount = srcMaterials.Count;
                                if ((scratchActorDirtyMask[i] & DirtySnapshotMaterials) == 0)
                                {
                                    for (int m = 0; m < matCount; m++)
                                        nextMaterials[matOffset + m] = baselineMaterials[matOffset + m];
                                }
                                else
                                {
                                    var prevMaterials = models[i].Surface.Snapshot.Materials;
                                    int indexBase = actorIndexOffsets[i];
                                    int[] texMap = actorTextureMaps![i];
                                    for (int m = 0; m < matCount; m++)
                                    {
                                        var material = srcMaterials[m];
                                        if (ReferenceEquals(prevMaterials[m], material))
                                        {
                                            nextMaterials[matOffset + m] = baselineMaterials[matOffset + m];
                                            continue;
                                        }
                                        int diff = material.DiffuseTexture < 0 ? -1 : texMap[material.DiffuseTexture];
                                        int toon = material.ToonTexture < 0 ? -1 : texMap[material.ToonTexture];
                                        nextMaterials[matOffset + m] = material with
                                        {
                                            StartIndex = checked(material.StartIndex + indexBase),
                                            DiffuseTexture = diff,
                                            ToonTexture = toon
                                        };
                                    }
                                }
                            }
                            if (!SameSceneMaterials(updated.Materials, nextMaterials))
                                updated = updated.WithMaterials(nextMaterials);
                        }

                        if (anySurfaceBindingsChanged)
                        {
                            var bindings = new OriginalStyleMaterialBinding[totalMaterialCount];
                            for (int i = 0; i < inputs.Count; i++)
                            {
                                int matOffset = actorMaterialOffsets[i];
                                var srcBindings = inputs[i].Surface.Materials;
                                int matCount = srcBindings.Count;
                                if ((scratchActorDirtyMask[i] & DirtySurfaceBindings) == 0)
                                {
                                    for (int m = 0; m < matCount; m++)
                                        bindings[matOffset + m] = baselineFrame.Surface.Materials[matOffset + m];
                                }
                                else
                                {
                                    var prevBindings = models[i].Surface.Materials;
                                    int[] texMap = actorTextureMaps![i];
                                    for (int m = 0; m < matCount; m++)
                                    {
                                        var binding = srcBindings[m];
                                        if (ReferenceEquals(prevBindings[m], binding))
                                        {
                                            bindings[matOffset + m] = baselineFrame.Surface.Materials[matOffset + m];
                                            continue;
                                        }
                                        int sphere = binding.SphereTexture < 0 ? -1 : texMap[binding.SphereTexture];
                                        bindings[matOffset + m] = binding with { SphereTexture = sphere };
                                    }
                                }
                            }
                            if (!SameStyleBindings(baselineFrame.Surface.Materials, bindings))
                                nextBindings = Array.AsReadOnly(bindings);
                        }

                        if (anyOutlineMaterialsChanged)
                        {
                            var edgeMaterials = new OriginalOutlineMaterial[totalMaterialCount];
                            for (int i = 0; i < inputs.Count; i++)
                            {
                                int matOffset = actorMaterialOffsets[i];
                                var srcEdges = inputs[i].Outline!.Materials;
                                int matCount = srcEdges.Count;
                                if ((scratchActorDirtyMask[i] & DirtyOutlineMaterials) == 0)
                                {
                                    for (int m = 0; m < matCount; m++)
                                        edgeMaterials[matOffset + m] = baselineFrame.Outline.Materials[matOffset + m];
                                }
                                else
                                {
                                    for (int m = 0; m < matCount; m++)
                                        edgeMaterials[matOffset + m] = srcEdges[m];
                                }
                            }
                            if (!SameOutlineMaterials(baselineFrame.Outline.Materials, edgeMaterials))
                                nextEdgeMaterials = Array.AsReadOnly(edgeMaterials);
                        }

                        if (anyFlagsChanged)
                        {
                            var flags = new OriginalFrameMaterialFlags[totalMaterialCount];
                            for (int i = 0; i < inputs.Count; i++)
                            {
                                int matOffset = actorMaterialOffsets[i];
                                var srcFlags = inputs[i].Materials;
                                int matCount = srcFlags.Count;
                                if ((scratchActorDirtyMask[i] & DirtyFrameFlags) == 0)
                                {
                                    for (int m = 0; m < matCount; m++)
                                        flags[matOffset + m] = baselineFrame.Materials[matOffset + m];
                                }
                                else
                                {
                                    for (int m = 0; m < matCount; m++)
                                        flags[matOffset + m] = srcFlags[m];
                                }
                            }
                            if (!SameFrameFlags(baselineFrame.Materials, flags))
                                nextFlags = Array.AsReadOnly(flags);
                        }
                    }
                }

                OriginalOutlineData outline;
                using (PoseEvaluationMetrics.Measure("composition.outline"))
                {
                    bool edgeMaterialsChanged = !ReferenceEquals(nextEdgeMaterials, baselineFrame.Outline.Materials);
                    if (scratchRanges.Count == 0)
                    {
                        outline = edgeMaterialsChanged
                            ? baselineFrame.Outline.WithMaterials(nextEdgeMaterials)
                            : baselineFrame.Outline;
                        if (rewrap)
                            outline = outline.WithDeferredSurface(updated);
                    }
                    else
                    {
                        outline = baselineFrame.Outline.WithSurfaceVertexChunks(
                            updated.StaticVertexView, scratchRanges, nextEdgeMaterials, OutlineCompositionDegree());
                        if (updated.HasDeferredDeformation)
                            outline = outline.WithDeferredSurface(updated);
                    }
                }

                var result = new OriginalFrameScene(
                    new(updated, nextBindings, baselineFrame.Surface.Profile),
                    outline,
                    baselineFrame.OutlineOptions,
                    baselineFrame.Options,
                    nextFlags);
                return Remember(result, actorIds, actorSources, inputs, ambient, incoming, compatible: true);
            }
            finally
            {
                scratchRanges.Clear();
                scratchDirtyModels.Clear();
                scratchDeferred.Clear();
            }
        }

        private void EnsureTopologyMaterialCache(OriginalFrameScene baselineFrame, IReadOnlyList<OriginalFrameScene> inputs, bool needTextureMaps)
        {
            if (actorMaterialOffsets.Length != inputs.Count)
            {
                var matOffsets = new int[inputs.Count];
                var idxOffsets = new int[inputs.Count];
                int matBase = 0, idxBase = 0;
                for (int i = 0; i < inputs.Count; i++)
                {
                    matOffsets[i] = matBase;
                    idxOffsets[i] = idxBase;
                    var snapshot = inputs[i].Surface.Snapshot;
                    matBase = checked(matBase + snapshot.Materials.Count);
                    idxBase = checked(idxBase + snapshot.Indices.Length);
                }
                totalMaterialCount = matBase;
                actorMaterialOffsets = matOffsets;
                actorIndexOffsets = idxOffsets;
            }

            if (needTextureMaps && actorTextureMaps is null)
            {
                var baselineTextures = baselineFrame.Surface.Snapshot.Textures;
                var globalTextures = new Dictionary<SceneTexture, int>(baselineTextures.Count, ReferenceEqualityComparer.Instance);
                for (int i = 0; i < baselineTextures.Count; i++)
                    globalTextures.TryAdd(baselineTextures[i], i);

                var maps = new int[inputs.Count][];
                for (int i = 0; i < inputs.Count; i++)
                {
                    var localTextures = inputs[i].Surface.Snapshot.Textures;
                    if (localTextures.Count == 0)
                    {
                        maps[i] = [];
                        continue;
                    }
                    var map = new int[localTextures.Count];
                    for (int t = 0; t < map.Length; t++)
                        map[t] = globalTextures[localTextures[t]];
                    maps[i] = map;
                }
                actorTextureMaps = maps;
            }
        }

        private static SceneRawRadialSeeds? CombineIncrementalRawRadialSeeds(IReadOnlyList<OriginalFrameScene> models,
            int vertexCount, SceneRawRadialSeeds? baselineSeeds, byte[] dirtyMask)
        {
            bool present = false;
            for (int i = 0; i < models.Count; i++)
                if (models[i].Surface.Snapshot.RawRadialSeeds is not null) { present = true; break; }
            if (!present) return null;
            var values = new Vector3[vertexCount];
            int offset = 0;
            for (int i = 0; i < models.Count; i++)
            {
                var snapshot = models[i].Surface.Snapshot;
                int count = snapshot.VertexCount;
                if (count > vertexCount - offset)
                    throw new ArgumentException("Project vertex layout changed; rebuild its baseline.", nameof(models));
                if (snapshot.RawRadialSeeds is { } stream)
                {
                    if (stream.Vertices.Length != count)
                        throw new ArgumentException("Raw radial seeds must preserve source vertex order and count.", nameof(models));
                    if ((dirtyMask[i] & DirtyRadialSeeds) == 0 && baselineSeeds is not null)
                        baselineSeeds.Vertices.Slice(offset, count).CopyTo(values.AsSpan(offset, count));
                    else if (snapshot.World == Matrix4x4.Identity)
                        stream.Vertices.CopyTo(values.AsSpan(offset, count));
                    else
                        for (int v = 0; v < count; v++)
                            values[offset + v] = Vector3.TransformNormal(stream.Vertices[v], snapshot.World);
                }
                offset = checked(offset + count);
            }
            if (offset != vertexCount)
                throw new ArgumentException("Project vertex layout changed; rebuild its baseline.", nameof(models));
            return new(values);
        }

        private OriginalFrameScene Remember(OriginalFrameScene result, IReadOnlyList<Guid> actorIds,
            IReadOnlyList<OriginalFrameScene> actorSources, IReadOnlyList<OriginalFrameScene> inputs,
            Vector3 ambient, Vector3 incoming, bool compatible)
        {
            // Copy the small owner tables; caller list mutation cannot alter provenance.
            int count = inputs.Count;
            if (!compatible)
            {
                if (ids.Length != count)
                {
                    ids = new Guid[count];
                    sources = new OriginalFrameScene[count];
                    models = new OriginalFrameScene[count];
                }
                for (int i = 0; i < count; i++)
                {
                    ids[i] = actorIds[i];
                    sources[i] = actorSources[i];
                    models[i] = inputs[i];
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                    if (!ReferenceEquals(models[i], inputs[i]))
                        models[i] = inputs[i];
            }
            lightColor = ambient;
            incomingDirection = incoming;
            frame = result;
            return result;
        }

        private static bool SameTopology(OriginalFrameScene first, OriginalFrameScene second)
        {
            if (ReferenceEquals(first, second)) return true;
            var a = first.Surface.Snapshot; var b = second.Surface.Snapshot;
            if (second.Outline is null) return false;
            if (ReferenceEquals(a, b)) return true;
            if (a.VertexCount != b.VertexCount || !a.SharesIndexStorage(b)
                || a.Materials.Count != b.Materials.Count || a.Textures.Count != b.Textures.Count) return false;
            if (!ReferenceEquals(a.Materials, b.Materials))
                for (int i = 0; i < a.Materials.Count; i++)
                    if (a.Materials[i].StartIndex != b.Materials[i].StartIndex || a.Materials[i].IndexCount != b.Materials[i].IndexCount) return false;
            if (!ReferenceEquals(a.Textures, b.Textures))
                for (int i = 0; i < a.Textures.Count; i++)
                    if (!ReferenceEquals(a.Textures[i], b.Textures[i])) return false;
            return true;
        }

        private static bool SameSceneMaterials(IReadOnlyList<SceneMaterial> first, IReadOnlyList<SceneMaterial> second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++)
                if (!ReferenceEquals(first[i], second[i]) && !first[i].Equals(second[i])) return false;
            return true;
        }

        private static bool SameStyleBindings(IReadOnlyList<OriginalStyleMaterialBinding> first, IReadOnlyList<OriginalStyleMaterialBinding> second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++)
                if (!ReferenceEquals(first[i], second[i]) && !first[i].Equals(second[i])) return false;
            return true;
        }

        private static bool SameOutlineMaterials(IReadOnlyList<OriginalOutlineMaterial> first, IReadOnlyList<OriginalOutlineMaterial> second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++)
                if (!ReferenceEquals(first[i], second[i]) && !first[i].Equals(second[i])) return false;
            return true;
        }

        private static bool SameFrameFlags(IReadOnlyList<OriginalFrameMaterialFlags> first, IReadOnlyList<OriginalFrameMaterialFlags> second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++)
            {
                var a = first[i];
                var b = second[i];
                if (ReferenceEquals(a, b)) continue;
                if (!a.Equals(b)
                    || !ReferenceEquals(a.CharacterToonResponse, b.CharacterToonResponse)
                    || !ReferenceEquals(a.CharacterOutlineStyle, b.CharacterOutlineStyle))
                    return false;
            }
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
        int vertexOffset=0,indexOffset=0;var deferred=new List<SceneDeformationRange>();
        foreach(var model in models)
        {
            var snapshot=model.Surface.Snapshot;
            if(snapshot.WholeDeformation is {} deformation)deferred.Add(SceneSnapshot.PlacedDeformation(vertexOffset,deformation,snapshot.World));
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
            // Deferred actors contribute placeholders; the composed range keeps their deformation instead.
            CopyTransformedVertices(snapshot.WholeDeformation is null?snapshot:snapshot.StaticVertexView,vertices.AsSpan(vertexOffset,snapshot.VertexCount));
            vertexOffset=checked(vertexOffset+snapshot.VertexCount);
            foreach(uint index in snapshot.Indices)indices[indexOffset++]=checked(index+(uint)vertexBase);
            foreach(var material in snapshot.Materials)materials.Add(material with{StartIndex=checked(material.StartIndex+indexBase),DiffuseTexture=Texture(material.DiffuseTexture),ToonTexture=Texture(material.ToonTexture)});
            foreach(var material in model.Surface.Materials)bindings.Add(material with{SphereTexture=Texture(material.SphereTexture)});
            var edgeSource=model.Outline.StaticView;
            for(int i=0;i<edgeSource.Vertices.Count;i++)
            {var edge=edgeSource.Vertices[i];var vertex=vertices[vertexBase+i];edges[vertexBase+i]=edge with{Position=vertex.Position,Normal=vertex.Normal,SelectedMaterialIndex=checked(edge.SelectedMaterialIndex+materialBase)};}
            edgeMaterials.AddRange(model.Outline.Materials);flags.AddRange(model.Materials);
        }
        var scene=new SceneSnapshot(vertices,indices,materials,textures,world:null,camera:null,lightDirection:incomingDirection,
            additionalUvs:CombineAdditionalUvs(models,vertexCount),rawRadialSeeds:CombineRawRadialSeeds(models,vertexCount),
            textureSource:textureSource,dynamicVertices:models.Any(m=>m.Surface.Snapshot.HasDynamicVertices));
        var profile=models[0].Surface.Profile with{LightAmbient=lightColor,LightSpecular=lightColor};
        var outline=new OriginalOutlineData(edges,edgeMaterials);
        if(deferred.Count>0){scene=scene.WithDeferredRanges(deferred);outline=outline.WithDeferredSurface(scene);}
        return new(new(scene,bindings,profile),outline,models[0].OutlineOptions,materials:flags);
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
