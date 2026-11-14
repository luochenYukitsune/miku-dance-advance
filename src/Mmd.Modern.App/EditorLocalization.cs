using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Mmd.Modern.App;

/// <summary>
/// Local editor UI strings. Model, bone, morph, material and file names are user content and must
/// never be passed through this catalog. AXAML uses DynamicResource; view models use Text/Format
/// and refresh their computed properties when Changed is raised.
/// </summary>
public static class EditorLocalization
{
    private const string AssetRoot = "avares://Mmd.Modern.App/Assets/Localization/";
    private static readonly Lazy<Catalog> Strings = new(LoadCatalog);
    private static ResourceDictionary? resources;
    private static Avalonia.Application? resourceOwner;
    private static string currentLanguage = "zh-CN";

    public static string CurrentLanguage => currentLanguage;
    public static event EventHandler? Changed;

    /// <summary>Observe UI language changes without keeping a view model or closed dialog alive.</summary>
    public static void Observe<T>(T target, Action<T> refresh) where T : class =>
        EditorLocalizationSubscriptions.Observe(target, refresh);

    /// <summary>
    /// Call on the UI thread after Avalonia initialization and before constructing the editor.
    /// Only this class's merged resource dictionary is updated; theme resources remain intact.
    /// Reapplying the current language also installs resources into a newly initialized app.
    /// </summary>
    public static void SetLanguage(string language)
    {
        string normalized = NormalizeLanguage(language);
        bool installedResources = false;
        if (Avalonia.Application.Current is { } application)
        {
            Dispatcher.UIThread.VerifyAccess();
            var values = Strings.Value.Languages[normalized];
            if (!ReferenceEquals(resourceOwner, application) || resources is null)
            {
                resources = new ResourceDictionary();
                resourceOwner = application;
                resources.SetItems(values.Select(pair =>
                    new KeyValuePair<object, object?>(pair.Key, pair.Value)));
                application.Resources.MergedDictionaries.Add(resources);
                installedResources = true;
            }
            else
            {
                // Avalonia 11.3 SetItems publishes one resources-changed notification for the batch.
                resources.SetItems(values.Select(pair =>
                    new KeyValuePair<object, object?>(pair.Key, pair.Value)));
            }
        }
        bool changed = !StringComparer.Ordinal.Equals(currentLanguage, normalized);
        currentLanguage = normalized;
        // A view model created before Avalonia used the default source literals. Refresh it once
        // resources are installed, even when the requested language is already the default.
        if (changed || installedResources) Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Accepts a catalog key or a frozen legacy source literal; unknown strings survive.</summary>
    public static string Text(string originalChineseOrKey)
    {
        ArgumentNullException.ThrowIfNull(originalChineseOrKey);
        // Keep the existing default-literal path usable before Avalonia registers its asset loader.
        // Once initialized, legacy literals must resolve the revised Chinese catalog as well.
        if (currentLanguage == "zh-CN" && !originalChineseOrKey.StartsWith("Mda.Text.", StringComparison.Ordinal)
            && Avalonia.Application.Current is null && !Strings.IsValueCreated)
            return originalChineseOrKey;
        var catalog = Strings.Value;
        string key = catalog.Languages[currentLanguage].ContainsKey(originalChineseOrKey)
            ? originalChineseOrKey
            : catalog.OriginalToKey.GetValueOrDefault(originalChineseOrKey) ?? originalChineseOrKey;
        return catalog.Languages[currentLanguage].GetValueOrDefault(key) ?? originalChineseOrKey;
    }

    /// <summary>
    /// Translate a composite-format template before inserting values. E.g. Format("第 {0} 帧", frame).
    /// Values, including names supplied by the model, are inserted unchanged.
    /// </summary>
    public static string Format(string originalChineseOrKey, params object?[] args) =>
        string.Format(CultureInfo.GetCultureInfo(currentLanguage), Text(originalChineseOrKey), args);

    /// <summary>UI validation retains its template so an already displayed error can change language.</summary>
    internal static ArgumentException Error(string originalChineseOrKey, params object?[] args) =>
        new EditorLocalizedArgumentException(originalChineseOrKey, args);

    /// <summary>Marks an application-authored field label used inside a validation template.</summary>
    internal static object Label(string originalChineseOrKey) => new EditorLocalizedLabel(originalChineseOrKey);

    public static string NormalizeLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return language.Trim().ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh-CN",
            "en" or "en-us" or "en-gb" => "en",
            "ja" or "ja-jp" => "ja",
            _ => throw new ArgumentException("Supported editor languages: zh-CN, en, ja.", nameof(language))
        };
    }

    private static Catalog LoadCatalog()
    {
        var languages = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (string language in new[] { "zh-CN", "en", "ja" })
        {
            using var input = AssetLoader.Open(new Uri(AssetRoot + language + ".json"));
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(input)
                ?? throw new InvalidDataException($"Empty editor localization catalog: {language}");
            if (values.Any(pair => !pair.Key.StartsWith("Mda.Text.", StringComparison.Ordinal)
                || string.IsNullOrEmpty(pair.Value)))
                throw new InvalidDataException($"Invalid editor localization catalog: {language}");
            languages.Add(language, values);
        }
        var source = languages["zh-CN"];
        foreach (var pair in languages)
            if (pair.Value.Count != source.Count || source.Keys.Any(key => !pair.Value.ContainsKey(key)))
                throw new InvalidDataException($"Editor localization keys differ: {pair.Key}");
        // Display text is editable and may repeat. Historical source literals identify their
        // original keys through the migration manifest, never through the current Chinese values.
        using var manifestInput = AssetLoader.Open(new Uri(AssetRoot + "manifest.json"));
        using var manifest = JsonDocument.Parse(manifestInput);
        if (!manifest.RootElement.TryGetProperty("literalToKey", out var aliases)
            || aliases.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing editor localization legacy aliases.");
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in aliases.EnumerateObject())
        {
            string? key = alias.Value.ValueKind == JsonValueKind.String ? alias.Value.GetString() : null;
            if (string.IsNullOrEmpty(alias.Name) || alias.Name.StartsWith("Mda.Text.", StringComparison.Ordinal)
                || string.IsNullOrEmpty(key) || !source.ContainsKey(key) || !originals.TryAdd(alias.Name, key))
                throw new InvalidDataException("Invalid editor localization legacy alias: " + alias.Name);
        }
        return new(languages, originals);
    }

    private sealed record Catalog(Dictionary<string, Dictionary<string, string>> Languages,
        Dictionary<string, string> OriginalToKey);
}
