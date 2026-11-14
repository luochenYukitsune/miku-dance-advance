// VPD grammar referenced from saba VPDFile.cpp @ 29b8efa8b31c8e746f9a88020fb0ad9dcdcf3332.
// MIT License, Copyright (c) 2016-2017 benikabocha. Serializer and validation are local implementations.
using System.Globalization;
using System.Text;

namespace Mmd.Modern.Formats;

/// <summary>Writes canonical VPD text without taking ownership of the output stream.</summary>
public static class VpdWriter
{
    /// <summary>Validates and encodes the complete pose before writing; preserves order and raw XYZW values.</summary>
    public static void Write(Stream stream, VpdPose pose, VpdTextEncoding encoding = VpdTextEncoding.Cp932)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(pose);
        if (!stream.CanWrite) throw new ArgumentException("VPD output stream must be writable.", nameof(stream));
        VpdText.ValidateName(pose.ParentFileName, "header.parentFileName", 0);
        if (pose.ParentFileName != pose.ParentFileName.Trim())
            throw new VpdFormatException("header.parentFileName", 0, "parent name cannot have surrounding whitespace");

        var text = new StringBuilder("Vocaloid Pose Data file\r\n\r\n");
        text.Append(pose.ParentFileName).Append(";\r\n");
        text.Append(pose.Bones.Count.ToString(CultureInfo.InvariantCulture)).Append(";\r\n\r\n");
        var boneIndices = new HashSet<int>();
        for (int i = 0; i < pose.Bones.Count; i++)
        {
            VpdBonePose bone = pose.Bones[i];
            string field = $"bones[{i}]";
            if (bone is null) throw new VpdFormatException(field, 0, "bone cannot be null");
            if (bone.Index < 0 || bone.Index >= pose.Bones.Count || !boneIndices.Add(bone.Index))
                throw new VpdFormatException(field + ".index", 0, "bone indices must be unique and within the bone count");
            VpdText.ValidateName(bone.BoneName, field + ".name", 0);
            if (!float.IsFinite(bone.Translation.X) || !float.IsFinite(bone.Translation.Y) || !float.IsFinite(bone.Translation.Z))
                throw new VpdFormatException(field + ".translation", 0, "expected finite translation values");
            VpdText.ValidateRotation(bone.Rotation, field + ".rotation", 0);
            text.Append("Bone").Append(bone.Index.ToString(CultureInfo.InvariantCulture)).Append('{').Append(bone.BoneName).Append("\r\n");
            AppendNumbers(text, bone.Translation.X, bone.Translation.Y, bone.Translation.Z);
            AppendNumbers(text, bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W);
            text.Append("}\r\n\r\n");
        }

        var morphIndices = new HashSet<int>();
        for (int i = 0; i < pose.Morphs.Count; i++)
        {
            VpdMorphPose morph = pose.Morphs[i];
            string field = $"morphs[{i}]";
            if (morph is null) throw new VpdFormatException(field, 0, "morph cannot be null");
            if (morph.Index < 0 || !morphIndices.Add(morph.Index))
                throw new VpdFormatException(field + ".index", 0, "morph index must be nonnegative and unique");
            VpdText.ValidateName(morph.MorphName, field + ".name", 0);
            if (!float.IsFinite(morph.Weight)) throw new VpdFormatException(field + ".weight", 0, "expected finite morph weight");
            text.Append("Morph").Append(morph.Index.ToString(CultureInfo.InvariantCulture)).Append('{').Append(morph.MorphName).Append("\r\n");
            AppendNumbers(text, morph.Weight);
            text.Append("}\r\n\r\n");
        }
        byte[] bytes = VpdText.Encode(text.ToString(), encoding);
        stream.Write(bytes);
    }

    private static void AppendNumbers(StringBuilder text, params float[] values)
    {
        text.Append("  ");
        for (int i = 0; i < values.Length; i++)
        {
            if (i != 0) text.Append(',');
            text.Append(values[i].ToString("R", CultureInfo.InvariantCulture));
        }
        text.Append(";\r\n");
    }
}
