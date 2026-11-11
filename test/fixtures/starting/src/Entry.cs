using Microsoft.EntityFrameworkCore;

namespace Starting;

static class Entry
{
    static async Task<int> Main(string[] args)
    {
        var host = args.FirstOrDefault(a => a.StartsWith("--builder=")) switch
        {
            "--builder=generic" => Generic(args),
            "--builder=default" => Default(args),
            _ => Web(args),
        };

        await Seen.Read("program", host.Services);

        if (args is ["read", ..])
        {
            await Seen.Read("command", host.Services);
            return 0;
        }

        await host.RunAsync();
        return 0;
    }

    static IHost Web(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<StartingDb>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Starting")));
        builder.Services.AddHostedService<Reader>();
        return builder.Build();
    }

    static IHost Generic(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddDbContext<StartingDb>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Starting")));
        builder.Services.AddHostedService<Reader>();
        return builder.Build();
    }

    static IHost Default(string[] args) => Host.CreateDefaultBuilder(args)
        .ConfigureServices((context, services) =>
        {
            services.AddDbContext<StartingDb>(options => options.UseNpgsql(context.Configuration.GetConnectionString("Starting")));
            services.AddHostedService<Reader>();
        })
        .Build();
}
