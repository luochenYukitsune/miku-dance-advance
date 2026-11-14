using System.Collections;
using System.Text;
using Mmd.Modern.Formats;
using Xunit;
namespace Mmd.Modern.Formats.Tests;

public sealed class VmdIkTrackTests
{
    [Fact] public void KeepsIkSwitchesFrameVisibilityAndRecordOrder()
    {
        using var stream=Fixture(false);
        var motion=VmdReader.Read(stream);
        var property=typeof(VmdMotion).GetProperty("IkKeyframes");Assert.NotNull(property);
        var keys=((IEnumerable)property.GetValue(motion)!).Cast<object>().ToArray();Assert.Equal(2,keys.Length);
        Assert.Equal(20u,keys[0].GetType().GetProperty("Frame")!.GetValue(keys[0]));
        Assert.Equal(false,keys[0].GetType().GetProperty("Visible")!.GetValue(keys[0]));
        var switches=((IEnumerable)keys[0].GetType().GetProperty("Switches")!.GetValue(keys[0])!).Cast<object>().ToArray();
        Assert.Equal("左足ＩＫ",switches[0].GetType().GetProperty("BoneName")!.GetValue(switches[0]));
        Assert.Equal(false,switches[0].GetType().GetProperty("Enabled")!.GetValue(switches[0]));
        Assert.Equal(10u,keys[1].GetType().GetProperty("Frame")!.GetValue(keys[1]));
    }
    [Fact] public void RejectsMalformedIkToggleAndKeepsPreciseOffset()
    {
        using var stream=Fixture(true);var error=Assert.Throws<VmdFormatException>(()=>VmdReader.Read(stream));
        Assert.Equal("ik[0].bones[0].enabled",error.Field);Assert.Equal(103,error.Offset);
    }
    private static MemoryStream Fixture(bool invalid)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var stream=new MemoryStream();
        using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))
        {
            void Name(string value,int length){var bytes=new byte[length];Encoding.GetEncoding(932).GetBytes(value).CopyTo(bytes,0);writer.Write(bytes);}
            Name("Vocaloid Motion Data 0002",30);Name("character",20);
            for(int i=0;i<5;i++)writer.Write(0u);
            writer.Write(2u);writer.Write(20u);writer.Write((byte)0);writer.Write(1u);Name("左足ＩＫ",20);writer.Write(invalid?(byte)2:(byte)0);
            writer.Write(10u);writer.Write((byte)1);writer.Write(1u);Name("左足ＩＫ",20);writer.Write((byte)1);
        }
        stream.Position=0;return stream;
    }
}
