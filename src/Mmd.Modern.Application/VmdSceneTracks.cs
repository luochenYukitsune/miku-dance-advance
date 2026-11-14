// Camera algorithm adapted from Saba VMDCameraAnimation.cpp @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332.
// Copyright (c) 2016-2019 benikabocha; MIT. Full license: third_party/saba/LICENCE.txt.
using System.Numerics;
using Mmd.Modern.Animation;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.Application;

/// <summary>Raw VMD shadow distance; renderer adapters own the file-to-world distance conversion.</summary>
public readonly record struct VmdSelfShadowPose(byte Mode, float Distance);

/// <summary>Immutable, GPU-independent scene tracks. Missing tracks return null rather than overwrite saved scene state.</summary>
public sealed class VmdSceneTracks
{
    readonly VmdCameraKeyframe[] cameras;
    readonly VmdLightKeyframe[] lights;
    readonly VmdSelfShadowKeyframe[] shadows;
    public uint DurationFrames { get; }

    public VmdSceneTracks(VmdMotion motion)
    {
        ArgumentNullException.ThrowIfNull(motion);
        SceneTrackRecords.Validate(motion);
        cameras = Sorted(motion.CameraKeyframes, k => k.Frame);
        lights = Sorted(motion.LightKeyframes, k => k.Frame);
        shadows = Sorted(motion.SelfShadowKeyframes, k => k.Frame);
        DurationFrames = cameras.Select(k => k.Frame).Concat(lights.Select(k => k.Frame))
            .Concat(shadows.Select(k => k.Frame)).DefaultIfEmpty(0u).Max();
    }

    static T[] Sorted<T>(IEnumerable<T> keys, Func<T, uint> frame) => keys.GroupBy(frame)
        .Select(g => g.Last()).OrderBy(frame).ToArray();

    public PmmCameraPose? CameraAt(double frame)
    {
        var indices = Segment(cameras, k => k.Frame, frame);
        if (indices is not { } s) return null;
        var a = cameras[s.A]; var b = cameras[s.B];
        double t = b.Frame - a.Frame <= 1 ? 0 : s.T;
        float Curve(int channel)
        {
            int i = channel * 4; var c = a.Interpolation;
            // VMD order is x1,x2,y1,y2, unlike PMM's x1,y1,x2,y2.
            return (float)new BezierCurve(c[i] / 127f, c[i + 2] / 127f, c[i + 1] / 127f, c[i + 3] / 127f).Evaluate(t);
        }
        static float L(float x, float y, float t) => x * (1 - t) + y * t;
        return new PmmCameraPose(new(L(a.Interest.X, b.Interest.X, Curve(0)), L(a.Interest.Y, b.Interest.Y, Curve(1)),
            L(a.Interest.Z, b.Interest.Z, Curve(2))), Vector3.Lerp(a.Rotation, b.Rotation, Curve(3)),
            L(a.Distance, b.Distance, Curve(4)), L(a.ViewAngle, b.ViewAngle, Curve(5)), a.Perspective);
    }

    public PmmLightPose? LightAt(double frame)
    {
        var indices = Segment(lights, k => k.Frame, frame);
        if (indices is not { } s) return null;
        var a = lights[s.A]; var b = lights[s.B];
        return new PmmLightPose(Vector3.Lerp(a.Color, b.Color, (float)s.T), Vector3.Lerp(a.Direction, b.Direction, (float)s.T));
    }

    /// <summary>Step/hold policy for the uninterpolated shadow track; exact MMD distance-transition parity remains an oracle check.</summary>
    public VmdSelfShadowPose? SelfShadowAt(double frame)
    {
        var indices = Segment(shadows, k => k.Frame, frame);
        if (indices is not { } s) return null;
        var a = shadows[s.A];
        return new VmdSelfShadowPose(a.Mode, a.Distance);
    }

