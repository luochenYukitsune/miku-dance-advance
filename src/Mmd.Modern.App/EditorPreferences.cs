using System.Numerics;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Core;
using Mmd.Modern.ViewOutput;
using Mmd.Modern.ViewOutput.FFmpeg;

namespace Mmd.Modern.App;

/// <summary>Machine-local editor choices. These values are never written into a PMA project.</summary>
public sealed record EditorPreferences
{
    public const long Gibibyte = 1024L * 1024 * 1024;
    public const int CurrentSchemaVersion = 1;
    private const int MaximumFileBytes = 512 * 1024;
    private static readonly object StorageGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public int ThemeIndex { get; init; }
    public bool ShowWorldGuides { get; init; } = true;
    public bool InvertCameraMouseX { get; init; }
    public bool InvertCameraMouseY { get; init; }
    public string Language { get; init; } = "zh-CN";
    public string FfmpegPath { get; init; } = "";
    public string FfprobePath { get; init; } = "";
    public long PmxMaxBytes { get; init; } = 10 * Gibibyte;
    public long PmaPackageMaxBytes { get; init; } = 50 * Gibibyte;
    public long PmaExpandedMaxBytes { get; init; } = 50 * Gibibyte;
    public long PmaEntryMaxBytes { get; init; } = 10 * Gibibyte;
    public bool AutoSaveEnabled { get; init; } = true;
    public int AutoSaveIntervalMinutes { get; init; } = 3;
    public int RecoveryMaxVersions { get; init; } = 5;
    public long RecoveryMaxTotalBytes { get; init; } = 5 * Gibibyte;
    public string RecoveryDirectory { get; init; } = DefaultRecoveryDirectory;
    // Previous locations remain discoverable after a preference change and process crash.
    // They are search roots only; maintenance never moves or deletes their unhandled copies.
    public ImmutableArray<string> RecoverySearchDirectories { get; init; } = [];

