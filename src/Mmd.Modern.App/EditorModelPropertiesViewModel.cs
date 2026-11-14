using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using Mmd.Modern.Application;
using Mmd.Modern.Core;
using Mmd.Modern.Rendering.D3D12;
using Mmd.Modern.ViewOutput.D3D12;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

/// <summary>One detached host edit. The workspace commits this together with its mapping Undo state.</summary>
public sealed record EditorModelPropertiesDraft(Guid ObjectId, string Name, ModelRole Role,
    Vector3 Position, Quaternion Rotation, float Scale, bool IkEnabled, bool MorphsEnabled,
    bool PhysicsEnabled, RenderingPresetReference? StyleOverride, D3D12MaterialBindingProfile? MaterialProfile,
    bool? EnvironmentIndoor = null);

public sealed record EditorModelStyleChoice(string SourceLabel, RenderingPresetReference? Reference,
    string? Suffix = null, bool TranslateSuffix = false) : INotifyPropertyChanged
{
    public string Label => Suffix is null ? Text(SourceLabel)
        : Format(SourceLabel, TranslateSuffix ? Text(Suffix) : Suffix);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public override string ToString() => Label;
}
public sealed record EditorSourceChoice(int Index, string SourceLabel) : INotifyPropertyChanged
{
    // Indexed texture/bone labels contain authored names; only the application-owned sentinel is translated.
    public string Label => Index < 0 ? Text("未选择") : SourceLabel;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public override string ToString() => Label;
}

/// <summary>Presentation-only copy of authored model properties. Opening/cancelling never edits a session.</summary>
public sealed class EditorModelPropertiesViewModel : INotifyPropertyChanged
{
    private readonly EditableSceneObject source;
    private readonly SceneActorMotion actor;
    private readonly RenderingPresetReference defaultStyle;
    private readonly D3D12MaterialBindingLayout layout;
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
    private readonly bool supportsOutlineScaling;

