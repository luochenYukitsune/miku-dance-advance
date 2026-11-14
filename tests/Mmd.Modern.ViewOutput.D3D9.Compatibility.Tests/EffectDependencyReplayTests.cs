using System.Text;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.ViewOutput.D3D9.Compatibility.Tests;

public sealed class EffectDependencyReplayTests
{
    private const string Main = @"C:\unavailable-source\main.fx";
    private const string Child = @"C:\unavailable-source\child\child.fx";
    private const string Local = @"C:\unavailable-source\階層\local.inc";
    private const string Leaf = @"C:\unavailable-source\階層\leaf.inc";
    private const string System = @"C:\unavailable-source\root-system.inc";
    private const string Texture = @"C:\unavailable-source\texture.bin";
    private const string FunctionProfile = "function:make:tx_1_0";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly D3D9EffectDependencyCompilerIdentity Identity =
        new("fixed-test-host", new string('a', 64), 64, 932);

    [Fact]
    public void NestedLocalAndSystemIncludesUsePrivatePathsAndExactFrozenBytes()
    {
        var fixture = new Fixture();
        var inspector = fixture.ReplayInspector();

        var result = D3D9EffectDependencyReplay.Validate(fixture.Tree, fixture.Map, inspector, cancellationToken: Token);

        Assert.Equal(D3D9EffectDependencyReplayStatus.Accepted, result.Status);
        Assert.True(result.Accepted);
        Assert.Equal(2, result.InspectedFxCount);
        Assert.Equal(new[] { Main, Child }, inspector.Calls);
        Assert.Equal(new[] { Local, Leaf, System }, inspector.Includes);
        Assert.Empty(result.Diagnostics);
        // Every inspector assertion checks the materialized identity and the frozen payload. The
        // fixture creates no files at these private names and replay cannot call the original source.
        Assert.Equal(fixture.SourceReadsAfterCollection, fixture.Source.Reads);
    }

    [Fact]
    public void SharedOwnersCompileEachFxOnceAndRetainEveryRootOnFailure()
    {
        var fixture = new Fixture(sharedOwners: true);
        var acceptedInspector = fixture.ReplayInspector();
        var accepted = D3D9EffectDependencyReplay.Validate(fixture.Tree, fixture.Map, acceptedInspector, cancellationToken: Token);
        Assert.True(accepted.Accepted);
        Assert.Equal(2, accepted.InspectedFxCount);
        Assert.Equal(new[] { Main, Child }, acceptedInspector.Calls);

        var driftInspector = fixture.ReplayInspector();
        driftInspector.ChangeReferences = (origin, references) => origin == Main
            ? Change(references, 0, reference => reference with { Reference = Ref(@".\texture.bin") }) : references;
        var rejected = D3D9EffectDependencyReplay.Validate(fixture.Tree, fixture.Map, driftInspector, cancellationToken: Token);

        AssertRejected(rejected.Status, rejected.Accepted, rejected.Diagnostics, "replay-request-drift");
        Assert.Equal(new int?[] { 0, 1 }, rejected.Diagnostics.Where(diagnostic => diagnostic.Code == "replay-request-drift")
            .Select(diagnostic => diagnostic.RootIndex).Distinct().Order());
        Assert.Equal(1, driftInspector.Calls.Count(origin => origin == Main));
    }

    [Fact]
    public void ChangedRawResourceReferenceRejectsEvenWhenCanonicalTargetIsUnchanged()
    {
        var fixture = new Fixture();
        var inspector = fixture.ReplayInspector();
        inspector.ChangeReferences = (origin, references) => origin == Main
            ? Change(references, 0, reference => reference with { Reference = Ref(@".\texture.bin") }) : references;

        var result = D3D9EffectDependencyReplay.Validate(fixture.Tree, fixture.Map, inspector, cancellationToken: Token);

        AssertRejected(result.Status, result.Accepted, result.Diagnostics, "replay-request-drift");
        Assert.Equal(fixture.SourceReadsAfterCollection, fixture.Source.Reads);
    }

