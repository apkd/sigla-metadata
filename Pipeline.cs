using System.Text.Json;

namespace Sigla.Metadata;

public static class Pipeline
{
    const string ReleaseTag = "metadata-archive";
    sealed record PlanState(EditorRelease[] Editors, Catalog Previous);
    static void Output(string name, object value)
    {
        var text = value is string s ? s : JsonSerializer.Serialize(value, Data.Json);
        Console.WriteLine($"{name}={text}");
        if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { } file) File.AppendAllText(file, name + "=" + text + "\n");
    }
    public static async Task Plan(Options options)
    {
        var output = options.Required("output");
        Directory.CreateDirectory(output);
        if (options.Get("phase") == "packages")
        {
            var state = Data.Read<PlanState>(options.Required("base"));
            var current = ReadArtifacts(options.Required("input")).Concat(state.Previous.Artifacts).ToArray();
            var editors = state.Editors.Select(e => current.FirstOrDefault(a => a.Origin.Kind == "editor" && a.Origin.Version == e.Version && a.Origin.Revision == e.Revision && a.Format == Data.Format && a.Analysis == Data.Analysis)
                ?? throw new InvalidDataException($"Editor output missing: {e.Version}")).ToArray();
            var plan = await Registry.Plan(editors);
            Data.Write(Path.Combine(output, "selection.json"), plan);
            var pending = plan.Requests.Where(r => options.Get("force") == "true" || !state.Previous.Artifacts.Any(a => a.Origin.Kind == "package" && a.Origin.Name == r.Name && a.Origin.Version == r.Version && a.Origin.Integrity == r.Integrity && a.Format == Data.Format && a.Analysis == Data.Analysis)).ToArray();
            Data.Write(Path.Combine(output, "requests.json"), pending);
            var shards = Math.Min(32, (pending.Length + 23) / 24);
            Output("matrix", new { include = Enumerable.Range(0, shards).Select(i => new { shard = i, shards }).ToArray() });
            Output("has_work", shards > 0 ? "true" : "false");
            Console.WriteLine($"Selected {plan.Requests.Length} package versions; {pending.Length} need building; {plan.Unavailable.Length} catalog entries are unavailable.");
            return;
        }
        var repo = options.Get("repo") ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "apkd/sigla-metadata";
        var previousJson = await Web.Json($"https://github.com/{repo}/releases/download/{ReleaseTag}/catalog.json", optional: true);
        var previous = previousJson is { } json ? Data.Decode<Catalog>(System.Text.Encoding.UTF8.GetBytes(json.GetRawText())) : new Catalog(Data.Format, []);
        var releases = new Dictionary<string, EditorRelease>(StringComparer.Ordinal);
        for (var offset = 0; ; offset += 25)
        {
            var page = (await Web.Json($"https://services.api.unity.com/unity/editor/release/v1/releases?platform=LINUX&architecture=X86_64&limit=25&offset={offset}"))!.Value;
            foreach (var release in page.GetProperty("results").EnumerateArray())
            {
                var versionText = release.GetProperty("version").GetString()!;
                if (!System.Text.RegularExpressions.Regex.IsMatch(versionText, @"^\d+\.\d+\.\d+[abfp]\d+$")) continue;
                var version = UnityVersion.Parse(versionText);
                if ((version.Major, version.Minor).CompareTo((2021, 3)) < 0) continue;
                foreach (var download in release.GetProperty("downloads").EnumerateArray())
                {
                    if (download.GetProperty("platform").GetString() != "LINUX" || download.GetProperty("architecture").GetString() != "X86_64" || download.GetProperty("type").GetString() != "TAR_XZ") continue;
                    var candidate = new EditorRelease(versionText, release.GetProperty("shortRevision").GetString()!, download.GetProperty("url").GetString()!, download.TryGetProperty("integrity", out var integrity) ? integrity.GetString() : null);
                    if (!releases.TryGetValue(version.Line, out var current) || version.CompareTo(UnityVersion.Parse(current.Version)) > 0) releases[version.Line] = candidate;
                }
            }
            if (offset + page.GetProperty("results").GetArrayLength() >= page.GetProperty("total").GetInt32()) break;
            if (page.GetProperty("results").GetArrayLength() == 0) throw new InvalidDataException("Unity release pagination stopped early.");
        }
        var selected = releases.Values.OrderBy(r => UnityVersion.Parse(r.Version)).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("Unity release catalog returned no supported editors.");
        Data.Write(Path.Combine(output, "plan.json"), new PlanState(selected, previous));
        var pendingEditors = selected.Where(e => options.Get("force") == "true" || !previous.Artifacts.Any(a => a.Origin.Kind == "editor" && a.Origin.Version == e.Version && a.Origin.Revision == e.Revision && a.Format == Data.Format && a.Analysis == Data.Analysis)).ToArray();
        Output("matrix", new { include = pendingEditors });
        Output("has_work", pendingEditors.Length > 0 ? "true" : "false");
    }

    public static async Task Editor(Options options)
    {
        var release = Data.Decode<EditorRelease>(System.Text.Encoding.UTF8.GetBytes(options.Required("release")));
        var work = options.Required("work");
        Directory.CreateDirectory(work);
        var archive = Path.Combine(work, "editor.tar.xz");
        var started = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"Downloading editor {release.Version}");
        await Web.Download(release.Url, archive, release.Integrity);
        Console.WriteLine($"Download verified in {started.Elapsed.TotalSeconds:F1}s ({new FileInfo(archive).Length:N0} bytes)");
        var origin = new Origin("editor", "unity", release.Version, release.Revision, release.Url, release.Integrity);
        var bundle = new Bundle(Path.Combine(work, "bundle"), origin);
        var nested = new List<string>();
        await Archives.Read(archive, async (path, stream, size) =>
        {
            const string data = "Editor/Data/";
            if (!path.StartsWith(data, StringComparison.Ordinal))
            {
                if (Bundle.License(path)) await bundle.Add("licenses/" + path, await Archives.Bytes(stream, size));
                return;
            }
            var relative = path[data.Length..];
            const string builtin = "Resources/PackageManager/BuiltInPackages/";
            const string catalog = "Resources/PackageManager/Editor/";
            if (relative.StartsWith(builtin, StringComparison.Ordinal) && Bundle.Input(relative))
            {
                var logical = relative[builtin.Length..];
                var bytes = await Archives.Bytes(stream, size);
                if (logical.EndsWith("/package.json", StringComparison.Ordinal) && logical.Count(c => c == '/') == 1)
                {
                    using var package = JsonDocument.Parse(bytes);
                    bundle.Package(package.RootElement, "editor", release.Version);
                }
                await bundle.Add("packages/" + logical, bytes, "package");
            }
            else if (relative.StartsWith(catalog, StringComparison.Ordinal) && relative.EndsWith(".tgz", StringComparison.Ordinal))
            {
                var target = Path.Combine(work, "nested-" + nested.Count + ".tgz");
                await File.WriteAllBytesAsync(target, await Archives.Bytes(stream, size));
                nested.Add(target);
            }
            else if (relative == catalog + "manifest.json")
            {
                var bytes = await Archives.Bytes(stream, size);
                using var manifest = JsonDocument.Parse(bytes);
                FindPackages(manifest.RootElement, bundle.PackageNames);
                await bundle.Add("configuration/package-catalog.json", bytes);
            }
            else if (relative == "Resources/modules.asset") await bundle.Add("configuration/modules.asset", await Archives.Bytes(stream, size));
            else if (relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && ReferenceGroup(relative) is { } group)
                await bundle.Add("references/" + relative, await Archives.Bytes(stream, size), group);
            else if (Bundle.License(relative)) await bundle.Add("licenses/" + relative, await Archives.Bytes(stream, size));
        });
        File.Delete(archive);
        foreach (var package in nested)
        {
            await ReadPackage(package, bundle, "editor", release.Version);
            File.Delete(package);
        }
        var manifestResult = bundle.Manifest;
        if (!manifestResult.Entries.Any(e => e.Group == "editor" && e.Name == "UnityEngine.CoreModule")
            || !manifestResult.Entries.Any(e => e.Path == "configuration/modules.asset")
            || !manifestResult.Entries.Any(e => e.Group == "standard")
            || !manifestResult.Entries.Any(e => e.Group == "framework"))
            throw new InvalidDataException("Editor reference groups are incomplete; this layout needs support.");
        await bundle.Save(options.Required("output"));
        Console.WriteLine($"Editor completed in {started.Elapsed.TotalSeconds:F1}s");
    }
    static string? ReferenceGroup(string path)
    {
        if (path.StartsWith("Managed/", StringComparison.Ordinal)) return "editor";
        if (path.StartsWith("NetStandard/", StringComparison.Ordinal)) return "standard";
        if (path.StartsWith("UnityReferenceAssemblies/", StringComparison.Ordinal) || path.StartsWith("MonoBleedingEdge/lib/mono/4.7.1-api/", StringComparison.Ordinal) || path.StartsWith("MonoBleedingEdge/lib/mono/4.8-api/", StringComparison.Ordinal)) return "framework";
        if (path.StartsWith("PlaybackEngines/LinuxStandaloneSupport/", StringComparison.Ordinal))
        {
            if (path.Contains("/il2cpp/", StringComparison.Ordinal) && path.Contains("/Managed/", StringComparison.Ordinal)) return "player-il2cpp";
            if (path.Contains("/mono/", StringComparison.Ordinal) && path.Contains("/Managed/", StringComparison.Ordinal)) return "player-mono";
            if (path.EndsWith(".Extensions.dll", StringComparison.Ordinal)) return "editor";
        }
        if (Path.GetFileName(path).StartsWith("Unity.IL2CPP", StringComparison.Ordinal)) return "tools";
        return null;
    }
    static void FindPackages(JsonElement value, SortedSet<string> names)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var p in value.EnumerateObject())
            {
                if (p.Name.StartsWith("com.", StringComparison.Ordinal)) names.Add(p.Name);
                FindPackages(p.Value, names);
            }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var v in value.EnumerateArray()) FindPackages(v, names);
    }
    static async Task<PackageInfo> ReadPackage(string archive, Bundle bundle, string source, string editor, PackageRequest? expected = null)
    {
        // Read the manifest first without keeping an entire expanded package in memory.
        JsonElement? manifest = null;
        await Archives.Read(archive, async (path, stream, size) =>
        {
            if (path is "package/package.json" or "package.json")
            {
                using var document = JsonDocument.Parse(await Archives.Bytes(stream, size));
                manifest = document.RootElement.Clone();
            }
        });
        var info = manifest ?? throw new InvalidDataException("Package archive lacks package.json.");
        var name = Data.SafeName(info.GetProperty("name").GetString()!);
        var version = info.GetProperty("version").GetString()!;
        if (expected is not null && (name != expected.Name || version != expected.Version)) throw new InvalidDataException("Package archive identity mismatch.");
        bundle.Package(info, source, editor);
        await Archives.Read(archive, async (path, stream, size) =>
        {
            if (path.StartsWith("package/", StringComparison.Ordinal)) path = path[8..];
            if (Bundle.Input(path)) await bundle.Add("packages/" + name + "/" + path, await Archives.Bytes(stream, size), "package");
        });
        return new(name, version, source, editor, Registry.Dependencies(info));
    }
    public static async Task Packages(Options options)
    {
        var requests = Data.Read<PackageRequest[]>(options.Required("plan"));
        var shard = int.Parse(options.Required("shard")); var shards = int.Parse(options.Required("shards"));
        if (shards <= 0 || shard < 0 || shard >= shards) throw new ArgumentException("Invalid package shard.");
        var work = options.Required("work"); Directory.CreateDirectory(work);
        foreach (var request in requests.Where((_, i) => i % shards == shard))
        {
            Console.WriteLine($"Package {request.Name}@{request.Version}");
            var directory = Path.Combine(work, Data.SafeName(request.Name) + "-" + Data.SafeName(request.Version));
            Directory.CreateDirectory(directory);
            var archive = Path.Combine(directory, "package.tgz");
            await Web.Download(request.Url, archive, request.Integrity);
            var bundle = new Bundle(Path.Combine(directory, "bundle"), new("package", request.Name, request.Version, "", request.Url, request.Integrity));
            await ReadPackage(archive, bundle, "registry", "", request);
            File.Delete(archive);
            await bundle.Save(options.Required("output"));
            Directory.Delete(directory, recursive: true);
        }
    }
    static Artifact[] ReadArtifacts(string input) => Directory.Exists(input) ? Directory.EnumerateFiles(input, "*.tar.zst.json", SearchOption.AllDirectories).Select(Data.Read<Artifact>).ToArray() : [];
    public static async Task Publish(Options options)
    {
        var repo = options.Required("repo");
        var input = options.Required("input");
        var state = Data.Read<PlanState>(options.Required("base"));
        var api = $"repos/{repo}/releases/tags/{ReleaseTag}";
        JsonElement release;
        try { release = JsonDocument.Parse(await Commands.Run("gh", "api", api)).RootElement.Clone(); }
        catch (IOException)
        {
            await Commands.Run("gh", "release", "create", ReleaseTag, "--repo", repo, "--title", "Unity metadata archive", "--notes", "Versioned Unity analysis bundles. Machine-readable inventory: catalog.json.", "--latest=false");
            release = JsonDocument.Parse(await Commands.Run("gh", "api", api)).RootElement.Clone();
        }
        var pages = JsonDocument.Parse(await Commands.Run("gh", "api", "--paginate", "--slurp", $"repos/{repo}/releases/{release.GetProperty("id").GetInt64()}/assets?per_page=100"));
        var assets = pages.RootElement.EnumerateArray().SelectMany(p => p.EnumerateArray()).ToDictionary(a => a.GetProperty("name").GetString()!, a => a.Clone(), StringComparer.Ordinal);
        var fresh = ReadArtifacts(input);
        foreach (var artifact in fresh)
        {
            var file = Directory.EnumerateFiles(input, artifact.Name, SearchOption.AllDirectories).Single();
            if (await Data.HashFile(file) != artifact.Sha256) throw new InvalidDataException("Publication checksum mismatch.");
            if (artifact.Size >= 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Bundle exceeds GitHub release asset limit.");
            if (assets.TryGetValue(artifact.Name, out var existing))
            {
                if (!existing.TryGetProperty("digest", out var digest) || digest.GetString() != "sha256:" + artifact.Sha256) throw new InvalidDataException($"Existing asset differs: {artifact.Name}");
                continue;
            }
            if (assets.Count + fresh.Length + 2 >= 1000) throw new InvalidDataException("Release asset capacity would be exceeded.");
            await Commands.Run("gh", "release", "upload", ReleaseTag, file, "--repo", repo);
        }
        static string Key(Artifact a) => JsonSerializer.Serialize(new { a.Origin, a.Format, a.Analysis }, Data.Json);
        var merged = state.Previous.Artifacts.ToDictionary(Key, StringComparer.Ordinal);
        foreach (var artifact in fresh) merged[Key(artifact)] = artifact;
        var catalog = Path.Combine(input, "catalog.json");
        Data.Write(catalog, new Catalog(Data.Format, merged.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray()));
        // Catalog is the last mutable pointer. Its prior version is archived by run ID.
        var generation = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? Data.Hash(File.ReadAllBytes(catalog))[..16];
        var snapshot = Path.Combine(input, $"catalog-{generation}.json");
        File.Copy(catalog, snapshot, overwrite: true);
        await Commands.Run("gh", "release", "upload", ReleaseTag, snapshot, "--repo", repo, "--clobber");
        await Commands.Run("gh", "release", "upload", ReleaseTag, options.Required("selection"), "--repo", repo, "--clobber");
        await Commands.Run("gh", "release", "upload", ReleaseTag, catalog, "--repo", repo, "--clobber");
        Console.WriteLine($"Published catalog with {merged.Count} bundles.");
    }
}
