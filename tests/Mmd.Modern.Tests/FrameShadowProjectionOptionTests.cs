using System.Numerics;
using System.Text;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class FrameShadowProjectionOptionTests
{
    [Fact]
    public void ExplicitLegacyKeepsTheDefaultFrameAndFitIsAnIndependentOptIn()
    {
        using var model = new ModelFixture();
        using var defaultVm = new FrameViewportViewModel(FrameViewportOptions.Parse(["--frame-viewer"]));
        using var legacyVm = new FrameViewportViewModel(FrameViewportOptions.Parse(["--frame-viewer", "--shadow-projection", "legacy"]));
        using var fittedVm = new FrameViewportViewModel(FrameViewportOptions.Parse(["--frame-viewer", "--shadow-projection", "fit"]));
        foreach (var vm in new[] { defaultVm, legacyVm, fittedVm }) vm.LoadModel(model.Path);
        var defaultFrame = defaultVm.GetFrame(801, 603);
        Assert.False(defaultVm.ShadowFitEnabled);
        Assert.False(legacyVm.ShadowFitEnabled);
        Assert.True(fittedVm.ShadowFitEnabled);
        Assert.Equal(0, defaultVm.ShadowProjectionFitCount);
        Assert.Equal(0, legacyVm.ShadowProjectionFitCount);
        Assert.Equal(defaultFrame.Options!.SelfShadow, legacyVm.GetFrame(801, 603).Options!.SelfShadow);
        Assert.NotEqual(defaultFrame.Options.SelfShadow!.LightViewProjection, fittedVm.GetFrame(801, 603).Options!.SelfShadow!.LightViewProjection);
        Assert.False(fittedVm.GiEnabled);
    }

    [Theory]
    [InlineData("FiT", true)]
    [InlineData("LEGACY", false)]
    public void ProjectionSelectionIsCaseInsensitiveAndIndependentOfTheRoomAndGi(string requested, bool fit)
    {
        var options = FrameViewportOptions.Parse(["--frame-viewer", "--shadow-projection", requested, "--demo-room", "--gi", "hardware"]);
        Assert.Equal(fit, options.FitShadow);
        Assert.True(options.DemoRoom);
        Assert.Equal(LightingMode.HardwareRayTracing, options.RayGi!.Mode);
        Assert.False(FrameViewportOptions.Parse([]).FitShadow);
        using var vm = new FrameViewportViewModel(options);
        Assert.Equal(fit, vm.ShadowFitEnabled);
        Assert.False(vm.GiEnabled); // Projection selection never fabricates device readiness.
    }

    [Fact]
    public void FitCoversBothScenesAndReusesMatricesAcrossCameraResizeAndSceneSwitches()
    {
        using var model = new ModelFixture();
        using var vm = new FrameViewportViewModel(new(FitShadow: true));
        vm.LoadModel(model.Path);
        Assert.Equal(0, vm.ShadowProjectionFitCount);
        var character = vm.GetFrame(801, 603);
        var characterProjection = Projection(character);
        AssertCoverage(character);
        Assert.Equal(1, vm.ShadowProjectionFitCount);
        Assert.Same(character, vm.GetFrame(801, 603));

        vm.Yaw = 75; vm.Pitch = -20; vm.Zoom = 1.5;
        var orbit = vm.GetFrame(1603, 901);
        Assert.NotEqual(character.Options!.CameraOverride!.Eye, orbit.Options!.CameraOverride!.Eye);
        Assert.Equal(characterProjection, Projection(orbit));
        Assert.Equal(1, vm.ShadowProjectionFitCount);

        vm.RoomEnabled = true;
        var room = vm.GetFrame(1603, 901);
        var roomProjection = Projection(room);
        Assert.NotEqual(characterProjection, roomProjection);
        AssertCoverage(room);
        Assert.Equal(2, vm.ShadowProjectionFitCount);
        vm.ResetCamera(); vm.GetFrame(801, 603);
        Assert.Equal(2, vm.ShadowProjectionFitCount);

        vm.RoomEnabled = false;
        Assert.Equal(characterProjection, Projection(vm.GetFrame(801, 603)));
        vm.RoomEnabled = true;
        Assert.Equal(roomProjection, Projection(vm.GetFrame(801, 603)));
        Assert.Equal(2, vm.ShadowProjectionFitCount);
    }

    [Fact]
    public void ShadowOffDefersFitAndProjectionToggleRestoresTheLegacyMatrix()
    {
        using var model = new ModelFixture();
        using var vm = new FrameViewportViewModel(new());
        vm.LoadModel(model.Path);
        var legacy = Projection(vm.GetFrame(801, 603));
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        vm.SelfShadowEnabled = false; vm.ShadowFitEnabled = true;
        Assert.Null(vm.GetFrame(801, 603).Options!.SelfShadow);
        Assert.Equal(0, vm.ShadowProjectionFitCount);
        Assert.Contains(nameof(vm.ShadowFitEnabled), changes);
        Assert.Contains(nameof(vm.ShadowProjectionPolicy), changes);
        Assert.Contains("modern scene-fit", vm.ShadowProjectionPolicy);
        Assert.Contains("original host parity unverified", vm.ShadowProjectionPolicy);
        vm.SelfShadowEnabled = true;
        Assert.NotEqual(legacy, Projection(vm.GetFrame(801, 603)));
        Assert.Equal(1, vm.ShadowProjectionFitCount);
        vm.ShadowFitEnabled = false;
        Assert.Equal(legacy, Projection(vm.GetFrame(801, 603)));
        Assert.Contains("selected mode1", vm.ShadowProjectionPolicy);
        vm.ShadowFitEnabled = true; vm.GetFrame(801, 603);
        Assert.Equal(1, vm.ShadowProjectionFitCount);
    }

    [Fact]
    public void SuccessfulModelReloadDropsBothSceneCachesButKeepsTheSelectedPolicy()
    {
        using var first = new ModelFixture();
        using var second = new ModelFixture(width: 5, height: 20);
        using var vm = new FrameViewportViewModel(new(FitShadow: true));
        vm.LoadModel(first.Path);
        var oldCharacter = Projection(vm.GetFrame(801, 603));
        vm.RoomEnabled = true;
        var oldRoom = Projection(vm.GetFrame(801, 603));
        Assert.Equal(2, vm.ShadowProjectionFitCount);

        vm.LoadModel(second.Path);
        Assert.True(vm.ShadowFitEnabled);
        Assert.True(vm.RoomEnabled);
        Assert.Equal(0, vm.ShadowProjectionFitCount);
        var newRoom = vm.GetFrame(801, 603);
        Assert.NotEqual(oldRoom, Projection(newRoom)); AssertCoverage(newRoom);
        Assert.Equal(1, vm.ShadowProjectionFitCount);
        vm.RoomEnabled = false;
        var newCharacter = vm.GetFrame(801, 603);
        Assert.NotEqual(oldCharacter, Projection(newCharacter)); AssertCoverage(newCharacter);
        Assert.Equal(2, vm.ShadowProjectionFitCount);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    public void UnsupportedShadowProjectionArgumentsAreRejected(string value)
        => Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--shadow-projection", value]));

    [Fact]
    public void ShadowProjectionArgumentRequiresAValue()
        => Assert.Throws<ArgumentException>(() => FrameViewportOptions.Parse(["--frame-viewer", "--shadow-projection"]));

    private static Matrix4x4 Projection(OriginalFrameScene frame) => frame.Options!.SelfShadow!.LightViewProjection;
    private static void AssertCoverage(OriginalFrameScene frame)
    {
        var projection = Projection(frame); var scene = frame.Surface.Snapshot;
        foreach (uint index in scene.Indices)
        {
            var clip = Vector4.Transform(new Vector4(scene.Vertices[(int)index].Position, 1), projection);
            Assert.True(float.IsFinite(clip.W) && clip.W > 0);
            Assert.InRange(clip.X / clip.W, -1, 1);
            Assert.InRange(clip.Y / clip.W, -1, 1);
            Assert.InRange(clip.Z / clip.W, 0, 1);
        }
    }

    private sealed class ModelFixture : IDisposable
    {
        private readonly string directory;
        public string Path { get; }
        public ModelFixture(float width = 2, float height = 10)
        {
            directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frame-shadow-option-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); Path = System.IO.Path.Combine(directory, "fixture.pmx");
            using var stream = File.Create(Path); using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write("PMX "u8); writer.Write(2f); writer.Write((byte)8);
            writer.Write(new byte[] { 1, 0, 1, 1, 1, 1, 1, 1 });
            Text("shadow projection fixture"); Text(""); Text(""); Text("");
            Vector3[] positions = [new(-width, 3, -1), new(width, 3, -1), new(0, 3 + height, 1)];
            writer.Write(positions.Length);
            foreach (var position in positions)
            {
                Vector(position); Vector(Vector3.UnitZ); writer.Write(0f); writer.Write(0f);
                writer.Write((byte)0); writer.Write((sbyte)-1); writer.Write(1f);
            }
            writer.Write(3); writer.Write(new byte[] { 0, 1, 2 });
            writer.Write(0); // No external textures.
            writer.Write(1); Text("fixture"); Text("");
            writer.Write(1f); writer.Write(.8f); writer.Write(.7f); writer.Write(1f);
            Vector(Vector3.Zero); writer.Write(1f); Vector(new(.4f));
            writer.Write((byte)0x1e); Vector(Vector3.Zero); writer.Write(1f); writer.Write(1f);
            writer.Write((sbyte)-1); writer.Write((sbyte)-1); writer.Write((byte)0);
            writer.Write((byte)0); writer.Write((sbyte)-1); Text(""); writer.Write(3);
            for (int section = 0; section < 5; section++) writer.Write(0); // Bones through joints.
            void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
            void Vector(Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
        }
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