    [Theory]
    [InlineData(Local)]
    [InlineData(Texture)]
    public void MissingIncludeOrResourceMappingRejectsWithoutOriginalFallback(string missingOrigin)
    {
        var fixture = new Fixture();
        var incompleteMap = new D3D9EffectResourceMap(fixture.Map.Bindings.Where(binding => binding.OriginAbsolutePath != missingOrigin));
        var inspector = fixture.ReplayInspector();

        var result = D3D9EffectDependencyReplay.Validate(fixture.Tree, incompleteMap, inspector, cancellationToken: Token);

        AssertRejected(result.Status, result.Accepted, result.Diagnostics, "replay-map-miss");
        Assert.Equal(fixture.SourceReadsAfterCollection, fixture.Source.Reads);
    }

    [Theory]
    [InlineData("main-flags")]
    [InlineData("unexpected-function")]
    [InlineData("missing-function")]
    public void ProfilesMustMatchEachFxRatherThanOnlyTheGlobalProfileUnion(string drift)
    {
        var fixture = new Fixture();
        var inspector = fixture.ReplayInspector();
        inspector.ChangeProfiles = (origin, profiles) => drift switch
        {
            "main-flags" when origin == Child => [new("effect-mip-off", 0x10c0, [new("_INDEX", "PSIZE15")]), profiles[1]],
            "unexpected-function" when origin == Child => [.. profiles, Function()],
            "missing-function" when origin == Main => profiles.Where(profile => profile.Id != FunctionProfile).ToArray(),
            _ => profiles
        };

        var result = D3D9EffectDependencyReplay.Validate(fixture.Tree, fixture.Map, inspector, cancellationToken: Token);

        AssertRejected(result.Status, result.Accepted, result.Diagnostics, "replay-profile-drift");
    }

    [Theory]
    [InlineData("row-ordinal")]
    [InlineData("row-key")]
    [InlineData("raw-row")]
    [InlineData("symbol")]
    [InlineData("function-reference")]
    [InlineData("function-entry")]
    [InlineData("function-target")]
    public void SemanticRowsSymbolsAndFunctionRequestsMustRemainExact(string drift)
    {
        var fixture = new Fixture();
        var inspector = fixture.ReplayInspector();
        inspector.ChangeReferences = (origin, references) => origin != Main ? references : drift switch
        {
            "row-ordinal" => Change(references, 1, reference => reference with { RowOrdinal = 8 }),
            "row-key" => Change(references, 1, reference => reference with { RowKey = "owner" }),
            "raw-row" => Change(references, 1, reference => reference with { RawRow = Ref("*=child\\child.fx") }),
            "symbol" => Change(references, 2, reference => reference with { Symbol = D3D9EffectDependencySymbol.MainDefault }),
            "function-reference" => Change(references, 3, reference => reference with { Reference = Ref("changed") }),
            "function-entry" => Change(references, 3, reference => reference with { FunctionEntryPoint = "changed" }),
            "function-target" => Change(references, 3, reference => reference with { FunctionTarget = "tx_1_1" }),
            _ => throw new InvalidOperationException()
        };

        var result = D3D9EffectDependencyReplay.Validate(fixture.Tree, fixture.Map, inspector, cancellationToken: Token);

        if (drift is "function-entry" or "function-target")
            AssertRejected(result.Status, result.Accepted, result.Diagnostics, "function-profile-missing", "replay-request-drift");
        else
            AssertRejected(result.Status, result.Accepted, result.Diagnostics, "replay-request-drift");
    }

    [Fact]
    public void IncludeFailureCannotBeSwallowedByACompleteInspectorReport()
    {
        var fixture = new Fixture(sharedOwners: true);
        var inspector = fixture.ReplayInspector();
        bool caught = false;
        inspector.AfterIncludes = (origin, include, _) =>
        {
            if (origin != Main) return;
            try { include(new("effect-mip-off", false, null, 1, Ref("undeclared.inc"))); }
            catch (Exception) { caught = true; }
        };

        var result = D3D9EffectDependencyReplay.Validate(fixture.Tree, fixture.Map, inspector, cancellationToken: Token);

        Assert.True(caught);
        AssertRejected(result.Status, result.Accepted, result.Diagnostics, "replay-map-miss");
        Assert.Equal(new int?[] { 0, 1 }, result.Diagnostics.Where(diagnostic => diagnostic.Code == "replay-map-miss")
            .Select(diagnostic => diagnostic.RootIndex).Distinct().Order());
    }

