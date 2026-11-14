using System.Collections;
using System.Text;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

public sealed class D3D9EffectProjectRootsTests
{
    private static readonly Guid Model = new("8069db0d-394f-4f87-b44a-d3d583c70101");
    private static readonly Guid Accessory = new("8069db0d-394f-4f87-b44a-d3d583c70102");
    private static readonly Guid Unmatched = new("8069db0d-394f-4f87-b44a-d3d583c70103");

    [Fact]
    public void FreezesEveryLiveSeedAndEveryAcceptedFileRowRegardlessOfOverridesOrVisibility()
    {
        var plan = Plan("Pmd0=models\\actor.pmx\nAcs4=accessories\\stage.x", """
            [Effect]
            Default=common.fx
            Pmd0=override.fx
            Pmd0.show=false
            Pmd0[0]=common.fx
            Pmd0[1]=none
            Acs4=none
            Acs4.show=false
            Acs4[2]=hidden.fx
            Acs4[2].show=false
            [Effect@Target]
            Owner=Pmd0[1]@label_fx
            Pmd0=named.fx
            Pmd0[0]=hide
            Pmd0[1]=main_default
            Acs4[2]=hidden.fx
            """, new EmmObjectBinding(Model, true, @"models\actor.pmx", 20),
            new EmmObjectBinding(Accessory, false, @"accessories\stage.x", -2), new EmmObjectBinding(Unmatched, false, @"extra.x", 50));
        var requests = new List<D3D9EffectImplicitRootRequest>();
        var result = D3D9EffectProjectRoots.Create(plan, request =>
        {
            requests.Add(request);
            return request.Object.ObjectId == Unmatched ? null : @"C:\work\implicit.fx";
        }, TestContext.Current.CancellationToken);

        Assert.Same(plan, result.ImportPlan);
        Assert.Equal(new[] { Accessory, Model, Unmatched }, requests.Select(r => r.Object.ObjectId));
        Assert.All(requests, r => Assert.Equal(@"C:\work", r.WorkingDirectory));
        Assert.All(requests, r => Assert.Equal(@"C:\work\common.fx", r.DefaultEffectAbsolutePath));
        Assert.Equal(@"C:\work\accessories\stage.x", requests[0].OriginAbsoluteObjectPath);
        Assert.Equal(@"C:\work\models\actor.pmx", requests[1].OriginAbsoluteObjectPath);
        Assert.Equal(new[] { Accessory, Model, Unmatched }, result.RootSeeds.Select(r => r.ObjectId));
        Assert.False(result.RootSeeds[0].IsModel);
        Assert.True(result.RootSeeds[1].IsModel);
        Assert.Null(result.RootSeeds[2].OriginAbsoluteFxPath);
        Assert.Equal(8, result.Roots.Count);
        Assert.Equal(result.Roots.Count, result.Origins.Count);
        Assert.Equal(2, result.Roots.Count(r => r.OriginAbsolutePath == @"C:\work\implicit.fx"));
        Assert.Equal(2, result.Roots.Count(r => r.OriginAbsolutePath == @"C:\work\common.fx"));
        Assert.Equal(2, result.Roots.Count(r => r.OriginAbsolutePath == @"C:\work\hidden.fx"));
        Assert.DoesNotContain(result.Roots, r => r.OriginAbsolutePath.EndsWith("label_fx", StringComparison.Ordinal));
        var named = Assert.Single(result.Origins, r => r.Kind == D3D9EffectProjectRootKind.EmmObject && r.EmmSection == "Effect@Target");
        Assert.Equal(Model, named.ObjectId);
        Assert.Equal("Pmd0", named.RecordKey);
        Assert.Equal(new EmmOwnerReference("Pmd0", 1, "label_fx"), named.SectionOwner);
        Assert.Equal("named.fx", named.OriginalValue);
        for (var i = 0; i < result.Roots.Count; i++)
        {
            Assert.Equal(i, result.Origins[i].RootIndex);
            Assert.Equal(result.Roots[i].OriginAbsolutePath, result.Origins[i].OriginAbsolutePath);
            Assert.Equal(result.Origins[i].ObjectId, result.Roots[i].Provenance.OwnerId);
        }
    }

