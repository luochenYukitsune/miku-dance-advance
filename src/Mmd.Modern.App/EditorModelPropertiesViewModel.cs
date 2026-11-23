using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Plugins.Contracts;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>One detached host edit. The workspace commits this together with its mapping Undo state.</summary>
public sealed record EditorModelPropertiesDraft(Guid ObjectId, string Name, ModelRole Role,
    Vector3 Position, Quaternion Rotation, float Scale, bool IkEnabled, bool MorphsEnabled,
    bool PhysicsEnabled, RenderingPresetReference? StyleOverride, D3D12MaterialBindingProfile? MaterialProfile,
    bool? EnvironmentIndoor = null);

public sealed class EditorModelStyleChoice : INotifyPropertyChanged
{
    private readonly Func<string> labelFactory;

    public EditorModelStyleChoice(string sourceLabel, RenderingPresetReference? reference,
        string? suffix = null, bool translateSuffix = false, Func<string>? dynamicLabel = null)
    {
        SourceLabel = sourceLabel;
        Reference = reference;
        Suffix = suffix;
        TranslateSuffix = translateSuffix;
        labelFactory = dynamicLabel ?? (() => Suffix is null ? Text(SourceLabel)
            : Format(SourceLabel, TranslateSuffix ? Text(Suffix) : Suffix));
    }

    public string SourceLabel { get; }
    public RenderingPresetReference? Reference { get; }
    public string? Suffix { get; }
    public bool TranslateSuffix { get; }
    public string Label => labelFactory();
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public override string ToString() => Label;
}

public sealed record EditorSourceChoice(int Index, string SourceLabel, string? RawName = null) : INotifyPropertyChanged
{
    public string EffectiveName => RawName ?? SourceLabel;
    // Indexed texture/bone labels contain authored names; only the application-owned sentinel is translated.
    public string Label => Index < 0 ? Text("未选择") : SourceLabel;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public override string ToString() => Label;
}

/// <summary>Presentation-only copy of authored model properties. Opening/cancelling never edits a session.</summary>
public sealed class EditorModelPropertiesViewModel : INotifyPropertyChanged
{
    private readonly EditorProjectWorkspace workspace;
    private readonly EditableSceneObject source;
    private readonly SceneActorMotion actor;
    private readonly RenderingPresetReference defaultStyle;
    private readonly D3D12MaterialBindingLayout layout;
    private readonly IReadOnlyList<(int Width, int Height)> textureDimensions;
    private D3D12MaterialBindingProfile? profile;
    private readonly Vector3 initialDegrees;
    private string name, validationMessage = "";
    private Exception? validationError;
    private bool refreshingLocalization;
    private int roleIndex, styleIndex;
    private decimal? positionX, positionY, positionZ, rotationX, rotationY, rotationZ, scale;
    private bool ikEnabled, morphsEnabled, physicsEnabled;
    private bool? environmentIndoor;
    private EditorMaterialPropertiesItem? selectedMaterial;
    private readonly ObservableCollection<EditorMaterialPropertiesItem> filteredMaterials = [];
    private string materialSearchText = "";
    private bool filteringMaterials;
    private readonly bool supportsOutlineScaling;

    public EditorModelPropertiesViewModel(EditorProjectWorkspace workspace, Guid objectId)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        supportsOutlineScaling = EditorRenderEngineOptions.UsesModernQualityPreferences(workspace.RenderEngine);
        var session = workspace.Session ?? throw new InvalidOperationException(Text("请先载入模型。"));
        source = session.Objects.SingleOrDefault(item => item.Id == objectId)
            ?? throw new ArgumentException(Text("选中的模型已不存在。"), nameof(objectId));
        actor = workspace.Motions.Single(item => item.ObjectId == objectId);
        defaultStyle = session.RenderingStyles.DefaultStyle;
        profile = workspace.MaterialBindings.GetValueOrDefault(objectId);
        layout = D3D12MaterialBindingLayout.Capture(source.Source);
        textureDimensions = source.Source.Surface.Snapshot.Textures.Select(static t => (t.Width, t.Height)).ToArray();
        name = source.Name; roleIndex = source.Role switch { ModelRole.Character => 0, ModelRole.Environment => 1, ModelRole.Prop => 2, _ => 3 };
        environmentIndoor = session.GetModelPurpose(objectId).EnvironmentIndoor;
        positionX = Decimal(source.Position.X); positionY = Decimal(source.Position.Y); positionZ = Decimal(source.Position.Z);
        initialDegrees = ToDegrees(source.Rotation);
        rotationX = Decimal(initialDegrees.X); rotationY = Decimal(initialDegrees.Y); rotationZ = Decimal(initialDegrees.Z);
        scale = Decimal(source.Scale);
        ikEnabled = actor.IkEnabled; morphsEnabled = actor.MorphsEnabled; physicsEnabled = actor.PhysicsEnabled;

        var activeCatalog = workspace.StyleCatalog;
        List<EditorModelStyleChoice> styles =
        [
            new("继承工程默认 · {0}", null, dynamicLabel: () => Format("继承工程默认 · {0}", ResolveStyleDisplayName(defaultStyle.StableId, activeCatalog))),
            new("MMD 原版", RenderingPresetReference.BuiltIn(RenderingStyleId.Mmd))
        ];
        foreach (var def in EditorStyleCatalogOrdering.OrderStyles(activeCatalog.Styles))
        {
            var styleRef = new RenderingPresetReference(def.StyleId);
            styles.Add(new(def.StyleId, styleRef, dynamicLabel: () => ResolveStyleDisplayName(def.StyleId, activeCatalog)));
        }

        if (session.RenderingStyles.ModelOverrides.TryGetValue(objectId, out var selected))
        {
            styleIndex = styles.FindIndex(choice => choice.Reference == selected);
            if (styleIndex < 0)
            {
                styleIndex = styles.Count;
                styles.Add(new("保留当前引用 · {0}", selected, selected.StableId));
            }
        }
        StyleChoices = styles.AsReadOnly();
        Textures = new[] { new EditorSourceChoice(-1, "未选择") }.Concat(source.Source.Surface.Snapshot.Textures.Select((texture, index) =>
            new EditorSourceChoice(index, $"{index} · {texture.Name} ({texture.Width}×{texture.Height})", texture.Name))).ToArray();
        Bones = actor.Model.Bones.Select((bone, index) => new EditorSourceChoice(index, $"{index} · {bone.Name}", bone.Name)).ToArray();
        RebuildMaterials();
        Observe(this, static model => model.RefreshLocalization());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid ObjectId => source.Id;
    public string SourcePath => source.SourcePath;
    public bool HasSourcePath => !string.IsNullOrWhiteSpace(SourcePath);
    public string[] RoleChoices => [Text("角色"), Text("场景"), Text("道具"), Text("Mda.Text.7409a608060f")];
    public bool SupportsModernStyles => supportsOutlineScaling;
    public IReadOnlyList<EditorModelStyleChoice> StyleChoices { get; }
    public IReadOnlyList<EditorSourceChoice> Textures { get; }
    public IReadOnlyList<EditorSourceChoice> Bones { get; }
    public ObservableCollection<EditorMaterialPropertiesItem> Materials { get; } = [];
    public ObservableCollection<EditorMaterialPropertiesItem> FilteredMaterials => IsMaterialSearchActive ? filteredMaterials : Materials;
    public bool IsMaterialSearchActive => !string.IsNullOrWhiteSpace(materialSearchText);
    public bool HasFilteredMaterials => FilteredMaterials.Count > 0;
    public bool ShowNoMatchingMaterials => HasMaterials && FilteredMaterials.Count == 0;
    public string MaterialFilterSummary => IsMaterialSearchActive
        ? Format("Mda.Text.Ux.ModelProps.FilteredMaterialsSummary", FilteredMaterials.Count, Materials.Count)
        : "";
    public string MaterialSearchText
    {
        get => materialSearchText;
        set
        {
            string next = value ?? "";
            if (Set(ref materialSearchText, next))
                ApplyMaterialFilter();
        }
    }
    public bool HasMaterials => Materials.Count != 0;
    public bool HasSelectedMaterial => SelectedMaterial is not null;
    public string ProfileSummary => profile is null ? Text("未设置显式映射；沿用原始材质。")
        : profile.OutlineWidths.Count == 0 ? Format("Mda.Text.1269413e2df4", profile.Name, profile.SchemaVersion, profile.Materials.Count)
        : Format("Mda.Text.OutlineScale.ProfileSummary", profile.Name, profile.Materials.Count, profile.OutlineWidths.Count);
    public string Fingerprint => layout.Fingerprint;
    public string Name { get => name; set => Set(ref name, value ?? ""); }
    public int RoleIndex { get => roleIndex; set { if (Set(ref roleIndex, value)) Changed(nameof(IsEnvironment)); } }
    public bool IsEnvironment => RoleIndex == 1;
    public bool IsIndoorEnvironment
    {
        get => environmentIndoor == true;
        set
        {
            // Reading an old project's unchecked control must retain its nullable compatibility choice.
            if (IsIndoorEnvironment == value || refreshingLocalization) return;
            environmentIndoor = value; Changed();
        }
    }
    public int StyleIndex
    {
        get => styleIndex;
        set
        {
            if (!Set(ref styleIndex, value)) return;
            string nextStyleId = SelectedEffectiveStyleId();
            foreach (var material in Materials)
                material.UpdateEffectiveStyle(nextStyleId);
        }
    }
    public decimal? PositionX { get => positionX; set => Set(ref positionX, value); }
    public decimal? PositionY { get => positionY; set => Set(ref positionY, value); }
    public decimal? PositionZ { get => positionZ; set => Set(ref positionZ, value); }
    public decimal? RotationX { get => rotationX; set => Set(ref rotationX, value); }
    public decimal? RotationY { get => rotationY; set => Set(ref rotationY, value); }
    public decimal? RotationZ { get => rotationZ; set => Set(ref rotationZ, value); }
    public decimal? Scale { get => scale; set => Set(ref scale, value); }
    public bool IkEnabled { get => ikEnabled; set => Set(ref ikEnabled, value); }
    public bool MorphsEnabled { get => morphsEnabled; set => Set(ref morphsEnabled, value); }
    public bool PhysicsEnabled { get => physicsEnabled; set => Set(ref physicsEnabled, value); }
    public string ValidationMessage
    { get => validationError?.Message ?? Text(validationMessage); private set { validationError = null; validationMessage = value; Changed(); Changed(nameof(HasValidationMessage)); } }
    public bool HasValidationMessage => ValidationMessage.Length > 0;
    public EditorMaterialPropertiesItem? SelectedMaterial
    {
        get => selectedMaterial;
        set
        {
            if (filteringMaterials || (value is not null && !FilteredMaterials.Contains(value))) return;
            if (Set(ref selectedMaterial, value)) Changed(nameof(HasSelectedMaterial));
        }
    }
    public void SetValidationMessage(string message) => ValidationMessage = message;

