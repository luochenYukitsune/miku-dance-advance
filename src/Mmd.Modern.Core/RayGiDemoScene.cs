using System.Numerics;

namespace Mmd.Modern.Core;

/// <summary>Asset-free static room for demonstrating diffuse GI. Build once per character load and cache the result.</summary>
public static class RayGiDemoScene
{
    /// <summary>
    /// Appends a floor, red/cyan side walls, back wall and two grey occluders while retaining the character's
    /// mesh, appearance, World and camera. The front (-Z) and ceiling are open. Only the returned frame's
    /// flat ground projection is removed; the input is unchanged. Dimensions follow indexed world-space bounds.
    /// Optional strength in [0,1] adds small inward-facing emissive panels. Their Ambient/toon=1
    /// visible-colour proxy is intended for AO=0; it is not a general raster emission shader.
    /// </summary>
    public static OriginalFrameScene AddRoom(OriginalFrameScene character, float emissionStrength = 0)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (!float.IsFinite(emissionStrength) || emissionStrength is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(emissionStrength));
        var source = character.Surface.Snapshot;
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (uint index in source.Indices)
        {
            var position = Vector3.Transform(source.Vertices[(int)index].Position, source.World);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }
        var extent = max - min;
        float height = extent.Y;
        if (height < 1e-4f) height = MathF.Max(1, MathF.Max(extent.X, extent.Z));
        float centerX = min.X + extent.X / 2;
        float centerZ = min.Z + extent.Z / 2;
        float halfWidth = MathF.Max(.8f * height, extent.X / 2 + .5f * height);
        float left = centerX - halfWidth, right = centerX + halfWidth;
        float front = min.Z - .8f * height, back = max.Z + .45f * height;
        float floor = min.Y - .015f * height, top = min.Y + 1.3f * height;

        // Preserve the existing World and original vertex prefix, baking only the added room back into model space.
        Matrix4x4.Invert(source.World, out var inverseWorld);
        var normalToModel = Matrix4x4.Transpose(source.World);
        var vertices = new List<SceneVertex>(source.Vertices.Length + 64);
        vertices.AddRange(source.Vertices.ToArray());
        var indices = new List<uint>(source.Indices.Length + 96);
        indices.AddRange(source.Indices.ToArray());
        var materials = new List<SceneMaterial>(source.Materials);
        var bindings = new List<OriginalStyleMaterialBinding>(character.Surface.Materials);
        var flags = new List<OriginalFrameMaterialFlags>(character.Materials);
        var edgeVertices = character.Outline is null ? null : new List<OriginalOutlineVertex>(character.Outline.Vertices);
        var edgeMaterials = character.Outline is null ? null : new List<OriginalOutlineMaterial>(character.Outline.Materials);

        int material = AddMaterial("floor", new(.62f));
        Quad(new(left, floor, front), new(right - left, 0, 0), new(0, 0, back - front), Vector3.UnitY, material);
        material = AddMaterial("red wall", new(.80f, .08f, .045f));
        Quad(new(left, floor, front), new(0, 0, back - front), new(0, top - floor, 0), Vector3.UnitX, material);
        material = AddMaterial("cyan wall", new(.045f, .64f, .78f));
        Quad(new(right, floor, front), new(0, 0, back - front), new(0, top - floor, 0), -Vector3.UnitX, material);
        material = AddMaterial("back wall", new(.60f, .62f, .65f));
        Quad(new(left, floor, back), new(right - left, 0, 0), new(0, top - floor, 0), -Vector3.UnitZ, material);
        Box(new(left + .23f * height, floor, centerZ + .1f * height), new Vector3(.30f, .36f, .30f) * height,
            AddMaterial("left grey box", new(.52f)));
        Box(new(right - .23f * height, floor, centerZ - .17f * height), new Vector3(.28f, .22f, .32f) * height,
            AddMaterial("right grey box", new(.70f)));

        if (emissionStrength > 0)
        {
            var along = new Vector3(0, 0, .16f * height);
            var up = new Vector3(0, .25f * height, 0);
            float panelBottom = min.Y + .55f * height, panelFront = centerZ - .08f * height;
            Quad(new(left + .003f * height, panelBottom, panelFront), along, up, Vector3.UnitX,
                AddEmitter("warm panel", new Vector3(1, .32f, .1f) * emissionStrength));
            Quad(new(right - .003f * height, panelBottom, panelFront), along, up, -Vector3.UnitX,
                AddEmitter("cool panel", new Vector3(.1f, .55f, 1) * emissionStrength));
        }

