using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Specrig;

// An entry whose value is null or empty is left out, as omitempty does in the Go record.
public sealed class Mapping : List<KeyValuePair<string, object?>>
{
    public void Add(string key, object? value) => Add(new KeyValuePair<string, object?>(key, value));
}

public static partial class Yaml
{
    public static string Emit(Mapping root)
    {
        var text = new StringBuilder();
        Write(text, root, 0);
        return text.ToString();
    }

    static void Write(StringBuilder text, Mapping mapping, int indent)
    {
        var pad = new string(' ', indent);
        foreach (var (key, value) in mapping)
        {
            switch (value)
            {
                case null:
                case IReadOnlyCollection<object> { Count: 0 }:
                case Mapping { Count: 0 }:
                    break;
                case string scalar:
                    text.Append(pad).Append(key).Append(": ").Append(Scalar(scalar, indent + 2)).Append('\n');
                    break;
                case Mapping inner:
                    text.Append(pad).Append(key).Append(":\n");
                    Write(text, inner, indent + 2);
                    break;
                case IEnumerable<string> items:
                    text.Append(pad).Append(key).Append(":\n");
                    foreach (var item in items)
                    {
                        text.Append(pad).Append("  - ").Append(Scalar(item, indent + 4)).Append('\n');
                    }
                    break;
                case IEnumerable<Mapping> items:
                    text.Append(pad).Append(key).Append(":\n");
                    foreach (var item in items)
                    {
                        var entry = new StringBuilder();
                        Write(entry, item, indent + 4);
                        text.Append(pad).Append("  - ").Append(entry.ToString(indent + 4, entry.Length - indent - 4));
                    }
                    break;
                default:
                    throw new ArgumentException($"{key}: no YAML for a {value.GetType().Name}");
            }
        }
    }

    static string Scalar(string value, int indent)
    {
        if (Plain(value))
        {
            return value;
        }
        if (!value.Contains('\n') || value.Contains('\r') || value.StartsWith(' ') || value.EndsWith("\n\n"))
        {
            return Quoted(value);
        }
        var clip = value.EndsWith('\n');
        var pad = new string(' ', indent);
        var lines = (clip ? value[..^1] : value).Split('\n');
        return (clip ? "|" : "|-") + string.Concat(lines.Select(line => line.Length == 0 ? "\n" : "\n" + pad + line));
    }

    static bool Plain(string value) =>
        value.Length > 0
        && value.Trim() == value
        && "-?:,[]{}#&*!|>'\"%@`".IndexOf(value[0]) < 0
        && !value.Contains(": ")
        && !value.Contains(" #")
        && !value.EndsWith(':')
        && !value.Any(char.IsControl)
        && !Reserved().IsMatch(value)
        && !Numeric().IsMatch(value);

    static string Quoted(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                _ when char.IsControl(c) => $"\\x{(int)c:x2}",
                _ => c.ToString(),
            });
        }
        return text.Append('"').ToString();
    }

    [GeneratedRegex("^(?i:true|false|yes|no|on|off|y|n|null|~)$")]
    private static partial Regex Reserved();

    [GeneratedRegex(@"^[-+]?(\.?[0-9]|0x|0o|\.inf|\.nan)", RegexOptions.IgnoreCase)]
    private static partial Regex Numeric();

    // An OpenAPI document read as JSON: a plain scalar is typed as YAML types it, a
    // quoted one is a string.
    public static JsonNode? ToJson(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Count == 0 ? null : ToJson(stream.Documents[0].RootNode);
    }

    static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(entry =>
            KeyValuePair.Create(((YamlScalarNode)entry.Key).Value ?? "", ToJson(entry.Value)))),
        YamlSequenceNode sequence => new JsonArray([.. sequence.Children.Select(ToJson)]),
        YamlScalarNode { Style: ScalarStyle.Plain } scalar => Typed(scalar.Value),
        YamlScalarNode scalar => JsonValue.Create(scalar.Value),
        _ => null,
    };

    static JsonNode? Typed(string? value)
    {
        if (value is null or "~" or "null" or "Null" or "NULL" or "")
        {
            return null;
        }
        if (value is "true" or "True" or "TRUE")
        {
            return JsonValue.Create(true);
        }
        if (value is "false" or "False" or "FALSE")
        {
            return JsonValue.Create(false);
        }
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
        {
            return JsonValue.Create(whole);
        }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
        {
            return JsonValue.Create(real);
        }
        return JsonValue.Create(value);
    }
}
