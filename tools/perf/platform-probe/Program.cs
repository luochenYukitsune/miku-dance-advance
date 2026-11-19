using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using Vortice.DXGI;

// Read-only platform facts behind three plan items: which adapter the D3D12 runtime's
// EnumAdapters1 loop selects, how logical processors split by efficiency class, and the
// real cadence of a 16 ms Avalonia DispatcherTimer on the Win32 dispatcher.
internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("PlatformProbe new-results-file.json"); return 2; }
        var output = Path.GetFullPath(args[0]);
        if (File.Exists(output)) throw new IOException("Choose a new results file.");
        var result = new Dictionary<string, object?>
        {
            ["adapters"] = Adapters(),
            ["cpuSets"] = CpuSets(),
            ["timerResolution100ns"] = TimerResolution(),
        };
        result["dispatcherTimer16ms"] = DispatcherCadence(seconds: 4);
        result["sleep1ms"] = SleepCadence(200);
        _ = TimeBeginPeriod(1);
        try
        {
            result["timerResolution100nsAfterTimeBeginPeriod1"] = TimerResolution();
            result["dispatcherTimer16msAfterTimeBeginPeriod1"] = DispatcherCadence(seconds: 4);
            result["sleep1msAfterTimeBeginPeriod1"] = SleepCadence(200);
        }
        finally { _ = TimeEndPeriod(1); }
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json);
        Console.WriteLine(json);
        return 0;
    }

    private static object Adapters()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        static object Describe(IDXGIAdapter1 adapter)
        {
            var d = adapter.Description1;
            return new { d.Description, DedicatedVideoMemoryMiB = (long)d.DedicatedVideoMemory / (1024 * 1024),
                Software = (d.Flags & AdapterFlags.Software) != 0, Luid = d.Luid.ToString() };
        }
        var enumOrder = new List<object>();
        for (int i = 0; factory.EnumAdapters1(i, out var adapter).Success && adapter is not null; i++)
            using (adapter) enumOrder.Add(Describe(adapter));
        var highPerformance = new List<object>();
        for (int i = 0; factory.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Success; i++)
            if (adapter is not null) using (adapter) highPerformance.Add(Describe(adapter));
        return new { EnumAdapters1 = enumOrder, HighPerformance = highPerformance };
    }

    private static object CpuSets()
    {
        _ = GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint length, IntPtr.Zero, 0);
        var buffer = new byte[length];
        unsafe
        {
            fixed (byte* pointer = buffer)
                if (!GetSystemCpuSetInformation((IntPtr)pointer, length, out length, IntPtr.Zero, 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        var classes = new SortedDictionary<int, int>();
        var cores = new SortedDictionary<int, HashSet<int>>();
        for (int offset = 0; offset < length;)
        {
            int size = BitConverter.ToInt32(buffer, offset);
            if (BitConverter.ToInt32(buffer, offset + 4) == 0)
            {
                int core = buffer[offset + 15], efficiency = buffer[offset + 18];
                classes[efficiency] = classes.GetValueOrDefault(efficiency) + 1;
                (cores.TryGetValue(efficiency, out var set) ? set : cores[efficiency] = []).Add(core);
            }
            offset += size;
        }
        return new
        {
            Environment.ProcessorCount,
            LogicalProcessorsByEfficiencyClass = classes.ToDictionary(item => item.Key.ToString(), item => item.Value),
            CoresByEfficiencyClass = cores.ToDictionary(item => item.Key.ToString(), item => item.Value.Count),
        };
    }

    private static object TimerResolution()
    {
        _ = NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);
        return new { Coarsest = coarsest, Finest = finest, Current = current };
    }

    private static bool avaloniaReady;
    private static object DispatcherCadence(int seconds)
    {
        if (!avaloniaReady) { AppBuilder.Configure<Application>().UseWin32().UseSkia().SetupWithoutStarting(); avaloniaReady = true; }
        var intervals = new List<double>();
        long last = 0;
        var stop = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            if (last != 0) intervals.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            last = now;
            if (clock.Elapsed.TotalSeconds >= seconds) { timer.Stop(); stop.Cancel(); Dispatcher.UIThread.Post(() => { }); }
        };
        timer.Start();
        Dispatcher.UIThread.MainLoop(stop.Token);
        return Summary(intervals, clock.Elapsed.TotalSeconds);
    }

    private static object SleepCadence(int count)
    {
        var samples = new List<double>(count);
        for (int i = 0; i < count; i++)
        {
            long start = Stopwatch.GetTimestamp();
            Thread.Sleep(1);
            samples.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        }
        return Summary(samples, null);
    }

    private static object Summary(List<double> values, double? seconds)
    {
        var sorted = values.Order().ToArray();
        double At(double p) => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(p * (sorted.Length - 1)))];
        return new { Count = sorted.Length, P05 = At(.05), P50 = At(.5), P95 = At(.95), Max = sorted.LastOrDefault(),
            TicksPerSecond = seconds is { } s && s > 0 ? sorted.Length / s : (double?)null };
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);
    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);
}
