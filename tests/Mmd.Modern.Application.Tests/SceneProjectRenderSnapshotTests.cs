using System.IO.Compression;
using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Physics;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneProjectRenderSnapshotTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SameSourceInstancesKeepDistinctReferencesAndCompositionOrder()
    {
        var model = Model(2); var layout = new SceneCompositionEditor(); var rest = EditedMotionProjectTests.Rest(model);
        var first = layout.Add("same", "same.pmx", ModelRole.Character, rest);
        var second = layout.Add("same", "same.pmx", ModelRole.Environment, rest);
        using var project = new SceneProjectSession(layout, new(), [], new PhysicsProvider(), cancellationToken: Token);
        project.SetDefaultRenderingStyle(new("studio.Default"), Token);
        project.SetModelRenderingStyle(first.Id, new("studio.Actor"), Token);
        var snapshot = project.CaptureRenderSnapshot();

        Assert.Same(project.Frame, snapshot.Scene); Assert.Equal(project.CurrentFrame, snapshot.SourceFrame);
        Assert.Equal(new[] { first.Id, second.Id }, snapshot.RenderingStyles.Select(binding => binding.ObjectId));
        Assert.Equal(new[] { "studio.Actor", "studio.Default" }, snapshot.RenderingStyles.Select(binding => binding.Preset.StableId));
        Assert.Equal(new[] { ModelRole.Character, ModelRole.Environment }, snapshot.RenderingStyles.Select(binding => binding.Role));
        Assert.Equal(new[] { 0, 2 }, snapshot.RenderingStyles.Select(binding => binding.MaterialStart));
        Assert.All(snapshot.RenderingStyles, binding => { Assert.Equal(2, binding.MaterialCount); Assert.True(binding.Visible); });
        Assert.Equal(snapshot.Scene.Materials.Count, snapshot.RenderingStyles.Sum(binding => binding.MaterialCount));
        Assert.Empty(snapshot.MorphChannels);
    }

    [Fact]
    public void HiddenAndReappearingActorsRetainIdentityAndUseCurrentCombinedMaterialRanges()
    {
        var large = Model(2); var small = Model(1); var layout = new SceneCompositionEditor();
        var first = layout.Add("first", "same.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(large));
        var second = layout.Add("second", "same.pmx", ModelRole.Environment, EditedMotionProjectTests.Rest(small));
        var third = layout.Add("static", "static.pmx", ModelRole.Prop, EditedMotionProjectTests.Rest(small));
        layout.SetTransform(second.Id, new(100, 0, 0), Quaternion.Identity, 2);
        using var project = new SceneProjectSession(layout, new(),
            [new(second.Id, small, Motion(), PhysicsEnabled: false), new(first.Id, large, Motion(hide: true), PhysicsEnabled: false)],
            new PhysicsProvider(), cancellationToken: Token);
        project.SetModelRenderingStyle(first.Id, RenderingStyleId.Genshin, Token);
        project.Seek(2, Token); var before = project.CaptureRenderSnapshot();
        Assert.Equal(new[] { 0, 2, 3 }, before.RenderingStyles.Select(binding => binding.MaterialStart));
        Assert.Equal(new[] { first.Id, second.Id }, before.MorphChannels.Keys);
        Assert.Equal(.2f, before.MorphChannels[first.Id].TextureFactors[0].Diffuse.Add.X, 5);
        Assert.Equal(.2f, before.Scene.Surface.Materials[0].TextureFactors.Diffuse.Add.X, 5);
        Assert.InRange(before.Scene.Surface.Snapshot.Vertices[6].Position.X, 103.9999f, 104.0001f);

        project.Seek(5, Token); var hidden = project.CaptureRenderSnapshot();
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, hidden.RenderingStyles.Select(binding => binding.ObjectId));
        Assert.Equal(new[] { 0, 0, 1 }, hidden.RenderingStyles.Select(binding => binding.MaterialStart));
        Assert.Equal(new[] { 0, 1, 1 }, hidden.RenderingStyles.Select(binding => binding.MaterialCount));
        Assert.False(hidden.RenderingStyles[0].Visible); Assert.True(hidden.RenderingStyles[1].Visible);
        Assert.Equal<RenderingPresetReference>(RenderingStyleId.Genshin, hidden.RenderingStyles[0].Preset);
        Assert.Equal(.5f, hidden.MorphChannels[first.Id].TextureFactors[0].Diffuse.Add.X, 5);
        Assert.Equal(.5f, hidden.Scene.Surface.Materials[0].TextureFactors.Diffuse.Add.X, 5);
        Assert.Equal(2, hidden.Scene.Materials.Count);
        Assert.InRange(hidden.Scene.Surface.Snapshot.Vertices[0].Position.X, 109.9999f, 110.0001f);

        project.Seek(10, Token); var shown = project.CaptureRenderSnapshot();
        Assert.Equal(new[] { 0, 2, 3 }, shown.RenderingStyles.Select(binding => binding.MaterialStart));
        Assert.Equal(new[] { 2, 1, 1 }, shown.RenderingStyles.Select(binding => binding.MaterialCount));
        Assert.All(shown.RenderingStyles, binding => Assert.True(binding.Visible));
        Assert.Equal(1, shown.MorphChannels[first.Id].TextureFactors[0].Diffuse.Add.X);
        Assert.Equal(.2f, before.MorphChannels[first.Id].TextureFactors[0].Diffuse.Add.X, 5);
        Assert.Equal(4, before.Scene.Materials.Count); Assert.Equal(2, hidden.Scene.Materials.Count);
    }

    [Fact]
    public void AllHiddenBindingsHaveZeroLengthAndKeepTheirReferences()
    {
        var model = Model(2); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(model));
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, Motion(hide: true), PhysicsEnabled: false)],
            new PhysicsProvider(), cancellationToken: Token);
        project.Seek(5, Token); var snapshot = project.CaptureRenderSnapshot(); var binding = Assert.Single(snapshot.RenderingStyles);
        Assert.Equal(actor.Id, binding.ObjectId); Assert.False(binding.Visible);
        Assert.Equal(0, binding.MaterialStart); Assert.Equal(0, binding.MaterialCount); Assert.Empty(snapshot.Scene.Materials);
        Assert.Single(snapshot.MorphChannels);
    }

    [Fact]
    public void EffectiveRolesUseExplicitLightingOverridesIncludingUnspecified()
    {
        var model = Model(1); var layout = new SceneCompositionEditor(); var rest = EditedMotionProjectTests.Rest(model);
        var first = layout.Add("first", "actor.pmx", ModelRole.Character, rest);
        var second = layout.Add("second", "actor.pmx", ModelRole.Environment, rest);
        var lighting = new SceneLightingEditor(); lighting.SetModelRole(first.Key, ModelRole.Unspecified);
        lighting.SetModelRole(second.Key, ModelRole.Character);
        using var project = new SceneProjectSession(layout, lighting, [], new PhysicsProvider(), cancellationToken: Token);
        var snapshot = project.CaptureRenderSnapshot();
        Assert.Equal(new[] { ModelRole.Unspecified, ModelRole.Character }, snapshot.RenderingStyles.Select(binding => binding.Role));
        Assert.All(snapshot.Scene.Materials, material =>
        {
            Assert.Null(material.CharacterToonResponse);
            Assert.Null(material.CharacterOutlineStyle);
        });
        Assert.Equal(rest.Materials[0].GiToonResponseOverride, snapshot.Scene.Materials[0].GiToonResponseOverride);
        Assert.Equal(RayGiToonResponse.PreservePaletteBands, snapshot.Scene.Materials[1].GiToonResponseOverride);
    }

    [Fact]
    public void CapturingAndStyleUndoKeepTheCurrentPhysicalFrameAndSolverHistory()
    {
        var model = Model(1, physics: true); var layout = new SceneCompositionEditor(); var physics = new PhysicsProvider();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(model));
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, Motion())], physics, cancellationToken: Token);
        project.Advance(1.0 / 30, Token); var before = project.CaptureRenderSnapshot(); var backend = Assert.Single(physics.Backends);
        int steps = backend.Steps;
        project.SetModelRenderingStyle(actor.Id, new("studio.Custom"), Token);
        var edited = project.CaptureRenderSnapshot(); Assert.Equal("studio.Custom", edited.RenderingStyles[0].Preset.StableId);
        Assert.True(project.Undo(Token)); var undone = project.CaptureRenderSnapshot();
        Assert.Equal("mmd", undone.RenderingStyles[0].Preset.StableId); Assert.True(project.Redo(Token));
        var redone = project.CaptureRenderSnapshot();
        Assert.Equal("studio.Custom", redone.RenderingStyles[0].Preset.StableId);
        foreach (var captured in new[] { edited, undone, redone })
        { Assert.Same(before.Scene, captured.Scene); Assert.Equal(before.SourceFrame, captured.SourceFrame); }
        Assert.Single(physics.Backends); Assert.Equal(steps, backend.Steps); Assert.Equal(0, backend.DisposeCount);
        Assert.False(project.IsFaulted); project.Advance(1.0 / 30, Token); Assert.Equal(steps + 4, backend.Steps);
    }

    [Fact]
    public void CapturedMetadataIsDetachedAndReadOnlyAcrossFurtherEditsAndSamples()
    {
        var model = Model(1); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(model));
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, Motion(), PhysicsEnabled: false)],
            new PhysicsProvider(), cancellationToken: Token);
        project.Seek(2, Token); var snapshot = project.CaptureRenderSnapshot(); var channels = snapshot.MorphChannels[actor.Id];
        Assert.Throws<NotSupportedException>(() => ((IList<SceneRenderingStyleBinding>)snapshot.RenderingStyles).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<Guid, CharacterMorphChannels>)snapshot.MorphChannels).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PmxAdditionalUvVertex>)channels.AdditionalUvs).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PmxMaterialTextureFactors>)channels.TextureFactors).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PmxRigidBodyImpulse>)channels.Impulses).Clear());
        project.SetDefaultRenderingStyle(RenderingStyleId.Endfield, Token); project.Seek(10, Token);
        Assert.Equal(2, snapshot.SourceFrame); Assert.Equal("mmd", snapshot.RenderingStyles[0].Preset.StableId);
        Assert.Equal(.2f, channels.TextureFactors[0].Diffuse.Add.X, 5);
        Assert.Equal(1, project.CaptureRenderSnapshot().MorphChannels[actor.Id].TextureFactors[0].Diffuse.Add.X);
    }

    [Fact]
    public async Task ProjectOfflineFramesShareTheFractionalClockAndCompleteMetadata()
    {
        var model = Model(1); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(model));
        var sceneMotion = SceneMotionAsset.FromMotion(new()
        {
            CameraKeyframes = [new() { Distance = -30, Interest = new(1, 2, 3), ViewAngle = 45, Perspective = true }],
            LightKeyframes = [new() { Color = new(.3f, .4f, .5f), Direction = new(0, -1, 1) }],
            SelfShadowKeyframes = [new() { Mode = 2, Distance = .001f }]
        });
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, Motion(), PhysicsEnabled: false)],
            new PhysicsProvider(), sceneMotion: sceneMotion, cancellationToken: Token);
        project.SetModelRenderingStyle(actor.Id, new("studio.Export"), Token);
        var frames = new List<OfflineFrame>();
        var result = await OfflineFrameRunner.RunProjectAsync(project, new(640, 360, new(120), 0, 1), (frame, _) =>
        {
            var snapshot = project.CaptureRenderSnapshot(); Assert.Equal(frame.Sample.SampledSourceFrame.ToDouble(), snapshot.SourceFrame);
            Assert.Same(snapshot.Scene, frame.Scene); Assert.Equal(snapshot.Camera, frame.Camera); Assert.Equal(snapshot.Light, frame.Light);
            Assert.Equal(snapshot.SelfShadow, frame.SelfShadow); Assert.Equal(snapshot.ResolvedCamera, frame.ResolvedCamera);
            Assert.Equal(snapshot.RenderingStyles.ToArray(), frame.RenderingStyles.ToArray()); frames.Add(frame); return ValueTask.CompletedTask;
        }, cancellationToken: Token);
        Assert.Equal(5, result.CompletedFrames); Assert.Equal(4, result.SimulationTicks);
        Assert.Equal(new OfflineFrameTime(1, 4), frames[1].Sample.SampledSourceFrame);
        Assert.InRange(frames[1].Scene.Surface.Snapshot.Vertices[0].Position.X, .2499f, .2501f);
        Assert.Equal(.025f, frames[1].MorphChannels[actor.Id].TextureFactors[0].Diffuse.Add.X, 5);
        Assert.All(frames, frame =>
        {
            Assert.Equal(640, frame.Width); Assert.Equal(360, frame.Height);
            Assert.Equal("studio.Export", Assert.Single(frame.RenderingStyles).Preset.StableId);
            Assert.Equal(new Vector3(1, 2, 3), frame.Camera!.Value.LookAt); Assert.NotNull(frame.Light); Assert.NotNull(frame.SelfShadow);
            Assert.Equal(frame.Scene.Materials.Count, frame.RenderingStyles.Sum(binding => binding.MaterialCount));
        });
        Assert.Equal(Vector3.Zero, frames[0].Scene.Surface.Snapshot.Vertices[0].Position);
    }

    [Fact]
    public async Task ProjectOfflineRunnerRetainsTheExistingCaptureClockGuard()
    {
        var model = Model(1); var layout = new SceneCompositionEditor();
        var actor = layout.Add("actor", "actor.pmx", ModelRole.Character, EditedMotionProjectTests.Rest(model));
        using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, Motion(), PhysicsEnabled: false)],
            new PhysicsProvider(), cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => OfflineFrameRunner.RunProjectAsync(project,
            new(10, 10, new(30), 0, 1), (_, _) => { project.Advance(.1, Token); return ValueTask.CompletedTask; }, cancellationToken: Token));
    }

    [Fact]
    public async Task FutureStyleSchemaLoadsButTypedCaptureAndOfflineOutputAreUnavailable()
    {
        string directory = Path.Combine(Path.GetTempPath(), "project-render-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var model = Model(1); string source = Path.Combine(directory, "actor.pmx"); File.WriteAllBytes(source, PmxWriter.ToBytes(model));
            var layout = new SceneCompositionEditor(); var actor = layout.Add("actor", source, ModelRole.Character, EditedMotionProjectTests.Rest(model));
            var asset = SceneMotionAsset.FromMotion(Motion());
            using var project = new SceneProjectSession(layout, new(), [new(actor.Id, model, asset.Motion, PhysicsEnabled: false, Asset: asset)],
                new PhysicsProvider(), cancellationToken: Token);
            string path = Path.Combine(directory, "project.pma"); PmaProjectStore.Save(path, project, cancellationToken: Token);
            JsonNode manifest;
            using (var archive = ZipFile.OpenRead(path))
            using (var input = archive.GetEntry("manifest.json")!.Open()) manifest = JsonNode.Parse(input)!;
            manifest["optionalExtensions"]![SceneRenderingStyles.ExtensionName] = new JsonObject
                { ["version"] = 2, ["futureSelection"] = new JsonObject { ["preset"] = "studio.Future" } };
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                archive.GetEntry("manifest.json")!.Delete();
                using var output = new StreamWriter(archive.CreateEntry("manifest.json").Open()); output.Write(manifest.ToJsonString());
            }
            using var loaded = PmaProjectStore.Load(path, Path.Combine(directory, "cache"), (file, token) =>
            {
                token.ThrowIfCancellationRequested(); using var input = File.OpenRead(file); var parsed = PmxReader.Read(input);
                return new SceneAnimationModelAsset(parsed, EditedMotionProjectTests.Rest(parsed));
            }, new PhysicsProvider(), cancellationToken: Token);
            Assert.False(loaded.Session.HasSupportedRenderingStyles); loaded.Session.Seek(2, Token); var before = loaded.Session.Frame;
            Assert.Throws<NotSupportedException>(() => loaded.Session.CaptureRenderSnapshot());
            int calls = 0;
            await Assert.ThrowsAsync<NotSupportedException>(() => OfflineFrameRunner.RunProjectAsync(loaded.Session,
                new(10, 10, new(30), 0, 1), (_, _) => { calls++; return ValueTask.CompletedTask; }, cancellationToken: Token));
            Assert.Equal(0, calls); Assert.Equal(2, loaded.Session.CurrentFrame); Assert.Same(before, loaded.Session.Frame);
        }
        finally
        {
            string target = Path.GetFullPath(directory); string temp = Path.GetFullPath(Path.GetTempPath());
            if (!target.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    private static VmdMotion Motion(bool hide = false) => new()
    {
        BoneKeyframes = [new() { BoneName = "root" }, new() { BoneName = "root", Frame = 10, Translation = new(10, 0, 0) }],
        MorphKeyframes = [new() { MorphName = "texture" }, new() { MorphName = "texture", Frame = 10, Weight = 1 }],
        IkKeyframes = hide ? [new() { Visible = true }, new() { Frame = 5, Visible = false }, new() { Frame = 10, Visible = true }] : []
    };

    private static PmxModel Model(int materialCount, bool physics = false)
    {
        var source = EditedMotionProjectTests.Model();
        return new()
        {
            Header = source.Header, Name = "snapshot", EnglishName = "", Comment = "", EnglishComment = "", Textures = [],
            Vertices = Enumerable.Range(0, materialCount).SelectMany(material => source.Vertices.Select(vertex => new PmxVertex
            {
                Position = vertex.Position + new Vector3(material * 2, 0, 0), Normal = vertex.Normal, Uv = vertex.Uv, AdditionalUvs = [],
                WeightType = vertex.WeightType, BoneIndices = vertex.BoneIndices, BoneWeights = vertex.BoneWeights, EdgeScale = vertex.EdgeScale
            })).ToArray(),
            Faces = Enumerable.Range(0, materialCount).Select(index => new PmxTriangle(index * 3, index * 3 + 1, index * 3 + 2)).ToArray(),
            Materials = Enumerable.Range(0, materialCount).Select(index => new PmxMaterial
            {
                Name = "material-" + index, EnglishName = "", Diffuse = Vector4.One, Specular = Vector3.Zero, SpecularPower = 1,
                Ambient = Vector3.Zero, DrawMode = PmxDrawModeFlags.None, EdgeColor = Vector4.Zero, EdgeSize = 0,
                TextureIndex = -1, SphereTextureIndex = -1, SphereMode = PmxSphereMode.None, ToonMode = PmxToonMode.Separate,
                ToonTextureIndex = -1, Memo = "", FaceVertexCount = 3
            }).ToArray(), Bones = source.Bones, DisplayFrames = [], Joints = [], SoftBodies = [],
            Morphs = [new() { Name = "texture", EnglishName = "", ControlPanel = 1, Type = PmxMorphType.Material,
                MaterialOffsets = Enumerable.Range(0, materialCount).Select(index => new PmxMorph.MaterialOffset(index,
                    PmxMaterialMorphOperation.Add, Vector4.Zero, Vector3.Zero, 0, Vector3.Zero, Vector4.Zero, 0,
                    new Vector4(index + 1), Vector4.Zero, Vector4.Zero)).ToArray() }],
            RigidBodies = physics ? [new() { Name = "body", EnglishName = "", BoneIndex = 0, Group = 0, CollisionGroup = 0,
                Shape = PmxRigidBodyShape.Sphere, ShapeSize = new(.2f), Translation = Vector3.Zero, Rotation = Vector3.Zero,
                Mass = 1, TranslationDamping = 0, RotationDamping = 0, Repulsion = 0, Friction = 0,
                Operation = PmxRigidBodyOperation.Dynamic }] : []
        };
    }

    private sealed class PhysicsProvider : IProjectPhysicsBackendProvider
    {
        public List<SceneAnimationSessionTests.FallingBackend> Backends { get; } = [];
        public ICharacterPhysicsBackend CreateIndependent(ProjectPhysicsSettings settings, EditableSceneObject item)
        { var backend = new SceneAnimationSessionTests.FallingBackend(); Backends.Add(backend); return backend; }
        public ICharacterPhysicsMutableBackend CreateShared(ProjectPhysicsSettings settings) => throw new NotSupportedException();
    }
}
