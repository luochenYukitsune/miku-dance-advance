using System.Text;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

public sealed class D3D9EffectDependencyCollectorTests
{
    private const string Main = @"C:\fx\main.fx";

    [Fact]
    public void KeepsBothProfilesIncludeRulesAndAnnotationTopFxBase()
    {
        var source = Source((Main, "main"), (@"C:\fx\nested\one.inc", "one"),
            (@"C:\fx\nested\two.inc", "two"), (@"C:\fx\system.inc", "system"), (@"C:\fx\image.png", "image"));
        var inspector = new Inspector((top, include) =>
        {
            include(new("effect-mip-off", false, null, 1, Ref(@"nested\one.inc")));
            include(new("effect-mip-off", false, @"C:\fx\nested\one.inc", 2, Ref("two.inc")));
            include(new("effect-mip-on", true, @"C:\fx\nested\one.inc", 2, Ref("system.inc")));
            return Inspection(new D3D9EffectDependencySemanticReference("effect-mip-on", D3D9EffectDependencyReferenceKind.Resource,
                Ref("image.png"), "unusedTexture", "ResourceName"));
        });
        var result = new D3D9EffectDependencyCollector(source, inspector).Collect([new(Main)], TestContext.Current.CancellationToken);

        Assert.Equal(D3D9EffectDependencyStatus.Complete, result.Status);
        Assert.Equal(5, result.Closure!.Nodes.Count);
        Assert.Equal(5, result.Closure.Edges.Count);
        Assert.Equal(@"C:\fx\main.fx", source.Requests.Last().BaseOriginAbsolutePath);
        Assert.Contains(result.Closure.Edges, e => e.Kind == D3D9EffectDependencyEdgeKind.IncludeSystem && e.ProfileId == "effect-mip-on");
    }

    [Fact]
    public void FollowsAllEffectRowsTerminatesCyclesAndPreservesEveryOwner()
    {
        var source = Source((Main, "main"), (@"D:\external\next.fx", "next"));
        var inspector = new Inspector((top, _) => Inspection(new D3D9EffectDependencySemanticReference("effect-mip-off", D3D9EffectDependencyReferenceKind.Effect,
            Ref(top.OriginAbsolutePath == Main ? @"D:\external\next.fx" : Main), "rt", "DefaultEffect", 7, "*", Ref(" *= original row "))));
        var owners = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var result = new D3D9EffectDependencyCollector(source, inspector).Collect(owners.Select(id =>
            new D3D9EffectDependencyRoot(Main, new(id, 3, "Effect@Hidden", 9))), TestContext.Current.CancellationToken);

        Assert.Equal(D3D9EffectDependencyStatus.Complete, result.Status);
        Assert.Equal(2, inspector.Calls);
        Assert.Equal(2, result.Closure!.Nodes.Count);
        Assert.Equal(6, result.Closure.Edges.Count);
        Assert.Equal(owners[1], result.Closure.Roots[1].Provenance.OwnerId);
        Assert.Equal(new[] { 0, 1 }, result.Closure.Edges.Where(e => e.RowOrdinal == 7).Select(e => e.RootIndex).Distinct());
        Assert.All(result.Closure.Edges.Where(e => e.RowOrdinal == 7), e => Assert.Equal(" *= original row ", Text(e.RawRow!)));
    }

    [Fact]
    public void KeepsDistinctOriginsWithIdenticalBytesAndCopiesFrozenInputs()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var snapshot = new D3D9EffectDependencySnapshot(Main, bytes);
        bytes[0] = 7;
        snapshot.CopyBytes()[1] = 8;
        Assert.Equal(new byte[] { 1, 2, 3 }, snapshot.CopyBytes());
        var source = Source((Main, "same"), (@"D:\other.fx", "same"));
        var result = new D3D9EffectDependencyCollector(source, new Inspector((_, _) => Inspection())).Collect([new(Main), new(@"D:\other.fx")], TestContext.Current.CancellationToken);

