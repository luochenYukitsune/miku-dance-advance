// PMM fields: nanoem MIT ext/document.c camera/light/self-shadow writers,
// @30acffaa29f5d2eb9e997d69418f2e4b97b5894f. Serialization remains PmmWriter's job.
using System.Numerics;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Maps edited scene VMD channels to a validated PMM 0002 candidate without touching assets.</summary>
public static class PmmSceneMotionExporter
{
    /// <summary>Uses the document's current editor frame. Empty input channels preserve their source tracks.</summary>
    public static PmmDocument ReplaceSceneMotion(PmmDocument source, VmdMotion motion, bool refreshSavedPose = true, bool preserveCameraParents = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ReplaceSceneMotion(source, motion, source.CurrentFrame, refreshSavedPose, preserveCameraParents);
    }

    /// <summary>
    /// Replaces nonempty camera/light/shadow channels, rebuilding independent linked records.
    /// Empty channels preserve the source, rather than erase it. Character channels reject explicitly.
    /// PMM editor frames must be nonnegative Int32 integers. By default VMD replacement detaches the camera;
    /// retained PMM provenance can explicitly preserve its independent parent channel. Removing a parent
    /// switch key then rejects before publication. Shadow sampling uses the existing explicit hold policy.
    /// </summary>
    public static PmmDocument ReplaceSceneMotion(PmmDocument source, VmdMotion motion, double currentFrame, bool refreshSavedPose = true,
        bool preserveCameraParents = false)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(motion);
        if (!double.IsFinite(currentFrame) || currentFrame < 0 || currentFrame > int.MaxValue || currentFrame != Math.Truncate(currentFrame))
            throw new ArgumentOutOfRangeException(nameof(currentFrame), "PMM editor frame must be a nonnegative Int32 integer.");
        if (source.Version != 2) throw new NotSupportedException("Scene motion export requires a PMM 0002 candidate.");
        if (motion.BoneKeyframes is null || motion.MorphKeyframes is null || motion.IkKeyframes is null)
            throw new ArgumentException("Motion track collections cannot be null.", nameof(motion));
        if (motion.BoneKeyframes.Count != 0 || motion.MorphKeyframes.Count != 0 || motion.IkKeyframes.Count != 0)
            throw new NotSupportedException("Scene PMM export cannot silently drop character animation tracks.");
        // Validate every raw key before collapsing duplicates, including keys hidden by a later duplicate.
        var sampled = new VmdSceneTracks(motion);
        foreach (var key in motion.CameraKeyframes)
        {
            _ = Frame(key.Frame);
            if (key.ViewAngle > int.MaxValue) throw new ArgumentException("Camera FOV exceeds the PMM Int32 field.", nameof(motion));
        }
        foreach (var key in motion.LightKeyframes) _ = Frame(key.Frame);
        foreach (var key in motion.SelfShadowKeyframes) _ = Frame(key.Frame);
        _ = PmmWriter.ToBytes(source);

