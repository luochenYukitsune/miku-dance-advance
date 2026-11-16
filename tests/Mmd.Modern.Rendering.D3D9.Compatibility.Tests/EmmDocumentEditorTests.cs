using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

/// <summary>Detached EMM authoring contracts; no files, native host or graphics device.</summary>
public sealed class EmmDocumentEditorTests
{
    private const string Minimum = "[Info]\nVersion=3\n[Object]\nPmd0=actor.pmx\nAcs1=stage.x\n[Effect]\nDefault=none\n";

    [Fact]
    public void ReplacingOneRowPreservesAllOtherSourceBytesIncludingUnknownRowsAndEofTail()
    {
        byte[] prefix = Encoding.ASCII.GetBytes(Minimum + "; untouched comment\r\nFuture=value # opaque\r\nPmd0=");
        byte[] suffix = Encoding.ASCII.GetBytes("\r\n[Unknown]\r\nFuture=still-opaque\n\n\u001aunscanned\0tail");
        byte[] source = prefix.Concat(new byte[] { 0xed, 0x40 }).Concat(Encoding.ASCII.GetBytes(".fx")).Concat(suffix).ToArray();
        var document = EmmReader.Read(source, new() { AnsiCodePage = 932 });

        var edited = EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", -1, "new.fx");

        byte[] expected = Encoding.ASCII.GetBytes(Minimum + "; untouched comment\r\nFuture=value # opaque\r\nPmd0=new.fx")
            .Concat(suffix).ToArray();
        Assert.Equal(expected, edited.CopySourceBytes());
        Assert.Equal(source, document.CopySourceBytes());
        Assert.Equal(932, edited.AnsiCodePage);
        Assert.Equal("new.fx", Entry(edited, "Effect", "Pmd0").EffectPath);
    }

    [Fact]
    public void AddingAnotherAssignmentPreservesNoncanonicalCp932SourceSpelling()
    {
        byte[] source = Encoding.ASCII.GetBytes("[Info]\nVersion=3\n[Object]\nPmd0=")
            .Concat(new byte[] { 0xed, 0x40 })
            .Concat(Encoding.ASCII.GetBytes(".pmx\n[Effect]\nDefault=none\n")).ToArray();
        var document = EmmReader.Read(source, new() { AnsiCodePage = 932 });

        var edited = EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", -1, "effect.fx");

        Assert.Equal(source.Concat("Pmd0=effect.fx\r\n"u8.ToArray()), edited.CopySourceBytes());
        Assert.Equal(source, document.CopySourceBytes());
    }

    [Theory]
    [InlineData("first.fx", "replacement.fx")]
    [InlineData("", "replacement.fx")]
    [InlineData("first.fx", null)]
    [InlineData("", null)]
    public void ReplacingOrResettingRemovesEveryDuplicateWithoutResurfacingShadowValue(string first, string? replacement)
    {
        var document = Read(Minimum + "Pmd0=" + first + "\n; keep between duplicates\nPmd0=shadow.fx\nAcs1=keep.fx\nPmd0=third.fx\n");
        byte[] original = document.CopySourceBytes();

        var edited = EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", -1, replacement);

        Assert.Equal(Bytes(Minimum + (replacement is null ? "" : "Pmd0=" + replacement + "\r\n")
            + "; keep between duplicates\nAcs1=keep.fx\n"), edited.CopySourceBytes());
        Assert.Equal(replacement is null ? 0 : 1, edited.Rows.Count(row => row.Key == "Pmd0" && row.SectionName == "Effect"));
        Assert.Equal("keep.fx", Entry(edited, "Effect", "Acs1").EffectPath);
        Assert.Equal(original, document.CopySourceBytes());
        if (replacement is null) Assert.DoesNotContain(Section(edited, "Effect").Entries, entry => entry.ObjectKey == "Pmd0");
        else Assert.Equal(replacement, Entry(edited, "Effect", "Pmd0").EffectPath);
    }

