using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mmd.Modern.ViewOutput;

/// <summary>Windows default waveform output for validated PCM WAV. No external package or executable
/// is required. Unsupported device formats fail explicitly instead of silently changing source data.
/// Lifecycle follows Microsoft's waveOutPrepareHeader/Write/Reset/UnprepareHeader/Close contracts.
/// NAudio's MIT WaveOutEvent was reviewed for the same native resource ordering; no source is copied.</summary>
public sealed class WindowsSceneAudioOutputFactory : ISceneAudioOutputFactory
{
    public ISceneAudioOutput Open(ScenePcmAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("PCM preview audio requires Windows waveform output.");
        return new WindowsSceneAudioOutput(audio);
    }
}

internal sealed class WindowsSceneAudioOutput : ISceneAudioOutput
{
    private readonly ScenePcmAudio audio;
    private GCHandle pinned;
    private IntPtr device, header, data;
    private bool prepared, queued, playing, disposed;
    private long startFrame;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();

    internal WindowsSceneAudioOutput(ScenePcmAudio audio)
    {
        this.audio = audio;
        // Pin the already private validated data; seeking never allocates/copies the remainder.
        if (!MemoryMarshal.TryGetArray(audio.PcmData, out ArraySegment<byte> segment) || segment.Array is null)
            throw new InvalidOperationException("Validated PCM requires owned array storage.");
        try
        {
            pinned = GCHandle.Alloc(segment.Array, GCHandleType.Pinned);
            data = IntPtr.Add(pinned.AddrOfPinnedObject(), segment.Offset);
            header = Marshal.AllocHGlobal((int)HeaderSize);
            var format = new WaveFormat
            {
                FormatTag = 1, Channels = (ushort)audio.Channels, SamplesPerSecond = (uint)audio.SampleRate,
                AverageBytesPerSecond = (uint)(audio.SampleRate * audio.BlockAlign),
                BlockAlign = (ushort)audio.BlockAlign, BitsPerSample = (ushort)audio.BitsPerSample, ExtraSize = 0
            };
            Check(Native.waveOutOpen(out device, new IntPtr(-1), ref format, IntPtr.Zero, IntPtr.Zero, 0), "waveOutOpen");
        }
        catch
        {
            // No header has been prepared or submitted during construction.
            if (device != IntPtr.Zero) Native.waveOutClose(device);
            if (header != IntPtr.Zero) { Marshal.FreeHGlobal(header); header = IntPtr.Zero; }
            if (pinned.IsAllocated) pinned.Free();
            throw;
        }
    }

    public long PositionFrames
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!queued) return startFrame;
            var position = new MultimediaTime { Type = 2, Value = 0 }; // TIME_SAMPLES
            Check(Native.waveOutGetPosition(device, ref position, (uint)Marshal.SizeOf<MultimediaTime>()), "waveOutGetPosition");
            long frames = position.Type switch
            {
                2 => position.Value,
                4 => position.Value / audio.BlockAlign, // TIME_BYTES fallback
                1 => (long)position.Value * audio.SampleRate / 1000, // TIME_MS fallback
                _ => throw new NotSupportedException("Audio device did not report a supported position unit.")
            };
            return Math.Min(audio.SampleCount, startFrame + frames);
        }
    }

    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame < 0 || frame > audio.SampleCount) throw new ArgumentOutOfRangeException(nameof(frame));
        ResetBuffer(); startFrame = frame;
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (playing || startFrame >= audio.SampleCount) return;
        if (!queued)
        {
            // First pause before submitting so the native first-write auto-start cannot race Seek.
            Check(Native.waveOutPause(device), "waveOutPause");
            var value = new WaveHeader
            {
                Data = IntPtr.Add(data, checked((int)(startFrame * audio.BlockAlign))),
                BufferLength = checked((uint)((audio.SampleCount - startFrame) * audio.BlockAlign)),
                BytesRecorded = 0, User = UIntPtr.Zero, Flags = 0, Loops = 0, Next = IntPtr.Zero, Reserved = UIntPtr.Zero
            };
            Marshal.StructureToPtr(value, header, false);
            Check(Native.waveOutPrepareHeader(device, header, HeaderSize), "waveOutPrepareHeader");
            prepared = true;
            Check(Native.waveOutWrite(device, header, HeaderSize), "waveOutWrite");
            queued = true;
        }
        Check(Native.waveOutRestart(device), "waveOutRestart"); playing = true;
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!playing) return;
        Check(Native.waveOutPause(device), "waveOutPause"); playing = false;
    }

    private void ResetBuffer()
    {
        Check(Native.waveOutReset(device), "waveOutReset"); playing = false;
        // Never free or overwrite header/data until the driver releases it. If unprepare fails,
        // retain all ownership so Seek/Dispose can retry instead of freeing in-flight memory.
        if (prepared)
        {
            Check(Native.waveOutUnprepareHeader(device, header, HeaderSize), "waveOutUnprepareHeader");
            prepared = false;
        }
        queued = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        if (device != IntPtr.Zero)
        {
            ResetBuffer();
            Check(Native.waveOutClose(device), "waveOutClose"); device = IntPtr.Zero;
        }
        if (header != IntPtr.Zero) { Marshal.FreeHGlobal(header); header = IntPtr.Zero; }
        if (pinned.IsAllocated) pinned.Free();
        disposed = true;
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new Win32Exception((int)result, $"{operation} failed with multimedia result {result}.");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        internal ushort FormatTag, Channels;
        internal uint SamplesPerSecond, AverageBytesPerSecond;
        internal ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        internal IntPtr Data;
        internal uint BufferLength, BytesRecorded;
        internal UIntPtr User;
        internal uint Flags, Loops;
        internal IntPtr Next;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MultimediaTime
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(4)] internal uint Value;
    }

    private static class Native
    {
        [DllImport("winmm.dll", ExactSpelling = true)] internal static extern uint waveOutOpen(out IntPtr device, IntPtr deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll", ExactSpelling = true)] internal static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)] internal static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)] internal static extern uint waveOutPause(IntPtr device);
        [DllImport("winmm.dll", ExactSpelling = true)] internal static extern uint waveOutRestart(IntPtr device);
        [DllImport("winmm.dll", ExactSpelling = true)] internal static extern uint waveOutReset(IntPtr device);
        [DllImport("winmm.dll", ExactSpelling = true)] internal static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)] internal static extern uint waveOutClose(IntPtr device);
        [DllImport("winmm.dll", ExactSpelling = true)] internal static extern uint waveOutGetPosition(IntPtr device, ref MultimediaTime position, uint size);
    }
}