    [Fact]
    public void PreCancellationReturnsCancelledWithoutInspectingAnyFx()
    {
        var fixture = new Fixture();
        var inspector = fixture.ReplayInspector();

        var result = D3D9EffectDependencyReplay.Validate(fixture.Tree, fixture.Map, inspector, cancellationToken: new(true));

        Assert.Equal(D3D9EffectDependencyReplayStatus.Cancelled, result.Status);
        Assert.False(result.Accepted);
        Assert.Equal(0, result.InspectedFxCount);
        Assert.Empty(inspector.Calls);
    }

    [Fact]
    public void CancellationDuringAnIncludeRemainsCancelledWhenInspectorSwallowsIt()
    {
        var fixture = new Fixture();
        var inspector = fixture.ReplayInspector();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bool caught = false;
        inspector.AfterIncludes = (origin, include, _) =>
        {
            if (origin != Main) return;
            cancellation.Cancel();
            try { include(new("effect-mip-off", false, null, 1, Ref(@"階層\local.inc", 932))); }
            catch (OperationCanceledException) { caught = true; }
        };

        var result = D3D9EffectDependencyReplay.Validate(fixture.Tree, fixture.Map, inspector, cancellationToken: cancellation.Token);

        Assert.True(caught);
        Assert.Equal(D3D9EffectDependencyReplayStatus.Cancelled, result.Status);
        Assert.False(result.Accepted);
        Assert.Equal(new[] { Main }, inspector.Calls);
    }

    [Fact]
    public void ResourceMapRequiresAMappedPrivateBaseEvenForAbsoluteRequests()
    {
        var map = ResolutionMap();
        Assert.Throws<ArgumentException>(() => map.Resolve(Main, Texture));
        Assert.Throws<ArgumentException>(() => map.Resolve(@"C:\pkg\private\unknown.fx", Texture));
        Assert.Equal(@"C:\pkg\private\leaf.bin", map.Resolve(@"C:\pkg\private\base.fx", Texture));
        Assert.Equal(@"C:\pkg\private\leaf.bin", map.Resolve(@"C:\pkg\private\base.fx", @"C:\pkg\private\leaf.bin"));
    }

    [Theory]
    [InlineData("texture.bin")]
    [InlineData(@".\texture.bin")]
    [InlineData(@"child\..\texture.bin")]
    [InlineData("C:/UNAVAILABLE-source/child/../TEXTURE.bin")]
    public void ResourceMapResolvesAgainstOriginalIdentityWithCaseAndSeparatorCanonicalization(string request)
    {
        var map = ResolutionMap();
        Assert.Equal(@"C:\pkg\private\leaf.bin", map.Resolve("c:/PKG/private/./base.fx", request));
    }

    [Theory]
    [InlineData(@"C:texture.bin")]
    [InlineData(@"\texture.bin")]
    [InlineData("/texture.bin")]
    [InlineData(@"\\server\share\texture.bin")]
    [InlineData("texture.bin:stream")]
    [InlineData("texture.bin.")]
    [InlineData("texture.bin ")]
    [InlineData(@"..\..\..\texture.bin")]
    public void ResourceMapRejectsAmbiguousAndNoncanonicalWindowsRequests(string request)
    {
        var map = ResolutionMap();
        Assert.Throws<ArgumentException>(() => map.Resolve(@"C:\pkg\private\base.fx", request));
    }

    [Fact]
    public void ResourceMapDoesNotResolveRelativeRequestsInThePrivateLocalDirectory()
    {
        var map = ResolutionMap();
        // This private file exists in the map, but "leaf.bin" was never an original sibling of main.fx.
        Assert.Throws<ArgumentException>(() => map.Resolve(@"C:\pkg\private\base.fx", "leaf.bin"));
    }

