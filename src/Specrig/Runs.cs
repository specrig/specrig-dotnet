namespace Specrig;

public sealed class Runs(Features features, string file)
{
    readonly Lock gate = new();
    readonly List<Run> runs = [];

    // A run narrowed to some cases saw part of the service, and its record would lose the rest.
    readonly List<Case> left = [.. features.Cases.Where(c => !c.Ignored)];

    // What is wrong goes back to a case: dotnet test shows a failure after the run only by its type.
    public IReadOnlyList<string> Add(string service, string feature, string title, IEnumerable<(string Column, string Value)> row, IReadOnlyList<Record> records)
    {
        if (features.Find(feature, title, row) is not { } found)
        {
            return [$"tests/flows.yaml: does not hold this case, tests/feature/ read by the English keywords has no {feature}: {title} {string.Join(' ', row)}".TrimEnd()];
        }
        lock (gate)
        {
            runs.Add(new Run(found.Label, records));
            return left.Remove(found) && left.Count == 0 && Flows.Write(file, service, runs) > 0
                ? ["tests/flows.yaml: some descriptions lost their anchor — delete them or move them under the input or branch they belong to now; this case only ended the run"]
                : [];
        }
    }
}
