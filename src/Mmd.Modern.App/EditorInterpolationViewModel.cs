using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application.MotionEditing;
using Mmd.Modern.Formats;

namespace Mmd.Modern.App;

/// <summary>Detached curve changes. The host checks document identity and commits ApplyTo's result once.</summary>
public sealed class EditorInterpolationDraft
{
    public IReadOnlyList<KeyframeAddress> Selection { get; }
    public IReadOnlyList<uint> CameraFrames { get; }
    public IReadOnlyDictionary<BoneInterpolationChannel, VmdBezierControl> BoneChannels { get; }
    public IReadOnlyDictionary<SceneCameraChannel, VmdBezierControl> CameraChannels { get; }
    public bool IsCamera => CameraFrames.Count != 0;

    internal EditorInterpolationDraft(IReadOnlyList<KeyframeAddress> selection, IReadOnlyList<uint> cameraFrames,
        IEnumerable<EditorInterpolationChannelViewModel> channels)
    {
        Selection = Array.AsReadOnly(selection.ToArray());
        CameraFrames = Array.AsReadOnly(cameraFrames.ToArray());
        var changed = channels.Where(c => c.HasChanges).ToArray();
        BoneChannels = new ReadOnlyDictionary<BoneInterpolationChannel, VmdBezierControl>(IsCamera ? new Dictionary<BoneInterpolationChannel, VmdBezierControl>() :
            changed.ToDictionary(c => (BoneInterpolationChannel)c.Index, c => c.GetCurve()));
        CameraChannels = new ReadOnlyDictionary<SceneCameraChannel, VmdBezierControl>(IsCamera ?
            changed.ToDictionary(c => (SceneCameraChannel)c.Index, c => c.GetCurve()) : new Dictionary<SceneCameraChannel, VmdBezierControl>());
    }

    /// <summary>Uses the established channel byte writer, including bone physics-marker preservation.</summary>
    public VmdMotion ApplyTo(VmdMotion source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsCamera)
        {
            var staged = new SceneTrackEditor(source);
            foreach (var (channel, curve) in CameraChannels)
                staged.Apply(staged.PrepareSetCameraInterpolation(CameraFrames, channel, curve));
            return staged.ExportMotion();
        }
        else
        {
            var staged = new MotionEditor(source);
            foreach (var (channel, curve) in BoneChannels)
                staged.Apply(staged.PrepareSetInterpolation(Selection, channel, curve));
            return staged.ExportMotion();
        }
    }
}