    private static D3D9EffectResourceMap ResolutionMap() => new(
        [new(Main, @"C:\pkg\private\base.fx"), new(Texture, @"C:\pkg\private\leaf.bin")]);

    private static void AssertRejected(D3D9EffectDependencyReplayStatus status, bool accepted,
        IReadOnlyList<D3D9EffectDependencyDiagnostic> diagnostics, params string[] codes)
    {
        Assert.Equal(D3D9EffectDependencyReplayStatus.NonPortable, status);
        Assert.False(accepted);
        Assert.Contains(diagnostics, diagnostic => codes.Contains(diagnostic.Code));
    }

    private static D3D9EffectDependencySemanticReference[] Change(D3D9EffectDependencySemanticReference[] references,
        int index, Func<D3D9EffectDependencySemanticReference, D3D9EffectDependencySemanticReference> change)
    {
        var result = references.ToArray(); result[index] = change(result[index]); return result;
    }

    private static D3D9EffectDependencyProfile Function() => new(FunctionProfile, 0, [], "make", "tx_1_0");
    private static D3D9EffectDependencyProfile[] Profiles(string origin) => origin == Main
        ? [new("effect-mip-off", 0x1000, [new("_INDEX", "PSIZE15")]),
           new("effect-mip-on", 0x1000, [new("_INDEX", "PSIZE15"), new("MME_MIPMAP", "")]), Function()]
        : [new("effect-mip-off", 0x1000, [new("_INDEX", "PSIZE15")]),
           new("effect-mip-on", 0x1000, [new("_INDEX", "PSIZE15"), new("MME_MIPMAP", "")])];
    private static D3D9EffectDependencySemanticReference[] References(string origin) => origin != Main ? [] :
        [new("effect-mip-off", D3D9EffectDependencyReferenceKind.Resource, Ref("texture.bin"), "image", "ResourceName"),
         new("effect-mip-on", D3D9EffectDependencyReferenceKind.Effect, Ref(@"child\child.fx"), "rt", "DefaultEffect", 7, "*", Ref(@" *= child\child.fx ")),
         new("effect-mip-on", D3D9EffectDependencyReferenceKind.Symbol, Ref("hide"), "rt", "DefaultEffect", 9, "hidden", Ref(" hidden=hide "), D3D9EffectDependencySymbol.Hide),
         new("effect-mip-off", D3D9EffectDependencyReferenceKind.TextureFunction, Ref("make"), "generated", "Function", FunctionEntryPoint: "make", FunctionTarget: "tx_1_0")];
    private static D3D9EffectDependencyReference Ref(string text, int codePage = 65001) => new(EncodingFor(codePage).GetBytes(text), codePage);
    private static Encoding EncodingFor(int codePage) => codePage == 65001 ? Encoding.UTF8 : CodePagesEncodingProvider.Instance.GetEncoding(codePage)!;

