using System.Collections;
using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

public sealed class D3D9EmmRuntimeConfigurationTests
{
    private static readonly Guid First = new("94866943-8e4a-4484-b700-360bd060f4d1");
    private static readonly Guid Second = new("94866943-8e4a-4484-b700-360bd060f4d2");
    private static readonly Guid Third = new("94866943-8e4a-4484-b700-360bd060f4d3");

    [Fact]
    public void PreservesEverySectionOwnerPresenceAndIndependentSubsetRow()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx\nAcs4=C:\\stage.x", """
            [Effect]
            Default=default.fx
            Pmd0.show=false
            Pmd0[2]=none
            Pmd0[3].show=true
            [Effect@Depth(2)]
            Owner=Pmd0[2]@Parent(1)
            Pmd0=hide
            Pmd0[2]=main_default
            Acs4=target.fx
            Acs4.show=false
            [Effect@Parent(1)]
            Owner=Acs4
            Pmd0=none
            """, Binding(First, true, @"C:\actor.pmx"), Binding(Second, false, @"C:\stage.x"));
        var configuration = D3D9EmmRuntimeConfiguration.Create(plan,
            [Seed(First), Seed(Second, false)], Map("default.fx", "target.fx"));

        Assert.Same(plan, configuration.ImportPlan);
        Assert.Equal(3, configuration.Sections.Count);
        var main = configuration.Sections[0];
        Assert.True(main.IsDefault);
        Assert.Equal(@"G:\materialized\default.fx", main.DefaultEffect!.MaterializedAbsolutePath);
        var entry = Assert.Single(main.Entries);
        Assert.Equal(First, entry.ObjectId);
        Assert.Null(entry.Effect);
        Assert.False(entry.Shown);
        Assert.Equal(D3D9EmmRuntimeEffectKind.None, entry.SubsetEffects[2].Kind);
        Assert.True(entry.SubsetShown[3]);
        Assert.False(entry.SubsetEffects.ContainsKey(3));
        Assert.False(entry.SubsetShown.ContainsKey(2));
        var named = configuration.Sections[1];
        Assert.Equal("Effect@Depth(2)", named.Name);
        Assert.Equal("Depth", named.BaseName);
        Assert.Null(named.DefaultEffect);
        Assert.Equal(First, named.Owner!.ObjectId);
        Assert.True(named.Owner.IsModel);
        Assert.Equal("Pmd0", named.Owner.RecordKey);
        Assert.Equal(2, named.Owner.Subset);
        Assert.Equal("Parent(1)", named.Owner.ParentTargetLabel);
        Assert.Equal(D3D9EmmRuntimeEffectKind.Hide, named.Entries[0].Effect!.Kind);
        Assert.Equal("hide", named.Entries[0].Effect!.OriginalValue);
        Assert.Equal(D3D9EmmRuntimeEffectKind.MainDefault, named.Entries[0].SubsetEffects[2].Kind);
        Assert.False(named.Entries[1].IsModel);
        Assert.Equal(Second, named.Entries[1].ObjectId);
        Assert.False(named.Entries[1].Shown);
    }

    [Fact]
    public void UsesPlanInstanceMatchesIncludingRepeatedFilesAndBothKinds()
    {
        var plan = Plan("Pmd7=C:\\same.x\nPmd1=C:\\same.x\nAcs1=C:\\same.x", """
            [Effect]
            Default=none
            Pmd7=none
            Pmd1.show=false
            Acs1.show=true
            """, Binding(First, true, @"C:\same.x", 20), Binding(Second, true, @"C:\same.x", -1),
            Binding(Third, false, @"C:\same.x", 0));
        var configuration = D3D9EmmRuntimeConfiguration.Create(plan,
            [Seed(First), Seed(Third, false), Seed(Second)], Map());

        Assert.Equal(First, configuration.ObjectsByRecordKey["Pmd7"].ObjectId);
        Assert.Equal(Second, configuration.ObjectsByRecordKey["Pmd1"].ObjectId);
        Assert.Equal(Third, configuration.ObjectsByRecordKey["Acs1"].ObjectId);
        Assert.Equal(First, configuration.Sections[0].Entries[0].ObjectId);
        Assert.Equal(Second, configuration.Sections[0].Entries[1].ObjectId);
        Assert.Equal(Third, configuration.Sections[0].Entries[2].ObjectId);
        Assert.Equal(@"C:\same.x", configuration.ObjectsByRecordKey["Pmd7"].SourcePath);
    }

    [Fact]
    public void RetainsSeedsForExplicitNoneSubsetOnlyAndUnmatchedLiveObjects()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx\nPmd1=C:\\other.pmx", """
            [Effect]
            Default=default.fx
            Pmd0=none
            Pmd1[1]=none
            """, Binding(First, true, @"C:\actor.pmx"), Binding(Second, true, @"C:\other.pmx"),
            Binding(Third, false, @"C:\unmatched.x"));
        var configuration = D3D9EmmRuntimeConfiguration.Create(plan,
            [Seed(First, path: @"C:\work\sibling.fx"), Seed(Second, path: @"C:\work\embedded.fx"),
                Seed(Third, false, @"C:\work\default.fx")], Map("default.fx", "sibling.fx", "embedded.fx"));

        Assert.Equal(3, configuration.Objects.Count);
        Assert.Equal(@"G:\materialized\sibling.fx", configuration.Objects.Single(o => o.ObjectId == First).RootSeed.MaterializedAbsolutePath);
        Assert.Equal(D3D9EmmRuntimeEffectKind.None, configuration.Sections[0].Entries[0].Effect!.Kind);
        Assert.Null(configuration.Sections[0].Entries[1].Effect);
        var unmatched = configuration.Objects.Single(o => o.ObjectId == Third);
        Assert.Null(unmatched.RecordKey);
        Assert.Null(unmatched.RecordSourcePath);
        Assert.Equal(@"G:\materialized\default.fx", unmatched.RootSeed.MaterializedAbsolutePath);
    }

    [Fact]
    public void FrozenNoEffectSeedIsDistinctFromAnAbsentWholeAssignment()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", "[Effect]\nDefault=none\nPmd0.show=false", Binding(First, true, @"C:\actor.pmx"));
        var configuration = D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], Map());

        Assert.Equal(D3D9EmmRuntimeEffectKind.None, Assert.Single(configuration.Objects).RootSeed.Kind);
        Assert.Null(Assert.Single(configuration.Sections[0].Entries).Effect);
        Assert.Equal(D3D9EmmRuntimeEffectKind.None, configuration.Sections[0].DefaultEffect!.Kind);
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("main_default")]
    [InlineData("None")]
    public void RootScopeOnlyRecognizesExactLowercaseNone(string value)
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", $"[Effect]\nDefault={value}\nPmd0={value}\nPmd0[0]={value}", Binding(First, true, @"C:\actor.pmx"));
        var configuration = D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], Map(value));

        Assert.Equal(D3D9EmmRuntimeEffectKind.File, configuration.Sections[0].DefaultEffect!.Kind);
        var entry = Assert.Single(configuration.Sections[0].Entries);
        Assert.Equal(D3D9EmmRuntimeEffectKind.File, entry.Effect!.Kind);
        Assert.Equal(D3D9EmmRuntimeEffectKind.File, entry.SubsetEffects[0].Kind);
    }

    [Fact]
    public void ResolvesRelativeRootedAndSameDriveRelativePathsFromFrozenWorkingDirectory()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", """
            [Effect]
            Default=effects\..\default.fx
            Pmd0=\root.fx
            Pmd0[0]=C:relative.fx
            """, Binding(First, true, @"C:\actor.pmx"));
        var map = new D3D9EffectResourceMap([
            new(@"C:\work\default.fx", @"G:\materialized\default.fx"),
            new(@"C:\root.fx", @"G:\materialized\root.fx"),
            new(@"C:\work\relative.fx", @"G:\materialized\relative.fx")]);
        var configuration = D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], map);

        Assert.Equal(@"C:\work\default.fx", configuration.Sections[0].DefaultEffect!.OriginAbsolutePath);
        Assert.Equal(@"C:\root.fx", configuration.Sections[0].Entries[0].Effect!.OriginAbsolutePath);
        Assert.Equal(@"C:\work\relative.fx", configuration.Sections[0].Entries[0].SubsetEffects[0].OriginAbsolutePath);
        Assert.Equal(@"D:\scene", configuration.ImportPlan.PathContext.EmmDirectory);
    }

    [Fact]
    public void MissingOriginalIdentityNeverFallsBackToEmmDirectoryOrMaterializedIdentity()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", "[Effect]\nDefault=missing.fx", Binding(First, true, @"C:\actor.pmx"));
        var map = new D3D9EffectResourceMap([new(@"D:\scene\missing.fx", @"C:\work\missing.fx")]);
        var error = Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], map));

        Assert.Contains("Effect", error.Message);
        Assert.Contains(@"C:\work\missing.fx", error.Message);
    }

    [Fact]
    public void RejectsCrossDriveRelativeEffectWithoutAmbientPerDriveLookup()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", "[Effect]\nDefault=D:missing.fx", Binding(First, true, @"C:\actor.pmx"));
        Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], Map()));
    }

    [Fact]
    public void ProjectsOnlyAcceptedMetadataAndKeepsEveryOriginalByteAndWarning()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", """
            [Effect]
            Default=none
            [Unknown]
            Pmd0=none
            Pmd0=ignored.fx
            Pmd0.show=TRUE
            """, Binding(First, true, @"C:\actor.pmx"));
        var expected = plan.Document.CopySourceBytes();
        var configuration = D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], Map());

        Assert.NotEmpty(configuration.ImportPlan.Document.Diagnostics);
        Assert.Equal(expected, configuration.ImportPlan.Document.CopySourceBytes());
        var entry = Assert.Single(configuration.Sections[0].Entries);
        Assert.Equal(D3D9EmmRuntimeEffectKind.None, entry.Effect!.Kind);
        Assert.Null(entry.Shown);
    }

    [Fact]
    public void KeepsSelectedLegacyEncodingAndNonAsciiOriginalBytesUnchanged()
    {
        var bytes = LegacyAnsiEncoding.Create(932).GetBytes("[Info]\r\nVersion=3\r\n[Object]\r\nPmd0=C:\\模型\\役者.pmx\r\n[Effect]\r\nDefault=none\r\n; 保存する行\r\n");
        var document = EmmReader.Read(bytes, new EmmReadOptions { AnsiCodePage = 932 });
        var plan = EmmImportPlan.Create(document, @"D:\scene\mapping.emm",
            [Binding(First, true, @"C:\模型\役者.pmx")], @"C:\work");
        var configuration = D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], Map());

        Assert.Equal(932, configuration.ImportPlan.Document.AnsiCodePage);
        Assert.Equal(bytes, configuration.ImportPlan.Document.CopySourceBytes());
        Assert.Equal(@"C:\模型\役者.pmx", Assert.Single(configuration.Objects).RecordSourcePath);
    }

    [Fact]
    public void RejectsErrorDiagnosticsAndUnsupportedVersionBeforeProjection()
    {
        var source = Document("Pmd0=C:\\actor.pmx", "[Effect]\nDefault=none");
        var invalid = new EmmDocument(source.CopySourceBytes(), 3, source.AnsiCodePage, source.Objects,
            source.EffectSections, [new("InjectedError", "Do not execute.", Severity: EmmDiagnosticSeverity.Error)], source.Rows);
        var plan = EmmImportPlan.Create(invalid, @"D:\scene\mapping.emm", [Binding(First, true, @"C:\actor.pmx")], @"C:\work");
        Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], Map()));
        var unsupported = EmmReader.Read(Encoding.ASCII.GetBytes("[Info]\nVersion=4\n[Object]\nPmd0=C:\\actor.pmx\n[Effect]\nDefault=none"));
        plan = EmmImportPlan.Create(unsupported, @"D:\scene\mapping.emm", [Binding(First, true, @"C:\actor.pmx")], @"C:\work");
        Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], Map()));
    }

    [Theory]
    [InlineData("Pmd0=none")]
    [InlineData("[Effect@Target]\nOwner=Pmd0")]
    public void RejectsReferencedUnmatchedRecordsRatherThanDroppingAcceptedRows(string rows)
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", "[Effect]\nDefault=none\n" + rows);
        var error = Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [], Map()));
        Assert.Contains("Pmd0", error.Message);
    }

    [Fact]
    public void RequiresExactlyOneKnownKindSeedForEveryLiveObject()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", "[Effect]\nDefault=none", Binding(First, true, @"C:\actor.pmx"));
        Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [], Map()));
        Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First), Seed(First)], Map()));
        Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First, false)], Map()));
        Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [Seed(Second)], Map()));
        Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [null!], Map()));
        Assert.Throws<ArgumentException>(() => Seed(Guid.Empty));
        Assert.Throws<ArgumentException>(() => Seed(First, path: "relative.fx"));
    }

    [Fact]
    public void RequiresMappedSeedEvenWhenAnExplicitRootWouldOverrideIt()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", "[Effect]\nDefault=none\nPmd0=none", Binding(First, true, @"C:\actor.pmx"));
        Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First, path: @"Z:\missing.fx")], Map()));
    }

    [Fact]
    public void CopiesInputsAndExposesReadOnlyCollections()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", "[Effect]\nDefault=none\nPmd0[0]=none\nPmd0[1].show=false", Binding(First, true, @"C:\actor.pmx"));
        var seeds = new List<D3D9EmmRuntimeRootSeed> { Seed(First) };
        var configuration = D3D9EmmRuntimeConfiguration.Create(plan, seeds, Map());
        seeds.Clear();

        Assert.Single(configuration.Objects);
        AssertReadOnly(configuration.Objects);
        AssertReadOnly(configuration.Sections);
        AssertReadOnly(configuration.Sections[0].Entries);
        var dictionary = Assert.IsAssignableFrom<IDictionary>(configuration.Sections[0].Entries[0].SubsetEffects);
        Assert.True(dictionary.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => dictionary.Clear());
    }

    [Fact]
    public void RejectsNullInputsAndInvalidUnicodeAndCopiedStringBudget()
    {
        var plan = Plan("Pmd0=C:\\actor.pmx", "[Effect]\nDefault=none", Binding(First, true, @"C:\actor.pmx"));
        Assert.Throws<ArgumentNullException>(() => D3D9EmmRuntimeConfiguration.Create(null!, [], Map()));
        Assert.Throws<ArgumentNullException>(() => D3D9EmmRuntimeConfiguration.Create(plan, null!, Map()));
        Assert.Throws<ArgumentNullException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], null!));
        Assert.Throws<ArgumentException>(() => Seed(First, path: "C:\\bad\ud800.fx"));
        var longPath = "C:\\" + new string('a', 32768) + ".pmx";
        plan = Plan("", "[Effect]\nDefault=none", Binding(First, true, longPath));
        Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], Map()));
    }

    [Fact]
    public void RejectsOversizedSectionTableBeforeProcessingAnyRows()
    {
        var source = Document("", "[Effect]\nDefault=none");
        var document = new EmmDocument(source.CopySourceBytes(), 3, 936, [],
            Enumerable.Repeat(source.EffectSections[0], 65537), [], source.Rows);
        var plan = EmmImportPlan.Create(document, @"D:\scene\mapping.emm", [], @"C:\work");
        var error = Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [], Map()));

        Assert.Contains("65536", error.Message);
    }

    [Fact]
    public void AppliesCombinedCopiedStringBudgetIncludingTheExistingResourceMap()
    {
        var filler = new string('a', 32700);
        var resources = new D3D9EffectResourceMap(Enumerable.Range(0, 256).Select(index =>
            new D3D9EffectResourceBinding(@"Z:\o\" + filler + index + ".fx", @"G:\l\" + filler + index + ".fx")));
        var sourcePath = @"C:\" + new string('b', 20000) + ".pmx";
        var plan = Plan("", "[Effect]\nDefault=none", Binding(First, true, sourcePath));
        var error = Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [Seed(First)], resources));

        Assert.Contains("16 MiB", error.Message);
    }

    [Fact]
    public void RejectsDuplicateSectionsInsteadOfMergingTheirOwnerState()
    {
        var source = Document("", "[Effect]\nDefault=none");
        var document = new EmmDocument(source.CopySourceBytes(), 3, 936, [],
            [source.EffectSections[0], source.EffectSections[0]], [], source.Rows);
        var plan = EmmImportPlan.Create(document, @"D:\scene\mapping.emm", [], @"C:\work");
        var error = Assert.Throws<ArgumentException>(() => D3D9EmmRuntimeConfiguration.Create(plan, [], Map()));

        Assert.Contains("Duplicate EMM section", error.Message);
    }

    private static void AssertReadOnly(object collection)
    {
        var list = Assert.IsAssignableFrom<IList>(collection);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    private static EmmDocument Document(string objects, string sections) =>
        EmmReader.Read(Encoding.ASCII.GetBytes($"[Info]\nVersion=3\n[Object]\n{objects}\n{sections}\n"));

    private static EmmImportPlan Plan(string objects, string sections, params EmmObjectBinding[] bindings) =>
        EmmImportPlan.Create(Document(objects, sections), @"D:\scene\mapping.emm", bindings, @"C:\work");

    private static EmmObjectBinding Binding(Guid id, bool model, string path, int order = 0) => new(id, model, path, order);
    private static D3D9EmmRuntimeRootSeed Seed(Guid id, bool model = true, string? path = null) => new(id, model, path);
    private static D3D9EffectResourceMap Map(params string[] names) => new(names.Select(name =>
        new D3D9EffectResourceBinding(@"C:\work\" + name, @"G:\materialized\" + name)));
}
