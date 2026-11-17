using System.Text;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

/// <summary>Detached assignment preparation with frozen bytes and a CPU-only compiler fixture.</summary>
public sealed class ClassicAssignmentPreparationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MdaClassicAssignmentTests", Guid.NewGuid().ToString("N"));
    private static readonly Guid First = new("840bd932-73c1-4da2-a2dd-e8956907d101");
    private static readonly Guid Second = new("840bd932-73c1-4da2-a2dd-e8956907d102");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Minimum = "[Info]\nVersion=3\n[Object]\nPmd0=actor.pmx\nPmd1=actor.pmx\n[Effect]\nDefault=none\n";

    [Fact]
    public void VisibilityEditAfterOriginalDirectoryMovesUsesFrozenDependenciesWithoutInspection()
    {
        var original = Capture();
        var oldTree = original.Tree!;
        byte[] oldDocument = original.Emm.Document.CopySourceBytes();
        Directory.Move(Originals, Originals + ".moved");
        var inspector = new Inspector { RejectCalls = true };
        var document = EmmDocumentEditor.SetShown(original.Emm.Document, "Effect", "Pmd1", 0, false);

        var result = original.PrepareAssignments(document, inspector, materialCounts: Counts(), cancellationToken: Token);

        Assert.Equal(D3D9EffectDependencyStatus.Complete, result.Status);
        var prepared = Assert.IsType<D3D9ClassicEffectsPreparation>(result.Preparation);
        Assert.Equal(0, inspector.Calls);
        Assert.Equal(Second, prepared.Emm.ImportPlan.MatchesByRecordKey["Pmd1"].ObjectId);
        Assert.False(Assert.Single(prepared.Emm.Document.EffectSections).Entries.Single(entry => entry.ObjectKey == "Pmd1").SubsetShown[0]);
        AssertFrozenOldBytes(prepared);
        Assert.Same(oldTree, original.Tree);
        Assert.Equal(oldDocument, original.Emm.Document.CopySourceBytes());
        Assert.False(Directory.Exists(Originals));
    }

    [Fact]
    public void NewAssignmentCapturesFullClosureWhileRetainingUnavailableOldRootAndDuplicateGuidBindings()
    {
        var original = Capture();
        Directory.Move(Originals, Originals + ".moved");
        string newDirectory = Path.Combine(directory, "new");
        Directory.CreateDirectory(newDirectory);
        string newFx = Write(newDirectory, "new.fx", "new effect bytes");
        string include = Write(newDirectory, "new.fxh", "included bytes");
        string resource = Write(newDirectory, "new.bin", "resource bytes");
        var inspector = new Inspector { IncludeNewDependencies = true };
        var document = EmmDocumentEditor.SetEffect(original.Emm.Document, "Effect", "Pmd1", 0, newFx);

        var result = original.PrepareAssignments(document, inspector, materialCounts: Counts(), cancellationToken: Token);

        Assert.Equal(D3D9EffectDependencyStatus.Complete, result.Status);
        var prepared = Assert.IsType<D3D9ClassicEffectsPreparation>(result.Preparation);
        Assert.Equal(1, inspector.Calls);
        Assert.Equal(new[] { Path.GetFullPath(newFx) }, inspector.InspectedPaths);
        Assert.Equal(First, prepared.Emm.ImportPlan.MatchesByRecordKey["Pmd0"].ObjectId);
        Assert.Equal(Second, prepared.Emm.ImportPlan.MatchesByRecordKey["Pmd1"].ObjectId);
        Assert.Equal(original.Emm.ImportPlan.Matches, prepared.Emm.ImportPlan.Matches);
        AssertFrozenOldBytes(prepared);
        AssertBytes(prepared, newFx, "new effect bytes");
        AssertBytes(prepared, include, "included bytes");
        AssertBytes(prepared, resource, "resource bytes");
        Assert.Equal(5, prepared.Tree!.DependencyGraph!.Nodes.Count);
        Assert.Equal(3, prepared.FrozenEffects.Roots.Count);
        Assert.Equal(Second, prepared.FrozenEffects.Origins.Single(origin => origin.Kind == D3D9EffectProjectRootKind.EmmSubset).ObjectId);
        Directory.Move(newDirectory, newDirectory + ".moved");
        AssertBytes(prepared, include, "included bytes");
        AssertBytes(prepared, resource, "resource bytes");
        AssertFrozenOldBytes(original);
        Assert.DoesNotContain(original.Emm.Document.EffectSections.Single().Entries, entry => entry.SubsetPaths.ContainsKey(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingNewRootOrDependencyExposesNoPartialPreparationAndKeepsOriginalUsable(bool missingDependency)
    {
        var original = Capture();
        var oldTree = original.Tree;
        var oldFrozen = original.FrozenEffects;
        byte[] oldDocument = original.Emm.Document.CopySourceBytes();
        Directory.Move(Originals, Originals + ".moved");
        string newDirectory = Path.Combine(directory, "new");
        Directory.CreateDirectory(newDirectory);
        string newFx = Path.Combine(newDirectory, "new.fx");
        if (missingDependency)
        {
            Write(newDirectory, "new.fx", "new effect bytes");
            Write(newDirectory, "new.fxh", "included bytes");
            // new.bin is deliberately absent after the top FX and include have been captured.
        }
        var document = EmmDocumentEditor.SetEffect(original.Emm.Document, "Effect", "Pmd1", 0, newFx);

        var result = original.PrepareAssignments(document, new Inspector { IncludeNewDependencies = missingDependency },
            materialCounts: Counts(), cancellationToken: Token);

        Assert.Equal(D3D9EffectDependencyStatus.Incomplete, result.Status);
        Assert.Null(result.Preparation);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Same(oldTree, original.Tree);
        Assert.Same(oldFrozen, original.FrozenEffects);
        Assert.Equal(oldDocument, original.Emm.Document.CopySourceBytes());
        AssertFrozenOldBytes(original);
        var visibility = EmmDocumentEditor.SetShown(original.Emm.Document, "Effect", "Pmd0", -1, false);
        var retry = original.PrepareAssignments(visibility, new Inspector { RejectCalls = true }, materialCounts: Counts(), cancellationToken: Token);
        Assert.Equal(D3D9EffectDependencyStatus.Complete, retry.Status);
        AssertFrozenOldBytes(Assert.IsType<D3D9ClassicEffectsPreparation>(retry.Preparation));
    }

    [Fact]
    public void ExplicitReloadRequiresOriginalsAndFailureDoesNotReplaceFrozenPreparation()
    {
        var original = Capture();
        Directory.Move(Originals, Originals + ".moved");
        byte[] bytes = original.Emm.Document.CopySourceBytes();

        var result = original.PrepareAssignments(original.Emm.Document, new Inspector(), reloadAll: true,
            materialCounts: Counts(), cancellationToken: Token);

        Assert.Equal(D3D9EffectDependencyStatus.Incomplete, result.Status);
        Assert.Null(result.Preparation);
        Assert.Equal(bytes, original.Emm.Document.CopySourceBytes());
        AssertFrozenOldBytes(original);
    }

    [Fact]
    public void WithoutEffectsProducesEmptyRuntimeWithoutChangingAuthoredMetadataOrFrozenResources()
    {
        const string extra = "Pmd0.show=false\n[Effect@Depth]\nOwner=Pmd1[0]\nPmd1[0]=none\n; authored comment\nFuture=value\n";
        var original = Capture(extra);
        byte[] originalBytes = original.Emm.Document.CopySourceBytes();
        var oldTree = original.Tree;
        var oldFrozen = original.FrozenEffects;
        Directory.Move(Originals, Originals + ".moved");

        var bypass = original.WithoutEffects();

        Assert.Null(bypass.Tree);
        Assert.Empty(bypass.FrozenEffects.Roots);
        Assert.Empty(bypass.FrozenEffects.Origins);
        Assert.Equal(2, bypass.FrozenEffects.RootSeeds.Count);
        Assert.All(bypass.FrozenEffects.RootSeeds, seed => Assert.Null(seed.OriginAbsoluteFxPath));
        Assert.Equal(original.Emm.ImportPlan.Matches, bypass.Emm.ImportPlan.Matches);
        Assert.Equal(original.Emm.Document.Objects.Select(obj => (obj.Key, obj.Path)), bypass.Emm.Document.Objects.Select(obj => (obj.Key, obj.Path)));
        var configuration = D3D9EmmRuntimeConfiguration.Create(bypass.FrozenEffects.ImportPlan,
            bypass.FrozenEffects.RootSeeds, new D3D9EffectResourceMap([]));
        Assert.All(configuration.Objects, obj => Assert.Equal(D3D9EmmRuntimeEffectKind.None, obj.RootSeed.Kind));
        var section = Assert.Single(configuration.Sections);
        Assert.Equal(D3D9EmmRuntimeEffectKind.None, section.DefaultEffect!.Kind);
        Assert.Empty(section.Entries);
        Assert.Equal(originalBytes, original.Emm.Document.CopySourceBytes());
        Assert.Same(oldTree, original.Tree);
        Assert.Same(oldFrozen, original.FrozenEffects);
        Assert.Equal(2, original.Emm.Document.EffectSections.Count);
        AssertFrozenOldBytes(original);
    }

    private string Originals => Path.Combine(directory, "originals");

    private D3D9ClassicEffectsPreparation Capture(string extra = "")
    {
        Directory.CreateDirectory(Originals);
        string oldFx = Write(Originals, "old.fx", "retained effect bytes");
        Write(Originals, "old.fxh", "retained include bytes");
        var document = EmmReader.Read(Encoding.ASCII.GetBytes(Minimum + extra));
        var plan = EmmImportPlan.Create(document, Path.Combine(Originals, "project.emm"),
            [new(First, true, "actor.pmx", 0), new(Second, true, "actor.pmx", 1)], Originals);
        var result = D3D9ClassicEffectsPreparation.Capture(plan, _ => oldFx, new Inspector(), cancellationToken: Token);
        Assert.Equal(D3D9EffectDependencyStatus.Complete, result.Status);
        return Assert.IsType<D3D9ClassicEffectsPreparation>(result.Preparation);
    }

    private void AssertFrozenOldBytes(D3D9ClassicEffectsPreparation preparation)
    {
        AssertBytes(preparation, Path.Combine(Originals, "old.fx"), "retained effect bytes");
        AssertBytes(preparation, Path.Combine(Originals, "old.fxh"), "retained include bytes");
    }

    private static void AssertBytes(D3D9ClassicEffectsPreparation preparation, string origin, string expected)
    {
        var node = Assert.Single(preparation.Tree!.DependencyGraph!.Nodes, value => value.OriginAbsolutePath == Path.GetFullPath(origin));
        Assert.Equal(Encoding.ASCII.GetBytes(expected), preparation.Tree.CopyFileBytes(node.RelativePath));
    }

    private static string Write(string parent, string name, string text)
    {
        string path = Path.Combine(parent, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(text));
        return path;
    }

    private static IReadOnlyDictionary<Guid, int> Counts() => new Dictionary<Guid, int> { [First] = 1, [Second] = 1 };

    private sealed class Inspector : ID3D9EffectDependencyInspector
    {
        internal bool RejectCalls { get; init; }
        internal bool IncludeNewDependencies { get; init; }
        internal int Calls { get; private set; }
        internal List<string> InspectedPaths { get; } = [];

        public D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
            Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> captureInclude, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            if (RejectCalls) throw new InvalidOperationException("Retained roots must use captured dependencies without inspection.");
            InspectedPaths.Add(topFx.OriginAbsolutePath);
            bool isOld = Path.GetFileName(topFx.OriginAbsolutePath) == "old.fx";
            if (isOld || IncludeNewDependencies)
                captureInclude(new("effect-mip-off", false, null, 1, Reference(isOld ? "old.fxh" : "new.fxh")));
            D3D9EffectDependencySemanticReference[] references = !isOld && IncludeNewDependencies
                ? [new("effect-mip-off", D3D9EffectDependencyReferenceKind.Resource, Reference("new.bin"), "Texture", "ResourceName")]
                : [];
            return new(D3D9EffectDependencyStatus.Complete,
                new("fixture", new string('a', 64), 64, 936, new string('b', 64)),
                [new("effect-mip-off", 0x1000, [new("_INDEX", "PSIZE15")]),
                    new("effect-mip-on", 0x1000, [new("_INDEX", "PSIZE15"), new("MME_MIPMAP", "")])], references);
        }

        private static D3D9EffectDependencyReference Reference(string value) => new(Encoding.ASCII.GetBytes(value), 936);
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(directory);
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MdaClassicAssignmentTests")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its temporary directory.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