    public bool TryCreate(out EditorModelPropertiesDraft? draft)
    {
        draft = null;
        try
        {
            string validatedName = Name == source.Name ? source.Name : Name.Trim();
            if (string.IsNullOrWhiteSpace(validatedName) || validatedName != source.Name && validatedName.Any(char.IsControl))
                throw Error("请填写模型名称，且不要包含控制字符。");
            if (RoleIndex is < 0 or > 3) throw Error("请选择模型用途。");
            if ((uint)StyleIndex >= (uint)StyleChoices.Count) throw Error("请选择此模型的画风。");
            var position = new Vector3(OriginalOrNumber(PositionX, source.Position.X, "位置 X"), OriginalOrNumber(PositionY, source.Position.Y, "位置 Y"), OriginalOrNumber(PositionZ, source.Position.Z, "位置 Z"));
            float chosenScale = OriginalOrNumber(Scale, source.Scale, "缩放");
            if (chosenScale <= 0) throw Error("缩放必须大于 0。");
            var rotation = RotationX == Decimal(initialDegrees.X) && RotationY == Decimal(initialDegrees.Y) && RotationZ == Decimal(initialDegrees.Z)
                ? source.Rotation : Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(Radians(RotationY, "旋转 Y"), Radians(RotationX, "旋转 X"), Radians(RotationZ, "旋转 Z")));
            var mapping = BuildProfile();
            draft = new(source.Id, validatedName, RoleIndex switch { 0 => ModelRole.Character, 1 => ModelRole.Environment, 2 => ModelRole.Prop, _ => ModelRole.Unspecified },
                position, rotation, chosenScale, IkEnabled, MorphsEnabled, PhysicsEnabled, StyleChoices[StyleIndex].Reference, mapping,
                IsEnvironment ? environmentIndoor : null);
            ValidationMessage = ""; return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException or OverflowException)
        { validationError = error; Changed(nameof(ValidationMessage)); Changed(nameof(HasValidationMessage)); return false; }
    }

    /// <summary>Full renderer schema codec, including all advanced fields. Input is bounded and layout/image guards are checked.</summary>
    public void ImportProfile(ReadOnlyMemory<byte> bytes)
    {
        var imported = D3D12MaterialBindingProfileJson.Decode(bytes);
        _ = D3D12MaterialBindingProfileResolver.Resolve(imported, source.Source, source.Id, EffectiveValidationPipelineId(), workspace.StyleCatalog);
        profile = imported; RebuildMaterials(); Changed(nameof(ProfileSummary)); ValidationMessage = Text("Mda.Text.ModelProps.ProfileImportedDraft");
    }

    public byte[] ExportProfile() => D3D12MaterialBindingProfileJson.Encode(BuildProfile()
        ?? new D3D12MaterialBindingProfile("custom-" + Guid.NewGuid().ToString("N"), SafeProfileName(), layout.Fingerprint, [], layout.Version,
            guessMaterialRoles: profile?.GuessMaterialRoles ?? workspace.GuessMaterialRoles));

    public void CaptureBoneBasis(SceneProjectSession session, EditorStyleModeItem? targetMode = null)
    {
        var material = SelectedMaterial ?? throw new InvalidOperationException(Text("请先选择材质。"));
        var chosenBone = targetMode?.SelectedBone ?? material.SelectedBone;
        if (chosenBone is not { Index: >= 0 } bone || bone.Index >= actor.Model.Bones.Count)
            throw Error("Mda.Text.ModelProps.SelectBoneError");
        // The exact authored actor and source identity must still be present; never reuse an index across models.
        var currentActor = session.CaptureAuthoredContent().Motions.SingleOrDefault(item => item.ObjectId == source.Id);
        if (currentActor is null || !ReferenceEquals(currentActor.Model, actor.Model)
            || !ReferenceEquals(session.Objects.SingleOrDefault(item => item.Id == source.Id)?.Source, source.Source))
            throw new InvalidOperationException(Text("模型已改变，请重新打开属性窗口。"));
        var snapshot = session.CaptureRenderSnapshot([new SceneObjectRenderRequest(source.Id, [bone.Index])]);
        if (!snapshot.SceneInputs.TryGetValue(source.Id, out var inputs) || !inputs.Visible
            || !inputs.BoneWorldMatrices.TryGetValue(bone.Index, out var world))
            throw new InvalidOperationException(Text("此帧无法读取所选骨骼，请先显示模型再采样。"));
        var forward = Vector3.TransformNormal(Vector3.UnitZ, world);
        var right = Vector3.TransformNormal(Vector3.UnitX, world);
        if (targetMode is not null)
            targetMode.SetFixedBasis(forward, right);
        material.SetFixedBasis(forward, right);
        ValidationMessage = Text("Mda.Text.ModelProps.CapturedFixedBasisStatus");
    }

    private D3D12MaterialBindingProfile? BuildProfile()
    {
        var rows = Materials.Select(item => item.Build()).Where(row => row is not null).Cast<D3D12MaterialBindingProfileRow>().ToArray();
        var outlines = Materials.Select(item => item.BuildOutline()).OfType<D3D12MaterialBindingProfileOutline>().ToArray();
        bool effectiveGuess = profile?.GuessMaterialRoles ?? workspace.GuessMaterialRoles;
        if (rows.Length == 0 && outlines.Length == 0 && effectiveGuess && profile?.PipelineId is null
            && (profile is null || profile.Materials.Count > 0 || profile.OutlineWidths.Count > 0)) return null;
        if (profile is not null && profile.Materials.OrderBy(row => row.LocalMaterialIndex).SequenceEqual(rows)
            && profile.OutlineWidths.OrderBy(row => row.LocalMaterialIndex).SequenceEqual(outlines)
            && profile.GuessMaterialRoles == effectiveGuess) return profile;
        var result = new D3D12MaterialBindingProfile("custom-" + Guid.NewGuid().ToString("N"), SafeProfileName(), layout.Fingerprint,
            rows, layout.Version, outlineWidths: outlines, pipelineId: profile?.PipelineId, guessMaterialRoles: effectiveGuess);
        _ = D3D12MaterialBindingProfileResolver.Resolve(result, source.Source, source.Id, EffectiveValidationPipelineId(), workspace.StyleCatalog);
        return result;
    }

    private string EffectiveValidationPipelineId()
    {
        string styleId = SelectedEffectiveStyleId();
        var resolved = workspace.PresetCatalog.Find(styleId);
        return resolved.IsAvailable && resolved.Definition is not null
            ? resolved.Definition.PipelineId
            : D3D12SurfaceStyleMap.DefaultStyleId;
    }

    private string SelectedEffectiveStyleId()
        => (uint)StyleIndex < (uint)StyleChoices.Count
            ? (StyleChoices[StyleIndex].Reference ?? defaultStyle).StableId
            : defaultStyle.StableId;

    private static string ResolveStyleDisplayName(string styleId, D3D12ViewStyleCatalog catalog)
    {
        if (string.Equals(styleId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal))
            return Text("Mda.Text.Context.Inspector.Style.Option.Mmd");
        if (catalog.FindStyle(styleId) is { } def)
        {
            string? resolved = def.DisplayName?.Resolve(EditorLocalization.CurrentLanguage);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }
        return styleId;
    }

    private string SafeProfileName() { string value = Name.Trim(); if (value.Length == 0) value = Text("模型材质映射"); return value[..Math.Min(value.Length, 128)]; }
    private void RebuildMaterials()
    {
        int oldIndex = SelectedMaterial?.LocalMaterialIndex ?? 0;
        Materials.Clear();
        var existing = profile?.Materials.ToDictionary(row => row.LocalMaterialIndex) ?? [];
        var outlines = profile?.OutlineWidths.ToDictionary(row => row.LocalMaterialIndex) ?? [];
        for (int index = 0; index < source.Source.Materials.Count; index++)
            Materials.Add(CreateMaterial(index, existing.GetValueOrDefault(index), outlines.GetValueOrDefault(index)?.Scale ?? 1f));
        selectedMaterial = Materials.FirstOrDefault(item => item.LocalMaterialIndex == oldIndex) ?? Materials.FirstOrDefault();
        ApplyMaterialFilter();
        Changed(nameof(SelectedMaterial));
        Changed(nameof(HasSelectedMaterial));
        Changed(nameof(HasMaterials));
    }

    private EditorMaterialPropertiesItem CreateMaterial(int index, D3D12MaterialBindingProfileRow? row, float outlineScale)
    {
        var pmxMaterial = index < actor.Model.Materials.Count ? actor.Model.Materials[index] : null;
        string materialName = pmxMaterial?.Name ?? source.Source.Surface.Snapshot.Materials[index].Name;
        return new(index, materialName, row, layout, Textures, Bones, outlineScale, supportsOutlineScaling,
            workspace.StyleCatalog, SelectedEffectiveStyleId(), source.Id, textureDimensions, pmxMaterial?.EnglishName ?? "");
    }

    public void ClearSelectedMaterialMapping()
    {
        if (SelectedMaterial is not { HasCustomization: true } selected || !FilteredMaterials.Contains(selected)) return;
        int index = selected.LocalMaterialIndex;
        var replacement = CreateMaterial(index, null, 1f);
        Materials[index] = replacement;
        selectedMaterial = replacement;
        ApplyMaterialFilter();
    }

    private void ApplyMaterialFilter()
    {
        var previousSelected = selectedMaterial;
        filteringMaterials = true;
        try
        {
            string query = materialSearchText.Trim();
            if (query.Length == 0)
            {
                filteredMaterials.Clear();
            }
            else
            {
                var matches = Materials.Where(item => item.MatchesSearch(query)).ToArray();
                filteredMaterials.Clear();
                foreach (var item in matches)
                    filteredMaterials.Add(item);
            }
            Changed(nameof(IsMaterialSearchActive));
            Changed(nameof(FilteredMaterials));
            Changed(nameof(HasFilteredMaterials));
            Changed(nameof(ShowNoMatchingMaterials));
            Changed(nameof(MaterialFilterSummary));
        }
        finally
        {
            filteringMaterials = false;
        }
        selectedMaterial = previousSelected is not null && FilteredMaterials.Contains(previousSelected)
            ? previousSelected : FilteredMaterials.FirstOrDefault();
        Changed(nameof(SelectedMaterial));
        Changed(nameof(HasSelectedMaterial));
    }

    internal static float Number(decimal? value, string name)
    { if (value is null) throw Error("请填写{0}。", EditorLocalization.Label(name)); float result = (float)value.Value; if (!float.IsFinite(result)) throw Error("{0}超出支持范围。", EditorLocalization.Label(name)); return result; }
    private static float OriginalOrNumber(decimal? value, float original, string name) => value == Decimal(original) ? original : Number(value, name);
    private static decimal? Decimal(float value) { try { return (decimal)value; } catch (OverflowException) { return null; } }
    private static float Radians(decimal? value, string name) => Number(value, name) * (MathF.PI / 180);
    private static Vector3 ToDegrees(Quaternion q)
    {
        double pitch = Math.Asin(Math.Clamp(2d * (q.W * q.X - q.Y * q.Z), -1, 1));
        double yaw = Math.Abs(Math.Cos(pitch)) < 1e-6 ? 2 * Math.Atan2(q.Y, q.W) : Math.Atan2(2d * (q.W * q.Y + q.X * q.Z), 1 - 2d * (q.X * q.X + q.Y * q.Y));
        double roll = Math.Abs(Math.Cos(pitch)) < 1e-6 ? 0 : Math.Atan2(2d * (q.W * q.Z + q.X * q.Y), 1 - 2d * (q.X * q.X + q.Z * q.Z));
        return new((float)(pitch * 180 / Math.PI), (float)(yaw * 180 / Math.PI), (float)(roll * 180 / Math.PI));
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (refreshingLocalization || EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(property); return true; }
    private void RefreshLocalization()
    {
        refreshingLocalization = true;
        try
        {
            foreach (var choice in StyleChoices) choice.RefreshLocalization();
            foreach (var choice in Textures) choice.RefreshLocalization();
            Changed(null);
        }
        finally { refreshingLocalization = false; }
        Changed(nameof(RoleIndex)); Changed(nameof(StyleIndex));
    }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

/// <summary>Each material owns its own draft, so switching rows never discards unfinished values.</summary>
public sealed class EditorMaterialPropertiesItem : INotifyPropertyChanged
{
    private readonly D3D12MaterialBindingProfileRow? original;
    private readonly D3D12MaterialBindingLayout layout;
    private readonly D3D12ViewStyleCatalog styleCatalog;
    private readonly Guid objectId;
    private readonly string rawMaterialName;
    private readonly IReadOnlyList<(int Width, int Height)> textureDimensions;
    private readonly List<PipelineEntry> pipelineEntries = [];
    private readonly List<DynamicModeEntry> legacyDynamicModes = [];
    // Retain edited cards across model-style switches, including incomplete input until the user applies the draft.
    private readonly Dictionary<(string StyleId, string ModeId), EditorStyleModeItem> editedModes = [];
    private readonly ObservableCollection<EditorStyleModeItem> dynamicModes = [];
    private readonly ObservableCollection<EditorStyleModeItem> visibleDynamicModes = [];
    private readonly ObservableCollection<EditorStyleModeItem> incompatibleDynamicModes = [];
    private string effectiveStyleId;
    private int roleIndex, modeIndex, pipelineIndex;
    private EditorSourceChoice? primaryTexture, secondaryTexture;
    private Vector3 fixedForward, fixedRight;
    private bool fixedBasisChanged, legacyModeEdited, showIncompatibleModes, roleEdited;
    private bool refreshingLocalization, rebuildingDynamicModes;
    private decimal? faceNormal, faceShadow, eyeShadow;
    private decimal? outlinePercent;
    private static readonly D3D12PmxMaterialTreatment NeutralTreatment = new();
    // An absent field follows the active style. A present null is an unfinished numeric entry.
    private readonly Dictionary<string, decimal?> pmxOverrides = new(StringComparer.Ordinal);
    private bool pmxEdited;
    public D3D12PmxMaterialTreatment PmxDefaults { get; private set; } = NeutralTreatment;
    public bool CanEditOrdinaryPmxTreatment { get; private set; }
    private string styleValidationMessage = "";

    private sealed record PipelineEntry(string StyleId, IRenderingStyleDefinition? Definition);
    private sealed record DynamicModeEntry(string StyleId, IRenderingStyleDefinition Definition, StyleMaterialModeDescriptor Mode);

    public EditorMaterialPropertiesItem(
        int index,
        string name,
        D3D12MaterialBindingProfileRow? row,
        D3D12MaterialBindingLayout layout,
        IReadOnlyList<EditorSourceChoice> textures,
        IReadOnlyList<EditorSourceChoice> bones,
        float outlineScale = 1f,
        bool supportsOutlineScaling = true,
        D3D12ViewStyleCatalog? styleCatalog = null,
        string? effectiveStyleId = null,
        Guid objectId = default,
        IReadOnlyList<(int Width, int Height)>? textureDimensions = null,
        string englishName = "")
    {
        LocalMaterialIndex = index;
        rawMaterialName = name;
        EnglishName = englishName ?? "";
        Label = $"{index} · {name}";
        original = row;
        this.layout = layout;
        this.styleCatalog = styleCatalog ?? D3D12ViewStyleCatalog.Empty;
        this.objectId = objectId;
        this.textureDimensions = textureDimensions ?? [];
        this.effectiveStyleId = effectiveStyleId ?? D3D12SurfaceStyleMap.DefaultStyleId;
        SupportsOutlineScaling = supportsOutlineScaling;
        outlinePercent = (decimal)outlineScale * 100m;
        Textures = textures;
        Bones = bones;
        roleIndex = row is null ? 0 : (int)row.Role + 1;
        faceNormal = (decimal)(row?.FaceNormalShadeStrength ?? .35f);
        faceShadow = (decimal)(row?.FaceGeometricShadowStrength ?? 1f);
        eyeShadow = (decimal)(row?.EyeGeometricShadowStrength ?? .2f);

        // The package schema stores whole explicit groups, including intentionally neutral values.
        if (row?.PmxMaterialTreatment is { } savedTreatment)
        {
            pmxOverrides[nameof(PmxSaturation)] = (decimal)savedTreatment.Saturation;
            pmxOverrides[nameof(PmxTransitionWidthScale)] = (decimal)savedTreatment.TransitionWidthScale;
            pmxOverrides[nameof(PmxValueContrast)] = (decimal)savedTreatment.ValueContrast;
            pmxOverrides[nameof(PmxShadeGamma)] = (decimal)savedTreatment.ShadeGamma;
            pmxOverrides[nameof(PmxHairHighlightStrength)] = (decimal)savedTreatment.HairHighlightStrength;
            pmxOverrides[nameof(PmxHairHighlightWidth)] = (decimal)savedTreatment.HairHighlightWidth;
        }

        foreach (var def in EditorStyleCatalogOrdering.OrderStyles(this.styleCatalog.Styles))
            pipelineEntries.Add(new(def.StyleId, def));

        var primaryBinding = FindPrimaryEditableBinding(row);
        if (primaryBinding is not null && !pipelineEntries.Any(entry => string.Equals(entry.StyleId, primaryBinding.StyleId, StringComparison.Ordinal)))
            pipelineEntries.Add(new(primaryBinding.StyleId, null));

        string? preferredPipeline = primaryBinding?.StyleId ?? effectiveStyleId;
        pipelineIndex = !string.IsNullOrWhiteSpace(preferredPipeline)
            ? pipelineEntries.FindIndex(entry => string.Equals(entry.StyleId, preferredPipeline, StringComparison.Ordinal))
            : -1;
        if (pipelineIndex < 0 && pipelineEntries.Count > 0)
            pipelineIndex = 0;

        var faceBinding = row?.PluginBindings.FirstOrDefault(static binding => string.Equals(binding.ModeId, "face-sdf", StringComparison.Ordinal));
        fixedForward = faceBinding?.BoneBinding?.FixedWorldForward ?? Vector3.UnitZ;
        fixedRight = faceBinding?.BoneBinding?.FixedWorldRight ?? Vector3.UnitX;
        if (faceBinding?.BoneBinding is { Mode: StyleBoneTrackingMode.SelectedBone, BoneName: { } boneName } && !string.IsNullOrEmpty(boneName))
            SelectedBone = bones.FirstOrDefault(bone => string.Equals(bone.EffectiveName, boneName, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(this.effectiveStyleId) && this.styleCatalog.FindStyle(this.effectiveStyleId) is { } activeStyle)
        {
            foreach (var mode in activeStyle.MaterialModes)
            {
                if (IsCoveredByBuiltInModeRows(activeStyle.StyleId, mode.ModeId))
                    continue;
                legacyDynamicModes.Add(new(activeStyle.StyleId, activeStyle, mode));
            }
        }

        int first = ExtractTextureOrdinal(primaryBinding, this.styleCatalog, slotIndex: 0);
        int second = ExtractTextureOrdinal(primaryBinding, this.styleCatalog, slotIndex: 1);
        primaryTexture = textures.FirstOrDefault(item => item.Index == first);
        secondaryTexture = textures.FirstOrDefault(item => item.Index == second);

        RebuildDynamicModes();
        RefreshPmxDefaults();
        ValidateLive();
        Observe(this, static model => model.RefreshLocalization());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public int LocalMaterialIndex { get; }
    public string Name => rawMaterialName;
    public string EnglishName { get; }
    public string Label { get; }
    public string ToolTipText => string.IsNullOrWhiteSpace(EnglishName)
        || string.Equals(EnglishName, rawMaterialName, StringComparison.Ordinal)
        ? Label
        : $"{Label} ({EnglishName})";
    public string[] RoleChoices => [Text("沿用原材质（不映射）"), Text("Mda.Text.6f93b2ca1bb0"), Text("Mda.Text.68fa14a6d0cf"), Text("Mda.Text.4f550cf482e7"), Text("Mda.Text.982acfe3cb07")];
    public string[] ModeChoices => BuildModeChoices();
    public string[] PipelineChoices => pipelineEntries.Select(ResolvePipelineChoiceLabel).ToArray();
    public IReadOnlyList<EditorSourceChoice> Textures { get; }
    public IReadOnlyList<EditorSourceChoice> Bones { get; }
    public ObservableCollection<EditorStyleModeItem> DynamicModes => dynamicModes;
    public ObservableCollection<EditorStyleModeItem> VisibleDynamicModes => visibleDynamicModes;
    public ObservableCollection<EditorStyleModeItem> IncompatibleDynamicModes => incompatibleDynamicModes;
    public bool HasDynamicModes => dynamicModes.Count > 0;
    public bool HasNoDynamicModes => !HasDynamicModes;
    public bool HasVisibleDynamicModes => visibleDynamicModes.Count > 0;
    public bool HasIncompatibleDynamicModes => incompatibleDynamicModes.Count > 0;
    public string IncompatibleDynamicModesHeader => Format("Mda.Text.Ux.ModelProps.IncompatibleModesHeader", incompatibleDynamicModes.Count);
    public bool ShowIncompatibleModes
    {
        get => showIncompatibleModes;
        set
        {
            if (showIncompatibleModes == value) return;
            showIncompatibleModes = value;
            Changed();
        }
    }
    public bool ShowMapRoleHint => !IsMapped && HasDynamicModes;
    public string EffectiveStyleDisplayName => styleCatalog.FindStyle(effectiveStyleId) is { } def
        ? ResolveLocalized(def.DisplayName, def.StyleId)
        : (string.Equals(effectiveStyleId, D3D12SurfaceStyleMap.DefaultStyleId, StringComparison.Ordinal)
            ? Text("Mda.Text.Context.Inspector.Style.Option.Mmd")
            : effectiveStyleId);
    public string DynamicModesHeaderHint => Format("Mda.Text.ModelProps.DynamicModesHint", EffectiveStyleDisplayName);
    public string NoDynamicModesNotice => styleCatalog.FindStyle(effectiveStyleId) is not null
        ? Text("Mda.Text.ModelProps.NoDynamicModes")
        : Format("Mda.Text.ModelProps.ModelStyleNoPluginNotice", EffectiveStyleDisplayName);
    public string StyleValidationMessage => styleValidationMessage;
    public bool HasStyleValidationMessage => !string.IsNullOrWhiteSpace(styleValidationMessage);
    public string ValidationMessage => StyleValidationMessage;
    public bool HasValidationMessage => HasStyleValidationMessage;
    public bool HasCustomization =>
        IsMapped
        || OutlinePercent != 100m
        || dynamicModes.Any(static m => m.IsEnabled)
        || HasDormantPluginBindings
        || (legacyModeEdited && ModeIndex > 1)
        || fixedBasisChanged
        || faceNormal != (decimal).35f
        || faceShadow != 1m
        || eyeShadow != (decimal).2f
        || pmxOverrides.Count != 0;
    private int DormantBindingCount => (original?.PluginBindings.Count(binding => binding.StyleId != effectiveStyleId
            && !TryGetEditedMode(binding, out _)) ?? 0)
        + editedModes.Values.Count(mode => mode.IsEnabled && mode.StyleId != effectiveStyleId);
    private bool HasDormantPluginBindings => DormantBindingCount > 0;

    public EditorSourceChoice? SelectedBone { get; set; }
    public int RoleIndex
    {
        get => roleIndex;
        set
        {
            if (refreshingLocalization || roleIndex == value) return;
            roleIndex = value;
            roleEdited = true;
            RefreshPmxDefaults();
            Changed();
            Changed(nameof(IsFaceRole));
            Changed(nameof(IsEyeRole));
            Changed(nameof(ExistingMapping));
            Changed(nameof(IsMapped));
            Changed(nameof(ShowMapRoleHint));
            Changed(nameof(IsBodyOrHair));
            Changed(nameof(IsNotBodyOrHair));
            Changed(nameof(IsHairRole));
            Changed(nameof(IsMappedAndNotHair));
            foreach (var mode in dynamicModes)
                mode.NotifyRoleChanged();
            RefreshDynamicModePartitions();
            ValidateLive();
            Changed(nameof(HasCustomization));
        }
    }
    public int ModeIndex
    {
        get => modeIndex;
        set
        {
            if (refreshingLocalization) return;
            modeIndex = value;
            legacyModeEdited = value > 0;
            RefreshPmxDefaults();
            Changed();
            Changed(nameof(NeedsPrimary));
            Changed(nameof(NeedsSecondary));
            Changed(nameof(NeedsPipeline));
            Changed(nameof(IsFace));
            Changed(nameof(PrimaryLabel));
            Changed(nameof(SecondaryLabel));
            Changed(nameof(ExistingMapping));
            Changed(nameof(HasCustomization));
        }
    }
    public int PipelineIndex
    {
        get => pipelineIndex;
        set
        {
            if (refreshingLocalization) return;
            pipelineIndex = value;
            RefreshPmxDefaults();
            Changed();
            Changed(nameof(NeedsSecondary));
            Changed(nameof(PrimaryLabel));
            Changed(nameof(SecondaryLabel));
        }
    }
    public EditorSourceChoice? PrimaryTexture { get => primaryTexture; set { primaryTexture = value; Changed(); } }
    public EditorSourceChoice? SecondaryTexture { get => secondaryTexture; set { secondaryTexture = value; Changed(); } }
    public decimal? FaceNormal { get => faceNormal; set { faceNormal = value; Changed(); Changed(nameof(HasCustomization)); } }
    public decimal? FaceShadow { get => faceShadow; set { faceShadow = value; Changed(); Changed(nameof(HasCustomization)); } }
    public decimal? EyeShadow { get => eyeShadow; set { eyeShadow = value; Changed(); Changed(nameof(HasCustomization)); } }
    public decimal? PmxSaturation { get => ReadPmx(PmxDefaults.Saturation); set => SetPmx(value, PmxDefaults.Saturation); }
    public decimal? PmxTransitionWidthScale { get => ReadPmx(PmxDefaults.TransitionWidthScale); set => SetPmx(value, PmxDefaults.TransitionWidthScale); }
    public decimal? PmxValueContrast { get => ReadPmx(PmxDefaults.ValueContrast); set => SetPmx(value, PmxDefaults.ValueContrast); }
    public decimal? PmxShadeGamma { get => ReadPmx(PmxDefaults.ShadeGamma); set => SetPmx(value, PmxDefaults.ShadeGamma); }
    public decimal? PmxHairHighlightStrength { get => ReadPmx(PmxDefaults.HairHighlightStrength); set => SetPmx(value, PmxDefaults.HairHighlightStrength); }
    public decimal? PmxHairHighlightWidth { get => ReadPmx(PmxDefaults.HairHighlightWidth); set => SetPmx(value, PmxDefaults.HairHighlightWidth); }
    public bool IsMapped => RoleIndex > 0;
    public bool IsFaceRole => RoleIndex == 2;
    public bool IsEyeRole => RoleIndex == 3;
    public bool HasActiveStylePlugin => styleCatalog.FindStyle(effectiveStyleId) is not null;
    public bool IsBodyOrHair => RoleIndex is 1 or 4;
    public bool IsNotBodyOrHair => IsMapped && !IsBodyOrHair;
    public bool IsHairRole => RoleIndex == 4;
    public bool IsMappedAndNotHair => IsMapped && IsBodyOrHair && !IsHairRole;
    public bool SupportsOutlineScaling { get; }
    public bool OutlineScalingUnavailable => !SupportsOutlineScaling;
    public decimal? OutlinePercent { get => outlinePercent; set { outlinePercent = value; Changed(); Changed(nameof(HasCustomization)); Changed(nameof(ExistingMapping)); } }

    private decimal? ReadPmx(float inherited, [CallerMemberName] string property = "") =>
        pmxOverrides.GetValueOrDefault(property, (decimal)inherited);

    private void SetPmx(decimal? value, float inherited, [CallerMemberName] string property = "")
    {
        if (refreshingLocalization || value == ReadPmx(inherited, property)) return;
        pmxEdited = true;
        if (value == (decimal)inherited) pmxOverrides.Remove(property);
        else pmxOverrides[property] = value;
        Changed(property);
        Changed(nameof(HasCustomization));
    }

    public void ResetPmxTreatment()
    {
        pmxEdited = true;
        pmxOverrides.Clear();
        NotifyPmxValues();
        Changed(nameof(HasCustomization));
    }

    private void NotifyPmxValues()
    {
        Changed(nameof(PmxSaturation));
        Changed(nameof(PmxTransitionWidthScale));
        Changed(nameof(PmxValueContrast));
        Changed(nameof(PmxShadeGamma));
        Changed(nameof(PmxHairHighlightStrength));
        Changed(nameof(PmxHairHighlightWidth));
    }

    private void RefreshPmxDefaults()
    {
        var next = NeutralTreatment;
        bool editable = false;
        if (IsBodyOrHair && styleCatalog.FindStyle(effectiveStyleId) is { } style)
        {
            var role = D3D12StylePluginAdapter.ToContractRole((D3D12CharacterMaterialRole)(RoleIndex - 1));
            var modes = ActivePmxModes().Where(id => id != "default").Select(id => FindMode(style, id))
                .Where(mode => mode is null || mode.AllowedRoles.Count == 0 || mode.AllowedRoles.Contains(role)).ToArray();
            bool dedicated = modes.Length != 0;
            bool replaces = modes.Any(mode => mode is { ReplacesOrdinaryPmxTreatment: true });
            editable = !replaces;
            // Keep dormant ordinary settings available for a later return from a dedicated mode.
            next = D3D12ViewRenderingStyles.ResolveDefaultRoleTreatment(style, role, dedicated && !replaces) ?? NeutralTreatment;
        }
        if (CanEditOrdinaryPmxTreatment != editable)
        { CanEditOrdinaryPmxTreatment = editable; Changed(nameof(CanEditOrdinaryPmxTreatment)); }
        if (PmxDefaults == next) return;
        PmxDefaults = next;
        Changed(nameof(PmxDefaults));
        NotifyPmxValues();
    }

    private IEnumerable<string> ActivePmxModes()
    {
        if (legacyModeEdited)
        {
            var (style, mode) = ModeIndex switch
            {
                2 => (SelectedPipelineEntry()?.StyleId, "face-sdf"),
                3 => ("genshin", "hair"),
                4 => ("genshin", "hair-atlas"),
                5 => (SelectedPipelineEntry()?.StyleId, "hair"),
                6 => ("endfield", "hair"),
                _ => (GetDynamicMode(ModeIndex)?.StyleId, GetDynamicMode(ModeIndex)?.Mode.ModeId)
            };
            if (style == effectiveStyleId && mode is not null) yield return mode;
            yield break;
        }
        foreach (var mode in dynamicModes)
            if (mode.IsEnabled) yield return mode.ModeId;
        foreach (var saved in original?.PluginBindings ?? [])
            if (saved.StyleId == effectiveStyleId && !dynamicModes.Any(mode => ModeMatches(mode.Mode, saved.ModeId)))
                yield return saved.ModeId;
    }

    internal void UpdateEffectiveStyle(string styleId)
    {
        if (string.Equals(effectiveStyleId, styleId, StringComparison.Ordinal))
            return;
        effectiveStyleId = styleId;
        RebuildDynamicModes();
        RefreshPmxDefaults();
        ValidateLive();
        Changed(nameof(EffectiveStyleDisplayName));
        Changed(nameof(HasActiveStylePlugin));
        Changed(nameof(ExistingMapping));
        Changed(nameof(DynamicModesHeaderHint));
        Changed(nameof(NoDynamicModesNotice));
        Changed(nameof(HasDynamicModes));
        Changed(nameof(HasNoDynamicModes));
        Changed(nameof(ShowMapRoleHint));
        Changed(nameof(HasCustomization));
    }

    public D3D12MaterialBindingProfileOutline? BuildOutline()
    {
        if (OutlinePercent is null or < 0 or > 400) throw Error("Mda.Text.OutlineScale.Invalid");
        if (!SupportsOutlineScaling && OutlinePercent != 100) throw Error("Mda.Text.OutlineScale.ModernOnly");
        return OutlinePercent == 100 ? null : new(LocalMaterialIndex, (float)(OutlinePercent.Value / 100m));
    }

    public bool NeedsPrimary => ModeIndex is >= 2 and <= 6 || (GetDynamicMode(ModeIndex) is { } extra && extra.Mode.TextureInputs.Count >= 1);
    public bool NeedsSecondary
    {
        get
        {
            if (ModeIndex is 4 or 5) return true;
            if (ModeIndex == 2)
            {
                var entry = SelectedPipelineEntry();
                var mode = FindMode(entry?.Definition, "face-sdf");
                if (mode is not null)
                    return mode.TextureInputs.Count >= 2;
                return original?.PluginBindings.Any(static b => string.Equals(b.ModeId, "face-sdf", StringComparison.Ordinal)
                    && (b.TextureSlots?.ContainsKey("secondary") ?? false)) == true;
            }
            return GetDynamicMode(ModeIndex) is { } extra && extra.Mode.TextureInputs.Count >= 2;
        }
    }
    public bool NeedsPipeline => ModeIndex is 2 or 5;
    public bool IsFace => ModeIndex == 2 || (GetDynamicMode(ModeIndex)?.Mode.SupportsBoneTracking == true);

    public string PrimaryLabel
    {
        get
        {
            if (GetDynamicMode(ModeIndex) is { } extra && extra.Mode.TextureInputs.Count >= 1)
                return ResolveLocalized(extra.Mode.TextureInputs[0].DisplayName, extra.Mode.TextureInputs[0].SlotKey);
            return Text(ModeIndex switch
            {
                2 => "FaceSDF 主图",
                3 or 4 => "光照控制图（LightMap）",
                5 => "头发高光图",
                6 => "径向头发图",
                _ => "主图"
            });
        }
    }

    public string SecondaryLabel
    {
        get
        {
            if (GetDynamicMode(ModeIndex) is { } extra && extra.Mode.TextureInputs.Count >= 2)
                return ResolveLocalized(extra.Mode.TextureInputs[1].DisplayName, extra.Mode.TextureInputs[1].SlotKey);
            return Text(ModeIndex switch
            {
                2 => "FaceSDF 副图",
                4 => "明暗渐变图／图集",
                5 => "头发阴影图",
                _ => "副图"
            });
        }
    }

    public string ExistingMapping => BuildExistingMappingSummary();

    public string FixedBasisSummary => Format("固定世界方向  前 ({0:0.###}, {1:0.###}, {2:0.###})  右 ({3:0.###}, {4:0.###}, {5:0.###})",
        fixedForward.X, fixedForward.Y, fixedForward.Z, fixedRight.X, fixedRight.Y, fixedRight.Z);

    public void SetFixedBasis(Vector3 forward, Vector3 right)
    {
        if (!float.IsFinite(forward.LengthSquared()) || !float.IsFinite(right.LengthSquared()) || forward.LengthSquared() < 1e-8f || right.LengthSquared() < 1e-8f)
            throw Error("所选骨骼的朝向无效。");
        fixedForward = Vector3.Normalize(forward); fixedRight = Vector3.Normalize(right); fixedBasisChanged = true; Changed(nameof(FixedBasisSummary)); Changed(nameof(HasCustomization));
    }

    public D3D12MaterialBindingProfileRow? Build()
    {
        if (RoleIndex == 0) return null;
        int maxMode = 6 + legacyDynamicModes.Count;
        if (RoleIndex is < 1 or > 4 || ModeIndex < 0 || ModeIndex > maxMode)
            throw Error("{0}：请选择有效材质角色与数据类型。", Label);
        var role = (D3D12CharacterMaterialRole)(RoleIndex - 1);

        var nextGuards = new Dictionary<int, D3D12MaterialBindingProfileTexture>();
        int CaptureSlot(EditorSourceChoice? choice, string slotLabel)
        {
            if (choice is not { Index: >= 0 })
                throw Error("{0}：请选择{1}。", Label, EditorLocalization.Label(slotLabel));
            return CaptureOrdinal(choice.Index);
        }
        int CaptureOrdinal(int ordinal)
        {
            var texture = layout.CaptureTexture(ordinal);
            nextGuards[texture.SourceSceneTextureOrdinal] = texture;
            return texture.SourceSceneTextureOrdinal;
        }

        IReadOnlyList<StyleMaterialBindingData> nextBindings;
        IReadOnlyList<D3D12MaterialBindingProfileTexture> resultGuards;
        D3D12PmxMaterialTreatment? nextTreatment = BuildPmxTreatment(role);

        if (legacyModeEdited)
        {
            if (ModeIndex == 1)
            {
                nextBindings = [];
                resultGuards = [];
            }
            else
            {
                var binding = ModeIndex switch
                {
                    2 => BuildFaceSdfBinding(CaptureSlot),
                    3 => BuildRequiredStyleBinding("genshin", "hair", new() { ["texture"] = CaptureSlot(PrimaryTexture, "光照控制图（LightMap）") }),
                    4 => BuildRequiredStyleBinding("genshin", "hair-atlas", new()
                    {
                        ["lightmap"] = CaptureSlot(PrimaryTexture, "光照控制图（LightMap）"),
                        ["atlas"] = CaptureSlot(SecondaryTexture, "明暗渐变图／图集")
                    }),
                    5 => BuildHighlightShadowHairBinding(CaptureSlot),
                    6 => BuildRequiredStyleBinding("endfield", "hair", new() { ["texture"] = CaptureSlot(PrimaryTexture, "径向头发图") }),
                    _ => BuildDynamicModeBinding(GetDynamicMode(ModeIndex)!, CaptureSlot)
                };
                nextBindings = [binding];
                resultGuards = nextGuards.Values.ToArray();
            }
        }
        else if (editedModes.Count == 0 && !fixedBasisChanged)
        {
            nextBindings = original?.PluginBindings ?? [];
            resultGuards = original?.TextureGuards ?? [];
        }
        else
        {
            var combined = new List<StyleMaterialBindingData>();
            var replaced = new HashSet<EditorStyleModeItem>();
            foreach (var saved in original?.PluginBindings ?? [])
            {
                if (TryGetEditedMode(saved, out var mode))
                {
                    if (replaced.Add(mode!) && mode!.IsEnabled) combined.Add(BuildMode(mode));
                    continue;
                }
                var binding = fixedBasisChanged && saved.StyleId == effectiveStyleId && saved.ModeId == "face-sdf"
                    ? saved with { BoneBinding = new StyleBoneAxisBinding(StyleBoneTrackingMode.FixedWorld,
                        FixedWorldForward: fixedForward, FixedWorldRight: fixedRight) } : saved;
                combined.Add(binding);
                if (binding.TextureSlots is not null)
                    foreach (int ordinal in binding.TextureSlots.Values)
                        if (ordinal >= 0) CaptureOrdinal(ordinal);
            }
            foreach (var mode in editedModes.Values)
                if (!replaced.Contains(mode) && mode.IsEnabled) combined.Add(BuildMode(mode));
            nextBindings = combined;
            resultGuards = nextGuards.Values.ToArray();

            StyleMaterialBindingData BuildMode(EditorStyleModeItem mode)
            {
                var binding = mode.BuildBindingData(Label, CaptureOrdinal, strictValidation: true);
                return fixedBasisChanged && mode.StyleId == effectiveStyleId && mode.SupportsBoneTracking && mode.BoneTrackingModeIndex == 0
                    ? binding with { BoneBinding = new StyleBoneAxisBinding(StyleBoneTrackingMode.FixedWorld,
                        FixedWorldForward: fixedForward, FixedWorldRight: fixedRight) } : binding;
            }
        }

        var row = new D3D12MaterialBindingProfileRow(
            LocalMaterialIndex,
            role,
            EditorModelPropertiesViewModel.Number(FaceNormal, "面部法线明暗"),
            EditorModelPropertiesViewModel.Number(FaceShadow, "面部投影强度"),
            EditorModelPropertiesViewModel.Number(EyeShadow, "眼睛投影强度"),
            pmxMaterialTreatment: nextTreatment,
            pluginBindings: nextBindings,
            textureGuards: resultGuards,
            explicitRole: roleEdited || original?.ExplicitRole != false);

        row.Validate();
        return row == original ? original : row;
    }

    private D3D12PmxMaterialTreatment? BuildPmxTreatment(D3D12CharacterMaterialRole role)
    {
        if (role is not (D3D12CharacterMaterialRole.Body or D3D12CharacterMaterialRole.Hair) || pmxOverrides.Count == 0)
            return null;
        if (!pmxEdited && original?.PmxMaterialTreatment is { } saved && original.Role == role)
            return saved;

        float sat = EditorModelPropertiesViewModel.Number(PmxSaturation, Text("Mda.Text.ModelProps.PmxTreatmentSaturation"));
        float transition = EditorModelPropertiesViewModel.Number(PmxTransitionWidthScale, Text("Mda.Text.ModelProps.PmxTreatmentTransition"));
        float contrast = EditorModelPropertiesViewModel.Number(PmxValueContrast, Text("Mda.Text.ModelProps.PmxTreatmentContrast"));
        float gamma = EditorModelPropertiesViewModel.Number(PmxShadeGamma, Text("Mda.Text.ModelProps.PmxTreatmentGamma"));
        float hairStrength = role == D3D12CharacterMaterialRole.Hair
            ? EditorModelPropertiesViewModel.Number(PmxHairHighlightStrength, Text("Mda.Text.ModelProps.PmxTreatmentHairStrength"))
            : 0f;
        float hairWidth = EditorModelPropertiesViewModel.Number(PmxHairHighlightWidth, Text("Mda.Text.ModelProps.PmxTreatmentHairWidth"));

        var candidate = new D3D12PmxMaterialTreatment(sat, transition, contrast, gamma, hairStrength, hairWidth);
        candidate.Validate();
        return candidate;
    }

    private void RebuildDynamicModes()
    {
        rebuildingDynamicModes = true;
        try
        {
            dynamicModes.Clear();
            var activeStyle = styleCatalog.FindStyle(effectiveStyleId);
            if (activeStyle is not null)
            {
                foreach (var mode in activeStyle.MaterialModes)
                {
                    if (string.Equals(mode.ModeId, "default", StringComparison.Ordinal)
                        && mode.Parameters.Count == 0
                        && mode.TextureInputs.Count == 0
                        && !mode.SupportsBoneTracking)
                        continue;

                    var key = (activeStyle.StyleId, mode.ModeId);
                    if (!editedModes.TryGetValue(key, out var card))
                    {
                        card = new EditorStyleModeItem(activeStyle, mode, Textures, Bones, FindPreviousBinding(activeStyle.StyleId, mode),
                            () => IsMapped ? D3D12StylePluginAdapter.ToContractRole((D3D12CharacterMaterialRole)(RoleIndex - 1)) : null,
                            () => OnDynamicModeChanged(card!));
                    }
                    dynamicModes.Add(card);
                }
            }
        }
        finally
        {
            rebuildingDynamicModes = false;
        }
        RefreshDynamicModePartitions();
    }

    internal bool MatchesSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string trimmed = query.Trim();
        string indexText = LocalMaterialIndex.ToString(CultureInfo.InvariantCulture);
        if (indexText.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith('#') && trimmed.Length > 1
            && indexText.Contains(trimmed[1..], StringComparison.OrdinalIgnoreCase))
            return true;
        if (Label.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        if (Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(EnglishName)
            && EnglishName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private void RefreshDynamicModePartitions()
    {
        var nextVisible = dynamicModes
            .Where(static m => m.IsEnabled || m.IsRoleAllowed)
            .OrderByDescending(static m => m.IsEnabled)
            .ToArray();
        var nextIncompatible = dynamicModes
            .Where(static m => !m.IsEnabled && !m.IsRoleAllowed)
            .ToArray();

        if (!visibleDynamicModes.SequenceEqual(nextVisible))
        {
            visibleDynamicModes.Clear();
            foreach (var item in nextVisible)
                visibleDynamicModes.Add(item);
            Changed(nameof(HasVisibleDynamicModes));
        }

        if (!incompatibleDynamicModes.SequenceEqual(nextIncompatible))
        {
            incompatibleDynamicModes.Clear();
            foreach (var item in nextIncompatible)
                incompatibleDynamicModes.Add(item);
            Changed(nameof(HasIncompatibleDynamicModes));
            Changed(nameof(IncompatibleDynamicModesHeader));
        }
    }

    private bool TryGetEditedMode(StyleMaterialBindingData binding, out EditorStyleModeItem? mode)
    {
        if (editedModes.TryGetValue((binding.StyleId, binding.ModeId), out mode)) return true;
        mode = editedModes.Values.FirstOrDefault(item => item.StyleId == binding.StyleId && ModeMatches(item.Mode, binding.ModeId));
        return mode is not null;
    }

    private void OnDynamicModeChanged(EditorStyleModeItem mode)
    {
        if (rebuildingDynamicModes) return;
        editedModes[(mode.StyleId, mode.ModeId)] = mode;
        RefreshPmxDefaults();
        RefreshDynamicModePartitions();
        ValidateLive();
        Changed(nameof(ExistingMapping));
        Changed(nameof(HasCustomization));
    }

    private void ValidateLive()
    {
        string nextMessage = "";
        if (IsMapped && styleCatalog.FindStyle(effectiveStyleId) is { } activeStyle)
        {
            var contractRole = D3D12StylePluginAdapter.ToContractRole((D3D12CharacterMaterialRole)(RoleIndex - 1));
            var enabledDrafts = dynamicModes
                .Where(static m => m.IsEnabled)
                .Select(m => m.BuildBindingData(Label, static ordinal => ordinal, strictValidation: false))
                .ToArray();
            if (enabledDrafts.Length > 0)
            {
                var validation = activeStyle.ValidateMaterial(new StyleMaterialValidationContext(
                    objectId,
                    LocalMaterialIndex,
                    rawMaterialName,
                    contractRole,
                    enabledDrafts,
                    textureDimensions.Count,
                    textureDimensions));
                if (!validation.IsValid)
                {
                    string? rawError = validation.LocalizedError?.Resolve(CurrentLanguage) ?? validation.ErrorMessage;
                    if (!string.IsNullOrWhiteSpace(rawError))
                        nextMessage = Format("Mda.Text.ModelProps.ValidationFailedBanner", rawError);
                }
            }
        }
        if (!string.Equals(styleValidationMessage, nextMessage, StringComparison.Ordinal))
        {
            styleValidationMessage = nextMessage;
            Changed(nameof(StyleValidationMessage));
            Changed(nameof(HasStyleValidationMessage));
            Changed(nameof(ValidationMessage));
            Changed(nameof(HasValidationMessage));
        }
    }

    private StyleMaterialBindingData BuildFaceSdfBinding(Func<EditorSourceChoice?, string, int> captureSlot)
    {
        var entry = SelectedPipelineEntry() ?? throw Error("请选择专用数据的画风。");
        var style = entry.Definition ?? throw Error("Mda.Text.ModelProps.PluginStyleUnavailableError", entry.StyleId);
        var mode = FindMode(style, "face-sdf") ?? throw Error("Mda.Text.ModelProps.StyleNoFaceSdfError", ResolvePipelineChoiceLabel(entry));
        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["primary"] = captureSlot(PrimaryTexture, "FaceSDF 主图")
        };
        if (mode.TextureInputs.Count >= 2)
            slots["secondary"] = captureSlot(SecondaryTexture, "FaceSDF 副图");

        var boneBinding = new StyleBoneAxisBinding(
            StyleBoneTrackingMode.FixedWorld,
            FixedWorldForward: fixedForward,
            FixedWorldRight: fixedRight);
        return BuildBindingFromDescriptor(style, mode, slots, boneBinding, FindPreviousBinding(style.StyleId, mode));
    }

    private StyleMaterialBindingData BuildHighlightShadowHairBinding(Func<EditorSourceChoice?, string, int> captureSlot)
    {
        var entry = SelectedPipelineEntry() ?? throw Error("请选择专用数据的画风。");
        var style = entry.Definition ?? throw Error("Mda.Text.ModelProps.PluginStyleUnavailableError", entry.StyleId);
        var mode = FindMode(style, "hair");
        bool hasHighlightShadow = mode is not null
            && mode.TextureInputs.Any(slot => string.Equals(slot.SlotKey, "highlight", StringComparison.Ordinal))
            && mode.TextureInputs.Any(slot => string.Equals(slot.SlotKey, "shadow", StringComparison.Ordinal));
        if (!hasHighlightShadow || mode is null)
            throw Error("Mda.Text.ModelProps.HighlightShadowHairStyleError");

        var slots = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["highlight"] = captureSlot(PrimaryTexture, "头发高光图"),
            ["shadow"] = captureSlot(SecondaryTexture, "头发阴影图")
        };
        return BuildBindingFromDescriptor(style, mode, slots, null, FindPreviousBinding(style.StyleId, mode));
    }

    private StyleMaterialBindingData BuildRequiredStyleBinding(string styleId, string modeId, Dictionary<string, int> slots)
    {
        var style = styleCatalog.FindStyle(styleId) ?? throw Error("Mda.Text.ModelProps.PluginStyleUnavailableError", styleId);
        var mode = FindMode(style, modeId) ?? throw Error("Mda.Text.ModelProps.StyleModeNotProvidedError", styleId, modeId);
        return BuildBindingFromDescriptor(style, mode, slots, null, FindPreviousBinding(style.StyleId, mode));
    }

    private StyleMaterialBindingData BuildDynamicModeBinding(DynamicModeEntry entry, Func<EditorSourceChoice?, string, int> captureSlot)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        if (entry.Mode.TextureInputs.Count >= 1)
        {
            var first = entry.Mode.TextureInputs[0];
            if (first.Required || PrimaryTexture is { Index: >= 0 })
                slots[first.SlotKey] = captureSlot(PrimaryTexture, ResolveLocalized(first.DisplayName, first.SlotKey));
        }
        if (entry.Mode.TextureInputs.Count >= 2)
        {
            var second = entry.Mode.TextureInputs[1];
            if (second.Required || SecondaryTexture is { Index: >= 0 })
                slots[second.SlotKey] = captureSlot(SecondaryTexture, ResolveLocalized(second.DisplayName, second.SlotKey));
        }
        StyleBoneAxisBinding? boneBinding = entry.Mode.SupportsBoneTracking
            ? new StyleBoneAxisBinding(StyleBoneTrackingMode.FixedWorld, FixedWorldForward: fixedForward, FixedWorldRight: fixedRight)
            : null;
        return BuildBindingFromDescriptor(entry.Definition, entry.Mode, slots, boneBinding, FindPreviousBinding(entry.StyleId, entry.Mode));
    }

    private StyleMaterialBindingData? FindPreviousBinding(string styleId, StyleMaterialModeDescriptor mode)
        => original?.PluginBindings.FirstOrDefault(binding =>
            string.Equals(binding.StyleId, styleId, StringComparison.Ordinal) && ModeMatches(mode, binding.ModeId));

    private static StyleMaterialBindingData BuildBindingFromDescriptor(
        IRenderingStyleDefinition style,
        StyleMaterialModeDescriptor mode,
        Dictionary<string, int> slots,
        StyleBoneAxisBinding? boneBinding,
        StyleMaterialBindingData? previous)
    {
        Dictionary<string, double>? numbers = null;
        Dictionary<string, bool>? bools = null;
        Dictionary<string, float[]>? vectors = null;
        Dictionary<string, string>? strings = null;

        foreach (var param in mode.Parameters)
        {
            switch (param.Kind)
            {
                case StyleParameterKind.Float:
                case StyleParameterKind.Int:
                    numbers ??= new(StringComparer.Ordinal);
                    numbers[param.Key] = previous?.NumberValues is { } prevNums && prevNums.TryGetValue(param.Key, out double num)
                        ? num
                        : param.DefaultNumber;
                    break;
                case StyleParameterKind.Bool:
                    bools ??= new(StringComparer.Ordinal);
                    bools[param.Key] = previous?.BoolValues is { } prevBools && prevBools.TryGetValue(param.Key, out bool flag)
                        ? flag
                        : param.DefaultBool;
                    break;
                case StyleParameterKind.Vector2:
                case StyleParameterKind.Vector3:
                case StyleParameterKind.Vector4:
                case StyleParameterKind.Color3:
                case StyleParameterKind.Color4:
                    if (previous?.VectorValues is { } prevVecs && prevVecs.TryGetValue(param.Key, out var vec) && vec is not null)
                    {
                        vectors ??= new(StringComparer.Ordinal);
                        vectors[param.Key] = vec.ToArray();
                    }
                    else if (param.DefaultVector is not null)
                    {
                        vectors ??= new(StringComparer.Ordinal);
                        vectors[param.Key] = param.DefaultVector.ToArray();
                    }
                    break;
                case StyleParameterKind.Enum:
                case StyleParameterKind.BoneName:
                    if (previous?.StringValues is { } prevStrs && prevStrs.TryGetValue(param.Key, out var str) && str is not null)
                    {
                        strings ??= new(StringComparer.Ordinal);
                        strings[param.Key] = str;
                    }
                    else if (param.DefaultString is not null)
                    {
                        strings ??= new(StringComparer.Ordinal);
                        strings[param.Key] = param.DefaultString;
                    }
                    break;
            }
        }

        return new StyleMaterialBindingData(
            style.StyleId,
            mode.ModeId,
            slots.Count > 0 ? slots : null,
            numbers,
            bools,
            vectors,
            strings,
            boneBinding);
    }

    private string[] BuildModeChoices()
    {
        List<string> choices =
        [
            Text("保留当前专用数据"),
            Text("普通 PMX（清除专用数据）"),
            "FaceSDF",
            Text("Mda.Text.56322a44a09f"),
            Text("Mda.Text.163107481cb7"),
            Text("星铁／鸣潮 · 头发"),
            Text("终末地 · 径向头发")
        ];
        foreach (var entry in legacyDynamicModes)
            choices.Add(ResolveLocalized(entry.Mode.DisplayName, entry.Mode.ModeId));
        return choices.ToArray();
    }

    private string BuildExistingMappingSummary()
    {
        if (!IsMapped) return OutlinePercent is { } percent && percent != 100m
            ? Format("Mda.Text.ModelProps.OutlineOnly", percent.ToString("0.##")) : Text("此材质尚无显式映射。");
        if (legacyModeEdited) return ModeIndex > 1 ? ModeChoices[ModeIndex] : Text("当前：显式材质角色与普通 PMX 参数");
        var enabled = dynamicModes.Where(static m => m.IsEnabled).Select(static m => m.DisplayName).ToArray();
        if (enabled.Length > 0) return Format("Mda.Text.ModelProps.ExistingBindingsList", string.Join(" + ", enabled));
        return DormantBindingCount > 0 ? Format("Mda.Text.ModelProps.DormantBindings", DormantBindingCount)
            : Text("当前：显式材质角色与普通 PMX 参数");
    }

    private PipelineEntry? SelectedPipelineEntry()
        => (uint)pipelineIndex < (uint)pipelineEntries.Count ? pipelineEntries[pipelineIndex] : null;

    private DynamicModeEntry? GetDynamicMode(int index)
        => index >= 7 && index - 7 < legacyDynamicModes.Count ? legacyDynamicModes[index - 7] : null;

    private string ResolvePipelineChoiceLabel(PipelineEntry entry)
        => entry.Definition is { } def
            ? ResolveLocalized(def.DisplayName, def.StyleId)
            : Format("Mda.Text.Context.Inspector.Style.RetainedOption", entry.StyleId);

    private static string ResolveLocalized(LocalizedText? text, string fallback)
    {
        string? resolved = text?.Resolve(CurrentLanguage);
        return string.IsNullOrWhiteSpace(resolved) ? fallback : resolved;
    }

    private static StyleMaterialModeDescriptor? FindMode(IRenderingStyleDefinition? style, string modeId)
        => style?.MaterialModes.FirstOrDefault(mode => ModeMatches(mode, modeId));

    private static bool ModeMatches(StyleMaterialModeDescriptor mode, string modeId)
        => string.Equals(mode.ModeId, modeId, StringComparison.Ordinal)
            || (mode.LegacyAliases?.Any(alias => string.Equals(alias, modeId, StringComparison.Ordinal)) ?? false);

    private static bool IsCoveredByBuiltInModeRows(string styleId, string modeId)
    {
        if (string.Equals(modeId, "default", StringComparison.Ordinal)
            || string.Equals(modeId, "face-sdf", StringComparison.Ordinal))
            return true;
        if (string.Equals(styleId, "genshin", StringComparison.Ordinal) && modeId is "hair" or "genshin-hair" or "hair-atlas")
            return true;
        if (styleId is ("star-rail" or "wuthering-waves") && string.Equals(modeId, "hair", StringComparison.Ordinal))
            return true;
        if (string.Equals(styleId, "endfield", StringComparison.Ordinal) && string.Equals(modeId, "hair", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static StyleMaterialBindingData? FindPrimaryEditableBinding(D3D12MaterialBindingProfileRow? row)
    {
        if (row is null || row.PluginBindings.Count == 0)
            return null;
        return row.PluginBindings.FirstOrDefault(static b => string.Equals(b.ModeId, "face-sdf", StringComparison.Ordinal))
            ?? row.PluginBindings.FirstOrDefault(static b => b.ModeId is "hair" or "genshin-hair" or "hair-atlas")
            ?? row.PluginBindings[0];
    }

    private static int ExtractTextureOrdinal(StyleMaterialBindingData? binding, D3D12ViewStyleCatalog catalog, int slotIndex)
    {
        if (binding?.TextureSlots is not { Count: > 0 } slots)
            return -1;
        var mode = FindMode(catalog.FindStyle(binding.StyleId), binding.ModeId);
        if (mode is not null && slotIndex < mode.TextureInputs.Count)
        {
            if (slots.TryGetValue(mode.TextureInputs[slotIndex].SlotKey, out int mapped))
                return mapped;
        }

        string[] keys = slotIndex == 0
            ? ["primary", "texture", "lightmap", "highlight", "normal", "baseImage"]
            : ["secondary", "atlas", "shadow", "matcap", "detail", "p"];
        foreach (string key in keys)
        {
            if (slots.TryGetValue(key, out int ordinal))
                return ordinal;
        }
        return -1;
    }

    private void RefreshLocalization()
    {
        refreshingLocalization = true;
        try
        {
            foreach (var mode in dynamicModes)
                mode.RefreshLocalization();
            Changed(null);
        }
        finally { refreshingLocalization = false; }
        Changed(nameof(RoleIndex)); Changed(nameof(ModeIndex)); Changed(nameof(PipelineIndex));
        ValidateLive();
    }

    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    public override string ToString() => Label;
}
