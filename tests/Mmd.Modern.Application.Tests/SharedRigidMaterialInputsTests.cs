using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed partial class SharedRigidPlaybackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentMaterialCaptureDoesNotCompleteVisualOrPhysicalPreviewLeaves(bool physicalLeaf)
    {
        AppContext.SetSwitch(PhysicalPreviewSwitch, true);
        try
        {
            var model = MaterialDemandModel(); var rest = SharedPhysicsFixture.Rest(model); var layout = new SceneCompositionEditor();
            var driver = layout.Add("driver", "material-driver.pmx", ModelRole.Character, rest);
            var leaf = layout.Add("hidden leaf", "material-leaf.pmx", ModelRole.Character, rest);
            var motions = new[] { new SceneActorMotion(driver.Id, model, MaterialDemandMotion()),
                new SceneActorMotion(leaf.Id, model, MaterialDemandMotion(hidden: true), PhysicsEnabled: physicalLeaf) };
            var settings = new ProjectSettings { Physics = new() { SharedWorld = true, GroundEnabled = false } };
            var expectedFactory = new Factory(); var actualFactory = new Factory();
            using var expected = Create(expectedFactory); using var actual = Create(actualFactory);
            expected.Advance(1.0 / 30, Token); AdvancePreview(actual, 1.0 / 30);
            var animation = PreviewAnimation(actual); var controller = PreviewController(actual, 1);
            var bones = controller.BoneWorldMatrices;
            var sampledField = typeof(CharacterAnimationController).GetField("sampledFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var deferredField = typeof(CharacterAnimationController).GetField("deferredRigidPose", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var recipeFrameField = typeof(CharacterAnimationController).GetField("preparedMorphsFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var sampled = sampledField.GetValue(controller); var deferred = deferredField.GetValue(controller);
            var pending = (Dictionary<Guid, double>)typeof(SceneAnimationSession).GetField("previewLeaves", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(animation)!;
            Assert.Equal(1, pending[leaf.Id]);
            if (physicalLeaf) Assert.NotNull(deferred);
            else { Assert.Null(deferred); Assert.NotEqual(1.0, (double)recipeFrameField.GetValue(controller)!); }
            var observer = typeof(SceneAnimationSession).GetField("PreviewLeafObserverForTesting", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var backend = Assert.Single(actualFactory.Worlds);
            var boundaries = backend.Boundaries.ToArray(); var targets = backend.Targets.ToArray();
            int impulses = backend.Impulses, initializations = backend.Inner.Initializations;
            long steps = animation.SharedPhysicsStepCount;
            observer.SetValue(animation, (Action<Guid>)(_ => throw new InvalidOperationException("Material capture completed a preview leaf.")));
            SceneObjectMaterialInputs input;
            try
            {
                input = CaptureCurrentMaterialInputs(animation, leaf.Id);
                Assert.Equal(input.Materials, CaptureCurrentMaterialInputs(animation, leaf.Id).Materials);
            }
            finally { observer.SetValue(animation, null); }
            Assert.Equal(1, animation.CurrentFrame); Assert.Equal(1, input.LocalFrame); Assert.Equal(1, input.SourceFrame);
            Assert.False(input.Visible); Assert.Equal(model.Materials.Count, input.MaterialCount);
            Assert.Same(bones, controller.BoneWorldMatrices); Assert.Equal(sampled, sampledField.GetValue(controller));
            Assert.Same(deferred, deferredField.GetValue(controller)); Assert.Same(pending,
                typeof(SceneAnimationSession).GetField("previewLeaves", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(animation));
            Assert.Equal(boundaries, backend.Boundaries); Assert.Equal(targets, backend.Targets);
            Assert.Equal(impulses, backend.Impulses); Assert.Equal(initializations, backend.Inner.Initializations);
            Assert.Equal(steps, animation.SharedPhysicsStepCount);
            var oracle = expected.CaptureRenderSnapshot([new(leaf.Id, includeMaterialInputs: true)]).ObjectMaterialInputs[leaf.Id];
            Assert.Equal(oracle.Materials, input.Materials); Assert.Equal(oracle.Bindings, input.Bindings);
            Assert.Equal(oracle.OutlineMaterials, input.OutlineMaterials); Assert.Equal(oracle.MaterialFlags, input.MaterialFlags);
            // The ordinary snapshot reader still completes its leaves. The new material seam
            // does not claim to optimize the existing complete geometry publication contract.
            var completed = actual.CaptureRenderSnapshot([new(leaf.Id, includeMaterialInputs: true)]).ObjectMaterialInputs[leaf.Id];
            Assert.Equal(input.Materials, completed.Materials); Assert.Equal(input.Bindings, completed.Bindings);
            Assert.Equal(boundaries, backend.Boundaries); Assert.Equal(impulses, backend.Impulses);

            SceneProjectSession Create(Factory factory) => new(layout, new(), motions, factory.Provider(), settings, cancellationToken: Token);
        }
        finally { AppContext.SetSwitch(PhysicalPreviewSwitch, false); }
    }

    [Fact]
    public void CurrentMaterialCaptureRejectsCancelledFaultedAndMismatchedDeferredBoundaries()
    {
        AppContext.SetSwitch(PhysicalPreviewSwitch, true);
        try
        {
            var model = MaterialDemandModel(); var layout = new SceneCompositionEditor();
            var actor = layout.Add("physical", "material-fault.pmx", ModelRole.Character, SharedPhysicsFixture.Rest(model));
            var factory = new Factory();
            using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, MaterialDemandMotion())], factory.Provider(),
                new() { Physics = new() { SharedWorld = true, GroundEnabled = false } }, cancellationToken: Token);
            var animation = PreviewAnimation(project); var controller = PreviewController(project, 0);
            var old = CaptureCurrentMaterialInputs(animation, actor.Id);
            AdvancePreview(project, 1.0 / 30);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => CaptureCurrentMaterialInputs(animation, actor.Id, cancelled.Token));
            Assert.False(project.IsFaulted);
            var pending = (Dictionary<Guid, double>)typeof(SceneAnimationSession).GetField("previewLeaves", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(animation)!;
            double accepted = pending[actor.Id]; pending[actor.Id] = accepted + .25;
            Assert.Throws<InvalidOperationException>(() => CaptureCurrentMaterialInputs(animation, actor.Id));
            pending[actor.Id] = accepted;
            var current = CaptureCurrentMaterialInputs(animation, actor.Id);
            Assert.NotEqual(old.Materials[0], current.Materials[0]); Assert.Equal(0, old.SourceFrame);
            var backend = Assert.Single(factory.Worlds); backend.BeforeStep = () => throw new InvalidOperationException("injected solver fault");
            Assert.ThrowsAny<Exception>(() => AdvancePreview(project, 1.0 / 30)); Assert.True(project.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => CaptureCurrentMaterialInputs(animation, actor.Id));
            backend.BeforeStep = null; project.Seek(0, Token);
            Assert.Equal(old.Materials, CaptureCurrentMaterialInputs(PreviewAnimation(project), actor.Id).Materials);
        }
        finally { AppContext.SetSwitch(PhysicalPreviewSwitch, false); }
    }

    private static SceneObjectMaterialInputs CaptureCurrentMaterialInputs(SceneAnimationSession owner, Guid id, CancellationToken? token = null)
    {
        try
        {
            return (SceneObjectMaterialInputs)typeof(SceneAnimationSession).GetMethod("CaptureMaterialInputs", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(owner, [id, token ?? Token])!;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static VmdMotion MaterialDemandMotion(bool hidden = false) => new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 16, Translation = new(2, 1, -.5f) }],
        MorphKeyframes = [new() { MorphName = "shade", Weight = .2f }, new() { MorphName = "shade", Frame = 16, Weight = .8f }],
        IkKeyframes = hidden ? [new() { Visible = false }, new() { Frame = 16, Visible = true }] : []
    };

    private static PmxModel MaterialDemandModel()
    {
        var source = Model();
        return new()
        {
            Header = source.Header, Name = source.Name, EnglishName = source.EnglishName, Comment = source.Comment, EnglishComment = source.EnglishComment,
            Vertices = source.Vertices, Faces = source.Faces, Textures = source.Textures, Materials = source.Materials, Bones = source.Bones,
            DisplayFrames = source.DisplayFrames, RigidBodies = source.RigidBodies, Joints = source.Joints, SoftBodies = source.SoftBodies,
            Morphs = [MaterialMorphAdapterTests.Morph("shade", MaterialMorphAdapterTests.Offset(-1, PmxMaterialMorphOperation.Add,
                new(.1f), new(.2f), .3f) with { TextureFactor = new(.2f), SphereTextureFactor = new(.3f), ToonTextureFactor = new(.4f) })]
        };
    }
}
