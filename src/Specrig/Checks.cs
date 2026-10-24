using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Specrig;

public sealed class Checks
{
    public List<string> Settings { get; set; } = [];

    public List<string> Optional { get; set; } = [];

    public List<string> Sections { get; set; } = [];

    public List<string> Consumers { get; set; } = [];

    public static Checks? From(string file) => File.Exists(file)
        ? new DeserializerBuilder().WithNamingConvention(LowerCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build()
            .Deserialize<Checks>(File.ReadAllText(file))
        : null;

    // A key under a section the code reads whole is read, the way options bind it.
    public IEnumerable<string> Unread(IEnumerable<string> answered) =>
        answered.Where(key => !Settings.Concat(Optional).Contains(key, StringComparer.OrdinalIgnoreCase)
            && !Sections.Any(section => key.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase)));
}