    private sealed class Fixture
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase)
        {
            [Main] = [0x66, 0x78, 0, 0xff], [Child] = [0x63, 0x68, 0, 0x81],
            [Local] = [0x81, 0x40, 0, 0xfe], [Leaf] = [9, 8, 0, 7],
            [System] = [6, 0, 5, 4], [Texture] = [0x42, 0x4d, 0, 0xff]
        };

        public Fixture(bool sharedOwners = false)
        {
            Source = new(files);
            D3D9EffectDependencyRoot[] roots = sharedOwners
                ? [new(Main, new(Guid.Parse("edc8f144-2f29-4808-bbd5-091d1f90b0e0"), 0, "Object", 1, "first owner", 11)),
                   new(Main, new(Guid.Parse("c1259814-f616-4fd0-93ad-ae6d020b9716"), 2, "Effect@hidden", 3, "second owner", 22))]
                : [new(Main)];
            var collected = new D3D9EffectDependencyCollector(Source, new Inspector(files)).Collect(roots, Token);
            Assert.Equal(D3D9EffectDependencyStatus.Complete, collected.Status);
            Tree = D3D9EffectResourceTree.FromClosure(Assert.IsType<D3D9EffectDependencyClosure>(collected.Closure), cancellationToken: Token);
            Map = new(Tree.DependencyGraph!.Nodes.Select(node =>
                new D3D9EffectResourceBinding(node.OriginAbsolutePath, @"C:\pkg\private\" + node.RelativePath)));
            SourceReadsAfterCollection = Source.Reads;
        }

        public MemorySource Source { get; }
        public int SourceReadsAfterCollection { get; }
        public D3D9EffectResourceTree Tree { get; }
        public D3D9EffectResourceMap Map { get; }
        public Inspector ReplayInspector() => new(files, Map);
    }

    private sealed class Inspector(Dictionary<string, byte[]> files, D3D9EffectResourceMap? map = null) : ID3D9EffectDependencyInspector
    {
        public List<string> Calls { get; } = [];
        public List<string> Includes { get; } = [];
        public Func<string, D3D9EffectDependencyProfile[], D3D9EffectDependencyProfile[]>? ChangeProfiles { get; set; }
        public Func<string, D3D9EffectDependencySemanticReference[], D3D9EffectDependencySemanticReference[]>? ChangeReferences { get; set; }
        public Action<string, Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot>, CancellationToken>? AfterIncludes { get; set; }

        public D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
            Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> include, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string origin = map is null ? topFx.OriginAbsolutePath
                : Assert.Single(map.Bindings, binding => binding.MaterializedAbsolutePath == topFx.OriginAbsolutePath).OriginAbsolutePath;
            Check(topFx, origin); Calls.Add(origin);
            if (origin == Main)
            {
                var local = include(new("effect-mip-off", false, null, 1, Ref(@"階層\local.inc", 932)));
                CheckInclude(local, Local);
                var leaf = include(new("effect-mip-off", false, local.OriginAbsolutePath, 2, Ref("leaf.inc")));
                CheckInclude(leaf, Leaf);
                // A SYSTEM request made from a nested parent resolves from the top FX, unlike LOCAL.
                var system = include(new("effect-mip-on", true, local.OriginAbsolutePath, 2, Ref("root-system.inc")));
                CheckInclude(system, System);
            }
            AfterIncludes?.Invoke(origin, include, cancellationToken);
            var profiles = Profiles(origin); var references = References(origin);
            return new(D3D9EffectDependencyStatus.Complete, Identity,
                ChangeProfiles?.Invoke(origin, profiles) ?? profiles, ChangeReferences?.Invoke(origin, references) ?? references);
        }

        private void CheckInclude(D3D9EffectDependencySnapshot snapshot, string origin) { Check(snapshot, origin); Includes.Add(origin); }
        private void Check(D3D9EffectDependencySnapshot snapshot, string origin)
        {
            string expectedPath = map is null ? origin
                : Assert.Single(map.Bindings, binding => binding.OriginAbsolutePath == origin).MaterializedAbsolutePath;
            Assert.Equal(expectedPath, snapshot.OriginAbsolutePath);
            Assert.Equal(files[origin], snapshot.CopyBytes());
        }
    }

    private sealed class MemorySource(Dictionary<string, byte[]> files) : ID3D9EffectDependencySource
    {
        public int Reads { get; private set; }
        public D3D9EffectDependencySnapshot Capture(D3D9EffectDependencySourceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            string text = EncodingFor(request.Reference.CodePage).GetString(request.Reference.CopyBytes());
            string origin;
            if (text.Length >= 3 && text[1] == ':') origin = text;
            else
            {
                string parent = request.BaseOriginAbsolutePath ?? throw new InvalidDataException("Relative test references require a base file.");
                origin = parent[..(parent.LastIndexOf('\\') + 1)] + text;
            }
            origin = D3D9EffectDependencySnapshot.CanonicalizeOrigin(origin);
            if (!files.TryGetValue(origin, out var bytes)) throw new InvalidDataException("Unexpected original-source request: " + origin);
            return new(origin, bytes);
        }
    }
}
