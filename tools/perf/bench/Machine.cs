using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Mmd.Modern.Rendering.D3D12;

namespace PerfBench;

static class BuildInfo
{
    public static object Describe()
    {
        var assembly = typeof(BuildInfo).Assembly;
        return new
        {
            sourceCommit = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SourceCommit")?.Value,
            tool = assembly.Location,
            renderer = typeof(D3D12FrameSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            tieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
            serverGc = GCSettings.IsServerGC, concurrentGc = GCSettings.LatencyMode.ToString(),
        };
    }
}

/// <summary>Machine identity recorded next to every result set. Read-only queries; nothing is configured.</summary>
static class MachineInfo
{
    public static object Collect()
    {
        string? adapter = null; long luid = 0; bool warp = false; object? gi = null;
        try
        {
            using var gpu = new D3D12FrameSession();
            adapter = gpu.AdapterName; luid = gpu.AdapterLuid; warp = gpu.IsWarp;
            var caps = gpu.QueryRayGiCapabilities();
            gi = new { caps.HardwareAvailable, caps.SoftwareAvailable, caps.RawRaytracingTier, caps.ShaderModel, caps.UnavailableReason };
        }
        catch (Exception error) { adapter = "unavailable: " + error.Message; }
        return new
        {
            schema = Output.Schema, kind = "machine", collectedUtc = DateTime.UtcNow,
            cpu = OperatingSystem.IsWindows() ? CpuName() : null,
            logicalProcessors = Environment.ProcessorCount,
            cores = OperatingSystem.IsWindows() ? CoreClasses() : null,
            memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            gpu = new { adapter, luid, warp, driver = OperatingSystem.IsWindows() && adapter is not null ? GpuDriver(adapter) : null, rayGi = gi },
            os = RuntimeInformation.OSDescription, arch = RuntimeInformation.OSArchitecture.ToString(),
            dotnet = RuntimeInformation.FrameworkDescription, runtimeVersion = Environment.Version.ToString(),
            build = BuildInfo.Describe(),
            power = OperatingSystem.IsWindows() ? Power() : null,
        };
    }

    [SupportedOSPlatform("windows")]
    static string? CpuName()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim();
    }

    [SupportedOSPlatform("windows")]
    static object? GpuDriver(string adapter)
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (root is null) return null;
            foreach (string name in root.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    if (key?.GetValue("DriverDesc") is string desc && string.Equals(desc.Trim(), adapter.Trim(), StringComparison.OrdinalIgnoreCase))
                        return new { version = key.GetValue("DriverVersion") as string, date = key.GetValue("DriverDate") as string };
                }
                catch (System.Security.SecurityException) { }
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Logical processors per core efficiency class (higher class = faster cores on hybrid CPUs).</summary>
    [SupportedOSPlatform("windows")]
    static object? CoreClasses()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
        if (length == 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref length)) return null;
            var classes = new SortedDictionary<int, (int Cores, int Logical)>();
            for (int offset = 0; offset < length;)
            {
                IntPtr entry = buffer + offset;
                int size = Marshal.ReadInt32(entry, 4);
                // PROCESSOR_RELATIONSHIP: Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2) GROUP_AFFINITY[]
                int efficiency = Marshal.ReadByte(entry, 9);
                int groups = Marshal.ReadInt16(entry, 30);
                int logical = 0;
                for (int g = 0; g < groups; g++)
                    logical += System.Numerics.BitOperations.PopCount((ulong)Marshal.ReadInt64(entry, 32 + g * 16));
                classes.TryGetValue(efficiency, out var current);
                classes[efficiency] = (current.Cores + 1, current.Logical + logical);
                offset += size;
            }
            return classes.Select(c => new { efficiencyClass = c.Key, cores = c.Value.Cores, logicalProcessors = c.Value.Logical }).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [SupportedOSPlatform("windows")]
    static object Power()
    {
        string? scheme = null;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var oem = Encoding.GetEncoding(checked((int)GetOEMCP()));
            using var process = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
            {
                RedirectStandardOutput = true, StandardOutputEncoding = oem, UseShellExecute = false, CreateNoWindow = true
            });
            if (process is not null) { scheme = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(5000); }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        bool known = GetSystemPowerStatus(out var status);
        return new { scheme, acLine = known ? status.ACLineStatus switch { 0 => "battery", 1 => "ac", _ => "unknown" } : "unknown",
            batteryPercent = known && status.BatteryLifePercent != 255 ? status.BatteryLifePercent : (int?)null };
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("kernel32.dll")]
    static extern uint GetOEMCP();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}
