namespace Mmd.Modern.ViewOutput;

/// <summary>Value-only identity of the device selected by an owned renderer. No GPU resource escapes.</summary>
public sealed record ViewRendererDeviceInfo(string BackendId, string AdapterName, long AdapterLuid, bool IsSoftware);

/// <summary>Optional device identity for export diagnostics; callers must not select a default adapter instead.</summary>
public interface IViewRendererDeviceInfoProvider
{
    ViewRendererDeviceInfo DeviceInfo { get; }
}
