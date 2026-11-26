using Mmd.Modern.Core;
using static Mmd.Modern.App.EditorLocalization;

namespace Mmd.Modern.App;

public sealed partial class EditorLightingViewModel
{
    public int EnabledLightCount
    {
        get
        {
            var disabledGroups = Groups.Where(group => !group.Enabled).Select(group => group.Id).ToHashSet();
            return Lights.Count(light => light.Enabled && (light.GroupId is not { } id || !disabledGroups.Contains(id)));
        }
    }

    /// <summary>The core remains authoritative. The editor gives an actionable, localized capacity
    /// error before attempting to publish the complete candidate; no active lights are truncated.</summary>
    private SceneLightingSnapshot CreateSnapshot(IEnumerable<NativeSceneLight> lights, IEnumerable<SceneLightGroup> groups)
    {
        var authored = lights.Take(SceneLightingSnapshot.MaxLights + 1).ToArray();
        var groupValues = groups.ToArray();
        if (authored.Length > SceneLightingSnapshot.MaxLights)
            throw Error("最多支持 {0} 个附加光源，请先移除一个。", SceneLightingSnapshot.MaxLights);
        var disabled = groupValues.Where(group => !group.Enabled).Select(group => group.Id).ToHashSet();
        if (authored.Count(light => light.Enabled && (light.GroupId is not { } id || !disabled.Contains(id))) > SceneLightingSnapshot.MaxActiveLights)
            throw Error("Mda.Text.Context.Lighting.ActiveCapacity", SceneLightingSnapshot.MaxActiveLights);
        return new(authored, groupValues, BuildGlobalSettings());
    }

    private void RefreshCapacity()
    {
        Changed(nameof(EnabledLightCount)); Changed(nameof(LightCountSummary)); Changed(nameof(CanAddLight));
        Changed(nameof(CanDuplicateSelection)); Changed(nameof(CanEnableSelection)); Changed(nameof(CanDisableSelection));
    }
}
