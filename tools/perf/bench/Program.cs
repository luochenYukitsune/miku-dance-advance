// PerfBench: repeatable headless benchmark for performance plan section 5.0. One configuration per process;
// Run-Matrix.ps1 interleaves processes, summarizes rounds and compares result directories. Usage: README.md.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PerfBench;

long processStart = Stopwatch.GetTimestamp();
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var cli = new Cli(args);
try
{
    switch (cli.Mode)
    {
        case "machine": Output.Write(cli.Required("out"), MachineInfo.Collect()); break;
        case "play": case "open": Output.Write(cli.Required("out"), Playback.Run(new BenchConfig(cli), cli.Mode == "open", processStart)); break;
        case "export": Output.Write(cli.Required("out"), await Export.RunAsync(new BenchConfig(cli), processStart)); break;
        case "summarize": Summary.Summarize(cli.Required("dir"), cli.Required("out")); break;
        case "compare": Summary.Compare(cli.Required("base"), cli.Required("cand"), cli.Required("out")); break;
        case "--compare":
        {
            if (cli.Positionals.Count != 2) throw new ArgumentException($"--compare expects <baselineDir> <candidateDir>.\n{Cli.Usage}");
            string baseDir = cli.Positionals[0], candDir = cli.Positionals[1];
            string outBase = cli.Optional("out") ?? Path.Combine(Path.GetFullPath(candDir), "compare");
            Summary.Compare(baseDir, candDir, outBase, printMarkdown: true);
            break;
        }
        default: throw new ArgumentException(Cli.Usage);
    }
    return 0;
}
catch (Exception error) when (cli.Values.ContainsKey("out") && cli.Mode is "play" or "open" or "export")
{
    // A failed configuration still leaves a result file, so the driver can report it and continue.
    Output.Write(cli.Required("out"), new { schema = Output.Schema, kind = cli.Mode, status = "failed", error = error.ToString(),
        tags = cli.Tags() });
    Console.Error.WriteLine(error);
    return 2;
}

namespace PerfBench
{
    sealed class Cli
    {
        public const string Usage = """
            PerfBench machine --out f.json
            PerfBench play|open|export --model a.pmx [--model b.pmx ...] --characters N [--stage s.pmx] [--vmd m.vmd ...] [--vmd-codepage 932|936]
                [--gi off|on] [--size 960x544] [--physics independent|shared|off] [--warmup 60] [--frames 300]
                [--serial-frames 60] [--seeks 3] [--export-frames 60] [--ffmpeg p --ffprobe p] [--keep-video]
                [--switch name=value ...] [--tag key=value ...] [--bone-audit matrices.bin] [--morph-audit morphs.bin] [--frame-timing] --out f.json
            PerfBench summarize --dir runs --out summary   (writes summary.json/.md)
            PerfBench compare --base a[/summary.json] --cand b[/summary.json] --out compare   (writes compare.json/.md)
            PerfBench --compare <baselineDir> <candidateDir> [--out compare]
            """;
        public string Mode { get; }
        public List<string> Positionals { get; } = [];
        public Dictionary<string, List<string>> Values { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);
        public Cli(string[] args)
        {
            if (args.Length == 0) throw new ArgumentException(Usage);
            Mode = args[0];
            int i = 1;
            if (Mode == "--compare")
            {
                while (i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal)) Positionals.Add(args[i++]);
            }
            for (; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected '{args[i]}'.\n{Usage}");
                string key = args[i][2..];
                if (key is "keep-video" or "frame-timing") { Flags.Add(key); continue; }
                if (i + 1 >= args.Length) throw new ArgumentException($"--{key} needs a value.");
                if (!Values.TryGetValue(key, out var list)) Values[key] = list = [];
                list.Add(args[++i]);
            }
        }
        public string? Optional(string key) => Values.TryGetValue(key, out var list) ? list[^1] : null;
        public string Required(string key) => Optional(key) ?? throw new ArgumentException($"--{key} is required.\n{Usage}");
        public int Int(string key, int fallback) => Optional(key) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
        public IReadOnlyList<string> All(string key) => Values.TryGetValue(key, out var list) ? list : [];
        public Dictionary<string, string> Tags() => All("tag").Select(Split).ToDictionary(p => p.Key, p => p.Value);
        public static KeyValuePair<string, string> Split(string pair)
        {
            int index = pair.IndexOf('=');
            if (index <= 0) throw new ArgumentException($"Expected key=value, got '{pair}'.");
            return new(pair[..index], pair[(index + 1)..]);
        }
    }

    static class Output
    {
        public const int Schema = 1;
        public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        public static void Write(string path, object data)
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(data, Json));
        }
    }
}
