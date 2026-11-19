using Xunit;

namespace Mmd.Modern.Application.Tests;

public sealed class ModelDirectoryPathsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "model-paths-" + Guid.NewGuid().ToString("N"));
    public ModelDirectoryPathsTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    [Fact]
    public void PlainRelativeReferencesStayInsideAndEscapesAreRejected()
    {
        string model = Directory.CreateDirectory(Path.Combine(root, "Mod")).FullName;
        Assert.Equal(Path.Combine(model, "tex", "a.png"), ModelDirectoryPaths.ResolveContained(model, Path.Combine("tex", "a.png")));
        Assert.Null(ModelDirectoryPaths.ResolveContained(model, Path.Combine("..", "a.png")));
        Directory.CreateDirectory(Path.Combine(root, "Other"));
        Assert.Null(ModelDirectoryPaths.ResolveContained(model, Path.Combine("..", "Other", "a.png")));
    }

    [Fact]
    public void ACaseVariantOfTheModelFolderIsInsideOnACaseInsensitiveVolume()
    {
        string model = Directory.CreateDirectory(Path.Combine(root, "Mod")).FullName;
        if (!Directory.Exists(Path.Combine(root, "MOD"))) return; // case-sensitive volume: covered by the sibling test
        // "../MOD/paint.png" names the model folder itself here; the resolved path keeps the folder's own spelling.
        Assert.Equal(Path.Combine(model, "paint.png"), ModelDirectoryPaths.ResolveContained(model, Path.Combine("..", "MOD", "paint.png")));
    }

    [Fact]
    public void AModelFolderPassedInADifferentCaseAcceptsItsOnDiskSpelling()
    {
        Directory.CreateDirectory(Path.Combine(root, "Mod"));
        string passed = Path.Combine(root, "MOD");
        if (!Directory.Exists(passed)) return; // case-sensitive volume: "MOD" is not this folder
        // The caller's spelling is "MOD"; the reference uses the real name "Mod". Both name the same directory.
        Assert.Equal(Path.Combine(passed, "paint.png"), ModelDirectoryPaths.ResolveContained(passed, Path.Combine("..", "Mod", "paint.png")));
    }

    [Fact]
    public void ACaseVariantSiblingInACaseSensitiveDirectoryIsRejected()
    {
        string model = Directory.CreateDirectory(Path.Combine(root, "Mod")).FullName;
        string sibling = Path.Combine(root, "mod");
        if (Directory.Exists(sibling)) return; // case-insensitive volume: "mod" is the model folder itself
        Directory.CreateDirectory(sibling);
        Assert.Null(ModelDirectoryPaths.ResolveContained(model, Path.Combine("..", "mod", "paint.png")));
    }

    [Fact]
    public void ACaseVariantThatNamesNoExistingFolderIsRejected()
    {
        // Without an on-disk entry to compare, the spelling cannot be shown to be the model folder.
        string model = Path.Combine(root, "Missing");
        Assert.Null(ModelDirectoryPaths.ResolveContained(model, Path.Combine("..", "MISSING", "paint.png")));
    }
}