    public EditorPreferences WithRecoveryDirectoryHistory(EditorPreferences previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        string current = NormalizeRecoverySearchDirectory(RecoveryDirectory);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current };
        var history = ImmutableArray.CreateBuilder<string>();
        foreach (string path in RecoverySearchDirectories.Concat(previous.RecoverySearchDirectories).Prepend(previous.RecoveryDirectory))
        {
            string normalized = NormalizeRecoverySearchDirectory(path);
            if (roots.Add(normalized)) history.Add(normalized);
        }
        var result = this with { RecoverySearchDirectories = history.ToImmutable() };
        result.ValidateRecoverySearchDirectories();
        return result;
    }

    private void ValidateRecoverySearchDirectories()
    {
        if (RecoverySearchDirectories.IsDefault || RecoverySearchDirectories.Length > 128)
            throw EditorLocalization.Error("Mda.Text.RecoveryPreferences.DirectoryHistoryLimit");
        foreach (string path in RecoverySearchDirectories) _ = NormalizeRecoverySearchDirectory(path);
        if (JsonSerializer.SerializeToUtf8Bytes(RecoverySearchDirectories).Length > 256 * 1024)
            throw EditorLocalization.Error("Mda.Text.RecoveryPreferences.DirectoryHistoryLimit");
    }

    private static string NormalizeRecoverySearchDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Any(char.IsControl)
            || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
            throw EditorLocalization.Error("Mda.Text.RecoveryPreferences.InvalidDirectory");
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        if (full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw EditorLocalization.Error("Mda.Text.RecoveryPreferences.InvalidDirectory");
        return Path.TrimEndingDirectorySeparator(full);
    }

    [JsonIgnore]
    public static string DirectoryPath => EditorUserDataPaths.Current.ConfigDirectory;
    [JsonIgnore]
    public static string DefaultPath => Path.Combine(DirectoryPath, "editor-preferences.json");
    [JsonIgnore]
    public static string DefaultOutputPath => Path.Combine(DirectoryPath, "editor-output.json");
    [JsonIgnore]
    public static string DefaultRecoveryDirectory => EditorUserDataPaths.Current.RecoveryDirectory;

    /// <summary>Editor-only first-run preset. Backend goldens, project settings and exports retain their own defaults.</summary>
    public static ViewQualityOptions DefaultPreviewQuality { get; } = new(
        Antialiasing: FrameAntialiasing.Smaa1X, SmaaQuality: SmaaQuality.High, SpatialSamples: 1);

    public static ViewQualityOptions LoadPreviewQuality(string? path = null)
    {
        try
        {
            var bytes = ReadBounded(path ?? Path.Combine(DirectoryPath, "editor-preview.json"));
            return bytes is null ? DefaultPreviewQuality : ViewOutputPresetJson.Deserialize(bytes).Preview.EffectiveQuality;
        }
        catch (Exception error) when (IsStorageFailure(error)) { return DefaultPreviewQuality; }
    }

    public static void SavePreviewQuality(ViewQualityOptions quality, string? path = null)
    {
        quality.Validate();
        var preset = new ViewOutputPresetDefinition("local.editor-preview", "Editor preview",
            new ViewRenderRequest(1280, 720, quality), new(1920, 1080, new(30, 1)));
        WriteAtomic(path ?? Path.Combine(DirectoryPath, "editor-preview.json"), ViewOutputPresetJson.Serialize(preset));
    }

    public void Validate(bool requireExecutableFiles = false)
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new NotSupportedException(EditorLocalization.Text("编辑器设置版本不受支持。"));
        if (ThemeIndex is < 0 or > 1) throw new ArgumentException(EditorLocalization.Text("请选择黑色或白色主题。"));
        if (Language is not ("zh-CN" or "en" or "ja")) throw new ArgumentException(EditorLocalization.Text("请选择简体中文、English 或日本語。"));
        ValidateExecutable(FfmpegPath, "FFmpeg", requireExecutableFiles);
        ValidateExecutable(FfprobePath, "ffprobe", requireExecutableFiles);
        if (PmxMaxBytes < 1 || PmaPackageMaxBytes < 1 || PmaExpandedMaxBytes < 1 || PmaEntryMaxBytes < 1)
            throw new ArgumentException(EditorLocalization.Text("所有容量上限必须大于 0。"));
        if (AutoSaveIntervalMinutes is < 1 or > 60)
            throw EditorLocalization.Error("Mda.Text.RecoveryPreferences.InvalidInterval");
        if (RecoveryMaxVersions is < 1 or > 20)
            throw EditorLocalization.Error("Mda.Text.RecoveryPreferences.InvalidVersions");
        if (RecoveryMaxTotalBytes < 1 || RecoveryMaxTotalBytes > 100 * Gibibyte)
            throw EditorLocalization.Error("Mda.Text.RecoveryPreferences.InvalidBudget");
        ValidateRecoveryDirectory(RecoveryDirectory);
        ValidateRecoverySearchDirectories();
    }

    public static EditorPreferences Load() => Load(out _);

    // Migration must reject unreadable preferences instead of publishing a new default copy.
    // Older schema-one files may omit the recovery location; interpret that default relative
    // to their source layout, independent of the production process's current paths.
    internal static EditorPreferences LoadForMigration(string path, string sourceDefaultRecoveryDirectory)
    {
        byte[] bytes = ReadBounded(path) ?? throw new FileNotFoundException("Preferences disappeared during migration.", path);
        var preferences = JsonSerializer.Deserialize<EditorPreferences>(bytes, JsonOptions)
            ?? throw new InvalidDataException(EditorLocalization.Text("设置文件内容为空。"));
        using var json = JsonDocument.Parse(bytes);
        if (!json.RootElement.TryGetProperty("recoveryDirectory", out _))
            preferences = preferences with { RecoveryDirectory = sourceDefaultRecoveryDirectory };
        preferences.Validate();
        return preferences;
    }

    internal byte[] SerializeForStorage()
    {
        Validate();
        return JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
    }

    /// <summary>Read-only recovery: malformed files remain on disk for diagnosis, and defaults are returned.</summary>
    public static EditorPreferences Load(out string? diagnostic, string? path = null)
    {
        diagnostic = null;
        try
        {
            byte[]? bytes = ReadBounded(path ?? DefaultPath);
            if (bytes is null) return new();
            var preferences = JsonSerializer.Deserialize<EditorPreferences>(bytes, JsonOptions)
                ?? throw new InvalidDataException(EditorLocalization.Text("设置文件内容为空。"));
            preferences.Validate();
            return preferences;
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            diagnostic = EditorLocalization.Text("无法读取编辑器设置，已使用默认设置；原文件未改动。") + error.Message;
            return new();
        }
    }

    /// <summary>Validate and flush a new sibling file, then publish it with a single atomic rename.</summary>
    public void Save(string? path = null)
    {
        WriteAtomic(path ?? DefaultPath, SerializeForStorage());
    }

    public static OutputSettingsDraft? LoadOutputSettings(out string? diagnostic, string? path = null)
    {
        diagnostic = null;
        try
        {
            byte[]? bytes = ReadBounded(path ?? DefaultOutputPath);
            if (bytes is null) return null;
            var stored = JsonSerializer.Deserialize<LocalOutputSnapshot>(bytes, JsonOptions)
                ?? throw new InvalidDataException(EditorLocalization.Text("输出设置内容为空。"));
            if (stored.SchemaVersion != CurrentSchemaVersion)
                throw new NotSupportedException(EditorLocalization.Text("本机输出设置版本不受支持。"));
            var preset = DecodePreset(stored.PresetJson);
            var requestPreset = DecodePreset(stored.RequestPresetJson);
            var request = requestPreset.Export.CreateRequest(stored.StartFrame, stored.EndFrame, stored.PhysicsPreRollFrames,
                stored.IncludeEndFrame ?? true);
            FfmpegOutputOptions? encoding = stored.VideoEncodingJson is null ? null :
                FfmpegEncodingPreset.ToOptions(new(FfmpegEncodingPreset.ProviderId,
                    FfmpegEncodingPreset.SchemaVersion, stored.VideoEncodingJson), stored.Destination, request.Plan)
                with { Timeout = TimeSpan.FromTicks(stored.VideoTimeoutTicks) };
            var draft = new OutputSettingsDraft(preset, request, stored.MediaKind, stored.Destination,
                stored.TransparentBackground, new Vector4(stored.Background.R, stored.Background.G,
                    stored.Background.B, stored.Background.A), encoding, stored.EndFrameFollowsAnimation);
            draft.Validate();
            return draft;
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            diagnostic = EditorLocalization.Text("无法读取本机输出设置，已使用默认输出配置；原文件未改动。") + error.Message;
            return null;
        }
    }

    /// <summary>Store preview/output presets with the existing strict codec; host paths and ranges stay local.</summary>
    public static void SaveOutputSettings(OutputSettingsDraft settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var plan = settings.Request.Plan;
        var requestPreset = new ViewOutputPresetDefinition("local.editor-request", "Editor export request",
            settings.Preset.Preview, new(plan.Width, plan.Height, plan.FrameRate, settings.Request.Quality, settings.Request.CaptureMemory));
        var background = settings.BackgroundColor;
        var stored = new LocalOutputSnapshot(CurrentSchemaVersion, EncodePreset(settings.Preset),
            EncodePreset(requestPreset), settings.MediaKind, settings.Destination, settings.TransparentBackground,
            new(background.X, background.Y, background.Z, background.W), plan.StartFrame, plan.EndFrame,
            plan.PhysicsPreRollFrames, settings.EndFrameFollowsAnimation,
            settings.VideoOptions is { } encoding ? FfmpegEncodingPreset.FromOptions(encoding).DataJson : null,
            settings.VideoOptions?.Timeout.Ticks ?? TimeSpan.FromMinutes(2).Ticks, plan.IncludeEndFrame);
        WriteAtomic(path ?? DefaultOutputPath, JsonSerializer.SerializeToUtf8Bytes(stored, JsonOptions));
    }

    private static string EncodePreset(ViewOutputPresetDefinition preset) => Encoding.UTF8.GetString(ViewOutputPresetJson.Serialize(preset));
    private static ViewOutputPresetDefinition DecodePreset(string json) => ViewOutputPresetJson.Deserialize(Encoding.UTF8.GetBytes(json));

    private static void ValidateExecutable(string path, string name, bool requireFile)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0) return;
        if (!Path.IsPathFullyQualified(path) || path.Length > 32767 || path.Any(char.IsControl)
            || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(EditorLocalization.Format("{0} 需要完整的 .exe 文件路径；留空可自动查找。", name));
        _ = Path.GetFullPath(path);
        if (requireFile && !File.Exists(path)) throw new ArgumentException(EditorLocalization.Format("找不到 {0} 文件，请重新选择，或清空路径以自动查找。", name));
    }

    private static void ValidateRecoveryDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Any(char.IsControl)
            || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
            throw EditorLocalization.Error("Mda.Text.RecoveryPreferences.InvalidDirectory");
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath)!;
            if (OperatingSystem.IsWindows() && new DriveInfo(root).DriveType == DriveType.Network
                || fullPath[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw EditorLocalization.Error("Mda.Text.RecoveryPreferences.InvalidDirectory");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw EditorLocalization.Error("Mda.Text.RecoveryPreferences.InvalidDirectory"); }
        if (File.Exists(fullPath)) throw EditorLocalization.Error("Mda.Text.RecoveryPreferences.DirectoryIsFile");
        // A new directory is valid; creation and write failures belong to the recovery save,
        // so opening or applying preferences never creates folders or probes with user files.
    }

    private static byte[]? ReadBounded(string path)
    {
        lock (StorageGate)
        {
            try
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length is < 1 or > MaximumFileBytes) throw new InvalidDataException(EditorLocalization.Text("设置文件大小超出范围。"));
                using var buffer = new MemoryStream();
                byte[] block = new byte[8192];
                while (true)
                {
                    int read = input.Read(block, 0, Math.Min(block.Length, MaximumFileBytes + 1 - checked((int)buffer.Length)));
                    if (read == 0) break;
                    buffer.Write(block, 0, read);
                    if (buffer.Length > MaximumFileBytes) throw new InvalidDataException(EditorLocalization.Text("设置文件大小超出范围。"));
                }
                return buffer.ToArray();
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaximumFileBytes) throw new InvalidDataException(EditorLocalization.Text("设置文件大小超出范围。"));
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        lock (StorageGate)
        {
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes); output.Flush(flushToDisk: true); }
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    // InvalidDataException covers empty (e.g. power-loss truncated), oversized and null-content
    // files. They are local damage like any other unreadable file: recover with defaults.
    private static bool IsStorageFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or JsonException or OverflowException or InvalidDataException;

    private sealed record LocalColor(float R, float G, float B, float A);
    private sealed record LocalOutputSnapshot(int SchemaVersion, string PresetJson, string RequestPresetJson,
        OutputMediaKind MediaKind, string Destination, bool TransparentBackground, LocalColor Background,
        uint StartFrame, uint EndFrame, uint PhysicsPreRollFrames, bool EndFrameFollowsAnimation,
        string? VideoEncodingJson, long VideoTimeoutTicks, bool? IncludeEndFrame = null);
}
