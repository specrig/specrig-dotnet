using System.Collections;
using System.Reflection;
using Reqnroll;
using Reqnroll.Bindings;
using Reqnroll.BoDi;

namespace Specrig;

[Binding]
public sealed class Hooks(IObjectContainer container, ScenarioContext scenario, FeatureContext feature)
{
    static readonly Lazy<Type> harness = new(FindHarness);
    static readonly Lazy<Runs> runs = new(() => new Runs(new Features(Path.Combine(Rig.Tests, "feature")), Path.Combine(Rig.Tests, "flows.yaml")));

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
            var info = scenario.ScenarioInfo;
            var row = info.Arguments.Cast<DictionaryEntry>().Select(e => ($"{e.Key}", $"{e.Value}"));
            IReadOnlyList<string> failures = [.. current.Failures(), .. runs.Value.Add(current.Service, feature.FeatureInfo.Title, info.Title, row, current.Trace.Records)];
            if (failures.Count > 0)
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
    public static Task End() => Rig.EndRun();

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
