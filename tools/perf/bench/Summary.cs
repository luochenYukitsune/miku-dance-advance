using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PerfBench;

/// <summary>Medians across rounds (with min/max spread) and baseline/candidate deltas. Raw values stay in the JSON.</summary>
static class Summary
{
    sealed record Stat(double Median, double Min, double Max, int N, double[] Values)
    {
        public double Range => Max - Min;
        public double SpreadPercent => Median == 0 ? 0 : Range / Math.Abs(Median) * 100;
    }

    public static void Summarize(string directory, string outBase)
    {
        var summary = BuildSummary(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outBase))!);
        File.WriteAllText(outBase + ".json", summary.ToJsonString(Output.Json));
        File.WriteAllText(outBase + ".md", Markdown(summary));
        Console.WriteLine($"{outBase}.md");
    }

    public static JsonObject BuildSummary(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Directory not found: {directory}");
        var runs = new List<JsonObject>();
        JsonNode? machine = null;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("summary", StringComparison.Ordinal) || name.StartsWith("compare", StringComparison.Ordinal)) continue;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject node) continue;
            string? kind = (string?)node["kind"];
            if (kind == "machine") { machine ??= node; continue; }
            if (kind is "play" or "open" or "export") { node["file"] = Path.GetRelativePath(directory, path); runs.Add(node); }
        }
        var groups = new JsonArray();
        foreach (var group in runs.GroupBy(GroupId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ok = group.Where(r => (string?)r["status"] == "ok").ToArray();
            var metrics = new JsonObject();
            foreach (string name in ok.SelectMany(r => r["metrics"]?.AsObject().Select(p => p.Key) ?? []).Distinct().Order(StringComparer.Ordinal))
            {
                var values = ok.Select(r => r["metrics"]?[name]).Where(v => v is not null).Select(v => v!.GetValue<double>())
                    .Where(double.IsFinite).ToArray();
                if (values.Length == 0) continue;
                var stat = Describe(values);
                metrics[name] = new JsonObject { ["median"] = stat.Median, ["min"] = stat.Min, ["max"] = stat.Max, ["n"] = stat.N,
                    ["values"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };
            }
            var first = group.First();
            groups.Add(new JsonObject
            {
                ["id"] = group.Key, ["kind"] = (string?)first["kind"], ["key"] = (string?)first["key"],
                ["cache"] = (string?)first["tags"]?["cache"] ?? "", ["config"] = ok.FirstOrDefault()?["config"]?.DeepClone(),
                ["ok"] = ok.Length, ["failed"] = group.Count(r => (string?)r["status"] == "failed"),
                ["unsupported"] = group.Count(r => (string?)r["status"] == "unsupported"),
                ["unsupportedReason"] = group.Select(r => (string?)r["giUnavailableReason"]).FirstOrDefault(r => r is not null),
                ["poseNotAdvanced"] = ok.Count(r => r["poseAdvanced"] is JsonValue v && !v.GetValue<bool>()),
                ["giEffective"] = new JsonArray(ok.Select(r => (string?)r["giEffective"]).Where(v => v is not null).Distinct().Select(v => (JsonNode)v!).ToArray()),
                ["gpuTimingSources"] = new JsonArray(ok.Select(r => (string?)r["gpuTimingSource"]).Where(v => v is not null).Distinct().Select(v => (JsonNode)v!).ToArray()),
                ["files"] = new JsonArray(group.Select(r => (JsonNode)(string)r["file"]!).ToArray()),
                ["metrics"] = metrics,
            });
        }
        return new JsonObject
        {
            ["schema"] = Output.Schema, ["generatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["source"] = directory,
            ["sourceCommits"] = new JsonArray(runs.Select(r => (string?)r["build"]?["sourceCommit"]).Where(c => c is not null).Distinct().Select(c => (JsonNode)c!).ToArray()),
            ["machine"] = machine?.DeepClone(), ["groups"] = groups,
        };
    }

    static string GroupId(JsonObject run) => $"{(string?)run["kind"]}|{(string?)run["key"]}|{(string?)run["tags"]?["cache"] ?? ""}";

    static Stat Describe(double[] values)
    {
        var sorted = values.Order().ToArray();
        return new(Playback.Percentile(sorted, .5), sorted[0], sorted[^1], sorted.Length, values);
    }

    static Stat? Get(JsonNode group, string metric) =>
        group["metrics"]?[metric] is JsonObject m ? new((double)m["median"]!, (double)m["min"]!, (double)m["max"]!, (int)m["n"]!, []) : null;

    static string F(double value, int digits = 2) => double.IsFinite(value) ? value.ToString("F" + digits, CultureInfo.InvariantCulture) : "-";
    static string M(JsonNode group, string metric, int digits = 2, double scale = 1) => Get(group, metric) is { } s ? F(s.Median * scale, digits) : "-";
    static string Triple(JsonNode g, string prefix) => $"{M(g, prefix + ".p50")} / {M(g, prefix + ".p95")} / {M(g, prefix + ".p99")}";
    static string Spread(JsonNode g, string metric) => Get(g, metric) is { N: > 1 } s ? F(s.SpreadPercent, 1) + "%" : "-";

    static string Markdown(JsonObject summary)
    {
        var text = new StringBuilder();
        var machine = summary["machine"];
        text.AppendLine("# PerfBench 汇总").AppendLine();
        text.AppendLine($"- 生成时间（UTC）：{(string?)summary["generatedUtc"]}；源提交：{string.Join(", ", summary["sourceCommits"]!.AsArray().Select(c => (string?)c))}");
        if (machine is not null)
        {
            text.AppendLine($"- CPU：{(string?)machine["cpu"]}，{(int?)machine["logicalProcessors"]} 个逻辑处理器；核心效率类：" +
                string.Join("，", machine["cores"]?.AsArray().Select(c => $"类 {(int?)c!["efficiencyClass"]} × {(int?)c["cores"]} 核 / {(int?)c["logicalProcessors"]} 线程") ?? []));
            text.AppendLine($"- GPU：{(string?)machine["gpu"]?["adapter"]}，驱动 {(string?)machine["gpu"]?["driver"]?["version"]}；系统：{(string?)machine["os"]}；{(string?)machine["dotnet"]}");
            text.AppendLine($"- 电源：{(string?)machine["power"]?["acLine"]}；{(string?)machine["power"]?["scheme"]}");
        }
        text.AppendLine("- 每格为各轮中位数；“波动”为 (最大−最小)/中位数。原始逐帧数据见 runs/*.json。").AppendLine();
        var groups = summary["groups"]!.AsArray().Select(g => g!).ToArray();

        var play = groups.Where(g => (string?)g["kind"] == "play").ToArray();
        if (play.Length > 0)
        {
            text.AppendLine("## 播放（无窗口，保留 GPU 图像，不含 Present）").AppendLine();
            text.AppendLine("| 场景 | 轮 | fps | 完成帧 P50/P95/P99 ms | 姿态求值 P50/P95/P99 ms | 录制调用 P50/P95 ms | 录制 CPU P50 ms | GPU/围栏 P50/P95 ms | 分配 MiB/帧 | Gen0/1/2 每百帧 | 打开到首帧 ms | 跳帧 P50 ms | 波动 fps / 帧P95 |");
            text.AppendLine("|---|---:|---:|---|---|---|---:|---|---:|---|---:|---:|---|");
            foreach (var g in play)
            {
                string gpuPair = Get(g, "play.stat.GpuFrameMilliseconds.p50") is not null
                    ? $"{M(g, "play.stat.GpuFrameMilliseconds.p50")} / {M(g, "play.stat.GpuFrameMilliseconds.p95")} (ts)"
                    : Get(g, "play.stat.GpuMilliseconds.p50") is not null
                    ? $"{M(g, "play.stat.GpuMilliseconds.p50")} / {M(g, "play.stat.GpuMilliseconds.p95")} (ts)"
                    : $"{M(g, "serial.submitToFenceMs.p50")} / {M(g, "serial.submitToFenceMs.p95")}";
                text.AppendLine($"| {(string?)g["key"]} | {(int?)g["ok"]} | {M(g, "play.fps", 1)} | {Triple(g, "play.frameMs")} | {Triple(g, "play.poseMs")} | " +
                    $"{M(g, "play.renderCallMs.p50")} / {M(g, "play.renderCallMs.p95")} | {M(g, "play.stat.RecordCpuMilliseconds.p50")} | " +
                    $"{gpuPair} | {M(g, "play.gc.allocBytesPerFrame", 2, 1d / 1048576)} | " +
                    $"{M(g, "play.gc.gen0Per100Frames", 1)} / {M(g, "play.gc.gen1Per100Frames", 1)} / {M(g, "play.gc.gen2Per100Frames", 1)} | {M(g, "open.toFirstFrameMs", 0)} | {M(g, "seek.ms.p50", 1)} | " +
                    $"{Spread(g, "play.fps")} / {Spread(g, "play.frameMs.p95")} |");
            }
            text.AppendLine();
        }
        var open = groups.Where(g => (string?)g["kind"] == "open").ToArray();
        if (open.Length > 0)
        {
            text.AppendLine("## 打开到首帧（新进程）").AppendLine();
            text.AppendLine("| 场景 | 缓存 | 轮 | 打开到首帧 ms | 进程启动到首帧 ms | 载入模型 ms | 会话与物理初始化 ms | 建设备 ms | 首帧 ms | 着色器编译 | 管线创建 | 波动 |");
            text.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var g in open)
                text.AppendLine($"| {(string?)g["key"]} | {(string?)g["cache"]} | {(int?)g["ok"]} | {M(g, "open.toFirstFrameMs", 0)} | {M(g, "open.processToFirstFrameMs", 0)} | " +
                    $"{M(g, "open.loadMs", 0)} | {M(g, "open.sessionMs", 0)} | {M(g, "open.deviceMs", 0)} | {M(g, "open.firstFrameMs", 0)} | " +
                    $"{M(g, "open.stat.ShaderCompilations", 0)} | {M(g, "open.stat.PipelineCreations", 0)} | {Spread(g, "open.toFirstFrameMs")} |");
            text.AppendLine();
        }
        var export = groups.Where(g => (string?)g["kind"] == "export").ToArray();
        if (export.Length > 0)
        {
            text.AppendLine("## 导出（ExportCoordinator → D3D12ViewRenderer → FFmpeg HEVC 软件编码）").AppendLine();
            text.AppendLine("| 场景 | 轮 | 首次导出 s | 首次 帧/秒 | 再次导出 s | 再次 帧/秒 | 再次 稳态 帧/秒 | 再次 分配 MiB/帧 | 波动 稳态 |");
            text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var g in export)
                text.AppendLine($"| {(string?)g["key"]} | {(int?)g["ok"]} | {M(g, "export.run0.totalMs", 2, .001)} | {M(g, "export.run0.fps", 1)} | " +
                    $"{M(g, "export.run1.totalMs", 2, .001)} | {M(g, "export.run1.fps", 1)} | {M(g, "export.run1.steadyFps", 1)} | " +
                    $"{M(g, "export.run1.gc.allocBytesPerFrame", 2, 1d / 1048576)} | {Spread(g, "export.run1.steadyFps")} |");
            text.AppendLine();
        }
        var notes = groups.Where(g => (int)g["failed"]! > 0 || (int)g["unsupported"]! > 0 || (int)g["poseNotAdvanced"]! > 0).ToArray();
        if (notes.Length > 0)
        {
            text.AppendLine("## 失败、不支持与异常").AppendLine();
            foreach (var g in notes)
                text.AppendLine($"- {(string?)g["id"]}：失败 {(int)g["failed"]!}，不支持 {(int)g["unsupported"]!}{((string?)g["unsupportedReason"] is { } r ? "（" + r + "）" : "")}，姿态未变化 {(int)g["poseNotAdvanced"]!}");
            text.AppendLine();
        }
        return text.ToString();
    }

    // Metric, lower-is-better.
    static readonly (string Name, string Label, bool LowerIsBetter)[] Compared =
    [
        ("play.fps", "播放 fps", false), ("play.frameMs.p50", "完成帧 P50", true), ("play.frameMs.p95", "完成帧 P95", true),
        ("play.frameMs.p99", "完成帧 P99", true), ("play.poseMs.p50", "姿态 P50", true), ("play.poseMs.p95", "姿态 P95", true),
        ("play.poseMs.p99", "姿态 P99", true), ("play.renderCallMs.p50", "录制调用 P50", true), ("play.renderCallMs.p95", "录制调用 P95", true),
        ("play.stat.RecordCpuMilliseconds.p50", "录制 CPU P50", true),
        ("play.stat.GpuFrameMilliseconds.p50", "GPU 时间戳 P50", true), ("play.stat.GpuMilliseconds.p50", "GPU 时间戳 P50", true),
        ("serial.submitToFenceMs.p50", "提交到围栏 P50", true), ("serial.submitToFenceMs.p95", "提交到围栏 P95", true),
        ("play.stat.FrameOwnedAllocations.p50", "每帧新建资源数 P50", true),
        ("play.gc.allocBytesPerFrame", "分配 字节/帧", true),
        ("play.gc.gen0Per100Frames", "Gen0/百帧", true), ("play.gc.gen1Per100Frames", "Gen1/百帧", true), ("play.gc.gen2Per100Frames", "Gen2/百帧", true),
        ("open.toFirstFrameMs", "打开到首帧", true), ("open.processToFirstFrameMs", "进程启动到首帧", true), ("seek.ms.p50", "跳帧 P50", true),
        ("export.run0.totalMs", "首次导出 ms", true), ("export.run0.fps", "首次导出 帧/秒", false),
        ("export.run1.totalMs", "再次导出 ms", true), ("export.run1.fps", "再次导出 帧/秒", false), ("export.run1.steadyFps", "导出稳态 帧/秒", false),
        ("export.run1.gc.allocBytesPerFrame", "导出分配 字节/帧", true),
    ];

    static JsonObject LoadSummaryInput(string path, out string resolvedLabel)
    {
        string full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            string summaryFile = Path.Combine(full, "summary.json");
            if (File.Exists(summaryFile)) { resolvedLabel = summaryFile; return JsonNode.Parse(File.ReadAllText(summaryFile))!.AsObject(); }
            resolvedLabel = full;
            return BuildSummary(full);
        }
        resolvedLabel = full;
        return JsonNode.Parse(File.ReadAllText(full))!.AsObject();
    }

    public static void Compare(string basePath, string candPath, string outBase, bool printMarkdown = false)
    {
        var baseline = LoadSummaryInput(basePath, out string baseLabel);
        var candidate = LoadSummaryInput(candPath, out string candLabel);
        var cand = candidate["groups"]!.AsArray().ToDictionary(g => (string)g!["id"]!, g => g!);
        var rows = new JsonArray();
        var text = new StringBuilder();
        text.AppendLine("# PerfBench 对照").AppendLine();
        text.AppendLine($"- 基线：{baseLabel}（{string.Join(", ", baseline["sourceCommits"]!.AsArray().Select(c => (string?)c))}）");
        text.AppendLine($"- 候选：{candLabel}（{string.Join(", ", candidate["sourceCommits"]!.AsArray().Select(c => (string?)c))}）");
        text.AppendLine("- 差值 = 候选中位数相对基线中位数的变化。仅当差值绝对值超过两边各自的轮间极差（最大−最小）时才判为“改善”或“变差”，否则记“波动内”；单轮记“样本不足”。").AppendLine();
        text.AppendLine("| 组 | 指标 | 基线 | 候选 | 差值 | 判定 |").AppendLine("|---|---|---:|---:|---:|---|");
        foreach (var b in baseline["groups"]!.AsArray().Select(g => g!))
        {
            string id = (string)b["id"]!;
            if (!cand.TryGetValue(id, out var c)) { text.AppendLine($"| {id} | （候选缺少此组） | | | | |"); continue; }
            foreach (var (name, label, lower) in Compared)
            {
                if (Get(b, name) is not { } x || Get(c, name) is not { } y) continue;
                double delta = y.Median - x.Median, percent = x.Median == 0 ? 0 : delta / Math.Abs(x.Median) * 100;
                string verdict = x.N < 2 || y.N < 2 ? "样本不足"
                    : Math.Abs(delta) <= Math.Max(x.Range, y.Range) ? "波动内"
                    : (delta < 0) == lower ? "改善" : "变差";
                rows.Add(new JsonObject { ["group"] = id, ["metric"] = name, ["base"] = x.Median, ["cand"] = y.Median, ["deltaPercent"] = percent,
                    ["baseRange"] = x.Range, ["candRange"] = y.Range, ["verdict"] = verdict });
                text.AppendLine($"| {id} | {label} | {F(x.Median)} | {F(y.Median)} | {(percent >= 0 ? "+" : "")}{F(percent, 1)}% | {verdict} |");
            }
        }
        outBase = Path.GetFullPath(outBase);
        Directory.CreateDirectory(Path.GetDirectoryName(outBase)!);
        File.WriteAllText(outBase + ".json", new JsonObject { ["base"] = baseLabel, ["cand"] = candLabel, ["rows"] = rows }.ToJsonString(Output.Json));
        string md = text.ToString();
        File.WriteAllText(outBase + ".md", md);
        if (printMarkdown) Console.WriteLine(md);
        Console.WriteLine($"{outBase}.md");
    }
}
