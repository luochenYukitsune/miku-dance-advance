using System.Numerics;
using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class PmmMotionAdapterTests
{
    private static PmmString Name(string value) => new(0, value, Encoding.ASCII.GetBytes(value));
    private static readonly byte[] Curves = [0, 42, 0, 85, 42, 0, 85, 0, 20, 20, 107, 107, 10, 60, 100, 120];
    private static PmmBoneKeyframe Bone(int record, int next = 0, int previous = 0, int frame = 0, float x = 0, int? objectIndex = null)
        => new() { RecordIndex = record, ObjectIndex = objectIndex ?? record, NextIndex = next, PreviousIndex = previous, Frame = frame,
            Translation = new(x, 0, 0), Rotation = Quaternion.Identity, Interpolation = Curves };

    [Fact]
    public void InterleavedRecordOrderMapsThroughLinksAndExactNamesRatherThanPmxIndices()
    {
        var source = new PmmModel
        {
            Name = Name("display"), BoneNames = [Name("A"), Name("B")],
            InitialBoneKeyframes = [Bone(0, 400), Bone(1, 300)],
            BoneKeyframes = [Bone(2, previous: 1, frame: 10, x: 22, objectIndex: 300), Bone(3, previous: 0, frame: 20, x: 33, objectIndex: 400)],
        };
        var result = PmmMotionAdapter.Convert(source, Pmx(["B", "A"]));
        Assert.Equal("B", result.BoneKeyframes.Single(k => k.Translation.X == 22).BoneName);
        Assert.Equal("A", result.BoneKeyframes.Single(k => k.Translation.X == 33).BoneName);
        Assert.Equal(20u, result.BoneKeyframes.Single(k => k.Translation.X == 33).Frame);
        var curve = result.BoneKeyframes[0].Interpolation;
        for (int channel = 0; channel < 4; channel++)
            for (int coordinate = 0; coordinate < 4; coordinate++)
                Assert.Equal(Curves[4 * channel + coordinate], curve[16 * channel + 4 * coordinate]);
        var clip = MmdBoneMotionAdapter.Convert(result);
        Assert.Equal(42f / 127, clip.Tracks.Single(t => t.BoneName == "A").Keys.Last().Curves.X.Y1);
    }

    [Fact]
    public void MorphChainsAndIkConstraintsUsePmmNamesRatherThanModelBoneOrder()
    {
        var source = new PmmModel
        {
            BoneNames = [Name("A"), Name("B")], InitialBoneKeyframes = [Bone(0), Bone(1)],
            MorphNames = [Name("smile"), Name("blink")],
            InitialMorphKeyframes = [new() { RecordIndex = 0, ObjectIndex = 0, NextIndex = 3 }, new() { RecordIndex = 1, ObjectIndex = 1, NextIndex = 2 }],
            MorphKeyframes = [new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 1, Frame = 8, Weight = .7f }, new() { RecordIndex = 3, ObjectIndex = 3, PreviousIndex = 0, Frame = 9, Weight = .3f }],
            ConstraintBoneIndices = [1], InitialModelKeyframe = new() { RecordIndex = 0, Visible = false, ConstraintEnabled = [false] },
        };
        var motion = PmmMotionAdapter.Convert(source, Pmx(["B", "A"], ["blink", "smile"]));
        Assert.Equal("blink", motion.MorphKeyframes.Single(k => k.Weight == .7f).MorphName);
        Assert.Equal("smile", motion.MorphKeyframes.Single(k => k.Weight == .3f).MorphName);
        Assert.Equal(new VmdIkSwitch("B", false), Assert.Single(Assert.Single(motion.IkKeyframes).Switches));
        Assert.False(motion.IkKeyframes[0].Visible);
    }

    [Fact]
    public void BrokenOrCyclicLinksAreRejectedBeforeAnyMotionCanReplaceActiveProject()
    {
        var orphan = new PmmModel { BoneNames = [Name("A")], InitialBoneKeyframes = [Bone(0)], BoneKeyframes = [Bone(1, frame: 1)] };
        Assert.Throws<InvalidDataException>(() => PmmMotionAdapter.Convert(orphan, Pmx(["A"])));
        var cyclic = new PmmModel { BoneNames = [Name("A")], InitialBoneKeyframes = [Bone(0, 1)], BoneKeyframes = [Bone(1, 1, frame: 1)] };
        Assert.Throws<InvalidDataException>(() => PmmMotionAdapter.Convert(cyclic, Pmx(["A"])));
    }

    [Fact]
    public void ReplacementModelReportsUnmatchedTracksAndKeepsNamedKeysDespiteReorderedSkeleton()
    {
        var source = new PmmModel
        {
            BoneNames = [Name("A"), Name("B"), Name("old-only")],
            InitialBoneKeyframes = [Bone(0, 3), Bone(1), Bone(2)],
            BoneKeyframes = [Bone(3, previous: 0, frame: 17, x: 42)],
            MorphNames = [Name("smile"), Name("old-morph")],
            InitialMorphKeyframes = [new() { RecordIndex = 0, ObjectIndex = 0, NextIndex = 2 }, new() { RecordIndex = 1, ObjectIndex = 1 }],
            MorphKeyframes = [new() { RecordIndex = 2, ObjectIndex = 2, PreviousIndex = 0, Frame = 19, Weight = .75f }]
        };
        var replacement = Pmx(["new-only", "B", "A"], ["new-morph", "smile"]);
        var report = PmmMotionAdapter.InspectBinding(source, replacement);
        Assert.Equal((2, 3, 1, 2), (report.MatchedBones, report.TotalBones, report.MatchedMorphs, report.TotalMorphs));
        Assert.Equal(["old-only"], report.UnmatchedBones);
        Assert.Equal(["old-morph"], report.UnmatchedMorphs);
        var motion = PmmMotionAdapter.Convert(source, replacement);
        Assert.Equal("A", motion.BoneKeyframes.Single(k => k.Frame == 17).BoneName);
        Assert.Equal(42, motion.BoneKeyframes.Single(k => k.Frame == 17).Translation.X);
        Assert.Equal("smile", motion.MorphKeyframes.Single(k => k.Frame == 19).MorphName);
        Assert.Equal(.75f, motion.MorphKeyframes.Single(k => k.Frame == 19).Weight);
        Assert.Contains(motion.Diagnostics, d => d.Contains("old-only"));
        Assert.Contains(motion.Diagnostics, d => d.Contains("old-morph"));
        Assert.Equal(3, source.InitialBoneKeyframes.Count);
        Assert.Equal(2, source.InitialMorphKeyframes.Count);
    }

    [Fact]
    public void ReplacementBindingReportRejectsAmbiguousNamesInsteadOfChoosingAnIndex()
    {
        var source = new PmmModel { BoneNames = [Name("A")], InitialBoneKeyframes = [Bone(0)] };
        Assert.Throws<InvalidDataException>(() => PmmMotionAdapter.InspectBinding(source, Pmx(["A", "A"])));
    }

    [Fact]
    public void RawCp932NamesMatchPmxEvenWhenPmmDisplayWasDecodedAsCp936()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] raw = Encoding.GetEncoding(932).GetBytes("センター");
        var source = new PmmModel { BoneNames = [new(0, Encoding.GetEncoding(936).GetString(raw), raw)], InitialBoneKeyframes = [Bone(0)] };
        Assert.Equal("センター", Assert.Single(PmmMotionAdapter.Convert(source, Pmx(["センター"])).BoneKeyframes).BoneName);
        Assert.Throws<InvalidDataException>(() => PmmMotionAdapter.Convert(source, Pmx(["unrelated"])));
    }

    [Fact]
    public void FirstInitialRecordsStalePreviousSentinelDoesNotChangeItsNameOwner()
    {
        var pmm = new PmmModel
        {
            BoneNames = [Name("A"), Name("B")],
            InitialBoneKeyframes = [Bone(0, 100, previous: 1), Bone(1)],
            BoneKeyframes = [Bone(2, previous: 0, frame: 1, x: 12, objectIndex: 100)],
        };
        var motion = PmmMotionAdapter.Convert(pmm, Pmx(["B", "A"]));
        Assert.Equal("A", motion.BoneKeyframes.Single(k => k.Translation.X == 12).BoneName);
    }

    internal static PmxModel Pmx(string[] names, string[]? morphs = null) => new()
    {
        Header = new() { Version = 2, HeaderDataSize = 8, TextEncoding = PmxTextEncoding.Utf8, AdditionalUvCount = 0,
            VertexIndexSize = 1, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 1, MorphIndexSize = 1, RigidBodyIndexSize = 1 },
        Name = "model", EnglishName = "", Comment = "", EnglishComment = "", Vertices = [], Faces = [], Textures = [], Materials = [],
        Bones = names.Select(n => new PmxBone { Name = n, EnglishName = "", Position = Vector3.Zero, ParentIndex = -1, DeformDepth = 0, Flags = PmxBoneFlags.None }).ToArray(),
        Morphs = (morphs ?? []).Select(n => new PmxMorph { Name = n, EnglishName = "", ControlPanel = 0, Type = PmxMorphType.Position }).ToArray(),
        DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
    };
}
