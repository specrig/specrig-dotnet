using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NJsonSchema;

namespace Specrig;

public sealed class Contract
{
    static readonly ConcurrentDictionary<(string File, string Neighbour, string? Failure), Contract> loaded = new();

    readonly JsonObject paths;
    readonly JsonObject definitions;
    readonly string neighbour;
    readonly string? failure;
    readonly ConcurrentDictionary<string, JsonSchema> compiled = new();

    public Contract(string file, string neighbour, string? failure = null)
    {
        var doc = Yaml.ToJson(File.ReadAllText(file)) as JsonObject
            ?? throw new InvalidDataException($"{file} is not an OpenAPI document");
        paths = doc["paths"] as JsonObject ?? [];
        definitions = new JsonObject();
        foreach (var (name, schema) in doc["components"]?["schemas"] as JsonObject ?? [])
        {
            definitions[name] = Rewritten(schema);
        }
        this.neighbour = neighbour;
        this.failure = failure;
    }

    public static Contract Load(string file, string neighbour, string? failure) =>
        loaded.GetOrAdd((Path.GetFullPath(file), neighbour, failure), key => new Contract(key.File, key.Neighbour, key.Failure));

    string? Template(string verb, string requestPath)
    {
        var wanted = requestPath.Split('/');
        string? best = null;
        var most = -1;
        foreach (var (template, operations) in paths)
        {
            var segments = template.Split('/');
            if (operations?[verb] is null || segments.Length != wanted.Length)
            {
                continue;
            }
            if (!segments.Select((s, i) => s.StartsWith('{') || s == wanted[i]).All(match => match))
            {
                continue;
            }
            var literal = segments.Count(s => !s.StartsWith('{'));
            if (literal > most)
            {
                (best, most) = (template, literal);
            }
        }
        return best;
    }

    // A request is named as the route that takes it, so that it meets the neighbour's input of the same name.
    public string Name(string method, string requestPath) =>
        $"{method} {Template(method.ToLowerInvariant(), requestPath) ?? requestPath}";

    public IReadOnlyList<string> Request(string method, string requestPath, JsonNode? body)
    {
        var verb = method.ToLowerInvariant();
        var template = Template(verb, requestPath);
        if (template is null)
        {
            return [$"{method} {requestPath}: {neighbour} serves no such route"];
        }
        var schema = paths[template]?[verb]?["requestBody"]?["content"]?["application/json"]?["schema"];
        if (body is null || schema is null)
        {
            return [];
        }
        return [.. Validate($"{template} {verb} request", schema, body).Select(e => $"{method} {requestPath} request body: {e}")];
    }

    public IReadOnlyList<string> Response(string method, string requestPath, int status, JsonNode? body)
    {
        var verb = method.ToLowerInvariant();
        var template = Template(verb, requestPath);
        if (template is null)
        {
            return [];
        }
        var where = $"{method} {requestPath} answered {status}";
        var documented = paths[template]?[verb]?["responses"]?[status.ToString()];
        if (documented is null)
        {
            if (status >= 500 && failure is not null)
            {
                if (definitions[failure] is null)
                {
                    throw new InvalidDataException($"{neighbour} has no schema {failure}");
                }
                return [.. Validate($"#{failure}", new JsonObject { ["$ref"] = $"#/definitions/{failure}" }, body).Select(e => $"{where}: {e}")];
            }
            return [$"{where}: {neighbour} does not answer {status} here"];
        }
        var content = documented["content"]?["application/json"]?["schema"];
        if (content is null)
        {
            return [];
        }
        return [.. Validate($"{template} {verb} {status}", content, body).Select(e => $"{where}: {e}")];
    }

    IEnumerable<string> Validate(string key, JsonNode schema, JsonNode? body)
    {
        var check = compiled.GetOrAdd(key, _ =>
        {
            var root = Rewritten(schema) as JsonObject ?? new JsonObject();
            root["definitions"] = definitions.DeepClone();
            return JsonSchema.FromJsonAsync(root.ToJsonString()).GetAwaiter().GetResult();
        });
        return check.Validate(body?.ToJsonString() ?? "{}").Select(e => $"{e.Path} {e.Kind}");
    }

    static JsonNode? Rewritten(JsonNode? schema)
    {
        switch (schema)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[key] = key == "$ref" && value?.GetValue<string>() is { } target && target.StartsWith("#/components/schemas/")
                        ? "#/definitions/" + target["#/components/schemas/".Length..]
                        : Rewritten(value);
                }
                return copy;
            case JsonArray array:
                return new JsonArray([.. array.Select(Rewritten)]);
            default:
                return schema?.DeepClone();
        }
    }
}
