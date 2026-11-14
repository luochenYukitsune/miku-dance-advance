using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

public sealed partial class ScenePmmControlState
{
    /// <summary>Registers the explicit parent-local editor camera with its original target in one immutable candidate.
    /// The project owner must publish the matching scene VMD in the same Edit transaction.</summary>
    public ScenePmmControlState WithCameraParent(uint frame, PmmCameraPose pose)
    {
        if (frame > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(frame));
        pose = pose.ToKeyframePose();
        _ = ResolveCameraParent(pose);
        if (!float.IsFinite(pose.Fov) || pose.Fov != MathF.Truncate(pose.Fov) || pose.Fov < 1 || pose.Fov > 179)
            throw new ArgumentOutOfRangeException(nameof(pose), "A registered PMM camera requires an integer FOV from 1 through 179.");
        var source = ExportDocument();
        var old = new[] { source.Camera.InitialKeyframe }.Concat(source.Camera.Keyframes).OrderBy(k => k.Frame).ToArray();
        var anchor = old.LastOrDefault(k => k.Frame <= frame) ?? old[0];
        var records = old.Where(k => k.Frame != frame).Append(new PmmCameraKeyframe
        {
            Frame = (int)frame, Distance = pose.Distance, LookAt = pose.LookAt, Angle = pose.Angle,
            Fov = (int)pose.Fov, Perspective = pose.Perspective, ParentModelIndex = pose.ParentModelIndex,
            ParentBoneIndex = pose.ParentBoneIndex, Interpolation = anchor.Interpolation.ToArray(), Selected = anchor.Selected,
        }).OrderBy(k => k.Frame).Select(k => new PmmCameraKeyframe
        {
            Frame = k.Frame, Distance = k.Distance, LookAt = k.LookAt, Angle = k.Angle, Fov = k.Fov,
            Perspective = k.Perspective, ParentModelIndex = k.ParentModelIndex, ParentBoneIndex = k.ParentBoneIndex,
            Interpolation = k.Interpolation.ToArray(), Selected = k.Selected,
        }).ToArray();
        for (int i = 0; i < records.Length; i++)
        {
            records[i].ObjectIndex = records[i].RecordIndex = i;
            records[i].PreviousIndex = Math.Max(0, i - 1); records[i].NextIndex = i + 1 < records.Length ? i + 1 : 0;
        }
        var camera = new PmmCamera { InitialKeyframe = records[0], Keyframes = records.Skip(1).ToArray(),
            LookAt = pose.LookAt, Angle = pose.Angle, Position = new(pose.ViewOffset, pose.Distance), Perspective = pose.Perspective };
        var s = source.Settings;
        var settings = new PmmSettings
        {
            Offset = s.Offset, HorizontalScroll = s.HorizontalScroll, HorizontalScrollThumb = s.HorizontalScrollThumb,
            EditingMode = s.EditingMode, CameraLookMode = s.CameraLookMode, LoopEnabled = s.LoopEnabled,
            BeginFrameEnabled = s.BeginFrameEnabled, EndFrameEnabled = s.EndFrameEnabled, BeginFrame = s.BeginFrame, EndFrame = s.EndFrame,
            AudioEnabled = s.AudioEnabled, AudioPath = s.AudioPath, VideoOffsetX = s.VideoOffsetX, VideoOffsetY = s.VideoOffsetY,
            VideoScale = s.VideoScale, VideoPath = s.VideoPath, VideoEnabledRaw = s.VideoEnabledRaw,
            ImageOffsetX = s.ImageOffsetX, ImageOffsetY = s.ImageOffsetY, ImageScale = s.ImageScale, ImagePath = s.ImagePath, ImageEnabled = s.ImageEnabled,
            InformationShown = s.InformationShown, GridAndAxisShown = s.GridAndAxisShown, GroundShadowShown = s.GroundShadowShown,
            PreferredFps = s.PreferredFps, ScreenCaptureMode = s.ScreenCaptureMode, AccessoryIndexAfterModels = s.AccessoryIndexAfterModels,
            GroundShadowBrightness = s.GroundShadowBrightness, TranslucentGroundShadowEnabled = s.TranslucentGroundShadowEnabled,
            PhysicsSimulationMode = s.PhysicsSimulationMode, EdgeColorRgb = s.EdgeColorRgb, BlackBackgroundEnabled = s.BlackBackgroundEnabled,
            CameraLookAtModelIndex = pose.ParentModelIndex, CameraLookAtBoneIndex = pose.ParentBoneIndex, UnknownMatrix = s.UnknownMatrix,
            FollowingLookAtRaw = s.FollowingLookAtRaw, UnknownBooleanRaw = s.UnknownBooleanRaw,
            PhysicsGroundEnabled = s.PhysicsGroundEnabled, CurrentFrameText = s.CurrentFrameText,
            SelectionTrailerFlag = s.SelectionTrailerFlag, ModelSelections = s.ModelSelections,
        };
        var result = new PmmDocument
        {
            Version = source.Version, AnsiCodePage = source.AnsiCodePage, HeaderBytes = source.HeaderBytes,
            OutputWidth = source.OutputWidth, OutputHeight = source.OutputHeight, TimelineWidth = source.TimelineWidth,
            CameraFov = pose.Fov, PanelStates = source.PanelStates, SelectedModelIndex = source.SelectedModelIndex, Models = source.Models,
            V1ModelNames = source.V1ModelNames, Camera = camera, Light = source.Light, SelectedAccessoryIndex = source.SelectedAccessoryIndex,
            AccessoryHorizontalScroll = source.AccessoryHorizontalScroll, AccessoryNames = source.AccessoryNames,
            Accessories = source.Accessories, CurrentFrame = (int)frame, Settings = settings, Gravity = source.Gravity,
            SelfShadow = source.SelfShadow, ParsedLength = source.ParsedLength, BytesConsumed = source.BytesConsumed,
            UnknownTail = source.UnknownTail, Diagnostics = source.Diagnostics,
        };
        return new(result, actors, OriginSha256, restoredBindings);
    }
}
