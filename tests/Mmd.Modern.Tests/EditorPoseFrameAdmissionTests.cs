using System.Numerics;
using Mmd.Modern.App;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class EditorPoseFrameAdmissionTests
{
    [Fact]
    public void OnlyRepeatedPendingDragPayloadIsSuppressedAndResetForgetsIt()
    {
        var policy = new EditorPoseFrameAdmission();
        var a = Candidate();
        Assert.True(policy.TryAdmit(a));
        // Capture/decorators allocate new wrappers and primitive records for the same image.
        var frame = a.Frame!;
        var duplicate = a with { Frame = frame with { Scene = Scene(frame.Scene) } };
        Assert.False(policy.TryAdmit(duplicate));
        Assert.Equal(1, policy.SuppressedFrames);
        policy.Clear();
        Assert.True(policy.TryAdmit(duplicate));
        Assert.Equal(1, policy.SuppressedFrames);
    }

    [Fact]
    public void NewPublicationWithTheSameClockAndGeometryAlwaysReachesTheRenderer()
    {
        var policy = new EditorPoseFrameAdmission();
        var a = Candidate();
        Assert.True(policy.TryAdmit(a));
        // Released/settling batches share input generation and SourceFrame, not snapshot identity.
        Assert.True(policy.TryAdmit(a with { SnapshotIdentity = new object() }));
        Assert.True(policy.TryAdmit(a));
        Assert.False(policy.TryAdmit(a));
    }

    [Fact]
    public void ReleaseNonPendingLightDragAndTemporalWorkAreNeverSuppressed()
    {
        var a = Candidate(); var frame = a.Frame!;
        EditorPoseFrameCandidate[] unconditionallyVisible = [
            a with { IsBoneDragging = false }, a with { PoseInputPending = false },
            a with { IsLightDragging = true }, a with { SnapshotIdentity = null },
            a with { Quality = new(TemporalUpscaling: new() { Mode = ViewTemporalUpscalingMode.Fsr }) },
            a with { Quality = new(Gi: new(Denoiser: RayGiDenoiser.TemporalSpatial)) },
            a with { Frame = frame with { Scene = Scene(frame.Scene,
                frame.Scene.Options! with { RayGi = new(Denoiser: RayGiDenoiser.TemporalSpatial) }) } }
        ];
        foreach (var candidate in unconditionallyVisible)
        {
            var policy = new EditorPoseFrameAdmission();
            Assert.True(policy.TryAdmit(candidate));
            Assert.True(policy.TryAdmit(candidate));
            Assert.Equal(0, policy.SuppressedFrames);
        }
    }

    [Fact]
    public void PickingReferenceTokensDistinguishEqualValuedRecordInstances()
    {
        var original = new ViewTemporalFrameState();
        var equalValue = original with { };
        Assert.Equal(original, equalValue);
        Assert.Equal(new EditorPresentationReference(original), new EditorPresentationReference(original));
        Assert.NotEqual(new EditorPresentationReference(original), new EditorPresentationReference(equalValue));
    }

    [Fact]
    public void ViewLoadingAndPickingChangesAlwaysAdmitIncludingReturningToAnEarlierView()
    {
        var a = Candidate();
        EditorPoseFrameCandidate[] changes = [
            a with { Window = 2 }, a with { Width = 101 }, a with { Height = 81 },
            a with { Revision = 1 }, a with { Quality = new(Antialiasing: FrameAntialiasing.Smaa1X) },
            a with { BindingIdentity = new object() }, a with { PreparationToken = 1 },
            a with { BonePickingIdentity = new object() }, a with { LightPickingIdentity = new object() }
        ];
        foreach (var changed in changes)
        {
            var policy = new EditorPoseFrameAdmission();
            Assert.True(policy.TryAdmit(a)); Assert.True(policy.TryAdmit(changed));
            Assert.True(policy.TryAdmit(a)); Assert.False(policy.TryAdmit(a));
        }
    }

    [Fact]
    public void CameraLightHistoryAndDecoratedSceneChangesCannotHideBehindSnapshotIdentity()
    {
        var a = Candidate(); var frame = a.Frame!;
        PreviewFrame[] changes = [
            frame with { SourceFrame = 1 }, frame with { Width = 101 },
            frame with { Camera = new(Vector3.One, Vector3.Zero, -45, 30, true) },
            frame with { ResolvedCamera = frame.ResolvedCamera! with { Eye = new(1, 1, -3) } },
            frame with { CameraParentTransform = Matrix4x4.CreateTranslation(1, 0, 0) },
            frame with { Light = new(Vector3.One, -Vector3.UnitY) },
            frame with { SelfShadow = new(1, 100) },
            frame with { TemporalState = new() { HistoryGeneration = 1 } },
            frame with { MorphChannels = new Dictionary<Guid, Mmd.Modern.Application.CharacterMorphChannels>() },
            frame with { SceneInputs = new Dictionary<Guid, Mmd.Modern.Application.SceneObjectRenderInputs>() },
            frame with { RenderingStyles = Array.AsReadOnly(Array.Empty<Mmd.Modern.Application.SceneRenderingStyleBinding>()) },
            frame with { Scene = new(new(SceneSnapshot.CreateEmpty(frame.ResolvedCamera!), []), options: frame.Scene.Options) },
            frame with { Scene = Scene(frame.Scene, frame.Scene.Options! with { BackgroundColor = Vector4.Zero }) },
            frame with { Scene = Scene(frame.Scene, lighting: new([])) },
            frame with { Scene = Scene(frame.Scene, overlay: new([new(Vector2.Zero, Vector2.One, Vector4.UnitX)])) }
        ];
        foreach (var changed in changes)
        {
            var policy = new EditorPoseFrameAdmission();
            Assert.True(policy.TryAdmit(a));
            Assert.True(policy.TryAdmit(a with { Frame = changed }));
        }
    }

    private static EditorPoseFrameCandidate Candidate()
    {
        var camera = new SceneCamera(new(0, 1, -3), Vector3.Zero, Vector3.UnitY, 4, .1f, 100);
        var scene = new OriginalFrameScene(new(SceneSnapshot.CreateEmpty(camera), []),
            options: new(OriginalAlphaBlendMode.Normal, true, false),
            editorOverlay: new([new(Vector2.Zero, Vector2.One, Vector4.One)]));
        return new(1, 100, 80, 0, new(), new object(),
            new(100, 80, 0, scene, null, null, null) { ResolvedCamera = camera }, 0,
            new object(), new object(), new object(), true, false, true);
    }

    private static OriginalFrameScene Scene(OriginalFrameScene source, OriginalFrameOptions? options = null,
        SceneLightingSnapshot? lighting = null, ViewportOverlaySnapshot? overlay = null)
        => new(source.Surface, source.Outline, source.OutlineOptions, options ?? source.Options,
            source.Materials, lighting ?? source.Lighting,
            overlay ?? new(source.EditorOverlay!.Lines.Select(line => line with { })));
}
