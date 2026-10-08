using Sigla.Metadata;

try
{
    if (args.Length == 0) throw new ArgumentException("Use plan, editor, packages, publish, verify, or self-test.");
    var options = new Options(args.Skip(1).ToArray());
    switch (args[0])
    {
        case "plan": await Pipeline.Plan(options); break;
        case "editor": await Pipeline.Editor(options); break;
        case "packages": await Pipeline.Packages(options); break;
        case "publish": await Pipeline.Publish(options); break;
        case "verify": await Bundle.Verify(options.Required("bundle")); break;
        case "self-test": await Tests.Run(); break;
        default: throw new ArgumentException($"Unknown command: {args[0]}");
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

namespace Sigla.Metadata
{
    public sealed class Options(string[] args)
    {
        public string? Get(string key)
        {
            for (var i = 0; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 == args.Length)
                    throw new ArgumentException("Options require --name value pairs.");
                if (args[i] == "--" + key) return args[i + 1];
            }
            return null;
        }
        public string Required(string key) => Get(key) ?? throw new ArgumentException($"Missing --{key}");
    }
}
