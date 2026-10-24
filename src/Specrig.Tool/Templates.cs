using System.Reflection;

namespace Specrig.Tool;

static class Templates
{
    public static string Version => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    public static IEnumerable<(string Name, string Version)> Packages(Plan plan)
    {
        foreach (var (name, version) in Init.Versions)
        {
            yield return (name, version);
        }
        if (plan.Checkout is null)
        {
            foreach (var name in Specrig(plan))
            {
                yield return (name, Version);
            }
        }
    }

    static IEnumerable<string> Specrig(Plan plan)
    {
        yield return "Specrig.Reqnroll";
        foreach (var kind in plan.Resources.Select(r => r.Kind).Distinct())
        {
            yield return kind switch { "RabbitBus" => "Specrig.RabbitMQ", var other => $"Specrig.{other}" };
        }
        if (plan.Grpc)
        {
            yield return "Specrig.Grpc";
        }
    }

    public static string Project(Plan plan)
    {
        var packages = Packages(plan).Select(p => plan.Central
            ? $"""    <PackageReference Include="{p.Name}" />"""
            : $"""    <PackageReference Include="{p.Name}" Version="{p.Version}" />""");
        var checkout = plan.Checkout is null
            ? ""
            : $"""

              <PropertyGroup>
                <SpecrigCheckout Condition="'$(SpecrigCheckout)' == ''">{plan.Checkout}</SpecrigCheckout>
              </PropertyGroup>
            """;
        var local = plan.Checkout is null ? [] : Specrig(plan).Select(p => $"""    <ProjectReference Include="$(SpecrigCheckout)/src/{p}/{p}.csproj" />""");
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{plan.Framework}</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>{plan.Namespace}.Specs</AssemblyName>
                <RootNamespace>{plan.Namespace}.Specs</RootNamespace>
                <OutputType>Exe</OutputType>
                <IsPackable>false</IsPackable>
              </PropertyGroup>{checkout}
              <ItemGroup>
            {string.Join("\n", packages)}
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="{plan.Project}" />
            {string.Join("\n", local)}
              </ItemGroup>
              <ItemGroup>
                <Using Include="Xunit" />
                <None Update="reqnroll.json" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
            </Project>

            """.Replace("\n\n  </ItemGroup>", "\n  </ItemGroup>");
    }

    public const string Reqnroll = """
        {
          "$schema": "https://schemas.reqnroll.net/reqnroll-config-latest.json",
          "bindingAssemblies": [
            { "assembly": "Specrig.Reqnroll" }
          ]
        }

        """;

    public static string Harness(Plan plan)
    {
        var members = new List<string>();
        foreach (var backend in plan.Backends)
        {
            var lines = new[]
            {
                backend.Setting is null ? null : $"Setting = \"{backend.Setting}\",",
                backend.Contract is null ? null : $"Contract = \"{backend.Contract}\",",
                backend.Failure is null ? null : $"Failure = \"{backend.Failure}\",",
            }.OfType<string>().ToList();
            members.Add(lines.Count == 0
                ? $"    public Backend {backend.Name} {{ get; }} = new(\"{backend.Origin}\");"
                : $"    public Backend {backend.Name} {{ get; }} = new(\"{backend.Origin}\")\n    {{\n{string.Join("\n", lines.Select(l => "        " + l))}\n    }};");
        }
        foreach (var grpc in plan.GrpcBackends)
        {
            var created = grpc.Descriptors.Count == 1
                ? $"new(\"{grpc.Origin}\", {grpc.Descriptors[0]})"
                : $"new(\"{grpc.Origin}\",\n{string.Join(",\n", grpc.Descriptors.Select(d => "        " + d))})";
            members.Add(grpc.Setting is null
                ? $"    public GrpcBackend {grpc.Name} {{ get; }} = {created};"
                : $"    public GrpcBackend {grpc.Name} {{ get; }} = {created}\n    {{\n        Setting = \"{grpc.Setting}\",\n    }};");
        }
        foreach (var resource in plan.Resources)
        {
            members.Add($"    public {resource.Kind} {resource.Name} {{ get; }} = new(\"{resource.Setting}\");");
        }
        if (plan.Constants.Count > 0)
        {
            members.Add($"    protected override Settings Constants => new() {{ {string.Join(", ", plan.Constants.Select(c => $"[\"{c}\"] = \"\""))} }};");
        }
        if (plan.Running.Count > 0)
        {
            members.Add($"    protected override Workers Running => [{string.Join(", ", plan.Running.Select(r => $"typeof(global::{r})"))}];");
        }
        return $$"""
            using Specrig;

            [assembly: CollectionBehavior(DisableTestParallelization = true)]

            namespace {{plan.Namespace}}.Specs.Internal;

            public sealed class Harness : Harness<Program>
            {
                public override string Service => "{{plan.Service}}";

            {{string.Join("\n\n", members)}}
            }

            """;
    }

    public static string Boot(Plan plan) => $$"""
        namespace {{plan.Namespace}}.Specs.Internal;

        public class Boot
        {
            [Fact]
            public void The_service_boots()
            {
                using var harness = new Harness();
                using var scope = harness.Scope();

                Assert.Empty(harness.Failures());
            }
        }

        """;

    public static string SpecrigMd(IReadOnlyList<Rules.Area> areas, IReadOnlyList<string> guard)
    {
        var command = string.Join(" ", guard);
        var slots = new Dictionary<string, string>
        {
            ["what"] = "service",
            ["writer"] = $"`{command} agents`",
            ["version"] = $"`Specrig.Tool` {Version}",
            ["cut"] = "`tests/internal/Harness.cs`",
            ["validate"] = "`dotnet test tests`",
            ["boundary"] = "What `tests/internal/Harness.cs` stands in for is held by the suite itself — a request to an origin it does not name fails the case — not by this rule, and with the rule off you may no more widen it than here.",
            ["spec-paths"] = "`tests/feature/`, `tests/steps/`",
            ["platform-paths"] = Shared.Ticked(Rules.Platform),
            ["tasks"] = "",
            ["modes"] = Shared.ModeRows(),
            ["platform-files"] = Shared.Ticked(Rules.Platform),
            ["generated"] = $"`tests/surface.yaml` and `tests/checks.yaml` are written by `{command} surface`, `specrig.md` by `{command} agents`, and the structure of `tests/flows.yaml` by every whole run of the suite, in any mode, and never by hand.",
            ["tool"] = "specrig",
            ["config"] = "`specrig.yaml`",
            ["areas"] = Shared.AreaRows(areas),
            ["cases"] = "`.feature` files in `tests/feature/`",
            ["contracts"] = "",
            ["describes"] = """
                **A case describes what the service's callers see and what reaches its
                neighbours** — an answer, an event, a request to a neighbour — not its classes or
                its tables.

                **A neighbour answers what the case declared.** A number in a case comes from a
                fixture, never computed inside a step. A neighbour's error codes are contract,
                held by a case.
                """,
            ["binding"] = """
                **Bind at the service's edge, as its callers do.** A step sends a request through
                `harness.Http` or `harness.Grpc<TClient>()`, delivers an event through
                `harness.Bus.Deliver`, runs a job with `harness.Run<TJob>()` or a command with
                `harness.Command(...)`. A `Given` writes fixtures through the container's own
                repositories in `harness.Scope()`; a `Then` reads back through them, not out of
                the answer.

                **The harness** is specrig, set up by `tests/internal/Harness.cs`. Steps receive
                it, with a property for each neighbour `Harness.cs` names. One over HTTP takes:

                - `harness.<Neighbour>.Reply(method, path, status, body)` — what it answers;
                - `harness.<Neighbour>.ReplyTo(method, path, query, status, body)` — the same,
                  for a request whose query has these values;
                - `harness.<Neighbour>.Requests(method, path)` — what reached it, for a `Then`.

                One over gRPC takes:

                - `harness.<Neighbour>.Reply(method, answer)` — what it answers, the method named
                  `Method`, or `Service.Method` where two of its services share the name;
                - `harness.<Neighbour>.Fail(method, code, reason)` — it refusing, the reason in
                  the rich status's `ErrorInfo`;
                - `harness.<Neighbour>.Requests<TRequest>(method)` — what reached it, for a
                  `Then`.

                A call to a neighbour no step answered fails the test, and so does a request to
                an origin `Harness.cs` does not name. An HTTP neighbour `Harness.cs` names a
                contract for is held to it: every answer a step declares, and every body the
                service sends, is checked against it, and a route, a status or a body the
                contract does not have fails the test; one without is checked by nothing but the
                cases. A gRPC neighbour's contract is its services' descriptors: a method they
                do not have, or an answer of a type the method does not return, fails the test.

                **What may be stood in for is given, not chosen.** `tests/internal/` is
                read-only. A case that needs another neighbour, or an answer the stand-in cannot
                give, is a stop: name the neighbour or the behaviour, and the assertion it would
                carry.

                **The boot test comes before any case.** `tests/internal/Boot.cs` boots the
                service on an empty cut and asserts nothing about behaviour; one run of it tells
                you what the harness is missing instead of an hour inside a half-written suite.

                **Time.** Set fixture dates relative to today — most cases need no clock. A case
                that needs a fixed date sets it in its own `Given` with `harness.Clock`.
                """,
        };
        return $"""
            {Shared.Render("header", slots).TrimEnd()}

            {Shared.Render("contributing", slots).TrimEnd()}

            ## The short version

            {Shared.Render("short", slots).TrimEnd()}

            {Shared.Render("rule11", slots).TrimEnd()}

            ## Running

            `dotnet test tests` runs the suite, with the resources started in Docker beside
            it; `{command} surface` rewrites `tests/surface.yaml` and `tests/checks.yaml`
            from the code. A project that rewrites the records in CI and then runs
            `git diff --exit-code` sees a record that fell behind as a diff.

            `tests/flows.yaml` is what the cases make the service send: every `When` step's
            call is an input, and what left the service after it is what it sent. Only the
            `description` fields are written by hand: they survive a rewrite, and one whose
            step is gone moves to `orphaned` and fails the run until it is moved or deleted.

            """;
    }

    public static string Reference() => Shared.Render("reference", new Dictionary<string, string> { ["what"] = "service" }).TrimEnd();

    public static string AgentsStub(string service, bool parentRules)
    {
        var parent = parentRules
            ? """
              The repository's rules in `../AGENTS.md` hold here too — ownership, the
              notation for specifications, a neighbour's rules are not reproduced.


              """
            : "";
        return $"""
            # AGENTS.md

            Instructions for AI coding agents working in {service}.

            {Reference()}

            {parent}## This service

            <!-- What {service} is for, who calls it, which neighbours it calls and what it
            publishes. Written by the person who owns the platform. -->

            """;
    }
}
