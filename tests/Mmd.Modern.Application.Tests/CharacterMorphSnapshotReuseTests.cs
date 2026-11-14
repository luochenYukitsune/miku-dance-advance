using System.Numerics;
using System.Reflection;
using Mmd.Modern.Animation;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class CharacterMorphSnapshotReuseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void BaseMorphPoseIsImmutableAndReusedWhileIkOutsideParentsAndPhysicalStepsRemainLive()
    {
        var model = Model(baseMorphs: true); var rest = EditedMotionProjectTests.Rest(model);
        using var controller = new CharacterAnimationController(model, Motion(), rest.Surface, rest.Outline!);
        var backend = new SceneAnimationSessionTests.FallingBackend();
        controller.EnablePhysics(backend, 5);
        var initial = Evaluation(controller, 5); var morph = Assert.IsType<MorphPose>(initial.Morphs);
        var oldVertices = morph.VertexDeltas.ToArray(); var oldBones = morph.BoneTranslations.ToArray();
        Assert.Contains(morph.VertexDeltas, delta => delta != Vector3.Zero);
        Assert.Contains(morph.BoneTranslations, delta => delta != Vector3.Zero);
        Assert.Throws<NotSupportedException>(() => ((IList<Vector3>)morph.VertexDeltas)[0] = new(100));
        Assert.Throws<NotSupportedException>(() => ((IList<Vector2>)morph.VertexUvDeltas)[0] = new(100));
        Assert.Throws<NotSupportedException>(() => ((IList<Vector3>)morph.BoneTranslations)[0] = new(100));
        Assert.Throws<NotSupportedException>(() => ((IList<Quaternion>)morph.BoneRotations)[0] = Quaternion.Identity);
        var parent = Matrix4x4.CreateTranslation(3, 0, 0);
        controller.SetOutsideParents(new Dictionary<int, Matrix4x4> { [-1] = parent });
        var attached = Evaluation(controller, 5, ik: false);
        Assert.Same(morph, attached.Morphs); Assert.False(attached.EnableIk);
        Assert.Equal(parent, attached.OutsideParents![-1]); Assert.Null(initial.OutsideParents);
        int steps = backend.Steps;
        controller.Advance(5, 1d / 120, enableIk: false, applyImpulses: false);
        Assert.Equal(steps + 1, backend.Steps); Assert.Same(morph, Evaluation(controller, 5).Morphs);
        Assert.Equal(oldVertices, morph.VertexDeltas); Assert.Equal(oldBones, morph.BoneTranslations);
    }

    [Fact]
    public void BaseMorphCacheChangesWithClockAndEnableFlagWithoutRetainingFailedPreparation()
    {
        var model = Model(baseMorphs: true); var rest = EditedMotionProjectTests.Rest(model);
        using var controller = new CharacterAnimationController(model, Motion(), rest.Surface, rest.Outline!);
        var atFive = Assert.IsType<MorphPose>(Evaluation(controller, 5).Morphs);
        var atTen = Assert.IsType<MorphPose>(Evaluation(controller, 10).Morphs);
        Assert.NotSame(atFive, atTen); Assert.NotEqual(atFive.VertexDeltas[0], atTen.VertexDeltas[0]);
        Assert.Null(Evaluation(controller, 10, morphs: false).Morphs);
        var enabledAgain = Assert.IsType<MorphPose>(Evaluation(controller, 10).Morphs);
        Assert.Equal(atTen.VertexDeltas, enabledAgain.VertexDeltas);
        Assert.Equal(atTen.BoneTranslations, enabledAgain.BoneTranslations);

        var invalidModel = Model(overflow: true, baseMorphs: true); var invalidRest = EditedMotionProjectTests.Rest(invalidModel);
        using var invalid = new CharacterAnimationController(invalidModel, Motion(2), invalidRest.Surface, invalidRest.Outline!);
        var valid = Assert.IsType<MorphPose>(Evaluation(invalid, 5).Morphs);
        var error = Assert.Throws<TargetInvocationException>(() => Evaluation(invalid, 10));
        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Same(valid, Evaluation(invalid, 5).Morphs);
    }

    [Fact]
    public void FixedClockBaseMorphPreparationDoesNotReallocateVertexSizedArrays()
    {
        var model = Model(triangles: 1000, baseMorphs: true); var rest = EditedMotionProjectTests.Rest(model);
        using var controller = new CharacterAnimationController(model, Motion(), rest.Surface, rest.Outline!);
        var morph = Evaluation(controller, 5).Morphs;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int step = 0; step < 120; step++) Assert.Same(morph, Evaluation(controller, 5, ik: step % 2 == 0).Morphs);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 512 * 1024, $"Fixed-clock morph evaluation recopied vertex arrays: {allocated} bytes.");
    }

    private static readonly MethodInfo EvaluationMethod = typeof(CharacterAnimationController)
        .GetMethod("Evaluation", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static CharacterEvaluationOptions Evaluation(CharacterAnimationController controller, double frame, bool ik = true, bool morphs = true)
        => (CharacterEvaluationOptions)EvaluationMethod.Invoke(controller, [frame, ik, morphs])!;

    [Fact]
    public void UnchangedRenderCapturesShareStableChannelsWithoutCopyingTheVertexStream()
    {
        var model = Model(triangles: 1000); var rest = EditedMotionProjectTests.Rest(model);
        var layout = new SceneCompositionEditor(); var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, rest);
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, Motion(), PhysicsEnabled: false)],
            new NoPhysics(), cancellationToken: Token);
        project.Seek(5, Token);
        var old = project.CaptureRenderSnapshot(); var channels = old.MorphChannels[actor.Id];
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 8; index++)
            Assert.Same(channels, project.CaptureRenderSnapshot().MorphChannels[actor.Id]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 128 * 1024, $"Unchanged capture copied a vertex-sized channel stream: {allocated} bytes.");
        Assert.Throws<NotSupportedException>(() => ((IList<PmxAdditionalUvVertex>)channels.AdditionalUvs)[0] = default);
        Assert.Throws<NotSupportedException>(() => ((IList<PmxMaterialTextureFactors>)channels.TextureFactors)[0] = default);
        Assert.Throws<NotSupportedException>(() => ((IList<PmxRigidBodyImpulse>)channels.Impulses)[0] = default);
        var replacement = channels with { Impulses = new[] { new PmxRigidBodyImpulse(0, false, new(100), new(100)) } };
        Assert.NotEqual(channels.Impulses[0], replacement.Impulses[0]);
        project.Seek(10, Token);
        Assert.Equal(new Vector4(0, 1, 2, 3), channels.AdditionalUvs[0].Uv1);
        Assert.Equal(new Vector4(-.25f), channels.TextureFactors[0].Diffuse.Add);
        Assert.Equal(new Vector3(-.5f, 0, 0), channels.Impulses[0].TranslationVelocity);
        Assert.Equal(new Vector4(-1, 0, 1, 2), project.CaptureRenderSnapshot().MorphChannels[actor.Id].AdditionalUvs[0].Uv1);
        Assert.Equal(5, old.SourceFrame);
    }

    [Fact]
    public void RepeatedFixedPosePreparationReusesOnlyTheMorphResultAcrossOutsideParentChanges()
    {
        var model = Model(); var rest = EditedMotionProjectTests.Rest(model);
        using var controller = new CharacterAnimationController(model, Motion(), rest.Surface, rest.Outline!);
        var initial = controller.ResetPhysics(5); var channels = controller.MorphChannels;
        var vertices = initial.Surface.Snapshot.Vertices.ToArray();
        controller.ResetPhysics(5);
        Assert.Same(channels, controller.MorphChannels);
        controller.SetOutsideParents(new Dictionary<int, Matrix4x4> { [-1] = Matrix4x4.CreateTranslation(3, 0, 0) });
        var attached = controller.Sample(5);
        Assert.Same(channels, controller.MorphChannels);
        Assert.Equal(vertices[0].Position + new Vector3(3, 0, 0), attached.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(vertices, initial.Surface.Snapshot.Vertices.ToArray());
        controller.Sample(10); Assert.NotSame(channels, controller.MorphChannels);
        Assert.Equal(new Vector4(-1, 0, 1, 2), controller.MorphChannels.AdditionalUvs[0].Uv1);
        controller.Sample(10, enableMorphs: false);
        Assert.Equal(new Vector4(1, 2, 3, 4), controller.MorphChannels.AdditionalUvs[0].Uv1);
        Assert.Equal(PmxMaterialTextureFactors.Identity, controller.MorphChannels.TextureFactors[0]);
        Assert.Empty(controller.MorphChannels.Impulses);
        controller.Sample(10);
        Assert.Equal(new Vector4(-1, 0, 1, 2), controller.MorphChannels.AdditionalUvs[0].Uv1);
        Assert.Equal(new Vector4(0, 1, 2, 3), channels.AdditionalUvs[0].Uv1);
    }

    [Fact]
    public void SameFramePhysicalStepsRemainLiveWhileTheirImmutableMorphInputsAreReused()
    {
        var model = Model(); var rest = EditedMotionProjectTests.Rest(model);
        using var controller = new CharacterAnimationController(model, Motion(), rest.Surface, rest.Outline!);
        var backend = new SceneAnimationSessionTests.FallingBackend();
        controller.EnablePhysics(backend, 5); var channels = controller.MorphChannels;
        var old = controller.Sample(5); var oldVertices = old.Surface.Snapshot.Vertices.ToArray();
        int steps = backend.Steps;
        var next = controller.Advance(5, 1d / 120, applyImpulses: false);
        Assert.Equal(steps + 1, backend.Steps); Assert.Same(channels, controller.MorphChannels);
        Assert.NotEqual(oldVertices[0].Position, next.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(oldVertices, old.Surface.Snapshot.Vertices.ToArray());
        Assert.Equal(new Vector3(-.5f, 0, 0), channels.Impulses[0].TranslationVelocity);
    }

    [Fact]
    public void CallerOwnedDefinitionArraysCannotChangeCachedOrLaterMorphSamples()
    {
        var model = Model(); var rest = EditedMotionProjectTests.Rest(model); var motion = Motion();
        using var controller = new CharacterAnimationController(model, motion, rest.Surface, rest.Outline!);
        controller.Sample(5); var channels = controller.MorphChannels;
        ((Vector4[])model.Vertices[0].AdditionalUvs)[0] = new(100);
        ((PmxMorph.UvOffset[])model.Morphs[0].UvOffsets)[0] = new(0, new(100));
        ((VmdMorphKeyframe[])motion.MorphKeyframes)[0] = new() { MorphName = "group", Weight = 100 };
        controller.ResetPhysics(5); Assert.Same(channels, controller.MorphChannels);
        controller.Sample(10);
        Assert.Equal(new Vector4(-1, 0, 1, 2), controller.MorphChannels.AdditionalUvs[0].Uv1);
        Assert.Equal(new Vector4(0, 1, 2, 3), channels.AdditionalUvs[0].Uv1);
    }

    [Fact]
    public void FailedMorphPreparationDoesNotReplaceTheLastSuccessfulCachedSnapshot()
    {
        var model = Model(overflow: true); var rest = EditedMotionProjectTests.Rest(model);
        using var controller = new CharacterAnimationController(model, Motion(2), rest.Surface, rest.Outline!);
        var old = controller.Sample(0); var channels = controller.MorphChannels;
        Assert.Throws<ArgumentException>(() => controller.Sample(10));
        Assert.Same(channels, controller.MorphChannels);
        controller.ResetPhysics(0); Assert.Same(channels, controller.MorphChannels);
        Assert.Equal(old.Surface.Snapshot.Vertices.ToArray(), controller.Sample(0).Surface.Snapshot.Vertices.ToArray());
        Assert.Throws<ArgumentException>(() => controller.Sample(10));
    }

    private static VmdMotion Motion(float finalWeight = -1) => new()
    {
        MorphKeyframes = new VmdMorphKeyframe[]
        {
            new() { MorphName = "group", Weight = 0 }, new() { MorphName = "group", Frame = 10, Weight = finalWeight }
        }
    };

    private static PmxModel Model(int triangles = 1, bool overflow = false, bool baseMorphs = false)
    {
        var source = EditedMotionProjectTests.Model();
        return new()
        {
            Header = new() { Version = 2.1f, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 1,
                VertexIndexSize = 4, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
            Name = "morph reuse", EnglishName = "", Comment = "", EnglishComment = "", Textures = [],
            Vertices = Enumerable.Range(0, triangles).SelectMany(triangle => source.Vertices.Select(vertex => new PmxVertex
            {
                Position = vertex.Position + new Vector3(triangle * 2, 0, 0), Normal = vertex.Normal, Uv = vertex.Uv,
                AdditionalUvs = new Vector4[] { new(1, 2, 3, 4) }, WeightType = vertex.WeightType,
                BoneIndices = vertex.BoneIndices, BoneWeights = vertex.BoneWeights, EdgeScale = vertex.EdgeScale
            })).ToArray(),
            Faces = Enumerable.Range(0, triangles).Select(triangle => new PmxTriangle(triangle * 3, triangle * 3 + 1, triangle * 3 + 2)).ToArray(),
            Materials = [new() { Name = "triangle", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero,
                SpecularPower = 1, Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero,
                EdgeSize = 0, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None,
                ToonMode = PmxToonMode.Separate, ToonTextureIndex = -1, Memo = "", FaceVertexCount = triangles * 3 }],
            Bones = source.Bones, DisplayFrames = [], Joints = [], SoftBodies = [],
            RigidBodies = [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
                Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0,
                Operation = PmxRigidBodyOperation.Dynamic }],
            Morphs = [new() { Name = "uv", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.AdditionalUv1,
                    UvOffsets = new PmxMorph.UvOffset[] { new(0, new(overflow ? float.MaxValue : 2)) } },
                new() { Name = "texture", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Material,
                    MaterialOffsets = [new(-1, PmxMaterialMorphOperation.Add, Vector4.Zero, Vector3.Zero, 0, Vector3.Zero,
                        Vector4.Zero, 0, new(.5f), Vector4.Zero, Vector4.Zero)] },
                new() { Name = "impulse", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Impulse,
                    ImpulseOffsets = [new(0, false, Vector3.UnitX, Vector3.UnitY)] },
                new() { Name = "group", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Group,
                    GroupOffsets = baseMorphs ? [new(0, 1), new(1, 1), new(2, 1), new(4, 1), new(5, 1)] : [new(0, 1), new(1, 1), new(2, 1)] },
                new() { Name = "position", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Position,
                    PositionOffsets = [new(0, new(.25f, .5f, 0))] },
                new() { Name = "bone", EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Bone,
                    BoneOffsets = [new(0, new(.125f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .2f))] }]
        };
    }

    private sealed class NoPhysics : IProjectPhysicsBackendProvider
    {
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item) => throw new NotSupportedException();
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new NotSupportedException();
    }
}
