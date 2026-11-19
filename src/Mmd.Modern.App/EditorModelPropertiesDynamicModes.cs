using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.ViewOutput.D3D12;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>Localized choice inside an enum-typed style parameter dropdown.</summary>
public sealed class EditorStyleEnumChoice : INotifyPropertyChanged
{
    public EditorStyleEnumChoice(string value, LocalizedText displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    public string Value { get; }
    public LocalizedText DisplayName { get; }
    public string Label => DisplayName.Resolve(CurrentLanguage) is { Length: > 0 } text ? text : Value;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public override string ToString() => Label;
}

/// <summary>Dynamically bound UI item for one <see cref="StyleParameterDescriptor"/> on a material mode.</summary>
public sealed class EditorStyleParameterItem : INotifyPropertyChanged
{
    private readonly Action onChanged;
    private readonly IReadOnlyList<EditorSourceChoice> textures;
    private readonly IReadOnlyList<EditorSourceChoice> bones;
    private decimal? decimalValue;
    private bool boolValue;
    private EditorStyleEnumChoice? selectedEnumChoice;
    private EditorSourceChoice? selectedTextureChoice;
    private EditorSourceChoice? selectedBoneChoice;
    private decimal? vecX, vecY, vecZ, vecW;
    private bool suppressNotifications;

    public EditorStyleParameterItem(
        StyleParameterDescriptor descriptor,
        IReadOnlyList<EditorSourceChoice> textures,
        IReadOnlyList<EditorSourceChoice> bones,
        StyleMaterialBindingData? previous,
        Action onChanged)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        this.textures = textures;
        this.bones = bones;
        this.onChanged = onChanged;

        EnumChoices = descriptor.EnumOptions is { Count: > 0 } opts
            ? opts.Select(opt => new EditorStyleEnumChoice(opt.Value, opt.DisplayName)).ToArray()
            : [];

        double lo = Math.Min(descriptor.MinNumber, descriptor.MaxNumber);
        double hi = Math.Max(descriptor.MinNumber, descriptor.MaxNumber);
        if (Math.Abs(hi - lo) < 1e-9)
            hi = lo + (descriptor.Kind == StyleParameterKind.Int ? 10 : 1);
        MinValue = lo;
        MaxValue = hi;
        MinDecimal = SafeDecimal(lo, -1000000m);
        MaxDecimal = SafeDecimal(hi, 1000000m);
        double step = descriptor.Step > 0 && double.IsFinite(descriptor.Step)
            ? descriptor.Step
            : (descriptor.Kind == StyleParameterKind.Int ? 1.0 : 0.01);
        StepSize = step;
        StepDecimal = SafeDecimal(step, descriptor.Kind == StyleParameterKind.Int ? 1m : 0.01m);
        if (StepDecimal <= 0) StepDecimal = descriptor.Kind == StyleParameterKind.Int ? 1m : 0.01m;

        ColorChannelMaxDecimal = Math.Max(10m, MaxDecimal);
        InitializeFrom(previous);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StyleParameterDescriptor Descriptor { get; }
    public string Key => Descriptor.Key;
    public StyleParameterKind Kind => Descriptor.Kind;
    public IReadOnlyList<EditorSourceChoice> Textures => textures;
    public IReadOnlyList<EditorSourceChoice> Bones => bones;
    public IReadOnlyList<EditorStyleEnumChoice> EnumChoices { get; }

    public bool IsFloat => Kind == StyleParameterKind.Float;
    public bool IsInt => Kind == StyleParameterKind.Int;
    public bool IsBool => Kind == StyleParameterKind.Bool;
    public bool IsEnum => Kind == StyleParameterKind.Enum;
    public bool IsColor3 => Kind == StyleParameterKind.Color3;
    public bool IsColor4 => Kind == StyleParameterKind.Color4;
    public bool IsColor => IsColor3 || IsColor4;
    public bool IsVector2 => Kind == StyleParameterKind.Vector2;
    public bool IsVector3 => Kind == StyleParameterKind.Vector3;
    public bool IsVector4 => Kind == StyleParameterKind.Vector4;
    public bool IsVector => IsVector2 || IsVector3 || IsVector4;
    public bool ShowVectorZ => IsVector3 || IsVector4 || IsColor;
    public bool ShowVectorW => IsVector4 || IsColor4;
    public bool IsTextureSlot => Kind == StyleParameterKind.TextureSlot;
    public bool IsBoneName => Kind == StyleParameterKind.BoneName;

