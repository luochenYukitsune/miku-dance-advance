using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Xunit;

namespace Mmd.Modern.Rendering.D3D9.Compatibility.Tests;

public sealed class EmmImportPlanTests
{
    private static readonly Guid First = new("21487dd1-0c03-4c35-8bad-4484355711f7");
    private static readonly Guid Second = new("21487dd1-0c03-4c35-8bad-4484355711f8");
    private static readonly Guid Third = new("21487dd1-0c03-4c35-8bad-4484355711f9");

    [Fact]
    public void CompletesAllExactMatchesBeforeAnyFileNameFallback()
    {
        var plan = Plan(Document("Pmd0 = C:\\source\\same.pmx\nPmd1 = C:\\other\\same.pmx"),
            Binding(First, true, "C:\\moved\\same.pmx", 0),
            Binding(Second, true, "C:\\source\\same.pmx", 1));

        AssertMatch(plan, "Pmd0", Second, "RawPath");
        AssertMatch(plan, "Pmd1", First, "FileName");
        Assert.Empty(Items(plan, "UnmatchedObjects"));
        Assert.Empty(Items(plan, "UnmatchedRecords"));
    }

    [Fact]
    public void RepeatedFilesConsumeDistinctRowsInRenderOrderAndSlotOrder()
    {
        var plan = Plan(Document("Pmd9 = C:\\same.pmx\nPmd2 = C:\\same.pmx\nPmd5 = C:\\same.pmx"),
            Binding(First, true, "C:\\same.pmx", 30),
            Binding(Second, true, "C:\\same.pmx", -10),
            Binding(Third, true, "C:\\same.pmx", 0));

        AssertMatch(plan, "Pmd2", Second, "RawPath");
        AssertMatch(plan, "Pmd5", Third, "RawPath");
        AssertMatch(plan, "Pmd9", First, "RawPath");
    }

    [Fact]
    public void EqualRenderOrdersRetainCallerEnumerationOrder()
    {
        var plan = Plan(Document("Pmd3 = C:\\same.pmx\nPmd1 = C:\\same.pmx"),
            Binding(Second, true, "C:\\same.pmx", 5), Binding(First, true, "C:\\same.pmx", 5));

        AssertMatch(plan, "Pmd1", Second, "RawPath");
        AssertMatch(plan, "Pmd3", First, "RawPath");
    }

    [Fact]
    public void ModelAndAccessoryPathsNeverCrossKinds()
    {
        var plan = Plan(Document("Acs0 = C:\\same.x\nPmd0 = C:\\same.x"),
            Binding(First, true, "C:\\same.x", 1), Binding(Second, false, "C:\\same.x", 0));

        AssertMatch(plan, "Pmd0", First, "RawPath");
        AssertMatch(plan, "Acs0", Second, "RawPath");
        var wrongKind = Plan(Document("Acs0 = C:\\same.x"), Binding(First, true, "C:\\same.x", 0));
        Assert.Empty(Items(wrongKind, "Matches"));
        Assert.Single(Items(wrongKind, "UnmatchedObjects"));
        Assert.Single(Items(wrongKind, "UnmatchedRecords"));
    }

    [Theory]
    [InlineData("models\\a.pmx", "C:\\work\\models\\a.pmx", "WorkingDirectoryPath")]
    [InlineData("..\\models\\a.pmx", "D:\\projects\\models\\a.pmx", "EmmDirectoryPath")]
    [InlineData("D:/projects/scene/./a.pmx", "d:\\PROJECTS\\scene\\a.pmx", "WorkingDirectoryPath")]
    [InlineData("C:\\WORK\\a.pmx", "c:\\work\\A.PMX", "RawPath")]
    public void ResolvesOnlyAgainstFrozenExplicitPathContexts(string record, string live, string reason)
    {
        var plan = Plan(Document($"Pmd0 = {record}"), Binding(First, true, live, 0));

        AssertMatch(plan, "Pmd0", First, reason);
        var context = Property(plan, "PathContext");
        Assert.Equal("D:\\projects\\scene\\mapping.emm", Property(context, "EmmPath"));
        Assert.Equal("D:\\projects\\scene", Property(context, "EmmDirectory"));
        Assert.Equal("C:\\work", Property(context, "WorkingDirectory"));
    }

    [Fact]
    public void PreservesPinnedNativeFullPathBufferSuffixFallback()
    {
        var record = new string('a', 260) + "\\model.pmx";
        var live = "C:\\relocated\\" + record;
        var plan = Plan(Document($"Pmd0 = {record}"), Binding(First, true, live, 0));

        AssertMatch(plan, "Pmd0", First, "PathSuffix");
    }

    [Fact]
    public void FileNameFallbackComparesExtensionAndCaseWithoutLookingUpFiles()
    {
        var plan = Plan(Document("Pmd0 = Z:\\not-present\\MODEL.pmx\nPmd1 = Z:\\not-present\\only.pmd"),
            Binding(First, true, "Y:\\also-not-present\\model.PMX", 0),
            Binding(Second, true, "Y:\\also-not-present\\only.pmx", 1));

        AssertMatch(plan, "Pmd0", First, "FileName");
        Assert.Equal(Second, Property(Assert.Single(Items(plan, "UnmatchedObjects")), "ObjectId"));
        Assert.Equal("Pmd1", Property(Assert.Single(Items(plan, "UnmatchedRecords")), "Key"));
        Assert.Equal(2, Items(plan, "Diagnostics").Length);
    }