    static (int A, int B, double T)? Segment<T>(T[] keys, Func<T, uint> keyFrame, double frame)
    {
        if (!double.IsFinite(frame) || frame < 0) throw new ArgumentOutOfRangeException(nameof(frame));
        if (keys.Length == 0) return null;
        if (frame <= keyFrame(keys[0])) return (0, 0, 0);
        int low = 0, high = keys.Length;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (keyFrame(keys[mid]) <= frame) low = mid + 1; else high = mid;
        }
        if (low == keys.Length) return (low - 1, low - 1, 0);
        uint a = keyFrame(keys[low - 1]), b = keyFrame(keys[low]);
        return (low - 1, low, (frame - a) / (b - a));
    }
}

internal static class SceneTrackRecords
{
    internal static void Validate(VmdMotion motion)
    {
        ArgumentNullException.ThrowIfNull(motion);
        if (motion.CameraKeyframes is null || motion.LightKeyframes is null || motion.SelfShadowKeyframes is null)
            throw new ArgumentException("Scene track lists cannot be null.", nameof(motion));
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        foreach (var k in motion.CameraKeyframes)
            if (k is null || !float.IsFinite(k.Distance) || !Finite(k.Interest) || !Finite(k.Rotation)
                || k.Interpolation.Count != 24 || k.Interpolation.Any(b => b > 127))
                throw new ArgumentException("Camera keys require finite values and 24 curve bytes in [0,127].", nameof(motion));
        foreach (var k in motion.LightKeyframes)
            if (k is null || !Finite(k.Color) || !Finite(k.Direction))
                throw new ArgumentException("Light keys require finite color and direction values.", nameof(motion));
        foreach (var k in motion.SelfShadowKeyframes)
            if (k is null || k.Mode > 2 || !float.IsFinite(k.Distance))
                throw new ArgumentException("Shadow keys require finite distance and mode 0, 1 or 2.", nameof(motion));
    }

    internal static SceneKeyframeAddress Address(object key) => key switch
    {
        VmdCameraKeyframe k => new(SceneTrackKind.Camera, k.Frame),
        VmdLightKeyframe k => new(SceneTrackKind.Light, k.Frame),
        VmdSelfShadowKeyframe k => new(SceneTrackKind.SelfShadow, k.Frame),
        _ => throw new ArgumentException("Unknown scene track record.", nameof(key))
    };

    internal static IEnumerable<object> Keys(VmdMotion motion) => motion.CameraKeyframes.Cast<object>()
        .Concat(motion.LightKeyframes).Concat(motion.SelfShadowKeyframes);

    internal static object Copy(object key, uint frame) => key switch
    {
        VmdCameraKeyframe k => new VmdCameraKeyframe { Frame = frame, Distance = k.Distance, Interest = k.Interest,
            Rotation = k.Rotation, ViewAngle = k.ViewAngle, Perspective = k.Perspective, Interpolation = k.Interpolation },
        VmdLightKeyframe k => new VmdLightKeyframe { Frame = frame, Color = k.Color, Direction = k.Direction },
        VmdSelfShadowKeyframe k => new VmdSelfShadowKeyframe { Frame = frame, Mode = k.Mode, Distance = k.Distance },
        _ => throw new ArgumentException("Unknown scene track record.", nameof(key))
    };

    internal static VmdMotion WithKeys(VmdMotion original, IEnumerable<object> records)
    {
        object[] keys = records.ToArray();
        return new VmdMotion
        {
            ModelName = original.ModelName, OriginalModelName = original.OriginalModelName,
            BoneKeyframes = original.BoneKeyframes, MorphKeyframes = original.MorphKeyframes, IkKeyframes = original.IkKeyframes,
            CameraKeyframes = Array.AsReadOnly(keys.OfType<VmdCameraKeyframe>().ToArray()),
            LightKeyframes = Array.AsReadOnly(keys.OfType<VmdLightKeyframe>().ToArray()),
            SelfShadowKeyframes = Array.AsReadOnly(keys.OfType<VmdSelfShadowKeyframe>().ToArray()), Diagnostics = original.Diagnostics
        };
    }

    internal static VmdMotion Snapshot(VmdMotion source) => new MotionEditor(source).ExportMotion();
}
