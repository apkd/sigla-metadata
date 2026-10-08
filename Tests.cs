using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Sigla.Metadata;

public static class Tests
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Test failed: " + message);
    }
    public static async Task Run()
    {
        var bytes = File.ReadAllBytes(typeof(Tests).Assembly.Location);
        var extracted = AssemblyExtractor.Extract(bytes, "fixture")!;
        using var document = JsonDocument.Parse(extracted);
        var types = document.RootElement.GetProperty("types").EnumerateArray().ToArray();
        var fixture = types.Single(t => t.GetProperty("type").GetProperty("name").GetString()!.EndsWith("Fixture`1", StringComparison.Ordinal));
        Check(fixture.GetProperty("generics")[0].GetProperty("constraints").GetArrayLength() > 0, "Generic constraints survive extraction");
        Check(fixture.GetProperty("fields").EnumerateArray().Any(f => f.TryGetProperty("constant", out _)), "Constants survive extraction");
        Check(fixture.GetProperty("properties").EnumerateArray().Any(p => p.TryGetProperty("getter", out _) && p.TryGetProperty("setter", out _)), "Property accessors survive extraction");
        Check(fixture.GetProperty("implementations").GetArrayLength() > 0, "Explicit interface implementations survive extraction");
        Check(fixture.GetProperty("methods").EnumerateArray().Any(m => m.GetProperty("signature").GetProperty("genericCount").GetInt32() > 0), "Generic method signatures survive extraction");
        Check(types.Any(t => t.GetProperty("type").GetProperty("name").GetString()!.Contains("+Nested`1", StringComparison.Ordinal)), "Nested names retain their owner");
        Check(AssemblyExtractor.Extract(Encoding.UTF8.GetBytes("native"), "native") is null, "Non-managed files are excluded");
        using var framework = JsonDocument.Parse(AssemblyExtractor.Extract(File.ReadAllBytes(typeof(object).Assembly.Location), "framework")!);
        Check(framework.RootElement.GetProperty("types").GetArrayLength() > 100, "Real framework metadata parses without loading target code");
        Check(Registry.CompareVersions("2.0.0-preview.10", "2.0.0-preview.2") > 0, "Preview numbers sort numerically");
        Check(Registry.CompareVersions("2.0.0", "2.0.0-preview.10") > 0, "Stable sorts after its prereleases");
        Check(UnityVersion.Parse("6000.0.0b10").CompareTo(UnityVersion.Parse("6000.0.0b2")) > 0, "Unity preview versions sort numerically");
        using var package = JsonDocument.Parse("{\"unity\":\"2022.3\",\"unityRelease\":\"2f1\"}");
        Check(!Registry.Compatible(package.RootElement, UnityVersion.Parse("2022.3.1f1")), "Minimum Unity patch is respected");
        Check(Registry.Compatible(package.RootElement, UnityVersion.Parse("6000.0.0b1")), "Newer editor satisfies declared minimum");
        try { Archives.PathName("../outside"); throw new InvalidOperationException("Escaping path accepted"); } catch (InvalidDataException) { }
        var root = Path.Combine(Path.GetTempPath(), "sigla-metadata-test-" + Guid.NewGuid());
        try
        {
            var padded = Path.Combine(root, "padded.tgz");
            Directory.CreateDirectory(root);
            using (var file = File.Create(padded))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
            {
                using (var writer = new System.Formats.Tar.TarWriter(gzip, leaveOpen: true))
                    writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "Code.cs") { DataStream = new MemoryStream("class C {}"u8.ToArray()) });
                gzip.Write(new byte[1024 * 1024]);
            }
            var paths = new List<string>();
            await Archives.Read(padded, (path, _, _) => { paths.Add(path); return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(10));
            Check(paths.Count == 1, "Padded compressed archives finish without waiting on a blocked decoder");
            var bundle = new Bundle(Path.Combine(root, "objects"), new("editor", "fixture", "1.0.0", "revision", "https://example.invalid/archive"));
            await bundle.Add("references/first.dll", bytes, "editor");
            await bundle.Add("references/second.dll", bytes, "framework");
            var source = Encoding.UTF8.GetBytes("// preserved\r\npublic class Example {}\r\n");
            await bundle.Add("packages/example/Code.cs", source, "package");
            await bundle.Add("packages/example/Alias.cs", source, "package");
            try
            {
                await bundle.Add("packages/example/Code.cs", Encoding.UTF8.GetBytes("different contents"));
                throw new InvalidOperationException("Conflicting logical path accepted");
            }
            catch (InvalidDataException) { }
            Check(bundle.Manifest.Entries[0].Hash == bundle.Manifest.Entries[1].Hash, "Duplicate source contents share an object");
            var first = await bundle.Save(Path.Combine(root, "one"));
            var second = await bundle.Save(Path.Combine(root, "two"));
            Check(first.Sha256 == second.Sha256, "Bundle generation is reproducible");
            Check(bundle.Manifest.Entries.Where(e => e.Kind == "assembly").All(e => e.Hash != Data.Hash(bytes)), "Assembly objects contain analysis, not DLLs");
            await Bundle.Verify(Path.Combine(root, "one", first.Name));
            var corrupted = File.ReadAllBytes(Path.Combine(root, "one", first.Name));
            corrupted[^1] ^= 1;
            var bad = Path.Combine(root, "corrupt.tar.zst");
            File.WriteAllBytes(bad, corrupted);
            try { await Bundle.Verify(bad); throw new InvalidOperationException("Corrupt archive accepted"); }
            catch (InvalidDataException) { }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        Console.WriteLine("All metadata, version selection, and bundle tests passed.");
    }
}

public class Fixture<T> : IDisposable where T : class, IDisposable
{
    public const int Constant = 7;
    public T? Value { get; set; }
    public U Echo<U>(T input, U value) where U : struct => value;
    void IDisposable.Dispose() { }
    public class Nested<TInner> { }
}
