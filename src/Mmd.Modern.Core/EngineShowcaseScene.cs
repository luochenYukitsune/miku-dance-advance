using System.Collections.ObjectModel;
using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>An original box mesh and its rigid body binding. Dimensions are half extents in scene units.</summary>
public sealed record EngineShowcasePart(string Name, int StartVertex, int VertexCount, Vector3 Position,
    Vector3 HalfExtents, Vector3 Rotation, bool IsDynamic, bool HasCollision = true)
{
    public Matrix4x4 RestTransform => Matrix4x4.CreateRotationZ(Rotation.Z) * Matrix4x4.CreateRotationX(Rotation.X)
        * Matrix4x4.CreateRotationY(Rotation.Y) * Matrix4x4.CreateTranslation(Position);
}

/// <summary>Original, asset-free prism laboratory. Core owns geometry; the application supplies real rigid transforms.</summary>
public sealed class EngineShowcaseScene
{
    private readonly SceneVertex[] localVertices;
    public OriginalFrameScene Frame { get; }
    public ReadOnlyCollection<EngineShowcasePart> Parts { get; }
    public int PendulumAnchorIndex { get; }
    public int PendulumBodyIndex { get; }
    public Vector3 PendulumPivot { get; }

    private EngineShowcaseScene(OriginalFrameScene frame, SceneVertex[] localVertices, List<EngineShowcasePart> parts,
        int pendulumAnchorIndex, int pendulumBodyIndex, Vector3 pendulumPivot)
    {
        Frame = frame; this.localVertices = localVertices; Parts = parts.AsReadOnly();
        PendulumAnchorIndex = pendulumAnchorIndex; PendulumBodyIndex = pendulumBodyIndex; PendulumPivot = pendulumPivot;
    }