    public EditorModelPropertiesViewModel(EditorProjectWorkspace workspace, Guid objectId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        supportsOutlineScaling = EditorRenderEngineOptions.UsesModernQualityPreferences(workspace.RenderEngine);
        var session = workspace.Session ?? throw new InvalidOperationException(Text("请先载入模型。"));
        source = session.Objects.SingleOrDefault(item => item.Id == objectId)
            ?? throw new ArgumentException(Text("选中的模型已不存在。"), nameof(objectId));
        actor = workspace.Motions.Single(item => item.ObjectId == objectId);
        defaultStyle = session.RenderingStyles.DefaultStyle;
        profile = workspace.MaterialBindings.GetValueOrDefault(objectId);
        layout = D3D12MaterialBindingLayout.Capture(source.Source);
        name = source.Name; roleIndex = source.Role switch { ModelRole.Character => 0, ModelRole.Environment => 1, _ => 2 };
        environmentIndoor = session.GetModelPurpose(objectId).EnvironmentIndoor;
        positionX = Decimal(source.Position.X); positionY = Decimal(source.Position.Y); positionZ = Decimal(source.Position.Z);
        initialDegrees = ToDegrees(source.Rotation);
        rotationX = Decimal(initialDegrees.X); rotationY = Decimal(initialDegrees.Y); rotationZ = Decimal(initialDegrees.Z);
        scale = Decimal(source.Scale);
        ikEnabled = actor.IkEnabled; morphsEnabled = actor.MorphsEnabled; physicsEnabled = actor.PhysicsEnabled;
        string defaultLabel = defaultStyle.StableId switch { "mmd" => "MMD 原版", "genshin" => "原神", "star-rail" => "星穹铁道", "wuthering-waves" => "鸣潮", "endfield" => "终末地", _ => defaultStyle.StableId };
        List<EditorModelStyleChoice> styles = [new("继承工程默认 · {0}", null, defaultLabel, true), new("MMD 原版", RenderingStyleId.Mmd),
            new("原神", RenderingStyleId.Genshin), new("星穹铁道", RenderingStyleId.StarRail),
            new("鸣潮", RenderingStyleId.WutheringWaves), new("终末地", RenderingStyleId.Endfield)];
        if (session.RenderingStyles.ModelOverrides.TryGetValue(objectId, out var selected))
        {
            styleIndex = styles.FindIndex(choice => choice.Reference == selected);
            if (styleIndex < 0) { styleIndex = styles.Count; styles.Add(new("保留当前引用 · {0}", selected, selected.StableId)); }
        }
        StyleChoices = styles.AsReadOnly();
        Textures = new[] { new EditorSourceChoice(-1, "未选择") }.Concat(source.Source.Surface.Snapshot.Textures.Select((texture, index) =>
            new EditorSourceChoice(index, $"{index} · {texture.Name} ({texture.Width}×{texture.Height})"))).ToArray();
        Bones = actor.Model.Bones.Select((bone, index) => new EditorSourceChoice(index, $"{index} · {bone.Name}")).ToArray();
        RebuildMaterials();
        Observe(this, static model => model.RefreshLocalization());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid ObjectId => source.Id;
    public string SourcePath => source.SourcePath;
    public string[] RoleChoices => [Text("角色"), Text("场景"), Text("道具")];
    public IReadOnlyList<EditorModelStyleChoice> StyleChoices { get; }
    public IReadOnlyList<EditorSourceChoice> Textures { get; }
    public IReadOnlyList<EditorSourceChoice> Bones { get; }
    public ObservableCollection<EditorMaterialPropertiesItem> Materials { get; } = [];
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
    public int StyleIndex { get => styleIndex; set => Set(ref styleIndex, value); }
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
    { get => selectedMaterial; set { if (Set(ref selectedMaterial, value)) Changed(nameof(HasSelectedMaterial)); } }
    public void SetValidationMessage(string message) => ValidationMessage = message;

    public bool TryCreate(out EditorModelPropertiesDraft? draft)
    {
        draft = null;
        try
        {
            string validatedName = Name == source.Name ? source.Name : Name.Trim();
            if (string.IsNullOrWhiteSpace(validatedName) || validatedName != source.Name && validatedName.Any(char.IsControl))
                throw Error("请填写模型名称，且不要包含控制字符。");
            if (RoleIndex is < 0 or > 2) throw Error("请选择模型用途。");
            if ((uint)StyleIndex >= (uint)StyleChoices.Count) throw Error("请选择此模型的画风。");
            var position = new Vector3(OriginalOrNumber(PositionX, source.Position.X, "位置 X"), OriginalOrNumber(PositionY, source.Position.Y, "位置 Y"), OriginalOrNumber(PositionZ, source.Position.Z, "位置 Z"));
            float chosenScale = OriginalOrNumber(Scale, source.Scale, "缩放");
            if (chosenScale <= 0) throw Error("缩放必须大于 0。");
            var rotation = RotationX == Decimal(initialDegrees.X) && RotationY == Decimal(initialDegrees.Y) && RotationZ == Decimal(initialDegrees.Z)
                ? source.Rotation : Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(Radians(RotationY, "旋转 Y"), Radians(RotationX, "旋转 X"), Radians(RotationZ, "旋转 Z")));
            var mapping = BuildProfile();
            draft = new(source.Id, validatedName, RoleIndex switch { 0 => ModelRole.Character, 1 => ModelRole.Environment, _ => ModelRole.Prop },
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
        _ = D3D12MaterialBindingProfileResolver.Resolve(imported, source.Source, source.Id, "mmd");
        profile = imported; RebuildMaterials(); Changed(nameof(ProfileSummary)); ValidationMessage = "映射已载入草稿，点击“应用”后写入工程。";
    }

    public byte[] ExportProfile() => D3D12MaterialBindingProfileJson.Encode(BuildProfile()
        ?? new D3D12MaterialBindingProfile("custom-" + Guid.NewGuid().ToString("N"), SafeProfileName(), layout.Fingerprint, [], layout.Version));

    public void CaptureBoneBasis(SceneProjectSession session)
    {
        var material = SelectedMaterial ?? throw new InvalidOperationException(Text("请先选择材质。"));
        if (material.SelectedBone is not { Index: >= 0 } bone || bone.Index >= actor.Model.Bones.Count)
            throw Error("请选择此模型的有效骨骼。");
        // The exact authored actor and source identity must still be present; never reuse an index across models.
        var currentActor = session.CaptureAuthoredContent().Motions.SingleOrDefault(item => item.ObjectId == source.Id);
        if (currentActor is null || !ReferenceEquals(currentActor.Model, actor.Model)
            || !ReferenceEquals(session.Objects.SingleOrDefault(item => item.Id == source.Id)?.Source, source.Source))
            throw new InvalidOperationException(Text("模型已改变，请重新打开属性窗口。"));
        var snapshot = session.CaptureRenderSnapshot([new SceneObjectRenderRequest(source.Id, [bone.Index])]);
        if (!snapshot.SceneInputs.TryGetValue(source.Id, out var inputs) || !inputs.Visible
            || !inputs.BoneWorldMatrices.TryGetValue(bone.Index, out var world))
            throw new InvalidOperationException(Text("此帧无法读取所选骨骼，请先显示模型再采样。"));
        material.SetFixedBasis(Vector3.TransformNormal(Vector3.UnitZ, world), Vector3.TransformNormal(Vector3.UnitX, world));
        ValidationMessage = "已采样当前骨骼朝向为固定世界方向；不会随之后的骨骼动画跟随。";
    }

    private D3D12MaterialBindingProfile? BuildProfile()
    {
        var rows = Materials.Select(item => item.Build()).Where(row => row is not null).Cast<D3D12MaterialBindingProfileRow>().ToArray();
        var outlines = Materials.Select(item => item.BuildOutline()).OfType<D3D12MaterialBindingProfileOutline>().ToArray();
        if (profile is null && rows.Length == 0 && outlines.Length == 0) return null;
        if (profile is not null && profile.Materials.OrderBy(row => row.LocalMaterialIndex).SequenceEqual(rows)
            && profile.OutlineWidths.OrderBy(row => row.LocalMaterialIndex).SequenceEqual(outlines)) return profile;
        var result = new D3D12MaterialBindingProfile("custom-" + Guid.NewGuid().ToString("N"), SafeProfileName(), layout.Fingerprint,
            rows, layout.Version, outlineWidths: outlines);
        _ = D3D12MaterialBindingProfileResolver.Resolve(result, source.Source, source.Id, "mmd");
        return result;
    }

    private string SafeProfileName() { string value = Name.Trim(); if (value.Length == 0) value = Text("模型材质映射"); return value[..Math.Min(value.Length, 128)]; }
    private void RebuildMaterials()
    {
        int oldIndex = SelectedMaterial?.LocalMaterialIndex ?? 0;
        Materials.Clear();
        var existing = profile?.Materials.ToDictionary(row => row.LocalMaterialIndex) ?? [];
        var outlines = profile?.OutlineWidths.ToDictionary(row => row.LocalMaterialIndex) ?? [];
        for (int index = 0; index < source.Source.Materials.Count; index++)
        {
            // Display names come from the actual PMX table; no role or style is inferred from them.
            string materialName = index < actor.Model.Materials.Count ? actor.Model.Materials[index].Name : source.Source.Surface.Snapshot.Materials[index].Name;
            Materials.Add(new(index, materialName, existing.GetValueOrDefault(index), layout, Textures, Bones,
                outlines.GetValueOrDefault(index)?.Scale ?? 1f, supportsOutlineScaling));
        }
        SelectedMaterial = Materials.FirstOrDefault(item => item.LocalMaterialIndex == oldIndex) ?? Materials.FirstOrDefault();
        Changed(nameof(HasMaterials));
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
    private int roleIndex, modeIndex, pipelineIndex;
    private EditorSourceChoice? primaryTexture, secondaryTexture;
    private Vector3 fixedForward, fixedRight;
    private bool fixedBasisChanged;
    private bool refreshingLocalization;
    private decimal? faceNormal, faceShadow, eyeShadow;
    private decimal? outlinePercent;
    public EditorMaterialPropertiesItem(int index, string name, D3D12MaterialBindingProfileRow? row,
        D3D12MaterialBindingLayout layout, IReadOnlyList<EditorSourceChoice> textures, IReadOnlyList<EditorSourceChoice> bones,
        float outlineScale = 1f, bool supportsOutlineScaling = true)
    {
        LocalMaterialIndex = index; Label = $"{index} · {name}"; original = row; this.layout = layout;
        SupportsOutlineScaling = supportsOutlineScaling; outlinePercent = (decimal)outlineScale * 100m;
        Textures = textures; Bones = bones; roleIndex = row is null ? 0 : (int)row.Role + 1;
        faceNormal = (decimal)(row?.FaceNormalShadeStrength ?? .35f); faceShadow = (decimal)(row?.FaceGeometricShadowStrength ?? 1f); eyeShadow = (decimal)(row?.EyeGeometricShadowStrength ?? .2f);
        var face = row?.Face;
        fixedForward = face?.HeadWorldForward ?? Vector3.UnitZ; fixedRight = face?.HeadWorldRight ?? Vector3.UnitX;
        string? pipeline = face?.PipelineId ?? row?.StarMcHair?.PipelineId;
        pipelineIndex = pipeline switch { "genshin" => 0, "wuthering-waves" => 2, "endfield" => 3, _ => 1 };
        int first = face?.Primary.SourceSceneTextureOrdinal ?? row?.GenshinHair?.Texture.SourceSceneTextureOrdinal
            ?? row?.GenshinHairAtlas?.Lightmap.SourceSceneTextureOrdinal ?? row?.StarMcHair?.Highlight.SourceSceneTextureOrdinal
            ?? row?.EndfieldHair?.Texture.SourceSceneTextureOrdinal ?? -1;
        int second = face?.Secondary?.SourceSceneTextureOrdinal ?? row?.GenshinHairAtlas?.Atlas.SourceSceneTextureOrdinal ?? row?.StarMcHair?.Shadow.SourceSceneTextureOrdinal ?? -1;
        primaryTexture = textures.FirstOrDefault(item => item.Index == first); secondaryTexture = textures.FirstOrDefault(item => item.Index == second);
        Observe(this, static model => model.RefreshLocalization());
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public int LocalMaterialIndex { get; }
    public string Label { get; }
    public string[] RoleChoices => [Text("沿用原材质（不映射）"), Text("Mda.Text.6f93b2ca1bb0"), Text("Mda.Text.68fa14a6d0cf"), Text("Mda.Text.4f550cf482e7"), Text("Mda.Text.982acfe3cb07")];
    public string[] ModeChoices => [Text("保留当前专用数据"), Text("普通 PMX（清除专用数据）"), "FaceSDF", Text("Mda.Text.56322a44a09f"), Text("Mda.Text.163107481cb7"), Text("星铁／鸣潮 · 头发"), Text("终末地 · 径向头发")];
    public string[] PipelineChoices => [Text("原神"), Text("星穹铁道"), Text("鸣潮"), Text("终末地")];
    public IReadOnlyList<EditorSourceChoice> Textures { get; }
    public IReadOnlyList<EditorSourceChoice> Bones { get; }
    public EditorSourceChoice? SelectedBone { get; set; }
    public int RoleIndex { get => roleIndex; set { if (refreshingLocalization) return; roleIndex = value; Changed(); Changed(nameof(IsMapped)); } }
    public int ModeIndex { get => modeIndex; set { if (refreshingLocalization) return; modeIndex = value; Changed(); Changed(nameof(NeedsPrimary)); Changed(nameof(NeedsSecondary)); Changed(nameof(NeedsPipeline)); Changed(nameof(IsFace)); Changed(nameof(PrimaryLabel)); Changed(nameof(SecondaryLabel)); } }
    public int PipelineIndex { get => pipelineIndex; set { if (refreshingLocalization) return; pipelineIndex = value; Changed(); Changed(nameof(NeedsSecondary)); } }
    public EditorSourceChoice? PrimaryTexture { get => primaryTexture; set { primaryTexture = value; Changed(); } }
    public EditorSourceChoice? SecondaryTexture { get => secondaryTexture; set { secondaryTexture = value; Changed(); } }
    public decimal? FaceNormal { get => faceNormal; set { faceNormal = value; Changed(); } }
    public decimal? FaceShadow { get => faceShadow; set { faceShadow = value; Changed(); } }
    public decimal? EyeShadow { get => eyeShadow; set { eyeShadow = value; Changed(); } }
    public bool IsMapped => RoleIndex > 0;
    public bool SupportsOutlineScaling { get; }
    public bool OutlineScalingUnavailable => !SupportsOutlineScaling;
    public decimal? OutlinePercent { get => outlinePercent; set { outlinePercent = value; Changed(); } }
    public D3D12MaterialBindingProfileOutline? BuildOutline()
    {
        if (OutlinePercent is null or < 0 or > 400) throw Error("Mda.Text.OutlineScale.Invalid");
        if (!SupportsOutlineScaling && OutlinePercent != 100) throw Error("Mda.Text.OutlineScale.ModernOnly");
        return OutlinePercent == 100 ? null : new(LocalMaterialIndex, (float)(OutlinePercent.Value / 100m));
    }
    public bool NeedsPrimary => ModeIndex >= 2;
    public bool NeedsSecondary => ModeIndex is 4 or 5 || ModeIndex == 2 && PipelineIndex is 0 or 3;
    public bool NeedsPipeline => ModeIndex is 2 or 5;
    public bool IsFace => ModeIndex == 2;
    public string PrimaryLabel => Text(ModeIndex switch { 2 => "FaceSDF 主图", 3 or 4 => "光照控制图（LightMap）", 5 => "头发高光图", 6 => "径向头发图", _ => "主图" });
    public string SecondaryLabel => Text(ModeIndex switch { 2 => "FaceSDF 副图", 4 => "明暗渐变图／图集", 5 => "头发阴影图", _ => "副图" });
    public string ExistingMapping => Text(original is null ? "此材质尚无显式映射。" : original.Face is not null ? "当前：FaceSDF"
        : original.StarMcBody is not null ? "Mda.Text.1b1caee6a93f"
        : original.EndfieldMatcap is not null ? "Mda.Text.fbbacdc212d0"
        : original.GenshinMetallic is not null ? "Mda.Text.37c4f44fe900"
        : original.GenshinHairAtlas is not null ? "Mda.Text.5bc3bbdf4dbf"
        : original.GenshinHair is not null ? "Mda.Text.cfb66317f18a"
        : original.StarMcHair is not null ? "当前：星铁／鸣潮头发"
        : original.EndfieldHair is not null ? "当前：终末地径向头发" : "当前：显式材质角色与普通 PMX 参数");
    public string FixedBasisSummary => Format("固定世界方向  前 ({0:0.###}, {1:0.###}, {2:0.###})  右 ({3:0.###}, {4:0.###}, {5:0.###})", fixedForward.X, fixedForward.Y, fixedForward.Z, fixedRight.X, fixedRight.Y, fixedRight.Z);
    public void SetFixedBasis(Vector3 forward, Vector3 right)
    {
        if (!float.IsFinite(forward.LengthSquared()) || !float.IsFinite(right.LengthSquared()) || forward.LengthSquared() < 1e-8f || right.LengthSquared() < 1e-8f)
            throw Error("所选骨骼的朝向无效。");
        fixedForward = Vector3.Normalize(forward); fixedRight = Vector3.Normalize(right); fixedBasisChanged = true; Changed(nameof(FixedBasisSummary));
    }

    public D3D12MaterialBindingProfileRow? Build()
    {
        if (RoleIndex == 0) return null;
        if (RoleIndex is < 1 or > 4 || ModeIndex is < 0 or > 6) throw Error("{0}：请选择有效材质角色与数据类型。", Label);
        var role = (D3D12CharacterMaterialRole)(RoleIndex - 1);
        var row = (ModeIndex == 0 ? original : null) ?? new D3D12MaterialBindingProfileRow(LocalMaterialIndex, role);
        row = row with { Role = role, FaceNormalShadeStrength = EditorModelPropertiesViewModel.Number(FaceNormal, "面部法线明暗"),
            FaceGeometricShadowStrength = EditorModelPropertiesViewModel.Number(FaceShadow, "面部投影强度"), EyeGeometricShadowStrength = EditorModelPropertiesViewModel.Number(EyeShadow, "眼睛投影强度") };
        D3D12MaterialBindingProfileTexture Texture(EditorSourceChoice? choice, string label)
        { if (choice is not { Index: >= 0 }) throw Error("{0}：请选择{1}。", Label, EditorLocalization.Label(label)); return layout.CaptureTexture(choice.Index); }
        string Pipeline() => PipelineIndex switch { 0 => "genshin", 1 => "star-rail", 2 => "wuthering-waves", 3 => "endfield", _ => throw Error("请选择专用数据的画风。") };
        switch (ModeIndex)
        {
            case 2:
                string pipeline = Pipeline();
                var encoding = pipeline switch { "genshin" => D3D12FaceSdfEncoding.GenshinSoftFace, "endfield" => D3D12FaceSdfEncoding.EndfieldRgbCmM, _ => D3D12FaceSdfEncoding.GrayRed };
                var oldFace = original?.Face;
                var face = oldFace is not null && oldFace.PipelineId == pipeline ? oldFace : new D3D12MaterialBindingProfileFace(pipeline,
                    Texture(PrimaryTexture, "FaceSDF 主图"), null, fixedForward, fixedRight,
                    pipeline == "genshin" ? .001f : 1f, pipeline == "genshin" ? .55f : 1f, encoding);
                row = row with { Face = face with { Primary = Texture(PrimaryTexture, "FaceSDF 主图"),
                    Secondary = pipeline is "genshin" or "endfield" ? Texture(SecondaryTexture, "FaceSDF 副图") : null,
                    HeadWorldForward = fixedForward, HeadWorldRight = fixedRight } }; break;
            case 3:
                row = row with { GenshinHair = (original?.GenshinHair ?? new(Texture(PrimaryTexture, "光照控制图（LightMap）"))) with { Texture = Texture(PrimaryTexture, "光照控制图（LightMap）") } }; break;
            case 4:
                row = row with { GenshinHairAtlas = (original?.GenshinHairAtlas ?? new(Texture(PrimaryTexture, "光照控制图（LightMap）"), Texture(SecondaryTexture, "明暗渐变图／图集"),
                    D3D12GenshinAtlasColorEncoding.LinearRgb, 1, 0, .55f, .01f, 1, 1, 1, 0)) with
                    { Lightmap = Texture(PrimaryTexture, "光照控制图（LightMap）"), Atlas = Texture(SecondaryTexture, "明暗渐变图／图集") } }; break;
            case 5:
                string hairPipeline = Pipeline();
                if (hairPipeline is not ("star-rail" or "wuthering-waves")) throw Error("此头发数据只能显式选择星铁或鸣潮。");
                var oldHair = original?.StarMcHair;
                row = row with { StarMcHair = (oldHair is not null && oldHair.PipelineId == hairPipeline ? oldHair : new(hairPipeline,
                    Texture(PrimaryTexture, "头发高光图"), Texture(SecondaryTexture, "头发阴影图"))) with
                    { Highlight = Texture(PrimaryTexture, "头发高光图"), Shadow = Texture(SecondaryTexture, "头发阴影图") } }; break;
            case 6:
                row = row with { EndfieldHair = (original?.EndfieldHair ?? new(Texture(PrimaryTexture, "径向头发图"), Vector3.One)) with { Texture = Texture(PrimaryTexture, "径向头发图") } }; break;
        }
        if (ModeIndex == 0 && fixedBasisChanged && row.Face is { } retainedFace)
            row = row with { Face = retainedFace with { HeadWorldForward = fixedForward, HeadWorldRight = fixedRight } };
        row.Validate();
        return row == original ? original : row;
    }
    private void RefreshLocalization()
    {
        refreshingLocalization = true;
        try { Changed(null); }
        finally { refreshingLocalization = false; }
        Changed(nameof(RoleIndex)); Changed(nameof(ModeIndex)); Changed(nameof(PipelineIndex));
    }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    public override string ToString() => Label;
}
