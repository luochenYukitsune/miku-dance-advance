using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Mmd.Modern.Application;

namespace Mmd.Modern.App;

/// <summary>Explicitly bounded diagnostic projection. Unknown objects are identified by type and are
/// never serialized through reflection or ToString, so Exception.Data cannot become a scene dump.</summary>
internal sealed record ExportDiagnosticException(string Type, string Message, int? HResult, string? StackTrace,
    IReadOnlyDictionary<string, object?> ExceptionData, IReadOnlyList<ExportDiagnosticException> InnerExceptions)
{
    internal static ExportDiagnosticException Capture(Exception error) => Capture(error, 0,
        new HashSet<Exception>(ReferenceEqualityComparer.Instance), new CaptureBudget());

    internal static string Readable(Exception error)
    {
        try
        {
            var text = new StringBuilder(); AppendReadable(text, Capture(error)); return text.ToString();
        }
        catch { return error.GetType().FullName ?? "Exception"; }
    }

    internal static void AppendReadable(StringBuilder text, ExportDiagnosticException value)
    {
        text.AppendLine(value.Type + ": " + value.Message);
        text.AppendLine("HResult: " + (value.HResult?.ToString(CultureInfo.InvariantCulture) ?? "not recorded"));
        if (!string.IsNullOrEmpty(value.StackTrace)) text.AppendLine(value.StackTrace);
        if (value.ExceptionData.Count != 0)
        {
            text.AppendLine("Exception.Data:");
            text.AppendLine(JsonSerializer.Serialize(value.ExceptionData, new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 32 }));
        }
        foreach (var inner in value.InnerExceptions) { text.AppendLine("Inner exception:"); AppendReadable(text, inner); }
    }

    private static ExportDiagnosticException Capture(Exception error, int depth, HashSet<Exception> seen, CaptureBudget budget)
    {
        if (!budget.TakeException()) return new(error.GetType().FullName ?? "Exception", "[maximum exception count]", null, null,
            new Dictionary<string, object?>(), []);
        bool repeat = !seen.Add(error);
        string message = budget.Text(error.Message, 4096);
        string? stack = repeat ? "[exception already recorded]"
            : error.StackTrace is { } originalStack ? budget.Text(originalStack, 24576) : null;
        var data = new Dictionary<string, object?>();
        if (!repeat && depth < 4)
        {
            try
            {
                int count = 0;
                foreach (DictionaryEntry item in error.Data)
                {
                    if (count++ >= 24) { data["$truncated"] = "More than 24 data entries."; break; }
                    string key = item.Key is string text ? Limit(text, 160) : item.Key.GetType().FullName ?? "unknown-key";
                    data[key] = Sensitive(key) ? "[redacted]" : Value(item.Value, depth + 1, seen, budget);
                }
            }
            catch (Exception problem) { data["$unavailable"] = problem.GetType().FullName; }
        }
        var inner = new List<ExportDiagnosticException>();
        if (!repeat && depth < 4)
        {
            if (error is AggregateException aggregate)
                foreach (var child in aggregate.InnerExceptions.Take(8)) inner.Add(Capture(child, depth + 1, seen, budget));
            else if (error.InnerException is { } child) inner.Add(Capture(child, depth + 1, seen, budget));
        }
        return new(error.GetType().FullName ?? error.GetType().Name, message, error.HResult, stack, data, inner);
    }

    private static object? Value(object? value, int depth, HashSet<Exception> seen, CaptureBudget budget)
    {
        if (value is null) return null;
        if (!budget.TakeValue()) return "[maximum diagnostic values]";
        if (depth > 4) return "[maximum diagnostic depth]";
        if (value is string text) return budget.Text(text, 4096);
        if (value is Exception error) return Capture(error, depth, seen, budget);
        if (value is OfflineFrameFailureContext context) return ExportDiagnosticFrame.From(context);
        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal) return value;
        if (value is double number) return double.IsFinite(number) ? number : number.ToString(CultureInfo.InvariantCulture);
        if (value is float single) return float.IsFinite(single) ? single : single.ToString(CultureInfo.InvariantCulture);
        if (value is Guid or DateTime or DateTimeOffset or TimeSpan or Enum) return Convert.ToString(value, CultureInfo.InvariantCulture);
        if (value is Vector2 v2) return Numbers([v2.X, v2.Y]);
        if (value is Vector3 v3) return Numbers([v3.X, v3.Y, v3.Z]);
        if (value is Vector4 v4) return Numbers([v4.X, v4.Y, v4.Z, v4.W]);
        if (value is Quaternion q) return Numbers([q.X, q.Y, q.Z, q.W]);
        if (value is Matrix4x4 m) return new { layout = "row-major m11..m44",
            values = Numbers([m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44]) };
        if (value is IEnumerable sequence)
        {
            var values = new List<object?>();
            try
            {
                foreach (object? item in sequence)
                {
                    if (values.Count == 8) { values.Add("[more items omitted]"); break; }
                    values.Add(Value(item, depth + 1, seen, budget));
                }
            }
            catch (Exception problem) { values.Add("[unavailable: " + problem.GetType().Name + "]"); }
            return values;
        }
        return "[" + value.GetType().FullName + "]";
    }

    private static object[] Numbers(float[] values) => values.Select(value => float.IsFinite(value)
        ? (object)value : value.ToString(CultureInfo.InvariantCulture)).ToArray();

    private sealed class CaptureBudget
    {
        private int characters = 96 * 1024, exceptions = 16, values = 128;
        internal bool TakeException() => exceptions-- > 0;
        internal bool TakeValue() => values-- > 0;
        internal string Text(string? value, int maximum)
        {
            if (characters <= 0) return "[maximum diagnostic text]";
            string result = Limit(value, Math.Min(maximum, characters));
            characters -= result.Length; return result;
        }
    }

    private static bool Sensitive(string key) => key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("credential", StringComparison.OrdinalIgnoreCase) || key.Contains("authorization", StringComparison.OrdinalIgnoreCase);
    private static string Limit(string? value, int maximum) => value is null ? "" : value.Length <= maximum ? value : value[..maximum] + " [truncated]";
}