    [Fact]
    public void AppliesExactCaseAndScopeForSymbolicValues()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", """
            [Effect]
            Default=none
            Pmd0=hide
            Pmd0[0]=main_default
            Pmd0[1]=NONE
            Pmd0[2]=none
            [Effect@Target]
            Owner=Pmd0
            Pmd0=hide
            Pmd0[0]=main_default
            Pmd0[1]=none
            Pmd0[2]=NONE
            """, new EmmObjectBinding(Model, true, @"C:\actor.pmx", 0));
        var result = D3D9EffectProjectRoots.Create(plan, request =>
        {
            Assert.Null(request.DefaultEffectAbsolutePath);
            return null;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { @"C:\work\hide", @"C:\work\main_default", @"C:\work\NONE", @"C:\work\NONE" },
            result.Roots.Select(r => r.OriginAbsolutePath));
        Assert.Null(Assert.Single(result.RootSeeds).OriginAbsoluteFxPath);
    }

    [Fact]
    public void KeepsRequiredMissingDefaultsWholeAndSubsetRootsWhenImplicitSelectionMisses()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", """
            [Effect]
            Default=missing-default.fx
            Pmd0=missing-whole.fx
            Pmd0[3]=missing-subset.fx
            Pmd0.show=false
            Pmd0[3].show=false
            """, new EmmObjectBinding(Model, true, @"C:\actor.pmx", 0));
        var result = D3D9EffectProjectRoots.Create(plan, _ => null, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Roots.Count);
        Assert.Equal(new[] { D3D9EffectProjectRootKind.EmmDefault, D3D9EffectProjectRootKind.EmmObject, D3D9EffectProjectRootKind.EmmSubset },
            result.Origins.Select(r => r.Kind));
        var collection = new D3D9EffectDependencyCollector(new MissingSource(), new UnusedInspector()).Collect(result.Roots, TestContext.Current.CancellationToken);
        Assert.Equal(D3D9EffectDependencyStatus.Incomplete, collection.Status);
        Assert.Null(collection.Closure);
        Assert.Equal(@"C:\work\missing-default.fx", Assert.Single(collection.Diagnostics).TopFxOriginAbsolutePath);
    }

    [Fact]
    public void UsesFrozenWorkingDirectoryAndOnlyAcceptedReaderState()
    {
        var plan = Plan("Pmd0=actor.pmx", """
            [Effect]
            Default=effects\..\default.fx
            [RejectedHeader]
            Pmd0=C:relative.fx
            Pmd0=ignored.fx
            Pmd0[0]=\root.fx
            [Effect@Target]
            Owner=Pmd0
            Default=rejected-default.fx
            Pmd0=named.fx
            """, new EmmObjectBinding(Model, true, "actor.pmx", 0));
        var original = plan.Document.CopySourceBytes();
        var result = D3D9EffectProjectRoots.Create(plan, request =>
        {
            Assert.Equal(@"C:\work\actor.pmx", request.OriginAbsoluteObjectPath);
            Assert.Equal(@"C:\work\default.fx", request.DefaultEffectAbsolutePath);
            return null;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { @"C:\work\default.fx", @"C:\work\relative.fx", @"C:\root.fx", @"C:\work\named.fx" },
            result.Roots.Select(r => r.OriginAbsolutePath));
        Assert.Equal(original, plan.Document.CopySourceBytes());
        Assert.NotEmpty(plan.Document.Diagnostics);
        Assert.Equal(@"D:\emm", plan.PathContext.EmmDirectory);
    }

    [Fact]
    public void PreservesExplicitUnmatchedRecordProvenanceRatherThanDroppingItsRequiredFile()
    {
        var plan = Plan("Pmd0=C:\\missing.pmx", "[Effect]\nDefault=none\nPmd0=required.fx\nPmd0[2]=required.fx");
        var result = D3D9EffectProjectRoots.Create(plan, _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken);

        Assert.Empty(result.RootSeeds);
        Assert.Equal(2, result.Roots.Count);
        Assert.All(result.Origins, origin =>
        {
            Assert.Null(origin.ObjectId);
            Assert.True(origin.IsModel);
            Assert.Equal("Pmd0", origin.RecordKey);
        });
        Assert.Equal(2, result.Origins[1].Subset);
    }

    [Fact]
    public void SuppliesExistingRuntimeProjectionAfterTheCompletedMapIsAvailable()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx\nAcs0=C:\\stage.x", """
            [Effect]
            Default=default.fx
            Pmd0=override.fx
            Pmd0.show=false
            Acs0=none
            Acs0.show=false
            """, new EmmObjectBinding(Model, true, @"C:\actor.pmx", 0), new EmmObjectBinding(Accessory, false, @"C:\stage.x", 1));
        var roots = D3D9EffectProjectRoots.Create(plan,
            request => request.Object.IsModel ? @"C:\work\implicit.fx" : null, TestContext.Current.CancellationToken);
        var completedMap = new D3D9EffectResourceMap(roots.Roots.Select(r => r.OriginAbsolutePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select((path, index) =>
                new D3D9EffectResourceBinding(path, $@"G:\materialized\{index}.fx")));
        var runtime = D3D9EmmRuntimeConfiguration.Create(plan, roots.RootSeeds, completedMap);

        Assert.Equal(@"C:\work\implicit.fx", runtime.ObjectsByRecordKey["Pmd0"].RootSeed.OriginAbsolutePath);
        Assert.Equal(@"C:\work\override.fx", runtime.Sections[0].Entries[0].Effect!.OriginAbsolutePath);
        Assert.Equal(D3D9EmmRuntimeEffectKind.None, runtime.ObjectsByRecordKey["Acs0"].RootSeed.Kind);
        Assert.Equal(D3D9EmmRuntimeEffectKind.None, runtime.Sections[0].Entries[1].Effect!.Kind);
        Assert.False(runtime.Sections[0].Entries[0].Shown);
        Assert.False(runtime.Sections[0].Entries[1].Shown);
    }

    [Fact]
    public void RequiresAbsoluteResolverWinnersAndRejectsUnfrozenDriveRelativePaths()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", "[Effect]\nDefault=none", new EmmObjectBinding(Model, true, @"C:\actor.pmx", 0));
        Assert.Throws<ArgumentException>(() => D3D9EffectProjectRoots.Create(plan, _ => "relative.fx", TestContext.Current.CancellationToken));
        plan = Plan("Pmd0=C:\\actor.pmx", "[Effect]\nDefault=D:unfrozen.fx", new EmmObjectBinding(Model, true, @"C:\actor.pmx", 0));
        Assert.Throws<ArgumentException>(() => D3D9EffectProjectRoots.Create(plan, _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        plan = Plan("", "[Effect]\nDefault=none", new EmmObjectBinding(Model, true, @"D:unfrozen.pmx", 0));
        Assert.Throws<ArgumentException>(() => D3D9EffectProjectRoots.Create(plan, _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExposesDetachedReadOnlyResultsAndSupportsAnEmptyEmmProject()
    {
        var bindings = new List<EmmObjectBinding> { new EmmObjectBinding(Model, true, @"C:\actor.pmx", 0) };
        var document = EmmReader.Read(Encoding.ASCII.GetBytes("[Info]\nVersion=3\n[Object]\n[Effect]\nDefault=none\n"));
        var plan = EmmImportPlan.Create(document, @"D:\emm\empty.emm", bindings, @"C:\work");
        var result = D3D9EffectProjectRoots.Create(plan, _ => @"C:\fx\..\actor.fx", TestContext.Current.CancellationToken);
        bindings.Clear();

        Assert.Single(result.ImportPlan.LiveObjects);
        Assert.Equal(@"C:\actor.fx", Assert.Single(result.RootSeeds).OriginAbsoluteFxPath);
        foreach (var collection in new object[] { result.Roots, result.Origins, result.RootSeeds })
        {
            var list = Assert.IsAssignableFrom<IList>(collection);
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
    }

    [Fact]
    public void CancellationStopsBeforeOrImmediatelyAfterAnImplicitResolverCall()
    {
        var plan = Plan("", "[Effect]\nDefault=none", new EmmObjectBinding(Model, true, @"C:\actor.pmx", 0), new EmmObjectBinding(Accessory, false, @"C:\stage.x", 1));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => D3D9EffectProjectRoots.Create(plan,
            _ => throw new InvalidOperationException(), cancelled.Token));
        using var during = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var calls = 0;
        Assert.Throws<OperationCanceledException>(() => D3D9EffectProjectRoots.Create(plan, _ =>
        {
            calls++;
            during.Cancel();
            return null;
        }, during.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RejectsUnsupportedDocumentsErrorsAndNullInputsBeforeCallingTheResolver()
    {
        var source = EmmReader.Read(Encoding.ASCII.GetBytes("[Info]\nVersion=3\n[Object]\n[Effect]\nDefault=none\n"));
        var errorDocument = new EmmDocument(source.CopySourceBytes(), 3, source.AnsiCodePage, [], [],
            [new("error", "Rejected input.", Severity: EmmDiagnosticSeverity.Error)], source.Rows);
        var errorPlan = EmmImportPlan.Create(errorDocument, @"D:\emm\map.emm", [], @"C:\work");
        Assert.Throws<ArgumentException>(() => D3D9EffectProjectRoots.Create(errorPlan, _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        var unsupported = EmmImportPlan.Create(EmmReader.Read(Encoding.ASCII.GetBytes("[Info]\nVersion=4\n[Object]\n[Effect]\nDefault=none\n")),
            @"D:\emm\map.emm", [], @"C:\work");
        Assert.Throws<ArgumentException>(() => D3D9EffectProjectRoots.Create(unsupported, _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => D3D9EffectProjectRoots.Create(null!, _ => null, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => D3D9EffectProjectRoots.Create(errorPlan, null!, TestContext.Current.CancellationToken));
    }

    private static EmmImportPlan Plan(string objects, string sections, params EmmObjectBinding[] bindings) =>
        EmmImportPlan.Create(EmmReader.Read(Encoding.ASCII.GetBytes($"[Info]\nVersion=3\n[Object]\n{objects}\n{sections}\n")),
            @"D:\emm\map.emm", bindings, @"C:\work");

    private sealed class MissingSource : ID3D9EffectDependencySource
    {
        public D3D9EffectDependencySnapshot Capture(D3D9EffectDependencySourceRequest request, CancellationToken cancellationToken) =>
            throw new D3D9EffectDependencyException(D3D9EffectDependencyStatus.Incomplete, "missing", "The required original is missing.");
    }

    private sealed class UnusedInspector : ID3D9EffectDependencyInspector
    {
        public D3D9EffectDependencyInspection Inspect(D3D9EffectDependencySnapshot topFx,
            Func<D3D9EffectDependencyIncludeRequest, D3D9EffectDependencySnapshot> captureInclude, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Missing required roots must fail before inspection.");
    }
}
