using System.Numerics;
using Avalonia.Headless;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Formats;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

[Collection(QualityLoadingCollection.Name)]
public sealed class EditorBonePickingTests : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessTestHost.Start(QualityLoadingAppBuilder.BuildAvaloniaApp);
    private static readonly SceneCamera Camera = new(new(6, 4, -12), Vector3.Zero, Vector3.UnitY, 10, .1f, 100);

    [Fact]
    public async Task RepeatedClicksCycleDrawnCentersButDraggingKeepsTheSelectedBone() => await session.Dispatch(() =>
    {
        var (shell, controller, frame) = Fixture();
        try
        {
            controller.IsManipulationEnabled = true;
            var center = new Vector2(400, 300);
            int x = (int)center.X, y = (int)center.Y;
            Click(); Assert.Equal(0, shell.Animation.SelectedBoneIndex);
            Click(); Assert.Equal(1, shell.Animation.SelectedBoneIndex);
            Click(); Assert.Equal(2, shell.Animation.SelectedBoneIndex);
            Click(); Assert.Equal(0, shell.Animation.SelectedBoneIndex);
            Assert.Empty(shell.Animation.TimelineDrafts);
            controller.DecorateFrame(frame);
            Assert.True(controller.TryBeginDrag(x, y, 800, 600, Camera));
            Assert.True(controller.MoveDrag(x + 24, y + 6));
            controller.EndPointerGesture(x + 24, y + 6);
            Assert.Equal(0, shell.Animation.SelectedBoneIndex);
            Assert.Equal("bone 0", Assert.Single(shell.Animation.TimelineDrafts).Name);
            return true;

            void Click()
            {
                controller.DecorateFrame(frame);
                Assert.True(controller.TryBeginDrag(x, y, 800, 600, Camera));
                controller.EndPointerGesture(x, y);
            }
        }
        finally { shell.Dispose(); }
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EachCornerAxisRotatesTheActiveBoneInTheSameBasisAsTheWorldRings(int axis) => await session.Dispatch(() =>
    {
        var (shell, controller, frame) = Fixture();
        try
        {
            shell.Browser.SelectedBoneIndex = 1;
            controller.IsManipulationEnabled = true;
            controller.RotateHandle = true;
            var visible = controller.DecorateFrame(frame);
            var corner = visible.Scene.EditorOverlay!.Lines.Where(line => line.Shape == ViewportOverlayShape.Square && line.Radius == 11)
                .OrderBy(line => line.Start.X).ElementAt(axis);
            Assert.True(visible.Scene.EditorOverlay.Lines.Count(line => line.Shape == ViewportOverlayShape.Segment && line.Start.Y < .9) >= 72);
            var pixel = corner.Start * new Vector2(800, 600);
            int x = (int)Math.Round(pixel.X), y = (int)Math.Round(pixel.Y);
            Assert.True(controller.TryBeginDrag(x, y, 800, 600, Camera));
            Assert.True(controller.MoveDrag(x + 30, y));
            Assert.True(controller.EndDrag());
            var draft = Assert.Single(shell.Animation.SnapshotDrafts());
            Assert.Equal(1, draft.BoneIndex);
            var expected = Quaternion.CreateFromAxisAngle(axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ, .3f);
            Assert.InRange(Math.Abs(Quaternion.Dot(expected, draft.Rotation)), .99999f, 1.00001f);
            var lighting = controller.DecorateFrame(frame, lightDragging: true);
            Assert.Empty(lighting.Scene.EditorOverlay?.Lines ?? []);
            Assert.False(controller.TryBeginDrag(x, y, 800, 600, Camera));
            return true;
        }
        finally { shell.Dispose(); }
    }, TestContext.Current.CancellationToken);

    private static (EditorShellViewModel Shell, EditorBoneViewportController Controller, PreviewFrame Frame) Fixture()
    {
        var source = MorphApplicationTests.Model([]);
        var model = new PmxModel
        {
            Header = source.Header, Name = "Overlap", EnglishName = "", Comment = "", EnglishComment = "",
            Vertices = source.Vertices, Faces = source.Faces, Textures = [], Materials = source.Materials,
            Morphs = [], DisplayFrames = [], RigidBodies = [], Joints = [], SoftBodies = [],
            Bones = Enumerable.Range(0, 3).Select(index => new PmxBone { Name = "bone " + index, EnglishName = "",
                ParentIndex = -1, Position = Vector3.Zero, DeformDepth = 0, Flags = PmxBoneFlags.AllowRotate
                    | PmxBoneFlags.AllowTranslate | PmxBoneFlags.AllowControl | PmxBoneFlags.Visible }).ToArray()
        };
        var shell = new EditorShellViewModel();
        var path = Path.Combine(AppContext.BaseDirectory, "overlap.pmx");
        var id = shell.Workspace.AddModel(path, EditorProjectWorkspace.CreateModelAsset(model, path), ModelRole.Character);
        shell.RefreshWorkspace(id);
        var controller = new EditorBoneViewportController(shell, () => { });
        var snapshot = shell.Workspace.Session!.CaptureRenderSnapshot(controller.SnapshotRequests([]));
        var frame = new PreviewFrame(800, 600, snapshot.SourceFrame, snapshot.Scene, snapshot.Camera, snapshot.Light, snapshot.SelfShadow)
            { ResolvedCamera = Camera, SceneInputs = snapshot.SceneInputs };
        return (shell, controller, frame);
    }

    public async ValueTask DisposeAsync() => await Task.Run(session.Dispose).ConfigureAwait(false);
}
