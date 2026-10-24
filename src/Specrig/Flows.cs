using System.Text.Json.Nodes;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Specrig;

public sealed record Run(string Case, IReadOnlyList<Record> Records);

public static class Flows
{
    public const string Format = "flows/1";

    const string Header =
        "# Generated from the component suite by specrig: every whole run rewrites the\n" +
        "# structure; only the description fields are yours.\n";

    public sealed class Doc
    {
        public string Specrig { get; set; } = Format;
        public string Service { get; set; } = "";
        public List<Input> Inputs { get; set; } = [];
        public List<Output>? Outputs { get; set; }
        public List<Orphan>? Orphaned { get; set; }
    }

    public sealed class Input
    {
        public string Message { get; set; } = "";
        public string? Description { get; set; }
        public List<string>? Fields { get; set; }
        public SortedDictionary<string, List<string>>? Needs { get; set; }
        public List<Branch> Branches { get; set; } = [];

        internal string Anchor => Message;
    }

    public sealed class Branch
    {
        public List<string>? Sends { get; set; }
        public SortedDictionary<string, List<string>>? Sent { get; set; }
        public string? Refused { get; set; }
        public string? Description { get; set; }
        public List<string> Cases { get; set; } = [];

        internal string AnchorIn(Input input) =>
            $"{input.Message} -> {string.Join(", ", Sends ?? [])}" + (Refused is null ? "" : $" (refused {Refused})");
    }

    public sealed class Output
    {
        public string Message { get; set; } = "";
        public List<string>? Fields { get; set; }
    }

    public sealed class Orphan
    {
        public string Anchor { get; set; } = "";
        public string? Title { get; set; }
        public string? Description { get; set; }
    }

    public static Doc Build(string service, IEnumerable<Run> runs)
    {
        var inputs = new SortedDictionary<string, Input>(StringComparer.Ordinal);
        var branches = new Dictionary<string, Dictionary<(string Sends, string Refusal), Branch>>();
        var fields = new Dictionary<string, SortedSet<string>>();
        var outputs = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var received = new Dictionary<Branch, Shape>();
        var sentFields = new Dictionary<Branch, SortedDictionary<string, SortedSet<string>>>();

        foreach (var run in runs.OrderBy(r => r.Case, StringComparer.Ordinal))
        {
            var records = run.Records.OrderBy(r => r.Seq).ToList();
            var marked = records.Any(r => r.Step != "");
            bool Acting(Record r) => !marked || r.Step == "Action";
            var last = -1;
            var sent = new SortedDictionary<int, List<string>>();
            var carried = new Dictionary<int, SortedDictionary<string, SortedSet<string>>>();
            for (var i = 0; i < records.Count; i++)
            {
                var r = records[i];
                var message = $"{r.Kind} {r.Name}";
                if (r.Dir == "in" && Acting(r))
                {
                    last = i;
                    sent[i] = [];
                    carried[i] = new(StringComparer.Ordinal);
                    if (!inputs.ContainsKey(message))
                    {
                        inputs[message] = new Input { Message = message };
                        branches[message] = [];
                        fields[message] = new(StringComparer.Ordinal);
                    }
                    Paths(r.Request, fields[message]);
                }
                else if (r.Dir == "out")
                {
                    Paths(r.Request, outputs.TryGetValue(message, out var shape) ? shape : outputs[message] = new(StringComparer.Ordinal));
                    if (last >= 0 && Acting(r))
                    {
                        sent[last].Add(message);
                        var into = carried[last];
                        Paths(r.Request, into.TryGetValue(message, out var of) ? of : into[message] = new(StringComparer.Ordinal));
                    }
                }
            }
            foreach (var (i, sends) in sent)
            {
                var r = records[i];
                var message = $"{r.Kind} {r.Name}";
                var key = (string.Join("\n", Passes(sends)), r.Error ?? "");
                if (!branches[message].TryGetValue(key, out var branch))
                {
                    branch = new Branch
                    {
                        Sends = key.Item1 == "" ? null : [.. key.Item1.Split('\n')],
                        Refused = key.Item2 == "" ? null : key.Item2,
                    };
                    branches[message][key] = branch;
                    received[branch] = new Shape();
                    sentFields[branch] = new(StringComparer.Ordinal);
                }
                if (!branch.Cases.Contains(run.Case))
                {
                    branch.Cases.Add(run.Case);
                }
                var got = new SortedSet<string>(StringComparer.Ordinal);
                Paths(r.Request, got);
                received[branch].Add(got);
                foreach (var (to, carriedFields) in carried[i])
                {
                    var all = sentFields[branch].TryGetValue(to, out var known) ? known : sentFields[branch][to] = new(StringComparer.Ordinal);
                    all.UnionWith(carriedFields);
                }
            }
        }

        var doc = new Doc { Service = service };
        foreach (var (message, input) in inputs)
        {
            input.Fields = fields[message].Count > 0 ? [.. fields[message]] : null;
            input.Needs = Needs(received, [.. branches[message].Values.Where(b => b.Refused is null)]);
            foreach (var branch in branches[message].Values)
            {
                branch.Cases.Sort(StringComparer.Ordinal);
                var carried = sentFields[branch].Where(entry => entry.Value.Count > 0).ToList();
                branch.Sent = carried.Count == 0 ? null : new(carried.ToDictionary(e => e.Key, e => e.Value.ToList()), StringComparer.Ordinal);
            }
            input.Branches = [.. branches[message].Values.OrderBy(b => b.AnchorIn(input), StringComparer.Ordinal)];
            doc.Inputs.Add(input);
        }
        if (outputs.Count > 0)
        {
            doc.Outputs = [.. outputs.Select(o => new Output { Message = o.Key, Fields = o.Value.Count > 0 ? [.. o.Value] : null })];
        }
        return doc;
    }