internal sealed record ExportDiagnosticRational(string Numerator, string Denominator)
{
    internal static ExportDiagnosticRational? From(OfflineFrameTime? time) => time is { } value && value.Denominator > 0
        ? new(value.Numerator.ToString(CultureInfo.InvariantCulture), value.Denominator.ToString(CultureInfo.InvariantCulture)) : null;
}

internal sealed record ExportDiagnosticFrame(string Phase, long? OutputIndex, long? OutputOrdinal,
    ExportDiagnosticRational? PresentationTime, ExportDiagnosticRational? RequestedSourceFrame,
    ExportDiagnosticRational? SampledSourceFrame, long? SimulationTicks, long? AttemptingSimulationTick,
    ExportDiagnosticRational? AttemptingSimulationSourceFrame, double? ObservedSourceFrame)
{
    internal static ExportDiagnosticFrame From(OfflineFrameFailureContext value) => new(value.Phase,
        value.Sample?.Index, value.Sample?.Index + 1, ExportDiagnosticRational.From(value.Sample?.PresentationTime),
        ExportDiagnosticRational.From(value.Sample?.RequestedSourceFrame), ExportDiagnosticRational.From(value.Sample?.SampledSourceFrame),
        value.SimulationTicks, value.AttemptingSimulationTick, value.AttemptingSimulationTick is { } attempt
            ? ExportDiagnosticRational.From(new OfflineFrameTime((System.Numerics.BigInteger)value.SimulationStartFrame * 4 + attempt, 4)) : null,
        value.ObservedSourceFrame);

    internal static ExportDiagnosticFrame Completed(OfflineFrameSample sample, long ticks) => new("frame.completed",
        sample.Index, sample.Index + 1, ExportDiagnosticRational.From(sample.PresentationTime),
        ExportDiagnosticRational.From(sample.RequestedSourceFrame), ExportDiagnosticRational.From(sample.SampledSourceFrame), ticks, null, null, null);
}
