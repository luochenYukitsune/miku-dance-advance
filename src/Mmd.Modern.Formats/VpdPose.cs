// Ported from saba (https://github.com/benikabocha/saba) @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332, MIT License, Copyright (c) 2016-2017 benikabocha
// VPDFile.h value layout, extended with explicit record indices and immutable collection snapshots.
using System.Numerics;

namespace Mmd.Modern.Formats;

public enum VpdTextEncoding { Auto, Cp932, Utf8 }

public sealed class VpdReadOptions
{
    public VpdTextEncoding Encoding { get; init; } = VpdTextEncoding.Auto;
    public int MaxInputBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxElementCount { get; init; } = 1_000_000;
}

/// <summary>A pose snapshot; indices are VPD record indices, not model bone indices.</summary>
public sealed class VpdPose
{
    private IReadOnlyList<VpdBonePose> bones = Array.Empty<VpdBonePose>();
    private IReadOnlyList<VpdMorphPose> morphs = Array.Empty<VpdMorphPose>();
    public string ParentFileName { get; init; } = string.Empty;
    public IReadOnlyList<VpdBonePose> Bones
    {
        get => bones;
        init { ArgumentNullException.ThrowIfNull(value); bones = Array.AsReadOnly(value.ToArray()); }
    }
    public IReadOnlyList<VpdMorphPose> Morphs
    {
        get => morphs;
        init { ArgumentNullException.ThrowIfNull(value); morphs = Array.AsReadOnly(value.ToArray()); }
    }
}

public sealed record VpdBonePose
{
    public int Index { get; init; }
    public string BoneName { get; init; } = string.Empty;
    public Vector3 Translation { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
}

/// <summary>The trailing Morph extension read by Saba; the header counts bones only.</summary>
public sealed record VpdMorphPose
{
    public int Index { get; init; }
    public string MorphName { get; init; } = string.Empty;
    public float Weight { get; init; }
}