        Assert.Equal(D3D9EffectDependencyStatus.Complete, result.Status);
        Assert.Equal(2, result.Closure!.Nodes.Select(n => n.ResourceId).Distinct().Count());
        Assert.Single(result.Closure.Nodes.Select(n => n.Snapshot.Sha256).Distinct());
    }

    [Fact]
    public void SymbolsNeverOpenFilesAndFunctionRequiresEmptyMacroCompilationProfile()
    {
        var source = Source((Main, "main"));
        var profiles = Profiles().Append(new D3D9EffectDependencyProfile("function:make:tx_1_0", 0, [], "make", "tx_1_0"));
        var inspector = new Inspector((_, _) => new(D3D9EffectDependencyStatus.Complete, Identity, profiles,
        [new("effect-mip-off", D3D9EffectDependencyReferenceKind.Symbol, Ref("main_default"), "rt", "DefaultEffect", Symbol: D3D9EffectDependencySymbol.MainDefault),
         new("effect-mip-on", D3D9EffectDependencyReferenceKind.TextureFunction, Ref("make"), "generated", "Function", FunctionEntryPoint: "make", FunctionTarget: "tx_1_0")]));
        var result = new D3D9EffectDependencyCollector(source, inspector).Collect([new(Main)], TestContext.Current.CancellationToken);

        Assert.Equal(D3D9EffectDependencyStatus.Complete, result.Status);
        Assert.Single(source.Requests);
        Assert.Contains(result.Closure!.Edges, e => e.Symbol == D3D9EffectDependencySymbol.MainDefault && e.TargetResourceId == null);
        Assert.Contains(result.Closure.Edges, e => e.Kind == D3D9EffectDependencyEdgeKind.TextureFunction && e.FunctionEntryPoint == "make");
    }

    [Fact]
    public void MissingSourceReturnsChainAndNoPartialClosure()
    {
        var source = Source((Main, "main"));
        var inspector = new Inspector((_, _) => Inspection(new D3D9EffectDependencySemanticReference("effect-mip-on", D3D9EffectDependencyReferenceKind.Resource,
            Ref("missing.png"), "texture", "ResourceName")));
        var result = new D3D9EffectDependencyCollector(source, inspector).Collect([new(Main, new(Guid.NewGuid(), 2))], TestContext.Current.CancellationToken);

        Assert.Equal(D3D9EffectDependencyStatus.Incomplete, result.Status);
        Assert.Null(result.Closure);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("missing", diagnostic.Code);
        Assert.Equal(Main, diagnostic.TopFxOriginAbsolutePath);
        Assert.Equal("effect-mip-on", diagnostic.ProfileId);
        Assert.Equal("texture", diagnostic.Parameter);
        Assert.Equal("missing.png", Text(diagnostic.Reference!));
    }

    [Fact]
    public void SourceMutationBetweenRequestsFailsRatherThanChoosingOneSnapshot()
    {
        var source = Source((Main, "main"), (@"C:\fx\one.inc", "first"));
        var inspector = new Inspector((_, include) =>
        {
            include(new("effect-mip-off", false, null, 1, Ref("one.inc")));
            source.Files[@"C:\fx\one.inc"] = Encoding.UTF8.GetBytes("changed");
            include(new("effect-mip-on", false, null, 1, Ref("one.inc")));
            return Inspection();
        });
        var result = new D3D9EffectDependencyCollector(source, inspector).Collect([new(Main)], TestContext.Current.CancellationToken);

        Assert.Equal(D3D9EffectDependencyStatus.Incomplete, result.Status);
        Assert.Null(result.Closure);
        Assert.Equal("file-changed", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void CancellationNeverReturnsCollectedNodes()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var inspector = new Inspector((_, _) => { cancellation.Cancel(); return Inspection(); });
        var result = new D3D9EffectDependencyCollector(Source((Main, "main")), inspector).Collect([new(Main)], cancellation.Token);
        Assert.Equal(D3D9EffectDependencyStatus.Cancelled, result.Status);
        Assert.Null(result.Closure);
    }

    [Fact]
    public void CompilerFailureAfterSuccessfulIncludeReturnsNoPartialClosure()
    {
        var inspector = new Inspector((_, include) =>
        {
            include(new("effect-mip-off", false, null, 1, Ref("one.inc")));
            return new(D3D9EffectDependencyStatus.Incomplete, Identity, Profiles(), [], [new("compile", "Rejected effect.", "effect-mip-on")]);
        });
        var result = new D3D9EffectDependencyCollector(Source((Main, "main"), (@"C:\fx\one.inc", "one")), inspector).Collect([new(Main)], TestContext.Current.CancellationToken);
        Assert.Equal(D3D9EffectDependencyStatus.Incomplete, result.Status);
        Assert.Null(result.Closure);
        Assert.Equal("compile", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void IncludeDepthAndFileBudgetAreHardFailures()
    {
        var source = Source((Main, "main"), (@"C:\fx\one.inc", "one"));
        var inspector = new Inspector((_, include) => { include(new("effect-mip-off", false, null, 2, Ref("one.inc"))); return Inspection(); });
        var result = new D3D9EffectDependencyCollector(source, inspector, new(MaximumIncludeDepth: 1)).Collect([new(Main)], TestContext.Current.CancellationToken);
        Assert.Null(result.Closure);
        Assert.Equal("include-depth-budget", Assert.Single(result.Diagnostics).Code);
        result = new D3D9EffectDependencyCollector(source, inspector, new(MaximumFileBytes: 1)).Collect([new(Main)], TestContext.Current.CancellationToken);
        Assert.Null(result.Closure);
        Assert.Equal("file-byte-budget", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void MissingRequiredMainProfileAndUncompiledFunctionCannotBeComplete()
    {
        var source = Source((Main, "main"));
        var inspector = new Inspector((_, _) => new(D3D9EffectDependencyStatus.Complete, Identity, Profiles().Take(1), []));
        var result = new D3D9EffectDependencyCollector(source, inspector).Collect([new(Main)], TestContext.Current.CancellationToken);
        Assert.Equal(D3D9EffectDependencyStatus.Unsupported, result.Status);
        Assert.Null(result.Closure);
        inspector = new((_, _) => Inspection(new D3D9EffectDependencySemanticReference("effect-mip-off", D3D9EffectDependencyReferenceKind.TextureFunction,
            Ref("make"), "generated", "Function", FunctionEntryPoint: "make", FunctionTarget: "tx_1_0")));
        result = new D3D9EffectDependencyCollector(source, inspector).Collect([new(Main)], TestContext.Current.CancellationToken);
        Assert.Equal(D3D9EffectDependencyStatus.Unsupported, result.Status);
        Assert.Null(result.Closure);
    }

    private static readonly D3D9EffectDependencyCompilerIdentity Identity = new("test-host-v1", new string('a', 64), 64, 932);
    private static D3D9EffectDependencyProfile[] Profiles() =>
    [new("effect-mip-off", 0x1000, [new("_INDEX", "PSIZE15")]),
     new("effect-mip-on", 0x1000, [new("_INDEX", "PSIZE15"), new("MME_MIPMAP", "")])];
    private static D3D9EffectDependencyInspection Inspection(params D3D9EffectDependencySemanticReference[] references) =>
        new(D3D9EffectDependencyStatus.Complete, Identity, Profiles(), references);
    private static D3D9EffectDependencyReference Ref(string text) => new(Encoding.UTF8.GetBytes(text), 65001);
    private static string Text(D3D9EffectDependencyReference reference) => Encoding.UTF8.GetString(reference.CopyBytes());
    private static MemorySource Source(params (string Path, string Text)[] files) => new(files);

    private sealed class Inspector(Func<D3D9EffectDependencySnapshot,
        Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot>, D3D9EffectDependencyInspection> inspect) : ID3D9EffectDependencyInspector
    {
        public int Calls { get; private set; }
        public D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
            Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> captureInclude, CancellationToken cancellationToken)
        { Calls++; return inspect(topFx, captureInclude); }
    }

    private sealed class MemorySource : ID3D9EffectDependencySource
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<D3D9EffectDependencySourceRequest> Requests { get; } = [];
        public MemorySource(IEnumerable<(string Path, string Text)> files)
        { foreach (var file in files) Files.Add(file.Path, Encoding.UTF8.GetBytes(file.Text)); }
        public D3D9EffectDependencySnapshot Capture(D3D9EffectDependencySourceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
            var raw = Text(request.Reference);
            var baseOrigin = request.BaseOriginAbsolutePath ?? "";
            var path = raw.Length >= 3 && raw[1] == ':' ? raw : baseOrigin[..(baseOrigin.LastIndexOf('\\') + 1)] + raw;
            path = D3D9EffectDependencySnapshot.CanonicalizeOrigin(path);
            if (!Files.TryGetValue(path, out var bytes)) throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Incomplete, "missing", "Missing original resource.", path);
            return new(path, bytes);
        }
    }
}
