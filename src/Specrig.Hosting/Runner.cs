namespace Specrig;

public interface IJobRunner
{
    bool Runs(object job);

    Task Run(object job, IServiceProvider services, CancellationToken cancel);
}

static class Runners
{
    // A suite that references an adapter has it beside it, whether or not its code names a type of it.
    static readonly Lazy<IReadOnlyList<IJobRunner>> known = new(() =>
        [.. new[] { "Specrig.QuartzRunner, Specrig.Quartz" }
            .Select(name => Type.GetType(name, throwOnError: false))
            .OfType<Type>()
            .Select(type => (IJobRunner)Activator.CreateInstance(type)!)]);

    public static IJobRunner? For(object job) => known.Value.FirstOrDefault(runner => runner.Runs(job));
}
