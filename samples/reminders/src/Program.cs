using Quartz;
using RabbitMQ.Client;
using Reminders;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient<Billing>(client => client.BaseAddress = new Uri(builder.Configuration["Billing:BaseUrl"]!));
builder.Services.AddSingleton<IConnectionFactory>(new ConnectionFactory { Uri = new Uri(builder.Configuration.GetConnectionString("Rabbit")!) });
builder.Services.AddSingleton(provider => provider.GetRequiredService<IConnectionFactory>().CreateConnectionAsync().GetAwaiter().GetResult());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PaidOrders>();
builder.Services.AddSingleton<Announcer>();
builder.Services.AddTransient<SendReminders>();
builder.Services.AddHostedService<InvoicesPaid>();
builder.Services.AddQuartz(quartz =>
{
    var reminders = new JobKey("reminders");
    quartz.AddJob<RemindersJob>(job => job.WithIdentity(reminders).UsingJobData("limit", builder.Configuration.GetValue("Reminders:Limit", 100)));
    quartz.AddTrigger(trigger => trigger.ForJob(reminders).WithCronSchedule("0 0 * * * ?"));
});
builder.Services.AddQuartzHostedService();

var host = builder.Build();

if (args is ["remind", ..])
{
    using (host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SendReminders>().RunAsync(CancellationToken.None);
    }
    return 0;
}

await host.RunAsync();
return 0;

public partial class Program;