    public string DisplayName => ResolveText(Descriptor.DisplayName, Descriptor.Key);
    public string Description => ResolveText(Descriptor.Description, "");
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public double MinValue { get; }
    public double MaxValue { get; }
    public double StepSize { get; }
    public double DefaultDouble => Descriptor.DefaultNumber;
    public decimal MinDecimal { get; }
    public decimal MaxDecimal { get; }
    public decimal StepDecimal { get; }
    public decimal ColorChannelMaxDecimal { get; }

    public decimal? DecimalValue
    {
        get => decimalValue;
        set
        {
            if (decimalValue == value) return;
            decimalValue = value;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public bool BoolValue
    {
        get => boolValue;
        set
        {
            if (boolValue == value) return;
            boolValue = value;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public EditorStyleEnumChoice? SelectedEnumChoice
    {
        get => selectedEnumChoice;
        set
        {
            if (ReferenceEquals(selectedEnumChoice, value)) return;
            selectedEnumChoice = value;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public EditorSourceChoice? SelectedTextureChoice
    {
        get => selectedTextureChoice;
        set
        {
            if (ReferenceEquals(selectedTextureChoice, value)) return;
            selectedTextureChoice = value;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public EditorSourceChoice? SelectedBoneChoice
    {
        get => selectedBoneChoice;
        set
        {
            if (ReferenceEquals(selectedBoneChoice, value)) return;
            selectedBoneChoice = value;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public decimal? VecX
    {
        get => vecX;
        set
        {
            if (vecX == value) return;
            vecX = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ColorPreviewBrush));
            NotifyChanged();
        }
    }

    public decimal? VecY
    {
        get => vecY;
        set
        {
            if (vecY == value) return;
            vecY = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ColorPreviewBrush));
            NotifyChanged();
        }
    }

    public decimal? VecZ
    {
        get => vecZ;
        set
        {
            if (vecZ == value) return;
            vecZ = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ColorPreviewBrush));
            NotifyChanged();
        }
    }

    public decimal? VecW
    {
        get => vecW;
        set
        {
            if (vecW == value) return;
            vecW = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ColorPreviewBrush));
            NotifyChanged();
        }
    }

    public ISolidColorBrush ColorPreviewBrush
    {
        get
        {
            float r = Math.Max(0f, (float)(vecX ?? 0m));
            float g = Math.Max(0f, (float)(vecY ?? 0m));
            float b = Math.Max(0f, (float)(vecZ ?? 0m));
            float peak = Math.Max(1f, Math.Max(r, Math.Max(g, b)));
            byte rb = (byte)Math.Clamp((int)MathF.Round((r / peak) * 255f), 0, 255);
            byte gb = (byte)Math.Clamp((int)MathF.Round((g / peak) * 255f), 0, 255);
            byte bb = (byte)Math.Clamp((int)MathF.Round((b / peak) * 255f), 0, 255);
            float a = IsColor4 ? Math.Clamp((float)(vecW ?? 1m), 0f, 1f) : 1f;
            byte ab = (byte)Math.Clamp((int)MathF.Round(a * 255f), 0, 255);
            return new SolidColorBrush(Color.FromArgb(ab, rb, gb, bb));
        }
    }

    public void InitializeFrom(StyleMaterialBindingData? previous)
    {
        suppressNotifications = true;
        try
        {
            decimalValue = SafeDecimal(Math.Clamp(Descriptor.DefaultNumber, MinValue, MaxValue), MinDecimal);
            vecX = 0m;
            vecY = 0m;
            vecZ = 0m;
            vecW = IsColor4 ? 1m : 0m;
            switch (Kind)
            {
                case StyleParameterKind.Float:
                {
                    double val = previous?.NumberValues is { } nums && nums.TryGetValue(Key, out double num) && double.IsFinite(num)
                        ? num
                        : Descriptor.DefaultNumber;
                    decimalValue = SafeDecimal(val, 0m);
                    break;
                }
                case StyleParameterKind.Int:
                {
                    double val = previous?.NumberValues is { } nums && nums.TryGetValue(Key, out double num) && double.IsFinite(num)
                        ? Math.Round(num)
                        : Math.Round(Descriptor.DefaultNumber);
                    decimalValue = SafeDecimal(val, 0m);
                    break;
                }
                case StyleParameterKind.Bool:
                    boolValue = previous?.BoolValues is { } bools && bools.TryGetValue(Key, out bool flag)
                        ? flag
                        : Descriptor.DefaultBool;
                    break;
                case StyleParameterKind.Enum:
                {
                    string? target = previous?.StringValues is { } strs && strs.TryGetValue(Key, out var s) && s is not null
                        ? s
                        : Descriptor.DefaultString;
                    selectedEnumChoice = EnumChoices.FirstOrDefault(c => string.Equals(c.Value, target, StringComparison.Ordinal))
                        ?? EnumChoices.FirstOrDefault();
                    break;
                }
                case StyleParameterKind.TextureSlot:
                {
                    int ordinal = -1;
                    if (previous?.TextureSlots is { } slotMap && slotMap.TryGetValue(Key, out int mappedSlot))
                        ordinal = mappedSlot;
                    else if (previous?.NumberValues is { } numMap && numMap.TryGetValue(Key, out double numSlot) && double.IsFinite(numSlot))
                        ordinal = (int)Math.Round(numSlot);
                    else if (double.IsFinite(Descriptor.DefaultNumber) && Descriptor.DefaultNumber >= 0 && previous is null)
                        ordinal = (int)Math.Round(Descriptor.DefaultNumber);
                    selectedTextureChoice = textures.FirstOrDefault(t => t.Index == ordinal) ?? textures.FirstOrDefault();
                    break;
                }
                case StyleParameterKind.BoneName:
                {
                    string? boneName = previous?.StringValues is { } strs && strs.TryGetValue(Key, out var s) && s is not null
                        ? s
                        : Descriptor.DefaultString;
                    selectedBoneChoice = string.IsNullOrEmpty(boneName)
                        ? null
                        : bones.FirstOrDefault(b => string.Equals(b.EffectiveName, boneName, StringComparison.Ordinal));
                    break;
                }
                case StyleParameterKind.Color3:
                case StyleParameterKind.Color4:
                case StyleParameterKind.Vector2:
                case StyleParameterKind.Vector3:
                case StyleParameterKind.Vector4:
                {
                    float[]? vec = previous?.VectorValues is { } vecs && vecs.TryGetValue(Key, out var savedVec) && savedVec is not null
                        ? savedVec
                        : Descriptor.DefaultVector;
                    float x = vec is { Length: > 0 } ? vec[0] : 0f;
                    float y = vec is { Length: > 1 } ? vec[1] : 0f;
                    float z = vec is { Length: > 2 } ? vec[2] : (IsColor4 ? 0f : 0f);
                    float w = vec is { Length: > 3 } ? vec[3] : (IsColor4 ? 1f : 0f);
                    vecX = SafeDecimal(x, 0m);
                    vecY = SafeDecimal(y, 0m);
                    vecZ = SafeDecimal(z, 0m);
                    vecW = SafeDecimal(w, IsColor4 ? 1m : 0m);
                    break;
                }
            }
            OnPropertyChanged(null);
        }
        finally
        {
            suppressNotifications = false;
        }
    }

    public void ResetToDefault() => InitializeFrom(null);

    public void WriteTo(
        ref Dictionary<string, int>? slots,
        ref Dictionary<string, double>? numbers,
        ref Dictionary<string, bool>? bools,
        ref Dictionary<string, float[]>? vectors,
        ref Dictionary<string, string>? strings,
        Action<int>? captureTextureGuard)
    {
        switch (Kind)
        {
            case StyleParameterKind.Float:
            {
                numbers ??= new(StringComparer.Ordinal);
                double raw = decimalValue.HasValue ? (double)decimalValue.Value : Descriptor.DefaultNumber;
                numbers[Key] = Math.Clamp(raw, MinValue, MaxValue);
                break;
            }
            case StyleParameterKind.Int:
            {
                numbers ??= new(StringComparer.Ordinal);
                double raw = decimalValue.HasValue ? Math.Round((double)decimalValue.Value) : Math.Round(Descriptor.DefaultNumber);
                numbers[Key] = Math.Clamp(raw, MinValue, MaxValue);
                break;
            }
            case StyleParameterKind.Bool:
                bools ??= new(StringComparer.Ordinal);
                bools[Key] = boolValue;
                break;
            case StyleParameterKind.Enum:
                if (selectedEnumChoice is { } chosen)
                {
                    strings ??= new(StringComparer.Ordinal);
                    strings[Key] = chosen.Value;
                }
                else if (Descriptor.DefaultString is not null)
                {
                    strings ??= new(StringComparer.Ordinal);
                    strings[Key] = Descriptor.DefaultString;
                }
                break;
            case StyleParameterKind.BoneName:
                if (selectedBoneChoice is { EffectiveName: { Length: > 0 } bone })
                {
                    strings ??= new(StringComparer.Ordinal);
                    strings[Key] = bone;
                }
                else if (Descriptor.DefaultString is not null)
                {
                    strings ??= new(StringComparer.Ordinal);
                    strings[Key] = Descriptor.DefaultString;
                }
                break;
            case StyleParameterKind.TextureSlot:
                if (selectedTextureChoice is { Index: >= 0 } tex)
                {
                    captureTextureGuard?.Invoke(tex.Index);
                    slots ??= new(StringComparer.Ordinal);
                    slots[Key] = tex.Index;
                }
                break;
            case StyleParameterKind.Vector2:
                vectors ??= new(StringComparer.Ordinal);
                vectors[Key] = [(float)(vecX ?? 0m), (float)(vecY ?? 0m)];
                break;
            case StyleParameterKind.Vector3:
            case StyleParameterKind.Color3:
                vectors ??= new(StringComparer.Ordinal);
                vectors[Key] = [(float)(vecX ?? 0m), (float)(vecY ?? 0m), (float)(vecZ ?? 0m)];
                break;
            case StyleParameterKind.Vector4:
            case StyleParameterKind.Color4:
                vectors ??= new(StringComparer.Ordinal);
                vectors[Key] = [(float)(vecX ?? 0m), (float)(vecY ?? 0m), (float)(vecZ ?? 0m), (float)(vecW ?? 0m)];
                break;
        }
    }

    internal void RefreshLocalization()
    {
        foreach (var choice in EnumChoices)
            choice.RefreshLocalization();
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
    }

    private void NotifyChanged()
    {
        if (!suppressNotifications)
            onChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));

    private static decimal SafeDecimal(float value, decimal fallback)
    {
        if (!float.IsFinite(value)) return fallback;
        try { return (decimal)value; }
        catch (OverflowException) { return fallback; }
    }

    private static decimal SafeDecimal(double value, decimal fallback)
    {
        if (!double.IsFinite(value)) return fallback;
        try { return (decimal)value; }
        catch (OverflowException) { return fallback; }
    }

    private static string ResolveText(LocalizedText? text, string fallback)
    {
        string? resolved = text?.Resolve(CurrentLanguage);
        return string.IsNullOrWhiteSpace(resolved) ? fallback : resolved;
    }
}

/// <summary>Dynamically bound UI item for one <see cref="StyleTextureSlotDescriptor"/>.</summary>
public sealed class EditorStyleTextureSlotItem : INotifyPropertyChanged
{
    private readonly Action onChanged;
    private EditorSourceChoice? selectedPrimary;
    private EditorSourceChoice? selectedSecondary;
    private bool suppressNotifications;

