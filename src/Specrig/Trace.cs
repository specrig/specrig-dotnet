using System.Text.Json.Nodes;

namespace Specrig;

public sealed record Record(long Seq, string Dir, string Kind, string Name, string Step)
{
    public JsonNode? Request { get; init; }
    public string? Error { get; init; }
}

public readonly record struct Call(long Seq, string Step);

public sealed class Trace
{
    const long Quiet = 50;
    const long Deadline = 5000;

    readonly Lock gate = new();
    readonly List<Record> records = [];
    long seq;
    int busy;
    long lastActivity = Environment.TickCount64;
    string step = "";

    public IReadOnlyList<Record> Records
    {
        get
        {
            lock (gate)
            {
                return [.. records];
            }
        }
    }

    // Context, Action or Outcome: what a Given, a When and a Then are for.
    public void Act(string kind)
    {
        lock (gate)
        {
            step = kind;
        }
    }

    public Call Begin()
    {
        lock (gate)
        {
            busy++;
            lastActivity = Environment.TickCount64;
            return new Call(++seq, step);
        }
    }

    public void End(Call call, string dir, string kind, string name, JsonNode? request = null, string? error = null)
    {
        lock (gate)
        {
            busy--;
            lastActivity = Environment.TickCount64;
            records.Add(new Record(call.Seq, dir, kind, name, call.Step) { Request = request, Error = error });
        }
    }

    public void Held()
    {
        lock (gate)
        {
            busy--;
        }
    }

    public void Released()
    {
        lock (gate)
        {
            busy++;
            lastActivity = Environment.TickCount64;
        }
    }

    public async Task Settle()
    {
        var deadline = Environment.TickCount64 + Deadline;
        while (Environment.TickCount64 < deadline)
        {
            lock (gate)
            {
                if (busy == 0 && Environment.TickCount64 - lastActivity >= Quiet)
                {
                    return;
                }
            }
            await Task.Delay(10);
        }
    }
}