    sealed class Shape
    {
        public SortedSet<string>? Every;
        public readonly SortedSet<string> Any = new(StringComparer.Ordinal);

        public void Add(SortedSet<string> got)
        {
            if (Every is null)
            {
                Every = new(got, StringComparer.Ordinal);
            }
            else
            {
                Every.IntersectWith(got);
            }
            Any.UnionWith(got);
        }
    }

    // A need splits the answered branches exactly, or fixtures naming a unit in most
    // cases would make the unit a need of the promo redeemed in some of them.
    static SortedDictionary<string, List<string>>? Needs(Dictionary<Branch, Shape> received, List<Branch> answered)
    {
        var needs = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var message in answered.SelectMany(b => b.Sends ?? []).Distinct())
        {
            var with = answered.Where(b => b.Sends?.Contains(message) == true).ToList();
            var without = answered.Except(with).ToList();
            if (without.Count == 0)
            {
                continue;
            }
            var split = new SortedSet<string>(received[with[0]].Every ?? [], StringComparer.Ordinal);
            foreach (var b in with.Skip(1))
            {
                split.IntersectWith(received[b].Every ?? []);
            }
            foreach (var b in without)
            {
                split.ExceptWith(received[b].Any);
            }
            if (split.Count > 0)
            {
                needs[message] = [.. split];
            }
        }
        return needs.Count > 0 ? needs : null;
    }

    static List<string> Passes(List<string> sends)
    {
        var passes = new List<string>(sends);
        for (var folded = true; folded;)
        {
            folded = false;
            for (var size = 1; size <= passes.Count / 2 && !folded; size++)
            {
                for (var i = 0; i + 2 * size <= passes.Count; i++)
                {
                    if (passes.Skip(i).Take(size).SequenceEqual(passes.Skip(i + size).Take(size)))
                    {
                        passes.RemoveRange(i + size, size);
                        folded = true;
                        break;
                    }
                }
            }
        }
        return passes;
    }

    static void Paths(JsonNode? body, SortedSet<string> into, string prefix = "")
    {
        switch (body)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    Paths(child, into, prefix == "" ? key : $"{prefix}.{key}");
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Paths(child, into, $"{prefix}[]");
                }
                break;
            case JsonValue when prefix != "":
                into.Add(prefix);
                break;
        }
    }

    public static Doc Reconcile(Doc? old, Doc fresh)
    {
        var prose = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in old?.Inputs ?? [])
        {
            if (input.Description is not null)
            {
                prose[input.Anchor] = input.Description;
            }
            foreach (var branch in input.Branches.Where(b => b.Description is not null))
            {
                prose[branch.AnchorIn(input)] = branch.Description!;
            }
        }
        var orphaned = new List<Orphan>();
        foreach (var orphan in old?.Orphaned ?? [])
        {
            if (prose.ContainsKey(orphan.Anchor))
            {
                orphaned.Add(orphan);
            }
            else if (orphan.Description is not null)
            {
                prose[orphan.Anchor] = orphan.Description;
            }
        }
        var used = new HashSet<string>();
        foreach (var input in fresh.Inputs)
        {
            if (prose.TryGetValue(input.Anchor, out var description))
            {
                input.Description = description;
                used.Add(input.Anchor);
            }
            foreach (var branch in input.Branches)
            {
                if (prose.TryGetValue(branch.AnchorIn(input), out var said))
                {
                    branch.Description = said;
                    used.Add(branch.AnchorIn(input));
                }
            }
        }
        orphaned.AddRange(prose.Where(p => !used.Contains(p.Key)).Select(p => new Orphan { Anchor = p.Key, Description = p.Value }));
        fresh.Orphaned = orphaned.Count > 0 ? orphaned : null;
        return fresh;
    }

    public static Doc? Read(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }
        return new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<Doc?>(File.ReadAllText(file));
    }

    public static string Encode(Doc doc) => Header + Yaml.Emit(new Mapping
    {
        { "specrig", doc.Specrig },
        { "service", doc.Service },
        { "inputs", doc.Inputs.Select(input => new Mapping
            {
                { "message", input.Message },
                { "description", input.Description },
                { "fields", input.Fields },
                { "needs", Lists(input.Needs) },
                { "branches", input.Branches.Select(branch => new Mapping
                    {
                        { "sends", branch.Sends },
                        { "sent", Lists(branch.Sent) },
                        { "refused", branch.Refused },
                        { "description", branch.Description },
                        { "cases", branch.Cases },
                    }).ToList() },
            }).ToList() },
        { "outputs", doc.Outputs?.Select(output => new Mapping
            {
                { "message", output.Message },
                { "fields", output.Fields },
            }).ToList() },
        { "orphaned", doc.Orphaned?.Select(orphan => new Mapping
            {
                { "anchor", orphan.Anchor },
                { "title", orphan.Title },
                { "description", orphan.Description },
            }).ToList() },
    });

    static Mapping? Lists(SortedDictionary<string, List<string>>? lists)
    {
        if (lists is null)
        {
            return null;
        }
        var mapping = new Mapping();
        foreach (var (key, values) in lists)
        {
            mapping.Add(key, values);
        }
        return mapping;
    }

    public static int Write(string file, string service, IEnumerable<Run> runs)
    {
        var old = Read(file);
        var fresh = Build(service, runs);
        if (fresh.Inputs.Count == 0 && (old?.Inputs.Count ?? 0) == 0)
        {
            return 0;
        }
        var merged = Reconcile(old, fresh);
        var body = Encode(merged);
        if (!File.Exists(file) || File.ReadAllText(file) != body)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, body);
        }
        return merged.Orphaned?.Count ?? 0;
    }
}