    [Fact]
    public void NamedSectionObjectSubsetAndVisibilityAreIndependentAndResetRestoresAbsence()
    {
        var document = Read(Minimum + "Pmd0=base.fx\nPmd0.show=true\n[Effect@Depth(2)]\nOwner=Pmd0[7]@Parent(1)\n"
            + "Pmd0=named.fx\nPmd0[2]=subset.fx\nPmd0[2].show=false\nAcs1=stage.fx\n");
        var edited = EmmDocumentEditor.SetEffect(document, "Effect@Depth(2)", "Pmd0", 2, "none");
        edited = EmmDocumentEditor.SetShown(edited, "Effect@Depth(2)", "Pmd0", -1, false);
        edited = EmmDocumentEditor.SetShown(edited, "Effect@Depth(2)", "Pmd0", 2, true);

        Assert.Equal("base.fx", Entry(edited, "Effect", "Pmd0").EffectPath);
        Assert.True(Entry(edited, "Effect", "Pmd0").Shown);
        var named = Entry(edited, "Effect@Depth(2)", "Pmd0");
        Assert.Equal("named.fx", named.EffectPath);
        Assert.Equal("none", named.SubsetPaths[2]);
        Assert.False(named.Shown);
        Assert.True(named.SubsetShown[2]);
        Assert.Equal(new EmmOwnerReference("Pmd0", 7, "Parent(1)"), Section(edited, "Effect@Depth(2)").Owner);
        Assert.Equal("stage.fx", Entry(edited, "Effect@Depth(2)", "Acs1").EffectPath);

        edited = EmmDocumentEditor.SetEffect(edited, "Effect@Depth(2)", "Pmd0", 2, null);
        edited = EmmDocumentEditor.SetShown(edited, "Effect@Depth(2)", "Pmd0", 2, null);
        edited = EmmDocumentEditor.SetShown(edited, "Effect@Depth(2)", "Pmd0", -1, null);
        named = Entry(edited, "Effect@Depth(2)", "Pmd0");
        Assert.Empty(named.SubsetPaths);
        Assert.Empty(named.SubsetShown);
        Assert.Null(named.Shown);
        Assert.Equal("named.fx", named.EffectPath);
        Assert.False(Entry(document, "Effect@Depth(2)", "Pmd0").SubsetShown[2]);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("INVALID")]
    public void ResetShownRemovesRejectedAndAcceptedDuplicates(string first)
    {
        var document = Read(Minimum + "Pmd0.show=" + first + "\nPmd0.show=false\n");
        var edited = EmmDocumentEditor.SetShown(document, "Effect", "Pmd0", -1, null);
        Assert.Equal(Bytes(Minimum), edited.CopySourceBytes());
        Assert.Empty(Section(edited, "Effect").Entries);
    }

    [Fact]
    public void InsertionUsesTargetSectionBoundaryAndKeepsFollowingNamedSectionBytes()
    {
        const string following = "[Effect@Depth]\r\nOwner=Acs1\r\nAcs1=depth.fx\r\n";
        var edited = EmmDocumentEditor.SetEffect(Read(Minimum + following), "Effect", "Pmd0", 0, "main_default");
        Assert.Equal(Bytes(Minimum + "Pmd0[0]=main_default\r\n" + following), edited.CopySourceBytes());
        Assert.Equal("depth.fx", Entry(edited, "Effect@Depth", "Acs1").EffectPath);
    }

    [Theory]
    [InlineData("Default=none", "Default=none\r\nPmd0=new.fx\r\n")]
    [InlineData("Default=none\nPmd0=old.fx", "Default=none\nPmd0=new.fx\r\n")]
    public void MissingFinalNewlineIsHandledForAppendAndReplacement(string ending, string expectedEnding)
    {
        string prefix = Minimum[..Minimum.IndexOf("Default=", StringComparison.Ordinal)];
        var edited = EmmDocumentEditor.SetEffect(Read(prefix + ending), "Effect", "Pmd0", -1, "new.fx");
        Assert.Equal(Bytes(prefix + expectedEnding), edited.CopySourceBytes());
        Assert.Equal("new.fx", Entry(edited, "Effect", "Pmd0").EffectPath);
    }