    public EditorStyleTextureSlotItem(
        StyleTextureSlotDescriptor descriptor,
        IReadOnlyList<EditorSourceChoice> textures,
        StyleMaterialBindingData? previous,
        Action onChanged)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Textures = textures;
        this.onChanged = onChanged;
        SecondarySlotKey = string.Equals(descriptor.SlotKey, "primary", StringComparison.Ordinal)
            ? "secondary"
            : descriptor.SlotKey + "Secondary";
        InitializeFrom(previous);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StyleTextureSlotDescriptor Descriptor { get; }
    public string SlotKey => Descriptor.SlotKey;
    public string SecondarySlotKey { get; }
    public IReadOnlyList<EditorSourceChoice> Textures { get; }
    public bool SupportsSecondaryTexture => Descriptor.SupportsSecondaryTexture;

    public string PrimaryDisplayName
    {
        get
        {
            string baseLabel = ResolveText(Descriptor.DisplayName, Descriptor.SlotKey);
            return Descriptor.Required ? baseLabel + " " + Text("Mda.Text.ModelProps.RequiredSlotSuffix") : baseLabel;
        }
    }

    public string SecondaryDisplayName => ResolveText(Descriptor.SecondaryDisplayName, Text("Mda.Text.ModelProps.SecondarySlotDefault"));
    public string Description => ResolveText(Descriptor.Description, "");
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public EditorSourceChoice? SelectedPrimary
    {
        get => selectedPrimary;
        set
        {
            if (ReferenceEquals(selectedPrimary, value)) return;
            selectedPrimary = value;
            OnPropertyChanged();
            if (!suppressNotifications) onChanged();
        }
    }

