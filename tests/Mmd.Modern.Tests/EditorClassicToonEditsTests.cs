using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed partial class EditorClassicToonPackageTests
{
    [Fact]
    public void CommonToonEditExpandsToEveryMaterialUsingTheSameModelSlot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-toon-common", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var model = Model([Material(PmxToonMode.Separate, -1), Material(PmxToonMode.Common, 4), Material(PmxToonMode.Common, 4)]);
            string path = Path.Combine(directory, "model.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
            var asset = Load(path, Token); Guid id = Guid.NewGuid();
            var seed = EditorClassicToonPackage.ReadPackage(new Resources(id, Bitmap(), extraCommon: true), Token)!;
            asset = seed.Apply(id, asset, path, Token);
            byte[] replacement = Bitmap(); replacement[54] = 200;
            var next = seed.Replace(id, model, [1], replacement, "chosen.bmp", Token).Apply(id, asset, path, Token);
            Assert.Equal(200, Pixel(next.Source, 1)); Assert.Equal(200, Pixel(next.Source, 2));
            Assert.Equal(51, Pixel(next.Source, 0)); Assert.Equal(51, Pixel(asset.Source, 2));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MaterialToonOverrideRetainsAuthoredModelAndUndoAndPortableBytes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mda-toon-edit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "model.pmx");
            var model = Model(); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
            byte[] original = File.ReadAllBytes(path);
            File.WriteAllBytes(Path.Combine(directory, "authored.bmp"), Bitmap());
            var asset = Load(path, Token);
            var composition = new SceneCompositionEditor();
            Guid id = composition.Add("actor", path, ModelRole.Character, asset.Source).Id;
            var seed = EditorClassicToonPackage.ReadPackage(new Resources(id, Bitmap()), Token)!;
            asset = seed.Apply(id, asset, path, Token);
            composition.ReplaceMaterialSource(id, asset.Source);
            var motion = SceneMotionAsset.FromMotion(new VmdMotion { ModelName = model.Name }, "motion.vmd");
            using var session = new SceneProjectSession(composition, new(),
                [new(id, asset.Model, motion.Motion, PhysicsEnabled: false, Asset: motion)], new NoPhysics());
            var before = session.Objects[0].Source;
            var moved = new OriginalFrameScene(new OriginalStyleScene(
                before.Surface.Snapshot.WithWorld(System.Numerics.Matrix4x4.CreateTranslation(1, 0, 0)),
                before.Surface.Materials, before.Surface.Profile), before.Outline, before.OutlineOptions,
                before.Options, before.Materials, before.Lighting, before.EditorOverlay);
            Assert.Throws<ArgumentException>(() => session.Edit(edit => edit.Composition.ReplaceMaterialSource(id, moved), Token));
            Assert.Same(before, session.Objects[0].Source);
            Assert.False(session.CanUndo);
            byte[] replacement = Bitmap(); replacement[54] = 200;
            var edited = seed.Replace(id, model, [1, 2], replacement, "chosen.bmp", Token);
            var next = edited.Apply(id, asset, path, Token);
            session.Edit(edit => edit.Composition.ReplaceMaterialSource(id, next.Source), Token);
            Assert.Same(asset.Model, session.CaptureAuthoredContent().Motions[0].Model);
            Assert.Same(motion.Motion, session.CaptureAuthoredContent().Motions[0].Motion);
            Assert.Equal(original, File.ReadAllBytes(path));
            edited.Validate(session);
            Assert.Equal(200, Pixel(session.Objects[0].Source, 1));
            Assert.Equal(200, Pixel(session.Objects[0].Source, 2));
            Assert.Equal(51, Pixel(before, 1));
            session.Undo(Token); Assert.Same(before, session.Objects[0].Source);
            session.Redo(Token); edited.Validate(session);
            string saved = Path.Combine(directory, "saved.pmc");
            PmcProjectStore.Save(saved, session, edited.CreateSaveOptions(session,
                new() { CacheDirectory = Path.Combine(directory, "save") }, Token), Token);
            File.Delete(path); File.Delete(Path.Combine(directory, "authored.bmp"));
            EditorClassicToonPackage? frozen = null; string? loadedPath = null;
            using var reopened = PmcProjectStore.Load(saved, Path.Combine(directory, "load"),
                (source, token) => { loadedPath = source; return Load(source, token); }, new NoPhysics(), new()
                {
                    PackagePreflight = (context, token) => frozen = EditorClassicToonPackage.ReadPackage(context, token),
                    ModelAssetTransform = (context, value) => frozen!.Apply(context.ObjectId, value, loadedPath!, Token)
                }, Token);
            frozen!.Validate(reopened.Session);
            Assert.Equal(200, Pixel(reopened.Session.Objects[0].Source, 2));
            Assert.Throws<ArgumentException>(() => edited.Replace(id, model, [-1], replacement, "bad.bmp", Token));
            Assert.ThrowsAny<Exception>(() => edited.Replace(id, model, [1], [0, 1], "bad.bmp", Token));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => edited.Replace(id, model, [1], replacement, "chosen.bmp", cancelled.Token));
            edited.Validate(session);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CommonSlotRestoreClearsOverrideForEveryMaterialInThatSlotAndSurvivesPmc()
    {
        // 十槽界面的包层：查询 Toon01–10 状态，替换后再恢复为原版图片，同槽材质一起变化。
        string directory = Path.Combine(MdaWorkRoot.Path("classic/20261117/toon-slots"), "package", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var model = Model([Material(PmxToonMode.Separate, -1), Material(PmxToonMode.Common, 4), Material(PmxToonMode.Common, 4)]);
            string path = Path.Combine(directory, "model.pmx"); File.WriteAllBytes(path, PmxWriter.ToBytes(model));
            File.WriteAllBytes(Path.Combine(directory, "authored.bmp"), Bitmap());
            var asset = Load(path, Token);
            var composition = new SceneCompositionEditor();
            Guid id = composition.Add("actor", path, ModelRole.Character, asset.Source).Id;
            var seed = EditorClassicToonPackage.ReadPackage(new Resources(id, Bitmap(), extraCommon: true), Token)!;
            asset = seed.Apply(id, asset, path, Token);
            composition.ReplaceMaterialSource(id, asset.Source);
            var motion = SceneMotionAsset.FromMotion(new VmdMotion { ModelName = model.Name }, "motion.vmd");
            using var session = new SceneProjectSession(composition, new(),
                [new(id, asset.Model, motion.Motion, PhysicsEnabled: false, Asset: motion)], new NoPhysics());

            var slots = seed.DescribeCommonSlots(id, model);
            Assert.Equal(Enumerable.Range(1, 10), slots.Select(value => value.Slot));
            Assert.Equal(new[] { 1, 2 }, slots[4].MaterialIndices); Assert.False(slots[4].Replaced); Assert.NotNull(slots[4].Image);
            Assert.All(slots.Where(value => value.Slot != 5), value => { Assert.False(value.Used); Assert.Null(value.Image); });

            byte[] replacement = Bitmap(); replacement[54] = 200;
            var replaced = seed.Replace(id, model, [1], replacement, "chosen.bmp", Token);
            Assert.True(replaced.DescribeCommonSlots(id, model)[4].Replaced);
            Assert.Equal(200, replaced.DescribeCommonSlots(id, model)[4].Image!.Rgba[2]);
            Assert.Throws<ArgumentException>(() => replaced.RestoreCommon(id, model, 1, Bitmap(), "toon01.bmp", Token));
            Assert.Throws<ArgumentOutOfRangeException>(() => replaced.RestoreCommon(id, model, 11, Bitmap(), "toon11.bmp", Token));

            var restored = replaced.RestoreCommon(id, model, 5, Bitmap(), "toon05.bmp", Token);
            var state = restored.DescribeCommonSlots(id, model)[4];
            Assert.False(state.Replaced); Assert.Equal(51, state.Image!.Rgba[2]);
            var next = restored.Apply(id, asset, path, Token);
            Assert.Equal(51, Pixel(next.Source, 1)); Assert.Equal(51, Pixel(next.Source, 2)); Assert.Equal(51, Pixel(next.Source, 0));
            session.Edit(edit => edit.Composition.ReplaceMaterialSource(id, next.Source), Token);
            restored.Validate(session);

            string saved = Path.Combine(directory, "restored.pmc");
            PmcProjectStore.Save(saved, session, restored.CreateSaveOptions(session,
                new() { CacheDirectory = Path.Combine(directory, "save") }, Token), Token);
            EditorClassicToonPackage? frozen = null; string? loadedPath = null;
            using var reopened = PmcProjectStore.Load(saved, Path.Combine(directory, "load"),
                (source, token) => { loadedPath = source; return Load(source, token); }, new NoPhysics(), new()
                {
                    PackagePreflight = (context, token) => frozen = EditorClassicToonPackage.ReadPackage(context, token),
                    ModelAssetTransform = (context, value) => frozen!.Apply(context.ObjectId, value, loadedPath!, Token)
                }, Token);
            frozen!.Validate(reopened.Session);
            Assert.False(frozen.DescribeCommonSlots(id, model)[4].Replaced);
            Assert.Equal(51, Pixel(reopened.Session.Objects[0].Source, 2));
        }
        finally { Directory.Delete(directory, true); }
    }

    // 十槽窗口测试用：材质 0 用默认 Toon，材质 1、2 共用 Common 槽 5。
    internal static PmxModel SlotModel()
        => Model([Material(PmxToonMode.Separate, -1), Material(PmxToonMode.Common, 4), Material(PmxToonMode.Common, 4)]);
    internal static byte[] SlotBitmap(byte blue) { byte[] bytes = Bitmap(); bytes[54] = blue; return bytes; }
    internal static EditorClassicToonPackage SlotPackage(IReadOnlyList<Guid> ids)
        => EditorClassicToonPackage.ReadPackage(new SlotResources(ids, Bitmap()), Token)!;

    private sealed class SlotResources : IPmaPackageResources
    {
        private readonly byte[] bytes;
        public SlotResources(IReadOnlyList<Guid> ids, byte[] bytes)
        {
            this.bytes = bytes;
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            string path = EditorClassicToonPackage.ResourcePrefix + sha.ToLowerInvariant() + ".bin";
            ResourcePaths = [path];
            OptionalExtensions = new Dictionary<string, System.Text.Json.JsonElement>
            {
                [EditorClassicToonPackage.ExtensionName] = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    schemaVersion = 2,
                    models = ids.Select(id => new { objectId = id, materials = new[]
                    {
                        new { materialIndex = 0, slot = 0, imageSha256 = sha }, new { materialIndex = 1, slot = 5, imageSha256 = sha },
                        new { materialIndex = 2, slot = 5, imageSha256 = sha }
                    } }).ToArray(),
                    images = new[] { new { sha256 = sha, authoredToken = "toon05.bmp", resourcePath = path } }
                })
            };
        }
        public string Format => PmcProjectStore.Format;
        public Guid ProjectId => Guid.Empty;
        public IReadOnlyDictionary<string, System.Text.Json.JsonElement> OptionalExtensions { get; }
        public IReadOnlyList<string> ResourcePaths { get; }
        public byte[] ReadResource(string path, long maxBytes = long.MaxValue) => bytes.ToArray();
    }

    private static byte Pixel(OriginalFrameScene source, int material)
        => source.Surface.Snapshot.Textures[source.Surface.Snapshot.Materials[material].ToonTexture].Rgba[2];
}
