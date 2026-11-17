using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

/// <summary>离屏命名页同步的纯 CPU 契约：不读文件、不编译 FX。</summary>
public sealed class EmmOffscreenPagesTests
{
    private const string Minimum = "[Info]\nVersion=3\n[Object]\nPmd0=actor.pmx\nAcs1=stage.x\n[Effect]\nDefault=none\n";
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MdaOffscreenPages"));
    private static string Fx(string name) => Path.Combine(Root, name);

    [Fact]
    public void NewWholeAndSubsetFxGenerateOwnerOnlyPagesInDeclarationOrder()
    {
        var previous = Read(Minimum);
        var current = EmmDocumentEditor.SetEffect(EmmDocumentEditor.SetEffect(previous, "Effect", "Pmd0", -1, Fx("glow.fx")),
            "Effect", "Pmd0", 2, Fx("mask.fx"));
        var asked = new List<string>();

        var result = EmmOffscreenPages.Synchronize(previous, current, Root, path =>
        {
            asked.Add(path);
            return Path.GetFileName(path) == "glow.fx" ? ["GlowRT", "DepthRT"] : ["DepthRT"];
        });

        Assert.Equal(new[] { Fx("glow.fx"), Fx("mask.fx") }, asked);
        Assert.Equal(new[] { "Effect@GlowRT", "Effect@DepthRT", "Effect@DepthRT(1)" }, result.Added);
        Assert.Empty(result.Removed);
        Assert.Equal(new EmmOwnerReference("Pmd0"), Section(result.Document, "Effect@GlowRT").Owner);
        Assert.Equal(new EmmOwnerReference("Pmd0", 2), Section(result.Document, "Effect@DepthRT(1)").Owner);
        Assert.All(result.Document.EffectSections.Where(section => !section.IsDefault), section => Assert.Empty(section.Entries));
        Assert.StartsWith(Encoding.ASCII.GetString(current.CopySourceBytes()), Encoding.ASCII.GetString(result.Document.CopySourceBytes()));
    }

    [Fact]
    public void ExistingPagesAndUserRowsAreKeptAndUnchangedRowsAreNotInspected()
    {
        var previous = Read(Minimum + "Acs1=" + Fx("stage.fx") + "\n[Effect@GlowRT]\nOwner=Pmd0\nAcs1=hide\n");
        var current = EmmDocumentEditor.SetEffect(previous, "Effect", "Pmd0", -1, Fx("glow.fx"));

        var result = EmmOffscreenPages.Synchronize(previous, current, Root, path =>
            path == Fx("glow.fx") ? ["GlowRT"] : throw new InvalidOperationException("Unchanged rows must not be inspected."));

        Assert.Empty(result.Added); Assert.Empty(result.Removed);
        Assert.Equal(current.CopySourceBytes(), result.Document.CopySourceBytes());
        Assert.Equal("hide", Assert.Single(Section(result.Document, "Effect@GlowRT").Entries).EffectPath);
    }

    [Fact]
    public void ReplacingFxRemovesUntouchedStalePagesIncludingNestedChildren()
    {
        var previous = Read(Minimum + "Pmd0=" + Fx("old.fx") + "\n[Effect@OldRT]\nOwner=Pmd0\n[Effect@InnerRT]\nOwner=Acs1@OldRT\n");
        var current = EmmDocumentEditor.SetEffect(previous, "Effect", "Pmd0", -1, Fx("new.fx"));

        var result = EmmOffscreenPages.Synchronize(previous, current, Root, _ => ["NewRT"]);

        Assert.Equal(new[] { "Effect@InnerRT", "Effect@OldRT" }, result.Removed);
        Assert.Equal(new[] { "Effect@NewRT" }, result.Added);
        Assert.Equal(new[] { "Effect", "Effect@NewRT" }, result.Document.EffectSections.Select(section => section.Name));
    }

    [Theory]
    [InlineData("none")]
    [InlineData(null)]
    public void RemovingFxOrResettingToInheritedRemovesItsUntouchedPages(string? value)
    {
        var previous = Read(Minimum + "Pmd0=" + Fx("old.fx") + "\n[Effect@OldRT]\nOwner=Pmd0\n");
        var current = EmmDocumentEditor.SetEffect(previous, "Effect", "Pmd0", -1, value);

        var result = EmmOffscreenPages.Synchronize(previous, current, Root, _ => throw new InvalidOperationException());

        Assert.Equal(new[] { "Effect@OldRT" }, result.Removed);
        Assert.Equal(Read(Minimum + (value is null ? "" : "Pmd0=" + value + "\r\n")).CopySourceBytes(), result.Document.CopySourceBytes());
    }

