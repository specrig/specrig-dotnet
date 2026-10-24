using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace Specrig.Tool;

sealed record Neighbour(string Name, string Origin, string? Setting, string? Contract, string? Failure);

sealed record GrpcNeighbour(string Name, string Origin, string? Setting, IReadOnlyList<string> Descriptors);

sealed record Resource(string Kind, string Name, string Setting);

sealed record Asked(
    string Dir,
    List<(string Name, string? Setting)> Backends,
    List<(string Name, string? Setting)> Grpcs,
    Dictionary<string, List<string>> Serves,
    Dictionary<string, string> Contracts,
    Dictionary<string, string> Failures,
    string? Service);

sealed record Plan(
    string Service,
    string Namespace,
    string Framework,
    string Project,
    string? Checkout,
    bool Central,
    IReadOnlyList<Neighbour> Backends,
    IReadOnlyList<GrpcNeighbour> GrpcBackends,
    IReadOnlyList<Resource> Resources,
    IReadOnlyList<string> Constants,
    IReadOnlyList<string> Running,
    bool Grpc,
    IReadOnlyList<string> GuardCommand);

static class Init
{
    public static readonly Dictionary<string, string> Versions = new()
    {
        ["Microsoft.NET.Test.Sdk"] = "18.11.0",
        ["Reqnroll.xunit.v3"] = "3.3.4",
        ["xunit.v3"] = "3.2.2",
        ["xunit.runner.visualstudio"] = "3.1.5",
    };

    public static async Task<int> Run(string[] args)
    {
        var asked = Parse(args);
        var project = Service.Project(asked.Dir);
        var service = asked.Service ?? Service.Name(project);
        var surface = await Surfacing.Extract(project, service);
        var plan = Planned(asked, project, service, surface);
        foreach (var line in Write(asked.Dir, plan, surface))
        {
            Console.WriteLine(line);
        }
        return 0;
    }

