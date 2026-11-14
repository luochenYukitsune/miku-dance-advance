using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;

namespace Mmd.Modern.App;

public partial class EditorWindow
{
    // Capture the UI-owned state before the await; hashing never borrows a project or solver on a worker.
    private async Task<IReadOnlyDictionary<string, string>> GetInteractionDiagnosticsMetadataAsync()
    {
        var viewport = this.FindControl<EditorViewportHost>("SceneViewport")!;
        var runtime = CapturePreviewRuntimeInfo();
        var project = _shell?.Workspace.Session;
        var app = typeof(EditorWindow).Assembly;
        var values = new Dictionary<string, string>
        {
            ["appVersion"] = app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? app.GetName().Version?.ToString() ?? "unknown",
            ["adapter"] = runtime.AdapterName ?? "not-started",
            ["isWarp"] = runtime.IsWarp.ToString(CultureInfo.InvariantCulture),
            ["viewportWidth"] = (runtime.Statistics?.Width ?? runtime.Width).ToString(CultureInfo.InvariantCulture),
            ["viewportHeight"] = (runtime.Statistics?.Height ?? runtime.Height).ToString(CultureInfo.InvariantCulture),
            ["viewQuality"] = JsonSerializer.Serialize(viewport.Quality),
            ["observedAntialiasing"] = runtime.Statistics?.Frame?.Antialiasing.ToString() ?? "not-presented",
            ["observedSmaaQuality"] = runtime.Statistics?.Frame?.SmaaQuality.ToString() ?? "not-presented",
            ["effectiveSpatialSamples"] = runtime.Statistics?.EffectiveSpatialSamples.ToString(CultureInfo.InvariantCulture) ?? "not-presented",
            ["requestedSpatialSamples"] = runtime.Statistics?.RequestedSpatialSamples.ToString(CultureInfo.InvariantCulture) ?? "not-presented",
            ["projectObjects"] = (project?.Objects.Count ?? 0).ToString(CultureInfo.InvariantCulture),
            ["projectActors"] = (project?.Actors.Count ?? 0).ToString(CultureInfo.InvariantCulture),
            ["outsideParentBindings"] = (project?.OutsideParents.Count ?? 0).ToString(CultureInfo.InvariantCulture),
            ["currentFrame"] = (project?.CurrentFrame ?? 0).ToString(CultureInfo.InvariantCulture),
            ["durationFrames"] = (project?.DurationFrames ?? 0).ToString(CultureInfo.InvariantCulture),
            ["projectPhysics"] = JsonSerializer.Serialize(project?.Settings.Physics),
            ["poseInitializationSubsteps"] = "120"
        };
        string location = app.Location;
        values["appSha256"] = await Task.Run(() =>
        {
            if (string.IsNullOrEmpty(location)) return "unavailable-single-file";
            using var file = File.OpenRead(location);
            return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        });
        return values;
    }
}