    [Fact]
    public void ResetOfAbsentKeyDoesNotAddNewlineOrChangeSource()
    {
        var document = Read(Minimum.TrimEnd('\n'));
        var edited = EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", 4, null);
        Assert.Equal(document.CopySourceBytes(), edited.CopySourceBytes());
    }

    [Fact]
    public void AddObjectChoosesFirstFreeSlotIndependentlyForModelsAndAccessories()
    {
        var document = Read(Minimum.Replace("Acs1=stage.x", "Pmd2=second.pmx\nAcs1=stage.x", StringComparison.Ordinal));
        var edited = EmmDocumentEditor.AddObject(document, true, "new actor.pmx");
        edited = EmmDocumentEditor.AddObject(edited, false, "new stage.x");
        Assert.Equal("new actor.pmx", Assert.Single(edited.Objects, item => item.Key == "Pmd1").Path);
        Assert.Equal("new stage.x", Assert.Single(edited.Objects, item => item.Key == "Acs0").Path);
        Assert.Equal(3, document.Objects.Count);
        Assert.Equal("none", Section(edited, "Effect").DefaultPath);
        Assert.Equal("added.fx", Entry(EmmDocumentEditor.SetEffect(edited, "Effect", "Pmd1", -1, "added.fx"), "Effect", "Pmd1").EffectPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\rb.fx")]
    [InlineData("a\nb.fx")]
    [InlineData("a\0b.fx")]
    [InlineData("a\u001ab.fx")]
    [InlineData("a;discarded.fx")]
    [InlineData("a#discarded.fx")]
    [InlineData("   ")]
    [InlineData(" leading.fx")]
    [InlineData("trailing.fx ")]
    [InlineData("\tleading.fx")]
    [InlineData("trailing.fx\v")]
    public void UnsafeValuesAreRejectedWithoutMutatingOriginal(string value)
    {
        var document = Read(Minimum + "Pmd0=original.fx\n");
        byte[] original = document.CopySourceBytes();
        Assert.Throws<ArgumentException>(() => EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", -1, value));
        Assert.Throws<ArgumentException>(() => EmmDocumentEditor.AddObject(document, true, value));
        Assert.Equal(original, document.CopySourceBytes());
        Assert.Equal("original.fx", Entry(document, "Effect", "Pmd0").EffectPath);
        Assert.Equal(2, document.Objects.Count);
    }

    [Theory]
    [InlineData(932)]
    [InlineData(936)]
    public void NonencodableValuesFailStrictlyAndLeaveOriginalUntouched(int codePage)
    {
        var document = EmmReader.Read(Bytes(Minimum), new() { AnsiCodePage = codePage });
        byte[] original = document.CopySourceBytes();
        Assert.Throws<EncoderFallbackException>(() => EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", -1, "\U0001f600.fx"));
        Assert.Throws<EncoderFallbackException>(() => EmmDocumentEditor.AddObject(document, false, "\U0001f600.x"));
        Assert.Equal(original, document.CopySourceBytes());
    }

    [Fact]
    public void SizeLimitCountsEncodedAnsiBytesAndGeneratedKeyNotUnicodeCharacters()
    {
        var document = Read(Minimum);
        string largest = new('a', 1016); // Pmd0= (5) + value + CRLF (2) == 1023 bytes.
        var edited = EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", -1, largest);
        Assert.Equal(largest, Entry(edited, "Effect", "Pmd0").EffectPath);
        Assert.Throws<ArgumentException>(() => EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", -1, largest + "a"));
        Assert.Throws<ArgumentException>(() => EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", 0, largest));
        Assert.Throws<ArgumentException>(() => EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", -1, new string('\u4e2d', 509)));
        Assert.Equal(Bytes(Minimum), document.CopySourceBytes());
    }

    [Fact]
    public void InvalidTargetOrSubsetFailsWithoutChangingSource()
    {
        var document = Read(Minimum);
        Assert.Throws<ArgumentException>(() => EmmDocumentEditor.SetEffect(document, "Effect@Missing", "Pmd0", -1, "a.fx"));
        Assert.Throws<ArgumentException>(() => EmmDocumentEditor.SetEffect(document, "Object", "Pmd0", -1, "a.fx"));
        Assert.Throws<ArgumentException>(() => EmmDocumentEditor.SetShown(document, "Object", "Pmd0", -1, true));
        Assert.Throws<ArgumentException>(() => EmmDocumentEditor.SetEffect(document, "Effect", "Pmd99", -1, "a.fx"));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", -2, "a.fx"));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmmDocumentEditor.SetShown(document, "Effect", "Pmd0", -2, true));
        Assert.Equal(Bytes(Minimum), document.CopySourceBytes());
    }

    [Fact]
    public void AssignmentEditsKeepDuplicateModelGuidMappingsAfterRenderOrderAndSourceChange()
    {
        string root = Path.Combine(Path.GetTempPath(), "mda-emm-authoring-context");
        var document = Read(Minimum.Replace("Acs1=stage.x", "Pmd1=actor.pmx", StringComparison.Ordinal)
            + "Pmd0=first.fx\nPmd1=second.fx\n");
        Guid first = new("9fd1c192-5811-457d-b780-4e043bfe8101");
        Guid second = new("9fd1c192-5811-457d-b780-4e043bfe8102");
        var original = EmmImportPlan.Create(document, Path.Combine(root, "scene.emm"),
            [new(first, true, "actor.pmx", 0), new(second, true, "actor.pmx", 1)], root);
        var rebound = EmmImportPlan.RebindLiveObjects(original,
            [new(first, true, "replacement.pmx", 5), new(second, true, "actor.pmx", 0)]);
        var edited = EmmDocumentEditor.SetEffect(document, "Effect", "Pmd0", 2, "edited.fx");

        var plan = EmmImportPlan.WithAssignments(rebound, edited);

        Assert.Equal(first, plan.MatchesByRecordKey["Pmd0"].ObjectId);
        Assert.Equal(second, plan.MatchesByRecordKey["Pmd1"].ObjectId);
        Assert.Equal("edited.fx", Entry(plan.Document, "Effect", "Pmd0").SubsetPaths[2]);
        Assert.Equal("first.fx", Entry(plan.Document, "Effect", "Pmd0").EffectPath);
        Assert.Equal("second.fx", Entry(plan.Document, "Effect", "Pmd1").EffectPath);
        Assert.Equal(rebound.StableMatchProvenance, plan.StableMatchProvenance);
        Assert.Equal(rebound.PathContext.EmmPath, plan.PathContext.EmmPath);
        Assert.Equal(rebound.PathContext.WorkingDirectory, plan.PathContext.WorkingDirectory);
        Assert.Same(edited, plan.Document);
        Assert.Same(document, original.Document);
        Assert.Empty(Entry(original.Document, "Effect", "Pmd0").SubsetPaths);
    }

    [Fact]
    public void AssignmentOnlyPlanRejectsChangedObjectTableAndRetainsOriginalPlan()
    {
        string root = Path.Combine(Path.GetTempPath(), "mda-emm-authoring-context");
        var document = Read(Minimum);
        Guid identity = new("9fd1c192-5811-457d-b780-4e043bfe8103");
        var plan = EmmImportPlan.Create(document, Path.Combine(root, "scene.emm"),
            [new(identity, true, "actor.pmx", 0)], root);
        byte[] source = document.CopySourceBytes();
        var addedObject = EmmDocumentEditor.AddObject(document, true, "new.pmx");
        var changedSource = Read(Minimum.Replace("actor.pmx", "replacement.pmx", StringComparison.Ordinal));

        Assert.Throws<ArgumentException>(() => EmmImportPlan.WithAssignments(plan, addedObject));
        Assert.Throws<ArgumentException>(() => EmmImportPlan.WithAssignments(plan, changedSource));
        Assert.Equal(identity, plan.MatchesByRecordKey["Pmd0"].ObjectId);
        Assert.Null(plan.StableMatchProvenance);
        Assert.Equal(source, plan.Document.CopySourceBytes());
    }

    [Fact]
    public void ExternalMappingUsesAbsoluteReferencesAndLeavesDocumentUntouched()
    {
        string root = Path.Combine(Path.GetTempPath(), "mda-emm-export-context");
        var document = Read("[Info]\nVersion=3\n[Object]\nPmd0=models/actor.pmx\n[Effect]\nDefault=none\nPmd0=fx/main.fx\n[Effect@Layer]\nOwner=Pmd0\nPmd0=main_default\nPmd0[1]=hide\n");
        var plan = EmmImportPlan.Create(document, Path.Combine(root, "scene.emm"),
            [new(Guid.NewGuid(), true, "models/actor.pmx", 0)], root);
        byte[] bytes = document.CopySourceBytes();
        var exported = EmmReader.Read(EmmDocumentEditor.ExportAbsolutePaths(plan));
        Assert.True(Path.IsPathFullyQualified(exported.Objects[0].Path));
        Assert.True(Path.IsPathFullyQualified(Entry(exported, "Effect", "Pmd0").EffectPath!));
        Assert.Equal("none", Section(exported, "Effect").DefaultPath);
        Assert.Equal("main_default", Entry(exported, "Effect@Layer", "Pmd0").EffectPath);
        Assert.Equal("hide", Entry(exported, "Effect@Layer", "Pmd0").SubsetPaths[1]);
        Assert.Equal(bytes, document.CopySourceBytes());
    }

    [Fact]
    public void ImplicitDuplicateInstancesCanReceiveDistinctNewRowsWithoutRebindingOldRows()
    {
        string root = Path.Combine(Path.GetTempPath(), "mda-emm-implicit-context");
        var document = Read("[Info]\nVersion=3\n[Object]\n[Effect]\nDefault=none\n");
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var plan = EmmImportPlan.Create(document, Path.Combine(root, "scene.emm"),
            [new(first, true, "actor.pmx", 0), new(second, true, "actor.pmx", 1)], root);
        var authored = EmmDocumentEditor.AddObject(EmmDocumentEditor.AddObject(document, true, "actor.pmx"), true, "actor.pmx");
        var assigned = EmmImportPlan.WithAssignments(plan, authored);
        Assert.Equal(first, assigned.MatchesByRecordKey["Pmd0"].ObjectId);
        Assert.Equal(second, assigned.MatchesByRecordKey["Pmd1"].ObjectId);
        var rebound = EmmImportPlan.RebindLiveObjects(assigned,
            [new(first, true, "actor.pmx", 1), new(second, true, "actor.pmx", 0)]);
        Assert.Equal(first, rebound.MatchesByRecordKey["Pmd0"].ObjectId);
        Assert.Equal(second, rebound.MatchesByRecordKey["Pmd1"].ObjectId);
        Assert.Empty(plan.Matches);
    }

    private static EmmDocument Read(string text) => EmmReader.Read(Bytes(text));
    private static byte[] Bytes(string text) => LegacyAnsiEncoding.Create(936).GetBytes(text);
    private static EmmEffectSection Section(EmmDocument document, string name) => Assert.Single(document.EffectSections, section => section.Name == name);
    private static EmmEffectEntry Entry(EmmDocument document, string section, string key) => Assert.Single(Section(document, section).Entries, entry => entry.ObjectKey == key);
}
