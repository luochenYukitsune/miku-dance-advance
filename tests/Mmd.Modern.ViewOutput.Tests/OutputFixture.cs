using System.Numerics;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;

namespace Mmd.Modern.ViewOutput.Tests;

internal sealed class OutputFixture
{
    public TestRenderer Renderer { get; } = new();
    public SceneAnimationSession? Session { get; private set; }
    public ExportCoordinator Coordinator() => new(CreateSession, (_, _) => ValueTask.FromResult<IViewRenderer>(Renderer));
    public ValueTask<SceneAnimationSession> CreateSession(CancellationToken token)
    {
        var model = new PmxModel
        {
            Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
                VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "test", EnglishName = "", Comment = "", EnglishComment = "", Textures = [], Faces = [new(0, 1, 2)],
            Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0, TextureIndex = -1,
                SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Bones = [new() { Name = "root", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }],
            Morphs = [], DisplayFrames = [], Joints = [], RigidBodies = [], SoftBodies = [],
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(position => new PmxVertex
            { Position = position, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1,
                BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray()
        };
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
        var layout = new SceneCompositionEditor(); var actor = layout.Add("test", "test.pmx", ModelRole.Character, rest);
        Session = new(layout.Objects, [new(actor.Id, model, new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30 }] }, PhysicsEnabled: false)],
            _ => throw new InvalidOperationException("No physics in fixture"), cancellationToken: token);
        return ValueTask.FromResult(Session);
    }
}

internal sealed class TestRenderer : IViewRenderer
{
    public ViewRendererCapabilities Capabilities { get; set; } = new("test-cpu", 4096, 4096, true, false);
    public int Captures, Releases, Outstanding, MaximumOutstanding, Disposals;
    public bool WrongSize, DisposeFailure;
    public Exception? CaptureFailure;
    public ValueTask<FrameBufferLease> CaptureAsync(OfflineFrame frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Captures++;
        if (CaptureFailure is not null) throw CaptureFailure;
        int width = WrongSize ? 1 : frame.Width;
        Outstanding++; MaximumOutstanding = Math.Max(MaximumOutstanding, Outstanding);
        return ValueTask.FromResult(new FrameBufferLease(width, frame.Height, width * 4, new byte[width * frame.Height * 4], () => { Releases++; Outstanding--; }));
    }
    public ValueTask DisposeAsync()
    { Disposals++; if (DisposeFailure) throw new IOException("renderer cleanup"); return ValueTask.CompletedTask; }
}

internal sealed class TestSink : IFrameSink
{
    public bool Begun, Completed, Aborted, Disposed, AbortTokenWasCancelled, AbortFailure;
    public string? FailureStage;
    public Exception? Failure;
    public Func<FrameBufferLease, CancellationToken, ValueTask>? Writing;
    public List<OfflineFrameSample> Samples { get; } = [];
    public ValueTask BeginAsync(ExportRequest request, CancellationToken cancellationToken = default)
    { Begun = true; Fail("begin"); return ValueTask.CompletedTask; }
    public async ValueTask WriteAsync(OfflineFrameSample sample, FrameBufferLease frame, CancellationToken cancellationToken = default)
    { Fail("write"); if (Writing is not null) await Writing(frame, cancellationToken); _ = frame.Pixels; Samples.Add(sample); }
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    { Fail("complete"); Completed = true; return ValueTask.CompletedTask; }
    public ValueTask AbortAsync(Exception? error, CancellationToken cancellationToken = default)
    { Aborted = true; AbortTokenWasCancelled = cancellationToken.IsCancellationRequested; if (AbortFailure) throw new IOException("abort cleanup"); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    private void Fail(string stage) { if (FailureStage == stage) throw Failure!; }
}
