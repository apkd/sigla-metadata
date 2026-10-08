using System.Diagnostics;
using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sigla.Metadata;

public static class Commands
{
    public static Process Start(string name, params string[] arguments)
    {
        var info = new ProcessStartInfo(name) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        return Process.Start(info) ?? throw new IOException($"Cannot start {name}");
    }
    public static async Task<string> Run(string name, params string[] arguments)
    {
        using var process = Start(name, arguments);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = await output;
        if (process.ExitCode != 0) throw new IOException($"{name} failed ({process.ExitCode}): {await error}");
        return text;
    }
}

public static class Web
{
    static readonly HttpClient Client = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30), PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(30) };
    static Web() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("sigla-metadata/1.0");
    public static async Task<HttpResponseMessage> Get(string url, bool optional = false)
    {
        if (new Uri(url).Scheme != "https") throw new InvalidDataException("Downloads require HTTPS.");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (optional && response.StatusCode == HttpStatusCode.NotFound) return response;
                if ((int)response.StatusCode is 429 or >= 500 && attempt < 3)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt);
                    response.Dispose();
                    await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    var code = response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"HTTP {(int)code} for {url}: {body[..Math.Min(body.Length, 1000)]}", null, code);
                }
                return response;
            }
            catch (HttpRequestException error) when (attempt < 3 && (error.StatusCode is null || (int)error.StatusCode >= 500 || (int)error.StatusCode == 429))
            { await Task.Delay(TimeSpan.FromSeconds(2 << attempt)); }
        }
    }
    public static async Task<JsonElement?> Json(string url, bool optional = false)
    {
        using var response = await Get(url, optional);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }
    public static async Task Download(string url, string path, string? integrity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var response = await Get(url);
        await using (var output = File.Create(path))
            await response.Content.CopyToAsync(output);
        if (response.Content.Headers.ContentLength is long length && new FileInfo(path).Length != length)
            throw new InvalidDataException("Truncated download.");
        await VerifyIntegrity(path, integrity);
    }
    public static async Task VerifyIntegrity(string path, string? integrity)
    {
        if (string.IsNullOrEmpty(integrity)) throw new InvalidDataException("Upstream did not provide archive integrity.");
        var parts = integrity.Split('-', 2);
        if (parts.Length != 2) throw new InvalidDataException("Invalid upstream integrity.");
        var algorithm = parts[0].ToLowerInvariant() switch
        {
            "md5" => HashAlgorithmName.MD5, "sha1" => HashAlgorithmName.SHA1,
            "sha256" => HashAlgorithmName.SHA256, "sha512" => HashAlgorithmName.SHA512,
            _ => throw new InvalidDataException("Unsupported upstream integrity.")
        };
        using var hash = IncrementalHash.CreateHash(algorithm);
        await using var file = File.OpenRead(path);
        var buffer = new byte[128 * 1024];
        int count;
        while ((count = await file.ReadAsync(buffer)) != 0) hash.AppendData(buffer, 0, count);
        var digest = hash.GetHashAndReset();
        var expected = Convert.FromBase64String(parts[1]);
        if (!expected.AsSpan().SequenceEqual(digest) && System.Text.Encoding.ASCII.GetString(expected) != Convert.ToHexStringLower(digest))
            throw new InvalidDataException("Upstream archive checksum mismatch.");
    }
}

public static class Archives
{
    public static string PathName(string name)
    {
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        if (name.StartsWith('/') || name.Split('/').Any(p => p == "..") || name.Contains('\0'))
            throw new InvalidDataException($"Archive path escapes its root: {name}");
        return name.TrimEnd('/');
    }
    public static async Task Read(string archive, Func<string, Stream, long, Task> visit)
    {
        var command = archive.EndsWith(".xz", StringComparison.Ordinal) ? "xz" : archive.EndsWith(".zst", StringComparison.Ordinal) ? "zstd" : "gzip";
        using var process = command == "xz" ? Commands.Start(command, "-T2", "-dc", archive) : Commands.Start(command, "-dc", archive);
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var reader = new TarReader(process.StandardOutput.BaseStream, leaveOpen: true);
            while (await reader.GetNextEntryAsync(copyData: false) is { } entry)
            {
                var name = PathName(entry.Name);
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is { } stream)
                    await visit(name, stream, entry.Length);
            }
            // Consume tar padding through the compression footer. Otherwise a
            // decoder can block on a full stdout pipe after TarReader reaches EOF.
            await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidDataException($"Archive decompression failed: {await errors}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
    public static async Task<byte[]> Bytes(Stream stream, long size)
    {
        if (size < 0 || size > 512 * 1024 * 1024) throw new InvalidDataException($"Analysis input too large: {size}");
        using var memory = new MemoryStream((int)size);
        await stream.CopyToAsync(memory);
        if (memory.Length != size) throw new InvalidDataException("Truncated archive member.");
        return memory.ToArray();
    }
}
