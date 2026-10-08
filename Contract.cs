using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sigla.Metadata;

public static class Data
{
    public const int Format = 1;
    public const int Analysis = 1;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    public static T Decode<T>(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize<T>(bytes, Json)
        ?? throw new InvalidDataException($"Empty {typeof(T).Name}");
    public static T Read<T>(string path) => Decode<T>(File.ReadAllBytes(path));
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Encode(value));
    }
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static async Task<string> HashFile(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
    }
    public static string SafeName(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        ? value : throw new InvalidDataException($"Unsafe identifier: {value}");
}

public sealed record Origin(string Kind, string Name, string Version, string Revision, string Url, string? Integrity = null);
public sealed record Entry(string Path, string Kind, string Hash, long Size, string? Group = null, string? Name = null);
public sealed record PackageInfo(string Name, string Version, string Source, string Editor, SortedDictionary<string, string> Dependencies);
public sealed record BundleManifest(int Format, int Analysis, Origin Origin, string Producer, Entry[] Entries, PackageInfo[] Packages);
public sealed record Artifact(string Name, string Sha256, long Size, Origin Origin, int Format, int Analysis, PackageInfo[] Packages, string[] PackageNames);
public sealed record Catalog(int Format, Artifact[] Artifacts);
public sealed record EditorRelease(string Version, string Revision, string Url, string? Integrity);
public sealed record PackageRequest(string Name, string Version, string Url, string? Integrity, string[] Editors);
public sealed record PackageSelection(string Editor, string Name, string Version, string Channel, string Source);
public sealed record PackagePlan(PackageRequest[] Requests, PackageSelection[] Selections, string[] Unavailable);

public readonly record struct UnityVersion(int Major, int Minor, int Patch, char Channel, int Build) : IComparable<UnityVersion>
{
    public static UnityVersion Parse(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"^(\d+)\.(\d+)\.(\d+)([abfp])(\d+)$");
        if (!match.Success) throw new FormatException($"Invalid Unity version: {value}");
        return new(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), match.Groups[4].Value[0], int.Parse(match.Groups[5].Value));
    }
    public int CompareTo(UnityVersion other) => (Major, Minor, Patch, "abfp".IndexOf(Channel), Build)
        .CompareTo((other.Major, other.Minor, other.Patch, "abfp".IndexOf(other.Channel), other.Build));
    public string Line => $"{Major}.{Minor}";
}