        var camera = source.Camera; var light = source.Light; var shadow = source.SelfShadow;
        var settings = source.Settings; float fov = source.CameraFov;
        if (motion.CameraKeyframes.Count != 0)
        {
            var retained = preserveCameraParents ? new PmmSceneTracks(source) : null;
            if (retained is not null)
            {
                var outputFrames = motion.CameraKeyframes.Select(k => Frame(k.Frame)).Append(0).ToHashSet();
                var parentKeys = new[] { source.Camera.InitialKeyframe }.Concat(source.Camera.Keyframes).OrderBy(k => k.Frame).ToArray();
                for (int i = 1; i < parentKeys.Length; i++)
                    if ((parentKeys[i].ParentModelIndex, parentKeys[i].ParentBoneIndex)
                        != (parentKeys[i - 1].ParentModelIndex, parentKeys[i - 1].ParentBoneIndex)
                        && !outputFrames.Contains(parentKeys[i].Frame))
                        throw new NotSupportedException($"Camera frame {parentKeys[i].Frame} changes its PMM parent; preserve or explicitly edit that parent boundary before removing the camera key.");
            }
            var records = SeedAndLink(motion.CameraKeyframes, k => k.Frame, (key, frame) => new PmmCameraKeyframe
            {
                Frame = frame, Distance = key.Distance, LookAt = key.Interest, Angle = key.Rotation,
                ParentModelIndex = retained?.CameraAt(frame).ParentModelIndex ?? -1,
                ParentBoneIndex = retained?.CameraAt(frame).ParentBoneIndex ?? -1, Interpolation = CameraInterpolation(key.Interpolation),
                Fov = (int)key.ViewAngle, Perspective = key.Perspective,
            });
            var pose = sampled.CameraAt(currentFrame)!.Value;
            camera = new()
            {
                Offset = source.Camera.Offset, InitialKeyframe = records[0], Keyframes = records.Skip(1).ToArray(),
                LookAt = refreshSavedPose ? pose.LookAt : source.Camera.LookAt,
                Position = refreshSavedPose ? new(0, 0, pose.Distance) : source.Camera.Position,
                Angle = refreshSavedPose ? pose.Angle : source.Camera.Angle,
                Perspective = refreshSavedPose ? pose.Perspective : source.Camera.Perspective,
            };
            if (refreshSavedPose)
            {
                fov = pose.Fov;
                var parent = retained?.CameraAt(currentFrame);
                settings = WithCameraParent(source.Settings, parent?.ParentModelIndex ?? -1, parent?.ParentBoneIndex ?? -1);
            }
        }
        if (motion.LightKeyframes.Count != 0)
        {
            var records = SeedAndLink(motion.LightKeyframes, k => k.Frame, (key, frame) => new PmmLightKeyframe
            { Frame = frame, Color = key.Color, Direction = key.Direction });
            var pose = sampled.LightAt(currentFrame)!.Value;
            light = new()
            {
                Offset = source.Light.Offset, InitialKeyframe = records[0], Keyframes = records.Skip(1).ToArray(),
                Color = refreshSavedPose ? pose.Color : source.Light.Color,
                Direction = refreshSavedPose ? pose.Direction : source.Light.Direction,
            };
        }
        if (motion.SelfShadowKeyframes.Count != 0)
        {
            var records = SeedAndLink(motion.SelfShadowKeyframes, k => k.Frame, (key, frame) => new PmmSelfShadowKeyframe
            { Frame = frame, Mode = key.Mode, Distance = key.Distance });
            var pose = sampled.SelfShadowAt(currentFrame)!.Value;
            shadow = new()
            {
                Offset = source.SelfShadow.Offset, InitialKeyframe = records[0], Keyframes = records.Skip(1).ToArray(),
                Enabled = refreshSavedPose ? pose.Mode != 0 : source.SelfShadow.Enabled,
                Distance = refreshSavedPose ? pose.Distance : source.SelfShadow.Distance,
            };
        }
        var result = new PmmDocument
        {
            Version = source.Version, AnsiCodePage = source.AnsiCodePage, HeaderBytes = source.HeaderBytes,
            OutputWidth = source.OutputWidth, OutputHeight = source.OutputHeight, TimelineWidth = source.TimelineWidth,
            CameraFov = fov, PanelStates = source.PanelStates, SelectedModelIndex = source.SelectedModelIndex, Models = source.Models,
            V1ModelNames = source.V1ModelNames,
            Camera = camera, Light = light, SelectedAccessoryIndex = source.SelectedAccessoryIndex,
            AccessoryHorizontalScroll = source.AccessoryHorizontalScroll, AccessoryNames = source.AccessoryNames,
            Accessories = source.Accessories, CurrentFrame = (int)currentFrame, Settings = settings, Gravity = source.Gravity,
            SelfShadow = shadow, ParsedLength = source.ParsedLength, BytesConsumed = source.BytesConsumed,
            UnknownTail = source.UnknownTail, Diagnostics = source.Diagnostics,
        };
        _ = PmmWriter.ToBytes(result);
        return result;
    }

    static int Frame(uint frame) => frame <= int.MaxValue ? (int)frame : throw new ArgumentException("Key frame exceeds the PMM Int32 field.");

    static byte[] CameraInterpolation(IReadOnlyList<byte> raw)
    {
        var result = raw.ToArray();
        // Both samplers use the outgoing (previous) camera key; only byte order changes.
        for (int i = 0; i < 24; i += 4) (result[i + 1], result[i + 2]) = (result[i + 2], result[i + 1]);
        return result;
    }

    static PmmKey[] SeedAndLink<VmdKey, PmmKey>(IEnumerable<VmdKey> source, Func<VmdKey, uint> frame,
        Func<VmdKey, int, PmmKey> convert) where PmmKey : PmmKeyframe
    {
        var keys = source.GroupBy(frame).Select(g => g.Last()).OrderBy(frame).ToArray();
        var records = new List<PmmKey>(keys.Length + 1);
        if (frame(keys[0]) != 0) records.Add(convert(keys[0], 0));
        records.AddRange(keys.Select(k => convert(k, Frame(frame(k)))));
        for (int i = 0; i < records.Count; i++)
        {
            var key = records[i]; key.RecordIndex = key.ObjectIndex = i;
            key.PreviousIndex = i == 0 ? 0 : i - 1; key.NextIndex = i == records.Count - 1 ? 0 : i + 1;
        }
        return records.ToArray();
    }

    static PmmSettings WithCameraParent(PmmSettings s, int modelIndex, int boneIndex) => new()
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
        CameraLookAtModelIndex = modelIndex, CameraLookAtBoneIndex = boneIndex, UnknownMatrix = s.UnknownMatrix,
        FollowingLookAtRaw = s.FollowingLookAtRaw, UnknownBooleanRaw = s.UnknownBooleanRaw,
        PhysicsGroundEnabled = s.PhysicsGroundEnabled, CurrentFrameText = s.CurrentFrameText,
        SelectionTrailerFlag = s.SelectionTrailerFlag, ModelSelections = s.ModelSelections,
    };
}
