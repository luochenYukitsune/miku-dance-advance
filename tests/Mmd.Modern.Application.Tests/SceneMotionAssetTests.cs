using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mmd.Modern.Formats;
using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class SceneMotionAssetTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "scene-motion-" + Guid.NewGuid().ToString("N"));
    private delegate ReadOnlySpan<byte> ReadAssetBytes(SceneMotionAsset asset);
    private delegate SceneMotionAsset RenameAsset(SceneMotionAsset asset, string name);

    public SceneMotionAssetTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void FromBytesPreservesOriginalBytesHashAndParsedTracks()
    {
        byte[] bytes = Fixture();
        SceneMotionAsset asset = SceneMotionAsset.FromBytes(bytes, "dance.vmd");

        Assert.Equal("dance.vmd", asset.Name);
        Assert.Equal(bytes.Length, asset.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), asset.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
        Assert.Equal(bytes, Bytes(asset).ToArray());
        Assert.Equal("model", asset.Motion.ModelName);
        VmdBoneKeyframe bone = Assert.Single(asset.Motion.BoneKeyframes);
        Assert.Equal("arm", bone.BoneName);
        Assert.Equal(7u, bone.Frame);
        Assert.Equal(new Vector3(1, -2, 3), bone.Translation);
        Assert.Equal(Quaternion.Identity, bone.Rotation);
        Assert.Equal(Enumerable.Range(0, 64).Select(i => (byte)i), bone.Interpolation);
        Assert.Equal(.75f, Assert.Single(asset.Motion.MorphKeyframes).Weight);
        Assert.False(Assert.Single(Assert.Single(asset.Motion.IkKeyframes).Switches).Enabled);
    }

    [Fact]
    public void FromBytesUsesDefaultNameAndStripsDirectoryFromSuppliedName()
    {
        byte[] bytes = Fixture();
        Assert.Equal("motion.vmd", SceneMotionAsset.FromBytes(bytes).Name);
        Assert.Equal("dance.VMD", SceneMotionAsset.FromBytes(bytes, Path.Combine("import", "dance.VMD")).Name);
    }

    [Fact]
    public void CallerMutationCannotChangeSnapshotHashOrParsedMotion()
    {
        byte[] bytes = Fixture();
        byte[] expected = (byte[])bytes.Clone();
        SceneMotionAsset asset = SceneMotionAsset.FromBytes(bytes);
        Array.Fill(bytes, (byte)0xff);

        Assert.Equal(expected, Bytes(asset).ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), asset.Sha256);
        Assert.Equal(new Vector3(1, -2, 3), Assert.Single(asset.Motion.BoneKeyframes).Translation);
        Assert.Equal(.75f, Assert.Single(asset.Motion.MorphKeyframes).Weight);
    }

    [Fact]
    public void ParsedCollectionsAndNestedTracksAreReadOnly()
    {
        VmdMotion motion = SceneMotionAsset.FromBytes(Fixture()).Motion;
        Assert.Throws<NotSupportedException>(() => ((IList<VmdBoneKeyframe>)motion.BoneKeyframes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<VmdMorphKeyframe>)motion.MorphKeyframes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<VmdIkKeyframe>)motion.IkKeyframes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)motion.BoneKeyframes[0].Interpolation)[0] = 255);
        Assert.Throws<NotSupportedException>(() => ((IList<VmdIkSwitch>)motion.IkKeyframes[0].Switches)[0] = new("ik", true));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)motion.Diagnostics).Clear());
    }

    [Fact]
    public void FromBytesRejectsMissingInvalidOrNonVmdNames()
    {
        byte[] bytes = Fixture();
        foreach (string? name in new string?[] { null, "", " ", "motion.pmx", "motion.vmd.tmp", "motion", "folder/", "bad\0.vmd" })
            Assert.ThrowsAny<ArgumentException>(() => SceneMotionAsset.FromBytes(bytes, name!));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    public void FromBytesRejectsMalformedVmdUsingExistingReader(string corruption)
    {
        byte[] bytes = Fixture();
        if (corruption == "signature") bytes[0] = 0;
        else if (corruption == "truncated") bytes = bytes[..^1];
        else bytes = [.. bytes, 1];

        Assert.Throws<VmdFormatException>(() => SceneMotionAsset.FromBytes(bytes));
    }

    [Fact]
    public void FromBytesRejectsEmptyAndOversizedInputBeforeParsing()
    {
        Assert.Equal(64 * 1024 * 1024, SceneMotionAsset.MaxBytes);
        Assert.Throws<InvalidDataException>(() => SceneMotionAsset.FromBytes([]));
        Assert.Throws<InvalidDataException>(() => SceneMotionAsset.FromBytes(new byte[SceneMotionAsset.MaxBytes + 1]));
    }

    [Fact]
    public void LoadReadsUppercaseExtensionWithoutChangingOriginal()
    {
        byte[] bytes = Fixture();
        string path = Write("original.VMD", bytes);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            SceneMotionAsset asset = SceneMotionAsset.Load(path, TestContext.Current.CancellationToken);
            Assert.Equal("original.VMD", asset.Name);
            Assert.Equal(bytes.Length, asset.Length);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), asset.Sha256);
            Assert.Equal(bytes, Bytes(asset).ToArray());
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
            Assert.Equal(FileAttributes.ReadOnly, File.GetAttributes(path) & FileAttributes.ReadOnly);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public void LoadedSnapshotSurvivesSourceReplacementAndDeletion()
    {
        byte[] bytes = Fixture();
        string path = Write("source.vmd", bytes);
        SceneMotionAsset asset = SceneMotionAsset.Load(path, TestContext.Current.CancellationToken);
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Equal(bytes, Bytes(asset).ToArray());
        File.Delete(path);

        Assert.Equal(bytes, Bytes(asset).ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), asset.Sha256);
        Assert.Equal(7u, Assert.Single(asset.Motion.BoneKeyframes).Frame);
    }

    [Theory]
    [InlineData("relative.vmd")]
    [InlineData("folder/relative.vmd")]
    [InlineData("C:relative.vmd")]
    public void LoadRejectsPathsThatDependOnWorkingDirectory(string path)
        => Assert.Throws<ArgumentException>(() => SceneMotionAsset.Load(path, TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("source.pmx")]
    [InlineData("source.vmd.tmp")]
    [InlineData("source")]
    public void LoadRejectsOtherFormatsBeforeOpeningThem(string name)
        => Assert.Throws<ArgumentException>(() => SceneMotionAsset.Load(Path.Combine(directory, name), TestContext.Current.CancellationToken));

    [Fact]
    public void LoadRejectsMalformedFileWithoutChangingIt()
    {
        byte[] invalid = [1, 2, 3];
        string path = Write("bad.vmd", invalid);
        Assert.Throws<VmdFormatException>(() => SceneMotionAsset.Load(path, TestContext.Current.CancellationToken));
        Assert.Equal(invalid, File.ReadAllBytes(path));
    }

    [Fact]
    public void LoadRejectsOversizedFileWithoutReadingOrChangingIt()
    {
        string path = Path.Combine(directory, "large.vmd");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(SceneMotionAsset.MaxBytes + 1L);

        Assert.Throws<InvalidDataException>(() => SceneMotionAsset.Load(path, TestContext.Current.CancellationToken));
        Assert.Equal(SceneMotionAsset.MaxBytes + 1L, new FileInfo(path).Length);
    }

    [Fact]
    public void LoadHonorsPreCancellationBeforeAccessingFile()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        string missing = Path.Combine(directory, "missing.vmd");
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() => SceneMotionAsset.Load(missing, source.Token));
        Assert.Equal(source.Token, error.CancellationToken);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void WithNamePreservesSnapshotAndSharedMotionWithoutChangingOriginalName()
    {
        SceneMotionAsset original = SceneMotionAsset.FromBytes(Fixture(), "original.vmd");
        var rename = typeof(SceneMotionAsset).GetMethod("WithName", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<RenameAsset>();
        SceneMotionAsset alias = rename(original, Path.Combine("folder", "alias.VMD"));

        Assert.Equal("alias.VMD", alias.Name);
        Assert.Equal("original.vmd", original.Name);
        Assert.Equal(original.Sha256, alias.Sha256);
        Assert.Equal(original.Length, alias.Length);
        Assert.Equal(Bytes(original).ToArray(), Bytes(alias).ToArray());
        Assert.True(Bytes(original).Overlaps(Bytes(alias), out int offset));
        Assert.Equal(0, offset);
        Assert.Same(original.Motion, alias.Motion);
        Assert.Throws<ArgumentException>(() => rename(original, "bad.pmx"));
    }

    private static ReadOnlySpan<byte> Bytes(SceneMotionAsset asset)
        => typeof(SceneMotionAsset).GetProperty("Bytes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetGetMethod(nonPublic: true)!.CreateDelegate<ReadAssetBytes>()(asset);

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Fixture()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            Name(writer, "Vocaloid Motion Data 0002", 30); Name(writer, "model", 20);
            writer.Write(1u);
            Name(writer, "arm", 15); writer.Write(7u);
            writer.Write(1f); writer.Write(-2f); writer.Write(3f);
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
            writer.Write(1u);
            Name(writer, "smile", 15); writer.Write(11u); writer.Write(.75f);
            writer.Write(0u); writer.Write(0u); writer.Write(0u);
            writer.Write(1u); writer.Write(13u); writer.Write((byte)1); writer.Write(1u);
            Name(writer, "ik", 20); writer.Write((byte)0);
        }
        return stream.ToArray();
    }

    private static void Name(BinaryWriter writer, string value, int length)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        writer.Write(bytes); writer.Write(new byte[length - bytes.Length]);
    }

    public void Dispose()
    {
        foreach (string path in Directory.EnumerateFiles(directory)) File.Delete(path);
        Directory.Delete(directory);
    }
}
