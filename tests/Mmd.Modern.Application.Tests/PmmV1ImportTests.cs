using System.Numerics;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmV1ImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pmm-v1-import-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(root, "scene.pmm");
    public PmmV1ImportTests()
    {
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "pmm-v1-one-model.pmm"), Project);
    }
    private string Pmd(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "character.pmd");
        var model = new PmdModel("model",
            [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero, 0, 0, 1), new(Vector3.UnitX, Vector3.UnitZ, Vector2.Zero, 0, 0, 1), new(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero, 0, 0, 1)],
            [0, 1, 2], [new(Vector4.One, Vector3.Zero, Vector3.Zero, 1, "", 0, 3)], [new("bone", -1, Vector3.Zero)], 0)
        { HasAnimationTail = true, BoneDisplayFrames = [new("\u30bb\u30f3\u30bf\u30fc", [])], Morphs = [new("morph", 0, [])], IkChains = [new(0, 0, 1, .5f, [0])] };
        File.WriteAllBytes(path, PmdWriter.ToBytes(model)); return path;
    }
    [Fact]
    public void ResolvesOriginalPmdTrackMetadataIncludingBaseMorphBeforeConversion()
    {
        string asset = Pmd(root); byte[] original = File.ReadAllBytes(asset);
        var document = PmmProjectReader.Read(Project, cancellationToken: TestContext.Current.CancellationToken);
        var model = Assert.Single(document.Models);
        Assert.Equal(1, document.Version); Assert.Equal("bone", Assert.Single(model.BoneNames).Value);
        Assert.Equal("morph", Assert.Single(model.MorphNames).Value); Assert.Equal(0, Assert.Single(model.ConstraintBoneIndices));
        Assert.Equal(original, File.ReadAllBytes(asset));
        var plan = PmmProjectReader.Prepare(Project, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(plan.CanCommit); Assert.Empty(plan.Models[0].Model.Morphs);
    }
    [Fact]
    public void MissingOrAmbiguousAssetReportsStoredModelAndReference()
    {
        var missing = Assert.Throws<InvalidDataException>(() => PmmProjectReader.Read(Project, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("#7", missing.Message); Assert.Contains("character.pmd", missing.Message);
        Pmd(Path.Combine(root, "a")); Pmd(Path.Combine(root, "b"));
        var ambiguous = Assert.Throws<InvalidDataException>(() => PmmProjectReader.Read(Project, new() { SearchRoots = [root] }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Ambiguous", ambiguous.Message); Assert.Contains("#7", ambiguous.Message);
    }
    [Fact]
    public void PmxMetadataUsesOriginalBoneMorphAndIkTables()
    {
        var template = PmmMotionAdapterTests.Pmx(["bone"], ["morph"]);
        var pmx = new PmxModel
        {
            Header = template.Header, Name = template.Name, EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = [], Faces = [], Textures = [], Materials = [],
            Bones = [new() { Name = "bone", EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0,
                Flags = PmxBoneFlags.Ik, IkTargetBoneIndex = 0, IkIterationCount = 1, IkLimit = .5f, IkLinks = [new(0, false, Vector3.Zero, Vector3.Zero)] }],
            Morphs = template.Morphs, DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = []
        };
        string asset = Path.Combine(root, "character.pmx"); File.WriteAllBytes(asset, PmxWriter.ToBytes(pmx));
        var document = PmmProjectReader.Read(Project, new() { ModelPathOverrides = new Dictionary<int, string> { [7] = asset } }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("bone", Assert.Single(document.Models[0].BoneNames).Value);
        Assert.Equal("morph", Assert.Single(document.Models[0].MorphNames).Value);
        Assert.Equal(0, Assert.Single(document.Models[0].ConstraintBoneIndices));
    }
    [Fact]
    public void ExplicitResolverNullDoesNotSilentlyFallBackToDisk()
    {
        Pmd(root);
        Assert.Throws<PmmFormatException>(() => PmmProjectReader.Read(Project, readOptions: new() { V1ModelMetadataResolver = _ => null }, cancellationToken: TestContext.Current.CancellationToken));
    }
    [Fact]
    public void CancellationRaisedByExplicitMetadataCallbackPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => PmmProjectReader.Read(Project, readOptions: new()
        {
            V1ModelMetadataResolver = _ => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); return null; }
        }, cancellationToken: cancellation.Token));
    }
    [Fact]
    public void CorruptSourceModelErrorRetainsAssetAndStoredIndex()
    {
        File.WriteAllText(Path.Combine(root, "character.pmd"), "broken");
        var error = Assert.Throws<InvalidDataException>(() => PmmProjectReader.Read(Project, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("#7", error.Message); Assert.Contains("character.pmd", error.Message);
    }
    [Fact]
    public void ExplicitPathOverrideWinsOverAmbiguousSearch()
    {
        string chosen = Pmd(Path.Combine(root, "a")); Pmd(Path.Combine(root, "b"));
        var plan = PmmProjectReader.Prepare(Project, new() { SearchRoots = [root], ModelPathOverrides = new Dictionary<int, string> { [7] = chosen } }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(plan.CanCommit); Assert.Equal(chosen, Assert.Single(plan.Models).Path);
    }
    [Fact]
    public void ExplicitMetadataResolverWinsAndInputLimitsRemainEnforced()
    {
        bool invoked = false;
        var options = new PmmReadOptions { V1ModelMetadataResolver = reference => { invoked = true; Assert.Equal(7, reference.StoredModelIndex); return new(["bone"], ["morph"], [0]); } };
        Assert.Equal(1, PmmProjectReader.Read(Project, readOptions: options, cancellationToken: TestContext.Current.CancellationToken).Version); Assert.True(invoked);
        Assert.Throws<PmmFormatException>(() => PmmProjectReader.Read(Project, readOptions: new() { MaxInputBytes = 30, V1ModelMetadataResolver = options.V1ModelMetadataResolver }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<PmmFormatException>(() => PmmProjectReader.Read(Project, readOptions: new() { MaxElementCount = 0, V1ModelMetadataResolver = options.V1ModelMetadataResolver }, cancellationToken: TestContext.Current.CancellationToken));
    }
    [Fact]
    public void CancellationDoesNotProduceOrMutateCandidate()
    {
        byte[] before = File.ReadAllBytes(Project); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PmmProjectReader.Prepare(Project, cancellationToken: cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(Project));
    }
    [Fact]
    public void V1ApplicationDefaultsDoNotAlterAbsentRawFieldsOrWriterRoundtrip()
    {
        Pmd(root); var plan = PmmProjectReader.Prepare(Project, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(plan.EffectivePhysics.Enabled); Assert.True(plan.EffectivePhysics.GroundEnabled);
        Assert.Equal(new Vector3(0, -98, 0), plan.EffectivePhysics.Gravity);
        Assert.Equal(0, plan.Document.Settings.PhysicsSimulationMode); Assert.Equal(0, plan.Document.Gravity.Acceleration);
        Assert.Equal(File.ReadAllBytes(Project), PmmWriter.ToBytes(plan.Document));
        Assert.Contains(plan.Diagnostics, message => message.Contains("application defaults", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V1PlaybackUsesUnparentedCameraAndPreservesAuthoredShadowFlags(bool shared)
    {
        Pmd(root); var plan = PmmProjectReader.Prepare(Project, cancellationToken: TestContext.Current.CancellationToken); var model = plan.Models[0].Model;
        var surface = PmxOriginalStyleAdapter.FromRestPose(model).Scene;
        var outline = PmxOriginalOutlineAdapter.FromRestPose(model);
        var rest = new OriginalFrameScene(surface, outline, new(Vector4.One, Matrix4x4.Identity, new(0, 0, -10), 30, 1),
            materials: PmxOriginalFrameAdapter.FromMaterials(model));
        using var playback = new PmmProjectPlayback(plan, new Dictionary<int, OriginalFrameScene> { [7] = rest },
            (gravity, ground) => { Assert.Equal(new Vector3(0, -98, 0), gravity); Assert.Equal(0f, ground); return new TrackingSharedBackend(); }, shared);
        Assert.True(playback.PhysicsEnabled); Assert.Equal(new Vector3(4, 5, 6), playback.Frame.Surface.Snapshot.Vertices[0].Position);
        Assert.Equal(-1, playback.CameraPose.ParentModelIndex);
        Assert.Equal(-1, playback.CameraPose.ParentBoneIndex); Assert.Equal(0, plan.Document.Settings.CameraLookAtModelIndex);
        Assert.NotEmpty(playback.Frame.Materials);
        Assert.All(playback.Frame.Materials, flags => { Assert.True(flags.CastSelfShadow); Assert.True(flags.ReceiveSelfShadow); });
        playback.Seek(5); Assert.Equal(new Vector3(1, 2, 3), playback.Frame.Surface.Snapshot.Vertices[0].Position); Assert.False(playback.IsSavedPose); Assert.Equal(-1, playback.CameraPose.ParentModelIndex);
        _ = playback.Camera.ViewProjection(1280, 720);
        Assert.NotEmpty(playback.Frame.Materials);
        Assert.All(playback.Frame.Materials, flags => { Assert.True(flags.CastSelfShadow); Assert.True(flags.ReceiveSelfShadow); });
        Assert.Equal(File.ReadAllBytes(Project), PmmWriter.ToBytes(plan.Document));
    }
    [Fact]
    public void V2KeepsExplicitDisabledPhysicsAndZeroGravity()
    {
        var plan = PmmProjectImport.Prepare(new() { Version = 2 }, Project, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(plan.EffectivePhysics.Enabled); Assert.False(plan.EffectivePhysics.GroundEnabled); Assert.Equal(Vector3.Zero, plan.EffectivePhysics.Gravity);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
