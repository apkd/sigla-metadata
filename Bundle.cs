using System.Formats.Tar;
using System.Text.Json;

namespace Sigla.Metadata;

public sealed class Bundle(string directory, Origin origin)
{
    readonly SortedDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, PackageInfo> packages = new(StringComparer.Ordinal);
    public SortedSet<string> PackageNames { get; } = new(StringComparer.Ordinal);
    public static bool License(string path) => Path.GetFileName(path).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Contains("Third Party", StringComparison.OrdinalIgnoreCase);
    public static bool Input(string path) => License(path) || Path.GetFileName(path) == "package.json"
        || new[] { ".cs", ".asmdef", ".asmref", ".rsp", ".dll", ".h", ".hpp", ".hxx", ".c", ".cc", ".cpp", ".cxx", ".inl", ".inc", ".hlsl", ".hlsli", ".cg", ".cginc", ".glsl", ".shader", ".compute", ".raytrace", ".ush", ".usf" }.Contains(Path.GetExtension(path).ToLowerInvariant())
        || new[] { ".asmdef.meta", ".asmref.meta", ".dll.meta", ".rsp.meta" }.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    public async Task Add(string logicalPath, byte[] bytes, string? group = null)
    {
        logicalPath = Archives.PathName(logicalPath);
        var kind = License(logicalPath) ? "license" : "source";
        if (logicalPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var analysis = AssemblyExtractor.Extract(bytes, Path.GetFileNameWithoutExtension(logicalPath));
            if (analysis is null) return;
            bytes = analysis;
            kind = "assembly";
        }
        else if (Path.GetExtension(logicalPath) is ".json" or ".asmdef" or ".asmref" or ".rsp" or ".meta" or ".asset") kind = "configuration";
        var hash = Data.Hash(bytes);
        var entry = new Entry(logicalPath, kind, hash, bytes.LongLength, group, kind == "assembly" ? Path.GetFileNameWithoutExtension(logicalPath) : null);
        if (entries.TryGetValue(logicalPath, out var existing) && existing != entry) throw new InvalidDataException($"Conflicting logical path: {logicalPath}");
        entries[logicalPath] = entry;
        var target = Path.Combine(directory, kind == "license" ? "licenses" : "objects", hash);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target)) await File.WriteAllBytesAsync(target, bytes);
    }
    public void Package(JsonElement manifest, string source, string editor)
    {
        var name = manifest.GetProperty("name").GetString()!;
        var version = manifest.GetProperty("version").GetString()!;
        var info = new PackageInfo(name, version, source, editor, Registry.Dependencies(manifest));
        packages[name] = info;
        PackageNames.Add(name);
    }
    public BundleManifest Manifest => new(Data.Format, Data.Analysis, origin,
        Environment.GetEnvironmentVariable("SOURCE_REVISION") ?? "local", entries.Values.ToArray(), packages.Values.ToArray());
    public async Task<Artifact> Save(string output)
    {
        Directory.CreateDirectory(output);
        var manifest = Manifest;
        if (manifest.Entries.Length == 0) throw new InvalidDataException("Empty bundle.");
        var identity = Data.Hash(Data.Encode(new { origin, format = Data.Format, analysis = Data.Analysis, entries = manifest.Entries }));
        var name = $"{Data.SafeName(origin.Kind)}-{Data.SafeName(origin.Name)}-{Data.SafeName(origin.Version)}-{identity[..16]}.tar.zst";
        var tar = Path.Combine(output, name[..^4]);
        var destination = Path.Combine(output, name);
        await using (var file = File.Create(tar))
        await using (var writer = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: false))
        {
            await WriteEntry(writer, "manifest.json", new MemoryStream(Data.Encode(manifest)));
            foreach (var group in manifest.Entries.GroupBy(e => (Directory: e.Kind == "license" ? "licenses" : "objects", e.Hash)).OrderBy(g => g.Key.Directory, StringComparer.Ordinal).ThenBy(g => g.Key.Hash, StringComparer.Ordinal))
                await WriteEntry(writer, group.Key.Directory + "/" + group.Key.Hash, File.OpenRead(Path.Combine(directory, group.Key.Directory, group.Key.Hash)));
        }
        try { await Commands.Run("zstd", "-19", "-T2", "-f", "-o", destination, "--", tar); }
        finally { File.Delete(tar); }
        await Verify(destination);
        var artifact = new Artifact(name, await Data.HashFile(destination), new FileInfo(destination).Length, origin, Data.Format, Data.Analysis, manifest.Packages, PackageNames.ToArray());
        Data.Write(Path.Combine(output, name + ".json"), artifact);
        Console.WriteLine($"Bundle {name}: {manifest.Entries.Length} entries, {artifact.Size:N0} compressed bytes");
        return artifact;
    }
    static async Task WriteEntry(TarWriter writer, string name, Stream data)
    {
        await using (data)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name, new Dictionary<string, string> { ["atime"] = "0", ["ctime"] = "0" }) { DataStream = data, ModificationTime = DateTimeOffset.UnixEpoch, Uid = 0, Gid = 0, UserName = "", GroupName = "", Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead };
            await writer.WriteEntryAsync(entry);
        }
    }
    public static async Task Verify(string archive)
    {
        BundleManifest? manifest = null;
        var expected = new Dictionary<string, long>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await Archives.Read(archive, async (name, stream, size) =>
        {
            if (!seen.Add(name)) throw new InvalidDataException($"Duplicate archive member: {name}");
            var bytes = await Archives.Bytes(stream, size);
            if (name == "manifest.json")
            {
                manifest = Data.Decode<BundleManifest>(bytes);
                if (manifest.Format != Data.Format || manifest.Analysis != Data.Analysis) throw new InvalidDataException("Unsupported bundle format.");
                var paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in manifest.Entries)
                {
                    if (!paths.Add(e.Path) || Archives.PathName(e.Path) != e.Path || e.Hash.Length != 64 || !e.Hash.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Invalid bundle entry.");
                    var key = (e.Kind == "license" ? "licenses/" : "objects/") + e.Hash;
                    if (expected.TryGetValue(key, out var previous) && previous != e.Size) throw new InvalidDataException("Object size disagreement.");
                    expected[key] = e.Size;
                }
            }
            else
            {
                if (manifest is null || !expected.Remove(name, out var length) || length != size || name.Split('/')[^1] != Data.Hash(bytes)) throw new InvalidDataException($"Unexpected or corrupt object: {name}");
                if (bytes.Length > 1 && bytes[0] == 'M' && bytes[1] == 'Z') throw new InvalidDataException("Bundle contains an executable payload.");
            }
        });
        if (manifest is null || expected.Count != 0) throw new InvalidDataException("Incomplete bundle.");
    }
}
