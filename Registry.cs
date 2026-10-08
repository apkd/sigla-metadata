using System.Collections.Concurrent;
using System.Text.Json;

namespace Sigla.Metadata;

public static class Registry
{
    static readonly ConcurrentDictionary<string, Task<JsonElement?>> Metadata = new(StringComparer.Ordinal);
    static readonly SemaphoreSlim Capacity = new(8);
    public static SortedDictionary<string, string> Dependencies(JsonElement value)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (value.TryGetProperty("dependencies", out var deps))
            foreach (var property in deps.EnumerateObject()) result.Add(property.Name, property.Value.GetString()!);
        return result;
    }
    static Task<JsonElement?> Get(string name) => Metadata.GetOrAdd(name, async n =>
    {
        Data.SafeName(n);
        await Capacity.WaitAsync();
        try { return await Web.Json("https://packages.unity.com/" + Uri.EscapeDataString(n), optional: true); }
        finally { Capacity.Release(); }
    });
    public static bool Compatible(JsonElement package, UnityVersion editor)
    {
        if (!package.TryGetProperty("unity", out var unity) || string.IsNullOrEmpty(unity.GetString())) return true;
        var release = package.TryGetProperty("unityRelease", out var r) && !string.IsNullOrEmpty(r.GetString()) ? r.GetString() : "0a0";
        return UnityVersion.Parse(unity.GetString() + "." + release).CompareTo(editor) <= 0;
    }
    public static int CompareVersions(string left, string right)
    {
        var a = left.Split('+')[0].Split('-', 2);
        var b = right.Split('+')[0].Split('-', 2);
        var compare = Version.Parse(a[0]).CompareTo(Version.Parse(b[0]));
        if (compare != 0) return compare;
        if (a.Length != b.Length) return a.Length == 1 ? 1 : -1;
        if (a.Length == 1) return 0;
        var aa = a[1].Split('.'); var bb = b[1].Split('.');
        for (var i = 0; i < Math.Min(aa.Length, bb.Length); i++)
        {
            var an = long.TryParse(aa[i], out var av); var bn = long.TryParse(bb[i], out var bv);
            compare = an && bn ? av.CompareTo(bv) : an != bn ? an ? -1 : 1 : StringComparer.Ordinal.Compare(aa[i], bb[i]);
            if (compare != 0) return compare;
        }
        return aa.Length.CompareTo(bb.Length);
    }
    public static async Task<PackagePlan> Plan(IEnumerable<Artifact> editors)
    {
        var artifacts = editors.ToArray();
        await Task.WhenAll(artifacts.SelectMany(e => e.PackageNames).Distinct(StringComparer.Ordinal).Select(Get));
        var requests = new SortedDictionary<string, PackageRequest>(StringComparer.Ordinal);
        var selections = new List<PackageSelection>();
        var unavailable = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts.OrderBy(e => e.Origin.Version, StringComparer.Ordinal))
        {
            var editor = UnityVersion.Parse(artifact.Origin.Version);
            var bundled = artifact.Packages.ToDictionary(p => p.Name, StringComparer.Ordinal);
            foreach (var name in artifact.PackageNames)
            {
                if (bundled.TryGetValue(name, out var builtIn))
                {
                    selections.Add(new(artifact.Origin.Version, name, builtIn.Version, "bundled", "editor"));
                    foreach (var dep in builtIn.Dependencies)
                        await Include(dep.Key, dep.Value, "dependency", required: true);
                    continue;
                }
                var metadata = await Get(name);
                if (metadata is null || !metadata.Value.TryGetProperty("versions", out var versions))
                {
                    unavailable.Add($"{artifact.Origin.Version}: {name}: no public registry metadata");
                    continue;
                }
                var candidates = versions.EnumerateObject().Where(v => Compatible(v.Value, editor))
                    .OrderByDescending(v => v.Name, Comparer<string>.Create(CompareVersions)).ToArray();
                foreach (var preview in new[] { false, true })
                {
                    foreach (var candidate in candidates.Where(v => v.Name.Contains('-') == preview).Take(1))
                        await Include(name, candidate.Name, preview ? "preview" : "stable", required: true);
                }
            }
            async Task Include(string name, string version, string channel, bool required)
            {
                if (selections.Any(s => s.Editor == artifact.Origin.Version && s.Name == name && s.Version == version)) return;
                if (bundled.TryGetValue(name, out var local) && local.Version == version)
                {
                    selections.Add(new(artifact.Origin.Version, name, version, channel, "editor"));
                    foreach (var dep in local.Dependencies) await Include(dep.Key, dep.Value, "dependency", true);
                    return;
                }
                var metadata = await Get(name);
                if (metadata is null || !metadata.Value.TryGetProperty("versions", out var versions) || !versions.TryGetProperty(version, out var node))
                {
                    var error = $"{artifact.Origin.Version}: {name}@{version}: exact dependency unavailable";
                    if (required) throw new InvalidDataException(error);
                    unavailable.Add(error);
                    return;
                }
                if (!Compatible(node, editor)) throw new InvalidDataException($"{name}@{version} is incompatible with {artifact.Origin.Version}");
                var dist = node.GetProperty("dist");
                var integrity = dist.TryGetProperty("integrity", out var i) ? i.GetString() : dist.TryGetProperty("shasum", out var sha) ? "sha1-" + Convert.ToBase64String(Convert.FromHexString(sha.GetString()!)) : null;
                var key = name + "@" + version;
                var targets = requests.TryGetValue(key, out var existing) ? existing.Editors.Append(artifact.Origin.Version).Distinct().Order().ToArray() : [artifact.Origin.Version];
                requests[key] = new(name, version, dist.GetProperty("tarball").GetString()!, integrity, targets);
                selections.Add(new(artifact.Origin.Version, name, version, channel, "registry"));
                foreach (var dep in Dependencies(node)) await Include(dep.Key, dep.Value, "dependency", true);
            }
        }
        return new(requests.Values.ToArray(), selections.Distinct().OrderBy(s => s.Editor, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Version, StringComparer.Ordinal).ToArray(), unavailable.ToArray());
    }
}
