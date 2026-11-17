using System.Numerics;
using System.Reflection;
using System.Text;
using Mmd.Modern.Demo;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class FrameGiCompositionOptionTests
{
    private readonly ITestOutputHelper output;
    public FrameGiCompositionOptionTests(ITestOutputHelper output) => this.output = output;
    private static LightingCapabilities Device => new(RenderApi.Direct3D12, true, true, true, 0);
    private static LightingExecutionCapabilities Execution => new(Compute: true, ShaderModel: 65,
        RawRaytracingTier: 12, Device5: true, CommandList4: true, CompilerAvailable: true,
        SoftwareImplementationReady: true, HardwareImplementationReady: true);

    [Fact]
    public void DefaultCompositionPreservesAdditiveAndNeverEnablesGiAoOrShadowFit()
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer"]);
        using var vm = new FrameViewportViewModel(options);
        Assert.Equal("Additive", Composition(options.RayGi!));
        Assert.Equal(0, Index(vm));
        Assert.False(vm.GiEnabled); Assert.False(vm.NearOcclusionEnabled); Assert.False(vm.ShadowFitEnabled);
    }

    [Theory]
    [InlineData("toon", "ToonPalette", 1)]
    [InlineData("ToOn", "ToonPalette", 1)]
    [InlineData("additive", "Additive", 0)]
    [InlineData("ADDITIVE", "Additive", 0)]
    public void CommandLineCompositionIsExplicitAndSurvivesHardwareObservation(string value, string expected, int index)
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--gi", "hardware", "--gi-composition", value]);
        Assert.Equal(expected, Composition(options.RayGi!));
        using var vm = new FrameViewportViewModel(options);
        Assert.Equal(index, Index(vm));
        Assert.False(vm.GiEnabled); Assert.False(vm.NearOcclusionEnabled); Assert.False(vm.ShadowFitEnabled);
        vm.UpdateLightingCapabilities(Device, Execution);
        Assert.Equal(LightingMode.HardwareRayTracing, vm.GiMode);
        Assert.Equal(expected, Composition(vm.RayGiOptions));
        Assert.True(vm.TrySelectGiMode(LightingMode.Legacy));
        Assert.True(vm.TrySelectGiMode(LightingMode.SoftwareRayTracing));
        Assert.Equal(index, Index(vm)); Assert.False(vm.NearOcclusionEnabled); Assert.False(vm.ShadowFitEnabled);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { argument = value,
            requestedComposition = Composition(options.RayGi!), actualVmComposition = Composition(vm.RayGiOptions),
            actualGiMode = vm.GiMode.ToString(), actualCompositionIndex = Index(vm),
            vm.NearOcclusionEnabled, vm.ShadowFitEnabled }));
    }

    [Fact]
    public void CompositionSelectionInvalidatesImmutableFrameAndNotifiesBindings()
    {
        using var model = new ModelFixture();
        using var vm = new FrameViewportViewModel(new(FitShadow: true));
        vm.LoadModel(model.Path);
        var before = vm.GetFrame(801, 603);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        SetIndex(vm, 1);
        var after = vm.GetFrame(801, 603);
        Assert.NotSame(before, after);
        Assert.Equal("ToonPalette", Composition(after.Options!.RayGi!));
        Assert.Equal("Additive", Composition(before.Options!.RayGi!));
        Assert.Contains("GiCompositionIndex", notifications); Assert.Contains(nameof(vm.RayGiOptions), notifications);
        Assert.Equal(before.Options.SelfShadow, after.Options.SelfShadow);
        Assert.Equal(1, vm.ShadowProjectionFitCount); // Composition does not rescan static scene geometry.
        Assert.False(vm.GiEnabled); Assert.False(vm.NearOcclusionEnabled);
        notifications.Clear(); SetIndex(vm, 1);
        Assert.Same(after, vm.GetFrame(801, 603)); Assert.Empty(notifications);
        SetIndex(vm, 0);
        Assert.Equal("Additive", Composition(vm.GetFrame(801, 603).Options!.RayGi!));
    }

    [Fact]
    public void InvalidUiSelectionIsIgnoredAndExplicitAoAndFitRemainIndependent()
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--gi-composition", "toon", "--gi-occlusion", "on", "--shadow-projection", "fit"]);
        using var vm = new FrameViewportViewModel(options);
        SetIndex(vm, -1); Assert.Equal(1, Index(vm));
        SetIndex(vm, 2); Assert.Equal(1, Index(vm));
        SetIndex(vm, 0);
        Assert.True(vm.NearOcclusionEnabled); Assert.True(vm.ShadowFitEnabled); Assert.False(vm.GiEnabled);
        Assert.Equal("Additive", Composition(vm.RayGiOptions));
    }

    [Fact]
    public void LifecycleSelfTestKeepsOffWhileCarryingTheExplicitComposition()
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--model", "fixture.pmx", "--ray-gi-self-test", "--gi-composition", "toon"]);
        using var vm = new FrameViewportViewModel(options);
        Assert.Equal(LightingMode.Legacy, options.RayGi!.Mode);
        Assert.Equal("ToonPalette", Composition(options.RayGi)); Assert.Equal(1, Index(vm));
        Assert.False(vm.GiEnabled); Assert.False(vm.NearOcclusionEnabled); Assert.False(vm.ShadowFitEnabled);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("palette")]
    public void UnknownCompositionIsRejected(string value)
        => Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--gi-composition", value]));

    [Fact]
    public void MissingCompositionIsRejected()
        => Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--gi-composition"]));

    // Reflection lets the Red evidence exercise the absent public behavior without a compiler failure.
    private static PropertyInfo IndexProperty => Assert.IsType<PropertyInfo>(typeof(FrameViewportViewModel).GetProperty("GiCompositionIndex"), exactMatch: false);
    private static int Index(FrameViewportViewModel vm) => Assert.IsType<int>(IndexProperty.GetValue(vm));
    private static void SetIndex(FrameViewportViewModel vm, int value) => IndexProperty.SetValue(vm, value);
    private static string Composition(RayGiOptions options)
    {
        string? value = typeof(RayGiOptions).GetProperty("Composition")?.GetValue(options)?.ToString();
        Assert.NotNull(value); return value;
    }

    private sealed class ModelFixture : IDisposable
    {
        private readonly string directory;
        public string Path { get; }
        public ModelFixture()
        {
            directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frame-gi-composition-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); Path = System.IO.Path.Combine(directory, "fixture.pmx");
            using var stream = File.Create(Path); using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write("PMX "u8); writer.Write(2f); writer.Write((byte)8); writer.Write(new byte[] { 1, 0, 1, 1, 1, 1, 1, 1 });
            Text("GI composition fixture"); Text(""); Text(""); Text("");
            Vector3[] positions = [new(-2, 3, -1), new(2, 3, -1), new(0, 13, 1)]; writer.Write(positions.Length);
            foreach (var position in positions)
            {
                Vector(position); Vector(Vector3.UnitZ); writer.Write(0f); writer.Write(0f);
                writer.Write((byte)0); writer.Write((sbyte)-1); writer.Write(1f);
            }
            writer.Write(3); writer.Write(new byte[] { 0, 1, 2 }); writer.Write(0); writer.Write(1); Text("fixture"); Text("");
            writer.Write(1f); writer.Write(.8f); writer.Write(.7f); writer.Write(1f);
            Vector(Vector3.Zero); writer.Write(1f); Vector(new(.4f)); writer.Write((byte)0x1e);
            Vector(Vector3.Zero); writer.Write(1f); writer.Write(1f); writer.Write((sbyte)-1); writer.Write((sbyte)-1);
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((sbyte)-1); Text(""); writer.Write(3);
            for (int section = 0; section < 5; section++) writer.Write(0);
            void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
            void Vector(Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
        }
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
