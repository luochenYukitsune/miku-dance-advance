using System.Numerics;
using System.Text.Json;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class VpdMotionAdapterTests
{
    [Fact]
    public void MatchesExactNamesNotRecordIndicesAndPreservesRawComponentsAndInput()
    {
        var model = PmmMotionAdapterTests.Pmx(["B", "A"], ["smile"]);
        var pose = new VpdPose { ParentFileName = "other.pmd", Bones =
            [new() { Index = 900, BoneName = "A", Translation = new(1, 2, 3), Rotation = new(.1f, -.2f, .3f, 2) },
             new() { Index = 4, BoneName = "B", Translation = new(4, 5, 6) }],
            Morphs = [new() { Index = 800, MorphName = "smile", Weight = -.5f }] };
        string original = JsonSerializer.Serialize(pose);
        var motion = VpdMotionAdapter.Convert(pose, model, 123);
        Assert.Equal(model.Name, motion.ModelName);
        Assert.Equal(new[] { "A", "B" }, motion.BoneKeyframes.Select(k => k.BoneName));
        Assert.All(motion.BoneKeyframes, k => Assert.Equal(123u, k.Frame));
        Assert.Equal(new(.1f, -.2f, .3f, 2), motion.BoneKeyframes[0].Rotation);
        Assert.Equal(new(1, 2, 3), motion.BoneKeyframes[0].Translation);
        Assert.Equal(-.5f, Assert.Single(motion.MorphKeyframes).Weight);
        Assert.Equal(123u, motion.MorphKeyframes[0].Frame);
        Assert.Empty(motion.IkKeyframes);
        Assert.Equal(original, JsonSerializer.Serialize(pose));
        Assert.Equal(64, motion.BoneKeyframes[0].Interpolation.Count);
    }

    [Fact]
    public void PartialMatchReportsUnknownNamesAndAllUnmatchedIsRejected()
    {
        var model = PmmMotionAdapterTests.Pmx(["A"], ["smile"]);
        var pose = new VpdPose { Bones = [new() { Index = 0, BoneName = "unknown" }, new() { Index = 1, BoneName = "A" }],
            Morphs = [new() { Index = 0, MorphName = "missing", Weight = 1 }] };
        var result = VpdMotionAdapter.Convert(pose, model);
        Assert.Equal("A", Assert.Single(result.BoneKeyframes).BoneName);
        Assert.Empty(result.MorphKeyframes);
        Assert.Contains(result.Diagnostics, d => d.Contains("unknown", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Contains("missing", StringComparison.Ordinal));
        Assert.Throws<NotSupportedException>(() => VpdMotionAdapter.Convert(new() { Bones = [new() { BoneName = "a" }] }, model));
    }

    [Theory]
    [InlineData("nullBone")]
    [InlineData("nullMorph")]
    [InlineData("nullBoneName")]
    [InlineData("nullMorphName")]
    [InlineData("nulName")]
    [InlineData("negativeIndex")]
    [InlineData("duplicateIndex")]
    [InlineData("duplicateName")]
    [InlineData("nanTranslation")]
    [InlineData("infiniteRotation")]
    [InlineData("zeroRotation")]
    [InlineData("nanWeight")]
    public void InvalidEvenUnmatchedRecordsCannotProducePartialMotion(string kind)
    {
        var good = new VpdBonePose { Index = 7, BoneName = "A" };
        var bad = kind switch
        {
            "nullBone" => null!,
            "nullBoneName" => new VpdBonePose { Index = 8, BoneName = null! },
            "nulName" => new VpdBonePose { Index = 8, BoneName = "bad\0name" },
            "negativeIndex" => new VpdBonePose { Index = -1, BoneName = "unmatched" },
            "duplicateIndex" => new VpdBonePose { Index = 7, BoneName = "unmatched" },
            "duplicateName" => new VpdBonePose { Index = 8, BoneName = "A" },
            "nanTranslation" => new VpdBonePose { Index = 8, BoneName = "unmatched", Translation = new(float.NaN, 0, 0) },
            "infiniteRotation" => new VpdBonePose { Index = 8, BoneName = "unmatched", Rotation = new(0, 0, float.PositiveInfinity, 1) },
            "zeroRotation" => new VpdBonePose { Index = 8, BoneName = "unmatched", Rotation = default },
            _ => new VpdBonePose { Index = 8, BoneName = "unmatched" }
        };
        VpdMorphPose[] morphs = kind switch
        {
            "nullMorph" => [null!],
            "nullMorphName" => [new() { MorphName = null! }],
            "nanWeight" => [new() { MorphName = "unmatched", Weight = float.NaN }],
            _ => []
        };
        var pose = new VpdPose { Bones = [good, bad], Morphs = morphs };
        Assert.ThrowsAny<ArgumentException>(() => VpdMotionAdapter.Convert(pose, PmmMotionAdapterTests.Pmx(["A"])));
    }

    [Fact]
    public void DuplicateMorphRecordsAndAmbiguousModelNamesAreRejectedButNamespacesAreSeparate()
    {
        Assert.Throws<ArgumentException>(() => VpdMotionAdapter.Convert(new() { Bones = [new() { BoneName = "A" }] },
            PmmMotionAdapterTests.Pmx(["A", "A"])));
        Assert.Throws<ArgumentException>(() => VpdMotionAdapter.Convert(new() { Morphs = [new() { MorphName = "A" }] },
            PmmMotionAdapterTests.Pmx([], ["A", "A"])));
        Assert.Throws<ArgumentException>(() => VpdMotionAdapter.Convert(new() { Morphs =
            [new() { Index = 0, MorphName = "A" }, new() { Index = 1, MorphName = "A" }] }, PmmMotionAdapterTests.Pmx([], ["A"])));
        Assert.Throws<ArgumentException>(() => VpdMotionAdapter.Convert(new() { Morphs =
            [new() { Index = 0, MorphName = "A" }, new() { Index = 0, MorphName = "B" }] }, PmmMotionAdapterTests.Pmx([], ["A", "B"])));
        var motion = VpdMotionAdapter.Convert(new() { Bones = [new() { BoneName = "A" }], Morphs = [new() { MorphName = "A", Weight = 1 }] },
            PmmMotionAdapterTests.Pmx(["A"], ["A"]));
        Assert.Single(motion.BoneKeyframes); Assert.Single(motion.MorphKeyframes);
    }

    [Fact]
    public void RegistrationUsesConflictConfirmationUndoAndCapturedMotionCanSampleThePose()
    {
        var model = EditedMotionProjectTests.Model();
        var original = new VmdMotion { ModelName = "model", BoneKeyframes = [new() { BoneName = "root", Frame = 10 }] };
        var editor = new MotionEditor(original);
        var pose = new VpdPose { Bones = [new() { Index = 999, BoneName = "root", Translation = new(3, 4, 5) }] };
        var plan = editor.PrepareRegister(VpdMotionAdapter.Convert(pose, model, 10));
        Assert.Single(plan.Conflicts);
        Assert.Throws<InvalidOperationException>(() => editor.Apply(plan));
        editor.Apply(plan, confirmOverwrite: true);
        var edited = SceneMotionAsset.FromMotion(editor.ExportMotion());
        var rest = EditedMotionProjectTests.Rest(model);
        using var controller = new CharacterAnimationController(model, edited.Motion, rest.Surface, rest.Outline!);
        var sample = controller.Sample(10);
        Assert.Equal(new Vector3(3, 4, 5), sample.Surface.Snapshot.Vertices[0].Position);
        Assert.True(editor.Undo()); Assert.Equal(Vector3.Zero, editor.ExportMotion().BoneKeyframes[0].Translation);
        Assert.True(editor.Redo()); Assert.Equal(new Vector3(3, 4, 5), editor.ExportMotion().BoneKeyframes[0].Translation);
    }

    [Theory]
    [InlineData(VpdTextEncoding.Cp932)]
    [InlineData(VpdTextEncoding.Utf8)]
    public void ExplicitVpdTextEncodingArrivesAsTheSameExactBoneName(VpdTextEncoding encoding)
    {
        var source = new VpdPose { ParentFileName = "model.pmd", Bones = [new() { BoneName = "センター", Translation = Vector3.UnitY }] };
        using var output = new MemoryStream(); VpdWriter.Write(output, source, encoding);
        output.Position = 0;
        var decoded = VpdReader.Read(output, new() { Encoding = encoding });
        var motion = VpdMotionAdapter.Convert(decoded, PmmMotionAdapterTests.Pmx(["センター"]));
        Assert.Equal("センター", motion.BoneKeyframes[0].BoneName);
        Assert.Equal(Vector3.UnitY, motion.BoneKeyframes[0].Translation);
    }
}