    static Asked Parse(string[] args)
    {
        var dir = ".";
        var backends = new List<(string, string?)>();
        var grpcs = new List<(string, string?)>();
        var serves = new Dictionary<string, List<string>>();
        var contracts = new Dictionary<string, string>();
        var failures = new Dictionary<string, string>();
        string? service = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            (string, string) Pair(string flag) => Value().Split('=', 2) is [var name, var value] ? (name, value) : throw new ArgumentException($"{flag} wants <name>=<value>");
            switch (args[i])
            {
                case "--backend":
                    var backend = Value().Split('=', 2);
                    backends.Add((backend[0], backend.Length > 1 ? backend[1] : null));
                    break;
                case "--grpc":
                    var grpc = Value().Split('=', 2);
                    grpcs.Add((grpc[0], grpc.Length > 1 ? grpc[1] : null));
                    break;
                case "--serves":
                    var (sn, sv) = Pair("--serves");
                    (serves.TryGetValue(sn, out var served) ? served : serves[sn] = []).AddRange(sv.Split(',', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "--contract":
                    var (cn, cv) = Pair("--contract");
                    contracts[cn] = cv;
                    break;
                case "--failure":
                    var (fn, fv) = Pair("--failure");
                    failures[fn] = fv;
                    break;
                case "--service":
                    service = Value();
                    break;
                case var flag when flag.StartsWith("--", StringComparison.Ordinal):
                    throw new ArgumentException($"unknown argument {flag}");
                default:
                    dir = args[i];
                    break;
            }
        }
        foreach (var name in contracts.Keys.Concat(failures.Keys).Where(n => backends.All(b => b.Item1 != n)))
        {
            throw new ArgumentException($"{name} is not a backend; name it with --backend first");
        }
        foreach (var name in serves.Keys.Where(n => grpcs.All(g => g.Item1 != n)))
        {
            throw new ArgumentException($"{name} is not a gRPC neighbour; name it with --grpc first");
        }
        return new Asked(dir, backends, grpcs, serves, contracts, failures, service);
    }

    static Plan Planned(Asked asked, string project, string service, Surface surface)
    {
        var (dir, backends, _, _, contracts, failures, _) = asked;
        bool Read(string key) => surface.Settings.Contains(key) || surface.Sections.Any(s => key.StartsWith(s + ":", StringComparison.OrdinalIgnoreCase));
        foreach (var (_, setting) in backends.Concat(asked.Grpcs).Where(b => b.Setting is not null && !Read(b.Setting)))
        {
            throw new ArgumentException($"the service reads no setting {setting}; it reads {string.Join(", ", surface.Settings)}");
        }
        if (backends.Count == 0 && surface.Calls.Where(c => !c.StartsWith('/')).ToList() is { Count: > 0 } http)
        {
            throw new ArgumentException($"the service calls {string.Join(", ", http)}: name the neighbour with --backend <name>[=<setting>]");
        }
        var grpcs = Served(asked, surface, service);
        var xml = XDocument.Load(project);
        var packages = xml.Descendants("PackageReference").Select(p => (string?)p.Attribute("Include") ?? "").ToList();
        bool Uses(string prefix) => packages.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var resources = new List<Resource>();
        foreach (var key in surface.Settings.Where(k => k.StartsWith("ConnectionStrings:", StringComparison.Ordinal)))
        {
            var name = key["ConnectionStrings:".Length..].ToLowerInvariant();
            var kind = name.Contains("rabbit") || name.Contains("amqp") || name.Contains("bus") ? Uses("RabbitMQ.Client") ? "RabbitBus" : null
                : name.Contains("redis") || name.Contains("cache") ? Uses("StackExchange.Redis") ? "Redis" : null
                : Uses("Npgsql") ? "Postgres" : null;
            if (kind is not null)
            {
                var property = kind switch { "RabbitBus" => "Bus", "Redis" => "Cache", _ => "Database" };
                resources.Add(new Resource(kind, resources.Any(r => r.Name == property) ? property + Pascal(name) : property, key));
            }
        }
        var answered = backends.Concat(asked.Grpcs).Select(b => b.Setting).Concat(resources.Select(r => r.Setting)).OfType<string>().ToHashSet();
        var shipped = Shipped(Path.Combine(Path.GetDirectoryName(project)!, "appsettings.json"));
        var tests = Path.Combine(dir, "tests");
        var local = Checkout();
        return new Plan(
            service,
            Pascal(Path.GetFileNameWithoutExtension(project)),
            xml.Descendants("TargetFramework").Select(t => t.Value).FirstOrDefault() ?? "net10.0",
            Path.GetRelativePath(tests, project).Replace('\\', '/'),
            local is null ? null : Path.GetRelativePath(tests, local).Replace('\\', '/'),
            Central(dir),
            [.. backends.Select(b => new Neighbour(Pascal(b.Name), $"https://{b.Name}.{service}.test", b.Setting, contracts.GetValueOrDefault(b.Name), failures.GetValueOrDefault(b.Name)))],
            grpcs,
            resources,
            [.. surface.Settings.Where(k => !answered.Contains(k) && !shipped.Contains(k, StringComparer.OrdinalIgnoreCase))],
            [.. surface.Consumers],
            Uses("Grpc.AspNetCore") || Uses("Grpc.Net.Client") || grpcs.Count > 0,
            local is null
                ? ["dotnet", "specrig"]
                : ["dotnet", "run", "--project", Path.GetRelativePath(dir, Path.Combine(local, "src", "Specrig.Tool")).Replace('\\', '/'), "--"]);
    }

    static List<GrpcNeighbour> Served(Asked asked, Surface surface, string service)
    {
        var called = surface.Descriptors.Keys.ToList();
        var taken = new Dictionary<string, string>();
        var neighbours = new List<GrpcNeighbour>();
        foreach (var (name, setting) in asked.Grpcs)
        {
            List<string> serves;
            if (asked.Serves.TryGetValue(name, out var named))
            {
                serves = [];
                foreach (var value in named)
                {
                    var matched = called.Where(s => s == value || s.StartsWith(value + ".", StringComparison.Ordinal)).ToList();
                    serves.AddRange(matched.Count > 0 ? matched
                        : throw new ArgumentException($"{value} is no gRPC service the service calls, nor their package; it calls {string.Join(", ", called)}"));
                }
            }
            else if (asked.Grpcs.Count == 1 && called.Count > 0)
            {
                serves = called;
            }
            else
            {
                throw new ArgumentException(called.Count == 0
                    ? $"the service calls no gRPC service, so {name} serves it nothing"
                    : $"which of {string.Join(", ", called)} does {name} serve? name them with --serves {name}=<package or service>");
            }
            foreach (var served in serves)
            {
                if (taken.TryGetValue(served, out var other) && other != name)
                {
                    throw new ArgumentException($"{served} is served by both {other} and {name}");
                }
                taken[served] = name;
            }
            neighbours.Add(new GrpcNeighbour(Pascal(name), $"https://{name}.{service}.test", setting,
                [.. serves.Distinct().Order(StringComparer.Ordinal).Select(s => $"global::{surface.Descriptors[s]}.Descriptor")]));
        }
        if (called.Where(s => !taken.ContainsKey(s)).ToList() is { Count: > 0 } unserved)
        {
            throw new ArgumentException($"the service calls {string.Join(", ", unserved)}, which no neighbour serves: name it with --grpc <name>[=<setting>] and --serves <name>=<package or service>");
        }
        return neighbours;
    }

    public static int Agents(string[] args)
    {
        var dir = args switch { [] => ".", [var given] => given, _ => throw new ArgumentException("agents wants [dir]") };
        var file = Path.Combine(dir, "AGENTS.md");
        if (!File.Exists(file))
        {
            throw new ArgumentException("there is no AGENTS.md here: `dotnet specrig init` writes one that brings specrig.md in");
        }
        Console.WriteLine(WriteSpecrigMd(dir));
        switch (Shared.Verdict(File.ReadAllText(file)))
        {
            case "stale":
                throw new InvalidOperationException("AGENTS.md still carries Rule #11, which specrig.md holds now: keep the service's own sections in it and delete the rest");
            case "unreferenced":
                throw new InvalidOperationException($"AGENTS.md does not bring specrig.md in: add the line\n\n{Templates.Reference()}\n\nwhere an agent reads it before the first edit");
        }
        return 0;
    }

    public static string WriteSpecrigMd(string dir)
    {
        var body = Templates.SpecrigMd(Rules.Areas(Path.Combine(dir, "specrig.yaml")), Rules.GuardOf(Path.Combine(dir, "specrig.yaml")) ?? ["dotnet", "specrig"]);
        var file = Path.Combine(dir, "specrig.md");
        var before = File.Exists(file) ? File.ReadAllText(file) : null;
        if (before == body)
        {
            return "= specrig.md";
        }
        File.WriteAllText(file, body);
        return before is null ? "+ specrig.md" : "~ specrig.md";
    }

    static string AgentsMd(string dir, string service)
    {
        var file = Path.Combine(dir, "AGENTS.md");
        if (!File.Exists(file))
        {
            File.WriteAllText(file, Templates.AgentsStub(service, File.Exists(Path.Combine(dir, "..", "AGENTS.md"))));
            return "+ AGENTS.md";
        }
        var text = File.ReadAllText(file);
        var left = Shared.Verdict(text) == "stale"
            ? "; it still carries Rule #11, which specrig.md holds now: keep the service's own sections in it and delete the rest"
            : "";
        if (Shared.References(text))
        {
            return $"= AGENTS.md — kept{left}";
        }
        var gap = text is "" ? "" : text.EndsWith('\n') ? "\n" : "\n\n";
        File.WriteAllText(file, text + gap + Templates.Reference() + "\n");
        return $"~ AGENTS.md: the line that brings specrig.md in, at its end{left}";
    }

    static IEnumerable<string> Write(string dir, Plan plan, Surface surface)
    {
        var files = new (string Path, string Body)[]
        {
            ("tests/Specs.csproj", Templates.Project(plan)),
            ("tests/reqnroll.json", Templates.Reqnroll),
            ("tests/internal/Harness.cs", Templates.Harness(plan)),
            ("tests/internal/Boot.cs", Templates.Boot(plan)),
        };
        foreach (var (path, body) in files)
        {
            var file = Path.Combine(dir, path);
            if (File.Exists(file))
            {
                yield return $"= {path} — kept";
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, body);
            yield return $"+ {path}";
        }
        var marker = Path.Combine(dir, "specrig.yaml");
        var guard = $"guard: [{string.Join(", ", plan.GuardCommand)}]";
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, guard + "\n");
            yield return "+ specrig.yaml";
        }
        else if (Rules.GuardOf(marker) is null)
        {
            yield return $"  specrig.yaml names no guard: add `{guard}` so the specrig plugin can ask it";
        }
        yield return AgentsMd(dir, plan.Service);
        yield return WriteSpecrigMd(dir);
        Directory.CreateDirectory(Path.Combine(dir, "tests", "feature"));
        Directory.CreateDirectory(Path.Combine(dir, "tests", "steps"));
        var claude = Path.Combine(dir, "CLAUDE.md");
        if (!File.Exists(claude))
        {
            File.CreateSymbolicLink(claude, "AGENTS.md");
            yield return "+ CLAUDE.md → AGENTS.md";
        }
        foreach (var (path, body) in new[] { ("tests/surface.yaml", surface.Encode()), ("tests/checks.yaml", surface.EncodeChecks()) })
        {
            if (Surface.Rewrite(Path.Combine(dir, path), body))
            {
                yield return $"~ {path}";
            }
        }
        var ignore = Path.Combine(dir, ".gitignore");
        var ignored = File.Exists(ignore) ? File.ReadAllText(ignore) : "";
        var missing = new[] { "bin/", "obj/", "TestResults/" }.Where(line => !ignored.Split('\n').Contains(line)).ToList();
        if (missing.Count > 0)
        {
            File.WriteAllText(ignore, ignored + (ignored is "" || ignored.EndsWith('\n') ? "" : "\n") + string.Join("\n", missing) + "\n");
            yield return $"~ .gitignore: {string.Join(", ", missing)}";
        }
        if (plan.Central)
        {
            yield return $"  Directory.Packages.props needs: {string.Join(", ", Templates.Packages(plan).Select(p => $"{p.Name} {p.Version}"))}";
        }
    }

    static HashSet<string> Shipped(string appsettings)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(appsettings))
        {
            return keys;
        }
        void Walk(JsonElement element, string prefix)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, prefix == "" ? property.Name : $"{prefix}:{property.Name}");
                }
            }
            else if (prefix != "")
            {
                keys.Add(prefix);
            }
        }
        Walk(JsonDocument.Parse(File.ReadAllText(appsettings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement, "");
        return keys;
    }

    static string? Checkout()
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Specrig.slnx")))
            {
                return dir.FullName;
            }
        }
        return null;
    }

    static bool Central(string dir)
    {
        for (var at = new DirectoryInfo(Path.GetFullPath(dir)); at is not null; at = at.Parent)
        {
            var props = Path.Combine(at.FullName, "Directory.Packages.props");
            if (File.Exists(props))
            {
                return File.ReadAllText(props).Contains("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
            }
        }
        return false;
    }

    static string Pascal(string name) =>
        string.Concat(name.Split('-', '_', '.', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}
