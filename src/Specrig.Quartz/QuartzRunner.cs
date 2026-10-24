using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl;

namespace Specrig;

// The scheduler stays unstarted, so nothing the service schedules fires by itself.
public sealed class QuartzRunner : IJobRunner
{
    public bool Runs(object job) => job is IJob;

    public async Task Run(object job, IServiceProvider services, CancellationToken cancel)
    {
        var scheduler = await services.GetRequiredService<ISchedulerFactory>().GetScheduler(cancel);
        IJobDetail? detail = null;
        foreach (var key in await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), cancel))
        {
            if (await scheduler.GetJobDetail(key, cancel) is { } registered && registered.JobType == job.GetType())
            {
                detail = registered;
                break;
            }
        }
        detail ??= JobBuilder.Create().OfType(job.GetType()).Build();
        var now = DateTimeOffset.UtcNow;
        var fired = new TriggerFiredBundle
        {
            JobDetail = detail,
            Trigger = (IOperableTrigger)TriggerBuilder.Create().ForJob(detail).StartNow().Build(),
            Recovering = false,
            FireTimeUtc = now,
            ScheduledFireTimeUtc = now,
            PreviousFireTimeUtc = null,
            NextFireTimeUtc = null,
        };
        await ((IJob)job).Execute(new JobExecutionContextImpl(scheduler, fired, (IJob)job, new SystemTextJsonJobInputSerializer()), cancel);
    }
}
