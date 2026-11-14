using System.Collections;
using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

[CollectionDefinition("Controller preparation", DisableParallelization = true)]
public sealed class ControllerPreparationCollection { }

[Collection("Controller preparation")]
public sealed class SceneControllerPreparationTests
{
    private const string DegreeName = "Mmd.Modern.Application.ParallelControllerPreparation.Degree";
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SixteenWayPreparationPreservesOutsideParentPoseFactoryOrderAndProjectSeek(bool shared)
    {
        var f = Fixture();
        var expectedFactory = new Factory(); var actualFactory = new Factory();
        using var scope = new PreparationScope(1);
        using var expected = Project(expectedFactory);
        AppContext.SetData(DegreeName, 16);
        using var actual = Project(actualFactory);
        EqualProject(expected, actual, f.Actors);
        expected.Advance(1d / 30, Token); actual.Advance(1d / 30, Token);
        EqualProject(expected, actual, f.Actors);
        AppContext.SetData(DegreeName, 1); expected.Seek(19, Token);
        AppContext.SetData(DegreeName, 16); actual.Seek(19, Token);
        EqualProject(expected, actual, f.Actors);
        Assert.Equal(expectedFactory.Order, actualFactory.Order);
        Assert.Equal(expectedFactory.Worlds.Select(world => world.Inner.Steps), actualFactory.Worlds.Select(world => world.Inner.Steps));
        if (!shared) Assert.Equal(new[] { f.Actors[2].Id, f.Actors[0].Id, f.Actors[1].Id, f.Actors[3].Id }, actualFactory.Order.Take(4));

        SceneProjectSession Project(Factory factory) => new(f.Layout, new(), f.Motions,
            new ProjectSettingsBackendProvider((_, item) => factory.Create(item.Id), _ => factory.Create(Guid.Empty), poseInitializationSubsteps: 3),
            new ProjectSettings { Physics = new() { SharedWorld = shared, GroundEnabled = false } },
            outsideParents: [new(f.Actors[0].Id, -1, f.Actors[2].Id, 0, Matrix4x4.CreateTranslation(.25f, 0, 0))],
            initialFrame: 7.5, cancellationToken: Token);
    }