/// <summary>Only actual selected keys supply these drafts. Opening, switching channels and cancelling never write curves.</summary>
public sealed class EditorInterpolationViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyList<KeyframeAddress> selection;
    private readonly IReadOnlyList<uint> cameraFrames;
    private EditorInterpolationChannelViewModel selectedChannel;
    private string validationMessage = string.Empty;
    private EditorInterpolationChannelViewModel? invalidChannel;

    public EditorInterpolationViewModel(VmdMotion source, IEnumerable<KeyframeAddress> selectedBoneKeys)
        : this(source, selectedBoneKeys, null) { }

    public static EditorInterpolationViewModel ForCamera(VmdMotion source, IEnumerable<uint> selectedFrames) =>
        new(source, null, selectedFrames ?? throw new ArgumentNullException(nameof(selectedFrames)));

    private EditorInterpolationViewModel(VmdMotion source, IEnumerable<KeyframeAddress>? selectedBoneKeys, IEnumerable<uint>? selectedCameraFrames)
    {
        ArgumentNullException.ThrowIfNull(source);
        IsCamera = selectedCameraFrames is not null;
        cameraFrames = Array.AsReadOnly(selectedCameraFrames?.Distinct().Order().ToArray() ?? []);
        selection = Array.AsReadOnly(selectedBoneKeys?.Distinct().OrderBy(k => k.Frame).ThenBy(k => k.Name, StringComparer.Ordinal).ToArray() ?? []);
        if (IsCamera)
        {
            if (cameraFrames.Count == 0) throw new ArgumentException(EditorLocalization.Text("请选择相机关键帧。"), nameof(selectedCameraFrames));
            var existing = source.CameraKeyframes.Select(k => k.Frame).ToHashSet();
            if (cameraFrames.Any(f => !existing.Contains(f))) throw new ArgumentException(EditorLocalization.Text("选中的相机关键帧已不存在。"), nameof(selectedCameraFrames));
            var keys = source.CameraKeyframes.Where(k => cameraFrames.Contains(k.Frame)).ToArray();
            Channels = Array.AsReadOnly(Enumerable.Range(0, 6).Select(index =>
                new EditorInterpolationChannelViewModel(index, keys.Select(k => ReadCamera(k, index)).ToArray())).ToArray());
        }
        else
        {
            if (selection.Count == 0 || selection.Any(k => k.Kind != KeyframeTrackKind.Bone))
                throw new ArgumentException(EditorLocalization.Text("请选择骨骼关键帧；表情和 IK 轨道没有骨骼插值曲线。"), nameof(selectedBoneKeys));
            var existing = source.BoneKeyframes.Select(k => KeyframeAddress.Bone(k.BoneName, k.Frame)).ToHashSet();
            if (selection.Any(k => !existing.Contains(k))) throw new ArgumentException(EditorLocalization.Text("选中的骨骼关键帧已不存在。"), nameof(selectedBoneKeys));
            var chosen = selection.ToHashSet();
            var keys = source.BoneKeyframes.Where(k => chosen.Contains(KeyframeAddress.Bone(k.BoneName, k.Frame))).ToArray();
            Channels = Array.AsReadOnly(Enumerable.Range(0, 4).Select(index =>
                new EditorInterpolationChannelViewModel(index, keys.Select(k => ReadBone(k, index)).ToArray())).ToArray());
        }
        selectedChannel = Channels[0];
        foreach (var channel in Channels)
            channel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(EditorInterpolationChannelViewModel.HasChanges)) return;
                Changed(nameof(HasChanges)); ValidationMessage = string.Empty;
            };
        EditorLocalization.Observe(this, static vm =>
        {
            vm.Changed(null);
            foreach (var channel in vm.Channels) channel.RefreshLanguage();
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsCamera { get; }
    public IReadOnlyList<EditorInterpolationChannelViewModel> Channels { get; }
    public bool HasChanges => Channels.Any(c => c.HasChanges);
    public string SelectionSummary => IsCamera ? EditorLocalization.Format("已选择 {0} 个相机关键帧", cameraFrames.Count)
        : EditorLocalization.Format("已选择 {0} 个骨骼关键帧", selection.Count);
    public string SegmentDescription => EditorLocalization.Text(IsCamera ? "相机曲线控制从所选帧到下一关键帧的运动。" : "骨骼曲线控制从前一关键帧到所选帧的运动。");
    public EditorInterpolationChannelViewModel SelectedChannel
    {
        get => selectedChannel;
        set { if (value is null || !Channels.Contains(value) || ReferenceEquals(value, selectedChannel)) return; selectedChannel = value; Changed(); }
    }
    public string ValidationMessage
    {
        get => invalidChannel is { } channel
            ? EditorLocalization.Format("{0}：四个控制点数值都必须是 0–127 的整数。", channel.Name)
            : EditorLocalization.Text(validationMessage);
        private set { invalidChannel = null; validationMessage = value; Changed(); Changed(nameof(HasValidationMessage)); }
    }
    public bool HasValidationMessage => ValidationMessage.Length != 0;
    public void SetValidationMessage(string message) => ValidationMessage = message;

    /// <summary>Null means a byte-preserving no-op; changed channels must each contain four integral control bytes.</summary>
    public bool TryCreateDraft(out EditorInterpolationDraft? draft)
    {
        draft = null;
        foreach (var channel in Channels.Where(c => c.HasChanges))
            if (!channel.IsCurveValid)
            {
                invalidChannel = channel; Changed(nameof(ValidationMessage)); Changed(nameof(HasValidationMessage));
                return false;
            }
        if (HasChanges) draft = new(selection, cameraFrames, Channels);
        ValidationMessage = string.Empty;
        return true;
    }

    private static int[] ReadBone(VmdBoneKeyframe key, int channel)
    {
        int start = 16 * channel;
        return [key.Interpolation[start], key.Interpolation[start + 4], key.Interpolation[start + 8], key.Interpolation[start + 12]];
    }
    private static int[] ReadCamera(VmdCameraKeyframe key, int channel)
    {
        int start = 4 * channel;
        return [key.Interpolation[start], key.Interpolation[start + 2], key.Interpolation[start + 1], key.Interpolation[start + 3]];
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class EditorInterpolationChannelViewModel : INotifyPropertyChanged
{
    private readonly decimal?[] initial;
    private decimal? x1, y1, x2, y2;
    internal EditorInterpolationChannelViewModel(int index, IReadOnlyList<int[]> curves)
    {
        Index = index;
        initial = Enumerable.Range(0, 4).Select(i => curves.Select(c => c[i]).Distinct().Take(2).Count() == 1 ? (decimal?)curves[0][i] : null).ToArray();
        IsOriginallyMixed = initial.Any(v => v is null);
        x1 = initial[0]; y1 = initial[1]; x2 = initial[2]; y2 = initial[3];
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public int Index { get; }
    public string Name => EditorLocalization.Text(Index switch { 0 => "位置 X", 1 => "位置 Y", 2 => "位置 Z", 3 => "旋转", 4 => "距离", _ => "视角" });
    public bool IsOriginallyMixed { get; }
    public bool HasChanges => X1 != initial[0] || Y1 != initial[1] || X2 != initial[2] || Y2 != initial[3];
    public bool IsCurveValid => Valid(X1) && Valid(Y1) && Valid(X2) && Valid(Y2);
    public string CurveHint => EditorLocalization.Text(IsCurveValid ? "拖动两个控制柄，或输入 0–127 的整数。" :
        IsOriginallyMixed && !HasChanges ? "所选关键帧的曲线不同。保留空值可保持原曲线；输入四个值后统一应用。" : "请将四个控制点设为 0–127 的整数。无效数值不会应用。");
    public decimal? X1 { get => x1; set => Set(ref x1, value); }
    public decimal? Y1 { get => y1; set => Set(ref y1, value); }
    public decimal? X2 { get => x2; set => Set(ref x2, value); }
    public decimal? Y2 { get => y2; set => Set(ref y2, value); }
    public VmdBezierControl GetCurve() => IsCurveValid ? new((int)X1!.Value, (int)Y1!.Value, (int)X2!.Value, (int)Y2!.Value)
        : throw new InvalidOperationException("Curve controls must be integral bytes in [0,127].");
    public void SetHandle(int handle, int x, int y)
    {
        if (handle is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(handle));
        if (x is < 0 or > 127 || y is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(x));
        if (handle == 1) { X1 = x; Y1 = y; } else { X2 = x; Y2 = y; }
    }
    public void Reset() { X1 = initial[0]; Y1 = initial[1]; X2 = initial[2]; Y2 = initial[3]; }
    public void SetLinear() { X1 = 20; Y1 = 20; X2 = 107; Y2 = 107; }
    internal void RefreshLanguage() { Changed(nameof(Name)); Changed(nameof(CurveHint)); }
    private static bool Valid(decimal? value) => value is >= 0 and <= 127 && decimal.Truncate(value.Value) == value.Value;
    private void Set(ref decimal? field, decimal? value, [CallerMemberName] string? name = null)
    {
        if (field == value) return; field = value;
        Changed(name); Changed(nameof(HasChanges)); Changed(nameof(IsCurveValid)); Changed(nameof(CurveHint));
    }
    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name));
}
