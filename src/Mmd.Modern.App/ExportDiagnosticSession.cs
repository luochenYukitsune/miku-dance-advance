using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput;

namespace Mmd.Modern.App;

internal sealed record ExportDiagnosticNativeModulePath(string Name, string? Path, string? Error = null);

/// <summary>One export's local diagnostics. JSONL is versioned and correlated; summary.txt and final.json
/// retain the final boundary. No live scene is retained. Logging failures never fail the export.</summary>
internal sealed class ExportDiagnosticSession : IDisposable
{
    internal const int SchemaVersion = 1;
    private const string Schema = "mda.export-diagnostics";
    private const int MaximumProgressEvents = 2048;
    private static readonly HashSet<string> NativeModuleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mmd_bullet.dll", "mmd_bullet_soft.dll", "d3d12.dll", "D3D12Core.dll", "dxgi.dll", "d3dcompiler_47.dll",
        "dxcompiler.dll", "dxil.dll", "mmd_mikktspace.dll", "d3d9.dll", "d3dx9_43.dll", "d3dcompiler_43.dll",
        "Mmd.Modern.D3D9.Compatibility.dll"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }, MaxDepth = 32, IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private static readonly JsonSerializerOptions ReadableJsonOptions = new(JsonOptions) { WriteIndented = true };
    private readonly object gate = new();
    private readonly OfflineFramePlan? plan;
    private readonly Dictionary<string, object?> metadata;
    private readonly Func<IReadOnlyList<ExportDiagnosticNativeModulePath>> nativeModuleInventory;
    private readonly Dictionary<string, object?> nativeFileIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private StreamWriter? writer;
    private string? directory;
    private long sequence, completedFrames;
    private long? simulationTicks;
    private int progressEvents;
    private TimeSpan? lastProgressAt;
    private string phase = "created";
    private ExportTaskState state = ExportTaskState.Running;
    private ExportDiagnosticFrame? lastCompleted, failureContext;
    private ExportDiagnosticException? failure;
    private readonly List<ExportDiagnosticException> secondaryErrors = [];
    private bool finished, disposed, identitiesCaptured, nativeFinalCaptured;

    internal string TaskId { get; } = Guid.NewGuid().ToString("N");
    internal string? JsonLinesPath { get; private set; }
    internal string? SummaryPath { get; private set; }
    internal string? FinalJsonPath { get; private set; }
    internal string? PersistenceError { get; private set; }
    internal string ReadableSummary
    {
        get
        {
            lock (gate)
            {
                try { return BuildSummary(); }
                catch (Exception error) { return "MDA export " + TaskId + "\nState: " + state + "\nDiagnostic report unavailable: " + error.GetType().Name; }
            }
        }
    }

    internal ExportDiagnosticSession(OfflineFramePlan? plan, IReadOnlyDictionary<string, object?> metadata, string? logRoot = null,
        Func<IReadOnlyList<ExportDiagnosticNativeModulePath>>? nativeModuleInventory = null)
    {
        this.plan = plan;
        this.metadata = new(metadata);
        this.nativeModuleInventory = nativeModuleInventory ?? ObserveLoadedNativeModules;
        Guard(() =>
        {
            directory = Path.Combine(logRoot ?? EditorUserDataPaths.Current.ExportLogDirectory,
                startedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + TaskId);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "events.jsonl");
            writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            JsonLinesPath = path;
        });
        Guard(() => Append("started", this.metadata));
    }

    // Called on the UI owner. Only bounded authored metadata is frozen here; hashing happens on the export worker.
    internal static ExportDiagnosticSession Start(EditorProjectWorkspace workspace, OutputSettingsDraft draft, string? logRoot = null)
    {
        var app = typeof(EditorExportService).Assembly;
        var source = workspace.Session;
        var request = draft.Request;
        var plan = request?.Plan;
        var encoding = draft.VideoOptions;
        return new(plan, new Dictionary<string, object?>
        {
            ["appVersion"] = app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? app.GetName().Version?.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["processId"] = Environment.ProcessId,
            ["documentId"] = workspace.DocumentId,
            ["projectPath"] = workspace.ProjectPath,
            ["projectHasUnsavedChanges"] = workspace.IsDirty,
            ["inputIdentityScope"] = "Observed on-disk file; detached authored snapshot may contain unsaved edits.",
            ["projectObjects"] = source?.Objects.Count,
            ["projectActors"] = source?.Actors.Count,
            ["projectDurationFrames"] = source?.DurationFrames,
            ["projectSourceFrameAtStart"] = source?.CurrentFrame,
            ["physics"] = source?.Settings.Physics,
            ["poseInitializationSubsteps"] = 120,
            ["renderEngineId"] = workspace.RenderEngine.Id,
            ["rendererBackendId"] = null,
            ["device"] = null,
            ["output"] = new { draft.MediaKind, draft.Destination, draft.TransparentBackground, draft.BackgroundColor,
                requestedQuality = request?.Quality,
                encoding = encoding is null ? null : new { encoding.Encoder, encoding.RateControl, encoding.Quality, encoding.Speed,
                    encoding.TargetBitrate, encoding.MaximumBitrate, encoding.BufferSize, encoding.KeyframeInterval, encoding.Threads,
                    encoding.Profile, encoding.Level, encoding.Timeout, encoding.BackgroundRed, encoding.BackgroundGreen, encoding.BackgroundBlue } },
            ["plan"] = plan is null ? null : new { plan.Width, plan.Height,
                plan.StartFrame, plan.EndFrame, plan.PhysicsPreRollFrames,
                plan.IncludeEndFrame, plan.FrameCount,
                fpsNumerator = plan.FrameRate.Numerator, fpsDenominator = plan.FrameRate.Denominator,
                sourceFramesPerSecond = OfflineFramePlan.SourceFramesPerSecond, simulationTicksPerSecond = OfflineFramePlan.SimulationTicksPerSecond }
        }, logRoot);
    }

    internal void ReportPhase(string value) => Guard(() =>
    {
        if (finished || phase == value) return;
        phase = value; Append("phase", null);
    });

    internal void ReportProgress(ExportTaskProgress value) => Guard(() =>
    {
        if (finished) return;
        completedFrames = value.CompletedFrames;
        simulationTicks = value.SimulationTicks;
        if (plan is not null && completedFrames > 0 && completedFrames <= plan.FrameCount)
            lastCompleted = ExportDiagnosticFrame.Completed(plan.GetSample(completedFrames - 1), value.SimulationTicks);
        // Keep the latest boundary for final.json even when disk progress has reached its cap.
        if (value.State != ExportTaskState.Running || progressEvents >= MaximumProgressEvents
            || lastProgressAt is { } previous && elapsed.Elapsed - previous < TimeSpan.FromSeconds(5)) return;
        lastProgressAt = elapsed.Elapsed; progressEvents++; phase = "rendering";
        Append("progress", null);
    });

    internal void ReportDevice(ViewRendererDeviceInfo value) => Guard(() =>
    {
        metadata["device"] = value; metadata["rendererBackendId"] = value.BackendId;
        Append("device", value);
    });

    internal void ReportRenderer(string backendId) => Guard(() =>
    { metadata["rendererBackendId"] = backendId; Append("renderer", new { backendId }); });

    internal void ReportResources(RenderResourceSnapshot value) => Guard(() =>
    {
        if (finished) return;
        metadata["lastResourceObservation"] = value;
        metadata["resourceObservationScope"] = "CPU is process time normalized by all logical CPUs. GPU is this process's busiest observed engine on the renderer LUID. Video memory is process local-segment usage/budget; peaks are observed during this task. Null means unavailable, not zero.";
    });

    internal void ReportEffectiveRequest(ExportRequest request) => Guard(() =>
    { metadata["effectiveQuality"] = request.Render.Quality; Append("effective-request", new { quality = request.Render.Quality }); });

    internal void CaptureFileIdentities() => Guard(() =>
    {
        if (identitiesCaptured) return;
        identitiesCaptured = true;
        string? projectPath = metadata.GetValueOrDefault("projectPath") as string;
        metadata["projectFile"] = FileIdentity(projectPath);
        // Read actual loaded assemblies, not a source revision or a hardcoded product version.
        metadata["modules"] = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("Mmd.Modern", StringComparison.Ordinal) == true)
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal).Take(32)
            .Select(assembly => new { name = assembly.GetName().Name, version = assembly.GetName().Version?.ToString(),
                file = FileIdentity(assembly.IsDynamic ? null : assembly.Location) }).ToArray();
        Append("identities", new { projectFile = metadata["projectFile"], modules = metadata["modules"] });
        CaptureNativeModules("start");
    });

    internal void CaptureFinalNativeModules() => Guard(() =>
    {
        if (nativeFinalCaptured) return;
        nativeFinalCaptured = true;
        CaptureNativeModules("finish");
    });

    private void CaptureNativeModules(string observation)
    {
        object[] modules = [];
        string? probeError = null;
        try
        {
            modules = nativeModuleInventory().Where(module => NativeModuleNames.Contains(module.Name)).Take(NativeModuleNames.Count)
                .Select(module =>
                {
                    object? identity = null;
                    if (module.Path is { } path)
                    {
                        if (!nativeFileIdentities.TryGetValue(path, out identity))
                        { identity = FileIdentity(path); nativeFileIdentities[path] = identity; }
                    }
                    return (object)new { name = module.Name, path = module.Path, file = identity, error = module.Error };
                }).ToArray();
        }
        catch (Exception error) { probeError = error.GetType().Name + ": " + error.Message; }
        var snapshot = new
        {
            observedAtUtc = DateTimeOffset.UtcNow, observation, modules, probeError,
            scope = "Only already-loaded allowlisted process modules are observed. File SHA describes on-disk bytes at first observation, not a memory dump. Missing modules were not observed; this probe never loads a library."
        };
        metadata[observation == "start" ? "nativeModulesAtStart" : "nativeModulesAtFinish"] = snapshot;
        Append("native-identities", snapshot);
    }

    private static IReadOnlyList<ExportDiagnosticNativeModulePath> ObserveLoadedNativeModules()
    {
        var result = new List<ExportDiagnosticNativeModulePath>();
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            string name = module.ModuleName;
            if (!NativeModuleNames.Contains(name)) continue;
            try { result.Add(new(name, module.FileName)); }
            catch (Exception error) { result.Add(new(name, null, error.GetType().Name + ": " + error.Message)); }
        }
        return result;
    }

    internal void RecordFailure(Exception error) => Guard(() =>
    {
        CaptureFileIdentities();
        failure = ExportDiagnosticException.Capture(error);
        if (TryData(error, OfflineFrameFailureContext.DataKey) is OfflineFrameFailureContext context)
        {
            failureContext = ExportDiagnosticFrame.From(context);
            completedFrames = context.CompletedFrames; simulationTicks = context.SimulationTicks;
        }
        phase = TryData(error, "MDA.ViewOutput.Phase") as string ?? failureContext?.Phase ?? phase;
        Append("exception", failure);
    });

    internal void RecordSecondaryError(Exception error) => Guard(() =>
    {
        var detail = ExportDiagnosticException.Capture(error);
        if (secondaryErrors.Count < 8) secondaryErrors.Add(detail);
        Append("secondary-exception", detail);
    });

    internal void Finish(ExportTaskState finalState, Exception? error = null) => Guard(() =>
    {
        if (finished) return;
        if (error is not null) RecordFailure(error);
        CaptureFinalNativeModules();
        state = finalState; finished = true; elapsed.Stop();
        Append(finalState.ToString().ToLowerInvariant(), null);
        // Terminal files must be readable and copyable by ordinary tools while the retained
        // status window remains open. No more progress events are accepted after this point.
        Guard(() => { try { writer?.Dispose(); } finally { writer = null; } });
        // Each artifact is independent: one failed write must not suppress the other useful formats.
        WriteArtifact("final.json", JsonSerializer.Serialize(FinalReport(), ReadableJsonOptions), path => FinalJsonPath = path);
        WriteArtifact("summary.txt", BuildSummary(), path => SummaryPath = path);
        // Include final paths (and any artifact I/O error) in the final machine report.
        if (FinalJsonPath is { } path) Guard(() => File.WriteAllText(path, JsonSerializer.Serialize(FinalReport(), ReadableJsonOptions), new UTF8Encoding(false)));
    });

    private object FinalReport() => new
    {
        schema = Schema, schemaVersion = SchemaVersion, taskId = TaskId, startedUtc, state,
        elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, phase, completedFrames, totalFrames = plan?.FrameCount,
        simulationTicks, lastCompletedFrame = lastCompleted, failureContext, metadata, exception = failure, secondaryErrors,
        progressEvents, progressEventsCapped = progressEvents >= MaximumProgressEvents,
        jsonLinesPath = JsonLinesPath, summaryPath = SummaryPath, finalJsonPath = FinalJsonPath, persistenceError = PersistenceError,
        clockContract = "outputIndex is zero-based; outputOrdinal is one-based. Simulation ticks are completed 1/120-second Advance calls since Seek, excluding pose initialization. Null means not observed."
    };

    private void Append(string eventType, object? detail)
    {
        var frame = eventType is "exception" or "failed" or "cancelled" ? failureContext : lastCompleted;
        var value = new
        {
            schema = Schema, schemaVersion = SchemaVersion, taskId = TaskId, sequence = ++sequence,
            timestampUtc = DateTimeOffset.UtcNow, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, eventType, phase,
            completedFrames = (long?)completedFrames, totalFrames = plan?.FrameCount,
            outputIndex = frame?.OutputIndex, outputOrdinal = frame?.OutputOrdinal,
            presentationTime = frame?.PresentationTime, requestedSourceFrame = frame?.RequestedSourceFrame,
            sampledSourceFrame = frame?.SampledSourceFrame, simulationTicks,
            attemptingSimulationTick = frame?.AttemptingSimulationTick,
            backendId = metadata.GetValueOrDefault("rendererBackendId"), detail
        };
        Guard(() => writer?.WriteLine(JsonSerializer.Serialize(value, JsonOptions)));
    }

    private string BuildSummary()
    {
        var text = new StringBuilder();
        text.AppendLine("MDA export diagnostic report / 导出诊断报告");
        text.AppendLine("Schema: " + Schema + " v" + SchemaVersion + "    Task ID: " + TaskId);
        text.AppendLine("Started UTC: " + startedUtc.ToString("O", CultureInfo.InvariantCulture));
        text.AppendLine("State: " + state + "    Phase: " + phase + "    Elapsed: " + elapsed.Elapsed.ToString("c", CultureInfo.InvariantCulture));
        text.AppendLine("Completed output frames: " + completedFrames + " / " + (plan?.FrameCount.ToString(CultureInfo.InvariantCulture) ?? "not observed"));
        text.AppendLine("Completed 120Hz simulation ticks since Seek: " + (simulationTicks?.ToString(CultureInfo.InvariantCulture) ?? "not observed"));
        text.AppendLine("Output index is zero-based; ordinal is one-based. Null means not observed. Attempted ticks are not completed ticks.");
        text.AppendLine("JSONL events: " + (JsonLinesPath ?? "unavailable"));
        text.AppendLine("Final JSON: " + (FinalJsonPath ?? "unavailable"));
        text.AppendLine("Readable report: " + (SummaryPath ?? (directory is null ? "unavailable" : Path.Combine(directory, "summary.txt"))));
        if (PersistenceError is { } problem) text.AppendLine("Log storage error (export continued): " + problem);
        text.AppendLine(); text.AppendLine("Last completed frame:");
        text.AppendLine(JsonSerializer.Serialize(lastCompleted, ReadableJsonOptions));
        text.AppendLine("Failure / cancellation attempt:"); text.AppendLine(JsonSerializer.Serialize(failureContext, ReadableJsonOptions));
        text.AppendLine("Environment, project identity and export configuration:"); text.AppendLine(JsonSerializer.Serialize(metadata, ReadableJsonOptions));
        if (failure is not null) { text.AppendLine("Original exception, stack and bounded Exception.Data:"); ExportDiagnosticException.AppendReadable(text, failure); }
        if (secondaryErrors.Count != 0) { text.AppendLine("Secondary cleanup errors:"); text.AppendLine(JsonSerializer.Serialize(secondaryErrors, ReadableJsonOptions)); }
        return text.ToString();
    }

    private void WriteArtifact(string name, string content, Action<string> published)
    {
        if (directory is null) return;
        Guard(() => { string path = Path.Combine(directory, name); File.WriteAllText(path, content, new UTF8Encoding(false)); published(path); });
    }

    private void Guard(Action action)
    {
        lock (gate)
        {
            try { action(); }
            catch (Exception error)
            {
                PersistenceError ??= error.GetType().Name + ": " + error.Message;
                try { writer?.Dispose(); } catch { }
                writer = null;
            }
        }
    }

    private static object? FileIdentity(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = input.Length;
            var lastWriteBeforeUtc = File.GetLastWriteTimeUtc(path);
            string sha256 = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            var lastWriteUtc = File.GetLastWriteTimeUtc(path);
            return new { path, length, sha256, lastWriteUtc,
                unchangedDuringRead = input.Length == length && lastWriteUtc == lastWriteBeforeUtc, error = (string?)null };
        }
        catch (Exception error) { return new { path, error = error.GetType().Name + ": " + error.Message }; }
    }

    private static object? TryData(Exception error, string key)
    { try { return error.Data[key]; } catch { return null; } }

    public void Dispose() => Guard(() =>
    {
        if (disposed) return;
        disposed = true;
        try { writer?.Dispose(); } finally { writer = null; }
    });
}