    public EditorSourceChoice? SelectedSecondary
    {
        get => selectedSecondary;
        set
        {
            if (ReferenceEquals(selectedSecondary, value)) return;
            selectedSecondary = value;
            OnPropertyChanged();
            if (!suppressNotifications) onChanged();
        }
    }

    public void InitializeFrom(StyleMaterialBindingData? previous)
    {
        suppressNotifications = true;
        try
        {
            int primaryOrdinal = previous?.TextureSlots is { } slots && slots.TryGetValue(SlotKey, out int p) ? p : -1;
            selectedPrimary = Textures.FirstOrDefault(t => t.Index == primaryOrdinal) ?? Textures.FirstOrDefault();

            int secondaryOrdinal = -1;
            if (SupportsSecondaryTexture && previous?.TextureSlots is { } secSlots)
            {
                if (!secSlots.TryGetValue(SecondarySlotKey, out secondaryOrdinal))
                    secSlots.TryGetValue("secondary", out secondaryOrdinal);
            }
            selectedSecondary = Textures.FirstOrDefault(t => t.Index == secondaryOrdinal) ?? Textures.FirstOrDefault();

            OnPropertyChanged(nameof(SelectedPrimary));
            OnPropertyChanged(nameof(SelectedSecondary));
        }
        finally
        {
            suppressNotifications = false;
        }
    }