    public static EngineShowcaseScene Create()
    {
        var parts = new List<EngineShowcasePart>();
        var local = new List<SceneVertex>();
        var world = new List<SceneVertex>();
        var indices = new List<uint>();
        var materials = new List<SceneMaterial>();
        var bindings = new List<OriginalStyleMaterialBinding>();
        var edges = new List<OriginalOutlineVertex>();
        var edgeMaterials = new List<OriginalOutlineMaterial>();
        var flags = new List<OriginalFrameMaterialFlags>();
        Add("Floor", new(0, -.25f, 1), new(6.4f, .25f, 5.3f), new(.28f, .32f, .38f), outline: false);
        Add("Terracotta wall", new(-6.3f, 3.2f, 3.85f), new(.15f, 3.2f, 2.35f), new(.48f, .16f, .095f), outline: false);
        Add("Blue wall", new(6.3f, 3.2f, 3.85f), new(.15f, 3.2f, 2.35f), new(.065f, .22f, .43f), outline: false);
        Add("Back wall", new(0, 3.2f, 6.1f), new(6.3f, 3.2f, .15f), new(.26f, .29f, .36f), outline: false);
        Add("Presentation plinth", new(-1.4f, .18f, .15f), new(2.1f, .18f, 1.65f), new(.42f, .46f, .52f));
        Add("Falling cyan cube", new(-1.75f, 3.05f, .1f), new(.58f), new(.10f, .74f, .80f), true, new(0, .12f, .12f));
        Add("Falling amber cube", new(-1.55f, 4.65f, .14f), new(.55f), new(.95f, .57f, .12f), true, new(.07f, -.2f, -.12f));
        Add("Falling coral cube", new(-1.35f, 6.25f, .05f), new(.52f), new(.87f, .21f, .26f), true, new(-.10f, .08f, .18f));
        Add("Pendulum base", new(4.65f, .16f, 1.5f), new(.85f, .16f, .75f), new(.25f, .30f, .37f));
        Add("Pendulum upright", new(4.8f, 2.85f, 1.5f), new(.14f, 2.65f, .14f), new(.66f, .71f, .77f));
        Add("Pendulum top beam", new(3.75f, 5.50f, 1.5f), new(1.20f, .14f, .14f), new(.66f, .71f, .77f));
        Vector3 pivot = new(3, 5.30f, 1.5f);
        int anchor = Add("Pendulum anchor", pivot, new(.16f), new(.95f, .79f, .33f), collision: false);
        Vector3 pendulumPosition = pivot + Vector3.TransformNormal(new(0, -1.2f, 0), Matrix4x4.CreateRotationZ(.52f));
        int pendulum = Add("Spring suspended prism", pendulumPosition, new(.37f, 1.2f, .37f), new(.58f, .28f, .85f), true, new(0, 0, .52f));
        Add("Warm wall accent", new(-5.95f, 3.2f, 4.8f), new(.025f, 1.55f, .13f), new(.95f, .40f, .12f), outline: false, collision: false);
        Add("Cool wall accent", new(5.95f, 3.2f, 4.8f), new(.025f, 1.55f, .13f), new(.15f, .50f, .95f), outline: false, collision: false);

        var camera = new SceneCamera(new(10.5f, 8.0f, -17.5f), new(0, 2.7f, 1), Vector3.UnitY, 12, .1f, 100, VerticalFovDegrees: 43);
        var snapshot = new SceneSnapshot(world.ToArray(), indices.ToArray(), materials, camera: camera,
            lightDirection: Vector3.Normalize(new(-.35f, .85f, -.7f)));
        var surface = new OriginalStyleScene(snapshot, bindings, new(new(.72f), new(.65f), Matrix4x4.Identity));
        var shadow = new OriginalSelfShadowOptions(ModernDirectionalShadowProjection.Fit(snapshot), 1024, 1024, true, false,
            OriginalTextureSampling.LinearClamp, OriginalDepthComparison.LessEqual);
        var options = new OriginalFrameOptions(OriginalAlphaBlendMode.Normal, true, false, SelfShadow: shadow,
            Antialiasing: FrameAntialiasing.Smaa1X, BackgroundColor: new(.025f, .035f, .055f, 1));
        var outlineOptions = new OriginalOutlineOptions(new(0, 2.7f, 1, 1), Matrix4x4.Identity, camera.Eye, 43, .65f);
        var frame = new OriginalFrameScene(surface, new(edges, edgeMaterials), outlineOptions, options, flags);
        return new(frame, local.ToArray(), parts, anchor, pendulum, pivot);

        int Add(string name, Vector3 position, Vector3 half, Vector3 color, bool dynamic = false, Vector3 rotation = default,
            bool outline = true, bool collision = true)
        {
            int partIndex = parts.Count, firstVertex = local.Count, firstIndex = indices.Count;
            var part = new EngineShowcasePart(name, firstVertex, 24, position, half, rotation, dynamic, collision);
            parts.Add(part);
            Vector3 x = new(half.X * 2, 0, 0), y = new(0, half.Y * 2, 0), z = new(0, 0, half.Z * 2), origin = -half;
            Quad(origin, x, y, -Vector3.UnitZ);
            Quad(origin + z, x, y, Vector3.UnitZ);
            Quad(origin, z, y, -Vector3.UnitX);
            Quad(origin + x, z, y, Vector3.UnitX);
            Quad(origin, x, z, -Vector3.UnitY);
            Quad(origin + y, x, z, Vector3.UnitY);
            materials.Add(new(name, firstIndex, 36, new(color, 1), dynamic ? new(.16f) : new(.04f), new(.10f), dynamic ? 48 : 24));
            bindings.Add(new(new(.40f, .43f, .50f), DoubleSided: false));
            edgeMaterials.Add(new(dynamic ? .85f : .35f, new(.025f, .03f, .055f, 1), outline));
            flags.Add(new(true, true, false));
            return partIndex;

            void Quad(Vector3 corner, Vector3 along, Vector3 up, Vector3 normal)
            {
                uint start = (uint)local.Count;
                Vertex(corner, Vector2.Zero); Vertex(corner + along, Vector2.UnitX);
                Vertex(corner + along + up, Vector2.One); Vertex(corner + up, Vector2.UnitY);
                if (Vector3.Dot(Vector3.Cross(along, up), normal) > 0) indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
                else indices.AddRange([start, start + 2, start + 1, start, start + 3, start + 2]);

                void Vertex(Vector3 point, Vector2 uv)
                {
                    local.Add(new(point, normal, uv));
                    Vector3 p = Vector3.Transform(point, part.RestTransform), n = Vector3.Normalize(Vector3.TransformNormal(normal, part.RestTransform));
                    world.Add(new(p, n, uv)); edges.Add(new(p, n, OriginalOutlineVertexMode.Expand, 1, partIndex));
                }
            }
        }
    }

    /// <summary>Topology/materials remain shared. Dynamic marking selects the renderer's bounded vertex stream.</summary>
    public OriginalFrameScene WithPartTransforms(IReadOnlyList<Matrix4x4> transforms)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        if (transforms.Count != Parts.Count) throw new ArgumentException("One rigid transform per showcase part is required.", nameof(transforms));
        var vertices = new SceneVertex[localVertices.Length];
        var outline = new OriginalOutlineVertex[localVertices.Length];
        foreach (var (part, partIndex) in Parts.Select((p, i) => (p, i)))
        {
            Matrix4x4 transform = transforms[partIndex];
            for (int i = part.StartVertex; i < part.StartVertex + part.VertexCount; i++)
            {
                var source = localVertices[i];
                Vector3 position = Vector3.Transform(source.Position, transform), normal = Vector3.Normalize(Vector3.TransformNormal(source.Normal, transform));
                vertices[i] = new(position, normal, source.Uv);
                outline[i] = Frame.Outline!.Vertices[i] with { Position = position, Normal = normal };
            }
        }
        var snapshot = Frame.Surface.Snapshot.WithDeformedVertices(vertices);
        return new(new(snapshot, Frame.Surface.Materials, Frame.Surface.Profile), new(outline, Frame.Outline!.Materials),
            Frame.OutlineOptions, Frame.Options, Frame.Materials, Frame.Lighting);
    }
}
