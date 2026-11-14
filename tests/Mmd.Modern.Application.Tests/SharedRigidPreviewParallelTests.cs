using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed partial class SharedRigidPlaybackTests
{
    private const string PreviewParallelSwitch = "Mmd.Modern.Application.ExperimentalParallelPreviewLeaves";

    [Fact]
    public async Task ParallelPreviewOverlapsWhenCaptureRunsOnExclusiveScheduler()
    {
        if (Environment.ProcessorCount < 2) return;
        AppContext.TryGetSwitch(PreviewParallelSwitch, out bool old);
        AppContext.SetSwitch(PreviewParallelSwitch, true);
        var testToken = TestContext.Current.CancellationToken;
        var scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1);
        try
        {
            using var project = ParallelFixture(out _); AdvancePreview(project, 1.0 / 30);
            using var overlap = new ManualResetEventSlim(); int active = 0, peak = 0;
            Observe(project, _ =>
            {
                int now = Interlocked.Increment(ref active);
                int oldPeak; do { oldPeak = peak; if (oldPeak >= now) break; } while (Interlocked.CompareExchange(ref peak, now, oldPeak) != oldPeak);
                if (now >= 2) overlap.Set();
                overlap.Wait(TimeSpan.FromSeconds(1), testToken); Interlocked.Decrement(ref active);
            });
            var capture = Task.Factory.StartNew(() => project.CaptureRenderSnapshot(), testToken,
                TaskCreationOptions.None, scheduler.ExclusiveScheduler);
            _ = await capture.WaitAsync(TimeSpan.FromSeconds(8), testToken);
            Assert.True(peak >= 2, "A restricted caller scheduler must not serialize independent leaves.");
        }
        finally { scheduler.Complete(); AppContext.SetSwitch(PreviewParallelSwitch, old); }
    }

    [Fact]
    public void ParallelPreviewCompletesOnlyLeavesAndPreservesPublishedOrderAndGeometry()
    {
        if (Environment.ProcessorCount < 2) return;
        WithParallelPreview(() =>
        {
            using var expected = ParallelFixture(out var expectedActors);
            using var actual = ParallelFixture(out var actors);
            expected.Advance(1.0 / 30, Token); AdvancePreview(actual, 1.0 / 30);
            var entered = new ConcurrentDictionary<Guid, byte>(); int active = 0, peak = 0;
            using var twoWorkers = new ManualResetEventSlim();
            Observe(actual, id =>
            {
                entered.TryAdd(id, 0); int now = Interlocked.Increment(ref active);
                if (now >= 2) twoWorkers.Set();
                int old; do { old = peak; if (old >= now) break; } while (Interlocked.CompareExchange(ref peak, now, old) != old);
                twoWorkers.Wait(TimeSpan.FromSeconds(10)); Interlocked.Decrement(ref active);
            });
            var snapshot = actual.CaptureRenderSnapshot(); var oracle = expected.CaptureRenderSnapshot();
            Assert.Equal(actors.Skip(1).Select(a => a.Id).Order(), entered.Keys.Order());
            Assert.True(peak >= 2, "Expected real overlapping independent leaf samples.");
            Assert.Equal(oracle.SourceFrame, snapshot.SourceFrame);
            AssertSpanBitsEqual<SceneVertex>(oracle.Scene.Surface.Snapshot.Vertices, snapshot.Scene.Surface.Snapshot.Vertices);
            AssertBitsEqual(oracle.Scene.Outline!.Vertices, snapshot.Scene.Outline!.Vertices);
            Assert.Equal(expectedActors.Select(a => a.Name), actors.Select(a => a.Name));
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    public void ParallelPreviewRespectsRequestedWorkerLimit(int requested)
    {
        WithParallelPreview(() =>
        {
            const string degreeName = "Mmd.Modern.Application.ExperimentalParallelPreviewLeaves.Degree";
            object? old = AppContext.GetData(degreeName); AppContext.SetData(degreeName, requested);
            try
            {
                using var project = ParallelFixture(out _, 21); AdvancePreview(project, 1.0 / 30);
                int active = 0, peak = 0, visited = 0;
                Observe(project, _ =>
                {
                    int now = Interlocked.Increment(ref active);
                    int oldPeak; do { oldPeak = peak; if (oldPeak >= now) break; } while (Interlocked.CompareExchange(ref peak, now, oldPeak) != oldPeak);
                    Interlocked.Increment(ref visited); Thread.Sleep(20); Interlocked.Decrement(ref active);
                });
                _ = project.Frame;
                Assert.Equal(20, visited);
                Assert.InRange(peak, 1, Math.Min(requested, Environment.ProcessorCount));
            }
            finally { AppContext.SetData(degreeName, old); }
        });
    }

    [Fact]
    public void ParallelPreviewJoinsAllLeavesAndThrowsFirstFaultBeforeSeekRecovery()
    {
        WithParallelPreview(() =>
        {
            using var project = ParallelFixture(out var actors);
            var lastGood = project.Frame; AdvancePreview(project, 1.0 / 30);
            var visited = new ConcurrentDictionary<Guid, byte>();
            Observe(project, id =>
            {
                visited.TryAdd(id, 0);
                if (id == actors[1].Id || id == actors[3].Id) throw new InvalidOperationException(id.ToString());
            });
            var error = Assert.Throws<InvalidOperationException>(() => _ = project.Frame);
            Assert.Equal(actors[1].Id.ToString(), error.Message);
            Assert.Equal(actors.Skip(1).Select(a => a.Id).Order(), visited.Keys.Order());
            Assert.True(project.IsFaulted); Assert.Same(lastGood, project.Frame);
            Observe(project, null); project.Seek(0, Token); Assert.False(project.IsFaulted);
        });
    }

    [Theory]
    [InlineData("soft")]
    [InlineData("ik")]
    [InlineData("morph")]
    [InlineData("cancelable")]
    public void ParallelPreviewFallsBackForCustomCollectionsAndCancelableToken(string reason)
    {
        WithParallelPreview(() =>
        {
            using var project = ParallelFixture(out _); AdvancePreview(project, 1.0 / 30);
            var model = (PmxModel)typeof(CharacterAnimationController).GetField("model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(PreviewController(project, 1))!;
            object owner = reason == "ik" ? model.Bones[0] : reason == "morph" ? model.Morphs[0] : model;
            string propertyName = reason == "ik" ? nameof(PmxBone.IkLinks) : reason == "morph" ? nameof(PmxMorph.PositionOffsets) : nameof(PmxModel.SoftBodies);
            var property = owner.GetType().GetProperty(propertyName)!;
            var original = property.GetValue(owner)!;
            if (reason == "ik") property.SetValue(owner, new CustomList<PmxIkLink>((IReadOnlyList<PmxIkLink>)original));
            if (reason == "morph") property.SetValue(owner, new CustomList<PmxMorph.PositionOffset>((IReadOnlyList<PmxMorph.PositionOffset>)original));
            if (reason == "soft") property.SetValue(owner, new CustomList<PmxSoftBody>((IReadOnlyList<PmxSoftBody>)original));
            int active = 0, peak = 0; var threads = new HashSet<int>();
            Observe(project, _ => { int now = Interlocked.Increment(ref active); peak = Math.Max(peak, now); lock (threads) threads.Add(Environment.CurrentManagedThreadId); Thread.Sleep(10); Interlocked.Decrement(ref active); });
            try
            {
                if (reason == "cancelable")
                {
                    using var cts = new CancellationTokenSource();
                    var method = typeof(SceneProjectSession).GetMethod("CapturePosePreviewRequestWithCancellation")!;
                    _ = method.Invoke(project, [new Dictionary<Guid, VmdMotion>(), cts.Token]);
                }
                else _ = project.Frame;
                Assert.Equal(1, peak); Assert.Single(threads);
            }
            finally { property.SetValue(owner, original); }
        });
    }

    private static SceneProjectSession ParallelFixture(out EditableSceneObject[] actors, int actorCount = 5)
    {
        // Collection expressions targeting IReadOnlyList may create compiler-generated wrappers.
        // Give this fixture independent array-backed PMX definitions like the production reader.
        var original = Model(); var model = ArrayBackedClone(original);
        typeof(PmxModel).GetProperty(nameof(PmxModel.Bones))!.SetValue(model, original.Bones.Select(ArrayBackedClone).ToArray());
        typeof(PmxModel).GetProperty(nameof(PmxModel.Morphs))!.SetValue(model, original.Morphs.Select(ArrayBackedClone).ToArray());
        typeof(PmxModel).GetProperty(nameof(PmxModel.Vertices))!.SetValue(model, original.Vertices.Select(ArrayBackedClone).ToArray());
        var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
        actors = Enumerable.Range(0, actorCount).Select(i => layout.Add("actor" + i, "parallel" + i + ".pmx", ModelRole.Character, rest)).ToArray();
        var motions = actors.Select((actor, i) => new SceneActorMotion(actor.Id, model, Motion(), PhysicsEnabled: i == 0)).ToArray();
        return new(layout, new(), motions, new Factory().Provider(), new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } }, cancellationToken: Token);
    }

    private static void Observe(SceneProjectSession project, Action<Guid>? observer)
        => typeof(SceneAnimationSession).GetField("PreviewLeafObserverForTesting", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(PreviewAnimation(project), observer);

    private static T ArrayBackedClone<T>(T original) where T : class
    {
        var clone = (T)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
        var toArray = typeof(Enumerable).GetMethods().Single(method => method.Name == nameof(Enumerable.ToArray) && method.GetParameters().Length == 1);
        foreach (var property in typeof(T).GetProperties())
        {
            if (!property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(IReadOnlyList<>)) continue;
            var element = property.PropertyType.GetGenericArguments()[0];
            property.SetValue(clone, toArray.MakeGenericMethod(element).Invoke(null, [property.GetValue(original)]));
        }
        return clone;
    }

    private static void WithParallelPreview(Action action)
    {
        AppContext.TryGetSwitch(PreviewParallelSwitch, out bool old);
        AppContext.SetSwitch(PreviewParallelSwitch, true);
        try { action(); } finally { AppContext.SetSwitch(PreviewParallelSwitch, old); }
    }

    private sealed class CustomList<T>(IReadOnlyList<T> source) : IReadOnlyList<T>
    {
        public int Count => source.Count;
        public T this[int index] => source[index];
        public IEnumerator<T> GetEnumerator() => source.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
