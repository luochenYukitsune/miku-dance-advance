using System.Diagnostics;
using System.Text;

namespace Mmd.Modern.ViewOutput.FFmpeg;

public sealed class FfmpegException(string message, string diagnostic, Exception? innerException = null)
    : IOException(message + (diagnostic.Length == 0 ? "" : Environment.NewLine + diagnostic), innerException)
{
    public string Diagnostic { get; } = diagnostic;
}

internal sealed class FfmpegProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private readonly Task<string> stdout;
    private readonly Task<long>? stdoutByteCount;
    public Stream Input => process.StandardInput.BaseStream;
    public bool HasExited => process.HasExited;
    public Task<string> DiagnosticAsync() => stderr;
    private FfmpegProcess(Process process, bool countBinaryOutput)
    {
        this.process = process;
        stderr = DrainAsync(process.StandardError, 32_768);
        stdout = countBinaryOutput ? Task.FromResult("") : DrainAsync(process.StandardOutput, 2_097_152);
        stdoutByteCount = countBinaryOutput ? CountAsync(process.StandardOutput.BaseStream) : null;
    }
    internal static ProcessStartInfo StartInfo(string executable, IEnumerable<string> args)
    {
        ProcessStartInfo info = new(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    public static FfmpegProcess Start(string executable, IEnumerable<string> args, bool countBinaryOutput = false)
    {
        Process process = new() { StartInfo = StartInfo(executable, args) };
        try { process.Start(); return new(process, countBinaryOutput); }
        catch { process.Dispose(); throw; }
    }
    public async Task<(string Output, string Diagnostic)> FinishAsync(CancellationToken cancellationToken)
    {
        process.StandardInput.Close();
        using var registration = cancellationToken.Register(Kill);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch { Kill(); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        string error = await stderr.ConfigureAwait(false), output = await stdout.ConfigureAwait(false);
        if (stdoutByteCount is not null) await stdoutByteCount.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new FfmpegException($"Process exited with code {process.ExitCode}.", error);
        return (output, error);
    }
    public async Task AbortAsync()
    {
        Kill();
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(stderr, stdout).ConfigureAwait(false);
        if (stdoutByteCount is not null) await stdoutByteCount.ConfigureAwait(false);
    }
    public void Kill()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    public async ValueTask DisposeAsync()
    {
        try { await AbortAsync().ConfigureAwait(false); }
        finally { process.Dispose(); }
    }
    private static async Task<string> DrainAsync(StreamReader reader, int capacity)
    {
        StringBuilder text = new(); char[] buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > capacity) text.Remove(0, text.Length - capacity);
        }
        return text.ToString();
    }
    public Task<long> OutputByteCountAsync() => stdoutByteCount
        ?? throw new InvalidOperationException("This process was not opened for binary output validation.");
    private static async Task<long> CountAsync(Stream input)
    {
        byte[] buffer = new byte[64 * 1024]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer).ConfigureAwait(false)) != 0) total = checked(total + count);
        return total;
    }
}
