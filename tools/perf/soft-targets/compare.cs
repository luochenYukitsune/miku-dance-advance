#:property AllowUnsafeBlocks=true
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

// Pass the full DLL path so the file-based app can run from any build directory.
internal static unsafe class Compare
{
    [DllImport("soft_target_probe", CallingConvention = CallingConvention.Cdecl)]
    private static extern int pack_targets(Vector3* source, nuint vertices, int* ids, nuint count, float* output);

    private static int Managed(Vector3[] source, int[] ids, float[] output)
    {
        for (int j = 0; j < ids.Length; j++)
        {
            int id = ids[j];
            if ((uint)id >= source.Length) return -1;
            var p = source[id];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) return -1;
            output[3 * j] = p.X;
            output[3 * j + 1] = p.Y;
            output[3 * j + 2] = p.Z;
        }
        return 0;
    }

    private static int Rust(Vector3[] points, int[] ids, float[] output)
    {
        fixed (Vector3* p = points)
        fixed (int* i = ids)
        fixed (float* o = output)
            return pack_targets(p, (nuint)points.Length, i, (nuint)ids.Length, o);
    }

    private static int ManagedPacked(Vector3[] source, int[] ids, float[] output)
    {
        var targets = MemoryMarshal.Cast<float, Vector3>(output.AsSpan());
        for (int j = 0; j < ids.Length; j++)
        {
            int id = ids[j];
            if ((uint)id >= source.Length) return -1;
            var p = source[id];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) return -1;
            targets[j] = p;
        }
        return 0;
    }

    private static void Main(string[] args)
    {
        var library = NativeLibrary.Load(Path.GetFullPath(args.Single()));
        NativeLibrary.SetDllImportResolver(typeof(Compare).Assembly, (name, _, _) =>
            name == "soft_target_probe" ? library : IntPtr.Zero);
        if (sizeof(Vector3) != 12) throw new InvalidOperationException("Unexpected Vector3 ABI.");
        foreach (int n in new[] { 256, 4096, 32768 })
        {
            var rng = new Random(17);
            var points = Enumerable.Range(0, n).Select(i => new Vector3(i, -i, i * 0.125f)).ToArray();
            var ids = Enumerable.Range(0, n).ToArray();
            rng.Shuffle(ids);
            var reference = new float[n * 3];
            var packed = new float[n * 3];
            var candidate = new float[n * 3];
            var expected = MemoryMarshal.AsBytes(reference.AsSpan());
            if (Managed(points, ids, reference) != 0 || Rust(points, ids, candidate) != 0 ||
                ManagedPacked(points, ids, packed) != 0 ||
                !expected.SequenceEqual(MemoryMarshal.AsBytes(candidate.AsSpan())) ||
                !expected.SequenceEqual(MemoryMarshal.AsBytes(packed.AsSpan())))
                throw new InvalidOperationException("Output mismatch.");

            Func<int> managed = () => Managed(points, ids, reference);
            Func<int> rust = () => Rust(points, ids, candidate);
            Func<int> packedStore = () => ManagedPacked(points, ids, packed);
            // Each sample averages a batch of calls. Rust timing includes pinning and P/Invoke.
            int calls = 1_048_576 / n;
            double Sample(Func<int> run)
            {
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < calls; i++)
                    if (run() != 0) throw new InvalidOperationException("Unexpected invalid input.");
                return Stopwatch.GetElapsedTime(start).TotalMicroseconds / calls;
            }
            Func<int>[] methods = [managed, packedStore, rust];
            var warmup = Stopwatch.StartNew();
            do
            {
                foreach (var method in methods) Sample(method);
            }
            while (warmup.ElapsedMilliseconds < 650);
            List<double>[] timings = [[], [], []];
            int[][] orders = [[0, 1, 2], [2, 1, 0], [1, 2, 0], [0, 2, 1], [2, 0, 1], [1, 0, 2]];
            for (int repeat = 0; repeat < 4; repeat++)
                foreach (var order in orders)
                    foreach (int index in order)
                        timings[index].Add(Sample(methods[index]));
            void Report(string name, List<double> times)
            {
                times.Sort();
                Console.WriteLine(FormattableString.Invariant(
                    $"{n},{name},{(times[11] + times[12]) / 2:F3},{times[22]:F3}"));
            }
            Report("managed", timings[0]);
            Report("managed-packed", timings[1]);
            Report("rust", timings[2]);
            ids[0] = n;
            if (methods.Any(run => run() != -1)) throw new InvalidOperationException("Index check failed.");
            ids[0] = 0;
            points[0].Z = float.NaN;
            if (methods.Any(run => run() != -1)) throw new InvalidOperationException("Finite check failed.");
        }
    }
}
