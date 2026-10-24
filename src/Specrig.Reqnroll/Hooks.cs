using System.Collections.Concurrent;
using System.Reflection;
using Reqnroll;
using Reqnroll.Bindings;
using Reqnroll.BoDi;

namespace Specrig;

[Binding]
public sealed class Hooks(IObjectContainer container, ScenarioContext scenario, FeatureContext feature)
{
    static readonly Lazy<Type> harness = new(FindHarness);
    static readonly Lazy<Features> features = new(() => new Features(Path.Combine(Rig.Tests, "feature")));
    static readonly ConcurrentBag<Run> runs = [];
    static readonly ConcurrentDictionary<string, bool> ran = new();
    static string? service;

    Rig Current => (Rig)container.Resolve(harness.Value);

    [BeforeStep]
    public async Task Act()
    {
        var kind = scenario.StepContext.StepInfo.StepDefinitionType switch
        {
            StepDefinitionType.Given => "Context",
            StepDefinitionType.When => "Action",
            _ => "Outcome",
        };
        if (kind != "Outcome")
        {
            await Current.Settle();
        }
        Current.Trace.Act(kind);
    }

    [AfterScenario]
    public async Task Check()
    {
        var current = Current;
        try
        {
            await current.Settle();
            var title = scenario.ScenarioInfo.Title;
            var label = features.Value.Find(feature.FeatureInfo.Title, title)?.Label ?? title;
            runs.Add(new Run(label, current.Trace.Records));
            ran[label] = true;
            service ??= current.Service;
            if (current.Failures() is { Count: > 0 } failures)
            {
                throw new InvalidOperationException(string.Join("\n", failures));
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    [AfterTestRun]
    public static async Task End()
    {
        try
        {
            Write();
        }
        finally
        {
            await Rig.EndRun();
        }
    }

    // A run narrowed to some cases saw part of the service, and its record would lose the rest.
    static void Write()
    {
        if (service is null || !features.Value.Cases.Where(c => !c.Ignored).All(c => ran.ContainsKey(c.Label)))
        {
            return;
        }
        if (Flows.Write(Path.Combine(Rig.Tests, "flows.yaml"), service, runs) > 0)
        {
            throw new InvalidOperationException(
                "tests/flows.yaml: some descriptions lost their anchor — delete them or move them under the input or branch they belong to now");
        }
    }

    static Type FindHarness()
    {
        var found = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.GetName().Name is { } name && !name.StartsWith("System.") && !name.StartsWith("Microsoft."))
            .SelectMany(Types)
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(Rig)))
            .ToList();
        return found is [var one] ? one
            : throw new InvalidOperationException($"the suite declares {found.Count} harnesses; it takes one, in tests/internal/Harness.cs");
    }

    static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }
}
