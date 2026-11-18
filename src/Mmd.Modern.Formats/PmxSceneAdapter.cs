using System.Runtime.ExceptionServices;
using Mmd.Modern.Core;

namespace Mmd.Modern.Formats;

/// <summary>Static PMX rest pose. SDEF/QDEF animation, morphs and physics are not evaluated.</summary>
public static class PmxSceneAdapter
{
    /// <summary>Distinct authored texture tokens that <see cref="FromRestPose"/> may request, in request order.
    /// Out-of-range identities are omitted; the adapter still reports them when it reaches them.</summary>
    public static IReadOnlyList<string> RequestedTextures(PmxModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var result = new List<string>();
        var seen = new HashSet<int>();
        void Add(int index) { if (index >= 0 && index < model.Textures.Count && seen.Add(index)) result.Add(model.Textures[index]); }
        foreach (var m in model.Materials)
        {
            Add(m.TextureIndex);
            if (m.ToonMode == PmxToonMode.Separate) Add(m.ToonTextureIndex);
        }
        return result.AsReadOnly();
    }

    public static SceneAdaptationResult FromRestPose(PmxModel model, Func<string, SceneTexture?>? loadTexture = null)
        => FromRestPoseCore(model, loadTexture is null ? null : (index, _) => loadTexture(model.Textures[index]));

    /// <summary>Loads referenced textures in parallel bounded by <see cref="SceneTextureCache.DefaultParallelism"/>
    /// (unless <paramref name="maxParallelism"/> is specified), while preserving sequential result order,
    /// diagnostic order, failure order and cancellation semantics.</summary>
    public static SceneAdaptationResult FromRestPose(PmxModel model, Func<string, CancellationToken, SceneTexture?> loadTexture,
        CancellationToken cancellationToken = default, int maxParallelism = 0)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(loadTexture);
        ArgumentOutOfRangeException.ThrowIfNegative(maxParallelism);
        cancellationToken.ThrowIfCancellationRequested();
        var vertices = BuildVertices(model);
        var indices = BuildIndices(model);
        int concurrency = maxParallelism == 0 ? SceneTextureCache.DefaultParallelism : maxParallelism;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new Dictionary<int, Task<SceneTexture?>>();
        var tasks = new List<Task>();
        using var budget = new SemaphoreSlim(concurrency, concurrency);
        void Schedule(int index)
        {
            if (index < 0 || index >= model.Textures.Count || pending.ContainsKey(index)) return;
            string token = model.Textures[index];
            var task = Task.Run(async () =>
            {
                await budget.WaitAsync(cts.Token).ConfigureAwait(false);
                try { cts.Token.ThrowIfCancellationRequested(); return loadTexture(token, cts.Token); }
                finally { budget.Release(); }
            }, cts.Token);
            pending.Add(index, task);
            tasks.Add(task);
        }
        foreach (var m in model.Materials)
        {
            Schedule(m.TextureIndex);
            if (m.ToonMode == PmxToonMode.Separate) Schedule(m.ToonTextureIndex);
        }
        try
        {
            return BuildResult(model, vertices, indices, (index, _) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var task = pending[index];
                try { task.Wait(cancellationToken); }
                catch (AggregateException aggregate) { ExceptionDispatchInfo.Capture(aggregate.InnerException!).Throw(); }
                return task.Result;
            });
        }
        finally
        {
            cts.Cancel();
            try { Task.WaitAll(tasks); } catch (AggregateException) { }
        }
    }

    private static SceneAdaptationResult FromRestPoseCore(PmxModel model, Func<int, string, SceneTexture?>? loadByIndex)
    {
        ArgumentNullException.ThrowIfNull(model);
        return BuildResult(model, BuildVertices(model), BuildIndices(model), loadByIndex);
    }

    private static SceneVertex[] BuildVertices(PmxModel model)
    {
        var vertices = new SceneVertex[model.Vertices.Count];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = new(model.Vertices[i].Position, model.Vertices[i].Normal, model.Vertices[i].Uv);
        return vertices;
    }

    private static uint[] BuildIndices(PmxModel model)
    {
        var indices = new uint[checked(model.Faces.Count * 3)];
        for (int i = 0; i < model.Faces.Count; i++)
        {
            var t = model.Faces[i];
            if (t.A < 0 || t.B < 0 || t.C < 0) throw new ArgumentException("Negative PMX vertex index.");
            indices[i * 3] = (uint)t.A; indices[i * 3 + 1] = (uint)t.B; indices[i * 3 + 2] = (uint)t.C;
        }
        return indices;
    }

    private static SceneAdaptationResult BuildResult(PmxModel model, SceneVertex[] vertices, uint[] indices, Func<int, string, SceneTexture?>? loadByIndex)
    {
        var diagnostics = new List<string> { "PMX rest pose only: no SDEF/QDEF animation, morph, bone or physics evaluation. Sphere maps, edges, additional UVs and transparency sorting are not implemented." };
        var textures = new List<SceneTexture>();
        var cache = new Dictionary<int, int>();
        int Resolve(int index, string label)
        {
            if (index == -1) return -1;
            if (index < 0 || index >= model.Textures.Count) throw new ArgumentException($"Invalid PMX {label} texture identity {index}.");
            if (cache.TryGetValue(index, out int existing)) return existing;
            if (loadByIndex?.Invoke(index, model.Textures[index]) is not { } loaded)
            {
                diagnostics.Add($"PMX {label} texture '{model.Textures[index]}' not loaded; explicit modern fallback.");
                cache[index] = -1; return -1;
            }
            int mapped = textures.Count; textures.Add(loaded); cache[index] = mapped; return mapped;
        }
        int next = 0;
        var materials = new SceneMaterial[model.Materials.Count];
        for (int i = 0; i < materials.Length; i++)
        {
            var m = model.Materials[i];
            int diffuse = Resolve(m.TextureIndex, "diffuse");
            int toon;
            if (m.ToonMode == PmxToonMode.Common)
            {
                if (m.ToonTextureIndex is < 0 or > 9) throw new ArgumentException("PMX common toon index outside 0..9.");
                toon = textures.Count; textures.Add(SceneTexture.ProceduralToon(m.ToonTextureIndex));
                diagnostics.Add($"Material {i}: authored procedural common toon {m.ToonTextureIndex}; not an original MMD bitmap.");
            }
            else if (m.ToonMode == PmxToonMode.Separate) toon = Resolve(m.ToonTextureIndex, "toon");
            else throw new ArgumentException("Invalid PMX toon mode.");
            if (m.FaceVertexCount < 0 || m.FaceVertexCount > indices.Length - next) throw new ArgumentException("PMX material range outside indices.");
            materials[i] = new(m.Name, next, m.FaceVertexCount, m.Diffuse, m.Specular, m.Ambient, m.SpecularPower, diffuse, toon,
                ToonUvTransform: m.ToonMode == PmxToonMode.Separate && toon >= 0 ? new(0, -1, 0, 1) : null);
            next += m.FaceVertexCount;
        }
        return new(new(vertices, indices, materials, textures), diagnostics.AsReadOnly());
    }
}
