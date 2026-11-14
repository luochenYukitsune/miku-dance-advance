using Mmd.Modern.App;
using Mmd.Modern.Formats;
using Mmd.Modern.Rendering.D3D9.Compatibility;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorClassicPmmEmmBindingTests
{
    private static readonly Guid First = new("f348691b-2dad-41b1-9fb3-87180ac2cf31");
    private static readonly Guid Second = new("f348691b-2dad-41b1-9fb3-87180ac2cf32");

    [Fact]
    public void RepairedDuplicatePmmPathsKeepOriginalRenderOrderAssignmentsAndOwnerSubsets()
    {
        const string original = @"C:\original\僼傿僽儕儞.pmx";
        var bytes = LegacyAnsiEncoding.Create(936).GetBytes($"""
            [Info]
            Version=3
            [Object]
            Pmd3={original}
            Pmd4={original}
            [Effect]
            Default=none
            Pmd3[2]=none
            Pmd4.show=false
            [Effect@Depth]
            Owner=Pmd3[2]
            Pmd4=hide
            """);
        var document = EmmReader.Read(bytes);
        var current = new[]
        {
            new EmmObjectBinding(First, true, @"D:\repaired\first.pmx", 20),
            new EmmObjectBinding(Second, true, @"D:\repaired\second.pmx", 10)
        };
        var plan = EditorProjectInterchange.CreateClassicPmmEffectsPlan(document, @"C:\project\source.emm", current,
            new Dictionary<Guid, string> { [First] = original, [Second] = original }, @"C:\project");

        Assert.Equal(Second, plan.MatchesByRecordKey["Pmd3"].ObjectId);
        Assert.Equal(First, plan.MatchesByRecordKey["Pmd4"].ObjectId);
        Assert.Empty(plan.UnmatchedObjects);
        Assert.Empty(plan.UnmatchedRecords);
        Assert.Equal(current.OrderBy(value => value.RenderOrder), plan.LiveObjects);
        Assert.All(plan.StableMatchProvenance!, value => Assert.Equal(original, value.OriginalSourcePath));
        Assert.Equal(bytes, plan.Document.CopySourceBytes());
        var runtime = D3D9EmmRuntimeConfiguration.Create(plan,
            [new(First, true, null), new(Second, true, null)], new D3D9EffectResourceMap([]));
        Assert.Equal(Second, runtime.Sections[1].Owner!.ObjectId);
        Assert.Equal(2, runtime.Sections[1].Owner!.Subset);
        Assert.Equal(First, Assert.Single(runtime.Sections[1].Entries).ObjectId);
        Assert.Contains(2, Assert.Single(runtime.Sections[0].Entries, value => value.ObjectId == Second).SubsetEffects.Keys);
        Assert.False(Assert.Single(runtime.Sections[0].Entries, value => value.ObjectId == First).Shown);
    }

    [Fact]
    public void OriginallyUnmatchedIdentityCanStillMatchItsRepairedCurrentPath()
    {
        var document = EmmReader.Read("[Info]\nVersion=3\n[Object]\nPmd0=C:\\old\\first.pmx\nPmd1=D:\\repaired\\second.pmx\n[Effect]\nDefault=none\nPmd0=none\nPmd1[1].show=false\n"u8);
        var current = new[]
        {
            new EmmObjectBinding(First, true, @"D:\repaired\first.pmx", 0),
            new EmmObjectBinding(Second, true, @"D:\repaired\second.pmx", 1)
        };
        var plan = EditorProjectInterchange.CreateClassicPmmEffectsPlan(document, @"C:\project\source.emm", current,
            new Dictionary<Guid, string> { [First] = @"C:\old\first.pmx", [Second] = @"C:\old\unmatched.pmx" }, @"C:\project");

        Assert.Equal(First, plan.MatchesByRecordKey["Pmd0"].ObjectId);
        Assert.Equal(Second, plan.MatchesByRecordKey["Pmd1"].ObjectId);
        Assert.Empty(plan.UnmatchedObjects);
        Assert.Empty(plan.UnmatchedRecords);
        var proof = plan.StableMatchProvenance!.ToDictionary(value => value.ObjectId);
        Assert.Equal(@"C:\old\first.pmx", proof[First].OriginalSourcePath);
        Assert.Equal(@"D:\repaired\second.pmx", proof[Second].OriginalSourcePath);
        var restored = EmmImportPlan.RestoreStableBindings(document, plan.PathContext.OriginalEmmPath,
            plan.LiveObjects, plan.PathContext.OriginalWorkingDirectory, plan.StableMatchProvenance!);
        Assert.Equal(plan.Matches, restored.Matches);
    }
}
