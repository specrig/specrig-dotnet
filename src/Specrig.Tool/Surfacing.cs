using System.Diagnostics;

namespace Specrig.Tool;

static class Surfacing
{
    public static async Task<int> Run(string[] args)
    {
        var (dir, flags) = Arguments.Parse(args, "--service");
        var project = Service.Project(dir);
        var surface = await Extract(project, flags.GetValueOrDefault("--service") ?? Service.Name(project));
        foreach (var (file, body) in new[] { ("tests/surface.yaml", surface.Encode()), ("tests/checks.yaml", surface.EncodeChecks()) })
        {
            Console.WriteLine(Surface.Rewrite(Path.Combine(dir, file), body) ? $"~ {file}" : $"= {file}");
        }
        return 0;
    }

    // A project the workspace loads unrestored compiles against nothing.
    public static async Task<Surface> Extract(string project, string service)
    {
        using var restore = Process.Start(new ProcessStartInfo("dotnet", ["restore", project, "-v", "q", "-nologo"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("dotnet does not start");
        var output = await restore.StandardOutput.ReadToEndAsync() + await restore.StandardError.ReadToEndAsync();
        await restore.WaitForExitAsync();
        if (restore.ExitCode != 0)
        {
            throw new InvalidOperationException($"{project} does not restore: {output.Trim()}");
        }
        return await Surface.Extract(project, service);
    }
}

static class Service
{
    public static string Project(string dir)
    {
        var found = new[] { dir, Path.Combine(dir, "src") }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj").Concat(Directory.EnumerateDirectories(d).Where(sub => Path.GetFileName(sub) != "tests").SelectMany(sub => Directory.EnumerateFiles(sub, "*.csproj"))))
            .Where(file => !Path.GetRelativePath(dir, file).Split(Path.DirectorySeparatorChar).Contains("tests"))
            .Distinct()
            .ToList();
        return found switch
        {
            [var one] => one,
            [] => throw new InvalidOperationException($"{Path.GetFullPath(dir)} holds no service project, nor does its src/"),
            _ => throw new InvalidOperationException($"{Path.GetFullPath(dir)} holds {found.Count} projects: {string.Join(", ", found.Select(f => Path.GetRelativePath(dir, f)))}"),
        };
    }

    public static string Name(string project) => Path.GetFileNameWithoutExtension(project).ToLowerInvariant();
}

static class Arguments
{
    public static (string Dir, Dictionary<string, string> Flags) Parse(string[] args, params string[] known)
    {
        var dir = ".";
        var flags = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (known.Contains(args[i]))
            {
                flags[args[i]] = i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"unknown argument {args[i]}");
            }
            else
            {
                dir = args[i];
            }
        }
        return (dir, flags);
    }
}