    [Fact]
    public async Task SixteenWayPreparationOverlapsOnExclusiveCallerSchedulerAndKeepsSharedFactoryOnCaller()
    {
        if (Environment.ProcessorCount < 2) return;
        var f = Fixture(); var ids = f.Actors.Select(actor => actor.Id).ToHashSet();
        var scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1);
        using var overlap = new ManualResetEventSlim();
        int active = 0, peak = 0, caller = 0; var factoryThreads = new List<int>();
        using var scope = new PreparationScope(16, id =>
        {
            if (!ids.Contains(id)) return;
            int count = Interlocked.Increment(ref active);
            UpdatePeak(ref peak, count);
            if (count >= 2) overlap.Set();
            try { overlap.Wait(TimeSpan.FromSeconds(3), Token); }
            finally { Interlocked.Decrement(ref active); }
        });
        try
        {
            var construction = Task.Factory.StartNew(() =>
            {
                caller = Environment.CurrentManagedThreadId;
                return new SceneAnimationSession(f.Layout.Objects, f.Motions,
                    _ => throw new InvalidOperationException("Expected one shared backend."), cancellationToken: Token,
                    sharedPhysicsFactory: () => { factoryThreads.Add(Environment.CurrentManagedThreadId); return new ProbeBackend(); });
            }, Token, TaskCreationOptions.None, scheduler.ExclusiveScheduler);
            using var session = await construction.WaitAsync(TimeSpan.FromSeconds(12), Token);
            Assert.InRange(peak, 2, Math.Min(16, Environment.ProcessorCount));
            Assert.Single(factoryThreads);
            Assert.All(factoryThreads, thread => Assert.Equal(caller, thread));
        }
        finally { scheduler.Complete(); }
    }

    [Theory]
    [InlineData("vertices")]
    [InlineData("weights")]
    [InlineData("ik")]
    [InlineData("morph")]
    [InlineData("motion")]
    public void UnknownCollectionsKeepEveryReadAndPreparationOnCaller(string collection)
    {
        var f = Fixture(); var reads = new ConcurrentBag<int>();
        int caller = Environment.CurrentManagedThreadId;
        switch (collection)
        {
            case "vertices": Set(f.Model, nameof(PmxModel.Vertices), new ObservedList<PmxVertex>(f.Model.Vertices, reads)); break;
            case "weights": Set(f.Model.Vertices[0], nameof(PmxVertex.BoneWeights), new ObservedList<float>(f.Model.Vertices[0].BoneWeights, reads)); break;
            case "ik":
                // Active IK makes rig preparation enumerate the unknown links collection.
                Set(f.Model, nameof(PmxModel.Bones), new PmxBone[]
                {
                    f.Model.Bones[0],
                    new() { Name = "tip", EnglishName = "", Position = Vector3.UnitX, ParentIndex = 0,
                        DeformDepth = 0, Flags = PmxBoneFlags.None },
                    new() { Name = "goal", EnglishName = "", Position = Vector3.UnitY, ParentIndex = -1,
                        DeformDepth = 1, Flags = PmxBoneFlags.Ik, IkTargetBoneIndex = 1,
                        IkIterationCount = 1, IkLimit = MathF.PI,
                        IkLinks = new ObservedList<PmxIkLink>(new PmxIkLink[] { new(0, false, default, default) }, reads) }
                });
                break;
            case "morph": Set(f.Model.Morphs[0], nameof(PmxMorph.PositionOffsets), new ObservedList<PmxMorph.PositionOffset>(f.Model.Morphs[0].PositionOffsets, reads)); break;
            case "motion":
                var motion = f.Motions[0].Motion;
                f.Motions[0] = f.Motions[0] with { Motion = new() { BoneKeyframes = new ObservedList<VmdBoneKeyframe>(motion.BoneKeyframes, reads), MorphKeyframes = motion.MorphKeyframes }, Asset = null };
                break;
        }
        var preparedThreads = new ConcurrentBag<int>();
        using var scope = new PreparationScope(16, _ => preparedThreads.Add(Environment.CurrentManagedThreadId));
        using var session = new SceneAnimationSession(f.Layout.Objects, f.Motions,
            _ => throw new InvalidOperationException("Expected one shared backend."), cancellationToken: Token,
            sharedPhysicsFactory: () => new ProbeBackend());
        Assert.NotEmpty(reads);
        Assert.All(reads, thread => Assert.Equal(caller, thread));
        Assert.Equal(4, preparedThreads.Count);
        Assert.All(preparedThreads, thread => Assert.Equal(caller, thread));
    }

    [Fact]
    public void PreparationJoinsWorkersThrowsFirstActorAndDisposesEveryUnadoptedController()
    {
        if (Environment.ProcessorCount < 2) return;
        var f = Fixture(); var prepared = new ConcurrentDictionary<Guid, CharacterAnimationController>();
        using var laterFault = new ManualResetEventSlim(); var factory = new Factory();
        using var scope = new PreparationScope(16, after: (id, controller) =>
        {
            prepared[id] = controller;
            if (id == f.Actors[1].Id)
            {
                if (Environment.ProcessorCount >= 2) laterFault.Wait(TimeSpan.FromSeconds(3), Token);
                throw new InvalidOperationException("first actor preparation fault");
            }
            if (id == f.Actors[3].Id) { laterFault.Set(); throw new InvalidOperationException("later actor preparation fault"); }
        });
        var error = Assert.Throws<SceneActorAnimationException>(() => new SceneAnimationSession(f.Layout.Objects, f.Motions,
            _ => throw new InvalidOperationException("Expected one shared backend."), cancellationToken: Token,
            sharedPhysicsFactory: () => factory.Create(Guid.Empty)));
        Assert.Equal(f.Actors[1].Id, error.ObjectId);
        Assert.Contains("first actor preparation fault", error.InnerException!.Message);
        Assert.Equal(4, prepared.Count);
        Assert.Empty(factory.Order);
        Assert.All(prepared.Values, AssertDisposed);
    }

    [Fact]
    public void IndependentFactoryFailureKeepsSerialConstructionAndErrorOrder()
    {
        var f = Fixture(); var prepared = new ConcurrentDictionary<Guid, CharacterAnimationController>();
        var factoryOrder = new List<Guid>();
        using var scope = new PreparationScope(16, after: (id, controller) =>
        {
            prepared[id] = controller;
            if (id == f.Actors[1].Id) throw new InvalidOperationException("later preparation fault");
        });
        var error = Assert.Throws<SceneActorAnimationException>(() => new SceneAnimationSession(f.Layout.Objects, f.Motions,
            item => { factoryOrder.Add(item.Id); throw new InvalidOperationException("earlier factory fault"); }, cancellationToken: Token));
        Assert.Equal(f.Actors[0].Id, error.ObjectId);
        Assert.Contains("earlier factory fault", error.InnerException!.Message);
        Assert.Equal(new[] { f.Actors[0].Id }, factoryOrder);
        Assert.Equal(f.Actors[0].Id, Assert.Single(prepared).Key);
        Assert.All(prepared.Values, AssertDisposed);
    }

    [Fact]
    public void SharedFactoryCancellationDisposesEveryPreparedControllerAndBackend()
    {
        if (Environment.ProcessorCount < 2) return;
        var f = Fixture(); var prepared = new ConcurrentDictionary<Guid, CharacterAnimationController>();
        var factory = new Factory(); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var scope = new PreparationScope(16, after: (id, controller) => prepared[id] = controller);
        Assert.ThrowsAny<OperationCanceledException>(() => new SceneAnimationSession(f.Layout.Objects, f.Motions,
            _ => throw new InvalidOperationException("Expected one shared backend."), cancellationToken: cancellation.Token,
            sharedPhysicsFactory: () => { var backend = factory.Create(Guid.Empty); backend.AfterInitialize = cancellation.Cancel; return backend; }));
        Assert.Equal(4, prepared.Count);
        Assert.Equal(new[] { Guid.Empty }, factory.Order);
        Assert.True(Assert.Single(factory.Worlds).Disposed);
        Assert.All(prepared.Values, AssertDisposed);
    }

    [Fact]
    public void IndependentFactoryMutationOfLaterModelRetainsSerialTopologyValidation()
    {
        foreach (int degree in new[] { 1, 16 })
        {
            var f = Fixture();
            // Only the later actor owns this mutable vertex array. The first actor's controller
            // and physics initialization must finish before the later topology is inspected.
            var laterModel = ArrayBackedClone(f.Model);
            f.Motions[1] = f.Motions[1] with { Model = laterModel };
            var vertices = (PmxVertex[])laterModel.Vertices;
            var operations = new ConcurrentQueue<(string Stage, Guid Id, int Thread)>();
            var controllers = new ConcurrentBag<CharacterAnimationController>(); var factory = new Factory();
            int caller = Environment.CurrentManagedThreadId;
            using var scope = new PreparationScope(degree,
                id => operations.Enqueue(("validate", id, Environment.CurrentManagedThreadId)),
                (id, controller) => { operations.Enqueue(("construct", id, Environment.CurrentManagedThreadId)); controllers.Add(controller); });
            var error = Assert.Throws<SceneActorAnimationException>(() => new SceneAnimationSession(f.Layout.Objects, f.Motions,
                item =>
                {
                    operations.Enqueue(("factory", item.Id, Environment.CurrentManagedThreadId));
                    vertices[0] = vertices[1];
                    return factory.Create(item.Id);
                }, cancellationToken: Token));
            Assert.Equal(f.Actors[1].Id, error.ObjectId);
            Assert.Contains("rest vertices", error.InnerException!.Message);
            Assert.Equal(new[] { ("validate", f.Actors[0].Id), ("construct", f.Actors[0].Id),
                ("factory", f.Actors[0].Id), ("validate", f.Actors[1].Id) }, operations.Select(item => (item.Stage, item.Id)));
            Assert.All(operations, item => Assert.Equal(caller, item.Thread));
            Assert.Equal(new[] { f.Actors[0].Id }, factory.Order);
            Assert.True(Assert.Single(factory.Worlds).Disposed);
            AssertDisposed(Assert.Single(controllers));
        }
    }

    private static (SceneCompositionEditor Layout, EditableSceneObject[] Actors, SceneActorMotion[] Motions, PmxModel Model) Fixture()
    {
        var model = ArrayBackedClone(SharedPhysicsFixture.Model());
        Set(model, nameof(PmxModel.Vertices), model.Vertices.Select(ArrayBackedClone).ToArray());
        Set(model, nameof(PmxModel.Bones), model.Bones.Select(ArrayBackedClone).ToArray());
        Set(model, nameof(PmxModel.Morphs), new PmxMorph[] { new() { Name = "move", EnglishName = "", ControlPanel = 4, Type = PmxMorphType.Position,
            PositionOffsets = new PmxMorph.PositionOffset[] { new(0, new(.1f, .2f, .3f)) } } });
        var layout = new SceneCompositionEditor(); var rest = SharedPhysicsFixture.Rest(model);
        var actors = Enumerable.Range(0, 4).Select(index => layout.Add("actor" + index, "prepare" + index + ".pmx", ModelRole.Character, rest)).ToArray();
        var motions = actors.Select((actor, index) =>
        {
            var motion = new VmdMotion
            {
                BoneKeyframes = new VmdBoneKeyframe[] { new() { BoneName = "root" }, new() { BoneName = "root", Frame = 30, Translation = new(index + 1, 0, 0) } },
                MorphKeyframes = new VmdMorphKeyframe[] { new() { MorphName = "move", Weight = .25f }, new() { MorphName = "move", Frame = 30, Weight = .75f } }
            };
            if (index % 2 != 0) return new SceneActorMotion(actor.Id, model, motion);
            var asset = SceneMotionAsset.FromMotion(motion);
            return new SceneActorMotion(actor.Id, model, asset.Motion, Asset: asset);
        }).ToArray();
        return (layout, actors, motions, model);
    }

    private static void EqualProject(SceneProjectSession expected, SceneProjectSession actual, EditableSceneObject[] actors)
    {
        var a = expected.Frame; var b = actual.Frame;
        Assert.Equal(expected.CurrentFrame, actual.CurrentFrame);
        Assert.Equal(expected.Actors.Select(actor => (actor.ObjectId, actor.LocalFrame, actor.PhysicsEnabled)),
            actual.Actors.Select(actor => (actor.ObjectId, actor.LocalFrame, actor.PhysicsEnabled)));
        Assert.Equal(Bytes(a.Surface.Snapshot.Vertices.ToArray()), Bytes(b.Surface.Snapshot.Vertices.ToArray()));
        Assert.Equal(a.Surface.Snapshot.Indices.ToArray(), b.Surface.Snapshot.Indices.ToArray());
        Assert.Equal(a.Surface.Snapshot.Materials, b.Surface.Snapshot.Materials);
        Assert.Equal(a.Surface.Materials, b.Surface.Materials);
        Assert.Equal(Bytes(a.Outline!.Vertices.ToArray()), Bytes(b.Outline!.Vertices.ToArray()));
        Assert.Equal(a.Outline.Materials, b.Outline.Materials);
        Assert.Equal(a.Materials, b.Materials);
        var first = (SceneAnimationSession)typeof(SceneProjectSession).GetField("animation", PrivateInstance)!.GetValue(expected)!;
        var second = (SceneAnimationSession)typeof(SceneProjectSession).GetField("animation", PrivateInstance)!.GetValue(actual)!;
        foreach (var actor in actors) Assert.Equal(Bytes(first.GetBoneWorldMatrices(actor.Id).ToArray()), Bytes(second.GetBoneWorldMatrices(actor.Id).ToArray()));
    }

    private static byte[] Bytes<T>(T[] values) where T : unmanaged => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static void AssertDisposed(CharacterAnimationController controller)
        => Assert.True((bool)typeof(CharacterAnimationController).GetField("disposed", PrivateInstance)!.GetValue(controller)!);
    private static void UpdatePeak(ref int peak, int count)
    {
        int old; do { old = peak; if (old >= count) return; } while (Interlocked.CompareExchange(ref peak, count, old) != old);
    }
    private static void Set(object target, string property, object value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    private static T ArrayBackedClone<T>(T original) where T : class
    {
        var clone = (T)typeof(object).GetMethod("MemberwiseClone", PrivateInstance)!.Invoke(original, null)!;
        var toArray = typeof(Enumerable).GetMethods().Single(method => method.Name == nameof(Enumerable.ToArray) && method.GetParameters().Length == 1);
        foreach (var property in typeof(T).GetProperties())
            if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                property.SetValue(clone, toArray.MakeGenericMethod(property.PropertyType.GetGenericArguments()[0]).Invoke(null, [property.GetValue(original)]));
        return clone;
    }

    private sealed class PreparationScope : IDisposable
    {
        private readonly object? previousDegree = AppContext.GetData(DegreeName);
        private readonly FieldInfo beforeField = typeof(SceneAnimationSession).GetField("ControllerPreparationObserverForTesting", PrivateStatic)!;
        private readonly FieldInfo afterField = typeof(SceneAnimationSession).GetField("ControllerPreparedObserverForTesting", PrivateStatic)!;
        private readonly object? previousBefore, previousAfter;
        internal PreparationScope(int degree, Action<Guid>? before = null, Action<Guid, CharacterAnimationController>? after = null)
        {
            previousBefore = beforeField.GetValue(null); previousAfter = afterField.GetValue(null);
            AppContext.SetData(DegreeName, degree); beforeField.SetValue(null, before); afterField.SetValue(null, after);
        }
        public void Dispose()
        { beforeField.SetValue(null, previousBefore); afterField.SetValue(null, previousAfter); AppContext.SetData(DegreeName, previousDegree); }
    }

    private sealed class ObservedList<T>(IReadOnlyList<T> source, ConcurrentBag<int> reads) : IReadOnlyList<T>
    {
        private void Read() => reads.Add(Environment.CurrentManagedThreadId);
        public int Count { get { Read(); return source.Count; } }
        public T this[int index] { get { Read(); return source[index]; } }
        public IEnumerator<T> GetEnumerator() { Read(); return source.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Factory
    {
        internal readonly List<Guid> Order = [];
        internal readonly List<ProbeBackend> Worlds = [];
        internal ProbeBackend Create(Guid id) { Order.Add(id); var backend = new ProbeBackend(); Worlds.Add(backend); return backend; }
    }

    private sealed class ProbeBackend : ICharacterPhysicsMutableBackend
    {
        internal readonly TrackingSharedBackend Inner = new();
        internal bool Disposed;
        internal Action? AfterInitialize;
        public string Name => "controller preparation probe";
        public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition definition, ReadOnlySpan<Matrix4x4> transforms)
        { Inner.Initialize(definition, transforms); AfterInitialize?.Invoke(); }
        public int AddBody(PhysicsBodyDefinition body, Matrix4x4 transform) => Inner.AddBody(body, transform);
        public int AddJoint(PhysicsJointDefinition joint, Matrix4x4 a, Matrix4x4 b) => Inner.AddJoint(joint, a, b);
        public void RemoveBody(int index) => Inner.RemoveBody(index);
        public void RemoveJoint(int index) => Inner.RemoveJoint(index);
        public void ResetBody(int index, Matrix4x4 transform) => Inner.ResetBody(index, transform);
        public void SetBodyKinematic(int index, bool value) => Inner.SetBodyKinematic(index, value);
        public void SetKinematicTarget(int index, Matrix4x4 transform, float seconds) => Inner.SetKinematicTarget(index, transform, seconds);
        public Matrix4x4 GetBodyWorldTransform(int index) => Inner.GetBodyWorldTransform(index);
        public void SetGravity(Vector3 gravity) => Inner.SetGravity(gravity);
        public void ApplyImpulse(int index, Vector3 linear, Vector3 angular) => Inner.ApplyImpulse(index, linear, angular);
        public void Step(float seconds) => Inner.Step(seconds);
        public void Dispose() { Disposed = true; Inner.Dispose(); }
    }
}
