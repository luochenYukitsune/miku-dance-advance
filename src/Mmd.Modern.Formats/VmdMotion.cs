// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// VMDFile.h record layouts. Coordinates and file quaternion (x,y,z,w) are preserved.

using System.Numerics;

namespace Mmd.Modern.Formats;

/// <summary>VMD tracks in original record order, including duplicate frames and unplayed sections.</summary>
public sealed class VmdMotion
{
    public string ModelName { get; init; } = string.Empty;
    public VmdEncodedName? OriginalModelName { get; init; }
    public IReadOnlyList<VmdBoneKeyframe> BoneKeyframes { get; init; } = Array.Empty<VmdBoneKeyframe>();
    public IReadOnlyList<VmdMorphKeyframe> MorphKeyframes { get; init; } = Array.Empty<VmdMorphKeyframe>();
    public IReadOnlyList<VmdCameraKeyframe> CameraKeyframes { get; init; } = Array.Empty<VmdCameraKeyframe>();
    public IReadOnlyList<VmdLightKeyframe> LightKeyframes { get; init; } = Array.Empty<VmdLightKeyframe>();
    public IReadOnlyList<VmdSelfShadowKeyframe> SelfShadowKeyframes { get; init; } = Array.Empty<VmdSelfShadowKeyframe>();
    public IReadOnlyList<VmdIkKeyframe> IkKeyframes {get;init;}=Array.Empty<VmdIkKeyframe>();

    /// <summary>Explicitly reports retained non-character sections which are not played.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public readonly record struct VmdIkSwitch(string BoneName,bool Enabled)
{
    public VmdEncodedName? OriginalName { get; init; }
    // Provenance is metadata; retain the existing two-value identity and deconstruction contract.
    public bool Equals(VmdIkSwitch other) => string.Equals(BoneName, other.BoneName, StringComparison.Ordinal) && Enabled == other.Enabled;
    public override int GetHashCode() => HashCode.Combine(BoneName, Enabled);
}

public sealed class VmdIkKeyframe
{
    private IReadOnlyList<VmdIkSwitch> switches=Array.Empty<VmdIkSwitch>();
    public uint Frame {get;init;}
    public bool Visible {get;init;}=true;
    public IReadOnlyList<VmdIkSwitch> Switches
    {
        get=>switches;
        init {ArgumentNullException.ThrowIfNull(value);switches=Array.AsReadOnly(value.ToArray());}
    }
}

public sealed class VmdBoneKeyframe
{
    private static readonly IReadOnlyList<byte> EmptyInterpolation = Array.AsReadOnly(new byte[64]);
    private IReadOnlyList<byte> _interpolation = EmptyInterpolation;

    public string BoneName { get; init; } = string.Empty;
    public VmdEncodedName? OriginalName { get; init; }
    public uint Frame { get; init; }
    public Vector3 Translation { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;

    /// <summary>The original 64 bytes, copied on assignment; playback chooses curve channels.</summary>
    public IReadOnlyList<byte> Interpolation
    {
        get => _interpolation;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count != 64)
                throw new ArgumentException("VMD bone interpolation must contain exactly 64 bytes.", nameof(value));
            _interpolation = Array.AsReadOnly(value.ToArray());
        }
    }
}

public sealed class VmdMorphKeyframe
{
    public string MorphName { get; init; } = string.Empty;
    public VmdEncodedName? OriginalName { get; init; }
    public uint Frame { get; init; }
    public float Weight { get; init; }
}

public sealed class VmdCameraKeyframe
{
    private IReadOnlyList<byte> _interpolation = Array.AsReadOnly(new byte[24]);

    public uint Frame { get; init; }
    public float Distance { get; init; }
    public Vector3 Interest { get; init; }
    public Vector3 Rotation { get; init; }
    public uint ViewAngle { get; init; }
    /// <summary>True is perspective (file byte 0); false is orthographic (file byte 1).</summary>
    public bool Perspective { get; init; } = true;

    /// <summary>The original 24 bytes, copied on assignment without curve conversion.</summary>
    public IReadOnlyList<byte> Interpolation
    {
        get => _interpolation;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count != 24)
                throw new ArgumentException("VMD camera interpolation must contain exactly 24 bytes.", nameof(value));
            _interpolation = Array.AsReadOnly(value.ToArray());
        }
    }
}

public sealed class VmdLightKeyframe
{
    public uint Frame { get; init; }
    public Vector3 Color { get; init; }
    public Vector3 Direction { get; init; }
}

public sealed class VmdSelfShadowKeyframe
{
    public uint Frame { get; init; }
    public byte Mode { get; init; }
    public float Distance { get; init; }
}
