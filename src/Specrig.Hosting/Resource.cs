using Microsoft.Extensions.DependencyInjection;

namespace Specrig;

public interface IResource
{
    string Setting { get; }

    void Attach(Trace trace, Action boot)
    {
    }

    Task<string> Provision(string service, CancellationToken cancel);

    void Configure(IServiceCollection services)
    {
    }

    Task Migrate(IServiceProvider services, CancellationToken cancel);

    Task Reset(CancellationToken cancel);

    IEnumerable<string> Failures() => [];
}
