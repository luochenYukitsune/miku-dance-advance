using System.Numerics;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmControllerTransactionTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void RetiredCleanupFailureDoesNotInterruptCompleteControllerPublication(bool shared, int throwingActor)
    {
        var backends = new List<ProbeSoft>();
        using var playback = Playback(shared, _ =>
        {
            var backend = new ProbeSoft { FailDispose = backends.Count == throwingActor };
            backends.Add(backend); return backend;
        });

        playback.Seek(0);
        Assert.False(playback.IsSavedPose);
        Assert.All(backends.Take(2), backend => Assert.Equal(1, backend.Disposals));
        Assert.All(backends.Skip(2), backend => Assert.Equal(0, backend.Disposals));
        Assert.Contains("injected cleanup", Assert.Single(Cleanup(playback)), StringComparison.Ordinal);
        playback.Advance(.25, 1d / 120);
        Assert.All(backends.Skip(2), backend => Assert.Equal(1, backend.Steps));
        playback.Dispose();
        Assert.All(backends, backend => Assert.Equal(1, backend.Disposals));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPreparationCleansEveryCandidateEvenWhenCandidateCleanupThrows(bool shared)
    {
        var backends = new List<ProbeSoft>();
        using var playback = Playback(shared, _ =>
        {
            var backend = new ProbeSoft { FailDispose = backends.Count == 2, FailReset = backends.Count == 3 };
            backends.Add(backend); return backend;
        });
        var saved = playback.Frame;
        var failure = Assert.Throws<InvalidOperationException>(() => playback.Seek(0));
        Assert.Contains("injected reset", failure.Message, StringComparison.Ordinal);
        Assert.True(playback.IsSavedPose); Assert.Same(saved, playback.Frame);
        Assert.All(backends.Take(2), backend => Assert.Equal(0, backend.Disposals));
        Assert.All(backends.Skip(2), backend => Assert.Equal(1, backend.Disposals));
        Assert.Contains("injected cleanup", Assert.Single(Cleanup(playback)), StringComparison.Ordinal);
        playback.Reset();
        playback.Seek(0); playback.Advance(.25, 1d / 120);
        Assert.All(backends.Take(2), backend => Assert.Equal(1, backend.Disposals));
        Assert.All(backends.Skip(4), backend => Assert.Equal(1, backend.Steps));
    }

    private static IReadOnlyList<string> Cleanup(PmmProjectPlayback playback)
    {
        var property = typeof(PmmProjectPlayback).GetProperty("CleanupDiagnostics");
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(property.GetValue(playback));
    }

    private static PmmProjectPlayback Playback(bool shared, Func<PreparedPmmModel, ISoftBodyBackend> factory)
    {
        var model = PmxSoftBodyTests.MaterialFixture();
        byte[] curves = Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray();
        PmmModel Actor(byte index) => new()
        {
            Index = index, DrawOrderIndex = index, TransformOrderIndex = index, Visible = true, LastFrame = 4,
            BoneNames = [new(0, "root", [])], BoneStates = [new(0, Vector3.Zero, Quaternion.Identity, false, false, 0)],
            InitialBoneKeyframes = [new() { NextIndex = 1, Rotation = Quaternion.Identity, Interpolation = curves }],
            BoneKeyframes = [new() { RecordIndex = 1, ObjectIndex = 1, Frame = 4, Rotation = Quaternion.Identity, Interpolation = curves }],
            InitialModelKeyframe = new() { Visible = true }
        };
        var document = new PmmDocument
        {
            Models = [Actor(0), Actor(1)], OutputWidth = 1280, OutputHeight = 720, CameraFov = 30,
            Settings = new() { PhysicsSimulationMode = 1, CameraLookAtModelIndex = -1 },
            Gravity = new() { Acceleration = 1, Direction = -Vector3.UnitY, InitialKeyframe = new() { Acceleration = 1, Direction = -Vector3.UnitY } },
            Camera = new() { Position = new(0, 0, -10), Perspective = true, InitialKeyframe = new()
            { Distance = -10, Fov = 30, Perspective = true, ParentModelIndex = -1,
                Interpolation = Enumerable.Range(0, 6).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray() } },
            Light = new() { Color = Vector3.One, Direction = -Vector3.UnitY, InitialKeyframe = new() { Color = Vector3.One, Direction = -Vector3.UnitY } }
        };
        var prepared = document.Models.Select(actor => new PreparedPmmModel(actor, "synthetic.pmx", model,
            PmmMotionAdapter.Convert(actor, model), PmmMotionAdapter.ConvertSavedPose(actor, model))).ToList();
        var plan = (PmmProjectImportPlan)Activator.CreateInstance(typeof(PmmProjectImportPlan), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [document, "synthetic.pmm", prepared, new List<PmmUnresolvedAsset>(), new List<string>()], null)!;
        var rest = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
            new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1));
        return new(plan, new Dictionary<int, OriginalFrameScene> { [0] = rest, [1] = rest },
            (_, _) => new TrackingSharedBackend(), shared, factory);
    }

    private sealed class ProbeSoft : ISoftBodyBackend
    {
        private SoftBodyDefinition[] definitions = [];
        private Vector3[] targets = [];
        public bool FailDispose, FailReset;
        public int Disposals, Steps;
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(IReadOnlyList<SoftBodyDefinition> value, IReadOnlyList<PhysicsBodyDefinition> colliders) => definitions = value.ToArray();
        public void Reset(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Matrix4x4> rigids)
        { if (FailReset) throw new InvalidOperationException("injected reset"); targets = positions.ToArray(); }
        public void Step(float seconds, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Matrix4x4> rigids)
        { ObjectDisposedException.ThrowIf(Disposals != 0, this); Steps++; targets = positions.ToArray(); }
        public IReadOnlyList<SoftBodySample> Sample() => definitions.Select(definition => new SoftBodySample(definition.VertexIndices.ToArray(),
            definition.VertexIndices.Select(index => targets[index]).ToArray(), new Vector3[definition.VertexIndices.Count])).ToArray();
        public void SetGravity(Vector3 gravity) { }
        public void Dispose() { Disposals++; if (FailDispose) throw new InvalidOperationException("injected cleanup"); }
    }
}
