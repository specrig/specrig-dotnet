using Specrig.Tool;

const string usage = """
    specrig init [dir] [--backend <name>[=<setting>]] [--contract <name>=<path>] [--failure <name>=<schema>]
                 [--grpc <name>[=<setting>]] [--serves <name>=<package or service>] [--service <name>]
    specrig surface [dir] [--service <name>]
    specrig agents [dir]
    specrig rules

    init lays the harness out beside a service; surface writes tests/surface.yaml and
    tests/checks.yaml from the service's code; agents writes specrig.md, Rule #11 for
    the service, and fails while AGENTS.md does not bring it in; rules prints rules/1
    of the specrig protocol for the service in the working directory, for the plugin
    that enforces the modes.
    """;

try
{
    return args switch
    {
        ["init", .. var rest] => await Init.Run(rest),
        ["surface", .. var rest] => await Surfacing.Run(rest),
        ["agents", .. var rest] => Init.Agents(rest),
        ["rules", .. var rest] => Rules.Run(rest, Console.Out),
        [var command, ..] when !command.StartsWith('-') => Unknown(command),
        _ => Usage(),
    };
}
catch (Exception e) when (e is InvalidOperationException or ArgumentException or FileNotFoundException)
{
    Console.Error.WriteLine($"Error: {e.Message}");
    return 1;
}

int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command \"{command}\"");
    return Usage();
}

int Usage()
{
    Console.Error.WriteLine(usage);
    return 2;
}