    [Fact]
    public void UserEditedStalePageBlocksTheEditWithoutChangingInput()
    {
        var previous = Read(Minimum + "Pmd0=" + Fx("old.fx") + "\n[Effect@OldRT]\nOwner=Pmd0\nAcs1=none\n");
        var current = EmmDocumentEditor.SetEffect(previous, "Effect", "Pmd0", -1, Fx("new.fx"));
        byte[] bytes = current.CopySourceBytes();

        var error = Assert.Throws<EmmOffscreenPageConflictException>(() =>
            EmmOffscreenPages.Synchronize(previous, current, Root, _ => []));

        Assert.Equal(new[] { "Effect@OldRT" }, error.Sections);
        Assert.Equal(bytes, current.CopySourceBytes());
    }

    [Fact]
    public void ConfirmedRemovalDeletesEditedAndMmeWrittenStalePagesWithTheirChildren()
    {
        // MME 保存时给每个对象写一行并带 .show：这种页不算“未改动”，确认后照样删除，嵌套子页一起删。
        var previous = Read(Minimum + "Pmd0=" + Fx("old.fx") + "\n[Effect@OldRT]\nOwner=Pmd0\nPmd0=none\nAcs1=hide.fx\nAcs1.show=false\n"
            + "[Effect@ChildRT]\nOwner=Acs1@OldRT\nPmd0=none\n");
        var current = EmmDocumentEditor.SetEffect(previous, "Effect", "Pmd0", -1, Fx("new.fx"));

        var result = EmmOffscreenPages.Synchronize(previous, current, Root, _ => ["NewRT"], removeEditedPages: true);

        Assert.Equal(new[] { "Effect@ChildRT", "Effect@OldRT" }, result.Removed);
        Assert.Equal(new[] { "Effect@NewRT" }, result.Added);
        Assert.DoesNotContain(result.Document.EffectSections, section => section.Name is "Effect@OldRT" or "Effect@ChildRT");
    }

    [Fact]
    public void FxAssignedInsideANamedPageCreatesANestedPageWithParentLabel()
    {
        var previous = Read(Minimum + "[Effect@GlowRT]\nOwner=Pmd0\n");
        var current = EmmDocumentEditor.SetEffect(previous, "Effect@GlowRT", "Acs1", -1, Fx("inner.fx"));

        var result = EmmOffscreenPages.Synchronize(previous, current, Root, _ => ["InnerRT"]);

        Assert.Equal(new EmmOwnerReference("Acs1", -1, "GlowRT"), Section(result.Document, "Effect@InnerRT").Owner);
        Assert.Equal(new[] { "Effect@InnerRT" }, result.Added);
    }

    [Fact]
    public void NamedPageSymbolsAreNotFxFilesButBaseSectionKeepsThemAsFileNames()
    {
        var previous = Read(Minimum + "[Effect@GlowRT]\nOwner=Pmd0\n");
        var current = EmmDocumentEditor.SetEffect(previous, "Effect@GlowRT", "Acs1", -1, "main_default");
        var owners = EmmOffscreenPages.ChangedOwners(previous, current, Root);
        Assert.Null(Assert.Single(owners).OriginAbsoluteFxPath);

        var baseRow = EmmDocumentEditor.SetEffect(previous, "Effect", "Acs1", -1, "main_default");
        Assert.Equal(Fx("main_default"), Assert.Single(EmmOffscreenPages.ChangedOwners(previous, baseRow, Root)).OriginAbsoluteFxPath);
    }

    [Fact]
    public void MainProfilesMustDeclareTheSameTargets()
    {
        var identity = new D3D9EffectDependencyCompilerIdentity("fixture", new string('a', 64), 64, 936, new string('b', 64));
        D3D9EffectDependencyInspection Inspection(params D3D9EffectOffscreenDeclaration[] values) =>
            new(D3D9EffectDependencyStatus.Complete, identity, [], [], null, values);

        var same = Inspection(new("effect-mip-off", "ZRT"), new("effect-mip-on", "ZRT"), new("effect-mip-off", "ART"), new("effect-mip-on", "ART"));
        Assert.Equal(new[] { "ART", "ZRT" }, same.MainOffscreenTargetNames());
        var error = Assert.Throws<D3D9EffectDependencyException>(() => Inspection(new D3D9EffectOffscreenDeclaration("effect-mip-on", "MipOnly")).MainOffscreenTargetNames());
        Assert.Equal("offscreen-profile-mismatch", error.Code);
    }

    private static EmmDocument Read(string text) => EmmReader.Read(LegacyAnsiEncoding.Create(936).GetBytes(text));
    private static EmmEffectSection Section(EmmDocument document, string name) => Assert.Single(document.EffectSections, section => section.Name == name);
}
