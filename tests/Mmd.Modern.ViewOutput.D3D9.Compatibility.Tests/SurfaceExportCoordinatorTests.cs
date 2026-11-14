using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

/// <summary>Real project/evaluation/export path with a pure managed surface-session substitute.</summary>
public sealed class SurfaceExportCoordinatorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectExportAtSixtyFpsUsesFiveSameSampleFramesAndEightPhysicsSteps(bool configuredFx)
    {
        var fixture = new ProjectFixture { ConfiguredFx = configuredFx };
        var sink = new RecordingSink(fixture);
        var coordinator = fixture.Coordinator();
        var result = await coordinator.RunAsync(new(new(2, 2, new(60), 0, 2)), sink, cancellationToken: Token);
        Assert.Equal(5, result.CompletedFrames); Assert.Equal(8, result.SimulationTicks);
        Assert.Equal(configuredFx ? "d3d9-object-fx-experimental" : "d3d9-isolated-surface-experimental", result.BackendId);
        Assert.Equal(new[] { 0d, .5d, 1d, 1.5d, 2d }, fixture.Native.SourceFrames);
        Assert.Equal(new[] { 0, 2, 4, 6, 8 }, fixture.Native.StepCounts);
        Assert.Equal(new[] { 0d, .5d, 1d, 1.5d, 2d }, sink.SourceFrames);
        Assert.Equal(8, fixture.Physics.TotalSteps);
        Assert.All(fixture.Physics.Backends.SelectMany(backend => backend.StepSeconds),
            seconds => Assert.Equal(1f / 120, seconds));
        Assert.Equal(5, fixture.CaptureClockChecks); Assert.Equal(1, sink.Begins);
        Assert.True(sink.Completed); Assert.False(sink.Aborted); Assert.Equal(1, fixture.Native.Disposals);
        Assert.All(fixture.Physics.Backends, backend => Assert.Equal(1, backend.Disposals));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedProjectPresetFailsBeforeSinkBeginOrPhysicsAdvance(bool configuredFx)
    {
        var fixture = new ProjectFixture { ModernPreset = true, ConfiguredFx = configuredFx };
        var sink = new RecordingSink(fixture);
        var coordinator = fixture.Coordinator();
        await Assert.ThrowsAsync<NotSupportedException>(() => coordinator.RunAsync(
            new(new(2, 2, new(60), 0, 2)), sink, cancellationToken: Token));
        Assert.Equal(0, sink.Begins); Assert.Equal(0, fixture.Physics.TotalSteps);
        Assert.Empty(fixture.Native.SourceFrames); Assert.True(sink.Aborted); Assert.False(sink.Completed);
        Assert.Equal(1, fixture.Native.Disposals);
    }

    private sealed class ProjectFixture
    {
        internal readonly TrackingPhysicsProvider Physics = new();
        internal readonly FakeCaptureSession Native;
        internal SceneProjectSession Project = null!;
        internal Guid ObjectId;
        internal bool ModernPreset, ConfiguredFx;
        internal int CaptureClockChecks;
        internal ProjectFixture() => Native = new(this);

        internal ExportCoordinator Coordinator() => ExportCoordinator.ForProject(token =>
        {
            var model = Model();
            var frame = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
                PmxOriginalOutlineAdapter.FromRestPose(model),
                new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, -10), 30, 1),
                materials: PmxOriginalFrameAdapter.FromMaterials(model));
            var layout = new SceneCompositionEditor();
            var item = layout.Add("fixture", "fixture.pmx", ModelRole.Character, frame);
            ObjectId = item.Id;
            var motion = new VmdMotion { BoneKeyframes = [new() { BoneName = "root" },
                new() { BoneName = "root", Frame = 2, Translation = Vector3.UnitX }] };
            Project = new(layout, new(), [new(item.Id, model, motion)], Physics, cancellationToken: token);
            if (ModernPreset) Project.SetDefaultRenderingStyle(RenderingStyleId.Genshin, token);
            return ValueTask.FromResult(Project);
        }, async (request, token) =>
        {
            D3D9SurfaceObjectBinding[] bindings = [new(ObjectId, 1, "fixture.pmx", "fixture", ["root"])];
            var renderer = ConfiguredFx
                ? await ExperimentalD3D9SurfaceViewRenderer.CreateObjectFxForTestingAsync(request, bindings, Vector4.One,
                    new([new(1, -1, D3D9ObjectFxAssignmentMode.Standard)]),
                    (_, _) => ValueTask.FromResult<ID3D9SurfaceCaptureSession>(Native), token)
                : await ExperimentalD3D9SurfaceViewRenderer.CreateForTestingAsync(request, bindings, Vector4.One,
                    D3D9SurfaceIsolation.NoConfiguredMmeOrEmm,
                    (_, _) => ValueTask.FromResult<ID3D9SurfaceCaptureSession>(Native), token);
            return new ClockCheckingRenderer(this, renderer);
        });
    }

    private sealed class ClockCheckingRenderer(ProjectFixture fixture, ExperimentalD3D9SurfaceViewRenderer inner)
        : IViewRenderer, IViewSceneInputRequests, IViewSceneInputPreflight, IViewRenderingStylePreflight
    {
        public ViewRendererCapabilities Capabilities => inner.Capabilities;
        public IReadOnlyList<SceneObjectRenderRequest> SceneInputRequests => inner.SceneInputRequests;
        public async ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
        {
            double clock = fixture.Project.CurrentFrame; int steps = fixture.Physics.TotalSteps;
            var result = await inner.CaptureAsync(frame, cancellationToken);
            Assert.Equal(clock, fixture.Project.CurrentFrame); Assert.Equal(steps, fixture.Physics.TotalSteps);
            fixture.CaptureClockChecks++;
            return result;
        }
        public void ValidateSceneInputs(PreviewFrame frame)
        {
            double clock = fixture.Project.CurrentFrame; int steps = fixture.Physics.TotalSteps;
            inner.ValidateSceneInputs(frame);
            Assert.Equal(clock, fixture.Project.CurrentFrame); Assert.Equal(steps, fixture.Physics.TotalSteps);
        }
        public void ValidateRenderingStyles(OriginalFrameScene scene, IReadOnlyList<SceneRenderingStyleBinding> bindings)
            => inner.ValidateRenderingStyles(scene, bindings);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FakeCaptureSession(ProjectFixture fixture) : ID3D9SurfaceCaptureSession
    {
        internal readonly List<double> SourceFrames = [];
        internal readonly List<int> StepCounts = [];
        internal int Disposals;
        public ValueTask<D3D9SurfacePixels> CaptureAsync(D3D9SurfaceSnapshot snapshot, D3D9SurfaceRenderOrder order, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(fixture.ConfiguredFx ? D3D9SurfaceScope.ConfiguredObjectFx : D3D9SurfaceScope.IsolatedSurface, snapshot.Scope);
            Assert.Equal(snapshot.SourceFrame, fixture.Project.CurrentFrame);
            SourceFrames.Add(snapshot.SourceFrame); StepCounts.Add(fixture.Physics.TotalSteps);
            return ValueTask.FromResult(new D3D9SurfacePixels(snapshot.Width, snapshot.Height, snapshot.SourceFrame,
                new byte[checked(snapshot.Width * snapshot.Height * 4)]));
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class RecordingSink(ProjectFixture fixture) : IFrameSink
    {
        internal readonly List<double> SourceFrames = [];
        internal int Begins;
        internal bool Completed, Aborted;
        public ValueTask BeginAsync(ExportRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Begins++; return ValueTask.CompletedTask; }
        public ValueTask WriteAsync(OfflineFrameSample sample, FrameBufferLease frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double source = sample.SampledSourceFrame.ToDouble();
            Assert.Equal(source, fixture.Project.CurrentFrame); Assert.Equal((int)(source * 4), fixture.Physics.TotalSteps);
            Assert.Equal(16, frame.Pixels.Length); SourceFrames.Add(source); return ValueTask.CompletedTask;
        }
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Completed = true; return ValueTask.CompletedTask; }
        public ValueTask AbortAsync(Exception? error, CancellationToken cancellationToken = default)
        { Aborted = true; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingPhysicsProvider : IProjectPhysicsBackendProvider
    {
        internal readonly List<TrackingPhysics> Backends = [];
        internal int TotalSteps => Backends.Sum(backend => backend.StepSeconds.Count);
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
        { var backend = new TrackingPhysics(); Backends.Add(backend); return backend; }
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new NotSupportedException();
    }

    private sealed class TrackingPhysics : ICharacterPhysicsBackend
    {
        private Matrix4x4[] worlds = [];
        internal readonly List<float> StepSeconds = [];
        internal int Disposals;
        public string Name => "CPU tracking fixture";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> input) => worlds = input.ToArray();
        public void SetKinematicTarget(int bodyIndex, Matrix4x4 worldTransform, float stepSeconds) => worlds[bodyIndex] = worldTransform;
        public Matrix4x4 GetBodyWorldTransform(int bodyIndex) => worlds[bodyIndex];
        public void Step(float stepSeconds)
        { StepSeconds.Add(stepSeconds); for (int i = 0; i < worlds.Length; i++) worlds[i].Translation += new Vector3(0, -stepSeconds, 0); }
        public void Dispose() => Disposals++;
    }

    private static PmxModel Model() => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "test", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
        Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
            Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
            SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
        Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
        Morphs = [], DisplayFrames = [], Joints = [], SoftBodies = [],
        RigidBodies = [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
            Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
            Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }],
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
        { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
            BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray()
    };
}
