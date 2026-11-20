// PMM v2 serialization layout adapted from nanoem/ext/document.c:6550-7050,
// @30acffaa29f5d2eb9e997d69418f2e4b97b5894f, MIT, Copyright (c) 2015-2023 hkrn.
// Uses the MIT component only; no nanoem editor-host code is included.
using System.Numerics;
using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace Mmd.Modern.Formats;

public sealed class PmmWriteOptions
{
    /// <summary>Absent: use the document's decoding page (or 936 for a newly constructed document).</summary>
    public int? AnsiCodePage { get; init; }
    /// <summary>Keep original fixed buffers, padding and mixed ANSI payloads. Stale value/byte pairs are rejected.</summary>
    public bool PreserveOriginalStringBytes { get; init; } = true;
    public int MaxElementCount { get; init; } = 2_000_000;
    public int MaxOutputBytes { get; init; } = 512 * 1024 * 1024;
}

/// <summary>Writes represented PMM 0001/0002 fields, linked-record IDs and unknown trailers without opening assets.</summary>
public static class PmmWriter
{
    /// <summary>Validates in a private buffer before writing at the caller's current stream position. Leaves it open.
    /// Destination I/O failure after validation can still produce a partial write.</summary>
    public static void Write(Stream stream, PmmDocument document, PmmWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream); ArgumentNullException.ThrowIfNull(document);
        if (!stream.CanWrite) throw new ArgumentException("PMM stream must be writable.", nameof(stream));
        stream.Write(ToBytes(document, options));
    }

    public static byte[] ToBytes(PmmDocument document, PmmWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var serializer = new Serializer(document, options ?? new());
        return serializer.Serialize();
    }

    private sealed class Serializer : IDisposable
    {
        readonly PmmDocument document;
        readonly PmmWriteOptions options;
        readonly MemoryStream buffer = new();
        readonly BinaryWriter writer;
        readonly Encoding encoding, originalEncoding;
        readonly int codePage;
        string field = "header";
        bool V2 => document.Version == 2;

        internal Serializer(PmmDocument document, PmmWriteOptions options)
        {
            this.document = document; this.options = options;
            codePage = options.AnsiCodePage ?? (document.AnsiCodePage == 0 ? 936 : document.AnsiCodePage);
            if (codePage is not (932 or 936) || document.AnsiCodePage is not (0 or 932 or 936))
                throw new ArgumentOutOfRangeException(nameof(options), "PMM ANSI code page must be 932 or 936.");
            if (options.MaxElementCount < 0 || options.MaxOutputBytes < 30) throw new ArgumentOutOfRangeException(nameof(options));
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            // The reader preserves raw bytes even when its selected page needs replacement decoding.
            // Compare against that original decoding for preservation; only new/transcoded text is strict.
            originalEncoding = Encoding.GetEncoding(document.AnsiCodePage == 0 ? codePage : document.AnsiCodePage);
            writer = new(buffer, Encoding.UTF8, leaveOpen: true);
        }

        void Require([DoesNotReturnIf(false)] bool condition, string reason) { if (!condition) throw new PmmFormatException(field, buffer.Position, reason); }
        void Limit(int count)
        {
            Require(count >= 0 && buffer.Length + count <= options.MaxOutputBytes, "output exceeds configured byte limit");
        }
        void B(byte value) { Limit(1); writer.Write(value); }
        void Flag(bool value) => B(value ? (byte)1 : (byte)0);
        void I(int value) { Limit(4); writer.Write(value); }
        void F(float value) { Require(float.IsFinite(value), "non-finite float"); Limit(4); writer.Write(value); }
        void V(Vector3 value) { F(value.X); F(value.Y); F(value.Z); }
        void Q(Quaternion value) { F(value.X); F(value.Y); F(value.Z); F(value.W); }
        void Bytes(IReadOnlyList<byte> bytes, int expected = -1)
        {
            Require(bytes is not null, "byte collection is null");
            Require(expected < 0 || bytes.Count == expected, $"expected {expected} bytes, got {bytes.Count}");
            Limit(bytes.Count); writer.Write(bytes.ToArray());
        }
        int Length<T>(IReadOnlyList<T> items)
        {
            Require(items is not null, "collection is null");
            Require(items.Count <= options.MaxElementCount, "element count exceeds configured limit");
            return items.Count;
        }
        void Exact<T>(IReadOnlyList<T> items, int expected)
            => Require(Length(items) == expected, $"expected {expected} records");
        void Count<T>(IReadOnlyList<T> items) => I(Length(items));
        void SmallCount<T>(IReadOnlyList<T> items)
        {
            int count = Length(items); Require(count <= byte.MaxValue, "count exceeds one-byte field"); B((byte)count);
        }
        void String(PmmString value, int width = -1)
        {
            Require(value is not null && value.Value is not null && value.RawBytes is not null, "string is null");
            Require(!value.Value.Contains('\0'), "string value contains NUL");
            byte[] data;
            if (options.PreserveOriginalStringBytes && value.RawBytes.Count > 0)
            {
                Require(width < 0 ? value.RawBytes.Count <= 255 : value.RawBytes.Count == width,
                    "original string buffer has wrong width or exceeds one-byte length");
                Limit(value.RawBytes.Count);
                data = value.RawBytes.ToArray();
                Require(width < 0 || data.Length == width, "original fixed buffer has wrong width");
                int nul = Array.IndexOf(data, (byte)0);
                Require(originalEncoding.GetString(data, 0, nul < 0 ? data.Length : nul) == value.Value,
                    "original string bytes no longer match Value; explicitly disable preservation to transcode");
            }
            else
            {
                try
                {
                    int count = encoding.GetByteCount(value.Value);
                    Require(count <= (width < 0 ? 255 : width), "encoded string exceeds its field width");
                    Limit(width < 0 ? count : width);
                    data = encoding.GetBytes(value.Value);
                }
                catch (EncoderFallbackException) { throw new PmmFormatException(field, buffer.Position, $"text cannot be encoded using code page {codePage}"); }
                if (width >= 0)
                {
                    Require(data.Length <= width, $"encoded string exceeds fixed width {width}");
                    Array.Resize(ref data, width);
                }
            }
            if (width < 0) { Require(data.Length <= 255, "encoded string exceeds one-byte length"); B((byte)data.Length); }
            Bytes(data, width);
        }
        void Frame(PmmKeyframe key, bool extra, int initialIndex)
        {
            Require(key is not null, "keyframe is null");
            Require(key.Frame >= 0, "negative frame");
            Require(key.ObjectIndex >= 0 && (extra || key.ObjectIndex == initialIndex), "initial record ObjectIndex must match its implicit track index");
            if (extra) I(key.ObjectIndex);
            I(key.Frame); I(key.PreviousIndex); I(key.NextIndex);
        }
        void Bone(PmmBoneKeyframe key, bool extra, int index)
        { Frame(key, extra, index); Bytes(key.Interpolation, 16); V(key.Translation); Q(key.Rotation); Flag(key.Selected); if (V2) Flag(key.PhysicsDisabled); else Require(!key.PhysicsDisabled, "PMM 0001 cannot represent physics-disabled keys"); }
        void Morph(PmmMorphKeyframe key, bool extra, int index)
        { Frame(key, extra, index); F(key.Weight); Flag(key.Selected); }
        void Parent(PmmOutsideParent parent)
        { Require(parent is not null, "outside parent is null"); ParentIndices(parent.ModelIndex, parent.BoneIndex, allowGround: true); I(parent.ModelIndex); I(parent.BoneIndex); }
        void ParentIndices(int modelIndex, int boneIndex, bool allowGround = false)
        {
            int minModel = allowGround ? -2 : -1;
            Require(modelIndex >= minModel && boneIndex >= -1,
                allowGround ? "parent indices must be -2, -1 or nonnegative" : "parent indices must be -1 or nonnegative");
            if (modelIndex < 0) return;
            var target = document.Models.FirstOrDefault(model => model is not null && model.Index == modelIndex);
            Require(target is not null && boneIndex < target.BoneNames.Count, "parent model/bone reference is outside the document");
        }
        void ModelFrame(PmmModelKeyframe key, bool extra, int constraints, int parents)
        {
            Frame(key, extra, 0); Flag(key.Visible); Exact(key.ConstraintEnabled, constraints); foreach (bool enabled in key.ConstraintEnabled) Flag(enabled);
            Exact(key.OutsideParents, parents); foreach (var parent in key.OutsideParents) Parent(parent); Flag(key.Selected);
        }
        void CameraFrame(PmmCameraKeyframe key, bool extra)
        {
            Frame(key, extra, 0); F(key.Distance); V(key.LookAt); V(key.Angle);
            if (V2) { ParentIndices(key.ParentModelIndex, key.ParentBoneIndex); I(key.ParentModelIndex); I(key.ParentBoneIndex); }
            else Require(key.ParentModelIndex == -1 && key.ParentBoneIndex == -1, "PMM 0001 cannot represent camera parents");
            Bytes(key.Interpolation, 24); B(key.Perspective ? (byte)0 : (byte)1); I(key.Fov); Flag(key.Selected);
        }
        void LightFrame(PmmLightKeyframe key, bool extra)
        { Frame(key, extra, 0); V(key.Color); V(key.Direction); Flag(key.Selected); }
        void AccessoryFrame(PmmAccessoryKeyframe key, bool extra)
        {
            Frame(key, extra, 0); B(key.OpacityAndVisibility); ParentIndices(key.ParentModelIndex, key.ParentBoneIndex); I(key.ParentModelIndex); I(key.ParentBoneIndex);
            V(key.Translation); V(key.Rotation); F(key.Scale); Flag(key.ShadowEnabled); Flag(key.Selected);
        }
        void GravityFrame(PmmGravityKeyframe key, bool extra)
        { Frame(key, extra, 0); Flag(key.NoiseEnabled); I(key.Noise); F(key.Acceleration); V(key.Direction); Flag(key.Selected); }
        void ShadowFrame(PmmSelfShadowKeyframe key, bool extra)
        { Frame(key, extra, 0); B(key.Mode); F(key.Distance); Flag(key.Selected); }

        internal byte[] Serialize()
        {
            Require(document.Version is 1 or 2, "expected PMM 0001 or 0002");
            Require(document.HeaderBytes is not null && document.HeaderBytes.Count is 0 or 30, "expected 0 or 30 header bytes");
            Length(document.Models); Length(document.Accessories);
            Require(document.Models.All(model => model is not null) && document.Models.Select(model => model.Index).Distinct().Count() == document.Models.Count,
                "model IDs must be distinct");
            Require(document.Accessories.All(accessory => accessory is not null) && document.Accessories.Select(accessory => accessory.Index).Distinct().Count() == document.Accessories.Count,
                "accessory IDs must be distinct");
            byte[] header = document.HeaderBytes.Count == 0 ? new byte[30] : document.HeaderBytes.ToArray();
            byte[] magic = Encoding.ASCII.GetBytes(V2 ? "Polygon Movie maker 0002" : "Polygon Movie maker 0001");
            if (document.HeaderBytes.Count == 0) magic.CopyTo(header, 0);
            Require(header.Length == 30 && header.AsSpan(0, 24).SequenceEqual(magic), "header does not match document version");
            Bytes(header, 30); I(document.OutputWidth); I(document.OutputHeight); I(document.TimelineWidth); F(document.CameraFov);
            Bytes(document.PanelStates, V2 ? 7 : 6); B(document.SelectedModelIndex); SmallCount(document.Models);
            if (!V2) { Exact(document.V1ModelNames, document.Models.Count); foreach (var name in document.V1ModelNames) String(name, 20); }
            for (int i = 0; i < document.Models.Count; i++) { field = $"models[{i}]"; Model(document.Models[i]); }
            field = "camera"; var camera = document.Camera; Require(camera is not null, "camera is null");
            CameraFrame(camera.InitialKeyframe, false); Count(camera.Keyframes); foreach (var key in camera.Keyframes) CameraFrame(key, true);
            V(camera.LookAt); V(camera.Position); V(camera.Angle); B(camera.Perspective ? (byte)0 : (byte)1);
            field = "light"; var light = document.Light; Require(light is not null, "light is null");
            LightFrame(light.InitialKeyframe, false); Count(light.Keyframes); foreach (var key in light.Keyframes) LightFrame(key, true); V(light.Color); V(light.Direction);
            field = "accessories"; B(document.SelectedAccessoryIndex); I(document.AccessoryHorizontalScroll); SmallCount(document.Accessories);
            Exact(document.AccessoryNames, document.Accessories.Count); foreach (var name in document.AccessoryNames) String(name, 100);
            for (int i = 0; i < document.Accessories.Count; i++) { field = $"accessories[{i}]"; Accessory(document.Accessories[i]); }
            field = "settings"; I(document.CurrentFrame);
            var settings = document.Settings; Require(settings is not null, "settings is null");
            I(settings.HorizontalScroll); I(settings.HorizontalScrollThumb); I(settings.EditingMode); B(settings.CameraLookMode);
            Flag(settings.LoopEnabled); Flag(settings.BeginFrameEnabled); Flag(settings.EndFrameEnabled); I(settings.BeginFrame); I(settings.EndFrame);
            Flag(settings.AudioEnabled); String(settings.AudioPath, 256); I(settings.VideoOffsetX); I(settings.VideoOffsetY); F(settings.VideoScale);
            String(settings.VideoPath, 256); I(settings.VideoEnabledRaw); I(settings.ImageOffsetX); I(settings.ImageOffsetY); F(settings.ImageScale); String(settings.ImagePath, 256);
            Flag(settings.ImageEnabled); Flag(settings.InformationShown); Flag(settings.GridAndAxisShown); Flag(settings.GroundShadowShown);
            F(settings.PreferredFps); I(settings.ScreenCaptureMode); I(settings.AccessoryIndexAfterModels); F(settings.GroundShadowBrightness);
            if (V2)
            {
            Flag(settings.TranslucentGroundShadowEnabled); B(settings.PhysicsSimulationMode);
            field = "gravity"; var gravity = document.Gravity; Require(gravity is not null, "gravity is null");
            F(gravity.Acceleration); I(gravity.Noise); V(gravity.Direction); Flag(gravity.NoiseEnabled); GravityFrame(gravity.InitialKeyframe, false);
            Count(gravity.Keyframes); foreach (var key in gravity.Keyframes) GravityFrame(key, true);
            field = "selfShadow"; var shadow = document.SelfShadow; Require(shadow is not null, "self-shadow is null");
            Flag(shadow.Enabled); F(shadow.Distance); ShadowFrame(shadow.InitialKeyframe, false); Count(shadow.Keyframes); foreach (var key in shadow.Keyframes) ShadowFrame(key, true);
            field = "settings.tail"; Exact(settings.EdgeColorRgb, 3); foreach (int color in settings.EdgeColorRgb) I(color);
            Flag(settings.BlackBackgroundEnabled); I(settings.CameraLookAtModelIndex); I(settings.CameraLookAtBoneIndex); Bytes(settings.UnknownMatrix, 64);
            B(settings.FollowingLookAtRaw); B(settings.UnknownBooleanRaw); Flag(settings.PhysicsGroundEnabled); I(settings.CurrentFrameText);
            if (settings.SelectionTrailerFlag is { } trailer)
            {
                B(trailer);
                Exact(settings.ModelSelections, trailer > 0 ? document.Models.Count : 0);
                foreach (var selection in settings.ModelSelections) { Require(selection is not null, "selection is null"); B(selection.ModelIndex); I(selection.SelectionIndex); }
            }
            else Require(settings.ModelSelections.Count == 0 && document.UnknownTail.Count == 0, "tail or selections require a represented trailer flag");
            }
            else
            {
                Require(!settings.TranslucentGroundShadowEnabled && settings.PhysicsSimulationMode == 0 && settings.EdgeColorRgb.Count == 0 &&
                    !settings.BlackBackgroundEnabled && settings.UnknownMatrix.Count == 0 && settings.FollowingLookAtRaw == 0 &&
                    settings.UnknownBooleanRaw == 0 && !settings.PhysicsGroundEnabled && settings.CurrentFrameText == 0 &&
                    settings.CameraLookAtModelIndex == 0 && settings.CameraLookAtBoneIndex == 0 &&
                    settings.SelectionTrailerFlag is null && settings.ModelSelections.Count == 0,
                    "PMM 0001 cannot represent PMM 0002 settings");
                var g = document.Gravity; var s = document.SelfShadow;
                Require(g is not null && s is not null, "gravity/self-shadow object is null");
                Require(g.Acceleration == 0 && g.Noise == 0 && g.Direction == Vector3.Zero && !g.NoiseEnabled && g.Keyframes.Count == 0 &&
                    EmptyFrame(g.InitialKeyframe) && !g.InitialKeyframe.NoiseEnabled && g.InitialKeyframe.Noise == 0 && g.InitialKeyframe.Acceleration == 0 && g.InitialKeyframe.Direction == Vector3.Zero &&
                    !s.Enabled && s.Distance == 0 && s.Keyframes.Count == 0 && EmptyFrame(s.InitialKeyframe) && s.InitialKeyframe.Mode == 0 && s.InitialKeyframe.Distance == 0,
                    "PMM 0001 cannot represent gravity/self-shadow sections");
            }
            field = "unknownTail"; Bytes(document.UnknownTail);
            writer.Flush(); buffer.Position = 0;
            // Independent reader implementation validates sparse links, cycles, ownership and all section boundaries.
            _ = PmmReader.Read(buffer, new()
            {
                AnsiCodePage = codePage, MaxElementCount = options.MaxElementCount, MaxInputBytes = options.MaxOutputBytes,
                V1ModelMetadataResolver = reference =>
                {
                    var model = document.Models[reference.ModelPosition];
                    return new(model.BoneNames.Select(name => name.Value), model.MorphNames.Select(name => name.Value), model.ConstraintBoneIndices);
                }
            });
            return buffer.ToArray();
        }

        void Model(PmmModel model)
        {
            Require(model is not null, "model is null"); B(model.Index);
            if (V2)
            {
            String(model.Name); String(model.EnglishName); String(model.Path, 256); B(model.FixedTrackCount);
            Count(model.BoneNames); foreach (var name in model.BoneNames) String(name);
            Count(model.MorphNames); foreach (var name in model.MorphNames) String(name);
            Count(model.ConstraintBoneIndices); foreach (int index in model.ConstraintBoneIndices) I(index);
            Count(model.OutsideParentSubjectBoneIndices);
            foreach (int index in model.OutsideParentSubjectBoneIndices)
            { Require(index >= -1 && index < model.BoneNames.Count, "outside-parent subject bone index is outside model"); I(index); }
            }
            else
            {
                String(model.Name, 20); String(model.Path, 256);
                Require(model.EnglishName.Value.Length == 0 && model.FixedTrackCount == 0 && model.OutsideParentSubjectBoneIndices.Count == 0 &&
                    model.OutsideParentStates.Count == 0 && !model.BlendEnabled && model.EdgeWidth == 0 && !model.SelfShadowEnabled && model.TransformOrderIndex == 0,
                    "PMM 0001 cannot represent PMM 0002 model fields");
            }
            B(model.DrawOrderIndex); Flag(model.Visible); I(model.SelectedBoneIndex); Exact(model.SelectedMorphIndices, 4); foreach (int index in model.SelectedMorphIndices) I(index);
            SmallCount(model.ExpansionStates); Bytes(model.ExpansionStates); I(model.VerticalScroll); I(model.LastFrame);
            Exact(model.InitialBoneKeyframes, model.BoneNames.Count); for (int i = 0; i < model.InitialBoneKeyframes.Count; i++) Bone(model.InitialBoneKeyframes[i], false, i);
            Count(model.BoneKeyframes); foreach (var key in model.BoneKeyframes) Bone(key, true, 0);
            Exact(model.InitialMorphKeyframes, model.MorphNames.Count); for (int i = 0; i < model.InitialMorphKeyframes.Count; i++) Morph(model.InitialMorphKeyframes[i], false, i);
            Count(model.MorphKeyframes); foreach (var key in model.MorphKeyframes) Morph(key, true, 0);
            ModelFrame(model.InitialModelKeyframe, false, model.ConstraintBoneIndices.Count, model.OutsideParentSubjectBoneIndices.Count);
            Count(model.ModelKeyframes); foreach (var key in model.ModelKeyframes) ModelFrame(key, true, model.ConstraintBoneIndices.Count, model.OutsideParentSubjectBoneIndices.Count);
            Exact(model.BoneStates, model.BoneNames.Count);
            foreach (var state in model.BoneStates)
            {
                Require(state is not null, "bone state is null"); V(state.Translation); Q(state.Rotation);
                if (!V2) { Require(!state.PhysicsDisabled, "PMM 0001 cannot represent physics-disabled state"); I(state.V1UnknownValue); }
                Flag(state.Dirty); if (V2) Flag(state.PhysicsDisabled); B(state.SelectedTrackCount);
            }
            Exact(model.MorphStates, model.MorphNames.Count); foreach (var state in model.MorphStates) { Require(state is not null, "morph state is null"); F(state.Weight); }
            Exact(model.ConstraintStates, model.ConstraintBoneIndices.Count); foreach (var state in model.ConstraintStates) { Require(state is not null, "constraint state is null"); Flag(state.Enabled); }
            Exact(model.OutsideParentStates, model.OutsideParentSubjectBoneIndices.Count);
            foreach (var state in model.OutsideParentStates) { Require(state is not null, "outside-parent state is null"); I(state.BeginFrame); I(state.EndFrame); Parent(state.Parent); }
            if (V2) { Flag(model.BlendEnabled); F(model.EdgeWidth); Flag(model.SelfShadowEnabled); B(model.TransformOrderIndex); }
        }
        static bool EmptyFrame(PmmKeyframe key) => key is not null && key.Frame == 0 && key.ObjectIndex == 0 && key.PreviousIndex == 0 && key.NextIndex == 0 && !key.Selected;
        void Accessory(PmmAccessory accessory)
        {
            Require(accessory is not null, "accessory is null"); B(accessory.Index); String(accessory.Name, 100); String(accessory.Path, 256); B(accessory.DrawOrderIndex);
            AccessoryFrame(accessory.InitialKeyframe, false); Count(accessory.Keyframes); foreach (var key in accessory.Keyframes) AccessoryFrame(key, true);
            B(accessory.OpacityAndVisibility); ParentIndices(accessory.ParentModelIndex, accessory.ParentBoneIndex); I(accessory.ParentModelIndex); I(accessory.ParentBoneIndex); V(accessory.Translation); F(accessory.Scale); V(accessory.Rotation);
            Flag(accessory.ShadowEnabled); if (V2) Flag(accessory.AddBlendEnabled); else Require(!accessory.AddBlendEnabled, "PMM 0001 cannot represent accessory additive blending");
        }
        public void Dispose() { writer.Dispose(); buffer.Dispose(); }
    }
}
