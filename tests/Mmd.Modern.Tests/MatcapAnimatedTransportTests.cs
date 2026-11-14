using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D12;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class MatcapAnimatedTransportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void BoneAndPositionAndUvMorphsReachMatcapFromThePublishedSampleWithoutMutatingSources()
    {
        var model = Model();
        var sourceWorld = Matrix4x4.CreateScale(1.5f, .75f, 2) * Matrix4x4.CreateRotationY(.2f);
        var rest = Rest(model, sourceWorld);
        var restVertices = rest.Surface.Snapshot.Vertices.ToArray();
        var restUvs = rest.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray();
        var modelUvs = model.Vertices.Select(v => v.AdditionalUvs.ToArray()).ToArray();
        var layout = new SceneCompositionEditor();
        var item = layout.Add("animated", SourcePath, ModelRole.Character, rest);
        layout.SetTransform(item.Id, new(7, 3, -2), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f), 2);
        using var project = new SceneProjectSession(layout, new(),
            [new(item.Id, model, Motion(), PhysicsEnabled: false)], new FallingPhysics(), cancellationToken: Token);
        SceneObjectRenderRequest[] requests = [new(item.Id, includeSourceGeometry: true)];
        var before = project.CaptureRenderSnapshot(requests);
        var beforeSource = before.SceneInputs[item.Id].SourceGeometry!;
        var beforeVertices = beforeSource.Vertices.ToArray();
        var beforeUvs = beforeSource.AdditionalUvs!.Vertices.ToArray();
        var beforeGeometry = D3D12EndfieldMatcapGeometry.Build(beforeSource, 0, 0);

        project.Seek(30, Token);
        var sampledFrame = project.Frame;
        var captured = project.CaptureRenderSnapshot(requests);
        var current = captured.SceneInputs[item.Id].SourceGeometry!;
        Assert.Same(sampledFrame, captured.Scene);
        Assert.Equal(30, captured.SourceFrame);
        Assert.True(current.HasDynamicVertices);
        Assert.Equal(sourceWorld * project.Objects[0].World, current.World);
        // This is a real skinned/morphed local pose, rather than the rest source or an assembled world mesh.
        Near(Vector3.Transform(new(.1f, .2f, .4f), Matrix4x4.CreateRotationY(.4f)) + Vector3.UnitX,
            current.Vertices[0].Position);
        Assert.Equal(new Vector2(1.25f, .1f), current.Vertices[1].Uv);
        Assert.Equal(new Vector4(.3f, .8f, 0, 0), current.AdditionalUvs!.Vertices[2].Uv1);
        Assert.NotEqual(beforeGeometry.Fingerprint, AssertDrawMatches(captured, item.Id, 0, 0).Fingerprint);
        AssertDrawMatches(captured, item.Id, 1, 1);

        // A later seek cannot alter the previously published geometry or the format-neutral/PMX assets.
        project.Seek(0, Token);
        Assert.Equal(beforeVertices, beforeSource.Vertices.ToArray());
        Assert.Equal(beforeUvs, beforeSource.AdditionalUvs!.Vertices.ToArray());
        Assert.Same(beforeGeometry, D3D12EndfieldMatcapGeometry.Build(beforeSource, 0, 0));
        Assert.Equal(restVertices, rest.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(restUvs, rest.Surface.Snapshot.AdditionalUvs!.Vertices.ToArray());
        for (int i = 0; i < model.Vertices.Count; i++)
        {
            Assert.Equal(restVertices[i].Position, model.Vertices[i].Position);
            Assert.Equal(restVertices[i].Uv, model.Vertices[i].Uv);
            Assert.Equal(modelUvs[i], model.Vertices[i].AdditionalUvs);
        }
        AssertDrawMatches(captured, item.Id, 0, 0);
    }

    [Fact]
    public void TwoInstancesKeepPhysicsPoseWorldAndMaterialSlotsWithoutAnotherSolverEvaluation()
    {
        var model = Model(); var rest = Rest(model, Matrix4x4.Identity);
        var layout = new SceneCompositionEditor();
        var a = layout.Add("first", SourcePath, ModelRole.Character, rest);
        var b = layout.Add("second", SourcePath, ModelRole.Character, rest);
        layout.SetTransform(a.Id, new(5, 2, -3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f), 1);
        layout.SetTransform(b.Id, new(-4, 6, 2), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.3f), 1.7f);
        var physics = new FallingPhysics();
        using var project = new SceneProjectSession(layout, new(),
            [new(a.Id, model, Motion()), new(b.Id, model, Motion())], physics, cancellationToken: Token);
        SceneObjectRenderRequest[] requests = [new(b.Id, boneIndices: [1], includeSourceGeometry: true),
            new(a.Id, boneIndices: [0, 1], includeSourceGeometry: true)];
        var before = project.CaptureRenderSnapshot(requests);
        project.Advance(1d / 30, Token);
        var published = project.Frame;
        var solverCounts = physics.Counts;
        Assert.Equal(2, physics.Backends.Count);
        Assert.All(physics.Backends, backend => Assert.Equal(4, backend.Steps));

        for (int repeat = 0; repeat < 3; repeat++)
        {
            var capture = project.CaptureRenderSnapshot(requests);
            Assert.Same(published, capture.Scene);
            Assert.Equal(1, project.CurrentFrame);
            Assert.Equal(new[] { 0, 2 }, capture.RenderingStyles.Select(binding => binding.MaterialStart));
            foreach (var instance in project.Objects)
            {
                var current = capture.SceneInputs[instance.Id].SourceGeometry!;
                Assert.Equal(instance.World, current.World);
                Assert.True(current.HasDynamicVertices);
                Assert.True(current.Vertices[2].Position.Y < before.SceneInputs[instance.Id].SourceGeometry!.Vertices[2].Position.Y - .49f,
                    "The current geometry must include actual dynamic body feedback, not a reset/seek pose.");
                // Bone 1 drives vertex 2 and has an identity rest transform in this fixture.
                Near(Vector3.Transform(model.Vertices[2].Position, capture.SceneInputs[instance.Id].BoneWorldMatrices[1]),
                    Vector3.Transform(current.Vertices[2].Position, current.World));
                AssertDrawMatches(capture, instance.Id, 0, 0);
                AssertDrawMatches(capture, instance.Id, 1, 1);
            }
            Assert.Equal(solverCounts, physics.Counts);
        }
    }

    private static D3D12EndfieldMatcapGeometryData AssertDrawMatches(SceneProjectRenderSnapshot capture,
        Guid objectId, int localMaterialIndex, int uvChannel)
    {
        var source = capture.SceneInputs[objectId].SourceGeometry!;
        var instance = capture.RenderingStyles.Single(value => value.ObjectId == objectId);
        var combined = capture.Scene.Surface.Snapshot;
        var draw = combined.Materials[instance.MaterialStart + localMaterialIndex];
        var descriptor = new D3D12EndfieldMatcapGeometry(source, localMaterialIndex);
        var data = D3D12EndfieldMatcapGeometry.Build(descriptor.Source, descriptor.LocalMaterialIndex, uvChannel);
        Assert.Equal(draw.IndexCount, data.Corners.Length);
        for (int i = 0; i < data.Corners.Length; i++)
        {
            int combinedVertex = checked((int)combined.Indices[draw.StartIndex + i]);
            var vertex = combined.Vertices[combinedVertex];
            var corner = data.Corners[i];
            Near(vertex.Position, Xyz(corner.WorldPosition));
            Near(vertex.Normal, Vector3.Normalize(Xyz(corner.WorldNormal)));
            var uv = uvChannel == 0 ? vertex.Uv : new Vector2(combined.AdditionalUvs!.Vertices[combinedVertex].Uv1.X,
                combined.AdditionalUvs!.Vertices[combinedVertex].Uv1.Y);
            Assert.Equal(uv, new Vector2(corner.UvParity.X, corner.UvParity.Y));
            Assert.InRange(Xyz(corner.Tangent).Length(), .99999f, 1.00001f);
        }
        return data;
    }

    private static string SourcePath => Path.Combine(Path.GetTempPath(), "matcap-animated-transport.pmx");
    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 2e-5f);

    private static OriginalFrameScene Rest(PmxModel model, Matrix4x4 world)
    {
        var adapted = PmxOriginalStyleAdapter.FromRestPose(model).Scene;
        var mesh = adapted.Snapshot;
        var placed = new SceneSnapshot(mesh.Vertices, mesh.Indices, mesh.Materials, mesh.Textures, world,
            mesh.Camera, mesh.LightDirection, mesh.AdditionalUvs);
        return new(new(placed, adapted.Materials, adapted.Profile), PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
    }

    private static VmdMotion Motion() => new()
    {
        BoneKeyframes = [new() { BoneName = "animated", Frame = 0 }, new() { BoneName = "animated", Frame = 30,
            Translation = Vector3.UnitX, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f) }],
        MorphKeyframes = new[] { "position", "uv0", "uv1" }.SelectMany(name => new[]
        { new VmdMorphKeyframe { MorphName = name, Frame = 0, Weight = 0 },
            new VmdMorphKeyframe { MorphName = name, Frame = 30, Weight = 1 } }).ToArray(),
    };

    private static PmxModel Model() => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 1,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "animated MatCap transport", EnglishName = "", Comment = "", EnglishComment = "",
        Vertices = [Vertex(Vector3.Zero, Vector2.Zero, 0), Vertex(Vector3.UnitX, Vector2.UnitX, 0),
            Vertex(Vector3.UnitY, Vector2.UnitY, 1), Vertex(new(1, 1, 0), Vector2.One, 1)],
        Faces = [new(0, 1, 2), new(1, 3, 2)], Textures = [], Materials = [Material("first slot"), Material("second slot")],
        Bones = [Bone("animated"), Bone("dynamic")],
        Morphs = [new() { Name = "position", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Position,
                PositionOffsets = [new(0, new(.1f, .2f, .4f))] },
            new() { Name = "uv0", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Uv,
                UvOffsets = [new(1, new(.25f, .1f, 0, 0))] },
            new() { Name = "uv1", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.AdditionalUv1,
                UvOffsets = [new(2, new(.3f, -.2f, 0, 0))] }],
        RigidBodies = [new() { Name = "falling", EnglishName = "", BoneIndex = 1, Group = 0, CollisionGroup = 0,
            Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
            Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }],
        DisplayFrames = [], Joints = [], SoftBodies = [],
    };

    private static PmxVertex Vertex(Vector3 position, Vector2 uv, int bone) => new()
    {
        Position = position, Normal = Vector3.UnitZ, Uv = uv, AdditionalUvs = [new(uv, 0, 0)],
        WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [bone, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1,
    };
    private static PmxBone Bone(string name) => new()
    {
        Name = name, EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0,
        Flags = PmxBoneFlags.AllowRotate | PmxBoneFlags.AllowTranslate,
    };
    private static PmxMaterial Material(string name) => new()
    {
        Name = name, EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero,
        DrawMode = PmxDrawModeFlags.DrawEdge, EdgeColor = Vector4.One, EdgeSize = 1, TextureIndex = -1, SphereTextureIndex = -1,
        SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3,
    };

    private sealed class FallingPhysics : IProjectPhysicsBackendProvider
    {
        internal List<FallingBackend> Backends { get; } = [];
        internal (int Creates, int Initializes, int Steps, int Reads, int Targets) Counts =>
            (Backends.Count, Backends.Sum(v => v.Initializes), Backends.Sum(v => v.Steps), Backends.Sum(v => v.Reads), Backends.Sum(v => v.Targets));
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
        { var backend = new FallingBackend(15 + Backends.Count * 6); Backends.Add(backend); return backend; }
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new NotSupportedException();
    }

    private sealed class FallingBackend(float speed) : ICharacterPhysicsBackend
    {
        private Matrix4x4[] worlds = [];
        internal int Initializes, Steps, Reads, Targets;
        public string Name => "MatCap deterministic falling feedback";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> input)
        { Initializes++; worlds = input.ToArray(); }
        public void Step(float seconds)
        { Steps++; for (int i = 0; i < worlds.Length; i++) worlds[i].Translation += new Vector3(0, -speed * seconds, 0); }
        public void SetKinematicTarget(int index, Matrix4x4 world, float seconds) { Targets++; worlds[index] = world; }
        public Matrix4x4 GetBodyWorldTransform(int index) { Reads++; return worlds[index]; }
        public void Dispose() { }
    }
}