    [Fact]
    public void RetainsOriginalDocumentAndEveryNamedOwnerAndShowScope()
    {
        var document = Document("Pmd0 = C:\\a.pmx", """
            [Effect]
            Default = relative-default.fx
            Pmd0 = relative-base.fx
            Pmd0.show = false
            Pmd0[2] = relative-subset.fx
            [Effect@Mirror]
            Owner = Pmd0[2]
            Pmd0 = relative-offscreen.fx
            Pmd0[2].show = true
            [Effect@Mirror(1)]
            Owner = Pmd0@Mirror
            Pmd0 = none
            """);
        var sections = Items(document, "EffectSections");
        Assert.Equal(3, sections.Length);
        var plan = Plan(document, Binding(First, true, "C:\\a.pmx", 0));

        Assert.Same(document, Property(plan, "Document"));
        Assert.Equal(sections, Items(Property(plan, "Document"), "EffectSections"));
        Assert.Equal("relative-default.fx", Property(sections[0], "DefaultPath"));
        Assert.Equal("Effect@Mirror", Property(sections[1], "Name"));
        Assert.NotNull(Property(sections[1], "Owner"));
        AssertMatch(plan, "Pmd0", First, "RawPath");
    }

    [Fact]
    public void FreezesCallerListAndExposesNoMutableCollections()
    {
        var inputs = new List<object> { Binding(First, true, "C:\\a.pmx", 0) };
        var plan = Plan(Document("Pmd0 = C:\\a.pmx"), inputs);
        inputs.Clear();

        Assert.Single(Items(plan, "LiveObjects"));
        AssertMatch(plan, "Pmd0", First, "RawPath");
        foreach (var name in new[] { "LiveObjects", "Matches", "UnmatchedObjects", "UnmatchedRecords", "Diagnostics" })
        {
            var list = Assert.IsAssignableFrom<IList>(Property(plan, name));
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
    }

    [Fact]
    public void RejectsMissingDocumentCollectionAndBindingsAndDuplicateIdentities()
    {
        Assert.Throws<ArgumentNullException>(() => Plan(null!, Binding(First, true, "C:\\a.pmx", 0)));
        Assert.Throws<ArgumentNullException>(() => InvokeCreate(Document("Pmd0 = C:\\a.pmx"), null));
        Assert.Throws<ArgumentException>(() => Plan(Document("Pmd0 = C:\\a.pmx"),
            Binding(First, true, "C:\\a.pmx", 0), Binding(First, false, "C:\\b.x", 1)));
        Assert.Throws<ArgumentException>(() => Plan(Document("Pmd0 = C:\\a.pmx"), new object[] { null! }));
    }

    [Fact]
    public void RejectsEmptyIdentityAndMissingOrNulPaths()
    {
        Assert.Throws<ArgumentException>(() => Binding(Guid.Empty, true, "C:\\a.pmx", 0));
        Assert.Throws<ArgumentNullException>(() => Binding(First, true, null!, 0));
        Assert.Throws<ArgumentException>(() => Binding(First, true, "", 0));
        Assert.Throws<ArgumentException>(() => Binding(First, true, "a\0.pmx", 0));
    }

    [Fact]
    public void RejectsRelativePathContextsBeforeAnyMatching()
    {
        var document = Document("Pmd0 = a.pmx");
        var inputs = new[] { Binding(First, true, "a.pmx", 0) };
        Assert.Throws<ArgumentException>(() => InvokeCreate(document, inputs, "relative.emm", "C:\\work"));
        Assert.Throws<ArgumentException>(() => InvokeCreate(document, inputs, "D:\\mapping.emm", "relative"));
        Assert.Throws<ArgumentException>(() => InvokeCreate(document, inputs, "D:\\map\0.emm", "C:\\work"));
    }

    private static object Document(string objects, string? effects = null)
    {
        var text = $"[Info]\nVersion = 3\n[Object]\n{objects}\n" + (effects ?? "[Effect]\nDefault = none\n");
        var reader = typeof(ExperimentalD3D9SurfaceSession).Assembly.GetType("Mmd.Modern.Rendering.D3D9.Compatibility.EmmReader", throwOnError: true)!;
        var method = reader.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "Read" && m.GetParameters()[0].ParameterType == typeof(Stream));
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));
        var args = method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        args[0] = stream;
        return Invoke(method, null, args)!;
    }

    private static object Binding(Guid id, bool model, string path, int order)
    {
        var type = CompatibilityType("EmmObjectBinding");
        try { return Activator.CreateInstance(type, id, model, path, order)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static object Plan(object document, params object[] objects) => Plan(document, (IEnumerable<object>)objects);
    private static object Plan(object document, IEnumerable<object> objects) => InvokeCreate(document, objects);

    private static object InvokeCreate(object document, IEnumerable<object>? objects,
        string emmPath = "D:\\projects\\scene\\mapping.emm", string? workingDirectory = "C:\\work")
    {
        Array? inputs = null;
        if (objects is not null)
        {
            var source = objects.ToArray();
            inputs = Array.CreateInstance(CompatibilityType("EmmObjectBinding"), source.Length);
            for (var i = 0; i < source.Length; i++) inputs.SetValue(source[i], i);
        }
        var method = CompatibilityType("EmmImportPlan").GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
        return Invoke(method, null, [document, emmPath, inputs, workingDirectory])!;
    }

    private static void AssertMatch(object plan, string key, Guid id, string reason)
    {
        var match = Assert.Single(Items(plan, "Matches"), m => (string)Property(m, "RecordKey") == key);
        Assert.Equal(id, Property(match, "ObjectId"));
        Assert.Equal(reason, Property(match, "Reason").ToString());
    }

    private static Type CompatibilityType(string name) => typeof(ExperimentalD3D9SurfaceSession).Assembly.GetType($"Mmd.Modern.Rendering.D3D9.Compatibility.{name}", true)!;
    private static object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
    private static object[] Items(object value, string name) => ((IEnumerable)Property(value, name)).Cast<object>().ToArray();
    private static object? Invoke(MethodInfo method, object? target, object?[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
