using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Mmd.Modern.App.Updates;

/// <summary>
/// Reads one JSON object with version, summary (plain text), optional releasePage, and optional
/// package { url, sha256, size }. URLs must use HTTPS. This reader never downloads the package.
/// The caller owns the injected HttpClient; at most 64 KiB of feed content is accepted.
/// </summary>
public sealed class JsonStartupUpdateMetadataSource(HttpClient client) : IStartupUpdateMetadataSource
{
    public async Task<StartupUpdateRelease?> GetLatestAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (!StartupUpdateService.IsHttps(endpoint)) throw new InvalidDataException("The update feed must use HTTPS.");
        using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > StartupUpdateOptions.MaximumFeedBytes)
            throw new InvalidDataException("The update feed is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (content.Length + count > StartupUpdateOptions.MaximumFeedBytes)
                throw new InvalidDataException("The update feed is too large.");
            content.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<StartupUpdateRelease>(content.ToArray(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 8 }) ??
            throw new InvalidDataException("The update feed has no release.");
    }
}

/// <summary>Stores only the explicitly skipped version. Construction performs no file access.</summary>
public sealed class FileStartupUpdateSkipStore(string path) : IStartupUpdateSkipStore
{
    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            128, FileOptions.Asynchronous);
        if (stream.Length > 256) return null;
        var bytes = new byte[257];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        return length > 256 ? null : Encoding.UTF8.GetString(bytes, 0, length).Trim();
    }

    public async Task WriteAsync(string version, CancellationToken cancellationToken)
    {
        if (!UpdateVersion.TryParse(version, out _)) throw new ArgumentException("Invalid update version.", nameof(version));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, version, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
