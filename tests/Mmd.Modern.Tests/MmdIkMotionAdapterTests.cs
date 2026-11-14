using Mmd.Modern.Application;
using Mmd.Modern.Formats;
using Xunit;
namespace Mmd.Modern.Tests;

public sealed class MmdIkMotionAdapterTests
{
    [Fact] public void SwitchesHoldPerBoneAndLastRecordWinsWithDeterministicSeek()
    {
        var motion=new VmdMotion {IkKeyframes=[
            new(){Frame=20,Switches=[new("left",true)]},
            new(){Frame=10,Switches=[new("left",false),new("right",false)]},
            new(){Frame=20,Switches=[new("left",false)]},
            new(){Frame=30,Switches=[new("right",true)]}]};
        var set=new MmdIkMotionAdapter(new Dictionary<string,int>{{"left",2},{"right",3}},motion);
        Assert.Empty(set.Evaluate(0));Assert.False(set.Evaluate(10)[2]);Assert.False(set.Evaluate(25)[2]);
        Assert.False(set.Evaluate(25)[3]);Assert.True(set.Evaluate(100)[3]);Assert.False(set.Evaluate(10)[3]);
        Assert.Equal(30u,set.DurationFrames);
    }
    [Fact] public void MissingBonesAreDiagnosedAndVisibilityCannotDisableIk()
    {
        var motion=new VmdMotion {IkKeyframes=[new(){Frame=5,Visible=false,Switches=[new("left",true),new("missing",false)]}]};
        var set=new MmdIkMotionAdapter(new Dictionary<string,int>{{"left",2}},motion);
        Assert.True(set.Evaluate(5)[2]);Assert.Contains(set.Diagnostics,d=>d.Contains("missing"));
        Assert.Contains(set.Diagnostics,d=>d.Contains("visibility",StringComparison.OrdinalIgnoreCase));
    }
}