    public void ResetToDefault() => InitializeFrom(null);

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(PrimaryDisplayName));
        OnPropertyChanged(nameof(SecondaryDisplayName));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));

    private static string ResolveText(LocalizedText? text, string fallback)
    {
        string? resolved = text?.Resolve(CurrentLanguage);
        return string.IsNullOrWhiteSpace(resolved) ? fallback : resolved;
    }
}

/// <summary>Composable mode card dynamically constructed from <see cref="StyleMaterialModeDescriptor"/>.</summary>
public sealed class EditorStyleModeItem : INotifyPropertyChanged
{
    private readonly Func<StyleMaterialRole?> getCurrentRole;
    private readonly Action onChanged;
    private bool isEnabled;
    private int boneTrackingModeIndex;
    private EditorSourceChoice? selectedBone;
    private Vector3 fixedForward;
    private Vector3 fixedRight;
    private Vector3 localForward;
    private Vector3 localUp;
    private bool suppressNotifications;

    public EditorStyleModeItem(
        IRenderingStyleDefinition style,
        StyleMaterialModeDescriptor mode,
        IReadOnlyList<EditorSourceChoice> textures,
        IReadOnlyList<EditorSourceChoice> bones,
        StyleMaterialBindingData? previous,
        Func<StyleMaterialRole?> getCurrentRole,
        Action onChanged)
    {
        Style = style ?? throw new ArgumentNullException(nameof(style));
        Mode = mode ?? throw new ArgumentNullException(nameof(mode));
        Bones = bones;
        this.getCurrentRole = getCurrentRole;
        this.onChanged = onChanged;

        foreach (var slot in mode.TextureInputs)
            TextureSlots.Add(new EditorStyleTextureSlotItem(slot, textures, previous, HandleChildChanged));

        foreach (var param in mode.Parameters)
            Parameters.Add(new EditorStyleParameterItem(param, textures, bones, previous, HandleChildChanged));

        InitializeBoneTracking(previous);
        isEnabled = previous is not null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IRenderingStyleDefinition Style { get; }
    public StyleMaterialModeDescriptor Mode { get; }
    public string StyleId => Style.StyleId;
    public string ModeId => Mode.ModeId;
    public IReadOnlyList<EditorSourceChoice> Bones { get; }
    public ObservableCollection<EditorStyleTextureSlotItem> TextureSlots { get; } = [];
    public ObservableCollection<EditorStyleParameterItem> Parameters { get; } = [];

    public string DisplayName => ResolveText(Mode.DisplayName, Mode.ModeId);
    public string Description => ResolveText(Mode.Description, "");
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasParameters => Parameters.Count > 0;
    public bool HasTextureSlots => TextureSlots.Count > 0;
    public bool SupportsBoneTracking => Mode.SupportsBoneTracking;

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (isEnabled == value) return;
            isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowRoleRequirementNote));
            if (!suppressNotifications) onChanged();
        }
    }

    public string[] BoneTrackingModes =>
    [
        Text("Mda.Text.ModelProps.BoneTrackingFixedWorld"),
        Text("Mda.Text.ModelProps.BoneTrackingSelectedBone")
    ];

    public int BoneTrackingModeIndex
    {
        get => boneTrackingModeIndex;
        set
        {
            int clamped = value == 1 ? 1 : 0;
            if (boneTrackingModeIndex == clamped) return;
            boneTrackingModeIndex = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFixedWorldTracking));
            OnPropertyChanged(nameof(IsSelectedBoneTracking));
            if (!suppressNotifications) onChanged();
        }
    }

    public bool IsFixedWorldTracking => SupportsBoneTracking && boneTrackingModeIndex == 0;
    public bool IsSelectedBoneTracking => SupportsBoneTracking && boneTrackingModeIndex == 1;

    public EditorSourceChoice? SelectedBone
    {
        get => selectedBone;
        set
        {
            if (ReferenceEquals(selectedBone, value)) return;
            selectedBone = value;
            OnPropertyChanged();
            if (!suppressNotifications) onChanged();
        }
    }

    public string FixedBasisSummary => Format(
        "固定世界方向  前 ({0:0.###}, {1:0.###}, {2:0.###})  右 ({3:0.###}, {4:0.###}, {5:0.###})",
        fixedForward.X, fixedForward.Y, fixedForward.Z, fixedRight.X, fixedRight.Y, fixedRight.Z);

    public string LocalAxisSummary => Format(
        "Mda.Text.ModelProps.LocalAxisSummary",
        $"({localForward.X:0.###}, {localForward.Y:0.###}, {localForward.Z:0.###})",
        $"({localUp.X:0.###}, {localUp.Y:0.###}, {localUp.Z:0.###})");

    public bool IsRoleAllowed
    {
        get
        {
            var current = getCurrentRole();
            if (current is null) return true;
            if (Mode.AllowedRoles.Count == 0) return true;
            return Mode.AllowedRoles.Contains(current.Value);
        }
    }

    public bool ShowRoleRequirementNote => getCurrentRole() is not null && !IsRoleAllowed;

    public string RoleRequirementNote
    {
        get
        {
            var current = getCurrentRole();
            if (current is null) return "";
            string allowed = string.Join(" / ", Mode.AllowedRoles.Select(FormatRoleName));
            return Format("Mda.Text.ModelProps.RoleRequirementNote", allowed, FormatRoleName(current.Value));
        }
    }

    public void SetFixedBasis(Vector3 forward, Vector3 right)
    {
        if (!float.IsFinite(forward.LengthSquared()) || !float.IsFinite(right.LengthSquared())
            || forward.LengthSquared() < 1e-8f || right.LengthSquared() < 1e-8f)
            throw Error("所选骨骼的朝向无效。");
        fixedForward = Vector3.Normalize(forward);
        fixedRight = Vector3.Normalize(right);
        OnPropertyChanged(nameof(FixedBasisSummary));
        if (!suppressNotifications) onChanged();
    }

    public void ResetToDefaults()
    {
        suppressNotifications = true;
        try
        {
            foreach (var slot in TextureSlots)
                slot.ResetToDefault();
            foreach (var param in Parameters)
                param.ResetToDefault();
            InitializeBoneTracking(null);
        }
        finally
        {
            suppressNotifications = false;
        }
        onChanged();
    }

    public StyleMaterialBindingData BuildBindingData(
        string materialLabel,
        Func<int, int> captureTextureOrdinal,
        bool strictValidation)
    {
        Dictionary<string, int>? slots = null;
        foreach (var slot in TextureSlots)
        {
            if (slot.SelectedPrimary is { Index: >= 0 } chosenPrimary)
            {
                slots ??= new(StringComparer.Ordinal);
                slots[slot.SlotKey] = captureTextureOrdinal(chosenPrimary.Index);
            }
            else if (strictValidation && slot.Descriptor.Required)
            {
                throw Error("Mda.Text.ModelProps.MissingSlotValueError", materialLabel, DisplayName, slot.PrimaryDisplayName);
            }

            if (slot.SupportsSecondaryTexture && slot.SelectedSecondary is { Index: >= 0 } chosenSecondary)
            {
                slots ??= new(StringComparer.Ordinal);
                slots[slot.SecondarySlotKey] = captureTextureOrdinal(chosenSecondary.Index);
            }
        }

        Dictionary<string, double>? numbers = null;
        Dictionary<string, bool>? bools = null;
        Dictionary<string, float[]>? vectors = null;
        Dictionary<string, string>? strings = null;

        foreach (var param in Parameters)
        {
            param.WriteTo(ref slots, ref numbers, ref bools, ref vectors, ref strings, ordinal => captureTextureOrdinal(ordinal));
        }

        StyleBoneAxisBinding? boneBinding = null;
        if (SupportsBoneTracking)
        {
            if (BoneTrackingModeIndex == 1)
            {
                if (SelectedBone is not { EffectiveName: { Length: > 0 } chosenBone })
                {
                    if (strictValidation)
                        throw Error("Mda.Text.ModelProps.SelectedBoneRequiredError");
                    boneBinding = new StyleBoneAxisBinding(
                        StyleBoneTrackingMode.SelectedBone,
                        BoneName: "",
                        LocalForward: localForward,
                        LocalUp: localUp);
                }
                else
                {
                    boneBinding = new StyleBoneAxisBinding(
                        StyleBoneTrackingMode.SelectedBone,
                        BoneName: chosenBone,
                        LocalForward: localForward,
                        LocalUp: localUp);
                }
            }
            else
            {
                boneBinding = new StyleBoneAxisBinding(
                    StyleBoneTrackingMode.FixedWorld,
                    FixedWorldForward: fixedForward,
                    FixedWorldRight: fixedRight);
            }
        }

        return new StyleMaterialBindingData(
            StyleId,
            ModeId,
            slots,
            numbers,
            bools,
            vectors,
            strings,
            boneBinding);
    }

    internal void NotifyRoleChanged()
    {
        OnPropertyChanged(nameof(IsRoleAllowed));
        OnPropertyChanged(nameof(ShowRoleRequirementNote));
        OnPropertyChanged(nameof(RoleRequirementNote));
    }

    internal void RefreshLocalization()
    {
        foreach (var slot in TextureSlots) slot.RefreshLocalization();
        foreach (var param in Parameters) param.RefreshLocalization();
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(BoneTrackingModes));
        OnPropertyChanged(nameof(FixedBasisSummary));
        OnPropertyChanged(nameof(LocalAxisSummary));
        OnPropertyChanged(nameof(RoleRequirementNote));
    }

    private void HandleChildChanged()
    {
        if (!suppressNotifications)
            onChanged();
    }

    private void InitializeBoneTracking(StyleMaterialBindingData? previous)
    {
        var defaults = Mode.BoneAxisDefaults;
        fixedForward = previous?.BoneBinding?.FixedWorldForward ?? defaults?.WorldForward ?? Vector3.UnitZ;
        fixedRight = previous?.BoneBinding?.FixedWorldRight ?? defaults?.WorldRight ?? Vector3.UnitX;
        localForward = previous?.BoneBinding?.LocalForward ?? defaults?.LocalForward ?? Vector3.UnitZ;
        localUp = previous?.BoneBinding?.LocalUp ?? defaults?.LocalUp ?? Vector3.UnitY;

        bool useSelectedBone = previous?.BoneBinding is { } saved
            ? saved.Mode == StyleBoneTrackingMode.SelectedBone
            : (defaults?.DefaultUseSelectedBone == true && Bones.Count > 0);
        boneTrackingModeIndex = useSelectedBone ? 1 : 0;

        string? boneName = previous?.BoneBinding?.BoneName;
        if (!string.IsNullOrEmpty(boneName))
            selectedBone = Bones.FirstOrDefault(b => string.Equals(b.EffectiveName, boneName, StringComparison.Ordinal));
        else
            selectedBone = Bones.FirstOrDefault(b => string.Equals(b.EffectiveName, "頭", StringComparison.Ordinal)
                || string.Equals(b.EffectiveName, "Head", StringComparison.OrdinalIgnoreCase)) ?? Bones.FirstOrDefault();

        OnPropertyChanged(nameof(BoneTrackingModeIndex));
        OnPropertyChanged(nameof(IsFixedWorldTracking));
        OnPropertyChanged(nameof(IsSelectedBoneTracking));
        OnPropertyChanged(nameof(SelectedBone));
        OnPropertyChanged(nameof(FixedBasisSummary));
        OnPropertyChanged(nameof(LocalAxisSummary));
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));

    private static string ResolveText(LocalizedText? text, string fallback)
    {
        string? resolved = text?.Resolve(CurrentLanguage);
        return string.IsNullOrWhiteSpace(resolved) ? fallback : resolved;
    }

    private static string FormatRoleName(StyleMaterialRole role) => role switch
    {
        StyleMaterialRole.Body => Text("Mda.Text.6f93b2ca1bb0"),
        StyleMaterialRole.Face => Text("Mda.Text.68fa14a6d0cf"),
        StyleMaterialRole.Eye => Text("Mda.Text.4f550cf482e7"),
        StyleMaterialRole.Hair => Text("Mda.Text.982acfe3cb07"),
        _ => role.ToString()
    };
}
