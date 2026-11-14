using System.Numerics;
using System.Text.Json.Nodes;
using Mmd.Modern.App;
using Mmd.Modern.ViewOutput;
using Xunit;

namespace Mmd.Modern.Tests;

public sealed class OutputFrameTimingPreferencesTests
{
    [Fact]
    public void DurationBoundaryRoundTripsAndOldSettingsKeepTheirInclusiveRange()
    {
        string path = Path.Combine(Path.GetTempPath(), "MDA-output-timing-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var preset = new ViewOutputPresetDefinition("test.duration", "Duration", new(64, 64), new(64, 64, new(60)));
            var request = preset.Export.CreateRequest(0, 300, includeEndFrame: false);
            var draft = new OutputSettingsDraft(preset, request, OutputMediaKind.PngSequence,
                Path.Combine(Path.GetTempPath(), "MDA-timing-frames"), false, Vector4.One, null);
            EditorPreferences.SaveOutputSettings(draft, path);
            var restored = EditorPreferences.LoadOutputSettings(out var diagnostic, path);
            Assert.Null(diagnostic);
            Assert.NotNull(restored);
            Assert.False(restored.Request.Plan.IncludeEndFrame);
            Assert.Equal(600, restored.Request.Plan.FrameCount);

            var legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.True(legacy.Remove("includeEndFrame"));
            File.WriteAllText(path, legacy.ToJsonString());
            restored = EditorPreferences.LoadOutputSettings(out diagnostic, path);
            Assert.Null(diagnostic);
            Assert.NotNull(restored);
            Assert.True(restored.Request.Plan.IncludeEndFrame);
            Assert.Equal(601, restored.Request.Plan.FrameCount);
        }
        finally { File.Delete(path); }
    }
}
