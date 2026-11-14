using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed partial class ScenePmmCameraParentTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mda-camera-parent-" + Guid.NewGuid().ToString("N"));
    public ScenePmmCameraParentTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void StoredIdsAndNativeNamesResolveParentSwitchesAndSeparateSavedTarget()
    {
        var f = Fixture();
        var first = f.Controls.CameraParentAt(0)!;
        Assert.Equal(f.Layout.Objects[0].Id, first.ObjectId); Assert.Equal(1, first.BoneIndex);
        Assert.Equal(7, first.PmmModelIndex); Assert.Equal(0, first.PmmBoneIndex);
        Assert.Equal(first, f.Controls.CameraParentAt(4.999));
        var second = f.Controls.CameraParentAt(5)!;
        Assert.Equal(f.Layout.Objects[1].Id, second.ObjectId); Assert.Equal(0, second.BoneIndex);
        Assert.Null(f.Controls.CameraParentAt(10)); Assert.Null(f.Controls.CameraParentAt(14.999));
        Assert.Equal(first, f.Controls.CameraParentAt(15));
        var saved = f.Controls.CameraParentAt(0, saved: true)!;
        Assert.Equal(second.ObjectId, saved.ObjectId); Assert.Equal(1, saved.BoneIndex);
        var restored = ScenePmmControlState.Restore(f.Controls.Capture(), f.Motions.ToDictionary(m => m.ObjectId, m => m.Model));
        foreach (double frame in new[] { 0d, 4.999, 5, 10, 14.999, 15, 20 })
            Assert.Equal(f.Controls.CameraParentAt(frame), restored.CameraParentAt(frame));
        Assert.Equal(saved, restored.CameraParentAt(0, saved: true));
    }

    [Fact]
    public void CurrentWorldCameraUsesSolvedBoneAndPlacementWithoutBakingLocalMotion()
    {
        var f = Fixture();
        using var session = new SceneAnimationSession(f.Layout.Objects, f.Motions, NoPhysics,
            pmmControls: f.Controls, sceneMotion: f.Scene);
        foreach (int frame in new[] { 0, 4, 5, 9, 10, 14, 15, 20 })
        {
            session.Seek(frame, Token);
            var local = new VmdSceneTracks(f.Scene.Motion).CameraAt(frame)!.Value;
            var target = f.Controls.CameraParentAt(frame);
            Matrix4x4? world = target is null ? null : session.GetBoneWorldMatrices(target.ObjectId)[target.BoneIndex];
            Assert.Equal(world, session.CameraParentTransform);
            Assert.Equal(local.LookAt, session.CameraPose!.Value.LookAt);
            Assert.Equal(local.Angle, session.CameraPose.Value.Angle);
            var authored = local with { ParentModelIndex = session.CameraPose.Value.ParentModelIndex,
                ParentBoneIndex = session.CameraPose.Value.ParentBoneIndex,
                IgnoreParentRotation = session.CameraPose.Value.IgnoreParentRotation };
            Assert.Equal(authored.CreateCamera(world), session.Camera);
        }
        session.Seek(0, Token);
        var before = session.Frame; var states = session.Actors;
        var saved = f.Controls.CameraAt(0, saved: true); var savedTarget = f.Controls.CameraParentAt(0, saved: true)!;
        Assert.Equal(session.GetBoneWorldMatrices(savedTarget.ObjectId)[savedTarget.BoneIndex], session.ResolveCameraParentTransform(saved));
        Assert.Same(before, session.Frame); Assert.Same(states, session.Actors); Assert.Equal(0, session.CurrentFrame);
        Assert.Throws<InvalidDataException>(() => session.ResolveCameraParentTransform(saved with { ParentModelIndex = 99 }));
        Assert.Throws<InvalidDataException>(() => session.ResolveCameraParentTransform(saved with { ParentBoneIndex = 99 }));
    }

    [Fact]
    public async Task OfflineFramesCaptureTheSolvedParentAtTheirOwnSharedClock()
    {
        var f = Fixture();
        using var session = new SceneAnimationSession(f.Layout.Objects, f.Motions, NoPhysics,
            pmmControls: f.Controls, sceneMotion: f.Scene);
        var captures = new List<OfflineFrame>();
        var result = await OfflineFrameRunner.RunAsync(session, new(320, 240, new(30), 4, 15), (frame, _) =>
        {
            Assert.Equal(session.CurrentFrame, frame.Sample.SampledSourceFrame.ToDouble());
            Assert.Equal(session.CameraParentTransform, frame.CameraParentTransform);
            Assert.Equal(session.Camera, frame.ResolvedCamera);
            captures.Add(frame); return ValueTask.CompletedTask;
        }, cancellationToken: Token);
        Assert.Equal(12, result.CompletedFrames);
        Assert.NotNull(captures[0].CameraParentTransform); Assert.NotNull(captures[1].CameraParentTransform);
        Assert.Null(captures[6].CameraParentTransform); Assert.NotNull(captures[^1].CameraParentTransform);
        Assert.Equal(7, captures[0].Camera!.Value.ParentModelIndex);
        Assert.Equal(42, captures[1].Camera!.Value.ParentModelIndex);
        Assert.Equal(-1, captures[6].Camera!.Value.ParentModelIndex);
        Assert.Equal(7, captures[^1].Camera!.Value.ParentModelIndex);
    }

    [Fact]
    public void PmaMoveReopenUndoAndPmmExportRetainParentTimelineAndLocalValues()
    {
        var f = Fixture(); var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("physics"), _ => throw new InvalidOperationException("physics"));
        foreach (var item in f.Layout.Objects.ToArray()) f.Layout.SetTransform(item.Id, Vector3.Zero, Quaternion.Identity, 1);
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, sceneMotion: f.Scene, pmmControls: f.Controls);
        project.Seek(5, Token); var before = project.CaptureRenderSnapshot();
        Assert.Equal(project.Camera, before.ResolvedCamera); Assert.Equal(project.CameraParentTransform, before.CameraParentTransform);
        var detached = ScenePmmControlState.Create(Document(detach: true), f.Sources);
        project.Edit(e => e.PmmControls = detached, Token); Assert.Null(project.CameraParent);
        Assert.True(project.Undo(Token)); Assert.Equal(before.ResolvedCamera, project.Camera);
        Assert.True(project.Redo(Token)); Assert.Null(project.CameraParent);
        Assert.True(project.Undo(Token));
        string package = Path.Combine(directory, "camera.pma"); PmaProjectStore.Save(package, project, cancellationToken: Token);
        foreach (var item in f.Layout.Objects) File.Delete(item.SourcePath);
        string moved = Path.Combine(directory, "moved.pma"); File.Move(package, moved);
        using var loaded = PmaProjectStore.Load(moved, Path.Combine(directory, "cache"), (path, _) =>
        { using var input = File.OpenRead(path); var model = PmxReader.Read(input); return new(model, Rest(model)); }, provider, cancellationToken: Token);
        Assert.Equal(project.PmmControls!.Sha256, loaded.Session.PmmControls!.Sha256);
        Assert.Equal(project.CameraParent, loaded.Session.CameraParent); Assert.Equal(project.Camera, loaded.Session.Camera);
        Assert.Contains(PmaPmmCompatibility.Inspect(loaded.Session).Issues, issue => issue.Code == "temporary-model-source");
        // PMM references must outlive the PMA extraction. A normal portable project save publishes durable model copies.
        string permanent = Path.Combine(directory, "permanent"); Directory.CreateDirectory(permanent);
        string normal = Path.Combine(permanent, "project.mmdscene"); loaded.Session.Save(normal, cancellationToken: Token);
        using var durable = SceneProjectSession.Load(normal, (path, _) =>
        { using var input = File.OpenRead(path); var model = PmxReader.Read(input); return new(model, Rest(model)); }, provider, Token);
        var output = durable.ExportPmm(); var actual = new PmmSceneTracks(output);
        foreach (int frame in new[] { 0, 4, 5, 9, 10, 14, 15, 20 })
        {
            var expected = f.Controls.CameraAt(frame); var sampled = actual.CameraAt(frame);
            Assert.Equal(expected.ParentModelIndex, sampled.ParentModelIndex); Assert.Equal(expected.ParentBoneIndex, sampled.ParentBoneIndex);
            Assert.InRange(Vector3.Distance(expected.LookAt, sampled.LookAt), 0, 1e-5f); Assert.Equal(expected.Angle, sampled.Angle);
            loaded.Session.Seek(frame, Token); project.Seek(frame, Token);
            Assert.Equal(project.Camera, loaded.Session.Camera);
        }
        Assert.Equal(42, output.Settings.CameraLookAtModelIndex); Assert.Equal(1, output.Settings.CameraLookAtBoneIndex);
    }

    [Fact]
    public void FutureAndSavedInvalidTargetsRejectBeforeAnyModelLoader()
    {
        var f = Fixture(); byte[] bytes = f.Controls.Capture().DocumentBytes;
        var document = f.Controls.ExportDocument();
        // Camera extra record: object/frame/prev/next, distance/lookAt/angle, then model/bone.
        int offset = checked((int)document.Camera.Keyframes[0].Offset + 44);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), 99);
        var payload = f.Controls.Capture() with { DocumentBytes = bytes, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.ValidatePayload(payload));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(Document(savedModel: 99), f.Sources));
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(Document(savedBone: 99), f.Sources));
    }

    [Fact]
    public void EditedLocalCameraPreservesParentsAndCannotEraseTheirSwitchBoundaries()
    {
        var f = Fixture();
        var motion = f.Scene.Motion;
        var edited = PmmSceneMotionExporter.ReplaceSceneMotion(f.Controls.ExportDocument(), motion, 5,
            preserveCameraParents: true);
        var tracks = new PmmSceneTracks(edited);
        Assert.Equal(7, tracks.CameraAt(4.999).ParentModelIndex); Assert.Equal(42, tracks.CameraAt(5).ParentModelIndex);
        Assert.Equal(-1, tracks.CameraAt(10).ParentModelIndex); Assert.Equal(7, tracks.CameraAt(15).ParentModelIndex);
        Assert.Equal(42, edited.Settings.CameraLookAtModelIndex); Assert.Equal(1, edited.Settings.CameraLookAtBoneIndex);
        var removed = new VmdMotion { CameraKeyframes = motion.CameraKeyframes.Where(k => k.Frame != 5).ToArray() };
        Assert.Throws<NotSupportedException>(() => PmmSceneMotionExporter.ReplaceSceneMotion(f.Controls.ExportDocument(), removed,
            preserveCameraParents: true));
    }

    [Fact]
    public void RegisterSavedDraftKeepsItsOwnParentAndRestoresThroughOwnerUndo()
    {
        var f = Fixture(); var pose = f.Controls.CameraAt(0, saved: true);
        var updated = f.Controls.WithCameraParent(3, pose);
        Assert.Equal(f.Controls.OriginSha256, updated.OriginSha256);
        Assert.Equal(f.Controls.CameraParentAt(0, saved: true), updated.CameraParentAt(3));
        Assert.Equal(pose, updated.CameraAt(3)); Assert.Equal(pose, updated.CameraAt(0, saved: true));
        Assert.NotEqual(f.Controls.CameraParentAt(3), updated.CameraParentAt(3));
        Assert.Equal(f.Controls.CameraParentAt(5), updated.CameraParentAt(5));
        Assert.Throws<InvalidDataException>(() => f.Controls.WithCameraParent(3, pose with { ParentBoneIndex = 99 }));
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("physics"), _ => throw new InvalidOperationException("physics"));
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, pmmControls: f.Controls);
        project.Seek(3, Token); var before = project.Camera;
        project.Edit(e => e.PmmControls = e.PmmControls!.WithCameraParent(3, pose), Token);
        Assert.Equal(updated.CameraParentAt(3), project.CameraParent);
        Assert.True(project.Undo(Token)); Assert.Equal(before, project.Camera);
        Assert.True(project.Redo(Token)); Assert.Equal(updated.CameraParentAt(3), project.CameraParent);
    }

    [Fact]
    public void MissingPassiveAnimationNamesRemainMetadataWhileActiveControlsReject()
    {
        var f = Fixture(); var replacement = f.Sources.Select(s => s with { Model = Model(["target"]) }).ToArray();
        var state = ScenePmmControlState.Create(Document(savedBone: 1), replacement);
        Assert.All(state.Capture().Actors, actor => Assert.Contains(-1, actor.BoneIndices));
        var restored = ScenePmmControlState.Restore(state.Capture(), replacement.ToDictionary(s => s.ObjectId, s => s.Model));
        Assert.Equal(state.Sha256, restored.Sha256); Assert.Equal(0, restored.CameraParentAt(0)!.BoneIndex);
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(Document(), replacement)); // saved camera uses missing unused
        var ik = Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(Document(savedBone: 1, missingIk: true), replacement));
        Assert.Contains("IK control", ik.Message);
        var physics = Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(Document(savedBone: 1, missingPhysics: true), replacement));
        Assert.Contains("physics control", physics.Message);
        var active = f.Sources.Select(s => s.PmmModelIndex == 7 ? s with { Model = Model(["unused"]) } : s).ToArray();
        Assert.Throws<InvalidDataException>(() => ScenePmmControlState.Create(Document(), active));
    }

    [Fact]
    public void CorrectedModelDisplayPreservesOriginalNameBytesWhileExplicitRenameWritesNewName()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string corrected = "センター";
        byte[] raw = Encoding.GetEncoding(932).GetBytes(corrected);
        var alias = new PmmString(0, Encoding.GetEncoding(936).GetString(raw), raw);
        var f = Fixture(corrected, alias);
        foreach (var item in f.Layout.Objects.ToArray()) f.Layout.SetTransform(item.Id, Vector3.Zero, Quaternion.Identity, 1);
        var provider = new ProjectSettingsBackendProvider((_, _) => throw new InvalidOperationException("physics"), _ => throw new InvalidOperationException("physics"));
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, pmmControls: f.Controls);
        var source = f.Controls.ExportDocument().Models.Single(m => m.Index == 7).Name;
        Assert.NotEqual(corrected, source.Value);
        var output = project.ExportPmm();
        Assert.Equal(raw, output.Models.Single(m => m.Index == 7).Name.RawBytes);
        var renamed = Fixture("renamed", alias);
        foreach (var item in renamed.Layout.Objects.ToArray()) renamed.Layout.SetTransform(item.Id, Vector3.Zero, Quaternion.Identity, 1);
        using var second = new SceneProjectSession(renamed.Layout, new(), renamed.Motions, provider, pmmControls: renamed.Controls);
        Assert.Equal("renamed", second.ExportPmm().Models.Single(m => m.Index == 7).Name.Value);
    }

    private (SceneCompositionEditor Layout, SceneActorMotion[] Motions, ScenePmmActorSource[] Sources,
        ScenePmmControlState Controls, SceneMotionAsset Scene) Fixture(string? displayName = null, PmmString? alias = null)
    {
        var models = new[] { Model(["unused", "target"]), Model(["target", "unused"]) };
        var layout = new SceneCompositionEditor(); var raw = Document(alias: alias);
        // Exercise the same physical-record boundary as a real PMM import; the parser supplies RecordIndex.
        var doc = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(raw)), new() { AnsiCodePage = raw.AnsiCodePage });
        var motions = models.Select((model, i) =>
        {
            string path = Path.Combine(directory, $"model{i}.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
            var item = layout.Add(i == 0 ? displayName ?? $"actor{i}" : $"actor{i}", path, ModelRole.Character, Rest(model));
            layout.SetTransform(item.Id, new(i * 8, 2, -3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .4f + i), 1.5f);
            var source = doc.Models.Single(m => m.Index == (i == 0 ? 7 : 42));
            var asset = SceneMotionAsset.FromMotion(PmmMotionAdapter.Convert(source, model));
            return new SceneActorMotion(item.Id, model, asset.Motion, PhysicsEnabled: false, Asset: asset);
        }).ToArray();
        var sources = motions.Select((m, i) => new ScenePmmActorSource(m.ObjectId, i == 0 ? (byte)7 : (byte)42, m.Model)).Reverse().ToArray();
        var controls = ScenePmmControlState.Create(doc, sources);
        var scene = SceneMotionAsset.FromMotion(new() { CameraKeyframes = new[] { doc.Camera.InitialKeyframe }.Concat(doc.Camera.Keyframes).Select(k =>
        {
            var raw = k.Interpolation.ToArray(); for (int i = 0; i < 24; i += 4) (raw[i + 1], raw[i + 2]) = (raw[i + 2], raw[i + 1]);
            return new VmdCameraKeyframe { Frame = (uint)k.Frame, Interest = k.LookAt, Rotation = k.Angle, Distance = k.Distance,
                ViewAngle = (uint)k.Fov, Perspective = k.Perspective, Interpolation = raw };
        }).ToArray() });
        return (layout, motions, sources, controls, scene);
    }

    private static PmmDocument Document(bool detach = false, int savedModel = 42, int savedBone = 0,
        bool missingIk = false, bool missingPhysics = false, PmmString? alias = null)
    {
        var frames = new[] { 0, 5, 10, 15, 20 };
        var keys = frames.Select((frame, i) => new PmmCameraKeyframe { ObjectIndex = i, RecordIndex = i, Frame = frame,
            PreviousIndex = Math.Max(0, i - 1), NextIndex = i == frames.Length - 1 ? 0 : i + 1,
            LookAt = new(frame * .1f, 3, 1), Angle = new(.1f, .2f, -.3f), Distance = -12, Fov = 35, Perspective = true,
            Interpolation = Linear(6), ParentModelIndex = detach || i == 2 ? -1 : i == 1 ? 42 : 7,
            ParentBoneIndex = detach || i == 2 ? -1 : i == 1 ? 1 : 0 }).ToArray();
        return new() { AnsiCodePage = alias is null ? 932 : 936, PanelStates = new byte[7], OutputWidth = 640, OutputHeight = 480, CameraFov = 35,
            Models = [Track(42, ["unused", "target"]), Track(7, ["target", "unused"], missingIk, missingPhysics, alias)],
            Camera = new() { InitialKeyframe = keys[0], Keyframes = keys.Skip(1).ToArray(), LookAt = new(2, 3, 4),
                Position = new(0, 0, -12), Angle = new(.1f, .2f, -.3f), Perspective = true },
            Settings = new() { EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0,
                CameraLookAtModelIndex = savedModel, CameraLookAtBoneIndex = savedBone, PreferredFps = 30 },
            Gravity = new() { Acceleration = 9.8f, Direction = -Vector3.UnitY,
                InitialKeyframe = new() { Acceleration = 9.8f, Direction = -Vector3.UnitY } } };
    }
    private static PmmModel Track(byte id, string[] names, bool missingIk = false, bool missingPhysics = false, PmmString? alias = null)
    {
        PmmString S(string name) => new(0, name, []);
        return new() { Index = id, Name = alias ?? S($"actor{id}"), Path = S($"actor{id}.pmx"), BoneNames = names.Select(S).ToArray(),
            SelectedMorphIndices = [-1, -1, -1, -1], Visible = true, LastFrame = 20,
            ConstraintBoneIndices = missingIk ? [1] : [], ConstraintStates = missingIk ? [new(0, true)] : [],
            InitialModelKeyframe = new() { Visible = true, ConstraintEnabled = missingIk ? [true] : [] },
            InitialBoneKeyframes = names.Select((name, i) => new PmmBoneKeyframe { ObjectIndex = i, NextIndex = i + 2,
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, name == "target" ? .3f : -.5f),
                Translation = name == "target" ? new(2, 3, 1) : new(70, -20, 0), Interpolation = Linear(4),
                PhysicsDisabled = missingPhysics && name == "unused" }).ToArray(),
            BoneKeyframes = names.Select((name, i) => new PmmBoneKeyframe { ObjectIndex = i + 2, PreviousIndex = i, Frame = 20,
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, name == "target" ? 1 : -.7f),
                Translation = name == "target" ? new(4, 6, 2) : new(90, -30, 0), Interpolation = Linear(4) }).ToArray(),
            BoneStates = names.Select(_ => new PmmBoneState(0, Vector3.Zero, Quaternion.Identity, false, false, 0)).ToArray() };
    }
    private static byte[] Linear(int count) => Enumerable.Range(0, count).SelectMany(_ => new byte[] { 20, 20, 107, 107 }).ToArray();
    private static PmxModel Model(string[] names)
    {
        var skeleton = PmmMotionAdapterTests.Pmx(names);
        return new() { Header = skeleton.Header, Name = "camera-parent", EnglishName = "", Comment = "", EnglishComment = "", Bones = skeleton.Bones,
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex { Position = p, Normal = Vector3.UnitZ,
                Uv = Vector2.Zero, AdditionalUvs = [], WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [0, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = [], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One,
                Specular = Vector3.Zero, SpecularPower = 1, Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None,
                EdgeColor = Vector4.One, EdgeSize = 1, TextureIndex = -1, SphereTextureIndex = -1, ToonTextureIndex = -1,
                SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate, Memo = "", FaceVertexCount = 3 }],
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [] };
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene,
        PmxOriginalOutlineAdapter.FromRestPose(model), new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private static ICharacterPhysicsBackend NoPhysics(EditableSceneObject _) => throw new InvalidOperationException("Unexpected physics allocation.");
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
