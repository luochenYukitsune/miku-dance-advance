using System.Text.Json;
using System.Numerics;
using System.Security.Cryptography;
using Mmd.Modern.App;
using Mmd.Modern.Application;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class ExportDiagnosticSessionTests
{
    [Fact]
    public void MachineAndReadableReportsRetainExactFailureBoundaryAndSecondaryErrors()
    {
        string root = TemporaryRoot();
        var plan = new OfflineFramePlan(4, 3, new(60), 0, 4706, includeEndFrame: false);
        using var log = new ExportDiagnosticSession(plan, new Dictionary<string, object?> { ["fixture"] = true }, root);
        log.ReportProgress(new(ExportTaskState.Running, 9356, 9412, 18710, plan.GetSample(9355).PresentationTime));
        var original = new ArgumentException("Physics pose rotation is not orthonormal.", "samples");
        original.Data[OfflineFrameFailureContext.DataKey] = new OfflineFrameFailureContext("simulation", 9356,
            plan.GetSample(9356), 18711, 18712, 0, 4677.75);
        original.Data["ViewOutputCleanupErrors"] = new[] { new IOException("secondary cleanup") };
        original.Data["boneIndex"] = 7;
        log.Finish(ExportTaskState.Failed, original);

        Assert.NotNull(log.JsonLinesPath); Assert.NotNull(log.SummaryPath); Assert.NotNull(log.FinalJsonPath);
        using var final = JsonDocument.Parse(File.ReadAllText(log.FinalJsonPath!));
        Assert.Equal(1, final.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(log.TaskId, final.RootElement.GetProperty("taskId").GetString());
        var failure = final.RootElement.GetProperty("failureContext");
        Assert.Equal(9356, failure.GetProperty("outputIndex").GetInt64());
        Assert.Equal(9357, failure.GetProperty("outputOrdinal").GetInt64());
        Assert.Equal("4678", failure.GetProperty("requestedSourceFrame").GetProperty("numerator").GetString());
        Assert.Equal(18711, failure.GetProperty("simulationTicks").GetInt64());
        Assert.Equal(18712, failure.GetProperty("attemptingSimulationTick").GetInt64());
        Assert.Contains("secondary cleanup", log.ReadableSummary);
        Assert.Contains("boneIndex", log.ReadableSummary);
        Assert.Contains("9356", File.ReadAllText(log.SummaryPath!));
        var events = File.ReadAllLines(log.JsonLinesPath!).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.All(events, item => Assert.Equal(log.TaskId, item.RootElement.GetProperty("taskId").GetString()));
            Assert.Equal(JsonValueKind.Null, events[0].RootElement.GetProperty("simulationTicks").ValueKind);
            Assert.Equal("failed", events[^1].RootElement.GetProperty("eventType").GetString());
        }
        finally { foreach (var item in events) item.Dispose(); }
    }

    [Fact]
    public void StorageFailureKeepsAnInMemoryReportAndNeverBecomesAnExportFailure()
    {
        string root = TemporaryRoot(); Directory.CreateDirectory(root);
        string blocked = Path.Combine(root, "file-instead-of-directory"); File.WriteAllText(blocked, "fixture");
        using var log = new ExportDiagnosticSession(new(4, 3, new(30), 0, 0), new Dictionary<string, object?>(), blocked);
        log.ReportPhase("renderer.create");
        log.Finish(ExportTaskState.Completed);
        Assert.Null(log.JsonLinesPath); Assert.Null(log.SummaryPath); Assert.Null(log.FinalJsonPath);
        Assert.NotNull(log.PersistenceError);
        Assert.Contains("Completed", log.ReadableSummary);
        Assert.Contains("fixture", File.ReadAllText(blocked));
    }

    [Fact]
    public void ProgressIsThrottledButTheFinalBoundaryIsNeverLost()
    {
        using var log = new ExportDiagnosticSession(new(4, 3, new(60), 0, 5000, includeEndFrame: false),
            new Dictionary<string, object?>(), TemporaryRoot());
        for (int i = 1; i <= 10000; i++) log.ReportProgress(new(ExportTaskState.Running, i, 10000, i * 2, new(i, 60)));
        log.Finish(ExportTaskState.Completed);
        Assert.True(File.ReadLines(log.JsonLinesPath!).Count() < 30);
        using var report = JsonDocument.Parse(File.ReadAllText(log.FinalJsonPath!));
        Assert.Equal(10000, report.RootElement.GetProperty("completedFrames").GetInt64());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("failureContext").ValueKind);
    }

    [Fact]
    public void CancelledSessionHasItsOwnFilesAndDoesNotClaimAnUnattemptedFirstFrame()
    {
        using var log = new ExportDiagnosticSession(new(4, 3, new(30), 0, 1), new Dictionary<string, object?>(), TemporaryRoot());
        var resources = new RenderResourceSnapshot { SampledAtUtc = DateTimeOffset.UtcNow,
            ProcessCpuPercent = 2.5, ProcessWorkingSetBytes = 12345, GpuUtilizationPercent = null,
            VideoMemoryUnavailableReason = "fixture: no GPU provider" };
        log.ReportResources(resources);
        log.Finish(ExportTaskState.Cancelled, new OperationCanceledException("fixture cancellation"));
        using var report = JsonDocument.Parse(File.ReadAllText(log.FinalJsonPath!));
        Assert.Equal("Cancelled", report.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("lastCompletedFrame").ValueKind);
        Assert.Contains("fixture cancellation", log.ReadableSummary);
        var observed = report.RootElement.GetProperty("metadata").GetProperty("lastResourceObservation");
        Assert.Equal(2.5, observed.GetProperty("processCpuPercent").GetDouble());
        Assert.Equal(JsonValueKind.Null, observed.GetProperty("gpuUtilizationPercent").ValueKind);
        Assert.Equal("fixture: no GPU provider", observed.GetProperty("videoMemoryUnavailableReason").GetString());
    }

    [Fact]
    public void ExceptionDataIsBoundedRedactedAndDoesNotCallUnknownObjectsToString()
    {
        using var log = new ExportDiagnosticSession(new(4, 3, new(30), 0, 0), new Dictionary<string, object?>(), TemporaryRoot());
        var error = new IOException("outer", new InvalidOperationException("inner"));
        error.Data["apiToken"] = "must-not-be-logged";
        error.Data["large"] = new string('x', 100000);
        error.Data["hostile"] = new HostileValue();
        var matrix = Matrix4x4.Identity; matrix.M11 = float.NaN;
        error.Data["rotation"] = matrix;
        error.Data["self"] = error;
        log.Finish(ExportTaskState.Failed, error);
        Assert.DoesNotContain("must-not-be-logged", log.ReadableSummary);
        Assert.Contains("inner", log.ReadableSummary);
        Assert.Contains("HostileValue", log.ReadableSummary);
        Assert.Contains("NaN", log.ReadableSummary);
        Assert.Contains("row-major", log.ReadableSummary);
        Assert.True(log.ReadableSummary.Length < 150000);
    }

    [Fact]
    public void NativeIdentityUsesOnlyObservedAllowlistedPathsAndAddsRuntimeModulesAtTheEnd()
    {
        string root = TemporaryRoot(); Directory.CreateDirectory(root);
        string bullet = Path.Combine(root, "fixture-bullet.dll"); File.WriteAllText(bullet, "fixture bullet bytes; never loaded");
        string graphics = Path.Combine(root, "fixture-graphics.dll"); File.WriteAllText(graphics, "fixture graphics bytes; never loaded");
        int observations = 0;
        IReadOnlyList<ExportDiagnosticNativeModulePath> Inventory() => observations++ == 0
            ? [new("mmd_bullet.dll", bullet), new("not-allowlisted.dll", "must-not-be-read")]
            : [new("mmd_bullet.dll", bullet), new("d3d12.dll", graphics)];
        using var log = new ExportDiagnosticSession(new(4, 3, new(30), 0, 0),
            new Dictionary<string, object?> { ["fixtureModuleInventory"] = true }, root, Inventory);
        log.CaptureFileIdentities(); log.Finish(ExportTaskState.Completed);
        using var report = JsonDocument.Parse(File.ReadAllText(log.FinalJsonPath!));
        var metadata = report.RootElement.GetProperty("metadata");
        Assert.Single(metadata.GetProperty("nativeModulesAtStart").GetProperty("modules").EnumerateArray());
        var finalModules = metadata.GetProperty("nativeModulesAtFinish").GetProperty("modules");
        Assert.Equal(2, finalModules.GetArrayLength());
        Assert.Equal(graphics, finalModules[1].GetProperty("path").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(graphics))).ToLowerInvariant(),
            finalModules[1].GetProperty("file").GetProperty("sha256").GetString());
        Assert.DoesNotContain("must-not-be-read", log.ReadableSummary);
        Assert.Equal(2, observations);
    }

    [Fact]
    public void NativeEnumerationAndMissingModuleFileFailuresRemainDiagnosticOnly()
    {
        string root = TemporaryRoot(); int observations = 0;
        IReadOnlyList<ExportDiagnosticNativeModulePath> Inventory()
        {
            if (observations++ == 0) throw new UnauthorizedAccessException("fixture module enumeration denied");
            return [new("mmd_bullet.dll", Path.Combine(root, "missing.dll")), new("dxgi.dll", null, "fixture path unavailable")];
        }
        using var log = new ExportDiagnosticSession(new(4, 3, new(30), 0, 0), new Dictionary<string, object?>(), root, Inventory);
        log.CaptureFileIdentities(); log.Finish(ExportTaskState.Completed);
        using var report = JsonDocument.Parse(File.ReadAllText(log.FinalJsonPath!));
        Assert.Equal("Completed", report.RootElement.GetProperty("state").GetString());
        var metadata = report.RootElement.GetProperty("metadata");
        Assert.Contains("fixture module enumeration denied", metadata.GetProperty("nativeModulesAtStart").GetProperty("probeError").GetString());
        var modules = metadata.GetProperty("nativeModulesAtFinish").GetProperty("modules");
        Assert.Contains("FileNotFoundException", modules[0].GetProperty("file").GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, modules[1].GetProperty("path").ValueKind);
        Assert.Null(log.PersistenceError);
    }

    private sealed class HostileValue { public override string ToString() => throw new InvalidOperationException("Do not invoke"); }
    private static string TemporaryRoot() => Path.Combine(Path.GetTempPath(), "mda-export-diagnostics-" + Guid.NewGuid().ToString("N"));
}
