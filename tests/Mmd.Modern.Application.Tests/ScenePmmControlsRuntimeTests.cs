using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Mmd.Modern.Physics.Bullet;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class ScenePmmControlsRuntimeTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "modern-pmm-controls-" + Guid.NewGuid().ToString("N"));
    public ScenePmmControlsRuntimeTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundaryFlagsDynamicParentsAndOneSceneNoiseStreamReachEveryWorld(bool shared)
    {
        var f = Fixture(); var worlds = new List<Probe>();
        Probe NewWorld() { var world = new Probe(); worlds.Add(world); return world; }
        using var session = new SceneAnimationSession(f.Layout.Objects, f.Motions, _ => NewWorld(),
            sharedPhysicsFactory: shared ? NewWorld : null, pmmControls: f.Controls);
        Assert.Equal(shared ? 1 : 2, worlds.Count); Assert.Equal(shared, session.UsesSharedPhysicsWorld);
        session.Advance(1d / 240, Token); Assert.All(worlds, w => Assert.Empty(w.History));
        session.Advance(1d / 240, Token); Assert.All(worlds, w => Assert.Single(w.History));
        for (int i = 1; i < 124; i++) session.Advance(1d / 120, Token);
        Assert.All(worlds, w => Assert.Equal(124, w.History.Count));
        var subjectHistory = worlds.SelectMany(w => w.History).Where(h => h.Bodies.Any(b => b.Name == "subject")).ToArray();
        Assert.False(subjectHistory[2].Bodies.Single(b => b.Name == "subject").Kinematic);
        var attached = subjectHistory[3].Bodies.Single(b => b.Name == "subject");
        Assert.True(attached.Kinematic); Assert.InRange(attached.World.Translation.X, 10.1f, 10.3f);
        Assert.False(subjectHistory[7].Bodies.Single(b => b.Name == "subject").Kinematic);
        var noise = new PmmGravityNoise(); noise.Reset(f.Controls.Sample(0).Gravity);
        var expected = Enumerable.Range(1, 124).Select(i => noise.Advance(f.Controls.Sample(i * .25).Gravity, 1d / 120)).ToArray();
        Assert.Equal(8ul, noise.RandomDrawCount);
        Assert.All(worlds, w => Assert.Equal(expected, w.History.Select(h => h.Gravity)));
        var before = session.Frame.Surface.Snapshot.Vertices.ToArray();
        session.Seek(0, Token); Assert.All(worlds, w => w.History.Clear());
        for (int i = 0; i < 124; i++) session.Advance(1d / 120, Token);
        Assert.Equal(before, session.Frame.Surface.Snapshot.Vertices);
        Assert.All(worlds, w => Assert.Equal(expected, w.History.Select(h => h.Gravity)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterpolatedGravityIsAppliedAtEachPhysicalBoundary(bool shared)
    {
        var f = Fixture(); var worlds = new List<Probe>();
        var controls = f.Controls.Edit(e => e.PrepareRegister([
            new(new(PmmTrackKind.Gravity, 0, "", 0), new PmmGravityKeyframe { Acceleration = 1, Direction = -Vector3.UnitY }),
            new(new(PmmTrackKind.Gravity, 0, "", 4), new PmmGravityKeyframe { Acceleration = 3, Direction = Vector3.UnitX })]), true);
        Probe NewWorld() { var p = new Probe(); worlds.Add(p); return p; }
        using var session = new SceneAnimationSession(f.Layout.Objects, f.Motions, _ => NewWorld(),
            sharedPhysicsFactory: shared ? NewWorld : null, pmmControls: controls);
        for (int i = 0; i < 16; i++) session.Advance(1d / 120, Token);
        var expected = Enumerable.Range(1, 16).Select(i => controls.Sample(i * .25).Gravity.WorldGravity).ToArray();
        Assert.NotEqual(expected[0], expected[^1]); Assert.All(worlds, w => Assert.Equal(expected, w.History.Select(h => h.Gravity)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FaultedWorldIsResetBeforeGravityAndSuccessfulSeekRestoresPlayback(bool shared)
    {
        var f = Fixture(); var worlds = new List<Probe>();
        Probe NewWorld() { var world = new Probe(); worlds.Add(world); return world; }
        using var session = new SceneAnimationSession(f.Layout.Objects, f.Motions, _ => NewWorld(),
            sharedPhysicsFactory: shared ? NewWorld : null, pmmControls: f.Controls);
        worlds[0].FailStep = true;
        Assert.Throws<InvalidOperationException>(() => session.Advance(1d / 120, Token)); Assert.True(session.IsFaulted);
        int calls = worlds.Sum(w => w.History.Count);
        Assert.Throws<InvalidOperationException>(() => session.Advance(1d / 120, Token)); Assert.Equal(calls, worlds.Sum(w => w.History.Count));
        worlds[0].FailStep = false; session.Seek(0, Token); Assert.False(session.IsFaulted);
        session.Advance(1d / 120, Token); Assert.All(worlds, w => Assert.NotEmpty(w.History));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualBulletPureClothConsumesControlsWithoutAnyRigidActor(bool shared)
    {
        var model = PmxSoftBodyTests.MaterialFixture(); var layout = new SceneCompositionEditor();
        var actor = layout.Add("cloth", "cloth.pmx", ModelRole.Character, Rest(model));
        var doc = Document([ModelTrack(7, "cloth", model.Bones.Select(b => b.Name).ToArray(), false)], gravityX: true);
        var controls = ScenePmmControlState.Create(doc, [new(actor.Id, 7, model)]);
        var asset = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 60 }] });
        int rigidFactories = 0;
        using var session = new SceneAnimationSession(layout.Objects, [new(actor.Id, model, asset.Motion, Asset: asset)],
            _ => { rigidFactories++; return new Probe(); }, sharedPhysicsFactory: shared ? () => { rigidFactories++; return new Probe(); } : null,
            softPhysicsFactory: _ => new SoftBodyBulletBackend(new(0, -9.8f, 0), null), pmmControls: controls);
        for (int i = 0; i < 15; i++) session.Advance(1d / 30, Token);
        var free = session.Frame.Surface.Snapshot.Vertices[4].Position;
        Assert.True(free.X > model.Vertices[4].Position.X + .01f); Assert.InRange(MathF.Abs(free.Y - model.Vertices[4].Position.Y), 0, .01f);
        Assert.Equal(shared ? 1 : 0, rigidFactories);
        session.Seek(0, Token); Assert.Equal(model.Vertices[4].Position, session.Frame.Surface.Snapshot.Vertices[4].Position);
        for (int i = 0; i < 15; i++) session.Advance(1d / 30, Token);
        Assert.Equal(free, session.Frame.Surface.Snapshot.Vertices[4].Position);
    }

    [Fact]
    public void OwnerImportEditUndoRedoAndFailedCandidatePreservePublishedSolver()
    {
        var f = Fixture(); var worlds = new List<Probe>(); bool fail = false;
        var provider = new ProjectSettingsBackendProvider((_, _) => { var p = new Probe { FailStep = fail, FailInitialize = fail }; worlds.Add(p); return p; }, _ => new Probe());
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider);
        project.Edit(e => e.ImportPmmControls(f.Controls.ExportDocument(), f.Controls.ModelIndices), Token);
        string original = project.PmmControls!.Sha256;
        project.Edit(e => e.EditPmmControls(GravityPlan, confirmOverwrite: true), Token);
        string edited = project.PmmControls!.Sha256; Assert.NotEqual(original, edited);
        Assert.True(project.Undo(Token)); Assert.Equal(original, project.PmmControls!.Sha256);
        Assert.True(project.Redo(Token)); Assert.Equal(edited, project.PmmControls!.Sha256);
        project.Advance(1d / 120, Token); var before = project.Frame; var active = worlds.TakeLast(2).ToArray();
        fail = true;
        Assert.Throws<InvalidOperationException>(() => project.Edit(e => e.EditPmmControls(GravityPlan, confirmOverwrite: true), Token));
        Assert.Same(before, project.Frame); Assert.Equal(edited, project.PmmControls!.Sha256); Assert.All(active, p => Assert.Equal(0, p.Disposals));
        int steps = active.Sum(p => p.History.Count); project.Advance(1d / 120, Token); Assert.True(active.Sum(p => p.History.Count) > steps);
        Assert.Throws<InvalidDataException>(() => project.Edit(e => e.RemoveObject(f.Motions[0].ObjectId), Token));
    }

    [Fact]
    public void PmmOwnerExportsCurrentSupportedOutputSettingsAndRejectsUnrepresentableRationalFps()
    {
        var f = Fixture(); var provider = new ProjectSettingsBackendProvider((_, _) => new Probe(), _ => new Probe());
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider,
            new() { Width = 2560, Height = 1440, FpsNumerator = 60 }, pmmControls: f.Controls);
        byte[] retained = PmmWriter.ToBytes(project.PmmControls!.ExportDocument());
        var output = project.ExportPmm();
        Assert.Equal(2560, output.OutputWidth); Assert.Equal(1440, output.OutputHeight); Assert.Equal(60, output.Settings.PreferredFps);
        Assert.Equal(retained, PmmWriter.ToBytes(project.PmmControls.ExportDocument()));
        Assert.Equal(project.PmmControls.ExportDocument().Models.Select(m => m.Index), output.Models.Select(m => m.Index));
        project.Edit(edit => edit.Settings = edit.Settings with { FpsNumerator = 60000, FpsDenominator = 1001 }, Token);
        var report = PmaPmmCompatibility.Inspect(project);
        Assert.Contains(report.Issues, issue => issue.Code == "output-settings");
        Assert.Throws<PmaPmmExportException>(() => project.ExportPmm());
    }

    [Fact]
    public void PortableV8ReopenAndExistingPmmExporterPreserveControlFieldsAndStableIds()
    {
        var f = Fixture(); var provider = new ProjectSettingsBackendProvider((_, _) => new Probe(), _ => new Probe());
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, pmmControls: f.Controls);
        project.Edit(e => e.EditPmmControls(GravityPlan, confirmOverwrite: true), Token); project.Seek(1, Token);
        string tree = Path.Combine(directory, "portable"); Directory.CreateDirectory(tree);
        string path = Path.Combine(tree, "project.mmdscene"); project.Save(path, cancellationToken: Token);
        Assert.Equal(8, JsonNode.Parse(File.ReadAllText(path))!["version"]!.GetValue<int>());
        string moved = Path.Combine(directory, "moved"); Directory.Move(tree, moved);
        using var loaded = SceneProjectSession.Load(Path.Combine(moved, "project.mmdscene"), (p, _) =>
        { Assert.StartsWith(moved + Path.DirectorySeparatorChar, p, StringComparison.OrdinalIgnoreCase); Assert.True(File.Exists(p)); using var input = File.OpenRead(p); var model = PmxReader.Read(input); return new(model, Rest(model)); }, provider, Token);
        Assert.Equal(project.PmmControls!.Sha256, loaded.PmmControls!.Sha256); Assert.Equal(project.PmmControls.OriginSha256, loaded.PmmControls.OriginSha256);
        Assert.Equal(project.Frame.Surface.Snapshot.Vertices, loaded.Frame.Surface.Snapshot.Vertices);
        string output = Path.Combine(moved, "roundtrip.pmm"); loaded.SavePmm(output, cancellationToken: Token);
        using var outputStream = File.OpenRead(output); var exported = PmmReader.Read(outputStream, new() { AnsiCodePage = 932 });
        Assert.Equal(loaded.Settings.Width, exported.OutputWidth); Assert.Equal(loaded.Settings.Height, exported.OutputHeight);
        Assert.Equal((float)loaded.Settings.FpsNumerator / loaded.Settings.FpsDenominator, exported.Settings.PreferredFps);
        var expected = loaded.PmmControls.ExportDocument();
        Assert.Equal(expected.Models.Select(m => m.Index), exported.Models.Select(m => m.Index));
        Assert.Equal(GravityFields(expected), GravityFields(exported));
        foreach (var model in expected.Models)
        {
            var actual = exported.Models.Single(m => m.Index == model.Index);
            Assert.Equal(model.OutsideParentSubjectBoneIndices, actual.OutsideParentSubjectBoneIndices);
            Assert.Equal(ParentFields(model), ParentFields(actual));
            Assert.Equal(BoneFlags(model), BoneFlags(actual));
        }
        Assert.Throws<InvalidDataException>(() => loaded.ExportPmm(new() { TemplateModelIndices = loaded.PmmControls.ModelIndices.ToDictionary(p => p.Key, _ => (byte)99) }));
        // An explicit verification run can retain generated fixtures for an independent native parser.
        if (Environment.GetEnvironmentVariable("MMD_C06_EVIDENCE_DIRECTORY") is { Length: > 0 } evidencePath)
        {
            string evidence = Path.GetFullPath(evidencePath); Directory.CreateDirectory(evidence);
            byte[] importedBytes = PmmWriter.ToBytes(f.Controls.ExportDocument());
            byte[] retainedBytes = PmmWriter.ToBytes(expected); byte[] exportedBytes = File.ReadAllBytes(output);
            File.WriteAllBytes(Path.Combine(evidence, "imported-controls.pmm"), importedBytes);
            File.WriteAllBytes(Path.Combine(evidence, "retained-controls.pmm"), retainedBytes);
            File.WriteAllBytes(Path.Combine(evidence, "exported-controls.pmm"), exportedBytes);
            foreach (string file in Directory.EnumerateFiles(moved, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(evidence, "portable-project", Path.GetRelativePath(moved, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, overwrite: true);
            }
            string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            File.WriteAllText(Path.Combine(evidence, "modern-controls-manifest.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                generatedFixtureOnly = true, currentFrame = loaded.CurrentFrame, originSha256 = loaded.PmmControls.OriginSha256,
                importedSha256 = Hash(importedBytes), retainedSha256 = Hash(retainedBytes), exportedSha256 = Hash(exportedBytes),
                projectSha256 = Hash(File.ReadAllBytes(Path.Combine(moved, "project.mmdscene"))),
                actorModels = loaded.PmmControls.ModelIndices.Select(p => new { objectId = p.Key, storedModelIndex = p.Value }).ToArray()
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Fact]
    public void TamperedV8PayloadRejectsBeforeOpeningAnyModel()
    {
        var f = Fixture(); var provider = new ProjectSettingsBackendProvider((_, _) => new Probe(), _ => new Probe());
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, pmmControls: f.Controls);
        string path = Path.Combine(directory, "tampered.mmdscene"); project.Save(path, cancellationToken: Token);
        var json = JsonNode.Parse(File.ReadAllText(path))!; json["pmmControls"]!["sha256"] = new string('0', 64); File.WriteAllText(path, json.ToJsonString());
        int loads = 0; Assert.Throws<InvalidDataException>(() => SceneProjectSession.Load(path, (_, _) => { loads++; throw new Exception(); }, provider, Token)); Assert.Equal(0, loads);
    }

    [Fact]
    public void FullBoneRecordEditsSynchronizeAuthoredMotionAndUndoRestoresBothChannels()
    {
        var f = Fixture(); var provider = new ProjectSettingsBackendProvider((_, _) => new Probe(), _ => new Probe());
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, pmmControls: f.Controls);
        project.Seek(1, Token); var before = project.Frame.Surface.Snapshot.Vertices.ToArray(); string digest = project.PmmControls!.Sha256;
        project.Edit(edit => edit.EditPmmControls(e => e.PrepareRegister([new(new(PmmTrackKind.Bone, 7, "root", 1), new PmmBoneKeyframe
        { Translation = new(4, 0, 0), Rotation = Quaternion.Identity, PhysicsDisabled = true,
            Interpolation = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(b => b).ToArray() })]), true), Token);
        Assert.Equal(new[] { 1 }, project.PmmControls!.Sample(1).DisabledBoneIndices[f.Motions[0].ObjectId]);
        Assert.InRange(project.Frame.Surface.Snapshot.Vertices[0].Position.X - before[0].Position.X, 3.999f, 4.001f);
        Assert.True(project.Undo(Token)); Assert.Equal(digest, project.PmmControls!.Sha256); Assert.Equal(before, project.Frame.Surface.Snapshot.Vertices);
        Assert.True(project.Redo(Token)); Assert.InRange(project.Frame.Surface.Snapshot.Vertices[0].Position.X - before[0].Position.X, 3.999f, 4.001f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnerCandidateCannotInitializeOrDisposeAnyActiveRigidBackend(bool shared)
    {
        var f = Fixture(); var worlds = new List<Probe>(); bool reuse = false; Probe? reused = null;
        Probe Factory() { if (reuse) return reused!; var p = new Probe(); worlds.Add(p); return p; }
        var provider = new ProjectSettingsBackendProvider((_, _) => Factory(), _ => Factory());
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider,
            new() { Physics = new() { SharedWorld = shared, GroundEnabled = false } }, pmmControls: f.Controls);
        reused = worlds[^1]; reuse = true;
        project.Advance(1d / 120, Token); var before = project.Frame; int initializations = reused.Initializations;
        Assert.Throws<InvalidOperationException>(() => project.Edit(e => e.EditPmmControls(GravityPlan, true), Token));
        Assert.Equal(initializations, reused.Initializations); Assert.Equal(0, reused.Disposals); Assert.Same(before, project.Frame);
        Assert.False(project.CanUndo); int steps = reused.History.Count; project.Advance(1d / 120, Token); Assert.True(reused.History.Count > steps);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("subject")]
    [InlineData("link")]
    public void ValidDigestWithMalformedParentMetadataRejectsBeforeOpeningAnyModel(string corruption)
    {
        var f = Fixture(); var provider = new ProjectSettingsBackendProvider((_, _) => new Probe(), _ => new Probe());
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, pmmControls: f.Controls);
        string path = Path.Combine(directory, "malformed.mmdscene"); project.Save(path, cancellationToken: Token);
        var document = f.Controls.ExportDocument(); var model = document.Models[0]; byte[] bytes = f.Controls.Capture().DocumentBytes;
        int offset;
        if (corruption == "cycle") offset = (int)model.ModelKeyframes[0].OutsideParents[0].Offset;
        else if (corruption == "link") offset = (int)model.ModelKeyframes[0].Offset + 12;
        else
        {
            var lastBone = model.BoneNames[^1];
            offset = (int)lastBone.Offset + 1 + lastBone.RawBytes.Count + 12;
        }
        BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), corruption == "cycle" ? 7 : 999);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["pmmControls"]!["documentBytes"] = Convert.ToBase64String(bytes);
        json["pmmControls"]!["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); File.WriteAllText(path, json.ToJsonString());
        int loads = 0;
        void Load() => SceneProjectSession.Load(path, (_, _) => { loads++; throw new Exception(); }, provider, Token).Dispose();
        if (corruption == "link") Assert.Throws<PmmFormatException>(Load);
        else Assert.Throws<InvalidDataException>(Load);
        Assert.Equal(0, loads);
    }

    private static PmmTrackEditPlan GravityPlan(PmmTrackEditor editor) => editor.PrepareRegister([new(new(PmmTrackKind.Gravity, 0, "", 0),
        new PmmGravityKeyframe { Acceleration = 3, Direction = -Vector3.UnitY, NoiseEnabled = true, Noise = 2 })]);
    private static object[] GravityFields(PmmDocument d) => new[] { d.Gravity.InitialKeyframe }.Concat(d.Gravity.Keyframes)
        .Select(k => (object)(k.Frame, k.Acceleration, k.Direction, k.Noise, k.NoiseEnabled)).ToArray();
    private static object[] ParentFields(PmmModel m) => new[] { m.InitialModelKeyframe }.Concat(m.ModelKeyframes)
        .SelectMany(k => k.OutsideParents.Select((p, i) => (object)(k.Frame, i, p.ModelIndex, p.BoneIndex))).ToArray();
    private static object[] BoneFlags(PmmModel m)
    {
        var tracks = new PmmPhysicsControlTracks(Document([m]));
        return new[] { 0, 1, 2, 60 }.SelectMany(frame => m.BoneNames.Select((name, i) =>
            (object)(name.Value, frame, tracks.DisabledBonesAt(m.Index, frame).Contains(i)))).ToArray();
    }

    [Fact]
    public void ModelAddedToAPmmProjectStepsPhysicsAndJoinsTheMixedDrawOrder()
    {
        // ValidateActors deliberately allows an actor added after the import. It used to fail later: physics
        // looked up its PMM disabled-bone set (KeyNotFoundException) and the mixed draw order left it out.
        var f = Fixture();
        var provider = new ProjectSettingsBackendProvider((_, _) => new Probe(), _ => new Probe());
        using var project = new SceneProjectSession(f.Layout, new(), f.Motions, provider, pmmControls: f.Controls);
        var extra = RigidModel("extra", ["root"], 0, false);
        string path = Path.Combine(directory, "extra.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(extra));
        Guid added = Guid.Empty;
        project.Edit(edit =>
        {
            var obj = edit.Composition.Add("extra", path, ModelRole.Character, Rest(extra)); added = obj.Id;
            var asset = SceneMotionAsset.FromMotion(new() { BoneKeyframes = [new() { BoneName = "root" }] });
            edit.Motions.Add(new(obj.Id, extra, asset.Motion, Asset: asset));
        }, Token);
        for (int i = 0; i < 4; i++) project.Advance(1d / 120, Token);
        var order = project.CaptureRenderSnapshot().MixedDrawOrder;
        Assert.Equal(3, order.Count(item => item.Kind == PmmDrawableKind.Model));
        var last = order.Single(item => item.ObjectId == added);
        Assert.Equal(order.Max(item => item.DrawOrderIndex), last.DrawOrderIndex);
    }

    private (SceneCompositionEditor Layout, SceneActorMotion[] Motions, ScenePmmControlState Controls) Fixture()
    {
        var layout = new SceneCompositionEditor(); var models = new[] { RigidModel("subject", ["unused", "root"], 1, false), RigidModel("parent", ["root"], 0, true) };
        var raw = Document([ModelTrack(7, "subject", ["root", "unused"], true), ModelTrack(42, "parent", ["root"], false)]);
        var doc = PmmReader.Read(new MemoryStream(PmmWriter.ToBytes(raw)), new() { AnsiCodePage = 932 });
        var motions = models.Select((model, i) =>
        {
            string path = Path.Combine(directory, model.Name + ".pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
            var actor = layout.Add(model.Name, path, ModelRole.Character, Rest(model));
            var motion = PmmMotionAdapter.Convert(doc.Models[i], model); var asset = SceneMotionAsset.FromMotion(motion);
            return new SceneActorMotion(actor.Id, model, asset.Motion, Asset: asset);
        }).ToArray();
        return (layout, motions, ScenePmmControlState.Create(doc, motions.Select((m, i) => new ScenePmmActorSource(m.ObjectId, doc.Models[i].Index, m.Model))));
    }
    private static PmmDocument Document(PmmModel[] models, bool gravityX = false) => new()
    {
        AnsiCodePage = 932, PanelStates = new byte[7], Models = models, OutputWidth = 640, OutputHeight = 480, CameraFov = 30,
        Camera = new() { InitialKeyframe = new() { Interpolation = new byte[24], Fov = 30, ParentModelIndex = -1, ParentBoneIndex = -1 } },
        Settings = new() { PhysicsSimulationMode = 1, EdgeColorRgb = [0, 0, 0], UnknownMatrix = new byte[64], SelectionTrailerFlag = 0,
            CameraLookAtModelIndex = -1, CameraLookAtBoneIndex = -1 },
        Gravity = new() { InitialKeyframe = new() { Acceleration = 1, Direction = gravityX ? Vector3.UnitX : -Vector3.UnitY, NoiseEnabled = !gravityX, Noise = 2 } }
    };
    private static PmmModel ModelTrack(byte id, string name, string[] bones, bool switches)
    {
        PmmString S(string value) => new(0, value, []);
        byte[] linear = Enumerable.Repeat(new byte[] { 20, 20, 107, 107 }, 4).SelectMany(b => b).ToArray();
        int count = bones.Length;
        var keys = new List<PmmBoneKeyframe>();
        for (int bone = 0; bone < count; bone++)
        {
            if (switches && bone == 0)
            {
                keys.Add(new() { ObjectIndex = count, PreviousIndex = 0, NextIndex = count + 1, Frame = 1, Rotation = Quaternion.Identity, Interpolation = linear, PhysicsDisabled = true });
                keys.Add(new() { ObjectIndex = count + 1, PreviousIndex = count, NextIndex = count + 2, Frame = 2, Rotation = Quaternion.Identity, Interpolation = linear });
            }
            keys.Add(new() { ObjectIndex = count + (switches ? 2 : 0) + bone, PreviousIndex = switches && bone == 0 ? count + 1 : bone,
                Frame = 60, Translation = name == "parent" ? new(20, 0, 0) : Vector3.Zero, Rotation = Quaternion.Identity, Interpolation = linear });
        }
        return new() { Index = id, Name = S(name), Path = S(name + ".pmx"), BoneNames = bones.Select(S).ToArray(), SelectedMorphIndices = [-1, -1, -1, -1], Visible = true,
            LastFrame = 60, OutsideParentSubjectBoneIndices = switches ? [0] : [],
            InitialBoneKeyframes = bones.Select((_, i) => new PmmBoneKeyframe { ObjectIndex = i, NextIndex = switches && i == 0 ? count : count + (switches ? 2 : 0) + i,
                Translation = name == "parent" ? new(10, 0, 0) : Vector3.Zero, Rotation = Quaternion.Identity, Interpolation = linear }).ToArray(), BoneKeyframes = keys,
            InitialModelKeyframe = new() { NextIndex = switches ? 1 : 0, Visible = true, OutsideParents = switches ? [new(0, -1, -1)] : [] },
            ModelKeyframes = switches ? [new() { ObjectIndex = 1, PreviousIndex = 0, NextIndex = 2, Frame = 1, Visible = true, OutsideParents = [new(0, 42, 0)] },
                new() { ObjectIndex = 2, PreviousIndex = 1, Frame = 2, Visible = true, OutsideParents = [new(0, -1, -1)] }] : [],
            BoneStates = bones.Select(_ => new PmmBoneState(0, Vector3.Zero, Quaternion.Identity, false, false, 0)).ToArray(), OutsideParentStates = switches ? [new(0, 0, int.MaxValue, new(0, -1, -1))] : [] };
    }
    private static PmxModel RigidModel(string name, string[] bones, int driven, bool kinematic)
    {
        var skeleton = PmmMotionAdapterTests.Pmx(bones);
        return new() { Header = skeleton.Header, Name = name, EnglishName = "", Comment = "", EnglishComment = "", Bones = skeleton.Bones,
            Vertices = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }.Select(p => new PmxVertex { Position = p, Normal = Vector3.UnitZ, Uv = Vector2.Zero, AdditionalUvs = [],
                WeightType = PmxVertexWeightType.Bdef1, BoneIndices = [driven, -1, -1, -1], BoneWeights = [1, 0, 0, 0], EdgeScale = 1 }).ToArray(),
            Faces = [new(0, 1, 2)], Textures = [], Materials = [new() { Name = "mat", EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                EdgeColor = Vector4.One, EdgeSize = 1, TextureIndex = -1, SphereTextureIndex = -1, ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3 }],
            Morphs = [], DisplayFrames = [], SoftBodies = [], Joints = [], RigidBodies = [new() { Name = name, EnglishName = "", BoneIndex = driven, Group = 0,
                CollisionGroup = ushort.MaxValue, Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.1f), Mass = 1, Translation = Vector3.Zero, Rotation = Vector3.Zero,
                TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = .5f,
                Operation = kinematic ? PmxRigidBodyOperation.Static : PmxRigidBodyOperation.Dynamic }] };
    }
    private static OriginalFrameScene Rest(PmxModel model) => new(PmxOriginalStyleAdapter.FromRestPose(model).Scene, PmxOriginalOutlineAdapter.FromRestPose(model),
        new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1), materials: PmxOriginalFrameAdapter.FromMaterials(model));
    private sealed record BodyState(string Name, bool Kinematic, Matrix4x4 World);
    private sealed record StepState(Vector3 Gravity, BodyState[] Bodies);
    private sealed class Probe : ICharacterPhysicsMutableBackend
    {
        private readonly TrackingSharedBackend inner = new();
        public readonly List<StepState> History = []; public bool FailStep, FailInitialize; public int Disposals, Initializations;
        public string Name => inner.Name; public IReadOnlyList<string> Diagnostics => [];
        public void Initialize(CharacterPhysicsDefinition d, ReadOnlySpan<Matrix4x4> w) { if (FailInitialize) throw new InvalidOperationException("candidate initialize"); Initializations++; inner.Initialize(d, w); }
        public int AddBody(PhysicsBodyDefinition d, Matrix4x4 w) => inner.AddBody(d, w);
        public int AddJoint(PhysicsJointDefinition d, Matrix4x4 a, Matrix4x4 b) => inner.AddJoint(d, a, b);
        public void RemoveBody(int i) => inner.RemoveBody(i); public void RemoveJoint(int i) => inner.RemoveJoint(i);
        public void ResetBody(int i, Matrix4x4 w) => inner.ResetBody(i, w);
        public void SetBodyKinematic(int i, bool k) => inner.SetBodyKinematic(i, k);
        public void SetKinematicTarget(int i, Matrix4x4 w, float dt) => inner.SetKinematicTarget(i, w, dt);
        public Matrix4x4 GetBodyWorldTransform(int i) => inner.GetBodyWorldTransform(i);
        public void SetGravity(Vector3 g) => inner.SetGravity(g);
        public void ApplyImpulse(int i, Vector3 l, Vector3 a) => inner.ApplyImpulse(i, l, a);
        public void Step(float dt) { if (FailStep) throw new InvalidOperationException("step fault"); inner.Step(dt); History.Add(new(inner.Gravity,
            inner.Bodies.Where(b => !b.Removed).Select(b => new BodyState(b.Definition.Name, b.Kinematic, b.Transform)).ToArray())); }
        public void Dispose() { Disposals++; inner.Dispose(); }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
