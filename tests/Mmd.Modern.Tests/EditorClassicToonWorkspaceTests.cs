using System.Text;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Mmd.Modern.ViewOutput.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed partial class EditorClassicToonPackageTests
{
    [Fact]
    public async Task WorkspaceToonTransactionRetainsFrozenEffectsThroughHistoryAndPortableReopen()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-toon-workspace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "actor.pmx");
            File.WriteAllBytes(sourcePath, PmxWriter.ToBytes(Model()));
            byte[] originalModel = File.ReadAllBytes(sourcePath);
            string authoredImage = Path.Combine(directory, "authored.bmp"); File.WriteAllBytes(authoredImage, Bitmap());
            var composition = new SceneCompositionEditor(); var asset = Load(sourcePath, Token);
            Guid id = composition.Add("actor", sourcePath, ModelRole.Character, asset.Source).Id;
            var seed = EditorClassicToonPackage.ReadPackage(new Resources(id, Bitmap()), Token)!;
            asset = seed.Apply(id, asset, sourcePath, Token);
            composition.ReplaceMaterialSource(id, asset.Source);
            var motion = SceneMotionAsset.FromMotion(new VmdMotion { ModelName = asset.Model.Name }, "motion.vmd");
            using var project = new SceneProjectSession(composition, new(),
                [new(id, asset.Model, motion.Motion, PhysicsEnabled: false, Asset: motion)], new NoPhysics());
            string fxPath = Path.Combine(directory, "frozen.fx");
            byte[] fxBytes = Encoding.ASCII.GetBytes("fixture frozen FX bytes"); File.WriteAllBytes(fxPath, fxBytes);
            var emm = EmmReader.Read(Encoding.ASCII.GetBytes("[Info]\nVersion=3\n[Object]\nPmd0=actor.pmx\n[Effect]\nDefault=none\n"));
            var plan = EmmImportPlan.Create(emm, Path.Combine(directory, "project.emm"),
                [new EmmObjectBinding(id, true, sourcePath, 0)], directory);
            var inspector = new ToonFixtureInspector();
            var effects = D3D9ClassicEffectsPreparation.Capture(plan, _ => fxPath, inspector, cancellationToken: Token).Preparation!;
            Assert.NotNull(effects.Tree); Assert.True(inspector.Calls > 0);
            string initialPath = Path.Combine(directory, "initial.pmc");
            var options = effects.CreateSaveOptions(project, cancellationToken: Token);
            options = seed.CreateSaveOptions(project, options with { CacheDirectory = Path.Combine(directory, "initial-save") }, Token);
            D3D9ClassicProjectStore.Save(initialPath, project, options, Token);
            File.Delete(fxPath);

            string[] runtimePaths = ["Mmd.Modern.D3D9.Compatibility.dll", "d3dx9_43.dll", "d3dcompiler_43.dll"];
            foreach (string name in runtimePaths) File.WriteAllBytes(Path.Combine(directory, name), [1]);
            var engine = new EditorClassicRenderEngine(new(Path.Combine(directory, runtimePaths[0]),
                Path.Combine(directory, runtimePaths[1]), Path.Combine(directory, runtimePaths[2])), directory);
            // Capture only file identities, then remove them: native validation/inspection must fail.
            foreach (string name in runtimePaths) File.Delete(Path.Combine(directory, name));
            using var workspace = new EditorProjectWorkspace(engine) { PackageCacheDirectory = Path.Combine(directory, "workspace-cache") };
            workspace.Open(initialPath, Token);
            var session = workspace.Session!;
            var before = Assert.IsType<EditorClassicDocumentState>(workspace.ProjectMetadata);
            var beforeSource = session.Objects[0].Source;
            var beforeModel = session.CaptureAuthoredContent().Motions[0].Model;
            var beforeMotion = session.CaptureAuthoredContent().Motions[0].Motion;
            int inspections = inspector.Calls;
            byte[] replacement = Bitmap(); replacement[54] = 200;
            string replacementPath = Path.Combine(directory, "replacement.bmp"); File.WriteAllBytes(replacementPath, replacement);
            await workspace.ApplyClassicToonAsync(id, [1, 2], replacementPath, Token);
            var after = Assert.IsType<EditorClassicDocumentState>(workspace.ProjectMetadata);
            Assert.NotSame(before, after); after.ValidateSession(session); after.Toons!.Validate(session);
            Assert.Same(before.Effects, after.Effects); Assert.Same(before.FrozenEffects, after.FrozenEffects);
            Assert.Same(before.Tree, after.Tree); Assert.Same(before.Emm, after.Emm);
            Assert.Same(beforeModel, session.CaptureAuthoredContent().Motions[0].Model);
            Assert.Same(beforeMotion, session.CaptureAuthoredContent().Motions[0].Motion);
            Assert.Equal(200, Pixel(session.Objects[0].Source, 2)); Assert.Equal(51, Pixel(beforeSource, 2));
            Assert.Equal(originalModel, File.ReadAllBytes(sourcePath)); Assert.Equal(inspections, inspector.Calls);
            Assert.True(workspace.Undo()); Assert.Same(before, workspace.ProjectMetadata); Assert.Same(beforeSource, session.Objects[0].Source);
            Assert.True(workspace.Redo()); Assert.Same(after, workspace.ProjectMetadata); after.ValidateSession(session);
            string savedPath = Path.Combine(directory, "edited.pmc"); workspace.Save(savedPath, Token);
            File.Delete(sourcePath); File.Delete(authoredImage); File.Delete(replacementPath);
            using var reopened = new EditorProjectWorkspace(engine) { PackageCacheDirectory = Path.Combine(directory, "reopened-cache") };
            reopened.Open(savedPath, Token);
            var restored = Assert.IsType<EditorClassicDocumentState>(reopened.ProjectMetadata);
            restored.ValidateSession(reopened.Session!); restored.Toons!.Validate(reopened.Session!);
            Assert.Equal(200, Pixel(reopened.Session!.Objects[0].Source, 2));
            Assert.NotNull(restored.FrozenEffects);
            Assert.Equal(fxBytes, restored.Tree!.CopyFileBytes(Assert.Single(restored.Tree.DependencyGraph!.Nodes).RelativePath));
            Assert.Equal(inspections, inspector.Calls);

            // 十槽界面的“恢复默认”：Common 槽 5 写回原版图片，单独贴图材质 2 保持替换；可撤销/重做。
            var original = new EditorClassicToonResources.Resource(5, "fixture", "toon05.bmp", Bitmap(),
                EditorTextureLoader.Decode(Bitmap(), "toon05.bmp", Token));
            await Assert.ThrowsAsync<ArgumentException>(() => workspace.RestoreClassicToonAsync(id, 4, original, Token));
            await workspace.RestoreClassicToonAsync(id, 5, original, Token);
            var reset = Assert.IsType<EditorClassicDocumentState>(workspace.ProjectMetadata);
            reset.ValidateSession(session); reset.Toons!.Validate(session);
            Assert.Same(after.Effects, reset.Effects); Assert.Same(after.Tree, reset.Tree);
            Assert.False(reset.Toons.DescribeCommonSlots(id, beforeModel)[4].Replaced);
            Assert.Equal(51, Pixel(session.Objects[0].Source, 1)); Assert.Equal(200, Pixel(session.Objects[0].Source, 2));
            Assert.Same(beforeModel, session.CaptureAuthoredContent().Motions[0].Model);
            Assert.True(workspace.Undo()); Assert.Same(after, workspace.ProjectMetadata);
            Assert.Equal(200, Pixel(session.Objects[0].Source, 1));
            Assert.True(workspace.Redo()); Assert.Same(reset, workspace.ProjectMetadata);
            Assert.Equal(51, Pixel(session.Objects[0].Source, 1));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ToonFixtureInspector : ID3D9EffectDependencyInspector
    {
        internal int Calls { get; private set; }
        public D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
            Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> captureInclude, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            var identity = new D3D9EffectDependencyCompilerIdentity("fixture", new string('a', 64), 64, 936, new string('b', 64));
            D3D9EffectDependencyProfile[] profiles =
            [new("effect-mip-off", 0x1000, [new("_INDEX", "PSIZE15")]),
                new("effect-mip-on", 0x1000, [new("_INDEX", "PSIZE15"), new("MME_MIPMAP", "")])];
            return new(D3D9EffectDependencyStatus.Complete, identity, profiles, []);
        }
    }
}
