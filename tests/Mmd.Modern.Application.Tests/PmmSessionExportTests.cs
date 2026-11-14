using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmSessionExportTests
{
    [Fact]
    public void NewProjectExportsEveryActorNamedMotionAndCurrentRawPose()
    {
        var model = Model(); var scene = Scene(model, 2);
        using var session = Session(scene, model); session.Seek(5, TestContext.Current.CancellationToken);
        var result = PmmSessionExporter.Export(session);
        var read = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(result)), new() { AnsiCodePage = 932 });
        Assert.Equal(2, read.Models.Count); Assert.Equal(5, read.CurrentFrame);
        Assert.Equal("actor-0.pmx", read.Models[0].Path.Value);
        Assert.Equal(5f, read.Models[0].BoneStates[0].Translation.X, 4);
        Assert.Equal(.5f, read.Models[0].MorphStates[0].Weight, 4);
        Assert.Equal(new uint[] { 0, 10 }, PmmMotionAdapter.Convert(read.Models[0], model).BoneKeyframes.Select(k => k.Frame));
        Assert.Equal(1920, read.OutputWidth); Assert.Equal(1080, read.OutputHeight);
    }

    [Fact]
    public void TemplateMappingUsesExplicitStoredIdsAndPreservesUneditedProjectState()
    {
        var model = Model(); var scene = Scene(model, 2); using var session = Session(scene, model); session.Seek(5, TestContext.Current.CancellationToken);
        var baseDocument = PmmSessionExporter.Export(session);
        byte[] raw = PmmWriter.ToBytes(baseDocument); var layout = PmmReader.Read(new MemoryStream(raw));
        raw[(int)layout.Models[0].Offset] = 7; raw[(int)layout.Models[1].Offset] = 2;
        var template = PmmReader.Read(new MemoryStream(raw), new() { AnsiCodePage = 932 });
        // Reorder actors in the session; their source model IDs must stay explicit.
        using var reversed = new SceneAnimationSession(scene.Objects.Reverse(), scene.Objects.Select((o, i) => new SceneActorMotion(o.Id, model, Motion(i * 100))), _ => throw new Exception(), cancellationToken: TestContext.Current.CancellationToken);
        var result = PmmSessionExporter.Export(reversed, new() { Template = template,
            TemplateModelIndices = scene.Objects.Select((o, i) => (o.Id, Index: (byte)(i == 0 ? 7 : 2))).ToDictionary(p => p.Id, p => p.Index) });
        Assert.Same(template.Settings, result.Settings); Assert.Same(template.Gravity, result.Gravity);
        Assert.Same(template.Camera, result.Camera); Assert.Equal(template.UnknownTail, result.UnknownTail);
        Assert.Equal("actor-0.pmx", result.Models.Single(m => m.Index == 7).Path.Value);
        Assert.Equal(100f, result.Models.Single(m => m.Index == 2).InitialBoneKeyframes[0].Translation.X);
        Assert.Equal(1, result.Models.Single(m => m.Index == 7).DrawOrderIndex);
        Assert.Equal(0, result.Models.Single(m => m.Index == 2).DrawOrderIndex);
    }

    [Fact]
    public void TemplateNeverGuessesModelMappingFromListOrder()
    {
        var model = Model(); var scene = Scene(model, 1); using var session = Session(scene, model);
        var template = PmmSessionExporter.Export(session);
        Assert.Throws<InvalidDataException>(() => PmmSessionExporter.Export(session, new() { Template = template }));
        Assert.Throws<InvalidDataException>(() => PmmSessionExporter.Export(session, new() { Template = template,
            TemplateModelIndices = new Dictionary<Guid, byte> { [scene.Objects[0].Id] = 99 } }));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, .5)]
    public void NonRepresentablePlacementIsRejected(float scale, float angle)
    {
        var model = Model(); var scene = Scene(model, 1); scene.SetTransform(scene.Objects[0].Id, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle), scale);
        using var session = Session(scene, model);
        Assert.Throws<NotSupportedException>(() => PmmSessionExporter.Export(session));
    }

    [Fact]
    public void StaticRawOutsideParentExportsActualStoredTargetAndSlot()
    {
        var model = Model(); var scene = Scene(model, 2); using var session = Session(scene, model);
        session.SetOutsideParent(scene.Objects[0].Id, 0, scene.Objects[1].Id, 0, keepWorld: false, token: TestContext.Current.CancellationToken);
        var result = PmmSessionExporter.Export(session);
        Assert.Equal(new[] { 0 }, result.Models[0].OutsideParentSubjectBoneIndices);
        Assert.Equal(1, Assert.Single(result.Models[0].InitialModelKeyframe.OutsideParents).ModelIndex);
        Assert.Equal(0, Assert.Single(result.Models[0].OutsideParentStates).Parent.BoneIndex);
    }

    [Fact]
    public void OutsideParentOffsetCannotSilentlyDisappear()
    {
        var model = Model(); var scene = Scene(model, 2); using var session = Session(scene, model);
        session.SetOutsideParent(scene.Objects[0].Id, 0, scene.Objects[1].Id, 0, keepWorld: false, offset: Matrix4x4.CreateTranslation(1, 2, 3), token: TestContext.Current.CancellationToken);
        Assert.Throws<NotSupportedException>(() => PmmSessionExporter.Export(session));
    }

    [Fact]
    public void StaticActorsNeedAnExplicitModelRatherThanGuessingSkeletonFromVertices()
    {
        var model = Model(); var scene = Scene(model, 1); using var session = new SceneAnimationSession(scene.Objects, [], _ => throw new Exception(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => PmmSessionExporter.Export(session));
        var result = PmmSessionExporter.Export(session, new() { StaticModels = new Dictionary<Guid, PmxModel> { [scene.Objects[0].Id] = model } });
        Assert.Single(result.Models); Assert.Equal(Vector3.Zero, result.Models[0].InitialBoneKeyframes[0].Translation);
    }

    [Fact]
    public void FractionalEditorFrameCannotBeTruncatedByExport()
    {
        var model = Model(); var scene = Scene(model, 1); using var session = Session(scene, model); session.Advance(1d / 60, TestContext.Current.CancellationToken);
        Assert.Throws<NotSupportedException>(() => PmmSessionExporter.Export(session));
    }

    [Fact]
    public void NativeLightingCannotSilentlyDisappear()
    {
        var model = Model(); var scene = Scene(model, 1);
        using var session = new SceneAnimationSession(scene.Objects, [new(scene.Objects[0].Id, model, Motion())], _ => throw new Exception(),
            new([new(Guid.NewGuid(), "point", SceneLightKind.Point, Vector3.One, Quaternion.Identity, Vector3.One)]), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Throws<NotSupportedException>(() => PmmSessionExporter.Export(session));
    }

    [Fact]
    public void TooLongOrUnencodableNewPathsFailWithoutTruncation()
    {
        var model = Model(); var scene = Scene(model, 1);
        foreach (string path in new[] { new string('a', 257) + ".pmx", "actor-\U0001f642.pmx" })
        {
            var item = scene.Objects[0] with { SourcePath = path };
            using var session = new SceneAnimationSession([item], [new(item.Id, model, Motion())], _ => throw new Exception(), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Throws<PmmFormatException>(() => PmmSessionExporter.Export(session));
        }
    }

    [Fact]
    public void SaveValidatesBeforePublishingAndCancellationRetainsOldDestination()
    {
        var model = Model(); var scene = Scene(model, 1); using var session = Session(scene, model);
        string parent = Path.Combine(Path.GetTempPath(), "pmm-session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(parent);
        string path = Path.Combine(parent, "project.pmm"); File.WriteAllBytes(path, [1, 2, 3]);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PmmSessionExporter.Save(path, session, cancellationToken: cancel.Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        PmmSessionExporter.Save(path, session, cancellationToken: TestContext.Current.CancellationToken); using var input = File.OpenRead(path); Assert.Single(PmmReader.Read(input).Models);
    }

    [Fact]
    public void VisibilityBeforeFirstSwitchMatchesTheExistingClampedRuntime()
    {
        var model = Model(); var scene = Scene(model, 1); var actor = scene.Objects[0];
        using var session = new SceneAnimationSession(scene.Objects, [new(actor.Id, model,
            new() { IkKeyframes = [new() { Frame = 10, Visible = true }, new() { Frame = 10, Visible = false }] })],
            _ => throw new Exception(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(session.Actors[0].Visible);
        var result = PmmSessionExporter.Export(session);
        Assert.False(result.Models[0].InitialModelKeyframe.Visible); Assert.False(result.Models[0].Visible);
    }

    [Fact]
    public void ConstantRootTranslationRetainsAnimationAndOffsetsEveryVertexExactlyOnce()
    {
        var model = Model(); var scene = Scene(model, 1); scene.SetTransform(scene.Objects[0].Id, new(3, -2, 7), Quaternion.Identity, 1);
        using var session = Session(scene, model); session.Seek(5, TestContext.Current.CancellationToken);
        var result = PmmSessionExporter.Export(session);
        var restored = PmmMotionAdapter.Convert(result.Models[0], model); var replayScene = Scene(model, 1);
        using var replay = new SceneAnimationSession(replayScene.Objects, [new(replayScene.Objects[0].Id, model, restored)],
            _ => throw new Exception(), cancellationToken: TestContext.Current.CancellationToken);
        replay.Seek(5, TestContext.Current.CancellationToken);
        Assert.Equal(session.Frame.Surface.Snapshot.Vertices, replay.Frame.Surface.Snapshot.Vertices);
        Assert.Equal(new Vector3(3, -2, 7), result.Models[0].InitialBoneKeyframes[0].Translation);
    }

    [Fact]
    public void DisabledMorphsAndPhysicsAreRepresentedInsteadOfSilentlyReenabled()
    {
        var model = Model(); var scene = Scene(model, 1); using var session = Session(scene, model);
        session.SetActorOptions(scene.Objects[0].Id, true, false, false, TestContext.Current.CancellationToken);
        session.Seek(5, TestContext.Current.CancellationToken);
        var result = PmmSessionExporter.Export(session);
        Assert.Equal(0, result.Models[0].MorphStates[0].Weight);
        Assert.Equal(new[] { 10 }, result.Models[0].MorphKeyframes.Select(k => k.Frame));
        Assert.True(result.Models[0].InitialBoneKeyframes[0].PhysicsDisabled);
        Assert.True(result.Models[0].BoneStates[0].PhysicsDisabled);
    }

    [Fact]
    public void ClearingAnActorMotionCannotRestoreStaleAnimationFromTheTemplate()
    {
        var model = Model(); var scene = Scene(model, 1); using var session = Session(scene, model);
        var template = PmmSessionExporter.Export(session); session.ClearMotion(scene.Objects[0].Id);
        var result = PmmSessionExporter.Export(session, new() { Template = template,
            TemplateModelIndices = new Dictionary<Guid, byte> { [scene.Objects[0].Id] = 0 }, StaticModels = new Dictionary<Guid, PmxModel> { [scene.Objects[0].Id] = model } });
        Assert.Empty(result.Models[0].BoneKeyframes); Assert.Empty(result.Models[0].MorphKeyframes);
        Assert.Equal(Vector3.Zero, result.Models[0].BoneStates[0].Translation);
        Assert.Equal(0, result.Models[0].MorphStates[0].Weight);
    }

    [Fact]
    public void SceneMotionUsesTheSharedClockAndExistingSceneExporter()
    {
        var model = Model(); var scene = Scene(model, 1); using var session = Session(scene, model);
        var sceneMotion = SceneMotionAsset.FromMotion(new() { LightKeyframes = [new() { Color = Vector3.Zero, Direction = -Vector3.UnitY },
            new() { Frame = 10, Color = Vector3.One, Direction = -Vector3.UnitY }] });
        session.BindSceneMotion(sceneMotion, TestContext.Current.CancellationToken); session.Seek(5, TestContext.Current.CancellationToken);
        var result = PmmSessionExporter.Export(session);
        Assert.Equal(new Vector3(.5f), result.Light.Color); Assert.Single(result.Light.Keyframes);
    }

    [Fact]
    public void TemplateOutsideParentsResolveBothSubjectAndTargetThroughPreservedNameOrder()
    {
        var originalModel = Model(["A", "B"]); var scene = Scene(originalModel, 2);
        using var original = new SceneAnimationSession(scene.Objects, scene.Objects.Select(o => new SceneActorMotion(o.Id, originalModel,
            new() { BoneKeyframes = [new() { BoneName = "A" }] })),
            _ => throw new Exception(), cancellationToken: TestContext.Current.CancellationToken);
        var template = PmmSessionExporter.Export(original);
        var reordered = Model(["B", "A"]);
        using var edited = new SceneAnimationSession(scene.Objects, scene.Objects.Select(o => new SceneActorMotion(o.Id, reordered,
            new() { BoneKeyframes = [new() { BoneName = "B" }] })),
            _ => throw new Exception(), cancellationToken: TestContext.Current.CancellationToken);
        edited.SetOutsideParent(scene.Objects[0].Id, 0, scene.Objects[1].Id, 0, keepWorld: false, token: TestContext.Current.CancellationToken);
        var result = PmmSessionExporter.Export(edited, new() { Template = template,
            TemplateModelIndices = new Dictionary<Guid, byte> { [scene.Objects[0].Id] = 0, [scene.Objects[1].Id] = 1 } });
        Assert.Equal(new[] { 1 }, result.Models[0].OutsideParentSubjectBoneIndices);
        Assert.Equal(1, result.Models[0].InitialModelKeyframe.OutsideParents[0].BoneIndex);
        Assert.Equal(new[] { "A", "B" }, result.Models[1].BoneNames.Select(n => n.Value));
    }

    [Fact]
    public void GloballyDisabledTemplateCannotSilentlyDisableAnActivePhysicalActor()
    {
        var model = Model(physics: true); var scene = Scene(model, 1);
        using var session = new SceneAnimationSession(scene.Objects, [new(scene.Objects[0].Id, model, Motion())],
            _ => new SceneAnimationSessionTests.FallingBackend(), cancellationToken: TestContext.Current.CancellationToken);
        var initial = PmmSessionExporter.Export(session);
        var template = new PmmDocument { Models = initial.Models, AnsiCodePage = 932, PanelStates = new byte[7], Camera = initial.Camera,
            Light = initial.Light, Settings = new() { PhysicsSimulationMode = 0, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0 } };
        Assert.Throws<NotSupportedException>(() => PmmSessionExporter.Export(session, new() { Template = template,
            TemplateModelIndices = new Dictionary<Guid, byte> { [scene.Objects[0].Id] = 0 } }));
    }

    [Theory]
    [InlineData("actor.pmx")]
    [InlineData("motion.vmd")]
    public void SaveCannotOverwriteModelOrMotionAssets(string name)
    {
        var model = Model(); var scene = Scene(model, 1); using var session = Session(scene, model);
        string parent = Path.Combine(Path.GetTempPath(), "pmm-protected-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(parent);
        string path = Path.Combine(parent, name); File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Throws<ArgumentException>(() => PmmSessionExporter.Save(path, session, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void UneditedTemplateNamesAndPathPaddingKeepTheirOriginalMixedEncodingBytes()
    {
        var model = Model(); var scene = Scene(model, 1); using var original = Session(scene, model);
        byte[] before = PmmWriter.ToBytes(PmmSessionExporter.Export(original));
        int nameOffset = (int)PmmReader.Read(new MemoryStream(before)).Models[0].Offset + 1;
        // A CP932 half-width kana byte cannot be newly encoded from its replacement-decoded CP936 display value.
        int tail = nameOffset + 1 + before[nameOffset];
        byte[] mixed = before[..nameOffset].Concat(new byte[] { 1, 0xb1 }).Concat(before[tail..]).ToArray();
        int pathOffset = nameOffset + 2; pathOffset += 1 + mixed[pathOffset]; mixed[pathOffset + 255] = 0xa5;
        var template = PmmReader.Read(new MemoryStream(mixed), new() { AnsiCodePage = 936 });
        var item = scene.Objects[0] with { Name = template.Models[0].Name.Value };
        using var edited = new SceneAnimationSession([item], [new(item.Id, model, Motion())], _ => throw new Exception(), cancellationToken: TestContext.Current.CancellationToken);
        var result = PmmSessionExporter.Export(edited, new() { Template = template, TemplateModelIndices = new Dictionary<Guid, byte> { [item.Id] = 0 } });
        Assert.Equal(new byte[] { 0xb1 }, result.Models[0].Name.RawBytes);
        Assert.Equal(template.Models[0].Path.RawBytes, result.Models[0].Path.RawBytes);
        Assert.Equal(0xa5, result.Models[0].Path.RawBytes[255]);
        _ = PmmWriter.ToBytes(result);
    }

    [Fact]
    public void ResolvedAbsoluteTemplatePathKeepsRawBytesWhenItsAlternateDecodingMatches()
    {
        var model = Model(); var scene = Scene(model, 1); var item = scene.Objects[0] with { SourcePath = "D:/assets/\uff71/model.pmx" };
        using var session = new SceneAnimationSession([item], [new(item.Id, model, Motion())], _ => throw new Exception(), cancellationToken: TestContext.Current.CancellationToken);
        var template = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(PmmSessionExporter.Export(session))), new() { AnsiCodePage = 936 });
        Assert.NotEqual(item.SourcePath, template.Models[0].Path.Value);
        var result = PmmSessionExporter.Export(session, new() { Template = template, TemplateModelIndices = new Dictionary<Guid, byte> { [item.Id] = 0 } });
        Assert.Equal(template.Models[0].Path.RawBytes, result.Models[0].Path.RawBytes);
        _ = PmmWriter.ToBytes(result);
    }

    static SceneCompositionEditor Scene(PmxModel model, int count)
    {
        var scene = new SceneCompositionEditor(); var source = new OriginalFrameScene(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
            PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.Zero, Matrix4x4.Identity, new(0, 0, 10), 30, 1));
        for (int i = 0; i < count; i++) scene.Add("actor-" + i, $"actor-{i}.pmx", ModelRole.Character, source);
        return scene;
    }
    static SceneAnimationSession Session(SceneCompositionEditor scene, PmxModel model)
        => new(scene.Objects, scene.Objects.Select((o, i) => new SceneActorMotion(o.Id, model, Motion(i * 100))), _ => throw new Exception(), cancellationToken: TestContext.Current.CancellationToken);
    static VmdMotion Motion(float x = 0) => new() { BoneKeyframes = [new() { BoneName = "root", Translation = new(x, 0, 0) }, new() { BoneName = "root", Frame = 10, Translation = new(x + 10, 0, 0) }],
        MorphKeyframes = [new() { MorphName = "smile" }, new() { MorphName = "smile", Frame = 10, Weight = 1 }] };
    static PmxModel Model(string[]? names = null, bool physics = false) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0, VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "model", EnglishName = "model", Comment = "", EnglishComment = "",
        Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
        Faces = [new(0, 1, 2)], Textures = [], Materials = [new() { Name = "m", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1, Ambient = new(.3f), DrawMode = PmxDrawModeFlags.DrawEdge, EdgeColor = Vector4.UnitW, EdgeSize = 1, TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Common, ToonTextureIndex = 0, Memo = "", FaceVertexCount = 3 }],
        Bones = (names ?? ["root"]).Select(name => new PmxBone { Name = name, EnglishName = "", ParentIndex = -1, Position = Vector3.Zero, Flags = PmxBoneFlags.AllowTranslate, DeformDepth = 0 }).ToArray(),
        Morphs = [new() { Name = "smile", EnglishName = "", Type = PmxMorphType.Position, ControlPanel = 0 }], DisplayFrames = [],
        RigidBodies = physics ? [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0, Shape = PmxRigidBodyShape.Sphere,
            ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero, Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0,
            Friction = 0, Operation = PmxRigidBodyOperation.Dynamic }] : [], Joints = [], SoftBodies = []
    };
}