        SceneAdditionalUvs? additionalUvs = null;
        if (source.AdditionalUvs is { } originalUvs)
        {
            var expandedUvs = new SceneAdditionalUvVertex[vertices.Count];
            originalUvs.Vertices.CopyTo(expandedUvs);
            additionalUvs = new(originalUvs.ChannelCount, expandedUvs);
        }
        SceneRawRadialSeeds? rawRadialSeeds = null;
        if (source.RawRadialSeeds is { } originalSeeds)
        {
            // Existing local vertex identities remain a prefix; new room vertices have no authored seed binding.
            var expandedSeeds = new Vector3[vertices.Count];
            originalSeeds.Vertices.CopyTo(expandedSeeds);
            rawRadialSeeds = new(expandedSeeds);
        }
        var snapshot = new SceneSnapshot(vertices.ToArray(), indices.ToArray(), materials, source.Textures,
            source.World, source.Camera, source.LightDirection, additionalUvs, rawRadialSeeds);
        var surface = new OriginalStyleScene(snapshot, bindings, character.Surface.Profile);
        var outline = edgeVertices is null ? null : new OriginalOutlineData(edgeVertices, edgeMaterials!);
        var options = character.Options is null ? null : character.Options with { GroundShadow = null };
        return new(surface, outline, character.OutlineOptions, options, flags, character.Lighting);

        int AddMaterial(string name, Vector3 diffuse)
        {
            int index = materials.Count;
            materials.Add(new($"GI demo / {name}", indices.Count, 0, new(diffuse, 1), new(.01f), Vector3.Zero, 16));
            bindings.Add(new(new(.22f), DoubleSided: true));
            flags.Add(new(true, true, false, SurfaceEnabled: true, ShadowDoubleSided: true));
            edgeMaterials?.Add(new(0, new(0, 0, 0, 1), DrawEnabled: false));
            return index;
        }

        int AddEmitter(string name, Vector3 emission)
        {
            int index = materials.Count;
            materials.Add(new SceneMaterial($"GI demo / {name}", indices.Count, 0, new(0, 0, 0, 1), Vector3.Zero, emission, 1)
                { Emission = emission });
            bindings.Add(new(Vector3.One, DoubleSided: false));
            flags.Add(new(true, false, false, SurfaceEnabled: true, ShadowDoubleSided: false));
            edgeMaterials?.Add(new(0, new(0, 0, 0, 1), DrawEnabled: false));
            return index;
        }

        void Quad(Vector3 corner, Vector3 along, Vector3 up, Vector3 worldNormal, int materialIndex)
        {
            uint start = checked((uint)vertices.Count);
            var modelNormal = Vector3.Normalize(Vector3.TransformNormal(worldNormal, normalToModel));
            AddVertex(corner, Vector2.Zero);
            AddVertex(corner + along, Vector2.UnitX);
            AddVertex(corner + along + up, Vector2.One);
            AddVertex(corner + up, Vector2.UnitY);
            // Establish winding in world space, including when the retained World mirrors an axis.
            if (Vector3.Dot(Vector3.Cross(along, up), worldNormal) > 0)
                indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
            else
                indices.AddRange([start, start + 2, start + 1, start, start + 3, start + 2]);
            materials[materialIndex] = materials[materialIndex] with { IndexCount = materials[materialIndex].IndexCount + 6 };

            void AddVertex(Vector3 worldPosition, Vector2 uv)
            {
                var position = Vector3.Transform(worldPosition, inverseWorld);
                vertices.Add(new(position, modelNormal, uv));
                edgeVertices?.Add(new(position, modelNormal, OriginalOutlineVertexMode.Expand, 0, materialIndex));
            }
        }

        void Box(Vector3 bottomCenter, Vector3 size, int materialIndex)
        {
            var origin = bottomCenter - new Vector3(size.X / 2, 0, size.Z / 2);
            var x = new Vector3(size.X, 0, 0);
            var y = new Vector3(0, size.Y, 0);
            var z = new Vector3(0, 0, size.Z);
            Quad(origin, x, y, -Vector3.UnitZ, materialIndex);
            Quad(origin + z, x, y, Vector3.UnitZ, materialIndex);
            Quad(origin, z, y, -Vector3.UnitX, materialIndex);
            Quad(origin + x, z, y, Vector3.UnitX, materialIndex);
            Quad(origin, x, z, -Vector3.UnitY, materialIndex);
            Quad(origin + y, x, z, Vector3.UnitY, materialIndex);
        }
    }
}
